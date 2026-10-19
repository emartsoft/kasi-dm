using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using Kasi.Core;

namespace Kasi.Engine;

/// <summary>FTP and FTPS entry point using the same bounded lease and part-file loop as HTTP (KASI-ENG-002).</summary>
public sealed class FtpTransferRunner
{
    private readonly X509Certificate2Collection? _testRoots;
    private readonly HttpTransferRunner _shared = new();

    public FtpTransferRunner()
    {
    }

    internal FtpTransferRunner(X509Certificate2Collection testRoots) => _testRoots = testRoots;

    public async Task<TransferOutcome> DownloadAsync(Uri url, string destinationFolder,
        HttpTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        FtpUrl.ValidateScheme(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        if (url.UserInfo.Length > 0)
        {
            return Fail(TransferErrorKind.ProtocolError,
                "Move FTP URL credentials to the job secret before starting the transfer.");
        }

        var settings = options ?? new HttpTransferOptions();
        settings.Validate();
        try
        {
            settings = await HttpTransferRunner.LoadSecretAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(TransferErrorKind.Cancelled, "The FTP download was cancelled while loading its secret.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail(TransferErrorKind.CheckpointError, $"The FTP secret could not be loaded ({ex.GetType().Name}).");
        }

        var probed = await ProbeAsync(url, settings, cancellationToken).ConfigureAwait(false);
        if (probed.Failure is { } failure)
        {
            return Failed(failure);
        }

        var metadata = probed.Metadata!.Value;
        var name = FileNameSanitiser.Sanitise(settings.OverrideFileName ?? Uri.UnescapeDataString(Path.GetFileName(url.AbsolutePath)));
        if (!settings.OverwriteDestination && File.Exists(Path.Combine(destinationFolder, name)))
        {
            return Fail(TransferErrorKind.DiskTargetExists, "The FTP destination file already exists.", name);
        }

        if (settings.CheckpointJobId is { } jobId && settings.JobRepository is { } jobs)
        {
            try
            {
                var saved = await jobs.UpdateTransferMetadataAsync(jobId,
                    new JobTransferMetadata(url.AbsoluteUri, name, metadata.Size, null, metadata.Modified,
                        metadata.RestSupported && metadata.Modified is not null), cancellationToken).ConfigureAwait(false);
                if (!saved)
                {
                    return Fail(TransferErrorKind.CheckpointError, "The FTP job disappeared before its metadata was saved.", name);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Fail(TransferErrorKind.Cancelled, "The FTP metadata save was cancelled.", name);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Fail(TransferErrorKind.CheckpointError, $"The FTP metadata save failed ({ex.GetType().Name}).", name);
            }
        }

        settings = ForFtp(settings, metadata.Size);
        var transport = new FtpLeaseTransport(url, settings.Secret, metadata.Size, metadata.Modified,
            metadata.RestSupported, _testRoots);
        var outcome = await _shared.SegmentedAsync(transport, url, probe: null, destinationFolder, name,
            metadata.Size, eTag: null, metadata.Modified, existingSegments: null, settings,
            settings.TimeProvider ?? TimeProvider.System, cancellationToken,
            rangesSupported: metadata.RestSupported).ConfigureAwait(false);
        return await HttpTransferRunner.DeleteSecretOnCompletionAsync(outcome, settings).ConfigureAwait(false);
    }

    public async Task<TransferOutcome> ResumeAsync(Uri url, string destinationFolder, string fileName,
        long totalBytes, string? lastModified, IReadOnlyList<Segment> existingSegments,
        HttpTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        FtpUrl.ValidateScheme(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(existingSegments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);
        if (url.UserInfo.Length > 0)
        {
            return Fail(TransferErrorKind.ProtocolError,
                "Move FTP URL credentials to the job secret before resuming the transfer.");
        }

        if (lastModified is null)
        {
            return Fail(TransferErrorKind.FileChanged,
                "The FTP job has no modification time and cannot be resumed safely.", fileName);
        }

        var settings = options ?? new HttpTransferOptions();
        settings.Validate();
        try
        {
            settings = await HttpTransferRunner.LoadSecretAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(TransferErrorKind.Cancelled, "The FTP resume was cancelled while loading its secret.", fileName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail(TransferErrorKind.CheckpointError, $"The FTP secret could not be loaded ({ex.GetType().Name}).", fileName);
        }

        var probed = await ProbeAsync(url, settings, cancellationToken).ConfigureAwait(false);
        if (probed.Failure is { } failure)
        {
            return Failed(failure);
        }

        var metadata = probed.Metadata!.Value;
        if (!metadata.RestSupported || metadata.Size != totalBytes
            || !string.Equals(metadata.Modified, lastModified, StringComparison.Ordinal))
        {
            return Fail(TransferErrorKind.FileChanged,
                "The FTP file changed or the server no longer supports REST resume.", fileName);
        }

        var name = FileNameSanitiser.Sanitise(fileName);
        if (!settings.OverwriteDestination && File.Exists(Path.Combine(destinationFolder, name)))
        {
            return Fail(TransferErrorKind.DiskTargetExists, "The FTP destination file already exists.", name);
        }

        settings = ForFtp(settings, totalBytes);
        var transport = new FtpLeaseTransport(url, settings.Secret, totalBytes, lastModified,
            restSupported: true, _testRoots);
        var outcome = await _shared.SegmentedAsync(transport, url, probe: null, destinationFolder, name,
            totalBytes, eTag: null, lastModified, existingSegments, settings,
            settings.TimeProvider ?? TimeProvider.System, cancellationToken).ConfigureAwait(false);
        return await HttpTransferRunner.DeleteSecretOnCompletionAsync(outcome, settings).ConfigureAwait(false);
    }

    private async Task<(FtpMetadata? Metadata, TransferFailure? Failure)> ProbeAsync(
        Uri url, HttpTransferOptions options, CancellationToken cancellationToken)
    {
        var time = options.TimeProvider ?? TimeProvider.System;
        var failedAttempts = 0;
        while (true)
        {
            var client = FtpConnection.Create(url, options.Secret, _testRoots);
            await using var clientScope = client.ConfigureAwait(false);
            try
            {
                await client.Connect(cancellationToken).ConfigureAwait(false);
                var path = Uri.UnescapeDataString(url.AbsolutePath);
                var size = await client.GetFileSize(path, -1, cancellationToken).ConfigureAwait(false);
                if (size < 0)
                {
                    var missing = new ProbeFailure(ProbeErrorKind.PermanentError, "The FTP file has no readable size.");
                    return (null, new TransferFailure(TransferErrorKind.ProbeFailed, missing.Message,
                        ProbeFailure: missing));
                }

                var modified = FtpConnection.Modified(await client.GetModifiedTime(path, cancellationToken).ConfigureAwait(false));
                return (new FtpMetadata(size, modified, client.Capabilities.Contains(FtpCapability.REST)), null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return (null, new TransferFailure(TransferErrorKind.Cancelled, "The FTP probe was cancelled."));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var failure = FtpConnection.Failure(ex);
                var decision = options.RetryPolicy.Decide(failure.Kind, probeFailure: failure.ProbeFailure,
                    failedAttempts: ++failedAttempts);
                if (!decision.Retry)
                {
                    return (null, failure);
                }

                if (decision.Delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(decision.Delay, time, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return (null, new TransferFailure(TransferErrorKind.Cancelled, "The FTP probe was cancelled."));
                    }
                }
            }
        }
    }

    private static HttpTransferOptions ForFtp(HttpTransferOptions options, long size)
    {
        var scheduler = options.SchedulerOptions with { MaxConnections = 1, InitialConnections = 1 };
        if (size > 0)
        {
            scheduler = scheduler with { MinLeaseBytes = size, MaxLeaseBytes = size };
        }

        return options with { SchedulerOptions = scheduler };
    }

    private static TransferFailed Failed(TransferFailure failure) => new()
    {
        ErrorKind = failure.Kind,
        Detail = failure.Detail,
        ProbeFailure = failure.ProbeFailure,
    };

    private static TransferFailed Fail(TransferErrorKind kind, string detail, string? fileName = null) => new()
    {
        ErrorKind = kind,
        Detail = detail,
        ResolvedFileName = fileName,
    };

    private readonly record struct FtpMetadata(long Size, string? Modified, bool RestSupported);
}
