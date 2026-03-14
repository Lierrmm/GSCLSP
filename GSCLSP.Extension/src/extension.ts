import * as path from "path";
import * as fs from "fs/promises";

import {
  type ExtensionContext,
  window,
  workspace,
  type OpenDialogOptions,
  commands,
  ProgressLocation,
  ExtensionMode,
  type WorkspaceFolder,
} from "vscode";
import {
  LanguageClient,
  type LanguageClientOptions,
  type ServerOptions,
  Trace,
} from "vscode-languageclient/node";

let client: LanguageClient;

async function ensureWorkspaceConfigFile(folder: WorkspaceFolder): Promise<void> {
  if (folder.uri.scheme !== "file") {
    return;
  }

  const configPath = path.join(folder.uri.fsPath, "gsclsp.config.json");

  try {
    await fs.access(configPath);
  } catch {
    await fs.writeFile(configPath, "{}\n", "utf8");
  }
}

export async function activate(context: ExtensionContext): Promise<void> {
  for (const folder of workspace.workspaceFolders ?? []) {
    await ensureWorkspaceConfigFile(folder);
  }

  context.subscriptions.push(
    workspace.onDidChangeWorkspaceFolders(async (event) => {
      for (const folder of event.added) {
        await ensureWorkspaceConfigFile(folder);
      }
    }),
  );

  let browseCommand = commands.registerCommand(
    "gsclsp.browseDumpPath",
    async () => {
      const options: OpenDialogOptions = {
        canSelectMany: false,
        openLabel: "Select GSC Dump Folder",
        canSelectFiles: false,
        canSelectFolders: true,
      };

      const fileUri = await window.showOpenDialog(options);
      if (fileUri && fileUri[0]) {
        const newPath = fileUri[0].fsPath;
        window.withProgress(
          {
            location: ProgressLocation.Notification,
            title: "GSC Indexer",
            cancellable: false,
          },
          async (progress) => {
            progress.report({ message: "Re-indexing dump..." });

            const activeUri = window.activeTextEditor?.document.uri;
            const targetWorkspace = activeUri
              ? workspace.getWorkspaceFolder(activeUri)
              : workspace.workspaceFolders?.[0];

            if (!targetWorkspace || targetWorkspace.uri.scheme !== "file") {
              window.showErrorMessage(
                "GSCLSP: Open a local workspace folder to save gsclsp.config.json",
              );
              return;
            }

            const configPath = path.join(
              targetWorkspace.uri.fsPath,
              "gsclsp.config.json",
            );

            try {
              let config: { dumpPath?: string } = {};

              try {
                const existing = await fs.readFile(configPath, "utf8");
                const parsed = JSON.parse(existing) as unknown;
                if (parsed && typeof parsed === "object") {
                  config = parsed as { dumpPath?: string };
                }
              } catch {
                config = {};
              }

              config.dumpPath = newPath;
              await fs.writeFile(
                configPath,
                JSON.stringify(config, null, 2),
                "utf8",
              );

              await client.sendNotification("custom/updateDumpPath", {
                path: newPath,
              });

              window.showInformationMessage(
                `GSCLSP: Updated ${configPath}`,
              );
            } catch (error) {
              const message =
                error instanceof Error ? error.message : String(error);
              window.showErrorMessage(
                `GSCLSP: Failed to write gsclsp.config.json: ${message}`,
              );
            }

            return new Promise((resolve) => setTimeout(resolve, 2000));
          },
        );
      }
    },
  );

  context.subscriptions.push(browseCommand);

  const outputChannel = window.createOutputChannel("bbe-gsclsp");
  context.subscriptions.push(outputChannel);

  window.showInformationMessage("BBE Tools: GSC Extension is spawning");
  console.log("TS: GSCLSP Activation started");

  const debugExe = context.asAbsolutePath(
    path.join(
      "..",
      "GSCLSP.Server",
      "bin",
      "Release",
      "net10.0",
      "win-x64",
      "GSCLSP.Server.exe",
    ),
  );

  const serverExe = context.asAbsolutePath(
    path.join("out", "GSCLSP.Server.exe"),
  );

  const serverOptions: ServerOptions = {
    run: {
      command: serverExe,
      options: { cwd: path.dirname(serverExe) },
    },
    debug: {
      command: debugExe,
      options: { cwd: path.dirname(debugExe) },
    },
  };

  const clientOptions: LanguageClientOptions = {
    documentSelector: [{ scheme: "file", language: "gsc" }],
    outputChannel,
    synchronize: {
      fileEvents: workspace.createFileSystemWatcher("**/*.gsc"),
    },
  };

  client = new LanguageClient(
    "gscServer",
    "GSC Language Server",
    serverOptions,
    clientOptions,
  );

  context.subscriptions.push(client);
  await client.start();

  if (context.extensionMode === ExtensionMode.Development) {
    await client.setTrace(Trace.Verbose);
  } else {
    await client.setTrace(Trace.Off);
  }
}

export async function deactivate(): Promise<void> {
  if (!client) {
    return undefined;
  }
  return await client.stop();
}
