namespace GSCLSP.Core.Services;

using GSCLSP.Core.Models;
using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<GscSymbol>))]
[JsonSerializable(typeof(GscSymbol))]
[JsonSerializable(typeof(List<GscLevelField>))]
[JsonSerializable(typeof(GscLevelField))]
internal partial class GscJsonContext : JsonSerializerContext
{
}