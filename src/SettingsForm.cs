using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace LiveWallpaper
{
    internal sealed class SettingsForm : Form
    {
        public event Action<Settings> Applied;

        static readonly int[] UnitSeconds = { 1, 60, 3600 };

        readonly Settings original;
        readonly TextBox txtFolder = new TextBox();
        readonly Label lblCount = new Label();
        readonly CheckBox chkSub = new CheckBox();
        readonly NumericUpDown numInterval = new NumericUpDown();
        readonly ComboBox cmbUnit = new ComboBox();
        readonly CheckBox chkShuffle = new CheckBox();
        readonly CheckBox chkWaitEnd = new CheckBox();
        readonly NumericUpDown numFade = new NumericUpDown();
        readonly ComboBox cmbStretch = new ComboBox();
        readonly ComboBox cmbMonitor = new ComboBox();
        readonly CheckBox chkMute = new CheckBox();
        readonly TrackBar trkVolume = new TrackBar();
        readonly Label lblVolume = new Label();
        readonly CheckBox chkCovered = new CheckBox();
        readonly NumericUpDown numThreshold = new NumericUpDown();
        readonly CheckBox chkForeground = new CheckBox();
        readonly CheckBox chkBattery = new CheckBox();
        readonly CheckBox chkUnload = new CheckBox();
        readonly NumericUpDown numUnload = new NumericUpDown();
        readonly CheckBox chkStartup = new CheckBox();
        readonly Timer countTimer = new Timer();

        readonly WallpaperController controller;
        readonly Label lblNow = new Label();
        readonly Button btnPause;
        readonly DropZone dropZone = new DropZone();
        readonly FlowLayoutPanel previewRow;
        readonly Label lblPreview = new Label();
        readonly Button btnAddToFolder;
        string addedPath;

        public SettingsForm(Settings settings, Icon icon, WallpaperController controller)
        {
            original = settings;
            this.controller = controller;
            Text = "Live Wallpaper 설정";
            if (icon != null) Icon = icon;
            Font = new Font("Segoe UI", 9f);
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ShowInTaskbar = true;

            var main = new FlowLayoutPanel();
            main.FlowDirection = FlowDirection.TopDown;
            main.WrapContents = false;
            main.AutoSize = true;
            main.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            main.Padding = new Padding(16, 8, 16, 12);
            Controls.Add(main);

            // --- playback controls
            main.Controls.Add(Header("재생", true));
            var btnPrev = Button("◀  이전", delegate { controller.Next(-1); });
            btnPause = Button("일시정지", delegate { TogglePause(); });
            var btnNext = Button("다음  ▶", delegate { controller.Next(1); });
            lblNow.AutoSize = true;
            lblNow.Margin = new Padding(10, 7, 3, 0);
            lblNow.ForeColor = SystemColors.GrayText;
            main.Controls.Add(Row(btnPrev, btnPause, btnNext, lblNow));

            // --- drag & drop preview
            dropZone.Text = "영상이나 GIF를 여기에 끌어다 놓으면 배경화면에서 바로 미리 볼 수 있어요\n(클릭해서 파일 고르기)";
            dropZone.Margin = new Padding(10, 6, 0, 2);
            dropZone.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            dropZone.Click += delegate { PickPreviewFile(); };
            HookDrop(dropZone);
            HookDrop(this);
            main.Controls.Add(dropZone);

            lblPreview.AutoSize = true;
            lblPreview.Margin = new Padding(3, 7, 10, 0);
            lblPreview.Font = new Font(Font, FontStyle.Bold);
            btnAddToFolder = Button("폴더에 추가", delegate { AddPreviewToFolder(); });
            var btnEndPreview = Button("미리보기 끝내기", delegate { controller.EndPreview(); });
            previewRow = Row(lblPreview, btnAddToFolder, btnEndPreview);
            previewRow.Visible = false;
            main.Controls.Add(previewRow);

            // --- folder
            main.Controls.Add(Header("배경화면 폴더", false));
            txtFolder.Width = 400;
            var btnBrowse = Button("찾아보기...", BrowseFolder);
            var btnOpen = Button("열기", delegate
            {
                if (Directory.Exists(txtFolder.Text)) Process.Start("explorer.exe", "\"" + txtFolder.Text + "\"");
            });
            main.Controls.Add(Row(txtFolder, btnBrowse, btnOpen));
            chkSub.Text = "하위 폴더 포함";
            main.Controls.Add(Row(Check(chkSub)));
            lblCount.AutoSize = true;
            lblCount.ForeColor = SystemColors.GrayText;
            lblCount.Margin = new Padding(3, 0, 3, 0);
            main.Controls.Add(Row(lblCount));

            // --- slideshow
            main.Controls.Add(Header("슬라이드쇼", false));
            numInterval.Minimum = 1; numInterval.Maximum = 9999; numInterval.Width = 70;
            cmbUnit.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbUnit.Items.AddRange(new object[] { "초", "분", "시간" });
            cmbUnit.Width = 70;
            main.Controls.Add(Row(Field("전환 간격"), numInterval, cmbUnit));
            chkShuffle.Text = "셔플 (무작위 순서로 재생)";
            main.Controls.Add(Row(Check(chkShuffle)));
            chkWaitEnd.Text = "영상·GIF는 전환 시간이 되어도 끝까지 재생한 뒤 넘어가기";
            main.Controls.Add(Row(Check(chkWaitEnd)));
            numFade.Minimum = 0; numFade.Maximum = 5000; numFade.Increment = 100; numFade.Width = 70;
            main.Controls.Add(Row(Field("페이드 전환"), numFade, Lbl("ms  (0 = 바로 전환)")));
            cmbStretch.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbStretch.Items.AddRange(new object[] { "채우기 (꽉 채움, 가장자리 잘림)", "맞춤 (전체 보이기, 여백 생김)", "늘이기 (비율 무시)" });
            cmbStretch.Width = 300;
            main.Controls.Add(Row(Field("화면 맞춤"), cmbStretch));
            cmbMonitor.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMonitor.Items.AddRange(new object[] { "모든 모니터 - 같은 파일", "모든 모니터 - 모니터마다 다른 파일", "주 모니터만" });
            cmbMonitor.Width = 300;
            main.Controls.Add(Row(Field("모니터"), cmbMonitor));

            // --- sound
            main.Controls.Add(Header("소리", false));
            chkMute.Text = "음소거";
            trkVolume.Minimum = 0; trkVolume.Maximum = 100; trkVolume.TickStyle = TickStyle.None; trkVolume.Width = 180;
            trkVolume.AutoSize = false; trkVolume.Height = 28;
            lblVolume.AutoSize = true; lblVolume.Margin = new Padding(3, 6, 3, 0);
            trkVolume.ValueChanged += delegate { lblVolume.Text = trkVolume.Value + "%"; };
            chkMute.CheckedChanged += delegate { trkVolume.Enabled = !chkMute.Checked; };
            main.Controls.Add(Row(Check(chkMute), Lbl("   볼륨"), trkVolume, lblVolume));

            // --- resources
            main.Controls.Add(Header("리소스 절약 (안 보일 때 멈춤)", false));
            chkCovered.Text = "다른 창이 화면을 덮으면 멈춤 - 화면의";
            numThreshold.Minimum = 50; numThreshold.Maximum = 100; numThreshold.Width = 55;
            chkCovered.CheckedChanged += delegate { numThreshold.Enabled = chkCovered.Checked; };
            main.Controls.Add(Row(Check(chkCovered), numThreshold, Lbl("% 이상 가려졌을 때")));
            chkForeground.Text = "바탕화면이 아닌 다른 프로그램을 쓰고 있으면 멈춤 (그 프로그램이 있는 모니터)";
            main.Controls.Add(Row(Check(chkForeground)));
            chkBattery.Text = "배터리로 동작 중이면 멈춤";
            main.Controls.Add(Row(Check(chkBattery)));
            chkUnload.Text = "멈춘 상태가";
            numUnload.Minimum = 0; numUnload.Maximum = 3600; numUnload.Width = 60;
            chkUnload.CheckedChanged += delegate { numUnload.Enabled = chkUnload.Checked; };
            main.Controls.Add(Row(Check(chkUnload), numUnload, Lbl("초 지속되면 영상을 메모리에서 내림 (다시 보일 때 이어서 재생)")));
            var hint = Lbl("화면 잠금 중에는 항상 멈춥니다. 트레이 아이콘 메뉴에서 수동으로 일시정지할 수도 있습니다.");
            hint.ForeColor = SystemColors.GrayText;
            main.Controls.Add(Row(hint));

            // --- misc
            main.Controls.Add(Header("기타", false));
            chkStartup.Text = "Windows 시작 시 자동 실행";
            main.Controls.Add(Row(Check(chkStartup)));

            // --- buttons
            var ok = Button("확인", delegate { if (Commit()) Close(); });
            var cancel = Button("취소", delegate { Close(); });
            var apply = Button("적용", delegate { Commit(); });
            var buttons = Row(ok, cancel, apply);
            buttons.Anchor = AnchorStyles.Right;
            buttons.Margin = new Padding(0, 14, 0, 0);
            main.Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;

            countTimer.Interval = 400;
            countTimer.Tick += delegate { countTimer.Stop(); UpdateCount(); };
            txtFolder.TextChanged += delegate { countTimer.Stop(); countTimer.Start(); };
            chkSub.CheckedChanged += delegate { UpdateCount(); };

            LoadValues(settings);

            controller.StatusChanged += RefreshPlayback;
            FormClosed += delegate { controller.StatusChanged -= RefreshPlayback; };
            RefreshPlayback();
        }

        // ---------------------------------------------------------------- playback / preview

        void TogglePause()
        {
            controller.ManualPause = !controller.ManualPause;
            controller.RefreshNow();
            RefreshPlayback();
        }

        void RefreshPlayback()
        {
            if (IsDisposed) return;
            btnPause.Text = controller.ManualPause ? "재생" : "일시정지";
            string name = controller.CurrentFileName;
            lblNow.Text = name == null ? "재생 중인 파일 없음" : (controller.IsPreviewing ? "" : "재생 중: ") + Shorten(name, 40);
            lblNow.Visible = !controller.IsPreviewing;

            bool previewing = controller.IsPreviewing;
            previewRow.Visible = previewing;
            if (!previewing) return;

            string path = controller.PreviewPath;
            lblPreview.Text = "미리보기 중: " + Shorten(Path.GetFileName(path), 36);
            if (string.Equals(path, addedPath, StringComparison.OrdinalIgnoreCase))
            {
                btnAddToFolder.Text = "추가됨";
                btnAddToFolder.Enabled = false;
            }
            else if (controller.IsInPlaylist(path))
            {
                btnAddToFolder.Text = "이미 폴더에 있음";
                btnAddToFolder.Enabled = false;
            }
            else
            {
                btnAddToFolder.Text = "폴더에 추가";
                btnAddToFolder.Enabled = true;
            }
        }

        static string Shorten(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max - 3) + "...";
        }

        void HookDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += delegate(object s, DragEventArgs e)
            {
                bool ok = FirstPlayable(e.Data) != null;
                e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
                dropZone.Highlight = ok;
            };
            c.DragLeave += delegate { dropZone.Highlight = false; };
            c.DragDrop += delegate(object s, DragEventArgs e)
            {
                dropZone.Highlight = false;
                string path = FirstPlayable(e.Data);
                if (path != null) controller.StartPreview(path);
            };
        }

        static string FirstPlayable(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
            var files = data.GetData(DataFormats.FileDrop) as string[];
            if (files == null) return null;
            foreach (var f in files)
                if (MediaFactory.KindOf(f) != MediaKind.None && File.Exists(f)) return f;
            return null;
        }

        void PickPreviewFile()
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = "미리 볼 영상/GIF 선택";
                d.Filter = MediaFactory.FileDialogFilter;
                if (Directory.Exists(txtFolder.Text)) d.InitialDirectory = txtFolder.Text;
                if (d.ShowDialog(this) == DialogResult.OK) controller.StartPreview(d.FileName);
            }
        }

        async void AddPreviewToFolder()
        {
            string src = controller.PreviewPath;
            string folder = controller.Folder;
            if (src == null) return;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                MessageBox.Show(this, "먼저 배경화면 폴더를 지정하고 적용하세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string dest = UniquePath(folder, Path.GetFileName(src));
            btnAddToFolder.Enabled = false;
            btnAddToFolder.Text = "복사 중...";
            try
            {
                await System.Threading.Tasks.Task.Run(delegate { File.Copy(src, dest); });
                addedPath = src;
                Log.Write("Added to folder: " + dest);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "파일을 복사하지 못했습니다:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            RefreshPlayback();
        }

        static string UniquePath(string folder, string fileName)
        {
            string dest = Path.Combine(folder, fileName);
            string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
            for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(folder, stem + " (" + i + ")" + ext);
            return dest;
        }

        public void ShowPreviewError(string path)
        {
            MessageBox.Show(this, "이 파일은 재생할 수 없습니다:\n" + Path.GetFileName(path) +
                "\n\nH.264 mp4가 가장 잘 재생됩니다. HEVC/webm은 Microsoft Store의 코덱 확장이 필요할 수 있습니다.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void LoadValues(Settings s)
        {
            txtFolder.Text = s.Folder;
            chkSub.Checked = s.IncludeSubfolders;
            int unit = s.IntervalSeconds % 3600 == 0 ? 2 : s.IntervalSeconds % 60 == 0 ? 1 : 0;
            cmbUnit.SelectedIndex = unit;
            numInterval.Value = Math.Max(1, Math.Min(9999, s.IntervalSeconds / UnitSeconds[unit]));
            chkShuffle.Checked = s.Shuffle;
            chkWaitEnd.Checked = s.WaitForEnd;
            numFade.Value = s.FadeMs;
            cmbStretch.SelectedIndex = s.StretchMode;
            cmbMonitor.SelectedIndex = s.MonitorMode;
            chkMute.Checked = s.Muted;
            trkVolume.Value = s.Volume;
            lblVolume.Text = s.Volume + "%";
            trkVolume.Enabled = !s.Muted;
            chkCovered.Checked = s.PauseWhenCovered;
            numThreshold.Value = s.CoverThreshold;
            numThreshold.Enabled = s.PauseWhenCovered;
            chkForeground.Checked = s.PauseWhenOtherAppActive;
            chkBattery.Checked = s.PauseOnBattery;
            chkUnload.Checked = s.UnloadWhenPaused;
            numUnload.Value = s.UnloadDelaySeconds;
            numUnload.Enabled = s.UnloadWhenPaused;
            chkStartup.Checked = s.RunAtStartup;
            UpdateCount();
        }

        bool Commit()
        {
            string folder = txtFolder.Text.Trim();
            if (folder.Length > 0 && !Directory.Exists(folder))
            {
                MessageBox.Show(this, "폴더를 찾을 수 없습니다:\n" + folder, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            var s = original.Clone();
            s.Folder = folder;
            s.IncludeSubfolders = chkSub.Checked;
            s.IntervalSeconds = (int)numInterval.Value * UnitSeconds[Math.Max(0, cmbUnit.SelectedIndex)];
            s.Shuffle = chkShuffle.Checked;
            s.WaitForEnd = chkWaitEnd.Checked;
            s.FadeMs = (int)numFade.Value;
            s.StretchMode = Math.Max(0, cmbStretch.SelectedIndex);
            s.MonitorMode = Math.Max(0, cmbMonitor.SelectedIndex);
            s.Muted = chkMute.Checked;
            s.Volume = trkVolume.Value;
            s.PauseWhenCovered = chkCovered.Checked;
            s.CoverThreshold = (int)numThreshold.Value;
            s.PauseWhenOtherAppActive = chkForeground.Checked;
            s.PauseOnBattery = chkBattery.Checked;
            s.UnloadWhenPaused = chkUnload.Checked;
            s.UnloadDelaySeconds = (int)numUnload.Value;
            s.RunAtStartup = chkStartup.Checked;
            s.Normalize();
            var h = Applied;
            if (h != null) h(s);
            return true;
        }

        void BrowseFolder(object sender, EventArgs e)
        {
            string picked = FolderPicker.Pick(this, txtFolder.Text);
            if (picked != null) txtFolder.Text = picked;
        }

        void UpdateCount()
        {
            try { lblCount.Text = Playlist.Describe(txtFolder.Text.Trim(), chkSub.Checked); }
            catch (Exception ex) { lblCount.Text = ex.Message; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) countTimer.Dispose();
            base.Dispose(disposing);
        }

        // ---------------------------------------------------------------- layout helpers

        static Label Header(string text, bool first)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = new Font("Segoe UI Semibold", 10.5f);
            l.Margin = new Padding(0, first ? 6 : 14, 0, 4);
            return l;
        }

        static Label Lbl(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(3, 7, 3, 0);
            return l;
        }

        /// <summary>Fixed-width label so the inputs after it line up.</summary>
        Label Field(string text)
        {
            var l = Lbl(text);
            l.MinimumSize = new Size(TextRenderer.MeasureText("페이드 전환  ", Font).Width, 0);
            return l;
        }

        /// <summary>Makes a drop-down wide enough for its longest item at the current DPI.</summary>
        void FitCombo(ComboBox c)
        {
            int w = 0;
            foreach (object item in c.Items) w = Math.Max(w, TextRenderer.MeasureText(item.ToString(), Font).Width);
            c.Width = w + SystemInformation.VerticalScrollBarWidth + 16;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            dropZone.Height = Font.Height * 4;
            FitCombo(cmbUnit);
            FitCombo(cmbStretch);
            FitCombo(cmbMonitor);
        }

        static CheckBox Check(CheckBox c)
        {
            c.AutoSize = true;
            c.Cursor = Cursors.Hand;
            c.Margin = new Padding(3, 5, 3, 3);
            return c;
        }

        static Button Button(string text, EventHandler click)
        {
            var b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.MinimumSize = new Size(80, 28);
            b.Cursor = Cursors.Hand;
            b.Click += click;
            return b;
        }

        static FlowLayoutPanel Row(params Control[] controls)
        {
            var p = new FlowLayoutPanel();
            p.FlowDirection = FlowDirection.LeftToRight;
            p.WrapContents = false;
            p.AutoSize = true;
            p.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            p.Margin = new Padding(10, 1, 0, 1);
            p.Controls.AddRange(controls);
            return p;
        }
    }

    /// <summary>Dashed drop target that lights up while a playable file is dragged over it.</summary>
    internal sealed class DropZone : Label
    {
        bool highlight;

        public DropZone()
        {
            AutoSize = false;
            TextAlign = ContentAlignment.MiddleCenter;
            Cursor = Cursors.Hand;
            ForeColor = SystemColors.GrayText;
            BackColor = SystemColors.Window;
            DoubleBuffered = true;
        }

        public bool Highlight
        {
            get { return highlight; }
            set
            {
                if (highlight == value) return;
                highlight = value;
                BackColor = value ? Color.FromArgb(232, 240, 254) : SystemColors.Window;
                ForeColor = value ? SystemColors.Highlight : SystemColors.GrayText;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float w = Math.Max(1f, DeviceDpi / 96f * 1.5f);
            using (var pen = new Pen(highlight ? SystemColors.Highlight : SystemColors.ControlDark, w))
            {
                pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                e.Graphics.DrawRectangle(pen, w / 2, w / 2, Width - w, Height - w);
            }
        }
    }
}
