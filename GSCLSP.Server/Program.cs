using GSCLSP.Core.Indexing;
using GSCLSP.Server.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using OmniSharp.Extensions.LanguageServer.Server;

var indexer = new GscIndexer();

string basePath = AppDomain.CurrentDomain.BaseDirectory;
string builtInPath = Path.Combine(basePath, "data", "iw4_builtins.json");
string dumpIndexPath = Path.Combine(basePath, "data", "symbols.json");

try
{
    indexer.BuiltIns.LoadBuiltIns(builtInPath);
    if (File.Exists(dumpIndexPath))
    {
        indexer.LoadGlobalIndex(dumpIndexPath);
    }
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
        })
        .WithHandler<GscDefinitionHandler>()
        .WithHandler<GscHoverHandler>()
        .WithHandler<GscCompletionHandler>()
        .WithHandler<GscReferencesHandler>()
        .OnInitialize((server, request, token) =>
        {
            string? workspacePath = request.RootPath ?? request.RootUri?.GetFileSystemPath();

            if (!string.IsNullOrEmpty(workspacePath))
            {
                var indexer = server.Services.GetService<GscIndexer>();
                indexer?.IndexWorkspace(workspacePath);
            }

            return Task.CompletedTask;
        })
        .OnStarted(async (server, ct) =>
        {
            var config = server.Configuration.GetSection("gsclsp");
            var newPath = config.GetValue<string>("dumpPath");
            indexer.UpdateDumpPath(newPath);
        })
        .OnDidChangeConfiguration(x =>
        {
            var settings = x.Settings?.SelectToken("gsclsp");

            if (settings != null)
            {
                var newPath = settings.Value<string>("dumpPath");
                Console.Error.WriteLine($"New Path {newPath}");
                indexer.UpdateDumpPath(newPath);
            }
        })
);


Console.Error.WriteLine("Running!");

await server.WaitForExit;