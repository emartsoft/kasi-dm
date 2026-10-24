namespace Kasi.App.Controls;

/// <summary>What a chip says by its colour: a state, or a kind of file (the status pill and the file-type chip of the redesign).</summary>
internal enum ChipKind
{
    Neutral,
    Accent,
    Info,
    Success,
    Warning,
    Danger,
}

/// <summary>The colour family of a file-type chip, from the extension (the redesign: ISO amber, MSI blue, EXE green, ZIP purple, video red).</summary>
internal static class FileTypeColors
{
    private static readonly Dictionary<string, ChipKind> _byExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["iso"] = ChipKind.Warning,
        ["img"] = ChipKind.Warning,
        ["dmg"] = ChipKind.Warning,
        ["msi"] = ChipKind.Info,
        ["msix"] = ChipKind.Info,
        ["appx"] = ChipKind.Info,
        ["deb"] = ChipKind.Info,
        ["rpm"] = ChipKind.Info,
        ["exe"] = ChipKind.Success,
        ["apk"] = ChipKind.Success,
        ["pkg"] = ChipKind.Success,
        ["zip"] = ChipKind.Accent,
        ["rar"] = ChipKind.Accent,
        ["7z"] = ChipKind.Accent,
        ["tar"] = ChipKind.Accent,
        ["gz"] = ChipKind.Accent,
        ["xz"] = ChipKind.Accent,
        ["bz2"] = ChipKind.Accent,
        ["mp4"] = ChipKind.Danger,
        ["mkv"] = ChipKind.Danger,
        ["webm"] = ChipKind.Danger,
        ["mov"] = ChipKind.Danger,
        ["avi"] = ChipKind.Danger,
    };

    /// <summary>The chip colour for a file name; neutral when it has no extension or the extension is not known.</summary>
    public static ChipKind For(string? fileNameOrExtension)
    {
        var extension = ExtensionOf(fileNameOrExtension);
        return extension.Length > 0 && _byExtension.TryGetValue(extension, out var kind) ? kind : ChipKind.Neutral;
    }

    /// <summary>The label on the chip: the extension in capitals, at most four characters; "FILE" when there is none.</summary>
    public static string LabelFor(string? fileNameOrExtension)
    {
        var extension = ExtensionOf(fileNameOrExtension);
        return extension.Length == 0 ? "FILE" : extension.ToUpperInvariant()[..Math.Min(4, extension.Length)];
    }

    private static string ExtensionOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // a name with no dot, or only a leading one (".gitignore"), has no extension
        var text = value.Trim();
        var dot = text.LastIndexOf('.');
        return dot > 0 ? text[(dot + 1)..].Trim() : string.Empty;
    }
}
