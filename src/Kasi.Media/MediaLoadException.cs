using System.Net;

namespace Kasi.Media;

public enum MediaLoadError
{
    InvalidUri,
    UnsupportedKind,
    HttpError,
    TooLarge,
    TooManyManifests,
    Timeout,
}

/// <summary>A manifest fetch failed before parsing. Messages never include a manifest URL with its query string.</summary>
public sealed class MediaLoadException : Exception
{
    public MediaLoadException(MediaLoadError error, string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        Error = error;
        StatusCode = statusCode;
    }

    public MediaLoadException(MediaLoadError error, string message, Exception inner)
        : base(message, inner)
    {
        Error = error;
    }

    public MediaLoadError Error { get; }

    public HttpStatusCode? StatusCode { get; }
}
