from __future__ import annotations

import argparse
import codecs
import json
import os
import stat
import sys
import tempfile
import tomllib
from pathlib import Path


SKILL_NAME = "sol-luna-delegation"
ECONOMY_MODEL = "gpt-5.6-luna"
ECONOMY_EFFORT = "max"
ACTIVE_MODES = ("on", "ask")

AGENT_BEGIN = "# BEGIN sol-luna-delegation economy agent defaults"
AGENT_END = "# END sol-luna-delegation economy agent defaults"
SKILL_BEGIN = "# BEGIN sol-luna-delegation economy skill switch"
SKILL_END = "# END sol-luna-delegation economy skill switch"


class ConfigConflict(RuntimeError):
    pass


def _default_config_path() -> Path:
    codex_home = os.environ.get("CODEX_HOME")
    root = Path(codex_home).expanduser() if codex_home else Path.home() / ".codex"
    return root / "config.toml"


def _default_skill_path() -> Path:
    installed = _default_config_path().parent / "skills" / SKILL_NAME / "SKILL.md"
    return installed if installed.is_file() else Path(__file__).resolve().parents[1] / "SKILL.md"


def _read_config(path: Path) -> tuple[str, str, bool, int | None, bytes | None]:
    if not path.exists():
        return "", os.linesep, False, None, None

    raw = path.read_bytes()
    has_bom = raw.startswith(codecs.BOM_UTF8)
    text = raw.decode("utf-8-sig")
    crlf = text.count("\r\n")
    bare_lf = text.count("\n") - crlf
    newline = "\r\n" if crlf > bare_lf else "\n"
    normalized = text.replace("\r\n", "\n").replace("\r", "\n")
    return normalized, newline, has_bom, stat.S_IMODE(path.stat().st_mode), raw


def _starts_with_triple(text: str, offset: int, quote: str) -> bool:
    return text.startswith(quote * 3, offset)


def _advance_multiline_state(line: str, state: str | None) -> str | None:
    in_basic = False
    in_literal = False
    index = 0
    while index < len(line):
        if state == "basic":
            if _starts_with_triple(line, index, '"'):
                state = None
                index += 3
            elif line[index] == "\\":
                index = min(len(line), index + 2)
            else:
                index += 1
            continue
        if state == "literal":
            if _starts_with_triple(line, index, "'"):
                state = None
                index += 3
            else:
                index += 1
            continue
        if in_basic:
            if line[index] == "\\":
                index = min(len(line), index + 2)
            else:
                if line[index] == '"':
                    in_basic = False
                index += 1
            continue
        if in_literal:
            if line[index] == "'":
                in_literal = False
            index += 1
            continue
        if line[index] == "#":
            break
        if _starts_with_triple(line, index, '"'):
            state = "basic"
            index += 3
        elif _starts_with_triple(line, index, "'"):
            state = "literal"
            index += 3
        elif line[index] == '"':
            in_basic = True
            index += 1
        elif line[index] == "'":
            in_literal = True
            index += 1
        else:
            index += 1
    return state


def _lex_lines(text: str) -> list[tuple[int, int, str, bool]]:
    lines: list[tuple[int, int, str, bool]] = []
    state: str | None = None
    start = 0
    while start <= len(text):
        newline = text.find("\n", start)
        end = len(text) if newline < 0 else newline
        line = text[start:end]
        outside_at_start = state is None
        state = _advance_multiline_state(line, state)
        lines.append((start, end if newline < 0 else end + 1, line, outside_at_start))
        if newline < 0:
            break
        start = newline + 1
    if state is not None:
        raise ConfigConflict("config.toml contains an unterminated multiline string")
    return lines


def _find_owned_block(text: str, begin: str, end: str) -> tuple[int, int, str] | None:
    lines = _lex_lines(text)
    begins = [line for line in lines if line[3] and line[2].strip() == begin]
    ends = [line for line in lines if line[3] and line[2].strip() == end]
    if len(begins) != len(ends) or len(begins) > 1:
        raise ConfigConflict(f"malformed managed block: {begin}")
    if not begins:
        return None
    begin_line = begins[0]
    end_line = ends[0]
    if end_line[0] < begin_line[1]:
        raise ConfigConflict(f"malformed managed block: {begin}")
    return begin_line[0], end_line[1], text[begin_line[1] : end_line[0]]


def _remove_owned_block(text: str, begin: str, end: str) -> str:
    block = _find_owned_block(text, begin, end)
    return text if block is None else text[: block[0]] + text[block[1] :]


def _without_owned_blocks(text: str) -> str:
    text = _remove_owned_block(text, AGENT_BEGIN, AGENT_END)
    return _remove_owned_block(text, SKILL_BEGIN, SKILL_END)


def _parse(text: str) -> dict[str, object]:
    try:
        return tomllib.loads(text)
    except tomllib.TOMLDecodeError as exc:
        raise ConfigConflict(f"config.toml is invalid: {exc}") from exc


def _same_path(left: object, right: Path) -> bool:
    if not isinstance(left, str):
        return False
    try:
        return os.path.normcase(os.path.abspath(os.path.expanduser(left))) == os.path.normcase(
            os.path.abspath(right)
        )
    except (OSError, TypeError, ValueError):
        return False


def _entry_targets_skill(entry: dict[str, object], skill_path: Path) -> bool:
    name = entry.get("name")
    return (isinstance(name, str) and name.strip() == SKILL_NAME) or _same_path(
        entry.get("path"), skill_path
    )


def _check_unmanaged_conflicts(
    data: dict[str, object], skill_path: Path, mode: str
) -> None:
    agents = data.get("agents", {})
    if not isinstance(agents, dict):
        raise ConfigConflict("[agents] must be a TOML table")
    if mode in ACTIVE_MODES and agents.get("enabled") is False:
        raise ConfigConflict(
            "native agents are disabled by unmanaged [agents].enabled = false"
        )
    if mode == "on":
        for key in ("default_subagent_model", "default_subagent_reasoning_effort"):
            if key in agents:
                raise ConfigConflict(f"unmanaged [agents].{key} already exists")

    skills = data.get("skills", {})
    if not isinstance(skills, dict):
        raise ConfigConflict("[skills] must be a TOML table")
    entries = skills.get("config", [])
    if entries is None:
        entries = []
    if not isinstance(entries, list):
        raise ConfigConflict("skills.config must be an array of tables")
    for entry in entries:
        if isinstance(entry, dict) and _entry_targets_skill(entry, skill_path):
            raise ConfigConflict(
                f"an unmanaged skills.config entry already targets {SKILL_NAME}"
            )


def _append_section(text: str, section: str) -> str:
    if not text:
        return f"{section}\n"
    separator = "" if text.endswith("\n\n") else "\n" if text.endswith("\n") else "\n\n"
    return f"{text}{separator}{section}\n"


def _find_agents_table_content_start(text: str) -> int | None:
    for _, next_start, raw_line, outside_at_start in _lex_lines(text):
        if not outside_at_start:
            continue
        line = raw_line.strip()
        if not line.startswith("[") or line.startswith("[["):
            continue
        try:
            header = _parse(f"{line}\n")
        except ConfigConflict:
            continue
        if set(header) == {"agents"} and header.get("agents") == {}:
            return next_start
    return None


def _add_agent_defaults(text: str) -> str:
    block = (
        f"{AGENT_BEGIN}\n"
        f'default_subagent_model = "{ECONOMY_MODEL}"\n'
        f'default_subagent_reasoning_effort = "{ECONOMY_EFFORT}"\n'
        f"{AGENT_END}"
    )
    insertion = _find_agents_table_content_start(text)
    if insertion is not None:
        prefix = "" if insertion > 0 and text[insertion - 1] == "\n" else "\n"
        return f"{text[:insertion]}{prefix}{block}\n{text[insertion:]}"
    return _append_section(text, f"[agents]\n{block}")


def _add_skill_switch(text: str, skill_path: Path, enabled: bool) -> str:
    encoded_path = json.dumps(str(skill_path.resolve()), ensure_ascii=False)
    block = (
        f"{SKILL_BEGIN}\n"
        "[[skills.config]]\n"
        f"path = {encoded_path}\n"
        f"enabled = {'true' if enabled else 'false'}\n"
        f"{SKILL_END}"
    )
    return _append_section(text, block)


def update_text(text: str, skill_path: Path, mode: str) -> str:
    if mode not in (*ACTIVE_MODES, "off"):
        raise ValueError(f"unsupported economy mode: {mode}")
    current_status = inspect_status(text, skill_path)
    unmanaged = _without_owned_blocks(text)
    data = _parse(unmanaged)
    _check_unmanaged_conflicts(data, skill_path, mode=mode)
    if current_status == "inconsistent":
        raise ConfigConflict(
            "current economy mode configuration is inconsistent; inspect it before changing modes"
        )

    updated = unmanaged
    if mode == "on":
        updated = _add_agent_defaults(updated)
    updated = _add_skill_switch(updated, skill_path, mode in ACTIVE_MODES)
    _parse(updated)
    candidate = inspect_status(updated, skill_path)
    if candidate != mode:
        raise ConfigConflict(
            f"economy mode preflight mismatch: expected {mode}, got {candidate}"
        )
    return updated


def inspect_status(text: str, skill_path: Path) -> str:
    try:
        _parse(text)
        agent_block = _find_owned_block(text, AGENT_BEGIN, AGENT_END)
        skill_block = _find_owned_block(text, SKILL_BEGIN, SKILL_END)
        if (
            agent_block is not None
            and skill_block is not None
            and agent_block[0] < skill_block[1]
            and skill_block[0] < agent_block[1]
        ):
            return "inconsistent"
        unmanaged = _without_owned_blocks(text)
        data = _parse(unmanaged)
    except ConfigConflict:
        return "inconsistent"
    agents = data.get("agents", {})
    skills = data.get("skills", {})
    if not isinstance(agents, dict) or not isinstance(skills, dict):
        return "inconsistent"
    entries = skills.get("config", [])
    if entries is None:
        entries = []
    if not isinstance(entries, list) or any(not isinstance(entry, dict) for entry in entries):
        return "inconsistent"
    unmanaged_matching = [
        entry
        for entry in entries
        if _entry_targets_skill(entry, skill_path)
    ]
    if unmanaged_matching:
        return "inconsistent"
    if agent_block is None and skill_block is None:
        return "unconfigured"
    if skill_block is None:
        return "inconsistent"

    try:
        managed_skill = _parse(skill_block[2])
        managed_skills = managed_skill.get("skills", {})
        managed_entries = managed_skills.get("config", []) if isinstance(managed_skills, dict) else []
        if (
            not isinstance(managed_entries, list)
            or len(managed_entries) != 1
            or not isinstance(managed_entries[0], dict)
            or not _entry_targets_skill(managed_entries[0], skill_path)
        ):
            return "inconsistent"
        enabled = managed_entries[0].get("enabled")
        if agent_block is None:
            if enabled is False:
                return "off"
            if enabled is True and agents.get("enabled") is not False:
                return "ask"
            return "inconsistent"

        managed_agent_data = _parse(f"[agents]\n{agent_block[2]}")
        managed_agents = managed_agent_data.get("agents", {})
        if not isinstance(managed_agents, dict):
            return "inconsistent"
        if (
            enabled is True
            and "enabled" not in managed_agents
            and managed_agents.get("default_subagent_model") == ECONOMY_MODEL
            and managed_agents.get("default_subagent_reasoning_effort") == ECONOMY_EFFORT
            and agents.get("enabled") is not False
        ):
            return "on"
    except ConfigConflict:
        return "inconsistent"
    return "inconsistent"


def _current_bytes(path: Path) -> bytes | None:
    return path.read_bytes() if path.exists() else None


def _write_atomic(
    path: Path,
    text: str,
    newline: str,
    bom: bool,
    mode: int | None,
    expected: bytes | None,
) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if _current_bytes(path) != expected:
        raise ConfigConflict("config changed during update; retry the command")
    normalized = text if text.endswith("\n") else f"{text}\n"
    rendered = normalized.replace("\n", newline)
    payload = rendered.encode("utf-8")
    if bom:
        payload = codecs.BOM_UTF8 + payload

    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.", suffix=".tmp", dir=path.parent
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        if mode is not None:
            os.chmod(temporary, mode)
        if _current_bytes(path) != expected:
            raise ConfigConflict("config changed during update; retry the command")
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Enable, advise on, disable, or inspect global subagent economy mode."
    )
    parser.add_argument("mode", choices=("on", "ask", "off", "status"))
    parser.add_argument("--config", type=Path, default=_default_config_path())
    parser.add_argument("--skill-path", type=Path, default=_default_skill_path())
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    config_path = args.config.expanduser().resolve()
    skill_path = args.skill_path.expanduser().resolve()
    if skill_path.name.casefold() != "skill.md":
        skill_path = skill_path / "SKILL.md"

    try:
        text, newline, bom, mode, original = _read_config(config_path)
        if args.mode == "status":
            status = inspect_status(text, skill_path)
            print(status)
            return 2 if status == "inconsistent" else 0

        updated = update_text(text, skill_path, mode=args.mode)
        if updated != text:
            _write_atomic(config_path, updated, newline, bom, mode, original)
        status = inspect_status(updated, skill_path)
        print(status)
        return 0 if status == args.mode else 2
    except (ConfigConflict, OSError, UnicodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
