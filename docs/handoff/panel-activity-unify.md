# 交接：速率 / Router 面板统一与用量活动

分支：`feat/panel-activity-unify`（基于 `main` `8b81351`）  
仓库：`ZeroGameStudio-CN/ZGSTokenBar`  
本机：`D:\projects\ZGSTokenBar`（用户电脑，不要改用云端 agent 当主工作区）  
**在用户确认之前，不要合入 main，不要重装/覆盖正在使用的 `release\ZGSTokenBar-v3.0.0\`。**

## 目标

优化「实时速率」面板：补上周/日用量与均值、总量，以及比 Codex「Token activity」年历小得多的活动热力图（约 8–12 周）；同时把 Router 面板与速率面板的视觉语言统一。先出方案和离屏稿，确认后再合入安装。

## 已确认的用户决定

- 热力图：最近 **12 周**、7 行、**周一起始**；**每日 / 每周**切换（不要一年）。
- 统计行五格，顺序：**本周 / 周均 / 本日 / 日均 / 总量**。
- 面板**横向加宽**（合理处用双栏），让「活跃会话」能显示更多条、标题更长。
- 去掉页脚空话：
  - 「已追平 / N 条待追平 / 待读取」
  - 「流量仅在 Bar 运行时计入」
  - 「N 个 Codex 会话」
- 去掉近 24 小时按小时曲线，以及原「累计」卡里与总量/今日重复的 Token 列（整张累计卡拆掉）。流量累计并进「本机网络」；缓存率也放在网络卡。
- Router：已与速率面板共用 UiKit 石墨卡片。大改与「排队 / 当前 / 今日完成 / 失败」四格统计**等用户确认后再做**（本期只出当前离屏对照图）。
- 为保住 12 周按日数据，分支里将 `SpendRetentionDays` 从 32 调到 **90**（`SpendHistory` 对外仍是 30 天窗口；活动网格走单独的 `ActivityDays`）。

## 「已追平」原意（已按用户要求从图上撤掉）

页脚文案读的是 `snapshot.LiveRate.Pending`（`LiveOutputRate.Pending`），来源是 `CodexConversationUsageReader.Refresh` 里的 `PendingSessions`，再经 `CodexLiveRateFeed.Pending` 传入。

`PendingSessions` 在下列情况 +1：

1. 该 session 日志还不算已追平（尚未成功读到文件末尾），且本轮冷扫描预算 `RefreshColdBudgetBytes`（默认 4 MiB）已经用完，留给下一轮刷新继续扫；
2. 本轮扫描后仍是 `state.Offset < length`（受 `ScanBudgetBytes` 8 MiB 限制，文件还有未读字节）；
3. 读文件 IO/权限失败，且该文件当时 `CaughtUp == false`。

`CaughtUp` 在扫描后设为 `state.Offset >= length`，表示这个 session 日志已经读到末尾。

页脚逻辑：`live == null` 显示「待读取」；`Pending > 0` 显示「N 条待追平」；否则显示「已追平」。旁边的「N 个 Codex 会话」是 `LiveOutputRate.Sessions`（已追平且未读失败的 live session 数），不是账本里的历史会话总数。

这与用量对账无关，只表示会话日志有没有扫完，所以从面板上撤掉。

## 当前状态

- 分支已从 `c4d471a`（v1：12 周热力图 + 四格统计 + 去掉 24h 曲线/累计卡）继续做 **v2**：五格统计、加宽双栏、去掉页脚空话。
- v1 离屏图（144 DPI 夹具，非实机窗口）：
  - `/workspace/panel-redesign/rate-panel-daily.png`
  - `/workspace/panel-redesign/rate-panel-weekly.png`
  - `/workspace/panel-redesign/router-panel-current.png`
  - `/workspace/panel-redesign/rate-panel-current.png`（用户提供的旧面板对照）
- v2 目标图：`/workspace/panel-redesign/v2-rate-panel-daily.png`、`v2-rate-panel-weekly.png`（尚未或刚生成，以仓库外该目录为准）。
- 未合入、未重装。运行中的仍是此前安装的版本。
- 技能仓库 `zgs-agent-skills` 经 `github-skills` / `100.64.0.14` 的 `git pull` 可能超时，规则按本地副本，未必最新。

## 数据与限制（只读元数据，不要把真实用量写进文档或日志）

- 热力图数据：`%APPDATA%\ZGSTokenBar\codex-token-usage-index.json` 各 session 的 `dailyModelUsage`，按本地日汇总 input+output+unattributed。
- 调查时（2026-11-13）索引里约 104 个有数据的自然日（2026-08-02 至 2026-11-13），近 12 周几乎无缺口。
- `总量` 用 `LocalTokens`（含 2026-09-06 历史基线），与按日序列不是同一套账，可能对不齐。
- 「本日」与热力图当日格使用同一套按日序列（不要和旧的 `LatestDayTokens` 混用，以免对不齐）。
- 日均 = 窗口内截至今日的总量 / 已过天数；周均 = 窗口总量 / 12；本周 = 本周一至今日。
- 旧 24h 曲线来自最近 500 条 conversation turns，覆盖往往远不足 24h。网络累计自 2026-10-30 起、仅 Bar 运行时计入，没有按日序列。
- `quota-rate-history.json` 是配额百分比采样，不是 token 日用量。

## 隐私

- 禁止移动用户鼠标，禁止截取可能含私人窗口的屏幕。
- 校验只允许：条窗口自身矩形，或测试夹具的离屏渲染。
- 禁止打印、复制、把真实 token / 会话标题写进提交、文档或报告（夹具请用合成标题）。
- 查本地账本只读元数据（文件名、schema、日期范围、条数），不要导出用量数值。

## 构建 / 离屏渲染 / 校验

本机 SDK 是 `10.0.115`，`global.json` 钉死 `10.0.113` + `rollForward: disable`。**不要把 rollForward 改动提交进仓库。** 本地跑 dotnet 时仅可临时改成 `latestFeature`，命令结束后必须改回 `disable`。官方门禁是 CI（Windows，10.0.113）的 `npm run verify`。

离屏渲染（WinForms 夹具，不碰实机窗口）：

```text
# 在仓库根，先确认 global.json 仍是 disable，再临时改为 latestFeature
$env:MSBUILDDISABLENODEREUSE = '1'
dotnet run --project tests\ZGSTokenBar.Tests\ZGSTokenBar.Tests.csproj -c Release -- --panel-captures <dir>
# 再把 global.json 改回 rollForward: disable
```

关键输出：

- `panel-rates-activity-daily-zh-CN.png`
- `panel-rates-activity-weekly-zh-CN.png`
- `panel-router-current.png`

拷到盒子：`/workspace/panel-redesign/v2-rate-panel-daily.png`、`v2-rate-panel-weekly.png`。

系统用量面板宽度必须仍是 `LogicalBodyWidth`（300），`TestSystemUsagePopoverPresentation` 按此断言。加宽只作用于实时速率模式。`LiveMetricTests` 要求无会话时速率面板高度 `<= 540`。

`pwsh`（PowerShell 7）是本机脚本外壳。不要覆盖 AppData 设置/缓存。

## 下一步

1. 做完并提交 v2：五格统计、双栏加宽、去掉页脚空话；重渲 v2 PNG。
2. 等用户看稿。确认后再考虑 Router 四格统计与其它美化。
3. **用户明确同意后**才合入 main、重建便携包、备份旧 exe、重装到 `D:\projects\ZGSTokenBar\release\ZGSTokenBar-v3.0.0\` 并做条窗口校验。
4. 每做完一块有意义的改动，更新本文件并提交；把分支推到 `origin`，避免只留在本机。

## 主要改动文件

- `src/ZGSTokenBar.Core/TokenActivityCalendar.cs`
- `src/ZGSTokenBar.Core/CodexTokenUsageReader.cs`（`SpendRetentionDays = 90`，`ActivityDays`）
- `src/ZGSTokenBar.App/UiKit.cs`（热力图 / 分段开关）
- `src/ZGSTokenBar.App/SystemUsagePopoverForm.cs`
- `src/ZGSTokenBar.App/BarForm.cs`（接线）
- `tests/ZGSTokenBar.Tests/PanelCaptures.cs`
- `tests/ZGSTokenBar.Tests/RouterModuleTests.cs`（`CaptureMonitor`）
