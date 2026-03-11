namespace GSCLSP.Core.Models;

public class GscFileMap
{
    public string FilePath { get; set; } = string.Empty;
    public List<string> Includes { get; set; } = [];
    public List<string> Inlines { get; set; } = [];
    public List<GscSymbol> LocalSymbols { get; set; } = [];
}