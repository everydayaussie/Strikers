using System.Text.Json.Serialization;

namespace Strikers.Core;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<StrikeBoard>))]
[JsonSerializable(typeof(List<Army>))]
[JsonSerializable(typeof(Settings))]
public sealed partial class StoreJson : JsonSerializerContext
{
}
