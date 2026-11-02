# Codex Pulse

Windows 桌面 Codex 额度看板。提供多账号额度查询、账号切换、官方 Token 用量与热力图，以及本机参考费用明细。

当前版本：**V0.4.4**。技术栈：**C# / WPF / .NET Framework 4.8 / Windows x64**。

## 快速开始

在 Windows PowerShell 或 PowerShell 中，从仓库根目录执行：

```powershell
./build.ps1
./artifacts/bin/Release/CodexPulse.exe
```

编译输出包含 `CodexPulse.exe` 和 `pulse.ico`。无需 NuGet 依赖；构建脚本使用 Windows 自带的 .NET Framework 编译器。也可以在安装了 .NET Framework 4.8 开发工具的 Visual Studio 中打开 [CodexPulse.sln](CodexPulse.sln)。

设置或托盘菜单中的“检查更新”可下载并重启升级。默认启动后检查，运行期间每 24 小时检查一次，可在设置中关闭。**v0.4.3 及更早版本需要先手动升级一次**：退出旧版，覆盖程序和图标，保留 `data` 文件夹；之后即可使用应用内更新。

## 工程结构

```text
Codex-Pulse/
├── CodexPulse.sln           # Visual Studio 解决方案
├── build.ps1               # 构建入口
├── src/CodexPulse/          # WPF 应用项目
│   ├── Application/        # 程序入口与启动流程
│   ├── Configuration/      # 设置加载与持久化
│   ├── Domain/Models/      # 账号、用量与快照模型
│   ├── Features/           # 账号、额度、用量、计价业务
│   ├── Infrastructure/     # Codex 通信、SQLite、Windows 集成
│   ├── Presentation/       # 主界面、窗口、控件与主题
│   ├── Properties/         # 程序集信息
│   └── Resources/          # 图标、清单与内置价格表
├── tests/                  # 账号、用量、桌面测试及夹具
├── scripts/                # 构建与测试工具
├── docs/                   # 使用说明、架构、开发与发布记录
└── artifacts/              # 本地构建和测试输出，不提交 Git
```

## 验证

```powershell
./scripts/test.ps1                 # 账号、用量与账号切换验证
./scripts/test.ps1 -Suite Desktop  # 主题、鼠标穿透和多屏验证
```

桌面验证需要可交互、未锁定的 Windows 桌面。测试使用隔离数据与模拟账号；只有明确传入真实设置路径的 `-Live` 验证会查询真实账号。

## 文档

- [使用说明](docs/user-guide.md)：账号、额度、统计范围、设置与数据兼容。
- [工程架构](docs/architecture.md)：目录职责和代码放置规则。
- [开发与测试](docs/development.md)：构建、测试、输出目录及资源维护。
- [模型价格来源](docs/pricing-sources.md)：内置价格的来源与计算口径。
- [V0.4.4 发布记录](docs/releases/v0.4.4.md) · [V0.4.3 发布记录](docs/releases/v0.4.3.md) · [开发路线](docs/development-roadmap.md)。

## 数据与隐私

账号数据库、凭据和运行设置保存在程序目录下的 `data/`，与源码分开。`artifacts/`、运行数据、凭据及机器设置均被 Git 忽略。账号切换会重新启动 Codex，进行中的任务可能中断。
