---
name: endgame-archonexus
description: Archonexus ending (Ideology DLC): grow wealth, move the colony across three archonexus cycles, then invoke the core. Read when a quest mentions the archonexus or a goal mentions it.
---

# Archonexus ending

## 1. Getting the quest
The storyteller offers it once colony wealth reaches about 150,000 (get_colony_overview shows wealth). There are three cycles; each is one quest and each new colony starts over with almost nothing.

## 2. Accepting a cycle (accept_quest)
list_quests names the requirements. They are:
- Colony wealth about 350,000 for every cycle.
- First cycle: an ally relation with the named faction. get_colony_overview lists goodwill; raise it with gifts and quests.
- Later cycles may ask to study an archotech structure on the map first.
Wealth counts everything on the map: raise it by making art and advanced goods, not by hoarding raw materials.

## 3. Moving (each step waits in list_pending_choices)
Accepting starts a chain; answer each with choose_option:
1. New colony selection: a numbered list of colonists, animals, relics and item stacks. Give the numbers as input. Pick your best workers across skills (a doctor, a builder, a grower, a shooter), plus weapons, medicine and components. A warning box may follow: read it and answer it.
2. A short cinematic plays; wait_for_time past it.
3. Tile picker: find a tile with get_world_tile and list_world_objects (temperate, growing season, near no enemies). Give the tile id as input.
4. Ideoligion screen, if shown: keep the current ideoligion.

Everything left behind is lost. After arriving, rebuild from colony-progression. Save first with save_game when allowed.

## 4. Final cycle
- The last cycle sends you to the archonexus by caravan: send_caravan with armed colonists, and expect mechanoid defenders.
- Clear the defenders, then list_right_click_menu for a colonist on the archonexus core: the invoke option starts the final job (use_right_click_option).
- Defend until the countdown ends and the credits roll.

Done when the invoke option is available; otherwise name the next missing requirement.
