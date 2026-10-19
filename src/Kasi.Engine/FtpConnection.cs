using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using FluentFTP.Exceptions;
using Kasi.Core;

namespace Kasi.Engine;

internal static class FtpConnection
{
    internal static AsyncFtpClient Create(Uri url, JobSecret? secret, X509Certificate2Collection? testRoots)
    {
        var credentials = secret?.Credentials is { } supplied
            ? new NetworkCredential(supplied.UserName, supplied.Password)
            : new NetworkCredential("anonymous", "anonymous@");
        var config = new FtpConfig
        {
            DataConnectionType = FtpDataConnectionType.AutoPassive,
            EncryptionMode = url.Scheme == "ftps"
                ? url.Port == 990 ? FtpEncryptionMode.Implicit : FtpEncryptionMode.Explicit
                : FtpEncryptionMode.None,
            DataConnectionEncryption = url.Scheme == "ftps",
            TimeConversion = FtpDate.UTC,
        };
        var client = new AsyncFtpClient(url.Host, credentials, url.Port, config, logger: null);
        if (testRoots is { Count: > 0 })
        {
            client.ValidateCertificate += (_, args) =>
            {
                if (args.Certificate is null || args.PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
                    || args.PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
                {
                    args.Accept = false;
                    return;
                }

                using var certificate = new X509Certificate2(args.Certificate);
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(testRoots);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                args.Accept = chain.Build(certificate);
            };
        }

        return client;
    }

    internal static string? Modified(DateTime value) => value == DateTime.MinValue
        ? null : value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static TransferFailure Failure(Exception exception)
    {
        if (HasAuthenticationException(exception))
        {
            return new TransferFailure(TransferErrorKind.CertificateError, "The FTPS certificate failed validation.");
        }

        if (exception is FtpSecurityNotAvailableException)
        {
            var probe = new ProbeFailure(ProbeErrorKind.PermanentError,
                "The FTP server does not support the requested TLS mode.");
            return new TransferFailure(TransferErrorKind.ProbeFailed, probe.Message, ProbeFailure: probe);
        }

        if (exception is FtpCommandException command)
        {
            var code = command.CompletionCode;
            if (code == "530")
            {
                var probe = new ProbeFailure(ProbeErrorKind.AuthRequired, "The FTP server rejected the login.");
                return new TransferFailure(TransferErrorKind.ProbeFailed, probe.Message, ProbeFailure: probe);
            }

            if (code == "550")
            {
                var probe = new ProbeFailure(ProbeErrorKind.PermanentError, "The FTP file is unavailable (550).");
                return new TransferFailure(TransferErrorKind.ProbeFailed, probe.Message, ProbeFailure: probe);
            }

            if (code is "421" or "426")
            {
                return new TransferFailure(TransferErrorKind.TransientNetworkError, $"The FTP connection failed ({code}).");
            }

            return new TransferFailure(TransferErrorKind.ProtocolError, $"The FTP server rejected a command ({code}).");
        }

        if (exception is IOException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return new TransferFailure(TransferErrorKind.TransientNetworkError,
                $"The FTP connection failed ({exception.GetType().Name}).");
        }

        return new TransferFailure(TransferErrorKind.ProtocolError,
            $"The FTP transfer failed ({exception.GetType().Name}).");
    }

    private static bool HasAuthenticationException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }
}
