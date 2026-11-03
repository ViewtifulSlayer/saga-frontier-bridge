# Changelog

All notable changes to the SaGa Frontier Bridge are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This project uses semantic versioning once tagged releases exist.

## [0.1.0] - 2026-11-03

Initial commit. This is the first repository version; there is no earlier tagged
release to compare against.

### Added

- A read-only loopback HTTP server bound to `127.0.0.1`, with optional bearer
  token authentication, `GET`/`HEAD` only, and no request bodies.
- A BepInEx 6 IL2CPP plugin that starts the bridge inside *SaGa Frontier
  Remastered* and reads live state through the game's generated interop
  assemblies.
- `GET /v1/health`, `/v1/runtime`, `/v1/save-work`, `/v1/party`, `/v1/flags`,
  `/v1/state`, `/v1/saves`, `/v1/saves/{id}`, and `/v1/capabilities`.
- `GET /v1/screenshot`, returning a binary PNG captured on the Unity main
  thread. Capture and encoding are split across two frames and do not block the
  loopback listener.
- A dependency-free stdio MCP server exposing ten read-only tools over the HTTP
  surface, including `bridge_screenshot` as MCP image content.
- A stdlib-only `tools/compare_save_work.py` helper for comparing live
  `SAVE_WORK` with a chosen save slot.
- Core integration tests plus process-level MCP tests covering initialize,
  tools/list, screenshot image content, newline and malformed framing, invalid
  JSON, uninitialized requests, malformed tool arguments, 401 propagation,
  non-image screenshot errors, and bridge timeouts.
- Documentation consistency tests that compare the bridge, game, Unity, and
  BepInEx versions across the README, docs, and plugin project.
- API and MCP documentation, and a Windows CI build workflow.

### Security and scope

- The bridge is read-only: no endpoint writes a file, changes game state,
  accepts arbitrary paths, or returns save-file bytes.
- The screenshot route returns a live rendered image, not a game file.
- The game, BepInEx installation, IL2CPP dump, generated interop, and local
  artifacts are not part of this repository.

### Known limits

- `SAVE_WORK.sum`, `sfSys.sum`, and `sfSys.chk*` are not fully understood, so
  there is intentionally no write endpoint.
- Live probes depend on generated interop member names and are best-effort.
- Screenshot capture depends on `UnityEngine.Application.InvokeOnBeforeRender`
  in this build; a future build can make the route return `503`.
