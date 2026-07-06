#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Container entrypoint for the whisper-subs worker image.
#   1. Resolve + (first run) download the requested GGML model into the cache volume.
#   2. Self-heal a stale VK_ICD_FILENAMES so Vulkan can always fall back to auto-discovery.
#   3. Hand off (exec) to the Python adapter, which spawns and supervises whisper-server.
set -eu

MODEL_DIR="${WHISPER_MODEL_DIR:-/models}"
MODEL_NAME="${WHISPER_MODEL:-large-v3-turbo-q5_0}"
MODEL_FILE="${MODEL_DIR}/ggml-${MODEL_NAME}.bin"

mkdir -p "$MODEL_DIR"

# Integrity-check a model file so a truncated download or an HTML error page can't be
# cached in place and crash-loop the container forever: download-ggml-model.sh fetches
# with curl WITHOUT -f and does no checksum, so a 404/partial is happily saved as
# ggml-<model>.bin. Require a plausible minimum size AND the ggml magic. whisper.cpp
# stores GGML_FILE_MAGIC (0x67676d6c) little-endian, so a valid file's first four bytes
# are literally "lmgg" (verified against real Hugging Face models + whisper.cpp v1.8.4,
# incl. the quantized default). Returns non-zero on a bad file.
validate_model() {
    _f="$1"
    [ -f "$_f" ] || { echo "[entrypoint] ERROR: model file missing: ${_f}" >&2; return 1; }
    _size=$(stat -c %s "$_f" 2>/dev/null || echo 0)
    if [ "${_size:-0}" -lt 1048576 ]; then
        echo "[entrypoint] ERROR: model file implausibly small (${_size} bytes < 1 MiB): ${_f}" >&2
        return 1
    fi
    _magic=$(head -c 4 "$_f" 2>/dev/null || true)
    if [ "$_magic" != "lmgg" ]; then
        echo "[entrypoint] ERROR: model file is not a ggml model (bad magic: '${_magic}'): ${_f}" >&2
        return 1
    fi
    return 0
}

if [ ! -f "$MODEL_FILE" ]; then
    echo "[entrypoint] model '${MODEL_NAME}' not present; downloading into ${MODEL_DIR} ..."
    # download-ggml-model.sh validates the name against whisper.cpp's known list and
    # fetches ggml-<model>.bin from huggingface.co/ggerganov/whisper.cpp.
    if ! download-ggml-model.sh "$MODEL_NAME" "$MODEL_DIR"; then
        echo "[entrypoint] FATAL: model download failed for '${MODEL_NAME}'." >&2
        echo "[entrypoint] check WHISPER_MODEL is a valid whisper.cpp model name." >&2
        rm -f "$MODEL_FILE"
        exit 1
    fi
fi

if [ ! -f "$MODEL_FILE" ]; then
    echo "[entrypoint] FATAL: model file still missing after download: ${MODEL_FILE}" >&2
    echo "[entrypoint] check WHISPER_MODEL is a valid whisper.cpp model name." >&2
    exit 1
fi

# Validate on EVERY start (freshly downloaded OR cached): a corrupt file left by a prior
# run would otherwise skip the download branch above and crash-loop whisper-server on
# load. On a bad file, delete it and fail loudly so the next restart re-fetches a clean
# copy instead of looping forever on the same corruption (M4). The check is O(1) (fstat
# for size + 4 bytes for magic), so re-validating a multi-GB cached model is cheap.
if ! validate_model "$MODEL_FILE"; then
    echo "[entrypoint] FATAL: model failed integrity check; deleting so the next start re-downloads: ${MODEL_FILE}" >&2
    rm -f "$MODEL_FILE"
    exit 1
fi
echo "[entrypoint] model integrity OK (ggml magic, >=1 MiB): ${MODEL_FILE}"

# Vulkan: if a specific ICD was pinned but does not exist in this image/host, drop
# it so the loader auto-discovers whatever driver IS present (Intel ANV, AMD RADV, ...).
if [ -n "${VK_ICD_FILENAMES:-}" ] && [ ! -e "${VK_ICD_FILENAMES}" ]; then
    echo "[entrypoint] WARN: VK_ICD_FILENAMES='${VK_ICD_FILENAMES}' not found; unsetting for Vulkan auto-discovery." >&2
    unset VK_ICD_FILENAMES
fi

export WHISPER_MODEL_FILE="$MODEL_FILE"
echo "[entrypoint] serving model: ${MODEL_FILE}"

exec python3 /opt/whisper-adapter/adapter.py
