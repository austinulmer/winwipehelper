using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace WinWipeHelper
{
    public sealed class ReleaseAsset
    {
        public string Name;
        public string Url;
        public long Size;
    }

    /// <summary>Downloads straight from vendors and GitHub (never mirrors).</summary>
    public static class Web
    {
        const string BrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WinWipeHelper";

        static Web()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288 /* Tls13 */; }
            catch { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
        }

        public static string GetString(string url, string accept = null)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = BrowserAgent;
            req.Timeout = 30000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (accept != null) req.Accept = accept;
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream()))
                return r.ReadToEnd();
        }

        /// <summary>A small download kept in memory (refuses anything over maxBytes). finalUrl is where redirects ended.</summary>
        public static byte[] GetBytes(string url, int maxBytes, int timeoutMs, out Uri finalUrl, string accept = null)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = BrowserAgent;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = true;
            req.MaximumAutomaticRedirections = 6;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.Accept = accept ?? "image/avif,image/png,image/x-icon,image/*;q=0.8,*/*;q=0.5";
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                finalUrl = resp.ResponseUri;
                if (resp.ContentLength > maxBytes) throw new InvalidDataException("Too big: " + resp.ContentLength);
                var buf = new byte[1 << 15];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ms.Length + n > maxBytes)
                    {
                        if (accept != null && accept.StartsWith("text/")) { ms.Write(buf, 0, (int)Math.Max(0, maxBytes - ms.Length)); break; }   // a page: the head is enough
                        throw new InvalidDataException("Too big");
                    }
                    ms.Write(buf, 0, n);
                }
                return ms.ToArray();
            }
        }

        /// <summary>Downloads a file. Returns the final file name the server suggested (if any).</summary>
        public static void Download(string url, string outPath, Action<long, long> progress = null, CancellationToken cancel = default)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = BrowserAgent;
            req.Timeout = 60000;
            req.ReadWriteTimeout = 60000;
            req.AllowAutoRedirect = true;
            var tmp = outPath + ".part";
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                long total = resp.ContentLength, done = 0;
                var buf = new byte[1 << 16];
                int n;
                var last = DateTime.MinValue;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    dst.Write(buf, 0, n);
                    done += n;
                    if (progress != null && (DateTime.UtcNow - last).TotalMilliseconds > 200) { progress(done, total); last = DateTime.UtcNow; }
                }
                progress?.Invoke(done, total);
            }
            if (File.Exists(outPath)) File.Delete(outPath);
            File.Move(tmp, outPath);
        }

        /// <summary>Assets of a GitHub repo's latest release ("owner/repo").</summary>
        public static ReleaseAsset[] LatestReleaseAssets(string repo, out string tag)
        {
            var json = Json.Parse(GetString($"https://api.github.com/repos/{repo}/releases/latest", "application/vnd.github+json"));
            tag = Json.Str(json, "tag_name");
            return Json.Arr(json, "assets").Select(a => new ReleaseAsset
            {
                Name = Json.Str(a, "name"),
                Url = Json.Str(a, "browser_download_url"),
                Size = long.TryParse(Json.Str(a, "size"), out var s) ? s : 0,
            }).ToArray();
        }

        public static ReleaseAsset FindAsset(ReleaseAsset[] assets, string pattern) =>
            assets.FirstOrDefault(a => Regex.IsMatch(a.Name ?? "", pattern ?? ".", RegexOptions.IgnoreCase));

        public static string FormatSize(double b)
        {
            if (b >= 1L << 30) return (b / (1L << 30)).ToString("N1") + " GB";
            if (b >= 1L << 20) return (b / (1L << 20)).ToString("N0") + " MB";
            return (b / 1024).ToString("N0") + " KB";
        }
    }
}
