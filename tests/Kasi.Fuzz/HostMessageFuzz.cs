#pragma warning disable CA1416 // the host is Windows-only like Kasi.Ipc's pipe client; this target never touches a pipe, and the nightly runs on Windows
using System.Buffers.Binary;
using Kasi.Ipc;
using Kasi.Ipc.Messages;
using Kasi.NativeHost.Framing;
using Kasi.NativeHost.Relay;

namespace Kasi.Fuzz;

/// <summary>The native host broke a rule of KASI-BRW-006 / KASI-NFR-010 (TC-BRW-006) for some input.</summary>
public sealed class HostInvariantException(string message) : Exception(message);

/// <summary>
/// The fuzz target for the native messaging host's frame reader, strict parser and error mapping (TPL 7.2, TC-BRW-006) and the one place
/// its invariants live: the SharpFuzz harness, the unit tests and the saved regression corpus all call <see cref="Run"/>. The first byte
/// picks the mode. Even: the rest is the payload of one well-formed frame, so the parser and the error replies are exercised. Odd: the rest
/// is raw stdin, so the frame reader is. Kasi is never reachable, so a message the parser accepts comes back as <c>APP_NOT_RUNNING</c>.
/// Any exception, including a violated invariant, is a finding.
/// </summary>
public static class HostMessageFuzz
{
    private static readonly TimeSpan _hang = TimeSpan.FromSeconds(30);

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        var raw = (data[0] & 1) == 1;
        var body = data[1..].ToArray();
        var stdin = raw ? body : Frame(body);
        var stdout = new MemoryStream();
        var log = new StringWriter();
        int exit;
        using (var writer = new NativeFrameWriter(stdout))
        using (var relay = new HostRelay(new MemoryStream(stdin), writer, new Unreachable(), log))
        {
            // the libFuzzer callback is synchronous, and a host that never returns is a finding too
#pragma warning disable VSTHRD002
            var run = Task.Run(() => relay.RunAsync(CancellationToken.None));
            if (!run.Wait(_hang))
            {
                throw new HostInvariantException("The host did not finish within 30 s.");
            }

            exit = run.Result;
#pragma warning restore VSTHRD002
        }

        var replies = ReadReplies(stdout.ToArray());
        CheckLog(log.ToString());
        if (raw)
        {
            Expect(exit is 0 or HostRelay.ExitFraming, $"Unexpected exit code {exit} for raw stdin.");
            foreach (var reply in replies)
            {
                Expect(reply.Code is IpcErrorCode.InvalidMessage or IpcErrorCode.UnsupportedVersion or IpcErrorCode.AppNotRunning, $"Unexpected reply code {reply.Code}.");
            }

            return;
        }

        if (body.Length == 0)
        {
            Expect(exit == HostRelay.ExitFraming && replies.Count == 0, "A zero-length frame must end the host with no output.");
            return;
        }

        Expect(exit == 0 && replies.Count == 1, $"A well-framed message must leave the host running with exactly one reply (exit {exit}, {replies.Count} replies).");
        CheckReply(body, replies[0]);
    }

    /// <summary>One valid example of every message type, as the browser would send them: the seed corpus of the nightly run.</summary>
    public static IEnumerable<byte[]> Seeds()
    {
        IpcCookie cookie = new() { Name = "n", Value = "v", Domain = "example.com", Path = "/", Secure = true, HttpOnly = false };
        IpcMessage[] messages =
        [
            new Hello { Id = "h1", Client = BrowserClient.Chrome, ExtensionVersion = "1.0.0", ProtocolMin = 1, ProtocolMax = 1 },
            new AddDownload { Id = "d1", Url = "https://example.com/a.zip", FinalUrl = "https://example.com/a.zip", Referrer = "https://example.com/", Filename = "a.zip", Mime = "application/zip", UserAgent = "ua", Cookies = [cookie] },
            new AddBatch { Id = "b1", Referrer = "https://example.com/", Links = [new IpcLink { Url = "https://example.com/1.zip", Filename = "1.zip" }], Cookies = [cookie], UserAgent = "ua" },
            new MediaDetected { Id = "m1", TabId = 7, PageUrl = "https://example.com/", Kind = MediaKind.Hls, Url = "https://example.com/a.m3u8", Title = "t" },
            new CaptureMedia { Id = "c1", TabId = 7, Url = "https://example.com/a.mpd", Kind = MediaKind.Dash, Cookies = [], Referrer = "https://example.com/", UserAgent = "ua" },
            new LinkRefreshed { Id = "l1", JobId = "j1", Url = "https://example.com/new.zip", Cookies = [cookie] },
            new OpenSettings { Id = "o1", Section = SettingsSection.Browser },
            new Ack { Id = "k1", Ok = true },
            new IpcError { Id = "e1", Code = IpcErrorCode.Internal, Detail = "x" },
            new Rules { Id = "r1", Enabled = true, Extensions = ["zip"], MimeTypes = [], ExcludedSites = [], BypassKey = BypassKey.Alt, AckTimeoutMs = 2000 },
            new RefreshLink { Id = "f1", JobId = "j1", ReferrerUrl = "https://example.com/" },
        ];
        return messages.Select(IpcSerializer.Serialize);
    }

    private static void CheckReply(byte[] payload, IpcError reply)
    {
        // the oracle: the strict parse alone decides what the host must answer
        IpcErrorCode expected;
        string? fixedText = null;
        try
        {
            var message = IpcSerializer.Deserialize(payload);
            expected = message is Rules or RefreshLink ? IpcErrorCode.InvalidMessage : IpcErrorCode.AppNotRunning;
        }
        catch (IpcProtocolException ex)
        {
            expected = ex.Code;
            fixedText = ex.Message;
        }

        Expect(reply.Code == expected, $"The host answered {reply.Code}, the strict parse says {expected}.");
        if (fixedText is not null)
        {
            Expect(reply.Detail == fixedText, "The detail of a refusal must be the fixed text, never the input.");
        }
    }

    private static List<IpcError> ReadReplies(byte[] output)
    {
        var replies = new List<IpcError>();
        var at = 0;
        while (at < output.Length)
        {
            Expect(output.Length - at >= 4, "Stdout ends inside a frame header.");
            var length = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(at, 4));
            Expect(length is >= 1 and <= NativeFrameWriter.MaxFrameBytes && at + 4 + (long)length <= output.Length, "Stdout holds a partial or oversized frame.");
            var message = IpcSerializer.Deserialize(output.AsSpan(at + 4, (int)length));
            Expect(message is IpcError, $"Stdout carried a {message.GetType().Name}, expected only error replies.");
            replies.Add((IpcError)message);
            at += 4 + (int)length;
        }

        return replies;
    }

    private static void CheckLog(string log)
    {
        foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Expect(line.StartsWith("host:", StringComparison.Ordinal) && line.Length < 200, "The log must hold only the host's fixed lines.");
        }
    }

    private static byte[] Frame(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    private static void Expect(bool condition, string rule)
    {
        if (!condition)
        {
            throw new HostInvariantException(rule);
        }
    }

    private sealed class Unreachable : IHostPipeConnector
    {
        public Task<IHostPipe> ConnectAsync(CancellationToken cancellationToken) => throw new AppUnavailableException(IpcErrorCode.AppNotRunning);
    }
}
