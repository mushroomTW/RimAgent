---
name: endgame-journey
description: Ending by reaching a ready-made ship far away (the journey offer quest, Core game). Read when list_quests shows a ship far away or a goal mentions the journey.
---

# Journey offer ending

The offer arrives as a quest about 20 days into the game and is accepted on its own. Unlike endgame-ship, the ship already exists and nothing has to be researched.

1. list_quests for the details; list_world_objects for the ship's tile.
2. Prepare for a long trip and a long hold: food, medicine, weapons and armor. Colonists left behind do not count, so bring everyone who should leave.
3. form_caravan, then send_caravan to the tile (get_world_tile numbers the orders); enter the map when the caravan arrives.
4. Build defenses around the ship before starting it (defense-building). The caravan's packed materials are all there is, so bring steel and components.
5. list_gizmos on the ship's reactor shows its status text and the startup button; use_gizmo starts it and choose_option answers the warning.
6. Raids attack until the startup ends. If the reactor is destroyed the ending is lost: keep it repaired and alive.
7. When the reactor is ready, board caskets (order_job enter_casket) and use the launch button on the computer core. Colonists not in caskets stay behind.

Done when the launch button is enabled; otherwise name the next missing step.
