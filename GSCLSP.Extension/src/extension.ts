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
  StatusBarAlignment,
  type StatusBarItem,
  type TextEditor,
  type TextEditorDecorationType,
  Range,
  Uri,
} from "vscode";
import {
  LanguageClient,
  type LanguageClientOptions,
  type ServerOptions,
  Trace,
} from "vscode-languageclient/node";

let client: LanguageClient;
let targetGameStatusBar: StatusBarItem | undefined;
let inactiveDecoration: TextEditorDecorationType | undefined;
const inactiveRangesByUri = new Map<string, Range[]>();

// games that GSCLSP can work on no problem, but they may use IW4 builtins for now
const KNOWN_GAMES = [
  "iw3",
  "iw4",
  "iw5",
  "iw6",
  "iw7",
  "iw8",
  "iw9",
  "s1",
  "s2",
  "s4",
  "t4",
  "t5",
  "t6",
  "t7",
  "t8",
  "h1",
  "h2"
];

function targetWorkspaceFolder(): WorkspaceFolder | undefined {
  const activeUri = window.activeTextEditor?.document.uri;
  return activeUri
    ? workspace.getWorkspaceFolder(activeUri) ?? workspace.workspaceFolders?.[0]
    : workspace.workspaceFolders?.[0];
}

async function readWorkspaceConfig(
  folder: WorkspaceFolder,
): Promise<Record<string, unknown>> {
  const configPath = path.join(folder.uri.fsPath, "gsclsp.config.json");
  try {
    const existing = await fs.readFile(configPath, "utf8");
    const parsed = JSON.parse(existing) as unknown;
    return parsed && typeof parsed === "object"
      ? (parsed as Record<string, unknown>)
      : {};
  } catch {
    return {};
  }
}

async function writeWorkspaceConfig(
  folder: WorkspaceFolder,
  config: Record<string, unknown>,
): Promise<void> {
  const configPath = path.join(folder.uri.fsPath, "gsclsp.config.json");
  await fs.writeFile(configPath, JSON.stringify(config, null, 2), "utf8");
}

async function readTargetGame(): Promise<string | undefined> {
  const folder = targetWorkspaceFolder();
  if (!folder || folder.uri.scheme !== "file") return undefined;
  const config = await readWorkspaceConfig(folder);
  const game = config.game;
  return typeof game === "string" && game.length > 0 ? game : undefined;
}

async function updateStatusBar(): Promise<void> {
  if (!targetGameStatusBar) return;
  const game = (await readTargetGame()) ?? "iw4";
  targetGameStatusBar.text = `$(chip) GSC Target Game`;
  targetGameStatusBar.tooltip = "Click to change the target game for GSC";
  targetGameStatusBar.show();
}

async function selectTargetGameCommand(): Promise<void> {
  const folder = targetWorkspaceFolder();
  if (!folder || folder.uri.scheme !== "file") {
    window.showErrorMessage("GSCLSP: Open a local workspace folder to configure target game.");
    return;
  }

  const current = (await readTargetGame()) ?? "iw4";
  const items = KNOWN_GAMES.map((game) => ({
    label: game.toUpperCase(),
    description: game === current ? "(current)" : undefined,
    game,
  }));
  items.push({ label: "Custom...", description: "Enter a custom value", game: "__custom__" });

  const pick = await window.showQuickPick(items, {
    placeHolder: `Select target game (current: ${current.toUpperCase()})`,
  });
  if (!pick) return;

  let chosen = pick.game;
  if (chosen === "__custom__") {
    const input = await window.showInputBox({
      prompt: "Enter target game identifier (e.g. iw9, t8)",
      value: current,
    });
    if (!input) return;
    chosen = input.trim().toLowerCase();
    if (!/^[a-z0-9_]+$/.test(chosen))
    {
      window.showErrorMessage(`GSCLSP: Invalid game identifier "${input}. Please try again.`);
      return;
    }
  }

  const config = await readWorkspaceConfig(folder);
  config.game = chosen;
  await writeWorkspaceConfig(folder, config);
  await updateStatusBar();
  window.showInformationMessage(`GSCLSP: Target game set to "${chosen.toUpperCase()}"`);
}

interface InactiveRegionsParams {
  uri: string;
  ranges: { start: number; end: number }[];
}

function applyDecorationsFor(editor: TextEditor | undefined): void {
  if (!editor || !inactiveDecoration) return;
  const key = editor.document.uri.toString();
  const ranges = inactiveRangesByUri.get(key) ?? [];
  editor.setDecorations(inactiveDecoration, ranges);
}

interface DumpStatusParams {
  game: string;
  hasDump: boolean;
}

async function handleDumpStatus(params: DumpStatusParams): Promise<void> {
  if (params.hasDump) return;

  const setUp = "Setup Path";
  const choice = await window.showWarningMessage(
    `Could not find a GSC dump for game "${params.game.toUpperCase()}". It is recommended you setup a GSC dump for the best experience.`,
    setUp,
  );

  if (choice === setUp) {
    await commands.executeCommand("gsclsp.browseDumpPath");
  }
}

function handleInactiveRegions(params: InactiveRegionsParams): void {
  const uri = Uri.parse(params.uri);
  const key = uri.toString();

  const ranges = params.ranges.map(
    (r) => new Range(r.start, 0, r.end, Number.MAX_SAFE_INTEGER),
  );
  inactiveRangesByUri.set(key, ranges);

  for (const editor of window.visibleTextEditors) {
    if (editor.document.uri.toString() === key) {
      applyDecorationsFor(editor);
    }
  }
}

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

  const config = await readWorkspaceConfig(folder);
  let changed = false;

  if (typeof config.dumpPaths !== "object" || config.dumpPaths === null) {
    config.dumpPaths = {};
    changed = true;
  }

  if ("dumpPath" in config) {
    const legacy = config.dumpPath;
    const game = (await readTargetGame()) ?? "iw4";
    const paths = config.dumpPaths as Record<string, unknown>;
    if (typeof legacy === "string" && legacy.length > 0 && !(game in paths)) {
      paths[game] = legacy;
    }
    delete config.dumpPath;
    changed = true;
  }

  if (changed) {
    await writeWorkspaceConfig(folder, config);
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
              window.showErrorMessage("GSCLSP: Open a local workspace folder to save gsclsp.config.json");
              return;
            }

            const configPath = path.join(
              targetWorkspace.uri.fsPath,
              "gsclsp.config.json",
            );

            try {
              let config: { dumpPaths?: Record<string, string> } = {};

              try {
                const existing = await fs.readFile(configPath, "utf8");
                const parsed = JSON.parse(existing) as unknown;
                if (parsed && typeof parsed === "object") {
                  config = parsed as typeof config;
                }
              } catch {
                config = {};
              }

              const game = (await readTargetGame()) ?? "iw4";
              config.dumpPaths = { ...(config.dumpPaths ?? {}), [game]: newPath };

              await fs.writeFile(
                configPath,
                JSON.stringify(config, null, 2),
                "utf8",
              );

              window.showInformationMessage(
                `GSCLSP: Set dump folder for ${game.toUpperCase()}`,
              );
            } catch (error) {
              const message =
                error instanceof Error ? error.message : String(error);
              window.showErrorMessage(`GSCLSP: Failed to write gsclsp.config.json: ${message}`);
            }

            return new Promise((resolve) => setTimeout(resolve, 2000));
          },
        );
      }
    },
  );

  context.subscriptions.push(browseCommand);

  const outputChannel = window.createOutputChannel("bbe-gsclsp", { log: true });
  context.subscriptions.push(outputChannel);

  window.showInformationMessage("BBE Tools: GSC Extension is spawning");

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

  if (context.extensionMode === ExtensionMode.Development) {
    await client.setTrace(Trace.Verbose);
  } else {
    await client.setTrace(Trace.Off);
  }

  targetGameStatusBar = window.createStatusBarItem(StatusBarAlignment.Right, 100);
  targetGameStatusBar.command = "gsclsp.selectTargetGame";
  context.subscriptions.push(targetGameStatusBar);
  await updateStatusBar();

  context.subscriptions.push(
    commands.registerCommand("gsclsp.selectTargetGame", selectTargetGameCommand),
  );

  const onConfigChanged = async (): Promise<void> => {
    await updateStatusBar();
    await client.sendNotification("custom/reloadConfig", {});
  };

  const configWatcher = workspace.createFileSystemWatcher("**/gsclsp.config.json");
  configWatcher.onDidChange(onConfigChanged);
  configWatcher.onDidCreate(onConfigChanged);
  configWatcher.onDidDelete(() => updateStatusBar());
  context.subscriptions.push(configWatcher);

  inactiveDecoration = window.createTextEditorDecorationType({
    opacity: "0.45",
    isWholeLine: true,
  });
  context.subscriptions.push(inactiveDecoration);

  context.subscriptions.push(
    client.onNotification("custom/inactiveRegions", handleInactiveRegions),
  );

  context.subscriptions.push(
    client.onNotification("custom/dumpStatus", handleDumpStatus),
  );

  context.subscriptions.push(
    workspace.onDidCloseTextDocument((doc) => {
      inactiveRangesByUri.delete(doc.uri.toString());
    }),
  );

  context.subscriptions.push(
    window.onDidChangeActiveTextEditor((editor) => applyDecorationsFor(editor)),
    window.onDidChangeVisibleTextEditors((editors) => {
      for (const e of editors) applyDecorationsFor(e);
    }),
  );

  await client.start();
}

export async function deactivate(): Promise<void> {
  if (!client) {
    return undefined;
  }
  return await client.stop();
}
