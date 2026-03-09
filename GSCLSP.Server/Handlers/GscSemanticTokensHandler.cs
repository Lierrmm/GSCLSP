using GSCLSP.Core.Indexing;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Text.RegularExpressions;

namespace GSCLSP.Server.Handlers
{
    public partial class GscSemanticTokensHandler(GscIndexer indexer) : SemanticTokensHandlerBase
    {
        private readonly GscIndexer _indexer = indexer;

        protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(
            SemanticTokensCapability capability,
            ClientCapabilities clientCapabilities)
        {
            return new SemanticTokensRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("gsc"),
                Legend = new SemanticTokensLegend
                {
                    TokenTypes = new Container<SemanticTokenType>(SemanticTokenType.Namespace),
                    TokenModifiers = new Container<SemanticTokenModifier>(SemanticTokenModifier.Declaration)
                },
                Full = true,
                Range = false
            };
        }

        protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(
            ITextDocumentIdentifierParams @params,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new SemanticTokensDocument(RegistrationOptions.Legend));
        }

        protected override async Task Tokenize(
            SemanticTokensBuilder builder,
            ITextDocumentIdentifierParams identifier,
            CancellationToken cancellationToken)
        {
            var filePath = identifier.TextDocument.Uri.GetFileSystemPath();
            if (!File.Exists(filePath)) return;

            var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
            bool inBlockComment = false;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var codeRanges = GscHandlerCommon.GetCodeRanges(line, ref inBlockComment);

                // #include #using
                var directiveMatch = directivePathRegex().Match(line);
                if (directiveMatch.Success && GscHandlerCommon.IsInCode(codeRanges, directiveMatch.Index))
                {
                    var group = directiveMatch.Groups[1];
                    if (_indexer.IsKnownPath(group.Value))
                        builder.Push(i, group.Index, group.Length, SemanticTokenType.Namespace, SemanticTokenModifier.Declaration);
                }

                // #inline - .gsh files aren't indexed so just kinda ignore but support basically
                var inlineMatch = inlinePathRegex().Match(line);
                if (inlineMatch.Success && GscHandlerCommon.IsInCode(codeRanges, inlineMatch.Index))
                {
                    var group = inlineMatch.Groups[1];
                    builder.Push(i, group.Index, group.Length, SemanticTokenType.Namespace, SemanticTokenModifier.Declaration);
                }

                // path::func
                foreach (Match m in namespacePathRegex().Matches(line))
                {
                    if (!GscHandlerCommon.IsInCode(codeRanges, m.Groups[1].Index)) continue;
                    var pathGroup = m.Groups[1];
                    if (_indexer.IsKnownPath(pathGroup.Value))
                        builder.Push(i, pathGroup.Index, pathGroup.Length, SemanticTokenType.Namespace, SemanticTokenModifier.Declaration);
                }
            }
        }

        // #include #using paths
        [GeneratedRegex(@"^#(?:include|using)\s+([\w\\]+)")]
        private static partial Regex directivePathRegex();

        // #inline .gsh
        [GeneratedRegex(@"^#inline\s+([\w\\]+(?:\.\w+)?)")]
        private static partial Regex inlinePathRegex();

        // path::func
        [GeneratedRegex(@"([\w\\]*\\[\w\\]+)::")]
        private static partial Regex namespacePathRegex();
    }
}
