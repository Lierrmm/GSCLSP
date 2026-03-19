# GSCLSP (GSC Language Support)

Language support for `.gsc` / `.gsh` files powered by the `GSCLSP.Server` language server.

## Features

- **Code completion**
  - Local/workspace/dump symbols
  - Engine built-ins
  - Include-aware suggestions
  - Macro and local variable completions
  - Context-aware call insertion (adds `;` when appropriate)

- **Go to Definition**
  - Local functions
  - Included/dump symbols
  - Include/using/inline directive path targets
  - Macro definitions

- **Find References**
  - Lexer-based reference matching for better accuracy

- **Hover**
  - Function signatures and docs
  - Built-in, macro, and local variable info
  - Directive include path preview

- **Diagnostics**
  - Unresolved function calls (`gsclsp.unresolvedFunction`)
  - Missing semicolon (`gsclsp.missingSemicolon`)
  - Recursive function warning (`gsclsp.recursiveFunction`)

- **Code Actions**
  - Quick fix to insert `#include ...` for unresolved functions

## Diagnostic Mute Comments

Warnings can be muted either:

- at the **top of the file**, or
- on the **line above** the affected line

Supported format:

- `// gsclsp-disable: recursive-function`
- `// gsclsp-disable: missing-semicolon`
- `// gsclsp-disable: recursive, semicolon`
- `// gsclsp-disable: all`

Aliases supported:

- `recursive` -> `recursive-function`
- `semicolon` -> `missing-semicolon`

## Project Setup

When you open a workspace, the extension ensures a `gsclsp.config.json` file exists in the workspace root.

Example:

```json
{
  "dumpPath": "D:\\your\\gsc_dump"
}
```

## Command

- `GSC: Set Dump Folder Path` (`gsclsp.browseDumpPath`)
  - Opens folder picker
  - Updates `gsclsp.config.json`
  - Notifies the server to re-index dump symbols

## Requirements

- VS Code `^1.80.0`
- Windows (bundled server executable is `GSCLSP.Server.exe`)

## File Types

- `.gsc`
- `.gsh`
