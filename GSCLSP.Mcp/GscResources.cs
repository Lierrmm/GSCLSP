using System.ComponentModel;
using ModelContextProtocol.Server;

namespace GSCLSP.Mcp;

[McpServerResourceType]
[McpServerToolType]
public static class GscResources
{
    private const string PrimerRelativePath = "Resources/gsc-primer.md";

    [McpServerResource(UriTemplate = "gsclsp://primer", Name = "GSC Language Primer", MimeType = "text/markdown")]
    [Description("A primer that teaches an AI client how to read and write GSC (Call of Duty scripting) through this server: syntax, per-game quirks, special literals, the runtime model, and how to verify facts with the GSC tools. Read this before generating GSC code.")]
    public static string GetPrimer() => ReadPrimer();

    [McpServerTool(Name = "get_gsc_primer")]
    [Description("Return the GSC language primer as markdown. Call this ONCE per session before generating or editing GSC code, if the 'gsclsp://primer' resource is not available to you. Covers GSC syntax, per-game differences, special literals (&func, #\"hash\", /# dev blocks #/), the level/self/entity runtime model, hashed dumps (_id_XXXX names) and how to identify hashed functions, and how to use the other GSC tools to verify against the active game.")]
    public static string GetGscPrimer() => ReadPrimer();

    private static string ReadPrimer()
    {
        string path = Path.Combine(AppContext.BaseDirectory, PrimerRelativePath);
        return File.Exists(path)
            ? File.ReadAllText(path)
            : "GSC primer resource not found on disk.";
    }
}
