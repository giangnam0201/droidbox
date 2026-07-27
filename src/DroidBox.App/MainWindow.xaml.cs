using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DroidBox.Core.Manifest;
using DroidBox.Core.Models;
using DroidBox.Core.Vm;

namespace DroidBox.App;

public partial class MainWindow : Window
{
    private readonly VmManager _vmManager = new();
    private readonly ToolsProvisioner _tools = new();
    private readonly ObservableCollection<VmCardViewModel> _cards = [];
    private IReadOnlyList<AndroidVersion> _versions = [];

    public MainWindow()
    {
        InitializeComponent();

        VmList.ItemsSource = _cards;

        _versions = VersionManifest.LoadEmbedded();
        VersionCombo.ItemsSource = _versions;
        VersionCombo.SelectedIndex = 0;

        foreach (var vm in _vmManager.Vms)
        {
            var version = _versions.FirstOrDefault(v => v.Id == vm.VersionId);
            if (version is not null)
                _cards.Add(new VmCardViewModel(vm, version));
        }

        // These fire from a QEMU process's own background thread, not the UI thread.
        _vmManager.VmChanged += vm => Dispatcher.BeginInvoke(() => OnVmChanged(vm));
        _vmManager.VmLogLine += (vm, line) => Dispatcher.BeginInvoke(() => AppendLog($"[{vm.Id}] {line}"));
    }

    private void OnVmChanged(VmInstance vm)
    {
        var card = _cards.FirstOrDefault(c => c.Vm.Id == vm.Id);
        card?.Refresh();
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogScroll.ScrollToBottom();
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        if (VersionCombo.SelectedItem is not AndroidVersion version)
            return;

        CreateButton.IsEnabled = false;
        StatusText.Text = $"Creating {version.DisplayName} VM…";
        try
        {
            var toolStatus = new Progress<string>(s => { StatusText.Text = s; AppendLog(s); });
            await _tools.EnsureAllAsync(toolStatus);

            var progress = new Progress<double>(p => StatusText.Text = $"Downloading golden image… {p:P0}");
            var vm = await _vmManager.CreateVmAsync(version, progress);
            _cards.Add(new VmCardViewModel(vm, version));
            StatusText.Text = "VM created.";
            AppendLog($"[{vm.Id}] created ({version.DisplayName}).");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            AppendLog($"[droidbox] Create failed: {ex}");
        }
        finally
        {
            CreateButton.IsEnabled = true;
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var card = (VmCardViewModel)((Button)sender).Tag;
        try
        {
            var toolStatus = new Progress<string>(s => { StatusText.Text = s; AppendLog(s); });
            await _tools.EnsureAllAsync(toolStatus);

            AppendLog($"[{card.Vm.Id}] starting…");
            _vmManager.StartVm(card.Vm, card.Version);
            card.Refresh();
            StatusText.Text = "VM started.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error starting VM: {ex.Message}";
            AppendLog($"[{card.Vm.Id}] Start failed: {ex}");
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        var card = (VmCardViewModel)((Button)sender).Tag;
        _vmManager.StopVm(card.Vm);
        card.Refresh();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var card = (VmCardViewModel)((Button)sender).Tag;
        _vmManager.DeleteVm(card.Vm);
        _cards.Remove(card);
    }

    private void VmCard_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void VmCard_Drop(object sender, DragEventArgs e)
    {
        var card = (VmCardViewModel)((Border)sender).Tag;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var apk = files.FirstOrDefault(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
        if (apk is null)
        {
            StatusText.Text = "Drop a .apk file to install it.";
            return;
        }

        if (card.Vm.State != VmState.Running)
        {
            StatusText.Text = "Start the VM before installing an APK.";
            return;
        }

        StatusText.Text = $"Installing {Path.GetFileName(apk)}…";
        try
        {
            await _vmManager.InstallApkAsync(card.Vm, apk);
            StatusText.Text = $"Installed {Path.GetFileName(apk)}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Install failed: {ex.Message}";
        }
    }
}
