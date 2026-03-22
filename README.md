# Delta

Delta 是一个 Windows 端 HY2 加速客户端（WinForms）。
当前分支已演进到 **G6.52**，核心目标是：
- 可验证的 TUN + HY2 接管链路
- 节点导入/切换驱动
- 面向真实使用的简化 UI（中文）

---

## 当前状态（与本地项目同步）

- 最新版本：`G6.52`
- 发布清单：`https://delta.zzao.de/latest.json`
- 程序下载：`https://delta.zzao.de/releases/Delta%20vG6.52.exe`
- 便携包：`https://delta.zzao.de/releases/Delta-G6.52-portable.zip`

核心二进制（随更新检查）：
- `sing-box.exe`（当前清单版本：`1.13.3`）
- `hy2-client.exe`（当前清单版本：`20250307`）

---

## 目录结构

- `dotnet/DeltaG2/`：主客户端（WinForms）
- `cmd/`：Go 侧辅助代码
- `scripts/`：构建与辅助脚本
- `dist-g6/`：本地构建输出目录

运行时文件（程序目录下统一管理）：
- `config/`：`settings.json`、`sing-box-delta.json`
- `core/`：`sing-box.exe`、`hy2-client.exe`、`wintun.dll`
- `cache/`：下载和解压缓存
- `logs/`：运行日志

---

## 功能概览（G6.52）

### 1) 节点驱动
- 支持导入/导出节点 JSON
- 支持“设为当前节点”
- 启动前会校验是否已有当前节点

### 2) 文件夹代理模式
- 选择文件夹后，自动纳入该文件夹及子文件夹中的全部 `.exe`
- 顶部已移除“游戏下拉”与冗余路径输入

### 3) 更新机制
- 检查更新时同时检查：
  - Delta 程序版本
  - sing-box 版本
  - hy2 版本
- 发现更新后弹窗“是否现在更新（是/否）”
- 一键更新会同步更新程序 + 核心二进制

### 4) TUN / 接管链路
- Wintun 使用 DLL 准备路径（不走 `wintun.inf/pnputil`）
- TUN 配置兼容 sing-box 1.12+（`address[]`）
- 握手探测改为非阻断告警，避免误判启动失败

### 5) UI 与日志
- 中文 UI，菜单化操作
- 分区面板（节点/游戏/状态/日志）
- 日志过滤与降噪，降低高日志量下的卡顿

---

## 本地构建

### 环境要求
- .NET SDK 8.0+
- 构建 Windows 目标（Linux 构建时需 Windows targeting）

### 构建命令

```bash
cd dotnet/DeltaG2
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true -o ../../dist-g6
```

输出：
- `dist-g6/Delta.exe`

---

## 发布约定

- 版本命名：`Delta vGx.xx.exe`
- 发布目录：`https://delta.zzao.de/releases/`
- 清单地址：`https://delta.zzao.de/latest.json`

`latest.json` 关键字段：
- `version`
- `exeUrl`
- `exeFileName`
- `singBoxUrl`
- `singBoxVersion`
- `hy2Url`
- `hy2Version`
- `url`
- `fileName`
- `publishedAt`

---

## License

暂未指定（默认保留所有权利）。
