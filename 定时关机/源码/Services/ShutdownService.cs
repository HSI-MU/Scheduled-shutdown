using System.Diagnostics;
using System.IO;
using System.Text;

namespace OnTimeShutdown.Services;

public sealed record ShutdownCommandResult(bool Success, int ExitCode, string Message);

/// <summary>
/// Thin wrapper around the built-in Windows shutdown.exe.
/// Windows owns the timer, so the plan survives closing this application.
/// </summary>
public sealed class ShutdownService
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorShutdownNotInitiated = 1116;
    private const int ErrorShutdownAlreadyScheduled = 1190;

    private static string ShutdownExe => Path.Combine(Environment.SystemDirectory, "shutdown.exe");

    public ShutdownCommandResult Schedule(int seconds, string reason)
    {
        seconds = Math.Clamp(seconds, 1, 315_360_000);
        reason = NormalizeReason(reason);

        return Run(new[]
        {
            "/s",
            "/t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "/c", reason
        });
    }

    public ShutdownCommandResult Cancel()
    {
        return Run(new[] { "/a" });
    }

    private static ShutdownCommandResult Run(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ShutdownExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Default,
            StandardErrorEncoding = Encoding.Default
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 Windows 关机命令。");

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(8_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore: the command is already gone or cannot be killed.
                }

                return new ShutdownCommandResult(false, -1, "系统命令响应超时，请重试。");
            }

            string output = outputTask.GetAwaiter().GetResult().Trim();
            string error = errorTask.GetAwaiter().GetResult().Trim();
            int exitCode = process.ExitCode;

            if (exitCode == 0)
            {
                return new ShutdownCommandResult(true, 0, string.Empty);
            }

            return new ShutdownCommandResult(false, exitCode, MapError(exitCode, error, output));
        }
        catch (Exception ex)
        {
            return new ShutdownCommandResult(false, -1, $"无法执行关机命令：{ex.Message}");
        }
    }

    private static string MapError(int exitCode, string error, string output)
    {
        return exitCode switch
        {
            ErrorShutdownAlreadyScheduled =>
                "系统中已经有一个关机计划，请先取消原来的计划。",
            ErrorShutdownNotInitiated =>
                "系统中没有正在等待执行的关机计划。",
            ErrorAccessDenied =>
                "当前账户没有权限执行关机操作，请检查系统策略。",
            _ => FirstNonEmpty(error, output, $"系统返回错误代码 {exitCode}。")
        };
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return "系统命令执行失败。";
    }

    private static string NormalizeReason(string reason)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? "准点关机：已安排自动关机" : reason.Trim();
        return reason.Length <= 200 ? reason : reason[..200];
    }
}
