using System.Text.Json;
using GSCLSP.Core.Models;

namespace GSCLSP.Core.Indexing;

public class BuiltInProvider
{
    private static readonly HashSet<string> VariadicBuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        "iprintln",
        "iprintlnbold"
    };

    private readonly Dictionary<string, GscSymbol> _builtInFunctions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GscSymbol> _builtInMethods = new(StringComparer.OrdinalIgnoreCase);

    public void LoadBuiltIns(string jsonPath)
    {
        if (!File.Exists(jsonPath))
        {
            Console.WriteLine($"[Warning] Built-ins file not found at: {jsonPath}");
            return;
        }

        try
        {
            _builtInFunctions.Clear();
            _builtInMethods.Clear();

            var json = File.ReadAllText(jsonPath);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                // Legacy format: [ { name, args: [ { name } ] } ]
                LoadEntries(root, _builtInFunctions, SymbolType.Function);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // New format: { functions: [...], methods: [...] }
                if (root.TryGetProperty("functions", out var functions) && functions.ValueKind == JsonValueKind.Array)
                {
                    LoadEntries(functions, _builtInFunctions, SymbolType.Function);
                }

                if (root.TryGetProperty("methods", out var methods) && methods.ValueKind == JsonValueKind.Array)
                {
                    LoadEntries(methods, _builtInMethods, SymbolType.Method);
                }
            }

            Console.Error.WriteLine($"Loaded {_builtInFunctions.Count} engine built-in functions and {_builtInMethods.Count} engine built-in methods.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Error] Failed to load built-ins: {ex.Message}");
        }
    }

    private static void LoadEntries(JsonElement entries, Dictionary<string, GscSymbol> target, SymbolType symbolType)
    {
        foreach (var element in entries.EnumerateArray())
        {
            if (!element.TryGetProperty("name", out var nameElement))
                continue;

            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var minArgs = ReadIntProperty(element, "minArgs");
            var maxArgs = ReadIntProperty(element, "maxArgs");
            var isVariadic = VariadicBuiltIns.Contains(name);
            var parms = ReadArgs(element, name, minArgs, maxArgs, isVariadic);

            target[name] = new GscSymbol(
                name,
                "Engine",
                0,
                parms,
                symbolType,
                MinArgs: minArgs,
                MaxArgs: maxArgs,
                IsVariadic: isVariadic
            );
        }
    }

    private static string ReadArgs(JsonElement element, string builtinName, int? minArgs, int? maxArgs, bool isVariadic)
    {
        if (element.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array)
        {
            var argNames = new List<string>();

            foreach (var arg in argsElement.EnumerateArray())
            {
                // New format: ["arg0", "arg1"]
                if (arg.ValueKind == JsonValueKind.String)
                {
                    var argName = arg.GetString();
                    if (!string.IsNullOrWhiteSpace(argName))
                        argNames.Add(argName);
                    continue;
                }

                // Legacy format: [{"name":"arg0"}]
                if (arg.ValueKind == JsonValueKind.Object &&
                    arg.TryGetProperty("name", out var argNameElement))
                {
                    var argName = argNameElement.GetString();
                    if (!string.IsNullOrWhiteSpace(argName))
                        argNames.Add(argName);
                }
            }

            if (argNames.Count > 0)
                return string.Join(", ", argNames);
        }

        var required = Math.Max(0, minArgs ?? 0);

        if (isVariadic)
        {
            var requiredForVariadic = Math.Max(1, required);
            var requiredArgs = Enumerable.Range(0, requiredForVariadic).Select(i => $"arg{i}");
            return string.Join(", ", requiredArgs.Append("...args"));
        }

        if (required <= 0)
            return string.Empty;

        return string.Join(", ", Enumerable.Range(0, required).Select(i => $"arg{i}"));
    }

    private static int? ReadIntProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return null;

        if (prop.ValueKind != JsonValueKind.Number)
            return null;

        return prop.TryGetInt32(out var value) ? value : null;
    }

    public GscSymbol? GetBuiltIn(string name, bool preferMethod = false)
    {
        if (preferMethod)
        {
            if (_builtInMethods.TryGetValue(name, out var methodSymbol))
                return methodSymbol;

            return _builtInFunctions.TryGetValue(name, out var fallbackFunction) ? fallbackFunction : null;
        }

        if (_builtInFunctions.TryGetValue(name, out var functionSymbol))
            return functionSymbol;

        return _builtInMethods.TryGetValue(name, out var fallbackMethod) ? fallbackMethod : null;
    }

    public IEnumerable<string> GetNames() =>
        _builtInFunctions.Keys.Concat(_builtInMethods.Keys).Distinct(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<GscSymbol> GetAll(bool preferMethodsFirst = false)
    {
        return preferMethodsFirst
            ? _builtInMethods.Values.Concat(_builtInFunctions.Values)
            : _builtInFunctions.Values.Concat(_builtInMethods.Values);
    }
}