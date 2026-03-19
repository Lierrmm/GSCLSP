using GSCLSP.Core.Indexing;
using GSCLSP.Core.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCLSP.Server.Handlers;

internal static class GscCompletionItemFactory
{
    public static CompletionItem FromSymbol(GscSymbol symbol, CompletionItemKind kind, string detailSource, bool appendSemicolon = false)
    {
        var argList = (symbol.Parameters ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .Where(a => !string.IsNullOrEmpty(a))
            .ToList();

        string insertText;
        if (argList.Count > 0)
        {
            var snippetParts = new List<string>();
            for (int i = 0; i < argList.Count; i++)
            {
                string paramName = argList[i].Replace("}", "\\}").Trim();
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

        var paramString = string.IsNullOrWhiteSpace(symbol.Parameters)
            ? "()"
            : $"({symbol.Parameters.Trim()})";

        return new CompletionItem
        {
            Label = symbol.Name,
            LabelDetails = new CompletionItemLabelDetails
            {
                Detail = $"{paramString}",
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
