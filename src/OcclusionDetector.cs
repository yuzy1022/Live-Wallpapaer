using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LiveWallpaper
{
    /// <summary>
    /// Decides per monitor whether the wallpaper is hidden behind other windows
    /// (or, optionally, whether another program is in use on that monitor).
    /// </summary>
    internal static class OcclusionDetector
    {
        static readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;

        static readonly HashSet<string> ShellClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
            "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
            "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "ForegroundStaging",
        };

        const int GridX = 48, GridY = 27;

        public static bool[] Evaluate(IList<ScreenWallpaper> screens, Settings s)
        {
            var result = new bool[screens.Count];
            if (screens.Count == 0) return result;

            if (s.PauseWhenCovered)
            {
                var rects = CollectWindowRects();
                for (int i = 0; i < screens.Count; i++)
                {
                    double covered = Coverage(screens[i].Screen.WorkingArea, rects);
                    if (covered * 100.0 >= s.CoverThreshold) result[i] = true;
                }
            }

            if (s.PauseWhenOtherAppActive)
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero && !IsDesktopLike(fg))
                {
                    RECT r;
                    if (GetBounds(fg, out r))
                    {
                        var center = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
                        for (int i = 0; i < screens.Count; i++)
                            if (screens[i].Screen.Bounds.Contains(center)) result[i] = true;
                    }
                }
            }
            return result;
        }

        static bool IsDesktopLike(IntPtr h)
        {
            if (Native.IsIconic(h)) return true;
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid == ownPid) return true;
            return ShellClasses.Contains(Native.ClassOf(h));
        }

        static List<RECT> CollectWindowRects()
        {
            var rects = new List<RECT>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;

                int cloaked;
                if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true;

                int ex = Native.ExStyleOf(h);
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
                if ((ex & Native.WS_EX_LAYERED) != 0)
                {
                    // Click-through overlays (game/GPU overlays etc.) don't really hide anything.
                    if ((ex & Native.WS_EX_TRANSPARENT) != 0) return true;
                    uint key, flags; byte alpha;
                    if (Native.GetLayeredWindowAttributes(h, out key, out alpha, out flags) && (flags & Native.LWA_ALPHA) != 0 && alpha < 200)
                        return true;
                }

                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pid == ownPid) return true;
                if (ShellClasses.Contains(Native.ClassOf(h))) return true;

                RECT r;
                if (!GetBounds(h, out r) || r.Width <= 0 || r.Height <= 0) return true;
                rects.Add(r);
                return true;
            }, IntPtr.Zero);
            return rects;
        }

        static bool GetBounds(IntPtr h, out RECT r)
        {
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) == 0)
                return true;
            return Native.GetWindowRect(h, out r);
        }

        /// <summary>Fraction (0..1) of the area hidden under the union of the given windows (grid sampling).</summary>
        static double Coverage(Rectangle area, List<RECT> rects)
        {
            if (area.Width <= 0 || area.Height <= 0 || rects.Count == 0) return 0;
            int hits = 0;
            for (int gy = 0; gy < GridY; gy++)
            {
                int py = area.Top + (int)((gy + 0.5) * area.Height / GridY);
                for (int gx = 0; gx < GridX; gx++)
                {
                    int px = area.Left + (int)((gx + 0.5) * area.Width / GridX);
                    for (int i = 0; i < rects.Count; i++)
                    {
                        RECT r = rects[i];
                        if (px >= r.Left && px < r.Right && py >= r.Top && py < r.Bottom) { hits++; break; }
                    }
                }
            }
            return hits / (double)(GridX * GridY);
        }
    }
}
