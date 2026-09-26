---
name: work-assignment
description: Work priorities or daily schedules, a new colonist joining, a job nobody does: assigns jobs by coverage, skill and passion.
---

# Work assignment

Gotcha: colonists finish every priority-1 job before any priority-2 job, and ties inside one number go to the game's column order. Setting everything to 1 therefore erases the priorities you meant to express.

## 1. Coverage
Coverage means every essential job has a primary and a backup (list_colonists, list_work_priorities; plan only with jobs a colonist can do):
- Firefighting and Patient/Bed rest: 1 for everyone who can.
- Doctor: best Medicine at 1, a second colonist at 2-3.
- Cooking: best Cooking at 1-2; low skill risks food poisoning.
- Hauling and Cleaning: 3-4 on low-skill colonists, so experts stay on their craft.
- Growing, Construction, Mining: the best at each, scaled to the current project.
Done when each essential job has a primary and a backup, or the gap is named to the player.

## 2. Specialise
- Each colonist gets one or two jobs at 1: the ones where they are the colony's best, or have a major passion (**).
- Skilled crafters and researchers stay on their craft; hauling sits at 3-4 for them.
- Passion with low skill is a long-term investment; start it on jobs where early quality matters little (Growing, Construction, Research).

## 3. Surge for the current need
Temporary raises on the best people, and tell the player /undo reverts them:
- Food shortage: Growing, Cooking, Hunting.
- Big build: Construction, with Doctor and Firefighting left as they are.
- After a raid: Doctor and Hauling for a day.

## 4. Schedules (set_schedule)
A baseline day: Sleep about 22-6, one or two Joy hours in the evening, Anything for the rest. Night owls may sleep by day when the player wants it. Joy hours pay for themselves in mood.

Batch all changes into one set_work_priorities call.
