using System;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace LiveWallpaper
{
    internal sealed class Settings
    {
        public string Folder = "";
        public bool IncludeSubfolders;
        public int IntervalSeconds = 300;
        public bool Shuffle = true;
        public bool WaitForEnd;
        public int FadeMs = 800;
        public int StretchMode;            // 0 = fill (crop), 1 = fit, 2 = stretch
        public bool Muted = true;
        public int Volume = 50;
        public int MonitorMode;            // 0 = all monitors, same file; 1 = all monitors, different files; 2 = primary only
        public bool PauseWhenCovered = true;
        public int CoverThreshold = 90;    // percent of the monitor's work area
        public bool PauseWhenOtherAppActive;
        public bool PauseOnBattery = true;
        public bool UnloadWhenPaused = true;
        public int UnloadDelaySeconds = 10;
        public bool RunAtStartup;

        public static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveWallpaper"); }
        }

        static string FilePath { get { return Path.Combine(DataDir, "settings.ini"); } }

        public Settings Clone() { return (Settings)MemberwiseClone(); }

        public void Normalize()
        {
            if (Folder == null) Folder = "";
            IntervalSeconds = Clamp(IntervalSeconds, 5, 7 * 86400);
            FadeMs = Clamp(FadeMs, 0, 5000);
            StretchMode = Clamp(StretchMode, 0, 2);
            Volume = Clamp(Volume, 0, 100);
            MonitorMode = Clamp(MonitorMode, 0, 2);
            CoverThreshold = Clamp(CoverThreshold, 50, 100);
            UnloadDelaySeconds = Clamp(UnloadDelaySeconds, 0, 3600);
        }

        static int Clamp(int v, int min, int max) { return v < min ? min : v > max ? max : v; }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int eq = raw.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = raw.Substring(0, eq).Trim();
                        string v = raw.Substring(eq + 1).Trim();
                        switch (k)
                        {
                            case "Folder": s.Folder = v; break;
                            case "IncludeSubfolders": s.IncludeSubfolders = B(v, s.IncludeSubfolders); break;
                            case "IntervalSeconds": s.IntervalSeconds = I(v, s.IntervalSeconds); break;
                            case "Shuffle": s.Shuffle = B(v, s.Shuffle); break;
                            case "WaitForEnd": s.WaitForEnd = B(v, s.WaitForEnd); break;
                            case "FadeMs": s.FadeMs = I(v, s.FadeMs); break;
                            case "StretchMode": s.StretchMode = I(v, s.StretchMode); break;
                            case "Muted": s.Muted = B(v, s.Muted); break;
                            case "Volume": s.Volume = I(v, s.Volume); break;
                            case "MonitorMode": s.MonitorMode = I(v, s.MonitorMode); break;
                            case "PauseWhenCovered": s.PauseWhenCovered = B(v, s.PauseWhenCovered); break;
                            case "CoverThreshold": s.CoverThreshold = I(v, s.CoverThreshold); break;
                            case "PauseWhenOtherAppActive": s.PauseWhenOtherAppActive = B(v, s.PauseWhenOtherAppActive); break;
                            case "PauseOnBattery": s.PauseOnBattery = B(v, s.PauseOnBattery); break;
                            case "UnloadWhenPaused": s.UnloadWhenPaused = B(v, s.UnloadWhenPaused); break;
                            case "UnloadDelaySeconds": s.UnloadDelaySeconds = I(v, s.UnloadDelaySeconds); break;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("Settings load failed: " + ex.Message); }
            s.RunAtStartup = Startup.IsEnabled();
            s.Normalize();
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var sb = new StringBuilder();
                sb.AppendLine("Folder=" + Folder);
                sb.AppendLine("IncludeSubfolders=" + IncludeSubfolders);
                sb.AppendLine("IntervalSeconds=" + IntervalSeconds.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Shuffle=" + Shuffle);
                sb.AppendLine("WaitForEnd=" + WaitForEnd);
                sb.AppendLine("FadeMs=" + FadeMs.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("StretchMode=" + StretchMode.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Muted=" + Muted);
                sb.AppendLine("Volume=" + Volume.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("MonitorMode=" + MonitorMode.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("PauseWhenCovered=" + PauseWhenCovered);
                sb.AppendLine("CoverThreshold=" + CoverThreshold.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("PauseWhenOtherAppActive=" + PauseWhenOtherAppActive);
                sb.AppendLine("PauseOnBattery=" + PauseOnBattery);
                sb.AppendLine("UnloadWhenPaused=" + UnloadWhenPaused);
                sb.AppendLine("UnloadDelaySeconds=" + UnloadDelaySeconds.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Log.Write("Settings save failed: " + ex.Message); }
            Startup.Apply(RunAtStartup);
        }

        static bool B(string v, bool def)
        {
            bool r;
            return bool.TryParse(v, out r) ? r : def;
        }

        static int I(string v, int def)
        {
            int r;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : def;
        }
    }

    internal static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "LiveWallpaper";

        static string Command { get { return "\"" + System.Windows.Forms.Application.ExecutablePath + "\""; } }

        public static bool IsEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue(ValueName) != null;
            }
            catch { return false; }
        }

        public static void Apply(bool enable)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return;
                    if (enable)
                    {
                        if (!string.Equals(k.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase))
                            k.SetValue(ValueName, Command);
                    }
                    else if (k.GetValue(ValueName) != null)
                    {
                        k.DeleteValue(ValueName, false);
                    }
                }
            }
            catch (Exception ex) { Log.Write("Startup registry: " + ex.Message); }
        }
    }

    internal static class Log
    {
        static readonly object gate = new object();
        static string path;

        public static void Init()
        {
            try
            {
                Directory.CreateDirectory(Settings.DataDir);
                path = Path.Combine(Settings.DataDir, "log.txt");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();
            }
            catch { }
        }

        public static void Write(string message)
        {
            if (path == null) return;
            lock (gate)
            {
                try { File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine); }
                catch { }
            }
        }
    }
}
