# ETS2 昼夜切换

Windows x64 桌面工具，支持自定义 Euro Truck Simulator 2 环境时间、快捷时刻和恢复游戏原始环境。

- 深色紫色主题、头像 Logo、自定义圆角窗口栏和过渡动画。
- 拖动时间滑块时预览，松开后应用一次；松开、失焦或捕获中断后停止拖动。
- 独立显示游戏时间与锁定的环境时间。
- 自动检查 GitHub Releases，后台下载并校验更新，点击“重启更新”安装。
- 更新支持取消、缓存复用、安装备份和立即启动失败回退。

## 下载

在 [Releases](https://github.com/Rita6333/ETS21/releases/latest) 下载 `ETS2DayNight-win-x64.exe`。发布版自带 .NET 运行库，无需额外安装 .NET。

启动 ETS2 后运行本工具；首次修改或恢复环境时会请求管理员权限。界面修改的是环境时间，游戏内实际时钟单独读取显示。

## 构建

安装 .NET 8 SDK，在 Windows 中运行：

```powershell
dotnet restore modern_ui/ETS2DayNightModern.csproj --source https://api.nuget.org/v3/index.json
dotnet publish modern_ui/ETS2DayNightModern.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

## 测试与发布

`tests/SliderInteraction` 使用 Avalonia Headless 验证滑块和窗口交互，游戏服务为替代实现；`tests/GameTimeResolution` 仅测试本进程分配的模拟游戏内存。`tests/SoftwareUpdate` 使用内存 HTTP 响应与隔离 Dummy 程序验证更新下载、安装和回退，不修改用户程序。

详细命令见各测试项目的 README。更新清单与 GitHub Releases 发布步骤见 [自动更新说明](modern_ui/UPDATES.md)。

项目使用的 Find Sans Pro 字体许可见 `modern_ui/Assets/Fonts/LICENSE.txt`。
