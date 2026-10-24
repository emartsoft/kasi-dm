using System.Reflection;
using Kasi.Media;

namespace Kasi.ArchitectureTests;

/// <summary>The M3.2 gate has no override and only the loader can produce a Playable plan (KASI-MED-005, ADR 0027).</summary>
[Trait("Requirement", "KASI-MED-005")]
public sealed class MediaProtectionArchitectureTests
{
    [Fact]
    public void Playable_has_no_public_constructor()
    {
        var playable = typeof(MediaSourceLoader).GetNestedType("Playable", BindingFlags.Public)
            ?? throw new InvalidOperationException("MediaSourceLoader.Playable is missing.");
        var publicConstructors = playable.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        publicConstructors.Should().BeEmpty("only MediaSourceLoader may create a Playable result");
        playable.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Should().Contain(constructor => constructor.IsAssembly
                && constructor.GetParameters().Length == 1
                && constructor.GetParameters()[0].ParameterType == typeof(MediaPresentation),
                "the loader's internal factory is the only way to supply a presentation");
    }

    [Fact]
    public void Media_code_exposes_no_override_member_for_the_protection_gate()
    {
        var suspicious = new[] { "bypass", "skip", "ignore", "drm" };
        var members = typeof(MediaSourceLoader).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(member => suspicious.Any(word => member.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(member => $"{member.DeclaringType?.Name}.{member.Name}")
            .ToArray();

        members.Should().BeEmpty("no public media member may suggest skipping or bypassing the DRM check");
    }
}
