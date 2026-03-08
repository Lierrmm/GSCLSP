namespace GSCLSP.Core.Models;

public enum ResolutionType { Local, Included, Global, NotFound }

public record GscResolution(
    GscSymbol? Symbol,
    ResolutionType Type,
    string? SourceFile = null
);