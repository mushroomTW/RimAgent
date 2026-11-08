# RimAgent MCP Server Integration

RimAgent operates as a Model Context Protocol (MCP) client over standard I/O (`stdio`), utilizing the official ModelContextProtocol SDK. It launches configured MCP servers as local background processes and exposes their tools to the agent through `mcp_list_tools` and `mcp_call_tool`.

The `Mcp` tool category is enabled by default. Discovering tools (`mcp_list_tools`) is treated as a read-only query; invoking tools (`mcp_call_tool`) carries the `Confirm` risk tier and adheres to the active execution mode.

---

## Configuration (`servers.json`)

No external MCP servers are bundled with RimAgent. To connect one or more servers, create a `servers.json` file in this directory alongside this README:

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

### Configuration Fields

| Field | Type | Description |
| --- | --- | --- |
| `name` | string | A unique identifier for this server instance |
| `command` | string | The executable binary to run (e.g. `npx`, `python`, `node`, or a direct executable path) |
| `args` | string[] | Array of command-line arguments passed to the executable |
| `enabled` | boolean | Set to `true` to activate this server; `false` to keep configuration while disabling |
| `timeoutSeconds` | integer | Maximum execution time in seconds per tool invocation (default: `30`) |

---

## Lifecycle & Operational Notes

- **Lazy Initialization:** Servers are spawned only upon their first invocation. `servers.json` is parsed once per RimWorld game session; restart RimWorld or reload your save after modifying this file.
- **Timeout Protection:** If a server process hangs or fails to respond within `timeoutSeconds`, RimAgent automatically terminates the unresponsive process and reports a structured error, preventing the game from freezing.
- **Idle Teardown:** A server idle for longer than 10 minutes is automatically closed on its next cycle or whenever the game is saved, releasing system resources.

---

## Security Warning

> [!CAUTION]
> MCP servers run locally under your operating system user account privileges. They can execute arbitrary code, access local files, and communicate with external networks. **Only connect and execute MCP servers that you completely trust.**
