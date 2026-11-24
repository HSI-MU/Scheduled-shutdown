# 准点关机

一个原生 Windows 定时关机小工具，使用 .NET 9 + WPF 编写。

## 功能

- **指定时间**：选择今天/明天，并设置小时、分钟；今天的时间已过时会自动顺延到明天。
- **倒计时**：支持 1 分钟到 99 小时 59 分，提供常用时长快捷项。
- **取消关机**：主界面一键取消，也可以从系统托盘取消。
- **系统级计划**：通过 Windows 自带的 `shutdown.exe /s /t` 安排关机，关闭窗口后计划仍有效。
- **托盘运行**：有任务时关闭窗口会最小化到托盘；托盘图标支持打开、取消、退出。
- **状态恢复**：计划、上次使用模式保存在 `data` 文件夹；同一个 Windows 启动周期内重开程序会自动恢复倒计时。
- **单实例**：重复启动会唤回已有窗口，避免多个定时任务冲突。

## 构建

要求：Windows 10/11、.NET 9 SDK。

```powershell
dotnet publish .\OnTimeShutdown.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -o .\publish
```

`publish\准点关机.exe` 是单文件程序，需要目标电脑安装 **.NET 9 Desktop Runtime**。当前电脑已安装。

如需完全免运行库的便携版本，可以改为自包含发布（体积约 150 MB）：

```powershell
dotnet publish .\OnTimeShutdown.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -o .\publish-portable
```

## 目录结构

```text
OnTimeShutdown.csproj   项目文件
App.xaml / App.xaml.cs  应用入口、单实例
MainWindow.xaml         UI 布局与样式
MainWindow.xaml.cs      交互、倒计时、托盘
Controls/ProgressRing.cs  圆形倒计时进度控件
Services/ShutdownService.cs  shutdown.exe 调用与错误映射
Services/StateStore.cs   计划/偏好持久化
Services/BootClock.cs    启动周期判断
Assets/app.ico           应用图标
tools/make-icon.ps1      重新生成图标
```

## 实现说明

- 安排关机时执行 `shutdown.exe /s /t <seconds> /c <comment>`，由 Windows 接管计时。
- 取消时执行 `shutdown.exe /a`；错误码 1116（没有待执行计划）也视为已重置。
- 计划状态写入程序目录的 `data/plan.json`，包含到期时间、原始总秒数、模式和启动周期标记。
- 重启电脑后旧计划会失效，程序据此清理过期状态，不会显示错误的倒计时。
- 程序为 `asInvoker`，无需管理员权限；若账户策略禁止关机，会显示对应提示。

## 验证

已在 Windows 11 上完成：

- 指定时间、倒计时两种模式的构建与界面检查；
- 真实系统关机计划安排成功，并在软件内成功取消；
- 关闭窗口隐藏到托盘、托盘界面恢复、单实例唤回；
- 直接输入时间、快捷项、进度环和状态恢复。
