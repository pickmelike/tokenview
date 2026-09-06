# Codex Token 用量可视化

一个零依赖的原生 Windows 桌面小工具（.NET Framework + WinForms，C#），用于读取本地 Codex 的会话记录并可视化 token 用量。

## 功能

- **现代无边框界面**：圆角窗体、自定义标题栏（可拖动/双击最大化/边缘缩放）、品牌 Logo 徽标
- **统计卡片**：累计 Tokens、输入、输出、缓存读取、推理 tokens、会话数、数据范围（圆角卡片 + 主题色点缀）
- **动态柱状图**：渐变圆角柱 + 入场生长动画，支持「按天 / 按会话」×「总量 / 输入 / 输出 / 缓存读取 / 推理」，鼠标悬停显示精确数值气泡
- **明细表格**：自定义表头、斑马纹行，逐条用量记录按时间倒序（最多 5000 条）
- **手动刷新数据**、一键打开数据目录

## 数据来源

程序扫描 `%USERPROFILE%\.codex\sessions` 下所有 `rollout-*.jsonl`（Codex 每次请求的 token 记录），
并读取同目录 `session_index.jsonl` 将会话 ID 映射为会话名。纯本地读取，不联网、不写任何配置。

## 构建

需要 Windows 自带的 .NET Framework 4.x（Win10/11 均已内置），无需安装任何 SDK。

```powershell
# 在项目根目录执行，输出到 .\bin\CodexTokenUsageViewer.exe
.\build.ps1
```

也可以直接用 csc 编译：

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /platform:anycpu /optimize+ `
  /win32icon:app.ico /out:bin\CodexTokenUsageViewer.exe `
  /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.dll `
  /r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Web.Extensions.dll CodexTokenUsageViewer.cs
```

## 使用

双击生成的 `CodexTokenUsageViewer.exe` 即可。窗口为无边框现代风格：按住顶部空白拖动窗口，
双击标题切换最大化，右下边缘可拖拽缩放，右上角为最小化 / 最大化 / 关闭按钮。

命令行自检参数（开发用）：

- `--report [文件路径]`：不启动界面，把统计报告写入文本文件
- `--shot [png路径]`：启动窗口渲染后自动截图保存并退出（用于 UI 验证）

## 目录结构

```
codex-usage-viewer/
├── CodexTokenUsageViewer.cs   # 全部源码（单文件）
├── app.ico                    # 程序图标（32x32）
├── build.ps1                  # 一键构建脚本
├── README.md
└── .gitignore
```

## 版本记录

- **v1.2**（2026-09-06）：UI 全面升级——无边框圆角窗体、自定义标题栏、统计卡片、动态渐变柱状图与精致表格样式。
- **v1.1**（2026-09-06）：第一版。WinForms 原生界面，支持按天 / 按会话统计、柱状图可视化与明细表格。