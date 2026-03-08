using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCLSP.Server
{
    public static class GscServerConstants
    {
        public static readonly TextDocumentSelector Selector = TextDocumentSelector.ForLanguage("gsc", "csc");
    }
}