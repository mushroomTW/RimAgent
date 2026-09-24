# RimAgent 提示詞版面

這個資料夾是出貨給模型的提示詞唯一真相來源，改這裡不必重編 dll，下一句指令生效。

- `Briefing.md` — 前情提要：你是 RimWorld 助手、observe-act-report 迴圈、工具結果協議。每次請求的第一層。
- `AGENTS.md` — 你的自留地：出貨是空的。想交代 agent 的事（玩法偏好、禁區、語氣）寫這裡，讀在前情提要之後、準則之前。
- `Conduct.md` — 常駐行為準則（最小改動、不可逆先講、模糊先問、殖民地存活優先）。
- `AskMode.md` — 詢問模式才附加的註記（查詢工具限定，操作改給建議）。
- `RiskClassifier.md` — 自動模式風險分類器的系統提示詞（兩軸判定＋JSON 回覆契約，別改格式）。
- `Autonomous.en.md` — 自主任務文案（只留英文），一個觸發一個 `## 章節`（Raid、RaidEnded、EmergencyMedical、Fire、MentalBreak、FoodShortage、Interval）。持續 goal 由獨立流程執行，不再附在這些章節後面。
- `Skills/<技能>/SKILL.md` — 自選技能包，格式如下。每次請求全部讀進系統提示詞。

```markdown
---
name: my-skill
description: 一句話說明何時用這個技能
---

# My skill

技能生效時 agent 遵守的作法。
寫短一點：單包超過 3000 字會截斷，
全部技能合計上限 8000 字。
```

- 資料夾裡沒有 `SKILL.md` 就等於沒有技能；不認得的檔案一律忽略。
- 沒有 frontmatter 時以資料夾名為技能名。
- 出貨預設是空的，把自備的技能包加在這裡。
- 缺檔、壞檔一律視為空，遊戲照跑：前情提要缺檔時以一行保底代替；分類器缺提示詞時自動模式退化成全部先問。
