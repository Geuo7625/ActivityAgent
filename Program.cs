using Microsoft.Win32;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

internal static class Program
{
    // 只在本机回环地址监听，外部网络无法访问。
    private const string ListenIp = "127.0.0.1";
    private const int ListenPort = 12000;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "ActivityAgent";

    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ActivityAgent");
    private static readonly string LogPath = Path.Combine(DataDirectory, "agent.log");
    private static readonly string CustomIconPath = Path.Combine(DataDirectory, "custom-icon.ico");
    private static readonly string SettingsPath = Path.Combine(DataDirectory, "settings.json");
    private static AgentSettings _settings = AgentSettings.CreateDefault();

    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly object LogLock = new();
    private static readonly object IconLock = new();
    private static readonly List<Icon> RetainedIcons = new();
    private static readonly ConcurrentDictionary<Guid, ClientConnection> Clients = new();
    private static ActivitySnapshot? _current;

    [STAThread]
    public static async Task Main()
    {
        using var singleInstance = new Mutex(true, @"Local\ActivityAgent", out var isFirstInstance);
        if (!isFirstInstance)
            return;

        LoadSettings();

        using var cts = new CancellationTokenSource();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://{ListenIp}:{ListenPort}/ws/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Log($"Could not listen on {ListenIp}:{ListenPort}: {ex}");
            ShowError($"无法启动监听：{ListenIp}:{ListenPort}\n\n请检查端口是否被占用，以及 HTTP URL ACL 权限。\n详细信息已写入日志：\n{LogPath}\n\n{ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            Log($"Listener startup failed: {ex}");
            ShowError($"程序启动失败，详细信息已写入日志：\n{LogPath}\n\n{ex.Message}");
            return;
        }

        Log($"Activity Agent listening at ws://{ListenIp}:{ListenPort}/ws/ (no authentication)");
        Log("App metadata only: no window titles, executable paths, or app icons are sent.");

        var trayThread = new Thread(() => RunTray(() =>
        {
            cts.Cancel();
            try { listener.Stop(); } catch { }
        }))
        {
            IsBackground = true,
            Name = "Activity Agent tray"
        };
        trayThread.SetApartmentState(ApartmentState.STA);
        trayThread.Start();

        var pollTask = PollForegroundAsync(cts.Token);
        try
        {
            while (!cts.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().WaitAsync(cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) when (cts.IsCancellationRequested) { break; }

                _ = Task.Run(() => HandleContextAsync(context, cts.Token));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            cts.Cancel();
            try { await pollTask; } catch (OperationCanceledException) { }
            if (trayThread.IsAlive)
                trayThread.Join(1500);
            lock (IconLock)
            {
                foreach (var icon in RetainedIcons)
                    icon.Dispose();
                RetainedIcons.Clear();
            }
        }
    }

    private static void RunTray(Action requestExit)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var menu = new ContextMenuStrip();
        var statusItem = new ToolStripMenuItem("状态：运行中") { Enabled = false };
        var startupItem = new ToolStripMenuItem("开机时自动启动")
        {
            Checked = IsStartupEnabled(),
            CheckOnClick = false
        };
        var editRulesItem = new ToolStripMenuItem("应用名称映射与排除列表…");
        var chooseIconItem = new ToolStripMenuItem("更换托盘图标…");
        var resetIconItem = new ToolStripMenuItem("恢复默认图标");
        var openLogItem = new ToolStripMenuItem("查看运行日志");
        var exitItem = new ToolStripMenuItem("退出 Activity Agent");

        startupItem.Click += (_, _) =>
        {
            var enable = !startupItem.Checked;
            try
            {
                SetStartupEnabled(enable);
                startupItem.Checked = enable;
                trayIconSafeNotify("Activity Agent", enable ? "已添加到开机启动项" : "已关闭开机自动启动");
                Log(enable ? "Startup enabled by user." : "Startup disabled by user.");
            }
            catch (Exception ex)
            {
                Log($"Could not change startup setting: {ex}");
                MessageBox.Show($"修改开机启动设置失败：\n{ex.Message}", "Activity Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        editRulesItem.Click += (_, _) => ShowSettingsEditor();
        chooseIconItem.Click += (_, _) => ChooseCustomIcon();
        resetIconItem.Click += (_, _) => ResetCustomIcon();
        openLogItem.Click += (_, _) => OpenLog();
        exitItem.Click += (_, _) =>
        {
            requestExit();
            Application.ExitThread();
        };

        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startupItem);
        menu.Items.Add(editRulesItem);
        menu.Items.Add(chooseIconItem);
        menu.Items.Add(resetIconItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openLogItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        // Load and assign the real icon before making NotifyIcon visible. If Visible is
        // set first, Windows may briefly create the tray entry with a generic icon.
        using var trayIcon = new NotifyIcon
        {
            Text = "Activity Agent",
            ContextMenuStrip = menu
        };
        ApplyTrayIcon(trayIcon);
        trayIcon.Visible = true;
        trayIcon.DoubleClick += (_, _) => OpenLog();
        trayIcon.BalloonTipTitle = "Activity Agent";
        trayIcon.BalloonTipText = "正在后台检测当前打开的软件。";
        trayIcon.BalloonTipIcon = ToolTipIcon.Info;

        // NotifyIcon requires a Windows message loop.
        Application.Run();
        trayIcon.Visible = false;
    }

    // Tray handlers run on the STA tray thread. The currently displayed icon is kept alive
    // until shutdown so Explorer never sees a disposed HICON while the tray is refreshing.
    private static NotifyIcon? _trayIconForNotifications;

    private static void trayIconSafeNotify(string title, string message)
    {
        try
        {
            _trayIconForNotifications?.ShowBalloonTip(2500, title, message, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log($"Could not show tray notification: {ex.Message}");
        }
    }

    private static void ApplyTrayIcon(NotifyIcon trayIcon)
    {
        var icon = LoadTrayIcon();
        lock (IconLock)
            RetainedIcons.Add(icon);
        trayIcon.Icon = icon;
        _trayIconForNotifications = trayIcon;
    }

    private static Icon LoadTrayIcon()
    {
        if (File.Exists(CustomIconPath))
        {
            try { return new Icon(CustomIconPath); }
            catch (Exception ex) { Log($"Could not load custom tray icon: {ex.Message}"); }
        }

        // The default icon is app.ico beside the executable (the installation root).
        // Prefer the executable's directory explicitly, then AppContext.BaseDirectory.
        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        var defaultIconPaths = new[]
        {
            string.IsNullOrWhiteSpace(executableDirectory) ? null : Path.Combine(executableDirectory, "app.ico"),
            Path.Combine(AppContext.BaseDirectory, "app.ico")
        };

        foreach (var defaultIconPath in defaultIconPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(defaultIconPath))
                continue;

            try
            {
                var icon = new Icon(defaultIconPath!);
                Log($"Loaded default tray icon: {defaultIconPath}");
                return icon;
            }
            catch (Exception ex)
            {
                Log($"Could not load default tray icon '{defaultIconPath}': {ex.Message}");
            }
        }

        Log($"Default app.ico was not found beside the executable. Falling back to the Windows application icon. Expected: {Path.Combine(AppContext.BaseDirectory, "app.ico")}");
        return (Icon)SystemIcons.Application.Clone();
    }

    private static void ChooseCustomIcon()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择托盘图标",
            Filter = "图标或图片|*.ico;*.png;*.jpg;*.jpeg;*.bmp|ICO 图标|*.ico|PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg|BMP 图片|*.bmp",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != DialogResult.OK)
            return;

        var tempPath = CustomIconPath + ".tmp";
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var extension = Path.GetExtension(dialog.FileName);
            using Icon icon = extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
                ? new Icon(dialog.FileName)
                : CreateIconFromImage(dialog.FileName);
            using (var stream = File.Create(tempPath))
                icon.Save(stream);
            File.Move(tempPath, CustomIconPath, true);

            if (_trayIconForNotifications is not null)
                ApplyTrayIcon(_trayIconForNotifications);
            trayIconSafeNotify("Activity Agent", "托盘图标已更新");
            Log($"Custom tray icon changed from: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            Log($"Could not change tray icon: {ex}");
            MessageBox.Show($"无法使用这张图片作为托盘图标：\n{ex.Message}", "Activity Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Icon CreateIconFromImage(string imagePath)
    {
        using var source = Image.FromFile(imagePath);
        using var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var scale = Math.Min(64f / source.Width, 64f / source.Height);
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            var destination = new Rectangle((64 - width) / 2, (64 - height) / 2, width, height);
            graphics.DrawImage(source, destination);
        }

        var handle = bitmap.GetHicon();
        try
        {
            var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);

    private static void ResetCustomIcon()
    {
        try
        {
            if (File.Exists(CustomIconPath))
                File.Delete(CustomIconPath);
            if (_trayIconForNotifications is not null)
                ApplyTrayIcon(_trayIconForNotifications);
            trayIconSafeNotify("Activity Agent", "已恢复默认托盘图标");
            Log("Custom tray icon reset.");
        }
        catch (Exception ex)
        {
            Log($"Could not reset tray icon: {ex}");
            MessageBox.Show($"恢复默认图标失败：\n{ex.Message}", "Activity Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(StartupValueName) is string command && !string.IsNullOrWhiteSpace(command);
        }
        catch (Exception ex)
        {
            Log($"Could not read startup setting: {ex.Message}");
            return false;
        }
    }

    private static void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
            ?? throw new InvalidOperationException("无法打开当前用户的开机启动注册表项。");

        if (enabled)
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new InvalidOperationException("无法确定程序 EXE 路径。");
            key.SetValue(StartupValueName, $"\"{executablePath}\"");
        }
        else
        {
            key.DeleteValue(StartupValueName, false);
        }
    }

    private static void OpenLog()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            if (!File.Exists(LogPath))
                File.WriteAllText(LogPath, "Activity Agent log" + Environment.NewLine, Encoding.UTF8);
            var startInfo = new ProcessStartInfo("notepad.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(LogPath);
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Log($"Could not open log: {ex}");
            MessageBox.Show($"无法打开日志：\n{ex.Message}", "Activity Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void ShowError(string message)
    {
        Log(message);
        MessageBox.Show(message, "Activity Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static async Task HandleContextAsync(HttpListenerContext context, CancellationToken ct)
    {
        Guid? registeredClientId = null;
        try
        {
            if (!context.Request.IsWebSocketRequest || context.Request.Url?.AbsolutePath != "/ws/")
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.Close();
                return;
            }

            // 只监听 127.0.0.1，且不再校验 Token，任何本机客户端均可连接。
            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
            using var ws = wsContext.WebSocket;
            var clientId = Guid.NewGuid();
            registeredClientId = clientId;
            var client = new ClientConnection(ws);
            Clients[clientId] = client;
            Log($"Client connected: {context.Request.RemoteEndPoint}");

            await SendJsonAsync(client, new
            {
                type = "started",
                protocolVersion = 1,
                server = "ActivityAgent",
                current = GetCurrentSnapshot()
            }, ct);

            var buffer = new byte[8192];
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (ws.State == WebSocketState.Open)
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        Log("Client disconnected.");
                        return;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "text messages only", CancellationToken.None);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 64 * 1024)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", CancellationToken.None);
                        return;
                    }
                } while (!result.EndOfMessage);

                await HandleClientMessageAsync(client, Encoding.UTF8.GetString(message.ToArray()), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { Log($"WebSocket error: {ex.Message}"); }
        catch (HttpListenerException ex) { Log($"HTTP listener error: {ex.Message}"); }
        catch (Exception ex) { Log($"Client error: {ex}"); }
        finally
        {
            if (registeredClientId is Guid id)
                Clients.TryRemove(id, out _);
        }
    }

    private static async Task HandleClientMessageAsync(ClientConnection client, string text, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

            switch (type)
            {
                case "get_current":
                    await SendJsonAsync(client, new { type = "current", current = GetCurrentSnapshot() }, ct);
                    break;
                case "ping":
                    await SendJsonAsync(client, new { type = "pong", timestamp = DateTimeOffset.UtcNow }, ct);
                    break;
                default:
                    await SendJsonAsync(client, new
                    {
                        type = "error",
                        message = "Unknown message type. Supported: get_current, ping."
                    }, ct);
                    break;
            }
        }
        catch (JsonException)
        {
            await SendJsonAsync(client, new { type = "error", message = "Invalid JSON." }, ct);
        }
    }

    private static async Task PollForegroundAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var next = ForegroundAppReader.Read();
                if (next is not null && (_current?.AppId != next.AppId || _current?.Name != next.Name))
                {
                    _current = next with { ChangedAt = DateTimeOffset.UtcNow };
                    Log($"Foreground app: {_current.Name} ({_current.AppId})");
                    await BroadcastAsync(new { type = "activity", current = _current }, ct);
                }

                await Task.Delay(500, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Foreground detection error: {ex.Message}");
                await Task.Delay(500, ct);
            }
        }
    }

    private static ActivitySnapshot? GetCurrentSnapshot() => _current;

    private static async Task BroadcastAsync(object value, CancellationToken ct)
    {
        foreach (var pair in Clients)
        {
            try
            {
                await SendJsonAsync(pair.Value, value, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"Broadcast failed for client {pair.Key}: {ex.Message}");
                Clients.TryRemove(pair.Key, out _);
            }
        }
    }

    private static async Task SendJsonAsync(ClientConnection client, object value, CancellationToken ct)
    {
        var ws = client.Socket;
        if (ws.State != WebSocketState.Open) return;

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));
        await client.SendLock.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            client.SendLock.Release();
        }
    }

    private sealed class ClientConnection(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private static void Log(string message)
    {
        lock (LogLock)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch
            {
                // Logging failure should never stop the background agent.
            }
        }
    }

    internal sealed record ActivitySnapshot(string AppId, string Name, DateTimeOffset ChangedAt);

    private sealed class NameMapping
    {
        public NameMapping() { }

        public string ProcessName { get; set; } = "";
        public string WindowTitleContains { get; set; } = "";
        public string AppId { get; set; } = "";
        public string DisplayName { get; set; } = "";

        public NameMapping Clone() => new()
        {
            ProcessName = ProcessName,
            WindowTitleContains = WindowTitleContains,
            AppId = AppId,
            DisplayName = DisplayName
        };
    }

    private sealed class AgentSettings
    {
        public AgentSettings() { }

        public List<NameMapping> NameMappings { get; set; } = new();
        public List<string> ExcludedProcesses { get; set; } = new();
        public List<string> ExcludedWindowClasses { get; set; } = new();

        public static AgentSettings CreateDefault()
        {
            var settings = new AgentSettings
            {
                ExcludedProcesses = new List<string>
                {
                    "Taskmgr", "SystemSettings", "WindowsTerminal", "OpenConsole", "conhost",
                    "cmd", "powershell", "pwsh", "LockApp", "LogonUI", "SearchHost",
                    "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "ScreenClippingHost"
                },
                ExcludedWindowClasses = new List<string>
                {
                    "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"
                }
            };

            // Match the Minecraft window title locally first. The title is never transmitted.
            settings.NameMappings.Add(new NameMapping { ProcessName = "javaw", WindowTitleContains = "Minecraft", AppId = "minecraft", DisplayName = "Minecraft" });
            settings.NameMappings.Add(new NameMapping { ProcessName = "java", WindowTitleContains = "Minecraft", AppId = "minecraft", DisplayName = "Minecraft" });
            settings.NameMappings.Add(new NameMapping { ProcessName = "Minecraft.Windows", AppId = "minecraft", DisplayName = "Minecraft" });
            settings.NameMappings.Add(new NameMapping { ProcessName = "mstsc", AppId = "mstsc", DisplayName = "远程桌面连接" });
            settings.NameMappings.Add(new NameMapping { ProcessName = "javaw", AppId = "javaw", DisplayName = "Java" });
            settings.NameMappings.Add(new NameMapping { ProcessName = "java", AppId = "java", DisplayName = "Java" });

            var friendlyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Code"] = "Visual Studio Code",
                ["devenv"] = "Visual Studio",
                ["msedge"] = "Microsoft Edge",
                ["chrome"] = "Google Chrome",
                ["firefox"] = "Mozilla Firefox",
                ["brave"] = "Brave",
                ["Discord"] = "Discord",
                ["Telegram"] = "Telegram",
                ["QQ"] = "QQ",
                ["WeChat"] = "微信",
                ["steam"] = "Steam",
                ["Blender"] = "Blender",
                ["Photoshop"] = "Adobe Photoshop",
                ["Illustrator"] = "Adobe Illustrator",
                ["obs64"] = "OBS Studio",
                ["explorer"] = "文件资源管理器"
            };
            foreach (var item in friendlyNames)
                settings.NameMappings.Add(new NameMapping
                {
                    ProcessName = item.Key,
                    AppId = item.Key.ToLowerInvariant(),
                    DisplayName = item.Value
                });

            return settings;
        }
    }

    private static AgentSettings CloneSettings(AgentSettings settings) => new()
    {
        NameMappings = settings.NameMappings.Select(x => x.Clone()).ToList(),
        ExcludedProcesses = settings.ExcludedProcesses.ToList(),
        ExcludedWindowClasses = settings.ExcludedWindowClasses.ToList()
    };

    private static void NormalizeSettings(AgentSettings settings)
    {
        settings.NameMappings ??= new List<NameMapping>();
        settings.ExcludedProcesses ??= new List<string>();
        settings.ExcludedWindowClasses ??= new List<string>();

        settings.NameMappings = settings.NameMappings
            .Where(x => x is not null)
            .Select(x =>
            {
                x.ProcessName = NormalizeProcessName(x.ProcessName);
                x.WindowTitleContains = (x.WindowTitleContains ?? "").Trim();
                x.AppId = (x.AppId ?? "").Trim().ToLowerInvariant();
                x.DisplayName = (x.DisplayName ?? "").Trim();
                return x;
            })
            .Where(x => x.ProcessName.Length > 0 && x.DisplayName.Length > 0)
            .ToList();

        settings.ExcludedProcesses = settings.ExcludedProcesses
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeProcessName)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        settings.ExcludedWindowClasses = settings.ExcludedWindowClasses
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeProcessName(string? processName)
    {
        var value = (processName ?? "").Trim();
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];
        return value.Trim();
    }

    private static void LoadSettings()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(SettingsPath, Encoding.UTF8), SettingsJsonOptions);
                if (loaded is not null)
                {
                    NormalizeSettings(loaded);
                    Volatile.Write(ref _settings, loaded);
                    Log($"Loaded app mappings and exclusion rules from {SettingsPath}");
                    return;
                }
            }

            var defaults = AgentSettings.CreateDefault();
            SaveSettings(defaults);
            Log($"Created default settings at {SettingsPath}");
        }
        catch (Exception ex)
        {
            // Keep the source defaults in memory if the user's JSON file is malformed or inaccessible.
            Volatile.Write(ref _settings, AgentSettings.CreateDefault());
            Log($"Could not load settings; using defaults for this run: {ex}");
        }
    }

    private static void SaveSettings(AgentSettings settings)
    {
        NormalizeSettings(settings);
        Directory.CreateDirectory(DataDirectory);
        var tempPath = SettingsPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, SettingsJsonOptions), new UTF8Encoding(false));
        File.Move(tempPath, SettingsPath, true);
        Volatile.Write(ref _settings, settings);
    }

    private sealed class ListEditor
    {
        public TabPage Page { get; init; } = null!;
        public ListBox Items { get; init; } = null!;
        public TextBox Input { get; init; } = null!;
    }

    private static ListEditor CreateListEditor(string tabTitle, string description, IEnumerable<string> values)
    {
        var page = new TabPage(tabTitle);
        var hint = new Label
        {
            Text = description,
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(8),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        foreach (var value in values)
            list.Items.Add(value);

        var input = new TextBox { Width = 260, Margin = new Padding(4) };
        var addButton = new Button { Text = "添加", AutoSize = true, Margin = new Padding(4) };
        var removeButton = new Button { Text = "删除选中项", AutoSize = true, Margin = new Padding(4) };
        var commands = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            Padding = new Padding(4),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        commands.Controls.Add(input);
        commands.Controls.Add(addButton);
        commands.Controls.Add(removeButton);

        addButton.Click += (_, _) =>
        {
            var value = input.Text.Trim();
            if (value.Length == 0) return;
            if (!list.Items.Cast<string>().Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
                list.Items.Add(value);
            input.Clear();
            input.Focus();
        };
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                addButton.PerformClick();
                e.SuppressKeyPress = true;
            }
        };
        removeButton.Click += (_, _) =>
        {
            while (list.SelectedIndices.Count > 0)
                list.Items.RemoveAt(list.SelectedIndices[0]);
        };

        page.Controls.Add(list);
        page.Controls.Add(commands);
        page.Controls.Add(hint);
        return new ListEditor { Page = page, Items = list, Input = input };
    }

    private static void ShowSettingsEditor()
    {
        var active = Volatile.Read(ref _settings);
        var working = CloneSettings(active);
        var form = new Form
        {
            Text = "应用名称映射与排除列表",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            ShowInTaskbar = false,
            MinimumSize = new Size(760, 480),
            Size = new Size(900, 600)
        };
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 5) };
        var mappingPage = new TabPage("名称映射");
        var mappingHint = new Label
        {
            Text = "按进程名映射显示名称；可选填写窗口标题关键词来精确区分同一个进程运行的不同软件（例如 javaw + Minecraft）。窗口标题只在本机用于识别，不会发送到服务器。",
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(8),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var mappingBinding = new BindingList<NameMapping>(working.NameMappings.Select(x => x.Clone()).ToList());
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            DataSource = mappingBinding
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = nameof(NameMapping.ProcessName), DataPropertyName = nameof(NameMapping.ProcessName), HeaderText = "进程名（不含 .exe）", FillWeight = 90 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = nameof(NameMapping.WindowTitleContains), DataPropertyName = nameof(NameMapping.WindowTitleContains), HeaderText = "窗口标题包含（可空）", FillWeight = 125 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = nameof(NameMapping.AppId), DataPropertyName = nameof(NameMapping.AppId), HeaderText = "应用 ID（可空）", FillWeight = 75 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = nameof(NameMapping.DisplayName), DataPropertyName = nameof(NameMapping.DisplayName), HeaderText = "显示名称", FillWeight = 100 });
        grid.DataError += (_, e) => { e.ThrowException = false; };

        var mappingCommands = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            Padding = new Padding(4),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        var addMappingButton = new Button { Text = "新增映射", AutoSize = true, Margin = new Padding(4) };
        var removeMappingButton = new Button { Text = "删除选中映射", AutoSize = true, Margin = new Padding(4) };
        addMappingButton.Click += (_, _) => mappingBinding.Add(new NameMapping());
        removeMappingButton.Click += (_, _) =>
        {
            var rows = grid.SelectedRows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow)
                .Select(r => r.DataBoundItem as NameMapping).Where(x => x is not null).ToList();
            foreach (var row in rows)
                mappingBinding.Remove(row!);
        };
        mappingCommands.Controls.Add(addMappingButton);
        mappingCommands.Controls.Add(removeMappingButton);
        mappingPage.Controls.Add(grid);
        mappingPage.Controls.Add(mappingCommands);
        mappingPage.Controls.Add(mappingHint);

        var processEditor = CreateListEditor("排除进程", "当前前台进程名在此列表中时，不会更新网站展示。可填写 javaw、mstsc 等，不需要添加 .exe。", working.ExcludedProcesses);
        var classEditor = CreateListEditor("排除窗口类", "当前窗口类在此列表中时，不会更新网站展示。一般不需要修改 Windows 默认项。", working.ExcludedWindowClasses);
        tabs.TabPages.Add(mappingPage);
        tabs.TabPages.Add(processEditor.Page);
        tabs.TabPages.Add(classEditor.Page);

        var saveButton = new Button { Text = "保存", AutoSize = true, DialogResult = DialogResult.None, Margin = new Padding(6) };
        var cancelButton = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel, Margin = new Padding(6) };
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(8, 4, 12, 4),
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        bottom.Controls.Add(cancelButton);
        bottom.Controls.Add(saveButton);
        form.Controls.Add(tabs);
        form.Controls.Add(bottom);
        form.AcceptButton = saveButton;
        form.CancelButton = cancelButton;

        saveButton.Click += (_, _) =>
        {
            try
            {
                grid.EndEdit();
                form.BindingContext[mappingBinding].EndCurrentEdit();
                var mappings = mappingBinding
                    .Where(x => !string.IsNullOrWhiteSpace(x.ProcessName) ||
                                !string.IsNullOrWhiteSpace(x.WindowTitleContains) ||
                                !string.IsNullOrWhiteSpace(x.AppId) ||
                                !string.IsNullOrWhiteSpace(x.DisplayName))
                    .Select(x => x.Clone()).ToList();
                if (mappings.Any(x => string.IsNullOrWhiteSpace(x.ProcessName) || string.IsNullOrWhiteSpace(x.DisplayName)))
                {
                    MessageBox.Show("名称映射中的每一条规则都必须填写“进程名”和“显示名称”。", "Activity Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                working.NameMappings = mappings;
                working.ExcludedProcesses = processEditor.Items.Items.Cast<string>().ToList();
                working.ExcludedWindowClasses = classEditor.Items.Items.Cast<string>().ToList();
                SaveSettings(working);
                Log("App name mappings and exclusion lists updated.");
                trayIconSafeNotify("Activity Agent", "名称映射与排除列表已保存");
                form.DialogResult = DialogResult.OK;
                form.Close();
            }
            catch (Exception ex)
            {
                Log($"Could not save settings: {ex}");
                MessageBox.Show($"保存设置失败：\n{ex.Message}", "Activity Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        using (form)
            form.ShowDialog();
    }

    private static class ForegroundAppReader
    {
        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

        public static ActivitySnapshot? Read()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == 0) return null;

            var settings = Volatile.Read(ref _settings);
            var className = new StringBuilder(256);
            if (GetClassName(hwnd, className, className.Capacity) > 0 &&
                settings.ExcludedWindowClasses.Contains(className.ToString(), StringComparer.OrdinalIgnoreCase))
                return null;

            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;

            try
            {
                using var process = Process.GetProcessById((int)pid);
                var processName = process.ProcessName;
                if (settings.ExcludedProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase) ||
                    string.Equals(processName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase))
                    return null;

                // Window text is only inspected locally for app identification. It is never logged or transmitted.
                var titleBuffer = new StringBuilder(1024);
                _ = GetWindowText(hwnd, titleBuffer, titleBuffer.Capacity);
                var windowTitle = titleBuffer.ToString();
                var processMappings = settings.NameMappings
                    .Where(x => string.Equals(x.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var mapping = processMappings
                    .Where(x => !string.IsNullOrWhiteSpace(x.WindowTitleContains) &&
                                windowTitle.Contains(x.WindowTitleContains, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.WindowTitleContains.Length)
                    .FirstOrDefault()
                    ?? processMappings.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.WindowTitleContains));

                var appId = !string.IsNullOrWhiteSpace(mapping?.AppId)
                    ? mapping.AppId
                    : processName.ToLowerInvariant();
                var name = !string.IsNullOrWhiteSpace(mapping?.DisplayName)
                    ? mapping.DisplayName
                    : TryGetFileDescription(process) ?? processName;

                return new ActivitySnapshot(appId, name, DateTimeOffset.UtcNow);
            }
            catch
            {
                // If Windows temporarily denies access, keep the previous normal app.
                return null;
            }
        }

        private static string? TryGetFileDescription(Process process)
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path)) return null;
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (string.IsNullOrWhiteSpace(description) || description.Length > 120 ||
                    string.Equals(description, process.ProcessName, StringComparison.OrdinalIgnoreCase))
                    return null;
                return description;
            }
            catch
            {
                return null;
            }
        }
    }

}