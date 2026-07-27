using System.Diagnostics;
using System.Text.Json;
using DroidBox.Core.Adb;
using DroidBox.Core.Models;

namespace DroidBox.Core.Vm;

public sealed class VmManager
{
    private readonly GoldenImageStore _goldenStore;
    private readonly List<VmInstance> _vms = [];
    private readonly Dictionary<string, Process> _processes = [];
    private readonly object _lock = new();

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

    public async Task<VmInstance> CreateVmAsync(
        AndroidVersion version,
        IProgress<double>? downloadProgress = null,
        CancellationToken ct = default)
    {
        var golden = await _goldenStore.EnsureAvailableAsync(version, downloadProgress, ct);

        var id = Guid.NewGuid().ToString("N")[..8];
        var overlayPath = Path.Combine(PathConfig.VmsDir, $"{id}.qcow2");

        // Instant create: a copy-on-write overlay file, not a full disk copy.
        await QemuProcessLauncher.CreateOverlayAsync(golden, overlayPath, ct);

        var vm = new VmInstance
        {
            Id = id,
            VersionId = version.Id,
            OverlayPath = overlayPath,
            AdbHostPort = PortAllocator.FindFreePort(),
        };

        lock (_lock) _vms.Add(vm);
        Save();
        return vm;
    }

    public void StartVm(VmInstance vm, AndroidVersion version)
    {
        var process = QemuProcessLauncher.Start(vm, version);
        vm.ProcessId = process.Id;
        vm.State = VmState.Running;

        lock (_lock) _processes[vm.Id] = process;

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            vm.State = VmState.Stopped;
            vm.ProcessId = null;
            lock (_lock) _processes.Remove(vm.Id);
            Save();
        };

        Save();
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
    }

    /// <summary>Instant delete: stop the process if running, then remove the small overlay file.</summary>
    public void DeleteVm(VmInstance vm)
    {
        StopVm(vm);

        if (File.Exists(vm.OverlayPath))
            File.Delete(vm.OverlayPath);

        lock (_lock) _vms.Remove(vm);
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

        var json = File.ReadAllText(PathConfig.StateFile);
        var loaded = JsonSerializer.Deserialize<List<VmInstance>>(json);
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
