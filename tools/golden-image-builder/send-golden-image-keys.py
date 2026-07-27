#!/usr/bin/env python3
"""
Drives the android-x86 installer's ncurses menus over a QEMU QMP socket so the
whole install-to-disk + setup-wizard-skip flow can run unattended in CI.

There is no serial-console or other text feedback available (the installer
runs on the emulated VGA console only), so this also periodically screendumps
the VM's framebuffer to --shots-dir via QMP the whole time it runs. Those
PPM screenshots are the only way to verify/calibrate the STEPS timings below
against what the installer is actually showing -- inspect them (CI uploads
them as an artifact) before changing STEPS.

STEPS is a list of (wait_seconds, keys) pairs. `keys` is a list of QEMU QMP
key names sent one at a time with a short delay between each.
"""
import argparse
import json
import os
import socket
import sys
import threading
import time

STEPS = [
    # Boot menu (isolinux) has 4 entries: Live CD / Live CD-debug /
    # Installation / Advanced options... -- confirmed via screendump.
    # Go into Advanced options (3 downs from the default first-item highlight).
    (20, ["down", "down", "down", "ret"]),
    # Advanced options submenu: Live CD-No Setup Wizard / Live CD VESA mode /
    # Auto_Installation / Auto_Update / Boot from local drive / Back...
    # Auto_Installation drives the whole partition+format+copy+bootloader
    # sequence itself (no cfdisk interaction needed) -- 2 downs from the
    # default first-item highlight.
    (3, ["down", "down", "ret"]),
    # "This is the last confirmation. Are you sure to do so?" defaults to
    # No -- confirmed via screendump it just sits here forever otherwise.
    # Kernel boot + initramfs + disk detection takes ~35-45s before this
    # dialog actually appears (confirmed via screendump timestamps), so wait
    # generously rather than racing it.
    (45, ["left", "ret"]),
    # Let the automatic installer run: partition, format, copy system, install
    # GRUB. This is the slow part; give it several minutes and just observe
    # via the periodic screendumps rather than guessing further keystrokes.
    (240, []),
]

KEY_ALIASES = {
    "ret": "ret",
    "down": "down",
    "left": "left",
    "right": "right",
}


class Qmp:
    def __init__(self, path):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.connect(path)
        self._recv()
        self._cmd("qmp_capabilities")
        self.lock = threading.Lock()

    def _recv(self):
        return self.sock.recv(65536)

    def _cmd(self, command, **args):
        with_args = {"execute": command}
        if args:
            with_args["arguments"] = args
        self.sock.sendall((json.dumps(with_args) + "\n").encode())
        return self._recv()

    def cmd(self, command, **args):
        with self.lock:
            return self._cmd(command, **args)

    def send_key(self, key):
        qcode = KEY_ALIASES.get(key, key)
        self.cmd("send-key", keys=[{"type": "qcode", "data": qcode}])

    def screendump(self, path):
        self.cmd("screendump", filename=path)


def screenshot_loop(qmp, shots_dir, interval, stop_event):
    i = 0
    while not stop_event.is_set():
        path = os.path.join(shots_dir, f"shot-{i:03d}.ppm")
        try:
            qmp.screendump(path)
        except Exception as e:
            print(f"[screenshot] failed: {e}", file=sys.stderr)
        i += 1
        stop_event.wait(interval)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--qmp-socket", required=True)
    parser.add_argument("--shots-dir", required=True)
    parser.add_argument("--shot-interval", type=float, default=3.0)
    args = parser.parse_args()

    os.makedirs(args.shots_dir, exist_ok=True)
    qmp = Qmp(args.qmp_socket)

    stop_event = threading.Event()
    shot_thread = threading.Thread(
        target=screenshot_loop, args=(qmp, args.shots_dir, args.shot_interval, stop_event), daemon=True
    )
    shot_thread.start()

    for wait_before, keys in STEPS:
        print(f"[send-keys] waiting {wait_before}s, then sending {keys}", file=sys.stderr)
        time.sleep(wait_before)
        for k in keys:
            qmp.send_key(k)
            time.sleep(0.3)

    print("[send-keys] done, taking a few more trailing screenshots", file=sys.stderr)
    time.sleep(6)
    stop_event.set()
    shot_thread.join(timeout=5)


if __name__ == "__main__":
    main()
