using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;

namespace Kasi.App.Startup;

/// <summary>
/// The browser pipe's handler until PR 2.8b brings the real one: <c>hello</c> gets <c>rules</c> that intercept nothing (an extension that is
/// told to take nothing never pauses a download Kasi cannot yet accept); every other message gets an <c>error</c>, so a browser falls back to
/// its own download instead of waiting for an ack that will not come.
/// </summary>
internal static class BrowserPipeHandler
{
    public static IpcMessageHandler Create() => (_, message, _) =>
    {
        IpcMessage reply = message is Hello
            ? new Rules
            {
                Id = message.Id,
                Enabled = false,
                Extensions = [],
                MimeTypes = [],
                ExcludedSites = [],
                BypassKey = BypassKey.Alt,
                AckTimeoutMs = 2000,
            }
            : new IpcError { Id = message.Id, Code = IpcErrorCode.Internal };
        return ValueTask.FromResult<IpcMessage?>(reply);
    };
}
