# GSC Primer

GSC = the Call of Duty scripting language. It is game-specific and sparsely documented online.
Your training data on it is unreliable. Every function you write must be verified against the
ACTIVE game with the tools below. Never write a call the tools cannot confirm.

Two tiers: the **Core** below is operational (read it fully before writing GSC); the
**Reference** after the divider is lookup material (read sections on demand).

---

# Core

## Rules (always apply)

1. Call `get_status` first. Note `currentGame`. All code you write targets that game.
2. Before using any function, confirm it exists for the active game with `resolve_function`,
   `get_symbol`, or `list_builtins`. If it does not resolve, do not use it.
3. Learn idioms by reading the dump (`read_script`), then write workspace code in that style.
4. Never carry an API from one game to another without re-verifying it in the target game.
5. Names like `_id_52F7` or `function_a1b2c3` mean the dump is **hashed** — see Reference §7.
6. If you cannot call tools, say so and ask the user. Do not guess APIs from memory.
7. The "Comment Rules" apply to how you think and use comments in any project. **THIS IS IMPORTANT TO MINIMIZE UNNEEDED CODE.**

## Task router

Pick the recipe that matches your task, then follow its tool sequence literally.

| Task | Recipe |
|------|--------|
| Edit existing workspace code | Recipe A |
| Write new code / call an unfamiliar function | Recipe B |
| Dump shows `_id_XXXX` / `function_abc123` names | Recipe C (hashed) |
| Tool returns empty/odd result | Call `get_status`; confirm the workspace and active game |
| Understand an unknown builtin | `search_script_content` for real usages, then `read_script` one |

## Recipes (copy the shape)

```
Recipe A — edit existing code:
1. get_status                                              → note currentGame
2. read_script {scriptPath: "<file you are editing>"}      → see the surrounding idiom
3. For each function it references:
     get_symbol {name: "<name>"}                           → exists for this game? args?
4. Make edits in that file's existing style.
5. resolve_function {callingScriptPath: "<file>", functionName: "path::name"}
     for EVERY cross-file call                             → must not be NotFound
6. Do NOT copy an API out of an inactive #ifdef branch (Reference §2).
```

```
Recipe B — write new code / verify a function:
1. get_status                                              → note currentGame
2. search_symbols {query: "endgame"}                       → exists for this game?
3. get_symbol {name: "endgame"}                            → signature, arg counts
4. read_script {scriptPath: "<file from step 2>"}          → copy the real idiom
5. Write code in that style.
6. resolve_function {callingScriptPath: "<your file>", functionName: "path::name"}
     for EVERY cross-file call                             → must not be NotFound
```

```
Recipe C — identify a hashed function via string anchors:
1. Locate the function in a NAMED dump of a nearby title (iw8 for s4/iw9).
     Find its definition and call sites.
2. Collect anchors that survive hashing: string literals ("tie"), array keys
     (game["end_reason"]["tie"]), builtin calls (logstring), branch shape
     (if (level.teambased)), argument counts.
3. search_script_content {pattern: "tie", pathFilter: "gametypes", contextLines: 2}
     in the hashed dump                                    → grep bodies; search_symbols
     matches names only and cannot find string anchors.
4. Match SEMANTICS not text: same string args + same branching around the call = your function.
5. For each neighbor call you enable: get_symbol / list_builtins → confirm it exists this game.
6. State your confidence. Full procedure: Reference §7.
```

## Code templates (fill the blanks)

```gsc
// Thread-watcher: endon self-cleans the thread; every loop has a wait.
// The two endons below are the standard pair — see the checklist before dropping either.
watch_<EVENT>()
{
    level endon( "game_ended" );   // match over → thread dies (almost always wanted)
    self endon( "disconnect" );    // when self is a player: player left → thread dies
    while ( true )
    {
        self waittill( "<EVENT>" );
        // handle event
        wait 0.05;
    }
}
```

```gsc
// Function reference: store a ref (::name, or &name on new-Treyarch), invoke with [[ ]].
level.callback = ::<FUNC>;           // &<FUNC> on new-Treyarch GSC
thread [[ level.callback ]]();
```

```gsc
// Minimal #ifdef region: keep shared lines OUTSIDE the branch.
end_round()
{
    setomnvarforallclients("ui_objective_state", 0);   // shared — outside branch
#ifdef <GAME_ID>
    // only lines that differ for this game
#else
    // fallback
#endif
}
```

## Pre-write checklist

- [ ] Every new function verified to exist for the active game (`get_symbol` / `search_symbols` / `list_builtins`).
- [ ] Every cross-file call passes `resolve_function` (not `NotFound`).
- [ ] Every `waittill` loop has a matching `endon`.
- [ ] Every spawned thread ends on the right lifetime: `level endon( "game_ended" )` for match-lifetime loops, plus `self endon( "disconnect" )` when it runs on a player. Omit `game_ended` only with an explicit reason (e.g. post-game/killcam logic that waits for its own end event).
- [ ] Every loop has a `wait` / `waitframe` (no `wait` starves the frame).
- [ ] No API copied out of an inactive `#ifdef` branch.
- [ ] On hashed titles: named calls from older games may not exist (Reference §7).
- [ ] `::foo` / `&foo` is a reference; `foo()` is a call — used the right one (Reference §3).

## Tools

| Tool | Purpose |
|------|---------|
| `get_status` | Active game, workspace, dump indexed?, symbol counts. **Call first.** |
| `search_symbols` | Substring search of symbol NAMES across builtins + dump + workspace. |
| `search_script_content` | Grep script BODIES (dump + workspace) for a substring/regex — string literals, hashed names, code patterns. |
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

---

# Reference (read sections on demand — do not read linearly)

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
| `t8` / `t9` | Black Ops 4 / Cold War | Treyarch | dumps **hashed**; compiled with a non gsc-tool compiler. atian-cod-tools (https://github.com/ate47/atian-cod-tools) supports these — if the user has it, tell them and work with it. |
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
reports an error there. Treyarch `t7`+ has its own native `#define`/`#insert` support.

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

**Keep `#ifdef` regions minimal.** Only the lines that actually differ per game belong
inside; hoist shared code out instead of duplicating it in every branch:

```gsc
end_round()
{
    setomnvarforallclients("ui_objective_state", 0);   // same on all games — outside
    setomnvar("ui_bomb_interacting", 0);
#ifdef S4
    thread scripts\mp\gamelogic::_id_52F7(game["attackers"], game["end_reason"][tolower(game[game["defenders"]]) + "_eliminated"]);
#else
    thread scripts\mp\gamelogic::endgame(game["attackers"], game["end_reason"][tolower(game[game["defenders"]]) + "_eliminated"]);
#endif
}
```

When you edit code with duplicated branches like this, deduplicate it the same way.

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

### Thread cleanup with `endon` (memory safety)

`endon` is how a thread declares its lifetime; without one it runs until the VM kills it,
and leaked threads accumulate — on dedicated servers that run match after match this is a
real crash vector, not a style nit. The two by far most common lifetime anchors:

```gsc
level endon( "game_ended" );   // level notifies "game_ended" when the match ends
self endon( "disconnect" );    // a player entity notifies "disconnect" on leaving
```

Rules of thumb:

- Almost every loop should die with the match: put `level endon( "game_ended" );` at the
  top of the thread unless there is an explicit reason it must outlive the game (e.g.
  post-game/killcam logic — anchor that to its own event instead, like a killcam-ended
  notify).
- Any thread running on a player (`self` is a player, or the loop touches one) also needs
  `self endon( "disconnect" );` so it cleans up when that player leaves. Waiting on an
  entity that no longer exists is a script error waiting to happen.
- Both together is the normal shape for per-player watcher loops; they are cheap, stack
  freely, and multiple `endon`s on one thread are fine.
- `endon` only ends the *current* thread — child threads it spawned keep running and need
  their own `endon`s.

## 5. Engine builtins

Builtins are C++ engine functions with no `.gsc` source (`iprintln`, `getentarray`, `spawn`,
`isdefined`, `distance`, …). GSCLSP reports their file as the sentinel `"Engine"`.

**Builtins differ per game.** Ancient ones (`iprintln`, since CoD2 2005 at least) rarely change; anything
else may be missing, renamed, or take different args on another title. GSCLSP's per-game
data: hand-tuned JSON with arg metadata for `iw4`/`iw5`; names-only lists from gsc-tool for
the rest.

If you don't know what a builtin does, search the dump for real usages, then read one (router:
"Understand an unknown builtin"). Final fallback: the CoD4 builtin docs (§8) — most core
builtins trace back to it.

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

Goal: identify which `_id_XXXX` or `function_ab5ub12` in the hashed dump is a known function
or location.

1. **Locate the function in a NAMED dump of a nearby title.** For `s4`/`iw9`, use an `iw8`
   dump. Find the definition and its call sites.
2. **Collect anchors that survive hashing:** string literals (`"tie"`, log strings), array
   keys (`game["end_reason"]["tie"]`), builtin calls (`logstring`), branch shape
   (`if (level.teambased)`), argument counts, but note it could be `if ( level.teambased )` with spaces on edges of parenthesis too.
3. **Search the hashed dump for those strings** with `search_script_content` (it greps
   script bodies — `search_symbols` matches names only and cannot find string anchors).
   Start in gamemode files (`war`, `dm`, `conf`, `dom`, …) and leftover utils — their
   structure rarely changes between titles.
4. **Match the context.** Same string args + same branching around the call = the hashed
   name at that site is your function.
5. **Verify the surrounding code too.** If you are enabling neighbor calls alongside the
   identified function (e.g. uncommenting `setomnvar` lines), confirm each one exists for
   the active game with `list_builtins` / `get_symbol` first — do not assume they ported.

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
- The tools index only the ACTIVE game's dump — `search_script_content` searches that dump
  and the workspace. For the OTHER (named) game's dump, ask the user to switch the workspace
  config to that game, or read it with the host's own file tools if it is on disk.
- State your confidence. One weak anchor = say "needs confirming" and suggest an in-game
  test before shipping the call.

## 8. Sources

- CoD4 "Introduction to GSC" (best intro): https://wiki.zeroy.com/index.php?title=Call_of_Duty_4:_Introduction
- CoD4 builtin function list: https://scripts.zeroy.com/cod4_script/index.html
- Aurora "Introduction to GSC" for H1 (by mjkzy, GSCLSP co-maintainer): https://docs.auroramod.dev/gsc-scripting
- Black Ops 3 scripting API docs + function list: https://scripts.zeroy.com/
- Black Ops 3 source code explorer: https://bo3explorer.zeroy.com/
- Ghosts developer GSC dump: https://github.com/mjkzy/iw6-gsc-dump
- MWR (H1) reverse-engineered dump, ~98% named — useful for s1/s2/h1/h2: https://github.com/mjkzy/h1-gsc-dump

## JUP-only rules (IMPORTANT FOR JUP)

**reserved-keyword namespaces.** If the target file's `#namespace` is a reserved
keyword (e.g. `class`), `class::func()` collides with the atian-cod-tools compiler's keyword handling; use the
script path form instead from legacy GSC like so:  `scripts\mp\class::func()`. Namespace form stays the default everywhere else; only fall back to the path form when the namespace actually breaks compiling, or you think it may warrant a use case over the namespace being used.

**issue with entity thread function calls.** The atian-cod-tools compiler miscompiles a
threaded call that has BOTH an entity prefix AND a direct function name. The script compiles
but the call misbehaves at runtime. Broken shape — `<entity> thread <name>(args)`:

```gsc
self thread monitor(slot);                      // BROKEN
player thread util::do_thing(a, b);             // BROKEN — namespace form too
self thread scripts\mp\class::setclass(x);      // BROKEN — path form too
```

Rewrite **every such call** as a function-reference invoke. It **is preferred** when there is a thread keyword being used in function calls. To do it properly, you take `&name`, call it with `[[ ]]` wrapping it:

```gsc
self thread [[ &monitor ]](slot);                    // local function
player thread [[ &util::do_thing ]](a, b);           // namespace-qualified
self thread [[ &scripts\mp\class::setclass ]](x);    // path-qualified
```

NOT affected — leave these forms alone:

```gsc
foo();                        // no entity prefix
self foo();                   // no thread
self thread [[ ref ]]();      // already a reference invoke ([[ variable ]] or [[ &name ]])
```

Detection rule: the token before `thread` is an entity (`self`, `level`, or a variable) AND
the token after `thread` is a function name, not `[[`. On `jup`, ALWAYS apply the rewrite. Other 3arc GSC games do **not** need this.

## Comment Rules

The default behavior is to use **no comment(s)**. Write code that explains itself through naming and structure instead, and can be completely understood without comments. A comment is only justified when it carries information that is *not recoverable from the code*, and that a competent developer reading this file would otherwise get wrong. In practice that means:

- A non-obvious external constraint: a Windows/NT behaviour, an undocumented structure layout, a hardware or
  emulator quirk that forces the code into a shape that otherwise looks wrong.
- A deliberate deviation: why the obvious approach was *not* taken, when the code alone would make a reader
  want to "fix" it.
- A reference that saves someone a research session: a spec section, an MSDN structure, a known-bug link.

Never write:

- Restatements of the code (`// increment the counter`, `// call the handler`, `// loop over the modules`).
- Section headers or banners inside a function (`// --- setup ---`, `// Step 3: cleanup`).
- Narration of your own edit or reasoning (`// changed to fix X`, `// this is safe because ...`, `// now uses Y`).
  This is talking to the reviewer, not to the next reader, and it is noise the moment the change is merged.
- Docstring-style headers that just spell out the signature in prose.
- Obvious type or scope information (`// pointer to the process`).

If you are unsure whether a comment qualifies, it does not. Leave it out. A change that adds zero comments is a
perfectly good change; a change that sprinkles comments over otherwise readable code will be rejected.