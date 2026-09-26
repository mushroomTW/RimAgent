# RimAgent prompt layout

Built-in prompts live in `AgentPack/Prompts/`, player instructions in `AgentPack/AGENTS.md`, and optional skills in `AgentPack/Skills/`. Reload the save or start a new game after editing; no DLL rebuild is needed.

- `Prompts/Briefing.md` — The preamble: the agent's RimWorld role, the observe-act-report loop, and the tool result protocol. This is the first layer of each request.
- `AGENTS.md` — Your own instructions. It ships empty. Put gameplay preferences, restrictions, or tone here; the agent reads it after the preamble and before the conduct rules.
- `Prompts/Conduct.md` — Persistent conduct rules, including minimal changes, explaining irreversible actions, clarifying ambiguity, and prioritizing colony survival.
- `Prompts/AskMode.md` — The note added in Ask mode: use query tools only and give advice instead of taking actions.
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

- A folder without `SKILL.md` is not a skill. Unrecognized files are ignored.
- Without frontmatter, the folder name becomes the skill name.
- The shipped `Skills/` folder has seven heuristic gameplay skills: `raid-defense`, `food-security`, `mood-management`, `work-assignment`, `medical-triage`, `seasonal-prep` and `colony-progression`. Edit or delete them freely, or add your own alongside.
- Missing or unreadable prompt files are treated as empty. The game continues: a missing preamble uses a one-line fallback, and a missing classifier prompt makes Auto mode ask before every action.
