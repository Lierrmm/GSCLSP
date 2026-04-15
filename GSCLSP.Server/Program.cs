using GSCLSP.Core.Indexing;
using GSCLSP.Server.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Server;

#if DEBUG // Dumping built-in argument usage from a folder of decompiled GSC files. Adjust paths as needed.
using GSCLSP.Core.Tools;
await BuiltinArgScanner.InferArgsAsync(
    dumpFolder: @"G:\Games\MS-IW5\raw\dump\gsc",
    builtinsJsonPath: @"F:\Web Development\GSCLSP\GSCLSP.Core\data\iw5_builtins.json", matchesLogPath: @"F:\Web Development\GSCLSP\GSCLSP.Core\data\output_iw5.json");

return;
#endif

var indexer = new GscIndexer();
var documentStore = new GscDocumentStore();

var server = await LanguageServer.From(options =>
    options
        .WithInput(Console.OpenStandardInput())
        .WithOutput(Console.OpenStandardOutput())
        .ConfigureLogging(x => x.AddLanguageProtocolLogging().SetMinimumLevel(LogLevel.Debug))
        .WithConfigurationSection("gsclsp")
        .WithServices(services =>
        {
            services.AddSingleton(indexer);
            services.AddSingleton(documentStore);
            services.AddSingleton<GscDiagnosticsHandler>();
        })
        .WithHandler<GscDocumentSyncHandler>()
        .WithHandler<GscDefinitionHandler>()
        .WithHandler<GscHoverHandler>()
        .WithHandler<GscCompletionHandler>()
        .WithHandler<GscReferencesHandler>()
        .WithHandler<GscCodeActionHandler>()
        .WithHandler<GscRenameHandler>()
        .OnInitialize((server, request, token) =>
        {
            var workspacePath = request.RootPath
                ?? request.RootUri?.GetFileSystemPath()
                ?? request.WorkspaceFolders?.FirstOrDefault()?.Uri?.GetFileSystemPath();

            if (!string.IsNullOrEmpty(workspacePath))
            {
                var serverIndexer = server.Services.GetService<GscIndexer>();
                serverIndexer?.IndexWorkspace(workspacePath);
            }
            else
            {
                indexer.UpdateSettingDumpPath(null);
            }

            return Task.CompletedTask;
        })
);

Console.Error.WriteLine("Running!");

await server.WaitForExit;