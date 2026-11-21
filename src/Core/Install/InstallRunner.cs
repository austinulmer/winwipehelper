using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace WinWipeHelper
{
    public sealed class InstallResult
    {
        public string App;
        public bool Ok;
        public string Detail;
    }

    /// <summary>Jobs that run in a worker process: the elevated batch and the per-user batch.</summary>
    public static class InstallJobs
    {
        public const string AdminBatch = "install-admin";
        public const string UserBatch = "install-user";

        public sealed class BatchPayload { public List<BatchItem> Items = new List<BatchItem>(); }
        public sealed class BatchItem
        {
            public string Kind;
            public string Id;
            public string Name;
            public string Override;
            public string OfficeXml;
        }

        public static void Register()
        {
            Worker.Register(AdminBatch, RunBatch);
            Worker.Register(UserBatch, RunBatch);
        }

        static int RunBatch(WorkerContext ctx, object payload)
        {
            var p = (BatchPayload)Json.ConvertTo(payload, typeof(BatchPayload));
            var winget = Winget.FindExe();
            for (int i = 0; i < p.Items.Count; i++)
            {
                var item = p.Items[i];
                if (ctx.Cancelled) { ctx.Result(i, -1, "cancelled"); continue; }
                ctx.Start(i, item.Name);
                if (winget == null) { ctx.Log("winget was not found."); ctx.Result(i, -1, "winget not found"); continue; }
                try
                {
                    if (item.Kind == AppKind.Office) RunOffice(ctx, i, item, winget);
                    else
                    {
                        var app = new AppItem { Id = item.Id, Override = item.Override };
                        ctx.Log($"> winget {Winget.InstallArgs(app)}");
                        var r = ProcessRunner.Run(winget, Winget.InstallArgs(app), line => ctx.Log("  " + line), 3 * 60 * 60 * 1000, ctx.Token);
                        ctx.Result(i, r.ExitCode, Winget.DescribeCode(r.ExitCode));
                    }
                }
                catch (Exception ex) { ctx.Log("ERROR: " + ex.Message); ctx.Result(i, -1, ex.Message); }
            }
            return 0;
        }

        public static string OdtSetupPath =>
            Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"OfficeDeploymentTool\setup.exe");

        static void RunOffice(WorkerContext ctx, int i, BatchItem item, string winget)
        {
            ctx.Log("> winget " + Winget.InstallArgs("Microsoft.OfficeDeploymentTool"));
            var r = ProcessRunner.Run(winget, Winget.InstallArgs("Microsoft.OfficeDeploymentTool"), line => ctx.Log("  " + line), 30 * 60 * 1000, ctx.Token);
            var odt = OdtSetupPath;
            if (!File.Exists(odt))
            {
                ctx.Log($"The Office Deployment Tool isn't at {odt} (winget: {Winget.DescribeCode(r.ExitCode)}).");
                ctx.Result(i, -1, "Office Deployment Tool missing");
                return;
            }
            var xmlPath = Path.Combine(Path.GetTempPath(), "WinWipeHelper-office-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".xml");
            File.WriteAllText(xmlPath, item.OfficeXml, new UTF8Encoding(false));
            ctx.Log("Downloading and installing Office. This takes a while and shows no progress...");
            ctx.Log($"> \"{odt}\" /configure \"{xmlPath}\"");
            var setup = ProcessRunner.Run(odt, "/configure " + ProcessRunner.Quote(xmlPath), line => ctx.Log("  " + line), 3 * 60 * 60 * 1000, ctx.Token, Path.GetDirectoryName(odt));
            try { File.Delete(xmlPath); } catch { }
            ctx.Result(i, setup.ExitCode, setup.ExitCode == 0 ? "installed" : "Office setup exit code " + setup.ExitCode);
        }
    }

    /// <summary>Runs an install: winget (elevated batch, per-user batch), Office, downloads.</summary>
    public sealed class InstallRunner
    {
        readonly Action<string> _log;
        readonly Action<double, string> _progress;
        readonly CancellationToken _cancel;
        int _done, _total;

        public InstallRunner(Action<string> log, Action<double, string> progress, CancellationToken cancel)
        {
            _log = log; _progress = progress; _cancel = cancel;
        }

        /// <summary>Tests point this somewhere else; normally downloads go to Desktop\Installers.</summary>
        public static string InstallersDirOverride;
        public static string InstallersDir => InstallersDirOverride ?? Path.Combine(Paths.Desktop, "Installers");
        public static string NotVerifiedDir => Path.Combine(InstallersDir, "_NOT VERIFIED - do not run");

        void Step(string text) { _progress(_total == 0 ? 100 : 100.0 * _done / _total, text); }

        public List<InstallResult> Run(List<AppItem> selected, bool dryRun)
        {
            var results = new List<InstallResult>();
            var adminItems = selected.Where(a => (a.Kind == AppKind.Winget && !a.User) || a.Kind == AppKind.Office).ToList();
            var userItems = selected.Where(a => a.Kind == AppKind.Winget && a.User).ToList();
            var downloads = selected.Where(a => a.Kind == AppKind.Download).ToList();
            _total = selected.Count; _done = 0;

            if (dryRun) return DryRun(adminItems, userItems, downloads);

            if (adminItems.Count + userItems.Count > 0)
            {
                Step("Checking winget...");
                if (!Winget.EnsureWorking(_log, _cancel))
                {
                    foreach (var a in adminItems.Concat(userItems)) results.Add(new InstallResult { App = a.Name, Ok = false, Detail = "winget isn't working" });
                    adminItems.Clear(); userItems.Clear();
                }
                else
                {
                    _log("> winget source update");
                    ProcessRunner.Run(Winget.FindExe(), "source update --disable-interactivity", s => _log("  " + s), 180000, _cancel);
                }
            }

            if (adminItems.Count > 0)
            {
                _log(Elevation.IsElevated ? "\nInstalling (already running as admin)..." : "\nAsking for admin permission to install " + adminItems.Count + " app(s)...");
                results.AddRange(RunBatch(InstallJobs.AdminBatch, adminItems, elevated: true));
            }
            if (userItems.Count > 0)
            {
                _log($"\nInstalling as your normal user (no admin): {string.Join(", ", userItems.Select(u => u.Name))}");
                results.AddRange(RunBatch(InstallJobs.UserBatch, userItems, elevated: false));
            }
            if (downloads.Count > 0)
            {
                _log("\nDownloading installers to " + InstallersDir);
                foreach (var d in downloads)
                {
                    if (_cancel.IsCancellationRequested) { results.Add(new InstallResult { App = d.Name, Ok = false, Detail = "cancelled" }); continue; }
                    Step(d.Name);
                    results.Add(Download(d));
                    _done++;
                }
            }
            Step("Done");
            return results;
        }

        List<InstallResult> RunBatch(string job, List<AppItem> items, bool elevated)
        {
            var payload = new InstallJobs.BatchPayload();
            foreach (var a in items)
                payload.Items.Add(new InstallJobs.BatchItem
                {
                    Kind = a.Kind, Id = a.Id, Name = a.Name, Override = a.Override,
                    OfficeXml = a.Kind == AppKind.Office ? (a.Office ?? new OfficeOptions()).ToConfigurationXml() : null,
                });
            var res = items.Select(a => new InstallResult { App = a.Name, Ok = false, Detail = "didn't run" }).ToList();
            int startDone = _done;
            void OnMessage(WorkerMessage m)
            {
                switch (m.Kind)
                {
                    case "log": _log(m.Text); break;
                    case "start":
                        _done = startDone + m.Index;
                        _log($"\n[{_done + 1}/{_total}] {m.Text}");
                        Step(m.Text);
                        break;
                    case "result":
                        if (m.Index >= 0 && m.Index < res.Count)
                        {
                            res[m.Index].Ok = Winget.IsOk(m.Code) && m.Text != "cancelled";
                            res[m.Index].Detail = string.IsNullOrEmpty(m.Text) ? Winget.DescribeCode(m.Code) : m.Text;
                            _log($"  -> {(res[m.Index].Ok ? "OK" : "FAILED")}: {res[m.Index].Detail}");
                        }
                        break;
                }
            }
            int code = elevated
                ? Worker.RunElevated(job, payload, OnMessage, _cancel)
                : Worker.RunAsUser(job, payload, OnMessage, _cancel, TimeSpan.FromHours(2));
            if (code == Worker.ExitUacDeclined)
                foreach (var r in res.Where(r => r.Detail == "didn't run")) r.Detail = "admin permission declined";
            _done = startDone + items.Count;
            return res;
        }

        InstallResult Download(AppItem p)
        {
            var result = new InstallResult { App = p.Name };
            try
            {
                Directory.CreateDirectory(InstallersDir);
                string url = p.Url, fileName = p.FileName;
                if (!string.IsNullOrEmpty(p.Github))
                {
                    var asset = Web.FindAsset(Web.LatestReleaseAssets(p.Github, out var tag), p.AssetPattern);
                    if (asset == null) throw new Exception("No file in the latest release matches " + p.AssetPattern);
                    url = asset.Url; fileName = asset.Name;
                    _log($"  {p.Github} {tag}: {asset.Name}");
                }
                if (string.IsNullOrEmpty(url)) throw new Exception("No download URL.");
                if (string.IsNullOrEmpty(fileName)) fileName = Path.GetFileName(new Uri(url).LocalPath);
                fileName = Paths.SafeFileName(WebUtility.UrlDecode(fileName));
                var outPath = Path.Combine(InstallersDir, fileName);
                _log("  " + url);
                Web.Download(url, outPath, (done, total) =>
                    _progress(_total == 0 ? 100 : 100.0 * _done / _total, $"{p.Name}: {Web.FormatSize(done)}" + (total > 0 ? " of " + Web.FormatSize(total) : "")), _cancel);

                var toCheck = new List<string> { outPath };
                string moveTarget = outPath;
                if (outPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var unzip = Path.Combine(InstallersDir, Path.GetFileNameWithoutExtension(fileName));
                    if (Directory.Exists(unzip)) Directory.Delete(unzip, true);
                    ZipFile.ExtractToDirectory(outPath, unzip);
                    File.Delete(outPath);
                    moveTarget = unzip;
                    toCheck = Directory.GetFiles(unzip, "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (toCheck.Count == 0) throw new Exception("The zip had no installer in it.");
                }

                var problems = new List<string>();
                foreach (var f in toCheck)
                {
                    var sig = Signature.Check(f);
                    bool ok = sig.Matches(p.Signer);
                    _log($"  {Path.GetFileName(f)}: {(ok ? "signature OK" : "NOT VERIFIED")} ({sig.Subject}, {sig.Status})");
                    if (!ok) problems.Add(sig.Valid
                        ? $"{Path.GetFileName(f)} is signed by {sig.Subject}, but \"{p.Signer}\" was expected"
                        : $"{Path.GetFileName(f)}: signature {sig.Status} ({sig.Subject})");
                }
                if (problems.Count > 0)
                {
                    Directory.CreateDirectory(NotVerifiedDir);
                    var dest = Path.Combine(NotVerifiedDir, Path.GetFileName(moveTarget));
                    if (File.Exists(dest)) File.Delete(dest);
                    if (Directory.Exists(dest)) Directory.Delete(dest, true);
                    if (Directory.Exists(moveTarget)) Directory.Move(moveTarget, dest); else File.Move(moveTarget, dest);
                    result.Ok = false;
                    result.Detail = "signature check failed: " + string.Join("; ", problems);
                }
                else { result.Ok = true; result.Detail = "downloaded"; }
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Detail = "download failed: " + ex.Message;
            }
            _log($"  -> {(result.Ok ? "OK" : "FAILED")}: {result.Detail}");
            return result;
        }

        List<InstallResult> DryRun(List<AppItem> adminItems, List<AppItem> userItems, List<AppItem> downloads)
        {
            var results = new List<InstallResult>();
            _log("DRY RUN: nothing will be installed or downloaded. These are the steps a real run takes.\n");
            if (adminItems.Count + userItems.Count > 0)
            {
                bool ok = Winget.IsWorking(out var v);
                _log(ok ? $"winget {v} is working." :
                    "winget is missing or broken. A real run would:\n" +
                    $"  1. powershell Add-AppxPackage -RegisterByFamilyName -MainPackage {Winget.FamilyName}\n" +
                    "  2. if that fails, download App Installer (.msixbundle + dependencies zip) from github.com/microsoft/winget-cli/releases/latest,\n" +
                    "     check they're signed by Microsoft Corporation, then Add-AppxPackage them. The Microsoft Store is never used.");
                _log("> winget source update --disable-interactivity");
            }
            if (adminItems.Count > 0)
            {
                _log($"\nAs admin ({(Elevation.IsElevated ? "already elevated" : "one UAC prompt")}):");
                foreach (var a in adminItems)
                {
                    _done++; Step(a.Name);
                    if (a.Kind == AppKind.Office)
                    {
                        var opts = a.Office ?? new OfficeOptions();
                        var xml = opts.ToConfigurationXml();
                        var problems = OfficeOptions.Validate(xml);
                        _log($"> winget {Winget.InstallArgs("Microsoft.OfficeDeploymentTool")}");
                        _log($"> \"{InstallJobs.OdtSetupPath}\" /configure office.xml");
                        _log("  office.xml (" + opts.Summary() + "):");
                        foreach (var line in xml.Split('\n')) _log("    " + line.TrimEnd('\r'));
                        _log(problems.Count == 0 ? "  XML check: valid" : "  XML check FAILED: " + string.Join("; ", problems));
                        results.Add(new InstallResult { App = a.Name, Ok = problems.Count == 0, Detail = problems.Count == 0 ? "dry run (XML valid)" : "XML invalid" });
                    }
                    else
                    {
                        _log($"> winget {Winget.InstallArgs(a)}");
                        results.Add(new InstallResult { App = a.Name, Ok = true, Detail = "dry run" });
                    }
                }
            }
            if (userItems.Count > 0)
            {
                _log($"\nAs your normal user, no admin ({(Elevation.IsElevated ? "through a one-time scheduled task with RunLevel Limited" : "run directly")}):");
                foreach (var a in userItems)
                {
                    _done++; Step(a.Name);
                    _log($"> winget {Winget.InstallArgs(a)}");
                    results.Add(new InstallResult { App = a.Name, Ok = true, Detail = "dry run" });
                }
            }
            if (downloads.Count > 0)
            {
                _log($"\nDownloads to {InstallersDir} (signature must be Valid and from the expected signer, otherwise moved to \"_NOT VERIFIED - do not run\"):");
                foreach (var d in downloads)
                {
                    _done++; Step(d.Name);
                    string where = d.Url;
                    if (!string.IsNullOrEmpty(d.Github))
                    {
                        try
                        {
                            var asset = Web.FindAsset(Web.LatestReleaseAssets(d.Github, out var tag), d.AssetPattern);
                            where = asset != null ? $"{asset.Url}  ({tag})" : $"github.com/{d.Github}: no asset matches {d.AssetPattern}";
                        }
                        catch (Exception ex) { where = $"github.com/{d.Github} latest release ({ex.Message})"; }
                    }
                    _log($"  GET {where}\n      signer must contain: {(string.IsNullOrEmpty(d.Signer) ? "(any valid signature)" : d.Signer)}");
                    results.Add(new InstallResult { App = d.Name, Ok = true, Detail = "dry run" });
                }
            }
            Step("Dry run finished");
            return results;
        }

        /// <summary>Writes the end-of-run page: failures, where downloads went, and the manual links.</summary>
        public static string WriteManualPage(List<InstallResult> results, List<ManualLink> links, bool hadDownloads)
        {
            string H(string s) => WebUtility.HtmlEncode(s ?? "");
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Manual installs</title><style>")
              .Append("body{font:16px/1.5 'Segoe UI',sans-serif;max-width:960px;margin:32px auto;padding:0 16px;color:#1f2328}")
              .Append("table{border-collapse:collapse;width:100%}td{border-bottom:1px solid #ddd;padding:8px;vertical-align:top}")
              .Append(".ok{color:#1a7f37}.bad{color:#cf222e}</style></head><body>");
            sb.Append("<h1>Install these by hand</h1>");
            var failed = results.Where(r => !r.Ok).ToList();
            sb.Append($"<p>{results.Count - failed.Count} of {results.Count} finished OK.</p>");
            if (hadDownloads && Directory.Exists(InstallersDir))
                sb.Append($"<p><b>Downloaded installers are waiting in:</b> {H(InstallersDir)}. Run each one yourself. Anything in the \"_NOT VERIFIED\" folder failed its signature check, so don't run it; get that one from the vendor's site instead.</p>");
            if (failed.Count > 0)
            {
                sb.Append("<h2>Failed in this run</h2><ul>");
                foreach (var f in failed) sb.Append($"<li class=\"bad\">{H(f.App)}: {H(f.Detail)}</li>");
                sb.Append("</ul>");
            }
            if (links.Count > 0)
            {
                sb.Append("<h2>Can't be automated</h2><table>");
                foreach (var l in links)
                {
                    var link = string.IsNullOrEmpty(l.Url) ? "" : $"<a href=\"{H(l.Url)}\">{H(l.Url)}</a>";
                    sb.Append($"<tr><td>{H(l.Name)}</td><td>{link}</td><td>{H(l.Note)}</td></tr>");
                }
                sb.Append("</table>");
            }
            sb.Append("</body></html>");
            var page = Path.Combine(InstallersDirOverride != null ? Path.GetDirectoryName(InstallersDirOverride) : Paths.Desktop, "manual-installs.html");
            File.WriteAllText(page, sb.ToString(), new UTF8Encoding(false));
            return page;
        }
    }
}
