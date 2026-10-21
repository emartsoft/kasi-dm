namespace Kasi.Ipc.Tests;

/// <summary>The shared sample messages in <c>tests/fixtures/ipc</c>; the extension's Vitest suite (M2.3) reads the same files.</summary>
internal static class IpcFixtures
{
    private static readonly string _root = FindRoot();

    public static string SchemaPath { get; } = Path.Combine(_root, "docs", "protocol", "kasi-messages.schema.json");

    public static TheoryData<string> Valid => Names("valid");

    public static TheoryData<string> Invalid => Names("invalid");

    public static byte[] Read(string kind, string name) => File.ReadAllBytes(Path.Combine(_root, "tests", "fixtures", "ipc", kind, name + ".json"));

    public static IReadOnlyList<string> NamesOf(string kind) =>
        [.. Directory.GetFiles(Path.Combine(_root, "tests", "fixtures", "ipc", kind), "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal)!];

    private static TheoryData<string> Names(string kind) => new(NamesOf(kind));

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
