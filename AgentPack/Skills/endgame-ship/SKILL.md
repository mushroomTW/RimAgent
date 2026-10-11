---
name: endgame-ship
description: Ending by building and launching the starship: research chain, parts, materials, hull layout, the 14-day reactor startup under raids, and launch. Read when the player wants to leave the planet or a goal mentions the ship.
---

# Ship launch ending

## 1. Research (set_research, get_research_status)
Electricity → MicroelectronicsBasics → MultiAnalyzer (hi-tech research bench) → Fabrication (needs a multi-analyzer next to the bench) → AdvancedFabrication → ShipBasics → ShipCryptosleep, ShipComputerCore, ShipSensorCluster, ShipReactor, ShipEngine.
AdvancedFabrication lets a fabrication bench make advanced components (ComponentSpacer); start that bill early, they are the bottleneck.

## 2. Parts the launch checks
| Part (defName) | Size | Needed | Cost each |
| --- | --- | --- | --- |
| Ship_Beam | 2x6 | 1+ (links parts) | 200 steel, 40 plasteel, 3 components, 1 adv. component |
| Ship_CryptosleepCasket | 1x2 | 1 per colonist leaving | 120 steel, 14 uranium, 3 components, 3 adv. |
| Ship_ComputerCore | 2x2 | 1 | 150 steel, 70 gold, 4 adv., 1 AIPersonaCore |
| Ship_SensorCluster | 2x2 | 1 | 140 steel, 4 gold, 6 components, 6 adv. |
| Ship_Reactor | 6x7 | 1 | 350 steel, 280 plasteel, 70 uranium, 8 adv. |
| Ship_Engine | 3x4 | 3 | 260 steel, 140 plasteel, 70 uranium, 6 adv. |
Without beams and with one casket: about 1540 steel, 700 plasteel, 294 uranium, 74 gold, 9 components, 39 advanced components, 1 AI persona core.
AI persona core: buy from an exotic orbital trader (list_traders), take it as a quest reward (list_quests shows rewards), or loot it from ancient sites.

## 3. Layout
- Every part must touch the ship cardinally (directly or through beams); the launch only counts parts connected to the one you launch from.
- No roof over any part. Each casket's head must sit on a beam.
- Pick an open, flat, unroofed area about 20x20 inside the defenses; check it with view_map_grid (map and roof layers).
- place_blueprint one part at a time with single shape; check refused cells and their reasons.

## 4. Startup: 14 days of raids
- Before startup: finish every part except what needs the reactor running, build defenses for a long hold (defense-building), stock food and medicine.
- list_gizmos on the reactor or the computer core → use_gizmo on the startup button → answer the warning with choose_option.
- Raids target the ship for 14 days. Keep the reactor and engines alive; repair between waves.

## 5. Launch
- Each colonist who leaves goes into a casket: order_job enter_casket. At least one casket must be occupied.
- Launch is a computer core button; a disabled one shows only the first problem. Every part's launch report button lists all of them in a dialog: fix each.
- use_gizmo on the launch button and confirm. Colonists not in caskets stay behind.

Done when the launch button is enabled; otherwise name the next missing step.
