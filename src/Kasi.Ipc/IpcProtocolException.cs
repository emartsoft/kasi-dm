using Kasi.Ipc.Messages;

namespace Kasi.Ipc;

/// <summary>
/// A message was rejected. The text is a fixed sentence built from the error code only: the offending JSON, URL or cookie value is never
/// copied into it (KASI-NFR-010, CLAUDE.md rule 3), so it is safe to log and to send back in an <c>error</c> reply.
/// </summary>
public sealed class IpcProtocolException(IpcErrorCode code) : Exception($"The message was rejected ({code}).")
{
    public IpcErrorCode Code { get; } = code;
}
