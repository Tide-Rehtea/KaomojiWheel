using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KaomojiWheel;

public sealed class UpdateService
{
    public const string ManifestUrl = "https://github.com/Tide-Rehtea/KaomojiWheel/releases/latest/download/latest.json";
    private static readonly HttpClient Client = CreateClient();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static string CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("KaomojiWheel-Updater/1.0");
        return client;
    }

    public async Task<UpdateManifest?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(ManifestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, Json, cancellationToken) ?? throw new InvalidDataException("更新清单为空。");
        ValidateManifest(manifest);
        return IsNewer(manifest.Version, CurrentVersion) ? manifest : null;
    }

    public async Task LaunchInstallerAsync(UpdateManifest manifest, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateManifest(manifest);
        var updateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaomojiWheel", "Updates", manifest.Version);
        Directory.CreateDirectory(updateRoot);
        var packagePath = Path.Combine(updateRoot, $"KaomojiWheel-{manifest.Version}.zip");
        var partialPath = packagePath + ".download";
        using (var response = await Client.GetAsync(manifest.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920]; long received = 0; int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken); received += read;
                if (total > 0) progress?.Report((int)Math.Clamp(received * 100 / total.Value, 0, 100));
            }
        }
        File.Move(partialPath, packagePath, true);
        var actualHash = await ComputeSha256Async(packagePath, cancellationToken);
        if (!actualHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包 SHA-256 校验失败，已停止安装。");

        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。");
        var helperDir = Path.Combine(updateRoot, "installer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(helperDir);
        var helperPath = Path.Combine(helperDir, "KaomojiWheel.Updater.exe"); File.Copy(processPath, helperPath, true);
        foreach (var nativeLibrary in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll", SearchOption.TopDirectoryOnly)) File.Copy(nativeLibrary, Path.Combine(helperDir, Path.GetFileName(nativeLibrary)), true);
        var start = new ProcessStartInfo(helperPath) { UseShellExecute = false, WorkingDirectory = helperDir };
        foreach (var arg in new[] { "--apply-update", "--package", packagePath, "--target", AppContext.BaseDirectory, "--parent-pid", Environment.ProcessId.ToString(), "--restart", Path.Combine(AppContext.BaseDirectory, "KaomojiWheel.exe"), "--version", manifest.Version }) start.ArgumentList.Add(arg);
        _ = Process.Start(start) ?? throw new InvalidOperationException("无法启动更新程序。");
    }

    public static bool IsInstallerInvocation(string[] args) => args.Contains("--apply-update", StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunInstallerAsync(string[] args)
    {
        var values = ParseArguments(args);
        if (!values.TryGetValue("--package", out var package) || !values.TryGetValue("--target", out var target) || !values.TryGetValue("--restart", out var restart)) return 2;
        values.TryGetValue("--version", out var version); values.TryGetValue("--parent-pid", out var pidText);
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaomojiWheel", "Updates"); Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "updater.log");
        try
        {
            if (int.TryParse(pidText, out var pid)) await WaitForExitAsync(pid, TimeSpan.FromSeconds(30));
            target = Path.GetFullPath(target); package = Path.GetFullPath(package); restart = Path.GetFullPath(restart);
            if (!File.Exists(package) || !Directory.Exists(target) || !restart.StartsWith(target, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新参数不安全或目标不存在。");
            var work = Path.Combine(Path.GetDirectoryName(package)!, "apply-" + Guid.NewGuid().ToString("N")); var stage = Path.Combine(work, "stage"); Directory.CreateDirectory(stage);
            ExtractPackageSafely(package, stage);
            var newFiles = ReadInstalledFiles(Path.Combine(stage, "installed-files.json"));
            if (!newFiles.Contains("KaomojiWheel.exe", StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("更新包缺少主程序。");
            var oldManifest = Path.Combine(target, "installed-files.json");
            var oldFiles = File.Exists(oldManifest) ? ReadInstalledFiles(oldManifest) : new List<string> { "KaomojiWheel.exe", "KaomojiWheel.pdb", "D3DCompiler_47_cor3.dll", "PenImc_cor3.dll", "PresentationNative_cor3.dll", "vcruntime140_cor3.dll", "wpfgfx_cor3.dll" };
            var affected = oldFiles.Concat(newFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var backup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaomojiWheel", "Backups", $"{DateTime.Now:yyyyMMdd-HHmmss}-{version ?? "update"}");
            foreach (var relative in affected)
            {
                var source = SafeCombine(target, relative); if (!File.Exists(source)) continue; var destination = SafeCombine(backup, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, true);
            }
            try
            {
                foreach (var relative in newFiles)
                {
                    if (IsDataPath(relative)) throw new InvalidDataException("更新包不得覆盖 Data 目录。");
                    var source = SafeCombine(stage, relative); if (!File.Exists(source)) throw new InvalidDataException($"更新包缺少文件：{relative}");
                    var destination = SafeCombine(target, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); var temporary = destination + ".update-new"; File.Copy(source, temporary, true); File.Move(temporary, destination, true);
                }
                foreach (var relative in oldFiles.Except(newFiles, StringComparer.OrdinalIgnoreCase)) { var obsolete = SafeCombine(target, relative); if (File.Exists(obsolete)) File.Delete(obsolete); }
            }
            catch
            {
                foreach (var relative in affected) { var destination = SafeCombine(target, relative); if (File.Exists(destination)) File.Delete(destination); var saved = SafeCombine(backup, relative); if (File.Exists(saved)) { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(saved, destination, true); } }
                throw;
            }
            await File.AppendAllTextAsync(logPath, $"[{DateTime.Now:O}] 已安装 v{version}，备份：{backup}{Environment.NewLine}");
            Process.Start(new ProcessStartInfo(restart) { UseShellExecute = true, WorkingDirectory = target });
            return 0;
        }
        catch (Exception ex)
        {
            try { await File.AppendAllTextAsync(logPath, $"[{DateTime.Now:O}] 更新失败：{ex}{Environment.NewLine}"); } catch { }
            try { if (File.Exists(restart)) Process.Start(new ProcessStartInfo(restart) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(restart)! }); } catch { }
            return 1;
        }
    }

    private static void ValidateManifest(UpdateManifest manifest)
    {
        if (!Version.TryParse(manifest.Version.TrimStart('v', 'V'), out _) || !Uri.TryCreate(manifest.PackageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("更新清单格式无效。");
    }
    private static bool IsNewer(string candidate, string current) => Version.TryParse(candidate.TrimStart('v', 'V'), out var a) && Version.TryParse(current.TrimStart('v', 'V'), out var b) && a > b;
    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken) { await using var stream = File.OpenRead(path); var hash = await SHA256.HashDataAsync(stream, cancellationToken); return Convert.ToHexString(hash); }
    private static Dictionary<string, string> ParseArguments(string[] args) { var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); for (var i = 0; i < args.Length - 1; i++) if (args[i].StartsWith("--") && args[i] != "--apply-update") result[args[i]] = args[++i]; return result; }
    private static async Task WaitForExitAsync(int pid, TimeSpan timeout) { try { using var process = Process.GetProcessById(pid); using var cancellation = new CancellationTokenSource(timeout); await process.WaitForExitAsync(cancellation.Token); } catch (ArgumentException) { } }
    private static void ExtractPackageSafely(string package, string stage)
    {
        using var archive = ZipFile.OpenRead(package);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar); if (IsDataPath(relative)) throw new InvalidDataException("更新包不得包含 Data 目录。");
            var destination = SafeCombine(stage, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); entry.ExtractToFile(destination, true);
        }
    }
    private static List<string> ReadInstalledFiles(string path)
    {
        if (!File.Exists(path)) throw new InvalidDataException("更新包缺少 installed-files.json。");
        var files = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? throw new InvalidDataException("安装文件清单无效。");
        foreach (var file in files) { _ = SafeCombine(Path.GetDirectoryName(path)!, file); if (IsDataPath(file)) throw new InvalidDataException("安装清单不得包含 Data 目录。"); }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    private static string SafeCombine(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("发现不安全的更新路径。");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("发现越界的更新路径。"); return full;
    }
    private static bool IsDataPath(string relative) { var normalized = relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar); return normalized.Equals("Data", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("Data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
}
