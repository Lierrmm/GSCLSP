using System.Text.Json;
using GSCLSP.Core.Models;

namespace GSCLSP.Core.Indexing;

public class BuiltInProvider
{
    private Dictionary<string, GscSymbol> _builtIns = new(StringComparer.OrdinalIgnoreCase);

    public void LoadBuiltIns(string jsonPath)
    {
        if (!File.Exists(jsonPath))
        {
            Console.WriteLine($"[Warning] Built-ins file not found at: {jsonPath}");
            return;
        }

        try
        {
            var json = File.ReadAllText(jsonPath);
            using var document = JsonDocument.Parse(json);

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("name", out var nameElement))
                {
                    var name = nameElement.GetString()!;

                    string parms = "";
                    if (element.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array)
                    {
                        var argNames = new List<string>();
                        foreach (var arg in argsElement.EnumerateArray())
                        {
                            if (arg.TryGetProperty("name", out var argName))
                            {
                                argNames.Add(argName.GetString() ?? "");
                            }
                        }
                        parms = string.Join(", ", argNames);
                    }

                    _builtIns[name] = new GscSymbol(
                        name,
                        "Engine",
                        0,
                        parms,
                        SymbolType.Function
                    );
                }
            }
            Console.Error.WriteLine($"Loaded {_builtIns.Count} engine built-ins.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Error] Failed to load built-ins: {ex.Message}");
        }
    }

    public GscSymbol? GetBuiltIn(string name)
        => _builtIns.TryGetValue(name, out var symbol) ? symbol : null;

    public IEnumerable<string> GetNames() => _builtIns.Keys;

    public IEnumerable<GscSymbol> GetAll() => _builtIns.Values;
}