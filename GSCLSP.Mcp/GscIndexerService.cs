using GSCLSP.Core.Diagnostics;
using GSCLSP.Core.Indexing;
using Microsoft.Extensions.Logging;

namespace GSCLSP.Mcp;

/// <summary>
/// Owns the singleton <see cref="GscIndexer"/> plus the workspace target and the
/// "index ready" state so tools can distinguish "no results" from "still indexing".
/// </summary>
public sealed class GscIndexerService(ILogger<GscIndexerService> logger, ILoggerFactory loggerFactory, WorkspaceOptions options)
{
    private volatile bool _indexingInProgress = true;

    public GscIndexer Indexer { get; } = new(loggerFactory.CreateLogger<GscIndexer>());

    public GscDiagnosticsAnalyzer Diagnostics => _diagnostics ??= new GscDiagnosticsAnalyzer(Indexer, loggerFactory.CreateLogger<GscDiagnosticsAnalyzer>());
    private GscDiagnosticsAnalyzer? _diagnostics;

    public string WorkspacePath { get; } = options.WorkspacePath;

    public bool IndexingInProgress => _indexingInProgress;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _indexingInProgress = true;
        try
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(WorkspacePath))
                {
                    logger.LogInformation("Indexing workspace {WorkspacePath}", WorkspacePath);
                    Indexer.IndexWorkspace(WorkspacePath);
                }
                else
                {
                    logger.LogWarning("Workspace path {WorkspacePath} does not exist; loading configuration only", WorkspacePath);
                    Indexer.RefreshConfiguration();
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Indexing failed for workspace {WorkspacePath}", WorkspacePath);
        }
        finally
        {
            _indexingInProgress = false;
            logger.LogInformation("Initial indexing complete for {WorkspacePath}", WorkspacePath);
        }
    }
}
