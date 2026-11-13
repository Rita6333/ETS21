# 软件自动更新

默认更新源为本程序的 GitHub Releases：

```text
https://github.com/Rita6333/ETS21/releases/latest/download/update.json
```

在窗口栏的“软件更新”中可手动检查更新、开关后台下载。更新源地址与自动检查选项按用户要求隐藏；默认启动时和每 6 小时自动检查新版本。发现新版后在后台下载，完成后点击“重启更新”替换程序。检查或下载不会中断游戏操作。后台检查失败保持安静，手动检查会显示具体原因。

检查成功且没有更高版本时显示“当前为最新版本”。GitHub 版本检查使用独立请求标识避开旧缓存；`latest` 下载入口返回 404 时，会查询该仓库最新正式 Release 的 `update.json` 资源并重试。仍无法获取时显示检查失败，可点击“检查更新”重试。

## 使用 GitHub 自动发布

仓库已提供 `.github/workflows/release.yml`。更新项目中的版本号与 `.github/release-notes.md` 后，提交源码并推送对应版本标签，例如：

```powershell
git push origin main
git tag -a v1.2.0 -m 'ETS2 DayNight v1.2.0'
git push origin v1.2.0
```

标签必须与 `ETS2DayNightModern.csproj` 的 `Version` 完全对应。GitHub Actions 会先运行界面、时钟和更新回归，再构建 Windows x64 单文件，并发布 EXE 与 `update.json` 到最新正式 Release。每次发布使用新的版本号和标签；发布后校验远程清单与下载文件的 SHA256。清单中的简短说明可在 workflow 的 `New-UpdateManifest.ps1 -Notes` 参数中更新。

## 手动发布一个新版本

每次发布先提高 `ETS2DayNightModern.csproj` 中的软件版本，再构建 Windows x64 自包含单文件。更新资产必须是可直接运行的 EXE；不能将 ZIP 下载地址填进更新清单。

以下以发布 `v1.2.0` 为例。在 GitHub 仓库 `Rita6333/ETS21` 中建立对应 Release，将发布出来的 EXE 上传为 `ETS2DayNight-win-x64.exe`，然后生成并上传 `update.json`：

```powershell
.\scripts\New-UpdateManifest.ps1 `
  -ExecutablePath '.\dist\ETS2昼夜切换.exe' `
  -DownloadUrl 'https://github.com/Rita6333/ETS21/releases/download/v1.2.0/ETS2DayNight-win-x64.exe' `
  -OutputPath '.\dist\update.json' `
  -Notes '本次版本的更新说明'
```

脚本默认读取 EXE 的产品版本，也可显式传入 `-Version '1.2.0'`。版本支持 2 至 4 段非负整数，例如 `1.2`、`1.2.0`、`1.2.0.1`；不接受 `v1.2.0` 或预发布后缀。显式版本必须与构建版本一致，否则客户端升级后的版本判断会不正确。

脚本仅在本地生成文件，不创建 Release，也不上传。先上传 EXE、再上传清单，并将 Release 发布为最新正式版本。不要将正式更新发布为 Draft 或 Pre-release；GitHub 的 `latest` 地址不会选择这两类版本。清单生成后请勿替换其中 EXE 的内容；如需重新构建，应重新生成清单。

生成的 JSON 包含以下字段：

| 字段 | 内容 |
| --- | --- |
| `version` | 软件版本，例如 `1.2.0` |
| `downloadUrl` | 已上传 EXE 的 HTTPS 下载地址 |
| `sha256` | EXE 的 64 位十六进制 SHA256 |
| `size` | EXE 的字节数 |
| `notes` | 展示给用户的更新说明 |

客户端下载时校验 SHA256，声明了 `size` 时还会校验文件大小。EXE 最大为 200 MiB，清单最大为 64 KiB；下载地址、清单地址以及重定向地址均须使用 HTTPS，且不能带用户名、密码或片段。

## 本地文件与失败处理

设置、下载缓存和安装日志保存在：

```text
%LocalAppData%\ETS2DayNight\updates
```

高级设置保存在该目录的 `settings.json`，包含 `manifestUrl`、`autoCheck`、`autoDownload`；如需修改隐藏设置，请关闭软件后编辑此文件再启动。

安装助手确认启动后，窗口才会关闭。助手等待当前进程退出，校验已下载文件后原子替换 EXE；旧版本保存在原程序目录中，名称为 `<程序名>.exe.backup-<随机标识>`。重新打开后恢复普通窗口并请求前台激活；新程序确认界面已显示后，助手才记录安装成功。启动失败或 20 秒内未确认窗口就绪时回退旧版。安装错误写入上述数据目录的 `install-<随机标识>.log`。下载缓存按 SHA256 校验后复用。修改更新源后，之前源下载的版本不能继续安装，必须重新检查。

若尚未创建包含 `update.json` 的正式 Release，手动检查会显示清单无法获取；上传首个有效版本后即可工作。
