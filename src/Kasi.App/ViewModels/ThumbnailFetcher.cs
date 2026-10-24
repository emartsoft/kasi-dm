namespace Kasi.App.ViewModels;

/// <summary>
/// Downloads the poster a page offered for a stream. http and https only, no cookies, a short timeout and a size cap, so a page
/// cannot make the picker hold an arbitrary amount of memory.
/// </summary>
internal static class ThumbnailFetcher
{
    public const int MaxBytes = 5 * 1024 * 1024;

    private static readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<byte[]?> FetchAsync(string? url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
            {
                return null;
            }

#pragma warning disable CA2007 // await using has no ConfigureAwait on its disposal
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2007
            using var memory = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > MaxBytes)
                {
                    return null;
                }

                await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return memory.ToArray();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null; // unreachable, refused or timed out
        }
    }
}
