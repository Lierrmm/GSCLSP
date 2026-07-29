using GSCLSP.Core.Models;
using GSCLSP.Core.Services;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Core.Indexing;

public partial class GscIndexer
{
    // level fields from the dump, deduplicated by name; persisted to levelfields.json
    private readonly Dictionary<string, GscLevelField> _dumpLevelFields = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _levelFieldsLock = new();

    public static List<GscLevelField> ScanLevelFields(string[] lines, string filePath)
    {
        var result = new List<GscLevelField>();

        for (int i = 0; i < lines.Length; i++)
        {
            var codeLine = StripTrailingLineComment(lines[i]);
            if (codeLine.Length == 0) continue;

            foreach (System.Text.RegularExpressions.Match match in LevelFieldAssignmentRegex().Matches(codeLine))
            {
                var name = match.Groups["name"].Value;
                var isDirectAssignment = match.Groups["access"].Length == 0 && match.Groups["op"].Value == "=";
                var value = isDirectAssignment ? match.Groups["value"].Value.Trim() : string.Empty;

                result.Add(new GscLevelField(name, filePath, i + 1, value));
            }
        }

        return result;
    }

    private void AddDumpLevelField(GscLevelField field)
    {
        lock (_levelFieldsLock)
        {
            // prefer a definition that shows a direct `level.x = value` assignment
            if (_dumpLevelFields.TryGetValue(field.Name, out var existing) &&
                (existing.Value.Length > 0 || field.Value.Length == 0))
                return;

            _dumpLevelFields[field.Name] = field;
        }
    }

    private void ClearDumpLevelFields()
    {
        lock (_levelFieldsLock)
        {
            _dumpLevelFields.Clear();
        }
    }

    private void IndexLevelFieldsFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return;

        var files = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(IsScriptFile);

        foreach (var file in files)
        {
            if (GscCompiledScriptDetector.IsCompiledFile(file))
                continue;

            try
            {
                foreach (var field in ScanLevelFields(File.ReadAllLines(file), file))
                    AddDumpLevelField(field);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }
    }

    private void SaveLevelFieldsCache(string outputPath)
    {
        try
        {
            List<GscLevelField> snapshot;
            lock (_levelFieldsLock)
            {
                snapshot = [.. _dumpLevelFields.Values];
            }

            File.WriteAllText(outputPath, JsonSerializer.Serialize(snapshot, GscJsonContext.Default.ListGscLevelField));
            _logger.LogDebug("Saved {Count} level fields to {Path}.", snapshot.Count, outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write level fields cache {Path}", outputPath);
        }
    }

    private void LoadLevelFieldsCache(string jsonPath)
    {
        try
        {
            var fields = JsonSerializer.Deserialize(File.ReadAllText(jsonPath), GscJsonContext.Default.ListGscLevelField);
            if (fields == null) return;

            foreach (var field in fields)
                AddDumpLevelField(field);

            _logger.LogDebug("Indexer loaded {Count} level fields from JSON.", fields.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read level fields cache {Path}", jsonPath);
        }
    }

    public GscLevelField? ResolveLevelField(string name)
    {
        GscLevelField? fallback = null;

        foreach (var map in _workspaceFileMaps.Values)
        {
            foreach (var field in map.LevelFields)
            {
                if (!field.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (field.Value.Length > 0) return field;
                fallback ??= field;
            }
        }

        if (fallback != null) return fallback;

        lock (_levelFieldsLock)
        {
            return _dumpLevelFields.TryGetValue(name, out var dumpField) ? dumpField : null;
        }
    }

    public List<GscLevelField> GetAllLevelFields()
    {
        var seen = new Dictionary<string, GscLevelField>(StringComparer.OrdinalIgnoreCase);

        foreach (var map in _workspaceFileMaps.Values)
        {
            foreach (var field in map.LevelFields)
            {
                if (!seen.TryGetValue(field.Name, out var existing) ||
                    (existing.Value.Length == 0 && field.Value.Length > 0))
                    seen[field.Name] = field;
            }
        }

        lock (_levelFieldsLock)
        {
            foreach (var field in _dumpLevelFields.Values)
                seen.TryAdd(field.Name, field);
        }

        return [.. seen.Values];
    }
}
