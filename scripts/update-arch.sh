#!/usr/bin/env bash
# In-app updater for the Arch package. Opened in a terminal by ShadowWhispr's
# update prompt so the user drives the update themselves: it fetches the
# PKGBUILD of the newest release, rebuilds the package with makepkg (pacman
# stays the owner of the install, sudo asks for the user's own password), and
# restarts ShadowWhispr when done.

set -u -o pipefail

REPO="shadowdoggie/ShadowWhispr"
STATE_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/ShadowWhispr"
LOG="$STATE_DIR/update-log.txt"

mkdir -p "$STATE_DIR"
exec > >(tee -a "$LOG") 2>&1
echo "=== ShadowWhispr update started $(date '+%Y-%m-%d %H:%M:%S') ==="

finish() {
    echo ""
    read -rp "Press Enter to close this window..." _ 2>/dev/null || true
}

fail() {
    echo ""
    echo "UPDATE FAILED: $1"
    echo "A full log was saved to: $LOG"
    finish
    exit 1
}

command -v makepkg >/dev/null 2>&1 || fail \
    "makepkg was not found — this updater is for Arch-based systems. Get the new build from https://github.com/$REPO/releases/latest"

echo "Checking the latest ShadowWhispr release..."
TAG="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" | grep -m1 '"tag_name"' | cut -d'"' -f4)"
[ -n "$TAG" ] || fail "Could not read the latest release from GitHub. Check your internet connection and try again."

INSTALLED="$(pacman -Q shadowwhispr-bin 2>/dev/null | awk '{print $2}' | cut -d- -f1 || true)"
echo "Latest release: $TAG — installed: ${INSTALLED:-not installed via pacman}"

if [ -n "$INSTALLED" ] && [ "v$INSTALLED" = "$TAG" ]; then
    echo "You are already on the latest version."
    finish
    exit 0
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cd "$WORK" || fail "Could not enter a temporary build directory."

echo "Fetching the $TAG package definition..."
curl -fsSLO "https://raw.githubusercontent.com/$REPO/$TAG/packaging/arch/PKGBUILD" || fail "Could not download the PKGBUILD."
curl -fsSLO "https://raw.githubusercontent.com/$REPO/$TAG/packaging/arch/shadowwhispr.install" || fail "Could not download the install file."

echo ""
echo "Building and installing with makepkg — pacman will ask for your password."
echo ""
makepkg -si || fail "makepkg did not finish. Nothing was changed if it failed before the install step."

echo ""
echo "Update to $TAG finished. ShadowWhispr will now restart."
pkill -x shadowwhispr 2>/dev/null || true
sleep 1
(nohup shadowwhispr >/dev/null 2>&1 &)
finish
