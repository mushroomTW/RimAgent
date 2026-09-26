---
name: seasonal-prep
description: Coming winter or summer, cold snap, heat wave, dangerous temperatures, toxic fallout or volcanic winter: checks food, heat, fuel and clothing against the season and closes the gaps.
---

# Seasonal preparation

Read the date, season and outdoor temperature in get_colony_overview, then work the branch that matches. Every branch checks the same four lines: food, climate control, fuel and power, clothing.

## Winter (crops stop below 0 C)
- Food: the runway must reach the next growing season (food-security). Harvest ripe crops before the frost kills them.
- Freezer: with sub-zero weather, an unheated roofed room is a free freezer.
- Heat: every bedroom, the dining room and the hospital need a heat source; inspect_area shows each room's temperature and whether it counts as indoors.
- Fuel and power: campfires and wood-fired generators burn wood, electric heaters draw power. Cut trees (designate_area cut_plants) before snow slows the haulers.
- Clothing: parkas and tuques for anyone working outdoors, via add_bill at a tailoring bench when materials exist.
- Animals: grazers go hungry under snow; stock hay or kibble, or plan for fewer animals.

## Summer
- Coolers, or at least roofed and shaded rooms, for bedrooms and the freezer.
- The freezer warms first in a heat wave: check its power and add a second cooler when it drifts above freezing.
- When heatstroke appears, move the hottest hours of work indoors.

## Cold snap or heat wave
Urgent, because exposure hurts within hours: find who is exposed (list_colonists, get_colonist_wellbeing), move sleeping and work spots indoors, add a heater or cooler to the most-used room, hand out weather-fit clothing, and harvest ripe crops now.

## Toxic fallout, volcanic winter and other long events
Indoors as much as possible, outdoor work only for essentials. Expect poor or no outdoor harvests while it lasts and lean on stored food, hunting and trade.

Done when food, climate control, fuel and power and clothing are each either confirmed ready or have a queued fix, and the biggest remaining gap is named.
