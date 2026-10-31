# 工程架构

项目采用一个 WPF 应用程序集，按职责划分源码目录。当前主要目标是明确代码位置、统一构建入口，同时保留既有运行行为和公共接口。

## 源码职责

| 目录 | 职责 | 主要文件 |
| --- | --- | --- |
| `Application` | 单实例入口、命令行分支和应用启动 | `Program.cs` |
| `Configuration` | 用户配置加载、兼容与保存 | `Settings.cs` |
| `Domain/Models` | 账号、每日记录和快照等公共数据类型 | `AccountUsage.cs`、`Snapshot.cs` |
| `Features/Accounts` | 独立账号操作、切换与重启流程 | `IndependentAccounts.cs`、`AccountSwitch.cs` |
| `Features/Quota` | 同周期额度汇总 | `QuotaSummary.cs` |
| `Features/Usage` | 本机日志增量采集和费用覆盖统计 | `LocalUsage.cs`、`LocalCostSummary.cs` |
| `Features/Pricing` | 内置和自定义价格表、计价规则 | `PriceBook.cs` |
| `Infrastructure/Codex` | Codex RPC、隔离运行环境和配置文件事务 | `CodexRpc.cs`、`ProfileFiles.cs` |
| `Infrastructure/Persistence` | SQLite 访问、账号库与统计快照 | `NativeSqlite.cs`、`UsageStore.cs` |
| `Infrastructure/Windows` | 原生窗口、显示器、鼠标穿透和启动项 | `MonitorDock.cs`、`PinInteraction.cs` |
| `Infrastructure/Serialization` | JSON 序列化辅助 | `Json.cs` |
| `Presentation/Dashboard` | 主窗口编排、显示和用户操作 | `Dashboard.cs` |
| `Presentation/Windows` | 账号管理、价格表和费用明细窗口 | `IndependentAccountWindow.cs` |
| `Presentation/Controls` | 额度弹窗、圆环和热力图 | `QuotaDetailsPopup.cs`、`TokenHeatmap.cs` |
| `Presentation/Theming` | 配色和公共对话框样式 | `Themes.cs`、`DialogTheme.xaml` |
| `Presentation/Views` | 主窗口 XAML | `MainWindow.xaml` |
| `Resources` | 图标、Windows 清单、内置模型价格 | `pulse.ico`、`model-prices.builtin.json` |

原 `App.cs` 已拆分为启动入口与 `Dashboard`；原 `Data.cs` 已拆分为独立模型、配置和序列化文件。类型继续使用 `CodexPulse` 命名空间，保持现有反射、程序集和验证接口兼容。

## 构建与资源

`src/CodexPulse/CodexPulse.csproj` 是唯一的源码和资源清单。Visual Studio / MSBuild 直接使用它；`scripts/build.ps1` 也读取相同清单，不再维护第二份源码列表。

XAML 继续作为资源嵌入，由运行时加载。文件移动后仍保留 `MainWindow.xaml`、`DialogTheme.xaml`、`model-prices.builtin.json` 三个逻辑资源名，避免路径变化影响运行时查找。

## 测试与文档

`tests/Accounts` 验证账号、登录和切换流程；`tests/Usage` 验证统计、价格和数据兼容；`tests/Desktop` 验证 Windows 命中测试、主题和多屏行为；`tests/Fixtures` 只放模拟程序。

测试脚本位于 `scripts/tests`，共享编译和输出目录处理。使用说明、价格来源、开发流程和发布历史位于 `docs`。新增测试和业务代码应进入相应职责目录，生成报告和截图应进入 `artifacts`。
