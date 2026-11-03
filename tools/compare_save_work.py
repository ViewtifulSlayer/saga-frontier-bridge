#!/usr/bin/env python3
"""Compare the live SAVE_WORK summary with one save-file slot.

The live side comes from the bridge's GET /v1/save-work route. The fixture
side comes from a raw SaveData.bin (DEFLATE) or an inflated JSON mirror.

Examples:

    python tools/compare_save_work.py --slot 3 --ignore-play-time
    python tools/compare_save_work.py --save fixture.bin --slot 3
    python tools/compare_save_work.py --save SaveData.bin --slot No.04

The slot is zero-based in saveDataList unless written as "No.04". "quick" and
"auto" select quickSaveData and autoSaveData. The helper is read-only.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
import zlib
from pathlib import Path
from typing import Any

DEFAULT_URL = "http://127.0.0.1:27999"
PLAY_TIME_FIELDS = {
    "playTimeHours",
    "playTimeMinutes",
    "playTimeSeconds",
}

FIELDS = [
    "test",
    "isTakeover",
    "psnCnt",
    "heroNo",
    "name",
    "name2",
    "money",
    "parLev",
    "playTimeHours",
    "playTimeMinutes",
    "playTimeSeconds",
    "partyCharacterIds",
    "rosterCount",
    "sumLength",
    "sumSha256",
]


def fail(message: str) -> None:
    print(f"error: {message}", file=sys.stderr)
    raise SystemExit(2)


def load_document(path: Path) -> dict[str, Any]:
    try:
        raw = path.read_bytes()
    except OSError as exc:
        fail(f"{path}: cannot read save ({exc})")

    stripped = raw.lstrip()
    if stripped.startswith(b"{"):
        payload = raw
    else:
        try:
            payload = zlib.decompress(raw, -15)
        except zlib.error as exc:
            fail(f"{path}: not raw DEFLATE and not a JSON mirror ({exc})")

    try:
        document = json.loads(payload.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        fail(f"{path}: payload is not UTF-8 JSON ({exc})")

    if not isinstance(document, dict):
        fail(f"{path}: root JSON is not an object")
    return document


def select_slot(
    document: dict[str, Any],
    slot: str,
) -> tuple[dict[str, Any], str]:
    value = slot.strip().lower()
    if value in ("quick", "auto"):
        key = "quickSaveData" if value == "quick" else "autoSaveData"
        entry = document.get(key)
        if not entry:
            fail(f"{key} is empty")
        label = key
    else:
        match = re.fullmatch(r"no\.?\s*(\d+)", value)
        if match:
            index = int(match.group(1)) - 1
            label = f"saveDataList[{index}] (No.{index + 1:02d})"
        elif value.isdigit():
            index = int(value)
            label = f"saveDataList[{index}] (No.{index + 1:02d})"
        else:
            fail(
                f"invalid slot {slot!r}; use a zero-based index, No.04, quick, or auto"
            )

        slots = document.get("saveDataList")
        if not isinstance(slots, list) or not (0 <= index < len(slots)):
            fail(f"slot {slot!r} is outside saveDataList")
        entry = slots[index]
        if not entry:
            fail(f"{label} is empty")

    blob = entry.get("saveDataJsonStr")
    if not isinstance(blob, str) or not blob:
        fail(f"{label} has no saveDataJsonStr")

    try:
        inner = json.loads(blob)
    except json.JSONDecodeError as exc:
        fail(f"{label} saveDataJsonStr is not JSON: {exc}")
    if not isinstance(inner, dict):
        fail(f"{label} saveDataJsonStr is not an object")
    return inner, label


def extract_fixture(inner: dict[str, Any]) -> dict[str, Any]:
    party = inner.get("pty")
    if not isinstance(party, dict):
        fail("selected slot has no pty object")

    each = party.get("each")
    par_lev = each.get("parLev") if isinstance(each, dict) else None
    team = party.get("team")
    if not isinstance(team, list):
        fail("selected slot has no pty.team array")
    chr_no = party.get("chrNo")
    if not isinstance(chr_no, list):
        fail("selected slot has no pty.chrNo array")
    sum_values = inner.get("sum")
    if not isinstance(sum_values, list):
        fail("selected slot has no sum array")

    try:
        sum_bytes = bytes(int(value) for value in sum_values)
    except (TypeError, ValueError) as exc:
        fail(f"selected slot has an invalid sum array: {exc}")

    return {
        "test": inner.get("test"),
        "isTakeover": inner.get("isTakeover"),
        "psnCnt": party.get("psnCnt"),
        "heroNo": party.get("heroNo"),
        "name": party.get("name"),
        "name2": party.get("name2"),
        "money": party.get("money"),
        "parLev": par_lev,
        "playTimeHours": party.get("PlayTimeHours"),
        "playTimeMinutes": party.get("PlayTimeMinutes"),
        "playTimeSeconds": party.get("PlayTimeSeconds"),
        "partyCharacterIds": [value for value in team if value != 255],
        "rosterCount": sum(1 for value in chr_no if value != 255),
        "sumLength": len(sum_bytes),
        "sumSha256": hashlib.sha256(sum_bytes).hexdigest(),
    }


def extract_live(live: dict[str, Any]) -> dict[str, Any]:
    party = live.get("party")
    if not isinstance(party, dict):
        party = {}
    return {
        "test": live.get("test"),
        "isTakeover": live.get("isTakeover"),
        "psnCnt": party.get("psnCnt"),
        "heroNo": party.get("heroNo"),
        "name": party.get("name"),
        "name2": party.get("name2"),
        "money": party.get("money"),
        "parLev": party.get("parLev"),
        "playTimeHours": party.get("playTimeHours"),
        "playTimeMinutes": party.get("playTimeMinutes"),
        "playTimeSeconds": party.get("playTimeSeconds"),
        "partyCharacterIds": live.get("partyCharacterIds"),
        "rosterCount": live.get("rosterCount"),
        "sumLength": live.get("sumLength"),
        "sumSha256": live.get("sumSha256"),
    }


def normalize_base_url(value: str) -> str:
    parsed = urllib.parse.urlparse(value.strip())
    if parsed.scheme != "http":
        fail("bridge URL must use http")
    if parsed.hostname not in {"127.0.0.1", "localhost"}:
        fail("bridge URL must target loopback")
    if parsed.username or parsed.password or parsed.query or parsed.fragment:
        fail("bridge URL must not contain credentials, query, or fragment")
    if parsed.path not in ("", "/"):
        fail("bridge URL must not contain a path")

    try:
        port = parsed.port or 27999
    except ValueError as exc:
        fail(f"invalid bridge port: {exc}")

    return f"http://{parsed.hostname}:{port}"


def fetch_json(base: str, path: str, token: str, timeout: float) -> dict[str, Any]:
    headers = {"Accept": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(f"{base}/{path.lstrip('/')}", headers=headers)

    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            payload = response.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace")
        fail(f"bridge returned HTTP {exc.code}: {body}")
    except urllib.error.URLError as exc:
        fail(f"bridge is unreachable at {base}: {exc.reason}")

    try:
        value = json.loads(payload)
    except json.JSONDecodeError as exc:
        fail(f"bridge returned non-JSON: {exc}")
    if not isinstance(value, dict):
        fail("bridge response is not a JSON object")
    return value


def resolve_save(
    args: argparse.Namespace,
    base: str,
    token: str,
) -> Path:
    if args.save and args.save_id:
        fail("use --save or --save-id, not both")
    if args.save:
        return Path(args.save)

    catalog = fetch_json(base, "v1/saves", token, args.timeout)
    saves = catalog.get("saves")
    if not isinstance(saves, list) or not saves:
        fail("the bridge found no SaveData.bin files; pass --save")

    if args.save_id:
        matches = [item for item in saves if item.get("id") == args.save_id]
        if not matches:
            fail(f"save id {args.save_id!r} was not found")
        return Path(matches[0]["path"])

    if len(saves) != 1:
        ids = ", ".join(str(item.get("id")) for item in saves)
        fail(
            f"the bridge found {len(saves)} saves ({ids}); pass --save or --save-id"
        )
    return Path(saves[0]["path"])


def display(value: Any) -> str:
    if isinstance(value, str):
        return f'"{value}"'
    if isinstance(value, list):
        return json.dumps(value, ensure_ascii=False, separators=(",", ":"))
    return str(value)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--save",
        help="SaveData.bin or JSON mirror. If omitted, use the bridge's single save.",
    )
    parser.add_argument(
        "--save-id",
        help="Bridge save id to locate through GET /v1/saves.",
    )
    parser.add_argument(
        "--slot",
        required=True,
        help="Zero-based saveDataList index, No.04, quick, or auto.",
    )
    parser.add_argument(
        "--ignore-play-time",
        action="store_true",
        help="Report play-time fields but exclude them from the result.",
    )
    parser.add_argument(
        "--url",
        default=os.environ.get("SAGA_BRIDGE_URL", DEFAULT_URL),
        help="Bridge base URL (default: SAGA_BRIDGE_URL or %(default)s).",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=10.0,
        help="HTTP timeout in seconds (default: 10).",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    args = build_parser().parse_args(argv)
    base = normalize_base_url(args.url)
    token = os.environ.get("SAGA_BRIDGE_TOKEN", "").strip()

    save_path = resolve_save(args, base, token)
    document = load_document(save_path)
    inner, label = select_slot(document, args.slot)
    expected = extract_fixture(inner)

    live = fetch_json(base, "v1/save-work", token, args.timeout)
    if not live.get("available"):
        fail(
            "live SAVE_WORK is unavailable: "
            + str(live.get("error") or "no error detail")
        )
    if not live.get("saveLoaded"):
        fail("live SAVE_WORK is not loaded; load the save in-game first")
    actual = extract_live(live)

    ignored = PLAY_TIME_FIELDS if args.ignore_play_time else set()
    compared = [field for field in FIELDS if field not in ignored]
    matches = 0
    diffs: list[tuple[str, Any, Any]] = []

    print(f"fixture: {save_path}")
    print(f"slot:    {label}")
    print(f"live:    {base}/v1/save-work")
    print()
    print(f"{'field':22} {'fixture':46} {'live':46} result")
    print("-" * 122)

    for field in FIELDS:
        expected_value = expected.get(field)
        actual_value = actual.get(field)
        if field in ignored:
            status = "IGNORE"
        elif expected_value == actual_value:
            matches += 1
            status = "OK"
        else:
            diffs.append((field, expected_value, actual_value))
            status = "DIFF"

        print(
            f"{field:22} {display(expected_value)[:46]:46} "
            f"{display(actual_value)[:46]:46} {status}"
        )

    print()
    print(
        f"summary: {matches}/{len(compared)} matched, "
        f"{len(diffs)} diff, {len(ignored)} ignored"
    )
    if diffs:
        print("RESULT: DIFF")
        return 1

    print("RESULT: PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
