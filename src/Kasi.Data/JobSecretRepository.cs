using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Kasi.Core;

namespace Kasi.Data;

/// <summary>Persists each job's cookies, headers and credentials as current-user DPAPI-protected JSON (TRD 9.4, 11.2).</summary>
[SupportedOSPlatform("windows")]
public sealed class JobSecretRepository(SqliteConnectionFactory factory) : IJobSecretRepository
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SetAsync(JobId id, JobSecret secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(secret, _jsonOptions);
        byte[]? payload = null;
        try
        {
            payload = ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
            using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            var changed = await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO job_secret (job_id, payload) " +
                "SELECT @id, @payload FROM job WHERE id = @id " +
                "ON CONFLICT(job_id) DO UPDATE SET payload = excluded.payload;",
                new { id = id.ToString(), payload },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (changed == 0)
            {
                throw new InvalidOperationException("Cannot save a secret for a job that does not exist.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    public async Task<JobSecret?> GetAsync(JobId id, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var payload = await connection.QuerySingleOrDefaultAsync<byte[]>(new CommandDefinition(
            "SELECT payload FROM job_secret WHERE job_id = @id;",
            new { id = id.ToString() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (payload is null)
        {
            return null;
        }

        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(payload, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<JobSecret>(plaintext, _jsonOptions)
                ?? throw new InvalidDataException("The stored job secret is invalid.");
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            throw new InvalidDataException("The stored job secret cannot be read.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public async Task<bool> DeleteAsync(JobId id, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM job_secret WHERE job_id = @id;",
            new { id = id.ToString() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed > 0;
    }
}
