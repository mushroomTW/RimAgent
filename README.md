# RimAgent

A RimWorld 1.6 mod that lets you run your colony in natural language. Open the **Agent** tab on the bottom bar and type what you want done — "set Alice's Doctor priority to 1", "mark the steel in the selected area for mining", "draft everyone and move to (25, 30)". The agent reads colony state through tools, plans, executes real game actions, and shows every tool call in an action log.

All LLM plumbing (provider, API key, model, fallback chain, budget) comes from **[RimLLM Framework](https://github.com/mushroomTW/RimLLM-Framework)**, version **2026.09.12 or later** (the first version with native tool calling). RimAgent adds only per-category tool permissions.

## What it can do

| Category | Tools | Default |
| --- | --- | --- |
| Observe | colony overview, colonists, resources, threats, research, map object search | on |
| Work | work priorities, daily schedules | on |
| Build | mine / cut / harvest / deconstruct / haul designations, blueprints (single or in a line) | off |
| Combat | draft, move, attack | off |

Combat orders reach the game several seconds after you speak — they are for positioning and retreats, not real-time control.

## How it works

```text
player text ──► ColonyAgentSession ──► ChatClientAgent (Microsoft Agent Framework)
         │                                    │ single GetResponseAsync, history in AF session
         │                                    ▼
         │                              FunctionInvokingChatClient (MEAI, via RimLLM Framework's main-thread wrapper)
         │                                    │ tool calls
         │                                    ▼
         │                              15 tools (AIFunctionFactory) ──► IGameWorld ──► RimWorldGameWorld ──► Verse / RimWorld API
         │
         └─► system prompt via ChatOptions.Instructions (CachedContext left empty, pending token measurement)
```

Two seams make the mod testable without the game:

* **`IChatClient`** — the agent accepts any MEAI chat client; tests use a scripted fake.
* **`IGameWorld`** — tools only see plain data; tests use an in-memory fake world. `RimWorldGameWorld` is a thin adapter verified in-game.

UI strings ship in English, Traditional Chinese and Simplified Chinese (`Languages/`). Planned work is tracked in [TODO.md](TODO.md).

## Building

```bash
dotnet build "Source/RimAgent/RimAgent.csproj" -c Release
```

Release writes `Assemblies/RimAgent.dll` plus the 8 Agent Framework DLLs the framework does not ship (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.Abstractions`, `Microsoft.Extensions.Compliance.Abstractions`, `Microsoft.Extensions.VectorData.Abstractions`, `Microsoft.ML.Tokenizers`, `Microsoft.Extensions.FileSystemGlobbing`, `Microsoft.Bcl.Memory`, `Microsoft.Bcl.HashCode`). Debug builds and `dotnet test` write to `bin/` and never touch `Assemblies/`.

The project references the installed framework at `../RimLLM Framework/Assemblies`; override with `/p:RimLLMDir=<path>` if RimWorld is not under the default Steam folder. Package versions are pinned to what the framework ships (`Microsoft.Extensions.AI` 10.10.0, `Microsoft.Agents.AI` 1.21.0); do not enable `CopyLocalLockFileAssemblies` — RimWorld loads every mod into one AppDomain and a second copy of `IChatClient` breaks every cast. The framework DLLs are copied from the NuGet cache by the `CopyAgentFrameworkAssemblies` target instead.

## Tests

```bash
dotnet test "Source/RimAgent.Tests/RimAgent.Tests.csproj"
```

NUnit, no game required. Tests run on real .NET Framework while the mod is compiled against RimWorld's Mono reference assemblies, so avoid APIs that exist in Mono's `mscorlib` but not in .NET Framework 4.7.2 (parameterless `TrimEnd()`, `Queue<T>` — Mono places it in `mscorlib`, .NET Framework in `System.dll`).

## License

MIT
