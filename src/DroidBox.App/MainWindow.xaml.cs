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
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        if (VersionCombo.SelectedItem is not AndroidVersion version)
            return;

        CreateButton.IsEnabled = false;
        StatusText.Text = $"Creating {version.DisplayName} VM…";
        try
        {
            var progress = new Progress<double>(p => StatusText.Text = $"Downloading golden image… {p:P0}");
            var vm = await _vmManager.CreateVmAsync(version, progress);
            _cards.Add(new VmCardViewModel(vm, version));
            StatusText.Text = "VM created.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            CreateButton.IsEnabled = true;
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var card = (VmCardViewModel)((Button)sender).Tag;
        try
        {
            _vmManager.StartVm(card.Vm, card.Version);
            card.Refresh();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error starting VM: {ex.Message}";
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
