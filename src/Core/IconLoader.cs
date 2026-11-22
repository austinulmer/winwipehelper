using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace WinWipeHelper
{
    /// <summary>
    /// The icons of one list or grid. Each app gets a letter tile at once; the real icon (installed,
    /// cached, or downloaded from the app's website) replaces it in the background.
    /// </summary>
    public sealed class IconLoader : IDisposable
    {
        public sealed class Request
        {
            public string Key, Name, Homepage;
            public Func<string> FindInstalled;   // runs on a background thread; returns an icon location
        }

        enum Quality { Tile, Web, Installed }

        readonly Control _owner;
        readonly Action<ImageList> _attach;
        ImageList _list;
        int _size;
        int _generation;
        bool _disposed;
        readonly List<string> _order = new List<string>();                    // index in the image list = position here + 1
        readonly Dictionary<string, Request> _requests = new Dictionary<string, Request>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Bitmap> _shown = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Quality> _quality = new Dictionary<string, Quality>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised on the UI thread when an icon changed (index = its image list index).</summary>
        public event Action<string, int> Updated;

        public int IconSize => _size;

        /// <param name="attach">Hands the image list to the control (called again if the DPI changes).</param>
        public IconLoader(Control owner, Action<ImageList> attach = null)
        {
            _owner = owner;
            _attach = attach;
            _size = SizeFor(owner.DeviceDpi);
            _list = NewList(_size);
            _attach?.Invoke(_list);
            owner.HandleCreated += (s, e) =>
            {
                if (owner.FindForm() is BaseForm form)
                {
                    form.DpiSwitched -= OnDpi;
                    form.DpiSwitched += OnDpi;
                    if (SizeFor(form.CurrentDpi) != _size) Rescale(SizeFor(form.CurrentDpi));
                }
                owner.BeginInvoke((Action)Flush);
            };
            owner.Disposed += (s, e) => Dispose();
        }

        void OnDpi(object sender, EventArgs e)
        {
            if (sender is BaseForm f && SizeFor(f.CurrentDpi) != _size) Rescale(SizeFor(f.CurrentDpi));
        }

        static int SizeFor(int dpi) => Math.Max(16, (int)Math.Round(16 * (dpi <= 0 ? 96 : dpi) / 96.0));

        static ImageList NewList(int size)
        {
            var l = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(size, size) };
            l.Images.Add(AppIcons.Blank(size));   // index 0: no icon (category rows)
            return l;
        }

        /// <summary>Index 0 is an empty image, for rows without an icon.</summary>
        public const int NoIcon = 0;

        /// <summary>Registers an app and returns its image index (a letter tile until the real icon arrives).</summary>
        public int Add(Request r)
        {
            if (_disposed) return NoIcon;
            if (_requests.ContainsKey(r.Key)) return IndexOf(r.Key);
            _requests[r.Key] = r;
            _order.Add(r.Key);
            Put(r.Key, AppIcons.LetterTile(r.Name, _size), Quality.Tile);
            Queue(r, _generation, _size);
            return IndexOf(r.Key);
        }

        public int IndexOf(string key)
        {
            int i = _order.IndexOf(key);
            return i < 0 ? NoIcon : i + 1;
        }

        /// <summary>The icon at the current size (for grids, which take images rather than an image list). Don't dispose it.</summary>
        public Image ImageFor(string key) => key != null && _shown.TryGetValue(key, out var b) ? b : null;

        void Put(string key, Bitmap bmp, Quality q)
        {
            int index = IndexOf(key);
            if (index == NoIcon) { bmp.Dispose(); return; }
            if (index < _list.Images.Count) _list.Images[index] = bmp; else _list.Images.Add(bmp);
            if (_shown.TryGetValue(key, out var old) && !ReferenceEquals(old, bmp)) old.Dispose();
            _shown[key] = bmp;
            _quality[key] = q;
        }

        /// <summary>Looks again for apps that still show a tile or a website icon (e.g. after installing them).</summary>
        public void Refresh(bool webOnly = false)
        {
            if (_disposed) return;
            foreach (var key in _order)
                if (_quality.TryGetValue(key, out var q) && (webOnly ? q == Quality.Tile : q != Quality.Installed))
                    Queue(_requests[key], _generation, _size);
        }

        void Rescale(int size)
        {
            if (_disposed) return;
            _size = size;
            _generation++;
            var old = _list;
            _list = NewList(size);
            foreach (var b in _shown.Values) b.Dispose();
            _shown.Clear();
            foreach (var key in _order)
            {
                var bmp = AppIcons.LetterTile(_requests[key].Name, size);
                _list.Images.Add(bmp);
                _shown[key] = bmp;
                _quality[key] = Quality.Tile;
            }
            _attach?.Invoke(_list);
            old.Dispose();
            foreach (var key in _order) Queue(_requests[key], _generation, size);
            for (int i = 0; i < _order.Count; i++) Updated?.Invoke(_order[i], i + 1);
            Changed?.Invoke();
        }

        void Queue(Request r, int generation, int size)
        {
            Local.Add(() =>
            {
                if (_disposed || generation != _generation) return;
                Bitmap bmp = null;
                try
                {
                    var loc = r.FindInstalled?.Invoke();
                    if (loc != null) bmp = AppIcons.FromFile(loc, size);
                }
                catch { }
                if (bmp != null) { Deliver(r.Key, bmp, Quality.Installed, generation); return; }
                if (string.IsNullOrWhiteSpace(r.Homepage)) return;
                bmp = AppIcons.Cached(r.Homepage, size);
                if (bmp != null) { Deliver(r.Key, bmp, Quality.Web, generation); return; }
                if (!Settings.Current.WebIcons || AppIcons.KnownMissing(r.Homepage)) return;
                Remote.Add(() =>
                {
                    if (_disposed || generation != _generation || !Settings.Current.WebIcons) return;
                    var site = AppIcons.SiteKey(r.Homepage) ?? r.Homepage;
                    lock (SiteLocks.GetOrAdd(site, _ => new object()))   // one download per site; the rest use the cache
                    {
                        Bitmap web = null;
                        try { web = AppIcons.Cached(r.Homepage, size) ?? (AppIcons.KnownMissing(r.Homepage) ? null : AppIcons.FetchSiteIcon(r.Homepage, size, () => _disposed)); }
                        catch { }
                        if (web != null) Deliver(r.Key, web, Quality.Web, generation);
                    }
                });
            });
        }

        readonly ConcurrentQueue<(string key, Bitmap bmp, Quality q, int generation)> _pending = new ConcurrentQueue<(string, Bitmap, Quality, int)>();

        void Deliver(string key, Bitmap bmp, Quality q, int generation)
        {
            if (_disposed || _owner.IsDisposed) { bmp.Dispose(); return; }
            _pending.Enqueue((key, bmp, q, generation));
            // Before the control has a window (a tab not shown yet), results wait; HandleCreated flushes them.
            if (_owner.IsHandleCreated) { try { _owner.BeginInvoke((Action)Flush); } catch { } }
        }

        void Flush()
        {
            bool any = false;
            while (_pending.TryDequeue(out var p))
            {
                if (_disposed || p.generation != _generation || (_quality.TryGetValue(p.key, out var have) && have > p.q)) { p.bmp.Dispose(); continue; }
                Put(p.key, p.bmp, p.q);
                any = true;
                Updated?.Invoke(p.key, IndexOf(p.key));
            }
            if (any) Changed?.Invoke();
        }

        /// <summary>Raised once per batch of updated icons (repaint here).</summary>
        public event Action Changed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var b in _shown.Values) b.Dispose();
            _shown.Clear();
        }

        // ---- shared workers: one for local files, four for downloads ---------------------------------

        static readonly ConcurrentDictionary<string, object> SiteLocks = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        static readonly WorkQueue Local = new WorkQueue("icons", 1);
        static readonly WorkQueue Remote = new WorkQueue("icons-web", 4);

        sealed class WorkQueue
        {
            readonly BlockingCollection<Action> _jobs = new BlockingCollection<Action>();
            public WorkQueue(string name, int threads)
            {
                for (int i = 0; i < threads; i++)
                {
                    var t = new Thread(() => { foreach (var job in _jobs.GetConsumingEnumerable()) { try { job(); } catch { } } })
                    { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
                    t.SetApartmentState(ApartmentState.STA);   // shell icon and shortcut calls want STA
                    t.Start();
                }
            }
            public void Add(Action a) => _jobs.Add(a);
        }
    }
}
