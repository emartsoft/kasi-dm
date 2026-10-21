using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Kasi.Ipc.Transport;

/// <summary>Finds the Windows user who owns the process serving a pipe, so a client can refuse a squatter before it sends a byte.</summary>
internal static partial class ServerIdentity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    /// <summary>The owner of the server process, or null when it cannot be read (the client then refuses: it fails closed).</summary>
    public static SecurityIdentifier? OwnerOfServer(NamedPipeClientStream client)
    {
        if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out var processId))
        {
            return null;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                return null;
            }

            try
            {
                GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var needed);
                if (needed == 0)
                {
                    return null;
                }

                var buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!GetTokenInformation(token, TokenUser, buffer, needed, out _))
                    {
                        return null;
                    }

                    // TOKEN_USER starts with the pointer to the user's SID
                    return new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, uint length, out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
