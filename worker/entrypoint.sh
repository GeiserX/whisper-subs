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

if [ ! -f "$MODEL_FILE" ]; then
    echo "[entrypoint] model '${MODEL_NAME}' not present; downloading into ${MODEL_DIR} ..."
    # download-ggml-model.sh validates the name against whisper.cpp's known list and
    # fetches ggml-<model>.bin from huggingface.co/ggerganov/whisper.cpp.
    download-ggml-model.sh "$MODEL_NAME" "$MODEL_DIR"
fi

if [ ! -f "$MODEL_FILE" ]; then
    echo "[entrypoint] FATAL: model file still missing after download: ${MODEL_FILE}" >&2
    echo "[entrypoint] check WHISPER_MODEL is a valid whisper.cpp model name." >&2
    exit 1
fi

# Vulkan: if a specific ICD was pinned but does not exist in this image/host, drop
# it so the loader auto-discovers whatever driver IS present (Intel ANV, AMD RADV, ...).
if [ -n "${VK_ICD_FILENAMES:-}" ] && [ ! -e "${VK_ICD_FILENAMES}" ]; then
    echo "[entrypoint] WARN: VK_ICD_FILENAMES='${VK_ICD_FILENAMES}' not found; unsetting for Vulkan auto-discovery." >&2
    unset VK_ICD_FILENAMES
fi

export WHISPER_MODEL_FILE="$MODEL_FILE"
echo "[entrypoint] serving model: ${MODEL_FILE}"

exec python3 /opt/whisper-adapter/adapter.py
