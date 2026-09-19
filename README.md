# RimAgent

A RimWorld 1.6 mod that lets you run your colony in natural language. Open the **Agent** tab on the bottom bar and type what you want done — "set Alice's Doctor priority to 1", "mark the steel in the selected area for mining", "draft everyone and move to (25, 30)". The agent reads colony state through tools, plans, executes real game actions, and shows every tool call in an action log.

The tab is a single input line plus **Send** (which turns into **Stop** while the agent works). Two dropdowns above it switch the **mode** and the model's **reasoning effort** (Auto / Off / Low / Medium / High; Auto leaves it to the framework's default) — both are saved in the mod settings. Everything else is a slash command: type `/` to open the command list (↑↓ to move, Tab or Enter to pick):

| Command | What it does |
| --- | --- |
| `/goal <text>` | add a long-term goal (autonomous runs work toward these; saved with the game) |
| `/goal` | list goals in the conversation |
| `/goal remove <n>` | delete goal `n` |
| `/undo` | restore the work priorities and schedules changed by the last instruction |
| `/clear` | clear the conversation |
| `/stop` | stop the running instruction |

All LLM plumbing (provider, API key, model, fallback chain, budget) comes from **[RimLLM Framework](https://github.com/mushroomTW/RimLLM-Framework)**, version **2026.09.12 or later** (the first version with native tool calling). RimAgent adds only per-category tool permissions.

## What it can do

| Category | Tools | Default |
| --- | --- | --- |
| Observe | colony overview, colonists, resources, threats, research status and switching, map listing and switching, map object search, game screenshots | on |
| Work | work priorities, daily schedules (undoable) | on |
| Build | mine / cut / harvest / deconstruct / haul designations, blueprints (single or in a line) | on |
| Combat | draft, move, attack | on |
| Trade | orbital trader and visiting caravan stock listing, buying and selling in a single deal, forming player caravans | on |
| Medical | health status, medical care levels, scheduling surgery | on |
| Prisoner | prisoner listing, interaction modes, release | on |
| Animal | animal listing, taming, training, allowed areas | on |
| Zone | zone listing, stockpile and growing zones, storage filters | on |
| Production | work tables and production bills | on |
| Time | pause and time flow speed | on |
| MCP | external tools from local MCP servers | on |

Combat orders reach the game several seconds after you speak — they are for positioning and retreats, not real-time control.

## Modes

One setting decides how much the agent may do on its own (per-category permissions above still decide which tools exist at all):

| Mode | What happens |
| --- | --- |
| **Ask** | Read-only. Only query tools are offered to the model; it inspects and advises. |
| **Confirm** (default) | Tools tagged irreversible — trades, caravans, surgery, prisoner release/interaction, zone and bill deletion, blueprints, designations, attacks — return a preview and wait for **Confirm**; everything else runs directly. |
| **Auto** | Every command is previewed first, then a second model call classifies it (`low` / `high`). Low runs immediately, high waits for **Confirm**. Any classifier failure counts as high. Costs one extra request per command. |
| **Autonomous** | Fully delegated: every command runs without asking, and the scheduler below starts the agent's own runs. You can still type instructions. |

Every tool carries a static risk tag (`Query` / `Safe` / `Confirm`) and every command tool has a preview branch that touches nothing. The test suite checks both paths for all command tools. Confirm executes the exact captured tool call and arguments from the preview; it never re-sends the instruction to the model, so already-safe actions cannot run twice.

## Safety

* **Confirm** / **Discard**: Confirm executes the captured previewed calls with their original arguments; it does not make another LLM request. A new instruction or Discard drops every pending call.
* **`/undo`**: restores the previous instruction's work priorities and daily schedules — also for an instruction that failed halfway, so whatever did land can be reverted.
* Guard rails inside the tools: `order_attack` refuses colony members, prisoners and friendly factions; `schedule_surgery` requires an explicit body part for surgeries that target one; `execute_trade` needs a negotiator who can talk and is not incapable of social work, and a powered comms console for orbital traders; `form_caravan` moves the named colonists off the map immediately (no walking to the edge, no supplies packed); zones are validated with the game's own zone designators (fog, map edge, blocking things, fertility).
* The visible conversation, compacted model session, autonomous scheduler budget and work undo history are saved with the game. If an older session cannot be deserialized after an upgrade, RimAgent safely starts a fresh model session while retaining the visible transcript.
* The combat-latency reminder appears once per instruction, and Markdown in model replies is rendered via RimLLM Framework (bold, italics, headings, links, code).
* The shipped conduct file (`AgentPack/AGENTS.md`) and skill packs (`AgentPack/Skills/`) are included with every instruction. MCP servers run as local processes with your user privileges — only enable ones you trust.

## Autonomy (Autonomous mode)

Pick the Autonomous mode (mod settings or the mode dropdown in the Agent tab) and the agent starts its own instructions: routine inspections on an interval, plus reactions to raid start/end, colonists near mental break, food shortages, downed or bleeding colonists, and fire. Signals aggregate every map in the save. Every run uses the same session (history, permissions), shows in the action log tagged `[auto]`, can be stopped with the **Stop** button or `/stop`, and counts against a daily tool-call budget (shown next to the dropdowns). Long-term goals are managed with `/goal` and saved with the game.

## How it works

```text
player text ──► ColonyAgentSession ──► ChatClientAgent (Microsoft Agent Framework)
         │                                    │ single GetResponseAsync, history in AF session
         │                                    ▼
         │                              FunctionInvokingChatClient (MEAI, via RimLLM Framework's main-thread wrapper)
         │                                    │ tool calls
         │                                    ▼
            │                              51 tools (AIFunctionFactory) ──► IGameWorld ──► RimWorldGameWorld ──► Verse / RimWorld API
          │
         └─► system prompt via ChatOptions.Instructions (CachedContext left empty, pending token measurement)
```

Two seams make the mod testable without the game:

* **`IChatClient`** — the agent accepts any MEAI chat client; tests use a scripted fake.
* **`IGameWorld`** — tools only see plain data; tests use an in-memory fake world. `RimWorldGameWorld` is a thin adapter verified in-game.

UI strings (`Languages/` Keyed) and the Agent main-button label (`Languages/` DefInjected) ship in English, Traditional Chinese and Simplified Chinese.

## Building

```bash
dotnet build "Source/RimAgent/RimAgent.csproj" -c Release
```

Release writes `Assemblies/RimAgent.dll` plus the 12 DLLs the framework does not ship: 10 Agent Framework DLLs (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.Abstractions`, `Microsoft.Extensions.Compliance.Abstractions`, `Microsoft.Extensions.ObjectPool`, `Microsoft.Extensions.VectorData.Abstractions`, `Microsoft.Extensions.FileSystemGlobbing`, `Microsoft.Bcl.Memory`, `Microsoft.Bcl.HashCode`, `Microsoft.ML.Tokenizers`, `Google.Protobuf`) and 2 MCP SDK DLLs (`ModelContextProtocol.Core`, `System.Collections.Immutable`; the rest of the SDK closure is covered by the framework). RimWorld scans every DLL with `Assembly.GetTypes()` and `GetFields()`, so base types and field types must resolve even for code paths we never execute: `Compliance.Abstractions` stays because AF's internal `ReplacingRedactor` derives from its `Redactor`, `ObjectPool 8.0.x` (assembly version 8.0.0.0, netstandard-only) stays because six `Pooled*Policy` types in Compliance derive from it, and `ML.Tokenizers` + `Google.Protobuf` stay because AF's `CompactionMessageIndex` has a `Tokenizer` field and Tokenizers' types implement Protobuf interfaces. Tokenizers references older `System.Text.Json` / `Microsoft.Bcl.Memory` / `System.Memory` than what is loaded in-game; Unity Mono binds a same-name, same-token assembly of a newer version, so no version alignment is needed (verified in-game). Debug builds and `dotnet test` write to `bin/` and never touch `Assemblies/`.

The project references the installed framework at `../RimLLM Framework/Assemblies`; override with `/p:RimLLMDir=<path>` if RimWorld is not under the default Steam folder. Package versions are pinned to what the framework ships (`Microsoft.Extensions.AI` 10.10.0, `Microsoft.Agents.AI` 1.21.0); do not enable `CopyLocalLockFileAssemblies` — RimWorld loads every mod into one AppDomain and a second copy of `IChatClient` breaks every cast. The framework DLLs are copied from the NuGet cache by the `CopyAgentFrameworkAssemblies` target instead.

## Tests

```bash
dotnet test "Source/RimAgent.Tests/RimAgent.Tests.csproj"
```

NUnit, no game required. Tests run on real .NET Framework while the mod is compiled against RimWorld's Mono reference assemblies, so avoid APIs that exist in Mono's `mscorlib` but not in .NET Framework 4.7.2 (parameterless `TrimEnd()`, `Queue<T>` — Mono places it in `mscorlib`, .NET Framework in `System.dll`).

## License

MIT
