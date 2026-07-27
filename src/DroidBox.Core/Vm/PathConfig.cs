namespace DroidBox.Core.Vm;

public static class PathConfig
{
    public static string AppDataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DroidBox");

    public static string GoldenImagesDir => Path.Combine(AppDataRoot, "golden");

    public static string VmsDir => Path.Combine(AppDataRoot, "vms");

    public static string ToolsDir => Path.Combine(AppContext.BaseDirectory, "Tools");

    public static string QemuImgExe => Path.Combine(ToolsDir, "qemu", "qemu-img.exe");

    public static string QemuSystemExe => Path.Combine(ToolsDir, "qemu", "qemu-system-x86_64.exe");

    public static string AdbExe => Path.Combine(ToolsDir, "platform-tools", "adb.exe");

    public static string StateFile => Path.Combine(AppDataRoot, "vms.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(GoldenImagesDir);
        Directory.CreateDirectory(VmsDir);
    }
}
