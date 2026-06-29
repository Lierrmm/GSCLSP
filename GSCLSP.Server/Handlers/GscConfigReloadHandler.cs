using MediatR;
using OmniSharp.Extensions.JsonRpc;
using GSCLSP.Core.Indexing;
using Microsoft.Extensions.Logging;

namespace GSCLSP.Server.Handlers;

[Method("custom/reloadConfig", Direction.ClientToServer)]
public record ConfigReloadNotification : IRequest;

public class GscConfigReloadHandler(GscIndexer indexer, ILogger<GscConfigReloadHandler> logger) : IJsonRpcNotificationHandler<ConfigReloadNotification>
{
    private readonly GscIndexer _indexer = indexer;

    public Task<Unit> Handle(ConfigReloadNotification notification, CancellationToken cancellationToken)
    {
        logger.LogInformation("Received configuration reload notification from client.");
        _indexer.RefreshConfiguration();
        return Unit.Task;
    }
}