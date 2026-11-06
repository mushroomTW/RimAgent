# RimAgent prompt layout

Built-in prompts live in `AgentPack/Prompts/`, player instructions in `AgentPack/AGENTS.md`, optional skills in `AgentPack/Skills/`, and optional subagents in `AgentPack/Agents/`. Reload the save or start a new game after editing; no DLL rebuild is needed.

- `Prompts/Briefing.md` — The preamble: the agent's RimWorld role, the observe-act-report loop, and the tool result protocol. This is the first layer of each request.
- `AGENTS.md` — Your own instructions. It ships empty. Put gameplay preferences, restrictions, or tone here; the agent reads it after the preamble and before the conduct rules.
- `Prompts/Conduct.md` — Persistent conduct rules, including minimal changes, explaining irreversible actions, clarifying ambiguity, and prioritizing colony survival.
- `Prompts/RiskClassifier.md` — The system prompt for risk classification in Auto mode. It defines the two-axis decision and JSON response contract; keep that format intact.
- `Prompts/Autonomous.en.md` — Autonomous task instructions in English. Each trigger has a `##` section: Raid, RaidEnded, EmergencyMedical, Fire, MentalBreak, FoodShortage, Choice, or Interval. Persistent goals run through a separate flow.
- `Skills/<skill>/SKILL.md` — An optional skill pack. Each request carries only a skill index (names and descriptions); the agent loads a full skill text on demand with `read_skill`. Use this format:

```markdown
---
name: my-skill
description: A sentence explaining when to use this skill
---

# My skill

Instructions the agent follows when this skill applies.
Keep it concise: a skill is truncated after 3,000 characters,
and all skills together have an 8,000-character limit.
```

- `Agents/<agent>/AGENT.md` — An optional subagent. The agent hands self-contained tasks to it with `delegate_task`; each task runs in a fresh context with the subagent's own tools and model, and only its final report comes back. Use this format:

```markdown
---
name: my-agent
description: A sentence telling the main agent when to delegate to it
categories: Observe, Build, Zone
read_only: false
reasoning: Low
---

The subagent's own instructions. Truncated after 4,000 characters.
```

  `categories` lists tool categories (Observe, Work, Build, Combat, Trade, Medical, Prisoner, Animal, Zone, Production, Time, Mcp, Web, Memory); the subagent still only gets the ones allowed in the settings, and its commands follow the current mode. `read_only: true` keeps only query tools and lets several tasks run in parallel. `reasoning` (Auto, Off, Low, Medium, High) overrides the subagent default in the settings; the model is picked per subagent in the settings. Subagents cannot delegate further. The shipped `Agents/` folder has one read-only subagent, `scout`.
- A folder without `SKILL.md` is not a skill. Unrecognized files are ignored.
- Without frontmatter, the folder name becomes the skill name.
- The shipped `Skills/` folder has fourteen skills: eight heuristic gameplay skills (`raid-defense`, `defense-building`, `food-security`, `mood-management`, `work-assignment`, `medical-triage`, `seasonal-prep` and `colony-progression`) and six ending routes (`endgame-ship`, `endgame-journey`, `endgame-archonexus`, `endgame-royal`, `endgame-void` and `endgame-mechhive`). Edit or delete them freely, or add your own alongside.
- Missing or unreadable prompt files are treated as empty. The game continues: a missing preamble uses a one-line fallback, and a missing classifier prompt makes Auto mode ask before every action.
