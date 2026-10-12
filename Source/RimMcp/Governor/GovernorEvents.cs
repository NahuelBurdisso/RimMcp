using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace RimMcp
{
    // Compact event stream: one JSON object per line in <save data folder>/RimMcp/events.jsonl.
    // Every line carries a growing "seq" (the cursor for the governor tool), tick, date, hour,
    // map and type. When the file passes maxFileKb it moves to events.1.jsonl (one old file kept).
    public static class GovernorEvents
    {
        public static string FilePath => Path.Combine(GovernorPolicy.Dir, "events.jsonl");
        public static string OldFilePath => Path.Combine(GovernorPolicy.Dir, "events.1.jsonl");

        static long seq = -1;
        static readonly List<Dictionary<string, object>> recent = new List<Dictionary<string, object>>();
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static bool ioWarned;

        public static long Seq { get { EnsureSeq(); return seq; } }

        public static List<Dictionary<string, object>> Recent(int n) =>
            recent.Skip(Math.Max(0, recent.Count - n)).ToList();

        public static string MapLabel(Map map) => map == null ? null : (map.Parent?.LabelCap.ToString() ?? ("map " + map.uniqueID));

        public static void Emit(string type, Map map, Dictionary<string, object> details = null)
        {
            EnsureSeq();
            var e = new Dictionary<string, object> { ["seq"] = ++seq, ["tick"] = Find.TickManager.TicksGame };
            var m = map ?? Find.AnyPlayerHomeMap;
            if (m != null)
            {
                e["date"] = GenDate.DateFullStringAt(Find.TickManager.TicksAbs, Find.WorldGrid.LongLatOf(m.Tile));
                e["hour"] = GenLocalDate.HourOfDay(m);
            }
            if (map != null)
            {
                e["map"] = MapLabel(map);
                e["mapId"] = map.uniqueID;
            }
            e["type"] = type;
            if (details != null) foreach (var kv in details) e[kv.Key] = kv.Value;
            recent.Add(e);
            if (recent.Count > 50) recent.RemoveAt(0);
            try
            {
                Directory.CreateDirectory(GovernorPolicy.Dir);
                var path = FilePath;
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > GovernorPolicy.Current.maxFileKb * 1024L)
                {
                    if (File.Exists(OldFilePath)) File.Delete(OldFilePath);
                    File.Move(path, OldFilePath);
                }
                File.AppendAllText(path, Json.Write(e) + "\n", Utf8);
            }
            catch (Exception ex)
            {
                if (!ioWarned) Log.Warning("[RimMcp] governor could not write events.jsonl: " + ex.Message);
                ioWarned = true;
            }
        }

        // The last seq in the files, so the cursor keeps growing across game restarts.
        static void EnsureSeq()
        {
            if (seq >= 0) return;
            seq = 0;
            foreach (var f in new[] { FilePath, OldFilePath })
            {
                var lines = ReadLines(f);
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    if (TryParse(lines[i], out var d) && d.TryGetValue("seq", out var s) && s is double v)
                    {
                        seq = (long)v;
                        return;
                    }
                }
            }
        }

        static List<string> ReadLines(string path)
        {
            try
            {
                if (!File.Exists(path)) return new List<string>();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new StreamReader(fs, Encoding.UTF8))
                    return r.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            }
            catch { return new List<string>(); }
        }

        static bool TryParse(string line, out Dictionary<string, object> d)
        {
            try { d = Json.Parse(line) as Dictionary<string, object>; return d != null; }
            catch { d = null; return false; }
        }

        // Events after a cursor (seq) or from a tick on; at most limit, oldest first.
        public static Dictionary<string, object> Since(long? sinceSeq, int? sinceTick, int limit)
        {
            EnsureSeq();
            var all = new List<object>();
            foreach (var f in new[] { OldFilePath, FilePath })
                foreach (var line in ReadLines(f))
                {
                    if (!TryParse(line, out var d)) continue;
                    var s = d.TryGetValue("seq", out var sv) && sv is double sd ? (long)sd : -1;
                    var t = d.TryGetValue("tick", out var tv) && tv is double td ? (int)td : -1;
                    if (sinceSeq.HasValue && s <= sinceSeq.Value) continue;
                    if (sinceTick.HasValue && t < sinceTick.Value) continue;
                    all.Add(d);
                }
            var more = all.Count > limit;
            var page = all.Take(limit).ToList();
            long next = page.Count > 0 && page[page.Count - 1] is Dictionary<string, object> last && last["seq"] is double ls
                ? (long)ls
                : sinceSeq ?? seq;
            return new Dictionary<string, object>
            {
                ["events"] = page,
                ["next"] = next,
                ["more"] = more,
                ["file"] = FilePath,
            };
        }
    }
}
