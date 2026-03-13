using GSCLSP.Core.Indexing;
using GSCLSP.Server.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using OmniSharp.Extensions.LanguageServer.Server;

var indexer = new GscIndexer();
var documentStore = new GscDocumentStore();

string basePath = AppDomain.CurrentDomain.BaseDirectory;
string builtInPath = Path.Combine(basePath, "data", "iw4_builtins.json");

try
{
    indexer.BuiltIns.LoadBuiltIns(builtInPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"GSCLSP Init Error: {ex.Message}");
}

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

            indexer.UpdateSettingDumpPath(null);
            return Task.CompletedTask;
        })
);

Console.Error.WriteLine("Running!");

await server.WaitForExit;