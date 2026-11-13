# 软件更新回归

测试通过源链接运行生产 UpdateService / UpdateInstaller，不访问真实更新源，也不替换用户程序。下载测试使用内存 HTTP handler；安装测试只在随机临时目录中复制并启动专用 Dummy 程序，覆盖窗口就绪确认、启动失败回滚、无窗口确认时回退、错误 hash、PowerShell 启动握手失败和取消。

先构建四个独立测试程序，再运行回归（工作目录为仓库根目录）：

```powershell
$testDotnet = '.\_deps\dotnet8\sdk\dotnet.exe'
& $testDotnet publish tests/SoftwareUpdateDummy/SoftwareUpdateDummy.csproj -c Release -p:DummyMarker=old -o _case/update_dummy/old --source https://api.nuget.org/v3/index.json
& $testDotnet publish tests/SoftwareUpdateDummy/SoftwareUpdateDummy.csproj -c Release -p:DummyMarker=new -o _case/update_dummy/new --no-restore
& $testDotnet publish tests/SoftwareUpdateDummy/SoftwareUpdateDummy.csproj -c Release -p:DummyMarker=failed -o _case/update_dummy/failed --no-restore
& $testDotnet publish tests/SoftwareUpdateDummy/SoftwareUpdateDummy.csproj -c Release -p:DummyMarker=no-window -o _case/update_dummy/no-window --no-restore
& $testDotnet run --project tests/SoftwareUpdate/SoftwareUpdate.csproj --no-restore
```

测试会暂时设置当前测试进程的 `DOTNET_ROOT`，让四个不自带运行库的极小 Dummy 程序使用本地 SDK 的运行库；未找到工作区 SDK 时使用当前 .NET 的运行库，兼容 GitHub Actions。结束后恢复原值，不会修改全局环境变量或启动桌面窗口。
