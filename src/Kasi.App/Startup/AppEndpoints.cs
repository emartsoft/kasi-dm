using System.Security.Principal;
using Kasi.Ipc.Transport;

namespace Kasi.App.Startup;

/// <summary>The three per-user names of a running Kasi: the instance mutex, the browser pipe and the argument hand-off pipe.</summary>
internal sealed record AppEndpoints(string InstanceLock, string BrowserPipe, string HandOffPipe)
{
    /// <summary>Tests set this so a test instance never meets a real Kasi on the same machine; it only appends to the three names.</summary>
    public const string TestSuffixVariable = KasiPipeNames.TestSuffixVariable;

    public static AppEndpoints ForCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("The current Windows user has no SID.");
        return For(sid, Environment.GetEnvironmentVariable(TestSuffixVariable));
    }

    internal static AppEndpoints For(string sid, string? suffix)
    {
        var tail = string.IsNullOrEmpty(suffix) ? "" : "-" + suffix;
        var user = new SecurityIdentifier(sid);
        return new AppEndpoints(
            @"Local\Kasi-" + sid + tail,
            KasiPipeNames.For(user) + tail,
            KasiPipeNames.HandOffFor(user) + tail);
    }
}
