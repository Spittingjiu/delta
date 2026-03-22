using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DeltaIPProbe;

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
    private const string DeltaProxyHost = "127.0.0.1";
    private const int DeltaProxyHttpPort = 10809;
    private const int DeltaProxySocksPort = 10808;

    private readonly ComboBox _processCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Label _status = new() { AutoSize = true, Text = "状态：检测中" };
    private readonly Label _currentIp = new() { AutoSize = true, Text = "当前IP：-" };
    private readonly Label _mode = new() { AutoSize = true, Text = "模式：-" };
    private readonly Label _takeover = new() { AutoSize = true, Text = "进程接管：-" };

    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Font = new("Consolas", 10f)
    };

    public MainForm()
    {
        Text = "Delta IP Probe";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Width = 840;
        Height = 520;
        StartPosition = FormStartPosition.CenterScreen;

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            AutoSize = false,
            WrapContents = false,
            Padding = new Padding(8, 8, 8, 4)
        };

        var btnRefreshProc = new Button { Text = "刷新进程", AutoSize = true };
        var btnCheck = new Button { Text = "检查IP", AutoSize = true };
        var btnVerifyProcess = new Button { Text = "验证进程接管", AutoSize = true };

        btnRefreshProc.Click += (_, _) => RefreshProcesses();
        btnCheck.Click += async (_, _) => await CheckCurrentIpAsync();
        btnVerifyProcess.Click += (_, _) => VerifyProcessTakeover();

        top.Controls.Add(btnRefreshProc);
        top.Controls.Add(_processCombo);
        top.Controls.Add(btnCheck);
        top.Controls.Add(btnVerifyProcess);

        var mid = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 78,
            AutoSize = false,
            WrapContents = true,
            Padding = new Padding(10, 4, 10, 4)
        };
        mid.Controls.Add(_status);
        mid.Controls.Add(new Label { Text = "   " });
        mid.Controls.Add(_mode);
        mid.Controls.Add(new Label { Text = "   " });
        mid.Controls.Add(_currentIp);
        mid.Controls.Add(new Label { Text = "   " });
        mid.Controls.Add(_takeover);

        Controls.Add(_log);
        Controls.Add(mid);
        Controls.Add(top);

        Load += async (_, _) =>
        {
            RefreshProcesses();
            await CheckCurrentIpAsync();
        };
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
            Log($"进程列表刷新完成: {list.Count}");
        }
        catch (Exception ex)
        {
            Log("刷新进程失败: " + ex.Message);
        }
    }

    private async Task CheckCurrentIpAsync()
    {
        try
        {
            var proxyAlive = await IsDeltaProxyAliveAsync();

            if (proxyAlive)
            {
                _status.Text = "状态：已检测到 Delta 代理";
                _mode.Text = $"模式：代理 ({DeltaProxyHost}:{DeltaProxyHttpPort})";

                var ip = await QueryIpAsync(useProxy: true);
                _currentIp.Text = string.IsNullOrWhiteSpace(ip) ? "当前IP：获取失败" : $"当前IP：{ip}";
                Log($"代理模式 IP = {ip ?? "-"}");
            }
            else
            {
                _status.Text = "状态：未检测到 Delta 代理";
                _mode.Text = "模式：直连";

                var ip = await QueryIpAsync(useProxy: false);
                _currentIp.Text = string.IsNullOrWhiteSpace(ip) ? "当前IP：获取失败" : $"当前IP：{ip}";
                Log($"直连模式 IP = {ip ?? "-"}");
            }
        }
        catch (Exception ex)
        {
            Log("检查异常: " + ex.Message);
        }
    }

    private void VerifyProcessTakeover()
    {
        try
        {
            var selected = _processCombo.SelectedItem?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(selected))
            {
                _takeover.Text = "进程接管：未选择进程";
                return;
            }

            var processName = Path.GetFileNameWithoutExtension(selected);
            var pids = Process.GetProcessesByName(processName).Select(p => p.Id).ToHashSet();
            if (pids.Count == 0)
            {
                _takeover.Text = $"进程接管：{selected} 未运行";
                Log($"{selected} 未运行");
                return;
            }

            var rows = GetNetstatRows();
            var targetRows = rows.Where(r => pids.Contains(r.Pid) && r.Protocol == "TCP" && r.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)).ToList();
            var toLocalProxy = targetRows.Where(r => IsLoopback(r.RemoteAddress) && (r.RemotePort == DeltaProxyHttpPort || r.RemotePort == DeltaProxySocksPort)).ToList();
            var directRemote = targetRows.Where(r => !IsLoopback(r.RemoteAddress)).ToList();

            if (toLocalProxy.Count > 0)
            {
                _takeover.Text = $"进程接管：已走本地代理 ({toLocalProxy.Count} 条)";
            }
            else if (directRemote.Count > 0)
            {
                _takeover.Text = $"进程接管：未接管（直连 {directRemote.Count} 条）";
            }
            else
            {
                _takeover.Text = "进程接管：无活动连接，暂无法判定";
            }

            Log($"验证 {selected} PID={string.Join(',', pids)} | 本地代理连接={toLocalProxy.Count} | 直连连接={directRemote.Count}");
        }
        catch (Exception ex)
        {
            _takeover.Text = "进程接管：验证异常";
            Log("验证异常: " + ex.Message);
        }
    }

    private static async Task<bool> IsDeltaProxyAliveAsync()
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connectTask = tcp.ConnectAsync(DeltaProxyHost, DeltaProxyHttpPort);
            var timeoutTask = Task.Delay(1200);
            var done = await Task.WhenAny(connectTask, timeoutTask);
            return done == connectTask && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> QueryIpAsync(bool useProxy)
    {
        var handler = new HttpClientHandler();
        if (useProxy)
        {
            handler.Proxy = new WebProxy($"http://{DeltaProxyHost}:{DeltaProxyHttpPort}");
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
            Arguments = "-ano -p tcp",
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
            if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = Regex.Split(line, "\\s+")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
            if (parts.Length < 5) continue;

            if (!TrySplitEndpoint(parts[1], out var localIp, out var localPort)) continue;
            if (!TrySplitEndpoint(parts[2], out var remoteIp, out var remotePort)) continue;
            var state = parts[3];
            if (!int.TryParse(parts[4], out var pid)) continue;

            rows.Add(new NetRow
            {
                Protocol = "TCP",
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

    private void Log(string msg)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
    }
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
