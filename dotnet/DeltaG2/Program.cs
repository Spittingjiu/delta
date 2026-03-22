using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Delta;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.Run(new MainForm());
    }
}

public sealed class MainForm : Form
{
    private const string Version = "G6.30";
    private const string SingBoxVersion = "1.10.3";
    private const string UpdateManifestUrl = "https://delta.zzao.de/latest.json";
    private const string DefaultExeUrlTemplate = "https://delta.zzao.de/releases/Delta v{0}.exe";
    private const string DefaultSingBoxUrl = "https://delta.zzao.de/releases/sing-box.exe";
    private const string DefaultHy2Url = "https://delta.zzao.de/releases/hy2-client.exe";
    private const string WintunZipUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip";
    private const string WintunZipSha256 = "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51";

    private enum EngineState
    {
        Idle,
        CheckingAdmin,
        CheckingWintun,
        InstallingWintun,
        PreparingConfig,
        StartingSingBox,
        WaitingTunReady,
        Running,
        Failed
    }

    private enum EngineError
    {
        None,
        AdminRequired,
        WintunCheckFailed,
        WintunInstallFailed,
        ConfigGenerateFailed,
        CoreStartFailed,
        TunCreateFailed,
        Unknown
    }
    private static string ReleaseNotes => $@"{Version} 更新内容

- 移除 INF/pnputil 驱动安装流程
- 改为仅准备 wintun.dll（从 wintun zip 解压 bin/amd64/wintun.dll 到程序目录）
- 以 sing-box TUN 创建成功/失败作为唯一接管判定信号";

    private readonly ComboBox _processCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly Label _status = new() { AutoSize = true, Text = "状态：未接管" };
    private readonly Label _engineStatus = new() { AutoSize = true, Text = "引擎：未连接" };
    private readonly Label _verifyStatus = new() { AutoSize = true, Text = "验证：未执行" };
    private readonly Label _updateStatus = new() { AutoSize = true, Text = "更新：检查中" };
    private readonly Label _routeStatus = new() { AutoSize = true, Text = "路由：未生成" };
    private readonly Label _ipDirect = new() { AutoSize = true, Text = "直连IP：-" };
    private readonly Label _ipProxy = new() { AutoSize = true, Text = "代理IP：-" };


    private readonly TextBox _hy2Ip = new() { Width = 170, Text = "178.22.26.114" };
    private readonly TextBox _hy2Port = new() { Width = 70, Text = "8443" };
    private readonly TextBox _hy2Token = new() { Width = 260 };
    private readonly TextBox _gameProcessPaths = new() { Width = 360, PlaceholderText = "游戏EXE全路径，支持多个(;分隔)" };
    private readonly TextBox _launcherProcessPaths = new() { Width = 360, PlaceholderText = "启动器EXE全路径，可选，支持多个(;)" };

    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Font = new("Consolas", 10f)
    };

    private readonly Button _btnCheckUpdate = new() { Text = "检查更新", AutoSize = true };
    private readonly Button _btnUpdate = new() { Text = "一键更新", AutoSize = true };

    private Process? _engineProc;
    private string? _activeProcess;
    private string _currentTunInterface = "DeltaTun";
    private string _currentTunCidr = "172.19.0.1/30";
    private string? _latestVersion;
    private string? _latestExeUrl;
    private string? _latestExeFileName;
    private string? _latestSingBoxUrl;
    private string? _latestHy2Url;
    private bool _verboseLogs = false;
    private bool _useTunMode = true;
    private EngineState _engineState = EngineState.Idle;
    private EngineError _lastEngineError = EngineError.None;
    private int _engineRunId = 0;

    private void SetEngineState(EngineState state)
    {
        _engineState = state;
        _engineStatus.Text = "引擎状态：" + state;
        Log($"[STATE] {state}");
    }

    private void FailEngine(EngineError code, string shortMessage, string? raw = null)
    {
        _engineState = EngineState.Failed;
        _lastEngineError = code;
        _engineStatus.Text = "引擎状态：Failed";
        Log($"[ERR:{code}] {shortMessage}");
        if (!string.IsNullOrWhiteSpace(raw))
            Log("[RAW] " + raw.Replace("\r", " ").Replace("\n", " | "));
    }

    public MainForm()
    {
        Text = "Delta " + Version;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Width = 1120;
        Height = 780;
        StartPosition = FormStartPosition.CenterScreen;

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            AutoSize = false,
            WrapContents = false,
            Padding = new Padding(8, 8, 8, 4)
        };

        var btnRefresh = new Button { Text = "刷新进程", AutoSize = true };
        var btnApply = new Button { Text = "开始接管(进程->HY2)", AutoSize = true };
        var btnVerify = new Button { Text = "验证接管", AutoSize = true };
        var btnRollback = new Button { Text = "停止/回滚", AutoSize = true };
        var chkVerbose = new CheckBox { Text = "详细日志", AutoSize = true };

        btnRefresh.Click += (_, _) => RefreshProcesses();
        btnApply.Click += async (_, _) => await ApplyTakeoverAsync();
        btnVerify.Click += async (_, _) => await VerifyTakeoverAsync();
        btnRollback.Click += (_, _) => Rollback();
        _btnCheckUpdate.Click += async (_, _) => await CheckUpdateAsync(true);
        chkVerbose.CheckedChanged += (_, _) => _verboseLogs = chkVerbose.Checked;
        _btnUpdate.Click += async (_, _) => await UpdateToLatestAsync();

        top.Controls.Add(btnRefresh);
        top.Controls.Add(_processCombo);
        top.Controls.Add(btnApply);
        top.Controls.Add(btnVerify);
        top.Controls.Add(btnRollback);
        top.Controls.Add(_btnCheckUpdate);
        top.Controls.Add(_btnUpdate);
        top.Controls.Add(chkVerbose);

        var cfgPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 124,
            AutoSize = false,
            WrapContents = true,
            Padding = new Padding(8, 4, 8, 4)
        };

        var btnEngineStop = new Button { Text = "停止接管引擎", AutoSize = true };
        var btnEngineRestart = new Button { Text = "重启接管引擎", AutoSize = true };
        var btnNetRepair = new Button { Text = "一键网络修复", AutoSize = true };
        var btnPickGame = new Button { Text = "选择游戏EXE", AutoSize = true };
        var btnPickLauncher = new Button { Text = "选择启动器EXE", AutoSize = true };
        var chkTunMode = new CheckBox { Text = "TUN模式(需虚拟网卡)", AutoSize = true, Checked = true };

        btnEngineStop.Click += (_, _) => StopEngine();
        btnEngineRestart.Click += async (_, _) => await RestartEngineAsync();
        btnNetRepair.Click += async (_, _) => await RepairNetworkAsync();
        btnPickGame.Click += (_, _) => PickExeInto(_gameProcessPaths);
        btnPickLauncher.Click += (_, _) => PickExeInto(_launcherProcessPaths);
        chkTunMode.CheckedChanged += (_, _) => _useTunMode = chkTunMode.Checked;

        cfgPanel.Controls.Add(new Label { Text = "HY2 IP", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        cfgPanel.Controls.Add(_hy2Ip);
        cfgPanel.Controls.Add(new Label { Text = "端口", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        cfgPanel.Controls.Add(_hy2Port);
        cfgPanel.Controls.Add(new Label { Text = "Token", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        cfgPanel.Controls.Add(_hy2Token);
        cfgPanel.Controls.Add(new Label { Text = "游戏路径", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        cfgPanel.Controls.Add(_gameProcessPaths);
        cfgPanel.Controls.Add(btnPickGame);
        cfgPanel.Controls.Add(new Label { Text = "启动器路径", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        cfgPanel.Controls.Add(_launcherProcessPaths);
        cfgPanel.Controls.Add(btnPickLauncher);
        cfgPanel.Controls.Add(chkTunMode);
        cfgPanel.Controls.Add(btnEngineStop);
        cfgPanel.Controls.Add(btnEngineRestart);
        cfgPanel.Controls.Add(btnNetRepair);

        var statusPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 30,
            AutoSize = false,
            WrapContents = false,
            Padding = new Padding(10, 4, 10, 2)
        };
        statusPanel.Controls.Add(_status);
        statusPanel.Controls.Add(new Label { Text = "   " });
        statusPanel.Controls.Add(_engineStatus);

        var verifyPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 30,
            AutoSize = false,
            WrapContents = false,
            Padding = new Padding(10, 0, 10, 4)
        };
        verifyPanel.Controls.Add(_verifyStatus);
        verifyPanel.Controls.Add(new Label { Text = "   " });
        verifyPanel.Controls.Add(_updateStatus);
        verifyPanel.Controls.Add(new Label { Text = "   " });
        verifyPanel.Controls.Add(_routeStatus);
        verifyPanel.Controls.Add(new Label { Text = "   " });
        verifyPanel.Controls.Add(_ipDirect);
        verifyPanel.Controls.Add(new Label { Text = "   " });
        verifyPanel.Controls.Add(_ipProxy);

        Controls.Add(_log);
        Controls.Add(verifyPanel);
        Controls.Add(statusPanel);
        Controls.Add(cfgPanel);
        Controls.Add(top);

        FormClosing += (_, _) =>
        {
            SaveSettings();
            StopEngine();
        };

        Load += (_, _) =>
        {
            var settings = LoadSettings();
            _hy2Ip.Text = string.IsNullOrWhiteSpace(settings.Hy2Server) ? "178.22.26.114" : settings.Hy2Server;
            _hy2Port.Text = settings.Hy2Port <= 0 ? "8443" : settings.Hy2Port.ToString();
            _hy2Token.Text = settings.Hy2Token ?? "";
            _gameProcessPaths.Text = settings.GameProcessPaths ?? "";
            _launcherProcessPaths.Text = settings.LauncherProcessPaths ?? "";

            Log("Delta 版本: " + Version);
            Log($"管理员权限: {(IsAdmin() ? "是" : "否")}");
            Log("模式：默认无TUN（无虚拟网卡依赖）；可手动开启 TUN 模式。") ;
            Log("验收口径：引擎存活 + TUN up + HY2隧道ESTABLISHED + 目标进程活动连接。") ;
            RefreshProcesses();
            if (!string.Equals(settings.LastSeenVersion, Version, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(ReleaseNotes, $"Delta {Version} 更新内容", MessageBoxButtons.OK, MessageBoxIcon.Information);
                settings.LastSeenVersion = Version;
                SaveSettings(settings);
            }
            _ = CheckUpdateAsync(false);
            _ = RefreshIpProbeAsync();
        };
    }

    private bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void RefreshProcesses()
    {
        try
        {
            var list = Process.GetProcesses()
                .Select(p => p.ProcessName + ".exe")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _processCombo.Items.Clear();
            foreach (var p in list) _processCombo.Items.Add(p);
            if (_processCombo.Items.Count > 0) _processCombo.SelectedIndex = 0;

            Log($"进程数量: {list.Count}");
        }
        catch (Exception ex)
        {
            Log("刷新进程失败: " + ex.Message);
        }
    }

    private async Task ApplyTakeoverAsync()
    {
        var proc = _processCombo.SelectedItem?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(proc))
        {
            Log("请先选择进程");
            return;
        }

        _activeProcess = proc;
        Log($"目标进程: {_activeProcess}");

        if (!_useTunMode)
        {
            _status.Text = "状态：未接管（请开启 TUN 模式）";
            Log("当前关闭了 TUN 模式，无法执行进程级接管。请勾选 TUN模式 后重试。");
            MessageBox.Show("进程接管必须开启 TUN 模式。\n请勾选 TUN模式(需虚拟网卡) 后再开始接管。", "Delta 提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var ok = await StartEngineAsync(_activeProcess);
        if (!ok)
        {
            _status.Text = "状态：接管失败";
            return;
        }

        _status.Text = $"状态：接管策略已启用（{_activeProcess}）";
        await VerifyTakeoverAsync();
        await RefreshIpProbeAsync();
    }

    private async Task RestartEngineAsync()
    {
        if (string.IsNullOrWhiteSpace(_activeProcess))
        {
            MessageBox.Show("当前没有已选择的目标进程。请先点开始接管。", "Delta 提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Log("执行引擎重启...");
        StopEngine();
        var ok = await StartEngineAsync(_activeProcess);
        if (ok)
        {
            _status.Text = $"状态：接管策略已启用（{_activeProcess}）";
            await VerifyTakeoverAsync();
        }
    }

    private void PickExeInto(TextBox target)
    {
        using var ofd = new OpenFileDialog
        {
            Filter = "Executable|*.exe",
            Multiselect = true,
            Title = "选择 EXE"
        };
        if (ofd.ShowDialog() == DialogResult.OK)
        {
            var existing = (target.Text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            foreach (var f in ofd.FileNames)
            {
                if (!existing.Any(x => x.Equals(f, StringComparison.OrdinalIgnoreCase)))
                    existing.Add(f);
            }
            target.Text = string.Join(';', existing);
            SaveSettings();
        }
    }

    private void Rollback()
    {
        _activeProcess = null;
        StopEngine();
        _status.Text = "状态：未接管";
        _verifyStatus.Text = "验证：未执行";
        Log("已回滚（接管引擎已停止）");
    }

    private async Task<bool> StartEngineAsync(string? processName)
    {
        try
        {
            _engineRunId++;
            var runId = _engineRunId;
            _lastEngineError = EngineError.None;
            SetEngineState(EngineState.Idle);

            var ip = (_hy2Ip.Text ?? "").Trim();
            var token = (_hy2Token.Text ?? "").Trim();
            var portTxt = (_hy2Port.Text ?? "8443").Trim();

            if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(token))
            {
                FailEngine(EngineError.ConfigGenerateFailed, "请填写 HY2 IP 和 Token");
                return false;
            }
            if (!int.TryParse(portTxt, out var port) || port <= 0 || port > 65535)
            {
                FailEngine(EngineError.ConfigGenerateFailed, "HY2 端口无效");
                return false;
            }
            if (string.IsNullOrWhiteSpace(processName))
            {
                FailEngine(EngineError.ConfigGenerateFailed, "未指定目标进程");
                return false;
            }

            SaveSettings();

            if (_useTunMode)
            {
                SetEngineState(EngineState.CheckingAdmin);
                if (!IsAdmin())
                {
                    FailEngine(EngineError.AdminRequired, "需要管理员权限");
                    return false;
                }

                SetEngineState(EngineState.CheckingWintun);
                var okTun = await EnsureTunReadyAsync();
                if (!okTun)
                {
                    _status.Text = "状态：接管失败（TUN未就绪）";
                    return false;
                }

                await HardCleanupBeforeStartAsync();
            }

            SetEngineState(EngineState.PreparingConfig);
            var exePath = await EnsureSingBoxAsync();
            _currentTunInterface = BuildTunInterfaceName();
            _currentTunCidr = BuildTunCidr();
            var cfgPath = await WriteSingBoxConfigAsync(processName, ip, port, token, _currentTunInterface, _currentTunCidr, _useTunMode);

            StopEngine();

            SetEngineState(EngineState.StartingSingBox);
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"run -c \"{cfgPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory
            };

            _engineProc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _engineProc.OutputDataReceived += (_, e) =>
            {
                if (runId != _engineRunId) return;
                if (!string.IsNullOrWhiteSpace(e.Data) && _verboseLogs)
                    BeginInvoke(() => Log($"[RUN#{runId}] SB> " + e.Data));
            };
            _engineProc.ErrorDataReceived += (_, e) =>
            {
                if (runId != _engineRunId) return;
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    var t = e.Data;
                    if (t.Contains("FATAL", StringComparison.OrdinalIgnoreCase) || (_verboseLogs && t.Contains("ERROR", StringComparison.OrdinalIgnoreCase)))
                        BeginInvoke(() => Log($"[RUN#{runId}] SB! " + t));
                }
            };
            _engineProc.Exited += (_, _) => BeginInvoke(() =>
            {
                if (runId != _engineRunId) return;
                if (_engineState != EngineState.Running)
                    SetEngineState(EngineState.Failed);
                else
                    SetEngineState(EngineState.Idle);
                Log($"[RUN#{runId}] 接管引擎已退出");
            });

            if (!_engineProc.Start())
            {
                FailEngine(EngineError.CoreStartFailed, "接管引擎启动失败");
                return false;
            }
            _engineProc.BeginOutputReadLine();
            _engineProc.BeginErrorReadLine();

            SetEngineState(EngineState.WaitingTunReady);
            var ready = await WaitForTunReadyAsync(_useTunMode, _currentTunInterface, 8000);
            if (!ready)
            {
                FailEngine(EngineError.TunCreateFailed, "TUN 接口创建失败或超时");
                try { _engineProc.Kill(true); } catch { }
                return false;
            }

            SetEngineState(EngineState.Running);
            Log($"[RUN#{runId}] 接管引擎已启动");
            return true;
        }
        catch (Exception ex)
        {
            FailEngine(EngineError.Unknown, "接管引擎异常", ex.ToString());
            return false;
        }
    }


    private sealed class CmdResult
    {
        public int ExitCode { get; set; }
        public string StdOut { get; set; } = "";
        public string StdErr { get; set; } = "";
    }


    private async Task<bool> WaitForTunReadyAsync(bool useTunMode, string tunInterface, int timeoutMs)
    {
        if (!useTunMode) return true;

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var up = NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.Name.Equals(tunInterface, StringComparison.OrdinalIgnoreCase) &&
                n.OperationalStatus == OperationalStatus.Up);
            if (up) return true;
            await Task.Delay(250);
        }

        return false;
    }

    private async Task<bool> EnsureTunReadyAsync()
    {
        try
        {
            SetEngineState(EngineState.CheckingWintun);

            if (!IsAdmin())
            {
                Log("[TunAdminRequired] 当前非管理员权限，阻断接管");
                MessageBox.Show("需要管理员权限才能启用 TUN。\n请右键以管理员身份运行 Delta。", "Delta TUN 前置检查", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            SetEngineState(EngineState.InstallingWintun);
            var ok = await EnsureWintunDllReadyAsync();
            if (!ok)
            {
                Log("[TunDllPrepareFailed] wintun.dll 准备失败");
                MessageBox.Show("wintun.dll 准备失败，已阻止接管启动。", "Delta TUN 前置检查", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            Log("wintun.dll 已就绪，进入 sing-box TUN 创建阶段");
            return true;
        }
        catch (Exception ex)
        {
            Log("[TunUnknownError] TUN 前置检查异常: " + ex.Message);
            return false;
        }
    }

    private async Task<bool> EnsureWintunDllReadyAsync()
    {
        try
        {
            var appDir = GetDataDir();
            var targetDll = Path.Combine(appDir, "wintun.dll");

            var cacheDir = Path.Combine(appDir, "cache");
            Directory.CreateDirectory(cacheDir);
            var cachedZip = Path.Combine(cacheDir, "wintun-0.14.1.zip");

            if (!File.Exists(cachedZip) || !VerifySha256(cachedZip, WintunZipSha256))
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                    var bytes = await http.GetByteArrayAsync(WintunZipUrl);
                    await File.WriteAllBytesAsync(cachedZip, bytes);
                }
                catch (Exception ex)
                {
                    Log("[TunDownloadFailed] 下载 Wintun 失败: " + ex.Message);
                    return false;
                }
            }

            if (!VerifySha256(cachedZip, WintunZipSha256))
            {
                Log("[TunHashMismatch] Wintun zip hash 校验失败");
                return false;
            }

            var tempRoot = Path.Combine(Path.GetTempPath(), "DeltaWintun_" + DateTime.Now.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(tempRoot);
            ZipFile.ExtractToDirectory(cachedZip, tempRoot, true);

            var dll = Path.Combine(tempRoot, "wintun", "bin", "amd64", "wintun.dll");
            if (!File.Exists(dll))
            {
                dll = Directory.GetFiles(tempRoot, "wintun.dll", SearchOption.AllDirectories)
                    .FirstOrDefault(x => x.Contains("amd64", StringComparison.OrdinalIgnoreCase))
                    ?? Directory.GetFiles(tempRoot, "wintun.dll", SearchOption.AllDirectories).FirstOrDefault()
                    ?? "";
            }

            if (string.IsNullOrWhiteSpace(dll) || !File.Exists(dll))
            {
                Log("[TunDllPrepareFailed] 压缩包内未找到 bin/amd64/wintun.dll");
                try { Directory.Delete(tempRoot, true); } catch { }
                return false;
            }

            File.Copy(dll, targetDll, true);
            Log($"wintun.dll 已写入: {targetDll}");

            try { Directory.Delete(tempRoot, true); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            Log("[TunDllPrepareFailed] 准备 wintun.dll 异常: " + ex.Message);
            return false;
        }
    }


    private bool VerifySha256(string path, string expectedLowerHex)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(fs);
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            return string.Equals(hex, expectedLowerHex, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task HardCleanupBeforeStartAsync()
    {
        try
        {
            Log("执行硬清理：停止残留 sing-box + 清理 DeltaTun* IPv4 配置...");

            _ = await RunHiddenAsync("cmd.exe", "/c taskkill /F /IM sing-box.exe >nul 2>nul");

            var ps = "Get-NetAdapter -Name 'DeltaTun*' -ErrorAction SilentlyContinue | ForEach-Object { " +
                     "try { Get-NetIPAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue } catch {} }";

            _ = await RunHiddenAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"");
            Log("硬清理完成");
        }
        catch (Exception ex)
        {
            Log("硬清理异常（已忽略继续）: " + ex.Message);
        }
    }

    private async Task<CmdResult> RunHiddenAsync(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi);
        if (p == null) return new CmdResult { ExitCode = -1 };

        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();

        return new CmdResult
        {
            ExitCode = p.ExitCode,
            StdOut = await outTask,
            StdErr = await errTask
        };
    }

    private async Task RepairNetworkAsync()
    {
        try
        {
            Log("开始一键网络修复...");
            StopEngine();

            _ = await RunHiddenAsync("cmd.exe", "/c taskkill /F /IM sing-box.exe >nul 2>nul");
            _ = await RunHiddenAsync("cmd.exe", "/c taskkill /F /IM hysteria.exe >nul 2>nul");

            // 清理 WinINET 代理
            _ = await RunHiddenAsync("cmd.exe", "/c reg add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyEnable /t REG_DWORD /d 0 /f");
            _ = await RunHiddenAsync("cmd.exe", "/c reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyServer /f");
            _ = await RunHiddenAsync("cmd.exe", "/c reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v AutoConfigURL /f");

            // 清理 WinHTTP 代理
            _ = await RunHiddenAsync("netsh", "winhttp reset proxy");

            // 清理可能残留的 TUN 网卡 IP
            var ps = "Get-NetAdapter -Name 'DeltaTun*' -ErrorAction SilentlyContinue | ForEach-Object { " +
                     "try { Get-NetIPAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue } catch {} }";
            _ = await RunHiddenAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"");

            // 重置网络栈
            _ = await RunHiddenAsync("netsh", "winsock reset");
            _ = await RunHiddenAsync("netsh", "int ip reset");
            _ = await RunHiddenAsync("ipconfig", "/flushdns");

            Log("网络修复完成：已清理代理与网络栈。建议重启电脑后再测试。");
            MessageBox.Show("网络修复完成。\n已清理系统代理/WinHTTP/Winsock/IP 栈。\n建议重启电脑后再测试网络。", "Delta 网络修复", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("网络修复失败: " + ex.Message);
            MessageBox.Show("网络修复失败：" + ex.Message, "Delta 网络修复", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopEngine()
    {
        try
        {
            if (_engineProc != null && !_engineProc.HasExited)
            {
                _engineProc.Kill(true);
                _engineProc.WaitForExit(2000);
            }
        }
        catch { }
        finally
        {
            _engineProc?.Dispose();
            _engineProc = null;
            _engineStatus.Text = "引擎：未连接";
        }
    }

    private async Task VerifyTakeoverAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                var selectedProcess = _activeProcess;
                if (string.IsNullOrWhiteSpace(selectedProcess))
                    selectedProcess = _processCombo.SelectedItem?.ToString()?.Trim();

                var serverIpText = (_hy2Ip.Text ?? "").Trim();
                var serverPortOk = int.TryParse((_hy2Port.Text ?? "8443").Trim(), out var serverPort);
                if (!serverPortOk) serverPort = 8443;

                var checks = new List<string>();
                var pass = 0;
                var total = 0;

                void Check(bool ok, string okMsg, string failMsg)
                {
                    total++;
                    if (ok)
                    {
                        pass++;
                        checks.Add("✅ " + okMsg);
                    }
                    else
                    {
                        checks.Add("❌ " + failMsg);
                    }
                }

                var engineRunning = _engineProc != null && !_engineProc.HasExited;
                Check(engineRunning, "sing-box 引擎存活", "sing-box 引擎未运行");

                if (_useTunMode)
                {
                    var tunUp = NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                        n.Name.Equals(_currentTunInterface, StringComparison.OrdinalIgnoreCase) &&
                        n.OperationalStatus == OperationalStatus.Up);
                    Check(tunUp, $"{_currentTunInterface} 虚拟网卡已 UP", $"{_currentTunInterface} 虚拟网卡未 UP");
                }
                else
                {
                    Check(false, "", "未开启 TUN 模式，未进入进程接管路径");
                }

                var netRows = GetNetstatRows();

                var tunnelEstablished = false;
                if (engineRunning && !string.IsNullOrWhiteSpace(serverIpText) && _engineProc != null)
                {
                    tunnelEstablished = netRows.Any(r =>
                        r.Pid == _engineProc.Id &&
                        r.RemoteAddress == serverIpText &&
                        r.RemotePort == serverPort &&
                        (
                            (r.Protocol == "TCP" && r.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) ||
                            (r.Protocol == "UDP")
                        ));
                }
                Check(tunnelEstablished,
                    $"HY2 通道已建立 ({serverIpText}:{serverPort}, TCP/UDP)",
                    $"未检测到到 {serverIpText}:{serverPort} 的 HY2 通道(TCP/UDP)");

                if (_useTunMode)
                {
                    if (string.IsNullOrWhiteSpace(selectedProcess))
                    {
                        checks.Add("⚠️ 未选择目标进程，无法做进程验证");
                    }
                    else
                    {
                        var procName = Path.GetFileNameWithoutExtension(selectedProcess).ToLowerInvariant();
                        var targetPids = Process.GetProcessesByName(procName).Select(p => p.Id).ToHashSet();
                        var targetAlive = targetPids.Count > 0;
                        Check(targetAlive,
                            $"目标进程 {selectedProcess} 正在运行 (PID: {string.Join(",", targetPids)})",
                            $"目标进程 {selectedProcess} 未运行");

                        if (targetAlive)
                        {
                            var targetConns = netRows.Where(r =>
                                targetPids.Contains(r.Pid) &&
                                !IsLoopback(r.RemoteAddress) &&
                                (
                                    (r.Protocol == "TCP" && r.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) ||
                                    (r.Protocol == "UDP")
                                )).ToList();

                            checks.Add($"ℹ️ 目标进程外网活跃连接数(TCP/UDP): {targetConns.Count}");
                            if (targetConns.Count > 0)
                            {
                                pass++;
                                total++;
                                checks.Add("✅ 目标进程有活跃外网连接，可进入接管路径");
                            }
                            else
                            {
                                total++;
                                checks.Add("❌ 目标进程暂无活跃外网连接，当前无法判断接管效果");
                            }
                        }
                    }

                    var strongPass = pass >= Math.Min(total, 4);
                    if (strongPass)
                        checks.Add("✅ 判定：接管链路有效（TUN+隧道+目标进程）");
                    else
                        checks.Add("❌ 判定：接管链路未闭环，请看失败项");
                }
                else
                {
                    checks.Add("❌ 判定：当前未启用 TUN 模式，未进入进程接管链路");
                }

                var summary = $"验证结果：{pass}/{total} 通过";
                var detail = string.Join(Environment.NewLine, checks);

                BeginInvoke(() =>
                {
                    _verifyStatus.Text = summary;
                    Log(summary);
                    Log(detail);
                });
            }
            catch (Exception ex)
            {
                BeginInvoke(() =>
                {
                    _verifyStatus.Text = "验证：执行异常";
                    Log("验证异常: " + ex.Message);
                });
            }
        });
    }

    private static bool IsLoopback(string ip)
    {
        if (string.Equals(ip, "127.0.0.1", StringComparison.OrdinalIgnoreCase) || string.Equals(ip, "::1", StringComparison.OrdinalIgnoreCase))
            return true;
        if (IPAddress.TryParse(ip, out var addr))
            return IPAddress.IsLoopback(addr);
        return false;
    }

    private static List<NetRow> GetNetstatRows()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netstat",
            Arguments = "-ano",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) return new List<NetRow>();

        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(3000);

        var rows = new List<NetRow>();
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!(line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase) || line.StartsWith("UDP", StringComparison.OrdinalIgnoreCase)))
                continue;

            var parts = Regex.Split(line, "\\s+")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
            if (parts.Length < 4) continue;

            var proto = parts[0].ToUpperInvariant();
            if (!TrySplitEndpoint(parts[1], out var localIp, out var localPort)) continue;
            if (!TrySplitEndpoint(parts[2], out var remoteIp, out var remotePort)) continue;

            string state;
            int pid;

            if (proto == "TCP")
            {
                if (parts.Length < 5) continue;
                state = parts[3];
                if (!int.TryParse(parts[4], out pid)) continue;
            }
            else
            {
                // UDP: netstat 输出通常为 UDP local remote pid
                state = "";
                if (!int.TryParse(parts[3], out pid)) continue;
            }

            rows.Add(new NetRow
            {
                Protocol = proto,
                LocalAddress = localIp,
                LocalPort = localPort,
                RemoteAddress = remoteIp,
                RemotePort = remotePort,
                State = state,
                Pid = pid
            });
        }

        return rows;
    }

    private static bool TrySplitEndpoint(string endpoint, out string ip, out int port)
    {
        ip = "";
        port = 0;

        endpoint = endpoint.Trim();
        if (endpoint.StartsWith("["))
        {
            var idx = endpoint.LastIndexOf(']');
            if (idx <= 0) return false;
            ip = endpoint[1..idx];
            var rest = endpoint[(idx + 1)..].TrimStart(':');
            return int.TryParse(rest, out port);
        }

        var sep = endpoint.LastIndexOf(':');
        if (sep <= 0 || sep >= endpoint.Length - 1) return false;
        ip = endpoint[..sep];
        return int.TryParse(endpoint[(sep + 1)..], out port);
    }

    private async Task<string> EnsureSingBoxAsync()
    {
        var exeInApp = Path.Combine(AppContext.BaseDirectory, "sing-box.exe");
        if (File.Exists(exeInApp)) return exeInApp;

        var dataDir = GetDataDir();
        var exeInData = Path.Combine(dataDir, "sing-box.exe");
        if (File.Exists(exeInData)) return exeInData;

        var zipPath = Path.Combine(dataDir, "sing-box.zip");
        var url = $"https://github.com/SagerNet/sing-box/releases/download/v{SingBoxVersion}/sing-box-{SingBoxVersion}-windows-amd64.zip";

        Log("未找到 sing-box.exe，开始下载...");
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
        {
            var bytes = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(zipPath, bytes);
        }

        var extractDir = Path.Combine(dataDir, "sing-box");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        ZipFile.ExtractToDirectory(zipPath, extractDir, true);

        var found = Directory.GetFiles(extractDir, "sing-box.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(found))
            throw new FileNotFoundException("下载成功但未找到 sing-box.exe");

        File.Copy(found, exeInData, true);
        Log("sing-box 下载并解压完成");
        return exeInData;
    }

    private async Task<string> WriteSingBoxConfigAsync(string processName, string serverIp, int serverPort, string token, string tunInterface, string tunCidr, bool useTunMode)
    {
        var dataDir = GetDataDir();
        var cfgPath = Path.Combine(dataDir, "sing-box-delta.json");

        var processExe = processName.Trim();
        var processBare = Path.GetFileNameWithoutExtension(processExe);

        var gamePaths = (_gameProcessPaths.Text ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var launcherPaths = (_launcherProcessPaths.Text ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (gamePaths.Count == 0)
        {
            // fallback: 至少用 process_name
            gamePaths.Add(processExe);
        }

        var rules = new List<object>();

        // 优先 process_path（游戏）
        var gameRulePaths = gamePaths.Where(x => x.Contains('\\') || x.Contains('/')).ToArray();
        if (gameRulePaths.Length > 0)
        {
            rules.Add(new
            {
                process_path = gameRulePaths,
                action = "route",
                outbound = "hy2-out"
            });
        }

        // 启动器路径
        var launcherRulePaths = launcherPaths.Where(x => x.Contains('\\') || x.Contains('/')).ToArray();
        if (launcherRulePaths.Length > 0)
        {
            rules.Add(new
            {
                process_path = launcherRulePaths,
                action = "route",
                outbound = "hy2-out"
            });
        }

        // fallback process_name
        var nameFallback = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { processExe, processBare };
        foreach (var gp in gamePaths)
        {
            var n = Path.GetFileName(gp);
            var b = Path.GetFileNameWithoutExtension(gp);
            if (!string.IsNullOrWhiteSpace(n)) nameFallback.Add(n);
            if (!string.IsNullOrWhiteSpace(b)) nameFallback.Add(b);
        }
        foreach (var lp in launcherPaths)
        {
            var n = Path.GetFileName(lp);
            var b = Path.GetFileNameWithoutExtension(lp);
            if (!string.IsNullOrWhiteSpace(n)) nameFallback.Add(n);
            if (!string.IsNullOrWhiteSpace(b)) nameFallback.Add(b);
        }

        rules.Add(new
        {
            process_name = nameFallback.ToArray(),
            action = "route",
            outbound = "hy2-out"
        });

        object cfg;
        if (useTunMode)
        {
            cfg = new
            {
                log = new { level = "info", timestamp = true },
                inbounds = new object[]
                {
                    new
                    {
                        type = "tun",
                        tag = "tun-in",
                        interface_name = tunInterface,
                        inet4_address = tunCidr,
                        auto_route = true,
                        strict_route = true,
                        stack = "system",
                        sniff = true
                    }
                },
                outbounds = new object[]
                {
                    new
                    {
                        type = "hysteria2",
                        tag = "hy2-out",
                        server = serverIp,
                        server_port = serverPort,
                        password = token,
                        tls = new { enabled = true, server_name = "www.bing.com", insecure = true }
                    },
                    new { type = "direct", tag = "direct" }
                },
                route = new
                {
                    auto_detect_interface = true,
                    rules = rules,
                    final = "direct"
                }
            };
        }
        else
        {
            cfg = new
            {
                log = new { level = "info", timestamp = true },
                inbounds = new object[]
                {
                    new { type = "mixed", tag = "mixed-in", listen = "127.0.0.1", listen_port = 10809 }
                },
                outbounds = new object[]
                {
                    new
                    {
                        type = "hysteria2",
                        tag = "hy2-out",
                        server = serverIp,
                        server_port = serverPort,
                        password = token,
                        tls = new { enabled = true, server_name = "www.bing.com", insecure = true }
                    },
                    new { type = "direct", tag = "direct" }
                },
                route = new
                {
                    rules = rules,
                    final = "direct"
                }
            };
        }

        var text = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(cfgPath, text);

        _routeStatus.Text = $"路由：{rules.Count}条规则，final=direct";
        Log($"已写入接管配置: {cfgPath}");
        Log($"规则(游戏路径): {string.Join(" | ", gameRulePaths)}");
        if (launcherRulePaths.Length > 0)
            Log($"规则(启动器路径): {string.Join(" | ", launcherRulePaths)}");
        Log($"规则(名称回退): {string.Join(",", nameFallback)} -> hy2-out");
        Log("回退保护：未匹配流量走 direct");
        if (useTunMode)
        {
            Log($"TUN 接口名: {tunInterface}");
            Log($"TUN 网段: {tunCidr}");
        }
        else
        {
            Log("当前模式：无TUN（mixed入站 127.0.0.1:10809）");
        }

        return cfgPath;
    }


    private async Task CheckUpdateAsync(bool manual)
    {
        try
        {
            _btnCheckUpdate.Enabled = false;
            _updateStatus.Text = "更新：检查中";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var json = await http.GetStringAsync(UpdateManifestUrl);
            var m = JsonSerializer.Deserialize<UpdateManifest>(json);

            if (m == null || string.IsNullOrWhiteSpace(m.version) || string.IsNullOrWhiteSpace(m.url))
            {
                _updateStatus.Text = "更新：清单异常";
                _btnUpdate.Enabled = false;
                if (manual)
                    MessageBox.Show("更新清单异常，请稍后重试。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _latestVersion = m.version.Trim();
            _latestExeUrl = string.IsNullOrWhiteSpace(m.exeUrl) ? string.Format(DefaultExeUrlTemplate, _latestVersion) : m.exeUrl.Trim();
            _latestExeFileName = string.IsNullOrWhiteSpace(m.exeFileName) ? $"Delta v{_latestVersion}.exe" : m.exeFileName.Trim();
            _latestSingBoxUrl = string.IsNullOrWhiteSpace(m.singBoxUrl) ? DefaultSingBoxUrl : m.singBoxUrl.Trim();
            _latestHy2Url = string.IsNullOrWhiteSpace(m.hy2Url) ? DefaultHy2Url : m.hy2Url.Trim();

            if (IsNewerVersion(_latestVersion, Version))
            {
                _updateStatus.Text = $"更新：发现新版本 {_latestVersion}";
                _btnUpdate.Text = $"一键更新到 {_latestVersion}";
                _btnUpdate.Enabled = true;
                Log($"发现新版本：{_latestVersion}");
                if (manual)
                    MessageBox.Show($"发现新版本：{_latestVersion}", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                _updateStatus.Text = "更新：已是最新";
                _btnUpdate.Text = "一键更新";
                _btnUpdate.Enabled = false;
                if (manual)
                    MessageBox.Show("当前已是最新版本。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            _updateStatus.Text = "更新：检查失败";
            _btnUpdate.Enabled = false;
            Log("检查更新失败: " + ex.Message);
            if (manual)
                MessageBox.Show("检查更新失败：" + ex.Message, "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnCheckUpdate.Enabled = true;
        }
    }

    private async Task UpdateToLatestAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_latestExeUrl))
            {
                await CheckUpdateAsync(false);
                if (string.IsNullOrWhiteSpace(_latestExeUrl))
                {
                    MessageBox.Show("无法获取更新清单，请稍后再试。", "Delta 更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            var currentExe = Application.ExecutablePath;
            var currentDir = Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory;
            var verText = string.IsNullOrWhiteSpace(_latestVersion) ? "latest" : _latestVersion;
            var newExeName = $"Delta v{verText}.exe";
            var targetExePath = Path.Combine(currentDir, newExeName);

            var hasTargetExe = File.Exists(targetExePath);
            var hasSingBox = File.Exists(Path.Combine(currentDir, "sing-box.exe"));
            var hasHy2 = File.Exists(Path.Combine(currentDir, "hy2-client.exe"));

            if (hasTargetExe && hasSingBox && hasHy2)
            {
                Log($"检测到 {newExeName} + sing-box.exe + hy2-client.exe 已存在，跳过下载。");
                MessageBox.Show($"当前目录已存在 {newExeName}、sing-box.exe、hy2-client.exe。\n无需重复下载。", "Delta 更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Log($"开始一键更新：下载 {newExeName} ...");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

            var exeBytes = await http.GetByteArrayAsync(_latestExeUrl!);
            await File.WriteAllBytesAsync(targetExePath, exeBytes);

            if (!hasSingBox)
            {
                Log("缺少 sing-box.exe，开始补齐...");
                var singUrl = string.IsNullOrWhiteSpace(_latestSingBoxUrl) ? DefaultSingBoxUrl : _latestSingBoxUrl!;
                var singBytes = await http.GetByteArrayAsync(singUrl);
                await File.WriteAllBytesAsync(Path.Combine(currentDir, "sing-box.exe"), singBytes);
            }
            else
            {
                Log("已存在 sing-box.exe，跳过下载。");
            }

            if (!hasHy2)
            {
                Log("缺少 hy2-client.exe，开始补齐...");
                var hy2Url = string.IsNullOrWhiteSpace(_latestHy2Url) ? DefaultHy2Url : _latestHy2Url!;
                var hy2Bytes = await http.GetByteArrayAsync(hy2Url);
                await File.WriteAllBytesAsync(Path.Combine(currentDir, "hy2-client.exe"), hy2Bytes);
            }
            else
            {
                Log("已存在 hy2-client.exe，跳过下载。");
            }

            Log($"更新完成：已更新到 {verText}。{newExeName} 已就位。");
            MessageBox.Show($"更新完成。\n目标版本：{verText}\n已写入当前目录。\n请关闭当前程序后启动 {newExeName}。", "Delta 更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("一键更新失败: " + ex.Message);
            MessageBox.Show("一键更新失败：" + ex.Message, "Delta 更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RefreshIpProbeAsync()
    {
        try
        {
            var direct = await QueryPublicIpAsync(false);
            var proxy = await QueryPublicIpAsync(true);
            _ipDirect.Text = string.IsNullOrWhiteSpace(direct) ? "直连IP：-" : $"直连IP：{direct}";
            _ipProxy.Text = string.IsNullOrWhiteSpace(proxy) ? "代理IP：-" : $"代理IP：{proxy}";
            Log($"IP探针：直连={direct ?? "-"} / 代理={proxy ?? "-"}");
        }
        catch (Exception ex)
        {
            Log("IP探针异常: " + ex.Message);
        }
    }

    private async Task<string?> QueryPublicIpAsync(bool viaProxy)
    {
        var handler = new HttpClientHandler();
        if (viaProxy)
        {
            handler.Proxy = new WebProxy("http://127.0.0.1:10809");
            handler.UseProxy = true;
        }

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        var urls = new[] { "https://api.ipify.org", "https://ifconfig.me/ip", "https://icanhazip.com" };

        foreach (var u in urls)
        {
            try
            {
                var t = (await http.GetStringAsync(u)).Trim();
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
            catch { }
        }

        return null;
    }

    private static bool IsNewerVersion(string remote, string local)
    {
        static int Parse(string v)
        {
            var s = v.Trim().ToUpperInvariant();
            if (s.StartsWith("G")) s = s[1..];
            var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var major = parts.Length > 0 && int.TryParse(parts[0], out var a) ? a : 0;
            var minor = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : 0;
            var patch = parts.Length > 2 && int.TryParse(parts[2], out var c) ? c : 0;
            return major * 10000 + minor * 100 + patch;
        }

        return Parse(remote) > Parse(local);
    }

    private string BuildTunInterfaceName()
    {
        // Windows 上若残留同名 TUN 设备，sing-box 可能报：Cannot create a file when that file already exists
        // 每次启动用新接口名，避免被残留网卡占用。
        var suffix = DateTime.Now.ToString("HHmmss");
        return $"DeltaTun{suffix}";
    }

    private string BuildTunCidr()
    {
        // 避免 "set ipv4 address: The object already exists"：每次启动切换一个 /30 网段
        var seed = DateTime.UtcNow.Ticks;
        var b = (int)(seed % 200) + 20;  // 20..219
        var c = (int)((seed / 13) % 200) + 20;
        return $"172.{b}.{c}.1/30";
    }

    private string GetDataDir()
    {
        // 老板要求：配置与运行文件写在当前目录，不写 AppData
        var dir = Path.GetDirectoryName(Application.ExecutablePath) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private DeltaSettings LoadSettings()
    {
        try
        {
            var p = Path.Combine(GetDataDir(), "settings.json");
            if (!File.Exists(p)) return new DeltaSettings();
            var t = File.ReadAllText(p);
            return JsonSerializer.Deserialize<DeltaSettings>(t) ?? new DeltaSettings();
        }
        catch
        {
            return new DeltaSettings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            var current = LoadSettings();
            SaveSettings(current);
        }
        catch { }
    }

    private void SaveSettings(DeltaSettings current)
    {
        try
        {
            var p = Path.Combine(GetDataDir(), "settings.json");
            var s = new DeltaSettings
            {
                Hy2Server = (_hy2Ip.Text ?? "").Trim(),
                Hy2Token = (_hy2Token.Text ?? "").Trim(),
                Hy2Port = int.TryParse((_hy2Port.Text ?? "8443").Trim(), out var n) ? n : 8443,
                LastSeenVersion = current.LastSeenVersion,
                GameProcessPaths = (_gameProcessPaths.Text ?? "").Trim(),
                LauncherProcessPaths = (_launcherProcessPaths.Text ?? "").Trim()
            };
            File.WriteAllText(p, JsonSerializer.Serialize(s));
        }
        catch { }
    }

    private void Log(string msg)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            try { BeginInvoke(() => Log(msg)); } catch { }
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n";
        _log.AppendText(line);

        const int maxChars = 200000;
        if (_log.TextLength > maxChars)
        {
            _log.Text = _log.Text[^maxChars..];
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }
    }
}

public sealed class DeltaSettings
{
    public string? Hy2Server { get; set; }
    public string? Hy2Token { get; set; }
    public int Hy2Port { get; set; } = 8443;
    public string? LastSeenVersion { get; set; }
    public string? GameProcessPaths { get; set; }
    public string? LauncherProcessPaths { get; set; }
}

public sealed class NetRow
{
    public string Protocol { get; set; } = "";
    public string LocalAddress { get; set; } = "";
    public int LocalPort { get; set; }
    public string RemoteAddress { get; set; } = "";
    public int RemotePort { get; set; }
    public string State { get; set; } = "";
    public int Pid { get; set; }
}

public sealed class UpdateManifest
{
    public string? version { get; set; }
    public string? url { get; set; }              // 兼容旧字段（zip）
    public string? fileName { get; set; }         // 兼容旧字段（zip）
    public string? exeUrl { get; set; }
    public string? exeFileName { get; set; }
    public string? singBoxUrl { get; set; }
    public string? hy2Url { get; set; }
    public string? notes { get; set; }
    public string? publishedAt { get; set; }
}
