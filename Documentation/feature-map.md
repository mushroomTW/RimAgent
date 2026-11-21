# 功能地圖

從玩家的角度列出 RimAgent 的功能：怎麼操作到它、改動後用什麼證明它還正常。
使用者說「左下角那個」「確認那一步」時，用這張表對上功能名稱；驗證改動時，照「證明」欄跑。

- **單元測試**：`dotnet test Source/RimAgent.Tests`（離線，假世界 `FakeGameWorld`）。
- **遊戲內測試**：`Source/RimAgent.InGameTests`（DevTools）。跑法見 AGENTS.md「遊戲內測試」。
- **截圖存證**：介面改動一律截圖再 Read 檢查，純文字比對看不出版面問題。用 `AgentUiDriver`：`OpenAgentWindow()` → `CaptureProof("名稱")` → 存到 `<存檔資料夾>/RimAgentProof/名稱.png`。

## 對話視窗

| 功能 | 怎麼操作到 | 證明 |
| --- | --- | --- |
| 開關視窗 | 底部主選單按鈕（`MainButtonWorker_ToggleAgent`）；收合後再按一次展開 | `AgentUiDriverTests`（截圖 `agent-window.png`） |
| 視窗位置與大小記憶 | 拖曳標題列、右下角 | 截圖：關閉再開，位置不變 |
| 模式／推理強度／模型選單 | 標題列下拉選單 | `AutoModeTests`、`DecisionRoutingTests`；截圖看選單 |
| 發送／停止 | 輸入框右側按鈕；處理中變成停止 | `ColonyAgentSessionTests` |
| 插話 | 處理中直接輸入送出 | `InterjectionTests` |
| 輸入 token 顯示 | 標題列；游標停留看歷史則數與壓縮門檻 | `ContextBreakdownTests` |
| 對話流（氣泡、分隔線、工具摘要可展開） | 對話區；點工具摘要展開 | `ToolGroupTests`、`AgentTranscriptTests`；截圖 |
| Markdown、LaTeX 轉純文字 | 模型回覆 | `ReplyMarkdownTests`、`LatexMathTests` |
| 框選複製、右鍵複製工具列 | 拖曳選取；工具列按右鍵 | `TranscriptUiTests`（遊戲內）；截圖 |
| 思考鏈顯示 | 模型回覆含思考內容時 | 截圖 |
| 截圖與貼上圖片 | 視窗內附圖、剪貼簿貼上 | `TranscriptScreenshotTests`、`DibDecoderTests`、`ImageAttachingTests` |
| 捲動跟隨、「回到最新」按鈕 | 對話區向上捲動 | 截圖 |
| 輸入歷史 | 輸入框焦點時按 ↑／↓ | 截圖 |
| 復原按鈕 | 狀態列；只在有可復原的操作時出現 | `AgentFixesTests`；截圖 |

## 斜線指令

| 功能 | 怎麼操作到 | 證明 |
| --- | --- | --- |
| 指令補全選單 | 輸入框開頭打 `/`，↑／↓ 瀏覽，Tab／Enter 帶入 | `SlashCommandsTests`；截圖 |
| `/goal`（新增、列出、show、edit、remove、clear、pause、resume） | 輸入框 | `GoalProgressTests`、`DecisionGoalAuditTests` |
| `/clear` | 輸入框 | `SlashCommandsTests` |
| `/compact` | 輸入框 | `CompactionTests` |
| `/stop` | 輸入框，或停止按鈕 | `ColonyAgentSessionTests` |

## 模式與確認

| 功能 | 怎麼操作到 | 證明 |
| --- | --- | --- |
| 確認／執行／自動／完整權限 | 標題列模式選單 | `CommandGateTests`、`AutoModeTests` |
| 預覽確認與捨棄 | 變更遊戲狀態的工具呼叫後出現的預覽 | `CommandGateTests`；遊戲內 `PendingUiCheck`；截圖 |
| 失敗連鎖保護 | 已確認的多個命令中有一項失敗 | `CommandGateTests` |
| 本局不再詢問 | 預覽上的勾選；黑名單工具不提供 | `CommandGateTests`、`ToolGuardTests` |
| MCP 呼叫審查 | 啟用 MCP 伺服器後呼叫其工具 | `McpTests` |

## 自主營運與目標

| 功能 | 怎麼操作到 | 證明 |
| --- | --- | --- |
| 主動照顧（事件響應、日常維護、定時巡檢） | 模組設定開啟「主動照顧」 | `AutonomousTests`、`AutonomousRunnerTests` |
| 指數退避重試 | 異常狀況持續未化解 | `AutonomousRunnerTests` |
| 行動預算與冷卻 | 模組設定 | `AutonomousTests` |
| 自動遊戲速度 | 主動照顧期間 | `AutoTimeSpeedTests` |
| 決策模型分流與目標審計 | 框架 Decision 分頁設定決策模型；模型選單選「自動」 | `DecisionTests`、`DecisionRoutingTests`、`SystemOneTests`；遊戲內 `SystemOneCheck` |

## 工具與擴充

| 功能 | 怎麼操作到 | 證明 |
| --- | --- | --- |
| 遊戲工具（觀察、工作、建造、戰鬥、交易、醫療、囚犯、動物、區域、生產、時間、存檔、世界、商隊、任務、飛船） | 模型呼叫；設定可依類別開關 | `Tools/*Tests`；遊戲內 `MapWorldTests.*` |
| 地圖截圖與總覽 | `view_map` 類工具 | `MapShotTests`、`MapOverviewToolsTests`；遊戲內 `MapViewCheck` |
| 房型藍圖 | `place_blueprint shape=layout` | `BuildToolsTests`；遊戲內 `LayoutCheck`、`ShipLayoutCheck`（截圖） |
| 腳本工具 `run_script` | 模型呼叫 | `ScriptToolsTests`；遊戲內 `VisionScriptCheck` |
| 技能 `read_skill` | `AgentPack/Skills/` | `AgentPackTests` |
| 子代理 | `AgentPack/Agents/` | `SubagentTests` |
| 記憶 | 模型呼叫記憶工具 | `MemoryScopeTests`、`WebAndMemoryTests` |
| 網頁搜尋（SearXNG／MCP） | 模組設定 | `WebAndMemoryTests` |
| MCP 伺服器 | `AgentPack/MCP/servers.json` | `McpTests` |
| 執行紀錄 | 模組設定「記錄 Agent 運行過程」 | `RunLogTests`、`RunLogStatsTests` |

## 載入與相容

| 功能 | 怎麼操作到 | 證明 |
| --- | --- | --- |
| 出貨組件都能載入、沒有重複的 MEAI | 啟動遊戲 | 建置期 `CheckShippingRules`；遊戲內 `StartupTests` |
| SmashTools 初始化與主執行緒派送 | 啟動遊戲 | 遊戲內 `SmashToolsTests` |
| 讀檔後狀態還原 | 讀取存檔 | 遊戲內 `ReloadCheck` |
| Unity 訊息方法會被呼叫 | — | `UnityMessageMethodTests` |
