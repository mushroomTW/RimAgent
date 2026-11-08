# RimAgent Prompt, Skill & Subagent Architecture

RimAgent features an open and customizable prompt architecture. All system prompts, custom instructions, gameplay skills, and autonomous subagents live in plain Markdown files under the `AgentPack/` directory.

Any changes made here take effect immediately upon reloading a save or starting a new game—**no code compilation is required**.

---

## Directory Overview

```text
AgentPack/
├── AGENTS.md                  # User-defined standing instructions and gameplay preferences
├── Prompts/                   # Core system prompts (preamble, conduct, safety, and autonomous rules)
│   ├── Briefing.md            # Foundational preamble and tool calling protocol
│   ├── Conduct.md             # Core behavioral rules and colony survival priorities
│   ├── Autonomous.en.md       # Autonomous trigger rules (raids, medical emergencies, fires, etc.)
│   ├── RiskClassifier.md      # Auto-mode LLM safety risk classification prompt
│   ├── DecisionRisk.md        # Lightweight decision model risk evaluation criteria
│   ├── DecisionGoal.md        # Lightweight decision model goal completion audit prompt
│   └── DecisionRouting.md     # Decision model task category routing prompt
├── Skills/                    # On-demand modular gameplay skills
└── Agents/                    # Delegated autonomous subagent profiles
```

---

## Prompt Composition Order

Every model instruction turn layers prompts in the following sequence:

1. **`Prompts/Briefing.md` (Preamble):** Establishes the agent's identity, RimWorld operational role, the observe-act-report loop, and the tool invocation protocol.
2. **`AGENTS.md` (Player Instructions):** Empty by default. Add your personal colony guidelines, roleplay rules, aesthetic preferences, or banned actions here. Loaded immediately after the preamble.
3. **`Prompts/Conduct.md` (Conduct Rules):** Non-negotiable behavioral boundaries, emphasizing minimal necessary disruption, clear explanation of irreversible actions, clarifying ambiguity, and prioritizing colonist survival.
4. **Tool & Skill Index:** Injects the active tool descriptors, dual-scope memory index, and the concise skill catalog (names and descriptions only). Full skill bodies are loaded on demand via `read_skill`.

---

## Authoring Skills (`Skills/<skill>/SKILL.md`)

Skills provide specialized domain knowledge and strategic heuristics. To conserve model context, turn instructions only contain a skill index; the agent retrieves the complete skill guide on demand using `read_skill`.

Each skill must be placed in its own folder containing a `SKILL.md` file:

```markdown
---
name: my-skill
description: A concise sentence explaining exactly when this skill applies.
---

# My Skill Title

Detailed instructions and heuristics for the agent to follow when this skill is relevant.
Keep guidance concise and actionable.
```

### Constraints & Default Skills
- **Length Limit:** Individual skill texts are truncated after 3,000 characters; combined active skills across a session are capped at 8,000 characters.
- **Folder Names:** If frontmatter is omitted, the containing folder name is used as the skill name. Folders without a `SKILL.md` are ignored.
- **14 Built-in Skills:**
  - **8 Heuristic Gameplay Skills:** `raid-defense`, `defense-building`, `food-security`, `mood-management`, `work-assignment`, `medical-triage`, `seasonal-prep`, `colony-progression`.
  - **6 Endgame Questlines:** `endgame-ship`, `endgame-journey`, `endgame-archonexus`, `endgame-royal`, `endgame-void`, `endgame-mechhive`.

---

## Authoring Subagents (`Agents/<agent>/AGENT.md`)

Subagents allow the primary agent to delegate self-contained tasks using `delegate_task`. Each subagent executes in a completely clean context with its own system prompt, isolated tool subset, and designated model.

Subagents are defined in `AgentPack/Agents/<name>/AGENT.md`:

```markdown
---
name: scout
description: Conducts comprehensive, read-only reconnaissance across the entire colony.
categories: Observe
read_only: true
reasoning: Low
---

You are a colony scout. Inspect maps, colonists, and threats, then return a structured situation report.
```

### Frontmatter Schema

| Property | Type | Description |
| --- | --- | --- |
| `name` | string | Identifier used by `delegate_task` |
| `description` | string | Guidance indicating to the main agent when this subagent should be chosen |
| `categories` | string | Comma-separated allowed tool categories (e.g. `Observe, Work, Build`) |
| `read_only` | boolean | Set to `true` to restrict to query tools and enable parallel execution |
| `reasoning` | enum | Optional reasoning effort override (`Auto`, `Off`, `Low`, `Medium`, `High`) |

### Operational Rules
- **Category Filter:** Subagents can only access tool categories that are also permitted in the main mod settings.
- **Safety Policy:** All state-altering commands issued by subagents adhere strictly to the active execution mode and confirmation gate.
- **Parallelism:** Read-only subagents (`read_only: true`) execute multiple task briefs in parallel; state-altering subagents execute sequentially.
- **Delegation Limit:** Subagents cannot delegate tasks further.
- **Default Agent:** Includes `scout`, a read-only reconnaissance specialist covering all colony observation tools.
