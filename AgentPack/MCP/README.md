# RimAgent 的 MCP 伺服器

RimAgent 以 MCP 用戶端身分經 stdio 連線（官方 ModelContextProtocol
SDK）：把下面的命令起成本機行程，工具經 `mcp_list_tools` /
`mcp_call_tool` 交給 agent（`Mcp` 工具類別，預設關閉）。

出貨不帶任何 server。要加的話，在本檔旁邊建 `servers.json`：

```json
{
  "servers": [
    {
      "name": "fetch",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-fetch"],
      "enabled": true,
      "timeoutSeconds": 30
    }
  ]
}
```

- Server 第一次被用到時才連線；改完設定送一句新指令即生效。
- 只裝信任的 server：server 以你的使用者權限執行，看得到 agent 傳給它的參數。
- `timeoutSeconds` 管單次呼叫（預設 30 秒）。卡住的 server 行程會被砍掉並回報，不會吊死遊戲。
- 閒置逾 10 分鐘的 server，下次使用或存檔時關掉。
