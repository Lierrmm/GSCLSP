using GSCLSP.Core.Indexing;
using GSCLSP.Server.Handlers;
using GSCLSP.Server.Logging;
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

var server = await LanguageServer.From(options =>
    options
        .WithInput(Console.OpenStandardInput())
        .WithOutput(Console.OpenStandardOutput())
        .ConfigureLogging(logging =>
        {
            logging.Services.AddSingleton<ILoggerProvider>(provider => new CustomLspLoggerProvider(provider));

            logging.AddFilter((category, level) =>
            {
                if (category != null && category.StartsWith("OmniSharp", StringComparison.OrdinalIgnoreCase))
                {
                    return level >= LogLevel.Warning;
                }
#if DEBUG
                return level >= LogLevel.Debug;
#else
                return level >= LogLevel.Information;
#endif
            });

            logging.SetMinimumLevel(LogLevel.Debug);
        })
        .WithConfigurationSection("gsclsp")
        .WithServices(services =>
        {
            services.AddSingleton<GscDocumentStore>();
            services.AddSingleton(provider =>
                new GscIndexer(provider.GetRequiredService<ILogger<GscIndexer>>()));
            services.AddScoped<GscDiagnosticsHandler>();
        })
        .WithHandler<GscDocumentSyncHandler>()
        .WithHandler<GscDefinitionHandler>()
        .WithHandler<GscHoverHandler>()
        .WithHandler<GscCompletionHandler>()
        .WithHandler<GscReferencesHandler>()
        .WithHandler<GscCodeActionHandler>()
        .WithHandler<GscRenameHandler>()
        .WithHandler<GscConfigReloadHandler>()
        .OnInitialize((server, request, token) =>
        {
            var diIndexer = server.Services.GetRequiredService<GscIndexer>();

            var workspacePath = request.RootPath
                ?? request.RootUri?.GetFileSystemPath()
                ?? request.WorkspaceFolders?.FirstOrDefault()?.Uri?.GetFileSystemPath();

            _ = Task.Run(() =>
            {
                if (!string.IsNullOrEmpty(workspacePath))
                {
                    diIndexer.IndexWorkspace(workspacePath);
                }
                else
                {
                    diIndexer.RefreshConfiguration();
                }
            }, token);

            var loggerFactory = server.Services.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("ServerStartup");

            logger.LogInformation("Running GSC LSP Server");

            return Task.CompletedTask;
        })
);

await server.WaitForExit;