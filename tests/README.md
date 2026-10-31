# 验证程序

| 目录 | 内容 |
| --- | --- |
| `Accounts/` | 独立账号、配置恢复与账号切换验证 |
| `Usage/` | 数据升级、官方用量、本机统计、计价与相关窗口 |
| `Desktop/` | 原生主题、鼠标穿透、交互区域与显示器定位 |
| `Fixtures/` | 模拟 Codex 桌面进程，只用于隔离测试 |

从仓库根目录执行 `./scripts/test.ps1`。测试脚本位于 `scripts/tests/`，共享编译工具位于 `scripts/tests/Common.ps1`。所有测试输出进入被忽略的 `artifacts/tests/`。

详细命令与运行条件见 [开发与测试](../docs/development.md)。
