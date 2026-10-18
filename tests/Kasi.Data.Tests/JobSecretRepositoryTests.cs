using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kasi.Core;

namespace Kasi.Data.Tests;

[Trait("Category", "Integration")]
[Trait("TC", "TC-ENG-016")]
[Trait("Requirement", "KASI-NFR-013")]
public sealed class JobSecretRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly TempDatabase _db = new();
#pragma warning disable CA1859 // exercise the repository through its Core contract
    private IJobSecretRepository _secrets = null!;
#pragma warning restore CA1859

    public async ValueTask InitializeAsync()
    {
        await new MigrationRunner(_db.Factory).MigrateAsync(TempDatabase.Ct);
        if (OperatingSystem.IsWindows())
        {
            _secrets = new JobSecretRepository(_db.Factory);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Secret_round_trips_and_upsert_replaces_the_old_value()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI current-user scope requires Windows.");
        var job = await AddJobAsync();
        var original = NewSecret();

        await _secrets.SetAsync(job.Id, original, TempDatabase.Ct);
        (await _secrets.GetAsync(job.Id, TempDatabase.Ct)).Should().BeEquivalentTo(original);

        var replacement = new JobSecret
        {
            Cookies = [new JobCookie { Name = "new", Value = "new-cookie", Domain = ".example.com", Secure = true }],
            Headers = new Dictionary<string, string> { ["X-Token"] = "new-token" },
        };
        await _secrets.SetAsync(job.Id, replacement, TempDatabase.Ct);

        (await _secrets.GetAsync(job.Id, TempDatabase.Ct)).Should().BeEquivalentTo(replacement);
        (await _db.ScalarAsync("SELECT COUNT(*) FROM job_secret;")).Should().Be(1L);
    }

    [Fact]
    public async Task Stored_blob_is_encrypted_and_its_json_uses_the_browser_cookie_shape()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI current-user scope requires Windows.");
        var job = await AddJobAsync();
        await _secrets.SetAsync(job.Id, NewSecret(), TempDatabase.Ct);

        var payload = (byte[])(await _db.ScalarAsync("SELECT payload FROM job_secret;"))!;
        var blobText = Encoding.UTF8.GetString(payload);
        blobText.Should().NotContain("cookie-value");
        blobText.Should().NotContain("password-value");
        blobText.Should().NotContain("header-value");

        byte[] plaintext;
        if (OperatingSystem.IsWindows())
        {
            plaintext = ProtectedData.Unprotect(payload, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        else
        {
            Assert.Skip("DPAPI current-user scope requires Windows.");
            return;
        }
        try
        {
            using var json = JsonDocument.Parse(plaintext);
            var cookie = json.RootElement.GetProperty("cookies")[0];
            cookie.GetProperty("name").GetString().Should().Be("session");
            cookie.GetProperty("value").GetString().Should().Be("cookie-value");
            cookie.GetProperty("domain").GetString().Should().Be(".example.com");
            cookie.GetProperty("path").GetString().Should().Be("/downloads");
            cookie.GetProperty("secure").GetBoolean().Should().BeTrue();
            cookie.GetProperty("httpOnly").GetBoolean().Should().BeTrue();
            json.RootElement.GetProperty("headers").GetProperty("Authorization").GetString().Should().Be("header-value");
            json.RootElement.GetProperty("credentials").GetProperty("userName").GetString().Should().Be("alice");
            json.RootElement.GetProperty("sensitiveOrigin").GetString().Should().Be("https://example.com/");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    [Fact]
    public async Task Corrupted_blob_throws_InvalidDataException_without_exposing_secret_values()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI current-user scope requires Windows.");
        var job = await AddJobAsync();
        await _secrets.SetAsync(job.Id, NewSecret(), TempDatabase.Ct);
        await _db.ExecAsync("UPDATE job_secret SET payload = X'010203';");

        Func<Task> read = () => _secrets.GetAsync(job.Id, TempDatabase.Ct);
        var failure = await read.Should().ThrowAsync<InvalidDataException>();
        failure.Which.Message.Should().NotContain("cookie-value");
        failure.Which.Message.Should().NotContain("password-value");
    }

    [Fact]
    public async Task Delete_and_job_cascade_remove_the_secret()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI current-user scope requires Windows.");
        var job = await AddJobAsync();
        (await _secrets.GetAsync(job.Id, TempDatabase.Ct)).Should().BeNull();
        (await _secrets.DeleteAsync(job.Id, TempDatabase.Ct)).Should().BeFalse();

        await _secrets.SetAsync(job.Id, NewSecret(), TempDatabase.Ct);
        (await _secrets.DeleteAsync(job.Id, TempDatabase.Ct)).Should().BeTrue();
        (await _secrets.GetAsync(job.Id, TempDatabase.Ct)).Should().BeNull();

        await _secrets.SetAsync(job.Id, NewSecret(), TempDatabase.Ct);
        (await new JobRepository(_db.Factory).DeleteAsync(job.Id, TempDatabase.Ct)).Should().BeTrue();
        (await _db.ScalarAsync("SELECT COUNT(*) FROM job_secret;")).Should().Be(0L);
    }

    [Fact]
    public async Task Missing_job_is_reported_without_a_foreign_key_error()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI current-user scope requires Windows.");
        Func<Task> write = () => _secrets.SetAsync(JobId.New(TimeProvider.System), NewSecret(), TempDatabase.Ct);

        await write.Should().ThrowAsync<InvalidOperationException>();
        (await _db.ScalarAsync("SELECT COUNT(*) FROM job_secret;")).Should().Be(0L);
    }

    private async Task<Job> AddJobAsync()
    {
        var job = new Job
        {
            Id = JobId.New(TimeProvider.System),
            Kind = JobKind.Http,
            Url = "https://example.com/file.bin",
            FileName = "file.bin",
            Folder = "Downloads",
            QueueId = 1,
            QueuePos = 1,
            CreatedUtc = TimeProvider.System.GetUtcNow(),
        };
        await new JobRepository(_db.Factory).AddAsync(job, TempDatabase.Ct);
        return job;
    }

    private static JobSecret NewSecret() => new()
    {
        Cookies = [new JobCookie
        {
            Name = "session",
            Value = "cookie-value",
            Domain = ".example.com",
            Path = "/downloads",
            Secure = true,
            HttpOnly = true,
        }],
        Headers = new Dictionary<string, string> { ["Authorization"] = "header-value" },
        Credentials = new HttpCredentials { UserName = "alice", Password = "password-value" },
        SensitiveOrigin = new Uri("https://example.com/"),
    };
}
