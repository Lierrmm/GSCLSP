using System.Diagnostics;
using GSCLSP.Core.Indexing;
using GSCLSP.Core.Models;
using GSCLSP.Core.Services;

#if DEBUG
// --- 1. SETUP & INITIALIZATION ---
var indexer = new GscIndexer();

// Define your paths
string builtInPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "iw4_builtins.json");
string dumpPath = @"D:\Xbox360\code\gsc\MW2 Dump";
indexer.ExportIndexToJson(dumpPath, "output_dump.json");
return;

Console.Title = "GSCLSP - MW2 Script Intelligence";
Console.WriteLine("===============================================");
Console.WriteLine("          GSC Language Server Project          ");
Console.WriteLine("===============================================");

// --- 2. LOAD ENGINE BUILT-INS ---
Console.WriteLine("\n[1/2] Loading Engine Built-ins...");
indexer.BuiltIns.LoadBuiltIns(builtInPath);

// --- 3. INDEX FILES ---
Console.WriteLine($"[2/2] Indexing MW2 Scripts at: {dumpPath}...");
var indexTime = indexer.IndexFolder(dumpPath);

Console.WriteLine("-----------------------------------------------");
Console.WriteLine($"Symbols Indexed: {indexer.SymbolCount}");
Console.WriteLine($"Indexing Time:   {indexTime.TotalMilliseconds:N2}ms");
Console.WriteLine("-----------------------------------------------");

// --- 4. CONTEXT SETUP ---
Console.WriteLine("\nEnter a file path to 'enter' its context");
Console.WriteLine("(e.g., maps/mp/gametypes/_rank.gsc)");
Console.Write("Context Path > ");
var contextFile = Console.ReadLine()?.Replace("\\", "/").Trim() ?? "";

Console.WriteLine($"\n[Active Context: {contextFile}]");
Console.WriteLine("Commands:");
Console.WriteLine("  - [Paste GSC Line] : Resolves the function call.");
Console.WriteLine("  - find <name>      : Finds all files calling that function.");
Console.WriteLine("  - change           : Switch active context file.");
Console.WriteLine("  - exit             : Quit the application.");

// --- 5. THE INTERACTION LOOP ---
while (true)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("\nGSCLSP > ");
    Console.ResetColor();

    var input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input)) continue;

    // Command: Exit
    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

    // Command: Change Context
    if (input.Equals("change", StringComparison.OrdinalIgnoreCase))
    {
        Console.Write("New Context Path > ");
        contextFile = Console.ReadLine()?.Replace("\\", "/").Trim() ?? "";
        Console.WriteLine($"[Context updated to: {contextFile}]");
        continue;
    }

    // Command: Find References
    if (input.StartsWith("find ", StringComparison.OrdinalIgnoreCase))
    {
        var funcToFind = input.Substring(5).Trim();
        Console.WriteLine($"Searching for references to '{funcToFind}'...");
        indexer.FindReferences(funcToFind);
        continue;
    }

    // --- 6. RESOLUTION LOGIC ---
    var sw = Stopwatch.StartNew();
    var resolution = indexer.ResolveFromLine(contextFile, input);
    sw.Stop();

    if (resolution.Type != ResolutionType.NotFound)
    {
        var s = resolution.Symbol!;

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n>>> MATCH FOUND: {s.Name}({s.Parameters})");
        Console.ResetColor();

        Console.WriteLine($"    Source: {s.FilePath}:{s.LineNumber}");
        Console.WriteLine($"    Type:   {resolution.Type}");
        Console.WriteLine($"    Time:   {sw.Elapsed.TotalMilliseconds:N4}ms");

        // --- 7. ACTION: OPEN IN EDITOR ---
        if (s.FilePath != "Engine")
        {
            Console.WriteLine("\n[O] Open in VS Code | [Any other key] Skip");
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.O)
            {
                Console.WriteLine("Launching VS Code...");
                EditorService.OpenAtLocation(s);
            }
        }
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("\n[!] Could not resolve function call.");
        Console.WriteLine("    Ensure the function is defined locally, #included, or is an engine built-in.");
        Console.ResetColor();
    }
}

Console.WriteLine("Exiting GSCLSP...");
#endif