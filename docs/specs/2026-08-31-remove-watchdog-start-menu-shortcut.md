# 移除 watchdog 并提供开始菜单入口

- 状态：Implemented
- 最后更新：2026-08-31
- 基线：`7bb1dee113af2edb64c985d08987dbdc27adcb10`
- 设计与执行授权：已授权；用户要求保留可手动启动入口并移除 watchdog
- 仓库提交与推送授权：用户已要求“收尾”；未授权创建发布版本
- 本机部署授权：已授权；当前自用便携版和当前用户开始菜单在范围内

## 目标与非目标

保留单实例桌面应用和可选的“随 Windows 启动”，完整移除应用内 watchdog、常驻运行设置及 `--watchdog` 启动模式；将已部署便携版固定到当前用户开始屏幕，便于应用退出后手动恢复。

本次不新增 Windows 服务、计划任务、安装器或自动崩溃恢复，不改变配额、插件、窗口布局、更新检查和数据目录语义，也不修改用户已有配额与插件生产配置。

## 当前行为与目标行为

当前 `KeepRunning` 设置会隐含开启登录启动，`StartupManager` 把 Run 命令切换到 `--watchdog`，主程序还会维持一个同进程二进制的监督实例。该监督实例与主程序可能同时退出，无法提供用户期望的系统级恢复。

目标行为只有两种：启用登录启动时 Run 值为带引号的当前 `ZGSTokenBar.exe` 路径，禁用时移除同名 Run 值。应用不再识别或生成 `--watchdog`，设置界面不再显示“常驻运行”。旧 `settings.json` 中的 `keepRunning` 作为未知兼容字段被忽略；下一次正常保存设置时自然消失，不为迁移而改写生产设置。

## 范围、约束与恢复

- 删除 `WatchdogManager.cs`，移除程序入口、应用上下文、计时器、退出流程和本地化中的 watchdog/常驻逻辑。
- `StartupManager` 保留现有幂等 Run 键协调，只接受 `openAtLogin`；首次运行新构建时将旧的 `--watchdog` 命令原位迁移为普通启动命令。
- 从 `AppSettings` 删除 `KeepRunning`。默认 JSON 反序列化继续忽略旧字段，其他设置值和格式保持兼容。
- 更新公开说明、生命周期静态契约和 .NET 回归测试；历史规格保留为当时实现记录，本规格明确取代其 watchdog 结论。
- 本机部署仍使用既有 `release/ZGSTokenBar-v3.0.0/ZGSTokenBar.exe`。用户从该文件手动固定到开始屏幕。
- 回滚可恢复本次源代码并重新构建；本机回滚时可取消固定开始屏幕入口并恢复旧构建。任何回滚都不得覆盖用户生产设置。

## 验证

- `node --test scripts/native-application-lifecycle.test.mjs scripts/native-localization.test.mjs`：无 watchdog 引用，登录启动仍在单实例判定前幂等协调，自定义数据目录仍不触碰全局启动项。
- `dotnet run --project tests/ZGSTokenBar.Tests/ZGSTokenBar.Tests.csproj -c Release`：普通启动命令、幂等设置/删除和旧 `keepRunning` 字段兼容通过。
- `npm run verify`：仓库完整门禁退出码为 0。
- 本机部署后复读进程和 Run 值：仅一个普通应用进程，无 `--watchdog`；Run 值无该参数。用户确认已将部署目录中的可执行文件固定到开始屏幕。

## 验收标准

1. 源码、测试和当前构建不包含 watchdog 入口、监督计时器、停止事件或“常驻运行”设置。
2. 登录启动关闭时无 Run 值；开启时 Run 值只包含带引号的当前可执行路径，且相同值不重复写入。
3. 旧配置中的 `keepRunning` 不阻止加载，不会重新启用监督；其他配置原样保留。
4. 自定义数据目录运行不读取或修改全局 Run 注册。
5. 当前用户可从开始屏幕手动启动已部署的 ZGSTokenBar 便携版。
6. 聚焦测试、完整门禁、本机部署复核和完整差异审查全部通过；无无关代码改动，生产配置除正常保存时移除已废弃字段外不应发生有意迁移。

## 实现结果

`WatchdogManager.cs`、`--watchdog` 入口、监督计时器、停止事件、退出时拉起逻辑和“常驻运行”设置已删除。登录启动继续在单实例判定前幂等协调，仅生成带引号的普通可执行路径；旧 `keepRunning` JSON 字段由兼容反序列化忽略，其他设置正常加载。

验证与部署证据：

- 聚焦 Node 契约 21/21 通过，.NET 回归测试 109/109 通过。
- `npm run verify` 通过 104 个 Node 契约、109 个 .NET 测试、Native HWND 生命周期、单文件发布、NativeAOT CLI、隔离插件验收、确定性截图和差异检查，退出码为 0。
- `npm run dist` 已部署到 `D:\projects\ZGSTokenBar\release\ZGSTokenBar-v3.0.0\ZGSTokenBar.exe`；SHA-256 为 `0BD6F2B231BD8975E403ACC7CF1DF56562CA88B2E7107F6ACFD8BD7910238E25`，便携 ZIP SHA-256 为 `385C0D034114737421DC047FE1770A997E250779FEE3ABBE88CE9A92C54F5AF1`。
- 本机当前只有一个无参数 ZGSTokenBar 进程。Run 值为 `"D:\projects\ZGSTokenBar\release\ZGSTokenBar-v3.0.0\ZGSTokenBar.exe"`，无 watchdog 参数。
- 用户已确认从部署目录手动将 `ZGSTokenBar.exe` 固定到开始屏幕；无需安装器或 watchdog。
- 新版本启动后的正常设置保存移除了生产 `settings.json` 中已废弃的 `keepRunning: true` 行：文件由 23090 字节变为 23066 字节，24 字节差值与该序列化行完全一致；当前 JSON 可解析、`openAtLogin=true`、其他运行行为正常。部署前未保存该文件的逐字节副本，因此不能独立证明完整内容只发生了这一处变化；按生产配置安全规则已保留现状且未自动回退。
- 完整代码差异审查未发现无关改动。部署临时备份已移入回收站；源代码尚未提交或公开发布。
