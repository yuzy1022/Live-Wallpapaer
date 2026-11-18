using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Gdi = System.Drawing;
using GdiImaging = System.Drawing.Imaging;

namespace LiveWallpaper
{
    internal enum MediaKind { None, Video, Gif, Image }

    internal interface IMediaLayer : IDisposable
    {
        FrameworkElement Element { get; }
        string Path { get; }
        /// <summary>Video or multi-frame GIF.</summary>
        bool IsAnimated { get; }
        TimeSpan Position { get; }
        event Action<IMediaLayer> Ready;
        /// <summary>Raised each time one full play-through finishes (the layer keeps looping).</summary>
        event Action<IMediaLayer> Ended;
        event Action<IMediaLayer> Failed;
        void Load();
        void Play();
        void Pause();
        void Apply(Settings s);
    }

    internal static class MediaFactory
    {
        static readonly string[] VideoExt = { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".3gp" };
        static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff", ".jfif" };

        public static string FileDialogFilter
        {
            get
            {
                var all = new System.Collections.Generic.List<string>();
                foreach (var e in VideoExt) all.Add("*" + e);
                all.Add("*.gif");
                foreach (var e in ImageExt) all.Add("*" + e);
                return "영상 / GIF / 이미지|" + string.Join(";", all) + "|모든 파일|*.*";
            }
        }

        public static MediaKind KindOf(string path)
        {
            string ext = System.IO.Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return MediaKind.None;
            ext = ext.ToLowerInvariant();
            if (ext == ".gif") return MediaKind.Gif;
            if (Array.IndexOf(VideoExt, ext) >= 0) return MediaKind.Video;
            if (Array.IndexOf(ImageExt, ext) >= 0) return MediaKind.Image;
            return MediaKind.None;
        }

        public static IMediaLayer Create(string path, Settings s, int targetWidth, TimeSpan start)
        {
            switch (KindOf(path))
            {
                case MediaKind.Video: return new VideoLayer(path, s, start);
                case MediaKind.Gif: return new GifLayer(path, s);
                case MediaKind.Image: return new ImageLayer(path, s, targetWidth);
            }
            throw new NotSupportedException("Unsupported file: " + path);
        }

        public static Stretch ToStretch(int mode)
        {
            if (mode == 1) return Stretch.Uniform;
            if (mode == 2) return Stretch.Fill;
            return Stretch.UniformToFill;
        }

        public static void Async(Action a)
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal, a);
        }
    }

    internal sealed class VideoLayer : IMediaLayer
    {
        readonly MediaElement media = new MediaElement();
        readonly string path;
        readonly TimeSpan start;
        bool ready, disposed;

        public event Action<IMediaLayer> Ready;
        public event Action<IMediaLayer> Ended;
        public event Action<IMediaLayer> Failed;

        public VideoLayer(string path, Settings s, TimeSpan start)
        {
            this.path = path;
            this.start = start;
            media.LoadedBehavior = MediaState.Manual;
            media.UnloadedBehavior = MediaState.Manual;
            media.ScrubbingEnabled = false;
            RenderOptions.SetBitmapScalingMode(media, BitmapScalingMode.Linear);
            media.MediaOpened += OnOpened;
            media.MediaEnded += OnEnded;
            media.MediaFailed += OnFailed;
            Apply(s);
        }

        public FrameworkElement Element { get { return media; } }
        public string Path { get { return path; } }
        public bool IsAnimated { get { return true; } }

        public TimeSpan Position
        {
            get { try { return media.Position; } catch { return TimeSpan.Zero; } }
        }

        public void Load()
        {
            media.Source = new Uri(path, UriKind.Absolute);
            media.Play();
        }

        public void Play() { if (!disposed) media.Play(); }
        public void Pause() { if (!disposed) media.Pause(); }

        public void Apply(Settings s)
        {
            media.Stretch = MediaFactory.ToStretch(s.StretchMode);
            media.IsMuted = s.Muted;
            media.Volume = s.Volume / 100.0;
        }

        void OnOpened(object sender, RoutedEventArgs e)
        {
            if (disposed) return;
            if (start > TimeSpan.Zero && media.NaturalDuration.HasTimeSpan && start < media.NaturalDuration.TimeSpan)
                media.Position = start;
            if (ready) return;
            ready = true;
            var h = Ready; if (h != null) h(this);
        }

        void OnEnded(object sender, RoutedEventArgs e)
        {
            if (disposed) return;
            media.Position = TimeSpan.Zero;
            media.Play();
            var h = Ended; if (h != null) h(this);
        }

        void OnFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (disposed) return;
            Log.Write("Video failed: " + path + " - " + (e.ErrorException != null ? e.ErrorException.Message : "unknown"));
            var h = Failed; if (h != null) h(this);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            media.MediaOpened -= OnOpened;
            media.MediaEnded -= OnEnded;
            media.MediaFailed -= OnFailed;
            try { media.Stop(); media.Close(); media.Source = null; } catch { }
        }
    }

    /// <summary>
    /// Animated GIF player. GDI+ composites each frame (handles GIF disposal rules); frames are copied
    /// into one reusable WriteableBitmap, so memory stays at a single frame instead of all frames.
    /// </summary>
    internal sealed class GifLayer : IMediaLayer
    {
        readonly Image image = new Image();
        readonly string path;
        readonly Stopwatch clock = new Stopwatch();
        MemoryStream stream;
        Gdi.Bitmap gif;
        WriteableBitmap target;
        DispatcherTimer timer;
        int[] delays;
        int frameCount = 1, frame, width, height;
        long due;
        bool playing, disposed;

        public event Action<IMediaLayer> Ready;
        public event Action<IMediaLayer> Ended;
        public event Action<IMediaLayer> Failed;

        public GifLayer(string path, Settings s)
        {
            this.path = path;
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
            Apply(s);
        }

        public FrameworkElement Element { get { return image; } }
        public string Path { get { return path; } }
        public bool IsAnimated { get { return frameCount > 1; } }
        public TimeSpan Position { get { return TimeSpan.Zero; } }

        public void Apply(Settings s) { image.Stretch = MediaFactory.ToStretch(s.StretchMode); }

        public void Load()
        {
            try
            {
                stream = new MemoryStream(File.ReadAllBytes(path));
                gif = new Gdi.Bitmap(stream);
                width = gif.Width;
                height = gif.Height;
                try { frameCount = Math.Max(1, gif.GetFrameCount(GdiImaging.FrameDimension.Time)); } catch { frameCount = 1; }

                delays = new int[frameCount];
                try
                {
                    var item = gif.GetPropertyItem(0x5100); // PropertyTagFrameDelay, 1/100 s per frame
                    for (int i = 0; i < frameCount && i * 4 + 3 < item.Value.Length; i++)
                        delays[i] = BitConverter.ToInt32(item.Value, i * 4) * 10;
                }
                catch { }
                for (int i = 0; i < frameCount; i++)
                    if (delays[i] < 20) delays[i] = 100; // same rule browsers use for 0/10 ms frames

                target = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                image.Source = target;
                frame = 0;
                Render(0);
            }
            catch (Exception ex)
            {
                Log.Write("GIF failed: " + path + " - " + ex.Message);
                MediaFactory.Async(delegate { if (!disposed) { var f = Failed; if (f != null) f(this); } });
                return;
            }

            if (frameCount > 1)
            {
                timer = new DispatcherTimer(DispatcherPriority.Render);
                timer.Tick += OnTick;
            }
            MediaFactory.Async(delegate { if (!disposed) { var r = Ready; if (r != null) r(this); } });
            Play();
        }

        void Render(int index)
        {
            if (frameCount > 1) gif.SelectActiveFrame(GdiImaging.FrameDimension.Time, index);
            var rect = new Gdi.Rectangle(0, 0, width, height);
            var data = gif.LockBits(rect, GdiImaging.ImageLockMode.ReadOnly, GdiImaging.PixelFormat.Format32bppArgb);
            try
            {
                target.WritePixels(new Int32Rect(0, 0, width, height), data.Scan0, data.Stride * height, data.Stride);
            }
            finally { gif.UnlockBits(data); }
        }

        void OnTick(object sender, EventArgs e)
        {
            if (!playing || disposed) return;
            long now = clock.ElapsedMilliseconds;
            bool looped = false;
            int guard = 0;
            while (now >= due && guard++ < frameCount)
            {
                frame++;
                if (frame >= frameCount) { frame = 0; looped = true; }
                due += delays[frame];
            }
            if (now - due > 1000) due = now + delays[frame]; // fell far behind: resync
            try { Render(frame); }
            catch (Exception ex) { Log.Write("GIF render failed: " + ex.Message); }
            Schedule();
            if (looped) { var h = Ended; if (h != null) h(this); }
        }

        void Schedule()
        {
            if (timer == null) return;
            long wait = due - clock.ElapsedMilliseconds;
            if (wait < 1) wait = 1;
            timer.Interval = TimeSpan.FromMilliseconds(wait);
        }

        public void Play()
        {
            if (disposed || timer == null || playing) return;
            playing = true;
            clock.Start();
            due = clock.ElapsedMilliseconds + delays[frame];
            Schedule();
            timer.Start();
        }

        public void Pause()
        {
            playing = false;
            if (timer != null) timer.Stop();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Pause();
            if (timer != null) timer.Tick -= OnTick;
            image.Source = null;
            target = null;
            if (gif != null) gif.Dispose();
            if (stream != null) stream.Dispose();
        }
    }

    internal sealed class ImageLayer : IMediaLayer
    {
        readonly Image image = new Image();
        readonly string path;
        readonly int targetWidth;
        bool disposed;

        public event Action<IMediaLayer> Ready;
        public event Action<IMediaLayer> Ended { add { } remove { } }
        public event Action<IMediaLayer> Failed;

        public ImageLayer(string path, Settings s, int targetWidth)
        {
            this.path = path;
            this.targetWidth = targetWidth;
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            Apply(s);
        }

        public FrameworkElement Element { get { return image; } }
        public string Path { get { return path; } }
        public bool IsAnimated { get { return false; } }
        public TimeSpan Position { get { return TimeSpan.Zero; } }

        public void Apply(Settings s) { image.Stretch = MediaFactory.ToStretch(s.StretchMode); }

        public void Load()
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    int pixelWidth = 0;
                    try
                    {
                        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                        pixelWidth = dec.Frames[0].PixelWidth;
                    }
                    catch { }
                    fs.Position = 0;

                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    // Decode huge photos at screen size to keep memory down.
                    if (targetWidth > 0 && pixelWidth > targetWidth * 2) bi.DecodePixelWidth = targetWidth * 2;
                    bi.StreamSource = fs;
                    bi.EndInit();
                    bi.Freeze();
                    image.Source = bi;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Image failed: " + path + " - " + ex.Message);
                MediaFactory.Async(delegate { if (!disposed) { var f = Failed; if (f != null) f(this); } });
                return;
            }
            MediaFactory.Async(delegate { if (!disposed) { var r = Ready; if (r != null) r(this); } });
        }

        public void Play() { }
        public void Pause() { }

        public void Dispose()
        {
            disposed = true;
            image.Source = null;
        }
    }
}
