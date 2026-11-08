using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace UpSnapTray;

internal static class Log
{
    private static readonly object Gate = new();
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpSnapTray");
    public static string FilePath { get; } = Path.Combine(DataDir, "log.txt");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            lock (Gate)
            {
                File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}

// Settings live outside the repository: %APPDATA%\UpSnapTray\settings.json
// { "url": "http://192.168.1.10:8090", "lang": "en" }
internal static class Settings
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UpSnapTray", "settings.json");

    public static string Url { get; private set; } = "http://localhost:8090";
    public static string Lang { get; private set; } = DefaultLang();

    static Settings()
    {
        Load();
    }

    private static string DefaultLang()
    {
        var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return string.Equals(two, "zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
    }

    private static void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.TryGetProperty("url", out var urlValue) && urlValue.ValueKind == JsonValueKind.String)
            {
                var url = urlValue.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    Url = url.TrimEnd('/');
                }
            }

            if (doc.RootElement.TryGetProperty("lang", out var langValue) && langValue.ValueKind == JsonValueKind.String)
            {
                var lang = langValue.GetString();
                if (lang is "zh" or "en")
                {
                    Lang = lang;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("settings read failed: " + ex.Message);
        }
    }

    public static void SetLang(string lang)
    {
        if (lang is not ("zh" or "en"))
        {
            return;
        }

        Lang = lang;
        Save();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = "{\n  \"url\": \"" + Url + "\",\n  \"lang\": \"" + Lang + "\"\n}";
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Log.Write("settings write failed: " + ex.Message);
        }
    }
}

internal static class Loc
{
    private static readonly Dictionary<string, (string Zh, string En)> Map = new()
    {
        ["panel"] = ("打开面板", "Open panel"),
        ["browser"] = ("在浏览器打开", "Open in browser"),
        ["reload"] = ("刷新", "Reload"),
        ["autostart"] = ("开机启动", "Start with Windows"),
        ["exit"] = ("退出", "Exit"),
        ["language"] = ("语言", "Language"),
        ["zh"] = ("中文", "中文"),
        ["en"] = ("English", "English"),
        ["webviewFail"] = ("WebView2 初始化失败", "WebView2 failed to initialize"),
    };

    public static string T(string key)
    {
        if (!Map.TryGetValue(key, out var value))
        {
            return key;
        }

        return Settings.Lang == "zh" ? value.Zh : value.En;
    }
}

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    private static void Main(string[] args)
    {
        _mutex = new Mutex(true, "UpSnapTray.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        var selftest = Array.Exists(args, a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));
        Application.Run(new TrayApp(selftest));
        GC.KeepAlive(_mutex);
    }
}

internal sealed class TrayApp : ApplicationContext
{
    private const string AppName = "UpSnapTray";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _panelItem;
    private readonly ToolStripMenuItem _browserItem;
    private readonly ToolStripMenuItem _reloadItem;
    private readonly ToolStripMenuItem _autoItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly ToolStripMenuItem _langMenu;
    private readonly ToolStripMenuItem _zhItem;
    private readonly ToolStripMenuItem _enItem;
    private PopupForm? _popup;
    private DateTime _lastHide = DateTime.MinValue;

    public TrayApp(bool selftest)
    {
        Log.Write("start exe=" + Application.ExecutablePath + " selftest=" + selftest + " url=" + Settings.Url + " lang=" + Settings.Lang);

        _tray = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "UpSnapTray",
            Visible = true,
        };

        _menu = new ContextMenuStrip();
        _panelItem = new ToolStripMenuItem(Loc.T("panel"));
        _panelItem.Click += (_, _) => ShowPopup();
        _browserItem = new ToolStripMenuItem(Loc.T("browser"));
        _browserItem.Click += (_, _) => OpenInBrowser();
        _reloadItem = new ToolStripMenuItem(Loc.T("reload"));
        _reloadItem.Click += (_, _) => _popup?.Reload();
        _autoItem = new ToolStripMenuItem(Loc.T("autostart")) { CheckOnClick = true, Checked = IsAutostartEnabled() };
        _autoItem.Click += (_, _) => SetAutostart(_autoItem.Checked);
        _exitItem = new ToolStripMenuItem(Loc.T("exit"));
        _exitItem.Click += (_, _) => Shutdown();

        _zhItem = new ToolStripMenuItem(Loc.T("zh")) { Checked = Settings.Lang == "zh" };
        _zhItem.Click += (_, _) => SwitchLanguage("zh");
        _enItem = new ToolStripMenuItem(Loc.T("en")) { Checked = Settings.Lang == "en" };
        _enItem.Click += (_, _) => SwitchLanguage("en");
        _langMenu = new ToolStripMenuItem(Loc.T("language"));
        _langMenu.DropDownItems.Add(_zhItem);
        _langMenu.DropDownItems.Add(_enItem);

        _menu.Items.Add(_panelItem);
        _menu.Items.Add(_browserItem);
        _menu.Items.Add(_reloadItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_autoItem);
        _menu.Items.Add(_langMenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);
        _tray.ContextMenuStrip = _menu;

        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                TogglePopup();
            }
        };

        var marker = Path.Combine(Log.DataDir, "firstrun.done");
        if (!File.Exists(marker))
        {
            SetAutostart(true);
            _autoItem.Checked = true;
            try
            {
                File.WriteAllText(marker, DateTime.Now.ToString("s"));
            }
            catch
            {
            }
            Log.Write("first run: autostart enabled");
        }

        if (selftest)
        {
            ShowPopup();
            var seconds = 12;
            var envSeconds = Environment.GetEnvironmentVariable("UPSNAPTRAY_SELFTEST_SEC");
            if (int.TryParse(envSeconds, out var parsed) && parsed > 0 && parsed <= 600)
            {
                seconds = parsed;
            }

            var timer = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Log.Write("SELFTEST done navOk=" + (_popup?.NavigationSucceeded == true) + " lang=" + Settings.Lang);
                Shutdown();
            };
            timer.Start();
        }
    }

    private void SwitchLanguage(string lang)
    {
        Settings.SetLang(lang);
        ApplyLanguage();
        Log.Write("lang -> " + lang);
    }

    private void ApplyLanguage()
    {
        _panelItem.Text = Loc.T("panel");
        _browserItem.Text = Loc.T("browser");
        _reloadItem.Text = Loc.T("reload");
        _autoItem.Text = Loc.T("autostart");
        _exitItem.Text = Loc.T("exit");
        _langMenu.Text = Loc.T("language");
        _zhItem.Text = Loc.T("zh");
        _enItem.Text = Loc.T("en");
        _zhItem.Checked = Settings.Lang == "zh";
        _enItem.Checked = Settings.Lang == "en";
    }

    private void TogglePopup()
    {
        if (_popup is { Visible: true })
        {
            _popup.Hide();
            _lastHide = DateTime.UtcNow;
            return;
        }

        if ((DateTime.UtcNow - _lastHide).TotalMilliseconds < 350)
        {
            return;
        }

        ShowPopup();
    }

    private void ShowPopup()
    {
        if (_popup is null || _popup.IsDisposed)
        {
            _popup = new PopupForm();
            _popup.HiddenByDeactivate += (_, _) => _lastHide = DateTime.UtcNow;
        }

        _popup.Initialize();
        _popup.ShowNearTray();
    }

    private static void OpenInBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Settings.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("open browser failed: " + ex.Message);
        }
    }

    private static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(AppName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SetAutostart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                key.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
            Log.Write("autostart -> " + enabled);
        }
        catch (Exception ex)
        {
            Log.Write("autostart failed: " + ex.Message);
        }
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var path = Path.Combine(Log.DataDir, "upsnap.ico");
            if (!File.Exists(path))
            {
                using var bmp = new Bitmap(64, 64);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var bg = new SolidBrush(Color.FromArgb(255, 20, 24, 31)))
                    {
                        g.FillEllipse(bg, 1, 1, 62, 62);
                    }

                    using var pen = new Pen(Color.FromArgb(255, 45, 212, 191), 6f)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round,
                    };
                    g.DrawArc(pen, 14, 18, 36, 36, -50, 280);
                    g.DrawLine(pen, 32, 10, 32, 32);
                }

                var hIcon = bmp.GetHicon();
                using (var tmp = Icon.FromHandle(hIcon))
                using (var fs = File.Create(path))
                {
                    tmp.Save(fs);
                }
                DestroyIcon(hIcon);
            }

            return new Icon(path);
        }
        catch (Exception ex)
        {
            Log.Write("icon failed: " + ex.Message);
            return SystemIcons.Application;
        }
    }

    private void Shutdown()
    {
        Log.Write("shutdown");
        _tray.Visible = false;
        _tray.Dispose();
        _popup?.Close();
        ExitThread();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

internal sealed class PopupForm : Form
{
    private static readonly string UserDataDir = Path.Combine(Log.DataDir, "WebView2");
    private WebView2? _web;
    private bool _initializing;
    private bool _initialized;

    public event EventHandler? HiddenByDeactivate;
    public event EventHandler? NavigationFinished;
    public bool NavigationSucceeded { get; private set; }

    public PopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(520, 760);
        BackColor = Color.FromArgb(20, 24, 31);
        Padding = new Padding(1);
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Hide();
            }
        };
    }

    public async void Initialize()
    {
        if (_initializing || _initialized)
        {
            return;
        }

        _initializing = true;
        try
        {
            Directory.CreateDirectory(UserDataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, UserDataDir);
            _web = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(20, 24, 31),
            };
            Controls.Add(_web);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                NavigationSucceeded = e.IsSuccess;
                Log.Write("nav completed success=" + e.IsSuccess);
                NavigationFinished?.Invoke(this, EventArgs.Empty);
            };
            _web.CoreWebView2.Navigate(Settings.Url);
            _initialized = true;
            Log.Write("webview init ok, navigating");
        }
        catch (Exception ex)
        {
            Log.Write("webview init failed: " + ex.Message);
            Controls.Clear();
            Controls.Add(new Label
            {
                Text = Loc.T("webviewFail") + "\r\n\r\n" + ex.Message,
                Dock = DockStyle.Fill,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(70, 24, 24),
            });
        }
        finally
        {
            _initializing = false;
        }
    }

    public void Reload()
    {
        try
        {
            _web?.CoreWebView2?.Reload();
        }
        catch
        {
        }
    }

    public void ShowNearTray()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(Cursor.Position);
        Location = new Point(area.Right - Width - 8, area.Bottom - Height - 8);
        Show();
        Activate();
        BringToFront();
        SetForegroundWindow(Handle);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible)
        {
            Hide();
            HiddenByDeactivate?.Invoke(this, EventArgs.Empty);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
