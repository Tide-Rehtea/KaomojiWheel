using System;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;
using Application = System.Windows.Application;

namespace KaomojiWheel;

public partial class App : Application
{
    private Mutex? _mutex; private EventWaitHandle? _showEvent; private RegisteredWaitHandle? _showWait; private Forms.NotifyIcon? _tray; private MainWindow? _window; private AppStore? _store;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (UpdateService.IsInstallerInvocation(e.Args)) { var exitCode = await UpdateService.RunInstallerAsync(e.Args); Shutdown(exitCode); return; }
        DispatcherUnhandledException += (_, args) => FileLogger.TryWrite(AppContext.BaseDirectory, args.Exception); _mutex = new Mutex(true, "KaomojiWheel.SingleInstance.v1", out var first);
        if (!first) { try { EventWaitHandle.OpenExisting("KaomojiWheel.ShowExisting.v1").Set(); } catch { } Shutdown(); return; }
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "KaomojiWheel.ShowExisting.v1");
            _store = new AppStore(AppContext.BaseDirectory); _store.Load(); _window = new MainWindow(_store); _window.InitializeHotkey(); CreateTray(); _window.UpdateAvailable += manifest => _tray?.ShowBalloonTip(5000, "颜文字轮盘有新版本", $"v{manifest.Version} 已发布，可在设置 → 软件更新中安装。", Forms.ToolTipIcon.Info); _window.StartAutomaticUpdateChecks();
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, timedOut) => { if (!timedOut) Dispatcher.Invoke(() => _window?.ShowWheel()); }, null, Timeout.Infinite, false);
            if (!_store.Settings.OnboardingSeen) _window.ShowOnboarding();
        }
        catch (Exception ex) { FileLogger.TryWrite(AppContext.BaseDirectory, ex); MessageBox.Show($"颜文字轮盘启动失败：\n{ex.Message}", "颜文字轮盘", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(); }
    }
    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("唤出轮盘", null, (_, _) => Dispatcher.Invoke(() => _window?.ShowWheel()));
        menu.Items.Add("打开数据目录", null, (_, _) => _store?.OpenDataFolder()); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray = new Forms.NotifyIcon { Text = "颜文字轮盘", Icon = CreateTrayIcon(), ContextMenuStrip = menu, Visible = true };
    }
    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using var g = System.Drawing.Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);
        using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 31, 37, 56));
        using var glow = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 127, 151, 235));
        using var line = new System.Drawing.Pen(System.Drawing.Color.White, 2.2f);
        g.FillEllipse(bg, 2, 2, 28, 28);
        for (var i = 0; i < 6; i++) { var a = i * Math.PI / 3; g.FillEllipse(glow, 14.5f + (float)Math.Cos(a) * 12, 14.5f + (float)Math.Sin(a) * 12, 3, 3); }
        g.DrawArc(line, 9, 8, 14, 14, 25, 130); g.DrawArc(line, 9, 8, 14, 14, 205, 130);
        var handle = bitmap.GetHicon(); return System.Drawing.Icon.FromHandle(handle);
    }
    private void ExitApp() { _window?.Dispose(); if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); } Shutdown(); }
    protected override void OnExit(ExitEventArgs e) { _showWait?.Unregister(null); _showEvent?.Dispose(); _tray?.Dispose(); try { _mutex?.ReleaseMutex(); } catch { } _mutex?.Dispose(); base.OnExit(e); }
}
