# Golden image builder

Builds one "golden" qcow2 disk per Android-x86 version: OS installed to disk,
setup wizard disabled, boots straight to the home screen. DroidBox VMs are
instant copy-on-write overlays on top of these.

This runs on `ubuntu-latest` in `.github/workflows/build-golden-images.yml`
(matrix over versions, `workflow_dispatch` only — these are slow, multi-GB
builds, not something to run on every push). The Windows app never builds
images itself; it only downloads the finished qcow2 from a GitHub Release.

## Why this is automated the way it is

The android-x86 installer is an ncurses/TUI menu, not a scriptable answer
file, so `build-golden-image.sh` drives it by sending raw key sequences over
QEMU's QMP socket (`send-golden-image-keys.py`) instead of trying to script
an interactive shell. The exact key timing in `send-golden-image-keys.py` is
calibrated against the android-x86 7.1-r5 installer menu; if a future
version's installer menu order differs, that script's `STEPS` list is the
first place to adjust, not `build-golden-image.sh`.

## Manual local run (Linux/WSL with KVM)

```
./build-golden-image.sh \
  --version 7.1 \
  --iso-url https://osdn.net/dl/android-x86/android-x86_64-7.1-r5.iso \
  --out android-x86-7.1.qcow2
```
