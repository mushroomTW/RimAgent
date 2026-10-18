# SmashPhil DevTools 編譯期參考組件

`DevTools.dll` 由固定來源 commit 在本專案快取重新建置（1.7 Assemblies，組件版本 1.0.0.0）。

**僅供遊戲內測試專案（InGameTests）編譯與測試伴隨模組使用，不隨正式 Mod 出貨**。
在 `RimAgent.InGameTests.csproj` 中引用，建置時封裝至開發伴隨模組的 `Mod/Assemblies/` 供遊戲內 DevTools 執行器載入。
正式發行之 `Assemblies/` 絕不包含此組件。

來源：上游 https://github.com/SmashPhil/DevTools ，固定 commit `e0b33dfc222de214c45af979c8a45a74e1fb06f3`。本機原始碼快取位於 `../FasterGameLoading---Continued/.test-temp/DevTools`。MIT 授權見本目錄 LICENSE。

參考 DLL SHA-256：`236d5f3e4e9c03d6873600446e28cbe865f2fb9258f7e2575c4bb6606e0e6afe`。

可重現建置指令：
```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File Source/Build-Dependencies.ps1
```
