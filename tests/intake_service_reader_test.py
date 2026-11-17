"""Synthetic owner-side receipts only. No SSH, systemd or production file is read."""
import base64
from contextlib import closing
import hashlib
import importlib.util
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[1] / "src/ZGSTokenBar.App/Assets/intake-service-read.py"
spec = importlib.util.spec_from_file_location("intake_reader", SCRIPT)
reader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reader)
reader.SOURCE_BASE64 = base64.b64encode(SCRIPT.read_bytes()).decode("ascii")
A = "00000000-0000-4000-8000-000000000001"
B = "00000000-0000-4000-8000-000000000002"
CONFIG = dict(id="fixture", unit="intake-fixture.service", stateDirectory="/var/lib/intake-fixture")


class ReaderTests(unittest.TestCase):
    def fixture(self, root):
        path = root / "intake.sqlite3"
        with closing(sqlite3.connect(path)) as db, db:
            db.execute("CREATE TABLE receipts(key TEXT PRIMARY KEY,intent TEXT NOT NULL,phase TEXT NOT NULL,value TEXT)")
            for identity, number in ((A, "LAB-1"), (B, "LAB-2")):
                reference = dict(project_key="LAB", work_item_number=number, title="Feedback " + number,
                                 content="NEVER_EXPORT_PRIVATE_PROMPT", token="NEVER_EXPORT_SECRET")
                db.execute("INSERT INTO receipts VALUES(?,?,?,?)", ("reference:" + identity, "{}", "confirmed", json.dumps(reference)))
            for key, phase in (("precheck:" + A + ":older", "waiting_upstream"),
                               ("precheck:" + A + ":newer", "confirmed"),
                               ("precheck:" + B + ":current", "model_started"),
                               ("archive:" + hashlib.sha256(A.encode()).hexdigest(), "archived")):
                intent = json.dumps(dict(secret="NEVER_EXPORT_SECRET", item_id=A)) if key.startswith("archive:") else '{"secret":"NEVER_EXPORT_SECRET"}'
                db.execute("INSERT INTO receipts VALUES(?,?,?,?)", (key, intent, phase,
                    '{"model_result":{"prompt":"NEVER_EXPORT_PRIVATE_PROMPT"}}'))
        return path

    def test_no_writes_or_private_payloads(self):
        with tempfile.TemporaryDirectory(prefix="zgstokenbar-intake-fixture-") as directory:
            root = Path(directory).resolve(); path = self.fixture(root)
            before = hashlib.sha256(path.read_bytes()).hexdigest()
            value = reader.read_store(root)
            self.assertEqual(value["projects"], ["LAB"])
            self.assertEqual(len(value["records"]), 4)
            self.assertNotIn("NEVER_EXPORT", json.dumps(value))
            self.assertEqual(before, hashlib.sha256(path.read_bytes()).hexdigest())
            self.assertEqual([p.name for p in root.iterdir()], ["intake.sqlite3"])

    def test_replay_and_old_inflight_intents_are_not_new_work(self):
        with tempfile.TemporaryDirectory(prefix="zgstokenbar-intake-fixture-") as directory:
            root = Path(directory).resolve(); self.fixture(root)
            lines = [json.dumps(dict(MESSAGE="Started intake service.", __REALTIME_TIMESTAMP="1700000000000000"))]
            for stamp in (1700000001000000, 1700000002000000):
                lines.append(json.dumps(dict(MESSAGE=json.dumps(dict(event="intake_archive", item_id=A, phase="archived", generation=1)),
                                            __REALTIME_TIMESTAMP=str(stamp))))
            result = reader.project(CONFIG, {"ActiveState": "active", "SubState": "running"}, reader.read_store(root), "\n".join(lines))
            self.assertEqual(result["gaps"], [])
            self.assertEqual([(r["itemId"], r["phase"]) for r in result["progress"]], [(B, "model_started")])
            self.assertEqual(len([r for r in result["history"] if r["kind"] == "archive"]), 1)
            self.assertTrue(any(r["observedAt"] is None for r in result["history"]))
            self.assertNotIn("NEVER_EXPORT", json.dumps(result))

    def test_hashed_archive_and_continuation_formats_keep_identity_and_stage(self):
        with tempfile.TemporaryDirectory(prefix="zgstokenbar-intake-fixture-") as directory:
            root = Path(directory).resolve(); path = self.fixture(root)
            with closing(sqlite3.connect(path)) as db, db:
                for key, phase in (("precheck:" + B + ":rework:2:digest", "waiting_original"),
                                   ("related-delivery:" + A + ":digest", "submitted")):
                    db.execute("INSERT INTO receipts VALUES(?,?,?,?)", (key, "{}", phase, "{}"))
            store = reader.read_store(root)
            archive = next(r for r in store["records"] if r["kind"] == "archive")
            self.assertEqual((archive["itemId"], archive["number"]), (A, "LAB-1"))
            result = reader.project(CONFIG, {"ActiveState": "active", "SubState": "running"}, store, "")
            self.assertIn(("rework", "waiting_original"), [(r["kind"], r["phase"]) for r in result["progress"]])
            self.assertIn(("related", "submitted"), [(r["kind"], r["phase"]) for r in result["progress"]])
            private_error = json.dumps(dict(MESSAGE=json.dumps(dict(event="intake_error", error="NEVER_EXPORT_SECRET")), __REALTIME_TIMESTAMP="1700000000000000"))
            self.assertNotIn("NEVER_EXPORT", json.dumps(reader.project(CONFIG, {}, store, private_error)))

    def test_collector_child_is_readonly_and_service_user_scoped(self):
        with tempfile.TemporaryDirectory(prefix="zgstokenbar-intake-fixture-") as directory:
            root = Path(directory).resolve(); self.fixture(root)
            calls = []
            def command(args, **kwargs):
                calls.append(args)
                if args[0] == "systemctl":
                    return "User=intake-fixture\nActiveState=active\nSubState=running\nInvocationID=fixture\n"
                if args[0] == "sudo":
                    self.assertEqual(args, ["sudo", "-n", "-u", "intake-fixture", "python3", "-"])
                    script = kwargs["input_text"].replace(repr(CONFIG["stateDirectory"]), repr(str(root)))
                    child = subprocess.run([sys.executable, "-I", "-B", "-c", script], capture_output=True, text=True, timeout=5)
                    self.assertEqual(child.returncode, 0, child.stderr)
                    return child.stdout
                if args[0] == "journalctl":
                    return ""
                raise AssertionError("Unexpected command")
            with patch.object(reader, "bounded", command):
                value = reader.collect(CONFIG)
            self.assertEqual(value["gaps"], [])
            self.assertTrue(value["active"])
            self.assertEqual(len(calls), 3)

    def test_invalid_targets_never_execute_commands(self):
        with patch.object(reader, "bounded", side_effect=AssertionError("must not execute")):
            for bad in (dict(CONFIG, unit="x.service;restart"), dict(CONFIG, stateDirectory="/var/lib/../etc")):
                with self.assertRaises(ValueError): reader.collect(bad)


if __name__ == "__main__":
    unittest.main()
