using System.Net;
using Kasi.Core;

namespace Kasi.Engine.Tests;

[Trait("TC", "TC-ENG-003")]
[Trait("TC", "TC-ENG-012")]
[Trait("TC", "TC-RES-010")]
[Trait("Requirement", "KASI-ENG-003")]
[Trait("Requirement", "KASI-RES-005")]
public class ProbeOutcomeTests
{
    [Fact]
    public void ProbeResponse_is_resumable_only_when_ranges_are_supported_and_a_strong_etag_or_last_modified_is_present()
    {
        var url = new Uri("https://example.com/file.bin");

#pragma warning disable CA2000 // ProbeResponse disposes the stream via using var
        using var withStrongETag = new ProbeResponse(
            url,
            "file.bin",
            new MemoryStream(),
            totalBytes: 1000,
            bodyLength: 1000,
            rangesSupported: true,
            eTag: "\"v1\"");

        using var withLastModified = new ProbeResponse(
            url,
            "file.bin",
            new MemoryStream(),
            totalBytes: 1000,
            bodyLength: 1000,
            rangesSupported: true,
            lastModified: "Thu, 01 Jan 2026 00:00:00 GMT");

        using var withWeakETagOnly = new ProbeResponse(
            url,
            "file.bin",
            new MemoryStream(),
            totalBytes: 1000,
            bodyLength: 1000,
            rangesSupported: true,
            eTag: "W/\"weak\"");

        using var withWeakETagAndLastModified = new ProbeResponse(
            url,
            "file.bin",
            new MemoryStream(),
            totalBytes: 1000,
            bodyLength: 1000,
            rangesSupported: true,
            eTag: "w/\"weak\"",
            lastModified: "Thu, 01 Jan 2026 00:00:00 GMT");

        using var withoutValidators = new ProbeResponse(
            url,
            "file.bin",
            new MemoryStream(),
            totalBytes: 1000,
            bodyLength: 1000,
            rangesSupported: true);

        using var withoutRanges = new ProbeResponse(
            url,
            "file.bin",
            new MemoryStream(),
            totalBytes: 1000,
            bodyLength: 1000,
            rangesSupported: false,
            eTag: "\"v1\"",
            lastModified: "Thu, 01 Jan 2026 00:00:00 GMT");
#pragma warning restore CA2000

        withStrongETag.RangesSupported.Should().BeTrue();
        withStrongETag.Resumable.Should().BeTrue();
        withStrongETag.ETag.Should().Be("\"v1\"");

        withLastModified.Resumable.Should().BeTrue();

        withWeakETagOnly.ETag.Should().BeNull();
        withWeakETagOnly.Resumable.Should().BeFalse();

        withWeakETagAndLastModified.ETag.Should().BeNull();
        withWeakETagAndLastModified.Resumable.Should().BeTrue();

        withoutValidators.RangesSupported.Should().BeTrue();
        withoutValidators.Resumable.Should().BeFalse();

        withoutRanges.RangesSupported.Should().BeFalse();
        withoutRanges.Resumable.Should().BeFalse();
    }

    [Fact]
    public void Non_identity_content_encoding_forces_single_connection_and_non_resumable_while_identity_is_normalised_to_null()
    {
        var url = new Uri("https://example.com/file.bin");

#pragma warning disable CA2000 // ProbeResponse disposes the stream via using var
        using var identity = new ProbeResponse(
            url,
            "file.bin",
            new MemoryStream(),
            totalBytes: 1000,
            rangesSupported: true,
            eTag: "\"v1\"",
            contentEncoding: "identity");

        using var gzip = new ProbeResponse(
            url,
            "file.bin",
            new MemoryStream(),
            totalBytes: 1000,
            rangesSupported: true,
            eTag: "\"v1\"",
            contentEncoding: "gzip");
#pragma warning restore CA2000

        identity.ContentEncoding.Should().BeNull();
        identity.RangesSupported.Should().BeTrue();
        identity.Resumable.Should().BeTrue();

        gzip.ContentEncoding.Should().Be("gzip");
        gzip.RangesSupported.Should().BeFalse();
        gzip.Resumable.Should().BeFalse();
    }

    [Fact]
    public void ApplyTo_copies_probe_metadata_onto_job_and_respects_preserveExistingFileName()
    {
        var job = new Job
        {
            Id = JobId.New(TimeProvider.System),
            Kind = JobKind.Http,
            Url = "https://example.com/redirect",
            FileName = "user-chosen.bin",
            Folder = @"C:\Downloads",
            QueueId = 1,
            QueuePos = 1.0,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        using var body = new MemoryStream();
        using var probe = new ProbeResponse(
            new Uri("https://cdn.example.com/server-name.bin"),
            "server-name.bin",
            body,
            totalBytes: 4096,
            bodyLength: 4096,
            rangesSupported: true,
            eTag: "\"etag-1\"",
            lastModified: "Thu, 01 Jan 2026 00:00:00 GMT");

        var replaced = probe.ApplyTo(job, preserveExistingFileName: false);
        var preserved = probe.ApplyTo(job, preserveExistingFileName: true);

        replaced.FinalUrl.Should().Be("https://cdn.example.com/server-name.bin");
        replaced.FileName.Should().Be("server-name.bin");
        replaced.TotalBytes.Should().Be(4096);
        replaced.ETag.Should().Be("\"etag-1\"");
        replaced.LastModified.Should().Be("Thu, 01 Jan 2026 00:00:00 GMT");
        replaced.Resumable.Should().BeTrue();

        preserved.FileName.Should().Be("user-chosen.bin");
    }

    [Fact]
    public void Dispose_disposes_both_body_and_response_owner_idempotently()
    {
#pragma warning disable CA2000 // ProbeResponse takes ownership of body and owner and disposes them under test
        var body = new TrackingStream();
        var owner = new TrackingDisposable();
        var response = new ProbeResponse(new Uri("https://example.com/a.bin"), "a.bin", body, responseOwner: owner);

        response.Dispose();
        response.Dispose();
#pragma warning restore CA2000

        body.DisposeCount.Should().Be(1);
        owner.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_disposes_both_body_and_response_owner_idempotently()
    {
#pragma warning disable CA2000 // ProbeResponse takes ownership of body and owner and disposes them under test
        var asyncBody = new TrackingStream();
        var asyncOwner = new TrackingDisposable();
        var asyncResponse = new ProbeResponse(new Uri("https://example.com/a.bin"), "a.bin", asyncBody, responseOwner: asyncOwner);

        await asyncResponse.DisposeAsync();
        await asyncResponse.DisposeAsync();
#pragma warning restore CA2000

        asyncBody.DisposeCount.Should().Be(1);
        asyncOwner.DisposeCount.Should().Be(1);
    }

    [Fact]
    public void Rejects_invalid_ProbeResponse_and_ProbeFailure_arguments()
    {
        using var stream = new MemoryStream();
        var validUri = new Uri("https://example.com/a.bin");

        var relativeUri = () => new ProbeResponse(new Uri("/relative", UriKind.Relative), "a.bin", stream);
        var ftpUri = () => new ProbeResponse(new Uri("ftp://example.com/a.bin"), "a.bin", stream);
        var emptyName = () => new ProbeResponse(validUri, "  ", stream);
        var negativeTotal = () => new ProbeResponse(validUri, "a.bin", stream, totalBytes: -1);
        var negativeBody = () => new ProbeResponse(validUri, "a.bin", stream, bodyLength: -1);
        var emptyFailureMessage = () => new ProbeFailure(ProbeErrorKind.PermanentError, "");
        var negativeRetryAfter = () => new ProbeFailure(ProbeErrorKind.Throttled, "throttled", HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(-1));

        relativeUri.Should().Throw<ArgumentException>();
        ftpUri.Should().Throw<ArgumentException>();
        emptyName.Should().Throw<ArgumentException>();
        negativeTotal.Should().Throw<ArgumentOutOfRangeException>();
        negativeBody.Should().Throw<ArgumentOutOfRangeException>();
        emptyFailureMessage.Should().Throw<ArgumentException>();
        negativeRetryAfter.Should().Throw<ArgumentOutOfRangeException>();

        var failure = new ProbeFailure(
            ProbeErrorKind.AuthRequired,
            "Authentication required.",
            HttpStatusCode.Unauthorized,
            authChallenges: ["Basic", "Digest"]);
        failure.Kind.Should().Be(ProbeErrorKind.AuthRequired);
        failure.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        failure.AuthChallenges.Should().Equal("Basic", "Digest");
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class TrackingStream : MemoryStream
    {
        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }

            base.Dispose(disposing);
        }
    }
}
