# GSC Primer for AI Clients

This resource explains the **GSC** scripting language for Call of Duty to read and write it
competently through the GSCLSP MCP server. GSC is not a mainstream language: it has
per-game quirks, no standard that is exactly known as it can change, and engine-provided builtins that vary
between titles. Read this before generating GSC code, and lean on the MCP tools to verify
facts against the *active* game and context rather than guessing things.

---

## 1. What GSC is

**.GSC** is the scripting language of **Call of Duty**
games. It runs on Infinity Ward's IW engine, and after Call of Duty 4 (IW3), Treyarch's engine variants also add a
few syntactic extensions later on in their fork. Studios and modders use it to implement almost all gameplay
logic that isn't hard-coded in the C++ engine for be ran in-agme: gamemodes, map scripts, killstreaks, perks,
bots, events from engine -> GSC, spawning, literally most of the ingame logic is done through **GSC**. It can even do *in-game HUD rendering/drawing* which is scripted togther with GSC *(used frequently in GSC dumps, it is called "hud utils", "hud_utils", etc.)*

**.GSC** is derived from "QuakeC", the compiled language developed in 1996 by John Carmack of id Software to program parts of Quake. While GSC is *not* as advanced as QuakeC may be, it still very well handles all of the in-game logic for gameplay. It is essentially the backend of the actual gameplay, and has specific sets of rules and things.

Files use the extensions **`.gsc`** (script) and **`.gsh`** (script header like .h/.hpp). Treyarch titles also use **`.csc`** (client-side script) with the same syntax to do stuff on the client all the time instead of server -> client like IW titles usually did before JUP.

**JUP** is important as a game because it is when GSC combined on both engines to form the universal new GSC that both IW and Treyarch AND other studios use nowadays. This powers Warzone 2 and onwards, and carries forward the Treyarch-like syntax that once existed when introduced in T7 (Black Ops 3) on Treyarch's own line of games. The Treyarch games usually was based off the previous, so if any code came from IW, it was added individually and nitpicked, which is why GSC and built-ins per game can be different as well.

### Game ids used by the tooling

GSCLSP is *game-aware* to make GSC the best it can. Every builtin set, every `#ifdef` region, and every dump is keyed
to a game id. Common ids you will encounter:

| id  | Game (informal) | Engine family |
|-----|-----------------|---------------|
| `iw4` | MW2 (2009)     | Infinity Ward |
| `iw5` | MW3 (2011)     | Infinity Ward |
| `iw6` | Ghosts         | Infinity Ward |
| `s1`  | Advanced Warfare | Sledgehammer |
| `s2`  | World War 2    | Sledgehammer |
| `s4`  | (Sledgehammer-era title) | Sledgehammer |
| `t6`  | Black Ops II   | Treyarch |
| `t7` / `t8` / `t9` | Black Ops III / 4 / Cold War | Treyarch | **(uses new treyarch format t7 and newer)**
| `jup` | (Treyarch-family) | Treyarch | **(uses treyarch format from t9 in jup, born from t7)**

The tooling treats `t7`, `t8`, `t9`, and `jup` as **Treyarch GSC games** — they enable the
Treyarch function-modifier syntax described below. Always confirm the active id via
`get_status`; do not assume. `jup` is Modern Warfare III (2023), which is a extensive fork of `iw9` (Modern Warfare II (2022)) that while it adds a lot of things, is inheritly the same built-in lists and hashing algorithm used, so gsc-tool aliases IW9 data for JUP since JUP doesn't have gsc-tool support.

---

## 2. Core syntax

### Function definitions

The fundamental unit is a **function**:

```gsc
foo(a, b)
{
    // body
    return a + b;
}
```

There are no classes and no top-level statements — everything lives in functions. A file is
a flat list of functions plus preprocessor directives.

**Treyarch modifier keywords** (optional, only on Treyarch-family games) may prefix a
definition, in this order: `function`, then optional `private`, then optional `autoexec`:

```gsc
function do_thing()            { }
function private helper()      { }
function private autoexec __init__()   { }   // autoexec runs at load time
```

Infinity Ward titles use the bare `foo() { }` form with no modifiers. GSCLSP accepts both;
only the three words `function`, `private`, `autoexec` are valid as a prefix.

### Calling conventions

GSC's call syntax encodes *how* and *on what* a function runs:

```gsc
foo();                     // plain synchronous call
result = foo(1, 2);        // call with return value
thread foo();              // start foo on a NEW logical thread (fire-and-forget)
self foo();                // method call: run foo with `self` bound to this entity
ent foo();                 // method call on the entity in variable `ent`
ent thread foo();          // threaded method call on `ent`
level thread watch_score();// threaded method on the global `level` object
```

`thread` schedules the call and continues on immediately after the call; the caller does not wait. Without
`thread`, the call is synchronous and stalls the caller's thread. Prefixing an entity (`self`, `level`, or any variable
holding an entity) makes the function a *method* thats ran on entities — inside it, `self` refers to that entity, which is the caller of the function (can be a player, can be a normal gsc variable, etc.)

### Cross-file calls

Functions in other files are reached one of two ways:

```gsc
// Path-qualified (Infinity Ward style): backslash path + :: + name
maps\mp\_utility::wait_endon( level.players[0], "death", 5 );

// Namespace-qualified (Treyarch style): namespace + :: + name
util::wait_network_frame();
```

The backslash form is a **file path** relative to the script root (`maps\mp\_utility` =>
`maps/mp/_utility.gsc`). The namespace form uses a `#namespace` declared in the target file.

### Directives (preprocessor)

Every legacy game before `t7` or `jup` on the Infinity Ward side has `#include` and `#using_animtree`. However, gsc-tool is a dependency that is a custom compiler for games, enabling the use of custom compiler features. so games like `t4`, `t5`, `iw1`, `iw2`, `iw3`, `iw4` do NOT use gsc-tool and have the custom preprocessors like `define`, `ifdef`, `elifdef`, etc.

```gsc
#include maps\mp\_utility;         // IW: pull in another file's functions
#using scripts\shared\util_shared; // Treyarch spelling of the same idea
#namespace util;                   // Treyarch: declare this file's namespace
#define MAX_PLAYERS 18             // object-like macro
#insert scripts\shared\shared.gsh; // Treyarch header insertion
#precache( "material", "hud_x" );  // asset precache directive (Treyarch)
#using_animtree( "generic" );      // bind an animation tree
```

### Conditional compilation

```gsc
#ifdef GAME_T8
    // only compiled for that game/target - GSCLSP will highlight this region if active
#else
    // fallback if not
#endif
```

GSCLSP marks `#ifdef` regions that don't match the **active game** as *inactive* (grayed
out) and excludes them from diagnostics. When you read a file, remember that inactive
regions are dead code for the current target — do not copy an API from an inactive branch.

For games that do not support it, a error should occur saying that this is not allowed for this game (as gsc-tool is not the compiler, and it is a native-game compiler instead that is legacy IW gsc format)

### Statements & control flow

Standard C-like control flow: `if/else`, `for`, `while`, `foreach (x in arr)`, `switch`
with `case`/`default`, `break`, `continue`, `return`. Semicolons terminate statements;
Allman braces are the convention. Variables are dynamically typed and declared by
assignment (`x = 5;`). There is no `var`/`let` keyword.

---

## 3. Special literals & operators

GSC has several sigil-prefixed literals that are easy to misread. The lexer does **not**
treat these as single tokens (it splits e.g. `&func` into `&` + `func`), so tooling handles
them explicitly — but semantically they are single things:

| Form | Meaning |
|------|---------|
| `&func_name` | **New function reference** — a reference to `func_name`, which can be invoked using `[[ func_variable ]](args...);` (Treyarch-based GSC form) |
| `::func_name` | **Function reference** — `level.callback = ::on_spawn;` (assign a fn to call later) |
| `path::func` | Qualified reference/call across a file or namespace |
| `&"LOCALIZED_STRING"` | **Localized string** — resolves to a translated string from the string table |
| `#"hash string"` | **Hash literal** — a precomputed string hash (Treyarch), used as a fast key |
| `%anim_name` | **Animation reference** — refers to an xanim asset |
| `/# ... #/` | **Developer block** — code compiled only in dev/debug builds |
| `maps\mp\gametypes\_rank` | **Backslash path identifier** — a script path used in calls/directives, as well as includes |

Notes:
- `&` in `&func` (function ref) is NOT the same as *binary* in `a & b` (bitwise AND). Context
  decides; spacing conventions differ.
- `::on_spawn` used as an rvalue is a first-class function pointer (reference) you can store and
  later invoke with `thread [[ level.callback ]]();` (the `[[ ]]` dereference-call syntax).
- Dev blocks `/# ... #/` typically wrap `println`/`assert`/cheat-only logic that is stripped
  from retail builds.

---

## 4. Runtime model

GSC's runtime is a cooperative, event-driven virtual machine embedded in the game engine.

- **`level`** — the global game-state object. One per running game/match. Persists for the
  whole match; scripts hang state off it (`level.round`, `level.players`, `level.callback`).
- **`self`** — inside a method, the entity the method is running on (a player, a weapon, a
  trigger, a spawner, etc.). Set by the call convention (`ent foo()` binds `self = ent`).
- **`game[]`** — a persistent associative array that survives across map/round restarts
  within a session (e.g. `game["scores"]`, `game["state"]`). Broader lifetime than `level`.
- **Entities** — engine objects (players, AI, models, triggers). Returned by builtins like
  `getentarray()` / `spawn()` and manipulated via methods and fields.

### Event system & threading

GSC is single-threaded but *cooperatively* scheduled — "threads" are coroutines that yield:

```gsc
wait 0.05;                          // yield for 0.05 seconds (~1 frame at 20Hz)
self waittill( "death", attacker ); // block this thread until the entity notifies "death"
self endon( "disconnect" );         // auto-kill this thread if entity notifies "disconnect"
self notify( "captured", flag );    // fire an event; wakes matching waittill/endon
level notify( "round_end" );
```

- `wait <seconds>` yields time.
- `waittill( "event", args... )` blocks until a matching `notify`, receiving payload args.
- `endon( "event" )` registers an auto-terminate condition for the current thread.
- `notify( "event", data... )` broadcasts an event on an entity/level.

The common idiom is: start a watcher with `thread`, put `self endon("death")` at the top so
it self-cleans, then loop on `waittill`. Getting `endon`/`waittill` wrong is the #1 source
of leaked threads and infinite loops.

---

## 5. Engine built-ins

**Builtins** are functions implemented in the C++ engine, not in any script. Examples:
`iprintln`, `iprintlnbold`, `getentarray`, `spawn`, `getcvar`, `setcvar`, `distance`,
`randomint`, `isdefined`, `getplayers`. They have no `.gsc` source — you cannot
go-to-definition on them; GSCLSP marks their source as the sentinel `"Engine"`.

Crucially, **builtins CAN differ per game.** Legacy functions that have existed since the first Call of Duty game in 2003, like `iprintln`, are usually never changed or removed. However, a function present in `iw5` may not exist in `t8` because this is 2 entirely different forks of the `IW3` (Call of Duty 4) engine which have their own changes respectfully. Some functions may not exist,
they may take different arguments, or may be spelled differently. GSCLSP knows each game's
builtins from per-game data:

- Hand-tuned JSON (`data/{game}_builtins.json`) for fully-supported games (e.g. `iw4`,
  `iw5`) — these carry arg names and min/max arg counts.
- Otherwise, names fetched from the **gsc-tool** project (xensik/gsc-tool) — names only, no
  arg metadata.

Before you use a builtin, verify it exists for the active game with `list_builtins` or
`get_symbol`. Do not assume an MW2 builtin works on Black Ops for example. 

### Additional Context for Built-ins

As a MCP server, you do not have access to the actual game's source code to see the GSC platform communication that enables the backend of these functions which are "engine functions" for GSC. For this reason, you may not understand what a function truly does, and that is to be expected. If you do not know what a function is or does confidently, research the GSC dump attached to the current GSCLSP workspace and see if it used there to get context about it. Else as a FINAL fallback, the Call of Duty 4 built-ins that we ship with GSCLSP can be used for this context. 

If the GSC dump you have been provided contains lots of comments and even developer comments like it may be developer-provided GSC, then you have the best source of reference for that. Developer GSC dumps can be from IW3 (mod tools source GSC), IW4 (raw GSC code still shipped with game), IW5 (developer fastfiles shipped on accident on re-release of Microsoft Store MW3), and IW6 (ps4 developer gsc dump) easily. The same is possible for the World at War (T4) and Black Ops 3 (T7) mod tool GSC files, so keep it in mind. If there are 0 comments that look to be human or explaining the code though, it is assumed to be looking at dumped GSC logic instead, which is still readable.

---

## 6. Dumps vs. workspace

There are two bodies of GSC the tooling reasons about:

- **Dump** — a folder of *decompiled full-game scripts*: the entire shipped script tree of a
  game, often **tens of hundreds** of `.gsc`/`.gsh` files. This is the de-facto reference
  library / "standard library" for that game — it shows exactly how the engine builtins are
  used and what helper functions exist. It is treated as static and indexed once (cached to
  `{dumpPath}/symbols.json`). If the GSC dump provided being used contains developer GSC,
  you will quickly be able to tell as many things are explained, including names of developers
  who worked on features or code there. you can recommend these dumps in the *Sources* section of the primer.
- **Workspace** — the files the user is actively editing (their mod). Small, watched for
  changes, re-indexed live.

### Symbol resolution order

When resolving whether `foo()` is defined, GSCLSP checks, in order:

1. **Local file** — functions defined in the same file.
2. **Engine builtins** — the active game's builtin set.
3. **Macros** — `#define`s in scope.
4. **Qualified path** — the `path::name` / `namespace::name` target (an `#include`d file, or
   any indexed symbol at that path).
5. **Unqualified** — any `#include`d file, then any symbol anywhere (workspace + dump).

There is **no classical stdlib** — the dump *is* the reference. To learn an idiom, read how
the shipped scripts do it, then mirror that in the workspace and build context/logic of it.

---

## 7. How to use the GSCLSP MCP tools

You have these tools. Use them to ground every claim about GSC in the *active game*:

| Tool | Use it to… |
|------|-----------|
| `get_status` | **Call this first.** Get the active game id, workspace path, whether a dump is loaded, and index size. Everything else is relative to the active game. |
| `search_symbols` | Fuzzy/substring-search functions across builtins + dump + workspace. |
| `get_symbol` | Fetch full detail for one symbol: signature, args, source file, whether it's a builtin. |
| `list_builtins` | Enumerate the engine builtins for the active game (with arg info where available). |
| `list_script_files` | List script files in the dump/workspace (to find where something lives). |
| `get_functions_in_file` | List the functions defined in a specific script file. |
| `read_script` | Read the actual source of a dump/workspace script — do this to learn real idioms. |
| `resolve_function` | Resolve a (possibly qualified) call to its definition using the real resolution order. |

Also available as a resource: **`gsclsp://primer`** (this document).

### Recommended workflow

1. `get_status` → confirm the active game (say, `iw5`). Everything you write must target it.
2. Before calling any function in generated code, verify it exists **for that game**:
   `resolve_function` or `get_symbol` / `list_builtins`. If it doesn't resolve, don't use it.
3. To learn how to do something (spawn a killstreak, hook a killcam, etc.), `search_symbols`
   for a relevant name, `list_script_files`/`get_functions_in_file` to locate it, then
   `read_script` the dump file to see the real idiom — then write code in that style.
4. Respect per-game API differences: an idiom read from an `iw5` dump may not port to `t8`.

---

## 8. Common pitfalls

- **Builtins vary per game.** Never carry a builtin from one title to another without
  verifying with `list_builtins` / `get_symbol` for the active game.
- **`#ifdef` regions inactive for the current game are dead code.** Don't copy an API out of
  a branch that isn't active for the target — it won't compile there.
- **Threading/`endon` leaks.** A `waittill` loop without a matching `endon` (or a `notify`
  that never fires) leaks the thread forever. Always pair watchers with an `endon`.
- **Recursion & tight loops.** A loop with no `wait` starves the scheduler and can hang the
  game frame; recursive threads without a base case leak. GSCLSP warns on some of these.
- **No stdlib.** There is no `import` of a standard library — reusable helpers come from the
  dump or the user's `#include`s. If a helper "should exist," search the dump; if it doesn't,
  it must be written.
- **Function refs vs. calls.** `&foo` / `::foo` is a *reference*, `foo()` is a *call*. Passing
  the wrong one is a silent logic bug.
- **Qualified-path spelling.** `maps\mp\_utility` (backslashes, no extension) is a path;
  getting a segment wrong makes the call unresolved. Verify with `resolve_function`.
- **Trust the tools over memory.** GSC APIs are game-specific and sparsely documented online.
  When unsure, resolve/read against the actual index rather than guessing.

### Sources

GSC is a well known scripting language, but other games do not officially provide support and access to it,
therefore additional resources and sources may be needed. As an AI assistant, you can recommend these to the user
OR you yourself could just set it up for them if you believe it is what you need. The following sources are
great ways to understand GSC better and get the best dumps available:
- Call of Duty 4 "Introduction to GSC" **(best)**: https://wiki.zeroy.com/index.php?title=Call_of_Duty_4:_Introduction
- Call of Duty 4 built-in script function list: https://scripts.zeroy.com/cod4_script/index.html
- Aurora's "Introduction to GSC" guide for H1 (by mjkzy, the other maintainer of GSCLSP): https://docs.auroramod.dev/gsc-scripting
- Black Ops 3 scripting API documentation (and function list): https://scripts.zeroy.com/
- Black Ops 3 Source Code Explorer (script explorer): https://bo3explorer.zeroy.com/
- Call of Duty Ghosts developer "source" GSC dump: https://github.com/mjkzy/iw6-gsc-dump
- Call of Duty MWR (H1) gsc dump **(reverse engineered GSC with 98% things named, useful for S1, S2, H1, H2)**: https://github.com/mjkzy/h1-gsc-dump
