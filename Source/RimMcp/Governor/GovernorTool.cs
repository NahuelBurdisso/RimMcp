using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimMcp
{
    // MCP tool "governor": status | enable | disable | reload | set_stations | events.
    public static partial class Tools
    {
        static object GovernorTool(Dictionary<string, object> a)
        {
            var action = (Str(a, "action", false) ?? "status").ToLowerInvariant();
            switch (action)
            {
                case "status": GovernorPolicy.Poll(); return GovernorStatus();
                case "enable":
                case "disable":
                {
                    GovernorPolicy.Poll(force: true);
                    if (GovernorPolicy.LastError != null)
                        throw new ToolError("governor.json has an error, fix it first: " + GovernorPolicy.LastError);
                    var p = GovernorPolicy.Current.Clone();
                    p.enabled = action == "enable";
                    GovernorPolicy.Save(p);
                    return GovernorStatus();
                }
                case "reload":
                {
                    var loaded = GovernorPolicy.Poll(force: true);
                    if (!loaded) throw new ToolError("governor.json not loaded (previous policy kept): " + GovernorPolicy.LastError);
                    return GovernorStatus();
                }
                case "set_stations": return GovernorSetStations(a);
                case "steward":
                case "research_queue":
                {
                    // steward settings: queue (research defNames, in order), reflexes {name: bool}, enabled
                    GovernorPolicy.Poll(force: true);
                    var p = GovernorPolicy.Current.Clone();
                    var unknown = new List<object>();
                    if (a.TryGetValue("queue", out var qv) && qv is List<object> ql)
                    {
                        p.steward.researchQueue = ql.Where(x => x != null).Select(x => x.ToString()).ToList();
                        unknown.AddRange(p.steward.researchQueue.Where(n => DefDatabase<ResearchProjectDef>.GetNamedSilentFail(n) == null));
                    }
                    if (a.TryGetValue("reflexes", out var rv) && rv is Dictionary<string, object> rd)
                        foreach (var kv in rd)
                        {
                            if (!StewardPolicy.Reflexes.Contains(kv.Key)) throw new ToolError("unknown reflex " + kv.Key + ". Known: " + string.Join(", ", StewardPolicy.Reflexes));
                            if (kv.Value is bool b) p.steward.reflexes[kv.Key] = b;
                        }
                    if (a.TryGetValue("on", out var ov) && ov is bool on) p.steward.enabled = on;
                    GovernorPolicy.Save(p);
                    var r = new Dictionary<string, object>
                    {
                        ["steward"] = p.ToObject()["steward"],
                        ["governorEnabled"] = p.enabled,
                    };
                    if (unknown.Count > 0) r["unknownProjects"] = unknown;
                    if (!p.enabled) r["note"] = "the governor is disabled: the steward runs only while it is enabled (action enable)";
                    return r;
                }
                case "events":
                {
                    long? since = a.TryGetValue("since", out var s) && s is double sd ? (long)sd : (long?)null;
                    int? tick = a.TryGetValue("tick", out var tv) && tv is double td ? (int)td : (int?)null;
                    return GovernorEvents.Since(since, tick, Math.Max(1, Math.Min(500, Int(a, "limit", 100))));
                }
                default: throw new ToolError("action: status, enable, disable, reload, set_stations, events");
            }
        }

        static Dictionary<string, object> GovernorStatus()
        {
            var pol = GovernorPolicy.Current;
            var r = new Dictionary<string, object>
            {
                ["enabled"] = pol.enabled,
                ["policyPath"] = GovernorPolicy.FilePath,
                ["policyError"] = GovernorPolicy.LastError,
                ["eventsPath"] = GovernorEvents.FilePath,
                ["cursor"] = GovernorEvents.Seq,
                ["policy"] = pol.ToObject(),
            };
            var gov = Current.ProgramState == ProgramState.Playing ? Governor.Instance : null;
            if (gov != null)
            {
                if (gov.Errors > 0) r["tickErrors"] = gov.Errors;
                r["draftedByGovernor"] = gov.Drafted.Where(p => p != null).Select(p => (object)p.LabelShort).ToList();
                r["maps"] = Find.Maps.Where(m => m.IsPlayerHome).Select(m => (object)gov.Status(gov.StateFor(m))).ToList();
            }
            r["lastEvents"] = GovernorEvents.Recent(10);
            return r;
        }

        static object GovernorSetStations(Dictionary<string, object> a)
        {
            RequireGame();
            string key;
            Map map = null;
            var mapArg = Str(a, "map", false);
            if (mapArg == null || mapArg == "current")
            {
                map = Find.CurrentMap ?? throw new ToolError("no current map");
                key = GovernorPositions.StationKey(map);
            }
            else if (mapArg == "home")
            {
                key = "home";
                map = GovernorPositions.PrimaryHome;
            }
            else
            {
                map = Find.Maps.FirstOrDefault(m => m.uniqueID.ToString() == mapArg)
                      ?? throw new ToolError("no map with id " + mapArg + ". Ids: " + string.Join(", ", Find.Maps.Select(m => m.uniqueID)));
                key = GovernorPositions.StationKey(map);
            }
            var cells = new List<IntVec3>();
            var rejected = new List<object>();
            if (a.TryGetValue("cells", out var cv) && cv is List<object> list)
                foreach (var item in list)
                {
                    if (!(item is List<object> xy) || xy.Count < 2 || !(xy[0] is double x) || !(xy[1] is double z))
                        throw new ToolError("cells must be [[x,z],...]");
                    var c = new IntVec3((int)Math.Round(x), 0, (int)Math.Round(z));
                    if (map != null && (!c.InBounds(map) || !c.Standable(map))) { rejected.Add(Pos(c)); continue; }
                    cells.Add(c);
                }
            GovernorPolicy.Poll(force: true);
            if (GovernorPolicy.LastError != null)
                throw new ToolError("governor.json has an error, fix it first: " + GovernorPolicy.LastError);
            var p = GovernorPolicy.Current.Clone();
            if (cells.Count == 0) p.stations.Remove(key);
            else p.stations[key] = cells;
            // home_center: [x,z] (primary home map) - the killbox plan writes the base center with its stations
            if (a.TryGetValue("home_center", out var hv) && hv is List<object> hl && hl.Count >= 2 && hl[0] is double hx && hl[1] is double hz)
                p.homeCenter = new IntVec3((int)Math.Round(hx), 0, (int)Math.Round(hz));
            else if (a.TryGetValue("home_center", out var hn) && hn == null)
                p.homeCenter = IntVec3.Invalid;
            GovernorPolicy.Save(p);
            var r = new Dictionary<string, object>
            {
                ["key"] = key,
                ["stations"] = cells.Select(c => (object)Pos(c)).ToList(),
                ["homeCenter"] = p.homeCenter.IsValid ? Pos(p.homeCenter) : null,
                ["policyPath"] = GovernorPolicy.FilePath,
                ["note"] = cells.Count == 0 ? "no stations: automatic positions (cover facing the enemy)" : "ranged fighters take the nearest free station",
            };
            if (rejected.Count > 0) r["rejected (outside the map or not standable)"] = rejected;
            return r;
        }
    }
}
