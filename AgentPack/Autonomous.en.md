# Autonomous tasks

One section per trigger (`## <Trigger>`). Goals run through their own continuation flow. English only.

## Raid

Hostiles are on the map. Deploy the defence with tools: draft the colonists who can fight, position them safely and attack the threats. Safe positioning beats chasing. Leave them drafted; a follow-up task fires when the raid ends.

## RaidEnded

The raid has ended. Check threats with tools; when the map is clear, undraft everyone still drafted so they return to food, rest and work. Then clean up with order_job: rescue downed colonists, capture downed raiders worth recruiting (a prisoner bed is needed), and allow the raiders' dropped gear with set_forbidden so it gets hauled.

## EmergencyMedical

A colonist is downed or bleeding. Check their health with tools, make sure their medical care level allows treatment, and take the least disruptive action that gets a doctor to them.

## Fire

Fire is burning on the map. Check threats and the burning area with tools, then use the actions you have to keep colonists out of danger and contain it.

## MentalBreak

A colonist is close to a mental break. Check their mood and its causes with tools, then relieve the pressure through schedule, needs, threats or health.

## FoodShortage

Food reserves are low. Check resources with tools, then secure more human-edible nutrition: harvest, hunt, add cooking bills or trade.

## Choice

An event is waiting for the colony's decision and may be pausing the game. List pending choices with tools and answer each one with choose_option. Weigh the colony's long-term survival: new colonists are valuable, but check the risks the text mentions (pursuers, hostile factions, costs). Postpone only when a decision truly needs more information.

## Interval

Routine inspection: check the colony overview, alerts, threats and key resources with tools. Handle anything urgent; when all is well, reply in one sentence. Long-term goals run through their own continuation flow.
