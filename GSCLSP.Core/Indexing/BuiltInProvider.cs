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

    private BuiltInSnapshot _snapshot = new(
        new Dictionary<string, GscSymbol>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, GscSymbol>(StringComparer.OrdinalIgnoreCase));

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
            var root = document.RootElement;
            var builtInFunctions = new Dictionary<string, GscSymbol>(StringComparer.OrdinalIgnoreCase);
            var builtInMethods = new Dictionary<string, GscSymbol>(StringComparer.OrdinalIgnoreCase);

            if (root.ValueKind == JsonValueKind.Array)
            {
                // Legacy format: [ { name, args: [ { name } ] } ]
                LoadEntries(root, builtInFunctions, SymbolType.Function);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // New format: { functions: [...], methods: [...] }
                if (root.TryGetProperty("functions", out var functions) && functions.ValueKind == JsonValueKind.Array)
                {
                    LoadEntries(functions, builtInFunctions, SymbolType.Function);
                }

                if (root.TryGetProperty("methods", out var methods) && methods.ValueKind == JsonValueKind.Array)
                {
                    LoadEntries(methods, builtInMethods, SymbolType.Method);
                }
            }
            else
            {
                Console.Error.WriteLine($"[Warning] Unexpected built-ins JSON root kind '{root.ValueKind}' in: {jsonPath}");
                return;
            }

            ReplaceSnapshot(builtInFunctions, builtInMethods);
            Console.Error.WriteLine($"Loaded {builtInFunctions.Count} engine built-in functions and {builtInMethods.Count} engine built-in methods.");
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
        var snapshot = _snapshot;

        if (preferMethod)
        {
            if (snapshot.Methods.TryGetValue(name, out var methodSymbol))
                return methodSymbol;

            return snapshot.Functions.TryGetValue(name, out var fallbackFunction) ? fallbackFunction : null;
        }

        if (snapshot.Functions.TryGetValue(name, out var functionSymbol))
            return functionSymbol;

        return snapshot.Methods.TryGetValue(name, out var fallbackMethod) ? fallbackMethod : null;
    }

    public IEnumerable<string> GetNames() =>
        _snapshot.Functions.Keys.Concat(_snapshot.Methods.Keys).Distinct(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<GscSymbol> GetAll(bool preferMethodsFirst = false)
    {
        var snapshot = _snapshot;
        return preferMethodsFirst
            ? snapshot.Methods.Values.Concat(snapshot.Functions.Values)
            : snapshot.Functions.Values.Concat(snapshot.Methods.Values);
    }

    public void LoadNameOnlyBuiltIns(IEnumerable<string> functionNames, IEnumerable<string> methodNames, IEnumerable<string> tokenNames)
    {
        var builtInFunctions = new Dictionary<string, GscSymbol>(StringComparer.OrdinalIgnoreCase);
        var builtInMethods = new Dictionary<string, GscSymbol>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in functionNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            builtInFunctions[name] = new GscSymbol(name, "Engine", 0, string.Empty, SymbolType.Function);
        }

        foreach (var name in methodNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            builtInMethods[name] = new GscSymbol(name, "Engine", 0, string.Empty, SymbolType.Method);
        }

        /* 
         * ignore for now - these include file paths and other non-function tokens that we don't want to treat as built-ins
         * if we want to add them back in the future, we'll need to enhance GscSymbol to support non-function types and decide how to handle them in the rest of the codebase
         */
        //foreach (var name in tokenNames)
        //{
        //    if (string.IsNullOrWhiteSpace(name)) continue;
        //    builtInFunctions[name] = new GscSymbol(name, "Engine", 0, string.Empty, SymbolType.Function);
        //}

        ReplaceSnapshot(builtInFunctions, builtInMethods);
    }

    private void ReplaceSnapshot(Dictionary<string, GscSymbol> functions, Dictionary<string, GscSymbol> methods)
    {
        Interlocked.Exchange(ref _snapshot, new BuiltInSnapshot(functions, methods));
    }

    private sealed record BuiltInSnapshot(
        Dictionary<string, GscSymbol> Functions,
        Dictionary<string, GscSymbol> Methods);
}
