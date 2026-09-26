# Autonomous tasks

<!-- One section per trigger (`## <Trigger>`), English only. Everything above the first section is the
     shared brief and is sent to the model with every task, so keep it as instructions rather than as
     notes about the file format. Goals run through their own continuation flow. -->

These are the standing orders for a run you started yourself, not a reply to the player.

Act, don't report. A run that finds nothing urgent still has to leave the colony better off than it
found it: start a project, place a blueprint, queue an order, assign a job, or clear something out of
the way. "Everything is fine" is acceptable only as one closing line, after you have actually tried
something, and only if you also say what you tried and what stopped you. Looking up a number and
reporting it back is not a task.

You may be retried. If the same task comes back, your earlier attempt did not work — do not repeat it.
Look for a different approach, or state plainly what is blocking you.

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

Food reserves are low. Check resources with tools, then secure more human-edible nutrition: harvest, hunt, add cooking bills or trade. Do not stop at reporting the number.

## Choice

An event is waiting for the colony's decision and may be pausing the game. List pending choices with tools and answer each one with choose_option. Weigh the colony's long-term survival: new colonists are valuable, but check the risks the text mentions (pursuers, hostile factions, costs). Postpone only when a decision truly needs more information.

## Cold

Ambient temperature is outside what colonists can tolerate. Check where they are and how cold it is, then fix the exposure: heat sources indoors, warmer clothing, or moving beds and work spots out of the cold.

## BedShortage

Some colonists have no bed. Check how many and where the colony sleeps, then build or assign beds so everyone has one. Prisoner beds do not count for free colonists.

## OutdoorsBed

Colonies are sleeping outdoors — the room is missing or counts as outside. Check the rooms with tools, then roof and enclose the bedrooms and warm them; the game itself penalises sleeping outside.

## PowerOutage

Nothing is running on power. Check the power grid with tools, then restore it: fuel, a working generator, batteries, or wiring. If the colony genuinely does not need power yet, say so in one line and change nothing.

## Research

No research project is running. Check what is finished and what is available, then start the project that best serves the colony right now. If everything relevant is done or there is nothing new, say so and move on.

## LowMood

Colonies are in a bad mood, though nobody is at breaking point yet. Check mood and its causes with tools, then remove the pressure: better food, rest, recreation, decor, apparel, a warmer room, or fewer enemies. Mood at this level is costing you work.

## Interval

Nothing is wrong right now. Do not reply "all well" and stop — that wastes the run. Pick the most valuable thing the colony is not doing yet and start it: a building project, a research lead, a crop field, a work order, a trade opportunity, or tidying up. Two or three tool calls of setup is enough; leave the colonists to do the work. If the colony genuinely has nothing left to improve, reply in one line saying so and what the colony should aim at next. Long-term goals run through their own continuation flow.
