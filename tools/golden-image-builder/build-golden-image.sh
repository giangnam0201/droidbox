#!/usr/bin/env bash
# Builds one golden qcow2 for a single android-x86 version:
#   1. Install android-x86 to a fresh disk image (automated via QMP key sends
#      against the installer's TUI menus — there is no scriptable answer file).
#   2. Patch the installed system OFFLINE (qemu-nbd + squashfs-tools) to disable
#      the setup wizard and enable adb-over-tcp, instead of trying to drive that
#      through the guest UI. This is the reliable half; step 1 is the part most
#      likely to need retiming if a version's installer menu differs.
set -euo pipefail

VERSION=""
ISO_URL=""
OUT=""
DISK_GB=8
RAM_MB=2048

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2 ;;
    --iso-url) ISO_URL="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --disk-gb) DISK_GB="$2"; shift 2 ;;
    --ram-mb) RAM_MB="$2"; shift 2 ;;
    *) echo "Unknown arg: $1" >&2; exit 1 ;;
  esac
done

[[ -n "$VERSION" && -n "$ISO_URL" && -n "$OUT" ]] || {
  echo "Usage: $0 --version 7.1 --iso-url <url> --out golden.qcow2" >&2
  exit 1
}

WORKDIR="$(mktemp -d)"
ISO_PATH="$WORKDIR/android-$VERSION.iso"
RAW_DISK="$WORKDIR/disk.qcow2"
QMP_SOCK="$WORKDIR/qmp.sock"
SERIAL_LOG="$WORKDIR/serial.log"
SHOTS_DIR="$WORKDIR/shots"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

cleanup() {
  local ec=$?
  [[ -n "${QEMU_PID:-}" ]] && kill "$QEMU_PID" 2>/dev/null || true
  if mountpoint -q "$WORKDIR/sysimg" 2>/dev/null; then sudo umount "$WORKDIR/sysimg" || true; fi
  if mountpoint -q "$WORKDIR/mnt" 2>/dev/null; then sudo umount "$WORKDIR/mnt" || true; fi
  sudo qemu-nbd --disconnect /dev/nbd0 2>/dev/null || true

  # Screenshots are the only visibility into the (headless) installer TUI --
  # always convert+copy them next to $OUT so CI can upload them, pass or fail.
  if [[ -d "$SHOTS_DIR" ]]; then
    local out_shots="$(dirname "$OUT")/shots"
    mkdir -p "$out_shots"
    for ppm in "$SHOTS_DIR"/*.ppm; do
      [[ -e "$ppm" ]] || continue
      pnmtopng "$ppm" > "$out_shots/$(basename "${ppm%.ppm}").png" 2>/dev/null || true
    done
  fi

  exit $ec
}
trap cleanup EXIT

echo "==> Downloading android-x86 $VERSION ISO"
curl -L --fail --retry 3 -o "$ISO_PATH" "$ISO_URL"

echo "==> Creating ${DISK_GB}G disk"
qemu-img create -f qcow2 "$RAW_DISK" "${DISK_GB}G"

ACCEL_ARGS=()
if [[ -w /dev/kvm ]]; then
  echo "==> KVM available, using hardware acceleration"
  ACCEL_ARGS=(-enable-kvm -cpu host)
else
  echo "==> No KVM access, falling back to TCG (slower)"
  ACCEL_ARGS=(-cpu max)
fi

echo "==> Booting installer"
qemu-system-x86_64 \
  -m "$RAM_MB" -smp 2 \
  "${ACCEL_ARGS[@]}" \
  -drive file="$RAW_DISK",if=virtio,format=qcow2 \
  -cdrom "$ISO_PATH" -boot d \
  -display none -vga std \
  -serial "file:$SERIAL_LOG" \
  -qmp "unix:$QMP_SOCK,server,nowait" \
  -no-reboot &
QEMU_PID=$!

echo "==> Waiting for QMP socket"
for _ in $(seq 1 30); do
  [[ -S "$QMP_SOCK" ]] && break
  sleep 1
done

python3 "$SCRIPT_DIR/send-golden-image-keys.py" --qmp-socket "$QMP_SOCK" --shots-dir "$SHOTS_DIR"

echo "==> Install steps sent, requesting clean shutdown"
python3 - "$QMP_SOCK" <<'PY'
import json, socket, sys, time
sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
sock.connect(sys.argv[1])
sock.recv(65536)
sock.sendall(b'{"execute":"qmp_capabilities"}\n')
sock.recv(65536)
sock.sendall(b'{"execute":"quit"}\n')
time.sleep(1)
PY

wait "$QEMU_PID" 2>/dev/null || true
unset QEMU_PID

echo "==> Patching installed system offline (disable setup wizard, enable adb-tcp)"
sudo modprobe nbd max_part=8
sudo qemu-nbd --connect=/dev/nbd0 "$RAW_DISK"
sleep 2
sudo partprobe /dev/nbd0 || true
sleep 2
echo "==> Partition table on /dev/nbd0:"
sudo fdisk -l /dev/nbd0 || true
lsblk /dev/nbd0 || true
mkdir -p "$WORKDIR/mnt" "$WORKDIR/sysroot"

# android-x86 installs one ext4 partition holding the installer payload,
# including a squashfs system image (system.sfs) that holds /system read-only.
PART="/dev/nbd0p1"
if [[ ! -b "$PART" ]]; then
  echo "!! $PART does not exist after partprobe — install likely didn't complete as expected." >&2
  echo "!! Dumping last 200 lines of installer serial console for diagnosis:" >&2
  tail -n 200 "$SERIAL_LOG" >&2 || true
  cp "$SERIAL_LOG" "$(dirname "$OUT")/serial-debug.log" 2>/dev/null || true
  exit 1
fi
sudo mount "$PART" "$WORKDIR/mnt"

echo "==> Contents of installed partition:"
find "$WORKDIR/mnt" -maxdepth 3 >&2 || true

SYSTEM_SFS="$(sudo find "$WORKDIR/mnt" -name 'system.sfs' | head -n1)"
if [[ -z "$SYSTEM_SFS" ]]; then
  echo "!! system.sfs not found anywhere on the installed partition — installer layout differs for this version." >&2
  exit 1
fi
echo "==> Found system.sfs at: $SYSTEM_SFS"

sudo unsquashfs -d "$WORKDIR/sysroot" "$SYSTEM_SFS"

# On this release, system.sfs is a single-file squashfs wrapping one ext4
# image (system.img) rather than exposing /system directly -- confirmed via
# `unsquashfs` reporting "1 file". Loop-mount that image to reach the real
# build.prop instead of writing a stray file next to it.
SYSTEM_IMG="$(sudo find "$WORKDIR/sysroot" -maxdepth 2 -type f | head -n1)"
if [[ -z "$SYSTEM_IMG" ]]; then
  echo "!! No file found inside extracted system.sfs -- layout differs for this version." >&2
  find "$WORKDIR/sysroot" -maxdepth 3 >&2
  exit 1
fi
echo "==> system.sfs contains: $SYSTEM_IMG"

mkdir -p "$WORKDIR/sysimg"
sudo mount -o loop "$SYSTEM_IMG" "$WORKDIR/sysimg"

BUILD_PROP="$(sudo find "$WORKDIR/sysimg" -maxdepth 2 -name 'build.prop' | head -n1)"
if [[ -z "$BUILD_PROP" ]]; then
  echo "!! build.prop not found inside $SYSTEM_IMG -- layout differs for this version." >&2
  find "$WORKDIR/sysimg" -maxdepth 3 >&2
  sudo umount "$WORKDIR/sysimg"
  exit 1
fi
echo "==> Patching $BUILD_PROP"
sudo tee -a "$BUILD_PROP" > /dev/null <<'PROPS'
ro.setupwizard.mode=DISABLED
ro.setupwizard.enterprise_mode=0
persist.service.adb.tcp.port=5555
ro.adb.secure=0
PROPS

sudo umount "$WORKDIR/sysimg"

sudo mksquashfs "$WORKDIR/sysroot" "$SYSTEM_SFS.new" -comp xz -noappend
sudo mv "$SYSTEM_SFS.new" "$SYSTEM_SFS"

sudo umount "$WORKDIR/mnt"
sudo qemu-nbd --disconnect /dev/nbd0

echo "==> Compressing final golden image"
qemu-img convert -O qcow2 -c "$RAW_DISK" "$OUT"
sha256sum "$OUT"

echo "==> Done: $OUT"
