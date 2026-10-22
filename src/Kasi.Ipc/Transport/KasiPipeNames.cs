using System.Security.Principal;

namespace Kasi.Ipc.Transport;

public static class KasiPipeNames
{
    /// <summary>The pipe name for a user: <c>kasi-&lt;SID&gt;</c>, so each Windows user has a pipe of their own (TRD 5.1).</summary>
    public static string For(SecurityIdentifier user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return "kasi-" + user.Value;
    }

    public static string ForCurrentUser() => For(CurrentUser());

    /// <summary>The argument hand-off pipe for a user: <c>kasi-&lt;SID&gt;-open</c>, a second pipe so the browser protocol keeps one purpose (ADR 0023).</summary>
    public static string HandOffFor(SecurityIdentifier user) => For(user) + "-open";

    public static string HandOffForCurrentUser() => HandOffFor(CurrentUser());

    internal static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("The current Windows user has no SID.");
    }
}
