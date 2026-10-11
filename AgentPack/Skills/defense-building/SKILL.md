---
name: defense-building
description: Building defenses before raids come: choke points, kill boxes, sandbags, turrets, traps and mortars, scaled to colony wealth. Read when planning defenses or before a long hold such as a ship reactor countdown.
---

# Defense building

Raids grow with colony wealth. Build defenses one step ahead of wealth, not after a raid breaks in.

## 1. Read the ground
- view_map_grid around the base (50x50 at a time) to see walls (#), doors (+), cover (c), turrets (t), rock (^) and open approaches. The roof layer shows mountain roof (M): no drop pods land under it.
- Note where raiders enter: open map edges, the gaps between rock and walls.
Done when you can name each approach and the one you want raiders to use.

## 2. Funnel
- Close every approach but one with walls; leave raiders one path that is shorter than breaking a wall.
- At the end of that path, a kill box: an open lane 3-5 cells wide, about 10-15 cells long, ending at a wall line with one door.
- Behind the end wall: a row of sandbags (c) with a 1-cell gap from the wall, so fighters stand behind cover and shoot down the lane.
- Raiders on the lane must have no cover: remove trees, rocks and walls along it (designate_area cut_plants / mine / deconstruct).

## 3. Firepower by stage (check research with get_research_status)
| Stage | Add |
| --- | --- |
| Early | Sandbags (no research), spike traps (TrapSpike) on the lane, one door to fall back through |
| GunTurrets | Turret_MiniTurret (80 W) behind sandbags covering the lane; keep the power line protected |
| IEDs | TrapIED_HighExplosive at the lane's narrowest point; colonists must not walk there |
| HeavyTurrets / SniperTurret | Turret_Autocannon or Turret_Sniper (150 W each) for mechanoids and long approaches |
| Mortars | Turret_Mortar against sieges and camps; needs a colonist to man it (list_right_click_menu on the mortar) and reinforced barrels as fuel; aim with use_gizmo and a target cell |

Turrets explode when destroyed: keep them 2+ cells from each other, the fighters and anything flammable.

## 4. Before a long hold (ship reactor, royal guest, quest defense)
- Repair and refuel everything, stock medicine and meals near the fighting position.
- Spare steel and components for rebuilding turrets between waves.
- Hospital beds close behind the line; one doctor never fights.

## Pick one action now
Close the widest open approach first, then cover for the fighters, then traps, then turrets. Confirm materials with get_resources before placing blueprints.
Done when one funnel step is placed and you can say what the next one is.
