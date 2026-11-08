using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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
// { "url": "http://192.168.1.10:8090" }
internal static class Settings
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UpSnapTray", "settings.json");

    public static string Url { get; } = Load();

    private static string Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                if (doc.RootElement.TryGetProperty("url", out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var url = value.GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url.TrimEnd('/');
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("settings read failed: " + ex.Message);
        }

        return "http://localhost:8090";
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
    private readonly ToolStripMenuItem _autoItem;
    private PopupForm? _popup;
    private DateTime _lastHide = DateTime.MinValue;

    public TrayApp(bool selftest)
    {
        Log.Write("start exe=" + Application.ExecutablePath + " selftest=" + selftest + " url=" + Settings.Url);

        _tray = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "UpSnap",
            Visible = true,
        };

        _menu = new ContextMenuStrip();
        _menu.Items.Add("打开面板", null, (_, _) => ShowPopup());
        _menu.Items.Add("在浏览器打开", null, (_, _) => OpenInBrowser());
        _menu.Items.Add("刷新", null, (_, _) => _popup?.Reload());
        _menu.Items.Add(new ToolStripSeparator());
        _autoItem = new ToolStripMenuItem("开机启动") { CheckOnClick = true, Checked = IsAutostartEnabled() };
        _autoItem.Click += (_, _) => SetAutostart(_autoItem.Checked);
        _menu.Items.Add(_autoItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => Shutdown());
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
            var timer = new System.Windows.Forms.Timer { Interval = 12000 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Log.Write("SELFTEST done navOk=" + (_popup?.NavigationSucceeded == true));
                Shutdown();
            };
            timer.Start();
        }
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
                Text = "WebView2 初始化失败\r\n\r\n" + ex.Message,
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
