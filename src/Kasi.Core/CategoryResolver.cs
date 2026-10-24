namespace Kasi.Core;

/// <summary>The category a download was given, and the rule that gave it (null when nothing matched and General was used).</summary>
public sealed record CategoryMatch(Category Category, CategoryRule? Rule);

/// <summary>
/// Chooses a download's category from its address, file name and MIME type, in the order of TRD 10.4 (KASI-CAT-001, 002). Pure: no I/O, no
/// clock, no logging, so the order is tested by tables.
/// <list type="number">
/// <item>user rules by domain or address, in position order, first match wins;</item>
/// <item>extension rules, user rules before built-in ones, then by position;</item>
/// <item>the MIME type, only when the file name has no extension;</item>
/// <item>General.</item>
/// </list>
/// </summary>
public static class CategoryResolver
{
    public const string FallbackName = "General";

    private static readonly (string Pattern, string Category)[] _mimeCategories =
    [
        ("video/*", "Video"),
        ("audio/*", "Music"),
        ("image/*", "Images"),
        ("application/zip", "Compressed"),
        ("application/x-zip-compressed", "Compressed"),
        ("application/gzip", "Compressed"),
        ("application/x-gzip", "Compressed"),
        ("application/x-tar", "Compressed"),
        ("application/x-bzip2", "Compressed"),
        ("application/x-7z-compressed", "Compressed"),
        ("application/x-rar-compressed", "Compressed"),
        ("application/vnd.rar", "Compressed"),
        ("application/pdf", "Documents"),
        ("application/msword", "Documents"),
        ("application/vnd.ms-excel", "Documents"),
        ("application/vnd.ms-powerpoint", "Documents"),
        ("application/vnd.openxmlformats-officedocument.*", "Documents"),
        ("application/epub+zip", "Documents"),
        ("text/plain", "Documents"),
        ("application/x-msdownload", "Programs"),
        ("application/vnd.microsoft.portable-executable", "Programs"),
        ("application/x-msi", "Programs"),
        ("application/x-iso9660-image", "Programs"),
        ("application/vnd.android.package-archive", "Programs"),
        ("application/x-debian-package", "Programs"),
        ("application/x-rpm", "Programs"),
        ("application/x-apple-diskimage", "Programs"),
    ];

    /// <summary>The category for a download, or null only when there are no categories at all.</summary>
    /// <param name="url">The address of the download.</param>
    /// <param name="fileName">The name the file will have, when known; otherwise the last part of the address is used.</param>
    /// <param name="mime">The MIME type the server or the browser reported, when known.</param>
    public static CategoryMatch? Resolve(
        Uri url,
        string? fileName,
        string? mime,
        IReadOnlyList<CategoryRule> rules,
        IReadOnlyList<Category> categories)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(categories);

        var byId = categories.ToDictionary(c => c.Id);
        var enabled = rules.Where(r => r.IsEnabled && byId.ContainsKey(r.CategoryId)).ToList();

        // 1. user rules by domain or address
        var host = url.Host;
        var address = AddressWithoutQuery(url);
        foreach (var rule in Ordered(enabled.Where(r => r.Kind is CategoryRuleKind.Domain or CategoryRuleKind.Url)))
        {
            var text = rule.Kind == CategoryRuleKind.Domain ? host : address;
            if (GlobPattern.IsMatch(rule.Pattern, text))
            {
                return new CategoryMatch(byId[rule.CategoryId], rule);
            }
        }

        // 2. extension rules, the user's before the built-in ones
        var extensions = Extensions(fileName, url);
        if (extensions.Count > 0)
        {
            var extensionRules = enabled.Where(r => r.Kind == CategoryRuleKind.Extension).ToList();
            foreach (var group in new[] { extensionRules.Where(r => !r.IsBuiltin), extensionRules.Where(r => r.IsBuiltin) })
            {
                var ordered = Ordered(group).ToList();
                foreach (var extension in extensions)
                {
                    var hit = ordered.FirstOrDefault(r => string.Equals(r.Pattern, extension, StringComparison.OrdinalIgnoreCase));
                    if (hit is not null)
                    {
                        return new CategoryMatch(byId[hit.CategoryId], hit);
                    }
                }
            }
        }
        else if (CategoryForMime(mime, categories) is { } byMime)
        {
            // 3. the MIME type, only when the name has no extension
            return new CategoryMatch(byMime, null);
        }

        // 4. General
        var fallback = categories.FirstOrDefault(c => string.Equals(c.Name, FallbackName, StringComparison.OrdinalIgnoreCase)) ?? (categories.Count > 0 ? categories[0] : null);
        return fallback is null ? null : new CategoryMatch(fallback, null);
    }

    /// <summary>
    /// The extensions to try, most specific first: for <c>setup.tar.gz</c> that is <c>tar.gz</c> and then <c>gz</c>. Empty when the name has none.
    /// </summary>
    public static IReadOnlyList<string> Extensions(string? fileName, Uri url)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? LastSegment(url) : fileName;
        var parts = name.Split('.');
        var found = new List<string>();
        if (parts.Length >= 3 && parts[^2].Length > 0)
        {
            found.Add(string.Concat(parts[^2], ".", parts[^1]));
        }

        // a leading dot (".gitignore") is a name, not an extension
        if (parts.Length >= 2 && parts[0].Length > 0 && parts[^1].Length > 0)
        {
            found.Add(parts[^1]);
        }

        return [.. found.Select(e => e.ToLowerInvariant())];
    }

    /// <summary>The address up to the path: scheme, host, port and path, with no query and no fragment (a query can carry a token).</summary>
    public static string AddressWithoutQuery(Uri url) => url.GetLeftPart(UriPartial.Path);

    private static IOrderedEnumerable<CategoryRule> Ordered(IEnumerable<CategoryRule> rules) =>
        rules.OrderBy(r => r.Position).ThenBy(r => r.Id);

    private static string LastSegment(Uri url)
    {
        var path = url.AbsolutePath.TrimEnd('/');
        var name = path[(path.LastIndexOf('/') + 1)..];
        try
        {
            return Uri.UnescapeDataString(name);
        }
        catch (UriFormatException)
        {
            return name;
        }
    }

    private static Category? CategoryForMime(string? mime, IReadOnlyList<Category> categories)
    {
        if (string.IsNullOrWhiteSpace(mime))
        {
            return null;
        }

        var type = mime.Split(';')[0].Trim().ToLowerInvariant();
        foreach (var (pattern, name) in _mimeCategories)
        {
            if (GlobPattern.IsMatch(pattern, type))
            {
                return categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            }
        }

        return null;
    }
}
