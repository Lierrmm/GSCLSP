namespace GSCLSP.Core.Services;

using GSCLSP.Core.Models;
using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<GscSymbol>))]
[JsonSerializable(typeof(GscSymbol))]
internal partial class GscJsonContext : JsonSerializerContext
{
}