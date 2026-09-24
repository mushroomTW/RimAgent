# RimAgent MCP servers

RimAgent connects as an MCP client over stdio using the official ModelContextProtocol SDK. It starts the configured command as a local process and exposes its tools to the agent through `mcp_list_tools` and `mcp_call_tool`. The `Mcp` category is enabled by default. Listing tools is a query; calling an MCP tool uses the Confirm risk tier and may require player approval, depending on the current mode.

No servers are shipped with the mod. To add one, create `servers.json` next to this README:

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

- A server connects only when first used. RimAgent reads `servers.json` once per game session, so reload the game after changing it.
- Install only servers you trust. They run with your user account's permissions and can see the arguments the agent sends to them.
- `timeoutSeconds` limits each call (default: 30 seconds). RimAgent stops an unresponsive server process and reports the failure instead of leaving the game stuck.
- A server idle for more than 10 minutes is closed on its next use or when the game is saved.
