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

        using var pool = new HttpHostPool(_hostPoolOptions);
        ProbeResponse probe;
        try
        {
            var prober = new HttpProber(pool.GetInvoker(url, 0), time, settings.RequestHeaders);
            var outcome = await prober.ProbeAsync(url, settings.SchedulerOptions.MinLeaseBytes, cancellationToken).ConfigureAwait(false);
            if (outcome is ProbeFailure failure)
            {
                return ProbeFailed(failure);
            }

            probe = (ProbeResponse)outcome;
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
                    var fullProber = new HttpProber(pool.GetInvoker(url, 0), time, settings.RequestHeaders);
                    var fullOutcome = await fullProber.ProbeAsync(url, firstRangeLength: null, cancellationToken).ConfigureAwait(false);
                    if (fullOutcome is ProbeFailure failure)
                    {
                        return ProbeFailed(failure);
                    }

                    probe = (ProbeResponse)fullOutcome;
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
                return await SingleStreamAsync(probe, destinationFolder, fileName, finalPath, settings, time, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await SegmentedAsync(
                pool,
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
        }
        finally
        {
            await probe.DisposeAsync().ConfigureAwait(false);
        }
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

        using var pool = new HttpHostPool(_hostPoolOptions);
        try
        {
            return await SegmentedAsync(
                pool,
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
        await using var writerScope = writer.ConfigureAwait(false);

        if (probe.TotalBytes is 0)
        {
            var emptySeal = await writer.SealAsync(0, 0, cancellationToken).ConfigureAwait(false);
            if (!emptySeal.IsSuccess)
            {
                return Failed(DiskFailure(emptySeal), fileName, 0, probe.ETag, probe.LastModified);
            }

            var emptyCommit = await writer.CommitAsync(options.OverwriteDestination, cancellationToken).ConfigureAwait(false);
            return emptyCommit.IsSuccess
                ? new TransferCompleted
                {
                    FinalUrl = probe.FinalUrl,
                    FileName = fileName,
                    FinalPath = finalPath,
                    TotalBytes = 0,
                    RangesSupported = false,
                    Resumable = false,
                    ETag = probe.ETag,
                    LastModified = probe.LastModified,
                }
                : Failed(DiskFailure(emptyCommit), fileName, 0, probe.ETag, probe.LastModified);
        }

        using var buffer = new PooledPartWriteBuffer(writer, options.WriteBufferMaxAge, time);
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

                int read;
                try
                {
                    read = await ReadBodyAsync(probe.Body, window, cancellationToken).ConfigureAwait(false);
                }
                catch (TransferException)
                {
                    throw;
                }

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

            var seal = await writer.SealAsync(readTotal, readTotal, cancellationToken).ConfigureAwait(false);
            if (!seal.IsSuccess)
            {
                throw new TransferException(DiskFailure(seal));
            }

            var commit = await writer.CommitAsync(options.OverwriteDestination, cancellationToken).ConfigureAwait(false);
            if (!commit.IsSuccess)
            {
                throw new TransferException(DiskFailure(commit));
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

    private async Task<TransferOutcome> SegmentedAsync(
        HttpHostPool pool,
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
        CancellationToken cancellationToken)
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
        await using var writerScope = writer.ConfigureAwait(false);

        SegmentScheduler scheduler;
        try
        {
            scheduler = existingSegments is null
                ? new SegmentScheduler(totalBytes, rangesSupported: true, options.SchedulerOptions, time, firstLeaseIsProbeStream: probe is not null)
                : new SegmentScheduler(totalBytes, existingSegments, rangesSupported: true, options.SchedulerOptions, time);
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
        var state = new FailureState();
        using var signal = new SemaphoreSlim(0, int.MaxValue);
        var workers = new Task[options.SchedulerOptions.MaxConnections];
        for (var worker = 0; worker < workers.Length; worker++)
        {
            var workerId = worker;
            var initial = worker == 0 ? probeLease : null;
            workers[worker] = Task.Run(
                () => WorkerAsync(workerId, initial, probe, pool, finalUrl, fileName, writer, scheduler, totalBytes, eTag, lastModified, options, time, state, signal, linked),
                CancellationToken.None);
        }

        var pulse = PulseAsync(scheduler, signal, workers.Length, time, linked.Token);
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            await pulse.ConfigureAwait(false);
        }

        if (probe is not null)
        {
            await probe.DisposeAsync().ConfigureAwait(false);
        }

        if (state.Failure is { } failure)
        {
            return await FailedSegmentedAsync(writer, scheduler, failure, fileName, totalBytes, eTag, lastModified).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return await FailedSegmentedAsync(writer, scheduler, new TransferFailure(TransferErrorKind.Cancelled, "The download was cancelled."), fileName, totalBytes, eTag, lastModified).ConfigureAwait(false);
        }

        if (!scheduler.IsComplete)
        {
            return await FailedSegmentedAsync(writer, scheduler, Protocol("The transfer stopped before every segment was complete."), fileName, totalBytes, eTag, lastModified).ConfigureAwait(false);
        }

        var snapshot = scheduler.GetSegmentsSnapshot();
        DiskStatus seal;
        DiskStatus commit;
        try
        {
            seal = await writer.SealAsync(totalBytes, scheduler.TotalWritten, cancellationToken).ConfigureAwait(false);
            if (!seal.IsSuccess)
            {
                return Failed(DiskFailure(seal), fileName, totalBytes, eTag, lastModified, snapshot.Sum(s => s.Written), snapshot);
            }

            commit = await writer.CommitAsync(options.OverwriteDestination, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await FailedSegmentedAsync(writer, scheduler, new TransferFailure(TransferErrorKind.Cancelled, "The download was cancelled before it committed."), fileName, totalBytes, eTag, lastModified).ConfigureAwait(false);
        }

        if (!commit.IsSuccess)
        {
            return Failed(DiskFailure(commit), fileName, totalBytes, eTag, lastModified, snapshot.Sum(s => s.Written), snapshot);
        }

        return new TransferCompleted
        {
            FinalUrl = finalUrl,
            FileName = fileName,
            FinalPath = writer.FinalPath,
            TotalBytes = totalBytes,
            RangesSupported = true,
            Resumable = !string.IsNullOrWhiteSpace(eTag) || !string.IsNullOrWhiteSpace(lastModified),
            ETag = eTag,
            LastModified = lastModified,
            FinalSegments = snapshot,
            StructureVersion = scheduler.StructureVersion,
            EndgameDuplicateBytes = scheduler.EndgameDuplicateBytesAllocated,
        };
    }

    private async Task WorkerAsync(
        int workerId,
        LeaseAssignment? initialLease,
        ProbeResponse? probe,
        HttpHostPool pool,
        Uri finalUrl,
        string fileName,
        PartFileWriter writer,
        SegmentScheduler scheduler,
        long totalBytes,
        string? eTag,
        string? lastModified,
        HttpTransferOptions options,
        TimeProvider time,
        FailureState state,
        SemaphoreSlim signal,
        CancellationTokenSource linked)
    {
        var cancellationToken = linked.Token;
        var lease = initialLease;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (state.Failure is not null)
            {
                return;
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
            try
            {
                await TransferLeaseAsync(workerId, current, probe, pool, finalUrl, writer, scheduler, totalBytes, eTag, lastModified, options, time, cancellationToken).ConfigureAwait(false);
            }
            catch (TransferException failure)
            {
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

                signal.Release();
            }
        }
    }

    private async Task TransferLeaseAsync(
        int workerId,
        LeaseAssignment lease,
        ProbeResponse? probe,
        HttpHostPool pool,
        Uri finalUrl,
        PartFileWriter writer,
        SegmentScheduler scheduler,
        long totalBytes,
        string? eTag,
        string? lastModified,
        HttpTransferOptions options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
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
#pragma warning disable CA2000 // the pool owns and disposes every invoker it returns
                var invoker = pool.GetInvoker(finalUrl, workerId);
                using var request = CreateLeaseRequest(finalUrl, lease, eTag, lastModified, options.RequestHeaders);
#pragma warning restore CA2000
                try
                {
                    response = await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new TransferException(NetworkFailure(ex));
                }

                ValidateLeaseResponse(response, lease, totalBytes, eTag, lastModified, time);
                try
                {
                    body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new TransferException(NetworkFailure(ex));
                }
            }

            using var buffer = new PooledPartWriteBuffer(writer, options.WriteBufferMaxAge, time);
            buffer.BeginLease(lease.Start, lease.End);
            await ReadLeaseAsync(lease, body, buffer, scheduler, options.SupersededLeaseDrainTimeout, cancellationToken).ConfigureAwait(false);
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
            response?.Dispose();
        }
    }

    private static async Task ReadLeaseAsync(
        LeaseAssignment lease,
        Stream body,
        PooledPartWriteBuffer buffer,
        SegmentScheduler scheduler,
        TimeSpan drainTimeout,
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
                read = await body.ReadAsync(window, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                throw new TransferException(NetworkFailure(ex));
            }

            if (read == 0)
            {
                break;
            }

            received += read;
            var result = await buffer.AdvanceBufferedAsync(read, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                throw new TransferException(DiskFailure(result.Status));
            }

            if (result.FlushedBytes > 0)
            {
                var advance = scheduler.AdvanceLease(lease.LeaseId, result.FlushedBytes);
                if (advance.IsSuperseded)
                {
                    buffer.DropSuperseded();
                    scheduler.CompleteLease(lease.LeaseId);
                    await DrainAsync(body, drainTimeout, cancellationToken).ConfigureAwait(false);
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
            throw new TransferException(Protocol($"Lease {lease.LeaseId} ended after {received} of {lease.Length} bytes."));
        }

        scheduler.CompleteLease(lease.LeaseId);
    }

    private static HttpRequestMessage CreateLeaseRequest(
        Uri finalUrl,
        LeaseAssignment lease,
        string? eTag,
        string? lastModified,
        IReadOnlyDictionary<string, string>? headers)
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

        if (headers is not null)
        {
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

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

    private static async Task DrainAsync(Stream body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        try
        {
            while (await body.ReadAsync(buffer.AsMemory(), timeoutSource.Token).ConfigureAwait(false) > 0)
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
        SemaphoreSlim signal,
        int workers,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PulseMilliseconds), time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                scheduler.EvaluateRampUp();
                signal.Release(workers);
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
        TransferFailure failure,
        string fileName,
        long totalBytes,
        string? eTag,
        string? lastModified)
    {
        var snapshot = scheduler.GetSegmentsSnapshot();
        await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
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

    private static async ValueTask<int> ReadBodyAsync(Stream body, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new TransferException(NetworkFailure(ex));
        }
    }

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

    private static TransferFailure Protocol(string detail) => new(TransferErrorKind.ProtocolError, detail);

    private static TransferFailure TransientNetworkError(Exception exception) => new(
        TransferErrorKind.TransientNetworkError,
        $"The transfer failed ({exception.GetType().Name}).");

    private static TransferFailure NetworkFailure(Exception exception)
    {
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

    private sealed record TransferFailure(
        TransferErrorKind Kind,
        string Detail,
        HttpStatusCode? HttpStatusCode = null,
        TimeSpan? RetryAfter = null,
        DiskStatus? DiskStatus = null,
        ProbeFailure? ProbeFailure = null);

    private sealed class TransferException(TransferFailure failure) : Exception(failure.Detail)
    {
        public TransferFailure Failure { get; } = failure;
    }

    private sealed class FailureState
    {
        private TransferFailure? _failure;

        public TransferFailure? Failure => Volatile.Read(ref _failure);

        public void TrySet(TransferFailure failure) => Interlocked.CompareExchange(ref _failure, failure, null);
    }
}
