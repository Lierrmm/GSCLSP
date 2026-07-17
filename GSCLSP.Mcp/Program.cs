using GSCLSP.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(WorkspaceOptions.Resolve(args));
builder.Services.AddSingleton<GscIndexerService>();
builder.Services.AddHostedService<IndexingHostedService>();

builder.Services
    .AddMcpServer(options => options.ServerInstructions =
        "GSC (Call of Duty script) index server. Before writing or editing any GSC code: " +
        "(1) read the GSC primer — resource 'gsclsp://primer' or tool get_gsc_primer — it covers syntax, " +
        "per-game differences, hashed dumps, and how to use these tools; " +
        "(2) call get_status to learn the active game; " +
        "(3) verify every function against that game with resolve_function, get_symbol, or list_builtins. " +
        "GSC APIs differ per game — do not rely on training-data memory.")
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly()
    .WithPromptsFromAssembly();

await builder.Build().RunAsync();
