<div align="right">

[English](README.md) | **[繁體中文](README.zh-TW.md)**

</div>

<div align="center">

# RimAgent

**以自然語言管理你的 RimWorld 殖民地。**

專為 RimWorld 1.6 設計的遊戲內 AI 助手。透過 51 項工具，以自然語言查詢殖民地、下達命令，並自行選擇操作前的把關方式。

**RimWorld 1.6** &nbsp;·&nbsp; **51 項工具** &nbsp;·&nbsp; **自動／自主模式** &nbsp;·&nbsp; **支援 MCP 擴充**

[![RimWorld 1.6](https://img.shields.io/badge/RimWorld-1.6-10b981.svg?style=flat)](#先決條件)
[![RimLLM Framework](https://img.shields.io/badge/RimLLM_Framework-%E2%89%A5_2026.09.12-38bdf8.svg?style=flat)](https://github.com/mushroomTW/RimLLM-Framework)
[![.NET Framework 4.7.2](https://img.shields.io/badge/.NET_Framework-4.7.2-512bd4.svg?style=flat)](#建置)

</div>

---

**快速跳轉：** [上手步驟](#快速上手) · [執行模式](#模式與安全機制) · [自主營運](#自主營運-autonomous-mode) · [工具](#工具功能清單) · [建置與測試](#建置)

---

## 快速上手

### 先決條件

- **RimWorld 1.6**
- **[RimLLM Framework](https://github.com/mushroomTW/RimLLM-Framework)**（版本需求 **2026.09.12 或更高**，以支援原生工具呼叫）
- 模組載入順序：`RimAgent` 必須排列於 `RimLLM Framework` **之後**載入。

> [!IMPORTANT]
> 所有底層大型語言模型（LLM）連線配置（API 供應商、金鑰、模型挑選、備援鏈與 Token 預算）皆在 **RimLLM Framework** 模組設定中管理。RimAgent 專注於提供殖民地操作工具與類別權限開關。

### 入門步驟

1. 安裝並啟用 **RimLLM Framework** 與 **RimAgent**，讓 RimAgent 排在框架之後載入。
2. 在 **模組設定 > RimLLM Framework** 設定支援的模型供應商與連線憑證。
3. 載入遊戲後點擊底部精簡的 **Agent** 圖示。初次使用可選 **詢問** 模式，只查詢不改動遊戲；或選 **確認** 模式，在每次命令執行前查看預覽。
4. 輸入「殖民地現在如何？」、「把 Alice 的就醫優先度設為 1」或「徵召所有人並移動到 (25, 30)」，再按 **發送** 或 Enter。

助手會先透過工具查詢目前地圖，再執行模式允許的操作。在確認模式下，請檢查預覽後再批准命令。

---

## 功能亮點

- **51 項殖民地工具：** 查詢地圖、指派工作、規劃建造、下達戰鬥與醫療命令、交易，以及管理動物、囚犯和區域。
- **五種執行模式：** 可從唯讀開始，要求逐次確認，或依選定規則讓助手自行操作。
- **自主事件與長期目標：** 定時巡檢、回應緊急事件和待處理抉擇，也能用 `/goal` 排入持續目標。
- **可編輯、可擴充：** 修改 `AgentPack/Prompts/`、新增技能，或連接信任的本機 MCP 伺服器。
- **進度隨存檔保存：** 可見對話、模型歷史、排程器、長期目標與 `/undo` 狀態都會保留。

---

## 視窗介面與斜線指令

拖曳視窗可移動位置，拖曳右下角可調整大小；RimAgent 會記住位置與尺寸。

- 從 Agent 視窗的下拉選單設定執行**模式**、模型**推理強度**與**模型**；游標停在模式上可看說明與風險。模型清單列出 RimLLM Framework 備援鏈上的條目，選定的模型會優先用於玩家指令與自主任務；選**預設**則依備援鏈。自動模式的分類器與 `/compact` 摘要仍用各自的模型。執行期間，**發送**會切換成**停止**。玩家每句指令不設定工具呼叫次數上限。
- 標題列顯示上一次模型請求回報的輸入 Token 數；供應商未提供用量時顯示「輸入 token 未回報」。游標停留可查看模型歷史則數與自動壓縮門檻。
- 玩家的話以靠右氣泡顯示，每句指令開啟新回合並以分隔線區隔。工具呼叫只顯示一行摘要，點一下可展開參數與結果；等待確認的預覽預設展開。
- 停在底部時自動跟隨新訊息；往上翻閱時不會被拉回，並出現「回到最新／有新訊息」按鈕。
- 輸入框有焦點時，↑↓ 可叫回先前送出的內容。
- 輸入 `/` 可開啟指令選單（↑↓ 選擇，Tab 或 Enter 確認）：

| 指令 | 說明 |
| --- | --- |
| `/goal <內容>` | 排入長期目標；建立後不久開始，並遵守當前模式的命令規則 |
| `/goal` | 列出目標及目前進度 |
| `/goal remove <序號>` | 刪除指定序號的目標 |
| `/goal pause` / `/goal resume` | 暫停或恢復長期目標 |
| `/undo` | 復原上一句指令所變更的工作優先級與作息時間表 |
| `/clear` | 清空當前可見的對話紀錄 |
| `/compact` | 壓縮上下文：用一次 LLM 摘要折疊較舊的模型歷史，只保留最近幾輪（可見紀錄保留） |
| `/stop` | 立即中斷當前正在執行的指令 |

> **上下文壓縮：** 請求前以 Agent Framework 估算模型歷史 token；達到 RimLLM Framework 提供的模型視窗上限之設定比例（預設 90%，並保留輸出 token 空間）時，折疊較舊的工具輸出，不增加模型呼叫。若框架沒有該模型的視窗上限，就不自動折疊。儲存歷史超過 120 則時另裁至約 60 則；可見紀錄不受影響。`/compact` 可另行要求模型產生更深入的摘要。

> **執行紀錄：** 在模組設定開啟「記錄 Agent 運行過程」後，指令、工具呼叫、安全判定與結果會寫入 `RimWorld save data/RimAgent/RimAgent-runs.jsonl`。預設關閉，最多保留 500 次執行或 16 MiB；設定頁可直接開啟紀錄檔所在資料夾。

> **提示詞可編輯：** `AgentPack/Prompts/` 放內建提示詞，`AgentPack/Skills/` 放自選技能；`AgentPack/AGENTS.md` 出貨是空的，可寫入自己的指示。修改後需重新讀取存檔或開新局，不必重編譯。詳見[技能設定](AgentPack/Skills/README.md)與 [MCP 設定](AgentPack/MCP/README.md)。

---

## 模式與安全機制

透過單一模式設定，即可決定助手能自行操作的授權範圍：

| 模式 | 行為說明 |
| --- | --- |
| **詢問** | 唯讀。模型只看得到查詢工具；切換地圖與截圖會改變狀態，因此不提供。 |
| **確認** | 查詢直接執行；每個命令先顯示預覽，等待玩家批准。 |
| **YOLO** | 許可的命令與 MCP 呼叫免確認執行；固定黑名單排除交易、商隊、手術等高風險工具。不啟用背景排程器。 |
| **自動** | 第二個模型會檢查每個命令（含 MCP）的風險，以及玩家是否要求該操作。只有低風險且受請求的操作會執行；高風險、未受請求或無法判定的操作會附理由拒絕，**不會轉成待確認命令**。分類器模型可在模組設定的下拉選單中，從備援鏈條目挑選。 |
| **自主** | 加入定時巡檢與事件反應。命令直接執行，不受 YOLO 黑名單與每日工具預算限制；冷卻時間可設定。 |

### 確認流程

- **確認與捨棄：** 確認會執行預覽時捕捉的工具呼叫及參數，不會再向模型詢問。捨棄或送出新指令會清除待確認命令。
- **本局不再問：** 確認模式可依工具名稱記住符合條件的批准；`/clear` 或重開遊戲後歸零，也不會寫入存檔。`mcp_call_tool` 與高風險黑名單（`execute_trade`、`form_caravan`、`release_prisoner`、`schedule_surgery`、`set_prisoner_interaction`）不提供此選項；自動模式仍逐次審查。
- **MCP 呼叫：** `mcp_call_tool` 在確認模式需要玩家批准，在自動模式由分類器判定，在 YOLO 或自主模式直接執行。

> [!WARNING]
> 戰鬥指令受限於 LLM 往返處理時間，通常有數秒延遲。僅適用於戰略集結、防線佈置與撤退，不適合作為即時戰術微操。

> [!CAUTION]
> 本機 MCP 伺服器是以作業系統目前使用者的本機權限執行。請務必僅啟用與連線至你完全信任的 MCP 伺服器。

---

## 自主營運 (Autonomous Mode)

在 Agent 視窗的模式下拉選單選擇 **自主**，助手就能在背景執行任務：

- **定時巡檢：** 依設定週期檢查殖民地，預設每 6 遊戲小時一次。
- **事件反應：** 回應襲擊開始或結束、瀕臨精神崩潰、缺糧、殖民者倒地或流血，以及火災。新抉擇信件與對話框每 2 秒（現實時間）檢查一次；即使對話框暫停遊戲也會檢查，每個抉擇只觸發一次。
- **預算與冷卻：** 不限制每日工具呼叫次數，但每輪自主任務最多呼叫 20 次工具，避免背景打轉。可在模組設定調整 0～60 分鐘的冷卻時間，預設關閉。
- **長期目標：** `/goal` 獨立運作，在任何模式下都遵守該模式的確認規則。目標隨遊戲存檔；連續三輪受阻或出錯，或執行滿 60 輪後會暫停，直到使用 `/goal resume`。

---

## 工具功能清單

12 類工具預設全開，可在模組設定中個別關閉，或一鍵全部允許／關閉；所選模式決定命令的執行方式。

| 類別 | 功能範例 |
| --- | --- |
| 觀察 | 殖民地狀態、資源、威脅、研究、地圖、物件、區域、截圖與待回應抉擇 |
| 工作 | 工作優先度與每日作息（可復原） |
| 建造 | 標記、藍圖（單點、直線、填滿或外框；最多 400 格）、物品禁用與指令按鈕 |
| 戰鬥 | 徵召、移動、攻擊、救援、俘虜、包紮、剝除、裝備與進入冷凍休眠艙 |
| 交易 | 軌道與來訪商人、買賣交易、商隊 |
| 醫療 | 健康狀態、用藥等級、手術 |
| 囚犯 | 囚犯狀態、互動方針、釋放 |
| 動物 | 動物清單、馴養、訓練、允許區域 |
| 區域 | 儲藏區、種植區、儲存篩選條件 |
| 生產 | 工作台與帳單 |
| 時間 | 暫停與遊戲速度 |
| MCP | 設定的本機 MCP 伺服器工具 |

---

## 運作架構

```text
玩家指令 → ColonyAgentSession → RimLLM Framework 模型 → 殖民地工具 → IGameWorld → RimWorld
```

模型取得的工具會依目前模式與類別權限篩選。兩個介面讓大部分行為不必啟動 RimWorld 也能測試：

- **`IChatClient`** 讓測試使用模擬模型回覆，不需網路呼叫。
- **`IGameWorld`** 讓工具測試使用記憶體中的假世界；`RimWorldGameWorld` 則連接實際遊戲。

介面文字與 Agent 主按鈕標籤支援英文、繁體中文及簡體中文。

---

## 建置

```bash
dotnet build "Source/RimAgent/RimAgent.csproj" -c Release
```

Release 建置將輸出 `Assemblies/RimAgent.dll` 以及本模組專屬、未隨框架出貨的 12 顆必要相依組件：

- **10 顆 Agent Framework 組件**：`Microsoft.Agents.AI`、`Microsoft.Agents.AI.Abstractions`、`Microsoft.Extensions.Compliance.Abstractions`、`Microsoft.Extensions.ObjectPool`、`Microsoft.Extensions.VectorData.Abstractions`、`Microsoft.Extensions.FileSystemGlobbing`、`Microsoft.Bcl.Memory`、`Microsoft.Bcl.HashCode`、`Microsoft.ML.Tokenizers`、`Google.Protobuf`。
- **2 顆 MCP SDK 組件**：`ModelContextProtocol.Core`、`System.Collections.Immutable`。

RimWorld 啟動時會透過 `Assembly.GetTypes()` 與 `GetFields()` 掃描所有組件，因此所有基底型別與欄位型別皆已精準對齊。本專案透過 `../RimLLM Framework/Assemblies` 引用框架組件（非預設 Steam 目錄時可透過 `/p:RimLLMDir=<路徑>` 覆寫）。

---

## 測試

```bash
dotnet test "Source/RimAgent.Tests/RimAgent.Tests.csproj"
```

NUnit 測試套件使用 .NET Framework 4.7.2，無需啟動 RimWorld，涵蓋工具行為、確認預覽、存讀檔、風險分類，以及兩份 README 顯示的工具數量。

---

本專案基於 [MIT License](LICENSE) 條款開源發布。
