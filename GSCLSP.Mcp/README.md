# GSCLSP.Mcp

This README explains how to use the MCP server for GSCLSP. The MCP server contains a "primer" that educates even the dumbest models on the in-and-out quirks of GSC, and how to write it properly, look up GSC functions, read scripts, and check for errors via GSCLSP's workspace.

## 1. Build

```bash
dotnet publish GSCLSP.Mcp -c Release
```

The exe is at `GSCLSP.Mcp/bin/Release/net10.0/win-x64/publish/GSCLSP.Mcp.exe`.

## 2. Set up your GSC project

In your GSC project folder, create `.gsclsp/config.json`:

```json
{
  "game": "iw4",
  "dumpPaths": {
    "iw4": "C:/path/to/iw4/dump"
  }
}
```

- `game` — which game you are writing for (needed, function names differ per game)
- `dumpPaths` — where the game's script dump lives (optional, gives you the full game library)

## 3. Add in agentic program

### Claude

Open a command prompt terminal, and run this in your GSC's project folder **(remember to change the path/to/your/gsc/project)**:

```bash
claude mcp add gsclsp -- "C:/path/to/GSCLSP.Mcp.exe" --workspace "C:/path/to/your/gsc/project"
```

Or put this in a `.mcp.json` *(create if it doesn't exist)* in your project:

```json
{
  "mcpServers": {
    "gsclsp": {
      "command": "C:/path/to/GSCLSP.Mcp.exe",
      "args": ["--workspace", "C:/path/to/your/gsc/project"]
    }
  }
}
```

### Codex

Open a command prompt terminal, and run this in your GSC's project folder **(remember to change the path/to/your/gsc/project)**:

```bash
codex mcp add gsclsp -- "C:/path/to/GSCLSP.Mcp.exe" --workspace "C:/path/to/your/gsc/project"
```

Or put this in a `~/.codex/config.toml` *(create if it doesn't exist)* in your project:

```toml
[mcp_servers.gsclsp]
command = "C:/path/to/GSCLSP.Mcp.exe"
args = ["--workspace", "C:/path/to/your/gsc/project"]
```

### OpenCode

Put this in an `opencode.json` *(create if it doesn't exist)* in your project **(remember to change the path/to/your/gsc/project)**:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "gsclsp": {
      "type": "local",
      "command": ["C:/path/to/GSCLSP.Mcp.exe", "--workspace", "C:/path/to/your/gsc/project"],
      "enabled": true
    }
  }
}
```

Or put the same in `~/.config/opencode/opencode.json` to use it in every project.

## What handles MCP

| File | Job |
|------|-----|
| `Program.cs` | Starts the server. `AddMcpServer()` + `WithStdioServerTransport()` turn on MCP. `WithToolsFromAssembly()` and `WithResourcesFromAssembly()` find the tools and resources below. |
| `GscTools.cs` | The tools. Each method with `[McpServerTool]` is one tool the AI can call: `get_status`, `search_symbols`, `get_symbol`, `list_builtins`, `list_script_files`, `get_functions_in_file`, `read_script`, `search_script_content`, `resolve_function`, `get_problems`. |
| `GscResources.cs` | The resources. `gsclsp://primer` (GSC language guide) and `gsclsp://problems` (current errors). Also the `get_gsc_primer` tool. |
| `WorkspaceOptions.cs` | Reads `--workspace` / `GSCLSP_WORKSPACE`. |
| `GscIndexerService.cs` | Holds the index. The tools read from it. |
| `IndexingHostedService.cs` | Runs indexing in the background when the server starts. |
| `Resources/gsc-primer.md` | The GSC guide text served by the primer resource. |

- The MCP protocol itself comes from the `ModelContextProtocol` NuGet package.
- `--workspace` can be skipped (server uses the `GSCLSP_WORKSPACE` env var, or the current folder instead)
- the server talks over stdio with logs going to stderr.
