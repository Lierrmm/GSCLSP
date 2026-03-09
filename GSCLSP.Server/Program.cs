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
        .WithHandler<GscSemanticTokensHandler>()
        //.WithHandler<GscReferencesHandler>() - Skip for now - its not searching for called functions, just their definitions in dumped files
        .OnStarted(async (server, ct) =>
        {
            var config = server.Configuration.GetSection("gsclsp");
            var newPath = config.GetValue<string>("dumpPath");
            indexer.UpdateDumpPath(newPath);
        })
        .OnNotification<IndexWorkspaceFoldersParams>("custom/indexWorkspaceFolders", (data) =>
        {
            if (data?.Paths == null) return;

            string[] subFolders = ["maps", "scripts", "custom_scripts", 
                "aitype", "animscripts", "character", "codescripts", "common_scripts", "destructible_scripts",
                "soundscripts", "vehicle_scripts"];

            _ = Task.Run(() =>
            {
                foreach (var root in data.Paths)
                {
                    foreach (var sub in subFolders)
                    {
                        var dir = Path.Combine(root, sub);
                        if (Directory.Exists(dir))
                            indexer.IndexFolder(dir);
                    }
                }
            });
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

record IndexWorkspaceFoldersParams(List<string> Paths);
