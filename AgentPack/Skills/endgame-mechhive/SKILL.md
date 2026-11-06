---
name: endgame-mechhive
description: Mechhive ending (Odyssey DLC): build and fly a gravship, follow the gravcore quests to the orbital mechhive, disable its stabilizers and finish the Cerebrex core. Read when a goal mentions the gravship, mechhive or Cerebrex.
---

# Mechhive ending

## 1. Build a gravship
- A grav engine comes from a quest (list_quests). A colonist inspects it first (its button in list_gizmos, targetId = the colonist); that unlocks the research and substructure. To move it, use its install or reinstall button with targetX and targetZ. Research BasicGravtech, then StandardGravtech and AdvancedGravtech (get_research_status).
- Parts stand on Substructure floor (place_blueprint, shape fill, close to the engine): PilotConsole, SmallThruster, ChemfuelTank, GravFieldExtender. Later LargeThruster, LargeChemfuelTank, GravcorePowerCell and SignalJammer. get_building_info gives each size and rule.
- Fill the tanks; list_gizmos on the console and engine shows status and what is missing.
- Everything on the substructure flies along: put beds, stores, power, food and defenses on it.

## 2. Fly (each step waits in list_pending_choices)
1. use_gizmo on the pilot console starts the launch ritual: choose_option begins it. A pilot and copilot are chosen by default.
2. After the ritual a confirmation box may appear, then a tile picker. Give the tile as input; orbital tiles read Orbit:123. The reply gives the game's reason when out of range or low on fuel.
3. Landing: the choice dialog-landing shows the marker and map size. Option 2 moves the marker to x,z given as input; view_map_grid helps pick open ground. Option 1 lands. The game stays paused until you confirm.

## 3. Quests to the mechhive
Gravcore quests lead on one at a time; the mechhive comes after several. It is on the Orbit layer, a few tiles from your colony. list_world_objects shows its tile as Orbit:N. It may require a SignalJammer on the ship (AdvancedGravtech): the tile picker's refusal says so. Until it falls, mechhive raids keep arriving.

## 4. Assault
- Land with the whole armed crew and turrets on the ship (defense-building, raid-defense).
- Find the three stabilizers (find_things: CerebrexStabilizer). Each needs a colonist interacting with it: list_gizmos or list_right_click_menu. Each one disabled lowers the defenses.
- Finally interact with the Cerebrex core. The dialog offers destroying or scavenging it. Take what the player asked for; otherwise destroy it. Both show the credits.

Done when the core dialog is open; otherwise name the next missing step.
