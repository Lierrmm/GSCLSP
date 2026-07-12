namespace GSCLSP.Core.Models;

public record GscLevelField(
    string Name,
    string FilePath,
    int LineNumber,
    string Value
);
