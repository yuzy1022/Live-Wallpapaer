using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace LiveWallpaper
{
    /// <summary>Owns the monitors' wallpaper surfaces, the playlist, the slideshow clock and the pause logic.</summary>
    internal sealed class WallpaperController : IDisposable
    {
        readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        readonly Playlist playlist = new Playlist();
        readonly List<ScreenWallpaper> screens = new List<ScreenWallpaper>();
        readonly List<Playlist.Cursor> cursors = new List<Playlist.Cursor>();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly DispatcherTimer tick, rebuildTimer, rescanTimer;
        Settings settings;
        DesktopHost host;
        FileSystemWatcher watcher;
        Playlist.Cursor syncCursor = new Playlist.Cursor();
        double syncElapsed;
        bool syncWaiting, sessionLocked, disposed, started;
        DateTime syncWaitingSince;
        long lastTickMs;
        int tickCount;

        string previewPath;

        public bool ManualPause;
        public event Action StatusChanged;
        /// <summary>A dropped file could not be played (argument: its path).</summary>
        public event Action<string> PreviewFailed;

        public WallpaperController(Settings settings)
        {
            this.settings = settings;
            tick = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            tick.Interval = TimeSpan.FromMilliseconds(500);
            tick.Tick += OnTick;
            rebuildTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
            rebuildTimer.Tick += delegate { rebuildTimer.Stop(); Build(); };
            rescanTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
            rescanTimer.Interval = TimeSpan.FromMilliseconds(1500);
            rescanTimer.Tick += delegate { rescanTimer.Stop(); Rescan(); };
        }

        bool Sync { get { return settings.MonitorMode != 1; } }

        public int FileCount { get { return playlist.Count; } }

        public bool IsAnythingPlaying
        {
            get
            {
                foreach (var s in screens) if (!s.IsPaused && s.CurrentPath != null) return true;
                return false;
            }
        }

        public string CurrentFileName
        {
            get
            {
                if (previewPath != null) return Path.GetFileName(previewPath);
                foreach (var s in screens) if (s.CurrentPath != null) return Path.GetFileName(s.CurrentPath);
                return null;
            }
        }

        public string Folder { get { return settings.Folder; } }

        public bool IsPreviewing { get { return previewPath != null; } }
        public bool IsInPlaylist(string path) { return playlist.Contains(path); }
        public string PreviewPath { get { return previewPath; } }

        // ---------------------------------------------------------------- preview (drag & drop)

        /// <summary>Plays one file on the wallpaper until EndPreview / Next is called. The slideshow waits meanwhile.</summary>
        public void StartPreview(string path)
        {
            if (disposed || path == null || MediaFactory.KindOf(path) == MediaKind.None || !File.Exists(path)) return;
            previewPath = path;
            ManualPause = false;
            Log.Write("Preview: " + path);
            if (screens.Count == 0) Build();
            else
            {
                UpdatePauseStates();
                foreach (var s in screens) { s.WaitingForEnd = false; s.Show(path); }
            }
            RaiseStatus();
        }

        /// <summary>Leaves preview and goes back to the slideshow file that was showing before.</summary>
        public void EndPreview()
        {
            if (previewPath == null) return;
            previewPath = null;
            syncElapsed = 0;
            syncWaiting = false;
            if (playlist.PlayableCount == 0) DisposeScreens();
            else
            {
                foreach (var s in screens) s.Elapsed = 0;
                ShowInitial();
            }
            RaiseStatus();
        }

        public void Start()
        {
            if (started) return;
            started = true;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            Rescan(); // builds the screens when the folder has files
            lastTickMs = clock.ElapsedMilliseconds;
            tick.Start();
        }

        // ---------------------------------------------------------------- building

        void Build()
        {
            if (disposed) return;
            DisposeScreens();
            if (playlist.Count == 0 && previewPath == null) { RaiseStatus(); return; }

            host = new DesktopHost();
            if (!host.Init())
            {
                Log.Write("Desktop not found, retrying");
                host = null;
                RequestRebuild(3000);
                return;
            }

            WF.Screen[] list = settings.MonitorMode == 2 ? new[] { WF.Screen.PrimaryScreen } : WF.Screen.AllScreens;
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    var s = new ScreenWallpaper(host, list[i], i, settings);
                    s.EndedWhileWaiting += OnEndedWhileWaiting;
                    s.LoadFailed += OnLoadFailed;
                    screens.Add(s);
                }
                catch (Exception ex) { Log.Write("Screen " + i + " failed: " + ex); }
            }
            var handles = new List<IntPtr>();
            foreach (var s in screens) handles.Add(s.Handle);
            host.ArrangeZOrder(handles);
            Log.Write("Built " + screens.Count + " screen(s)");

            UpdatePauseStates();   // screens that are already covered don't even load
            ShowInitial();
            RaiseStatus();
        }

        void ShowInitial()
        {
            if (previewPath != null)
            {
                foreach (var s in screens) s.Show(previewPath);
                return;
            }
            if (playlist.PlayableCount == 0)
            {
                foreach (var s in screens) s.Clear();
                return;
            }
            if (Sync)
            {
                string path = playlist.Contains(syncCursor.Current) ? syncCursor.Current : playlist.Next(syncCursor, settings.Shuffle, 1);
                foreach (var s in screens) s.Show(path);
                syncElapsed = 0;
            }
            else
            {
                foreach (var s in screens)
                {
                    var c = CursorFor(s.Index);
                    string path = playlist.Contains(c.Current) ? c.Current : playlist.Start(c, settings.Shuffle, s.Index);
                    s.Show(path);
                }
            }
        }

        Playlist.Cursor CursorFor(int index)
        {
            while (cursors.Count <= index) cursors.Add(new Playlist.Cursor());
            return cursors[index];
        }

        public void RequestRebuild(int delayMs)
        {
            if (disposed) return;
            rebuildTimer.Stop();
            rebuildTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
            rebuildTimer.Start();
        }

        void DisposeScreens()
        {
            bool had = screens.Count > 0;
            foreach (var s in screens)
            {
                s.EndedWhileWaiting -= OnEndedWhileWaiting;
                s.LoadFailed -= OnLoadFailed;
                s.Dispose();
            }
            screens.Clear();
            if (had && host != null) host.Repaint();
            if (had) TrimMemory();
        }

        // ---------------------------------------------------------------- playlist / folder

        void Rescan()
        {
            if (disposed) return;
            bool wasEmpty = playlist.PlayableCount == 0;
            playlist.Scan(settings.Folder, settings.IncludeSubfolders);
            SetupWatcher();

            if (previewPath != null)
            {
                // Keep showing the preview; the slideshow picks up the new list afterwards.
                if (screens.Count == 0) Build();
            }
            else if (playlist.Count == 0)
            {
                DisposeScreens();
            }
            else if (screens.Count == 0)
            {
                Build();
            }
            else if (wasEmpty)
            {
                ShowInitial();
            }
            else
            {
                // A file that is on screen was deleted or renamed: move on.
                foreach (var s in screens.ToArray())
                    if (s.CurrentPath != null && !playlist.Contains(s.CurrentPath))
                    {
                        if (Sync) { AdvanceAll(1); break; }
                        AdvanceScreen(s, 1);
                    }
            }
            RaiseStatus();
        }

        void SetupWatcher()
        {
            if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); watcher = null; }
            if (string.IsNullOrEmpty(settings.Folder) || !Directory.Exists(settings.Folder)) return;
            try
            {
                watcher = new FileSystemWatcher(settings.Folder);
                watcher.IncludeSubdirectories = settings.IncludeSubfolders;
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName;
                FileSystemEventHandler changed = delegate { dispatcher.BeginInvoke(new Action(RestartRescanTimer)); };
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Renamed += delegate { dispatcher.BeginInvoke(new Action(RestartRescanTimer)); };
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Log.Write("Watcher: " + ex.Message); }
        }

        void RestartRescanTimer()
        {
            if (disposed) return;
            rescanTimer.Stop();
            rescanTimer.Start();
        }

        public void ApplySettings(Settings next)
        {
            Settings old = settings;
            settings = next;
            bool rescan = !string.Equals(old.Folder, next.Folder, StringComparison.OrdinalIgnoreCase) || old.IncludeSubfolders != next.IncludeSubfolders;
            bool rebuild = old.MonitorMode != next.MonitorMode;

            if (rescan)
            {
                syncCursor = new Playlist.Cursor();
                cursors.Clear();
                DisposeScreens();
                Rescan();
                if (screens.Count == 0 && playlist.Count > 0) Build();
                return;
            }
            if (rebuild) { Build(); return; }
            foreach (var s in screens) s.ApplySettings(next);
            UpdatePauseStates();
            RaiseStatus();
        }

        // ---------------------------------------------------------------- slideshow

        public void Next(int direction)
        {
            if (previewPath != null)
            {
                // Next/previous during a preview leaves the preview and moves on from the slideshow position.
                previewPath = null;
                if (playlist.PlayableCount == 0) { DisposeScreens(); RaiseStatus(); return; }
            }
            if (playlist.PlayableCount == 0) return;
            AdvanceAll(direction);
        }

        void AdvanceAll(int direction)
        {
            syncElapsed = 0;
            syncWaiting = false;
            if (Sync)
            {
                string path = playlist.Next(syncCursor, settings.Shuffle, direction);
                foreach (var s in screens)
                {
                    s.WaitingForEnd = false;
                    s.Elapsed = 0;
                    if (path != null) s.Show(path); else s.Clear();
                }
            }
            else
            {
                foreach (var s in screens) AdvanceScreen(s, direction);
            }
            RaiseStatus();
        }

        void AdvanceScreen(ScreenWallpaper s, int direction)
        {
            string path = playlist.Next(CursorFor(s.Index), settings.Shuffle, direction);
            s.Elapsed = 0;
            s.WaitingForEnd = false;
            if (path != null) s.Show(path); else s.Clear();
            RaiseStatus();
        }

        void Slideshow(double dt)
        {
            if (previewPath != null || playlist.PlayableCount <= 1 || screens.Count == 0) return;
            double interval = Math.Max(5, settings.IntervalSeconds);

            if (Sync)
            {
                ScreenWallpaper lead = null;
                foreach (var s in screens) if (!s.IsPaused) { lead = s; break; }
                if (lead == null) return; // nothing visible: the clock stops too

                if (syncWaiting)
                {
                    if ((DateTime.Now - syncWaitingSince).TotalMinutes > 15) AdvanceAll(1);
                    return;
                }
                syncElapsed += dt;
                if (syncElapsed < interval) return;
                if (settings.WaitForEnd && lead.IsAnimated)
                {
                    syncWaiting = true;
                    syncWaitingSince = DateTime.Now;
                    foreach (var s in screens) s.WaitingForEnd = s == lead;
                }
                else AdvanceAll(1);
                return;
            }

            foreach (var s in screens.ToArray())
            {
                if (s.IsPaused) continue;
                if (s.WaitingForEnd)
                {
                    if ((DateTime.Now - s.WaitingSince).TotalMinutes > 15) AdvanceScreen(s, 1);
                    continue;
                }
                s.Elapsed += dt;
                if (s.Elapsed < interval) continue;
                if (settings.WaitForEnd && s.IsAnimated)
                {
                    s.WaitingForEnd = true;
                    s.WaitingSince = DateTime.Now;
                }
                else AdvanceScreen(s, 1);
            }
        }

        void OnEndedWhileWaiting(ScreenWallpaper s)
        {
            if (Sync) AdvanceAll(1);
            else AdvanceScreen(s, 1);
        }

        void OnLoadFailed(ScreenWallpaper s, string path)
        {
            if (previewPath != null && string.Equals(path, previewPath, StringComparison.OrdinalIgnoreCase))
            {
                Log.Write("Preview could not be played: " + path);
                EndPreview();
                var h = PreviewFailed;
                if (h != null) h(path);
                return;
            }
            playlist.MarkFailed(path);
            Log.Write("Skipping unplayable file: " + path);
            if (playlist.PlayableCount == 0)
            {
                foreach (var x in screens) x.Clear();
                RaiseStatus();
                return;
            }
            if (Sync)
            {
                if (path == syncCursor.Current) AdvanceAll(1);
            }
            else if (s.CurrentPath == path) AdvanceScreen(s, 1);
        }

        // ---------------------------------------------------------------- pausing

        void OnTick(object sender, EventArgs e)
        {
            if (disposed) return;
            long now = clock.ElapsedMilliseconds;
            double dt = Math.Min(5.0, (now - lastTickMs) / 1000.0);
            lastTickMs = now;

            try
            {
                if (++tickCount % 6 == 0) CheckHealth();
                UpdatePauseStates();

                bool trimmed = false;
                if (settings.UnloadWhenPaused)
                {
                    foreach (var s in screens)
                    {
                        if (s.IsPaused && !s.IsUnloaded && (DateTime.Now - s.PausedAt).TotalSeconds >= settings.UnloadDelaySeconds)
                        {
                            s.Unload();
                            trimmed = true;
                        }
                    }
                }
                if (trimmed) { TrimMemory(); RaiseStatus(); }

                Slideshow(dt);
            }
            catch (Exception ex) { Log.Write("Tick: " + ex); }
        }

        public void RefreshNow()
        {
            UpdatePauseStates();
            RaiseStatus();
        }

        void UpdatePauseStates()
        {
            if (screens.Count == 0) return;
            bool global = ManualPause || sessionLocked ||
                (settings.PauseOnBattery && WF.SystemInformation.PowerStatus.PowerLineStatus == WF.PowerLineStatus.Offline);

            bool[] hidden = global ? null : OcclusionDetector.Evaluate(screens, settings);
            bool changed = false;
            for (int i = 0; i < screens.Count; i++)
            {
                var s = screens[i];
                bool pause = global || hidden[i];
                if (pause == s.IsPaused) continue;
                changed = true;
                if (pause) s.Pause(); else s.Resume();
            }
            if (changed) RaiseStatus();
        }

        void CheckHealth()
        {
            if (playlist.Count == 0 && previewPath == null) return;
            bool broken = host == null || !host.IsValid || screens.Count == 0;
            if (!broken) foreach (var s in screens) if (!s.IsAlive) { broken = true; break; }
            if (!broken)
            {
                // Still attached, but are we still *behind* the icons? If our window ends up on top,
                // the icons disappear and the desktop stops taking clicks.
                var handles = new List<IntPtr>();
                foreach (var s in screens) handles.Add(s.Handle);
                if (host.IsBelowIcons(handles)) return;

                Log.Write("Wallpaper is above the desktop icons, fixing. Order: " + host.DescribeOrder());
                host.ArrangeZOrder(handles);
                if (host.IsBelowIcons(handles)) { Log.Write("Order fixed: " + host.DescribeOrder()); return; }
                broken = true;
            }
            if (rebuildTimer.IsEnabled) return;
            Log.Write("Desktop changed (Explorer restarted?) - rebuilding");
            RequestRebuild(1000);
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                case SessionSwitchReason.ConsoleDisconnect:
                case SessionSwitchReason.RemoteDisconnect:
                    sessionLocked = true; break;
                case SessionSwitchReason.SessionUnlock:
                case SessionSwitchReason.ConsoleConnect:
                case SessionSwitchReason.RemoteConnect:
                    sessionLocked = false; break;
                default: return;
            }
            dispatcher.BeginInvoke(new Action(RefreshNow));
        }

        static void TrimMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
        }

        void RaiseStatus()
        {
            var h = StatusChanged;
            if (h != null) h();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            tick.Stop();
            rebuildTimer.Stop();
            rescanTimer.Stop();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); }
            DisposeScreens();
        }
    }
}
