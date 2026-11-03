# SaGa Frontier Bridge

[![build](https://github.com/ViewtifulSlayer/saga-frontier-bridge/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/ViewtifulSlayer/saga-frontier-bridge/actions/workflows/build.yml)

A read-only BepInEx 6 IL2CPP plugin that exposes a small loopback HTTP API from
*SaGa Frontier Remastered* (PC/Steam, Unity 2019.4.16f1, IL2CPP metadata v24).

This is the bridge layer for save and data-mapping work. It is not a save editor,
a mod menu, or a trainer. The server binds only to `127.0.0.1`, accepts only
`GET` and `HEAD`, and has no endpoint that writes a file or changes game state.

## Status

Field-tested on 2026-11-03 against the Steam build (Unity 2019.4.16f1, game
1.0.1) with BepInEx 6.0.0-be.791. The loader injected, IL2CPP interop generated,
and the plugin loaded. `/v1/health`, `/v1/runtime`, `/v1/save-work`,
`/v1/party`, `/v1/flags`, `/v1/state`, `/v1/saves`, `/v1/saves/{id}`,
`/v1/capabilities`, and `/v1/screenshot` all returned `200`; `HEAD` worked, and
the negative cases returned `404`, `405`, and `400`. The API's SHA-256 for the
live save matched the file on disk. `/v1/save-work` first reported `saveLoaded: false` at the
title screen, then `saveLoaded: true` after loading Asellus slot No.04; the live
response then matched the current on-disk slot on every field, and the
historical fixture on every stable field except a +37.96s play-time advance.
`/v1/party` matched all 19 active member records on 21 mapped fields,
`/v1/flags` matched all seven arrays exactly against the loaded slot, and
`/v1/state` matched `imf1b0` / map 432 / area 26 / region 17 / X 32 / Z 5120 /
direction 17. A live save to a new slot also showed that `SAVE_WORK.sum` and the
`sfSys.chk*` fields stayed unchanged. `/v1/screenshot` returned a 1280x720 RGBA
PNG from the title screen. Capture runs on the Unity main thread; the loopback
server keeps serving other requests while a frame is pending. The IL2CPP
metadata was dumped with Il2CppDumper 6.7.46; [docs/save-path.md](docs/save-path.md)
records the serialization and integrity path. A thin stdio MCP wrapper is
included in `src/SagaFrontierBridge.Mcp/` and documented in
[docs/mcp.md](docs/mcp.md).

The Unity runtime and `SAVE_WORK` probes are reflection-based and intentionally
best-effort. They returned a live scene, frame count, `SAVE_WORK`, party, and
flag data on this build, but may still report `available: false` if a future
build strips a member or interop is incomplete.

The current version is `0.1.0`, the initial repository version; see
[CHANGELOG.md](CHANGELOG.md).

## What works now

- Loopback-only HTTP server with optional bearer token.
- `GET /v1/health`: process, bridge, save-root, and runtime summary.
- `GET /v1/runtime`: Unity version, product, active scene, frame count, and
  time scale through reflection. Returns a structured error instead of failing
  when the interop assemblies are not available.
- `GET /v1/save-work`: compact read-only summary of the live
  `MainGame.SAVE_WORK` object: test marker, party core, active party ids,
  roster count, and checksum length and SHA-256. It does not return raw arrays
  or write game state.
- `GET /v1/party`: live party core, active and roster ids, and per-member
  stats and equipment from `SAVE_WORK.ply`.
- `GET /v1/flags`: live scenario, box, chart, event, battle, and `cfg1` flag
  arrays as integer arrays.
- `GET /v1/state`: current IMF room, derived map id, area and region ids, X/Z
  coordinates, direction, and previous map id from `mapwk`/`eventwk`.
- `GET /v1/saves`: discovers `SaveData.bin` under the configured save root and
  reports size, timestamp, path, and SHA-256. It reads the file; it never
  writes or rewrites it.
- `GET /v1/saves/{id}`: metadata for one discovered save.
- `GET /v1/screenshot`: a live PNG captured on the Unity main thread. The
  route returns `image/png` with `X-Screenshot-Width` and
  `X-Screenshot-Height`; it never writes a file.
- `GET /v1/capabilities`: the implemented read-only surface.
- `src/SagaFrontierBridge.Mcp`: a dependency-free stdio MCP server with ten
  read-only tools over the HTTP surface.

The API contract is in [docs/api.md](docs/api.md). The MCP tools are in
[docs/mcp.md](docs/mcp.md). The IL2CPP save map is in
[docs/save-path.md](docs/save-path.md).

## Requirements

- Windows 64-bit.
- .NET SDK 8 or newer. The plugin targets `net6.0` because BepInEx 6 IL2CPP
  loads plugins on CoreCLR 6. The MCP host targets `net8.0`.
- BepInEx 6 IL2CPP, 64-bit Windows build. BepInEx 6 has no stable release; the
  plugin pins `BepInEx.Unity.IL2CPP` `6.0.0-be.791`, the build used for the live
  field test. The runtime loader is a separate download from
  <https://builds.bepinex.dev/projects/bepinex_be>.
- The game's `GameAssembly.dll` and
  `SaGa Frontier Remastered_Data/il2cpp_data/Metadata/global-metadata.dat`.

## Build

From the repository root:

```powershell
dotnet build SagaFrontierBridge.sln -c Release
dotnet test SagaFrontierBridge.sln -c Release --no-build
```

Or:

```powershell
pwsh ./tools/build.ps1
```

The plugin output is
`src/SagaFrontierBridge.Plugin/bin/Release/`. It contains
`SagaFrontierBridge.dll` and `SagaFrontierBridge.Core.dll`. The MCP host is
under `src/SagaFrontierBridge.Mcp/bin/Release/net8.0/`.

## Install BepInEx

1. Download a BepInEx 6 IL2CPP `win-x64` bleeding-edge archive.
2. Extract the archive contents into the game root, beside
   `SaGa Frontier Remastered.exe`. The result should include `winhttp.dll`,
   `doorstop_config.ini`, and `BepInEx/`.
3. Start the game once. The first IL2CPP launch generates `BepInEx/interop` and
   `BepInEx/unity-libs`; this took a few seconds on the tested build, but allow
   several minutes on a slower machine.
4. Close the game and confirm `BepInEx/interop` contains generated assemblies.

The official installation notes are at
<https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html>.
If interop generation fails on this older Unity version, try an older BepInEx 6
bleeding-edge build rather than the newest one, then update the package pin in
`src/SagaFrontierBridge.Plugin/SagaFrontierBridge.Plugin.csproj` to match.

## Deploy

With the game closed:

```powershell
pwsh ./tools/deploy.ps1 -GamePath "<game-folder>"
```

The script builds the solution and copies the two bridge assemblies to
`BepInEx/plugins/SagaFrontierBridge/`. It refuses to deploy unless the game
executable, `winhttp.dll`, and `BepInEx/plugins` are present.

## Configure

The BepInEx config file is:

```text
BepInEx/config/com.viewtifulslayer.sagafrontier.bridge.cfg
```

| Key | Default | Purpose |
|---|---|---|
| `Enabled` | `true` | Start or skip the bridge. |
| `Port` | `27999` | Loopback port. The server always binds `127.0.0.1`. |
| `AccessToken` | empty | Optional bearer token required on every request. |
| `SaveRoot` | empty | Root to search for `SaveData.bin`. Empty uses `Documents\My Games\SaGa Frontier Remastered\Steam`. |

Set `AccessToken` to a random local value before using the bridge with any agent.
The plugin logs a warning when it is empty, but it does not log the token.
The MCP wrapper reads the same value from `SAGA_BRIDGE_TOKEN`; see
[docs/mcp.md](docs/mcp.md).

The normal save path is:

```text
%USERPROFILE%\Documents\My Games\SaGa Frontier Remastered\Steam\<steamid>\SaveData\SaveData.bin
```

The default save root is the `Steam` directory. Discovery is recursive and
matches the exact filename `SaveData.bin`; it does not read `AppSetting.bin`,
`.bak` files, or arbitrary files.

## Query

```powershell
$token = "replace-with-your-token"
curl.exe -H "Authorization: Bearer $token" http://127.0.0.1:27999/v1/health
curl.exe -H "Authorization: Bearer $token" http://127.0.0.1:27999/v1/save-work
curl.exe -H "Authorization: Bearer $token" http://127.0.0.1:27999/v1/saves
```

Without a token configured, omit the header. The API returns
`401 unauthorized` if a token is configured and the header is missing or wrong.

## Compare live SAVE_WORK with a save slot

`tools/compare_save_work.py` reads one slot from a `SaveData.bin` or JSON
mirror, calls the live `/v1/save-work` route, and reports every field as `OK`,
`DIFF`, or `IGNORE`. It is stdlib-only and never writes the save. If `--save`
is omitted, it uses the single save discovered through `/v1/saves`.

```powershell
$env:SAGA_BRIDGE_TOKEN = "<the same token as BepInEx>"
python tools/compare_save_work.py --slot 3
python tools/compare_save_work.py --slot No.04
python tools/compare_save_work.py --save path\SaveData.bin --slot 3 --ignore-play-time
python tools/compare_save_work.py --slot quick
```

`--slot` is zero-based in `saveDataList`; `No.04` is an alias for index `3`.
`quick` and `auto` select those separate records. `--ignore-play-time` still
prints the three play-time fields but excludes them from the result, which is
useful when the fixture predates the current slot.

The helper exits `0` when every compared field matches, `1` when there are
differences, and `2` for a configuration, transport, or unloaded-save error.
The comparison covers `test`, `isTakeover`, party fields, play time, active team
order, roster count, and the `sum` length and SHA-256.

## Read-only contract

- The listener binds `IPAddress.Loopback` only.
- Non-loopback remote addresses are rejected.
- The `Host` header must be `127.0.0.1` or `localhost`, with the configured port
  when a port is present. This blocks DNS-rebinding requests.
- Only `GET` and `HEAD` are accepted. `POST`, `PUT`, `PATCH`, and `DELETE` return
  `405`.
- Request bodies are rejected. A small body on an unsupported method is drained
  before the error response so the client can read the status.
- There is no endpoint for raw file contents, arbitrary paths, writes, deletes,
  input injection, or process control.
- Save hashing opens files with `FileAccess.Read` and `FileShare.ReadWrite`.
- Responses are `Cache-Control: no-store` and have no CORS headers.

## Repository layout

```text
src/SagaFrontierBridge.Core/      HTTP server, routing, save discovery, contracts
src/SagaFrontierBridge.Plugin/    BepInEx plugin, config, reflection runtime probe
src/SagaFrontierBridge.Mcp/       stdio MCP wrapper over the HTTP surface
tests/SagaFrontierBridge.Core.Tests/  Core integration and documentation tests
tests/SagaFrontierBridge.Mcp.Tests/   Stdio MCP integration tests
tools/                            Build, deploy, and save-work comparison scripts
docs/api.md                       Endpoint contract and examples
docs/mcp.md                       MCP host configuration and tools
docs/save-path.md                 IL2CPP serialization and integrity map
CHANGELOG.md                      Initial version and known limits
```

The core project has no BepInEx or Unity reference. It can be built and tested
without the game. The plugin is the only project that depends on BepInEx. The
MCP tests launch the built server host and do not require the game.

## Current limits

- `/v1/save-work` is a compact summary. `/v1/party` exposes selected `ply`
  fields and equipment, not raw skill arrays or every `savecalcWork` field.
  `/v1/flags` exposes the mapped flag arrays. `/v1/state` exposes current room
  and coordinates, not the `mapwk.obj[]` array.
- `/v1/screenshot` needs a rendered frame. It returns `503` with
  `screenshot_unavailable` or `screenshot_timeout` instead of blocking the
  request indefinitely when the frame hook cannot run.
- The bridge itself does not inflate or parse the save container. The
  comparison helper does so read-only for verification; editing remains the
  domain of the save tool and the Counselor pack.
- The integrity fields (`sfSys.chk*`, `sfSys.sum`, `SAVE_WORK.sum`) are not
  fully understood, so there is deliberately no write path. A live save write
  left `SAVE_WORK.sum` at `[0]` and all `sfSys.chk*`/`sfSys.sum` values
  unchanged. `CompareCheckSum` is a stub in this build, but that is not a write
  authorization.
- The runtime probe is reflection-based. If the interop type or member names
  differ, `/v1/runtime` reports the error instead of crashing the plugin.
- The MCP wrapper exposes ten read-only tools, including
  `bridge_screenshot`.

## Next engineering steps

1. Locate the code path that would fill `SAVE_WORK.sum` and `sfSys.chk*`; the
   live save trace did not populate either, so the fields remain closed.
2. Keep the IL2CPP dump local and refresh it after a game or Unity update.
3. Use the MCP wrapper as the agent entry point; add tools only when a new
   read-only HTTP route exists.

## Related work

- `saga-frontier-save-tool`: the parser and viewer for the same save format.
- Counselor Library `pc/saga-frontier-remastered`: the data map and save lab.
- BepInEx: <https://github.com/BepInEx/BepInEx>
- Il2CppDumper: <https://github.com/Perfare/Il2CppDumper>
- Cpp2IL: <https://github.com/SamboyCoding/Cpp2IL>

## License

MIT. See [LICENSE](LICENSE).
