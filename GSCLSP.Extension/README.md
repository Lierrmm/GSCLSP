# GSCLSP

[Visual Studio Code](https://code.visualstudio.com/) language support for *Call of Duty®*'s scripting language `.gsc`, `.gsh`, `.csc`, and `.csh` files, powered by the **GSCLSP [C# language server](https://github.com/Lierrmm/GSCLSP/tree/main/GSCLSP.Server)**.

## Choosing Your Target Game

Once you are inside a valid *GSC* file, you will see a `GSC Target Game` dropbox appear in the **bottom right corner.** Choosing the game you are working on will help GSCLSP changes the built-ins list and gives accurate diagnostics.

## Features

### Syntax Highlighting
- Adds colored keywording
- "Dead code" zones for early returns & unused preprocessors
<div align="center"> 
<img src="https://raw.githubusercontent.com/Lierrmm/GSCLSP/refs/heads/main/GSCLSP.Extension/images/syntax1.png" width="60%">
</div>

### Code Completion
- Local workspace & your choice of dump symbols
- Engine built-ins per target game
- Context-aware suggestions, functions, and more
- Preprocessor directives and global/local variable completions
<div align="center"> 
<img src="https://raw.githubusercontent.com/Lierrmm/GSCLSP/refs/heads/main/GSCLSP.Extension/images/completion1.png" width="60%">
<img src="https://raw.githubusercontent.com/Lierrmm/GSCLSP/refs/heads/main/GSCLSP.Extension/images/completion3-preprocessor.png" width="75%">
</div>

### Go to Definition & Go to References

When you **Right mouse click** on any user-defined function or local variable, you can:
- use the `Go to Definition` button to quickly jump to the line its defined at
- use the `Go to References` or `Find All References` button to find every place that calls it

This works for any function or local variable defined in GSC, including `#include`/`#using` GSC paths.
<div align="center"> 
<img src="https://raw.githubusercontent.com/Lierrmm/GSCLSP/refs/heads/main/GSCLSP.Extension/images/defandref.png" width="60%">
</div>

### Hover Information

When you hover your mouse cursor over any sort of function, macro, variable, or file path, a hover box will appear giving detailed information about what you are hovering. This includes line information, any comments **above the definition of the function/variable**, and any additional comments official documents contain.
<div align="center"> 
<img src="https://raw.githubusercontent.com/Lierrmm/GSCLSP/refs/heads/main/GSCLSP.Extension/images/hover1.png" width="60%">
</div>

### Diagnostics
- `gsclsp.unresolvedFunction`: Unresolved function calls
- `gsclsp.recursiveFunction`: Recursive function warning
- `gsclsp.missingSemicolon`: Missing semicolon
- `gsclsp.invalidBuiltinArgCount`: Built-in argument count warning
- `gsclsp.earlyReturn`: Early return cutting off code warning
- `gsclsp.missingAnimtree`: Sanity check #animtree for valid #using_animtree

### Code Actions
- Quick fix to insert `#include ...` for unresolved functions

## How to disable warnings/errors

Warnings can be muted on either:

- the **first line** of the file
- or the **line above** the error

Supported format:

- `// gsclsp-disable: recursive-function`
- `// gsclsp-disable: missing-semicolon`
- `// gsclsp-disable: builtin-arg-count`
- `// gsclsp-disable: recursive, semicolon, builtin-args`
- `// gsclsp-disable: all`

Aliases supported:

- `recursive` -> `recursive-function`
- `semicolon` -> `missing-semicolon`
- `builtin-args` or `arity` -> `builtin-arg-count`

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

- `GSC: Select Target Game` (`gsclsp.selectTargetGame`)
  - Opens a dropdown box that asks which game you working on for the workspace
  - Does a refresh of built-ins and dump
