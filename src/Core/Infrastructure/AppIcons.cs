using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WinWipeHelper
{
    /// <summary>
    /// Icons shown next to app names. In order: the installed program's own icon, the icon from the
    /// app's own website (cached, can be turned off), or a colored letter tile.
    /// </summary>
    public static class AppIcons
    {
        public static string CacheDir => Path.Combine(Paths.LocalAppDataDir, "icons");

        // ---- installed programs --------------------------------------------------------------------

        /// <summary>"C:\x\app.exe,0", "\"C:\x\app.exe\",-101", "%ProgramFiles%\x\app.ico" -> (path, index).</summary>
        public static (string path, int index) ParseIconLocation(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return (null, 0);
            s = Environment.ExpandEnvironmentVariables(s.Trim());
            int index = 0;
            string path;
            if (s.StartsWith("\""))
            {
                int end = s.IndexOf('"', 1);
                path = end > 1 ? s.Substring(1, end - 1) : s.Trim('"');
                var rest = end > 1 ? s.Substring(end + 1).Trim() : "";
                if (rest.StartsWith(",")) int.TryParse(rest.Substring(1).Trim(), out index);
            }
            else
            {
                path = s;
                var m = Regex.Match(s, @"^(.*?),\s*(-?\d+)\s*$");
                if (m.Success && !File.Exists(s)) { path = m.Groups[1].Value.Trim(); index = int.Parse(m.Groups[2].Value); }
            }
            path = path.Trim().Trim('"');
            return (path.Length > 0 ? path : null, index);
        }

        /// <summary>The icon in an exe/dll/ico (or a shortcut's icon), drawn at size x size. Null if there is none.</summary>
        public static Bitmap FromFile(string location, int size)
        {
            var (path, index) = ParseIconLocation(location);
            if (path == null || !Path.IsPathRooted(path)) return null;
            try
            {
                if (!File.Exists(path)) return null;
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    var icon = Shell.LinkIcon(path);
                    if (icon != null && !icon.Equals(path, StringComparison.OrdinalIgnoreCase)) { var b = FromFile(icon, size); if (b != null) return b; }
                    return ShellIcon(path, size);
                }
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".png" || ext == ".bmp" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".ico")
                {
                    var fi = new FileInfo(path);
                    if (fi.Length > 4 << 20) return null;
                    var fromImage = Decode(File.ReadAllBytes(path), size);
                    if (fromImage != null) return fromImage;
                }
                if (SHDefExtractIconW(path, index, 0, out var large, out var small, (uint)(size | (16 << 16))) == 0 && large != IntPtr.Zero)
                {
                    try { return FromHicon(large, size); }
                    finally { DestroyIcon(large); if (small != IntPtr.Zero) DestroyIcon(small); }
                }
                return ext == ".exe" ? ShellIcon(path, size) : null;
            }
            catch { return null; }
        }

        /// <summary>What Explorer shows for a file (falls back to the generic icon, so only used for exes and shortcuts).</summary>
        static Bitmap ShellIcon(string path, int size)
        {
            var info = new SHFILEINFO();
            if (SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | (size <= 16 ? SHGFI_SMALLICON : SHGFI_LARGEICON)) == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try { return FromHicon(info.hIcon, size); }
            finally { DestroyIcon(info.hIcon); }
        }

        static Bitmap FromHicon(IntPtr h, int size)
        {
            using (var icon = Icon.FromHandle(h))
            using (var bmp = icon.ToBitmap())
                return Fit(bmp, size);
        }

        // ---- images from the web ---------------------------------------------------------------------

        static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        static bool StartsWith(byte[] data, int offset, byte[] magic)
        {
            if (data == null || data.Length < offset + magic.Length) return false;
            for (int i = 0; i < magic.Length; i++) if (data[offset + i] != magic[i]) return false;
            return true;
        }

        /// <summary>Decodes ICO (bitmap or PNG frames), PNG, GIF, JPEG or BMP and fits it in size x size. Null if it can't.</summary>
        public static Bitmap Decode(byte[] data, int size)
        {
            if (data == null || data.Length < 8) return null;
            try
            {
                if (data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0) return DecodeIco(data, size);
                using (var ms = new MemoryStream(data))
                using (var img = Image.FromStream(ms, false, true))
                {
                    if (img.Width < 8 || img.Height < 8) return null;
                    return Fit(img, size);
                }
            }
            catch { return null; }
        }

        static Bitmap DecodeIco(byte[] data, int size)
        {
            int count = BitConverter.ToUInt16(data, 4);
            if (count == 0 || data.Length < 6 + count * 16) return null;
            var entries = new List<(int w, int bpp, int len, int off)>();
            for (int i = 0; i < count; i++)
            {
                int p = 6 + i * 16;
                int w = data[p] == 0 ? 256 : data[p];
                int bpp = BitConverter.ToUInt16(data, p + 6);
                int len = BitConverter.ToInt32(data, p + 8);
                int off = BitConverter.ToInt32(data, p + 12);
                if (len > 0 && off >= 6 && (long)off + len <= data.Length) entries.Add((w, bpp, len, off));
            }
            if (entries.Count == 0) return null;
            // The smallest frame that's at least as big as wanted (best color depth first), else the biggest.
            var pick = entries.Where(e => e.w >= size).OrderBy(e => e.w).ThenByDescending(e => e.bpp).Cast<(int w, int bpp, int len, int off)?>().FirstOrDefault()
                       ?? entries.OrderByDescending(e => e.w).ThenByDescending(e => e.bpp).First();
            var frame = new byte[pick.len];
            Buffer.BlockCopy(data, pick.off, frame, 0, pick.len);
            if (StartsWith(frame, 0, PngMagic)) return Decode(frame, size);
            // A one-frame .ico that System.Drawing can read.
            var one = new byte[22 + frame.Length];
            Buffer.BlockCopy(data, 0, one, 0, 6);
            one[4] = 1; one[5] = 0;
            int src = FindEntry(data, count, pick.off);
            Buffer.BlockCopy(data, src, one, 6, 16);
            BitConverter.GetBytes(22).CopyTo(one, 18);
            Buffer.BlockCopy(frame, 0, one, 22, frame.Length);
            using (var ms = new MemoryStream(one))
            using (var icon = new Icon(ms, pick.w, pick.w))
            using (var bmp = icon.ToBitmap())
                return Fit(bmp, size);
        }

        static int FindEntry(byte[] data, int count, int offset)
        {
            for (int i = 0; i < count; i++) if (BitConverter.ToInt32(data, 6 + i * 16 + 12) == offset) return 6 + i * 16;
            return 6;
        }

        /// <summary>False when an icon would be (nearly) invisible on a white list: almost no opaque pixels, or almost all of them white.</summary>
        public static bool ShowsOnWhite(Bitmap b)
        {
            int visible = 0, light = 0, total = b.Width * b.Height;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                {
                    var c = b.GetPixel(x, y);
                    if (c.A < 64) continue;
                    visible++;
                    if (c.R > 225 && c.G > 225 && c.B > 225) light++;
                }
            return visible >= total / 30 && light < visible * 0.85;
        }

        /// <summary>Scales an image into a transparent size x size square, keeping its shape.</summary>
        public static Bitmap Fit(Image img, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = img.Width == size && img.Height == size ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                float scale = Math.Min(size / (float)img.Width, size / (float)img.Height);
                float w = img.Width * scale, h = img.Height * scale;
                using (var attr = new ImageAttributes())
                {
                    attr.SetWrapMode(WrapMode.TileFlipXY);   // no gray fringe at the edges
                    g.DrawImage(img, new Rectangle((int)Math.Round((size - w) / 2), (int)Math.Round((size - h) / 2), (int)Math.Round(w), (int)Math.Round(h)),
                                0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attr);
                }
            }
            return bmp;
        }

        /// <summary>The icon links a home page declares, best first (SVG skipped: System.Drawing can't draw it).</summary>
        public static List<string> IconLinks(string html, Uri page, int wanted = 32)
        {
            var found = new List<(string url, int size, int order)>();
            if (string.IsNullOrEmpty(html)) return new List<string>();
            int order = 0;
            foreach (Match m in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
            {
                var tag = m.Value;
                string Attr(string name)
                {
                    var a = Regex.Match(tag, @"\b" + name + @"\s*=\s*(""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
                    return a.Success ? WebUtilityDecode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value) : null;
                }
                var rel = (Attr("rel") ?? "").ToLowerInvariant();
                var href = Attr("href");
                if (string.IsNullOrWhiteSpace(href) || rel.Contains("mask-icon")) continue;
                bool touch = rel.Contains("apple-touch-icon");
                if (!touch && !Regex.IsMatch(rel, @"(^|\s)icon(\s|$)")) continue;
                var type = (Attr("type") ?? "").ToLowerInvariant();
                if (type.Contains("svg") || Regex.IsMatch(href, @"\.svg(\?|#|$)", RegexOptions.IgnoreCase) || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                int size = touch ? 180 : 0;
                var sm = Regex.Match(Attr("sizes") ?? "", @"(\d+)\s*[xX]\s*\d+");
                if (sm.Success) size = int.Parse(sm.Groups[1].Value);
                if (!Uri.TryCreate(page, href.Trim(), out var abs) || (abs.Scheme != Uri.UriSchemeHttps && abs.Scheme != Uri.UriSchemeHttp)) continue;
                found.Add((abs.AbsoluteUri, size, order++));
            }
            // Big enough (smallest first), then unknown sizes, then too small (biggest first).
            return found.OrderBy(f => f.size >= wanted ? 0 : f.size == 0 ? 1 : 2)
                        .ThenBy(f => f.size >= wanted ? f.size : -f.size)
                        .ThenBy(f => f.order)
                        .Select(f => f.url).Distinct().ToList();
        }

        static string WebUtilityDecode(string s) => WebUtility.HtmlDecode(s ?? "");

        /// <summary>Cache file name for a home page: its host, or github.com_owner for GitHub projects.</summary>
        public static string SiteKey(string homepage)
        {
            if (!TryHomepage(homepage, out var uri)) return null;
            var host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host.Substring(4);
            var owner = GithubOwner(uri);
            return Paths.SafeFileName(owner != null ? "github.com_" + owner.ToLowerInvariant() : host);
        }

        static bool TryHomepage(string homepage, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(homepage)) return false;
            var s = homepage.Trim();
            if (!s.Contains("://")) s = "https://" + s;
            return Uri.TryCreate(s, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.Host.Contains(".");
        }

        static string GithubOwner(Uri uri)
        {
            var host = uri.Host.ToLowerInvariant();
            if (host != "github.com" && host != "www.github.com") return null;
            var seg = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return seg.Length >= 1 && Regex.IsMatch(seg[0], @"^[A-Za-z0-9-]+$") ? seg[0] : null;
        }

        static readonly TimeSpan RetryMissingAfter = TimeSpan.FromDays(14);

        /// <summary>A cached site icon (never downloads). Null if it isn't cached.</summary>
        public static Bitmap Cached(string homepage, int size)
        {
            var key = SiteKey(homepage);
            if (key == null) return null;
            var file = Path.Combine(CacheDir, key + ".png");
            try { return File.Exists(file) ? Decode(File.ReadAllBytes(file), size) : null; } catch { return null; }
        }

        /// <summary>True when an earlier try found no usable icon for this site recently.</summary>
        public static bool KnownMissing(string homepage)
        {
            var key = SiteKey(homepage);
            if (key == null) return true;
            var none = Path.Combine(CacheDir, key + ".none");
            try { return File.Exists(none) && DateTime.UtcNow - File.GetLastWriteTimeUtc(none) < RetryMissingAfter; } catch { return false; }
        }

        /// <summary>
        /// Downloads the site's own icon (from the page's icon links or /favicon.ico; GitHub projects use the
        /// owner's avatar), caches it as a 64 px PNG, and returns it at the wanted size. Null if there's none.
        /// </summary>
        public static Bitmap FetchSiteIcon(string homepage, int size, Func<bool> cancelled = null)
        {
            if (!TryHomepage(homepage, out var uri)) return null;
            var key = SiteKey(homepage);
            Directory.CreateDirectory(CacheDir);
            var file = Path.Combine(CacheDir, key + ".png");
            var none = Path.Combine(CacheDir, key + ".none");
            Bitmap best = null;
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool Try(string url)
            {
                if (url == null || !tried.Add(url) || (cancelled?.Invoke() ?? false)) return false;
                try
                {
                    var data = Web.GetBytes(url, 1 << 20, 10000, out _);
                    var bmp = Decode(data, 64);
                    if (bmp == null) return false;
                    if (!ShowsOnWhite(bmp)) { bmp.Dispose(); return false; }   // e.g. a white logo made for dark pages
                    best = bmp;
                    return true;
                }
                catch { return false; }
            }

            var owner = GithubOwner(uri);
            if (owner != null) Try($"https://github.com/{owner}.png?size=64");
            else
            {
                var pages = new List<Uri> { uri.AbsolutePath.Length > 1 ? new Uri(uri.GetLeftPart(UriPartial.Path)) : uri };
                if (uri.AbsolutePath.Length > 1) pages.Add(new Uri(uri.GetLeftPart(UriPartial.Authority) + "/"));
                foreach (var page in pages)
                {
                    if (best != null) break;
                    Uri final = page;
                    string html = null;
                    try { html = Encoding.UTF8.GetString(Web.GetBytes(page.AbsoluteUri, 768 << 10, 10000, out final, "text/html")); } catch { }
                    foreach (var link in IconLinks(html, final ?? page, 64).Take(4)) if (Try(link)) break;
                    if (best == null) Try(new Uri(final ?? page, "/favicon.ico").AbsoluteUri);
                }
                // Download hosts (dl.vendor.com) often have no icon: try the vendor's main site.
                var labels = uri.Host.Split('.');
                if (best == null && labels.Length >= 3) Try($"https://{labels[labels.Length - 2]}.{labels[labels.Length - 1]}/favicon.ico");
            }

            try
            {
                if (best == null) { File.WriteAllText(none, DateTime.UtcNow.ToString("o")); return null; }
                best.Save(file + ".tmp", ImageFormat.Png);
                if (File.Exists(file)) File.Delete(file);
                File.Move(file + ".tmp", file);
                if (File.Exists(none)) File.Delete(none);
            }
            catch { }
            using (best) return Fit(best, size);
        }

        // ---- letter tiles ------------------------------------------------------------------------------

        static readonly Color[] TileColors =
        {
            Color.FromArgb(0x2D, 0x6C, 0xDF), Color.FromArgb(0x16, 0x8A, 0x5B), Color.FromArgb(0xC2, 0x41, 0x1C), Color.FromArgb(0x7A, 0x4C, 0xC9),
            Color.FromArgb(0xB0, 0x2A, 0x6B), Color.FromArgb(0x0E, 0x7C, 0x86), Color.FromArgb(0x9A, 0x6A, 0x00), Color.FromArgb(0x4B, 0x5B, 0x6E),
            Color.FromArgb(0x3C, 0x7D, 0x23), Color.FromArgb(0xA3, 0x3B, 0x3B),
        };

        /// <summary>The character shown on a tile: the first letter or digit of the cleaned-up name.</summary>
        public static string TileLetter(string name)
        {
            var s = Names.Clean(name ?? "") ?? "";
            foreach (var c in s) if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
            return "?";
        }

        public static Color TileColor(string name)
        {
            uint h = 2166136261;
            foreach (var c in Names.Normalize(name ?? "")) { h ^= c; h *= 16777619; }
            return TileColors[h % (uint)TileColors.Length];
        }

        /// <summary>A rounded square in a color picked from the name, with its first letter.</summary>
        public static Bitmap LetterTile(string name, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                float inset = Math.Max(1f, size / 16f), r = size / 4.5f;
                var rect = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
                using (var path = new GraphicsPath())
                {
                    path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
                    path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
                    path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
                    path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
                    path.CloseFigure();
                    using (var brush = new SolidBrush(TileColor(name))) g.FillPath(brush, path);
                }
                using (var font = new Font("Segoe UI Semibold", size * 0.52f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(TileLetter(name), font, Brushes.White, new RectangleF(0, size * 0.02f, size, size), fmt);
            }
            return bmp;
        }

        public static Bitmap Blank(int size) => new Bitmap(size, size, PixelFormat.Format32bppArgb);

        // ---- native ----------------------------------------------------------------------------------

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHDefExtractIconW(string pszIconFile, int iIndex, uint uFlags, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIconSize);

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);

        const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_SMALLICON = 0x1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
    }
}
