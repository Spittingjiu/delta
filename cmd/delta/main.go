package main

import (
	"bufio"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
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

type AppConfig struct {
	Nodes []Node `json:"nodes"`
}

var nodes []Node

type ProbeResult struct {
	Node   Node
	AvgMs  int
	RawOut string
	Err    error
}

type State struct {
	ProcessName string    `json:"processName"`
	NodeKey     string    `json:"nodeKey"`
	NodeHost    string    `json:"nodeHost"`
	PolicyName  string    `json:"policyName"`
	StartedAt   time.Time `json:"startedAt"`
}

func main() {
	if err := loadNodesFromConfig(); err != nil {
		fmt.Println("load config failed:", err)
		os.Exit(1)
	}
	if len(os.Args) < 2 {
		usage()
		os.Exit(1)
	}

	cmd := strings.ToLower(os.Args[1])
	switch cmd {
	case "probe":
		mustWindows()
		cmdProbe()
	case "list-proc":
		mustWindows()
		cmdListProc()
	case "apply":
		mustWindows()
		cmdApply(os.Args[2:])
	case "rollback":
		mustWindows()
		cmdRollback()
	case "run":
		mustWindows()
		cmdRun(os.Args[2:])
	case "version", "-v", "--version":
		fmt.Println("Delta Alpha v0.1.0")
	default:
		usage()
		os.Exit(1)
	}
}

func usage() {
	fmt.Println("Delta Alpha v0.1.0")
	fmt.Println("Usage:")
	fmt.Println("  delta.exe probe")
	fmt.Println("  delta.exe list-proc")
	fmt.Println("  delta.exe apply --process game.exe [--node jp|de|sjc]")
	fmt.Println("  delta.exe rollback")
	fmt.Println("  delta.exe run --process game.exe [--node jp|de|sjc]")
}

func mustWindows() {
	if runtime.GOOS != "windows" {
		fmt.Println("This build is for Windows runtime only.")
		os.Exit(2)
	}
}

func cmdProbe() {
	results := make([]ProbeResult, 0, len(nodes))
	for _, n := range nodes {
		res := pingNode(n)
		results = append(results, res)
	}

	sort.Slice(results, func(i, j int) bool {
		if results[i].Err != nil && results[j].Err == nil {
			return false
		}
		if results[i].Err == nil && results[j].Err != nil {
			return true
		}
		return results[i].AvgMs < results[j].AvgMs
	})

	fmt.Println("Node probe results (lower is better):")
	for _, r := range results {
		if r.Err != nil {
			fmt.Printf("- %-4s %-20s %-15s error: %v\n", r.Node.Key, r.Node.Name, r.Node.Host, r.Err)
			continue
		}
		fmt.Printf("- %-4s %-20s %-15s avg=%dms\n", r.Node.Key, r.Node.Name, r.Node.Host, r.AvgMs)
	}

	for _, r := range results {
		if r.Err == nil {
			fmt.Printf("\nRecommended node: %s (%s, %dms)\n", r.Node.Key, r.Node.Host, r.AvgMs)
			return
		}
	}
	fmt.Println("\nNo reachable node detected.")
}

func cmdListProc() {
	names, err := listProcesses()
	if err != nil {
		fmt.Println("list processes failed:", err)
		os.Exit(1)
	}
	for _, n := range names {
		fmt.Println(n)
	}
}

func cmdApply(args []string) {
	fs := flag.NewFlagSet("apply", flag.ExitOnError)
	proc := fs.String("process", "", "target process name (e.g. valorant.exe)")
	node := fs.String("node", "", "node key (jp|de|sjc); empty=auto probe")
	_ = fs.Parse(args)

	if *proc == "" {
		fmt.Println("--process is required")
		os.Exit(1)
	}

	selected, err := selectNode(*node)
	if err != nil {
		fmt.Println("select node failed:", err)
		os.Exit(1)
	}

	path, err := resolveProcessPath(*proc)
	if err != nil {
		fmt.Println("resolve process failed:", err)
		fmt.Println("Tip: start the game first, then run apply.")
		os.Exit(1)
	}

	policy := "Delta_" + safePolicyName(strings.TrimSuffix(*proc, filepath.Ext(*proc)))
	if err := removeQosPolicy(policy); err != nil {
		fmt.Println("warning: remove existing policy failed:", err)
	}

	if err := createQosPolicy(policy, path); err != nil {
		fmt.Println("create QoS policy failed:", err)
		os.Exit(1)
	}

	if err := setProcessPriority(*proc, "High"); err != nil {
		fmt.Println("warning: set process priority failed:", err)
	}

	st := State{
		ProcessName: *proc,
		NodeKey:     selected.Key,
		NodeHost:    selected.Host,
		PolicyName:  policy,
		StartedAt:   time.Now().UTC(),
	}
	if err := saveState(st); err != nil {
		fmt.Println("warning: save state failed:", err)
	}

	fmt.Printf("Applied: process=%s node=%s(%s) policy=%s\n", *proc, selected.Key, selected.Host, policy)
	fmt.Println("Done. Use delta.exe rollback to revert.")
}

func cmdRollback() {
	st, err := loadState()
	if err != nil {
		fmt.Println("No state found, rollback skipped.")
		return
	}

	if err := removeQosPolicy(st.PolicyName); err != nil {
		fmt.Println("warning: remove QoS policy failed:", err)
	}
	if err := setProcessPriority(st.ProcessName, "Normal"); err != nil {
		fmt.Println("warning: restore process priority failed:", err)
	}
	_ = os.Remove(statePath())
	fmt.Println("Rollback complete.")
}

func cmdRun(args []string) {
	fs := flag.NewFlagSet("run", flag.ExitOnError)
	proc := fs.String("process", "", "target process name")
	node := fs.String("node", "", "node key (jp|de|sjc); empty=auto probe")
	interval := fs.Int("interval", 3, "watch interval seconds")
	_ = fs.Parse(args)

	if *proc == "" {
		fmt.Println("--process is required")
		os.Exit(1)
	}

	cmdApply([]string{"--process", *proc, "--node", *node})
	fmt.Printf("Watching process %s ...\n", *proc)

	tick := time.NewTicker(time.Duration(*interval) * time.Second)
	defer tick.Stop()

	for range tick.C {
		running, _ := processRunning(*proc)
		if !running {
			fmt.Println("Process exited, auto rollback...")
			cmdRollback()
			return
		}
	}
}

func configPath() string {
	base := os.Getenv("ProgramData")
	if base == "" {
		base = `C:\ProgramData`
	}
	dir := filepath.Join(base, "Delta", "config")
	_ = os.MkdirAll(dir, 0755)
	return filepath.Join(dir, "settings.json")
}

func loadNodesFromConfig() error {
	b, err := os.ReadFile(configPath())
	if err != nil {
		return err
	}
	var cfg AppConfig
	if err := json.Unmarshal(b, &cfg); err != nil {
		return err
	}
	if len(cfg.Nodes) == 0 {
		return errors.New("no nodes in config/settings.json")
	}
	nodes = cfg.Nodes
	return nil
}

func selectNode(key string) (Node, error) {
	if key != "" {
		for _, n := range nodes {
			if n.Key == key {
				return n, nil
			}
		}
		return Node{}, fmt.Errorf("unknown node key: %s", key)
	}

	var best *ProbeResult
	for _, n := range nodes {
		r := pingNode(n)
		if r.Err != nil {
			continue
		}
		if best == nil || r.AvgMs < best.AvgMs {
			cpy := r
			best = &cpy
		}
	}
	if best == nil {
		return Node{}, errors.New("all nodes unreachable")
	}
	return best.Node, nil
}

func pingNode(n Node) ProbeResult {
	cmd := exec.Command("ping", "-n", "4", n.Host)
	out, err := cmd.CombinedOutput()
	if err != nil {
		return ProbeResult{Node: n, Err: err, RawOut: string(out)}
	}
	avg, err := parsePingAvgWindows(string(out))
	if err != nil {
		return ProbeResult{Node: n, Err: err, RawOut: string(out)}
	}
	return ProbeResult{Node: n, AvgMs: avg, RawOut: string(out)}
}

func parsePingAvgWindows(out string) (int, error) {
	idx := strings.LastIndex(strings.ToLower(out), "average")
	if idx == -1 {
		idx = strings.LastIndex(strings.ToLower(out), "平均")
	}
	if idx == -1 {
		return 0, errors.New("average not found")
	}
	fragment := out[idx:]
	msIdx := strings.Index(strings.ToLower(fragment), "ms")
	if msIdx == -1 {
		return 0, errors.New("ms suffix not found")
	}
	left := fragment[:msIdx]
	numStart := -1
	for i := len(left) - 1; i >= 0; i-- {
		if left[i] >= '0' && left[i] <= '9' {
			numStart = i
		} else if numStart != -1 {
			numStart = i + 1
			break
		}
	}
	if numStart == -1 {
		return 0, errors.New("avg number not found")
	}
	num := strings.TrimSpace(left[numStart:])
	return strconv.Atoi(num)
}

func listProcesses() ([]string, error) {
	out, err := exec.Command("tasklist", "/FO", "CSV", "/NH").Output()
	if err != nil {
		return nil, err
	}
	scanner := bufio.NewScanner(strings.NewReader(string(out)))
	seen := map[string]bool{}
	var names []string
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" {
			continue
		}
		parts := parseCSVLine(line)
		if len(parts) == 0 {
			continue
		}
		name := strings.ToLower(strings.TrimSpace(parts[0]))
		if name == "" || seen[name] {
			continue
		}
		seen[name] = true
		names = append(names, name)
	}
	sort.Strings(names)
	return names, nil
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
				out = append(out, cur.String())
				cur.Reset()
			}
		default:
			cur.WriteByte(ch)
		}
	}
	out = append(out, cur.String())
	for i := range out {
		out[i] = strings.Trim(out[i], "\"")
	}
	return out
}

func resolveProcessPath(proc string) (string, error) {
	proc = strings.ToLower(proc)
	query := fmt.Sprintf("name='%s'", proc)
	cmd := exec.Command("wmic", "process", "where", query, "get", "ExecutablePath", "/value")
	out, err := cmd.CombinedOutput()
	if err != nil {
		return "", err
	}
	for _, line := range strings.Split(string(out), "\n") {
		line = strings.TrimSpace(line)
		if strings.HasPrefix(strings.ToLower(line), "executablepath=") {
			path := strings.TrimSpace(strings.TrimPrefix(line, "ExecutablePath="))
			if path != "" {
				return path, nil
			}
		}
	}
	return "", errors.New("process executable path not found")
}

func processRunning(proc string) (bool, error) {
	out, err := exec.Command("tasklist", "/FI", "IMAGENAME eq "+proc).CombinedOutput()
	if err != nil {
		return false, err
	}
	text := strings.ToLower(string(out))
	return strings.Contains(text, strings.ToLower(proc)), nil
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
	cmd := exec.Command("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script)
	out, err := cmd.CombinedOutput()
	if err != nil {
		return string(out), fmt.Errorf("%w: %s", err, string(out))
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

func escapePS(s string) string {
	return strings.ReplaceAll(s, "'", "''")
}
