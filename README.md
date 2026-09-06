# AI Agent Token 用量可视化

一个零依赖的原生 Windows 桌面小工具（.NET Framework + WinForms，C#），用于读取本地 **Codex / Claude 等 AI Agent** 的会话记录并可视化 token 用量与任务效率。

## 功能

- **多 Agent 支持**：自动扫描 Codex 与 Claude 的本地会话记录，可筛选「全部 / Codex / Claude」
- **Claude 对话名**：读取 Claude 会话的对话名（自定义标题 → 首次用户输入 → 最近提示），按会话分别展示
- **效率指标**：统计「完成任务数」与「每任务平均 token 消耗」，直观衡量完成任务的开销
- **现代无边框界面**：圆角窗体、自定义标题栏（拖动 / 双击最大化 / 边缘缩放）
- **统计卡片**：累计 Tokens、完成任务、每任务均耗、输入、输出、缓存读取、推理 tokens、会话数
- **动态柱状图**：渐变圆角柱 + 入场动画，按天 / 按会话视图 × 五种指标，悬停显示精确数值
- **明细表格**：Agent / 时间 / 会话 / 各类 token，按时间倒序（最多 5000 条）
- 手动刷新、一键打开数据目录

## 数据来源（纯本地，不联网）

| Agent | 扫描目录 |
|---|---|
| Codex | `%USERPROFILE%\.codex\sessions`（rollout-*.jsonl + session_index.jsonl） |
| Claude | `%LOCALAPPDATA%\Claude-3p\local-agent-mode-sessions`、`%LOCALAPPDATA%\Claude`、`%USERPROFILE%\.claude\projects` |

- **任务**：Codex 以 `task_complete` 事件计数；Claude 以用户请求 / 会话完成事件计数
- **每任务均耗** = 累计 tokens ÷ 完成任务数（衡量完成一个任务的平均开销，越低越高效）

## 构建

需要 Windows 自带的 .NET Framework 4.x（Win10/11 均已内置），无需安装任何 SDK。

```powershell
# 在项目根目录执行，输出到 .\bin\CodexTokenUsageViewer.exe
.\build.ps1
```

## 使用

双击生成的 exe 即可。顶部「Agent」下拉可切换统计范围；「完成任务 / 每任务均耗」卡片即任务效率指标。

命令行自检参数（开发用）：

- `--report [文件路径]`：不启动界面，把统计报告写入文本文件
- `--shot [png路径]`：启动窗口渲染后自动截图保存并退出（用于 UI 验证）

## 目录结构

```
codex-usage-viewer/
├── CodexTokenUsageViewer.cs   # 全部源码（单文件）
├── app.ico                    # 程序图标
├── build.ps1                  # 一键构建脚本
├── push-to-github.ps1         # 一键推送 GitHub 脚本
├── README.md
└── .gitignore
```

## 版本记录

- **v1.3.3**（2026-09-06）：读取 Claude 会话的真实对话名（自定义标题 / 首次用户输入 / 最近提示），按会话（每个 `.jsonl`）分别统计。
- **v1.3**（2026-09-06）：多 Agent 统计（Codex + Claude）与任务效率指标（完成任务数 / 每任务均耗）。
- **v1.2**（2026-09-06）：UI 全面升级——无边框圆角窗体、统计卡片、动态渐变柱状图与精致表格。
- **v1.1**（2026-09-06）：第一版。按天 / 按会话统计、柱状图可视化与明细表格。