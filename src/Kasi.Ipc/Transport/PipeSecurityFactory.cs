using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Kasi.Ipc.Transport;

internal static class PipeSecurityFactory
{
    /// <summary>
    /// Exactly two entries: allow the user read, write and the right to create further instances (the server opens one instance per client),
    /// and deny the NETWORK SID so the pipe cannot be reached over SMB even if a remote client is ever accepted. Nothing for Everyone,
    /// Users, Administrators or SYSTEM: only this user's processes can open it (TRD 9.3).
    /// </summary>
    public static PipeSecurity Create(SecurityIdentifier user)
    {
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }
}
