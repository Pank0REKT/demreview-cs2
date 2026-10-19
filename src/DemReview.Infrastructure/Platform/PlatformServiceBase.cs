using System.Diagnostics;
using DemReview.Application;

namespace DemReview.Infrastructure.Platform;

public abstract class PlatformServiceBase : IPlatformService
{
    public const string AppFolderName = "DemReview";

    public abstract string PlatformName { get; }

    public abstract StringComparer PathComparer { get; }

    public abstract string WindowIconFileName { get; }

    /// <summary>Windows: %LOCALAPPDATA%\DemReview, Linux: ~/.local/share/DemReview.</summary>
    public string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        AppFolderName);

    /// <summary>Папка maps/ рядом с исполняемым файлом (для single-file — рядом с .exe).</summary>
    public string MapsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "maps");

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        using var process = Process.Start(CreateOpenFolderStartInfo(path));
    }

    protected abstract ProcessStartInfo CreateOpenFolderStartInfo(string path);
}

public sealed class WindowsPlatformService : PlatformServiceBase
{
    public override string PlatformName => "Windows";

    public override string WindowIconFileName => "demreview.ico";

    public override StringComparer PathComparer => StringComparer.OrdinalIgnoreCase;

    protected override ProcessStartInfo CreateOpenFolderStartInfo(string path) =>
        new("explorer.exe") { ArgumentList = { path }, UseShellExecute = false };
}

public sealed class LinuxPlatformService : PlatformServiceBase
{
    public override string PlatformName => "Linux";

    public override string WindowIconFileName => "demreview.png";

    public override StringComparer PathComparer => StringComparer.Ordinal;

    protected override ProcessStartInfo CreateOpenFolderStartInfo(string path) =>
        new("xdg-open") { ArgumentList = { path }, UseShellExecute = false };
}
