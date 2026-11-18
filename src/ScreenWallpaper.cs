using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using WF = System.Windows.Forms;

namespace LiveWallpaper
{
    /// <summary>One wallpaper surface covering one monitor.</summary>
    internal sealed class ScreenWallpaper : IDisposable
    {
        readonly DesktopHost host;
        readonly ContainerWindow container;
        readonly HwndSource source;
        readonly Grid root;
        Settings settings;
        IMediaLayer current, incoming, outgoing;
        Image snapshot;
        bool shown, disposed;
        string pendingPath;
        TimeSpan resumePosition;

        public readonly WF.Screen Screen;
        public readonly int Index;
        public string CurrentPath { get; private set; }
        public bool IsPaused { get; private set; }
        public bool IsUnloaded { get; private set; }
        public DateTime PausedAt { get; private set; }

        public double Elapsed;
        public bool WaitingForEnd;
        public DateTime WaitingSince;

        public event Action<ScreenWallpaper> EndedWhileWaiting;
        public event Action<ScreenWallpaper, string> LoadFailed;

        public ScreenWallpaper(DesktopHost host, WF.Screen screen, int index, Settings settings)
        {
            this.host = host;
            this.settings = settings;
            Screen = screen;
            Index = index;

            container = new ContainerWindow(host.Parent, host.Raised);

            var b = screen.Bounds;
            var p = new HwndSourceParameters("LiveWallpaperView", Math.Max(1, b.Width), Math.Max(1, b.Height));
            p.ParentWindow = container.Handle;
            p.WindowStyle = Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPSIBLINGS;
            source = new HwndSource(p);

            root = new Grid();
            root.Background = Brushes.Black;
            root.ClipToBounds = true;
            source.RootVisual = root;

            UpdatePosition();
        }

        public IntPtr Handle { get { return container.Handle; } }

        public bool IsAlive
        {
            get
            {
                IntPtr h = container.Handle;
                return h != IntPtr.Zero && Native.IsWindow(h) && Native.GetParent(h) == host.Parent;
            }
        }

        public bool IsAnimated { get { return current != null && current.IsAnimated; } }

        public void UpdatePosition()
        {
            var b = Screen.Bounds;
            var r = new RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
            Native.MapWindowPoints(IntPtr.Zero, host.Parent, ref r, 2);
            Native.SetWindowPos(container.Handle, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Native.SetWindowPos(source.Handle, IntPtr.Zero, 0, 0, r.Width, r.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        public void ApplySettings(Settings s)
        {
            settings = s;
            if (current != null) current.Apply(s);
            if (incoming != null) incoming.Apply(s);
        }

        /// <summary>Switches to a file. While paused, the switch is remembered and done on resume.</summary>
        public void Show(string path)
        {
            if (disposed || path == null) return;
            if (IsPaused)
            {
                pendingPath = path;
                return;
            }
            if (path == CurrentPath && current != null && incoming == null) return;
            pendingPath = null;
            StartLayer(path, TimeSpan.Zero, settings.FadeMs, true);
        }

        void StartLayer(string path, TimeSpan start, int fadeMs, bool resetElapsed)
        {
            CurrentPath = path;
            if (resetElapsed) { Elapsed = 0; WaitingForEnd = false; }
            if (incoming != null) { DropLayer(incoming); incoming = null; }

            IMediaLayer layer;
            try
            {
                layer = MediaFactory.Create(path, settings, Screen.Bounds.Width, start);
            }
            catch (Exception ex)
            {
                Log.Write("Cannot create layer: " + ex.Message);
                RaiseFailed(path);
                return;
            }

            layer.Element.Opacity = 0;
            layer.Element.Tag = fadeMs;
            root.Children.Add(layer.Element);
            incoming = layer;
            layer.Ready += OnReady;
            layer.Failed += OnFailed;
            layer.Ended += OnEnded;
            try { layer.Load(); }
            catch (Exception ex)
            {
                Log.Write("Load threw: " + path + " - " + ex.Message);
                OnFailed(layer);
            }
        }

        void OnReady(IMediaLayer layer)
        {
            if (layer != incoming || disposed) return;
            incoming = null;

            // A previous fade still running: end it now (keep the still snapshot as the fade base).
            if (outgoing != null) { DropLayer(outgoing); outgoing = null; }
            bool first = current == null && snapshot == null;
            outgoing = current;
            current = layer;
            if (IsPaused) layer.Pause();

            int fade = layer.Element.Tag is int ? (int)layer.Element.Tag : settings.FadeMs;
            EnsureShown();

            if (first || fade <= 0)
            {
                layer.Element.Opacity = 1;
                FinishOutgoing();
                return;
            }
            var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(fade));
            anim.Completed += delegate { if (current == layer) FinishOutgoing(); };
            layer.Element.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        void OnFailed(IMediaLayer layer)
        {
            if (disposed) return;
            string path = layer.Path;
            if (layer == incoming) incoming = null;
            if (layer == current) current = null;
            if (layer == outgoing) outgoing = null;
            DropLayer(layer);
            RaiseFailed(path);
        }

        void RaiseFailed(string path)
        {
            var h = LoadFailed;
            if (h != null) MediaFactory.Async(delegate { if (!disposed) h(this, path); });
        }

        void OnEnded(IMediaLayer layer)
        {
            if (layer != current || !WaitingForEnd) return;
            WaitingForEnd = false;
            var h = EndedWhileWaiting;
            if (h != null) h(this);
        }

        void FinishOutgoing()
        {
            if (outgoing != null) { DropLayer(outgoing); outgoing = null; }
            if (snapshot != null) { root.Children.Remove(snapshot); snapshot.Source = null; snapshot = null; }
            if (current != null)
            {
                current.Element.BeginAnimation(UIElement.OpacityProperty, null);
                current.Element.Opacity = 1;
            }
        }

        void DropLayer(IMediaLayer layer)
        {
            layer.Ready -= OnReady;
            layer.Failed -= OnFailed;
            layer.Ended -= OnEnded;
            root.Children.Remove(layer.Element);
            layer.Dispose();
        }

        void EnsureShown()
        {
            if (shown) return;
            shown = true;
            Native.ShowWindow(container.Handle, Native.SW_SHOWNOACTIVATE);
            host.ArrangeZOrder(new[] { container.Handle });
        }

        public void Pause()
        {
            if (IsPaused || disposed) return;
            IsPaused = true;
            PausedAt = DateTime.Now;
            if (incoming != null)
            {
                // Don't finish loading something nobody can see; load it on resume instead.
                if (pendingPath == null) pendingPath = incoming.Path;
                DropLayer(incoming);
                incoming = null;
            }
            FinishOutgoing();
            if (current != null) current.Pause();
        }

        public void Resume()
        {
            if (!IsPaused || disposed) return;
            IsPaused = false;
            if (IsUnloaded)
            {
                IsUnloaded = false;
                string path = pendingPath ?? CurrentPath;
                TimeSpan pos = pendingPath != null ? TimeSpan.Zero : resumePosition;
                bool reset = pendingPath != null;
                pendingPath = null;
                if (path != null) StartLayer(path, pos, snapshot != null ? 250 : 0, reset);
            }
            else if (pendingPath != null)
            {
                string path = pendingPath;
                pendingPath = null;
                StartLayer(path, TimeSpan.Zero, settings.FadeMs, true);
            }
            else if (current != null)
            {
                current.Play();
            }
        }

        /// <summary>Releases the decoder and frames; keeps a small still image so resuming doesn't flash black.</summary>
        public void Unload()
        {
            if (!IsPaused || IsUnloaded || disposed) return;
            IsUnloaded = true;
            FinishOutgoing();
            if (current == null) return;

            resumePosition = current.Position;
            ImageSource still = TakeSnapshot();
            if (still != null)
            {
                snapshot = new Image();
                snapshot.Stretch = Stretch.Fill;
                snapshot.Source = still;
                root.Children.Insert(0, snapshot);
            }
            DropLayer(current);
            current = null;
        }

        ImageSource TakeSnapshot()
        {
            try
            {
                double w = root.ActualWidth, h = root.ActualHeight;
                if (w < 1 || h < 1) return null;
                double scale = Math.Min(1.0, 1280.0 / w);
                int pw = Math.Max(1, (int)(w * scale)), ph = Math.Max(1, (int)(h * scale));
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    var brush = new VisualBrush(root);
                    brush.Stretch = Stretch.Fill;
                    dc.DrawRectangle(brush, null, new Rect(0, 0, pw, ph));
                }
                var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            catch (Exception ex)
            {
                Log.Write("Snapshot failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Removes all content and hides the surface so the normal wallpaper shows.</summary>
        public void Clear()
        {
            if (incoming != null) { DropLayer(incoming); incoming = null; }
            if (current != null) { DropLayer(current); current = null; }
            FinishOutgoing();
            CurrentPath = null;
            pendingPath = null;
            if (shown && container.Handle != IntPtr.Zero) Native.ShowWindow(container.Handle, Native.SW_HIDE);
            shown = false;
        }

        public void Dispose()
        {
            if (disposed) return;
            try { Clear(); } catch { }
            disposed = true;
            try { source.Dispose(); } catch { }
            try { if (container.Handle != IntPtr.Zero) container.DestroyHandle(); } catch { }
        }
    }
}
