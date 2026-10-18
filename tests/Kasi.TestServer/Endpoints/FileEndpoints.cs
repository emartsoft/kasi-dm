using System.Buffers;
using System.Globalization;
using Kasi.TestServer.Chaos;
using Microsoft.Net.Http.Headers;

namespace Kasi.TestServer.Endpoints;

/// <summary><c>/files</c>, <c>/novalidators</c>, <c>/norange</c>, <c>/chunked</c> and <c>/mutable</c> (TPL 3.1).</summary>
internal static class FileEndpoints
{
    private const string ContentType = "application/octet-stream";
    private static readonly string[] _getAndHead = ["GET", "HEAD"];
    private static readonly DateTimeOffset _baseModified = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void Map(IEndpointRouteBuilder app)
    {
        // Range-capable, with strong ETag and Last-Modified; ASP.NET Core handles Range, If-Range and 416.
        app.MapMethods("/files/{seed}/{size:long}", _getAndHead, (HttpContext context, string seed, long size, TestServerMetrics metrics) =>
        {
            if (!IsValid(seed, size))
            {
                return Results.BadRequest();
            }

            var changed = context.Items[ChaosSession.ItemKey] is ChaosSession { Profile.ChangeContentAfterRequests: { } after } session
                && session.RequestIndex >= after;
            var effective = changed ? seed + "~changed" : seed;
            return Ranged(effective, size, $"\"{effective}-{size}\"", changed ? _baseModified.AddDays(1) : _baseModified, metrics);
        });

        // Range-capable, without ETag or Last-Modified (TC-RES-010).
        app.MapMethods("/novalidators/{seed}/{size:long}", _getAndHead, (string seed, long size, TestServerMetrics metrics) =>
        {
            if (!IsValid(seed, size))
            {
                return Results.BadRequest();
            }

#pragma warning disable CA2000 // the stream result owns the stream and disposes it after the response
            return Results.Stream(new GeneratedStream(seed, size, metrics), ContentType, enableRangeProcessing: true);
#pragma warning restore CA2000
        });

        // Ignores Range and always answers 200 with the whole body.
        app.MapMethods("/norange/{seed}/{size:long}", _getAndHead, async (HttpContext context, string seed, long size, TestServerMetrics metrics) =>
        {
            if (!IsValid(seed, size))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var headers = context.Response.GetTypedHeaders();
            context.Response.ContentType = ContentType;
            context.Response.ContentLength = size;
            headers.ETag = new EntityTagHeaderValue($"\"{seed}-{size}\"");
            headers.LastModified = _baseModified;
            context.Response.Headers.AcceptRanges = "none";
            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await WriteBodyAsync(context.Response.Body, seed, size, context.RequestAborted, metrics);
            }
        });

        // No Content-Length: Kestrel uses chunked transfer encoding.
        app.MapMethods("/chunked/{seed}/{size:long}", _getAndHead, async (HttpContext context, string seed, long size, TestServerMetrics metrics) =>
        {
            if (!IsValid(seed, size))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            context.Response.ContentType = ContentType;
            context.Response.Headers.AcceptRanges = "none";
            await context.Response.StartAsync(context.RequestAborted); // commit headers now, or Kestrel adds a Content-Length for a short body
            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await WriteBodyAsync(context.Response.Body, seed, size, context.RequestAborted, metrics);
            }
        });

        // A file whose bytes, size and ETag change when the test calls MutableFiles.Change (or POST /_mutable/{id}/change).
        app.MapMethods("/mutable/{id}", _getAndHead, (string id, MutableFiles files, TestServerMetrics metrics) =>
        {
            var (version, size) = files.Get(id);
            var seed = $"{id}.v{version}";
            return Ranged(seed, size, $"\"{seed}-{size}\"", _baseModified.AddMinutes(version), metrics);
        });

        app.MapPost("/_mutable/{id}/change", (string id, long? size, MutableFiles files) => Results.Ok(new { version = files.Change(id, size) }));
    }

    internal static async Task WriteBodyAsync(Stream body, string seed, long size, CancellationToken cancellationToken, TestServerMetrics? metrics = null)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            for (long offset = 0; offset < size; offset += buffer.Length)
            {
                var chunk = (int)Math.Min(buffer.Length, size - offset);
                GeneratedContent.Fill(seed, offset, buffer.AsSpan(0, chunk));
                await body.WriteAsync(buffer.AsMemory(0, chunk), cancellationToken);
                metrics?.FileBytesServed(chunk);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static IResult Ranged(string seed, long size, string etag, DateTimeOffset lastModified, TestServerMetrics metrics)
    {
#pragma warning disable CA2000 // the stream result owns the stream and disposes it after the response
        return Results.Stream(new GeneratedStream(seed, size, metrics), ContentType, null, lastModified, new EntityTagHeaderValue(etag), enableRangeProcessing: true);
#pragma warning restore CA2000
    }

    private static bool IsValid(string seed, long size) => size >= 0 && seed.Length is > 0 and <= GeneratedContent.MaxSeedLength;
}
