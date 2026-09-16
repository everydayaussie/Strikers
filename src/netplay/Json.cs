using System.Text.Json.Serialization;

namespace Strikers.Netplay;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Frame))]
[JsonSerializable(typeof(SnapshotDto))]
[JsonSerializable(typeof(List<Preset>))]
[JsonSerializable(typeof(SetupContent))]
internal sealed partial class WireJson : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Preset))]
internal sealed partial class DumpJson : JsonSerializerContext
{
}

internal sealed class SetupContent
{
    [JsonPropertyName("challenge")] public string? Challenge { get; init; }

    [JsonPropertyName("army")] public List<string>? Army { get; init; }

    [JsonPropertyName("placements")] public List<int[]>? Placements { get; init; }

    [JsonPropertyName("board")] public List<int>? Board { get; init; }

    [JsonPropertyName("boardWidth")] public int BoardWidth { get; init; }

    [JsonPropertyName("boardHeight")] public int BoardHeight { get; init; }

    [JsonPropertyName("placementRows")] public int PlacementRows { get; init; }

    [JsonPropertyName("victoryPoints")] public int VictoryPoints { get; init; }

    [JsonPropertyName("draftPoints")] public int DraftPoints { get; init; }

    [JsonPropertyName("first")] public string? First { get; init; }

    [JsonPropertyName("name")] public string? Name { get; init; }
}
