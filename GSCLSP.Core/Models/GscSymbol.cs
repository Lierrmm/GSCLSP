namespace GSCLSP.Core.Models;

public enum SymbolType { Function, Method, Pointer }

public record GscSymbol(
    string Name,
    string FilePath,
    int LineNumber,
    string Parameters,
    SymbolType Type,
    string Documentation = "",
    int? MinArgs = null,
    int? MaxArgs = null,
    bool IsVariadic = false
);