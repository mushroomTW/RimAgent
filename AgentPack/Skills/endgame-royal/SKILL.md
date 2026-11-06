---
name: endgame-royal
description: Royal ascent ending (Royalty DLC): earn a Count title from the Empire, host a stellarch and leave on the imperial shuttle. Read when a quest mentions the stellarch or a goal mentions the Empire or royal ascent.
---

# Royal ascent ending

## 1. Title first
The offer comes from about day 35 and repeats about every 22 days, but needs a colonist titled Count or higher.
- list_colonists shows each colonist's title and honor. get_colony_overview shows goodwill with the Empire: never let it turn hostile.
- Titles cost honor in order: Freeholder 1, Yeoman 6, Acolyte 6, Knight 8, Praetor 10, Baron 14, Count 20. That is 65 honor in total.
- Honor comes from Empire quests that reward honor (list_quests, accept_quest), usually by hosting or helping. Check the reward before accepting.
- A bestowing ceremony grants the next title when an imperial bestower visits: it starts from a quest or letter, and list_pending_choices shows the ceremony start. Answer it with choose_option.
- A title brings expectations (throne room, bedroom, apparel, permitted food). Meet them; unmet ones hurt mood.

## 2. Accepting the quest
list_quests names the requirements; usually the Count colonist plus good bedrooms for the guests. Build the required bedrooms before accepting: a private room for each guest, with the quality the quest asks for; the quest text and the alerts say what is missing.

## 3. Hosting for about 12 days
A stellarch and four guards live with you. The quest fails if:
- the stellarch dies, leaves or is arrested, or is operated on without permission;
- the Empire turns hostile;
- the stellarch's mood collapses. Feed them, keep their rooms clean and warm, and keep raids well away from them.
Build defenses before they arrive (defense-building).

## 4. Leaving
The shuttle lands and waits about 3 days. Use list_right_click_menu on the shuttle for each colonist who leaves, including the titled one. Board in time: the ending triggers when the shuttle takes off with colonists aboard, and the shuttle's list_gizmos status shows what it still waits for.

Done when the shuttle is ready; otherwise name the next missing step.
