---
name: medical-triage
description: Injured, downed, bleeding or sick colonists, plague or infection, medicine levels, surgery: orders patients by urgency and gets each one treated.
---

# Medical triage

## 1. Triage (get_health, list_colonists)
Put every patient in a tier:
1. Bleeding fast, or bleeding while downed: minutes matter.
2. Downed in the open, in the cold or near hostiles: rescue before tending.
3. A disease racing immunity (infection, plague, flu): tending once a day until immune.
4. Stable wounds, scars, missing parts: can wait.
Done when every patient has a tier.

## 2. Treat tier by tier
- Rescue: order_job rescue carries a downed colonist to a bed; a clean, roofed room is best.
- Tend: order_job tend with the best available doctor, fastest bleeder first. Self-tending is slow and weak, so a wounded doctor gets tended by someone else when anyone can.
- A doctor who is also fighting comes back to doctoring as soon as the fight allows.
- Sick colonists stay in bed with Patient work on, and are tended once per day.
Done when every tier 1-2 patient has been rescued and tended, and every tier 3 patient has a doctor lined up.

## 3. Medicine level (set_medical_care)
| Case | Level |
| --- | --- |
| Scratches, routine tending | Herbal |
| Heavy bleeding, infection, plague, anything racing immunity | Industrial or better: tend quality decides who lives |
| Life-threatening disease, hard surgery | Glitterworld, which is rare, so keep it for these |
| Prisoners and guests | Herbal or none, unless the player values them |
When severity outpaces immunity, raise the level and switch to the best doctor. With no medicine left, check list_traders for some.

## 4. Surgery (schedule_surgery)
A failed operation can injure or kill, and the odds drop with the doctor's Medicine skill. Schedule it when the player asks or when it is clearly lifesaving; for bionics and prosthetics, suggest and let the player decide. Check get_health for the body part and missing materials first.
