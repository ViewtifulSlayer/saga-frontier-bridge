# MCP Wrapper

`saga-frontier-bridge-mcp` is a thin stdio MCP server over the live read-only
HTTP bridge. It does not read the save file directly, change game state, or add
any endpoint to the plugin.

## Requirements

- The game is running with the bridge plugin loaded.
- The bridge is listening on `http://127.0.0.1:27999` unless `SAGA_BRIDGE_URL`
  says otherwise.
- .NET 8 runtime or SDK.

## Build and run

From the repository root:

```powershell
dotnet build SagaFrontierBridge.sln -c Release
dotnet src\SagaFrontierBridge.Mcp\bin\Release\net8.0\SagaFrontierBridge.Mcp.dll
```

An MCP client normally launches the second command and talks to it over stdin
and stdout. Do not run it interactively unless you are sending framed JSON-RPC
yourself.

## Configuration

| Environment variable | Default | Purpose |
|---|---|---|
| `SAGA_BRIDGE_URL` | `http://127.0.0.1:27999` | Base URL. Only loopback hosts are accepted. A missing port uses 27999. |
| `SAGA_BRIDGE_TOKEN` | empty | Bearer token. It must match the BepInEx `AccessToken`. |

The URL check rejects non-loopback hosts, paths, and non-HTTP schemes. This keeps
the token from being sent to an arbitrary service.

Set the token in the MCP client's environment or env block. Do not put it in a
committed MCP config.

Example MCP client entry:

```json
{
  "mcpServers": {
    "saga-frontier-bridge": {
      "command": "dotnet",
      "args": [
        "<path-to-bridge-repo>\\src\\SagaFrontierBridge.Mcp\\bin\\Release\\net8.0\\SagaFrontierBridge.Mcp.dll"
      ],
      "env": {
        "SAGA_BRIDGE_URL": "http://127.0.0.1:27999",
        "SAGA_BRIDGE_TOKEN": "set-this-in-the-client-environment"
      }
    }
  }
}
```

Replace `<path-to-bridge-repo>` with the absolute path to your checkout. If your
client supports environment expansion, use `${SAGA_BRIDGE_TOKEN}` instead of a
literal.

## Tools

| Tool | Arguments | Returns |
|---|---|---|
| `bridge_status` | none | Health, Unity runtime, and the save catalog in one read-only snapshot |
| `bridge_runtime` | none | Unity and game versions, active scene, frame count, time scale |
| `bridge_save_work` | none | Live `MainGame.SAVE_WORK` summary: test marker, party core, active party ids, roster count, checksum length and SHA-256 |
| `bridge_party` | none | Party core, active and roster ids, and per-member stats and equipment from `SAVE_WORK.ply` |
| `bridge_flags` | none | Scenario, box, chart, event, battle, and `cfg1` flag arrays as integer arrays |
| `bridge_state` | none | Current IMF room, derived map id, area and region ids, X/Z position, direction, previous map id |
| `bridge_screenshot` | none | Live game screenshot as a PNG image; the text content reports its width and height |
| `bridge_list_saves` | none | Discovered `SaveData.bin` paths, sizes, timestamps, SHA-256 |
| `bridge_get_save` | `id` | One save record by the id from `bridge_list_saves` |
| `bridge_capabilities` | none | Methods, implemented endpoints, planned endpoints |

`bridge_status` is the first call to make. It returns `isError: true` only when
the health request fails; runtime or save-catalog failures are embedded as
error objects so one unavailable probe does not hide the rest.

`bridge_save_work`, `bridge_party`, `bridge_flags`, and `bridge_state` are
separate so a missing live save object does not hide health, runtime, or the
save catalog. They read the live game object by reflection and return
`available: false` with an error instead of failing the tool call. Before a save
is loaded the game may expose a default instance; the tools then return
`saveLoaded: false` without party, flag, location, or checksum fields.

`bridge_party` returns `members` in active team order. `bridge_flags` returns
integer arrays so a client can index `scenarioFlags[0x25]` directly; the arrays
are not base64. `bridge_state` reports `imfName`, the derived `mapId`, `areaId`,
`regionId`, `x`, `z`, `direction`, and `previousMapId`. `bridge_screenshot`
returns MCP image content with the PNG bytes; capture is queued to the Unity
main thread, and the tool reports the bridge's `503` message if no frame is
available. None of these tools accepts a path or returns raw game or save files.

## Contract

- Every tool is a `GET`.
- No tool returns save or game file bytes. `bridge_screenshot` returns a live
  PNG image rendered by the game, not a file from disk.
- No tool writes a file, changes game state, or accepts an arbitrary path.
- A 401 is returned as a tool error with the bridge's JSON error body.
- The HTTP token is never printed, logged, or included in a tool result.
- The MCP server supports Content-Length framing and newline-delimited JSON.

## Tests

`tests/SagaFrontierBridge.Mcp.Tests` launches the built MCP host as a child
process, speaks Content-Length framed JSON-RPC over stdin/stdout, and points it
at a loopback fake bridge. It verifies the `initialize` handshake, the ten-tool
list, and that `bridge_screenshot` returns `image/png` content whose base64
decodes to the expected PNG. Error-path tests cover newline-delimited JSON
framing, malformed framing, invalid JSON payloads, requests before `initialize`,
malformed `tools/call` arguments, a bridge `401` propagated as a tool error with
the bridge JSON body, a non-image `503` screenshot error returned as text with
no image content, and the client timeout against a non-responsive bridge. The
tests do not require the game or BepInEx.

## Live smoke test

With the game and bridge running:

```powershell
$env:SAGA_BRIDGE_TOKEN = "<the same token as BepInEx>"
dotnet src\SagaFrontierBridge.Mcp\bin\Release\net8.0\SagaFrontierBridge.Mcp.dll
```

The process waits for an MCP `initialize` frame. A normal client sends that
automatically.

## Related

- `docs/api.md` - the HTTP contract this server wraps.
- `docs/save-path.md` - the IL2CPP serialization and integrity map.
- `README.md` - install, deploy, and bridge configuration.
