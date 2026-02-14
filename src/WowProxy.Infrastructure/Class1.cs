using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using WowProxy.Domain;
using WowProxy.Core.Abstractions.Models;

namespace WowProxy.Infrastructure;

public static class AppDataPaths
{
    private const string AppFolderName = "WowProxy";

    public static string GetAppRoot()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(baseDir, AppFolderName);
        Directory.CreateDirectory(root);
        return root;
    }

    public static string GetCoreRoot()
    {
        var dir = Path.Combine(GetAppRoot(), "core");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetLogsRoot()
    {
        var dir = Path.Combine(GetAppRoot(), "logs");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetSettingsPath() => Path.Combine(GetAppRoot(), "settings.json");

    public static string GetSystemProxySnapshotPath() => Path.Combine(GetAppRoot(), "system-proxy.snapshot.json");
}

public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = AppDataPaths.GetSettingsPath();
        if (!File.Exists(path))
        {
            var settings = AppSettings.Default;
            await SaveAsync(settings, cancellationToken);
            return settings;
        }

        await using var stream = File.OpenRead(path);
        var settingsFromDisk = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken);
        return settingsFromDisk ?? AppSettings.Default;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var path = AppDataPaths.GetSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
    }
}

public sealed record SystemProxySnapshot(
    int ProxyEnable,
    string? ProxyServer,
    string? ProxyOverride
);

public sealed class WindowsSystemProxy
{
    private const string InternetSettingsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public void EnableGlobalProxy(string proxyServer)
    {
        var snapshot = ReadSnapshot();
        PersistSnapshot(snapshot);

        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Internet Settings 注册表键。");

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", proxyServer, RegistryValueKind.String);
        key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);

        NotifyChanged();
    }

    public void RestoreFromSnapshotIfAny()
    {
        var snapshot = TryLoadSnapshot();
        if (snapshot is null)
        {
            return;
        }

        ApplySnapshot(snapshot);
        TryDeleteSnapshot();
    }

    public void DisableAndRestore()
    {
        RestoreFromSnapshotIfAny();
    }

    private static SystemProxySnapshot ReadSnapshot()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath, writable: false)
            ?? throw new InvalidOperationException("无法打开 Internet Settings 注册表键。");

        var enable = Convert.ToInt32(key.GetValue("ProxyEnable", 0));
        var server = key.GetValue("ProxyServer") as string;
        var bypass = key.GetValue("ProxyOverride") as string;
        return new SystemProxySnapshot(enable, server, bypass);
    }

    private static void ApplySnapshot(SystemProxySnapshot snapshot)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Internet Settings 注册表键。");

        key.SetValue("ProxyEnable", snapshot.ProxyEnable, RegistryValueKind.DWord);

        if (snapshot.ProxyServer is null)
        {
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
        }
        else
        {
            key.SetValue("ProxyServer", snapshot.ProxyServer, RegistryValueKind.String);
        }

        if (snapshot.ProxyOverride is null)
        {
            key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
        }
        else
        {
            key.SetValue("ProxyOverride", snapshot.ProxyOverride, RegistryValueKind.String);
        }

        NotifyChanged();
    }

    private static void PersistSnapshot(SystemProxySnapshot snapshot)
    {
        var path = AppDataPaths.GetSystemProxySnapshotPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static SystemProxySnapshot? TryLoadSnapshot()
    {
        var path = AppDataPaths.GetSystemProxySnapshotPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SystemProxySnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteSnapshot()
    {
        try
        {
            var path = AppDataPaths.GetSystemProxySnapshotPath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void NotifyChanged()
    {
        const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        const int INTERNET_OPTION_REFRESH = 37;

        _ = NativeMethods.InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        _ = NativeMethods.InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }

    private static class NativeMethods
    {
        [DllImport("wininet.dll", SetLastError = true)]
        internal static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
    }
}
