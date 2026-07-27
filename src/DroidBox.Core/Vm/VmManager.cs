using System.Diagnostics;
using System.Text.Json;
using DroidBox.Core.Adb;
using DroidBox.Core.Models;

namespace DroidBox.Core.Vm;

public sealed class VmManager
{
    private const int MaxLogLinesPerVm = 500;

    private readonly GoldenImageStore _goldenStore;
    private readonly List<VmInstance> _vms = [];
    private readonly Dictionary<string, Process> _processes = [];
    private readonly Dictionary<string, Queue<string>> _logs = [];
    private readonly object _lock = new();

    /// <summary>Raised whenever a VM's runtime state changes (started, stopped, or the QEMU
    /// process died on its own) so the UI can stop trusting a stale "Running" label.</summary>
    public event Action<VmInstance>? VmChanged;

    /// <summary>Raised for every line QEMU prints to stdout/stderr, so failures are actually
    /// visible instead of silently disappearing.</summary>
    public event Action<VmInstance, string>? VmLogLine;

    public VmManager(GoldenImageStore? goldenStore = null)
    {
        _goldenStore = goldenStore ?? new GoldenImageStore();
        PathConfig.EnsureDirectories();
        Load();
    }

    public IReadOnlyList<VmInstance> Vms
    {
        get { lock (_lock) return _vms.ToList(); }
    }

    public IReadOnlyList<string> GetRecentLog(VmInstance vm)
    {
        lock (_lock) return _logs.TryGetValue(vm.Id, out var q) ? q.ToList() : [];
    }

    public async Task<VmInstance> CreateVmAsync(
        AndroidVersion version,
        IProgress<double>? downloadProgress = null,
        CancellationToken ct = default)
    {
        var golden = await _goldenStore.EnsureAvailableAsync(version, downloadProgress, ct);

        var id = Guid.NewGuid().ToString("N")[..8];
        var diskPath = Path.Combine(PathConfig.VmsDir, $"{id}.qcow2");

        // A raw copy, not a COW overlay -- see VmInstance.DiskPath for why: only a real
        // standalone file can -loadvm a snapshot baked into the golden image.
        await QemuProcessLauncher.CreateVmDiskAsync(golden, diskPath, ct);
        var hasSnapshot = await QemuProcessLauncher.HasEmbeddedSnapshotAsync(diskPath, ct);

        var adbPort = PortAllocator.FindFreePort();
        var vm = new VmInstance
        {
            Id = id,
            VersionId = version.Id,
            DiskPath = diskPath,
            AdbHostPort = adbPort,
            MonitorPort = PortAllocator.FindFreePort(adbPort + 1),
            HasSnapshot = hasSnapshot,
        };

        lock (_lock) _vms.Add(vm);
        Save();
        return vm;
    }

    public void StartVm(VmInstance vm, AndroidVersion version)
    {
        lock (_lock) _logs[vm.Id] = new Queue<string>();

        var wasFirstBoot = !vm.HasSnapshot;
        var process = QemuProcessLauncher.Start(vm, version, line => OnQemuOutputLine(vm, line));
        vm.ProcessId = process.Id;
        vm.State = VmState.Running;

        lock (_lock) _processes[vm.Id] = process;

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            var exitCode = SafeExitCode(process);
            vm.State = VmState.Stopped;
            vm.ProcessId = null;
            lock (_lock) _processes.Remove(vm.Id);

            // A VM that just quit normally (user clicked Stop) already has its process removed
            // above before this fires in practice, but if QEMU dies on its own -- e.g. because
            // WHPX isn't available and it couldn't start at all -- this is the only signal the
            // UI gets, so make it loud instead of leaving a stale "Running" label.
            if (exitCode is not 0)
                AppendLog(vm, $"[droidbox] QEMU exited with code {exitCode}.");

            Save();
            VmChanged?.Invoke(vm);
        };

        Save();
        VmChanged?.Invoke(vm);

        if (wasFirstBoot)
        {
            AppendLog(vm, "[droidbox] No snapshot on this VM's disk yet, so this boot will be a " +
                          "full cold boot. Once it reaches the home screen, a snapshot is saved " +
                          "automatically so every start after this one resumes almost instantly. " +
                          "(Golden images built with a baked-in snapshot skip this entirely.)");
            _ = WatchForFirstBootAsync(vm);
        }
    }

    /// <summary>Polls adb until Android reports it finished booting, then saves a QEMU snapshot
    /// so every subsequent StartVm can resume from it instead of cold-booting again.</summary>
    private async Task WatchForFirstBootAsync(VmInstance vm)
    {
        var adb = new AdbClient(PathConfig.AdbExe);
        var deadline = DateTime.UtcNow.AddMinutes(15); // TCG cold boot can be very slow

        while (DateTime.UtcNow < deadline)
        {
            if (vm.State != VmState.Running)
                return; // stopped/deleted while we were waiting

            var result = await adb.TryShellAsync(vm.AdbHostPort, "getprop sys.boot_completed");
            if (result == "1")
            {
                AppendLog(vm, "[droidbox] Boot completed -- saving snapshot for instant future starts...");
                try
                {
                    await QemuMonitorClient.SaveSnapshotAsync(vm.MonitorPort);
                    vm.HasSnapshot = true;
                    Save();
                    AppendLog(vm, "[droidbox] Snapshot saved. Stop and Start this VM again and it will resume instantly.");
                }
                catch (Exception ex)
                {
                    AppendLog(vm, $"[droidbox] Snapshot save failed (will retry cold boot next time): {ex.Message}");
                }
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        AppendLog(vm, "[droidbox] Gave up waiting for boot to complete (15 min) -- no snapshot saved this run.");
    }

    private static readonly string[] WhpxFailureMarkers =
        ["failed to initialize whpx", "WHPX: No accelerator found"];

    private void OnQemuOutputLine(VmInstance vm, string line)
    {
        AppendLog(vm, line);

        if (WhpxFailureMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            AppendLog(vm,
                "[droidbox] Hardware acceleration (WHPX) isn't available, so this VM is running " +
                "in slow software emulation. To fix: open an elevated PowerShell and run " +
                "'Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform', then " +
                "reboot Windows. Until then, the first boot of each VM will be slow, but it only " +
                "happens once -- after that a snapshot lets it resume instantly.");
        }
    }

    private void AppendLog(VmInstance vm, string line)
    {
        lock (_lock)
        {
            if (!_logs.TryGetValue(vm.Id, out var q))
                _logs[vm.Id] = q = new Queue<string>();
            q.Enqueue(line);
            while (q.Count > MaxLogLinesPerVm)
                q.Dequeue();
        }
        VmLogLine?.Invoke(vm, line);
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.ExitCode; } catch { return null; }
    }

    public void StopVm(VmInstance vm)
    {
        lock (_lock)
        {
            if (_processes.TryGetValue(vm.Id, out var process) && !process.HasExited)
                process.Kill(entireProcessTree: true);
            _processes.Remove(vm.Id);
        }

        vm.State = VmState.Stopped;
        vm.ProcessId = null;
        Save();
        VmChanged?.Invoke(vm);
    }

    /// <summary>Instant delete: stop the process if running, then remove the VM's disk file.</summary>
    public void DeleteVm(VmInstance vm)
    {
        StopVm(vm);

        if (File.Exists(vm.DiskPath))
            File.Delete(vm.DiskPath);

        lock (_lock)
        {
            _vms.Remove(vm);
            _logs.Remove(vm.Id);
        }
        Save();
    }

    public async Task InstallApkAsync(VmInstance vm, string apkPath, CancellationToken ct = default)
    {
        if (vm.State != VmState.Running)
            throw new InvalidOperationException("VM must be running before installing an APK.");

        var adb = new AdbClient(PathConfig.AdbExe);
        await adb.InstallApkAsync(vm.AdbHostPort, apkPath, ct);
    }

    private void Save()
    {
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(_vms, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PathConfig.StateFile, json);
        }
    }

    private void Load()
    {
        if (!File.Exists(PathConfig.StateFile))
            return;

        List<VmInstance>? loaded;
        try
        {
            var json = File.ReadAllText(PathConfig.StateFile);
            loaded = JsonSerializer.Deserialize<List<VmInstance>>(json);
        }
        catch (JsonException)
        {
            // Schema changed underneath an existing state file (e.g. OverlayPath -> DiskPath).
            // Treat as "no VMs yet" instead of crashing the app on startup -- the old disk
            // files are orphaned but harmless leftovers under PathConfig.VmsDir.
            return;
        }

        if (loaded is null)
            return;

        // Process handles don't survive an app restart; any VM we find is treated as stopped
        // until the user explicitly starts it again.
        foreach (var vm in loaded)
        {
            vm.State = VmState.Stopped;
            vm.ProcessId = null;
        }

        lock (_lock)
        {
            _vms.Clear();
            _vms.AddRange(loaded);
        }
    }
}
