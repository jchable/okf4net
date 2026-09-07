#!/bin/sh
# SPDX-License-Identifier: LGPL-3.0-or-later
#
# install.sh -- downloads and installs a prebuilt OKF4net Native AOT binary
# (okf or okf-render) from a GitHub Release, verifying its SHA-256 checksum
# before anything is put on PATH.
#
# Usage:
#   curl -sSL https://raw.githubusercontent.com/jchable/okf4net/main/packaging/install.sh | sh
#   curl -sSL .../install.sh | sh -s -- --bin okf-render --version v0.6.0
#
# Options (each also settable via an env var; a flag wins over its env var):
#   --bin <okf|okf-render>   Which binary to install.
#                              [env OKF_INSTALL_BIN, default: okf]
#   --version <tag>          Release tag to install, e.g. v0.5.0.
#                              [env OKF_INSTALL_VERSION, default: latest release]
#   --dir <path>             Install destination.
#                              [env OKF_INSTALL_DIR, default: see below]
#   --dry-run                Resolve and print the plan; download, verify and
#                             install nothing.
#                              [env OKF_INSTALL_DRY_RUN=1]
#   -h, --help               Show this help and exit.
#
# Install destination defaults to /usr/local/bin when running as root or when
# that directory is writable by the current user, otherwise ~/.local/bin
# (created if it does not exist yet). This script never invokes sudo -- if
# neither location is writable, pass --dir yourself.
#
# Supported platforms: Linux and macOS, on x86_64/amd64 or aarch64/arm64.
# Windows is not supported by this script -- see the winget instructions in
# the project README, or download a .zip release asset by hand.
#
# Requires curl or wget to fetch, tar to unpack, and sha256sum or
# `shasum -a 256` to verify. Checksum verification is mandatory: if neither
# sha256sum nor shasum is available, this script refuses to install rather
# than silently skip verification.
#
# POSIX sh, not bash -- it is meant to be piped into `sh`. `local` is used
# throughout for function-scoped variables even though it is not in POSIX
# proper: it is a builtin in dash (Debian/Ubuntu's /bin/sh), bash (macOS's
# /bin/sh and most Linux distros' bash), and BusyBox ash, which covers every
# shell this script is realistically invoked under.

set -eu

REPO="jchable/okf4net"

BIN="${OKF_INSTALL_BIN:-okf}"
VERSION="${OKF_INSTALL_VERSION:-}"
DEST_DIR="${OKF_INSTALL_DIR:-}"
case "${OKF_INSTALL_DRY_RUN:-0}" in
  1 | true | yes) DRY_RUN=1 ;;
  *) DRY_RUN=0 ;;
esac

usage() {
  cat <<'EOF'
install.sh -- install a prebuilt okf/okf-render binary from a GitHub Release

Usage:
  install.sh [options]

Options:
  --bin <okf|okf-render>   Which binary to install (default: okf)
  --version <tag>          Release tag to install, e.g. v0.5.0 (default: latest)
  --dir <path>             Install destination (default: /usr/local/bin if
                            writable/root, else ~/.local/bin)
  --dry-run                Print the resolved plan; download/verify/install nothing
  -h, --help               Show this help and exit

Env var equivalents: OKF_INSTALL_BIN, OKF_INSTALL_VERSION, OKF_INSTALL_DIR,
OKF_INSTALL_DRY_RUN=1. A flag wins over its env var.

Supported platforms: Linux and macOS, x86_64/amd64 or aarch64/arm64.
EOF
}

err() { printf 'install.sh: error: %s\n' "$1" >&2; }
info() { printf 'install.sh: %s\n' "$1"; }
have() { command -v "$1" >/dev/null 2>&1; }

# ---- argument parsing ------------------------------------------------------

while [ $# -gt 0 ]; do
  case "$1" in
    --bin)
      [ $# -ge 2 ] || { err "--bin requires a value"; exit 1; }
      BIN=$2
      shift 2
      ;;
    --bin=*) BIN=${1#--bin=}; shift ;;
    --version)
      [ $# -ge 2 ] || { err "--version requires a value"; exit 1; }
      VERSION=$2
      shift 2
      ;;
    --version=*) VERSION=${1#--version=}; shift ;;
    --dir)
      [ $# -ge 2 ] || { err "--dir requires a value"; exit 1; }
      DEST_DIR=$2
      shift 2
      ;;
    --dir=*) DEST_DIR=${1#--dir=}; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    -h | --help) usage; exit 0 ;;
    --) shift; break ;;
    *)
      err "unknown argument: $1"
      usage >&2
      exit 1
      ;;
  esac
done

case "$BIN" in
  okf | okf-render) ;;
  *)
    err "unsupported --bin value '$BIN' (expected 'okf' or 'okf-render')"
    exit 1
    ;;
esac

# ---- OS / architecture detection -------------------------------------------

detect_os() {
  case "$(uname -s)" in
    Linux) printf '%s\n' linux ;;
    Darwin) printf '%s\n' osx ;;
    *) return 1 ;;
  esac
}

detect_arch() {
  case "$(uname -m)" in
    x86_64 | amd64) printf '%s\n' x64 ;;
    aarch64 | arm64) printf '%s\n' arm64 ;;
    *) return 1 ;;
  esac
}

if ! OS=$(detect_os); then
  err "unsupported OS: $(uname -s) -- this installer supports Linux and macOS only."
  err "Windows users: install via winget (see the README) or grab a .zip release asset by hand."
  exit 1
fi

if ! ARCH=$(detect_arch); then
  err "unsupported architecture: $(uname -m) -- supported: x86_64/amd64, aarch64/arm64."
  exit 1
fi

RID="$OS-$ARCH"

# ---- checksum tool (fail fast, before any network activity) ---------------

sha256_of() {
  if have sha256sum; then
    sha256sum "$1"
  elif have shasum; then
    shasum -a 256 "$1"
  else
    return 1
  fi
}

if ! have sha256sum && ! have shasum; then
  err "no SHA-256 tool found (need sha256sum or shasum -a 256)."
  err "refusing to install without checksum verification."
  exit 1
fi

# ---- HTTP helpers -----------------------------------------------------------

fetch_to_stdout() {
  if have curl; then
    curl -fsSL "$1"
  elif have wget; then
    wget -qO- "$1"
  else
    return 127
  fi
}

fetch_to_file() {
  if have curl; then
    curl -fsSL -o "$2" "$1"
  elif have wget; then
    wget -q -O "$2" "$1"
  else
    return 127
  fi
}

if ! have curl && ! have wget; then
  err "neither curl nor wget is available -- cannot download a release."
  exit 1
fi

# ---- resolve the version tag -----------------------------------------------

resolve_latest_tag() {
  # GitHub's release API returns JSON; pull tag_name out with sed instead of
  # depending on jq, which is not installed everywhere this script runs.
  fetch_to_stdout "https://api.github.com/repos/$REPO/releases/latest" \
    | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' \
    | head -n 1
}

if [ -z "$VERSION" ]; then
  TAG=$(resolve_latest_tag) || TAG=""
  if [ -z "$TAG" ]; then
    err "could not resolve the latest release from the GitHub API."
    err "pass --version <tag> explicitly (e.g. --version v0.5.0) to skip this lookup."
    exit 1
  fi
else
  TAG="$VERSION"
fi

case "$TAG" in
  v*) : ;;
  *) TAG="v$TAG" ;;
esac
ASSET_VERSION=${TAG#v}

ARCHIVE="$BIN-$ASSET_VERSION-$RID.tar.gz"
ARCHIVE_URL="https://github.com/$REPO/releases/download/$TAG/$ARCHIVE"
SHA_URL="$ARCHIVE_URL.sha256"

# ---- resolve the install destination ---------------------------------------

if [ -z "$DEST_DIR" ]; then
  if [ "$(id -u)" -eq 0 ] || [ -w /usr/local/bin ]; then
    DEST_DIR=/usr/local/bin
  else
    DEST_DIR="$HOME/.local/bin"
    info "/usr/local/bin is not writable; installing to $DEST_DIR instead (pass --dir to override, this script never uses sudo)."
  fi
fi
TARGET="$DEST_DIR/$BIN"

if [ "$DRY_RUN" = 1 ]; then
  info "dry run -- would install '$BIN' $TAG for $RID"
  info "  archive:      $ARCHIVE_URL"
  info "  checksum:     $SHA_URL"
  info "  destination:  $TARGET"
  exit 0
fi

# ---- download, verify, extract, install ------------------------------------

WORK_DIR=$(mktemp -d "${TMPDIR:-/tmp}/okf-install.XXXXXX")
cleanup() { rm -rf "$WORK_DIR"; }
trap cleanup EXIT INT TERM HUP

ARCHIVE_PATH="$WORK_DIR/$ARCHIVE"
SHA_PATH="$WORK_DIR/$ARCHIVE.sha256"

info "downloading $ARCHIVE_URL"
if ! fetch_to_file "$ARCHIVE_URL" "$ARCHIVE_PATH"; then
  err "download failed: $ARCHIVE_URL"
  err "does release $TAG publish a $RID archive for '$BIN'?"
  exit 1
fi
if ! fetch_to_file "$SHA_URL" "$SHA_PATH"; then
  err "download failed: $SHA_URL"
  exit 1
fi

expected_sha=$(awk '{print tolower($1)}' "$SHA_PATH")
actual_sha=$(sha256_of "$ARCHIVE_PATH" | awk '{print tolower($1)}')
if [ -z "$expected_sha" ] || [ "$expected_sha" != "$actual_sha" ]; then
  err "checksum mismatch for $ARCHIVE"
  err "  expected: $expected_sha"
  err "  actual:   $actual_sha"
  exit 1
fi
info "checksum OK ($actual_sha)"

tar -xzf "$ARCHIVE_PATH" -C "$WORK_DIR"
EXTRACTED="$WORK_DIR/$BIN"
if [ ! -f "$EXTRACTED" ]; then
  err "archive $ARCHIVE did not contain the expected '$BIN' binary"
  exit 1
fi
chmod +x "$EXTRACTED"

if ! "$EXTRACTED" --version >/dev/null 2>&1; then
  err "the downloaded binary did not run ('$EXTRACTED' --version failed) -- not installing it"
  exit 1
fi

mkdir -p "$DEST_DIR" || { err "could not create install directory: $DEST_DIR"; exit 1; }
mv -f "$EXTRACTED" "$TARGET"
chmod +x "$TARGET"

if ! version_output=$("$TARGET" --version 2>&1); then
  err "installed to $TARGET but it failed to run from there -- check permissions"
  exit 1
fi
info "installed: $version_output"
info "location:  $TARGET"

case ":$PATH:" in
  *":$DEST_DIR:"*) ;;
  *) info "note: $DEST_DIR is not on your PATH -- add it, e.g. export PATH=\"$DEST_DIR:\$PATH\"" ;;
esac
