using System;
using System.Collections.Generic;
using System.IO;

namespace LiveWallpaper
{
    internal sealed class Playlist
    {
        readonly List<string> files = new List<string>();
        readonly HashSet<string> failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Random random = new Random();
        int version;

        public sealed class Cursor
        {
            public List<int> Order = new List<int>();
            public int Pos = -1;
            public string Current;
            public bool Shuffled;
            public int Version = -1;
        }

        public int Count { get { return files.Count; } }
        public int PlayableCount { get { return files.Count - failed.Count; } }

        public bool Contains(string path)
        {
            if (path == null) return false;
            foreach (var f in files) if (string.Equals(f, path, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public void Scan(string folder, bool includeSubfolders)
        {
            files.Clear();
            failed.Clear();
            version++;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            Collect(folder, includeSubfolders, files);
            files.Sort(Native.StrCmpLogicalW);
            Log.Write("Playlist: " + files.Count + " files in " + folder);
        }

        static void Collect(string dir, bool recurse, List<string> into)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    if (MediaFactory.KindOf(f) == MediaKind.None) continue;
                    try { if ((File.GetAttributes(f) & FileAttributes.Hidden) != 0) continue; } catch { }
                    into.Add(f);
                }
                if (!recurse) return;
                foreach (var d in Directory.EnumerateDirectories(dir)) Collect(d, true, into);
            }
            catch (Exception ex) { Log.Write("Scan " + dir + ": " + ex.Message); }
        }

        /// <summary>Short summary of a folder for the settings window.</summary>
        public static string Describe(string folder, bool includeSubfolders)
        {
            if (string.IsNullOrEmpty(folder)) return "폴더를 선택하세요.";
            if (!Directory.Exists(folder)) return "폴더를 찾을 수 없습니다.";
            var list = new List<string>();
            Collect(folder, includeSubfolders, list);
            int video = 0, gif = 0, image = 0;
            foreach (var f in list)
            {
                var k = MediaFactory.KindOf(f);
                if (k == MediaKind.Video) video++;
                else if (k == MediaKind.Gif) gif++;
                else if (k == MediaKind.Image) image++;
            }
            if (list.Count == 0) return "지원되는 파일이 없습니다. (영상, GIF, 이미지)";
            return string.Format("영상 {0}개 · GIF {1}개 · 이미지 {2}개", video, gif, image);
        }

        public void MarkFailed(string path)
        {
            if (path != null && Contains(path)) failed.Add(path);
        }

        /// <summary>Start a cursor at a given offset (used to give each monitor a different file).</summary>
        public string Start(Cursor c, bool shuffle, int offset)
        {
            EnsureOrder(c, shuffle);
            c.Pos = c.Order.Count == 0 ? -1 : (offset % c.Order.Count) - 1;
            return Next(c, shuffle, 1);
        }

        public string Next(Cursor c, bool shuffle, int direction)
        {
            if (files.Count == 0) { c.Current = null; return null; }
            EnsureOrder(c, shuffle);
            for (int attempt = 0; attempt < files.Count; attempt++)
            {
                c.Pos += direction >= 0 ? 1 : -1;
                if (c.Pos >= c.Order.Count)
                {
                    if (shuffle) Reshuffle(c);
                    c.Pos = 0;
                }
                else if (c.Pos < 0)
                {
                    c.Pos = c.Order.Count - 1;
                }
                string f = files[c.Order[c.Pos]];
                if (!failed.Contains(f)) { c.Current = f; return f; }
            }
            c.Current = null;
            return null;
        }

        void EnsureOrder(Cursor c, bool shuffle)
        {
            if (c.Version == version && c.Shuffled == shuffle && c.Order.Count == files.Count) return;
            c.Order = new List<int>(files.Count);
            for (int i = 0; i < files.Count; i++) c.Order.Add(i);
            c.Shuffled = shuffle;
            c.Version = version;
            if (shuffle) Shuffle(c.Order);
            c.Pos = -1;
            if (c.Current != null)
            {
                int idx = files.FindIndex(f => string.Equals(f, c.Current, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) c.Pos = c.Order.IndexOf(idx);
            }
        }

        void Reshuffle(Cursor c)
        {
            int last = c.Order.Count > 0 ? c.Order[c.Order.Count - 1] : -1;
            Shuffle(c.Order);
            // Avoid playing the same file twice in a row across rounds.
            if (c.Order.Count > 1 && c.Order[0] == last)
            {
                int j = 1 + random.Next(c.Order.Count - 1);
                int t = c.Order[0]; c.Order[0] = c.Order[j]; c.Order[j] = t;
            }
        }

        void Shuffle(List<int> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                int t = list[i]; list[i] = list[j]; list[j] = t;
            }
        }
    }
}
