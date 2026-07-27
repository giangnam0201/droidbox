using System.ComponentModel;
using System.Runtime.CompilerServices;
using DroidBox.Core.Models;

namespace DroidBox.App;

public sealed class VmCardViewModel : INotifyPropertyChanged
{
    public VmInstance Vm { get; }
    public AndroidVersion Version { get; }

    public VmCardViewModel(VmInstance vm, AndroidVersion version)
    {
        Vm = vm;
        Version = version;
    }

    public string Title => $"{Version.DisplayName}  ·  {Vm.Id}";

    public string StatusText => Vm.State switch
    {
        VmState.Stopped => "Stopped",
        VmState.Starting => "Starting…",
        VmState.Running => $"Running — adb 127.0.0.1:{Vm.AdbHostPort}",
        _ => "Unknown",
    };

    public bool CanStart => Vm.State == VmState.Stopped;
    public bool CanStop => Vm.State == VmState.Running || Vm.State == VmState.Starting;

    public void Refresh()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
