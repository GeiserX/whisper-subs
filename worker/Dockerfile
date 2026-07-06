# SPDX-License-Identifier: GPL-3.0-or-later
#
# whisper-subs worker image: whisper.cpp `whisper-server` (Vulkan, static) fronted
# by a tiny stdlib-only Python adapter that speaks the plugin's OpenAI-audio dialect.
#
# Build (Intel/AMD iGPU via Vulkan, the shipped default):
#   docker build -t drumsergio/whisper-subs-worker:0.1.0 worker/
#
# Other backends are a one-line flag change (see worker/README.md):
#   CPU-only : --build-arg WHISPER_CMAKE_FLAGS="-DGGML_NATIVE=OFF -DGGML_AVX=ON -DGGML_AVX2=ON -DGGML_FMA=ON -DGGML_F16C=ON"
#   +ffmpeg  : --build-arg INSTALL_FFMPEG=true      (accept non-WAV audio from other clients)
# CUDA/ROCm need a different base image + runtime libs -- documented in the README.

# Pin base images by explicit tag (never :latest). For fully reproducible CI builds,
# replace with a digest pin, e.g. debian:trixie-slim@sha256:<digest>.
ARG BUILD_IMAGE=debian:trixie-slim
ARG RUNTIME_IMAGE=debian:trixie-slim

# --------------------------------------------------------------------------- #
# Stage 1: build whisper-server (static, Vulkan) from a pinned whisper.cpp tag
# --------------------------------------------------------------------------- #
FROM ${BUILD_IMAGE} AS build

# Keep in lockstep with the plugin's pinned whisper.cpp (CLAUDE.md / build-release.yml).
ARG WHISPER_VERSION=v1.8.4
# Mirrors the plugin's `vulkan` CI matrix flags exactly.
ARG WHISPER_CMAKE_FLAGS="-DGGML_VULKAN=ON -DGGML_NATIVE=OFF -DGGML_AVX=ON -DGGML_AVX2=ON -DGGML_FMA=ON -DGGML_F16C=ON -DGGML_BMI2=ON"

RUN apt-get update && apt-get install -y --no-install-recommends \
        git cmake g++ make pkg-config ca-certificates \
        libvulkan-dev glslc \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
RUN git clone --depth 1 --branch "${WHISPER_VERSION}" \
        https://github.com/ggml-org/whisper.cpp.git .

# -DBUILD_SHARED_LIBS=OFF is MANDATORY (static link; see plugin CLAUDE.md).
# static libgcc/libstdc++ avoids GLIBCXX drift between build and runtime layers.
RUN cmake -B build \
        -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_SHARED_LIBS=OFF \
        -DCMAKE_EXE_LINKER_FLAGS="-static-libgcc -static-libstdc++" \
        ${WHISPER_CMAKE_FLAGS} \
    && cmake --build build --config Release --target whisper-server -j"$(nproc)" \
    && test -x build/bin/whisper-server

# --------------------------------------------------------------------------- #
# Stage 2: slim runtime
# --------------------------------------------------------------------------- #
FROM ${RUNTIME_IMAGE} AS runtime

ARG INSTALL_FFMPEG=false
# Vulkan loader + Mesa ICDs (Intel ANV / AMD RADV) are the shipped default backend, so
# ON by default. CPU-only and CUDA/ROCm builds don't use them and can slim the runtime
# image with --build-arg INSTALL_VULKAN=false (N2).
ARG INSTALL_VULKAN=true
# uid for the non-root `whisper` user. Override when the base image already ships a
# uid-1000 account: nvidia/cuda:*-ubuntu24.04 has a uid-1000 `ubuntu` user, so a plain
# useradd --uid 1000 collides -- build the CUDA variant with --build-arg APP_UID=1001 (M1).
ARG APP_UID=1000

# Runtime deps:
#   python3        -> the adapter (standard library only)
#   tini           -> PID 1, reaps the whisper-server child, forwards signals
#   curl           -> Docker healthcheck + the model downloader
#   ca-certificates-> HTTPS to Hugging Face for model download
#   libgomp1       -> OpenMP runtime used by the whisper.cpp CPU paths
#   libvulkan1 + mesa-vulkan-drivers (INSTALL_VULKAN=true) -> Vulkan loader + Intel
#                     (ANV) / AMD (RADV) drivers + ICD JSONs
RUN apt-get update && apt-get install -y --no-install-recommends \
        python3 tini curl ca-certificates libgomp1 \
    && if [ "$INSTALL_VULKAN" = "true" ]; then \
           apt-get install -y --no-install-recommends libvulkan1 mesa-vulkan-drivers; \
       fi \
    && if [ "$INSTALL_FFMPEG" = "true" ]; then \
           apt-get install -y --no-install-recommends ffmpeg; \
       fi \
    && rm -rf /var/lib/apt/lists/*

# Non-root user; own the model cache so a named volume is writable on first run.
#   groupadd -f -r render : create the `render` group so the compose's `group_add`
#     resolves the NAME on this Debian base (which otherwise has no such group, making
#     `docker compose up` hard-fail with "unable to find group render"). The GID here
#     does NOT match the host's /dev/dri owner -- the compose still must pin the host's
#     numeric render GID for actual GPU access (H1). -f => no-op if the base image
#     (e.g. a CUDA/Ubuntu runtime) already ships a `render` group.
#   --uid ${APP_UID} : avoid the uid-1000 collision on Ubuntu/CUDA bases (M1).
RUN groupadd -f -r render \
    && useradd --uid ${APP_UID} --create-home --shell /usr/sbin/nologin whisper \
    && mkdir -p /models /opt/whisper-adapter \
    && chown -R whisper:whisper /models

COPY --from=build /src/build/bin/whisper-server        /usr/local/bin/whisper-server
COPY --from=build /src/models/download-ggml-model.sh   /usr/local/bin/download-ggml-model.sh
COPY adapter.py     /opt/whisper-adapter/adapter.py
COPY entrypoint.sh  /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/whisper-server \
             /usr/local/bin/download-ggml-model.sh \
             /usr/local/bin/entrypoint.sh

ENV WHISPER_MODEL=large-v3-turbo-q5_0 \
    WHISPER_MODEL_DIR=/models \
    WHISPER_BIN=/usr/local/bin/whisper-server \
    WHISPER_LANGUAGE=auto \
    LISTEN_HOST=0.0.0.0 \
    LISTEN_PORT=8080 \
    WHISPER_BACKEND_HOST=127.0.0.1 \
    WHISPER_BACKEND_PORT=8081 \
    CONVERT=false

EXPOSE 8080
VOLUME ["/models"]

USER whisper
WORKDIR /opt/whisper-adapter

# tini as PID 1 so the whisper-server grandchild is reaped and SIGTERM is forwarded.
ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/entrypoint.sh"]
