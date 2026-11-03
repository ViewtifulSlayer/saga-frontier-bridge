# IL2CPP Save Path

Local analysis of the Steam build. This is a map of the serialization and
integrity code, not a write recipe. The bridge remains read-only.

## Provenance

| Item | Value |
|---|---|
| Game | SaGa Frontier Remastered 1.0.1 |
| Engine | Unity 2019.4.16f1, IL2CPP metadata v24 |
| Dumper | Il2CppDumper 6.7.46 (net6 win) |
| Inputs | `GameAssembly.dll`, `global-metadata.dat` |
| Local output | `artifacts/il2cpp/dump/dump.cs`, `script.json`, `il2cpp.h`, `stringliteral.json` |
| Repository policy | The dump, DummyDll, and generated interop stay local and gitignored |

Method addresses below are RVAs from `script.json` and `dump.cs`. The game's
compiled methods are in the PE `il2cpp` section; `.text` is mostly the Unity
runtime.

## Container layer

The disk file is a raw DEFLATE stream (RFC 1951) wrapping UTF-8 JSON. There is
no gzip header, no zlib header, and no trailer. `zlib` and `gunzip` reject it;
`inflateRaw` and `DecompressionStream('deflate-raw')` accept it.

The JSON root is `SaveFileData`:

| Field | Contents |
|---|---|
| `systemSaveData` | `sfSys`, `systemData`, `configData` |
| `saveDataList` | `SaveData` records, one per slot |
| `quickSaveData` | One `SaveData` record |
| `autoSaveData` | One `SaveData` record |

Each `SaveData` has `saveItemInfoData` plus `saveDataJsonStr`. The latter is a
second JSON document stored as a string, not an object.

## Serialization path

| Step | Method | RVA | Confirmed behavior |
|---|---|---|---|
| 1 | `SaveUtility.<NormalSaveToFile>d__5.MoveNext` | `0x1028D20` | Calls `JsonUtility.ToJson` at `0x1028FCD` for the live save work object, clones `SaveFileData`, attaches `GetSaveItemInfoData`, then calls `SaveToFile` |
| 2 | `SaveUtility.<SaveToFile>d__0.MoveNext` | `0x102A4B0` | Builds the destination path, serializes the outer `SaveFileData`, and calls `WriteTextAsync` |
| 3 | `SaveUtility.<WriteTextAsync>d__10.MoveNext` | `0x102AED0` | Awaits `Compress(text)` and writes the returned bytes with a `FileStream` (`FileMode.Create`, `FileAccess.Write`) |
| 4 | `SaveUtility.Compress` | `0xF9BB70` | Async wrapper; the state machine is at `0x1026E00` |
| 5 | `SaveUtility.<Compress>d__83.MoveNext` | `0x1026E00` | `Encoding.UTF8.GetBytes`, `MemoryStream`, `DeflateStream(CompressionMode.Compress, leaveOpen: true)`, `WriteAsync`, then `MemoryStream.ToArray()` |

The `DeflateStream` constructor is at `0x166B440`. That is the engine-level
confirmation that the output is raw DEFLATE, not gzip.

`WriteTextAsync` is misleadingly named. It writes the compressed byte array,
not the input string.

## Load path

| Step | Method | RVA | Confirmed behavior |
|---|---|---|---|
| 1 | `SaveUtility.<LoadFromFile>d__1.MoveNext` | `0x1027B70` | Reads the file bytes and calls `Decompress` |
| 2 | `SaveUtility.Decompress` | `0xF9BC70` | `MemoryStream` + `DeflateStream(CompressionMode.Decompress)` + `Stream.CopyTo` + `MemoryStream.ToArray()` |
| 3 | `SaveUtility.<LoadFromFile>d__1.MoveNext` | `0x1027B70` | Calls `JsonUtility.FromJson<SaveFileData>` at `0x14A10B0` |
| 4 | `MainGame.GetSaveDataString` | `0xE0F580` | Runs `quick_save` and then `JsonUtility.ToJson` |
| 5 | `MainGame.CanParseSaveDataString` | `0xE0F5B0` | Uses `JsonUtility.FromJson` and returns whether the parse produced an object |

`SaveUtility.IsCompressed` at `0xF9BF20` checks whether the first byte is not
`{` and the last byte is not `}`. It is a JSON-shape heuristic, not a gzip or
zlib magic check.

## Integrity path

`MainGame.CompareCheckSum` at `0xCC17F0` is a stub:

```text
0xCC17F0: mov eax, 1
0xCC17F5: ret
```

It unconditionally returns true. A direct-call scan of the `il2cpp` section
found no callers. That does not prove that no other build or reflection path
uses it, but in this build it is not a checksum gate.

System checksum fields are still generated. `MainGame.new_sys_chk` at
`0xE0E2C0` does the following:

1. Reads `_SFSYS` with `_SFSYS.FromBytes` at `0x1170E20`.
2. Uses the MSVC LCG `seed = seed * 0x41C64E6D + 0x3039` to pick a 16-bit value
   and a free index.
3. Writes through `_SFSYS.ToBytes` at `0x11715B0`.
4. Copies 24 bytes from offset `0x19` of the serialized system buffer into the
   object at `[MainGame + 0x318] + 0x10`.
5. Stores the serialized system buffer at `[MainGame + 0x318] + 0xE8`.

`_SFSYS` layout:

| Field | Offset | Type |
|---|---|---|
| `buf` | `0x10` | `sbyte[]` |
| `chk` | `0x18` | `sbyte[]` |
| `name` | `0x20` | `sbyte[]` |
| `chk2` | `0x28` | `sbyte[]` |
| `blood` | `0x30` | `sbyte` |
| `chk3` | `0x38` | `sbyte[]` |
| `seiza` | `0x40` | `sbyte` |
| `learning` | `0x48` | `byte[]` |
| `parLev` | `0x50` | `byte` |
| `chk4` | `0x58` | `sbyte[]` |
| `start` | `0x60` | `uint[]` |
| `end` | `0x68` | `uint[]` |
| `id` | `0x70` | `ushort[]` |
| `end2` | `0x78` | `uint[]` |
| `fill` | `0x80` | `sbyte[]` |
| `sum` | `0x88` | `sbyte[]` |

The inner JSON root is `MainGame.SAVE_WORK`, which has `sum` as a `byte[]` at
offset `0x40`. The code that fills that `sum` was not located in this pass.
`sfSys.chk*`, `sfSys.sum`, and `SAVE_WORK.sum` should be treated as separate
fields until a live write trace shows their relationship.

## Live save-write trace

On 2026-11-03 the bridge was used with Asellus slot No.04 loaded. The current
`SaveData.bin` was copied and inflated, then the game wrote a new save to UI
slot No.12 (`saveDataList[11]`). For the observed rows, UI No.04 maps to `[3]`,
No.11 to `[10]`, and No.12 to `[11]` on this build. The file grew from 80,080 to 86,589 bytes (payload SHA-256 `f5160f2e...` to
`674be9d9...`). The live `/v1/save-work` was queried before and after the write.

| Field | Before | After |
|---|---|---|
| `SAVE_WORK.sum` (live) | `[0]` | `[0]` |
| new slot `saveDataJsonStr.sum` | absent | `[0]` |
| `sfSys.chk` | `[84]` | `[84]` |
| `sfSys.chk2` | `[0, 0, 1]` | `[0, 0, 1]` |
| `sfSys.chk3` | `[0, 1, 0]` | `[0, 1, 0]` |
| `sfSys.chk4` | `[1, 5]` | `[1, 5]` |
| `sfSys.sum` | `[0]` | `[0]` |
| `sfSys.buf` | 24 bytes | unchanged |

No `sfSys` field changed, and no existing slot's `sum` changed. The new slot was
the only inner state added. This build's normal save path did not call
`new_sys_chk`: the function exists at `0xE0E2C0`, but the serialized system
record was preserved byte for byte. That is consistent with a system-data update
path that is separate from `ext_save_data`/`quick_save`, not proof that
`new_sys_chk` is unreachable.

The trace also separates the three fields:

- `SAVE_WORK.sum` is the live inner object's byte array. It stayed one zero
  byte through a normal save, so it is not a generated save checksum in this
  path.
- `sfSys.sum` is the system record's byte array. It also stayed `[0]`.
- `sfSys.chk*` are the system-record pieces. They can be non-zero from prior
  state but were not recomputed by this save.

## Implications

- The save tool's `inflateRaw` behavior is engine-confirmed, not just observed.
- The `chk*` fields are not a simple equality gate in this build; `CompareCheckSum`
  is stubbed. Do not turn that into a write path without a live before/after
  trace.
- The bridge's `/v1/save-work` probe reads the live `SAVE_WORK` object
  (`MainGame.SAVEWK_ADR`, with `save` and `takeoverData` as fallbacks).
  Re-inflating `SaveData.bin` is a load-path check, not live state.
- `/v1/state` reads `mapwk.imfName` for the current room and `eventwk.xpos`,
  `zpos`, and `dir` for position. `mapwk.map_name` is an area-table index, and
  `eventwk.mapNo` is the previous map, not the current room.
- A loaded Asellus slot (No.04, `saveDataList[3]`) reports `SAVE_WORK.sum` as
  `[0]`, matching the file. A normal save to a new slot still left it `[0]`, so
  it is not usable as a write gate in this build.
- `tools/compare_save_work.py` automates the live-vs-slot comparison. Use
  `--ignore-play-time` when checking a fixture that predates the current save,
  because play time advances independently of the mapped fields.
- The bridge stays read-only. No endpoint writes, compresses, or replaces a
  save.

## Open questions

- What path would populate `SAVE_WORK.sum`; a normal save did not.
- What triggers `new_sys_chk` and `update_sys_data`; this save did not change
  any `sfSys.chk*` value.
- Whether `CompareCheckSum` is reached through a delegate, reflection, or
  platform-specific path in another build.
- The exact meaning of `chk`, `chk2`, `chk3`, and `chk4`.
- Whether `sfSys.sum` is a checksum over `_SFSYS` or over the outer save file.
