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

import http.client
import importlib.util
import io
import os
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock

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


# A stand-in whisper-server: answers /health 503 until FAKE_READY_DELAY seconds have passed (never,
# with FAKE_NEVER_READY=1), then 200, and /inference with FAKE_BODY after FAKE_INFER_SLEEP seconds.
# FAKE_TERM_DELAY makes SIGTERM take that long, so a test can land a request mid-stop.
_FAKE_SERVER = r"""
import os, signal, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

port = int(sys.argv[sys.argv.index("--port") + 1])
start = time.monotonic()
ready_delay = float(os.environ.get("FAKE_READY_DELAY", "0"))
never_ready = os.environ.get("FAKE_NEVER_READY") == "1"
infer_sleep = float(os.environ.get("FAKE_INFER_SLEEP", "0"))
term_delay = float(os.environ.get("FAKE_TERM_DELAY", "0"))

def _term(*_):
    time.sleep(term_delay)
    os._exit(0)

signal.signal(signal.SIGTERM, _term)

class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    def log_message(self, *a):
        pass
    def _reply(self, status, body):
        body = body.encode()
        self.send_response(status)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def do_GET(self):
        ok = not never_ready and time.monotonic() - start >= ready_delay
        self._reply(200 if ok else 503, "ok" if ok else "loading")
    def do_POST(self):
        self.rfile.read(int(self.headers.get("Content-Length", "0")))
        time.sleep(infer_sleep)
        self._reply(200, "1\n00:00:00,000 --> 00:00:01,000\nfake\n")

ThreadingHTTPServer(("127.0.0.1", port), H).serve_forever()
"""


def _free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class _AdapterHarness(unittest.TestCase):
    """Loads a fresh adapter against the fake whisper-server and serves it on a free port.

    os._exit is patched for the whole test, so a watcher that decides to exit records the call
    instead of killing the test run.
    """

    def start(self, env=None, fake_env=None):
        self.tmp = tempfile.TemporaryDirectory()
        fake = os.path.join(self.tmp.name, "fake-whisper-server")
        with open(fake, "w") as f:
            f.write("#!%s\n%s" % (sys.executable, _FAKE_SERVER))
        os.chmod(fake, 0o755)
        model = os.path.join(self.tmp.name, "model.bin")
        open(model, "w").close()

        base = {
            "WHISPER_BIN": fake,
            "WHISPER_MODEL_FILE": model,
            "WHISPER_BACKEND_PORT": str(_free_port()),
            "WHISPER_VAD": "false",
            "WHISPER_CLIENT_POLL_INTERVAL": "0.2",
        }
        base.update(env or {})
        self.fake_env = mock.patch.dict(os.environ, fake_env or {})
        self.fake_env.start()
        self.addCleanup(self.fake_env.stop)
        self.m = _load_adapter(base)
        self.m.log = lambda msg: None

        self.exit_calls = []
        exit_patch = mock.patch.object(os, "_exit", side_effect=self.exit_calls.append)
        exit_patch.start()
        self.addCleanup(exit_patch.stop)

        real_popen = subprocess.Popen
        self.spawned = []

        def _popen(*a, **kw):
            p = real_popen(*a, **kw)
            self.spawned.append(p)
            return p

        popen_patch = mock.patch.object(self.m.subprocess, "Popen", side_effect=_popen)
        popen_patch.start()
        self.addCleanup(popen_patch.stop)

        self.httpd = self.m.ThreadingHTTPServer(("127.0.0.1", 0), self.m.Handler)
        self.httpd.daemon_threads = True
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()
        self.addCleanup(self._stop)

    def _stop(self):
        self.httpd.shutdown()
        self.httpd.server_close()
        self.m._child = None  # detach first: the watchers then return quietly
        for p in self.spawned:
            if p.poll() is None:
                p.kill()
            p.wait()
        time.sleep(0.1)
        self.tmp.cleanup()

    def request(self, method, path="/health", body=None, timeout=30):
        c = http.client.HTTPConnection("127.0.0.1", self.httpd.server_address[1], timeout=timeout)
        headers = {}
        if body is not None:
            headers = {"Content-Type": "multipart/form-data; boundary=x", "Content-Length": str(len(body))}
        c.request(method, path, body=body, headers=headers)
        r = c.getresponse()
        data = r.read().decode()
        c.close()
        return r.status, data

    def post(self, **kw):
        return self.request("POST", "/v1/audio/transcriptions", body=b"--x--\r\n", **kw)

    def wait_for(self, cond, timeout=10):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if cond():
                return True
            time.sleep(0.05)
        return False

    def fast_reaper(self, idle_seconds):
        self.m.IDLE_SECONDS = idle_seconds
        self.m.REAP_INTERVAL = 0.05
        self.m.boot()


class IdleUnloadTests(_AdapterHarness):
    """WHISPER_IDLE_MINUTES: load the model on demand and free it after an idle stretch."""

    def test_a_default_keeps_the_boot_load(self):
        self.start(fake_env={"FAKE_READY_DELAY": "1"})
        self.assertEqual(self.m.IDLE_MINUTES, 0)
        self.m.spawn_whisper_server()
        # Same as 0.1.5: 503 while the model loads, then 200 ok.
        self.assertEqual(self.request("GET"), (503, '{"status":"loading model"}'))
        self.assertTrue(self.wait_for(lambda: self.request("GET")[0] == 200))
        self.assertEqual(self.request("GET"), (200, '{"status":"ok"}'))
        self.assertEqual(self.post()[0], 200)
        time.sleep(0.5)
        self.assertIsNone(self.spawned[0].poll())
        self.assertNotIn("idle-reaper", [t.name for t in threading.enumerate()])
        self.assertEqual(len(self.spawned), 1)

    def test_b_idle_mode_loads_on_first_request(self):
        self.start({"WHISPER_IDLE_MINUTES": "10"})
        self.m.boot()
        self.assertEqual(self.spawned, [])
        self.assertEqual(self.request("GET"), (200, '{"status":"idle"}'))
        self.assertEqual(self.request("HEAD")[0], 200)
        status, body = self.post()
        self.assertEqual(status, 200)
        self.assertIn("fake", body)
        self.assertEqual(len(self.spawned), 1)
        self.assertEqual(self.request("GET"), (200, '{"status":"ok"}'))

    def test_c_unloads_after_idle_without_exiting(self):
        self.start({"WHISPER_IDLE_MINUTES": "1"})
        self.fast_reaper(0.3)
        self.assertEqual(self.post()[0], 200)
        child = self.spawned[0]
        self.assertTrue(self.wait_for(lambda: child.poll() is not None))
        time.sleep(0.3)  # give the watcher time to (wrongly) exit
        self.assertEqual(self.exit_calls, [])
        self.assertIsNone(self.m._child)
        self.assertEqual(self.request("GET"), (200, '{"status":"idle"}'))

    def test_d_request_during_stop_gets_a_fresh_child(self):
        self.start({"WHISPER_IDLE_MINUTES": "1"}, fake_env={"FAKE_TERM_DELAY": "1"})
        self.fast_reaper(0.2)
        self.assertEqual(self.post()[0], 200)
        first = self.spawned[0]
        # The reaper holds the lifecycle lock for the whole (slow) stop.
        self.assertTrue(self.wait_for(lambda: self.m._child is None and first.poll() is None))
        status, body = self.post()
        self.assertEqual(status, 200)
        self.assertIn("fake", body)
        self.assertIsNotNone(first.poll())
        self.assertEqual(len(self.spawned), 2)
        self.assertEqual(self.exit_calls, [])

    def test_e_concurrent_requests_spawn_one_child(self):
        self.start({"WHISPER_IDLE_MINUTES": "10", "WHISPER_MAX_INFLIGHT": "0"},
                   fake_env={"FAKE_READY_DELAY": "1"})
        self.m.boot()
        results = []
        threads = [threading.Thread(target=lambda: results.append(self.post()[0])) for _ in range(4)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        self.assertEqual(results, [200] * 4)
        self.assertEqual(len(self.spawned), 1)
        self.assertEqual(self.m._active, 0)

    def test_f_never_stops_a_child_mid_request(self):
        self.start({"WHISPER_IDLE_MINUTES": "1"}, fake_env={"FAKE_INFER_SLEEP": "1.5"})
        self.fast_reaper(0.2)
        status, body = self.post()
        self.assertEqual(status, 200)
        self.assertIn("fake", body)
        self.assertEqual(len(self.spawned), 1)
        self.assertIsNone(self.spawned[0].poll())
        self.assertEqual(self.m._active, 0)

    def test_g_a_crash_still_exits(self):
        self.start({"WHISPER_IDLE_MINUTES": "10"})
        self.m.boot()
        self.assertEqual(self.post()[0], 200)
        self.spawned[0].kill()
        self.assertTrue(self.wait_for(lambda: self.exit_calls))
        self.assertEqual(self.exit_calls, [1])

    def test_h_load_failure_is_a_503_and_releases_the_request(self):
        self.start({"WHISPER_IDLE_MINUTES": "10", "WHISPER_READY_TIMEOUT": "1"},
                   fake_env={"FAKE_NEVER_READY": "1"})
        self.m.boot()
        self.assertEqual(self.post(), (503, '{"error":"model failed to load"}'))
        self.assertEqual(self.m._active, 0)

    def test_i_env_parsing(self):
        m = _load_adapter()
        warnings = []
        m.log = warnings.append
        self.assertEqual(m._parse_idle_minutes("-1"), 0)
        self.assertEqual(m._parse_idle_minutes("x"), 0)
        self.assertEqual(m._parse_idle_minutes("5000"), 1440)
        self.assertEqual(len(warnings), 3)
        self.assertEqual(m._parse_idle_minutes("10"), 10)
        self.assertEqual(m._parse_idle_minutes("0"), 0)
        self.assertEqual(len(warnings), 3)
        self.assertEqual(_load_adapter({"WHISPER_IDLE_MINUTES": "5000"}).IDLE_MINUTES, 1440)

    def test_j_log_survives_a_broken_stdout(self):
        m = _load_adapter()

        class Broken(io.StringIO):
            def write(self, _):
                raise OSError("stdout gone")

        with mock.patch.object(sys, "stdout", Broken()):
            m.log("hello")  # must not raise


if __name__ == "__main__":
    unittest.main(verbosity=2)
