using Microsoft.Extensions.Hosting;

namespace GSCLSP.Mcp;

/// <summary>
/// Kicks off the initial workspace/dump index at host start without blocking the
/// MCP stdio transport, so the server can answer <c>get_status</c> immediately.
/// </summary>
public sealed class IndexingHostedService(GscIndexerService indexerService) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        indexerService.InitializeAsync(stoppingToken);
}
