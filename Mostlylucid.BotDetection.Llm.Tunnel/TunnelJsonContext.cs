using System.Text.Json.Serialization;

namespace Mostlylucid.BotDetection.Llm.Tunnel;

[JsonSerializable(typeof(LlmTunnelConnectionPayload))]
[JsonSerializable(typeof(LlmNodeImportRequest))]
[JsonSerializable(typeof(LlmNodeImportResponse))]
[JsonSerializable(typeof(LlmNodeDescriptor))]
[JsonSerializable(typeof(LlmNodeModelInventory))]
[JsonSerializable(typeof(LlmNodeModelInfo))]
[JsonSerializable(typeof(LlmSignedInferenceRequest))]
[JsonSerializable(typeof(LlmSignedInferenceResponse))]
[JsonSerializable(typeof(LlmTunnelUsage))]
[JsonSerializable(typeof(LlmTunnelCompletionRequest))]
[JsonSerializable(typeof(LlmTunnelMessage))]
[JsonSerializable(typeof(LlmTunnelEnvelope))]
[JsonSerializable(typeof(LlmTunnelHealthResponse))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<LlmNodeModelInfo>))]
[JsonSerializable(typeof(List<LlmTunnelMessage>))]
public sealed partial class TunnelJsonContext : JsonSerializerContext { }
