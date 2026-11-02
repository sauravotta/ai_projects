import os
import tempfile
import unittest
from types import SimpleNamespace as NS
from unittest import mock

import agent


def fake_msg(content=None, calls=()):
    return NS(content=content, tool_calls=[
        NS(id=i, function=NS(name=n, arguments=a)) for i, n, a in calls] or None)


class HistoryTests(unittest.TestCase):
    def test_roundtrip_and_session_isolation(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "s.db")
            s = agent.HistoryStore(path)
            s.append("a", [{"role": "user", "content": "hi"}])
            s.append("a", [{"role": "assistant", "content": "yo"}])
            s.append("b", [{"role": "user", "content": "other"}])
            s.close()
            s = agent.HistoryStore(path)
            self.assertEqual([m["content"] for m in s.load("a")], ["hi", "yo"])
            self.assertEqual(len(s.load("b")), 1)
            s.clear("a")
            self.assertEqual(s.load("a"), [])
            self.assertEqual(len(s.load("b")), 1)
            s.close()

    def test_tool_turn_is_serialisable_and_replayable(self):
        replies = [fake_msg(None, [("c1", "add", '{"a": 1, "b": 2}')]), fake_msg("3")]
        history = [{"role": "system", "content": "x"}]
        with mock.patch.object(agent, "chat", side_effect=replies):
            self.assertEqual(agent.run_agent("1+2?", history), "3")
        s = agent.HistoryStore(":memory:")
        s.append("t", history)
        self.assertEqual(s.load("t"), history)
        self.assertEqual(history[3]["role"], "tool")


if __name__ == "__main__":
    unittest.main()
