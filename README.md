<div align="right">

**[English](README.md)** | [繁體中文](README.zh-TW.md)

</div>

<div align="center">

# RimAgent

**Run your RimWorld colony in natural language.**

An in-game AI agent for RimWorld 1.7. Inspect your colony and issue orders in natural language through 77 tools, with control over when actions require approval.

**RimWorld 1.7** &nbsp;·&nbsp; **77 Tools** &nbsp;·&nbsp; **Autonomous & Auto Modes** &nbsp;·&nbsp; **MCP Extensible**

[![RimWorld 1.7](https://img.shields.io/badge/RimWorld-1.7-10b981.svg?style=flat)](#prerequisites)
[![RimLLM Framework](https://img.shields.io/badge/RimLLM_Framework-%E2%89%A5_2026.10.20.1-38bdf8.svg?style=flat)](https://github.com/mushroomTW/RimLLM-Framework)
[![.NET 10](https://img.shields.io/badge/.NET-10-512bd4.svg?style=flat)](#building)

</div>

---

**Jump to:** [Quick start](#getting-started) · [Modes](#modes--safety) · [Autonomy](#autonomy-autonomous-mode) · [Tools](#what-it-can-do) · [Build and tests](#building)

---

## Getting Started

### Prerequisites

- **RimWorld 1.7**
- **[RimLLM Framework](https://github.com/mushroomTW/RimLLM-Framework)** (version **2026.10.20.1 or later**, required for native tool calling and the decision model)
- Mod load order: `RimAgent` must be placed and loaded **after** `RimLLM Framework`.

> [!IMPORTANT]
> All LLM plumbing (provider selection, API key, model selection, fallback chains, and token budget) is configured in **RimLLM Framework**. RimAgent adds colony tools and per-category tool permissions.

### Quick Start

1. Install and enable **RimLLM Framework** and **RimAgent**, with RimAgent later in the mod load order.
2. In **Mod Settings > RimLLM Framework**, configure a supported model provider and its credentials.
3. Load a game and click the compact **Agent** icon on the bottom bar. Choose **Ask** to inspect without changing the game, or **Confirm** to preview every command before it runs.
4. Enter a request such as `How is the colony doing?`, `Set Alice's Doctor priority to 1`, or `Draft everyone and move to (25, 30)`, then press **Send** or Enter.

The agent uses tools to inspect the current map and carry out permitted actions. In Confirm mode, review each command preview before approving it.

---

## Highlights

- **77 tools:** inspect the colony, issue orders, search the web, and manage global and per-save memories.
- **Five execution modes:** start read-only, require approval, or let the agent act under the selected policy.
- **Autonomous events and goals:** run scheduled checks, respond to urgent events and pending choices, or queue a persistent `/goal`.
- **Subagents and model roles:** the agent can hand self-contained tasks to subagents with their own tools and model (read-only ones run in parallel), and subagents, the Auto-mode classifier and `/compact` summaries each get their own model and reasoning setting.
- **Editable and extensible:** customize prompts in `AgentPack/Prompts/`, add skills or subagents, or connect trusted local MCP servers.
- **Saved progress:** the transcript, model history, scheduler, goals, this save's memories, and `/undo` state persist with the game; global memory lives in the mod folder and is shared by every save.

---

## Window Interface & Slash Commands

Drag the window to move it or its lower-right corner to resize it. RimAgent remembers the position and size.

- Select the execution **mode**, model **reasoning effort**, and **model** from the Agent window dropdowns; hover a mode to see what it does and its risks. The model list shows the entries in your RimLLM Framework fallback chain; the chosen model is tried first for player instructions and autonomous runs, and **Fallback** follows the fallback chain. Everything else is set under **Model roles** in mod settings (after hermes-agent's auxiliary models): the subagent default, each subagent, the Auto-mode classifier and `/compact` summaries each pick a model (**Default (agent's current model)**, **Fallback**, or a fallback-chain entry) and a reasoning level. All of them follow the agent's current model by default. **Send** changes to **Stop** while a request is running. A player instruction has no configured tool-call limit.
- The title shows input tokens reported for the last model request, or "unavailable" if the provider supplied no usage. Hover to see the model-history message count and automatic compaction threshold.
- Your messages appear in right-aligned bubbles, and each instruction starts a new turn separated by a divider. Tool calls show a one-line summary; click one to show its arguments and result. Previews awaiting confirmation are expanded by default. Drag across any message to select and copy its text; right-click a tool row to copy it. LaTeX math (`$...$`) is shown as plain readable text.
- The conversation follows new messages only while you are at the bottom. If you scroll up to read history, a **Jump to latest / New messages** button appears.
- With the input box focused, ↑↓ recall previously sent input.
- Type `/` for slash-command completion (↑↓ to navigate, Tab or Enter to select):

| Command | Description |
| --- | --- |
| `/goal <text>` | Queue a persistent goal that starts shortly after creation and follows the current mode's command policy |
| `/goal` | Show queued goals, status, turn counts, latest progress, and the last completed goal |
| `/goal show <n>` | Show the full text of goal `n` |
| `/goal edit <n> <text>` | Edit goal `n`; editing the active goal resets its progress while preserving a manual pause |
| `/goal remove <n>` | Delete goal `n` |
| `/goal clear` | Clear all goals and the last completed record |
| `/goal pause` / `/goal resume` | Pause or resume persistent goal execution |
| `/undo` | Restore the work priorities and schedules modified by the last instruction |
| `/clear` | Clear the visible conversation transcript |
| `/compact` | Compact context: summarize older model history with one LLM call, keeping only recent turns (visible log kept) |
| `/stop` | Immediately abort the currently running instruction |

> **Context compaction:** Before a request, Agent Framework estimates history tokens. At the configured share of the model context limit supplied by RimLLM Framework (30–95%, 90% by default, with output tokens reserved), older tool output is folded without another model call. Auto-fold is unavailable if the model limit is unknown. Stored history is separately trimmed after 120 messages to about 60; the visible transcript remains. `/compact` requests a deeper, model-generated summary.

> **Run history:** Enable **Record Agent runs** in mod settings to write instructions, tool calls, safety decisions, and outcomes to `RimWorld save data/RimAgent/RimAgent-runs.jsonl`. It is off by default and keeps at most 500 runs or 16 MiB; the settings page can open the log folder directly.

> **Prompts are editable:** `AgentPack/Prompts/` contains the built-in prompts, `AgentPack/Skills/` holds optional skills (nine skills ship by default: eight heuristic gameplay skills (raid defense, defense building, food security, mood, work assignment, medical triage, seasonal prep and colony progression) and the ship-launch ending route), `AgentPack/Agents/` holds subagents (one read-only `scout` ships by default), and `AgentPack/AGENTS.md` ships empty for your own instructions. Reload the save or start a new game after editing; no rebuild is needed. The agent only receives a skill index each turn; full skill texts load on demand through `read_skill`. See [skill setup](AgentPack/Skills/README.md) and [MCP setup](AgentPack/MCP/README.md).

---

## Modes & Safety

One setting decides how much the agent may do on its own:

| Mode | Behavior |
| --- | --- |
| **Ask** | Read-only. The model sees query tools only; map switching and screenshots are excluded because they change state. |
| **Confirm** | Queries run directly. Every command is previewed and waits for your approval. |
| **YOLO** | Permitted commands, including MCP calls, run without confirmation. A fixed blacklist excludes high-risk tools such as trade, caravans, and surgery; generic gizmo, right-click and dialog options still ask every time. No background scheduler. |
| **Auto** | A secondary model checks each command, including MCP calls, for risk and whether you requested it. Only low-risk, requested actions run. High-risk, unrequested, or unclassifiable actions are rejected with a reason; they do not become confirmation prompts. Blacklisted high-risk tools are rejected directly without a classifier call. Pick the classifier model and reasoning under Model roles in mod settings: it follows the agent's current model by default, or you can pin a fallback-chain entry or Fallback. |
| **Autonomous** | Adds emergency responses, base upkeep, and a fixed-cadence scheduled check. Commands run directly, without the YOLO blacklist or a daily tool budget; cooldown is configurable. |

### Confirmation details

- **Confirm / Discard:** Confirm executes the captured tool call and arguments without asking the model again. Discard or a new instruction clears pending calls.
- If a confirmed command fails, later commands remain pending for another confirmation. The failed command is removed because it may have partially taken effect; the log shows how many commands completed and remain.
- **Always allow:** In Confirm mode, eligible tools can be allowed for the current session by name. This is reset by `/clear` or a restart and never applies to `mcp_call_tool`, the high-risk blacklist (`execute_trade`, `form_caravan`, `release_prisoner`, `schedule_surgery`, `send_caravan`, `set_prisoner_interaction`, `settle_caravan`) or generic options (`use_gizmo`, `use_right_click_option`, `choose_option`). Auto still reviews every command.
- **MCP calls:** `mcp_call_tool` needs explicit approval in Confirm mode, passes through the classifier in Auto mode, and runs directly in YOLO or Autonomous mode.

> [!WARNING]
> Combat orders reach the game several seconds after speaking due to LLM round-trip latency. They are designed for strategic positioning, rally points, and tactical retreats, not split-second micro-control.

> [!CAUTION]
> MCP servers run as local processes with your user operating system privileges. Only enable local MCP servers that you trust.

---

## Decision Models (Optional)

Set up a decision model in **RimLLM Framework's Decision tab** and RimAgent uses it for a few high-frequency judgements — cases where the set of valid answers is known in advance and no text has to be generated, so these calls are cheaper and faster than a chat model:

- **Auto mode risk checks:** instead of a second chat-model call per command, the decision model answers "how hard is this action to undo" and "did the player ask for this action"; the answer decides whether the command runs or is rejected with a reason. The criteria live in `AgentPack/Prompts/DecisionRisk.md` and you can rewrite them for your own colony.
- **Goal completion audit:** when the agent claims a `/goal` is complete, the decision model independently checks whether the work really is done, whether the agent still has steps of its own, and whether it is stuck on something only you can provide. A goal that fails the audit keeps running and is asked for evidence instead of being removed.
- **Task routing:** pick **Auto routing** in the Agent window's model dropdown and every instruction is classified first (observe, routine, build, risky), then sent with that category's model and reasoning level. Categories and their models are edited under Decision routing in mod settings.

Without a decision model none of this is enabled and the agent behaves exactly as before. With one configured but the call failing (timeout, missing key, service offline), that judgement falls back to the ordinary model call instead of stalling the instruction. To turn these features off, set the provider back to Disabled in the framework's Decision tab.

RimAgent can pick which decision provider and model to use (Decision model in mod settings); leaving both empty follows the provider the framework has enabled. Keys and endpoints stay in RimLLM Framework.

---

## Autonomy (Autonomous Mode)

Select **Autonomous** from the Agent window's mode dropdown to let the agent act in the background:

- **Event responses:** React to raids beginning or ending, impending mental breaks, food shortages, downed or bleeding colonists, and fires. New choice letters and dialogs are checked every 2 real-time seconds, including while a dialog pauses the game; each choice triggers once.
- **Base upkeep:** On by default. The agent acts when temperatures become dangerous, a colonist has no bed, beds are outdoors, nothing is running on power, no research project is running, or moods are low but nobody is breaking yet. None of these kill anyone or raise an alert, but left alone they compound into the emergencies above. Turn the whole group off in mod settings.
- **Retries while unresolved:** A condition that persists is retried with backoff (1 in-game hour, then 2), for three attempts. If it is still unresolved the conversation reports that the agent gave up, and it starts fresh only once the situation clears. Stopping a run from the Agent window (or `/stop`) also stops retries for that condition until it clears; a stopped routine inspection waits a full interval before running again. Older versions asked once and then ignored a fire that kept burning.
- **Scheduled checks:** When nothing is wrong, push one thing forward every 6 in-game hours (a construction project, a research lead, a crop field, an order). The cadence is fixed and deliberately not configurable: it is the agent's operating rhythm, not a noise preference.
- **Budget and cooldown:** No daily tool-call limit, but each autonomous run is capped at 20 tool calls to stop background loops. A 0–60 minute cooldown can be set in mod settings and is disabled by default.
- **Persistent goals:** `/goal` runs independently in any mode and follows that mode's approval policy. Goals can contain up to 4,000 characters; longer objectives are rejected instead of truncated. Goals, status, and progress are saved with the game. Completion requires a successful query after the last change. The goal pauses after the same blocker repeats for three turns, three turns pass without a successful tool action, repeated errors, or 60 turns in one run; `/goal resume` starts a fresh run. Editing does not undo a manual pause.

---

## What It Can Do

All 15 categories are enabled by default and can be switched off individually in mod settings, or all at once with Allow all / Deny all. The selected mode determines how their commands run.

| Category | Examples |
| --- | --- |
| Observe | Colony status, resources, threats, research, maps, objects, areas, character-grid map views, screenshots, skill texts, pending choices, and quests (list, accept, dismiss) |
| Work | Work priorities and daily schedules (undoable) |
| Build | Designations, blueprints (single, line, fill, or outline; up to 400 cells), forbidden items, gizmo buttons (including ones that need a target), and right-click menu options |
| Combat | Draft, move, attack, rescue, capture, tend, strip, equip, and enter cryptosleep caskets |
| Trade | Orbital and visiting traders, buy/sell transactions, planning and forming caravans (pack animals, food, cargo; cancel while forming), and the world map (world objects and tiles, sending caravans with arrival orders, pause/stop, settling) |
| Medical | Health, medicine levels, and surgery |
| Prisoner | Prisoner status, interaction modes, and release |
| Animal | Animals, taming, training, and allowed areas |
| Zone | Stockpiles, growing zones, and storage filters |
| Production | Work tables and bills |
| Time | Pause, game speed, and waiting for in-game time to pass |
| MCP | Tools from configured local MCP servers |
| Web | `web_search` uses Parallel Search MCP anonymously by default; a local SearXNG server is optional |
| Memory | `file_memory_*` tools read, list, search, write, edit and delete memory files; names starting with `global_` are shared by every save, the rest belong to one save (30 files in each scope) |
| Subagents | `delegate_task` hands self-contained tasks to the subagents in `AgentPack/Agents/` |

Web search connects directly to [Parallel Search MCP](https://docs.parallel.ai/integrations/mcp/search-mcp) by default, without an API key or local server. Anonymous requests have lower rate limits; search queries are sent to Parallel. To use a local source instead, enable **Use local SearXNG** in RimAgent settings and enter its URL (`http://127.0.0.1:8888` by default). The server must be running with JSON output enabled; [free-web-search](https://github.com/vksco/free-web-search) provides one setup. Local searches request Google, DuckDuckGo and Qwant engines and exclude Bing results. Results are external text and should be checked before use. Because queries leave the game, web search follows the current mode's command policy and is unavailable in Ask mode.

Memory uses the Agent Framework's `FileMemoryProvider` in two scopes, chosen by the file name. A name starting with `global_` is **global memory**: it is kept in `Memory/global-memory.json` inside the same RimAgent folder as the run logs and is shared by every save, so it suits the player's standing preferences and rules (`global_preferences.md` by convention). Every other name is **memory for this save**: it is stored in the game save, a new game starts empty, and loading an older save restores its memories. Every instruction receives an index built from both scopes (file names and descriptions of every file in both scopes), and the agent reads a file when it is relevant. Each scope allows 30 files of 4,000 characters; names are limited to 60 characters, and descriptions are flattened to one line of up to 200 characters. Reading, listing and searching are queries; writing, editing and deleting follow the current mode's command policy, so Ask mode can only read memories. Memories named `global_*` that an older save still holds are handed back to global memory when it is loaded.

Subagents follow hermes-agent's `delegate_task`. Each subagent is an `AgentPack/Agents/<name>/AGENT.md` file whose frontmatter declares its tool categories, `read_only`, and optional `reasoning`. The agent passes up to four task briefs per call. Each task runs in a fresh context with the subagent's own prompt, tools and model, and only the final report enters the agent's history. Read-only subagents run their tasks in parallel; others run them in order. A subagent only gets categories that are also allowed in the settings. Its commands pass through the same mode, confirmation, classifier, autonomous budget, `/undo` and action log, and the action log labels its tool calls with its name. Subagents cannot delegate further. The shipped `scout` is read-only reconnaissance across every colony category.

---

## How It Works

```text
Player request → ColonyAgentSession → RimLLM Framework model → colony tools → IGameWorld → RimWorld
```

The model receives tools selected for the current mode and category permissions. With the Subagents category on, it also gets `delegate_task`, which runs a subagent through the same tools on a separate model call. Two interfaces keep most behavior testable without starting RimWorld:

- **`IChatClient`** lets tests use scripted model responses without network calls.
- **`IGameWorld`** lets tool tests use an in-memory world; `RimWorldGameWorld` connects those tools to the game.

UI text and the Agent main-button label are localized in English, Traditional Chinese, and Simplified Chinese.

---

## Building

```bash
dotnet build "Source/RimAgent/RimAgent.csproj" -c Release
```

Release compilation writes `Assemblies/RimAgent.dll` plus bundled SmashTools assemblies and standalone assemblies required by the mod that are not provided by the base game or framework:

- **Bundled SmashTools**: `SmashTools.dll`, `CoreLib.dll`.
- **Agent Framework & MCP SDK**: `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Abstractions`, `Microsoft.Extensions.AI.Evaluation`, `Microsoft.Extensions.Compliance.Abstractions`, `Microsoft.Extensions.VectorData.Abstractions`, `Microsoft.Extensions.FileSystemGlobbing`, `Microsoft.ML.Tokenizers`, `Google.Protobuf`, `ModelContextProtocol.Core`.

RimWorld 1.7 provides `System.Collections.Immutable`, `Microsoft.Bcl.Memory`, and `Microsoft.Extensions.ObjectPool`; `HashCode` is part of .NET 10 itself. RimWorld scans assemblies on startup with `Assembly.GetTypes()` and `GetFields()`, requiring all base and referenced field types to resolve cleanly. The project references RimLLM Framework assemblies from `../RimLLM Framework/Assemblies` (override with `/p:RimLLMDir=<path>` if installed elsewhere).

---

## Tests

### Unit Tests (Offline)

```bash
dotnet test "Source/RimAgent.Tests/RimAgent.Tests.csproj"
```

The NUnit suite runs on .NET 10 without launching RimWorld. It covers tool behavior, confirmation previews, save/load state, risk classification, and the tool count shown in both READMEs.

### In-Game Integration Tests

In-game integration tests live in `Source/RimAgent.InGameTests/` and use SmashPhil DevTools. They compile to the development-only companion mod `Source/RimAgent.InGameTests/Mod/Assemblies/` and are not distributed to players.

Build all projects with `dotnet build Source/RimAgent.slnx -c Release`, then load the companion mod alongside RimAgent and its dependencies. Use the DevTools Test Explorer, or launch the game with `--pid GreenMushroom.RimAgent.InGameTests --test --where "namespace =~ RimAgent.InGameTests" --exit`. Save progress first: tests create a colony and switch scenes. Results are written to `Test.log` and `test-report.md` in the game's persistent data directory; `Mod/Configs/UnitTestConfig.xml` can set a separate log folder. The suite includes startup checks, colony operations, and SmashTools initialization and thread dispatch. Marker-based model experiments are separate and require explicit configuration.

Third-party DLL sources are pinned under `Source/Libs/`. To rebuild them, use PowerShell 7: `pwsh -File Source/Build-Dependencies.ps1`. The script retains source in `.scratch/` and prints hashes of the actual build output. Full redistributed license texts are in `ThirdPartyNotices.txt`.

---

This project is licensed under the [MIT License](LICENSE).
