<p align="center"><img src="assets/readme-icon.png" width="88" alt="ZGSTokenBar 图标" /></p>
<h1 align="center">ZGSTokenBar</h1>

ZGSTokenBar 是一个开源、本地优先的 Windows 任务栏用量工具。紧凑 Mini 可以吸附任务栏，也可以拖出成为桌面悬浮条。

## 主要能力

- 内置 Claude、Codex 配额 Provider、系统指标，以及随应用打包的 DeepSeek Harness Provider。现有本机 Provider 凭据会被自动识别，无需复制 Key；显式模块开关始终优先。
- 本机 Codex Token 汇总，以及今日、昨日和 30 天 API 等值美元估算。估算使用带日期的模型价格快照，不等同于订阅账单。
- 多个 Codex 账号先按 Pro、Plus 分组，同一套餐内再按剩余额度从高到低排序。
- Mini 区域可独立排序、折叠和调整宽度，并提供配额详情与重置倒计时。
- 可选“登录时启动”。
- 类型化 Provider SDK、严格 Manifest、自动生成的内置注册表，以及隔离的进程插件支持。
- 本机命名管道控制 API 和 NativeAOT 命令行工具。
- 无遥测、无云同步。

## 使用

拖动 Mini 卡片区域可沿任务栏重新定位，也可拖到桌面成为悬浮条。悬停真实配额胶囊可查看准确重置时间、实时倒计时、新鲜度和用量详情；单击可固定弹窗，单击外部或按 Escape 关闭。启用可选 Radar 数据源后，悬停 Mini 中的 Codex Logo 可打开 Radar。

Claude、Codex 和 DeepSeek Harness 会复用各自本机工具已经配置的凭据，ZGSTokenBar 不提供 Provider API Key 输入框。未知或不受支持的 Codex 价格类别会明确显示为“未定价”；混合周期显示为 `≈$1.23+` 这类下限估算，不会错误显示为零。

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
