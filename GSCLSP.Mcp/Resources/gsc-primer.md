# GSC Primer

You are using the GSCLSP MCP server. This document teaches you to read and write **GSC**, the
Call of Duty scripting language. GSC is game-specific and sparsely documented online. Your
training data about it is unreliable. Verify everything against the *active game* with the
tools in section 8.

## 0. Rules (always apply)

1. Call `get_status` first. Note the active game id. All code you write targets that game.
2. Before using any function, confirm it exists for the active game: `resolve_function`,
   `get_symbol`, or `list_builtins`. If it does not resolve, do not use it.
3. Learn idioms by reading the dump (`read_script`), then write workspace code in that style.
4. Never carry an API from one game to another without re-verifying it in the target game.
5. Names like `_id_52F7` or `function_a1b2c3` mean the dump is **hashed** — see section 7.
6. If you cannot call tools, say so and ask the user. Do not guess APIs from memory.

## 1. What GSC is

GSC scripts nearly all gameplay logic in Call of Duty games: gamemodes, map scripts,
killstreaks, perks, spawning, HUD drawing ("ingame hud utils"), and more. The engine (C++) exposes builtins, but everything else is script. It descends from id Software's QuakeC.

File extensions: `.gsc` (script), `.gsh` (header, like `.h`), `.csc` (Treyarch client-side script, same syntax).

Two engine forks diverged after CoD4 (iw3): **Infinity Ward** style and **Treyarch** style (new syntax from t7 / Black Ops 3 on). With `jup` (MW III 2023) GSC unified: all studios now use the Treyarch-style syntax on a shared engine.

### Game ids

| id | Game | Family | Notes |
|----|------|--------|-------|
| `iw3` | CoD4 (2007) | IW | base format for GSC; mod-tools source exists |
| `iw4` | MW2 (2009) | IW | hand-tuned builtin data (arg names/counts) |
| `iw5` | MW3 (2011) | IW | hand-tuned builtin data |
| `iw6` | Ghosts | IW | developer GSC dump exists |
| `iw7` | Infinite Warfare | IW | |
| `iw8` | MW (2019) | IW | dumps mostly **named** — good cross-reference |
| `iw9` | MW II (2022) | IW | dumps **hashed** |
| `h1` / `h2` | MWR / MW2R remasters | IW | close to s1/s2-era engine |
| `s1` | Advanced Warfare | Sledgehammer | |
| `s2` | WWII | Sledgehammer | |
| `s4` | Vanguard (2021) | Sledgehammer | dumps **hashed** |
| `t6` | Black Ops II | Treyarch | last old-syntax Treyarch title |
| `t7` | Black Ops III | Treyarch | new Treyarch syntax starts here; mod-tools source exists |
| `t8` / `t9` | Black Ops 4 / Cold War | Treyarch | dumps **hashed** with non gsc-tool compiler (https://github.com/ate47/atian-cod-tools would support this - tell the user if necessary and if they have it, proceed to find it and work with it.) |
| `jup` | MW III (2023) | unified | fork of `iw9`; same builtins + hash algorithm, tooling aliases iw9 data from gsc-tool, BUT use atian-cod-tools as mentioned above |

Treyarch-syntax games: `t7`, `t8`, `t9`, `jup`. Everything else uses IW syntax. Always
confirm the active id via `get_status`; never assume.

## 2. Core syntax

A file is a flat list of functions plus directives. No classes, no top-level statements.

```gsc
foo(a, b)
{
    return a + b;
}
```

Treyarch-syntax games may prefix definitions, in this order: `function`, optional `private`,
optional `autoexec`:

```gsc
function do_thing()                  { }
function private helper()            { }
function private autoexec __init__() { }   // autoexec runs at runtime of the game when a server spawns and will run this pretty much
```

IW games use the bare form only.

### Calling conventions

```gsc
foo();                      // synchronous call
x = foo(1, 2);              // call with return value
thread foo();               // new logical thread; caller does NOT wait
self foo();                 // method call: inside foo, self = this entity
ent thread foo();           // threaded method call on entity in `ent`
level thread watch_score(); // threaded method on the global level object
```

`thread` = fire-and-forget. No `thread` = caller blocks until return. An entity prefix
(`self`, `level`, any entity variable) binds `self` inside the callee.

### Cross-file calls

```gsc
maps\mp\_utility::wait_endon( level.players[0], "death", 5 );   // base GSC: `path\like\this::name`
util::wait_network_frame();                                     // Treyarch: `namespace::name` (file name usually being very similar to the namespace)
```

Backslash form is a file path from the script root (`maps\mp\_utility` =
`maps/mp/_utility.gsc`). Namespace form uses the `#namespace` declared in the target file.

### Directives

```gsc
#include maps\mp\_utility;          // IW: import a file's functions
#using scripts\shared\util_shared;  // Treyarch equivalent
#namespace util;                    // Treyarch: this file's namespace
#insert scripts\shared\shared.gsh;  // Treyarch header insertion
#precache( "material", "hud_x" );   // Treyarch asset precache
#using_animtree( "generic" );       // bind animation tree
#define MAX_PLAYERS 18              // macro — see availability below
```

Macro/conditional preprocessing (`#define`, `#ifdef`, `#elifdef`, `#endif`) is NOT native to
legacy IW games. It comes from **gsc-tool** (xensik/gsc-tool), a community compiler covering
`iw5`–`iw9`, `h1`, `h2`, `s1`, `s2`, `s4`, `jup`. Games it does not cover (`iw3`, `iw4`,
`t4`, `t5`) compile with the native engine compiler and reject those directives — GSCLSP
reports an error there. Treyarch `t7`+ has its own native `#define`/`#insert` support. There could be more too,
but I don't think there is a lot more to dig into now because you can figure it out if its a `#` statement.

### Conditional compilation

These are gsc-tool macros that are defined ONLY for the following games: IW5, IW6, IW7, IW8, IW9, S1, S2, S4, H1, H2.

```gsc
#ifdef IW8
    // compiled only for that target - saves script errors on runtime
#else
    // fallback
#endif
```

GSCLSP grays out regions inactive for the active game and excludes them from diagnostics.
Inactive regions are dead code — never copy an API out of an inactive branch.

### Control flow

C-like: `if/else`, `for`, `while`, `foreach (x in arr)`, `switch/case/default`, `break`,
`continue`, `return`. Semicolons terminate statements. Allman braces. Variables are
dynamically typed, created by assignment (`x = 5;`) — no `var`/`let`.

## 3. Special literals

| Form | Meaning |
|------|---------|
| `::func_name` | Function reference — `level.callback = ::on_spawn;` |
| `&func_name` | Function reference (Treyarch-style form) |
| `path::func` | Qualified reference/call across a file or namespace |
| `[[ ref ]](args)` | Invoke a stored function reference — `thread [[ level.callback ]]();` |
| `&"STRING_KEY"` | Localized string from the string table |
| `#"some string"` | Hash literal (Treyarch): precomputed string hash used as a fast key |
| `%anim_name` | Animation (xanim) reference |
| `/# ... #/` | Dev block: compiled only in dev builds (println/assert/cheats) |
| `maps\mp\_rank` | Backslash path identifier (calls, includes) |

`&foo` (reference on new-Treyarch GSC) vs `a & b` (bitwise AND): context decides. `::foo` is a reference for base GSC that many games use;
`foo()` is a call — passing the wrong one is a silent logic bug.

## 4. Runtime model

Cooperative, event-driven VM inside the engine. Single-threaded; "threads" are coroutines.

- **`level`** — global match state. One per match. Scripts hang state off it
  (`level.players`, `level.callback`).
- **`self`** — inside a method, the entity it runs on (player, weapon, trigger, etc.).
- **`game[]`** — associative array that persists across map/round restarts
  (`game["scores"]`).
- **Entities** — engine objects returned by builtins (`getentarray()`, `spawn()`).

### Events & threading

```gsc
wait 0.05;                                // yield ~1 server frame
wait(0.05);
waitframe();
self waittill( "death" );                 // block until this entity notifies "death"
self waittill( "weapon_fired", weapon );  // can have parameters too
self endon( "disconnect" );               // kill this thread when entity notifies "disconnect"
self notify( "captured", flag );          // fire event, wake matching waittill/endon
```

Standard idiom: `thread` a watcher, put `self endon("...")` at the top so it self-cleans,
loop on `waittill`. Wrong `endon`/`waittill` pairing is the #1 cause of leaked threads. A
loop with no `wait` hangs the game frame.

## 5. Engine builtins

Builtins are C++ engine functions with no `.gsc` source (`iprintln`, `getentarray`, `spawn`,
`isdefined`, `distance`, …). GSCLSP reports their file as the sentinel `"Engine"`.

**Builtins differ per game.** Ancient ones (`iprintln`, since CoD2 2005 at least) rarely change; anything
else may be missing, renamed, or take different args on another title. GSCLSP's per-game
data: hand-tuned JSON with arg metadata for `iw4`/`iw5`; names-only lists from gsc-tool for
the rest.

If you don't know what a builtin does: search the dump for real usages (`search_symbols` +
`read_script`). Final fallback: the CoD4 builtin docs (section 10) — most core builtins
trace back to it.

Dumps with human comments are **developer source** (best reference). Known developer GSC:
iw3 (mod tools), iw4 (shipped raw), iw5 (MS Store re-release developer fastfiles), iw6 (PS4 dump),
t4 and t7 (mod tools). Zero comments = decompiled dump; still readable.

## 6. Dump vs. workspace, resolution order

- **Dump** — decompiled full-game script tree (often tens of thousands of files). The
  de-facto standard library and idiom reference. Indexed once, cached to
  `{dumpPath}/symbols.json`.
- **Workspace** — the user's mod files. Watched and re-indexed live.

`foo()` resolves in this order: (1) local file, (2) engine builtins, (3) macros,
(4) qualified `path::name` / `namespace::name` target, (5) any `#include`d file, then any
indexed symbol. There is no stdlib — the dump *is* the reference.

## 7. Hashed dumps & finding hashed functions

Newer titles hash script identifiers at compile time. Decompilers cannot reverse them, so
dumps show placeholders:

- `_id_52F7(...)` / `function_a1b2c3(...)` — hashed function name
- `level._id_EF89` — hashed field name
- hashed path segments in includes and calls

Seen commonly in `s4`, `iw9`, `jup`, and often `t8`/`t9` dumps. `iw8` (MW2019) dumps are
mostly named, which makes iw8 the best cross-reference for the hashed Sledgehammer/IW-era
titles.

Consequence: a call that is valid on an older title — e.g.
`scripts\mp\gamelogic::endgame` — may not exist under that name on a hashed title. Writing
it errors at compile/load. `search_symbols` cannot find `endgame` there; the name is gone.

### Fuzzy cross-dump matching

For fuzzy cross-dump matching, your goal would be to identify which `_id_XXXX` or `function_ab5ub12` in the hashed dump is a KNOWN function,
or location.

1. **Locate the function in a NAMED dump of a nearby title.** For `s4`/`iw9`, use an `iw8`
   dump. Find the definition and its call sites.
2. **Collect anchors that survive hashing:** string literals (`"tie"`, log strings), array
   keys (`game["end_reason"]["tie"]`), builtin calls (`logstring`), branch shape
   (`if (level.teambased)`), argument counts, but note it could be `if ( level.teambased )` with spaces on edges of parenthesis too.
3. **Search the hashed dump for those strings.** Start in gamemode files (`war`, `dm`,
   `conf`, `dom`, …) and leftover utils — their structure rarely changes between titles.
4. **Match the context.** Same string args + same branching around the call = the hashed
   name at that site is your function.

Example. iw8 (named):

```gsc
if ( level.teambased )
    thread endgame( "tie", game["end_reason"]["tie"] );
else
    thread endgame( undefined, game["end_reason"]["tie"] );
```

s4 (hashed), same logical site:

```gsc
if ( level.teambased )
{
    thread _id_52F7( "tie", game["end_reason"]["tie"] );
    return;
}
thread _id_52F7( undefined, game["end_reason"]["tie"] );
return;
```

Same arguments, same `level.teambased` branch → `_id_52F7` is `endgame` on s4. Note the
decompiler rewrote `else` into early `return`s — match **semantics, not exact text**.

Caveats:

- Very old functions (CoD4-era: `endgame`, `iprintln`) usually keep parameter count/order
  across titles — but not always. Check several call sites in the hashed dump before
  trusting a signature.
- Log strings drift per title (`"[KEY_MOMENT] tie"` vs `"tie"`). Match the distinctive part.
- The tools index only the ACTIVE game's dump. For cross-dump comparison, ask the user to
  switch the workspace config to the other game, or read the other dump with the host's own
  file tools if it is on disk.
- State your confidence. One weak anchor = say "needs confirming" and suggest an in-game
  test before shipping the call.

## 8. Tools

| Tool | Purpose |
|------|---------|
| `get_status` | Active game, workspace, dump indexed?, symbol counts. **Call first.** |
| `search_symbols` | Substring search across builtins + dump + workspace. |
| `get_symbol` | Full detail for one exact name: signature, args, file, builtin flag. |
| `list_builtins` | Enumerate engine builtins for the active game. |
| `list_script_files` | List indexed script paths (filter by path substring). |
| `get_functions_in_file` | Functions defined in one script file. |
| `read_script` | Read dump/workspace source, with line numbers. |
| `resolve_function` | Where does `foo()` bind when called from file X? Real resolution order. |
| `get_gsc_primer` | This document (also resource `gsclsp://primer`). |

Calling rules:

- One tool call at a time; wait for each result.
- Arguments are plain strings/numbers; results are JSON.
- Result `{"status":"indexing_in_progress"}` → wait briefly, retry the same call.
- Empty or odd result → call `get_status`; the server may point at the wrong workspace.
- Only these tools exist. Do not invent others.

Workflow for writing code:

1. `get_status` → active game.
2. `search_symbols` / `list_builtins` → does the function exist for this game?
3. `read_script` a dump file that uses it → copy the real idiom.
4. Write workspace code in that style.
5. `resolve_function` on each cross-file call to confirm it binds.

## 9. Pitfalls

- Builtins vary per game — verify every one against the active game.
- Inactive `#ifdef` regions are dead code — never source an API from them.
- `waittill` loop without matching `endon` leaks the thread forever.
- Loop without `wait` starves the scheduler and hangs the frame.
- No stdlib: if a helper "should exist", search the dump; otherwise write it.
- `::foo` / `&foo` is a reference; `foo()` is a call.
- Qualified paths (`maps\mp\_utility`) — one wrong segment = unresolved call. Verify with
  `resolve_function`.
- Hashed titles: named calls from older games may not exist (section 7).
- Trust the tools over memory.

## 10. Sources

- CoD4 "Introduction to GSC" (best intro): https://wiki.zeroy.com/index.php?title=Call_of_Duty_4:_Introduction
- CoD4 builtin function list: https://scripts.zeroy.com/cod4_script/index.html
- Aurora "Introduction to GSC" for H1 (by mjkzy, GSCLSP co-maintainer): https://docs.auroramod.dev/gsc-scripting
- Black Ops 3 scripting API docs + function list: https://scripts.zeroy.com/
- Black Ops 3 source code explorer: https://bo3explorer.zeroy.com/
- Ghosts developer GSC dump: https://github.com/mjkzy/iw6-gsc-dump
- MWR (H1) reverse-engineered dump, ~98% named — useful for s1/s2/h1/h2: https://github.com/mjkzy/h1-gsc-dump
