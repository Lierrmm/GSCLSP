import * as path from "path";

import {
  type ExtensionContext,
  window,
  workspace,
  type OpenDialogOptions,
  commands,
  ProgressLocation,
} from "vscode";
import {
  LanguageClient,
  type LanguageClientOptions,
  type ServerOptions,
  Trace,
} from "vscode-languageclient/node";

let client: LanguageClient;

export async function activate(context: ExtensionContext): Promise<void> {
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

            await workspace
              .getConfiguration("gsclsp")
              .update("dumpPath", newPath, true);
            await client.sendNotification("custom/updateDumpPath", {
              path: newPath,
            });

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
      configurationSection: "gsclsp",
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
  await client.setTrace(Trace.Verbose);
}

export async function deactivate(): Promise<void> {
  if (!client) {
    return undefined;
  }
  return await client.stop();
}
