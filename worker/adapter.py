#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
#
# whisper-subs worker adapter
# ===========================
#
# A tiny, dependency-free (Python standard library only) HTTP adapter that makes
# an upstream `whisper-server` (ggml-org/whisper.cpp, examples/server) speak the
# exact OpenAI-audio dialect that the whisper-subs plugin's RemoteWhisperProvider
# expects.
#
# WHY THIS EXISTS
# ---------------
# whisper.cpp's `whisper-server` exposes a SINGLE inference path (default
# `/inference`, remappable with `--inference-path`). It cannot serve both
# `/v1/audio/transcriptions` AND `/v1/audio/translations` at once, and it selects
# translation via a `translate` multipart FIELD rather than a distinct path.
#
# The plugin (Providers/RemoteWhisperProvider.cs), by contrast, is hard-wired to:
#   * POST /v1/audio/transcriptions   -> transcribe (or detect language)
#   * POST /v1/audio/translations     -> translate to English
# with multipart fields: file, model, response_format (srt | verbose_json),
# and an optional language. Translation is chosen purely by the PATH.
#
# A pure reverse proxy (nginx/Caddy) can rewrite the path but cannot inject a new
# multipart field into a streamed body, so the translations route could never be
# mapped to whisper.cpp's `translate=true`. This adapter does exactly and only
# that mapping, while STREAMING the (potentially very large) audio body straight
# through so N concurrent workers never buffer whole films in RAM.
#
# WHAT IT DOES
# ------------
#   * POST /v1/audio/transcriptions  -> proxied verbatim to upstream /inference.
#   * POST /v1/audio/translations    -> proxied to /inference with a `translate=true`
#                                       multipart part PREPENDED to the body (no
#                                       reparsing, fully streamed).
#   * GET|HEAD /health and GET|HEAD on the two audio paths -> cheap readiness probe
#     (200 when the upstream model is loaded, 503 while loading). Never runs
#     inference, never requires the API key -> safe for Docker healthchecks.
#   * Optional Bearer auth on the POST routes when API_KEY is set.
#   * Spawns and supervises the upstream `whisper-server` child process; if it
#     dies, the adapter exits non-zero so the container restarts.
#
# Language handling: whisper-server defaults `language` to "en" (server.cpp), which
# would break auto-detect and the plugin's verbose_json language-detection call.
# We therefore launch it with `--language auto`; an explicit `language` field in a
# request still overrides per-call. (See entrypoint / adapter spawn below.)

import hmac
import http.client
import os
import select
import shlex
import signal
import socket
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


# --------------------------------------------------------------------------- #
# Configuration (all via environment; sensible defaults for the shipped image)
# --------------------------------------------------------------------------- #
def _env(name, default):
    v = os.environ.get(name)
    return v if v is not None and v != "" else default


def _env_bool(name, default):
    """Parse a boolean env var: true/1/yes/y (case-insensitive) => True. `default` is the
    STRING used when the var is unset/empty, so it reads like the other _env() calls."""
    return _env(name, default).strip().lower() in ("1", "true", "yes", "y")


def _parse_int(value):
    """int(value) that returns None instead of raising, so a malformed numeric env var
    degrades to 'leave whisper-server's own default' rather than crash-looping the
    container (a bad -mc/-bs value would otherwise std::stoi-throw inside whisper-server)."""
    try:
        return int(str(value).strip())
    except (TypeError, ValueError):
        return None


LISTEN_HOST = _env("LISTEN_HOST", "0.0.0.0")
LISTEN_PORT = int(_env("LISTEN_PORT", "8080"))

BACKEND_HOST = _env("WHISPER_BACKEND_HOST", "127.0.0.1")
BACKEND_PORT = int(_env("WHISPER_BACKEND_PORT", "8081"))
BACKEND_INFERENCE_PATH = _env("WHISPER_INFERENCE_PATH", "/inference")

# Wedge backstop for a SINGLE upstream inference, computed PER-REQUEST from the audio length
# (see compute_backend_timeout). A whole-file transcription IS one request, so a flat ceiling
# would guillotine a legitimately long film on a slow/CPU worker (a 2h15m film at 16 CPU threads
# is ~1h48m per this repo's own benchmark). The plugin already enforces its OWN per-call deadline
# (audioSeconds × JobTimeoutRealtimeFactor, clamped ≤12h) and drops the socket at it — the
# disconnect-watcher below then aborts the upstream — so this backstop only matters if the client
# never drops, and is deliberately scaled WELL ABOVE the plugin's deadline (factor 12 vs the
# plugin's 6) so it can never fire before the plugin gives up. It exists solely to recover from a
# whisper-server truly wedged in a decode loop that ignores the abort. Effective per request:
# clamp(audioSeconds × FACTOR, FLOOR, CAP). The plugin always sends 16 kHz mono s16le = 32000 B/s.
BACKEND_TIMEOUT_FLOOR = float(_env("WHISPER_BACKEND_TIMEOUT", str(60 * 60)))          # min backstop (short probes)
BACKEND_TIMEOUT_FACTOR = float(_env("WHISPER_BACKEND_TIMEOUT_FACTOR", "12"))          # × realtime; ≫ any real decode
BACKEND_TIMEOUT_CAP = float(_env("WHISPER_BACKEND_TIMEOUT_CAP", str(24 * 60 * 60)))   # absolute ceiling
_WAV_BYTES_PER_SEC = 16000 * 2  # 16 kHz, mono, s16le — what the plugin always sends


def compute_backend_timeout(body_bytes):
    """Per-request wedge backstop derived from the audio size. Unknown/zero size => the CAP (be
    lenient — never guillotine), always >= FLOOR so a tiny detection probe still gets a sane minimum."""
    if body_bytes <= 0:
        return BACKEND_TIMEOUT_CAP
    audio_seconds = body_bytes / _WAV_BYTES_PER_SEC
    return max(BACKEND_TIMEOUT_FLOOR, min(BACKEND_TIMEOUT_CAP, audio_seconds * BACKEND_TIMEOUT_FACTOR))

# Max concurrent inferences forwarded to the upstream whisper-server. whisper.cpp's server
# runs a SINGLE inference at a time, so a second request that arrives mid-transcription just
# sits in the OS accept queue behind the first — invisible to the disconnect-watcher (which
# can only abort a request whisper-server has actually accepted), so a short language-detection
# probe stuck behind a full-film transcription silently blows its deadline (head-of-line
# blocking). Serialising here (default 1) makes the worker robust regardless of how many
# requests a client fans at it: extra requests wait on this semaphore — where the wait IS
# cancellable (we poll client liveness) — instead of wedging the backend. 0 disables the cap.
WHISPER_MAX_INFLIGHT = _parse_int(_env("WHISPER_MAX_INFLIGHT", "1"))
if WHISPER_MAX_INFLIGHT is None or WHISPER_MAX_INFLIGHT < 0:
    WHISPER_MAX_INFLIGHT = 1

API_KEY = _env("API_KEY", "")  # empty -> no auth required

WHISPER_BIN = _env("WHISPER_BIN", "/usr/local/bin/whisper-server")
# entrypoint.sh resolves + downloads the model and exports the absolute path.
MODEL_FILE = _env("WHISPER_MODEL_FILE", "")
WHISPER_LANGUAGE = _env("WHISPER_LANGUAGE", "auto")
WHISPER_THREADS = _env("WHISPER_THREADS", "")   # "" -> whisper.cpp default
WHISPER_CONVERT = _env_bool("CONVERT", "false")
WHISPER_EXTRA_ARGS = _env("WHISPER_EXTRA_ARGS", "")
# Where whisper-server writes ffmpeg's transcoded WAV when CONVERT=true. whisper-server
# defaults its --tmp-dir to "." (cwd = /opt/whisper-adapter), which is read-only on the
# shipped hardened rootfs, so CONVERT would fail there. Point it at the writable tmpfs
# the example compose mounts at /tmp (M3).
WHISPER_TMP_DIR = _env("WHISPER_TMP_DIR", "/tmp")

# --- Decoding-quality flags ------------------------------------------------- #
# Defaults deliberately MIRROR the plugin's LOCAL whisper-cli path
# (Providers/WhisperProvider.cs -> BuildTranscribeArguments emits -sns, -mc 0, --vad), so a
# remote worker's subtitles match local-transcription quality instead of whisper-server's
# looser out-of-the-box defaults (which hallucinate on music/silence and carry long stale
# text-context). All are overridable, and a WHISPER_EXTRA_ARGS value still wins (appended last).
#
# -sns / --suppress-nst : drop non-speech tokens. Plugin ON; whisper-server default OFF.
WHISPER_SUPPRESS_NON_SPEECH = _env_bool("WHISPER_SUPPRESS_NON_SPEECH", "true")
# -mc / --max-context   : tokens of prior text carried into the next window. Plugin uses 0
#   (no stale context -> far less runaway hallucination); whisper-server default is -1.
WHISPER_MAX_CONTEXT = _env("WHISPER_MAX_CONTEXT", "0")
# -bs / --beam-size     : beam-search width. EMPTY (default) leaves whisper-server's own
#   default (greedy, -bs -1), matching the plugin; set a positive int (e.g. 5) to trade
#   speed for a bit more accuracy.
WHISPER_BEAM_SIZE = _env("WHISPER_BEAM_SIZE", "")
# -ml / --max-len       : maximum characters per subtitle cue. EMPTY/0 (default) leaves
#   whisper-server's own default of 0 = UNLIMITED, matching the plugin's default so existing
#   workers are unchanged. Whisper applies no cap of its own, so when the model drops into its
#   documented "no-punctuation mode" a whole utterance lands in one enormous run-on cue
#   (issue #151). Set e.g. 47 to cap it. Mirrors the plugin's SubtitleMaxLineLength setting,
#   which only reaches the LOCAL whisper-cli — a remote worker owns its own segmentation, so
#   the same cap has to be set here.
WHISPER_MAX_LEN = _env("WHISPER_MAX_LEN", "")
# -sow / --split-on-word: split on word rather than token boundaries, so a --max-len cap never
#   cuts mid-word. Only meaningful alongside -ml, so it is emitted only when one is set.
#   Defaults ON to match the plugin, which always pairs the two.
WHISPER_SPLIT_ON_WORD = _env_bool("WHISPER_SPLIT_ON_WORD", "true")
# --vad / --vad-model   : native Silero VAD. Snaps cue starts to real speech onset and
#   avoids decoding long silences (another hallucination source). Plugin default ON. Added
#   ONLY when the model file exists; if enabled but missing we log a warning and skip it
#   (never a hard fail). The model is baked into the image at the path below, OUTSIDE the
#   /models bind-mount so a mounted model cache can't shadow it.
WHISPER_VAD = _env_bool("WHISPER_VAD", "true")
WHISPER_VAD_MODEL = _env("WHISPER_VAD_MODEL", "/opt/whisper-vad/ggml-silero-v5.1.2.bin")

READY_TIMEOUT = int(_env("WHISPER_READY_TIMEOUT", "600"))  # seconds to first-ready

# Drop a stalled-but-open client after this many seconds so one slow/silent
# connection can't hold the single whisper-server inference slot forever (M2).
SOCKET_TIMEOUT = float(_env("WHISPER_SOCKET_TIMEOUT", "120"))

# How often the disconnect-watcher polls the client socket while the request thread is
# blocked on the upstream inference. On a drop we tear down the upstream connection so
# whisper-server's abort_callback fires and it stops holding the single inference slot
# for a transcription nobody will read (M2).
CLIENT_POLL_INTERVAL = float(_env("WHISPER_CLIENT_POLL_INTERVAL", "2"))

# Reject an absurd Content-Length outright (M2). A 2h film's 16 kHz mono WAV is
# ~260 MB; the default 8 GiB ceiling is generous but bounds a memory/disk-abuse
# upload. Set WHISPER_MAX_BODY_BYTES=0 to disable the cap.
MAX_BODY_BYTES = int(_env("WHISPER_MAX_BODY_BYTES", str(8 * 1024 * 1024 * 1024)))

CHUNK = 256 * 1024

TRANSCRIBE_PATH = "/v1/audio/transcriptions"
TRANSLATE_PATH = "/v1/audio/translations"


def log(msg):
    sys.stdout.write("[adapter] %s\n" % msg)
    sys.stdout.flush()


# Serialises inference forwarding to the single-threaded upstream (see WHISPER_MAX_INFLIGHT).
# None => uncapped (opt-out). A BoundedSemaphore also self-checks against an accidental
# extra release (a programming error surfaces as a ValueError instead of silently widening
# the cap).
_inflight_sem = (
    threading.BoundedSemaphore(WHISPER_MAX_INFLIGHT) if WHISPER_MAX_INFLIGHT > 0 else None
)


# --------------------------------------------------------------------------- #
# Upstream whisper-server lifecycle
# --------------------------------------------------------------------------- #
_child = None  # type: subprocess.Popen | None
# Set the first time the upstream reports ready (via backend_ready). Lets the watcher
# tell a genuine mid-run crash from a startup misconfiguration that exits 0 (N6).
_became_ready = threading.Event()


def build_backend_cmd():
    """Assemble the whisper-server argv from the module configuration.

    Extracted from spawn_whisper_server() so the exact command is unit/print-testable. The
    decoding-quality flags (-sns, -mc, -bs, --vad) are appended BEFORE any WHISPER_EXTRA_ARGS
    so an operator's explicit override always wins (whisper-server honours the LAST value for
    a repeated flag). VAD is added only when its model file is actually present, so a missing
    model degrades to 'no VAD' with a warning instead of a whisper-server startup failure.
    """
    cmd = [
        WHISPER_BIN,
        "--host", BACKEND_HOST,
        "--port", str(BACKEND_PORT),
        "--model", MODEL_FILE,
        "--inference-path", BACKEND_INFERENCE_PATH,
        # Default to auto-detect so a request WITHOUT a language field detects the
        # spoken language instead of forcing English (whisper-server defaults to
        # "en"). The plugin's verbose_json language-detection call relies on this.
        "--language", WHISPER_LANGUAGE,
    ]
    if WHISPER_THREADS:
        cmd += ["--threads", WHISPER_THREADS]
    if WHISPER_CONVERT:
        # Requires ffmpeg in the image (build with --build-arg INSTALL_FFMPEG=true).
        # --tmp-dir: whisper-server writes the transcoded WAV to its temp dir, whose
        # default "." (cwd /opt/whisper-adapter) is read-only on the hardened rootfs;
        # send it to the writable tmpfs so CONVERT works under read_only: true (M3).
        cmd += ["--convert", "--tmp-dir", WHISPER_TMP_DIR]

    # --- decoding-quality flags: mirror the plugin's local whisper-cli path --- #
    if WHISPER_SUPPRESS_NON_SPEECH:
        cmd += ["-sns"]
    mc = _parse_int(WHISPER_MAX_CONTEXT)
    if WHISPER_MAX_CONTEXT != "" and mc is None:
        log("WARNING: WHISPER_MAX_CONTEXT=%r is not an integer; ignoring it (leaving "
            "whisper-server's own default)." % WHISPER_MAX_CONTEXT)
    elif mc is not None:
        cmd += ["-mc", str(mc)]
    if WHISPER_BEAM_SIZE != "":
        bs = _parse_int(WHISPER_BEAM_SIZE)
        if bs is not None and bs > 0:
            cmd += ["-bs", str(bs)]
        else:
            log("WARNING: WHISPER_BEAM_SIZE=%r is not a positive integer; ignoring it "
                "(leaving whisper-server's default = greedy)." % WHISPER_BEAM_SIZE)
    if WHISPER_MAX_LEN != "":
        ml = _parse_int(WHISPER_MAX_LEN)
        if ml is not None and ml > 0:
            cmd += ["-ml", str(ml)]
            # Only ever alongside -ml: a bare -sow is inert, since it just changes WHERE a
            # --max-len cap cuts. Same pairing the plugin enforces.
            if WHISPER_SPLIT_ON_WORD:
                cmd += ["-sow"]
        elif ml == 0:
            pass  # explicit 0 = "unlimited", whisper-server's own default; emit nothing
        else:
            log("WARNING: WHISPER_MAX_LEN=%r is not a non-negative integer; ignoring it "
                "(leaving whisper-server's default = 0 / unlimited)." % WHISPER_MAX_LEN)
    if WHISPER_VAD:
        if os.path.isfile(WHISPER_VAD_MODEL):
            cmd += ["--vad", "--vad-model", WHISPER_VAD_MODEL]
        else:
            log("WARNING: WHISPER_VAD is enabled but no VAD model file exists at %r; "
                "continuing WITHOUT VAD (cue starts may drift on gapless segments and long "
                "silences may decode/hallucinate). The image bakes one in by default; point "
                "WHISPER_VAD_MODEL at a ggml Silero model, or set WHISPER_VAD=false to "
                "silence this warning." % WHISPER_VAD_MODEL)

    if WHISPER_EXTRA_ARGS:
        cmd += shlex.split(WHISPER_EXTRA_ARGS)
    return cmd


def spawn_whisper_server():
    global _child
    if not MODEL_FILE or not os.path.isfile(MODEL_FILE):
        log("FATAL: model file not found: %r (set WHISPER_MODEL / WHISPER_MODEL_FILE)" % MODEL_FILE)
        sys.exit(1)

    cmd = build_backend_cmd()

    log("starting upstream: %s" % " ".join(shlex.quote(c) for c in cmd))
    _child = subprocess.Popen(cmd)

    # If the upstream dies, take the whole container down so it restarts cleanly
    # (docker-compose `restart: unless-stopped`) rather than serving 502s forever.
    def _watch():
        code = _child.wait()
        if code == 0 and not _became_ready.is_set():
            # whisper-server exits 0 (NOT non-zero) on an unrecognized CLI flag -- it
            # prints usage then exit(0) -- and likewise when --convert is set but ffmpeg
            # is missing from the image (both verified in server.cpp v1.8.4). A clean
            # exit BEFORE it ever became ready is almost always one of those, and a bare
            # "exited (code=0)" reads like a graceful shutdown. Say why, loudly (N6).
            # (restart: unless-stopped restarts regardless of code, so the real symptom
            # is a SILENT crash-loop, not a stopped container.)
            log("ERROR: upstream whisper-server exited 0 before it became ready -- likely "
                "an unknown flag in WHISPER_EXTRA_ARGS=%r, or CONVERT=true without ffmpeg "
                "in the image (rebuild with --build-arg INSTALL_FFMPEG=true). Fix it and "
                "recreate the container." % WHISPER_EXTRA_ARGS)
        else:
            log("upstream whisper-server exited (code=%s); shutting down" % code)
        os._exit(1 if code else 0)

    threading.Thread(target=_watch, name="whisper-watch", daemon=True).start()


def _terminate_child():
    if _child and _child.poll() is None:
        try:
            _child.terminate()
            try:
                _child.wait(timeout=10)
            except Exception:
                _child.kill()
        except Exception:
            pass


def backend_ready():
    try:
        c = http.client.HTTPConnection(BACKEND_HOST, BACKEND_PORT, timeout=5)
        c.request("GET", "/health")
        r = c.getresponse()
        r.read()
        c.close()
        ok = r.status == 200
        if ok:
            _became_ready.set()  # once ready, a later exit(0) is a normal stop, not a bad flag (N6)
        return ok
    except Exception:
        return False


def wait_until_ready(timeout):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if backend_ready():
            return True
        if _child and _child.poll() is not None:
            return False  # child already crashed; watcher will exit us
        time.sleep(1.0)
    return False


def _client_disconnected(sock):
    """True iff the client socket is at EOF (the plugin gave up: cancel/shutdown/deadline).

    By this point we've consumed exactly Content-Length bytes from the client, so the only
    thing left to observe on a well-behaved client is a close. select()+MSG_PEEK: readable
    with an EMPTY peek == EOF == gone. Readable WITH data would be a pipelined request
    (unsupported here) -- treat that as 'still connected' so a live client is never aborted.
    Any socket error == gone.
    """
    try:
        r, _, _ = select.select([sock], [], [], 0)
        if not r:
            return False
        return sock.recv(1, socket.MSG_PEEK) == b""
    except (OSError, ValueError):
        return True


def _acquire_inflight_slot(sock):
    """Block until the single inference slot is free, returning True once held.

    Returns False WITHOUT holding the slot if the client vanished while we waited (its
    request was cancelled — running it would waste the slot on output nobody reads). We
    poll the client socket between short semaphore-acquire attempts so a queued-then-cancelled
    request drops out promptly instead of blocking the line behind a long transcription.
    Uncapped (WHISPER_MAX_INFLIGHT=0) => always True immediately. Caller MUST release with
    _release_inflight_slot() iff this returned True.
    """
    if _inflight_sem is None:
        return True
    while True:
        if _inflight_sem.acquire(timeout=CLIENT_POLL_INTERVAL):
            # Got the slot — but if the client gave up during the wait, hand it straight
            # back so we neither run an abandoned job nor strand the slot.
            if _client_disconnected(sock):
                _release_inflight_slot()
                return False
            return True
        if _client_disconnected(sock):
            return False


def _release_inflight_slot():
    if _inflight_sem is not None:
        try:
            _inflight_sem.release()
        except ValueError:
            # BoundedSemaphore over-release: a double-release bug. Never crash the worker
            # over it — log loudly so it's caught, and keep serving.
            log("BUG: inflight semaphore over-released (ignored)")


# --------------------------------------------------------------------------- #
# HTTP handler
# --------------------------------------------------------------------------- #
class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "whisper-subs-worker"
    # Drop a stalled socket (slow-loris / silent client) so it can't wedge the GPU slot (M2).
    timeout = SOCKET_TIMEOUT

    # Quieter, single-line access log.
    def log_message(self, fmt, *args):
        log("%s - %s" % (self.address_string(), fmt % args))

    # -- helpers ----------------------------------------------------------- #
    def _send_json(self, status, payload, close=False):
        body = payload.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        if close:
            self.close_connection = True
            self.send_header("Connection", "close")
        self.end_headers()
        try:
            self.wfile.write(body)
        except Exception:
            pass

    def _readiness(self):
        if backend_ready():
            self._send_json(200, '{"status":"ok"}')
        else:
            self._send_json(503, '{"status":"loading model"}', close=True)

    def _authorized(self):
        if not API_KEY:
            return True
        # Constant-time compare so the key can't be recovered byte-by-byte via response timing (L1).
        return hmac.compare_digest(self.headers.get("Authorization", ""), "Bearer " + API_KEY)

    # -- verbs ------------------------------------------------------------- #
    def do_HEAD(self):
        if self.path in ("/health", TRANSCRIBE_PATH, TRANSLATE_PATH, "/"):
            # HEAD readiness: status only, no body.
            ok = backend_ready()
            self.send_response(200 if ok else 503)
            self.send_header("Content-Length", "0")
            if not ok:
                self.close_connection = True
                self.send_header("Connection", "close")
            self.end_headers()
        else:
            self.send_response(404)
            self.send_header("Content-Length", "0")
            self.close_connection = True
            self.end_headers()

    def do_GET(self):
        # GET on the audio paths is a lightweight readiness probe only; the real
        # work is POST. This lets a healthcheck "hit the transcriptions endpoint"
        # without spending a GPU inference and without needing the API key.
        if self.path in ("/health", TRANSCRIBE_PATH, TRANSLATE_PATH, "/"):
            self._readiness()
        else:
            self._send_json(404, '{"error":"not found"}', close=True)

    def do_POST(self):
        if self.path == TRANSCRIBE_PATH:
            self._proxy_inference(inject_translate=False)
        elif self.path == TRANSLATE_PATH:
            self._proxy_inference(inject_translate=True)
        else:
            self._send_json(404, '{"error":"not found"}', close=True)

    # -- core proxy -------------------------------------------------------- #
    def _proxy_inference(self, inject_translate):
        if not self._authorized():
            self._send_json(401, '{"error":"unauthorized"}', close=True)
            return

        content_type = self.headers.get("Content-Type", "")

        # This adapter frames the body strictly by Content-Length. Reject Transfer-Encoding outright
        # (501) so a `Content-Length` + `Transfer-Encoding: chunked` pair can't desync a front proxy
        # against this origin (TE.CL request smuggling, M3).
        if self.headers.get("Transfer-Encoding"):
            self._send_json(501, '{"error":"transfer-encoding not supported"}', close=True)
            return

        # Exactly one Content-Length, strictly a non-negative decimal. int() is too lenient (it accepts
        # " 10 ", "+10", "-5", "1_000"); reject duplicates/garbage to avoid a CL.CL desync (M3).
        cl_values = self.headers.get_all("Content-Length") or []
        if len(cl_values) != 1 or not cl_values[0].strip().isdigit():
            self._send_json(
                400 if cl_values else 411,
                '{"error":"a single non-negative Content-Length is required"}',
                close=True,
            )
            return
        body_len = int(cl_values[0].strip())

        # Reject an absurd upload before streaming it anywhere (M2).
        if MAX_BODY_BYTES and body_len > MAX_BODY_BYTES:
            self._send_json(413, '{"error":"request body too large"}', close=True)
            return

        prefix = b""
        total_len = body_len
        if inject_translate:
            boundary = _extract_boundary(content_type)
            if not boundary:
                self._send_json(
                    400,
                    '{"error":"translations route requires multipart/form-data"}',
                    close=True,
                )
                return
            # Prepend a well-formed multipart part. The original body already
            # starts with `--boundary...`, so `prefix + body` stays valid and the
            # injected field is simply the first one parsed. No reparse, no buffer.
            prefix = (
                "--%s\r\n"
                'Content-Disposition: form-data; name="translate"\r\n'
                "\r\n"
                "true\r\n" % boundary
            ).encode("latin-1")
            total_len = body_len + len(prefix)

        # Serialise: hold the single inference slot for the whole upstream exchange (body
        # upload + inference + response), so a second request can't pile into whisper-server's
        # accept queue behind this one. A request whose client already vanished while it waited
        # here for the slot is dropped without running. Released in the finally below.
        if not _acquire_inflight_slot(self.connection):
            log("client gone while queued for the inference slot; dropping request")
            self.close_connection = True
            return

        conn = None
        client_gone = threading.Event()
        watch_stop = threading.Event()
        # Distinguishes a timeout while UPLOADING the body from the client (a client/network stall —
        # not an upstream fault) from a timeout while awaiting the INFERENCE (a possible backend wedge).
        # Only the latter may recycle whisper-server.
        upload_done = False
        try:
            conn = http.client.HTTPConnection(
                BACKEND_HOST, BACKEND_PORT, timeout=compute_backend_timeout(total_len)
            )
            conn.putrequest(
                "POST", BACKEND_INFERENCE_PATH, skip_host=True, skip_accept_encoding=True
            )
            conn.putheader("Host", "%s:%d" % (BACKEND_HOST, BACKEND_PORT))
            conn.putheader("Content-Type", content_type)
            conn.putheader("Content-Length", str(total_len))
            conn.endheaders()

            if prefix:
                conn.send(prefix)

            remaining = body_len
            aborted = False
            while remaining > 0:
                chunk = self.rfile.read(min(CHUNK, remaining))
                if not chunk:
                    aborted = True  # client (plugin) closed the body early
                    break
                conn.send(chunk)
                remaining -= len(chunk)

            if aborted:
                # We promised Content-Length bytes we can no longer deliver. The finally resets the
                # upstream connection so whisper-server stops waiting and frees its context.
                self._send_json(400, '{"error":"client closed request body"}', close=True)
                return

            # Body is fully upstream. From here a socket.timeout is the INFERENCE wait, not the upload.
            upload_done = True

            # The whole request is now upstream; whisper-server runs the (possibly very long)
            # inference while we block in getresponse(). Watch the CLIENT socket meanwhile: if the
            # plugin cancels / shuts down / hits its deadline and drops the connection, tear down
            # the UPSTREAM socket. whisper-server's abort_callback polls is_connection_closed()
            # throughout whisper_full (verified in server.cpp v1.8.4), so this actually aborts the
            # in-flight transcription and frees the single GPU inference slot instead of running an
            # abandoned job to completion (M2).
            def _watch_client(upstream=conn, sock=self.connection):
                while True:
                    if _client_disconnected(sock):
                        client_gone.set()
                        s = upstream.sock
                        if s is not None:
                            try:
                                # shutdown() (not merely close()) wakes the getresponse()
                                # blocked in this request's thread and signals EOF upstream.
                                s.shutdown(socket.SHUT_RDWR)
                            except OSError:
                                pass
                        return
                    if watch_stop.wait(CLIENT_POLL_INTERVAL):
                        return

            threading.Thread(target=_watch_client, name="client-watch", daemon=True).start()

            resp = conn.getresponse()
            payload = resp.read()  # SRT / JSON responses are small text bodies
            resp_ctype = resp.getheader("Content-Type", "text/plain; charset=utf-8")
        except (socket.timeout, ConnectionError, OSError, http.client.HTTPException) as exc:
            if client_gone.is_set():
                # We deliberately tore the upstream down because the client vanished; this is the
                # intended outcome, not an upstream fault. The slot is freed; nothing to reply to.
                log("client disconnected during inference; aborted upstream to free the slot")
                self.close_connection = True
                return
            if upload_done and isinstance(exc, socket.timeout):
                # We were the ONLY in-flight request (serialised) and the body was fully uploaded, yet
                # got no response within the (audio-length-scaled) backstop: whisper-server is wedged —
                # stuck in a decode loop that never honoured the abort — not merely slow. Recycle the
                # backend so the worker self-heals: the lifecycle watch thread restarts the process
                # cleanly (launchd KeepAlive / compose restart) and the plugin requeues the job.
                # (A timeout BEFORE upload_done is a client/upload stall, handled as a 502 below —
                # never a backend kill.)
                log("upstream inference exceeded %.0fs with no response — whisper-server wedged; "
                    "recycling the backend to recover" % compute_backend_timeout(total_len))
                self._send_json(503, '{"error":"upstream whisper-server wedged; restarting"}', close=True)
                try:
                    self.wfile.flush()  # get the 503 out before the watch thread os._exit()s us
                except Exception:
                    pass
                _terminate_child()
                return
            log("upstream error: %r" % exc)
            self._send_json(502, '{"error":"upstream whisper-server unavailable"}', close=True)
            return
        finally:
            # Stop the watcher, always release the upstream socket — including the exception path,
            # which previously relied on GC to close it (L4). close() is safe to call twice — then
            # hand back the inference slot so the next queued request can proceed.
            watch_stop.set()
            if conn is not None:
                conn.close()
            _release_inflight_slot()

        # Relay upstream status + body back to the plugin verbatim.
        self.send_response(resp.status)
        self.send_header("Content-Type", resp_ctype)
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        try:
            self.wfile.write(payload)
        except Exception:
            pass


def _extract_boundary(content_type):
    # e.g. 'multipart/form-data; boundary=----XYZ' (boundary may be quoted).
    if "multipart/form-data" not in content_type.lower():
        return None
    for part in content_type.split(";"):
        part = part.strip()
        if part.lower().startswith("boundary="):
            b = part[len("boundary="):].strip()
            if len(b) >= 2 and b[0] == '"' and b[-1] == '"':
                b = b[1:-1]
            return b
    return None


# --------------------------------------------------------------------------- #
# main
# --------------------------------------------------------------------------- #
def main():
    def _term(signum, _frame):
        log("received signal %s; stopping" % signum)
        _terminate_child()
        os._exit(0)

    signal.signal(signal.SIGTERM, _term)
    signal.signal(signal.SIGINT, _term)

    spawn_whisper_server()

    log("waiting for upstream model to load (timeout=%ds)..." % READY_TIMEOUT)
    if not wait_until_ready(READY_TIMEOUT):
        log("upstream not ready within %ds; continuing to serve (readiness=503 until loaded)" % READY_TIMEOUT)
    else:
        log("upstream ready; model loaded")

    if not API_KEY and LISTEN_HOST not in ("127.0.0.1", "localhost", "::1"):
        log("WARNING: serving on %s with NO API_KEY — this transcription endpoint is UNAUTHENTICATED. "
            "Run it only on a trusted network; set API_KEY (and put TLS in front) before exposing it "
            "to any untrusted network or the internet (M1)." % LISTEN_HOST)

    httpd = ThreadingHTTPServer((LISTEN_HOST, LISTEN_PORT), Handler)
    httpd.daemon_threads = True
    log("listening on %s:%d -> upstream %s:%d%s (auth=%s, convert=%s)" % (
        LISTEN_HOST, LISTEN_PORT, BACKEND_HOST, BACKEND_PORT, BACKEND_INFERENCE_PATH,
        "on" if API_KEY else "off", "on" if WHISPER_CONVERT else "off",
    ))
    try:
        httpd.serve_forever()
    finally:
        _terminate_child()


if __name__ == "__main__":
    main()
