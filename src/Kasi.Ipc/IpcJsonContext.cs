using System.Text.Json.Serialization;
using Kasi.Ipc.Messages;

namespace Kasi.Ipc;

/// <summary>
/// The source-generated serializer for the protocol (Native AOT, no reflection). The settings are the strictness the protocol relies on:
/// unknown members and duplicate properties are errors, required members must be present, nulls are not accepted for non-nullable
/// members, and <c>type</c> may come anywhere in the object because a browser does not promise an order.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowOutOfOrderMetadataProperties = true,
    AllowDuplicateProperties = false,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(IpcMessage))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;
