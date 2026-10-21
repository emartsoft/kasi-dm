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

    internal static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("The current Windows user has no SID.");
    }
}
