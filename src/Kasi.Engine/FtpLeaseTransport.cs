using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using Kasi.Core;

namespace Kasi.Engine;

internal sealed class FtpLeaseTransport(
    Uri url,
    JobSecret? secret,
    long expectedSize,
    string? expectedModified,
    bool restSupported,
    X509Certificate2Collection? testRoots) : ILeaseTransport
{
    public async Task<LeaseBody> OpenAsync(int workerId, LeaseAssignment lease, CancellationToken cancellationToken)
    {
        if (workerId != 0 || (!restSupported && lease.Start != 0))
        {
            throw new TransferException(new TransferFailure(TransferErrorKind.FileChanged,
                "The FTP server cannot resume this file from a saved cursor."));
        }

        var client = FtpConnection.Create(url, secret, testRoots);
        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
            var path = Uri.UnescapeDataString(url.AbsolutePath);
            var size = await client.GetFileSize(path, -1, cancellationToken).ConfigureAwait(false);
            var modified = FtpConnection.Modified(await client.GetModifiedTime(path, cancellationToken).ConfigureAwait(false));
            if (size != expectedSize || !string.Equals(modified, expectedModified, StringComparison.Ordinal))
            {
                throw new TransferException(new TransferFailure(TransferErrorKind.FileChanged,
                    "The FTP file size or modification time changed during the transfer."));
            }

            var body = await client.OpenRead(path, FtpDataType.Binary, lease.Start,
                checkIfFileExists: false, cancellationToken).ConfigureAwait(false);
            return new LeaseBody(body, client);
        }
        catch (TransferException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new TransferException(FtpConnection.Failure(ex));
        }
    }
}
