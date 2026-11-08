<div align="right">

**[English](README.md)** | [繁體中文](README.zh-TW.md)

</div>

<div align="center">

# RimAgent

**Run your RimWorld colony in natural language.**

An in-game AI agent for RimWorld 1.7. Inspect your colony and issue orders in natural language through 82 tools, with control over when actions require approval.

**RimWorld 1.7** &nbsp;·&nbsp; **82 Tools** &nbsp;·&nbsp; **Proactive Care** &nbsp;·&nbsp; **MCP Extensible**

[![RimWorld 1.7](https://img.shields.io/badge/RimWorld-1.7-10b981.svg?style=flat)](#prerequisites)
[![RimLLM Framework](https://img.shields.io/badge/RimLLM_Framework-%E2%89%A5_2026.10.20.1-38bdf8.svg?style=flat)](https://github.com/mushroomTW/RimLLM-Framework)
[![.NET 10](https://img.shields.io/badge/.NET-10-512bd4.svg?style=flat)](#building)

</div>

---

**Jump to:** [Quick start](#getting-started) · [Modes & safety](#modes--safety) · [Decision models](#decision-models-optional) · [Autonomy](#autonomy-autonomous-mode) · [Tool categories](#what-it-can-do) · [Build and tests](#building)

---

## Getting Started

### Prerequisites

- **RimWorld 1.7**
- **[RimLLM Framework](https://github.com/mushroomTW/RimLLM-Framework)** (version **2026.10.20.1 or later**, required for native tool calling and decision models)
- **Mod load order:** `RimAgent` must be placed and loaded **after** `RimLLM Framework`.

> [!IMPORTANT]
> All LLM plumbing (provider selection, API key, model selection, fallback chains, and token budget) is configured in **RimLLM Framework**. RimAgent provides colony tools and per-category tool permissions.

### Quick Start

1. Install and enable **RimLLM Framework** and **RimAgent**, ensuring RimAgent loads after the framework.
2. In **Mod Settings > RimLLM Framework**, configure a supported model provider and its API credentials.
3. Load a save or start a new game, then click the compact **Agent** icon on the bottom navigation bar. Start with **Confirm** mode to preview every command before it takes effect.
4. Enter an instruction such as `How is the colony doing?`, `Set Alice's Doctor priority to 1`, or `Draft everyone and move to (25, 30)`, then press **Send** or Enter.

The agent uses tools to inspect the current map and execute authorized actions. In Confirm mode, review each command preview before granting approval.

---

## Highlights

- **82 Tools:** Comprehensive capabilities to inspect colony state, issue real-time orders, search the web, and manage dual-scope memories (global and save-specific).
- **Four execution modes:** Select from strict step-by-step confirmation, routine actions with major action gating, automated AI safety classification (Auto mode), or unrestricted autonomous execution.
- **Autonomous events and goals:** Proactively handle emergencies, manage routine base upkeep, perform scheduled patrols, or queue multi-step persistent tasks via `/goal`.
- **Subagents and model roles:** Delegate self-contained missions to subagents with isolated tools and contexts (read-only tasks run in parallel); assign dedicated models and reasoning levels to subagents, the Auto-mode classifier, and `/compact` summaries.
- **Customizable and MCP-extensible:** Edit prompts in `AgentPack/Prompts/`, add custom skills or subagents, or connect local external tools via the official Model Context Protocol (MCP).
- **Persistent progress:** Full conversation transcripts, model history, scheduler state, persistent goals, colony memories, and `/undo` history persist with your save; global memories are shared across all saves in the mod data directory.

---

## Window Interface & Slash Commands

Drag the window title bar to move it, or drag the lower-right corner to resize it. RimAgent automatically remembers its position and size.

### Controls & Model Selection

- **Mode and Model Dropdowns:** Switch execution **mode**, model **reasoning effort**, and active **model** directly from the Agent window. Hover over any mode to view its operational behavior and safety implications. The model list reflects entries in your RimLLM Framework fallback chain; the selected model is prioritized for player commands and autonomous routines, while **Fallback** follows the chain sequentially.
- **Model Roles Configuration:** In the advanced section of mod settings (**Model roles**, inspired by hermes-agent auxiliary models), assign specialized models and reasoning levels for subagents (shared slot), the Auto-mode classifier, and `/compact` summaries. Options include **Default (agent's current model)**, **Fallback**, or specific fallback-chain entries. All default to following the agent's current model.
- **Execution State:** While a request is running, the **Send** button dynamically toggles to **Stop**. Direct player instructions have no configured tool-call limit.
- **Token Counter & Telemetry:** The title bar displays the input token count reported from the latest model request (or "unavailable" if not reported by the provider). Hovering displays total history count and the auto-compaction threshold.
- **Conversation Flow:** Player messages appear in right-aligned bubbles, each instruction initiating a distinct turn separated by a divider. Tool calls display clean one-line summaries; click any row to inspect full arguments and results. Previews awaiting confirmation expand by default. Text can be selected and copied freely; right-click any tool row to copy its summary. LaTeX math notation (`$...$`) is rendered as readable plain text.
- **Smart Auto-Scroll:** The view follows incoming messages automatically when scrolled to the bottom. If you scroll up to inspect history, scrolling is decoupled, and a **Jump to latest / New messages** button appears.
- **Input History:** With the text input focused, press ↑ / ↓ to cycle through previously submitted commands.
- **Slash Command Autocompletion:** Type `/` at the prompt to invoke the command menu (navigate with ↑ / ↓, select with Tab or Enter):

| Command | Description |
| --- | --- |
| `/goal <text>` | Queue a persistent goal that starts promptly and adheres to current mode safety policies |
| `/goal` | List queued goals, operational status, turn counts, latest progress, and last completed goal |
| `/goal show <n>` | Display the full text and progress details of goal `n` |
| `/goal edit <n> <text>` | Edit goal `n`; updating the active goal recalculates progress while retaining manual pause state |
| `/goal remove <n>` | Remove goal `n` from the queue |
| `/goal clear` | Clear all queued goals and recent completion history |
| `/goal pause` / `/goal resume` | Pause or resume persistent goal execution |
| `/undo` | Revert work priorities and daily schedules altered by the previous instruction |
| `/clear` | Clear the visible conversation transcript in the window |
| `/compact` | Compact context: trigger an LLM summarization call to fold older model history into a concise briefing (visible log is preserved) |
| `/stop` | Immediately terminate the currently running instruction and active tool calls |

### Context Management & Run History

> **Context Compaction:**
> 1. **Auto-fold:** Prior to each model invocation, Agent Framework estimates total history tokens. When reaching a configured threshold (configurable from 30% to 95%, default 90% with reserved output headroom) of the smallest known context limit among the chosen model and fallback entries, older tool outputs are folded automatically without additional LLM calls. If no limit is known, auto-fold is skipped.
> 2. **History Trimming:** Stored model message history exceeding 120 messages is pruned back to approximately 60 messages to preserve memory; visible transcript logs remain untouched.
> 3. **Deep Semantic Compact:** Trigger `/compact` at any time to invoke a dedicated model summarization pass, condensing extended dialogue history into an actionable summary.

> **Run History Logging:**
> Enable **Record Agent runs** in mod settings to log all instructions, tool invocations, safety assessments, and outcomes to `RimWorld save data/RimAgent/RimAgent-runs.jsonl`. Off by default; retains up to 500 runs or 16 MiB. A shortcut button in settings opens the log directory directly.

> **Prompt & Extensibility Architecture:**
> - `AgentPack/Prompts/`: Houses built-in system prompts.
> - `AgentPack/Skills/`: Holds optional skills (ships with fourteen built-in skills: eight heuristic gameplay skills for raid defense, defense building, food security, mood management, work assignment, medical triage, seasonal prep, and colony progression; plus six ending questlines: ship launch, journey offer, archonexus, royal ascent, void, and mechhive). Requests contain only a skill index; full skill texts are loaded on demand via `read_skill`.
> - `AgentPack/Agents/`: Contains subagent definitions (includes a default read-only reconnaissance agent, `scout`).
> - `AgentPack/AGENTS.md`: An empty file reserved for user gameplay rules and standing orders.
> - Modifications take effect immediately upon reloading a save or starting a new game—no compilation required. See [Skill setup](AgentPack/Skills/README.md) and [MCP setup](AgentPack/MCP/README.md).

---

## Modes & Safety

A single execution mode configuration governs the autonomy and safety boundaries of the agent:

| Mode | Behavior |
| --- | --- |
| **Confirm** | Read-only queries run immediately. Any command altering game state generates a preview and awaits explicit player approval. |
| **Execute** | Routine commands execute immediately; high-impact commands (such as surgery, trade, or caravans) require confirmation. Proactive care major actions follow a dedicated setting. |
| **Auto** | Each command is evaluated beforehand by a secondary classifier model or decision model for risk and player intent. Low-risk, explicitly requested actions proceed automatically; high-risk, unrequested, or ambiguous actions are rejected with a clear explanation, **without generating confirmation prompts**. Blacklisted high-hazard tools are rejected immediately without invoking the classifier. Classifier model and reasoning are customizable under Model Roles. |
| **Full access** | All enabled tools (including MCP) execute directly without execution-mode gates. Category permission toggles remain in effect. |

### Confirmation Details

- **Confirm / Discard:** Approving a preview executes the exact captured tool call and arguments without querying the model again, preventing unintended repeated executions. Discarding or submitting a new prompt clears pending calls.
- **Failure Gating:** If a confirmed command in a multi-step sequence fails, subsequent pending commands remain paused awaiting re-confirmation. The failed command is removed from the queue (as partial game-state mutations may have occurred), and the log indicates completed versus remaining operations.
- **Always Allow (Session Whitelist):** In Confirm mode, eligible tools can be whitelisted by name for the remainder of the game session. This whitelist resets upon `/clear` or restarting RimWorld and is never written to saves. It never applies to `mcp_call_tool`, high-risk blacklist tools (`execute_trade`, `form_caravan`, `release_prisoner`, `schedule_surgery`, `send_caravan`, `set_prisoner_interaction`, `settle_caravan`), or generic UI interactors (`use_gizmo`, `use_right_click_option`, `choose_option`).
- **MCP Invocations:** In Confirm and Execute modes, MCP calls require explicit player approval. In Auto mode, they are evaluated by the safety classifier. In Full access mode, enabled MCP tools execute directly. Proactive care in Execute mode obeys the major-action confirmation setting.

> [!WARNING]
> Combat commands experience several seconds of latency due to LLM round-trip inference. They are designed for macro-level positioning, rally points, and tactical retreats rather than split-second micro-control.

> [!CAUTION]
> Local MCP servers run as background processes with your current operating system user privileges. Only install and enable MCP servers that you thoroughly trust.

---

## Decision Models (Optional)

Configure a dedicated decision model in **RimLLM Framework's Decision tab** to delegate high-frequency, low-latency categorical evaluations to specialized, lightweight models. Because outputs are constrained to known option sets without generating freeform text, these calls are significantly faster and cheaper:

- **Auto Mode Risk Evaluation:** Replaces secondary chat-model invocations by evaluating "how difficult is this action to reverse" and "did the player explicitly request this action". Evaluation criteria are defined in `AgentPack/Prompts/DecisionRisk.md` and can be customized to match your playstyle.
- **Goal Completion Audit:** When the agent reports a `/goal` as completed, the decision model independently verifies whether all tasks were truly fulfilled, whether remaining autonomous steps exist, or whether the agent is blocked by requirements only the player can provide. Unverified goals continue running and request evidence rather than prematurely closing.
- **Task Category Routing:** Selecting **Auto routing** in the Agent window model dropdown routes each incoming player command through the decision model to classify it (observation, routine upkeep, construction, or high-risk planning), subsequently executing it with the designated model and reasoning level. Category-to-model mappings can be configured in the Decision Model section of advanced mod settings.

> [!NOTE]
> If no decision model is configured, these features remain disabled and the agent behaves identically to previous versions. If a configured decision call fails (due to timeout, missing credentials, or offline service), RimAgent gracefully falls back to a standard model invocation without blocking execution. To disable completely, set the provider to Disabled in the framework's Decision tab.

---

## Autonomy (Autonomous Mode)

Enable **proactive care** in mod settings to allow the agent to monitor events and maintain the colony in the background, strictly adhering to the selected execution mode. In Execute mode, major proactive actions execute without confirmation by default; enable the dedicated confirmation setting to review them. Full access always executes enabled tools directly. Confirm mode always requires approval for every action.

Major actions are determined by static risk metadata on each tool (including trade, surgery, generic gizmos, and MCP calls) rather than dynamic LLM estimation.

- **Emergency Event Responses:** Monitors and reacts to raid start and end events, impending mental breaks, food shortages, downed or bleeding colonists, and fires. Game choice letters and dialogs are inspected every 2 real-time seconds (even while dialogs pause the game); each choice prompts an action only once.
- **Base Upkeep:** Enabled by default. Proactively remedies hazardous room temperatures, unassigned beds, outdoor sleeping, unpowered grids, idle research benches, and declining mood levels. While not immediately lethal, unattended upkeep issues compound into severe crises. The entire upkeep group can be toggled off in mod settings.
- **Exponential Backoff Retries:** Persistent unresolved conditions trigger retries with exponential backoff (waiting 1 in-game hour, then 2), up to three attempts. If unresolved, the conversation logs an explicit surrender message and resets tracking only after the condition clears, eliminating repeated loops while ensuring persistent issues are not permanently ignored. Aborting a run via the window stop button or `/stop` also pauses retries until cleared; aborted scheduled patrols wait a full interval before re-running.
- **Scheduled Patrols:** Every 6 in-game hours, the agent conducts a holistic review of colony operations to prioritize upcoming tasks.
- **Call Budget and Cooldown:** Each proactive cycle is capped at 20 tool calls (except in Full access mode). A customizable cooldown of 0 to 60 minutes can be set in mod settings (disabled by default).
- **Persistent Goals (`/goal`):** Runs independently across any execution mode and respects the active approval policy. Supports detailed instructions up to 4,000 characters (exceeding lengths are rejected rather than silently truncated). Goal state, step-by-step progress, and completion logs persist with the save file. Completion requires an explicit verification query after the final modification. Goals automatically pause if the same blocker recurs for three turns, if three turns elapse without successful tool operations, after consecutive errors, or upon reaching 60 turns in a single run; resume execution anytime with `/goal resume`. Manual pause states are preserved when editing.

---

## What It Can Do

All 16 tool categories except Save are enabled by default and can be toggled individually in mod settings, or simultaneously with Allow All / Deny All. The selected execution mode governs how commands are evaluated and run.

| Category | Capabilities & Examples |
| --- | --- |
| **Observe** | Comprehensive colony overview, resources, threat assessment, research progress, maps, object lookups, allowed areas, ASCII character-grid map representations, real-time screenshots, skill index/body retrieval, pending choices, and quest management (list, accept, dismiss) |
| **Work** | Individual and batch work priority adjustments, 24-hour daily schedules (fully reversible with `/undo`) |
| **Build** | Designations (mine, cut, harvest, hunt, deconstruct, haul), blueprint placement (single cell, line, rectangle fill, outline; up to 400 cells), item forbidden toggles, gizmo command triggers (including targeted commands), and right-click context menu actions |
| **Combat** | Colonist drafting, tactical movement, attack targeting, casualty rescue, enemy capture, emergency medical tending, equipment stripping, weapon/gear equipping, and entering cryptosleep caskets |
| **Trade** | Orbital and visiting trader interactions, buying and selling transactions, caravan preparation (pack animal assignment, rations, cargo loading, cancellation during packing), and world map operations (inspecting world objects and tiles, dispatching caravans with arrival directives, pause/resume movement, settling new colonies) |
| **Medical** | Colonist health triage, care/medicine level policies, and surgical procedure scheduling |
| **Prisoner** | Prisoner physical and mental status, interaction modes (recruit, enslave, etc.), and unconditional release |
| **Animal** | Livestock and wild animal rosters, taming designations, training regimes, and assigned zones |
| **Zone** | Stockpile and dumping zones, growing zone crop selection, and storage filter criteria |
| **Production** | Crafting bench inspection, bill creation, recipe configuration, and workload prioritization |
| **Time** | Game pause, speed multiplier adjustment (1x, 2x, 3x, 4x), and precise waiting for in-game time intervals |
| **MCP** | Dynamic discovery and execution of external tools from connected local MCP servers |
| **Web** | `web_search` queries online RimWorld documentation and guides via anonymous Parallel Search MCP by default; optional self-hosted SearXNG integration |
| **Memory** | `file_memory_*` suite reads, lists, searches, writes, edits, and deletes memory notes; files prefixed with `global_` are shared across all saves, while others belong strictly to the active save (up to 30 files per scope) |
| **Subagents** | `delegate_task` delegates self-contained sub-tasks to specialized subagents defined in `AgentPack/Agents/` |
| **Save** | Automatic checkpoint creation and death-triggered save recovery using checkpoints or autosaves (disabled by default) |

### Core Subsystems

#### Web Search
`web_search` connects out-of-the-box to [Parallel Search MCP](https://docs.parallel.ai/integrations/mcp/search-mcp) anonymously without requiring an API key or local setup. To use a local provider, enable **Use local SearXNG** in RimAgent settings and specify its URL (`http://127.0.0.1:8888` by default, with JSON output enabled; see [free-web-search](https://github.com/vksco/free-web-search)). Local queries aggregate Google, DuckDuckGo, and Qwant while filtering out Bing results. Search queries exit the game environment and strictly adhere to the active execution mode policy.

#### Save Checkpoints & Automatic Recovery
The Save category is off by default. When enabled, RimAgent creates a safe daily checkpoint (`RimAgent-<colony>-<tick>`) once per in-game day provided no hostiles, bleeding/downed colonists, or active fires are on the map, retaining the newest 5 checkpoints. `list_saves` displays checkpoints and autosaves belonging to the current colony (correlated via an internal colony identifier). If proactive care is enabled, the death of a colonist initiates an autonomous evaluation where the agent decides whether to invoke `load_save` to revert to an earlier save, passing lessons learned into the reloaded timeline. Each save can be reloaded up to 3 times, after which older saves are locked to enforce permanent consequences. Loading an older save purges newer checkpoints. Reload counts persist until the game process exits. Note that Commitment mode prohibits save/reload interventions.

#### Dual-Scope Memory Architecture
Memory management utilizes Agent Framework's `FileMemoryProvider` across two scopes determined by filename prefixes:
- **Global Memory:** Files prefixed with `global_` are persisted in `Memory/global-memory.json` within the RimAgent data folder. These files are shared across all saves, making them ideal for standing player preferences, playstyles, and colony rules (`global_preferences.md` by convention).
- **Save Memory:** All other memory files are serialized directly into the RimWorld save file. New games start with clean save memory, and loading an older save restores memories from that point in time.
- **Index & Constraints:** Each turn automatically injects a unified index summarizing filenames and descriptions from both scopes; the agent retrieves full contents on demand. Each scope allows up to 30 files of 4,000 characters each; filenames are limited to 60 characters and descriptions are truncated to 200 characters single-line.

#### Subagent Collaboration
Subagents follow the hermes-agent `delegate_task` paradigm. Subagents are defined in `AgentPack/Agents/<name>/AGENT.md` using YAML frontmatter to declare permitted tool categories, `read_only` status, and optional `reasoning` levels.
The primary agent can delegate up to four distinct task briefs per invocation. Each brief executes in an isolated context with its own prompt, tool pack, and assigned model, returning only its final summary report to the primary agent's history. Read-only subagents execute concurrently in parallel; state-altering subagents run sequentially. Subagents inherit only tool categories enabled in global mod settings, and all actions remain bound by the current execution mode, confirmation gates, proactive budget limits, `/undo` stacks, and run logs. The default `scout` subagent provides comprehensive read-only reconnaissance across all categories.

---

## How It Works

```text
Player request → ColonyAgentSession → RimLLM Framework model → colony tools → IGameWorld → RimWorld
```

Available tools are dynamically filtered based on current mode and category permissions. When the Subagents category is active, `delegate_task` is exposed to invoke subagents via separate model calls. Two core abstractions ensure extensive testability without booting RimWorld:

- **`IChatClient`:** Enables offline unit testing with pre-recorded or scripted model responses without network traffic.
- **`IGameWorld`:** Provides an in-memory mock game world for comprehensive tool validation; `RimWorldGameWorld` bridges calls to the actual RimWorld engine in-game.

All UI text and the Agent main-button label are fully localized in English, Traditional Chinese, and Simplified Chinese.

---

## Building

```bash
dotnet build "Source/RimAgent/RimAgent.csproj" -c Release
```

Release compilation outputs `Assemblies/RimAgent.dll`, alongside bundled SmashTools and required standalone assemblies not supplied by the base game or framework:

- **Bundled SmashTools Assemblies:** `SmashTools.dll`, `CoreLib.dll`.
- **Agent Framework & MCP SDK Assemblies:** `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Abstractions`, `Microsoft.Extensions.AI.Evaluation`, `Microsoft.Extensions.Compliance.Abstractions`, `Microsoft.Extensions.VectorData.Abstractions`, `Microsoft.Extensions.FileSystemGlobbing`, `Microsoft.ML.Tokenizers`, `Google.Protobuf`, `ModelContextProtocol.Core`.

RimWorld 1.7 natively supplies `System.Collections.Immutable`, `Microsoft.Bcl.Memory`, and `Microsoft.Extensions.ObjectPool`; `HashCode` is provided by the .NET 10 runtime. Because RimWorld inspects types via `Assembly.GetTypes()` and `GetFields()` during startup, all base and referenced types resolve cleanly. The project references RimLLM Framework assemblies from `../RimLLM Framework/Assemblies` (override with `/p:RimLLMDir=<path>` if installed elsewhere).

---

## Tests

### Unit Tests (Offline)

```bash
dotnet test "Source/RimAgent.Tests/RimAgent.Tests.csproj"
```

The NUnit test suite runs on .NET 10 without launching RimWorld. It covers tool execution, confirmation gate logic, save/load state persistence, risk classification, and documentation tool count validation.

### In-Game Integration Tests

In-game integration tests reside in `Source/RimAgent.InGameTests/` and use SmashPhil DevTools. They compile into a development-only companion mod at `Source/RimAgent.InGameTests/Mod/Assemblies/` and are not distributed to players.

1. Build all projects with `dotnet build Source/RimAgent.slnx -c Release`.
2. Load the companion test mod alongside RimAgent and its prerequisites.
3. Run tests via the DevTools Test Explorer UI, or launch RimWorld with `--pid GreenMushroom.RimAgent.InGameTests --test --where "namespace =~ RimAgent.InGameTests" --exit`.
4. Integration tests create a live colony and cycle game scenes; save your progress before running. Results are logged to `Test.log` and `test-report.md` in the game's persistent data directory (or a custom path defined in `Mod/Configs/UnitTestConfig.xml`). The suite validates startup sequences, colony mutations, SmashTools lifecycle, and multi-threaded dispatch. Marker-enabled live model experiments are isolated and require explicit API key configuration.

Third-party dependencies are pinned under `Source/Libs/`. Rebuild them using PowerShell 7 with `pwsh -File Source/Build-Dependencies.ps1`. The script caches sources in `.scratch/` and validates output binary SHA-256 hashes. Full license notices are available in `ThirdPartyNotices.txt`.

---

## License

This project is licensed under the [MIT License](LICENSE).
