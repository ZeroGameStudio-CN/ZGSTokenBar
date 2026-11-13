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
- 用户对 V2 的评价：「还行」。随后要求 V3：去掉会话 tok/s 的「≈」；第二行展示首 token、缓存率、总 token。
- V3 之后用户先说「感觉不需要两行」，后又改回需要两行。**V4 定稿布局**（仍不 merge / 不安装）：
  - 第 1 行：agent instance 短芯片 · 短标题（ellipsis）· 右侧突出的 tok/s。
  - 第 2 行：model/effort · 首 token · 缓存 · 总 token（图标标注，列对齐）。
  - 排序：先 `running`，再按速度降序。
  - 最多约 10 条会话。
  - 「总 token」仍是**当前轮**（input+output），不是会话一生累计。

### 「已追平」原逻辑（已从 UI 移除，只记录在此）

页脚读的是 `snapshot.LiveRate.Pending`（`LiveOutputRate.Pending` ← `CodexLiveRateFeed.Pending` ← `CodexConversationUsageReader.Refresh` 的 `PendingSessions`）。

递增条件：

1. 某会话日志还没追到文件末尾，本轮又把 `RefreshColdBudgetBytes`（默认 4 MiB）用尽；
2. 扫描后 `state.Offset < length`（受 `ScanBudgetBytes` 8 MiB 限制）；
3. 读写失败且 `CaughtUp == false`。

`CaughtUp` = `state.Offset >= length`（解析到 EOF）。页脚原文：`live == null` →「待读取」；`Pending > 0` →「N 条待追平」；否则「已追平」。「N 个 Codex 会话」是已追上、未失败的活会话数（`LiveOutputRate.Sessions`），不是历史会话总数。这是日志追平状态，**不是**用量统计，用户要求去掉。

### 会话行数据来源（V4）

- **Agent instance 芯片**：当前**没有**「Claude / Codex / 某客户端实例 ID」这种按行字段。速率面板的会话列表只来自 `BarForm.LiveConversationRows` → `_codexTokenUsage.ConversationTurns`（Codex 会话日志）；Claude 等其它 agent 不会出现在此列表。`CodexConversationTurn` 上也没有 client/instance/account 字段（最接近的是 `SessionId` 和来自 cwd 末段的 `ProjectName`，都不是 agent 实例）。因此芯片固定画 **「Codex」**，表示数据源是 Codex 客户端。以后若接入其它 agent 的活会话，需要新增来源字段，不能从现有字段猜。
- **短标题**：`CodexTurnName` = `Title`（`CodexConversationTitleReader` 按 SessionId 补上的会话标题），没有标题时退回 `ProjectName · SessionId` 末 8 位。过长 ellipsis。
- **model/effort**：`turn.Model`、`turn.Effort`，来自会话日志 `turn_context`（及同轮写入的模型/强度）。显示为 `gpt-5.5 · high`，缺则「—」。
- **首 token**：延迟（秒），不是 tok/s。只采 `event_msg` 的 `task_complete` / `turn_aborted` 载荷字段 `time_to_first_token_ms`（有限、≥0、且不超过该轮时长）再 `/ 1000`，写入 `FirstTokenSeconds`。缺失不估算（不用第一次 `token_count` 时间戳）。
- **缓存**：`CacheHitPercent` = `CachedInputTokens / InputTokens`。
- **总 token**：该轮 `InputTokens + OutputTokens`（缓存是输入的子集）。**不是**整个 session 的历史总和。
- **速度**：3 秒窗口 `live.Total`，不再加「≈」。估算中仍用琥珀色数字。历史表 `CodexTurnLiveSpeed` 未改。
- **排序**：`State == "running"` 优先，然后 `LiveRate.Total` 降序，再 `ObservedAt` 降序。筛选仍是「有活速率或 running」。

## 当前状态（2026-11-13 14:10 北京时间）

- 分支已推远程：`origin/feat/panel-activity-unify`。
- V1：`c4d471a` Preview: compact 12-week token activity on the live-rate panel.
- 交接文档初稿：`5a5efa3` Docs: handoff for the panel-activity-unify preview branch.
- V2：`d4419a5` Preview: widen the rate panel and use a five-stat activity row.
- V3：`6fea030` Preview: session rows show first-token latency, cache, and total tokens.
- **V4 代码已落地、测试工程 Release 编译 0 警告 0 错误，离屏 PNG 已出。本提交含 V4 代码与本文档更新。未 merge、未安装。**
- 已安装的仍是 PR #47 构建：`D:\projects\ZGSTokenBar\release\ZGSTokenBar-v3.0.0\ZGSTokenBar.exe`。不要动它。

### V2 / V3 仍有效的实现要点

- `LiveRateBodyWidth = 640`；两栏：左约 300，右约 304。系统用量仍是 `LogicalBodyWidth = 300`。离屏速率面板 960×702（144 DPI）。
- `TokenActivityView.TodayTokens` 与热力图同一日序列。页脚废话已删。
- 无会话时右侧留空（可后续再收窄，尚未做）。

### V4 实现要点

- `MaximumSessionRows = 10`；行高仍 34（两行）。
- 第 1 行：固定宽「Codex」芯片 + 短标题 ellipsis + 右对齐 tok/s。
- 第 2 行：左 `Model · Effort`，右三列图标（时钟 / 缓存柱 / 代币柱）对齐。
- 排序改为 running-first，再速度降序。fixture 里两条已结束、速度更高的会话应排在所有 running 之后（用于演示排序）。

### Mockup 路径（box）

对照（现网 / 旧稿，仅对照）：

- `/workspace/panel-redesign/refs/rate-panel-current.png`
- `/workspace/panel-redesign/v2-rate-panel-daily.png`
- `/workspace/panel-redesign/v3-rate-panel-daily.png`

V4（请给用户看这批）：

- `/workspace/panel-redesign/v4-rate-panel-daily.png`（中文，每日 + 10 条会话）
- `/workspace/panel-redesign/v4-rate-panel-weekly.png`（中文，每周）
- `/workspace/panel-redesign/v4-rate-panel-daily-en.png`
- `/workspace/panel-redesign/v4-rate-panel-weekly-en.png`

源文件在用户机：`C:\Users\2025\AppData\Local\Temp\panel-v4\`。

## 下一步（待用户确认后才做）

1. 用户看 V4 mockup：两行布局、Codex 芯片、model/effort、running-first 排序，是否就这样。
2. 无会话时右侧大空区：是否要在无会话时收窄回 300。
3. Router 四格统计条（排队 / 当前 / 今日完成 / 失败）是否要做；确认前不要改 Router 布局。
4. 若用户要看 Claude / 其它 agent 的活会话，需要先做数据接入（现有 turn 上没有来源字段）。
5. 确认后：再谈 merge / 安装（需用户明确点头）。官方 `npm run verify` 本地跑不了（`global.json` 钉 10.0.113 + `rollForward: disable`，机器是 10.0.115）。**不要提交 rollForward 改动。** 本地预览构建可临时改、用完必须改回 `disable`。

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
- 不要用时间戳去估算缺失的首 token 延迟。
- 不要把 `SessionId` / `ProjectName` 假装成 agent 实例。
- `git pull` `/workspace/zgs-agent-skills` 会因 SSH 到 100.64.0.14 超时而失败；继续用本地副本，可能偏旧。

## 关键文件

- `src/ZGSTokenBar.App/SystemUsagePopoverForm.cs` — 速率面板绘制、两栏、五格统计、会话两行、排序。
- `src/ZGSTokenBar.App/BarForm.cs` — `LiveConversationRows`（目前只接 Codex）。
- `src/ZGSTokenBar.App/UiKit.cs` — `UiPaint.Segmented`、`UiPaint.ActivityHeatmap`、`UiIcons`。
- `src/ZGSTokenBar.Core/TokenActivityCalendar.cs`、`TokenActivityView`。
- `src/ZGSTokenBar.Core/CodexConversationUsageReader.cs` — `FirstTokenSeconds`、`Model`、`Effort`。
- `src/ZGSTokenBar.Core/CodexConversationTitleReader.cs` — 会话标题。
- `src/ZGSTokenBar.Core/CodexTokenUsageReader.cs` — `SpendRetentionDays = 90`、`BuildActivityDays`。
- `tests/ZGSTokenBar.Tests/PanelCaptures.cs` — `--panel-captures`。
- `tests/ZGSTokenBar.Tests/RouterModuleTests.cs` — `CaptureMonitor`。
