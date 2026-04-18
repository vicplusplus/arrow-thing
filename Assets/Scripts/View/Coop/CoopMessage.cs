using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// WebSocket message envelope for co-op. Mirrors the server's CoopMessage shape:
/// <c>{ type, seq, payload }</c>. Uses Newtonsoft.Json so payloads can be
/// arbitrary JSON objects without ahead-of-time type definitions.
/// </summary>
public class CoopMessage
{
    [JsonProperty("type")]
    public string Type { get; set; } = "";

    [JsonProperty("seq")]
    public long Seq { get; set; }

    [JsonProperty("payload", NullValueHandling = NullValueHandling.Ignore)]
    public JToken Payload { get; set; }

    public static CoopMessage Hello(long seq) => new CoopMessage { Type = "hello", Seq = seq };

    public static CoopMessage Echo(long seq, object payload) =>
        new CoopMessage
        {
            Type = "echo",
            Seq = seq,
            Payload = JToken.FromObject(payload),
        };

    public static CoopMessage Goodbye() => new CoopMessage { Type = "goodbye" };

    public static CoopMessage Heartbeat(bool focused, DateTime lastInputUtc) =>
        new CoopMessage
        {
            Type = "heartbeat",
            Payload = JToken.FromObject(
                new { focused, lastInputTsUtc = lastInputUtc.ToString("O") }
            ),
        };

    public static CoopMessage ClearAttempt(float tapX, float tapY, long clientSeq) =>
        new CoopMessage
        {
            Type = "clear_attempt",
            Payload = JToken.FromObject(
                new
                {
                    tapX,
                    tapY,
                    clientTsUtc = DateTime.UtcNow.ToString("O"),
                    clientSeq,
                }
            ),
        };

    public string Serialize() => JsonConvert.SerializeObject(this);

    public static CoopMessage Deserialize(string json) =>
        JsonConvert.DeserializeObject<CoopMessage>(json);
}
