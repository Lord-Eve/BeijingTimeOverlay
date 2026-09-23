# 北京时间浮窗

一个不修改 Windows 系统时区的独立北京时间显示工具。

当前版本：`1.0.0`

## 设计

- 始终用 `DateTimeOffset.UtcNow` 作为时间源。
- 用 Windows 的 `China Standard Time` 转换到北京时间，不手动加减小时。
- 默认显示两行：24 小时制 `HH:mm:ss` 和年在前的 `yyyy/M/d`，尺寸和颜色贴近 Windows 右下角时钟。
- 默认放在整块屏幕的右下角，因此可以覆盖任务栏系统时钟。
- 无边框、无任务栏按钮、默认置顶；任务栏交互改变层级后会自动重新置顶。
- 不联网、不请求定位、不需要管理员权限、不修改系统时间或时区。
- 支持拖动、位置记忆、显示/隐藏、始终置顶、鼠标穿透、全屏应用时自动隐藏、移动到当前屏幕右下角和可选开机启动。

## 运行

日常使用请从 [GitHub Releases](https://github.com/Lord-Eve/BeijingTimeOverlay/releases) 下载并解压 Windows 程序包，然后双击 `BeijingTimeOverlay.exe`；不需要打开 PowerShell。

如果使用源码，需要 Windows 和 .NET 8 SDK。先在项目目录构建一次：

```powershell
dotnet build .\北京时间浮窗.csproj -c Release
```

之后双击 `启动北京时间浮窗.cmd` 即可直接启动 GUI 程序，不会把终端留在后台。脚本优先使用 `bin\Release\net8.0-windows` 下的构建版本。

发布版本：

```powershell
dotnet publish .\北京时间浮窗.csproj -c Release -r win-x64 --self-contained false -o .\发布
```

可双击 `启动北京时间浮窗.cmd`。若已有构建产物，它会直接启动；否则会用 .NET SDK 从源码运行。也可以在发布后直接双击 `发布\BeijingTimeOverlay.exe`。
再次启动不会创建第二个实例，而是唤醒并恢复已有浮窗。

首次运行后，右键托盘图标可以管理窗口。窗口位置和开关保存在：

`%APPDATA%\BeijingTimeOverlay\settings.json`

默认不启用全屏自动隐藏。勾选托盘菜单中的“全屏应用时自动隐藏”后，当同一显示器上的前台窗口覆盖整个屏幕时，浮窗会暂时隐藏；退出全屏后自动恢复。此检测适用于覆盖整屏的游戏和视频窗口，不按具体程序名称筛选。

## 验收重点

1. 把 Windows 系统时区留在美国时区，观察浮窗仍显示北京时间。
2. 等待秒数变化，确认显示来自 UTC 转换而不是固定加八小时。
3. 拖动窗口、重启程序，确认位置被记住。
4. 点击“鼠标穿透”后，确认任务栏底层区域可以继续接收点击；需要移动窗口时从托盘菜单关闭穿透。
5. 勾选“全屏应用时自动隐藏”，打开同一显示器上的全屏游戏或视频后确认浮窗隐藏，退出全屏后确认浮窗恢复；取消勾选后确认行为停止。
