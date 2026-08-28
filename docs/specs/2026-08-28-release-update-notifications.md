# 新版本自动检测与更新通知

- 状态：Implemented
- 最后更新：2026-08-28
- 基线：`54221ee8bd704605b1de62f404d8349b3ba73419`，并包含同工作区尚未提交的稳定启动注册改动
- 执行授权：已授权；用户确认继续并要求获得类似桌面应用的新版推送体验
- 提交、公开 Release 与签名凭据配置授权：未请求

## 目标与非目标

应用运行后自动检查 ZGSTokenBar 的 GitHub 最新稳定 Release；发现更高语义版本且发布资产完整时，显示一次 Windows 通知，并在托盘菜单持续显示“更新到 vX”按钮。用户点击后打开该 Release 页面获取已签名安装包。网络或发布源失败不得影响配额栏、启动项或常驻运行。

本次不静默下载或替换正在运行的可执行文件，不自动创建公开 Release，不写入 GitHub 凭据，也不接受草稿、预发布、普通 Git tag、缺少校验文件的发布。真正的无人值守自更新留到签名发布链路实际启用并完成端到端回滚验收后。

## 基线与决策

当前产品版本唯一来自 `Directory.Build.props`，便携包由 `scripts/build-native-portable.ps1` 生成；公开发布规则要求应用与 CLI 通过 Authenticode 签名和时间戳验证。远端仓库为 `ZeroGameStudio-CN/ZGSTokenBar`，截至 2026-08-28 尚无 GitHub Release。

- 使用 GitHub 公开 REST `releases/latest` 接口，无需用户令牌；只接受 `vMAJOR.MINOR.PATCH`，并要求同版本的 `ZGSTokenBar-Portable-vX.zip` 与 `ZGSTokenBar-vX-SHA256.txt` 两项资产。
- 应用显示后立即检查一次，之后每 6 小时检查一次。该间隔是可逆的代理选择：足够及时，同时避免频繁请求公开 API；每个运行进程对同一版本最多通知一次。
- API 请求超时 10 秒，响应上限 256 KiB，设置明确的 `User-Agent`、GitHub JSON Accept 与 API 版本头。404、限流、超时、超大或畸形响应均静默降级。
- 更新入口位于托盘菜单，只有发现有效新版时才显示；通知点击与菜单点击均打开经过主机和 HTTPS 校验的 GitHub Release 页面。
- 新增 tag 触发的 GitHub Actions 发布工作流：先运行完整门禁，再从仓库 Secrets 解码 PFX，强制签名并打包，最后使用仓库 `GITHUB_TOKEN` 创建 Release 和上传 ZIP、校验文件。缺少签名 Secrets 必须失败关闭，绝不发布未签名公开包。

## 范围、状态流与恢复

新增 Core 更新查询服务与解析模型；`QuotaApplicationContext` 持有服务、并发门、6 小时计时器、隐藏的托盘菜单项和当前可用版本。查询在 UI 初始化后异步运行，成功发现新版后更新菜单并发一次通知。语言变化时重新生成菜单文案；退出时取消查询并释放服务、计时器和并发门。

新增 `.github/workflows/release.yml`，只在 `v*` tag 上运行，并验证 tag 与 `Directory.Build.props` 版本一致。工作流所需 Secrets 为 `WINDOWS_SIGNING_CERTIFICATE_BASE64` 和 `WINDOWS_SIGNING_CERTIFICATE_PASSWORD`。未配置时不会影响普通 CI，只会阻止发布任务。

删除代码即可回滚应用侧功能；删除发布工作流即可回滚流水线。查询失败无需恢复状态，因为不持久化更新结果。既有启动注册、设置 JSON、配额缓存与 watchdog 不变。

## 验证与验收

验证命令：聚焦 Node 生命周期/本地化契约、.NET 合成 HTTP 测试、工作流静态契约，以及完整 `npm run verify`。

1. 当前版本 3.0.0 收到有效 v3.0.1 Release 和两项精确资产时返回新版；同版、旧版、404 均不提示。
2. 非法 tag、非 HTTPS 或非 GitHub 页面、缺任一资产、超大或畸形 JSON 均失败关闭。
3. 应用显示后自动检查，并以 6 小时周期继续；同一进程同一版本只弹一次，托盘更新按钮持续可用。
4. 更新通知和按钮全部使用中英文文本；语言切换后菜单文案更新。
5. 查询异常和应用退出竞态不影响主界面或关闭流程，所有新增资源均被释放。
6. 发布工作流校验 tag/版本、运行完整门禁、要求 PFX Secrets、设置 `ZTB_REQUIRE_SIGNATURE=1`，并只上传匹配的 ZIP 与 SHA-256 文件。
7. 不实现静默替换，不修改生产设置格式，不触碰 `StartupApproved`，不发布未签名资产。
8. 完整 `npm run verify` 退出码为 0，任务差异无无关改动。

## 实现结果

应用现会在界面初始化后异步检查 GitHub 最新稳定 Release，并每 6 小时复查；只有更高的三段式版本同时具备精确命名的便携 ZIP 与 SHA-256 文件时，才显示一次 Windows 通知和持续可用的托盘“更新到 vX”入口。更新页及资产 URL 均限制为项目在 GitHub 上的 HTTPS Release 路径，网络、限流、超时、过大或异常响应均不会影响主程序。

已新增 tag 驱动的签名发布工作流：tag 必须匹配产品版本，完整门禁必须通过，且两个签名 Secrets 均存在，才会生成并发布 ZIP 与校验文件。当前未配置或使用签名凭据，也未创建公开 Release；因此代码已就绪，但真实新版通知要从首个合规签名 Release 发布后才会出现。本阶段不包含静默下载或进程内自替换。

验证证据：`node --test scripts/native-application-lifecycle.test.mjs scripts/native-localization.test.mjs scripts/public-ci-contract.test.mjs` 为 17/17；`.NET` 合成更新响应及回归测试为 98/98；完整 `npm run verify` 通过 94 个 Node 契约、98 个 .NET 契约、窗口生命周期、发布、插件验收、确定性截图及差异检查，退出码为 0。`npm run dist` 随后完成本机自用部署，应用与 watchdog 已从既有 v3.0.0 路径重新启动，启动项仍精确指向该路径；应用 SHA-256 为 `83812F4C28E871798BECA74A3B5D5432E96730829C4E0DF14C0FA13DCE60568C`。部署备份在复核成功后已移入回收站。源代码尚未提交、推送或公开发布。
