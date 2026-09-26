---
name: food-security
description: Food shortage, food questions, farm or hunting planning: measures the colony's food runway in days and closes the gap.
---

# Food security

Runway is the measure everything here serves: days of food = human-edible nutrition (get_resources) / (colonists x 1.6). An adult eats about 1.6 nutrition a day. Count animals that eat hay or kibble on top; grazers need feed only when the grass is gone (winter, desert).

| Runway | State |
| --- | --- |
| Under 5 days | Emergency: section 1 now |
| 5-15 days | Thin: section 2 |
| 15+ days | Comfortable; before winter, stretch it to the next growing season (seasonal-prep) |

## 1. Emergency: stack the fast sources at once
1. Harvest everything ripe (designate_area harvest).
2. Hunt safe game (designate_area hunt): deer, ibex, gazelle, hares, rabbits, squirrels. These fight back, so leave them alone: predators (bears, big cats, wolves, wargs), thrumbos, elephants, rhinos, megasloths, emus, ostriches, cassowaries. Muffalo and other herds can retaliate together; boomalopes explode on death, so hunt them only far from buildings.
3. Cook: a meal bill on a stove (list_bills, add_bill "simple meal"). A simple meal turns 0.5 raw nutrition into 0.9 and spares the raw-food mood penalty.
4. Buy survival meals, pemmican or raw food from list_traders if silver allows.
Done when all four are queued or ruled out with a reason, and you can state the runway before and after.

## 2. Farming
- Sow when the outdoor temperature (get_colony_overview) is in the 6-42 C growing band; crops stop below 0 C.
- Crop by soil and season: rice for speed on good soil (early game, late season); potatoes on poor or gravel soil; corn for large yields in a long stable season.
- Size: about 16 tiles of rice per colonist just keeps pace with eating, as simple meals on fertile soil; double it to build a winter reserve, and add more for animals that eat from the fields.
- Place fields on fertile soil near the base (inspect_area shows fertility), then confirm a colonist with Plants skill has Growing enabled (list_work_priorities).
Done when a growing zone of the right crop and size exists and a grower is assigned.

## 3. Storage
- A roofed, refrigerated room (a freezer) keeps a stockpile alive; without one, keep reserves small and cook often.
- Rotting meals mean over-cooking: lower the bill count.
