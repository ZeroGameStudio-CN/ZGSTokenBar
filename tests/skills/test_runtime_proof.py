import contextlib
import importlib.util
import io
import json
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[2] / "src/ZGSTokenBar.Core/Skills/sol-luna-delegation/scripts/verify_subagent_runtime.py"
SPEC = importlib.util.spec_from_file_location("runtime_proof", SCRIPT)
proof = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(proof)

CHILD = "11111111-1111-4111-8111-111111111111"
OTHER = "22222222-2222-4222-8222-222222222222"
PARENT = "parent-task"


class RuntimeProofTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)

    def tearDown(self):
        self.temporary.cleanup()

    def record(self, child=CHILD, parent=PARENT, effort="max", inherited=False, complete=True):
        entries = [{"type": "session_meta", "payload": {
            "id": child, "parent_thread_id": parent, "agent_path": "/root/executor",
        }}]
        if inherited:
            entries.extend([
                {"type": "session_meta", "payload": {"id": "old-parent"}},
                {"type": "turn_context", "payload": {"model": "gpt-6-astra", "effort": "high"}},
                {"type": "event_msg", "payload": {"type": "thread_settings_applied"}},
            ])
        entries.append({"type": "event_msg", "payload": {"type": "task_started"}})
        if complete:
            entries.extend([
                {"type": "turn_context", "payload": {"model": "gpt-5.6-luna", "effort": effort}},
                {"type": "inter_agent_communication_metadata", "payload": {"trigger_turn": True}},
            ])
        path = self.root / f"rollout-{child}.jsonl"
        path.write_text("\n".join(json.dumps(entry) for entry in entries) + "\n", encoding="utf-8")
        return path

    def test_exact_child_ignores_another_task_with_the_same_agent_name(self):
        wanted = self.record()
        self.record(child=OTHER, parent="different-parent")
        found = proof.find_session(self.root, CHILD, PARENT)
        self.assertEqual(wanted, found)
        result = proof.inspect_session(found, PARENT, CHILD, "/root/executor")
        self.assertEqual(("gpt-5.6-luna", "max"), (result["model"], result["effort"]))

    def test_parent_identity_is_required_even_with_an_exact_file(self):
        with self.assertRaises(proof.RuntimeProofError):
            proof.inspect_session(self.record(parent="different-parent"), PARENT)

    def test_wrong_child_identity_and_path_are_rejected(self):
        path = self.record()
        with self.assertRaises(proof.RuntimeProofError):
            proof.inspect_session(path, PARENT, OTHER)
        with self.assertRaises(proof.RuntimeProofError):
            proof.inspect_session(path, PARENT, CHILD, "/root/other")

    def test_inherited_parent_runtime_is_not_child_proof(self):
        result = proof.inspect_session(self.record(inherited=True), PARENT)
        self.assertEqual("gpt-5.6-luna", result["model"])

    def test_pending_child_is_not_a_model_mismatch(self):
        with self.assertRaises(proof.RuntimeProofPending):
            proof.inspect_session(self.record(complete=False), PARENT)

    def test_ambiguous_records_do_not_select_the_latest_file(self):
        original = self.record()
        nested = self.root / "duplicate"
        nested.mkdir()
        (nested / original.name).write_bytes(original.read_bytes())
        with self.assertRaises(proof.RuntimeProofError):
            proof.find_session(self.root, CHILD, PARENT)

    def test_session_lookup_does_not_accept_a_glob(self):
        with self.assertRaises(proof.RuntimeProofError):
            proof.find_session(self.root, "*", PARENT)

    def test_cli_reports_xhigh_as_a_mismatch_not_max(self):
        path = self.record(effort="xhigh")
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            result = proof.main(["--session", str(path), "--parent-thread-id", PARENT])
        self.assertEqual(3, result)
        self.assertEqual("mismatch", json.loads(output.getvalue())["status"])


if __name__ == "__main__":
    unittest.main()
