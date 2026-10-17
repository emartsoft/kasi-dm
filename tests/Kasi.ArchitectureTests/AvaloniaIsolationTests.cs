using System.Reflection;
using System.Text.Json;

namespace Kasi.ArchitectureTests;

public class AvaloniaIsolationTests
{
    public static TheoryData<string> UiFreeProjects => new(SolutionLayout.UiFreeProjects);

    [Theory]
    [MemberData(nameof(UiFreeProjects))]
    [Trait("Requirement", "KASI-NFR-019")]
    public void Resolved_dependency_graph_contains_no_Avalonia_package_and_no_Kasi_App(string project)
    {
        var assets = Path.Combine(SolutionLayout.ProjectDirectory(project), "obj", "project.assets.json");
        File.Exists(assets).Should().BeTrue("the project must be restored before the architecture tests run");

        using var doc = JsonDocument.Parse(File.ReadAllText(assets));
        var libraries = doc.RootElement.GetProperty("libraries").EnumerateObject().Select(p => p.Name);

        libraries.Should().NotContain(l => l.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("FluentAvalonia", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("Kasi.App/", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(UiFreeProjects))]
    [Trait("Requirement", "KASI-NFR-019")]
    public void Compiled_assembly_does_not_reference_Avalonia_transitively(string project)
    {
        // NativeHost is an exe that the test project does not reference; its graph is covered by the assets test.
        var root = TryLoad(project);
        if (root is null)
        {
            return;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Assembly>([root]);
        while (queue.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                var name = reference.Name ?? string.Empty;
                if (!visited.Add(name))
                {
                    continue;
                }

                name.Should().NotStartWith("Avalonia", "{0} must not depend on Avalonia (via {1})", project, assembly.GetName().Name);
                name.Should().NotStartWith("FluentAvalonia");
                if (TryLoad(name) is { } loaded)
                {
                    queue.Enqueue(loaded);
                }
            }
        }
    }

    private static Assembly? TryLoad(string name)
    {
        try
        {
            return Assembly.Load(new AssemblyName(name));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (FileLoadException)
        {
            return null;
        }
    }
}
