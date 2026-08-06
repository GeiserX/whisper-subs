#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Zero-dependency unit tests for the worker adapter's pure decision logic (stdlib unittest only,
# matching adapter.py's no-dependency ethos). Run: `python3 worker/test_adapter.py`.
#
# Focus: the two pieces the v4.3.1 hardening added and an adversarial review then corrected —
#   * compute_backend_timeout: the per-request wedge backstop MUST scale with audio length so a long
#     film is never guillotined, yet stay bounded so a truly wedged whisper-server is recovered.
#   * the inflight serialization semaphore: exactly-once slot accounting incl. the cancelled-client
#     and over-release paths.

import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))


def _load_adapter(env=None):
    """Import adapter.py fresh with a given environment (module-level config reads env at import)."""
    old = dict(os.environ)
    try:
        if env:
            os.environ.update({k: str(v) for k, v in env.items()})
        spec = importlib.util.spec_from_file_location("adapter_under_test", os.path.join(_HERE, "adapter.py"))
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        return mod
    finally:
        os.environ.clear()
        os.environ.update(old)


WAV_BPS = 16000 * 2  # 16 kHz mono s16le — what the plugin always sends


class ComputeBackendTimeoutTests(unittest.TestCase):
    def setUp(self):
        self.m = _load_adapter()

    def test_tiny_probe_is_floored(self):
        # A ~30s detection probe must still get the 1h floor, not a few seconds.
        self.assertEqual(self.m.compute_backend_timeout(30 * WAV_BPS), 3600.0)

    def test_scales_with_audio_length(self):
        # A 30-min episode (1800s) -> 1800*12 = 21600s, and comfortably above the plugin's own
        # deadline (1800*6 = 10800s) so the worker never guillotines before the plugin gives up.
        self.assertEqual(self.m.compute_backend_timeout(1800 * WAV_BPS), 21600.0)
        self.assertGreater(self.m.compute_backend_timeout(1800 * WAV_BPS), 1800 * 6)

    def test_long_film_is_not_guillotined_at_one_hour(self):
        # THE bug the review caught: a 2h15m film (8100s) must NOT be cut at 1h. It caps at 24h,
        # which is still > the plugin's 12h max deadline, so the plugin always drops first.
        t = self.m.compute_backend_timeout(8100 * WAV_BPS)
        self.assertEqual(t, 24 * 60 * 60.0)
        self.assertGreater(t, 12 * 60 * 60)

    def test_capped(self):
        self.assertEqual(self.m.compute_backend_timeout(10_000 * WAV_BPS), 24 * 60 * 60.0)

    def test_unknown_size_is_lenient(self):
        # Never guillotine when we can't estimate the audio length.
        self.assertEqual(self.m.compute_backend_timeout(0), 24 * 60 * 60.0)
        self.assertEqual(self.m.compute_backend_timeout(-5), 24 * 60 * 60.0)


class InflightSerializationTests(unittest.TestCase):
    def setUp(self):
        self.m = _load_adapter({"WHISPER_MAX_INFLIGHT": "1"})
        # Make client-liveness deterministic in tests: a dict with {"gone": bool}.
        self.m._client_disconnected = lambda sock: bool(sock.get("gone", False))

    def test_default_is_one(self):
        self.assertEqual(self.m.WHISPER_MAX_INFLIGHT, 1)
        self.assertEqual(type(self.m._inflight_sem).__name__, "BoundedSemaphore")

    def test_acquire_release_roundtrip(self):
        live = {"gone": False}
        self.assertTrue(self.m._acquire_inflight_slot(live))
        self.m._release_inflight_slot()
        self.assertTrue(self.m._acquire_inflight_slot(live))
        self.m._release_inflight_slot()

    def test_cancelled_client_does_not_wait_forever(self):
        live = {"gone": False}
        self.assertTrue(self.m._acquire_inflight_slot(live))  # hold the only slot
        gone = {"gone": True}
        # A second acquirer whose client already vanished must return False without holding the slot.
        self.assertFalse(self.m._acquire_inflight_slot(gone))
        self.m._release_inflight_slot()

    def test_over_release_is_swallowed(self):
        # BoundedSemaphore over-release is a logged no-op, never a crash (would wedge the worker).
        live = {"gone": False}
        self.assertTrue(self.m._acquire_inflight_slot(live))
        self.m._release_inflight_slot()
        self.m._release_inflight_slot()  # extra release: must not raise

    def test_uncapped_mode(self):
        m = _load_adapter({"WHISPER_MAX_INFLIGHT": "0"})
        self.assertIsNone(m._inflight_sem)
        self.assertTrue(m._acquire_inflight_slot({"gone": False}))
        m._release_inflight_slot()  # no-op, must not raise


class MaxLenFlagTests(unittest.TestCase):
    """WHISPER_MAX_LEN / WHISPER_SPLIT_ON_WORD -> whisper-server's -ml / -sow (issue #151).

    whisper-server applies NO cue-length cap of its own (-ml defaults to 0 = unlimited), so a stretch
    the model fails to punctuate arrives as one enormous run-on cue. The plugin's own
    SubtitleMaxLineLength setting only reaches the LOCAL whisper-cli, so a remote worker needs its own
    knob. Opt-in: the load-bearing case is that an unset value changes nothing.
    """

    def test_unset_emits_nothing(self):
        # Back-compat guarantee: existing workers must keep their exact argv after an image bump.
        cmd = _load_adapter().build_backend_cmd()
        self.assertNotIn("-ml", cmd)
        self.assertNotIn("-sow", cmd)

    def test_positive_emits_ml_with_value(self):
        cmd = _load_adapter({"WHISPER_MAX_LEN": "47"}).build_backend_cmd()
        self.assertIn("-ml", cmd)
        self.assertEqual("47", cmd[cmd.index("-ml") + 1])

    def test_positive_also_emits_split_on_word(self):
        # Never a bare -ml: whisper.cpp splits on TOKEN boundaries by default, so an uncapped
        # word can be cut in half mid-word.
        self.assertIn("-sow", _load_adapter({"WHISPER_MAX_LEN": "47"}).build_backend_cmd())

    def test_split_on_word_can_be_disabled_without_losing_the_cap(self):
        cmd = _load_adapter({"WHISPER_MAX_LEN": "47", "WHISPER_SPLIT_ON_WORD": "false"}).build_backend_cmd()
        self.assertIn("-ml", cmd)
        self.assertNotIn("-sow", cmd)

    def test_split_on_word_is_never_emitted_alone(self):
        # -sow is inert without -ml, so emitting it alone would be dead noise on the argv.
        cmd = _load_adapter({"WHISPER_SPLIT_ON_WORD": "true"}).build_backend_cmd()
        self.assertNotIn("-sow", cmd)

    def test_explicit_zero_means_unlimited(self):
        # 0 is whisper-server's own default and a legitimate "no cap" value, NOT a bad input --
        # it must emit nothing rather than warn.
        cmd = _load_adapter({"WHISPER_MAX_LEN": "0"}).build_backend_cmd()
        self.assertNotIn("-ml", cmd)
        self.assertNotIn("-sow", cmd)

    def test_garbage_is_ignored_not_fatal(self):
        # A typo must degrade to whisper-server's default, never crash-loop the container.
        for bad in ("abc", "-5", "47.5", " "):
            cmd = _load_adapter({"WHISPER_MAX_LEN": bad}).build_backend_cmd()
            self.assertNotIn("-ml", cmd, "WHISPER_MAX_LEN=%r should be ignored" % bad)
            self.assertNotIn("-sow", cmd, "WHISPER_MAX_LEN=%r should be ignored" % bad)


if __name__ == "__main__":
    unittest.main(verbosity=2)
