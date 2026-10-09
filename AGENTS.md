# AGENTS.md

给在本仓库工作的 AI 编程助手（Claude Code、Codex 等）的约定。面向用户的说明见 [README.md](README.md)。

## 项目概况

- Windows 桌面北京时间浮窗，C# WinForms，目标框架 `net8.0-windows`。
- 主要逻辑在 `ClockForm.cs`（窗口、置顶、全屏检测、托盘菜单），入口在 `Program.cs`，设置读写在 `OverlaySettings.cs`，开机启动在 `StartupManager.cs`。
- 大量使用 Win32 互操作（`SetWindowPos`、任务栏 owner、`SetWinEventHook` 等）。改动置顶或窗口生命周期时，注意句柄重建（例如切换鼠标穿透会触发 `RecreateHandle`）和 WinForms 自己对原生 owner 的修改。

## 构建与验证

- Windows：`dotnet build .\北京时间浮窗.csproj -c Release`
- 非 Windows 环境只能编译、不能运行：`dotnet build 北京时间浮窗.csproj -c Release -p:EnableWindowsTargeting=true -p:TreatWarningsAsErrors=true`
- 自动化回归项目在 `tests/BeijingTimeOverlay.Tests`：运行 `dotnet run --project .\tests\BeijingTimeOverlay.Tests -c Release` 检查缩放、日期排版和位置计算；加 `-- --live-ui` 可在独立非输入桌面检查当前激活屏幕的 DPI 与窗口生命周期，不改变显示模式或用户设置。
- 隔离桌面回归不能代替真实 Win+P 切屏、任务栏点击、全屏游戏/视频或重启登录验收。涉及窗口行为的改动仍需在真实 Windows 桌面上按 README 的"验收重点"手工验证；在汇报时说明哪些场景没有实际验证过。

## CI

- `.github/workflows/build-check.yml`：所有指向 `main` 的 PR 和推送到 `main` 时运行，以警告视为错误的方式编译 Release、执行基础回归，并检查 `.csproj` 版本在 `CHANGELOG.md` 中有对应段落。
- `.github/workflows/build-windows.yml`：推送 `vMAJOR.MINOR.PATCH` tag 时先执行基础回归，再构建自包含单文件、生成 ZIP 和 SHA-256，并以 `CHANGELOG.md` 对应段落作为说明发布 GitHub Release。

## 发版流程

1. 把 `CHANGELOG.md` 中 `[Unreleased]` 下的内容移到新的 `## [x.y.z] - YYYY-MM-DD` 段落。
2. 同步更新 `CHANGELOG.md` 底部的版本对比链接。
3. 更新 `.csproj` 的 `<Version>` 和 README 中的"当前版本"。
4. 推送对应的 `vx.y.z` tag。已发布的 tag 和 Release 资产不覆盖。

## PR 审查约定

- 本仓库的 PR 由 Codex（`chatgpt-codex-connector[bot]`）自动审查。
- **Codex 有意见时留 review 评论；没有意见时只在 PR 上点 👍（`+1` reaction），不留评论。** 这个 👍 就表示"已审查，无意见"。
- Codex 审查进行中时会在 PR 上点 👀（`eyes` reaction）。只有 👀、还没有 👍 也没有新评论，说明它还在审，不要当成已审完。
- reaction 不会产生 PR 评论或 review 事件。跟进 PR 时，在 CI 通过后和每次检查 PR 状态时主动查询：`gh api repos/Lord-Eve/BeijingTimeOverlay/issues/<PR 号>/reactions`。
- 看到 Codex 的 👍、CI 通过且没有合并冲突、没有未处理的 review 讨论时，直接告诉用户可以合并，不要再说"等 Codex 审查"。
- 合并由仓库所有者决定，AI 助手不要自行合并。

## 文档

- 面向用户的变化写进 `CHANGELOG.md` 的 `[Unreleased]`，用中文，格式参考 Keep a Changelog。
- README 和 CHANGELOG 使用中文；提交信息使用英文。
