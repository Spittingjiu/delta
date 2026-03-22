# Delta

Delta 是一个 Windows 端 HY2 加速客户端实验项目，目标是做“可验证”的代理/接管链路，而不是只改 UI 文案。

> 当前仓库用于快速迭代验证。接口与实现会持续变化。

## 目录结构

- `dotnet/DeltaG2/`：主客户端（WinForms）
- `dotnet/DeltaIPProbe/`：独立 IP / 接管验证工具
- `cmd/`：Go 侧启动与相关辅助代码
- `scripts/`：构建/辅助脚本

## 功能概览（当前）

- HY2 连接配置（IP / Port / Token）
- 更新检查与一键更新（从 `delta.zzao.de/latest.json` 拉取）
- 网络修复（清理代理、重置网络栈）
- TUN 前置检查（含 Wintun 安装路径）
- 独立 `DeltaIPProbe.exe` 用于直连/代理 IP 与接管验证

## 本地构建

### 环境要求

- .NET SDK 8.0+
- Windows 目标构建（Linux 构建时需 `EnableWindowsTargeting=true`）

### 构建主程序

```bash
cd dotnet/DeltaG2
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true -o ../../dist-g6
```

输出示例：

- `dist-g6/Delta.exe`

### 构建 IPProbe

```bash
cd dotnet/DeltaIPProbe
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true -o ../../dist-g6/ipprobe
```

输出示例：

- `dist-g6/ipprobe/DeltaIPProbe.exe`

## 发布约定

- 版本命名：`Delta vGx.x.exe`
- 发布目录：`https://delta.zzao.de/releases/`
- 更新清单：`https://delta.zzao.de/latest.json`

`latest.json` 关键字段：

- `version`
- `exeUrl`
- `exeFileName`
- `singBoxUrl`
- `hy2Url`
- `ipProbeUrl`

## 注意事项

- TUN / 驱动相关逻辑需要管理员权限。
- 进程“真实接管”必须靠实际连接路径验证（而非仅 UI 状态）。
- 若网络异常，可先用客户端“网络修复”回到直连基线。

## License

暂未指定（默认保留所有权利）。
