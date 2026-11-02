# 开发与测试

以下命令均从仓库根目录执行。

## 环境与构建

运行及脚本构建需要 Windows x64 和 .NET Framework 4.8。脚本不需要安装 NuGet 包或额外 SDK。

```powershell
./build.ps1
./build.ps1 -Configuration Debug
./build.ps1 -OutputDirectory ./artifacts/package/CodexPulse
./scripts/package.ps1
```

默认输出到 `artifacts/bin/Release`；Debug 输出到 `artifacts/bin/Debug`。构建会复制托盘图标 `pulse.ico`，内置价格和 XAML 仍嵌入程序资源。

安装了 Visual Studio 的 .NET Framework 4.8 开发工具后，可以打开 `CodexPulse.sln`，或在开发者终端运行：

```powershell
MSBuild.exe CodexPulse.sln /p:Configuration=Release /p:Platform=x64
```

IDE 构建和脚本构建使用同一份 `.csproj`。新增 C# 文件或嵌入资源时，需要同步加入项目文件；资源的 `LogicalName` 应与运行时查找名称一致。

## 验证入口

```powershell
./scripts/test.ps1 -Suite Core
./scripts/test.ps1 -Suite Desktop
./scripts/test.ps1 -Suite All
```

| Suite | 覆盖范围 |
| --- | --- |
| `Core` | Accounts、Usage、AccountSwitch、Updates |
| `Desktop` | Themes、ClickThrough，包含多显示器验证 |
| `Accounts` | 登录、凭据、缓存、恢复、设置界面及数据兼容 |
| `Usage` | 官方快照、本机增量记录、日期、计价和费用窗口 |
| `AccountSwitch` | 切换成功与失败、重复点击、额度弹窗和模拟桌面重启 |
| `Updates` | 版本、发布来源、SHA256、ZIP 解压、原程序退出、文件替换回滚与重启 |
| `Themes` | 对话框样式与原生标题栏颜色 |
| `ClickThrough` | 原生命中、交互区域、热力图和多屏定位 |

桌面验证应在未锁定的交互桌面上执行，其他置顶窗口可能遮挡测试坐标。核心验证使用隔离账号、数据库和模拟桌面程序，不读取现用账号凭据。命令失败会抛出错误，详细报告和截图位于 `artifacts/tests/<suite>/`。

可单独执行高级验证：

```powershell
./scripts/tests/verify-v03.ps1 -ProbeRuntime
./scripts/tests/verify-v04.ps1 -Live -SettingsPath 'C:/YourInstall/data/settings.json'
./scripts/tests/verify-clickthrough.ps1 -ProgramPath ./artifacts/bin/Release/CodexPulse.exe
./scripts/tests/verify-updates.ps1 -Live
```

`-Live` 必须显式指定真实安装的设置文件；会复制账号库到隔离目录后查询，不续期复制的凭据、不更改当前登录、不重启 Codex。

## 输出与提交

`artifacts/bin` 存放应用输出，`artifacts/obj` 存放 MSBuild 中间文件，`artifacts/tests` 存放验证程序与报告。运行数据位于各程序输出目录的 `data` 中。

提交源码、资源、项目文件、脚本和文档。数据库、设置、凭据、构建产物、测试报告和机器路径均不应提交。`.gitignore` 覆盖这些文件；`Resources/model-prices.builtin.json` 属于可发布的应用资源。

GitHub Actions 在 Windows 上执行 Release 构建与 Core 验证；桌面命中和原生像素检查保留为本地交互验证。
