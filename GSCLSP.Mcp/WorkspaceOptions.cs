namespace GSCLSP.Mcp;

public sealed class WorkspaceOptions
{
    public required string WorkspacePath { get; init; }

    public static WorkspaceOptions Resolve(string[] args)
    {
        string? fromArg = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--workspace")
            {
                fromArg = args[i + 1];
                break;
            }
        }

        string path = fromArg
            ?? Environment.GetEnvironmentVariable("GSCLSP_WORKSPACE")
            ?? Directory.GetCurrentDirectory();

        return new WorkspaceOptions { WorkspacePath = Path.GetFullPath(path) };
    }
}
