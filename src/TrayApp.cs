using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace LiveWallpaper
{
    internal sealed class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem miCurrent, miPause, miEndPreview;
        readonly WallpaperController controller;
        readonly ShellMessageWindow messages;
        readonly Icon appIcon;
        Settings settings;
        SettingsForm form;

        public TrayApp()
        {
            settings = Settings.Load();
            if (settings.RunAtStartup) Startup.Apply(true); // keeps the path right if the exe was moved
            appIcon = LoadIcon();

            controller = new WallpaperController(settings.Clone());
            controller.StatusChanged += UpdateTooltip;
            controller.PreviewFailed += OnPreviewFailed;

            var menu = new ContextMenuStrip();
            miCurrent = new ToolStripMenuItem("재생 중인 파일 없음");
            miCurrent.Enabled = false;
            menu.Items.Add(miCurrent);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("다음 배경화면", null, delegate { controller.Next(1); });
            menu.Items.Add("이전 배경화면", null, delegate { controller.Next(-1); });
            miPause = new ToolStripMenuItem("일시정지", null, delegate { TogglePause(); });
            menu.Items.Add(miPause);
            miEndPreview = new ToolStripMenuItem("미리보기 끝내기", null, delegate { controller.EndPreview(); });
            menu.Items.Add(miEndPreview);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("배경화면 폴더 열기", null, delegate { OpenFolder(); });
            var miSettings = new ToolStripMenuItem("설정...", null, delegate { ShowSettings(); });
            miSettings.Font = new Font(miSettings.Font, FontStyle.Bold);
            menu.Items.Add(miSettings);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("종료", null, delegate { ExitApp(); });
            menu.Opening += delegate
            {
                string name = controller.CurrentFileName;
                if (name != null && name.Length > 48) name = name.Substring(0, 45) + "...";
                miCurrent.Text = name == null ? "재생 중인 파일 없음" : (controller.IsPreviewing ? "미리보기 중: " : "재생 중: ") + name;
                miPause.Checked = controller.ManualPause;
                miEndPreview.Visible = controller.IsPreviewing;
            };

            tray = new NotifyIcon();
            tray.Icon = appIcon;
            tray.Text = "Live Wallpaper";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowSettings(); };

            messages = new ShellMessageWindow(controller);
            controller.Start();
            UpdateTooltip();

            if (string.IsNullOrEmpty(settings.Folder) || !Directory.Exists(settings.Folder)) ShowSettings();
        }

        static Icon LoadIcon()
        {
            try
            {
                using (var s = typeof(TrayApp).Assembly.GetManifestResourceStream("LiveWallpaper.app.ico"))
                    if (s != null) return new Icon(s, SystemInformation.SmallIconSize);
            }
            catch (Exception ex) { Log.Write("Icon: " + ex.Message); }
            return SystemIcons.Application;
        }

        void TogglePause()
        {
            controller.ManualPause = !controller.ManualPause;
            controller.RefreshNow();
        }

        void OpenFolder()
        {
            if (Directory.Exists(settings.Folder)) Process.Start("explorer.exe", "\"" + settings.Folder + "\"");
            else ShowSettings();
        }

        public void ShowSettings()
        {
            if (form != null && !form.IsDisposed)
            {
                if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
                form.Activate();
                return;
            }
            form = new SettingsForm(settings.Clone(), appIcon, controller);
            form.Applied += delegate(Settings s)
            {
                settings = s;
                s.Save();
                controller.ApplySettings(s.Clone());
            };
            form.FormClosed += delegate { form = null; };
            form.Show();
            form.Activate();
        }

        public void Preview(string path)
        {
            controller.StartPreview(path);
        }

        void OnPreviewFailed(string path)
        {
            if (form != null && !form.IsDisposed) form.ShowPreviewError(path);
            else tray.ShowBalloonTip(4000, "Live Wallpaper", "재생할 수 없는 파일입니다: " + Path.GetFileName(path), ToolTipIcon.Warning);
        }

        void UpdateTooltip()
        {
            if (tray == null) return;
            string state = controller.IsPreviewing ? "미리보기 중"
                : controller.ManualPause ? "일시정지됨"
                : controller.FileCount == 0 ? "폴더에 파일 없음"
                : controller.IsAnythingPlaying ? "재생 중" : "가려져서 멈춤";
            string text = "Live Wallpaper - " + state + " (" + controller.FileCount + "개)";
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        void ExitApp()
        {
            if (form != null && !form.IsDisposed) form.Close();
            tray.Visible = false;
            controller.Dispose();
            messages.DestroyHandle();
            tray.Dispose();
            ExitThread();
        }
    }

    /// <summary>Hidden window that hears about Explorer restarts and display changes.</summary>
    internal sealed class ShellMessageWindow : NativeWindow
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        readonly WallpaperController controller;
        readonly int taskbarCreated;

        public ShellMessageWindow(WallpaperController controller)
        {
            this.controller = controller;
            taskbarCreated = (int)Native.RegisterWindowMessage("TaskbarCreated");
            var cp = new CreateParams();
            cp.Caption = "LiveWallpaperMessages";
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == taskbarCreated)
            {
                Log.Write("Explorer restarted");
                controller.RequestRebuild(2500);
            }
            else if (m.Msg == WM_DISPLAYCHANGE)
            {
                Log.Write("Display settings changed");
                controller.RequestRebuild(1500);
            }
            base.WndProc(ref m);
        }
    }
}
