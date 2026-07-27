#!/usr/bin/env python3
"""
Drives the android-x86 installer's ncurses menus over a QEMU QMP socket so the
whole install-to-disk + setup-wizard-skip flow can run unattended in CI.

STEPS is a list of (wait_seconds, keys) pairs. `keys` is a list of QEMU QMP
key names (see `qemu-qmp` `send-key` docs) sent one at a time with a short
delay between each. Timings are calibrated for the android-x86 7.1-r5
installer; adjust here first if a different version's menu order changes.
"""
import argparse
import json
import socket
import sys
import time

STEPS = [
    # Boot menu (isolinux): "Installation - Install Android-x86 to harddisk"
    # is the 4th entry (Live CD/Live CD-debug/Live CD-VESA/Installation/...).
    (20, ["down", "down", "down", "ret"]),
    # Partition tool: "Create/Modify partitions" -> cfdisk
    (5, ["ret"]),
    (3, ["ret"]),  # cfdisk: New
    (2, ["ret"]),  # Primary
    (2, ["ret"]),  # full size (default)
    (2, ["left", "ret"]),  # Bootable
    (2, ["right", "right", "right", "right", "ret"]),  # Write
    (2, ["y", "e", "s", "ret"]),  # confirm "yes"
    (2, ["right", "right", "ret"]),  # cfdisk: Quit
    # Back in installer: choose filesystem
    (3, ["down", "ret"]),  # ext4
    (3, ["ret"]),  # confirm format -> Yes
    (5, ["ret"]),  # install boot loader GRUB -> Yes
    (3, ["ret"]),  # install /system directory as read-write -> Yes
    (60, []),  # let file copy finish (payload copy is the slow part)
    (3, ["ret"]),  # installation complete -> Run Android-x86
]

KEY_ALIASES = {
    "ret": "ret",
    "down": "down",
    "left": "left",
    "right": "right",
}


def qmp_command(sock, command, **args):
    payload = {"execute": command}
    if args:
        payload["arguments"] = args
    sock.sendall((json.dumps(payload) + "\n").encode())
    return sock.recv(65536)


def send_key(sock, key):
    if key in KEY_ALIASES:
        qmp_command(sock, "send-key", keys=[{"type": "qcode", "data": KEY_ALIASES[key]}])
    else:
        qmp_command(sock, "send-key", keys=[{"type": "qcode", "data": key}])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--qmp-socket", required=True)
    args = parser.parse_args()

    sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    sock.connect(args.qmp_socket)
    sock.recv(65536)  # greeting
    qmp_command(sock, "qmp_capabilities")

    for wait_before, keys in STEPS:
        print(f"[send-keys] waiting {wait_before}s, then sending {keys}", file=sys.stderr)
        time.sleep(wait_before)
        for k in keys:
            send_key(sock, k)
            time.sleep(0.3)

    print("[send-keys] done", file=sys.stderr)


if __name__ == "__main__":
    main()
