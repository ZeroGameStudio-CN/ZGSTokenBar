#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path
from typing import Any, Iterable, Sequence
from uuid import UUID


EXPECTED_MODEL = "gpt-5.6-luna"
EXPECTED_EFFORT = "max"


class RuntimeProofError(ValueError):
    pass


class RuntimeProofPending(RuntimeProofError):
    pass


def _read_entries(path: Path) -> list[tuple[int, dict[str, Any]]]:
    entries: list[tuple[int, dict[str, Any]]] = []
    try:
        with path.open(encoding="utf-8-sig") as stream:
            for line_number, raw_line in enumerate(stream, start=1):
                if not raw_line.strip():
                    continue
                value = json.loads(raw_line)
                if isinstance(value, dict) and value.get("type") in {
                    "session_meta", "turn_context", "event_msg",
                    "inter_agent_communication_metadata",
                }:
                    entries.append((line_number, value))
    except (OSError, json.JSONDecodeError) as exc:
        raise RuntimeProofError(f"invalid session record {path}: {exc}") from exc
    return entries


def _child_metadata(
    entries: Iterable[tuple[int, dict[str, Any]]],
) -> tuple[int, dict[str, Any]]:
    for line_number, entry in entries:
        if entry.get("type") != "session_meta":
            continue
        payload = entry.get("payload")
        if (
            isinstance(payload, dict)
            and payload.get("agent_path")
            and payload.get("parent_thread_id")
        ):
            return line_number, payload
    raise RuntimeProofError("session record does not contain child metadata")


def _event_type(entry: dict[str, Any]) -> str | None:
    if entry.get("type") != "event_msg":
        return None
    payload = entry.get("payload")
    return payload.get("type") if isinstance(payload, dict) else None


def inspect_session(
    path: Path,
    parent_thread_id: str,
    session_id: str | None = None,
    agent_path: str | None = None,
) -> dict[str, Any]:
    entries = _read_entries(path)
    metadata_line, metadata = _child_metadata(entries)
    if metadata.get("parent_thread_id") != parent_thread_id:
        raise RuntimeProofError("child record belongs to a different parent task")
    if session_id is not None and metadata.get("id") != session_id:
        raise RuntimeProofError("child record has a different session identity")
    if agent_path is not None and metadata.get("agent_path") != agent_path:
        raise RuntimeProofError("child record has a different agent path")
    inherited_history = any(
        line_number > metadata_line
        and entry.get("type") == "session_meta"
        and isinstance(entry.get("payload"), dict)
        and not entry["payload"].get("agent_path")
        for line_number, entry in entries
    )
    settings_lines = [
        line_number
        for line_number, entry in entries
        if _event_type(entry) == "thread_settings_applied"
    ]
    if inherited_history and not settings_lines:
        raise RuntimeProofPending("child settings have not been applied yet")

    boundary_line = max(settings_lines, default=metadata_line)
    task_lines = [
        line_number
        for line_number, entry in entries
        if line_number > boundary_line and _event_type(entry) == "task_started"
    ]
    if not task_lines:
        raise RuntimeProofPending("child task has not started yet")
    task_line = max(task_lines)

    trigger_lines = [
        line_number
        for line_number, entry in entries
        if line_number > task_line
        and entry.get("type") == "inter_agent_communication_metadata"
        and isinstance(entry.get("payload"), dict)
        and entry["payload"].get("trigger_turn") is True
    ]
    if not trigger_lines:
        raise RuntimeProofPending("child task context is not available yet")
    trigger_line = max(trigger_lines)

    contexts = [
        (line_number, entry["payload"])
        for line_number, entry in entries
        if task_line < line_number < trigger_line
        and entry.get("type") == "turn_context"
        and isinstance(entry.get("payload"), dict)
    ]
    if not contexts:
        raise RuntimeProofPending("child turn_context is not available yet")
    context_line, context = contexts[-1]
    collaboration = context.get("collaboration_mode")
    settings = (
        collaboration.get("settings", {})
        if isinstance(collaboration, dict)
        else {}
    )
    model = context.get("model") or settings.get("model")
    effort = context.get("effort") or settings.get("reasoning_effort")
    if not isinstance(model, str) or not isinstance(effort, str):
        raise RuntimeProofError("child turn_context lacks model or effort")

    return {
        "session": str(path.resolve()),
        "session_id": metadata.get("id"),
        "parent_thread_id": metadata.get("parent_thread_id"),
        "agent_path": metadata.get("agent_path"),
        "nickname": metadata.get("agent_nickname"),
        "role": metadata.get("agent_role"),
        "task_started_line": task_line,
        "turn_context_line": context_line,
        "model": model,
        "effort": effort,
    }


def _default_sessions_root() -> Path:
    codex_home = os.environ.get("CODEX_HOME")
    return Path(codex_home) / "sessions" if codex_home else Path.home() / ".codex" / "sessions"


def find_session(sessions_root: Path, session_id: str, parent_thread_id: str) -> Path:
    try:
        canonical_id = str(UUID(session_id))
    except ValueError as exc:
        raise RuntimeProofError("session-id must be the native child UUID") from exc
    matches: list[Path] = []
    try:
        candidates = sessions_root.rglob(f"*{canonical_id}.jsonl")
        for path in candidates:
            try:
                with path.open(encoding="utf-8-sig") as stream:
                    first_line = stream.readline()
                entry = json.loads(first_line)
            except (OSError, json.JSONDecodeError):
                continue
            payload = entry.get("payload") if isinstance(entry, dict) else None
            if (
                isinstance(entry, dict)
                and entry.get("type") == "session_meta"
                and isinstance(payload, dict)
                and payload.get("id") == canonical_id
            ):
                if payload.get("parent_thread_id") != parent_thread_id:
                    raise RuntimeProofError("child record belongs to a different parent task")
                matches.append(path)
    except OSError as exc:
        raise RuntimeProofError(f"cannot search sessions root {sessions_root}: {exc}") from exc
    if not matches:
        raise RuntimeProofPending(f"no session record found for {canonical_id}")
    if len(matches) != 1:
        raise RuntimeProofError("multiple records match this child; supply the exact --session path")
    return matches[0]


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Verify the current runtime of one native Codex subagent"
    )
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--session", type=Path)
    source.add_argument("--session-id")
    parser.add_argument("--parent-thread-id", required=True)
    parser.add_argument("--agent-path")
    parser.add_argument("--sessions-root", type=Path, default=_default_sessions_root())
    parser.add_argument("--expected-model", default=EXPECTED_MODEL)
    parser.add_argument("--expected-effort", default=EXPECTED_EFFORT)
    return parser


def _emit(status: str, **values: Any) -> None:
    print(json.dumps({"status": status, **values}, ensure_ascii=False, sort_keys=True))


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        session_id = str(UUID(args.session_id)) if args.session_id else None
        path = args.session or find_session(args.sessions_root, session_id, args.parent_thread_id)
        proof = inspect_session(path, args.parent_thread_id, session_id, args.agent_path)
    except RuntimeProofPending as exc:
        _emit("pending", reason=str(exc))
        return 1
    except (RuntimeProofError, ValueError) as exc:
        _emit("error", reason=str(exc))
        return 2

    matches = (
        proof["model"] == args.expected_model
        and proof["effort"] == args.expected_effort
    )
    _emit(
        "verified" if matches else "mismatch",
        expected_model=args.expected_model,
        expected_effort=args.expected_effort,
        **proof,
    )
    return 0 if matches else 3


if __name__ == "__main__":
    raise SystemExit(main())
