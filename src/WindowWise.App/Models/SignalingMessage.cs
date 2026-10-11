namespace WindowWise.Models;

using System.Text.Json.Serialization;

/// <summary>
/// These are types that are required for signaling server
/// type : types of msg
/// from : sender id
/// to : receiver id
/// room id : group that is being shared
/// payload : data being sent
/// </summary>

public sealed class SignalingMessage
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("from")]
    public required string From { get; init; }

    [JsonPropertyName("to")]
    public string? To { get; init; }

    [JsonPropertyName("roomId")]
    public string? RoomId { get; init; }

    [JsonPropertyName("payload")]
    public object? Payload { get; init; }

}
