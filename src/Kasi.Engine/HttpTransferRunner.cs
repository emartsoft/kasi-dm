using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using Kasi.Core;
using Kasi.Core.Logging;

namespace Kasi.Engine;

/// <summary>
/// Drives one HTTP download or resume end to end (M1.7b): probe, bounded leases through
/// <see cref="SegmentScheduler"/>, pooled writes into one <c>.kasipart</c>, seal and commit.
/// </summary>
public sealed class HttpTransferRunner
{
    private const int PulseMilliseconds = 250;

    private readonly HttpHostPoolOptions _hostPoolOptions;

    public HttpTransferRunner(HttpHostPoolOptions? hostPoolOptions = null)
    {
        _hostPoolOptions = hostPoolOptions ?? new HttpHostPoolOptions();
    }

    public async Task<TransferOutcome> DownloadAsync(
        Uri url,
        string destinationFolder,
        HttpTransferOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        var settings = options ?? new HttpTransferOptions();
        settings.Validate();
        var time = settings.TimeProvider ?? TimeProvider.System;

        try
        {
            settings = await LoadSecretAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled("The download was cancelled while the job secret was loaded.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed(SecretLoadFailure(ex));
        }

        settings = settings with { Scope = new RequestScope(url, settings.RequestHeaders, settings.Secret) };

        using var pool = CreatePool(url, url, settings);
        ProbeResponse probe;
        try
        {
            var initial = await ProbeWithRetryAsync(
                pool,
                url,
                settings.SchedulerOptions.MinLeaseBytes,
                settings,
                time,
                cancellationToken).ConfigureAwait(false);
            settings = initial.Settings;
            if (initial.Outcome is ProbeFailure failure)
            {
                return ProbeFailed(failure);
            }

            probe = (ProbeResponse)initial.Outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(detail: "The probe was cancelled.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed(TransientNetworkError(ex));
        }

        try
        {
            var forceSingleStream = false;
            if (probe.RangesSupported && probe.TotalBytes is null)
            {
                // The first probe was bounded to one lease, so its body is not the whole file. Ask once more with an
                // open-ended Range; a server that still gives no total can only be streamed in one piece.
                forceSingleStream = true;
                await probe.DisposeAsync().ConfigureAwait(false);
                try
                {
                    var full = await ProbeWithRetryAsync(
                        pool,
                        url,
                        firstRangeLength: null,
                        settings,
                        time,
                        cancellationToken).ConfigureAwait(false);
                    settings = full.Settings;
                    if (full.Outcome is ProbeFailure failure)
                    {
                        return ProbeFailed(failure);
                    }

                    probe = (ProbeResponse)full.Outcome;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return Cancelled(detail: "The probe was cancelled.");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return Failed(TransientNetworkError(ex));
                }
            }

            var fileName = ResolveFileName(probe.FileName, settings.OverrideFileName);
            destinationFolder = await RouteAfterProbeAsync(settings, destinationFolder, fileName, probe.ContentType, cancellationToken).ConfigureAwait(false);
            settings.Log?.Invoke(probe.RangesSupported
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Probe: ranges supported, {(probe.TotalBytes is { } known ? known.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes" : "size unknown")}")
                : "Probe: ranges not supported, one connection");
            if (settings.CheckpointJobId is { } checkpointJob && settings.JobRepository is { } jobs)
            {
                try
                {
                    await PersistProbeMetadataAsync(jobs, checkpointJob, probe, fileName, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return Cancelled("The download was cancelled while the probe result was saved.", fileName, probe.TotalBytes, probe.ETag, probe.LastModified);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return Failed(CheckpointFailure(ex), fileName, probe.TotalBytes, probe.ETag, probe.LastModified);
                }
            }

            var finalPath = ResolveFinalPath(destinationFolder, fileName, out var pathFailure);
            if (pathFailure is not null)
            {
                return pathFailure;
            }

            if (!settings.OverwriteDestination && File.Exists(finalPath))
            {
                return TargetExists(fileName, finalPath);
            }

            if (forceSingleStream || !probe.RangesSupported || probe.TotalBytes is null or <= 0)
            {
                var single = await SingleStreamAsync(probe, destinationFolder, fileName, finalPath, settings, time, cancellationToken)
                    .ConfigureAwait(false);
                return await DeleteSecretOnCompletionAsync(single, settings).ConfigureAwait(false);
            }

            var segmented = await SegmentedAsync(
                new HttpLeaseTransport(pool, probe.FinalUrl, probe.TotalBytes.Value, probe.ETag,
                    probe.LastModified, settings.Scope!, time),
                probe.FinalUrl,
                probe,
                destinationFolder,
                fileName,
                probe.TotalBytes.Value,
                probe.ETag,
                probe.LastModified,
                existingSegments: null,
                settings,
                time,
                cancellationToken).ConfigureAwait(false);
            return await DeleteSecretOnCompletionAsync(segmented, settings).ConfigureAwait(false);
        }
        finally
        {
            await probe.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The folder after the router has had its say (KASI-CAT-001); the router failing leaves the folder as it was.</summary>
    private static async Task<string> RouteAfterProbeAsync(HttpTransferOptions settings, string folder, string fileName, string? contentType, CancellationToken cancellationToken)
    {
        if (settings.RouteAfterProbe is not { } route)
        {
            return folder;
        }

        try
        {
            if (await route(fileName, contentType, cancellationToken).ConfigureAwait(false) is { Length: > 0 } routed)
            {
                settings.Log?.Invoke("Category: saved by file type");
                return routed;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            settings.Log?.Invoke("Category: not re-checked");
        }

        return folder;
    }

    public async Task<TransferOutcome> ResumeAsync(
        Uri finalUrl,
        string destinationFolder,
        string fileName,
        long totalBytes,
        string? eTag,
        string? lastModified,
        IReadOnlyList<Segment> existingSegments,
        HttpTransferOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finalUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(existingSegments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);

        if (string.IsNullOrWhiteSpace(eTag) && string.IsNullOrWhiteSpace(lastModified))
        {
            return new TransferFailed
            {
                ErrorKind = TransferErrorKind.FileChanged,
                Detail = "The job has no ETag or Last-Modified value, so it cannot be resumed safely.",
                ResolvedFileName = fileName,
                TotalBytes = totalBytes,
                SegmentsSnapshot = existingSegments,
            };
        }

        var settings = options ?? new HttpTransferOptions();
        settings.Validate();
        var time = settings.TimeProvider ?? TimeProvider.System;
        try
        {
            settings = await LoadSecretAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled("The resume was cancelled while the job secret was loaded.", fileName, totalBytes, eTag, lastModified);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed(SecretLoadFailure(ex), fileName, totalBytes, eTag, lastModified);
        }

        Uri? initialUrl;
        try
        {
            initialUrl = await ResolveResumeInitialUrlAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled("The resume was cancelled while the job URL was loaded.", fileName, totalBytes, eTag, lastModified);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed(CheckpointFailure(ex), fileName, totalBytes, eTag, lastModified);
        }

        settings = settings with { Scope = new RequestScope(initialUrl, settings.RequestHeaders, settings.Secret) };
        var safeName = FileNameSanitiser.Sanitise(fileName);
        var finalPath = ResolveFinalPath(destinationFolder, safeName, out var pathFailure);
        if (pathFailure is not null)
        {
            return pathFailure;
        }

        if (!settings.OverwriteDestination && File.Exists(finalPath))
        {
            return TargetExists(safeName, finalPath);
        }

        using var pool = CreatePool(finalUrl, initialUrl, settings);
        TransferOutcome outcome;
        try
        {
            outcome = await SegmentedAsync(
                new HttpLeaseTransport(pool, finalUrl, totalBytes, eTag, lastModified, settings.Scope!, time),
                finalUrl,
                probe: null,
                destinationFolder,
                safeName,
                totalBytes,
                eTag,
                lastModified,
                existingSegments,
                settings,
                time,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(detail: "The resume was cancelled.", fileName: safeName, totalBytes, eTag, lastModified);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new TransferFailed
            {
                ErrorKind = TransferErrorKind.ProtocolError,
                Detail = $"The resume could not be started ({ex.GetType().Name}).",
                ResolvedFileName = safeName,
                TotalBytes = totalBytes,
                ETag = eTag,
                LastModified = lastModified,
            };
        }

        return await DeleteSecretOnCompletionAsync(outcome, settings).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates a refreshed signed URL against the paused file before changing the saved URLs or requesting any
    /// remaining bytes. The original final URL identifies the paused checkpoint; M1.13 owns the job-state transition.
    /// </summary>
    public async Task<TransferOutcome> RefreshAndResumeAsync(
        Uri finalUrl,
        Uri refreshedUrl,
        string destinationFolder,
        string fileName,
        long totalBytes,
        string? eTag,
        string? lastModified,
        IReadOnlyList<Segment> existingSegments,
        HttpTransferOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finalUrl);
        ArgumentNullException.ThrowIfNull(refreshedUrl);
        if (!finalUrl.IsAbsoluteUri || finalUrl.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The saved final URL must be an absolute HTTP or HTTPS URL.", nameof(finalUrl));
        }

        if (!refreshedUrl.IsAbsoluteUri || refreshedUrl.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The refreshed URL must be an absolute HTTP or HTTPS URL.", nameof(refreshedUrl));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(existingSegments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);

        var settings = options ?? new HttpTransferOptions();
        settings.Validate();
        var savedBytes = existingSegments.Sum(segment => segment.Written);
        TransferFailed Failure(TransferFailure failure) =>
            Failed(failure, fileName, totalBytes, eTag, lastModified, savedBytes, existingSegments);
        if (settings.CheckpointJobId is not { } jobId || settings.JobRepository is not { } jobs
            || settings.SecretRepository is not { } secrets)
        {
            return Failure(new TransferFailure(TransferErrorKind.CheckpointError,
                "Link refresh needs the job, secret and checkpoint repositories."));
        }

        Job? job;
        try
        {
            job = await jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(new TransferFailure(TransferErrorKind.Cancelled, "The link refresh was cancelled."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failure(CheckpointFailure(ex));
        }

        if (job is null || job.FinalUrl != finalUrl.AbsoluteUri || job.TotalBytes != totalBytes
            || job.ETag != eTag || job.LastModified != lastModified
            || !Uri.TryCreate(job.Url, UriKind.Absolute, out var oldSourceUrl)
            || oldSourceUrl.Scheme is not ("http" or "https"))
        {
            return Failure(new TransferFailure(TransferErrorKind.FileChanged,
                "The saved job identity changed before the link could be refreshed."));
        }

        var freshSecretSupplied = settings.Secret is not null;
        try
        {
            settings = await LoadSecretAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(new TransferFailure(TransferErrorKind.Cancelled, "The link refresh was cancelled."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failure(SecretLoadFailure(ex));
        }

        var boundSecret = settings.Secret is { } secret
            ? secret with
            {
                SensitiveOrigin = secret.SensitiveOrigin
                    ?? (freshSecretSupplied ? OriginOf(refreshedUrl) : OriginOf(oldSourceUrl)),
            }
            : null;
        var headers = RequestScope.IsSameOrigin(oldSourceUrl, refreshedUrl)
            ? settings.RequestHeaders
            : settings.RequestHeaders?.Where(header => !RequestScope.IsSensitiveHeader(header.Key))
                .ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);
        settings = settings with
        {
            Secret = boundSecret,
            RequestHeaders = headers,
            Scope = new RequestScope(refreshedUrl, headers, boundSecret),
        };

        Uri resolvedUrl;
        using (var pool = CreatePool(refreshedUrl, refreshedUrl, settings))
        {
            ProbeAttempt attempt;
            try
            {
                attempt = await ProbeWithRetryAsync(pool, refreshedUrl, firstRangeLength: 1, settings,
                    settings.TimeProvider ?? TimeProvider.System, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failure(new TransferFailure(TransferErrorKind.Cancelled, "The link refresh probe was cancelled."));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Failure(TransientNetworkError(ex));
            }

            settings = attempt.Settings;
            if (attempt.Outcome is ProbeFailure probeFailure)
            {
                var kind = probeFailure.Kind == ProbeErrorKind.CertificateError
                    ? TransferErrorKind.CertificateError : TransferErrorKind.ProbeFailed;
                return Failure(new TransferFailure(kind, probeFailure.Message, probeFailure.StatusCode,
                    probeFailure.RetryAfter, ProbeFailure: probeFailure));
            }

            var probe = (ProbeResponse)attempt.Outcome;
            await using var probeScope = probe.ConfigureAwait(false);
            if (!probe.RangesSupported || probe.TotalBytes != totalBytes)
            {
                return Failure(new TransferFailure(TransferErrorKind.FileChanged,
                    "The refreshed link does not serve the paused file's length and byte ranges."));
            }

            var sameValidator = !string.IsNullOrWhiteSpace(eTag)
                ? string.Equals(eTag, probe.ETag, StringComparison.Ordinal)
                : !string.IsNullOrWhiteSpace(lastModified)
                    && string.Equals(lastModified, probe.LastModified, StringComparison.Ordinal);
            if (!sameValidator)
            {
                return Failure(new TransferFailure(TransferErrorKind.FileChanged,
                    "The refreshed link's file validator differs from the paused file."));
            }

            resolvedUrl = probe.FinalUrl;
        }

        try
        {
            if (boundSecret is not null)
            {
                await secrets.SetAsync(jobId, boundSecret, cancellationToken).ConfigureAwait(false);
            }

            if (!await jobs.UpdateUrlAsync(jobId, refreshedUrl.AbsoluteUri, resolvedUrl.AbsoluteUri, cancellationToken).ConfigureAwait(false))
            {
                return Failure(new TransferFailure(TransferErrorKind.CheckpointError,
                    "The saved job disappeared before its refreshed URL could be stored."));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(new TransferFailure(TransferErrorKind.Cancelled, "The link refresh was cancelled."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failure(CheckpointFailure(ex));
        }

        return await ResumeAsync(resolvedUrl, destinationFolder, fileName, totalBytes, eTag, lastModified,
            existingSegments, settings, cancellationToken).ConfigureAwait(false);
    }

    private static Uri OriginOf(Uri url) => new(url.GetLeftPart(UriPartial.Authority));

    private async Task<ProbeAttempt> ProbeWithRetryAsync(
        HttpHostPool pool,
        Uri url,
        long? firstRangeLength,
        HttpTransferOptions settings,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var failedAttempts = 0;
        while (true)
        {
            ProbeOutcome outcome;
            try
            {
                var prober = new HttpProber(hop => pool.GetInvoker(hop, 0), settings.Scope!, time);
                outcome = await prober.ProbeAsync(url, firstRangeLength, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                outcome = new ProbeFailure(ProbeErrorKind.TransientError, $"The probe failed ({ex.GetType().Name}).");
            }

            if (outcome is ProbeResponse)
            {
                return new ProbeAttempt(outcome, settings);
            }

            var failure = (ProbeFailure)outcome;
            var decision = settings.RetryPolicy.Decide(
                TransferErrorKind.ProbeFailed,
                failure.StatusCode,
                failure.RetryAfter,
                failure,
                ++failedAttempts);
            if (!decision.Retry)
            {
                return new ProbeAttempt(failure, settings);
            }

            if (decision.ReduceConnections)
            {
                settings = ReduceConnections(settings);
            }

            if (decision.Delay > TimeSpan.Zero)
            {
                await Task.Delay(decision.Delay, time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static HttpTransferOptions ReduceConnections(HttpTransferOptions settings)
    {
        var scheduler = settings.SchedulerOptions;
        var max = Math.Max(1, scheduler.MaxConnections - 1);
        return settings with
        {
            SchedulerOptions = scheduler with
            {
                MaxConnections = max,
                InitialConnections = Math.Min(scheduler.InitialConnections, max),
            },
        };
    }

    private HttpHostPool CreatePool(Uri targetUrl, Uri? initialUrl, HttpTransferOptions options)
    {
        var secret = options.Secret;
        var credentialOrigin = secret?.SensitiveOrigin ?? initialUrl;
        var proxyCredentials = secret?.ProxyCredentials is { } proxySecret
            ? new NetworkCredential(proxySecret.UserName, proxySecret.Password) : null;
        if (credentialOrigin is not null && RequestScope.IsSameOrigin(credentialOrigin, targetUrl)
            && secret?.Credentials is { } credentials)
        {
            return new HttpHostPool(_hostPoolOptions,
                (credentialOrigin, new NetworkCredential(credentials.UserName, credentials.Password)),
                options.Proxy, proxyCredentials);
        }

        return new HttpHostPool(_hostPoolOptions, proxy: options.Proxy, proxyCredentials: proxyCredentials);
    }

    private static async Task<Uri?> ResolveResumeInitialUrlAsync(HttpTransferOptions settings, CancellationToken cancellationToken)
    {
        if (settings.CheckpointJobId is { } jobId && settings.JobRepository is { } jobs)
        {
            var job = await jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (job is not null && Uri.TryCreate(job.Url, UriKind.Absolute, out var savedUrl)
                && savedUrl.Scheme is "http" or "https")
            {
                return savedUrl;
            }
        }

        return settings.InitialUrl;
    }

    internal static async Task<HttpTransferOptions> LoadSecretAsync(HttpTransferOptions settings, CancellationToken cancellationToken)
    {
        if (settings.Secret is null && settings.CheckpointJobId is { } jobId && settings.SecretRepository is { } repository)
        {
            return settings with { Secret = await repository.GetAsync(jobId, cancellationToken).ConfigureAwait(false) };
        }

        return settings;
    }

    internal static async Task<TransferOutcome> DeleteSecretOnCompletionAsync(TransferOutcome outcome, HttpTransferOptions settings)
    {
        if (outcome is TransferCompleted && settings.CheckpointJobId is { } jobId && settings.SecretRepository is { } repository)
        {
            try
            {
                await repository.DeleteAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The file is already committed, so a database failure here must not turn the download into an error; the
                // engine facade (M1.13) removes a leftover secret when it completes the job.
            }
        }

        return outcome;
    }

    private static TransferFailure SecretLoadFailure(Exception exception) => new(
        TransferErrorKind.CheckpointError,
        $"The job secret could not be loaded ({exception.GetType().Name}).");

    private static async Task PersistProbeMetadataAsync(
        IJobRepository jobs,
        JobId jobId,
        ProbeResponse probe,
        string fileName,
        CancellationToken cancellationToken)
    {
        var stored = await jobs.UpdateTransferMetadataAsync(
            jobId,
            new JobTransferMetadata(probe.FinalUrl.AbsoluteUri, fileName, probe.TotalBytes, probe.ETag, probe.LastModified, probe.Resumable),
            cancellationToken).ConfigureAwait(false);
        if (!stored)
        {
            throw new InvalidOperationException("The job to checkpoint does not exist.");
        }
    }

    private async Task<TransferOutcome> SingleStreamAsync(
        ProbeResponse probe,
        string destinationFolder,
        string fileName,
        string finalPath,
        HttpTransferOptions options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var opened = PartFileWriter.TryOpen(
            destinationFolder,
            fileName,
            probe.TotalBytes,
            resume: false,
            spaceProvider: options.SpaceProvider);
        if (opened is PartFileOpenFailure openFailure)
        {
            return OpenFailed(openFailure.Status, fileName, probe.TotalBytes, probe.ETag, probe.LastModified);
        }

        var writer = ((PartFileOpened)opened).Writer;
        writer.WriteFaultInjector = options.WriteFaultInjector;
        writer.FlushFaultInjector = options.FlushFaultInjector;
        await using var writerScope = writer.ConfigureAwait(false);

        if (probe.TotalBytes is 0)
        {
            TransferFailure? emptyFailure;
            try
            {
                emptyFailure = await FinalizePartAsync(writer, 0, 0, options, probe.FinalUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Cancelled("The download was cancelled before it committed.", fileName, 0, probe.ETag, probe.LastModified);
            }

            if (emptyFailure is not null)
            {
                return Failed(emptyFailure, fileName, 0, probe.ETag, probe.LastModified);
            }

            return new TransferCompleted
            {
                FinalUrl = probe.FinalUrl,
                FileName = fileName,
                FinalPath = finalPath,
                TotalBytes = 0,
                RangesSupported = false,
                Resumable = false,
                ETag = probe.ETag,
                LastModified = probe.LastModified,
            };
        }

        using var buffer = new PooledPartWriteBuffer(writer, options.WriteBufferMaxAge, time);
        using var stallGuard = new StallGuard(time, options.RetryPolicy.ReadStallTimeout, cancellationToken);
        long readTotal = 0;
        long flushed = 0;
        try
        {
            buffer.BeginLease(0);
            while (true)
            {
                var window = buffer.AvailableWindow;
                if (window.IsEmpty)
                {
                    break;
                }

                var read = await ReadBodyAsync(probe.Body, window, stallGuard, options, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                readTotal += read;
                var result = await buffer.AdvanceBufferedAsync(read, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    throw new TransferException(DiskFailure(result.Status));
                }

                flushed += result.FlushedBytes;
            }

            var completed = await buffer.CompleteLeaseAsync(cancellationToken).ConfigureAwait(false);
            if (!completed.IsSuccess)
            {
                throw new TransferException(DiskFailure(completed.Status));
            }

            flushed += completed.FlushedBytes;
            if (probe.TotalBytes is { } expected && readTotal != expected)
            {
                throw new TransferException(Protocol($"The server sent {readTotal} bytes but declared {expected}."));
            }

            var finalFailure = await FinalizePartAsync(writer, readTotal, readTotal, options, probe.FinalUrl, cancellationToken).ConfigureAwait(false);
            if (finalFailure is not null)
            {
                throw new TransferException(finalFailure);
            }

            var total = probe.TotalBytes ?? readTotal;
            var segments = readTotal == 0 ? [] : new[] { new Segment(0, 0, readTotal) };
            return new TransferCompleted
            {
                FinalUrl = probe.FinalUrl,
                FileName = fileName,
                FinalPath = finalPath,
                TotalBytes = total,
                RangesSupported = false,
                Resumable = false,
                ETag = probe.ETag,
                LastModified = probe.LastModified,
                FinalSegments = segments,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FlushQuietlyAsync(writer).ConfigureAwait(false);
            return Cancelled("The download was cancelled.", fileName, probe.TotalBytes, probe.ETag, probe.LastModified, flushed);
        }
        catch (TransferException failure)
        {
            await FlushQuietlyAsync(writer).ConfigureAwait(false);
            return Failed(failure.Failure, fileName, probe.TotalBytes, probe.ETag, probe.LastModified, flushed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await FlushQuietlyAsync(writer).ConfigureAwait(false);
            return Failed(TransientNetworkError(ex), fileName, probe.TotalBytes, probe.ETag, probe.LastModified, flushed);
        }
    }

    internal async Task<TransferOutcome> SegmentedAsync(
        ILeaseTransport transport,
        Uri finalUrl,
        ProbeResponse? probe,
        string destinationFolder,
        string fileName,
        long totalBytes,
        string? eTag,
        string? lastModified,
        IReadOnlyList<Segment>? existingSegments,
        HttpTransferOptions options,
        TimeProvider time,
        CancellationToken cancellationToken,
        bool rangesSupported = true)
    {
        var opened = PartFileWriter.TryOpen(
            destinationFolder,
            fileName,
            totalBytes,
            resume: existingSegments is not null,
            alreadyWrittenBytes: existingSegments?.Sum(s => s.Written) ?? 0,
            spaceProvider: options.SpaceProvider);
        if (opened is PartFileOpenFailure openFailure)
        {
            return OpenFailed(openFailure.Status, fileName, totalBytes, eTag, lastModified);
        }

        var writer = ((PartFileOpened)opened).Writer;
        writer.WriteFaultInjector = options.WriteFaultInjector;
        writer.FlushFaultInjector = options.FlushFaultInjector;
        await using var writerScope = writer.ConfigureAwait(false);

        // A duplicated tail costs bandwidth that a cap makes scarce (2 MiB is 10 s at 200 KB/s), so a capped run has none.
        var schedulerOptions = options.BandwidthLimiter is { } cap && cap.IsLimited(options.JobBandwidthLimit)
            ? options.SchedulerOptions with { MaxEndgameDuplicateBytes = 0 }
            : options.SchedulerOptions;
        SegmentScheduler scheduler;
        try
        {
            scheduler = existingSegments is null
                ? new SegmentScheduler(totalBytes, rangesSupported, schedulerOptions, time, firstLeaseIsProbeStream: probe is not null)
                : new SegmentScheduler(totalBytes, existingSegments, rangesSupported, schedulerOptions, time);
        }
        catch (ArgumentException ex)
        {
            return new TransferFailed
            {
                ErrorKind = TransferErrorKind.ProtocolError,
                Detail = $"The segment snapshot is invalid ({ex.GetType().Name}).",
                ResolvedFileName = fileName,
                TotalBytes = totalBytes,
                ETag = eTag,
                LastModified = lastModified,
            };
        }

        options.SchedulerTick?.Invoke(scheduler);

        LeaseAssignment? probeLease = null;
        if (probe is not null)
        {
            probeLease = scheduler.TryAcquireNextLease(0);
            if (probeLease is null || !probeLease.Value.IsInitialProbeStream)
            {
                return new TransferFailed
                {
                    ErrorKind = TransferErrorKind.ProtocolError,
                    Detail = "The probe response could not be assigned as the first lease.",
                    ResolvedFileName = fileName,
                    TotalBytes = totalBytes,
                    ETag = eTag,
                    LastModified = lastModified,
                };
            }

            if (probe.BodyLength is { } bodyLength && bodyLength != probeLease.Value.Length)
            {
                return new TransferFailed
                {
                    ErrorKind = TransferErrorKind.ProtocolError,
                    Detail = $"The probe body has {bodyLength} bytes but the first lease covers {probeLease.Value.Length}.",
                    ResolvedFileName = fileName,
                    TotalBytes = totalBytes,
                    ETag = eTag,
                    LastModified = lastModified,
                };
            }
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SegmentCheckpointCoordinator? checkpoint = null;
        if (options.CheckpointJobId is { } checkpointJob && options.CheckpointWriter is { } checkpointWriter && options.SegmentRepository is { } segmentRepository)
        {
            checkpoint = new SegmentCheckpointCoordinator(
                checkpointJob,
                checkpointWriter,
                segmentRepository,
                writer,
                scheduler,
                time,
                options.CheckpointInterval,
                onPersistentFailure: _ => linked.Cancel());
            try
            {
                await checkpoint.SaveStructureAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Cancelled("The download was cancelled while the segment structure was saved.", fileName, totalBytes, eTag, lastModified);
            }
            catch (SegmentCheckpointException ex)
            {
                return Failed(DiskFailure(ex.Status), fileName, totalBytes, eTag, lastModified);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Failed(CheckpointFailure(ex), fileName, totalBytes, eTag, lastModified);
            }
        }

        var state = new FailureState();
        using var signal = new WorkerWakeSignal(options.SchedulerOptions.MaxConnections);
        var throttleGate = new ThrottleGate(time);
        var checkpointTask = checkpoint?.RunAsync(linked.Token);
        var workers = new Task[options.SchedulerOptions.MaxConnections];
        for (var worker = 0; worker < workers.Length; worker++)
        {
            var workerId = worker;
            var initial = worker == 0 ? probeLease : null;
            workers[worker] = Task.Run(
                () => WorkerAsync(workerId, initial, probe, transport, fileName, writer, scheduler, options, time, state, signal, throttleGate, linked),
                CancellationToken.None);
        }

        var pulse = PulseAsync(scheduler, signal, workers.Length, time, options.SchedulerTick, linked.Token);
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            await pulse.ConfigureAwait(false);
            if (checkpointTask is not null)
            {
                await checkpointTask.ConfigureAwait(false);
            }
        }

        if (probe is not null)
        {
            await probe.DisposeAsync().ConfigureAwait(false);
        }

        var failure = state.Failure;
        if (failure is null && !scheduler.IsComplete && checkpoint?.Failure is { } checkpointFailure)
        {
            // a failed flush keeps its disk classification (disk full pauses, other I/O errors fail); a database failure is a checkpoint error
            failure = checkpointFailure is SegmentCheckpointException disk ? DiskFailure(disk.Status) : CheckpointFailure(checkpointFailure);
        }

        if (failure is { } transferFailure)
        {
            return await FailedSegmentedAsync(writer, scheduler, checkpoint, transferFailure, fileName, totalBytes, eTag, lastModified).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return await FailedSegmentedAsync(writer, scheduler, checkpoint, new TransferFailure(TransferErrorKind.Cancelled, "The download was cancelled."), fileName, totalBytes, eTag, lastModified).ConfigureAwait(false);
        }

        if (!scheduler.IsComplete)
        {
            return await FailedSegmentedAsync(writer, scheduler, checkpoint, Protocol("The transfer stopped before every segment was complete."), fileName, totalBytes, eTag, lastModified).ConfigureAwait(false);
        }

        if (checkpoint is not null)
        {
            try
            {
                await checkpoint.SaveAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (SegmentCheckpointException ex)
            {
                return Failed(DiskFailure(ex.Status), fileName, totalBytes, eTag, lastModified);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Failed(CheckpointFailure(ex), fileName, totalBytes, eTag, lastModified);
            }
        }

        var snapshot = scheduler.GetSegmentsSnapshot();
        TransferFailure? finalFailure;
        try
        {
            finalFailure = await FinalizePartAsync(writer, totalBytes, scheduler.TotalWritten, options, finalUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await FailedSegmentedAsync(writer, scheduler, checkpoint, new TransferFailure(TransferErrorKind.Cancelled, "The download was cancelled before it committed."), fileName, totalBytes, eTag, lastModified).ConfigureAwait(false);
        }

        if (finalFailure is not null)
        {
            return Failed(finalFailure, fileName, totalBytes, eTag, lastModified, snapshot.Sum(s => s.Written), snapshot);
        }

        return new TransferCompleted
        {
            FinalUrl = finalUrl,
            FileName = fileName,
            FinalPath = writer.FinalPath,
            TotalBytes = totalBytes,
            RangesSupported = rangesSupported,
            Resumable = rangesSupported && (!string.IsNullOrWhiteSpace(eTag) || !string.IsNullOrWhiteSpace(lastModified)),
            ETag = eTag,
            LastModified = lastModified,
            FinalSegments = snapshot,
            StructureVersion = scheduler.StructureVersion,
            EndgameDuplicateBytes = scheduler.EndgameDuplicateBytesAllocated,
            RetryCount = state.Retries,
            FinalAllowedConnections = scheduler.AllowedConnections,
        };
    }

    private async Task WorkerAsync(
        int workerId,
        LeaseAssignment? initialLease,
        ProbeResponse? probe,
        ILeaseTransport transport,
        string fileName,
        PartFileWriter writer,
        SegmentScheduler scheduler,
        HttpTransferOptions options,
        TimeProvider time,
        FailureState state,
        WorkerWakeSignal signal,
        ThrottleGate throttleGate,
        CancellationTokenSource linked)
    {
        var cancellationToken = linked.Token;
        var lease = initialLease;
        var failedAttempts = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (state.Failure is not null)
            {
                return;
            }

            var throttled = throttleGate.Remaining();
            if (throttled > TimeSpan.Zero)
            {
                // a throttling server asked every worker to wait, not only the one that received the 429
                try
                {
                    await Task.Delay(throttled, time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            lease ??= scheduler.TryAcquireNextLease(workerId);
            if (lease is null)
            {
                if (scheduler.IsComplete)
                {
                    return;
                }

                try
                {
                    await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            var current = lease.Value;
            lease = null;
            var progress = new LeaseProgress();
            try
            {
                await TransferLeaseAsync(workerId, current, probe, transport, writer, scheduler, options, time, progress, cancellationToken).ConfigureAwait(false);
                failedAttempts = 0;
            }
            catch (TransferException failure)
            {
                if (progress.Flushed >= options.RetryPolicy.MinProgressBytes)
                {
                    // the attempt moved the file forward, so this failure starts the count again instead of using up the budget
                    failedAttempts = 0;
                }

                var decision = options.RetryPolicy.Decide(
                    failure.Failure.Kind,
                    failure.Failure.HttpStatusCode,
                    failure.Failure.RetryAfter,
                    failure.Failure.ProbeFailure,
                    ++failedAttempts);
                if (decision.Retry)
                {
                    state.CountRetry();
                    options.Log?.Invoke(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Connection {workerId + 1}: retry {failedAttempts} after {failure.Failure.Kind}"));
                    if (decision.ReduceConnections && throttleGate.Enter(decision.Delay))
                    {
                        scheduler.ReduceAllowedConnections();
                    }

                    try
                    {
                        if (decision.Delay > TimeSpan.Zero)
                        {
                            await Task.Delay(decision.Delay, time, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                state.TrySet(failure.Failure);
                await linked.CancelAsync().ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                state.TrySet(TransientNetworkError(ex));
                await linked.CancelAsync().ConfigureAwait(false);
                return;
            }
            finally
            {
                if (current.IsInitialProbeStream && probe is not null)
                {
                    await probe.DisposeAsync().ConfigureAwait(false);
                }

                signal.Wake();
            }
        }
    }

    private async Task TransferLeaseAsync(
        int workerId,
        LeaseAssignment lease,
        ProbeResponse? probe,
        ILeaseTransport transport,
        PartFileWriter writer,
        SegmentScheduler scheduler,
        HttpTransferOptions options,
        TimeProvider time,
        LeaseProgress progress,
        CancellationToken cancellationToken)
    {
        LeaseBody? opened = null;
        var completed = false;
        try
        {
            Stream body;
            if (lease.IsInitialProbeStream)
            {
                body = probe?.Body ?? throw new TransferException(Protocol("The probe stream is missing."));
            }
            else
            {
                opened = await transport.OpenAsync(workerId, lease, cancellationToken).ConfigureAwait(false);
                body = opened.Body;
            }

            using var buffer = new PooledPartWriteBuffer(writer, options.WriteBufferMaxAge, time);
            buffer.BeginLease(lease.Start, lease.End);
            using var stallGuard = new StallGuard(time, options.RetryPolicy.ReadStallTimeout, cancellationToken);
            await ReadLeaseAsync(lease, body, buffer, scheduler, options, progress, stallGuard, cancellationToken).ConfigureAwait(false);
            completed = true;
        }
        catch
        {
            if (!completed)
            {
                try
                {
                    scheduler.AbandonLease(lease.LeaseId);
                }
                catch (InvalidOperationException)
                {
                    // The scheduler already removed the lease (for example after a supersede); nothing to abandon.
                }
            }

            throw;
        }
        finally
        {
            if (opened is not null)
            {
                await opened.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class HttpLeaseTransport(
        HttpHostPool pool,
        Uri finalUrl,
        long totalBytes,
        string? eTag,
        string? lastModified,
        RequestScope scope,
        TimeProvider time) : ILeaseTransport
    {
        public async Task<LeaseBody> OpenAsync(int workerId, LeaseAssignment lease, CancellationToken cancellationToken)
        {
#pragma warning disable CA2000 // the pool owns and disposes every invoker it returns
            var invoker = pool.GetInvoker(finalUrl, workerId);
#pragma warning restore CA2000
            using var request = CreateLeaseRequest(finalUrl, lease, eTag, lastModified, scope);
            HttpResponseMessage response;
            try
            {
                response = await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new TransferException(NetworkFailure(ex));
            }

            try
            {
                ValidateLeaseResponse(response, lease, totalBytes, eTag, lastModified, time);
                var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return new LeaseBody(body, response);
            }
            catch (HttpRequestException ex)
            {
                response.Dispose();
                throw new TransferException(NetworkFailure(ex));
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    internal static async Task ReadLeaseAsync(
        LeaseAssignment lease,
        Stream body,
        PooledPartWriteBuffer buffer,
        SegmentScheduler scheduler,
        HttpTransferOptions options,
        LeaseProgress progress,
        StallGuard stallGuard,
        CancellationToken cancellationToken)
    {
        long received = 0;
        while (true)
        {
            var window = buffer.AvailableWindow;
            if (window.IsEmpty)
            {
                break;
            }

            int read;
            try
            {
                read = await ReadLimitedAsync(body, window, stallGuard, options.BandwidthLimiter, options.JobBandwidthLimit, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stallGuard.Stalled)
            {
                await SalvageAsync(lease, buffer, scheduler, progress).ConfigureAwait(false);
                throw new TransferException(StallFailure());
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                await SalvageAsync(lease, buffer, scheduler, progress).ConfigureAwait(false);
                throw new TransferException(NetworkFailure(ex));
            }

            if (read == 0)
            {
                break;
            }

            received += read;
            var result = await buffer.AdvanceBufferedAsync(read, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess && result.FlushedBytes == 0
                && options.BandwidthLimiter is { } limiter && limiter.IsLimited(options.JobBandwidthLimit))
            {
                // A capped worker may wait seconds for its next tokens (many connections share a small cap). Write what it
                // has now, so the wait never holds received bytes beyond the 2 s loss budget (KASI-NFR-007).
                result = await buffer.FlushPendingAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!result.IsSuccess)
            {
                throw new TransferException(DiskFailure(result.Status));
            }

            if (result.FlushedBytes > 0)
            {
                progress.Flushed += result.FlushedBytes;
                var advance = scheduler.AdvanceLease(lease.LeaseId, result.FlushedBytes);
                if (advance.IsSuperseded)
                {
                    buffer.DropSuperseded();
                    scheduler.CompleteLease(lease.LeaseId);
                    await DrainAsync(body, options.SupersededLeaseDrainTimeout, options.BandwidthLimiter, options.JobBandwidthLimit, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
            }
        }

        var completed = await buffer.CompleteLeaseAsync(cancellationToken).ConfigureAwait(false);
        if (!completed.IsSuccess)
        {
            throw new TransferException(DiskFailure(completed.Status));
        }

        if (received != lease.Length)
        {
            if (completed.FlushedBytes > 0)
            {
                progress.Flushed += completed.FlushedBytes;
                scheduler.AdvanceLease(lease.LeaseId, completed.FlushedBytes);
            }

            throw new TransferException(Protocol($"Lease {lease.LeaseId} ended after {received} of {lease.Length} bytes."));
        }

        scheduler.CompleteLease(lease.LeaseId);
    }

    /// <summary>
    /// Writes what the worker has already received before its lease is given up, so the retry starts at the last byte that
    /// arrived and a connection that dies every few hundred KiB still moves forward (only written bytes ever count).
    /// </summary>
    private static async Task SalvageAsync(LeaseAssignment lease, PooledPartWriteBuffer buffer, SegmentScheduler scheduler, LeaseProgress progress)
    {
        try
        {
            var flushed = await buffer.FlushPendingAsync(CancellationToken.None).ConfigureAwait(false);
            if (flushed.IsSuccess && flushed.FlushedBytes > 0)
            {
                progress.Flushed += flushed.FlushedBytes;
                scheduler.AdvanceLease(lease.LeaseId, flushed.FlushedBytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            // best effort: the retry simply fetches these bytes again
        }
    }

    private static HttpRequestMessage CreateLeaseRequest(
        Uri finalUrl,
        LeaseAssignment lease,
        string? eTag,
        string? lastModified,
        RequestScope scope)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, finalUrl)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.AcceptEncoding.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("identity"));
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(lease.Start, lease.End - 1);
        var validator = string.IsNullOrWhiteSpace(eTag) ? lastModified : eTag;
        if (!string.IsNullOrWhiteSpace(validator))
        {
            request.Headers.TryAddWithoutValidation("If-Range", validator);
        }

        scope.Apply(request);

        return request;
    }

    private static void ValidateLeaseResponse(
        HttpResponseMessage response,
        LeaseAssignment lease,
        long totalBytes,
        string? eTag,
        string? lastModified,
        TimeProvider time)
    {
        if (response.StatusCode == HttpStatusCode.OK)
        {
            throw new TransferException(new TransferFailure(TransferErrorKind.FileChanged, "The server answered 200 to a ranged request; the file changed."));
        }

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new TransferException(new TransferFailure(
                TransferErrorKind.HttpStatusError,
                $"The server returned {(int)response.StatusCode} for a lease.",
                response.StatusCode,
                TryGetRetryAfter(response.Headers.RetryAfter, time)));
        }

        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange is null
            || !contentRange.HasRange
            || contentRange.From != lease.Start
            || contentRange.To != lease.End - 1)
        {
            throw new TransferException(Protocol("The server returned a Content-Range that does not match the lease."));
        }

        if (contentRange.HasLength && contentRange.Length != totalBytes)
        {
            throw new TransferException(new TransferFailure(TransferErrorKind.FileChanged, "The total file length changed during the transfer."));
        }

        if (!string.IsNullOrWhiteSpace(eTag))
        {
            var tag = response.Headers.ETag;
            if (tag is null || tag.IsWeak || !string.Equals(tag.Tag, eTag, StringComparison.Ordinal))
            {
                throw new TransferException(new TransferFailure(TransferErrorKind.FileChanged, "The response ETag does not match the probe."));
            }
        }
        else if (!string.IsNullOrWhiteSpace(lastModified))
        {
            var raw = response.Content.Headers.TryGetValues("Last-Modified", out var values) ? values.FirstOrDefault() : null;
            if (raw is not null && !string.Equals(raw, lastModified, StringComparison.Ordinal))
            {
                throw new TransferException(new TransferFailure(TransferErrorKind.FileChanged, "The response Last-Modified does not match the probe."));
            }
        }
    }

    private static async Task DrainAsync(
        Stream body,
        TimeSpan timeout,
        BandwidthLimiter? limiter,
        BandwidthLimiter.JobBandwidthLimit? jobLimit,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        try
        {
            while (await ReadLimitedAsync(body, buffer.AsMemory(), null, limiter, jobLimit, timeoutSource.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
        {
            // The lease is already complete; draining is only to let the connection return quietly.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task PulseAsync(
        SegmentScheduler scheduler,
        WorkerWakeSignal signal,
        int workers,
        TimeProvider time,
        Action<SegmentScheduler>? onTick,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PulseMilliseconds), time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                scheduler.EvaluateRampUp();
                onTick?.Invoke(scheduler);
                signal.Wake(workers);
            }
        }
        catch (OperationCanceledException)
        {
            // The transfer finished or failed.
        }
    }

    private async Task<TransferFailed> FailedSegmentedAsync(
        PartFileWriter writer,
        SegmentScheduler scheduler,
        SegmentCheckpointCoordinator? checkpoint,
        TransferFailure failure,
        string fileName,
        long totalBytes,
        string? eTag,
        string? lastModified)
    {
        // Snapshot first, then flush, and offer the snapshot only if the flush succeeded: a cursor may be recorded only after
        // the bytes below it are on disk (ADR 0003). If the flush fails, nothing since the caller's last durable checkpoint
        // is known to be safe, so no snapshot is returned and the flush failure is what the caller must act on.
        var snapshot = scheduler.GetSegmentsSnapshot();
        var flush = await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        if (!flush.IsSuccess)
        {
            return Failed(DiskFailure(flush), fileName, totalBytes, eTag, lastModified);
        }

        if (checkpoint is not null)
        {
            try
            {
                await checkpoint.SaveAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (SegmentCheckpointException ex)
            {
                return Failed(DiskFailure(ex.Status), fileName, totalBytes, eTag, lastModified);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Failed(CheckpointFailure(ex), fileName, totalBytes, eTag, lastModified);
            }
        }

        return Failed(failure, fileName, totalBytes, eTag, lastModified, snapshot.Sum(s => s.Written), snapshot);
    }

    private static async Task FlushQuietlyAsync(PartFileWriter writer)
    {
        try
        {
            await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException)
        {
            // Best effort on the failure path; the part file is still preserved.
        }
    }

    private static async Task<TransferFailure?> FinalizePartAsync(
        PartFileWriter writer,
        long expectedLength,
        long bytesWritten,
        HttpTransferOptions options,
        Uri? source,
        CancellationToken cancellationToken)
    {
        var seal = await writer.SealAsync(expectedLength, bytesWritten, cancellationToken).ConfigureAwait(false);
        if (!seal.IsSuccess)
        {
            return DiskFailure(seal);
        }

        if (options.ExpectedChecksum is { } expected)
        {
            if (options.BeforeVerifyAsync is { } beforeVerify)
            {
                await beforeVerify().ConfigureAwait(false);
            }

            string computed;
            try
            {
                computed = await ChecksumVerifier.ComputeAsync(writer.PartPath, expected.Algorithm, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return DiskFailure(DiskStatus.FromException(ex, writer.PartPath));
            }

            if (!string.Equals(expected.Hex, computed, StringComparison.OrdinalIgnoreCase))
            {
                return new TransferFailure(
                    TransferErrorKind.VerificationFailed,
                    $"Checksum mismatch: expected {expected.Hex.ToLowerInvariant()}, computed {computed}.");
            }
        }

        if (!MarkOfTheWeb.TryWrite(writer.PartPath, source))
        {
            options.Log?.Invoke("Mark-of-the-Web not supported on this volume");
        }

        var commit = await writer.CommitAsync(options.OverwriteDestination, cancellationToken).ConfigureAwait(false);
        return commit.IsSuccess ? null : DiskFailure(commit);
    }

    private static async ValueTask<int> ReadBodyAsync(
        Stream body,
        Memory<byte> buffer,
        StallGuard guard,
        HttpTransferOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadLimitedAsync(body, buffer, guard, options.BandwidthLimiter, options.JobBandwidthLimit, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (guard.Stalled && !cancellationToken.IsCancellationRequested)
        {
            throw new TransferException(StallFailure());
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new TransferException(NetworkFailure(ex));
        }
    }

    internal static async ValueTask<int> ReadLimitedAsync(
        Stream body,
        Memory<byte> buffer,
        StallGuard? guard,
        BandwidthLimiter? limiter,
        BandwidthLimiter.JobBandwidthLimit? jobLimit,
        CancellationToken cancellationToken)
    {
        if (limiter is null || !limiter.IsLimited(jobLimit))
        {
            guard?.Reset();
            try
            {
                return await body.ReadAsync(buffer, guard?.Token ?? cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                guard?.Pause();
            }
        }

        var requested = Math.Min(buffer.Length, BandwidthLimiter.MaximumReadBytes);
        var reserved = await limiter.AcquireAsync(requested, jobLimit, cancellationToken).ConfigureAwait(false);
        var read = 0;
        try
        {
            guard?.Reset();
            read = await body.ReadAsync(buffer[..reserved], guard?.Token ?? cancellationToken).ConfigureAwait(false);
            return read;
        }
        finally
        {
            guard?.Pause();
            limiter.ReturnUnused(reserved - read, jobLimit);
        }
    }

    private static TransferFailure StallFailure() =>
        new(TransferErrorKind.TransientNetworkError, "The connection delivered no data for the read-stall timeout.");

    private static string ResolveFileName(string probedFileName, string? overrideFileName) =>
        FileNameSanitiser.Sanitise(
            string.IsNullOrWhiteSpace(overrideFileName) ? probedFileName : overrideFileName,
            probedFileName);

    private static string ResolveFinalPath(string destinationFolder, string fileName, out TransferFailed? failure)
    {
        try
        {
            failure = null;
            return Path.GetFullPath(Path.Combine(Path.GetFullPath(destinationFolder), fileName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            failure = new TransferFailed
            {
                ErrorKind = TransferErrorKind.DiskError,
                Detail = "The target path could not be resolved.",
                ResolvedFileName = fileName,
            };
            return string.Empty;
        }
    }

    private static TransferFailed ProbeFailed(ProbeFailure failure) => new()
    {
        ErrorKind = failure.Kind == ProbeErrorKind.CertificateError
            ? TransferErrorKind.CertificateError
            : TransferErrorKind.ProbeFailed,
        Detail = failure.Message,
        ProbeFailure = failure,
        HttpStatusCode = failure.StatusCode,
        RetryAfter = failure.RetryAfter,
    };

    private static TransferFailed OpenFailed(
        DiskStatus status,
        string fileName,
        long? totalBytes,
        string? eTag,
        string? lastModified) => new()
        {
            ErrorKind = status.ErrorKind switch
            {
                DiskErrorKind.NotEnoughSpace => TransferErrorKind.DiskNotEnoughSpace,
                DiskErrorKind.TargetExists => TransferErrorKind.DiskTargetExists,
                _ => TransferErrorKind.DiskError,
            },
            Detail = status.Detail ?? "The part file could not be opened.",
            DiskStatus = status,
            ResolvedFileName = fileName,
            TotalBytes = totalBytes,
            ETag = eTag,
            LastModified = lastModified,
        };

    private static TransferFailed TargetExists(string fileName, string finalPath) => new()
    {
        ErrorKind = TransferErrorKind.DiskTargetExists,
        Detail = $"The destination file '{Redact.Path(finalPath)}' already exists.",
        ResolvedFileName = fileName,
    };

    private static TransferFailed Cancelled(
        string detail,
        string? fileName = null,
        long? totalBytes = null,
        string? eTag = null,
        string? lastModified = null,
        long bytesFlushed = 0) => new()
        {
            ErrorKind = TransferErrorKind.Cancelled,
            Detail = detail,
            ResolvedFileName = fileName,
            TotalBytes = totalBytes,
            ETag = eTag,
            LastModified = lastModified,
            BytesFlushed = bytesFlushed,
        };

    private static TransferFailed Failed(TransferFailure failure) => new()
    {
        ErrorKind = failure.Kind,
        Detail = failure.Detail,
        ProbeFailure = failure.ProbeFailure,
        DiskStatus = failure.DiskStatus,
        HttpStatusCode = failure.HttpStatusCode,
        RetryAfter = failure.RetryAfter,
    };

    private static TransferFailed Failed(
        TransferFailure failure,
        string fileName,
        long? totalBytes,
        string? eTag,
        string? lastModified,
        long bytesFlushed = 0,
        IReadOnlyList<Segment>? snapshot = null) => new()
        {
            ErrorKind = failure.Kind,
            Detail = failure.Detail,
            BytesFlushed = bytesFlushed,
            SegmentsSnapshot = snapshot ?? [],
            ResolvedFileName = fileName,
            TotalBytes = totalBytes,
            ProbeFailure = failure.ProbeFailure,
            DiskStatus = failure.DiskStatus,
            HttpStatusCode = failure.HttpStatusCode,
            RetryAfter = failure.RetryAfter,
            ETag = eTag,
            LastModified = lastModified,
        };

    private static TransferFailure DiskFailure(DiskStatus status) => new(
        status.ErrorKind switch
        {
            DiskErrorKind.NotEnoughSpace => TransferErrorKind.DiskNotEnoughSpace,
            DiskErrorKind.TargetExists => TransferErrorKind.DiskTargetExists,
            _ => TransferErrorKind.DiskError,
        },
        status.Detail ?? "A disk operation failed.",
        DiskStatus: status);

    private static TransferFailure CheckpointFailure(Exception exception) => new(
        TransferErrorKind.CheckpointError,
        $"The segment checkpoint failed ({exception.GetType().Name}).");

    private static TransferFailure Protocol(string detail) => new(TransferErrorKind.ProtocolError, detail);

    private static TransferFailure TransientNetworkError(Exception exception) => new(
        TransferErrorKind.TransientNetworkError,
        $"The transfer failed ({exception.GetType().Name}).");

    private static TransferFailure NetworkFailure(Exception exception)
    {
        if (exception is HttpRequestException { StatusCode: HttpStatusCode.ProxyAuthenticationRequired })
        {
            return new TransferFailure(TransferErrorKind.HttpStatusError,
                "The proxy rejected authentication (407).", HttpStatusCode.ProxyAuthenticationRequired);
        }

        if (exception is HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded or HttpRequestError.HttpProtocolError or HttpRequestError.InvalidResponse }
            or HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded or HttpRequestError.HttpProtocolError or HttpRequestError.InvalidResponse })
        {
            return Protocol($"The HTTP response ended or was malformed ({exception.GetType().Name}).");
        }

        var certificate = exception is HttpRequestException http && IsCertificateFailure(http);
        var detail = exception is HttpRequestException request
            ? $"The HTTP request failed ({request.HttpRequestError})."
            : $"The network read failed ({exception.GetType().Name}).";
        return new TransferFailure(certificate ? TransferErrorKind.CertificateError : TransferErrorKind.TransientNetworkError, detail);
    }

    private static bool IsCertificateFailure(HttpRequestException exception)
    {
        if (exception.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return true;
        }

        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }

    private static TimeSpan? TryGetRetryAfter(System.Net.Http.Headers.RetryConditionHeaderValue? header, TimeProvider time)
    {
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header.Date is { } date)
        {
            var remaining = date - time.GetUtcNow();
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }

        return null;
    }

    private sealed record ProbeAttempt(ProbeOutcome Outcome, HttpTransferOptions Settings);

    internal sealed class LeaseProgress
    {
        /// <summary>Bytes of this lease attempt that reached the part file and were counted by the scheduler.</summary>
        public long Flushed { get; set; }
    }

    private sealed class FailureState
    {
        private TransferFailure? _failure;
        private int _retries;

        public int Retries => Volatile.Read(ref _retries);

        public void CountRetry() => Interlocked.Increment(ref _retries);

        public TransferFailure? Failure => Volatile.Read(ref _failure);

        public void TrySet(TransferFailure failure) => Interlocked.CompareExchange(ref _failure, failure, null);
    }
}
