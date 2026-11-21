using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace WinWipeHelper
{
    public sealed class ProcessResult
    {
        public int ExitCode;
        public string Output = "";
        public bool TimedOut;
        public bool Started = true;
        public string Error;
    }

    /// <summary>Runs console tools hidden (no console window) and captures their output.</summary>
    public static class ProcessRunner
    {
        static readonly Regex Noise = new Regex(@"^[\s\-\\|/]*$|^\s*[█▒░■□▓]+|^\s*\d+(\.\d+)?\s*(KB|MB|GB|B)\s*/\s*\d", RegexOptions.Compiled);

        /// <summary>
        /// Runs a program and waits. onLine receives each output line (progress-bar noise removed).
        /// </summary>
        public static ProcessResult Run(string file, string args, Action<string> onLine = null, int timeoutMs = -1,
                                        CancellationToken cancel = default, string workingDir = null, Encoding encoding = null)
        {
            var result = new ProcessResult();
            var psi = new ProcessStartInfo(file, args ?? "")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = encoding ?? Encoding.UTF8,
                StandardErrorEncoding = encoding ?? Encoding.UTF8,
                WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
            };
            var sb = new StringBuilder();
            var sbLock = new object();
            using (var p = new Process { StartInfo = psi })
            {
                void Handle(string data)
                {
                    if (data == null) return;
                    // winget redraws progress with \r; keep only the last state of the line.
                    var parts = data.Split('\r');
                    foreach (var part in parts)
                    {
                        var line = part.TrimEnd();
                        if (line.Length == 0 || Noise.IsMatch(line)) continue;
                        lock (sbLock) sb.AppendLine(line);
                        onLine?.Invoke(line);
                    }
                }
                p.OutputDataReceived += (s, e) => Handle(e.Data);
                p.ErrorDataReceived += (s, e) => Handle(e.Data);
                try
                {
                    if (!p.Start()) { result.Started = false; result.ExitCode = -1; return result; }
                }
                catch (Exception ex)
                {
                    result.Started = false; result.ExitCode = -1; result.Error = ex.Message;
                    return result;
                }
                try { p.StandardInput.Close(); } catch { }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                var sw = Stopwatch.StartNew();
                while (!p.WaitForExit(200))
                {
                    if (cancel.IsCancellationRequested || (timeoutMs >= 0 && sw.ElapsedMilliseconds > timeoutMs))
                    {
                        result.TimedOut = !cancel.IsCancellationRequested;
                        try { KillTree(p.Id); } catch { }
                        try { p.WaitForExit(5000); } catch { }
                        break;
                    }
                }
                try { p.WaitForExit(); } catch { }
                try { result.ExitCode = p.HasExited ? p.ExitCode : -1; } catch { result.ExitCode = -1; }
            }
            lock (sbLock) result.Output = sb.ToString();
            return result;
        }

        /// <summary>Runs a program and returns its raw stdout (no line filtering). Used for parsing tables.</summary>
        public static ProcessResult Capture(string file, string args, int timeoutMs = 120000, CancellationToken cancel = default)
        {
            var result = new ProcessResult();
            var psi = new ProcessStartInfo(file, args ?? "")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    try { p.StandardInput.Close(); } catch { }
                    var errTask = p.StandardError.ReadToEndAsync();
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var sw = Stopwatch.StartNew();
                    while (!p.WaitForExit(200))
                    {
                        if (cancel.IsCancellationRequested || sw.ElapsedMilliseconds > timeoutMs)
                        {
                            result.TimedOut = true;
                            try { KillTree(p.Id); } catch { }
                            break;
                        }
                    }
                    try { outTask.Wait(5000); } catch { }
                    result.Output = outTask.IsCompleted ? outTask.Result : "";
                    try { result.ExitCode = p.HasExited ? p.ExitCode : -1; } catch { result.ExitCode = -1; }
                }
            }
            catch (Exception ex) { result.Started = false; result.ExitCode = -1; result.Error = ex.Message; }
            return result;
        }

        public static void KillTree(int pid)
        {
            try
            {
                using (var k = Process.Start(new ProcessStartInfo("taskkill.exe", $"/PID {pid} /T /F") { CreateNoWindow = true, UseShellExecute = false }))
                    k?.WaitForExit(10000);
            }
            catch { }
        }

        /// <summary>Quotes one argument for the Windows command line (CommandLineToArgvW rules).</summary>
        public static string Quote(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            if (arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', backslashes); sb.Append(c); }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        public static string JoinArgs(IEnumerable<string> args)
        {
            var parts = new List<string>();
            foreach (var a in args) parts.Add(Quote(a));
            return string.Join(" ", parts);
        }

        /// <summary>Runs a PowerShell command hidden. Used only for Appx cmdlets that have no simple .NET equivalent.</summary>
        public static ProcessResult PowerShell(string command, Action<string> onLine = null, int timeoutMs = 600000, CancellationToken cancel = default)
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + command));
            var ps = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            return Run(ps, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, onLine, timeoutMs, cancel);
        }
    }
}
