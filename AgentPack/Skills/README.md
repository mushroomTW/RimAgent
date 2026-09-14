# RimAgent 技能包

這裡的子資料夾各是一個技能包。Agent 每次指令都會把每個
`<技能>/SKILL.md` 讀進系統提示詞。

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
