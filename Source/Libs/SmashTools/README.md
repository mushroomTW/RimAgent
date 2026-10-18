# SmashPhil SmashTools 組建與出貨組件

`SmashTools.dll` 與 `CoreLib.dll` 取自 SmashPhil SmashTools develop 分支（commit: `be9eb6fe762d9762e09faee20005db12ca9592b0`），以 .NET 10 (net10.0) 與 RimWorld 1.7 遊戲組件建置完成。

**出貨說明**：
- SmashTools 採用 bundled DLL 方式隨 RimAgent 發行（複製至 `Assemblies/`）。
- 玩家無需安裝額外前置 Mod。
- 不複製 RimWorld 遊戲根目錄或 RimLLM Framework 已提供的組件（如 Harmony、System.*、UnityStubs、Waylith 等）。
- 於 Mod 載入時由 RimWorld 自動實例化 `ProjectSetup` (Verse.Mod) 與執行 `ProjectStartup` ([StaticConstructorOnStartup])，確保初始化恰好一次。

來源：上游 https://github.com/SmashPhil/SmashTools ，develop 分支 commit `be9eb6fe762d9762e09faee20005db12ca9592b0`。
MIT 授權見本目錄 LICENSE。

組件雜湊：
- `SmashTools.dll` SHA-256: `8080C35D99B67CA30397E2B809FBC7E11528CE4B7AAA23D31F81F08CF7E96BB2`
- `CoreLib.dll` SHA-256: `94C5CA3BD310D65E6BBC1B1582BEC3E1380668DCF0F8964875E94A23E647EAFA`

可重現建置指令：
```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File Source/Build-Dependencies.ps1
```

