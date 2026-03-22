using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
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
    private const string Version = "G6.41";
    private const string SingBoxVersion = "1.13.3";
    private const string UpdateManifestUrl = "https://delta.zzao.de/latest.json";
    private const string DefaultExeUrlTemplate = "https://delta.zzao.de/releases/Delta v{0}.exe";
    private const string DefaultSingBoxUrl = "https://delta.zzao.de/releases/sing-box.exe";
    private const string DefaultHy2Url = "https://delta.zzao.de/releases/hy2-client.exe";
    private const string Hy2Version = "20250307";
    private const string WintunZipUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip";
    private const string WintunZipSha256 = "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51";

    private enum EngineState
    {
        Idle,
        CheckingAdmin,
        PreparingWintun,
        PreparingCore,
        WritingConfig,
        StartingCore,
        WaitingTunReady,
        Running,
        Failed,
        Stopping
    }

    private enum EngineError
    {
        None,
        TunAdminRequired,
        TunDownloadFailed,
        TunHashMismatch,
        TunDllCopyFailed,
        CoreStartFailed,
        TunCreateFailed,
        TunCreateTimeout,
        HysteriaHandshakeFailed,
        NodeUnreachable,
        RouteRuleNotMatched,
        ConfigWriteFailed,
        CoreBinaryMissing,
        CoreCrashed,
        NodeProbeFailed,
        Unknown
    }

    private enum AccelerationMode
    {
        Stable,
        Balanced,
        Performance
    }
    private static string ReleaseNotes => $@"{Version} 更新内容

- 模板实质化：Stable/Balanced/Performance 现在影响 TUN 与运行参数
- Stable：更保守（sniff off、更长超时）
- Balanced：默认平衡
- Performance：更激进（更短超时、strict_route off）";

    private readonly ComboBox _processCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly ComboBox _nodeCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly ComboBox _profileCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly Label _status = new() { AutoSize = true, Text = "状态：未接管" };
    private readonly Label _engineStatus = new() { AutoSize = true, Text = "引擎：未连接" };
    private readonly Label _verifyStatus = new() { AutoSize = true, Text = "验证：未执行" };
    private readonly Label _updateStatus = new() { AutoSize = true, Text = "更新：检查中" };
    private readonly Label _routeStatus = new() { AutoSize = true, Text = "路由：-" };
    private readonly Label _diagStatus = new() { AutoSize = true, Text = "诊断：-" };
    private readonly Label _nodeHealthStatus = new() { AutoSize = true, Text = "节点健康：未测试" };
    private readonly Label _coreVersionsStatus = new() { AutoSize = true, Text = "核心：sing-box v1.13.3 | hy2 20250307" };
    private readonly Label _ipDirect = new() { AutoSize = true, Text = "直连IP：-" };
    private readonly Label _ipProxy = new() { AutoSize = true, Text = "代理IP：-" };
    private readonly Label _quickSummary = new() { AutoSize = true, Text = "[未运行] | 节点:- | 模式:稳定 | 游戏:- | 路由:直连" };
    private readonly ListBox _nodeList = new() { Dock = DockStyle.Fill };
    private readonly ListBox _gameList = new() { Dock = DockStyle.Fill };
    private readonly Label _cardEngine = new() { AutoSize = true, Text = "引擎状态：-" };
    private readonly Label _cardNode = new() { AutoSize = true, Text = "当前节点：-" };
    private readonly Label _cardMode = new() { AutoSize = true, Text = "当前模式：-" };
    private readonly Label _cardTun = new() { AutoSize = true, Text = "TUN状态：-" };
    private readonly Label _cardMatched = new() { AutoSize = true, Text = "命中进程：-" };
    private readonly Label _cardRoute = new() { AutoSize = true, Text = "当前路由：-" };
    private readonly TabControl _logsTabs = new() { Dock = DockStyle.Fill };
    private readonly TextBox _logEngineView = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10f) };
    private readonly TextBox _logCoreView = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10f) };
    private readonly TextBox _logNetView = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10f) };
    private readonly ComboBox _logLevelFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly Button _btnCopyLog = new() { Text = "复制日志", AutoSize = true };


    private readonly TextBox _hy2Ip = new() { Width = 170, Text = "178.22.26.114" };
    private readonly TextBox _hy2Port = new() { Width = 70, Text = "8443" };
    private readonly TextBox _hy2Token = new() { Width = 220, Visible = false };
    private readonly TextBox _hy2Sni = new() { Width = 160, Text = "www.bing.com", Visible = false };
    private readonly TextBox _hy2ObfsType = new() { Width = 110, PlaceholderText = "混淆类型", Visible = false };
    private readonly TextBox _hy2ObfsPassword = new() { Width = 140, PlaceholderText = "混淆密码", Visible = false };
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
    private string? _latestSingBoxVersion;
    private string? _latestHy2Version;
    private bool _verboseLogs = false;
    private bool _useTunMode = true;
    private bool _fullTunnelValidationMode = false;
    private bool _advancedMode = false;
    private EngineState _engineState = EngineState.Idle;
    private EngineError _lastEngineError = EngineError.None;
    private int _engineRunId = 0;
    private EngineError _lastTunPrepError = EngineError.None;
    private readonly List<Hy2NodeProfile> _nodes = new();
    private string? _lastConfigPath;
    private string? _lastConfigBackupPath;
    private bool _manualStopping = false;
    private int _autoRestartCount = 0;
    private AccelerationMode _accelerationMode = AccelerationMode.Stable;
    private readonly List<LogEntry> _recentEngineLogs = new();
    private readonly List<LogEntry> _recentCoreLogs = new();
    private readonly List<LogEntry> _recentProbeLogs = new();

    private static string EngineStateZh(EngineState s) => s switch
    {
        EngineState.Idle => "空闲",
        EngineState.CheckingAdmin => "检查管理员权限",
        EngineState.PreparingWintun => "准备Wintun",
        EngineState.PreparingCore => "准备核心",
        EngineState.WritingConfig => "写入配置",
        EngineState.StartingCore => "启动核心",
        EngineState.WaitingTunReady => "等待TUN就绪",
        EngineState.Running => "运行中",
        EngineState.Failed => "失败",
        EngineState.Stopping => "停止中",
        _ => s.ToString()
    };

    private static string AccelerationModeZh(AccelerationMode m) => m switch
    {
        AccelerationMode.Stable => "稳定",
        AccelerationMode.Balanced => "平衡",
        AccelerationMode.Performance => "性能",
        _ => "稳定"
    };

    private static string EngineErrorZh(EngineError e) => e switch
    {
        EngineError.None => "无",
        EngineError.TunAdminRequired => "需要管理员权限",
        EngineError.TunDownloadFailed => "Wintun下载失败",
        EngineError.TunHashMismatch => "Wintun校验失败",
        EngineError.TunDllCopyFailed => "Wintun DLL拷贝失败",
        EngineError.CoreStartFailed => "核心启动失败",
        EngineError.TunCreateFailed => "TUN创建失败",
        EngineError.TunCreateTimeout => "TUN创建超时",
        EngineError.HysteriaHandshakeFailed => "HY2握手失败",
        EngineError.NodeUnreachable => "节点不可达",
        EngineError.RouteRuleNotMatched => "未命中路由规则",
        EngineError.ConfigWriteFailed => "配置写入失败",
        EngineError.CoreBinaryMissing => "核心文件缺失",
        EngineError.CoreCrashed => "核心崩溃",
        EngineError.NodeProbeFailed => "节点探测失败",
        _ => "未知错误"
    };

    private void SetEngineState(EngineState state)
    {
        _engineState = state;
        _engineStatus.Text = "引擎状态：" + EngineStateZh(state);
        LogEngine($"[状态] {EngineStateZh(state)}");
    }

    private void FailEngine(EngineError code, string shortMessage, string? raw = null)
    {
        _engineState = EngineState.Failed;
        _lastEngineError = code;
        _engineStatus.Text = "引擎状态：失败";
        LogEx("ERROR", "engine", $"[错误:{EngineErrorZh(code)}] {shortMessage}", _recentEngineLogs);
        if (!string.IsNullOrWhiteSpace(raw))
            LogEx("ERROR", "engine", "[RAW] " + raw.Replace("\r", " ").Replace("\n", " | "), _recentEngineLogs);
    }

    public MainForm()
    {
        Text = "Delta " + Version;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Width = 1320;
        Height = 780;
        StartPosition = FormStartPosition.CenterScreen;

        var menu = new MenuStrip();

        var fileMenu = new ToolStripMenuItem("文件");
        var miImportNodes = new ToolStripMenuItem("导入节点");
        var miExportNodes = new ToolStripMenuItem("导出节点");
        var miExit = new ToolStripMenuItem("退出");
        miImportNodes.Click += (_, _) => ImportNodes();
        miExportNodes.Click += (_, _) => ExportNodes();
        miExit.Click += (_, _) => Close();
        fileMenu.DropDownItems.AddRange(new ToolStripItem[] { miImportNodes, miExportNodes, new ToolStripSeparator(), miExit });

        var engineMenu = new ToolStripMenuItem("引擎");
        var miStart = new ToolStripMenuItem("开始加速");
        var miStop = new ToolStripMenuItem("停止");
        var miRestart = new ToolStripMenuItem("重启核心");
        miStart.Click += async (_, _) => await ApplyTakeoverAsync();
        miStop.Click += (_, _) => StopEngine();
        miRestart.Click += async (_, _) => await RestartEngineAsync();
        engineMenu.DropDownItems.AddRange(new ToolStripItem[] { miStart, miStop, miRestart });

        var toolsMenu = new ToolStripMenuItem("工具");
        var miNetRepair = new ToolStripMenuItem("网络修复");
        var miFlushDns = new ToolStripMenuItem("刷新DNS");
        var miResetAdapter = new ToolStripMenuItem("重置适配器");
        var miTestNode = new ToolStripMenuItem("测试节点");
        miNetRepair.Click += async (_, _) => await RepairNetworkAsync();
        miFlushDns.Click += async (_, _) => await RunHiddenAsync("ipconfig", "/flushdns");
        miResetAdapter.Click += async (_, _) => await HardCleanupBeforeStartAsync();
        miTestNode.Click += async (_, _) => await TestCurrentNodeAsync();
        toolsMenu.DropDownItems.AddRange(new ToolStripItem[] { miNetRepair, miFlushDns, miResetAdapter, miTestNode });

        var viewMenu = new ToolStripMenuItem("视图");
        var miToggleVerbose = new ToolStripMenuItem("切换详细日志") { CheckOnClick = true, Checked = _verboseLogs };
        var miOpenLog = new ToolStripMenuItem("打开日志窗口");
        miToggleVerbose.CheckedChanged += (_, _) => _verboseLogs = miToggleVerbose.Checked;
        miOpenLog.Click += (_, _) => MessageBox.Show(_log.TextLength == 0 ? "暂无日志" : _log.Text, "日志窗口", MessageBoxButtons.OK, MessageBoxIcon.Information);
        viewMenu.DropDownItems.AddRange(new ToolStripItem[] { miToggleVerbose, miOpenLog });

        var helpMenu = new ToolStripMenuItem("帮助");
        var miAbout = new ToolStripMenuItem("关于");
        var miCheckUpdate = new ToolStripMenuItem("检查更新");
        miAbout.Click += (_, _) => MessageBox.Show($"Delta {Version}\n游戏加速器", "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
        miCheckUpdate.Click += async (_, _) => await CheckUpdateAsync(true);
        helpMenu.DropDownItems.AddRange(new ToolStripItem[] { miAbout, miCheckUpdate });

        menu.Items.AddRange(new ToolStripItem[] { fileMenu, engineMenu, toolsMenu, viewMenu, helpMenu });
        MainMenuStrip = menu;

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
        var btnNodeUpsert = new Button { Text = "保存节点", AutoSize = true };
        var btnNodeRemove = new Button { Text = "删除节点", AutoSize = true };
        var btnNodeTest = new Button { Text = "测试节点", AutoSize = true };
        var btnNodeImport = new Button { Text = "导入节点", AutoSize = true };
        var btnNodeExport = new Button { Text = "导出节点", AutoSize = true };
        var btnStability = new Button { Text = "稳定性测试(5轮)", AutoSize = true };
        var btnExportLogs = new Button { Text = "导出日志", AutoSize = true };
        var chkVerbose = new CheckBox { Text = "详细日志", AutoSize = true };

        btnRefresh.Click += (_, _) => RefreshProcesses();
        btnApply.Click += async (_, _) => await ApplyTakeoverAsync();
        btnVerify.Click += async (_, _) => await VerifyTakeoverAsync();
        btnRollback.Click += (_, _) => Rollback();
        btnNodeUpsert.Click += (_, _) => UpsertCurrentNode();
        btnNodeRemove.Click += (_, _) => RemoveCurrentNode();
        btnNodeTest.Click += async (_, _) => await TestCurrentNodeAsync();
        btnNodeImport.Click += (_, _) => ImportNodes();
        btnNodeExport.Click += (_, _) => ExportNodes();
        btnStability.Click += async (_, _) => await RunStabilityCyclesAsync(5);
        btnExportLogs.Click += (_, _) => ExportLogs();
        _nodeCombo.SelectedIndexChanged += (_, _) => ApplySelectedNodeToInputs();
        _profileCombo.SelectedIndexChanged += (_, _) => ApplySelectedProfileTemplate();
        _btnCheckUpdate.Click += async (_, _) => await CheckUpdateAsync(true);
        chkVerbose.CheckedChanged += (_, _) => _verboseLogs = chkVerbose.Checked;
        _btnUpdate.Click += async (_, _) => await UpdateToLatestAsync();

        top.Controls.Add(new Label { Text = "节点", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        top.Controls.Add(_nodeCombo);
        top.Controls.Add(new Label { Text = "游戏", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        top.Controls.Add(_processCombo);
        top.Controls.Add(new Label { Text = "模式", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        top.Controls.Add(_profileCombo);
        top.Controls.Add(btnApply);
        top.Controls.Add(btnRollback);

        var cfgPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 156,
            AutoSize = false,
            WrapContents = true,
            Padding = new Padding(8, 4, 8, 4)
        };

        var btnEngineStop = new Button { Text = "停止接管引擎", AutoSize = true };
        var btnEngineRestart = new Button { Text = "重启接管引擎", AutoSize = true };
        var btnNetRepair = new Button { Text = "一键网络修复", AutoSize = true };
        var btnPickGame = new Button { Text = "选择游戏EXE", AutoSize = true };
        var btnPickLauncher = new Button { Text = "选择启动器EXE", AutoSize = true };
        var chkTunMode = new CheckBox { Text = "TUN模式(需虚拟网卡)", AutoSize = true, Checked = true, Visible = false };
        var chkFullTunnelValidation = new CheckBox { Text = "全隧道验证模式", AutoSize = true, Checked = false, Visible = false };
        var chkAdvanced = new CheckBox { Text = "高级模式", AutoSize = true, Checked = false };

        btnEngineStop.Click += (_, _) => StopEngine();
        btnEngineRestart.Click += async (_, _) => await RestartEngineAsync();
        btnNetRepair.Click += async (_, _) => await RepairNetworkAsync();
        btnPickGame.Click += (_, _) => PickExeInto(_gameProcessPaths);
        btnPickLauncher.Click += (_, _) => PickExeInto(_launcherProcessPaths);
        chkTunMode.CheckedChanged += (_, _) => _useTunMode = chkTunMode.Checked;
        chkFullTunnelValidation.CheckedChanged += (_, _) => _fullTunnelValidationMode = chkFullTunnelValidation.Checked;
        chkAdvanced.CheckedChanged += (_, _) =>
        {
            _advancedMode = chkAdvanced.Checked;
            _hy2Token.Visible = _advancedMode;
            _hy2Sni.Visible = _advancedMode;
            _hy2ObfsType.Visible = _advancedMode;
            _hy2ObfsPassword.Visible = _advancedMode;
            chkTunMode.Visible = _advancedMode;
            chkFullTunnelValidation.Visible = _advancedMode;
        };

        cfgPanel.Controls.Add(new Label { Text = "HY2 IP", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        cfgPanel.Controls.Add(_hy2Ip);
        cfgPanel.Controls.Add(new Label { Text = "端口", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        cfgPanel.Controls.Add(_hy2Port);
        cfgPanel.Controls.Add(new Label { Text = "令牌", AutoSize = true, Padding = new Padding(0, 8, 0, 0), Visible = false });
        cfgPanel.Controls.Add(_hy2Token);
        cfgPanel.Controls.Add(new Label { Text = "SNI", AutoSize = true, Padding = new Padding(0, 8, 0, 0), Visible = false });
        cfgPanel.Controls.Add(_hy2Sni);
        cfgPanel.Controls.Add(new Label { Text = "混淆", AutoSize = true, Padding = new Padding(0, 8, 0, 0), Visible = false });
        cfgPanel.Controls.Add(_hy2ObfsType);
        cfgPanel.Controls.Add(_hy2ObfsPassword);
        cfgPanel.Controls.Add(new Label { Text = "游戏路径", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        cfgPanel.Controls.Add(_gameProcessPaths);
        cfgPanel.Controls.Add(btnPickGame);
        cfgPanel.Controls.Add(new Label { Text = "启动器路径", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        cfgPanel.Controls.Add(_launcherProcessPaths);
        cfgPanel.Controls.Add(btnPickLauncher);
        cfgPanel.Controls.Add(chkAdvanced);
        cfgPanel.Controls.Add(chkTunMode);
        cfgPanel.Controls.Add(chkFullTunnelValidation);
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
        statusPanel.Controls.Add(_quickSummary);
        statusPanel.Controls.Add(new Label { Text = "   " });
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
        verifyPanel.Controls.Add(_coreVersionsStatus);
        verifyPanel.Controls.Add(new Label { Text = "   " });
        verifyPanel.Controls.Add(_routeStatus);
        verifyPanel.Controls.Add(new Label { Text = "   " });
        verifyPanel.Controls.Add(_nodeHealthStatus);
        verifyPanel.Controls.Add(new Label { Text = "   " });
        verifyPanel.Controls.Add(_diagStatus);
        verifyPanel.Controls.Add(new Label { Text = "   " });
        verifyPanel.Controls.Add(_ipDirect);
        verifyPanel.Controls.Add(new Label { Text = "   " });
        verifyPanel.Controls.Add(_ipProxy);

        var leftPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(8)
        };
        leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 48));

        var nodePanel = new GroupBox { Text = "节点面板", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var nodeLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        nodeLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        nodeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        nodeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var nodeBtnBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var btnNodeAdd2 = new Button { Text = "新增节点", AutoSize = true };
        var btnNodeEdit2 = new Button { Text = "编辑节点", AutoSize = true };
        var btnNodeDel2 = new Button { Text = "删除节点", AutoSize = true };
        btnNodeAdd2.Click += (_, _) => UpsertCurrentNode();
        btnNodeEdit2.Click += (_, _) => UpsertCurrentNode();
        btnNodeDel2.Click += (_, _) => RemoveCurrentNode();
        nodeBtnBar.Controls.Add(btnNodeAdd2);
        nodeBtnBar.Controls.Add(btnNodeEdit2);
        nodeBtnBar.Controls.Add(btnNodeDel2);
        var nodeStat = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        nodeStat.Controls.Add(new Label { Text = "Ping：见日志" });
        nodeStat.Controls.Add(new Label { Text = "   状态：在线/离线见节点健康" });
        nodeStat.Controls.Add(new Label { Text = "   质量：快/普通/差" });
        nodeLayout.Controls.Add(_nodeList, 0, 0);
        nodeLayout.Controls.Add(nodeBtnBar, 0, 1);
        nodeLayout.Controls.Add(nodeStat, 0, 2);
        nodePanel.Controls.Add(nodeLayout);

        var gamePanel = new GroupBox { Text = "游戏面板", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var gameLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        gameLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        gameLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var gameBtnBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var btnGameAdd2 = new Button { Text = "新增游戏", AutoSize = true };
        var btnGameEdit2 = new Button { Text = "编辑游戏", AutoSize = true };
        var btnGameDel2 = new Button { Text = "删除游戏", AutoSize = true };
        btnGameAdd2.Click += (_, _) => PickExeInto(_gameProcessPaths);
        btnGameEdit2.Click += (_, _) => PickExeInto(_gameProcessPaths);
        btnGameDel2.Click += (_, _) => { _gameProcessPaths.Text = ""; RefreshGameUi(); SaveSettings(); };
        gameBtnBar.Controls.Add(btnGameAdd2);
        gameBtnBar.Controls.Add(btnGameEdit2);
        gameBtnBar.Controls.Add(btnGameDel2);
        gameLayout.Controls.Add(_gameList, 0, 0);
        gameLayout.Controls.Add(gameBtnBar, 0, 1);
        gamePanel.Controls.Add(gameLayout);

        leftPanel.Controls.Add(nodePanel, 0, 0);
        leftPanel.Controls.Add(gamePanel, 0, 1);

        var rightPanel = new GroupBox { Text = "状态面板", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var rightFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        rightFlow.Controls.Add(_cardEngine);
        rightFlow.Controls.Add(_cardNode);
        rightFlow.Controls.Add(_cardMode);
        rightFlow.Controls.Add(_cardTun);
        rightFlow.Controls.Add(_cardMatched);
        rightFlow.Controls.Add(_cardRoute);
        rightPanel.Controls.Add(rightFlow);

        var centerSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 620 };
        centerSplit.Panel1.Controls.Add(leftPanel);
        centerSplit.Panel2.Controls.Add(rightPanel);

        var tabEngine = new TabPage("引擎日志");
        var tabCore = new TabPage("核心日志");
        var tabNet = new TabPage("网络日志");
        tabEngine.Controls.Add(_logEngineView);
        tabCore.Controls.Add(_logCoreView);
        tabNet.Controls.Add(_logNetView);
        _logsTabs.TabPages.Clear();
        _logsTabs.TabPages.Add(tabEngine);
        _logsTabs.TabPages.Add(tabCore);
        _logsTabs.TabPages.Add(tabNet);

        _logLevelFilter.Items.Clear();
        _logLevelFilter.Items.AddRange(new object[] { "全部", "信息", "警告", "错误" });
        _logLevelFilter.SelectedIndex = 0;
        _logLevelFilter.SelectedIndexChanged += (_, _) => RefreshLogViews();
        _btnCopyLog.Click += (_, _) =>
        {
            try
            {
                var t = _logsTabs.SelectedTab?.Text ?? "引擎日志";
                var txt = t.Contains("核心") ? _logCoreView.Text : t.Contains("网络") ? _logNetView.Text : _logEngineView.Text;
                if (!string.IsNullOrWhiteSpace(txt)) Clipboard.SetText(txt);
                Log("已复制当前日志");
            }
            catch (Exception ex)
            {
                MessageBox.Show("复制日志失败: " + ex.Message, "日志", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        var logToolBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, AutoSize = false, WrapContents = false, Padding = new Padding(6, 4, 6, 2) };
        logToolBar.Controls.Add(new Label { Text = "级别过滤", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        logToolBar.Controls.Add(_logLevelFilter);
        logToolBar.Controls.Add(_btnCopyLog);

        var bottomPanel = new Panel { Dock = DockStyle.Fill };
        bottomPanel.Controls.Add(_logsTabs);
        bottomPanel.Controls.Add(logToolBar);
        bottomPanel.Controls.Add(_log);
        _log.Visible = false;

        var verticalMain = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 430 };
        verticalMain.Panel1.Controls.Add(centerSplit);
        verticalMain.Panel2.Controls.Add(bottomPanel);

        Controls.Add(verticalMain);
        Controls.Add(verifyPanel);
        Controls.Add(statusPanel);
        Controls.Add(cfgPanel);
        Controls.Add(top);
        Controls.Add(menu);

        FormClosing += (_, _) =>
        {
            SaveSettings();
            StopEngine();
            CleanupStaleTemp();
        };

        Load += (_, _) =>
        {
            var settings = LoadSettings();
            _hy2Ip.Text = string.IsNullOrWhiteSpace(settings.Hy2Server) ? "178.22.26.114" : settings.Hy2Server;
            _hy2Port.Text = settings.Hy2Port <= 0 ? "8443" : settings.Hy2Port.ToString();
            _hy2Token.Text = settings.Hy2Token ?? "";
            _hy2Sni.Text = string.IsNullOrWhiteSpace(settings.Hy2Sni) ? "www.bing.com" : settings.Hy2Sni!;
            _hy2ObfsType.Text = settings.Hy2ObfsType ?? "";
            _hy2ObfsPassword.Text = settings.Hy2ObfsPassword ?? "";
            _gameProcessPaths.Text = settings.GameProcessPaths ?? "";
            _launcherProcessPaths.Text = settings.LauncherProcessPaths ?? "";
            _nodes.Clear();
            if (settings.Nodes != null) _nodes.AddRange(settings.Nodes.Where(n => !string.IsNullOrWhiteSpace(n.DisplayName) && !string.IsNullOrWhiteSpace(n.Server)));
            SeedDefaultNodeIfNeeded();
            RefreshNodeUi();
            RefreshGameUi();
            _profileCombo.Items.Clear();
            _profileCombo.Items.AddRange(new object[] { "稳定模式", "平衡模式", "性能模式" });
            _profileCombo.SelectedIndex = 0;

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
            UpdateDiagnostics();
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
        Log($"当前节点: {_nodeCombo.Text}");

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
                FailEngine(EngineError.ConfigWriteFailed, "请填写 HY2 IP 和 Token");
                return false;
            }
            if (!int.TryParse(portTxt, out var port) || port <= 0 || port > 65535)
            {
                FailEngine(EngineError.ConfigWriteFailed, "HY2 端口无效");
                return false;
            }
            if (string.IsNullOrWhiteSpace(processName))
            {
                FailEngine(EngineError.ConfigWriteFailed, "未指定目标进程");
                return false;
            }

            SaveSettings();

            if (_useTunMode)
            {
                SetEngineState(EngineState.CheckingAdmin);
                if (!IsAdmin())
                {
                    FailEngine(EngineError.TunAdminRequired, "需要管理员权限");
                    return false;
                }

                SetEngineState(EngineState.PreparingWintun);
                var okTun = await EnsureTunReadyAsync();
                if (!okTun)
                {
                    _status.Text = "状态：接管失败（TUN未就绪）";
                    return false;
                }

                await HardCleanupBeforeStartAsync();
            }

            SetEngineState(EngineState.PreparingCore);
            var exePath = await EnsureSingBoxAsync();
            _currentTunInterface = BuildTunInterfaceName();
            _currentTunCidr = BuildTunCidr();
            SetEngineState(EngineState.WritingConfig);
            var cfgPath = await WriteSingBoxConfigAsync(processName, ip, port, token, _currentTunInterface, _currentTunCidr, _useTunMode);
            _lastConfigPath = cfgPath;

            StopEngine();

            SetEngineState(EngineState.StartingCore);
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
                if (!string.IsNullOrWhiteSpace(e.Data))
                    BeginInvoke(() => LogCore($"[RUN#{runId}] {e.Data}"));
            };
            _engineProc.ErrorDataReceived += (_, e) =>
            {
                if (runId != _engineRunId) return;
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    var t = e.Data;
                    BeginInvoke(() => LogCore($"[RUN#{runId}] ERR {t}"));
                }
            };
            _engineProc.Exited += (_, _) => BeginInvoke(async () =>
            {
                if (runId != _engineRunId) return;
                var wasRunning = _engineState == EngineState.Running;
                if (wasRunning && !_manualStopping)
                {
                    _lastEngineError = EngineError.CoreCrashed;
                    SetEngineState(EngineState.Failed);
                }
                else
                {
                    SetEngineState(wasRunning ? EngineState.Idle : EngineState.Failed);
                }
                Log($"[RUN#{runId}] 接管引擎已退出");
                if (!_manualStopping && wasRunning && _autoRestartCount < 2 && !string.IsNullOrWhiteSpace(_activeProcess))
                {
                    _autoRestartCount++;
                    Log($"检测到引擎异常退出，执行受控重启 #{_autoRestartCount}");
                    await Task.Delay(600);
                    _ = await StartEngineAsync(_activeProcess);
                }
                UpdateDiagnostics();
            });

            if (!_engineProc.Start())
            {
                FailEngine(EngineError.CoreStartFailed, "接管引擎启动失败");
                TryRollbackPreviousConfig();
                return false;
            }
            _engineProc.BeginOutputReadLine();
            _engineProc.BeginErrorReadLine();

            SetEngineState(EngineState.WaitingTunReady);
            var ready = await WaitForTunReadyAsync(_engineProc, TimeSpan.FromMilliseconds(TemplateTunReadyTimeoutMs()));
            if (!ready)
            {
                try { _engineProc.Kill(true); } catch { }
                TryRollbackPreviousConfig();
                return false;
            }

            var hs = await WaitForHy2HandshakeAsync(ip, port, TemplateHandshakeTimeoutMs());
            if (!hs)
            {
                FailEngine(EngineError.HysteriaHandshakeFailed, $"未检测到到 {ip}:{port} 的握手连接");
                try { _engineProc.Kill(true); } catch { }
                TryRollbackPreviousConfig();
                return false;
            }

            SetEngineState(EngineState.Running);
            _autoRestartCount = 0;
            Log($"[RUN#{runId}] 接管引擎已启动");
            UpdateDiagnostics();
            return true;
        }
        catch (Exception ex)
        {
            var code = ex is FileNotFoundException ? EngineError.CoreBinaryMissing : EngineError.Unknown;
            FailEngine(code, "接管引擎异常", ex.ToString());
            TryRollbackPreviousConfig();
            return false;
        }
    }


    private sealed class CmdResult
    {
        public int ExitCode { get; set; }
        public string StdOut { get; set; } = "";
        public string StdErr { get; set; } = "";
    }


    private async Task<bool> WaitForTunReadyAsync(Process process, TimeSpan timeout)
    {
        if (!_useTunMode) return true;

        var startAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startAt < timeout)
        {
            if (process == null || process.HasExited)
            {
                FailEngine(EngineError.CoreStartFailed, "sing-box 在 TUN 就绪前退出", GetRecentCoreLogsText(30));
                return false;
            }

            var recent = GetRecentCoreLogs(60);
            var tunOkLog = recent.Any(x => x.Contains("tun", StringComparison.OrdinalIgnoreCase) &&
                                           (x.Contains("created", StringComparison.OrdinalIgnoreCase) ||
                                            x.Contains("started", StringComparison.OrdinalIgnoreCase) ||
                                            x.Contains("inbound", StringComparison.OrdinalIgnoreCase) ||
                                            x.Contains("interface", StringComparison.OrdinalIgnoreCase)));

            if (tunOkLog)
            {
                var up = NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                    n.Name.Equals(_currentTunInterface, StringComparison.OrdinalIgnoreCase) &&
                    n.OperationalStatus == OperationalStatus.Up);
                if (up || recent.Any(x => x.Contains(_currentTunInterface, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            var tunErr = recent.Where(x => x.Contains("tun", StringComparison.OrdinalIgnoreCase) &&
                                           (x.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                            x.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                                            x.Contains("cannot", StringComparison.OrdinalIgnoreCase)))
                               .TakeLast(20)
                               .ToArray();
            if (tunErr.Length > 0)
            {
                FailEngine(EngineError.TunCreateFailed, "TUN 创建失败", string.Join("\\n", tunErr));
                return false;
            }

            await Task.Delay(200);
        }

        FailEngine(EngineError.TunCreateTimeout, "等待 TUN 就绪超时", GetRecentCoreLogsText(30));
        return false;
    }


    private async Task<bool> EnsureTunReadyAsync()
    {
        try
        {
            _lastTunPrepError = EngineError.None;
            SetEngineState(EngineState.PreparingWintun);

            if (!IsAdmin())
            {
                Log("[TunAdminRequired] 当前非管理员权限，阻断接管");
                FailEngine(EngineError.TunAdminRequired, "需要管理员权限");
                MessageBox.Show("需要管理员权限才能启用 TUN。\n请右键以管理员身份运行 Delta。", "Delta TUN 前置检查", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            SetEngineState(EngineState.PreparingWintun);
            var ok = await EnsureWintunDllReadyAsync();
            if (!ok)
            {
                Log("[TunDllPrepareFailed] wintun.dll 准备失败");
                if (_lastTunPrepError == EngineError.None) _lastTunPrepError = EngineError.TunDownloadFailed;
                FailEngine(_lastTunPrepError, "wintun.dll 准备失败");
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
            if (File.Exists(targetDll)) return true;

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
                    _lastTunPrepError = EngineError.TunDownloadFailed;
                    Log("[TunDownloadFailed] 下载 Wintun 失败: " + ex.Message);
                    return false;
                }
            }

            if (!VerifySha256(cachedZip, WintunZipSha256))
            {
                _lastTunPrepError = EngineError.TunHashMismatch;
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
                _lastTunPrepError = EngineError.TunDllCopyFailed;
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
            _lastTunPrepError = EngineError.TunDllCopyFailed;
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

    private void TryRollbackPreviousConfig()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_lastConfigPath) || string.IsNullOrWhiteSpace(_lastConfigBackupPath)) return;
            if (!File.Exists(_lastConfigBackupPath)) return;
            File.Copy(_lastConfigBackupPath, _lastConfigPath, true);
            Log($"已回滚上次配置: {_lastConfigBackupPath} -> {_lastConfigPath}");
        }
        catch (Exception ex)
        {
            Log("回滚配置失败: " + ex.Message);
        }
    }

    private void StopEngine()
    {
        SetEngineState(EngineState.Stopping);
        _manualStopping = true;
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
            _manualStopping = false;
            UpdateDiagnostics();
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
                                _routeStatus.Text = $"路由观测：MatchedProcess=YES | Mode={( _fullTunnelValidationMode ? "Hy2(全局)" : "Hy2(命中规则)")} | Node={_nodeCombo.Text} | Cfg={_lastConfigPath ?? "-"}";
                            }
                            else
                            {
                                total++;
                                _lastEngineError = EngineError.RouteRuleNotMatched;
                                _routeStatus.Text = $"路由观测：MatchedProcess=NO | Mode=Direct(回退) | Node={_nodeCombo.Text} | Cfg={_lastConfigPath ?? "-"}";
                                checks.Add("❌ 目标进程暂无活跃外网连接，疑似未命中路由规则");
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
                    UpdateDiagnostics();
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

    private async Task<bool> WaitForHy2HandshakeAsync(string serverIp, int serverPort, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var rows = GetNetstatRows();
                var ok = _engineProc != null && !_engineProc.HasExited && rows.Any(r =>
                    r.Pid == _engineProc.Id &&
                    r.RemoteAddress == serverIp &&
                    r.RemotePort == serverPort &&
                    ((r.Protocol == "TCP" && r.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) || r.Protocol == "UDP"));
                if (ok) return true;
            }
            catch { }
            await Task.Delay(250);
        }
        return false;
    }

    private void SeedDefaultNodeIfNeeded()
    {
        if (_nodes.Count > 0) return;
        _nodes.Add(new Hy2NodeProfile
        {
            DisplayName = "DEOPT-Default",
            Server = _hy2Ip.Text.Trim(),
            Port = int.TryParse(_hy2Port.Text.Trim(), out var p) ? p : 8443,
            Password = _hy2Token.Text.Trim(),
            Sni = string.IsNullOrWhiteSpace(_hy2Sni.Text) ? "www.bing.com" : _hy2Sni.Text.Trim(),
            ObfsType = _hy2ObfsType.Text.Trim(),
            ObfsPassword = _hy2ObfsPassword.Text.Trim()
        });
    }

    private void RefreshGameUi()
    {
        _gameList.Items.Clear();
        var gps = (_gameProcessPaths.Text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var g in gps)
            _gameList.Items.Add(Path.GetFileName(g));
    }

    private void RefreshNodeUi()
    {
        var selected = _nodeCombo.SelectedItem?.ToString();
        _nodeCombo.Items.Clear();
        _nodeList.Items.Clear();
        foreach (var n in _nodes)
        {
            _nodeCombo.Items.Add(n.DisplayName);
            _nodeList.Items.Add(n.DisplayName);
        }
        if (_nodeCombo.Items.Count > 0)
        {
            var idx = 0;
            if (!string.IsNullOrWhiteSpace(selected))
            {
                var found = _nodes.FindIndex(x => x.DisplayName.Equals(selected, StringComparison.OrdinalIgnoreCase));
                if (found >= 0) idx = found;
            }
            _nodeCombo.SelectedIndex = idx;
        }
    }

    private void ApplySelectedNodeToInputs()
    {
        if (_nodeCombo.SelectedIndex < 0 || _nodeCombo.SelectedIndex >= _nodes.Count) return;
        var n = _nodes[_nodeCombo.SelectedIndex];
        _hy2Ip.Text = n.Server;
        _hy2Port.Text = n.Port.ToString();
        _hy2Token.Text = n.Password;
        _hy2Sni.Text = string.IsNullOrWhiteSpace(n.Sni) ? "www.bing.com" : n.Sni;
        _hy2ObfsType.Text = n.ObfsType ?? "";
        _hy2ObfsPassword.Text = n.ObfsPassword ?? "";
        UpdateDiagnostics();
    }

    private void UpsertCurrentNode()
    {
        var name = (_nodeCombo.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("请先填写节点显示名（可直接在节点下拉框输入）。", "节点管理", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var port = int.TryParse((_hy2Port.Text ?? "8443").Trim(), out var p) ? p : 8443;
        var node = new Hy2NodeProfile
        {
            DisplayName = name,
            Server = (_hy2Ip.Text ?? "").Trim(),
            Port = port,
            Password = (_hy2Token.Text ?? "").Trim(),
            Sni = (_hy2Sni.Text ?? "").Trim(),
            ObfsType = (_hy2ObfsType.Text ?? "").Trim(),
            ObfsPassword = (_hy2ObfsPassword.Text ?? "").Trim()
        };

        var idx = _nodes.FindIndex(x => x.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) _nodes[idx] = node;
        else _nodes.Add(node);

        RefreshNodeUi();
        _nodeCombo.SelectedItem = name;
        SaveSettings();
        Log($"节点已保存: {name} ({node.Server}:{node.Port})");
        UpdateDiagnostics();
    }

    private void RemoveCurrentNode()
    {
        if (_nodeCombo.SelectedIndex < 0 || _nodeCombo.SelectedIndex >= _nodes.Count) return;
        var name = _nodes[_nodeCombo.SelectedIndex].DisplayName;
        _nodes.RemoveAt(_nodeCombo.SelectedIndex);
        RefreshNodeUi();
        SaveSettings();
        Log($"节点已删除: {name}");
        UpdateDiagnostics();
    }

    private void ImportNodes()
    {
        using var ofd = new OpenFileDialog { Filter = "Delta Nodes|*.json" };
        if (ofd.ShowDialog() != DialogResult.OK) return;
        try
        {
            var json = File.ReadAllText(ofd.FileName);
            var list = JsonSerializer.Deserialize<List<Hy2NodeProfile>>(json) ?? new List<Hy2NodeProfile>();
            foreach (var n in list)
            {
                if (string.IsNullOrWhiteSpace(n.DisplayName) || string.IsNullOrWhiteSpace(n.Server)) continue;
                var idx = _nodes.FindIndex(x => x.DisplayName.Equals(n.DisplayName, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) _nodes[idx] = n;
                else _nodes.Add(n);
            }
            RefreshNodeUi();
            SaveSettings();
            Log($"节点导入完成: {list.Count} 条");
            UpdateDiagnostics();
        }
        catch (Exception ex)
        {
            MessageBox.Show("节点导入失败: " + ex.Message, "节点管理", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportNodes()
    {
        using var sfd = new SaveFileDialog { Filter = "Delta Nodes|*.json", FileName = "delta-nodes.json" };
        if (sfd.ShowDialog() != DialogResult.OK) return;
        try
        {
            var json = JsonSerializer.Serialize(_nodes, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(sfd.FileName, json);
            Log($"节点导出完成: {sfd.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show("节点导出失败: " + ex.Message, "节点管理", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ApplySelectedProfileTemplate()
    {
        var tpl = _profileCombo.SelectedItem?.ToString() ?? "稳定模式";
        _hy2Sni.Text = string.IsNullOrWhiteSpace(_hy2Sni.Text) ? "www.bing.com" : _hy2Sni.Text;

        switch (tpl)
        {
            case "稳定模式":
                _accelerationMode = AccelerationMode.Stable;
                break;
            case "平衡模式":
                _accelerationMode = AccelerationMode.Balanced;
                break;
            case "性能模式":
                _accelerationMode = AccelerationMode.Performance;
                break;
            default:
                _accelerationMode = AccelerationMode.Stable;
                break;
        }

        Log($"已应用模板: {tpl} (mode={_accelerationMode})");
        UpdateDiagnostics();
    }

    private bool TemplateTunSniff()
    {
        return _accelerationMode != AccelerationMode.Stable;
    }

    private bool TemplateTunStrictRoute()
    {
        return _accelerationMode != AccelerationMode.Performance;
    }

    private int TemplateTunReadyTimeoutMs()
    {
        return _accelerationMode switch
        {
            AccelerationMode.Stable => 10000,
            AccelerationMode.Balanced => 8000,
            AccelerationMode.Performance => 6000,
            _ => 8000
        };
    }

    private int TemplateHandshakeTimeoutMs()
    {
        return _accelerationMode switch
        {
            AccelerationMode.Stable => 6000,
            AccelerationMode.Balanced => 4000,
            AccelerationMode.Performance => 3000,
            _ => 4000
        };
    }

    private async Task RunStabilityCyclesAsync(int rounds)
    {
        var proc = _processCombo.SelectedItem?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(proc))
        {
            MessageBox.Show("请先选择目标游戏进程。", "稳定性测试", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Log($"开始稳定性测试：{rounds} 轮（进程={proc}，节点={_nodeCombo.Text}）");
        var ok = 0;
        var fail = 0;

        for (var i = 1; i <= rounds; i++)
        {
            try
            {
                Log($"[STABILITY] Round {i}/{rounds} start");
                var started = await StartEngineAsync(proc);
                if (!started)
                {
                    fail++;
                    Log($"[STABILITY] Round {i} 启动失败");
                    TryRollbackPreviousConfig();
                    await Task.Delay(300);
                    continue;
                }

                await Task.Delay(500);
                await VerifyTakeoverAsync();
                StopEngine();
                ok++;
                Log($"[STABILITY] Round {i} 通过");
                await Task.Delay(250);
            }
            catch (Exception ex)
            {
                fail++;
                Log($"[STABILITY] Round {i} 异常: " + ex.Message);
                StopEngine();
                await Task.Delay(250);
            }
        }

        Log($"稳定性测试完成：OK={ok}, FAIL={fail}");
        _diagStatus.Text = $"诊断: 稳定性测试 OK={ok} FAIL={fail}";
        UpdateDiagnostics();
    }

    private string ValidateClientServerConsistency(Hy2NodeProfile node)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(node.Server)) issues.Add("server为空");
        if (node.Port <= 0 || node.Port > 65535) issues.Add("port非法");
        if (string.IsNullOrWhiteSpace(node.Password)) issues.Add("auth/password为空");
        if (string.IsNullOrWhiteSpace(node.Sni)) issues.Add("TLS server_name(SNI)为空");
        if (!string.IsNullOrWhiteSpace(node.ObfsType) && string.IsNullOrWhiteSpace(node.ObfsPassword)) issues.Add("obfs已填type但password为空");
        if (string.IsNullOrWhiteSpace(node.ObfsType) && !string.IsNullOrWhiteSpace(node.ObfsPassword)) issues.Add("obfs已填password但type为空");

        if (issues.Count == 0) return "服务端参数一致性：OK";
        return "服务端参数一致性：FAIL - " + string.Join("; ", issues);
    }

    private async Task TestCurrentNodeAsync()
    {
        var node = new Hy2NodeProfile
        {
            DisplayName = _nodeCombo.Text,
            Server = (_hy2Ip.Text ?? "").Trim(),
            Port = int.TryParse((_hy2Port.Text ?? "8443").Trim(), out var p) ? p : 8443,
            Password = (_hy2Token.Text ?? "").Trim(),
            Sni = (_hy2Sni.Text ?? "").Trim(),
            ObfsType = (_hy2ObfsType.Text ?? "").Trim(),
            ObfsPassword = (_hy2ObfsPassword.Text ?? "").Trim()
        };

        var consistency = ValidateClientServerConsistency(node);
        var result = await TestNodeHealthAsync(node);
        _nodeHealthStatus.Text = $"节点健康：{result.Grade} ({result.Summary})";
        LogProbe($"节点测试[{node.DisplayName}] {result.Summary}");
        LogProbe(consistency);
        if (consistency.Contains("FAIL", StringComparison.OrdinalIgnoreCase) || result.Summary.Contains("握手失败", StringComparison.OrdinalIgnoreCase))
            _lastEngineError = EngineError.HysteriaHandshakeFailed;
        UpdateDiagnostics();
    }

    private async Task<NodeHealthResult> TestNodeHealthAsync(Hy2NodeProfile node)
    {
        var rtts = new List<long>();
        var failures = new List<string>();
        var handshakeOk = false;
        var udpOk = false;

        try
        {
            using var ping = new Ping();
            for (var i = 0; i < 4; i++)
            {
                var rep = await ping.SendPingAsync(node.Server, 1200);
                if (rep.Status == IPStatus.Success) rtts.Add(rep.RoundtripTime);
            }
        }
        catch (Exception ex)
        {
            failures.Add("RTT测试异常: " + ex.Message);
        }

        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(2200);
            await tcp.ConnectAsync(node.Server, node.Port, cts.Token);

            var tlsHost = string.IsNullOrWhiteSpace(node.Sni) ? node.Server : node.Sni!;
            SslPolicyErrors tlsErrors = SslPolicyErrors.None;
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, errors) =>
            {
                tlsErrors = errors;
                return true; // 这里只做诊断，不在测试阶段拦截
            });

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = tlsHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cts.Token);

            handshakeOk = tcp.Connected && ssl.IsAuthenticated;
            if (tlsErrors != SslPolicyErrors.None)
                failures.Add("TLS证书告警: " + tlsErrors);
        }
        catch (Exception ex)
        {
            failures.Add("握手失败(TCP/TLS): " + ex.Message);
            _lastEngineError = EngineError.NodeUnreachable;
        }

        try
        {
            using var udp = new UdpClient();
            udp.Client.SendTimeout = 1200;
            var data = new byte[] { 0x44, 0x45, 0x4C, 0x54, 0x41 };
            await udp.SendAsync(data, data.Length, node.Server, node.Port);
            udpOk = true;
        }
        catch (Exception ex)
        {
            failures.Add("UDP不可达: " + ex.Message);
            if (_lastEngineError == EngineError.None) _lastEngineError = EngineError.NodeProbeFailed;
        }

        var avg = rtts.Count == 0 ? -1 : (long)rtts.Average();
        long jitter = 0;
        if (rtts.Count >= 2)
        {
            var diffs = new List<long>();
            for (var i = 1; i < rtts.Count; i++) diffs.Add(Math.Abs(rtts[i] - rtts[i - 1]));
            jitter = (long)diffs.Average();
        }

        var available = handshakeOk && udpOk;
        var grade = !available ? "Unavailable" : avg <= 90 && jitter <= 20 ? "Fast" : avg <= 180 && jitter <= 50 ? "Normal" : "Poor";
        var summary = $"HS={(handshakeOk ? "OK" : "FAIL")}, UDP={(udpOk ? "OK" : "FAIL")}, RTT={(avg < 0 ? "N/A" : avg + "ms")}, Jitter={jitter}ms";
        if (failures.Count > 0) summary += "; " + string.Join(" | ", failures);

        return new NodeHealthResult
        {
            Available = available,
            Grade = grade,
            AvgRttMs = avg,
            JitterMs = jitter,
            Summary = summary
        };
    }

    private void UpdateDiagnostics()
    {
        var wintunPath = Path.Combine(GetDataDir(), "wintun.dll");
        var wintunState = File.Exists(wintunPath) ? "Ready" : "Missing";
        var nodeName = _nodeCombo.SelectedItem?.ToString() ?? (_nodeCombo.Text ?? "-");
        var games = (_gameProcessPaths.Text ?? "").Trim();
        var tail = GetLogTail(8).Replace("\r", " ").Replace("\n", " | ");

        _diagStatus.Text = $"诊断: 状态={EngineStateZh(_engineState)}/{EngineErrorZh(_lastEngineError)}, Wintun={wintunState}, SB={SingBoxVersion}, 节点={nodeName}, 模式={_accelerationMode}";
        var route = _fullTunnelValidationMode ? "Hy2" : "直连/命中走Hy2";
        var matched = _lastEngineError == EngineError.RouteRuleNotMatched ? "否" : "是/待验证";
        _quickSummary.Text = $"[{EngineStateZh(_engineState)}] | 节点:{nodeName} | 模式:{AccelerationModeZh(_accelerationMode)} | 游戏:{_activeProcess ?? "-"} | 路由:{route}";
        _cardEngine.Text = $"引擎状态：{EngineStateZh(_engineState)}";
        _cardNode.Text = $"当前节点：{nodeName}";
        _cardMode.Text = $"当前模式：{AccelerationModeZh(_accelerationMode)}";
        _cardTun.Text = $"TUN状态：{wintunState}";
        _cardMatched.Text = $"命中进程：{matched}";
        _cardRoute.Text = $"当前路由：{route}";

        Log($"[DIAG] Engine={_engineState}, Error={_lastEngineError}, Wintun={wintunState}, SB={SingBoxVersion}, Node={nodeName}, Games={games}, Cfg={_lastConfigPath ?? "-"}");
        Log($"[DIAG] LastLogs={tail}");
    }

    private string GetLogTail(int lines)
    {
        var arr = (_log.Text ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (arr.Length <= lines) return string.Join("\n", arr);
        return string.Join("\n", arr.Skip(arr.Length - lines));
    }


    private void CleanupStaleTemp()
    {
        try
        {
            var temp = Path.GetTempPath();
            foreach (var d in Directory.GetDirectories(temp, "DeltaWintun_*"))
            {
                try
                {
                    var di = new DirectoryInfo(d);
                    if (di.CreationTimeUtc < DateTime.UtcNow.AddHours(-3))
                        di.Delete(true);
                }
                catch { }
            }
        }
        catch { }
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

        CleanupStaleTemp();
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
        var sni = (_hy2Sni.Text ?? "www.bing.com").Trim();
        if (string.IsNullOrWhiteSpace(sni)) sni = "www.bing.com";
        var obfsType = (_hy2ObfsType.Text ?? "").Trim();
        var obfsPassword = (_hy2ObfsPassword.Text ?? "").Trim();

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
        var gamePathRegex = gameRulePaths.Select(x => "^" + Regex.Escape(x).Replace("\\\\", "[/\\\\]") + "$").ToArray();
        if (gameRulePaths.Length > 0)
        {
            rules.Add(new
            {
                process_path = gameRulePaths,
                action = "route",
                outbound = "hy2-out"
            });
            rules.Add(new
            {
                process_path_regex = gamePathRegex,
                action = "route",
                outbound = "hy2-out"
            });
        }

        // 启动器路径
        var launcherRulePaths = launcherPaths.Where(x => x.Contains('\\') || x.Contains('/')).ToArray();
        var launcherPathRegex = launcherRulePaths.Select(x => "^" + Regex.Escape(x).Replace("\\\\", "[/\\\\]") + "$").ToArray();
        if (launcherRulePaths.Length > 0)
        {
            rules.Add(new
            {
                process_path = launcherRulePaths,
                action = "route",
                outbound = "hy2-out"
            });
            rules.Add(new
            {
                process_path_regex = launcherPathRegex,
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

        var useValidationMode = _fullTunnelValidationMode;

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
                        strict_route = TemplateTunStrictRoute(),
                        stack = "system",
                        sniff = TemplateTunSniff()
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
                        tls = new { enabled = true, server_name = sni, insecure = true },
                        obfs = string.IsNullOrWhiteSpace(obfsType) ? null : new { type = obfsType, password = obfsPassword }
                    },
                    new { type = "direct", tag = "direct" }
                },
                route = new
                {
                    auto_detect_interface = true,
                    rules = useValidationMode ? Array.Empty<object>() : rules.ToArray(),
                    final = useValidationMode ? "hy2-out" : "direct"
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
                        tls = new { enabled = true, server_name = sni, insecure = true },
                        obfs = string.IsNullOrWhiteSpace(obfsType) ? null : new { type = obfsType, password = obfsPassword }
                    },
                    new { type = "direct", tag = "direct" }
                },
                route = new
                {
                    rules = useValidationMode ? Array.Empty<object>() : rules.ToArray(),
                    final = useValidationMode ? "hy2-out" : "direct"
                }
            };
        }

        if (File.Exists(cfgPath))
        {
            _lastConfigBackupPath = cfgPath + ".bak";
            File.Copy(cfgPath, _lastConfigBackupPath, true);
        }

        var text = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            await File.WriteAllTextAsync(cfgPath, text);
        }
        catch (Exception ex)
        {
            FailEngine(EngineError.ConfigWriteFailed, "配置写入失败", ex.ToString());
            throw;
        }

        _routeStatus.Text = useValidationMode ? "路由：全隧道验证模式(final=hy2-out)" : $"路由：{rules.Count}条规则，final=direct";
        var modeTxt = useValidationMode ? "Hy2(全局)" : "Direct(默认)+Hy2(命中规则)";
        Log($"已写入接管配置: {cfgPath}");
        Log($"规则(游戏路径): {string.Join(" | ", gameRulePaths)}");
        if (launcherRulePaths.Length > 0)
            Log($"规则(启动器路径): {string.Join(" | ", launcherRulePaths)}");
        Log($"规则(名称回退): {string.Join(",", nameFallback)} -> hy2-out");
        Log($"路由观测: MatchedProcess=未知, CurrentMode={modeTxt}, ActiveNode={_nodeCombo.Text}, Config={cfgPath}");
        if (useValidationMode)
            Log("验证模式：全流量走 hy2-out（仅排障使用）");
        else
            Log("回退保护：未匹配流量走 direct");
        Log($"当前路由模式: {(useValidationMode ? "FullTunnelValidation" : "PerProcess")} ");
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

            _latestSingBoxVersion = string.IsNullOrWhiteSpace(m.singBoxVersion) ? ParseVersionFromUrl(_latestSingBoxUrl, "sing-box") : m.singBoxVersion.Trim();
            _latestHy2Version = string.IsNullOrWhiteSpace(m.hy2Version) ? ParseVersionFromUrl(_latestHy2Url, "hy2-client") : m.hy2Version.Trim();

            var appNeed = IsNewerVersion(_latestVersion, Version);
            var sbNeed = VersionChanged(_latestSingBoxVersion, SingBoxVersion);
            var hyNeed = VersionChanged(_latestHy2Version, Hy2Version);

            _coreVersionsStatus.Text = $"核心：sing-box v{SingBoxVersion}→v{(_latestSingBoxVersion ?? "?")} | hy2 {Hy2Version}→{(_latestHy2Version ?? "?")}";

            if (appNeed || sbNeed || hyNeed)
            {
                var parts = new List<string>();
                if (appNeed) parts.Add($"程序 {_latestVersion}");
                if (sbNeed) parts.Add($"sing-box v{_latestSingBoxVersion}");
                if (hyNeed) parts.Add($"hy2 {_latestHy2Version}");
                _updateStatus.Text = "更新：发现 " + string.Join(" / ", parts);
                _btnUpdate.Text = "一键更新（程序+核心）";
                _btnUpdate.Enabled = true;
                Log("发现更新: " + _updateStatus.Text);
                if (manual)
                    MessageBox.Show(_updateStatus.Text, "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                _updateStatus.Text = "更新：已是最新";
                _btnUpdate.Text = "一键更新";
                _btnUpdate.Enabled = false;
                if (manual)
                    MessageBox.Show("当前已是最新版本（程序 + 核心）。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

            Log($"开始一键更新：目标 {newExeName} + 最新核心...");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

            var exeBytes = await http.GetByteArrayAsync(_latestExeUrl!);
            await File.WriteAllBytesAsync(targetExePath, exeBytes);

            var singUrl = string.IsNullOrWhiteSpace(_latestSingBoxUrl) ? DefaultSingBoxUrl : _latestSingBoxUrl!;
            var hy2Url = string.IsNullOrWhiteSpace(_latestHy2Url) ? DefaultHy2Url : _latestHy2Url!;

            Log("下载最新 sing-box.exe ...");
            var singBytes = await http.GetByteArrayAsync(singUrl);
            await File.WriteAllBytesAsync(Path.Combine(currentDir, "sing-box.exe"), singBytes);

            Log("下载最新 hy2-client.exe ...");
            var hy2Bytes = await http.GetByteArrayAsync(hy2Url);
            await File.WriteAllBytesAsync(Path.Combine(currentDir, "hy2-client.exe"), hy2Bytes);

            _coreVersionsStatus.Text = $"核心：sing-box v{_latestSingBoxVersion ?? "?"} | hy2 {_latestHy2Version ?? "?"}";

            Log($"更新完成：程序 {verText} + sing-box v{_latestSingBoxVersion ?? "?"} + hy2 {_latestHy2Version ?? "?"}");
            MessageBox.Show($"更新完成。\n程序版本：{verText}\nsing-box：v{_latestSingBoxVersion ?? "?"}\nhy2：{_latestHy2Version ?? "?"}\n请关闭当前程序后启动 {newExeName}。", "Delta 更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

    private static string? ParseVersionFromUrl(string? url, string prefix)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var file = Path.GetFileName(url);
        if (string.IsNullOrWhiteSpace(file)) return null;

        var m = Regex.Match(file, $@"{Regex.Escape(prefix)}-([0-9]+(?:\.[0-9]+)*)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(file, "([0-9]+(?:\\.[0-9]+){0,3})");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool VersionChanged(string? remote, string local)
    {
        if (string.IsNullOrWhiteSpace(remote)) return false;
        return !string.Equals(remote.Trim(), local.Trim(), StringComparison.OrdinalIgnoreCase);
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
                LauncherProcessPaths = (_launcherProcessPaths.Text ?? "").Trim(),
                Hy2Sni = (_hy2Sni.Text ?? "").Trim(),
                Hy2ObfsType = (_hy2ObfsType.Text ?? "").Trim(),
                Hy2ObfsPassword = (_hy2ObfsPassword.Text ?? "").Trim(),
                Nodes = _nodes
            };
            File.WriteAllText(p, JsonSerializer.Serialize(s));
        }
        catch { }
    }

    private void RefreshLogViews()
    {
        var level = _logLevelFilter.SelectedItem?.ToString() ?? "全部";
        _logEngineView.Text = string.Join("\r\n", FilterLogEntries(_recentEngineLogs, level).Select(x => x.ToString()));
        _logCoreView.Text = string.Join("\r\n", FilterLogEntries(_recentCoreLogs, level).Select(x => x.ToString()));
        _logNetView.Text = string.Join("\r\n", FilterLogEntries(_recentProbeLogs, level).Select(x => x.ToString()));
    }

    private IEnumerable<LogEntry> FilterLogEntries(IEnumerable<LogEntry> src, string zhLevel)
    {
        return zhLevel switch
        {
            "信息" => src.Where(x => x.Level.Equals("INFO", StringComparison.OrdinalIgnoreCase)),
            "警告" => src.Where(x => x.Level.Equals("WARN", StringComparison.OrdinalIgnoreCase)),
            "错误" => src.Where(x => x.Level.Equals("ERROR", StringComparison.OrdinalIgnoreCase)),
            _ => src
        };
    }

    private void LogEngine(string msg) => LogEx("INFO", "engine", msg, _recentEngineLogs);
    private void LogCore(string msg) => LogEx("INFO", "core", msg, _recentCoreLogs);
    private void LogProbe(string msg) => LogEx("INFO", "probe", msg, _recentProbeLogs);

    private void LogEx(string level, string source, string msg, List<LogEntry> bucket)
    {
        var e = new LogEntry { Time = DateTime.Now, Level = level, Source = source, Message = msg };
        bucket.Add(e);
        if (bucket.Count > 200) bucket.RemoveRange(0, bucket.Count - 200);
        Log($"[{source}] {msg}");
        RefreshLogViews();
    }

    private List<string> GetRecentCoreLogs(int n)
    {
        if (_recentCoreLogs.Count == 0) return new List<string>();
        return _recentCoreLogs.Skip(Math.Max(0, _recentCoreLogs.Count - n)).Select(x => x.Message).ToList();
    }

    private string GetRecentCoreLogsText(int n)
    {
        return string.Join("\n", GetRecentCoreLogs(n));
    }

    private void ExportLogs()
    {
        using var sfd = new SaveFileDialog { Filter = "Delta Logs|*.log", FileName = $"delta-{DateTime.Now:yyyyMMdd-HHmmss}.log" };
        if (sfd.ShowDialog() != DialogResult.OK) return;
        try
        {
            var lines = new List<string>();
            lines.Add("=== ENGINE ===");
            lines.AddRange(_recentEngineLogs.Select(x => x.ToString()));
            lines.Add("=== CORE ===");
            lines.AddRange(_recentCoreLogs.Select(x => x.ToString()));
            lines.Add("=== PROBE ===");
            lines.AddRange(_recentProbeLogs.Select(x => x.ToString()));
            File.WriteAllLines(sfd.FileName, lines);
            Log($"日志已导出: {sfd.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出日志失败: " + ex.Message, "导出日志", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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

public sealed class LogEntry
{
    public DateTime Time { get; set; }
    public string Level { get; set; } = "";
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public override string ToString() => $"[{Time:HH:mm:ss}] [{Level}] [{Source}] {Message}";
}

public sealed class DeltaSettings
{
    public string? Hy2Server { get; set; }
    public string? Hy2Token { get; set; }
    public int Hy2Port { get; set; } = 8443;
    public string? LastSeenVersion { get; set; }
    public string? GameProcessPaths { get; set; }
    public string? LauncherProcessPaths { get; set; }
    public string? Hy2Sni { get; set; }
    public string? Hy2ObfsType { get; set; }
    public string? Hy2ObfsPassword { get; set; }
    public List<Hy2NodeProfile>? Nodes { get; set; }
}

public sealed class GameProfile
{
    public string Name { get; set; } = "Default";
    public List<string> ProcessPaths { get; set; } = new();
    public List<string> ProcessNames { get; set; } = new();
}

public sealed class Hy2NodeProfile
{
    public string DisplayName { get; set; } = "";
    public string Server { get; set; } = "";
    public int Port { get; set; } = 8443;
    public string Password { get; set; } = "";
    public string? Sni { get; set; }
    public string? ObfsType { get; set; }
    public string? ObfsPassword { get; set; }
}

public sealed class NodeHealthResult
{
    public bool Available { get; set; }
    public string Grade { get; set; } = "Unavailable";
    public long AvgRttMs { get; set; }
    public long JitterMs { get; set; }
    public string Summary { get; set; } = "";
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
    public string? singBoxVersion { get; set; }
    public string? hy2Url { get; set; }
    public string? hy2Version { get; set; }
    public string? notes { get; set; }
    public string? publishedAt { get; set; }
}
