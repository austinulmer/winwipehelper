using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace WinWipeHelper
{
    public static class Elevation
    {
        public static readonly bool IsElevated = CheckElevated();

        static bool CheckElevated()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>A message from a worker process (elevated or per-user) back to the window.</summary>
    public sealed class WorkerMessage
    {
        public string Kind;     // log | start | result | progress | exit
        public int Index;
        public int Code;
        public double Percent;
        public string Text;

        public string Encode() => $"{Kind}\t{Index}\t{Code}\t{Percent.ToString(System.Globalization.CultureInfo.InvariantCulture)}\t{Esc(Text)}";

        public static WorkerMessage Decode(string line)
        {
            var p = line.Split(new[] { '\t' }, 5);
            if (p.Length < 5) return null;
            var m = new WorkerMessage { Kind = p[0], Text = Unesc(p[4]) };
            int.TryParse(p[1], out m.Index);
            int.TryParse(p[2], out m.Code);
            double.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out m.Percent);
            return m;
        }

        static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "").Replace("\n", "\\n");
        static string Unesc(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    var n = s[++i];
                    sb.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }

    /// <summary>Job file handed to a worker process.</summary>
    public sealed class WorkerJob
    {
        public string Type;
        public string ProgressPath;
        public object Payload;
    }

    /// <summary>What a job handler uses to report back.</summary>
    public sealed class WorkerContext
    {
        readonly Action<WorkerMessage> _send;
        readonly string _cancelPath;
        public WorkerContext(Action<WorkerMessage> send, string cancelPath, CancellationToken token)
        {
            _send = send; _cancelPath = cancelPath; Token = token;
        }
        public CancellationToken Token { get; }
        public bool Cancelled => Token.IsCancellationRequested || (_cancelPath != null && File.Exists(_cancelPath));
        public void Log(string text) => _send(new WorkerMessage { Kind = "log", Text = text });
        public void Start(int index, string text) => _send(new WorkerMessage { Kind = "start", Index = index, Text = text });
        public void Result(int index, int code, string text = null) => _send(new WorkerMessage { Kind = "result", Index = index, Code = code, Text = text });
        public void Progress(double percent, string text = null) => _send(new WorkerMessage { Kind = "progress", Percent = percent, Text = text });
    }

    /// <summary>
    /// Runs a job in a separate copy of this EXE: elevated (one UAC prompt) or, when we're elevated
    /// but the job must not be (e.g. Spotify), as the normal user through a one-time scheduled task.
    /// </summary>
    public static class Worker
    {
        public const int ExitUacDeclined = -1223;
        public const int ExitFailedToStart = -2;

        static readonly Dictionary<string, Func<WorkerContext, object, int>> Handlers =
            new Dictionary<string, Func<WorkerContext, object, int>>(StringComparer.OrdinalIgnoreCase);

        public static void Register(string type, Func<WorkerContext, object, int> handler) => Handlers[type] = handler;

        /// <summary>
        /// Entry point for "--worker job.json sha256". The hash is checked against the bytes actually read, so a job file
        /// swapped in %TEMP% between writing it and the elevated read is refused.
        /// </summary>
        public static int RunFromFile(string jobPath, string expectedHash = null)
        {
            WorkerJob job;
            try
            {
                var bytes = File.ReadAllBytes(jobPath);
                if (expectedHash == null || !string.Equals(Sha256(bytes), expectedHash, StringComparison.OrdinalIgnoreCase)) return 4;
                job = Json.Deserialize<WorkerJob>(new UTF8Encoding(false).GetString(bytes));
            }
            catch { return 2; }
            Modules.RegisterWorkers();
            var progress = job.ProgressPath;
            var writeLock = new object();
            void Send(WorkerMessage m)
            {
                lock (writeLock)
                {
                    for (int attempt = 0; attempt < 20; attempt++)
                    {
                        try
                        {
                            using (var fs = new FileStream(progress, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                            using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                                w.WriteLine(m.Encode());
                            return;
                        }
                        catch (IOException) { Thread.Sleep(50); }
                        catch { return; }
                    }
                }
            }
            int code;
            try
            {
                if (!Handlers.TryGetValue(job.Type ?? "", out var h)) { Send(new WorkerMessage { Kind = "log", Text = "Unknown job type: " + job.Type }); code = 3; }
                else code = h(new WorkerContext(Send, progress + ".cancel", CancellationToken.None), job.Payload);
            }
            catch (Exception ex)
            {
                Send(new WorkerMessage { Kind = "log", Text = "ERROR: " + ex });
                code = 1;
            }
            Send(new WorkerMessage { Kind = "exit", Code = code });
            return code;
        }

        public static string Sha256(byte[] bytes)
        {
            using (var h = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "");
        }

        /// <summary>Runs a job elevated (or in-process if we already are). Blocks until it's done.</summary>
        public static int RunElevated(string type, object payload, Action<WorkerMessage> onMessage, CancellationToken cancel)
        {
            Modules.RegisterWorkers();
            if (Elevation.IsElevated) return RunInProcess(type, payload, onMessage, cancel);

            var (jobPath, progressPath, hash) = WriteJob(type, payload);
            Process proc;
            try
            {
                proc = Process.Start(new ProcessStartInfo(Paths.ExePath, "--worker " + ProcessRunner.Quote(jobPath) + " " + hash)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Paths.ExeDir,
                });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                onMessage(new WorkerMessage { Kind = "log", Text = "Admin permission was declined, so this step was skipped." });
                return ExitUacDeclined;
            }
            catch (Exception ex)
            {
                onMessage(new WorkerMessage { Kind = "log", Text = "Couldn't start the admin helper: " + ex.Message });
                return ExitFailedToStart;
            }
            if (proc == null) return ExitFailedToStart;
            using (proc)
            {
                int? exit = Tail(progressPath, onMessage, () => proc.HasExited, cancel);
                try
                {
                    if (!proc.WaitForExit(cancel.IsCancellationRequested ? 1000 : -1)) return exit ?? -1;
                    return exit ?? proc.ExitCode;
                }
                catch { return exit ?? 1; }
            }
        }

        /// <summary>Runs a job as the normal (non-elevated) user. Blocks until it's done.</summary>
        public static int RunAsUser(string type, object payload, Action<WorkerMessage> onMessage, CancellationToken cancel, TimeSpan timeout, bool forceTask = false)
        {
            Modules.RegisterWorkers();
            if (!Elevation.IsElevated && !forceTask) return RunInProcess(type, payload, onMessage, cancel);

            // We're elevated: hand the job to a one-time scheduled task that runs as the signed-in desktop user
            // without admin rights (LeastPrivilege). Created from XML so it also starts on battery power.
            var (jobPath, progressPath, hash) = WriteJob(type, payload);
            var task = "WinWipeHelper-User-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var user = InteractiveUser() ?? (Environment.UserDomainName + "\\" + Environment.UserName);
            string X(string s) => System.Security.SecurityElement.Escape(s);
            var xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                $"  <Principals><Principal id=\"Author\"><UserId>{X(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\r\n" +
                "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled>" +
                "<ExecutionTimeLimit>PT6H</ExecutionTimeLimit><Priority>5</Priority></Settings>\r\n" +
                $"  <Actions Context=\"Author\"><Exec><Command>{X(Paths.ExePath)}</Command><Arguments>{X("--worker " + ProcessRunner.Quote(jobPath) + " " + hash)}</Arguments></Exec></Actions>\r\n" +
                "</Task>\r\n";
            var xmlPath = Path.Combine(Paths.TempDir, task + ".xml");
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);
            var create = ProcessRunner.Run("schtasks.exe", $"/Create /TN {ProcessRunner.Quote(task)} /XML {ProcessRunner.Quote(xmlPath)} /F");
            try { File.Delete(xmlPath); } catch { }
            if (create.ExitCode != 0)
            {
                onMessage(new WorkerMessage { Kind = "log", Text = "Couldn't create the per-user task: " + create.Output.Trim() });
                return ExitFailedToStart;
            }
            try
            {
                var run = ProcessRunner.Run("schtasks.exe", $"/Run /TN {ProcessRunner.Quote(task)}");
                if (run.ExitCode != 0)
                {
                    onMessage(new WorkerMessage { Kind = "log", Text = "Couldn't start the per-user task: " + run.Output.Trim() });
                    return ExitFailedToStart;
                }
                var started = DateTime.UtcNow;
                var deadline = started + timeout;
                var lastCheck = DateTime.MinValue;
                bool Finished()
                {
                    if (DateTime.UtcNow > deadline) return true;
                    if ((DateTime.UtcNow - lastCheck).TotalSeconds < 5) return false;
                    lastCheck = DateTime.UtcNow;
                    // 4 = running, 2 = queued. Anything else after the first 20 s means the worker is gone.
                    int state = ScheduledTasks.State("\\" + task);
                    return (DateTime.UtcNow - started).TotalSeconds > 20 && state != 4 && state != 2;
                }
                int? exit = Tail(progressPath, onMessage, Finished, cancel, stopOnExitMessage: true);
                if (exit == null)
                    onMessage(new WorkerMessage { Kind = "log", Text = cancel.IsCancellationRequested ? "Cancelled." :
                        DateTime.UtcNow > deadline ? "The per-user install timed out." : "The per-user helper stopped without reporting back." });
                return exit ?? -1;
            }
            finally
            {
                ProcessRunner.Run("schtasks.exe", $"/End /TN {ProcessRunner.Quote(task)}");
                ProcessRunner.Run("schtasks.exe", $"/Delete /TN {ProcessRunner.Quote(task)} /F");
            }
        }

        /// <summary>The user signed in at the desktop of this session (owner of explorer.exe), e.g. when an admin approved UAC for them.</summary>
        static string InteractiveUser()
        {
            try
            {
                int session = Process.GetCurrentProcess().SessionId;
                using (var s = new System.Management.ManagementObjectSearcher($"SELECT Handle FROM Win32_Process WHERE Name = 'explorer.exe' AND SessionId = {session}"))
                    foreach (System.Management.ManagementObject p in s.Get())
                    {
                        var owner = new object[2];
                        if (Convert.ToInt32(p.InvokeMethod("GetOwner", owner)) == 0 && owner[0] != null)
                            return (owner[1] != null ? owner[1] + "\\" : "") + owner[0];
                    }
            }
            catch { }
            return null;
        }

        public static int RunInProcess(string type, object payload, Action<WorkerMessage> onMessage, CancellationToken cancel)
        {
            if (!Handlers.TryGetValue(type, out var h)) throw new InvalidOperationException("Unknown job " + type);
            // Round-trip through JSON so the handler sees exactly what a worker process would.
            var copy = Json.Parse(Json.Serialize(payload));
            return h(new WorkerContext(onMessage, null, cancel), copy);
        }

        static (string job, string progress, string hash) WriteJob(string type, object payload)
        {
            var id = Guid.NewGuid().ToString("N").Substring(0, 10);
            var jobPath = Path.Combine(Paths.TempDir, "j-" + id + ".json");
            var progressPath = Path.Combine(Paths.TempDir, "j-" + id + ".progress");
            File.WriteAllText(progressPath, "");
            var bytes = new UTF8Encoding(false).GetBytes(Json.Serialize(new WorkerJob { Type = type, ProgressPath = progressPath, Payload = payload }));
            File.WriteAllBytes(jobPath, bytes);
            return (jobPath, progressPath, Sha256(bytes));
        }

        /// <summary>Follows the progress file until the worker exits. Returns the code from its exit message.</summary>
        static int? Tail(string path, Action<WorkerMessage> onMessage, Func<bool> finished, CancellationToken cancel, bool stopOnExitMessage = false)
        {
            long pos = 0;
            var pending = new List<byte>();      // raw bytes: decode only whole lines, so UTF-8 characters never split
            int? exit = null;
            DateTime? cancelSentAt = null;
            while (true)
            {
                bool done = finished();
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (fs.Length > pos)
                        {
                            fs.Seek(pos, SeekOrigin.Begin);
                            var buf = new byte[fs.Length - pos];
                            int n = fs.Read(buf, 0, buf.Length);
                            pos += n;
                            for (int i = 0; i < n; i++) pending.Add(buf[i]);
                        }
                    }
                }
                catch { }
                int nl;
                while ((nl = pending.IndexOf((byte)'\n')) >= 0)
                {
                    var line = Encoding.UTF8.GetString(pending.GetRange(0, nl).ToArray()).TrimEnd('\r');
                    pending.RemoveRange(0, nl + 1);
                    var m = WorkerMessage.Decode(line);
                    if (m == null) continue;
                    if (m.Kind == "exit") exit = m.Code;
                    else onMessage(m);
                }
                if (exit != null && stopOnExitMessage) return exit;
                if (done) return exit;
                if (cancel.IsCancellationRequested && cancelSentAt == null)
                {
                    try { File.WriteAllText(path + ".cancel", "cancel"); } catch { }
                    cancelSentAt = DateTime.UtcNow;
                }
                // The worker finishes its current step and stops; don't wait forever if that step hangs.
                if (cancelSentAt != null && (DateTime.UtcNow - cancelSentAt.Value).TotalSeconds > 30) return exit;
                Thread.Sleep(300);
            }
        }
    }
}
