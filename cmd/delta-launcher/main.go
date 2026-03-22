package main

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"sort"
	"strconv"
	"strings"
	"time"
)

type Node struct {
	Key  string `json:"key"`
	Name string `json:"name"`
	Host string `json:"host"`
}

type ProbeResult struct {
	Node  Node `json:"node"`
	AvgMs int  `json:"avgMs"`
	OK    bool `json:"ok"`
	Err   string
}

type State struct {
	ProcessName string    `json:"processName"`
	NodeKey     string    `json:"nodeKey"`
	NodeHost    string    `json:"nodeHost"`
	PolicyName  string    `json:"policyName"`
	StartedAt   time.Time `json:"startedAt"`
}

type apiResp struct {
	OK    bool        `json:"ok"`
	Error string      `json:"error,omitempty"`
	Data  interface{} `json:"data,omitempty"`
}

type applyReq struct {
	Process string `json:"process"`
	Node    string `json:"node"`
}

var nodes = []Node{
	{Key: "jp", Name: "Japan", Host: "216.23.84.252"},
	{Key: "de", Name: "Germany Optimized", Host: "178.22.26.114"},
	{Key: "sjc", Name: "San Jose", Host: "45.143.130.90"},
}

func main() {
	if runtime.GOOS != "windows" {
		fmt.Println("Delta is for Windows runtime.")
		os.Exit(2)
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/", serveIndex)
	mux.HandleFunc("/api/version", handleVersion)
	mux.HandleFunc("/api/processes", handleProcesses)
	mux.HandleFunc("/api/probe", handleProbe)
	mux.HandleFunc("/api/apply", handleApply)
	mux.HandleFunc("/api/rollback", handleRollback)
	mux.HandleFunc("/api/status", handleStatus)

	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		fmt.Println("listen failed:", err)
		os.Exit(1)
	}
	url := "http://" + ln.Addr().String()

	go func() {
		time.Sleep(400 * time.Millisecond)
		_ = openBrowser(url)
	}()

	_ = http.Serve(ln, mux)
}

func serveIndex(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	_, _ = w.Write([]byte(indexHTML))
}

func handleVersion(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, apiResp{OK: true, Data: "Delta GUI Alpha v0.2.0"})
}

func handleStatus(w http.ResponseWriter, r *http.Request) {
	st, err := loadState()
	if err != nil {
		writeJSON(w, apiResp{OK: true, Data: map[string]any{"active": false}})
		return
	}
	writeJSON(w, apiResp{OK: true, Data: map[string]any{"active": true, "state": st}})
}

func handleProcesses(w http.ResponseWriter, r *http.Request) {
	names, err := listProcesses()
	if err != nil {
		writeJSON(w, apiResp{OK: false, Error: err.Error()})
		return
	}
	writeJSON(w, apiResp{OK: true, Data: names})
}

func handleProbe(w http.ResponseWriter, r *http.Request) {
	results := make([]ProbeResult, 0, len(nodes))
	for _, n := range nodes {
		pr := pingNode(n)
		results = append(results, pr)
	}
	sort.Slice(results, func(i, j int) bool {
		if results[i].OK != results[j].OK {
			return results[i].OK
		}
		return results[i].AvgMs < results[j].AvgMs
	})
	writeJSON(w, apiResp{OK: true, Data: results})
}

func handleApply(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeJSON(w, apiResp{OK: false, Error: "method not allowed"})
		return
	}
	var req applyReq
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSON(w, apiResp{OK: false, Error: "bad json"})
		return
	}
	req.Process = strings.TrimSpace(strings.ToLower(req.Process))
	req.Node = strings.TrimSpace(strings.ToLower(req.Node))
	if req.Process == "" {
		writeJSON(w, apiResp{OK: false, Error: "process required"})
		return
	}

	sel, err := selectNode(req.Node)
	if err != nil {
		writeJSON(w, apiResp{OK: false, Error: err.Error()})
		return
	}

	procPath, err := resolveProcessPath(req.Process)
	if err != nil {
		writeJSON(w, apiResp{OK: false, Error: "进程未找到，请先启动游戏后再试"})
		return
	}

	policy := "Delta_" + safePolicyName(strings.TrimSuffix(req.Process, filepath.Ext(req.Process)))
	_ = removeQosPolicy(policy)
	if err := createQosPolicy(policy, procPath); err != nil {
		writeJSON(w, apiResp{OK: false, Error: "创建QoS策略失败，请用管理员权限启动 Delta"})
		return
	}
	_ = setProcessPriority(req.Process, "High")

	st := State{ProcessName: req.Process, NodeKey: sel.Key, NodeHost: sel.Host, PolicyName: policy, StartedAt: time.Now().UTC()}
	_ = saveState(st)
	writeJSON(w, apiResp{OK: true, Data: map[string]any{"message": "加速已应用", "state": st}})
}

func handleRollback(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeJSON(w, apiResp{OK: false, Error: "method not allowed"})
		return
	}
	st, err := loadState()
	if err != nil {
		writeJSON(w, apiResp{OK: true, Data: map[string]any{"message": "无活动策略"}})
		return
	}
	_ = removeQosPolicy(st.PolicyName)
	_ = setProcessPriority(st.ProcessName, "Normal")
	_ = os.Remove(statePath())
	writeJSON(w, apiResp{OK: true, Data: map[string]any{"message": "已回滚"}})
}

func writeJSON(w http.ResponseWriter, v apiResp) {
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(v)
}

func openBrowser(url string) error {
	return exec.Command("rundll32", "url.dll,FileProtocolHandler", url).Start()
}

func selectNode(key string) (Node, error) {
	if key != "" {
		for _, n := range nodes {
			if n.Key == key {
				return n, nil
			}
		}
		return Node{}, errors.New("未知节点")
	}
	best := ProbeResult{OK: false, AvgMs: 1<<31 - 1}
	for _, n := range nodes {
		pr := pingNode(n)
		if pr.OK && pr.AvgMs < best.AvgMs {
			best = pr
		}
	}
	if !best.OK {
		return Node{}, errors.New("节点不可达")
	}
	return best.Node, nil
}

func pingNode(n Node) ProbeResult {
	out, err := exec.Command("ping", "-n", "4", n.Host).CombinedOutput()
	if err != nil {
		return ProbeResult{Node: n, OK: false, Err: err.Error()}
	}
	avg, err := parsePingAvgWindows(string(out))
	if err != nil {
		return ProbeResult{Node: n, OK: false, Err: err.Error()}
	}
	return ProbeResult{Node: n, AvgMs: avg, OK: true}
}

func parsePingAvgWindows(out string) (int, error) {
	low := strings.ToLower(out)
	idx := strings.LastIndex(low, "average")
	if idx == -1 {
		idx = strings.LastIndex(low, "平均")
	}
	if idx == -1 {
		return 0, errors.New("average not found")
	}
	frag := out[idx:]
	msIdx := strings.Index(strings.ToLower(frag), "ms")
	if msIdx == -1 {
		return 0, errors.New("ms not found")
	}
	left := frag[:msIdx]
	digits := ""
	for i := len(left) - 1; i >= 0; i-- {
		if left[i] >= '0' && left[i] <= '9' {
			digits = string(left[i]) + digits
		} else if digits != "" {
			break
		}
	}
	if digits == "" {
		return 0, errors.New("avg parse failed")
	}
	return strconv.Atoi(digits)
}

func listProcesses() ([]string, error) {
	out, err := exec.Command("tasklist", "/FO", "CSV", "/NH").Output()
	if err != nil {
		return nil, err
	}
	scanner := bufio.NewScanner(strings.NewReader(string(out)))
	seen := map[string]bool{}
	arr := []string{}
	for scanner.Scan() {
		parts := parseCSVLine(strings.TrimSpace(scanner.Text()))
		if len(parts) == 0 {
			continue
		}
		name := strings.ToLower(strings.TrimSpace(parts[0]))
		if name == "" || seen[name] {
			continue
		}
		seen[name] = true
		arr = append(arr, name)
	}
	sort.Strings(arr)
	return arr, nil
}

func parseCSVLine(line string) []string {
	line = strings.TrimPrefix(line, "\ufeff")
	var out []string
	cur := strings.Builder{}
	inQuote := false
	for i := 0; i < len(line); i++ {
		ch := line[i]
		switch ch {
		case '"':
			if inQuote && i+1 < len(line) && line[i+1] == '"' {
				cur.WriteByte('"')
				i++
			} else {
				inQuote = !inQuote
			}
		case ',':
			if inQuote {
				cur.WriteByte(ch)
			} else {
				out = append(out, strings.Trim(cur.String(), "\""))
				cur.Reset()
			}
		default:
			cur.WriteByte(ch)
		}
	}
	out = append(out, strings.Trim(cur.String(), "\""))
	return out
}

func resolveProcessPath(proc string) (string, error) {
	query := fmt.Sprintf("name='%s'", proc)
	out, err := exec.Command("wmic", "process", "where", query, "get", "ExecutablePath", "/value").CombinedOutput()
	if err != nil {
		return "", err
	}
	for _, line := range strings.Split(string(out), "\n") {
		line = strings.TrimSpace(line)
		if strings.HasPrefix(strings.ToLower(line), "executablepath=") {
			p := strings.TrimSpace(strings.TrimPrefix(line, "ExecutablePath="))
			if p != "" {
				return p, nil
			}
		}
	}
	return "", errors.New("not found")
}

func createQosPolicy(policyName, processPath string) error {
	ps := fmt.Sprintf("New-NetQosPolicy -Name '%s' -AppPathNameMatchCondition '%s' -DSCPAction 46 -IPProtocolMatchCondition Both -NetworkProfile All -PolicyStore ActiveStore", escapePS(policyName), escapePS(processPath))
	_, err := runPowerShell(ps)
	return err
}

func removeQosPolicy(policyName string) error {
	ps := fmt.Sprintf("$p=Get-NetQosPolicy -Name '%s' -PolicyStore ActiveStore -ErrorAction SilentlyContinue; if($p){ Remove-NetQosPolicy -Name '%s' -PolicyStore ActiveStore -Confirm:$false }", escapePS(policyName), escapePS(policyName))
	_, err := runPowerShell(ps)
	return err
}

func setProcessPriority(proc, priority string) error {
	name := strings.TrimSuffix(proc, filepath.Ext(proc))
	ps := fmt.Sprintf("Get-Process -Name '%s' -ErrorAction SilentlyContinue | ForEach-Object { $_.PriorityClass = '%s' }", escapePS(name), escapePS(priority))
	_, err := runPowerShell(ps)
	return err
}

func runPowerShell(script string) (string, error) {
	out, err := exec.Command("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script).CombinedOutput()
	if err != nil {
		return string(out), fmt.Errorf("%v: %s", err, string(out))
	}
	return string(out), nil
}

func safePolicyName(s string) string {
	s = strings.ToLower(s)
	s = strings.ReplaceAll(s, " ", "_")
	s = strings.ReplaceAll(s, "-", "_")
	var b strings.Builder
	for _, r := range s {
		if (r >= 'a' && r <= 'z') || (r >= '0' && r <= '9') || r == '_' {
			b.WriteRune(r)
		}
	}
	if b.Len() == 0 {
		return "game"
	}
	return b.String()
}

func statePath() string {
	base := os.Getenv("ProgramData")
	if base == "" {
		base = `C:\ProgramData`
	}
	dir := filepath.Join(base, "Delta")
	_ = os.MkdirAll(dir, 0755)
	return filepath.Join(dir, "state.json")
}

func saveState(st State) error {
	b, err := json.MarshalIndent(st, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(statePath(), b, 0644)
}

func loadState() (State, error) {
	b, err := os.ReadFile(statePath())
	if err != nil {
		return State{}, err
	}
	var st State
	if err := json.Unmarshal(b, &st); err != nil {
		return State{}, err
	}
	return st, nil
}

func escapePS(s string) string { return strings.ReplaceAll(s, "'", "''") }

const indexHTML = `<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8" />
<meta name="viewport" content="width=device-width,initial-scale=1" />
<title>Delta</title>
<style>
body{font-family:Segoe UI,system-ui,sans-serif;background:#0b1020;color:#e8ecff;margin:0;padding:18px}
.card{max-width:980px;margin:0 auto;background:#121933;border:1px solid #2c3962;border-radius:14px;padding:16px}
.row{display:flex;gap:8px;flex-wrap:wrap;align-items:center}
select,button{background:#0f1730;color:#e8ecff;border:1px solid #3a4b79;border-radius:10px;padding:8px 10px}
button{cursor:pointer}
pre{background:#0a1126;border:1px solid #2c3962;border-radius:10px;padding:10px;min-height:260px;white-space:pre-wrap}
small{opacity:.75}
.status{margin-left:auto}
</style>
</head>
<body>
<div class="card">
  <h2 style="margin-top:0">Delta</h2>
  <div class="row">
    <button onclick="loadProcesses()">刷新进程</button>
    <select id="proc" style="min-width:280px"></select>
    <select id="node">
      <option value="">自动选择（推荐）</option>
      <option value="jp">JP 日本</option>
      <option value="de">DE 德国优化</option>
      <option value="sjc">SJC 圣何塞</option>
    </select>
    <button onclick="probe()">测速</button>
    <button onclick="applyBoost()">开始加速</button>
    <button onclick="rollbackBoost()">停止/回滚</button>
    <span id="st" class="status"></span>
  </div>
  <small>请用管理员权限启动 Delta.exe。</small>
  <pre id="log"></pre>
</div>
<script>
const logEl=document.getElementById('log');
const procEl=document.getElementById('proc');
const nodeEl=document.getElementById('node');
const stEl=document.getElementById('st');
function log(s){logEl.textContent='['+new Date().toLocaleTimeString()+'] '+s+'\n'+logEl.textContent;}
async function j(url,opt){const r=await fetch(url,opt);return r.json();}
function setStatus(s){stEl.textContent=s||'';}
async function loadProcesses(){const x=await j('/api/processes');if(!x.ok){log('进程刷新失败: '+x.error);return;}procEl.innerHTML=(x.data||[]).map(p=>'<option>'+p+'</option>').join('');log('进程数量: '+(x.data||[]).length);}
async function probe(){const x=await j('/api/probe');if(!x.ok){log('测速失败: '+x.error);return;}const arr=x.data||[];if(!arr.length){log('无测速结果');return;}let lines=['节点测速结果:'];for(const r of arr){lines.push('- '+r.node.key+' '+r.node.host+' '+(r.ok?(r.avgMs+'ms'):('ERR '+(r.err||''))));}const best=arr.find(x=>x.ok);if(best)lines.push('推荐: '+best.node.key+' '+best.avgMs+'ms');log(lines.join('\n'));}
async function applyBoost(){const process=(procEl.value||'').trim();if(!process){log('请先选择进程');return;}const node=(nodeEl.value||'').trim();const x=await j('/api/apply',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({process,node})});if(!x.ok){log('应用失败: '+x.error);return;}log((x.data&&x.data.message)||'加速已应用');await loadStatus();}
async function rollbackBoost(){const x=await j('/api/rollback',{method:'POST'});if(!x.ok){log('回滚失败: '+x.error);return;}log((x.data&&x.data.message)||'已回滚');await loadStatus();}
async function loadStatus(){const x=await j('/api/status');if(!x.ok){setStatus('状态异常');return;}if(!x.data||!x.data.active){setStatus('当前：未加速');return;}const s=x.data.state||{};setStatus('当前：'+(s.processName||'-')+' @ '+(s.nodeKey||'-'));}
(async()=>{const v=await j('/api/version').catch(()=>null);if(v&&v.ok)log('版本: '+v.data);await loadProcesses();await loadStatus();})();
</script>
</body>
</html>`
