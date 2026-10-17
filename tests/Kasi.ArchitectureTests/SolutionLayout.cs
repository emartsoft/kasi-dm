using System.Xml.Linq;

namespace Kasi.ArchitectureTests;

internal static class SolutionLayout
{
    private static readonly string[] _engineProjects =
    [
        "Kasi.Core", "Kasi.Engine", "Kasi.Media", "Kasi.Torrent", "Kasi.Scheduling", "Kasi.Data", "Kasi.Ipc",
    ];

    // TRD 5.2, "Depends on" column. Adding a project to src/ without adding it here fails the build.
    private static readonly Dictionary<string, string[]> _allowedReferences = new()
    {
        ["Kasi.Core"] = [],
        ["Kasi.Engine"] = ["Kasi.Core"],
        ["Kasi.Media"] = ["Kasi.Core", "Kasi.Engine"],
        ["Kasi.Torrent"] = ["Kasi.Core"],
        ["Kasi.Scheduling"] = ["Kasi.Core"],
        ["Kasi.Data"] = ["Kasi.Core"],
        ["Kasi.Ipc"] = ["Kasi.Core"],
        ["Kasi.App"] = _engineProjects,
        ["Kasi.NativeHost"] = ["Kasi.Ipc"],
    };

    public static string Root { get; } = FindRoot();

    /// <summary>Projects that must never depend on Avalonia (KASI-NFR-019): the engine and everything below the UI.</summary>
    public static IReadOnlyList<string> UiFreeProjects { get; } = [.. _engineProjects, "Kasi.NativeHost"];

    public static IReadOnlyDictionary<string, string[]> AllowedReferences => _allowedReferences;

    public static string ProjectDirectory(string project) => Path.Combine(Root, "src", project);

    public static IReadOnlyList<string> SourceProjects() =>
        [.. Directory.GetDirectories(Path.Combine(Root, "src")).Select(d => Path.GetFileName(d)!).Order()];

    public static IReadOnlyList<string> ProjectReferences(string project)
    {
        var csproj = Path.Combine(ProjectDirectory(project), project + ".csproj");
        return [.. XDocument.Load(csproj).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension((string?)e.Attribute("Include") ?? string.Empty))
            .Order()];
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kasi.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Kasi.slnx not found above the test output folder.");
    }
}
