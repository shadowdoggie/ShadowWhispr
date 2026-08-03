#!/usr/bin/env bash
# One-time setup for ShadowWhispr's local speech engine on Linux.
#
# Builds a Python 3.12 environment, installs Parakeet v3 + CUDA PyTorch and
# downloads the model — all under the user's own data directory
# (~/.local/share/ShadowWhispr), because the app itself may live somewhere
# read-only like /usr. Requires an NVIDIA GPU.
#
# Like the app, this never touches a system Python: a pinned standalone
# CPython 3.12 is fetched through uv, which is itself downloaded into the data
# directory when not already installed.
#
# Progress is emitted as machine-readable "##SW## percent|message" lines so
# ShadowWhispr can show an in-app progress screen; failures emit
# "##SWERR## message". Everything is also logged to setup-log.txt in the data
# directory.

set -u -o pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/ShadowWhispr"
VENV="$DATA_DIR/.venv"
VENV_PYTHON="$VENV/bin/python"
REQUIREMENTS="$APP_ROOT/stt/requirements.txt"
LOG="$DATA_DIR/setup-log.txt"
MODEL_DIR="$DATA_DIR/speech-model"
PYTHON_VERSION="3.12"

mkdir -p "$DATA_DIR"
exec > >(tee -a "$LOG") 2>&1
echo "=== ShadowWhispr speech setup started $(date '+%Y-%m-%d %H:%M:%S') ==="

step() {
    echo ""
    echo "[$1%] $2"
    echo "##SW## $1|$2"
}

fail() {
    echo ""
    echo "##SWERR## $1"
    echo "SETUP FAILED: $1"
    echo "A full log was saved to: $LOG"
    exit 1
}

# Runs a command, retrying genuine transient failures. Every attempt's output
# is echoed so setup-log.txt shows what actually went wrong.
retry() {
    local description="$1"; shift
    local attempt
    for attempt in 1 2 3; do
        if "$@"; then
            return 0
        fi
        if [ "$attempt" -lt 3 ]; then
            echo ""
            echo "$description failed (attempt $attempt of 3). Retrying in 10 seconds..."
            sleep 10
        fi
    done
    fail "$description failed after 3 attempts. See $LOG for the real error."
}

# --- Locate or fetch uv, which supplies the pinned Python ------------------
step 2 "Preparing ShadowWhispr's Python"

UV=""
if command -v uv >/dev/null 2>&1; then
    UV="$(command -v uv)"
elif [ -x "$DATA_DIR/uv/uv" ]; then
    UV="$DATA_DIR/uv/uv"
else
    echo "uv not found; downloading it into $DATA_DIR/uv"
    retry "Downloading uv" bash -c \
        "curl -LsSf https://astral.sh/uv/install.sh | UV_INSTALL_DIR='$DATA_DIR/uv' UV_NO_MODIFY_PATH=1 sh"
    UV="$DATA_DIR/uv/uv"
fi
[ -x "$UV" ] || fail "uv could not be installed; install it from your package manager (pacman -S uv) and run setup again."
echo "Using uv: $UV"

# --- Build the local environment -------------------------------------------
venv_usable() {
    [ -x "$VENV_PYTHON" ] && "$VENV_PYTHON" -c "import sys; assert sys.version_info[:2] == (3, 12)" >/dev/null 2>&1
}

if venv_usable; then
    echo "The existing environment works; keeping it."
else
    step 14 "Creating the local Python environment"
    rm -rf "$VENV"
    retry "Creating the Python environment" "$UV" venv --python "$PYTHON_VERSION" "$VENV"
    venv_usable || fail "The local Python environment was created but does not run."
fi

[ -f "$REQUIREMENTS" ] || fail "The package list is missing from $REQUIREMENTS. Please reinstall ShadowWhispr."

step 22 "Downloading speech and CUDA packages (about 2 GB)"
retry "Installing the speech-to-text packages" \
    "$UV" pip install --python "$VENV_PYTHON" --requirement "$REQUIREMENTS"

step 58 "Checking your NVIDIA GPU"
if ! "$VENV_PYTHON" -c "import torch; assert torch.cuda.is_available(), 'CUDA is unavailable'; print('CUDA ready:', torch.cuda.get_device_name(0))"; then
    fail "PyTorch cannot use the NVIDIA GPU. Check that you have an NVIDIA GPU and current driver, then run this again."
fi

# The worker resolves the model folder relative to itself, so it (and its
# requirements pin) live in the data directory alongside everything else.
mkdir -p "$DATA_DIR/stt"
cp "$APP_ROOT/stt/worker.py" "$DATA_DIR/stt/worker.py" || fail "Could not copy the speech worker into $DATA_DIR."
cp "$REQUIREMENTS" "$DATA_DIR/stt/requirements.txt" || true

step 62 "Downloading the speech model (about 2.4 GB)"
retry "Downloading the speech model" "$VENV_PYTHON" - <<EOF
from huggingface_hub import snapshot_download
snapshot_download(
    'nvidia/parakeet-tdt-0.6b-v3',
    revision='7c35754d166cca382ad1e53e68b01e7c575f3a1d',
    local_dir=r'$MODEL_DIR',
    allow_patterns=[
        'config.json',
        'generation_config.json',
        'model.safetensors',
        'processor_config.json',
        'tokenizer.json',
        'tokenizer_config.json',
    ],
)
EOF

# Prove the engine really starts before declaring setup finished: launch the
# worker exactly like the app does and wait for its ready message.
step 90 "Starting the speech engine for the first time"
READY_LINE=""
for attempt in 1 2; do
    READY_LINE="$(cd "$DATA_DIR" && timeout 300 "$VENV_PYTHON" "$DATA_DIR/stt/worker.py" --server </dev/null | head -n 1 || true)"
    case "$READY_LINE" in
        *'"ready":true'*) break ;;
    esac
    if [ "$attempt" -lt 2 ]; then
        echo "The engine did not start on the first check. Waiting 15 seconds and trying once more..."
        sleep 15
    fi
done
case "$READY_LINE" in
    *'"ready":true'*) echo "Speech engine verified." ;;
    *) fail "The speech engine could not start. It reported: ${READY_LINE:-nothing}" ;;
esac

# Written last: the app treats setup as finished only when this file exists,
# so an interrupted setup is retried instead of half-loading.
echo ok > "$VENV/setup-complete"

step 100 "Setup complete"
echo "Speech-to-text setup is ready. You can close this window and use ShadowWhispr."
