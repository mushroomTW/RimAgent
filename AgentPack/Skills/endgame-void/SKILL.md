---
name: endgame-void
description: Void endings (Anomaly DLC): raise the monolith, study entities, enter the void, then embrace it or disrupt the link. Read when a void monolith exists on the map or a goal mentions the void or Anomaly.
---

# Void endings

## 1. Monolith levels
find_things for the monolith, then list_gizmos on it: the status text and the disabled reason always say what the next level needs. Activating asks for a colonist as targetId. Each level brings stronger entities, so prepare before pressing it.
- The first two activations only need a colonist.
- Later levels need entities discovered: first 7 basic, then 12 advanced.
- Void awakened: darkness falls and void structures appear. Activate each (find_things, list_gizmos), defend against the metalhorror defenders.
- Gleaming: a colonist interacts with the monolith to enter the void.
The monolith may also wake on its own timer, so do not fall behind on defenses.

## 2. Discovering entities
- Research the Anomaly tree (get_research_status); build containment and holding platforms early.
- Capture downed entities: list_right_click_menu on one for a colonist. If the option asks for a platform, the reply lists the map target; answer it with choose_option and the platform's id.
- Study them: list_gizmos on the platform for study toggles. Keep bioferrite and power running so nothing escapes.
- Kill what cannot be held.

## 3. Defenses
Entities ignore normal raid patterns and can arise inside the base. Keep colonists armed, keep doors and walls sound (defense-building), and keep a doctor ready (medical-triage). Do not leave anyone alone at night after awakening.

## 4. The void
- Take the strongest colonist. Inside, interact with the void node (list_gizmos or list_right_click_menu), then read list_pending_choices: the final dialog offers both endings.
- If the player named an ending, take it; otherwise choose disrupt.
- Both show the credits.

Done when the void node dialog is open; otherwise name what the monolith status says it needs next.
