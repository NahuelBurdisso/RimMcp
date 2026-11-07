using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Verse;

namespace RimMcp
{
    // The policy Claude edits: <save data folder>/RimMcp/governor.json. Every field is optional;
    // a missing file is created with the defaults (disabled). Re-read when its mtime changes.
    public class GovernorPolicy
    {
        public bool enabled;
        // combat
        public bool autoDraft = true;
        public float retreatHealthPct = 0.5f;
        public float retreatBleedRate = 0.5f;          // BleedRateTotal: 1.0 = bleeds out in a day
        public Dictionary<string, List<IntVec3>> stations = new Dictionary<string, List<IntVec3>>();
        public IntVec3 homeCenter = IntVec3.Invalid;   // applies to the primary home map
        public float engageMeleeRadius = 5f;
        public int undraftAfterQuietTicks = 600;
        public float defendIndoorsIfRatioAbove = 2.5f;
        public float engageDistance = 45f;             // deploy only when an enemy is this close to home (0 = always)
        public List<string> ignoreFactions = new List<string>();
        // rescue
        public bool rescueDowned = true;
        public bool captureDowned = true;
        // non-combatants
        public bool shelter = true;
        // events
        public int maxFileKb = 512;
        public StewardPolicy steward = new StewardPolicy();

        public static GovernorPolicy Current = new GovernorPolicy();
        public static string LastError;
        static DateTime lastMtime = DateTime.MinValue;

        public static string Dir => Path.Combine(GenFilePaths.SaveDataFolderPath, "RimMcp");
        public static string FilePath => Path.Combine(Dir, "governor.json");

        // Main thread. Creates the default file when missing; reloads when the mtime changed.
        // Returns true when a new policy was loaded.
        public static bool Poll(bool force = false)
        {
            var path = FilePath;
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(path, new GovernorPolicy().ToJson());
            }
            var mtime = File.GetLastWriteTimeUtc(path);
            if (!force && mtime == lastMtime) return false;
            lastMtime = mtime;
            try
            {
                Current = Parse(File.ReadAllText(path));
                LastError = null;
                return true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Log.Warning("[RimMcp] governor.json not loaded, keeping the previous policy: " + e.Message);
                return false;
            }
        }

        public static void Save(GovernorPolicy p)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, p.ToJson());
            Current = p;
            lastMtime = File.GetLastWriteTimeUtc(FilePath);
            LastError = null;
        }

        public GovernorPolicy Clone() => Parse(ToJson());

        // ------------------------------------------------------------------ parse

        public static GovernorPolicy Parse(string text)
        {
            var root = Json.Parse(text) as Dictionary<string, object> ?? throw new FormatException("top level must be an object");
            var p = new GovernorPolicy();
            p.enabled = B(root, "enabled", p.enabled);
            var c = O(root, "combat");
            if (c != null)
            {
                p.autoDraft = B(c, "autoDraft", p.autoDraft);
                p.retreatHealthPct = F(c, "retreatHealthPct", p.retreatHealthPct);
                p.retreatBleedRate = F(c, "retreatBleedRate", p.retreatBleedRate);
                p.engageMeleeRadius = F(c, "engageMeleeRadius", p.engageMeleeRadius);
                p.undraftAfterQuietTicks = (int)F(c, "undraftAfterQuietTicks", p.undraftAfterQuietTicks);
                p.defendIndoorsIfRatioAbove = F(c, "defendIndoorsIfRatioAbove", p.defendIndoorsIfRatioAbove);
                p.engageDistance = F(c, "engageDistance", p.engageDistance);
                if (c.TryGetValue("homeCenter", out var hc) && hc is List<object> hl) p.homeCenter = CellOf(hl);
                if (c.TryGetValue("ignoreFactions", out var ig) && ig is List<object> il)
                    p.ignoreFactions = il.Where(x => x != null).Select(x => x.ToString()).ToList();
                var st = O(c, "stations");
                if (st != null)
                    foreach (var kv in st)
                        if (kv.Value is List<object> cells)
                            p.stations[kv.Key] = cells.OfType<List<object>>().Select(CellOf).Where(x => x.IsValid).ToList();
            }
            var r = O(root, "rescue");
            if (r != null)
            {
                p.rescueDowned = B(r, "downedColonists", p.rescueDowned);
                p.captureDowned = B(r, "captureDowned", p.captureDowned);
            }
            var n = O(root, "nonCombatants");
            if (n != null) p.shelter = B(n, "shelter", p.shelter);
            var sw = O(root, "steward");
            if (sw != null)
            {
                p.steward.enabled = B(sw, "enabled", p.steward.enabled);
                var rf = O(sw, "reflexes");
                if (rf != null) foreach (var kv in rf) if (kv.Value is bool bv) p.steward.reflexes[kv.Key] = bv;
                if (sw.TryGetValue("researchQueue", out var rq) && rq is List<object> rql)
                    p.steward.researchQueue = rql.Where(x => x != null).Select(x => x.ToString()).ToList();
                if (sw.TryGetValue("stopOn", out var so) && so is List<object> sol)
                    p.steward.stopOn = sol.Where(x => x != null).Select(x => x.ToString()).ToList();
                p.steward.freezerTargetC = F(sw, "freezerTargetC", p.steward.freezerTargetC);
                p.steward.forcedRestFoodPct = F(sw, "forcedRestFoodPct", p.steward.forcedRestFoodPct);
                p.steward.feedBelowPct = F(sw, "feedBelowPct", p.steward.feedBelowPct);
            }
            var e = O(root, "events");
            if (e != null) p.maxFileKb = Math.Max(16, (int)F(e, "maxFileKb", p.maxFileKb));
            p.retreatHealthPct = Math.Max(0f, Math.Min(1f, p.retreatHealthPct));
            p.undraftAfterQuietTicks = Math.Max(60, p.undraftAfterQuietTicks);
            p.engageMeleeRadius = Math.Max(1f, p.engageMeleeRadius);
            return p;
        }

        static Dictionary<string, object> O(Dictionary<string, object> d, string k) =>
            d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;

        static bool B(Dictionary<string, object> d, string k, bool def) =>
            d.TryGetValue(k, out var v) && v is bool b ? b : def;

        static float F(Dictionary<string, object> d, string k, float def) =>
            d.TryGetValue(k, out var v) && v is double x ? (float)x : def;

        static IntVec3 CellOf(List<object> l) =>
            l.Count >= 2 && l[0] is double x && l[1] is double z ? new IntVec3((int)Math.Round(x), 0, (int)Math.Round(z)) : IntVec3.Invalid;

        // ------------------------------------------------------------------ write (readable, one field per line)

        public string ToJson()
        {
            string Num(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
            string Cell(IntVec3 c) => "[" + c.x + "," + c.z + "]";
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"enabled\": ").Append(enabled ? "true" : "false").Append(",\n");
            sb.Append("  \"combat\": {\n");
            sb.Append("    \"autoDraft\": ").Append(autoDraft ? "true" : "false").Append(",\n");
            sb.Append("    \"retreatHealthPct\": ").Append(Num(retreatHealthPct)).Append(",\n");
            sb.Append("    \"retreatBleedRate\": ").Append(Num(retreatBleedRate)).Append(",\n");
            sb.Append("    \"stations\": {");
            var first = true;
            foreach (var kv in stations)
            {
                sb.Append(first ? "\n" : ",\n");
                first = false;
                sb.Append("      ").Append(Json.Write(kv.Key)).Append(": [").Append(string.Join(", ", kv.Value.Select(Cell))).Append("]");
            }
            sb.Append(first ? "},\n" : "\n    },\n");
            sb.Append("    \"homeCenter\": ").Append(homeCenter.IsValid ? Cell(homeCenter) : "null").Append(",\n");
            sb.Append("    \"engageMeleeRadius\": ").Append(Num(engageMeleeRadius)).Append(",\n");
            sb.Append("    \"undraftAfterQuietTicks\": ").Append(undraftAfterQuietTicks).Append(",\n");
            sb.Append("    \"defendIndoorsIfRatioAbove\": ").Append(Num(defendIndoorsIfRatioAbove)).Append(",\n");
            sb.Append("    \"engageDistance\": ").Append(Num(engageDistance)).Append(",\n");
            sb.Append("    \"ignoreFactions\": ").Append(Json.Write(ignoreFactions)).Append("\n");
            sb.Append("  },\n");
            sb.Append("  \"rescue\": {\"downedColonists\": ").Append(rescueDowned ? "true" : "false")
              .Append(", \"captureDowned\": ").Append(captureDowned ? "true" : "false").Append("},\n");
            sb.Append("  \"nonCombatants\": {\"shelter\": ").Append(shelter ? "true" : "false").Append("},\n");
            sb.Append("  \"steward\": {\n");
            sb.Append("    \"enabled\": ").Append(steward.enabled ? "true" : "false").Append(",\n");
            sb.Append("    \"reflexes\": {").Append(string.Join(", ", steward.reflexes.Select(kv => Json.Write(kv.Key) + ": " + (kv.Value ? "true" : "false")))).Append("},\n");
            sb.Append("    \"researchQueue\": ").Append(Json.Write(steward.researchQueue)).Append(",\n");
            sb.Append("    \"stopOn\": ").Append(Json.Write(steward.stopOn)).Append(",\n");
            sb.Append("    \"freezerTargetC\": ").Append(Num(steward.freezerTargetC)).Append(",\n");
            sb.Append("    \"forcedRestFoodPct\": ").Append(Num(steward.forcedRestFoodPct)).Append(",\n");
            sb.Append("    \"feedBelowPct\": ").Append(Num(steward.feedBelowPct)).Append("\n");
            sb.Append("  },\n");
            sb.Append("  \"events\": {\"maxFileKb\": ").Append(maxFileKb).Append("}\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        public Dictionary<string, object> ToObject() => (Dictionary<string, object>)Json.Parse(ToJson());
    }
}
