using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Kasi.Engine.Tests.Containers;

/// <summary>
/// Builds the conformance image once per run (Containers/Dockerfile: real vsftpd, Squid and Dante on pinned Ubuntu 24.04) and tells
/// the tests whether Docker is there. Without Docker the tests skip, unless <c>KASI_REQUIRE_DOCKER=1</c> (set on the Ubuntu CI leg),
/// which turns a missing Docker into a failure, like <c>KASI_REQUIRE_VHD</c> does for the VHD test.
/// </summary>
public sealed class ConformanceImage : IAsyncLifetime
{
    public const string Name = "kasi-conformance:test";

    public string? Unavailable { get; private set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            var image = new ImageFromDockerfileBuilder()
                .WithName(Name)
                .WithDockerfileDirectory(Path.Combine(AppContext.BaseDirectory, "Containers"))
                .WithDockerfile("Dockerfile")
                .WithDeleteIfExists(false)
                .Build();
            await image.CreateAsync(TestContext.Current.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (Environment.GetEnvironmentVariable("KASI_REQUIRE_DOCKER") == "1")
            {
                throw new InvalidOperationException("KASI_REQUIRE_DOCKER=1 is set, but the conformance image could not be built.", ex);
            }

            Unavailable = $"Docker is not available here ({ex.GetType().Name}); set KASI_REQUIRE_DOCKER=1 to make that a failure.";
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Skips the calling test when Docker is not available.</summary>
    public void SkipWithoutDocker() => Assert.SkipWhen(Unavailable is not null, Unavailable ?? string.Empty);

    /// <summary>A container of the conformance image that runs <paramref name="shellCommand"/>.</summary>
    public static ContainerBuilder Role(string shellCommand) =>
        new ContainerBuilder(Name).WithEntrypoint("sh", "-c").WithCommand(shellCommand);
}

[CollectionDefinition(Name)]
public sealed class ConformanceGroup : ICollectionFixture<ConformanceImage>
{
    public const string Name = "Linux container conformance";
}
