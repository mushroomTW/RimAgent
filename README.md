<div align="right">

**[English](README.md)** | [繁體中文](README.zh-TW.md)

</div>

<div align="center">

# RimAgent

**Run your RimWorld colony in natural language.**

An in-game AI agent for RimWorld 1.6. Inspect your colony and issue orders in natural language through 51 tools, with control over when actions require approval.

**RimWorld 1.6** &nbsp;·&nbsp; **51 Tools** &nbsp;·&nbsp; **Autonomous & Auto Modes** &nbsp;·&nbsp; **MCP Extensible**

[![RimWorld 1.6](https://img.shields.io/badge/RimWorld-1.6-10b981.svg?style=flat)](#prerequisites)
[![RimLLM Framework](https://img.shields.io/badge/RimLLM_Framework-%E2%89%A5_2026.09.12-38bdf8.svg?style=flat)](https://github.com/mushroomTW/RimLLM-Framework)
[![.NET Framework 4.7.2](https://img.shields.io/badge/.NET_Framework-4.7.2-512bd4.svg?style=flat)](#building)

</div>

---

**Jump to:** [Quick start](#getting-started) · [Modes](#modes--safety) · [Autonomy](#autonomy-autonomous-mode) · [Tools](#what-it-can-do) · [Build and tests](#building)

---

## Getting Started

### Prerequisites

- **RimWorld 1.6**
- **[RimLLM Framework](https://github.com/mushroomTW/RimLLM-Framework)** (version **2026.09.12 or later**, required for native tool calling)
- Mod load order: `RimAgent` must be placed and loaded **after** `RimLLM Framework`.

> [!IMPORTANT]
> All LLM plumbing (provider selection, API key, model selection, fallback chains, and token budget) is configured in **RimLLM Framework**. RimAgent adds colony tools and per-category tool permissions.

### Quick Start

1. Install and enable **RimLLM Framework** and **RimAgent**, with RimAgent later in the mod load order.
2. In **Mod Settings > RimLLM Framework**, configure a supported model provider and its credentials.
3. Load a game and click **Agent** on the bottom bar. Choose **Ask** to inspect without changing the game, or **Confirm** to preview every command before it runs.
4. Enter a request such as `How is the colony doing?`, `Set Alice's Doctor priority to 1`, or `Draft everyone and move to (25, 30)`, then press **Send** or Enter.

The agent uses tools to inspect the current map and carry out permitted actions. In Confirm mode, review each command preview before approving it.

---

## Highlights

- **51 colony tools:** inspect the map, assign work, plan construction, issue combat and medical orders, trade, and manage animals, prisoners, and zones.
- **Five execution modes:** start read-only, require approval, or let the agent act under the selected policy.
- **Autonomous events and goals:** run scheduled checks, respond to urgent events and pending choices, or queue a persistent `/goal`.
- **Editable and extensible:** customize prompts in `AgentPack/Prompts/`, add skills, or connect trusted local MCP servers.
- **Saved progress:** the transcript, model history, scheduler, goals, and `/undo` state persist with the game.

---

## Window Interface & Slash Commands

Drag the window to move it or its lower-right corner to resize it. RimAgent remembers the position and size.

- Select the execution **mode** and model **reasoning effort** from the dropdowns. **Send** changes to **Stop** while a request is running.
- The title shows input tokens reported for the last model request, or "unavailable" if the provider supplied no usage. Hover to see the model-history message count and automatic compaction threshold.
- Type `/` for slash-command completion (↑↓ to navigate, Tab or Enter to select):

| Command | Description |
| --- | --- |
| `/goal <text>` | Queue a persistent goal that starts shortly after creation and follows the current mode's command policy |
| `/goal` | List queued goals and the active goal's status |
| `/goal remove <n>` | Delete goal `n` |
| `/goal pause` / `/goal resume` | Pause or resume persistent goal execution |
| `/undo` | Restore the work priorities and schedules modified by the last instruction |
| `/clear` | Clear the visible conversation transcript |
| `/compact` | Compact context: summarize older model history with one LLM call, keeping only recent turns (visible log kept) |
| `/stop` | Immediately abort the currently running instruction |

> **Context compaction:** When model history exceeds the configured threshold (default 60 messages), older tool output is folded before a request without an extra model call. At twice the threshold, the oldest complete turns are removed from stored model history; the visible transcript remains. `/compact` requests a deeper, model-generated summary.

> **Run history:** Enable **Record Agent runs** in mod settings to write instructions, tool calls, safety decisions, and outcomes to `RimWorld save data/RimAgent/RimAgent-runs.jsonl`. It is off by default and keeps at most 500 runs or 16 MiB.

> **Prompts are editable:** `AgentPack/Prompts/` contains the built-in prompts, `AgentPack/Skills/` holds optional skills, and `AgentPack/AGENTS.md` ships empty for your own instructions. Reload the save or start a new game after editing; no rebuild is needed. See [skill setup](AgentPack/Skills/README.md) and [MCP setup](AgentPack/MCP/README.md).

---

## Modes & Safety

One setting decides how much the agent may do on its own:

| Mode | Behavior |
| --- | --- |
| **Ask** | Read-only. The model sees query tools only; map switching and screenshots are excluded because they change state. |
| **Confirm** | Queries run directly. Every command is previewed and waits for your approval. |
| **YOLO** | Permitted commands, including MCP calls, run without confirmation. A fixed blacklist excludes high-risk tools such as trade, caravans, and surgery. No background scheduler. |
| **Auto** | A secondary model checks each command, including MCP calls, for risk and whether you requested it. Only low-risk, requested actions run. High-risk, unrequested, or unclassifiable actions are rejected with a reason; they do not become confirmation prompts. The classifier model is configurable in mod settings. |
| **Autonomous** | Adds scheduled inspections and event responses. Commands run directly, without the YOLO blacklist or a daily tool budget; cooldown is configurable. |

### Confirmation details

- **Confirm / Discard:** Confirm executes the captured tool call and arguments without asking the model again. Discard or a new instruction clears pending calls.
- **Always allow:** In Confirm mode, eligible tools can be allowed for the current session by name. This is reset by `/clear` or a restart and never applies to `mcp_call_tool` or the high-risk blacklist (`execute_trade`, `form_caravan`, `release_prisoner`, `schedule_surgery`, `set_prisoner_interaction`). Auto still reviews every command.
- **MCP calls:** `mcp_call_tool` needs explicit approval in Confirm mode, passes through the classifier in Auto mode, and runs directly in YOLO or Autonomous mode.

> [!WARNING]
> Combat orders reach the game several seconds after speaking due to LLM round-trip latency. They are designed for strategic positioning, rally points, and tactical retreats, not split-second micro-control.

> [!CAUTION]
> MCP servers run as local processes with your user operating system privileges. Only enable local MCP servers that you trust.

---

## Autonomy (Autonomous Mode)

Select **Autonomous** in mod settings or the mode dropdown to let the agent act in the background:

- **Scheduled checks:** Inspect the colony at a configurable interval (default: every 6 in-game hours).
- **Event responses:** React to raids beginning or ending, impending mental breaks, food shortages, downed or bleeding colonists, and fires. New choice letters and dialogs are checked every 2 real-time seconds, including while a dialog pauses the game; each choice triggers once.
- **Budget and cooldown:** No daily tool-call limit. A 0–60 minute cooldown can be set in mod settings and is disabled by default.
- **Persistent goals:** `/goal` runs independently in any mode and follows that mode's approval policy. Goals are saved with the game; after three blocked/error turns or 60 turns, a goal pauses for review until `/goal resume`.

---

## What It Can Do

All 12 categories are enabled by default and can be switched off individually in mod settings. The selected mode determines how their commands run.

| Category | Examples |
| --- | --- |
| Observe | Colony status, resources, threats, research, maps, objects, areas, screenshots, and pending choices |
| Work | Work priorities and daily schedules (undoable) |
| Build | Designations, blueprints (single, line, fill, or outline; up to 400 cells), forbidden items, and gizmo buttons |
| Combat | Draft, move, attack, rescue, capture, tend, strip, equip, and enter cryptosleep caskets |
| Trade | Orbital and visiting traders, buy/sell transactions, and caravans |
| Medical | Health, medicine levels, and surgery |
| Prisoner | Prisoner status, interaction modes, and release |
| Animal | Animals, taming, training, and allowed areas |
| Zone | Stockpiles, growing zones, and storage filters |
| Production | Work tables and bills |
| Time | Pause and game speed |
| MCP | Tools from configured local MCP servers |

---

## How It Works

```text
Player request → ColonyAgentSession → RimLLM Framework model → colony tools → IGameWorld → RimWorld
```

The model receives tools selected for the current mode and category permissions. Two interfaces keep most behavior testable without starting RimWorld:

- **`IChatClient`** lets tests use scripted model responses without network calls.
- **`IGameWorld`** lets tool tests use an in-memory world; `RimWorldGameWorld` connects those tools to the game.

UI text and the Agent main-button label are localized in English, Traditional Chinese, and Simplified Chinese.

---

## Building

```bash
dotnet build "Source/RimAgent/RimAgent.csproj" -c Release
```

Release compilation writes `Assemblies/RimAgent.dll` plus the 12 standalone assemblies required by the mod that are not provided by the base game or framework:

- **10 Agent Framework Assemblies**: `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Abstractions`, `Microsoft.Extensions.Compliance.Abstractions`, `Microsoft.Extensions.ObjectPool`, `Microsoft.Extensions.VectorData.Abstractions`, `Microsoft.Extensions.FileSystemGlobbing`, `Microsoft.Bcl.Memory`, `Microsoft.Bcl.HashCode`, `Microsoft.ML.Tokenizers`, `Google.Protobuf`.
- **2 MCP SDK Assemblies**: `ModelContextProtocol.Core`, `System.Collections.Immutable`.

RimWorld scans assemblies on startup with `Assembly.GetTypes()` and `GetFields()`, requiring all base and referenced field types to resolve cleanly. The project references RimLLM Framework assemblies from `../RimLLM Framework/Assemblies` (override with `/p:RimLLMDir=<path>` if installed elsewhere).

---

## Tests

```bash
dotnet test "Source/RimAgent.Tests/RimAgent.Tests.csproj"
```

The NUnit suite runs on .NET Framework 4.7.2 without launching RimWorld. It covers tool behavior, confirmation previews, save/load state, risk classification, and the tool count shown in both READMEs.

---

This project is licensed under the [MIT License](LICENSE).
