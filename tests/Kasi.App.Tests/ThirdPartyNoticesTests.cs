using System.Xml.Linq;

namespace Kasi.App.Tests;

[Trait("Requirement", "KASI-NFR-020")]
public sealed class ThirdPartyNoticesTests
{
    [Fact]
    public void Every_central_package_is_named_in_the_committed_notices()
    {
        var root = FindRepositoryRoot();
        var props = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
        var packages = props.Descendants("PackageVersion")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(name => name is not null)
            .Cast<string>()
            .ToArray();
        var notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.txt"));

        packages.Should().NotBeEmpty();
        foreach (var package in packages)
        {
            notices.Should().Contain(package, $"the notices must name the package {package}");
        }

        notices.Should().Contain("FFmpeg / ffprobe");
        notices.Should().Contain("LGPL-2.1-or-later");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }
}
