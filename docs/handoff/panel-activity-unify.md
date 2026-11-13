# 面板活动统一（feat/panel-activity-unify）交接

> 分支：`feat/panel-activity-unify`（基于 `main` `8b81351`）
> 仓库：`D:\projects\ZGSTokenBar`（远程 `ZeroGameStudio-CN/ZGSTokenBar`）
> **未确认前禁止 merge、禁止安装、禁止动正在运行的 Bar、禁止改 AppData。**

## 目标

Phase 1：检查现有数据，给出一套统一设计，用真实 WinForms 离屏渲染 mockup。实时速率面板增加「类似 Codex、但范围更小」的用量活动，并顺带统一 Router 面板视觉。用户确认前不出货。

## 已确认的用户决定

- 活动图：12 周、周一为一周之始；「每日 / 每周」切换（每周模式隐藏「一二三四五六日」标签，改画周柱）。
- 统计行五格，顺序固定：**本周 / 周均 / 本日 / 日均 / 总量**。
  - 本周 = 本周一至今天（与热力图同一日序列）。
  - 周均 = 窗口总和 / 12。
  - 本日 = 热力图当天格子的同一日序列，**不要**用 `LatestDayTokens` / 内容里旧的 `TodayTokens` 字段。
  - 日均 = 窗口总和（截至今）/ 窗口内已过天数。
  - 总量 = `LocalTokens`（含 2026-09-06 历史基线，不一定等于格子之和）。
- 实时速率面板可横向加宽（两栏），让「活跃会话」能显示更多会话、更长标题。系统用量面板的 `LogicalBodyWidth = 300` **不得**改动。
- 去掉页脚废话：含「已追平 / N 条待追平 / 待读取」、「N 个 Codex 会话」、「流量仅在 Bar 运行时计入」。时间戳也不再单独放页脚。
- 去掉 24 小时曲线和原「累计」Token 卡；网络累计并入「本机网络」卡。
- Router：保持现有 Graphite 卡片风格；**建议的四格统计条（排队 / 当前 / 今日完成 / 失败）仍待用户确认**，尚未改代码。
- `SpendRetentionDays`：32 → **90**（保证 12 周格子刷新后还在）。`SpendHistory` API 仍只暴露 30 天；活动图走单独的 `ActivityDays`（最多 84 天）。

### 「已追平」原逻辑（已从 UI 移除，只记录在此）

页脚读的是 `snapshot.LiveRate.Pending`（`LiveOutputRate.Pending` ← `CodexLiveRateFeed.Pending` ← `CodexConversationUsageReader.Refresh` 的 `PendingSessions`）。

递增条件：

1. 某会话日志还没追到文件末尾，本轮又把 `RefreshColdBudgetBytes`（默认 4 MiB）用尽；
2. 扫描后 `state.Offset < length`（受 `ScanBudgetBytes` 8 MiB 限制）；
3. 读写失败且 `CaughtUp == false`。

`CaughtUp` = `state.Offset >= length`（解析到 EOF）。页脚原文：`live == null` →「待读取」；`Pending > 0` →「N 条待追平」；否则「已追平」。「N 个 Codex 会话」是已追上、未失败的活会话数（`LiveOutputRate.Sessions`），不是历史会话总数。这是日志追平状态，**不是**用量统计，用户要求去掉。

## 当前状态（2026-11-13 13:55 北京时间）

- 分支已推远程：`origin/feat/panel-activity-unify`。
- V1 已提交：`c4d471a` Preview: compact 12-week token activity on the live-rate panel.
- 交接文档已提交并推送：`5a5efa3` Docs: handoff for the panel-activity-unify preview branch.
- **V2 代码已落地、测试工程 Release 编译 0 警告 0 错误，离屏 PNG 已出。本提交含 V2 代码与本文档更新。未 merge、未安装。**
- 已安装的仍是 PR #47 构建：`D:\projects\ZGSTokenBar\release\ZGSTokenBar-v3.0.0\ZGSTokenBar.exe`。不要动它。

### V2 实现要点

- `SystemUsagePopoverForm.LiveRateBodyWidth = 640`（约 1.5× 系统用量宽）；两栏：左约 300（速率 + 活动 + 网络），右约 304（活跃会话，最多 8 行）。
- 系统用量仍用 `LogicalBodyWidth = 300`。测试：`TestSystemUsagePopoverPresentation`；离屏系统面板 450×540（144 DPI），速率面板 960×702。
- `LiveRateBodyHeight = 296`，高度不再叠会话区（会话改到右侧）。无会话时右侧留空（可后续再收窄，尚未做）。
- `TokenActivityView` 增加 `TodayTokens`（`FromDailyTotals` 从同一 `byDay` 取值）。
- 页脚废话已删除。
- 离屏 fixture 用合成会话标题，没有真实会话名。

### Mockup 路径（box）

对照（现网样式，未改）：

- `/workspace/panel-redesign/refs/rate-panel-current.png`
- `/workspace/panel-redesign/refs/codex-heatmap-ref.png`
- `/workspace/panel-redesign/rate-panel-current.png`

V1（四格统计、窄面板，仅对照）：

- `/workspace/panel-redesign/rate-panel-daily.png`
- `/workspace/panel-redesign/rate-panel-weekly.png`
- `/workspace/panel-redesign/router-panel-current.png`

V2（请给用户看这批）：

- `/workspace/panel-redesign/v2-rate-panel-daily.png`（中文，每日 + 8 条会话）
- `/workspace/panel-redesign/v2-rate-panel-weekly.png`（中文，每周）
- `/workspace/panel-redesign/v2-rate-panel-daily-en.png`
- `/workspace/panel-redesign/v2-rate-panel-weekly-en.png`
- `/workspace/panel-redesign/v2-rate-panel-no-sessions.png`（无会话，右侧留空）
- `/workspace/panel-redesign/v2-router-panel-current.png`（Router 现态，尚未按建议改）

源文件在用户机：`C:\Users\2025\AppData\Local\Temp\panel-v2\`。

## 下一步（待用户确认后才做）

1. 用户看 V2 mockup：五格统计、两栏宽度、8 行会话、去页脚，是否就这样。
2. 无会话时右侧大空区：是否要在无会话时收窄回 300。
3. Router 四格统计条（排队 / 当前 / 今日完成 / 失败）是否要做；确认前不要改 Router 布局。
4. 确认后：再谈 merge / 安装（需用户明确点头）。官方 `npm run verify` 本地跑不了（`global.json` 钉 10.0.113 + `rollForward: disable`，机器是 10.0.115）。**不要提交 rollForward 改动。** 本地预览构建可临时改、用完必须改回 `disable`。

## 构建 / 渲染 / 校验命令

均在用户 Windows 电脑 `D:\projects\ZGSTokenBar` 上执行。先 `vcvars64.bat`，然后把 System32 / Windows / nodejs / dotnet / pwsh / Git\cmd 接到 PATH 前面。`MSBUILDDISABLENODEREUSE=1`。`dotnet build-server shutdown` 必须在仓库目录**之外**做。PowerShell 用 7。不要覆盖 AppData。

临时允许 SDK 滚动（用完改回）：

```text
global.json 里 rollForward: disable → latestFeature
dotnet build tests\ZGSTokenBar.Tests\ZGSTokenBar.Tests.csproj -c Release
改回 rollForward: disable
```

离屏出图（可直接跑已编译的测试 exe，避开 SDK 钉死）：

```text
tests\ZGSTokenBar.Tests\bin\x64\Release\net10.0-windows\win-x64\ZGSTokenBar.Tests.exe --panel-captures <DIR>
```

不要跑会改安装目录或覆盖 AppData 的脚本。不要移动鼠标、不要截正在运行的完整桌面（只允许 Bar 矩形或离屏渲染）。

安装路径（确认前不要动）：`D:\projects\ZGSTokenBar\release\ZGSTokenBar-v3.0.0\ZGSTokenBar.exe`。
PR #47 备份：`release\local-backups\PRE-PR47-CLOCKWISE-20261112-172737`。

## 隐私规则

- 永不移动鼠标。
- 只截 Bar 矩形或离屏渲染，禁止整桌面/其它窗口。
- 提交、文档、汇报里禁止打印或复制真实 token、真实会话标题；只用合成 fixture 标题。
- AppData 只读元数据（路径、schema、日期范围、条目数），不转储用量数值。
- Codex 主目录：`C:\Users\2025\.codex`。索引：`%APPDATA%\ZGSTokenBar\codex-token-usage-index.json`（约 15 MB，schema 9）。

## 不要做的事

- **用户确认前不要 merge、不要安装。**
- 不要改 `global.json` 的 `rollForward` 并提交。
- 不要把 `SpendHistory` 从 30 天改成 90；90 天只给活动图用的 `ActivityDays`。
- 不要改系统用量面板的 300 逻辑宽度。
- 不要提交或汇报真实 token / 真实会话标题。
- `git pull` `/workspace/zgs-agent-skills` 会因 SSH 到 100.64.0.14 超时而失败；继续用本地副本，可能偏旧。

## 关键文件

- `src/ZGSTokenBar.App/SystemUsagePopoverForm.cs` — 速率面板绘制、两栏、五格统计。
- `src/ZGSTokenBar.App/UiKit.cs` — `UiPaint.Segmented`、`UiPaint.ActivityHeatmap`。
- `src/ZGSTokenBar.Core/TokenActivityCalendar.cs`、`TokenActivityView`。
- `src/ZGSTokenBar.Core/CodexTokenUsageReader.cs` — `SpendRetentionDays = 90`、`BuildActivityDays`。
- `src/ZGSTokenBar.App/BarForm.cs` — `Activity` 从 `usage.ActivityDays` 接入。
- `tests/ZGSTokenBar.Tests/PanelCaptures.cs` — `--panel-captures`。
- `tests/ZGSTokenBar.Tests/RouterModuleTests.cs` — `CaptureMonitor`。
