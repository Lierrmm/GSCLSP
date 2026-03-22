using GSCLSP.Core.Indexing;
using GSCLSP.Core.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCLSP.Server.Handlers;

internal static class GscCompletionItemFactory
{
    private static readonly HashSet<string> VariadicBuiltInNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "iprintln",
        "iprintlnbold"
    };

    public static CompletionItem FromSymbol(GscSymbol symbol, CompletionItemKind kind, string detailSource, bool appendSemicolon = false)
    {
        var parsedArgs = (symbol.Parameters ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .Where(a => !string.IsNullOrEmpty(a))
            .ToList();

        var isEngineBuiltIn = symbol.FilePath.Equals("Engine", StringComparison.OrdinalIgnoreCase);
        var isVariadic = symbol.IsVariadic || (isEngineBuiltIn && VariadicBuiltInNames.Contains(symbol.Name));

        var requiredArgCount = isEngineBuiltIn
            ? Math.Max(0, symbol.MinArgs ?? parsedArgs.Count)
            : parsedArgs.Count;

        if (isEngineBuiltIn && isVariadic)
            requiredArgCount = Math.Max(1, requiredArgCount);

        string GetArgName(int index)
        {
            if (index < parsedArgs.Count && !string.IsNullOrWhiteSpace(parsedArgs[index]))
                return parsedArgs[index].Trim('[', ']');

            return $"arg{index}";
        }

        string insertText;
        if (requiredArgCount > 0)
        {
            var snippetParts = new List<string>();
            for (int i = 0; i < requiredArgCount; i++)
            {
                string paramName = GetArgName(i).Replace("}", "\\}").Trim();
                snippetParts.Add($"${{{i + 1}:{paramName}}}");
            }
            insertText = $"{symbol.Name}({string.Join(", ", snippetParts)})";
        }
        else
        {
            insertText = $"{symbol.Name}($0)";
        }

        if (appendSemicolon)
        {
            insertText += ";";
        }

        var paramString = BuildParamDetail(symbol, parsedArgs, GetArgName, isVariadic);

        return new CompletionItem
        {
            Label = symbol.Name,
            LabelDetails = new CompletionItemLabelDetails
            {
                Detail = paramString,
                Description = detailSource
            },
            Kind = kind,
            Documentation = new StringOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = $"**Source:** `{Path.GetFileName(symbol.FilePath)}`"
            }),
            InsertText = insertText,
            InsertTextFormat = InsertTextFormat.Snippet,
            FilterText = symbol.Name
        };
    }

    private static string BuildParamDetail(GscSymbol symbol, List<string> parsedArgs, Func<int, string> getArgName, bool isVariadic)
    {
        if (symbol.FilePath.Equals("Engine", StringComparison.OrdinalIgnoreCase))
        {
            var min = Math.Max(0, symbol.MinArgs ?? 0);
            var maxFromSymbol = symbol.MaxArgs ?? parsedArgs.Count;
            var max = Math.Max(min, maxFromSymbol);

            if (isVariadic)
            {
                var requiredParts = Enumerable.Range(0, Math.Max(1, min)).Select(getArgName);
                return $"({string.Join(", ", requiredParts)}, ...args)";
            }

            if (min == max)
            {
                if (max == 0) return "()";
                var parts = Enumerable.Range(0, max).Select(getArgName);
                return $"({string.Join(", ", parts)})";
            }

            var required = min == 0
                ? string.Empty
                : string.Join(", ", Enumerable.Range(0, min).Select(getArgName));

            var optional = string.Join(", ", Enumerable.Range(min, max - min).Select(i => $"[{getArgName(i)}]"));

            if (string.IsNullOrEmpty(required))
                return $"({optional})";

            return $"({required}, {optional})";
        }

        return string.IsNullOrWhiteSpace(symbol.Parameters)
            ? "()"
            : $"({symbol.Parameters.Trim()})";
    }

    public static CompletionItem FromMacro(GscIndexer.MacroDefinition macro)
    {
        var macroDetail = string.IsNullOrEmpty(macro.Value) ? "" : $" {macro.Value}";
        return new CompletionItem
        {
            Label = macro.Name,
            LabelDetails = new CompletionItemLabelDetails
            {
                Detail = macroDetail,
                Description = "Macro"
            },
            Kind = CompletionItemKind.Constant,
            InsertText = macro.Name,
            InsertTextFormat = InsertTextFormat.PlainText,
            FilterText = macro.Name
        };
    }

    public static CompletionItem FromLocalVariable(GscIndexer.LocalVariable localVar)
    {
        return new CompletionItem
        {
            Label = localVar.Name,
            LabelDetails = new CompletionItemLabelDetails
            {
                Detail = $" = {localVar.Value}",
                Description = "Local Variable"
            },
            Kind = CompletionItemKind.Variable,
            InsertText = localVar.Name,
            InsertTextFormat = InsertTextFormat.PlainText,
            FilterText = localVar.Name
        };
    }

    private static readonly string[] BuiltInDefineNames =
    [
        "__FILE__", "__LINE__", "__DATE__", "__TIME__",
        "IW5", "IW6", "IW7", "IW8", "IW9",
        "S1", "S2", "S4",
        "H1", "H2",
        "T6", "T7", "T8", "T9"
    ];

    public static readonly CompletionItem[] BuiltInDefines = BuiltInDefineNames.Select(name => new CompletionItem
    {
        Label = name,
        LabelDetails = new CompletionItemLabelDetails
        {
            Description = "gsc-tool Define"
        },
        Kind = CompletionItemKind.Constant,
        InsertText = name,
        InsertTextFormat = InsertTextFormat.PlainText,
        FilterText = name
    }).ToArray();

    public static CompletionList ToFilteredList(IEnumerable<CompletionItem> items)
    {
        return new CompletionList(items
            .OrderByDescending(x => x.LabelDetails?.Description?.Contains("Project"))
            .GroupBy(x => x.Label)
            .Select(x => x.First()));
    }
}
