# DroidBox — handoff notes for the next AI session

You're picking up a Windows desktop app that creates real, hardware-accelerated
Android VMs (android-x86 under QEMU — not an emulator like BlueStacks/Android
Studio AVD). Repo: https://github.com/giangnam0201/droidbox

Read this whole file before changing anything. It's written by the agent that
built M1 and iterated through the real bugs the user hit running it — the
"known gotchas" section below cost real debugging time to find; don't
re-derive them from scratch.

## What the user actually wants (in their words, cleaned up)

- Instant create/delete of Android VMs, versions 4.0–7.1 (see "Version range"
  below for why not 1.0–3.0).
- Drag-and-drop APK install.
- Boots straight to the home screen, no setup wizard.
- **Not a simulator** — a real VM (this is why QEMU+android-x86, not
  Genymotion/BlueStacks-style tooling).
- Fast. The user was extremely unhappy when a cold boot took 30 minutes under
  software emulation with zero feedback about why — see "Snapshot instant
  resume" below, which is the fix, but it needs more polish (see Next steps).
- A real GitHub repo, built by GitHub Actions, with someone (an agent) actually
  watching the CI logs and fixing failures — not "push and hope."
- **A genuinely good UI.** The current one is a functional MVP the user called
  "shit ass." This is the biggest open ask — see Next steps.

## Repo map

```
src/DroidBox.App/            WPF UI (net8.0-windows). MainWindow.xaml(.cs) is the whole app right now.
src/DroidBox.Core/           All the real logic — read this before the UI.
  Manifest/versions.json     Android version list (currently only 7.1). Embedded resource.
  Manifest/VersionManifest.cs
  Models/AndroidVersion.cs, VmInstance.cs
  Vm/PathConfig.cs           Where everything lives on disk (%LOCALAPPDATA%\DroidBox\...)
  Vm/GoldenImageStore.cs     Downloads+caches golden qcow2 images from GitHub Releases
  Vm/QemuProcessLauncher.cs  Builds the qemu-system-x86_64 command line, starts/creates overlays
  Vm/QemuMonitorClient.cs    Talks to QEMU's HMP monitor to savevm (snapshot) — NEW, see below
  Vm/VmManager.cs            The orchestrator: create/start/stop/delete, boot-completion polling,
                              log capture, events (VmChanged, VmLogLine). Read this file first.
  Vm/ToolsProvisioner.cs     Downloads QEMU + adb into Tools/ on first use (app itself stays small)
  Vm/HypervisorCheck.cs      Best-effort (non-authoritative) WHPX registry check — barely used
  Adb/AdbClient.cs           adb install + adb shell wrapper
src/DroidBox.Tests/          Unit tests — manifest parsing, port allocation, golden-image hash
                              verification. Deliberately does NOT boot a real VM in tests.
tools/golden-image-builder/  Bash + Python scripts that build android-x86 golden qcow2 images in CI.
                              build-golden-image.sh orchestrates; send-golden-image-keys.py drives
                              the installer's TUI blind over QEMU's QMP socket.
.github/workflows/
  build-windows.yml          Every push: dotnet build+test+publish win-x64. Currently green.
  build-golden-images.yml    workflow_dispatch only, matrix over versions. Currently only 7.1.
```

## Current state (as of this handoff)

- `build-windows.yml`: green.
- `build-golden-images.yml`: **golden-images release on GitHub has a working
  android-x86-7.1.qcow2** (~1.67GB). It took 6 debugging iterations to get
  right — see "Golden image builder gotchas" below before touching it again.
- The Windows app: builds, installs QEMU+adb on first use, can create/start/
  stop/delete a 7.1 VM, drag-drop APK install works. UI is a bare-bones single
  window with VM cards and a log panel — functional, not good.
- Snapshot-based instant resume was **just added and is unverified end-to-end**
  by a full real boot-to-snapshot-to-fast-resume cycle (see Next steps #1).

## Known gotchas (do not rediscover these the hard way)

### WHPX / acceleration
- `-accel whpx:tcg` as a single value is **invalid QEMU syntax**. Multiple
  accelerators need separate flags: `-accel whpx -accel tcg`
  (`QemuProcessLauncher.cs`).
- WHPX requires the user to have enabled Windows Hypervisor Platform
  (`Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform` +
  reboot). If it's not enabled, QEMU logs `failed to initialize whpx` /
  `WHPX: No accelerator found` and silently falls back to TCG (software
  emulation), which is 10-30x slower and can take 15-30 minutes to boot
  Android cold. `VmManager.OnQemuOutputLine` watches for these strings and
  logs an actionable message — if you touch that pattern-matching, keep it,
  it's load-bearing for user sanity.
- `HypervisorCheck.cs` exists but is a weak, non-authoritative registry
  sniff — don't trust it as a gate, it's just a hint.

### Display
- `-vga virtio` boots to a **permanently black screen** on android-x86 7.1 —
  the guest kernel's virtio-gpu driver support is unreliable. Use
  `-vga std` instead (confirmed working via real screenshots during
  debugging). If you ever add a different Android version, re-verify this;
  don't assume std VGA is universally right, just that virtio was wrong here.

### Snapshot instant resume (the fix for "30 minutes, not instant")
- Mechanism: after `StartVm`, if the VM has no snapshot yet,
  `VmManager.WatchForFirstBootAsync` polls `adb shell getprop
  sys.boot_completed` every 5s (up to 15 min) on a background task. When it
  returns `"1"`, `QemuMonitorClient.SaveSnapshotAsync` sends HMP `savevm
  dbsnap` over a TCP monitor socket (`-monitor tcp:127.0.0.1:<port>,server,
  nowait`, a new port per VM: `VmInstance.MonitorPort`). Once
  `VmInstance.HasSnapshot` is true, every future `StartVm` passes `-loadvm
  dbsnap` and should resume near-instantly instead of cold-booting.
- **This has not been verified end-to-end with a real successful boot** as of
  this handoff (the boot that would have triggered it got killed mid-boot to
  unblock a DLL hotfix). First thing to do: let a VM actually reach the home
  screen, confirm the log shows "Snapshot saved", stop it, start it again,
  and time how fast it comes back. If `-loadvm` doesn't work as expected
  (e.g. because of a QEMU version quirk, or because `-vga std` state doesn't
  restore cleanly), that's the actual next bug to chase — don't assume it
  works just because it compiles.
- HMP `savevm`/`loadvm` writes into the **overlay** qcow2 (not the golden
  image), so it's per-VM and gets deleted along with the VM. That's correct
  behavior — don't move it to the golden image.

### Golden image builder (`tools/golden-image-builder/`)
This was the hardest part to get working and is worth understanding before
touching it, especially if you add more Android versions (see Next steps #4):
1. `osdn.net` (formerly hosted android-x86 releases) is dead — DNS doesn't
   resolve. ISOs now come from SourceForge mirror URLs
   (`sourceforge.net/projects/android-x86/files/...`).
2. The installer is a blind ncurses TUI with **no serial console output** —
   you cannot debug it by reading logs. `send-golden-image-keys.py`
   periodically screendumps the VM's framebuffer via QMP to PNGs, uploaded as
   a `installer-screenshots-<version>` CI artifact on every run (pass or
   fail). **If you need to retime the installer's key sequence for a
   different version, download that artifact and actually look at the
   screenshots** — don't guess timings blind, that wasted several iterations
   here.
3. The boot menu structure (for 7.1-r5): Live CD / Live CD-debug /
   Installation / Advanced options. Advanced options has: Live CD-No Setup
   Wizard / Live CD VESA / **Auto_Installation** (what we use — fully
   non-interactive disk install) / Auto_Update / Boot from local drive / Back.
   This may differ for other versions — re-verify via screenshots.
4. Auto_Installation shows a confirmation dialog ("erase whole disk, are you
   sure?", defaults to **No**) that takes ~35-45s to actually appear after
   selecting it (kernel boot + initramfs + disk detection) — sending the
   confirm keystroke too early lands on nothing and it sits on No forever.
5. `system.sfs` (the squashfs holding /system) wraps a **single ext4 file
   `system.img`**, not `/system` directly — you have to loop-mount
   `system.img` after `unsquashfs` to reach the real `build.prop`. Writing to
   a `build.prop` file directly inside the unsquashed root does nothing.
6. The default `GITHUB_TOKEN` needs `contents: write` (set at the top of
   `build-golden-images.yml`) or `gh release create/upload` gets HTTP 403.

### Tools provisioning (`ToolsProvisioner.cs`)
- Downloads QEMU's NSIS installer from `qemu.weilnetz.de/w64/` — **the
  filename is date-stamped with no stable "latest" alias**
  (`qemu-w64-setup-20260723.exe` as of this writing). This will go stale;
  check https://qemu.weilnetz.de/w64/ periodically and bump the constant in
  `ToolsProvisioner.cs`.
- NSIS silent-install syntax `/S /D=<path>` **cannot be quoted**, even if the
  path has spaces — which breaks if it's ever passed through
  `ProcessStartInfo.ArgumentList` (which auto-quotes). It's currently built as
  a raw `Arguments` string specifically to avoid that. Don't "clean this up"
  by switching to ArgumentList.
- adb comes from Google's stable `dl.google.com/android/repository/
  platform-tools-latest-windows.zip` — this one *is* a stable alias, no issue.

### Local dev / hotfix workflow (faster than waiting on CI)
- .NET 8 SDK is installed via winget on the dev machine used so far
  (`winget install Microsoft.DotNet.SDK.8`); `dotnet` isn't on PATH by
  default in every shell — check `C:\Program Files\dotnet`.
- To hot-patch a running user's already-downloaded build without a full CI
  round-trip: `dotnet build src/DroidBox.Core/DroidBox.Core.csproj -c Release`
  then copy `DroidBox.Core.dll`/`.pdb` over the ones in their extracted
  publish folder. **The app (and any leftover qemu-system-x86_64.exe) must
  not be running** — the DLL will be locked (`Device or resource busy`).
  Only copy `DroidBox.Core.dll` if only Core changed; copy `DroidBox.App.*`
  too if the UI changed.
- Watching CI: `gh run list --repo giangnam0201/droidbox --workflow <name>`,
  `gh run watch <id> --repo giangnam0201/droidbox --exit-status`,
  `gh run view <id> --repo giangnam0201/droidbox --log-failed`. For the
  golden-image workflow specifically, also `gh run download <id> -n
  installer-screenshots-<version>` to actually see what the installer did.

## Next steps, roughly prioritized

1. **Verify the snapshot feature actually works** with a real full boot →
   confirm "Snapshot saved" in the log → stop → start → confirm it's fast.
   This is unverified and is the single most important thing to check first.
2. **UI revamp.** This is the user's loudest complaint and hasn't been
   substantively addressed yet — current UI is a single window with wrapping
   cards and a raw log textbox. Ideas worth considering, not mandates:
   - Per-VM boot state should be visible and specific, not just "Running":
     "Cold booting (first time, ~X min)...", "Resuming from snapshot...",
     "Ready". `VmManager` already has the events (`VmChanged`, `VmLogLine`)
     and data (`HasSnapshot`) to support this — the UI just isn't using them
     well.
   - Consider embedding the QEMU display instead of it opening as a separate
     OS window (QEMU supports this awkwardly on Windows; may not be worth the
     complexity — evaluate before committing).
   - Per-VM log tabs instead of one shared global log panel.
   - Visual distinction/badge for "instant" (has snapshot) vs "first boot"
     VMs in the version picker / VM list.
   - A proper settings surface: RAM/disk size overrides, WHPX status/warning
     shown proactively at app startup (not just reactively in logs after a
     failed boot).
   - Whatever design system you use, keep it — don't half-reskin.
3. **M2: remaining android-x86 versions** (4.0, 4.4, 5.0, 5.1, 6.0). Add to
   `versions.json` and the `build-golden-images.yml` matrix. Expect to repeat
   some of the golden-image-builder debugging cycle above (boot menu layout,
   system.sfs layout, and the confirm-dialog timing may all differ per
   version) — use the screenshot-artifact technique from the start rather
   than guessing.
4. **M3 polish**: proper installer packaging (Inno Setup/MSIX) instead of a
   raw published folder, app icon, first-run WHPX check with an actionable
   in-app prompt rather than just a log line after the fact.

## Ground rules this session followed — keep following them

- The user explicitly wants CI failures actually debugged by reading real
  logs/screenshots, not guessed at. When something fails, pull the actual
  `gh run view --log-failed` output (or screenshot artifacts for the golden
  image builder) before proposing a fix.
- Be honest about what "instant" and "not a simulator" mean technically —
  don't oversell. Cold boot is not instant; the snapshot mechanism is what
  makes *repeat* starts instant, and that distinction matters to the user.
- Don't bundle huge binaries (QEMU) into the app itself — keep the published
  app small and provision tools on first use, as `ToolsProvisioner` does.
