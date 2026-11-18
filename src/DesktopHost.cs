using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace LiveWallpaper
{
    /// <summary>
    /// Finds the window that sits between the desktop icons and the regular wallpaper,
    /// so our video windows can live there.
    ///  - Windows 11 24H2 and later: a WorkerW is a child of Progman, below SHELLDLL_DefView (the icons).
    ///    Our windows become children of Progman, placed between the icons and that WorkerW.
    ///  - Older builds: a separate top-level WorkerW behind the icons; our windows become its children.
    /// </summary>
    internal sealed class DesktopHost
    {
        public IntPtr Progman { get; private set; }
        public IntPtr Parent { get; private set; }
        public IntPtr DefView { get; private set; }
        public IntPtr WorkerW { get; private set; }
        /// <summary>True for the 24H2+ layout, where our windows must be layered children of Progman.</summary>
        public bool Raised { get; private set; }

        public bool Init()
        {
            Progman = Native.FindWindow("Progman", null);
            if (Progman == IntPtr.Zero) return false;

            IntPtr result;
            // Ask Progman to create the WorkerW that sits behind the desktop icons.
            Native.SendMessageTimeout(Progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), 0, 1000, out result);
            Native.SendMessageTimeout(Progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out result);

            for (int attempt = 0; attempt < 30; attempt++)
            {
                IntPtr child = Native.FindWindowEx(Progman, IntPtr.Zero, "WorkerW", null);
                IntPtr defView = Native.FindWindowEx(Progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                // 24H2: only attach once the icon layer is there too. Without it we can't place our
                // windows below the icons, and they would end up covering them (and eating clicks).
                if (child != IntPtr.Zero && defView != IntPtr.Zero)
                {
                    Raised = true;
                    WorkerW = child;
                    Parent = Progman;
                    DefView = defView;
                    Log.Write("Desktop host: 24H2 layout (Progman child WorkerW)");
                    return true;
                }

                IntPtr legacy = FindLegacyWorkerW();
                if (legacy != IntPtr.Zero)
                {
                    Raised = false;
                    WorkerW = legacy;
                    Parent = legacy;
                    DefView = IntPtr.Zero;
                    Log.Write("Desktop host: legacy layout (top-level WorkerW)");
                    return true;
                }
                Thread.Sleep(100);
            }

            // Last resort: draw directly inside Progman, below the icons - but only if the icons
            // are there to go below. Otherwise give up; the caller retries a bit later.
            DefView = Native.FindWindowEx(Progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (DefView == IntPtr.Zero) return false;
            Raised = true;
            Parent = Progman;
            WorkerW = IntPtr.Zero;
            Log.Write("Desktop host: fallback to Progman");
            return true;
        }

        static IntPtr FindLegacyWorkerW()
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr top, IntPtr l)
            {
                IntPtr defView = Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero)
                {
                    // The WorkerW right after the one hosting the icons is the wallpaper layer.
                    found = Native.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>Puts our windows below the icons and the original wallpaper below ours.</summary>
        public void ArrangeZOrder(IEnumerable<IntPtr> ours)
        {
            if (!Raised) return;
            // Explorer may have re-created the icon layer since Init; always use the current one.
            IntPtr current = Native.FindWindowEx(Parent, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (current != IntPtr.Zero) DefView = current;
            foreach (IntPtr h in ours)
            {
                if (h == IntPtr.Zero) continue;
                if (DefView != IntPtr.Zero && Native.GetParent(DefView) == Parent)
                    Native.SetWindowPos(h, DefView, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            if (WorkerW != IntPtr.Zero)
                Native.SetWindowPos(WorkerW, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public bool IsValid
        {
            get { return Parent != IntPtr.Zero && Native.IsWindow(Parent) && Native.FindWindow("Progman", null) == Progman; }
        }

        /// <summary>
        /// True when every one of our visible windows is still below the desktop icons.
        /// Explorer can move or re-create the icon layer (display changes, GPU resets, its own restart)
        /// without our windows being destroyed, so parent checks alone don't catch this.
        /// </summary>
        public bool IsBelowIcons(IEnumerable<IntPtr> ours)
        {
            if (!Raised)
            {
                // Legacy layout: our parent must still be the WorkerW right behind the icons.
                return FindLegacyWorkerW() == Parent;
            }

            IntPtr defView = Native.FindWindowEx(Parent, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero) return false;   // icons moved elsewhere: we can't be sure
            DefView = defView;

            var pending = new HashSet<IntPtr>();
            foreach (IntPtr h in ours)
                if (h != IntPtr.Zero && Native.IsWindowVisible(h)) pending.Add(h);
            if (pending.Count == 0) return true;

            // Walk Progman's children from the top; DefView must come before all of ours.
            for (IntPtr c = Native.GetWindow(Parent, Native.GW_CHILD); c != IntPtr.Zero; c = Native.GetWindow(c, Native.GW_HWNDNEXT))
            {
                if (c == defView) return true;
                if (pending.Contains(c)) return false;
            }
            return false;
        }

        /// <summary>One-line dump of the children of our parent, top to bottom, for the log.</summary>
        public string DescribeOrder()
        {
            var sb = new System.Text.StringBuilder();
            IntPtr p = Parent != IntPtr.Zero ? Parent : Progman;
            int n = 0;
            for (IntPtr c = Native.GetWindow(p, Native.GW_CHILD); c != IntPtr.Zero && n < 20; c = Native.GetWindow(c, Native.GW_HWNDNEXT), n++)
            {
                var cls = new System.Text.StringBuilder(64);
                Native.GetClassName(c, cls, 64);
                if (sb.Length > 0) sb.Append(" > ");
                sb.Append(cls).Append(Native.IsWindowVisible(c) ? "" : "(hidden)");
            }
            return sb.ToString();
        }

        /// <summary>Repaints the original wallpaper after our windows are gone.</summary>
        public void Repaint()
        {
            try
            {
                IntPtr target = Parent != IntPtr.Zero ? Parent : Progman;
                if (target != IntPtr.Zero && Native.IsWindow(target))
                    Native.RedrawWindow(target, IntPtr.Zero, IntPtr.Zero,
                        Native.RDW_INVALIDATE | Native.RDW_ERASE | Native.RDW_ALLCHILDREN | Native.RDW_UPDATENOW);
            }
            catch { }
        }
    }

    /// <summary>Plain Win32 child window living inside the desktop; hosts the WPF view.</summary>
    internal sealed class ContainerWindow : NativeWindow
    {
        public ContainerWindow(IntPtr parent, bool layered)
        {
            var cp = new CreateParams();
            cp.Caption = "LiveWallpaperHost";
            cp.Parent = parent;
            cp.Style = Native.WS_CHILD | Native.WS_CLIPCHILDREN | Native.WS_CLIPSIBLINGS;
            cp.ExStyle = Native.WS_EX_NOACTIVATE | (layered ? Native.WS_EX_LAYERED : 0);
            CreateHandle(cp);
            if (layered) Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_ERASEBKGND = 0x0014;
            const int WM_MOUSEACTIVATE = 0x0021;
            if (m.Msg == WM_ERASEBKGND) { m.Result = new IntPtr(1); return; }
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref m);
        }
    }
}
