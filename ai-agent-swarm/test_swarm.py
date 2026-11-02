import json
import os
import tempfile
import unittest
from types import SimpleNamespace as NS
from unittest import mock

import swarm


def reply(text="", tool=None, message=""):
    calls = [NS(function=NS(name=tool, arguments=json.dumps({"message": message})))] if tool else None
    return NS(content=text, tool_calls=calls)


SCRIPT = [
    reply(tool="transfer_to_researcher", message="topic"),
    reply(tool="transfer_to_writer", message="notes"),
    reply(tool="transfer_to_critic", message="draft"),
    reply(tool="approve", message="final draft"),
    reply(text="final draft"),
]


class SwarmTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.dir.name, "s.db")

    def tearDown(self):
        self.dir.cleanup()

    def test_full_run_persists(self):
        store = swarm.RunStore(self.path)
        rid = store.create_run("sky")
        with mock.patch.object(swarm, "chat", side_effect=SCRIPT):
            out = swarm.run_swarm(run_id=rid, store=store, log=lambda _: None)
        self.assertEqual(out, "final draft")
        run = store.get_run(rid)
        self.assertEqual((run["status"], run["result"]), ("completed", "final draft"))
        # Completed runs return the stored result without calling the model.
        with mock.patch.object(swarm, "chat", side_effect=AssertionError):
            self.assertEqual(swarm.run_swarm(run_id=rid, store=store), "final draft")

    def test_interrupted_run_resumes_and_runs_are_isolated(self):
        store = swarm.RunStore(self.path)
        other = store.create_run("other topic")
        rid = store.create_run("sky")
        calls = iter(SCRIPT[:2] + [RuntimeError("boom")])

        def flaky(*a, **k):
            r = next(calls)
            if isinstance(r, Exception):
                raise r
            return r

        with mock.patch.object(swarm, "chat", side_effect=flaky):
            with self.assertRaises(RuntimeError):
                swarm.run_swarm(run_id=rid, store=store, log=lambda _: None)
        store.close()

        store = swarm.RunStore(self.path)  # fresh process
        run = store.get_run(rid)
        self.assertEqual((run["status"], run["current_agent"], run["turns"]), ("running", "Writer", 2))
        with mock.patch.object(swarm, "chat", side_effect=SCRIPT[2:]):
            self.assertEqual(swarm.run_swarm(run_id=rid, store=store, log=lambda _: None), "final draft")
        self.assertEqual(len(store.history(other)), 1)
        self.assertEqual(store.get_run(other)["status"], "running")

    def test_unknown_run(self):
        with self.assertRaises(RuntimeError):
            swarm.run_swarm(run_id="nope", store=swarm.RunStore(":memory:"))


if __name__ == "__main__":
    unittest.main()
