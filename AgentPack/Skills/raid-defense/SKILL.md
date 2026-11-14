---
name: raid-defense
description: Hostiles on the map (raid, siege, sappers, manhunter pack, mechanoids): read before drafting anyone, to size up the threat, pick a stance and position fighters.
---

# Raid defense

Orders land seconds late: win by positioning and let the colonists' own AI do the shooting. Pause with set_time_speed before a batch of orders when the enemy is already close.

## 1. Size up
From list_threats, list_colonists and take_screenshot with a rectangle on the approach (hostiles, cover, turrets):
- Fighters: colonists with a weapon who can do violence and are not downed.
- Threat type: ranged, melee, manhunting animals, mechanoids, or a siege or sapper camp that is not charging yet.
- Approach: where they come from relative to the colony's walls and doors.
Done when you can state: N hostiles of type T coming from D, against M fighters.

## 2. Pick a stance
| Situation | Stance |
| --- | --- |
| Clearly stronger (about 1.5x fighters, better weapons) | Hold a line just outside the base |
| Even or weaker | Hold inside: fight from doorways and behind walls, let them come |
| Manhunting animals | Shelter: everyone indoors with order_move until the madness wears off; fight only at a breached door |
| Siege or sappers still camped | Dig in; shell the camp with a manned mortar (use_gizmo with a target cell) if the colony has one; otherwise offer the player the choice: wait, strike with the best fighters, or reinforce |
| Mechanoids | Behind cover at long range, focus fire one target at a time |

## 3. Position
- Ranged fighters behind cover (walls, sandbags, rock) with a clear line on the approach; melee fighters beside the door or corner the enemy must pass.
- One group, one line.
- Focus fire: order_attack the nearest dangerous ranged enemy, then the next.
- Non-fighters (incapable of violence, very low combat skill) wait indoors; keep one doctor out of the line.
- Rotate out: order_move a bleeding fighter behind the line while they can still walk.
- Hold the ground you defend; raiders who flee are already beaten.
Done when every fighter has a covered position and a target, and every non-fighter is indoors.

## 4. Cleanup
When list_threats is clear: undraft everyone, rescue downed colonists (order_job rescue), tend them (medical-triage), capture raiders worth recruiting (a prisoner bed must exist), then allow dropped gear with set_forbidden.
