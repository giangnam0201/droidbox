# DroidBox

Instant Android VM manager for Windows. Creates real, hardware-accelerated
Android VMs (android-x86 under QEMU + WHPX — not an emulator like BlueStacks
or the Android Studio AVD), boots straight to the home screen with no setup
wizard, and lets you drag-and-drop an APK onto a VM to install it.

Supported versions: android-x86 4.0 through 7.1 (1.0–3.0 never had an x86
build, so they're out of scope — see the design notes below).

## How it works

- One prebuilt **golden** qcow2 disk per Android version (setup wizard
  disabled, boot trimmed for speed). Built in CI — see
  `tools/golden-image-builder/`.
- **Create VM** = an instant copy-on-write overlay on top of the golden
  image (`qemu-img create -b golden.qcow2 overlay.qcow2`) — a few
  milliseconds, not a disk copy.
- **Delete VM** = stop the process, delete the overlay file. Instant.
- **Install APK** = drag a `.apk` onto a VM card; DroidBox runs
  `adb install` against that VM's forwarded adb port.

## Requirements

- Windows 10/11 with **Windows Hypervisor Platform** enabled (one-time):
  ```powershell
  Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform
  ```
  then reboot. This is what gives QEMU hardware acceleration (WHPX) instead
  of slow software emulation.

## Repo layout

```
src/DroidBox.App/            WPF UI
src/DroidBox.Core/           VM lifecycle, adb wrapper, version manifest
src/DroidBox.Tests/          unit tests
tools/golden-image-builder/  CI scripts that build each version's golden qcow2
.github/workflows/
  build-windows.yml          builds/tests the Windows app on every push
  build-golden-images.yml    workflow_dispatch: builds golden images, publishes to a release
```

## Status

M1: Android 7.1 end-to-end (create → boot to home → install APK → delete).
Remaining android-x86 versions (4.0/4.4/5.0/5.1/6.0) follow in the same
manifest + golden-image matrix.
