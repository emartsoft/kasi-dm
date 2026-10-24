using System.Security.Principal;
using Kasi.Ipc.Transport;

namespace Kasi.Ipc.Tests;

/// <summary>
/// The test suffix (<c>KASI_ENDPOINT_SUFFIX</c>) lets a test instance of Kasi and the native host meet on pipes of their own, so
/// they never reach a Kasi the developer is running (M2.13c, ADR 0026). Without it the names are the per-user names of TRD 5.1.
/// </summary>
[Trait("Requirement", "KASI-BRW-001")]
public class PipeSuffixTests
{
    private static readonly SecurityIdentifier _user = new("S-1-5-21-1-2-3-1001");

    [Fact]
    public void Without_a_suffix_the_names_are_the_plain_per_user_names()
    {
        KasiPipeNames.WithSuffix(KasiPipeNames.For(_user), null).Should().Be("kasi-S-1-5-21-1-2-3-1001");
        KasiPipeNames.WithSuffix(KasiPipeNames.For(_user), "").Should().Be("kasi-S-1-5-21-1-2-3-1001");
    }

    [Fact]
    public void A_suffix_is_appended_to_the_browser_pipe_and_to_the_hand_off_pipe()
    {
        KasiPipeNames.WithSuffix(KasiPipeNames.For(_user), "ui1a2b").Should().Be("kasi-S-1-5-21-1-2-3-1001-ui1a2b");
        KasiPipeNames.WithSuffix(KasiPipeNames.HandOffFor(_user), "ui1a2b").Should().Be("kasi-S-1-5-21-1-2-3-1001-open-ui1a2b");
    }
}
