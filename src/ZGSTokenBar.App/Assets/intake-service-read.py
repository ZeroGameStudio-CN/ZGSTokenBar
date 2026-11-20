"""Bounded, read-only intake observation. Receipts are observations, never an executable queue."""
import datetime
import json
import re
import sqlite3
import subprocess
import time
from pathlib import Path, PurePosixPath

SCHEMA = "zgstokenbar.intake-monitor.v1"
PROCESSING = {"sending", "model_started", "classified", "submitting", "submitted", "archive_pending", "archive_submitted"}
ACTIVE = PROCESSING | {"waiting_upstream", "waiting_original", "retry"}
ATTENTION = {"indeterminate", "needs_attention", "needs_review"}
SOURCE_CLOSED = {"passed", "screened_no_issue"}
SOURCE_ADVANCED = {"pending_acceptance", "rework"}
SOURCE_PENDING = {"pending_triage", "pending_execution"}
HISTORY_ONLY = {"source_closed", "source_advanced", "archive_retained"}
EVENTS = {"intake_ready", "intake_result", "intake_rework", "intake_archive", "intake_error",
          "intake_rework_attention", "intake_related_attention", "intake_archive_attention", "intake_resume_attention"}


def clean(value, limit=160):
    return "".join(c if c.isprintable() else " " for c in str(value or ""))[:limit]


def code(value):
    text = str(value or "")
    return text if re.fullmatch(r"[A-Za-z0-9_.:-]{0,100}", text) else "unavailable"


def reason_code(value):
    return value if isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9_]{1,80}", value) else ""


def archive_generation(value):
    return str(value) if type(value) is int and 0 <= value <= 2**63 - 1 else ""


def evidence_requests(db):
    """Return the first question from each current, still actionable review."""
    rows = db.execute("""SELECT json_extract(r.value,'$.item_id'),
          json_extract(ref.value,'$.work_item_number'),
          substr(json_extract(r.value,'$.decision.missing_evidence[0]'),1,200)
        FROM receipts r JOIN receipts ref ON ref.key='reference:'||
          CASE WHEN json_valid(r.value) THEN json_extract(r.value,'$.item_id') END
        WHERE r.key LIKE 'precheck:%' AND r.key NOT LIKE '%:rework:%'
          AND r.phase='needs_review' AND json_valid(r.value)
          AND json_extract(r.value,'$.decision.action')='need_evidence'
          AND ref.phase='confirmed' AND json_valid(ref.value)
          AND json_extract(ref.value,'$.status') IN ('pending_triage','pending_execution')
          AND r.rowid=(SELECT max(newest.rowid) FROM receipts newest
              WHERE newest.key LIKE 'precheck:'||json_extract(r.value,'$.item_id')||':%')
        ORDER BY r.rowid LIMIT 64""")
    result = []
    for identity, number, text in rows:
        if (not isinstance(identity, str) or not re.fullmatch(r"[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}", identity)
                or not isinstance(number, str) or not re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]{0,18}-[0-9]{1,12}", number)
                or not isinstance(text, str)):
            continue
        question = clean(" ".join(re.sub(r"https?://\S+", "", text).split()))
        if question:
            result.append(dict(itemId=identity, number=number, question=question))
    return result


def read_store(root, summary_only=False):
    """Only selected JSON fields leave SQLite; prompts, model results and credentials never do."""
    root = Path(root)
    if root.resolve() != root.absolute():
        raise ValueError("redirected_state_directory")
    path = root / "intake.sqlite3"
    if path.is_symlink() or not path.is_file():
        raise ValueError("receipts_unavailable")
    db = sqlite3.connect(path.as_uri() + "?mode=ro", uri=True, timeout=2)
    try:
        db.execute("PRAGMA query_only=ON")
        db.execute("BEGIN")  # Bind receipt stage and original-card reference to one SQLite snapshot.
        deadline = time.monotonic() + 5
        db.set_progress_handler(lambda: int(time.monotonic() > deadline), 10000)
        references = {}
        query = """SELECT key, json_extract(value,'$.project_key'), json_extract(value,'$.work_item_number'),
                   substr(json_extract(value,'$.title'),1,160), json_extract(value,'$.status')
                   FROM receipts WHERE key LIKE 'reference:%' AND phase='confirmed' AND json_valid(value) LIMIT 1000"""
        if not summary_only:
            for key, project, number, title, status in db.execute(query):
                references[key.removeprefix("reference:")] = (clean(project, 64), clean(number, 32), clean(title), code(status))
        rows = []
        # No payload column is returned. Newest intent per operation/item is used for the current view.
        for key, phase, intent_id, value_id, reason, decision, generation in db.execute("""SELECT key,phase,
                CASE WHEN json_valid(intent) THEN coalesce(json_extract(intent,'$.item_id'),
                    json_extract(intent,'$.work_item_id'),json_extract(intent,'$.reported_item_id'),json_extract(intent,'$.original_item_id')) END,
                CASE WHEN json_valid(value) THEN coalesce(json_extract(value,'$.item_id'),
                    json_extract(value,'$.work_item_id'),json_extract(value,'$.reported_item_id')) END,
                CASE WHEN ? AND phase IN ('needs_attention','needs_review','indeterminate','retry') AND json_valid(value)
                    THEN substr(json_extract(value,'$.reason'),1,81) END,
                CASE WHEN ? AND phase IN ('needs_attention','needs_review','indeterminate') AND json_valid(value)
                    THEN substr(json_extract(value,'$.decision.action'),1,81) END,
                CASE WHEN ? AND key LIKE 'archive:%' AND json_valid(intent)
                    THEN json_extract(intent,'$.generation') END FROM receipts
                WHERE key LIKE 'precheck:%' OR key LIKE 'rework:%' OR key LIKE 'archive:%'
                OR key LIKE 'related:%' OR key LIKE 'related-delivery:%' OR key LIKE 'original-blocked-continuation:%'
                OR key LIKE 'new-user-evidence-continuation:%' ORDER BY rowid DESC LIMIT 1001""",
                (not summary_only, not summary_only, not summary_only)):
            ids = re.findall(r"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}", key)
            candidates = [intent_id, value_id, *ids]
            identity = next((v.lower() for v in candidates if isinstance(v, str)
                             and re.fullmatch(r"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}", v)), "")
            project, number, title, source = references.get(identity, ("", identity[:8], "", ""))
            kind = key.split(":", 1)[0]
            if kind == "precheck" and ":rework:" in key:
                kind = "rework"
            if kind == "related-delivery":
                kind = "related"
            if kind in ("original-blocked-continuation", "new-user-evidence-continuation"):
                kind = "original_continuation"
            if isinstance(value_id, str) and value_id.lower() != identity:
                source = ""
            rows.append(dict(id=key[:256], itemId=identity, project=project, number=number, title=title,
                             kind=kind, phase=code(phase), observedAt=None, sourceStatus=source,
                             reasonCode=reason_code(reason) or reason_code(decision),
                             archiveGeneration=archive_generation(generation)))
        return dict(records=rows[:1000], projects=sorted({v[0] for v in references.values() if v[0]}),
                    evidenceRequests=evidence_requests(db),
                    gaps=["receipts_capped"] if len(rows) > 1000 else [])
    finally:
        db.close()


def bounded(command, *, input_text=None, limit=2 * 1024 * 1024):
    # All children are owned read-only commands, with bounded time and output.
    with subprocess.Popen(command, stdin=subprocess.PIPE if input_text is not None else subprocess.DEVNULL,
                          stdout=subprocess.PIPE, stderr=subprocess.DEVNULL) as process:
        if input_text is not None:
            process.stdin.write(input_text.encode("utf-8"))
            process.stdin.close()
        import threading
        timer = threading.Timer(8, process.kill)
        timer.start()
        try:
            data = process.stdout.read(limit + 1)
            if len(data) > limit:
                process.kill()
                raise ValueError("output_limit")
            if process.wait(timeout=2) != 0:
                raise ValueError("read_unavailable")
            return data.decode("utf-8")
        finally:
            timer.cancel()


def current_receipts(records):
    latest = {}
    for row in records:
        key = (row["kind"], row["itemId"] or row["id"])
        current = latest.setdefault(key, row)
        if (row is not current and row["kind"] == "precheck" and row["itemId"]
                and row["phase"] == "needs_review" and row.get("reasonCode") == "need_evidence"
                and row.get("sourceStatus") in SOURCE_PENDING and current["phase"] == "confirmed"):
            # Confirmed dispatch can precede opening the original desktop turn.
            # Only the intake ledger may retire this review as reconciled.
            latest[key] = row
    return latest.values()


def review_state(row):
    phase, kind = row["phase"], row["kind"]
    reason = row.get("reasonCode", "")
    source = row.get("sourceStatus", "")
    if phase == "retry" and kind == "archive" and reason == "archive_thread_active":
        return "archive_waiting"
    if phase == "needs_attention" and kind == "archive":
        if reason == "archive_thread_retained":
            return "archive_retained"
        if reason == "archive_thread_changed_or_active":
            return "archive_protected"
        return "attention" if reason else "unconfirmed"
    if phase == "needs_review" and kind == "precheck":
        if source in SOURCE_CLOSED:
            return "source_closed"
        if source in SOURCE_ADVANCED:
            return "source_advanced"
        return "attention" if source in SOURCE_PENDING else "unconfirmed"
    if phase in ("indeterminate", "", "unavailable"):
        return "unconfirmed"
    return "attention" if phase in ATTENTION else ""


def history_kind(kind):
    return {"result": "precheck", "archive_attention": "archive"}.get(kind, kind)


def history_binding(row):
    kind = history_kind(row["kind"])
    phase = row["phase"]
    if kind == "archive":
        if phase == "archive_retry":
            phase = "retry"
        elif row["reviewState"] == "archive_retained":
            phase = "archive_retained"
    return kind, row["itemId"], phase


def matching_history(row, matches, history):
    if not row["itemId"]:
        return None
    candidates = matches.get(history_binding(row), [])
    if row["kind"] != "archive":
        return candidates[0] if candidates else None
    generation = row.get("archiveGeneration", "")
    if generation:
        exact = next((key for key in candidates if history[key]["archiveGeneration"] == generation), None)
        if exact is not None:
            return exact
    if len(candidates) == 1 and (not generation or not history[candidates[0]]["archiveGeneration"]):
        return candidates[0]
    return None


def observed_at(row):
    return row["observedAt"] or ""


def project(config, service, store, journal, summary_only=False):
    gaps = list(store.get("gaps", []))
    records = store.get("records", [])
    latest = current_receipts(records)
    active = service.get("ActiveState") == "active" and service.get("SubState") == "running"
    processing = [row for row in latest if row["phase"] in PROCESSING]
    count = len(processing) if active and not gaps and all(row["itemId"] for row in processing) else None
    requests = store.get("evidenceRequests", []) if active and not gaps else []
    snapshot = dict(schema=SCHEMA, sourceId=config["id"], observedAt=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                    active=active, serviceState=code(service.get("SubState")), invocation=code(service.get("InvocationID")),
                    processingCount=count, projects=[], progress=[], history=[], gaps=gaps, evidenceRequests=requests)
    if summary_only:
        return snapshot
    questions = {request["itemId"]: request["question"] for request in requests}
    latest = [{**row, "reviewState": review_state(row),
               "reviewQuestion": questions.get(row["itemId"], "") if row["kind"] == "precheck" and
               row["phase"] == "needs_review" and row["reasonCode"] == "need_evidence" else ""} for row in latest]
    lookup = {}
    for row in records:
        if row["itemId"]:
            lookup.setdefault(row["itemId"], row)
    history = {}
    for line in journal.splitlines():
        try:
            entry = json.loads(line)
            message = entry.get("MESSAGE", "")
            if not isinstance(message, str) or not message.lstrip().startswith("{"):
                continue  # Ordinary systemd lifecycle text is not a missing structured service event.
            event = json.loads(message)
            if event.get("event") not in EVENTS:
                continue
            identity = str(event.get("item_id", "")).lower()
            item = lookup.get(identity, {})
            stamp = int(entry["__REALTIME_TIMESTAMP"]) / 1000000
            at = datetime.datetime.fromtimestamp(stamp, datetime.timezone.utc).isoformat()
            kind = event["event"].removeprefix("intake_")
            phase = code(event.get("phase") or ("error" if kind == "error" else "needs_attention" if kind.endswith("attention") else "ready"))
            if phase.startswith("archive_") or phase == "archived":
                kind = "archive"
            # Startup backfill can repeat a confirmed archive; do not present it as another operation.
            key = (kind, identity, phase, str(event.get("generation", "")))
            history[key] = dict(id="|".join(key), itemId=identity, project=item.get("project", ""),
                                number=item.get("number", identity[:8]), title=item.get("title", ""),
                                kind=kind, phase=phase, observedAt=at,
                                reviewState="archive_retained" if phase == "archive_retained" else "",
                                reasonCode=reason_code(event.get("reason")), sourceStatus=item.get("sourceStatus", ""),
                                archiveGeneration=archive_generation(event.get("generation")) if kind == "archive" else "")
        except (ValueError, TypeError, KeyError, OverflowError, AttributeError):
            gaps.append("unparsed_event")
    stages = {"": 0, "archive_waiting": 0, "attention": 1, "unconfirmed": 2, "archive_protected": 3}
    pending = [r for r in latest if r["phase"] in ACTIVE or r["reviewState"] in ("attention", "unconfirmed", "archive_protected")]
    pending.sort(key=lambda r: (stages[r["reviewState"]], r["id"]))
    if len(pending) > 64:
        gaps.append("progress_capped")
    # Receipt states fill older history when the journal no longer retains an event. No timestamp is invented.
    seen = {}
    for key, row in sorted(history.items(), key=lambda entry: observed_at(entry[1]), reverse=True):
        seen.setdefault(history_binding(row), []).append(key)
    for row in latest:
        match = matching_history(row, seen, history)
        if match is None:
            history[("receipt", row["id"])] = {**row, "kind": "receipt:" + row["kind"]}
        elif row["reviewState"] in ("archive_retained", "archive_waiting"):
            # The journal supplies the time; the ledger supplies the original phase and reason.
            history[match] = {**row, "kind": "receipt:archive", "observedAt": history[match]["observedAt"]}
        elif row["reviewState"]:
            history[match].update(reviewState=row["reviewState"], reasonCode=row.get("reasonCode", ""),
                                  sourceStatus=row.get("sourceStatus", ""))
    # Old reviewed receipts can lack a journal timestamp. Keep them in the bounded history.
    ordered = sorted(history.values(), key=observed_at, reverse=True)
    selected = sorted(ordered, key=lambda row: row["reviewState"] not in HISTORY_ONLY)[:160]
    return dict(snapshot, projects=store.get("projects", []), progress=pending[:64],
                history=sorted(selected, key=observed_at, reverse=True), gaps=sorted(set(gaps)))


def collect(config, summary_only=False):
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.@-]{0,100}\.service", config["unit"]):
        raise ValueError("invalid_unit")
    root = PurePosixPath(config["stateDirectory"])
    if not str(root).startswith("/var/lib/") or ".." in root.parts or Path(str(root)).is_symlink():
        raise ValueError("invalid_state_directory")
    raw = bounded(["systemctl", "show", config["unit"], "--no-pager", "-p", "User", "-p", "ActiveState",
                   "-p", "SubState", "-p", "InvocationID"])
    service = dict(line.split("=", 1) for line in raw.splitlines() if "=" in line)
    user = service.get("User", "")
    if user == "root" or not re.fullmatch(r"[a-z_][a-z0-9_-]{0,31}", user):
        raise ValueError("invalid_service_user")
    script = "import base64,json\nscope={'__name__':'intake_reader'}\n"
    script += "exec(base64.b64decode(" + repr(SOURCE_BASE64) + "),scope)\n"
    script += "print(json.dumps(scope['read_store'](" + repr(str(root)) + ", " + repr(summary_only) + ")))\n"
    try:
        store = json.loads(bounded(["sudo", "-n", "-u", user, "python3", "-"], input_text=script))
    except (ValueError, subprocess.SubprocessError):
        store = dict(records=[], projects=[], gaps=["receipts_unavailable"])
    if summary_only:
        return project(config, service, store, "", summary_only=True)
    gaps = []
    try:
        journal = bounded(["journalctl", "-u", config["unit"], "-n", "160", "--since", "3 days ago", "--no-pager",
                           "-o", "json", "--output-fields=__REALTIME_TIMESTAMP,MESSAGE"])
    except (ValueError, subprocess.SubprocessError):
        journal = ""
        gaps.append("journal_unavailable")
    result = project(config, service, store, journal)
    result["gaps"] = sorted(set(result["gaps"] + gaps))
    return result


if __name__ == "__main__":
    # The native client supplies CONFIG as data before this bundled module, never as shell text.
    print(json.dumps(collect(CONFIG, globals().get("SUMMARY_ONLY", False)), ensure_ascii=False))
