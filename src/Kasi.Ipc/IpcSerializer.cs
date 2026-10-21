using System.Text.Json;
using Kasi.Ipc.Messages;

namespace Kasi.Ipc;

/// <summary>
/// Turns UTF-8 JSON into a message and back, rejecting anything the protocol does not define (KASI-NFR-010). A rejected message throws
/// <see cref="IpcProtocolException"/> with a code and nothing of the input; no handler ever sees a half-valid message.
/// </summary>
public static class IpcSerializer
{
    /// <summary>Parses one message. <see cref="IpcErrorCode.UnsupportedVersion"/> for a <c>v</c> other than 1, otherwise <see cref="IpcErrorCode.InvalidMessage"/>.</summary>
    public static IpcMessage Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        IpcMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(utf8Json, IpcJsonContext.Default.IpcMessage);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // a message of another version may have another shape, so say "version" when that is what is wrong with it
            throw new IpcProtocolException(HasOtherVersion(utf8Json) ? IpcErrorCode.UnsupportedVersion : IpcErrorCode.InvalidMessage);
        }

        if (message is null)
        {
            throw new IpcProtocolException(IpcErrorCode.InvalidMessage);
        }

        if (message.V != IpcProtocol.Version)
        {
            throw new IpcProtocolException(IpcErrorCode.UnsupportedVersion);
        }

        IpcValidator.Check(message);
        return message;
    }

    /// <summary>Writes one message as UTF-8 JSON after the same checks a received message gets, so a bug cannot send what the peer would refuse.</summary>
    public static byte[] Serialize(IpcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.V != IpcProtocol.Version)
        {
            throw new IpcProtocolException(IpcErrorCode.UnsupportedVersion);
        }

        IpcValidator.Check(message);
        return JsonSerializer.SerializeToUtf8Bytes(message, IpcJsonContext.Default.IpcMessage);
    }

    private static bool HasOtherVersion(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isVersion = reader.ValueTextEquals("v"u8);
                reader.Read();
                if (isVersion)
                {
                    return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var v) && v != IpcProtocol.Version;
                }

                reader.Skip();
            }
        }
        catch (JsonException)
        {
            // not even JSON: it is simply invalid
        }

        return false;
    }
}
