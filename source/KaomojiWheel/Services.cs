using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace KaomojiWheel;

public sealed class AppStore
{
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private readonly string _dataFile, _backupFile, _settingsFile;
    public string DataDirectory { get; }
    public WheelData Data { get; private set; } = new();
    public WheelSettings Settings { get; private set; } = new();
    public AppStore(string baseDir)
    {
        DataDirectory = Path.Combine(baseDir, "Data"); _dataFile = Path.Combine(DataDirectory, "data.json");
        _backupFile = Path.Combine(DataDirectory, "data.bak"); _settingsFile = Path.Combine(DataDirectory, "settings.json");
    }
    public void Load()
    {
        Directory.CreateDirectory(DataDirectory); Directory.CreateDirectory(Path.Combine(DataDirectory, "Logs")); CleanupLogs();
        Settings = Read<WheelSettings>(_settingsFile) ?? new();
        Data = Read<WheelData>(_dataFile) ?? Read<WheelData>(_backupFile) ?? CreateInitialData(); Normalize(true); SaveAll(); StartupService.SetEnabled(Settings.AutoStart);
    }
    private T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), _json); }
        catch (Exception ex) { FileLogger.TryWrite(AppContext.BaseDirectory, ex); try { File.Copy(path, path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", true); } catch { } return default; }
    }
    public void SaveAll()
    {
        Normalize(false); AtomicWrite(_dataFile, JsonSerializer.Serialize(Data, _json), true); AtomicWrite(_settingsFile, JsonSerializer.Serialize(Settings, _json), false);
    }
    public KaomojiImportPreview Import(KaomojiTransferDocument document)
    {
        var snapshot = JsonSerializer.Serialize(Data, _json);
        try
        {
            var result = KaomojiTransferService.Merge(Data, document);
            Normalize(false);
            AtomicWrite(_dataFile, JsonSerializer.Serialize(Data, _json), true);
            return result;
        }
        catch
        {
            Data = JsonSerializer.Deserialize<WheelData>(snapshot, _json) ?? new WheelData();
            throw;
        }
    }
    private void AtomicWrite(string target, string content, bool backup)
    {
        var temp = target + ".tmp"; File.WriteAllText(temp, content, new System.Text.UTF8Encoding(false));
        if (backup && File.Exists(target)) File.Copy(target, _backupFile, true); File.Move(temp, target, true);
    }
    public void Normalize(bool sortByStoredOrder)
    {
        if (sortByStoredOrder) Data.Repositories = Data.Repositories.OrderBy(x => x.Order).ToList();
        for (var i = 0; i < Data.Repositories.Count; i++) { var repo = Data.Repositories[i]; repo.Order = i; if (sortByStoredOrder) repo.Items = repo.Items.OrderBy(x => x.Order).ToList(); for (var j = 0; j < repo.Items.Count; j++) repo.Items[j].Order = j; }
    }
    private static WheelData CreateInitialData() => new();
    private void CleanupLogs() { foreach (var file in Directory.EnumerateFiles(Path.Combine(DataDirectory, "Logs"), "*.log")) if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-7)) try { File.Delete(file); } catch { } }
    public void OpenDataFolder() => Process.Start(new ProcessStartInfo("explorer.exe", DataDirectory) { UseShellExecute = true });
}

public static class FileLogger
{
    public static void TryWrite(string baseDir, Exception ex) { try { var dir = Path.Combine(baseDir, "Data", "Logs"); Directory.CreateDirectory(dir); File.AppendAllText(Path.Combine(dir, $"{DateTime.Today:yyyy-MM-dd}.log"), $"[{DateTime.Now:O}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n"); } catch { } }
}
public static class ClipboardService
{
    public static async Task<bool> CopyAsync(string text) { for (var i = 0; i < 3; i++) { try { Clipboard.SetText(text); return true; } catch (COMException) when (i < 2) { await Task.Delay(50); } } return false; }
}
public static class StartupService
{
    private const string KeyName = "颜文字轮盘";
    public static void SetEnabled(bool enabled) { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true); if (enabled) key?.SetValue(KeyName, $"\"{Environment.ProcessPath}\""); else key?.DeleteValue(KeyName, false); }
}
public sealed class HotkeyService : IDisposable
{
    private const int Id = 0x4B57; private readonly HwndSource _source; private readonly Action _action;
    public HotkeyService(Window window, Action action) { _action = action; _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle); _source.AddHook(WndProc); }
    public bool Register(string value) { UnregisterHotKey(_source.Handle, Id); if (!TryParse(value, out var modifiers, out var key)) return false; return RegisterHotKey(_source.Handle, Id, modifiers | 0x4000, key); }
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) { if (msg == 0x0312 && wParam.ToInt32() == Id) { handled = true; _action(); } return IntPtr.Zero; }
    public static bool TryParse(string text, out uint modifiers, out uint key)
    {
        modifiers = 0; key = 0;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        { if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) modifiers |= 2; else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= 1; else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= 4; else if (part.Length == 1 && char.IsLetterOrDigit(part[0])) key = char.ToUpperInvariant(part[0]); else return false; }
        return modifiers != 0 && key != 0;
    }
    public void Dispose() { UnregisterHotKey(_source.Handle, Id); _source.RemoveHook(WndProc); }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
