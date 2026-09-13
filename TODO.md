# RimAgent 待辦

第一版（玩家下指令、Agent 執行）已完成並在遊戲內驗證。以下是刻意留到之後的事，依優先順序排列。

## 第二階段工具

- [x] 貿易查詢與下單（軌道商人／來訪商隊庫存、一次結清買賣；遊戲內驗收待玩家實測，見 `.scratch/trade/issues/04-ingame-verify-i18n.md`）
- [x] 組商隊（指名殖民者往羅盤方向離圖；遊戲內驗收待玩家實測，見 `.scratch/caravan/issues/01-form-caravan.md`）
- [ ] 醫療手術與健康查詢（醫療等級、健康查詢、手術排程已做，剩遊戲內驗收，見 `.scratch/medical/issues/`）
- [x] 囚犯（查詢、互動模式、立即釋放；招募走獄卒原生機制；遊戲內驗收待玩家實測，見 `.scratch/prisoner/issues/01-prisoner-manage.md`）
- [x] 動物（查詢、馴養、訓練、區域指派；遊戲內驗收待玩家實測，見 `.scratch/animal/issues/01-animal-manage.md`）
- [x] 區域與儲存設定（查詢、新增儲藏／種植區、刪除、儲藏篩選；遊戲內驗收待玩家實測，見 `.scratch/zone/issues/01-zone-manage.md`）
- [ ] 生產帳單（工作台加減帳單、數量、材料）
- [ ] 研究切換（`get_research_status` 目前唯讀）
- [ ] 多地圖（目前只操作 `Find.CurrentMap`）

## 互動與安全

- [ ] 「需確認」模式：Work / Build / Combat 類工具執行前先列出將做的事，玩家按確認才執行
- [ ] 復原：一鍵回到上一句指令改動前的值（至少工作優先級與時間表）
- [ ] 對話歷史寫入存檔（目前新局／讀檔歸零；歷史現由 Agent Framework 的 session 持有，未來可評估 `SerializeSessionAsync` 持久化，而非自持歷史）
- [ ] `maxSteps` 設定即時生效（目前 session 建立時綁定，對之後載入的遊戲才生效；AF 化後重建範圍是 `inner + agent + AF session`）
- [ ] 戰鬥延遲提醒一句指令只顯示一次（目前每個戰鬥工具各顯示一次）
- [ ] 模型回覆中的 Markdown（`**粗體**` 等）在 UI 去除或渲染

## 自主模式（架構已預留）

- [ ] 排程器：定期或事件觸發（襲擊、心情警戒、資源短缺）自動起一次任務
- [ ] 目標管理：玩家設長期目標，Agent 拆解並持續推進
- [ ] 自主模式下的預算與步數總量上限

## 本地化與發佈

- [x] 英文、繁體中文、簡體中文 Keyed 與 MainButtonDef DefInjected
- [ ] Steam 工作坊描述與 Preview 圖
- [ ] `About.xml` 無法表達框架最低版本：目前靠能力探測擋，舊版框架會 `MissingMethodException`——等框架提供版本 API 後改用

## 已知限制（不打算改）

- 戰鬥指令有數秒延遲，只適合佈署與撤退，不做即時操作
- 不做串流顯示（工具迴圈本質上不適合串流）
- 不用 Harmony（沒有要攔截遊戲行為）
- Agent 執行期間不自動暫停遊戲
