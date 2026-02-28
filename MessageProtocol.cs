using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace DigitRaverHelperMCP;

// Mirror of Bridge protocol types — matches MessageEnvelope.cs wire format exactly
[JsonConverter(typeof(StringEnumConverter))]
public enum MessageType
{
    command,
    subscribe,
    unsubscribe,
    @event,
    result,
    error
}

[Serializable]
public class MessageEnvelope
{
    [JsonProperty("id")]
    public string Id = "";

    [JsonProperty("type")]
    public MessageType Type;

    [JsonProperty("domain")]
    public string Domain = "";

    [JsonProperty("action")]
    public string Action = "";

    [JsonProperty("payload", NullValueHandling = NullValueHandling.Ignore)]
    public JObject? Payload;

    [JsonProperty("timestamp", NullValueHandling = NullValueHandling.Ignore)]
    public string? Timestamp;
}
