package main

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/lxn/walk"
	. "github.com/lxn/walk/declarative"
)

type Node struct {
	Key  string
	Name string
	Host string
}

type ProbeResult struct {
	Node  Node
	AvgMs int
	OK    bool
	Err   error
}

type State struct {
	ProcessName string    `json:"processName"`
	NodeKey     string    `json:"nodeKey"`
	NodeHost    string    `json:"nodeHost"`
	PolicyName  string    `json:"policyName"`
	StartedAt   time.Time `json:"startedAt"`
}

const AppVersion = "G1"

var nodes = []Node{
	{Key: "jp", Name: "JP 日本", Host: "216.23.84.252"},
	{Key: "de", Name: "DE 德国优化", Host: "178.22.26.114"},
	{Key: "sjc", Name: "SJC 圣何塞", Host: "45.143.130.90"},
}

func main() {
	var mw *walk.MainWindow
	var procCB *walk.ComboBox
	var nodeCB *walk.ComboBox
	var logTE *walk.TextEdit
	var statusLbl *walk.Label

	appendLog := func(s string) {
		t := fmt.Sprintf("[%s] %s\r\n", time.Now().Format("15:04:05"), s)
		logTE.SetText(t + logTE.Text())
	}

	refreshProcesses := func() {
		procs, err := listProcesses()
		if err != nil {
			appendLog("刷新进程失败: " + err.Error())
			return
		}
		_ = procCB.SetModel(procs)
		if len(procs) > 0 {
			procCB.SetCurrentIndex(0)
		}
		appendLog(fmt.Sprintf("进程数量: %d", len(procs)))
	}

	showStatus := func() {
		st, err := loadState()
		if err != nil {
			statusLbl.SetText("当前：未加速")
			return
		}
		statusLbl.SetText(fmt.Sprintf("当前：%s @ %s", st.ProcessName, st.NodeKey))
	}

	probeNodes := func() {
		results := make([]ProbeResult, 0, len(nodes))
		for _, n := range nodes {
			results = append(results, pingNode(n))
		}
		sort.Slice(results, func(i, j int) bool {
			if results[i].OK != results[j].OK {
				return results[i].OK
			}
			return results[i].AvgMs < results[j].AvgMs
		})
		appendLog("节点测速结果:")
		for _, r := range results {
			if r.OK {
				appendLog(fmt.Sprintf("- %s %s %dms", r.Node.Key, r.Node.Host, r.AvgMs))
			} else {
				appendLog(fmt.Sprintf("- %s %s ERR %v", r.Node.Key, r.Node.Host, r.Err))
			}
		}
		for _, r := range results {
			if r.OK {
				appendLog(fmt.Sprintf("推荐节点: %s", r.Node.Key))
				return
			}
		}
	}

	applyBoost := func() {
		proc := strings.TrimSpace(strings.ToLower(procCB.Text()))
		if proc == "" {
			appendLog("请先选择进程")
			return
		}

		nodeTxt := strings.TrimSpace(nodeCB.Text())
		key := ""
		if strings.Contains(nodeTxt, "jp") || strings.Contains(nodeTxt, "JP") {
			key = "jp"
		} else if strings.Contains(nodeTxt, "de") || strings.Contains(nodeTxt, "DE") {
			key = "de"
		} else if strings.Contains(nodeTxt, "sjc") || strings.Contains(nodeTxt, "SJC") {
			key = "sjc"
		}

		sel, err := selectNode(key)
		if err != nil {
			appendLog("节点选择失败: " + err.Error())
			return
		}

		p, err := resolveProcessPath(proc)
		if err != nil {
			appendLog("进程未找到，请先启动游戏")
			return
		}

		policy := "Delta_" + safePolicyName(strings.TrimSuffix(proc, filepath.Ext(proc)))
		_ = removeQosPolicy(policy)
		if err := createQosPolicy(policy, p); err != nil {
			appendLog("创建QoS失败，请用管理员权限运行")
			return
		}
		_ = setProcessPriority(proc, "High")
		_ = saveState(State{ProcessName: proc, NodeKey: sel.Key, NodeHost: sel.Host, PolicyName: policy, StartedAt: time.Now().UTC()})
		appendLog("加速已应用: " + proc + " @ " + sel.Key)
		showStatus()
	}

	rollback := func() {
		st, err := loadState()
		if err != nil {
			appendLog("无活动策略")
			showStatus()
			return
		}
		_ = removeQosPolicy(st.PolicyName)
		_ = setProcessPriority(st.ProcessName, "Normal")
		_ = os.Remove(statePath())
		appendLog("已回滚")
		showStatus()
	}

	err := (MainWindow{
		AssignTo: &mw,
		Title:    "Delta " + AppVersion,
		MinSize:  Size{Width: 980, Height: 680},
		Layout:   VBox{MarginsZero: false},
		Children: []Widget{
			Composite{
				Layout: HBox{},
				Children: []Widget{
					PushButton{Text: "刷新进程", OnClicked: refreshProcesses},
					ComboBox{AssignTo: &procCB, Editable: true, MinSize: Size{Width: 300, Height: 0}},
					ComboBox{AssignTo: &nodeCB, Model: []string{"自动选择（推荐）", "jp - JP 日本", "de - DE 德国优化", "sjc - SJC 圣何塞"}, CurrentIndex: 0, MinSize: Size{Width: 220, Height: 0}},
					PushButton{Text: "测速", OnClicked: probeNodes},
					PushButton{Text: "开始加速", OnClicked: applyBoost},
					PushButton{Text: "停止/回滚", OnClicked: rollback},
				},
			},
			Label{AssignTo: &statusLbl, Text: "当前：未加速"},
			TextEdit{AssignTo: &logTE, ReadOnly: true, VScroll: true},
		},
	}).Create()
	if err != nil {
		panic(err)
	}

	refreshProcesses()
	showStatus()
	appendLog("Delta 版本: " + AppVersion)
	appendLog("Delta GUI 已启动")
	mw.Run()
}

func selectNode(key string) (Node, error) {
	if key != "" {
		for _, n := range nodes {
			if n.Key == key {
				return n, nil
			}
		}
		return Node{}, errors.New("unknown node")
	}
	best := ProbeResult{OK: false, AvgMs: 1<<31 - 1}
	for _, n := range nodes {
		pr := pingNode(n)
		if pr.OK && pr.AvgMs < best.AvgMs {
			best = pr
		}
	}
	if !best.OK {
		return Node{}, errors.New("all nodes unreachable")
	}
	return best.Node, nil
}

func pingNode(n Node) ProbeResult {
	out, err := exec.Command("ping", "-n", "4", n.Host).CombinedOutput()
	if err != nil {
		return ProbeResult{Node: n, OK: false, Err: err}
	}
	avg, err := parsePingAvgWindows(string(out))
	if err != nil {
		return ProbeResult{Node: n, OK: false, Err: err}
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
		return 0, errors.New("avg not found")
	}
	frag := out[idx:]
	msIdx := strings.Index(strings.ToLower(frag), "ms")
	if msIdx == -1 {
		return 0, errors.New("ms not found")
	}
	left := frag[:msIdx]
	d := ""
	for i := len(left) - 1; i >= 0; i-- {
		if left[i] >= '0' && left[i] <= '9' {
			d = string(left[i]) + d
		} else if d != "" {
			break
		}
	}
	if d == "" {
		return 0, errors.New("avg parse fail")
	}
	return strconv.Atoi(d)
}

func listProcesses() ([]string, error) {
	out, err := exec.Command("tasklist", "/FO", "CSV", "/NH").Output()
	if err != nil {
		return nil, err
	}
	s := bufio.NewScanner(strings.NewReader(string(out)))
	seen := map[string]bool{}
	arr := []string{}
	for s.Scan() {
		parts := parseCSVLine(strings.TrimSpace(s.Text()))
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
