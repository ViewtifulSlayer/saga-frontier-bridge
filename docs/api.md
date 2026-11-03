# Bridge API

Base URL: `http://127.0.0.1:27999/`

All routes are read-only. The server accepts `GET` and `HEAD` only. Every
response is JSON except `/v1/screenshot`, which returns `image/png`; `HEAD` has
no body. The connection closes after one request.

A stdio MCP wrapper over this API is documented in [mcp.md](mcp.md).

## Authentication

If `AccessToken` is configured, include either:

```http
Authorization: Bearer <token>
```

or:

```http
X-Bridge-Token: <token>
```

The check is constant-time for equal-length values. Without a configured token,
the bridge is still loopback-only, but any local process can query it.

## Common response headers

```http
Content-Type: application/json; charset=utf-8
Cache-Control: no-store
X-Content-Type-Options: nosniff
Connection: close
Server: saga-frontier-bridge/0.1.0
```

`/v1/screenshot` uses `Content-Type: image/png` and adds
`X-Screenshot-Width` and `X-Screenshot-Height`. `Content-Length` is always
present. `HEAD` returns the same headers and the length of the body that the
corresponding `GET` would return.

## Errors

```json
{
  "error": {
    "code": "unauthorized",
    "message": "A valid bearer token is required."
  }
}
```

| Status | Code | Meaning |
|---|---|---|
| `400` | `bad_request` | Malformed request, invalid Host, or a request body |
| `401` | `unauthorized` | Token missing or wrong |
| `404` | `not_found` | Unknown route or save id |
| `405` | `method_not_allowed` | Anything other than GET or HEAD |
| `500` | `internal_error` | The bridge could not build a response |
| `503` | `screenshot_unavailable` | No main-thread frame hook, no texture, or PNG encoding failed |
| `503` | `screenshot_timeout` | The requested frame did not arrive before the request timeout |

## GET /v1/health

Process and readiness summary. This does not read the save payload.

```json
{
  "status": "ok",
  "bridge": "saga-frontier-bridge",
  "version": "0.1.0",
  "readOnly": true,
  "pid": 12345,
  "startedAtUtc": "2026-11-03T12:00:00.0000000+00:00",
  "uptimeSeconds": 12.34,
  "saveRoot": "C:\\Users\\name\\Documents\\My Games\\SaGa Frontier Remastered\\Steam",
  "saveRootExists": true,
  "saveCount": 1,
  "runtimeAvailable": true
}
```

`runtimeAvailable` is `false` when the reflection probe cannot see
`UnityEngine.Application`; that is expected before BepInEx has generated its
interop assemblies. The route still returns `200`.

## GET /v1/runtime

Best-effort Unity state. This is read-only reflection, not a game-specific save
probe.

```json
{
  "available": true,
  "unityVersion": "2019.4.16f1",
  "gameVersion": "1.0.1",
  "productName": "SaGa Frontier Remastered",
  "companyName": "SquareEnix",
  "scene": {
    "name": "Field",
    "path": "Assets/Scenes/Field.unity",
    "buildIndex": 3,
    "isLoaded": true
  },
  "time": {
    "frameCount": 18422,
    "realtimeSinceStartup": 412.73,
    "timeScale": 1.0
  },
  "capturedAtUtc": "2026-11-03T12:00:00.0000000+00:00"
}
```

If the interop types are not available:

```json
{
  "available": false,
  "error": "UnityEngine.Application was not found in the loaded assemblies. The IL2CPP interop assemblies may not be generated yet.",
  "capturedAtUtc": "2026-11-03T12:00:00.0000000+00:00"
}
```

Individual fields are omitted when the member cannot be read. The endpoint does
not throw into the HTTP layer.

## GET /v1/save-work

Read-only reflection summary of the live `MainGame.SAVE_WORK` object. It does
not force a save, read the save container, or return raw arrays. This is the
live counterpart to the save-file probe, not a replacement for it.

```json
{
  "available": true,
  "source": "SAVEWK_ADR",
  "saveLoaded": true,
  "test": 75,
  "isTakeover": false,
  "party": {
    "psnCnt": 1,
    "heroNo": 5,
    "name": "Asellus",
    "name2": "Asellus",
    "money": 100,
    "parLev": 23,
    "playTimeHours": 0,
    "playTimeMinutes": 11,
    "playTimeSeconds": 42.0
  },
  "partyCharacterIds": [5],
  "rosterCount": 1,
  "sumLength": 1,
  "sumSha256": "6e340b9cffb37a989ca544e6bb780a2c78901d3fb33738768511a30617afa01d",
  "capturedAtUtc": "2026-11-03T12:00:00.0000000+00:00"
}
```

`source` is the `MainGame` field that supplied the object (`SAVEWK_ADR` first,
then `save`, then `takeoverData`). `partyCharacterIds` is the active `team`
list with `255` padding removed. `rosterCount` counts non-padding `chrNo`
entries. `sumLength` and `sumSha256` describe the `sum` byte array without
returning it.

Before a save is loaded, the game can expose a default `SAVE_WORK` instance.
That response is still `available: true`, but `saveLoaded` is `false` and the
party, roster, and checksum fields are omitted; it is not an error. A missing
`MainGame`, a null `Instance`, or a missing `SAVE_WORK` field returns
`available: false` with an `error` string.

## GET /v1/party

Read-only party detail from the live `SAVE_WORK`. It combines the compact party
summary, the active `team` order, the roster `chrNo` order, and the matching
records from `SAVE_WORK.ply`. It does not read `SaveData.bin` and does not return
raw skill or item arrays.

```json
{
  "available": true,
  "source": "SAVEWK_ADR",
  "saveLoaded": true,
  "party": {
    "psnCnt": 19,
    "heroNo": 5,
    "name": "Ａｓｅｌｌｕｓ",
    "name2": "Ａｓｅｌｌｕｓ",
    "money": 54511,
    "parLev": 128,
    "playTimeHours": 25,
    "playTimeMinutes": 11,
    "playTimeSeconds": 37.138389587402344
  },
  "rosterSlotCount": 56,
  "activeCharacterIds": [5, 19, 21, 22, 25, 20, 23, 24, 8, 39, 12, 0, 11, 3, 10, 1, 34, 37, 38],
  "rosterCharacterIds": [5, 21, 39, 19, 20, 23, 11, 37, 12, 22, 24, 25, 3, 38, 34, 0, 10, 1, 8],
  "members": [
    {
      "characterId": 5,
      "hpMax": 867,
      "lpNow": 4,
      "lpMax": 4,
      "wpNow": 146,
      "wpMax": 146,
      "jpNow": 64,
      "jpMax": 64,
      "strNow": 81,
      "dexNow": 76,
      "hndNow": 37,
      "mndNow": 77,
      "mgcNow": 34,
      "conNow": 79,
      "chmNow": 86,
      "race": 65,
      "nature": 90,
      "characterType": 1,
      "allLpLoss": 1263,
      "battleCount": 888,
      "equipmentItemIds": [44, 46, 126, 7, 163, 173, 209, 216],
      "equipmentAttackIds": [2, 20, 28, 12, 17, 29, 368, 65535]
    }
  ],
  "capturedAtUtc": "2026-11-03T19:30:00.0000000+00:00"
}
```

`members` is in active `team` order and has one entry per non-`255` active id.
`equipmentItemIds` maps to `ply[].eqpItm`; `equipmentAttackIds` maps to
`ply[].eqpAtk`. `characterType` is the `ply[].getType` byte, renamed so it does
not shadow `object.GetType`. `nature` is a byte, not a boolean. `rosterSlotCount`
is the full 56-entry `chrNo` array length; `rosterCharacterIds` removes the `255`
padding. A missing `ply` array or a missing member record omits the affected
detail fields rather than inventing values.

Before a save is loaded, the route returns `200` with `available: true`,
`saveLoaded: false`, and no party fields.

## GET /v1/flags

Read-only live flag arrays from `SAVE_WORK.pty` and `SAVE_WORK.eventwk`. Arrays
are returned as integer arrays, not base64, so callers can index them directly.

```jsonc
{
  "available": true,
  "source": "SAVEWK_ADR",
  "saveLoaded": true,
  "scenarioFlags": [4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 213, 42, 75, 247, 207],
  "boxFlags": [0, 0, 0],
  "scenarioChartState": [0, 0, 0],
  "eventFlags": [4, 0, 164, 4, 88, 255],
  "eventTemp": [0, 0, 0],
  "eventBattle": [0, 0, 0],
  "cfg1": [0, 1, 2],
  "setScenarioFlagCount": 111,
  "setScenarioChartCount": 42,
  "capturedAtUtc": "2026-11-03T19:30:00.0000000+00:00"
}
```

The example arrays are truncated for readability. The live lengths are:

| Field | Source | Length |
|---|---|---|
| `scenarioFlags` | `pty.snroFlg` | 512 |
| `boxFlags` | `pty.boxFlg` | 64 |
| `scenarioChartState` | `pty.scenarioChartStateArray` | 1,024 |
| `eventFlags` | `eventwk.wk` | 32 |
| `eventTemp` | `eventwk.temp` | 256 |
| `eventBattle` | `pty.event_btl` | 16 |
| `cfg1` | `pty.cfg1` | 12 |

`scenarioFlags` is a byte state array, not a boolean array; use the exact-state
rule in the Counselor pack before interpreting bits. `setScenarioFlagCount` and
`setScenarioChartCount` count non-zero bytes in the first two arrays. The route
returns `200` with `saveLoaded: false` and no arrays before a save is loaded, and
an unavailable probe returns `available: false` with an `error` string.

## GET /v1/state

Read-only current room and map coordinates from the live `SAVE_WORK.mapwk` and
`SAVE_WORK.eventwk` objects. It does not read `SaveData.bin` or move the player.

```json
{
  "available": true,
  "source": "SAVEWK_ADR",
  "saveLoaded": true,
  "imfName": "imf1b0",
  "mapId": 432,
  "areaId": 26,
  "regionId": 17,
  "x": 32,
  "z": 5120,
  "direction": 17,
  "previousMapId": 775,
  "capturedAtUtc": "2026-11-03T19:30:00.0000000+00:00"
}
```

`imfName` is the loaded IMF map asset and names the current room. `mapId` is the
hex suffix of that name parsed as a number (`imf1b0` -> 432); it is omitted when
the name does not match `imf` plus one to four hex digits. `areaId` is
`mapwk.map_name`, which indexes the area table and names the region rather than
the room. `regionId` is `mapwk.region_no`. `x` and `z` are `eventwk.xpos` and
`eventwk.zpos`; `direction` is `eventwk.dir`. `previousMapId` is
`eventwk.mapNo`, the previously loaded map, not the current room.

Before a save is loaded the route returns `200` with `saveLoaded: false` and no
location fields. An unavailable probe returns `available: false` with an
`error` string.

## GET /v1/screenshot

Returns a live PNG of the game window. The capture is queued to the Unity main
thread and encoded on the next rendered frame. The HTTP worker awaits the
result without blocking the listener, so `/v1/health` and the other routes keep
responding while a capture is pending.

```http
HTTP/1.1 200 OK
Content-Type: image/png
Content-Length: 1108266
Cache-Control: no-store
X-Content-Type-Options: nosniff
Connection: close
Server: saga-frontier-bridge/0.1.0
X-Screenshot-Width: 1280
X-Screenshot-Height: 720
```

The body is the PNG bytes. `HEAD /v1/screenshot` returns the same headers and
`Content-Length` with no body. A capture does not write a file or change game
state, but it includes whatever the game is currently rendering, including UI
and overlays.

If the Unity frame hook is unavailable, the route returns
`503 screenshot_unavailable`. If no rendered frame arrives before the request
timeout, it returns `503 screenshot_timeout`. The plugin installs the frame hook
with a Harmony postfix on `UnityEngine.Application.InvokeOnBeforeRender`; if a
future build strips that method, the route degrades to `503` rather than
blocking.

## GET /v1/saves

Discovers `SaveData.bin` files under the configured save root. It computes a
full-file SHA-256 so callers can detect changes without receiving the file.

```json
{
  "root": "C:\\Users\\name\\Documents\\My Games\\SaGa Frontier Remastered\\Steam",
  "rootExists": true,
  "saves": [
    {
      "id": "8f1c2d3e4a5b",
      "path": "C:\\Users\\name\\Documents\\My Games\\SaGa Frontier Remastered\\Steam\\<steamid>\\SaveData\\SaveData.bin",
      "relativePath": "<steamid>\\SaveData\\SaveData.bin",
      "length": 16933,
      "lastWriteTimeUtc": "2026-11-01T02:15:21.0000000Z",
      "sha256": "73499747..."
    }
  ],
  "errors": [],
  "capturedAtUtc": "2026-11-03T12:00:00.0000000+00:00"
}
```

`<steamid>` is a placeholder for the numeric Steam account folder in the save
path; it is not a real account identifier. `id` is the first 12 lowercase hex
characters of a SHA-256 over the normalized absolute path. It is stable for a
given installation path. `sha256` is the full 64-character digest of the file
bytes.

If the root does not exist, the route returns `200` with `rootExists: false` and
an empty `saves` array. Per-file read errors are collected in `errors`; one bad
file does not hide the others.

There is no endpoint that returns `SaveData.bin` bytes.

## GET /v1/saves/{id}

Returns the same record as one entry in `/v1/saves`.

```json
{
  "id": "8f1c2d3e4a5b",
  "path": "C:\\Users\\name\\Documents\\My Games\\SaGa Frontier Remastered\\Steam\\<steamid>\\SaveData\\SaveData.bin",
  "relativePath": "<steamid>\\SaveData\\SaveData.bin",
  "length": 16933,
  "lastWriteTimeUtc": "2026-11-01T02:15:21.0000000Z",
  "sha256": "73499747..."
}
```

An unknown id returns `404 not_found`.

## GET /v1/capabilities

```json
{
  "readOnly": true,
  "methods": ["GET", "HEAD"],
  "endpoints": [
    "/v1/health",
    "/v1/runtime",
    "/v1/save-work",
    "/v1/party",
    "/v1/flags",
    "/v1/state",
    "/v1/screenshot",
    "/v1/saves",
    "/v1/saves/{id}",
    "/v1/capabilities"
  ],
  "planned": [],
  "requestBodies": false,
  "fileContents": false
}
```

`planned` is empty; every listed endpoint is implemented. `fileContents` remains
`false` because the bridge does not return save-file bytes. `/v1/screenshot`
returns a live image, not a game file.

## Transport behavior

- Bind address: `127.0.0.1` only. The port is configured in BepInEx.
- `Host` must be `127.0.0.1` or `localhost`; if a port is supplied it must match
  the configured port. This blocks DNS-rebinding requests.
- Remote peers must be loopback.
- One request per connection; keep-alive is not implemented.
- Request line maximum: 4,096 bytes. Header block maximum: 8,192 bytes.
- No request body is accepted. A non-GET method with a small declared body is
  drained before the `405` so the client receives the response cleanly.
- No CORS headers are emitted.

## curl examples

```powershell
$token = "replace-with-your-token"
$headers = @{ Authorization = "Bearer $token" }

curl.exe -H "Authorization: Bearer $token" http://127.0.0.1:27999/v1/health
curl.exe -H "Authorization: Bearer $token" http://127.0.0.1:27999/v1/runtime
curl.exe -H "Authorization: Bearer $token" http://127.0.0.1:27999/v1/save-work
curl.exe -H "Authorization: Bearer $token" http://127.0.0.1:27999/v1/party
curl.exe -H "Authorization: Bearer $token" http://127.0.0.1:27999/v1/flags
curl.exe -H "Authorization: Bearer $token" http://127.0.0.1:27999/v1/state
curl.exe -H "Authorization: Bearer $token" -o screenshot.png http://127.0.0.1:27999/v1/screenshot
curl.exe -H "Authorization: Bearer $token" http://127.0.0.1:27999/v1/saves
curl.exe -H "Authorization: Bearer $token" http://127.0.0.1:27999/v1/capabilities
```

PowerShell's `Invoke-RestMethod` can use a system proxy; `curl.exe` avoids that
for loopback diagnostics.
