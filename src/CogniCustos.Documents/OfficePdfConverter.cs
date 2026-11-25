using System.Diagnostics;
using System.Text;
using System.Runtime.Versioning;

namespace CogniCustos.Documents;

/// <summary>
/// Turns a .ppt/.pptx/.doc/.docx into a PDF using the Office install on this machine.
///
/// The conversion runs in a **child PowerShell process**, not in this one, for a measured
/// reason: from .NET 8, <c>CreateInstance(Word.Application)</c> never returns on this machine
/// (a bare 45-line .NET 8 console app reproduces it), while the same call from PowerShell
/// finishes in about two seconds. Because the work happens in another process, the timeout is
/// also a real one — a wedged Office window is killed instead of freezing the reader.
///
/// Nothing here is required for PDFs; without Office, .doc/.docx/.ppt/.pptx report a reason
/// and ask the user to save as PDF.
/// </summary>
public static class OfficePdfConverter
{
    private static readonly string[] OfficeExtensions = [".doc", ".docx", ".ppt", ".pptx"];

    public static bool IsOfficeDocument(string path) =>
        OfficeExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Convert to <paramref name="pdfPath"/>. Returns null on success, or a message saying
    /// what stopped it (no Office, a dialog, a broken file).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? TryConvert(string source, string pdfPath, TimeSpan timeout, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows()) return "只有 Windows 上才能自动转换 Office 文件；请另存为 PDF 再打开。";
        if (!File.Exists(source)) return "文件不存在：" + source;

        var powershell = PowershellPath();
        if (powershell is null) return "找不到 PowerShell，无法调用 Office 转换；请另存为 PDF 再打开。";

        var directory = Path.GetDirectoryName(pdfPath)!;
        Directory.CreateDirectory(directory);

        // Word automation can hang for a whole timeout on machines where Office refuses to be
        // driven (it does on this one — see docs/IMPLEMENTATION-LOG.md). Remember that for the
        // rest of the day so the second attempt fails immediately instead of waiting again.
        var probePath = Path.Combine(directory, "office-bridge.txt");
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var probe = ReadIfExists(probePath);
        if (probe.StartsWith("FAIL " + today, StringComparison.Ordinal))
        {
            log?.Invoke("office bridge already failed today; failing fast");
            return "这台机器今天已经试过：Office 自动转换不可用（Word/PowerPoint 不接受自动化）。\n"
                   + "请用 Office 的「另存为 → PDF」导出一份，再用点读打开那个 PDF。";
        }

        var scriptPath = Path.Combine(directory, "office-to-pdf.ps1");
        File.WriteAllText(scriptPath, Script, new UTF8Encoding(true));

        // Convert a private copy: Office holds the file open while it works, and the user's
        // original must not be locked (or touched) by our reading of it.
        var working = Path.Combine(directory, "conv-" + Guid.NewGuid().ToString("N")[..8]
                                              + Path.GetExtension(source).ToLowerInvariant());
        File.Copy(source, working, overwrite: true);

        try
        {
            var resultPath = Path.Combine(directory, "conv-result.txt");
            try { File.Delete(resultPath); } catch { }

            // UseShellExecute: the child gets its own (hidden) console. Measured on this
            // machine, Word started by a console-less WinExe sits on its startup screen and
            // never opens the document — the same script from a console finishes in seconds.
            var start = new ProcessStartInfo(powershell)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                         "-File", scriptPath,
                         "-Source", working,
                         "-Target", pdfPath,
                         "-Result", resultPath,
                     })
            {
                start.ArgumentList.Add(argument);
            }

            log?.Invoke("powershell: starting conversion");
            using var process = Process.Start(start);
            if (process is null) return "无法启动 PowerShell。";

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                // A wedged Office window is the common case; killing the child is the whole
                // point of running it out of process.
                try { process.Kill(entireProcessTree: true); } catch { }
                Remember(probePath, "FAIL " + today + " timeout");
                return "Office 自动转换没有响应，已经放弃（这台机器上的 Word/PowerPoint 不接受自动化）。\n"
                       + "请用 Office 的「另存为 → PDF」导出一份，再用点读打开那个 PDF。";
            }

            var text = ReadIfExists(resultPath);
            log?.Invoke("powershell: " + text.Replace("\r", " ").Replace("\n", " | ").Trim());

            if (File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0)
            {
                Remember(probePath, "OK " + today);
                return null;
            }

            Remember(probePath, "FAIL " + today + " " + Clip(text, 120).Replace('\n', ' '));
            return text.Length == 0
                ? "转换失败：Office 没有生成 PDF（需要本机安装 Word / PowerPoint）。"
                : "转换失败：" + Clip(text, 300);
        }
        catch (Exception ex)
        {
            Remember(probePath, "FAIL " + today + " " + ex.Message.Replace('\n', ' '));
            return "转换失败：" + Innermost(ex).Message;
        }
        finally
        {
            try { File.Delete(working); } catch { }
        }
    }

    /// <summary>Remember today's verdict so a repeat attempt fails immediately.</summary>
    private static void Remember(string path, string value)
    {
        try { File.WriteAllText(path, value); } catch { }
    }

    private static string ReadIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// PowerShell 7 first: it drives this machine's Word in seconds, while Windows PowerShell
    /// 5.1 fails outright and .NET 8 blocks forever (both measured). 5.1 is kept as a last
    /// resort for machines without PowerShell 7.
    /// </summary>
    private static string? PowershellPath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell", "7", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell", "7-preview", "pwsh.exe"),
        };

        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        var onPath = WhereIs("pwsh.exe");
        if (onPath is not null) return onPath;

        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(legacy) ? legacy : null;
    }

    private static string? WhereIs(string executable)
    {
        try
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory.Trim(), executable);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch
        {
            // a malformed PATH is not fatal; the fixed locations above are tried first
        }

        return null;
    }

    /// <summary>
    /// The bridge. Word and PowerPoint are opened invisibly, the document is exported to PDF,
    /// everything is closed, and the outcome is printed on one line so the parent can read it.
    /// </summary>
    private const string Script = """
        param($Source, $Target, $Result)

        $ErrorActionPreference = 'Stop'
        if (-not $Source -or -not $Target) {
            if ($Result) { Set-Content -Path $Result -Value 'ERR missing arguments' -Encoding utf8 }
            exit 1
        }

        $ext = [IO.Path]::GetExtension($Source).ToLowerInvariant()
        $app = $null
        $message = 'ERR unknown failure'

        try {
            if ($ext -eq '.doc' -or $ext -eq '.docx') {
                $app = New-Object -ComObject Word.Application
                $app.Visible = $false
                $app.DisplayAlerts = 0
                $app.AutomationSecurity = 3
                # positional: named arguments hang on this machine
                $doc = $app.Documents.Open($Source, $false, $true, $false)
                $doc.SaveAs2($Target, 17)
                $doc.Close(0)
                $app.Quit(0)
            }
            else {
                $app = New-Object -ComObject PowerPoint.Application
                $app.DisplayAlerts = 0
                $pres = $app.Presentations.Open($Source, -1, 0, 0)
                $pres.SaveAs($Target, 32)
                $pres.Close()
                $app.Quit()
            }

            if (Test-Path $Target) { $message = 'OK ' + (Get-Item $Target).Length }
            else { $message = 'ERR no output file' }
        }
        catch {
            $message = 'ERR ' + $_.Exception.Message
            if ($app -ne $null) { try { $app.Quit() } catch { } }
        }

        if ($Result) { Set-Content -Path $Result -Value $message -Encoding utf8 }
        Write-Output $message
        if ($message.StartsWith('OK')) { exit 0 } else { exit 1 }
        """;

    private static Exception Innermost(Exception ex)
    {
        var current = ex;
        while (current.InnerException is { } inner) current = inner;
        return current;
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
