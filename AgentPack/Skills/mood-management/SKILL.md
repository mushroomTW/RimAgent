---
name: mood-management
description: Low mood, impending mental break, "why is X unhappy": finds the root cause among mood thoughts and removes it.
---

# Mood management

## 1. Triage by break risk
- The default mental break threshold is 35% mood (minor breaks); below 20% major breaks and below 5% extreme breaks (berserk, fire starting, running wild) become possible. Traits move these lines, so the break warning in get_colony_overview is the authority.
- Order: anyone with a break warning first, then anyone within about 5 points of 35%, then the rest.
- Imminent break: buy time with the fastest relief for that colonist: undraft, move them away from danger, set_schedule Joy or Sleep for the next hours, make a meal available. Then continue to step 2.

## 2. Find the root cause (get_colonist_wellbeing)
- Sort the active thoughts by size. The root cause is the largest negative thought that can be changed; grief and other timed thoughts are offset with positives, and they fade on their own.
- Low food, rest or recreation needs each add their own thought and are the cheapest fixes.
- Many colonists low at once points to a shared root cause (bad food, no recreation, a cramped barracks, constant raids); fix that once instead of per colonist.
Done when each colonist in step 1's list has a named root cause.

## 3. Remove it
| Root cause | Fix |
| --- | --- |
| Hungry, ate raw food, ate without a table | Meal bill on a stove (add_bill); a table and chairs in the dining room |
| Tired, slept on the ground or outside | A bed in a roofed, enclosed room; Sleep hours in set_schedule |
| Recreation low | 1-2 Joy hours a day in set_schedule; a recreation building (horseshoes pin, chess table) |
| Too cold or hot | Heater or cooler in the room; weather-fit clothing |
| Shared, ugly or noisy bedroom | Own bedroom with a floor and a light, away from workbenches |
| Pain, sickness | Tend first (medical-triage) |
| Saw corpses, ate human meat | Haul corpses out of sight (a dump zone); forbid the tainted food |
| Tattered or dead man's apparel | Replace it with fresh clothing |
| Overworked, no free time | Protect Joy hours; keep work priorities as they are while mood is low |

Done when every colonist from step 1 has a fix queued for their root cause, or a stated reason it cannot be fixed now.
