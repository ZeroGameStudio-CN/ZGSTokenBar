# 稳定的 Windows 登录启动注册

- 状态：Implemented
- 最后更新：2026-08-28
- 基线：`54221ee8bd704605b1de62f404d8349b3ba73419`
- 执行授权：已授权；用户要求完成本地实现与验证
- 本机部署授权：已授权；用户要求在当前机器处理完成
- 提交与对外发布授权：未请求

## 目标与非目标

让便携版 ZGSTokenBar 的“随 Windows 启动”在应用升级、版本目录变化及已有实例运行时仍指向用户最后启动的新版本，并避免应用每次由 Windows 启动时反复改写正在枚举的 `Run` 项。关闭功能时必须移除注册；“常驻运行”仍隐含启用登录启动并使用 watchdog。

本次不引入安装器、MSIX、计划任务或未公开的 `StartupApproved` 数据格式，也不改变 watchdog 的重启策略、设置格式或发布目录结构。

## 当前行为与目标行为

当前 `StartupManager.Apply` 在 `QuotaApplicationContext` 构造期间无条件写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ZGSTokenBar`。这既发生在 Windows 正从该项启动应用时，也只发生在成功取得单实例互斥量之后。因此，正常登录会重复写注册表，而用户从新版本目录启动时，若旧版本仍在运行，新进程会先退出，启动路径无法迁移。现场证据显示实际注册表已是 v3 路径，但 Windows 启动项枚举仍报告 v2.1.12 路径。

目标行为：主程序在单实例判定前读取设置并协调登录启动注册。协调必须幂等：目标命令未变化时只读不写；路径或模式变化时才更新；禁用时才删除。这样，新版本即使因旧实例仍在运行而退出，也能先把下一次登录的命令迁移到新路径；正常登录则不会改写正在被 Windows 枚举的项。

## 决策、约束与范围

- 继续使用微软公开支持的当前用户 `Run` 键，保持值名 `ZGSTokenBar`，从而保持用户级权限、启动项身份及 Windows 侧启用/禁用选择；不读写 `StartupApproved`。
- 命令始终对可执行路径加引号；普通模式只启动应用，常驻模式追加 `--watchdog`。
- 比较采用注册表字符串的精确比较。仅当期望值与现值不同时调用 `SetValue`，并明确写为 `REG_SZ`。
- 启动注册失败仍不得阻止主程序运行，但应通过 `Trace` 留下包含动作和异常类型的诊断；不得记录凭据或设置内容。
- 单实例判定前只协调注册表，不启动或停止 watchdog；watchdog 生命周期仍由取得主实例后的应用上下文负责。
- 仅修改 `Program.cs`、`StartupManager.cs`、相关生命周期静态契约、.NET 回归测试及本规格。

## 状态流、兼容与恢复

`Program.Main` 对非 watchdog 请求加载现有 `AppSettingsStore`，以 `openAtLogin || keepRunning` 计算目标命令，并调用幂等协调后再竞争主实例互斥量。`QuotaApplicationContext` 在主实例建立后继续应用 watchdog 状态；设置保存后继续同时协调启动注册和 watchdog。

旧版本写入的同名值会在用户首次启动新版本时原位迁移。路径含空格、版本目录变化和普通/常驻模式切换均由完整命令差异触发更新。若注册表暂时不可写，应用继续运行；后续启动或设置保存会再次协调。回滚到旧版本时，旧程序仍能覆盖同一值；不会留下并行启动项。

## 实现与验证

实现顺序：先拆分“仅协调注册表”和“应用完整启动策略”，再把只读/必要写协调前移到单实例判定前，最后补充纯决策测试与静态生命周期约束。

验证命令及通过信号：

- `node --test scripts/native-application-lifecycle.test.mjs`：生命周期契约全部通过。
- `dotnet run --project tests/ZGSTokenBar.Tests/ZGSTokenBar.Tests.csproj -c Release`：命令生成和幂等协调决策测试通过。
- `npm run verify`：仓库毕业门禁退出码为 0。
- 在隔离的临时注册表键或纯内存适配器上验证写入行为；自动化测试不得修改用户真实的生产启动项。

## 验收标准

1. 启用普通启动时，注册命令为带引号的当前可执行路径；启用常驻时额外带 `--watchdog`；两者都关闭时无启动项。
2. 当前注册值与期望命令完全相同时，协调结果不产生注册表写入。
3. 可执行路径或常驻模式变化时，即使已有旧实例导致新进程退出，启动项也在单实例判定前更新为新命令。
4. 禁用启动时移除同名启动项；重复禁用不产生额外写入或错误。
5. 注册表访问失败不阻止应用运行，并产生不含敏感信息的 Trace 诊断；后续运行可重试。
6. 不写入或删除 `StartupApproved`，不新增第二个启动项，不改变用户设置格式和 watchdog 策略。
7. 聚焦测试与完整 `npm run verify` 均通过，完整任务差异不包含无关改动。

## 实现结果

`Program.Main` 现会在单实例互斥量之前加载设置并调用 `StartupManager.ReconcileRegistration`。协调器先读取当前 `Run` 值，再通过纯决策函数选择无操作、设置或删除；命令相同时不写注册表，版本路径或 watchdog 模式变化时才更新。注册失败会记录动作和异常类型，但不会阻断应用。watchdog 启停仍只在主应用上下文中执行。

验证证据：

- `node --test scripts/native-application-lifecycle.test.mjs`：8/8 通过。
- `dotnet run --project tests/ZGSTokenBar.Tests/ZGSTokenBar.Tests.csproj -c Release`：97/97 通过。
- `npm run verify`：92 个 Node 契约、97 个 .NET 契约、窗口生命周期、单文件发布、NativeAOT CLI、隔离插件验收、确定性截图及差异检查全部通过，退出码为 0。
- 本机 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ZGSTokenBar` 已按原值安全刷新并复读验证为 `"D:\projects\ZGSTokenBar\release\ZGSTokenBar-v3.0.0\ZGSTokenBar.exe" --watchdog`。
- `npm run dist` 已把通过门禁的实现部署到该路径；可执行文件 SHA-256 为 `9A4DF133668F194ECE33BC3419E5B487B385CF359D4981AC25BDA09914E662F5`，便携 ZIP SHA-256 为 `437C45B30571072BF3EF5E38E7E991FBECEA89449F8A50E34B14CE96F5DEE6D2`，均与生成的校验文件一致。它是项目支持的未签名自用构建。

所有验收标准均已满足。Windows 的 `Win32_StartupCommand` WMI 视图在当前登录会话仍返回旧 v2.1.12 缓存；该缓存不改变已复读验证的真实 `Run` 值，用户停止了进一步的只读界面核验。源代码尚未提交或对外发布；本机部署前的临时备份在部署与哈希复核成功后已移入回收站。源码回滚可恢复本次四个代码/测试文件和本规格，旧版本也可再次协调同名启动值。
