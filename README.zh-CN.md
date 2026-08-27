<p align="center"><img src="assets/readme-icon.png" width="88" alt="ZGSTokenBar 图标" /></p>
<h1 align="center">ZGSTokenBar</h1>

ZGSTokenBar 是一个开源、本地优先的 Windows 任务栏用量工具。紧凑 Mini 可以吸附任务栏，也可以拖出成为桌面悬浮条。

## 主要能力

- 内置 Claude、Codex 配额 Provider、本机 Codex Token 汇总和系统指标。
- 多个 Codex 账号先按 Pro、Plus 分组，同一套餐内再按剩余额度从高到低排序。
- Mini 区域可独立排序、折叠和调整宽度，并提供配额详情与重置倒计时。
- 可选“登录时启动”和“保持运行”。开启保持运行后，程序异常退出会由看护进程自动重启，直到关闭该选项。
- 类型化 Provider SDK、严格 Manifest、自动生成的内置注册表，以及隔离的进程插件支持。
- 本机命名管道控制 API 和 NativeAOT 命令行工具。
- 无遥测、无云同步。

## 构建与验证

需要 Windows 10 或更高版本。源码构建使用 .NET SDK 10 和 Node.js 24：

```powershell
npm ci
npm run verify
npm run dist
```

`npm run verify` 是完整的非交互终审门禁；`npm run dist` 会在 `release/` 下生成便携版应用和 CLI。

Provider 扩展请看 [Provider 开发](docs/providers.md)，其他说明见 [开发文档](docs/development.md) 和 [隐私与安全](docs/privacy-security.md)。

## 许可证

MIT，详见 [LICENSE](LICENSE)。
