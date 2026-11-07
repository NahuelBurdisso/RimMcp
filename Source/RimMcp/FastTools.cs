using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMcp
{
    // The fast path: fewer round trips and fewer tokens per decision.
    //   batch     N tool calls in one request, run in order on the main thread
    //   snapshot  what play/brief needed six calls for, compact (no empty fields)
    //   map_view summary:true  the map as rectangles and clusters instead of an ASCII grid
    // Everything reads only what the player can see (fogged cells are skipped).
    public static partial class Tools
    {
        static readonly HashSet<string> notBatchable = new HashSet<string> { "batch", "newgame" };

        static object Batch(Dictionary<string, object> a)
        {
            if (!(a.TryGetValue("calls", out var cv) && cv is List<object> calls))
                throw new ToolError("calls: [{tool, args}, ...]");
            bool stop = Bool(a, "stop_on_error", false);
            bool quiet = Bool(a, "quiet", false);       // only errors and a count back
            var results = new List<object>();
            int ok = 0, failed = 0, i = 0;
            foreach (var c in calls)
            {
                var d = c as Dictionary<string, object> ?? throw new ToolError($"calls[{i}] is not an object");
                var tool = Str(d, "tool");
                var args = d.TryGetValue("args", out var av) && av is Dictionary<string, object> ad ? ad : new Dictionary<string, object>();
                var entry = new Dictionary<string, object> { ["i"] = i, ["tool"] = tool };
                try
                {
                    if (notBatchable.Contains(tool)) throw new ToolError(tool + " cannot run inside a batch");
                    var r = Run(tool, args);
                    ok++;
                    if (!quiet || HasRejections(r)) { entry["ok"] = true; entry["result"] = r; results.Add(entry); }
                }
                catch (Exception e)
                {
                    failed++;
                    entry["ok"] = false;
                    entry["error"] = e is ToolError ? e.Message : e.GetType().Name + ": " + e.Message;
                    results.Add(entry);
                    if (stop) { i++; break; }
                }
                i++;
            }
            return new Dictionary<string, object>
            {
                ["ok"] = ok, ["failed"] = failed, ["skipped"] = calls.Count - i, ["results"] = results,
            };
        }

        static bool HasRejections(object r) =>
            r is Dictionary<string, object> d && (d.ContainsKey("rejected") || d.TryGetValue("placed", out var p) && p is int n && n == 0);

        // ------------------------------------------------------------------ snapshot

        static readonly string[] snapshotItems =
        {
            "Steel", "WoodLog", "ComponentIndustrial", "ComponentSpacer", "Plasteel", "Silver", "Gold",
            "MedicineHerbal", "MedicineIndustrial", "MedicineUltratech", "Cloth", "MealSimple", "MealFine",
            "MealLavish", "MealSurvivalPack", "Pemmican", "Chemfuel", "Neutroamine",
        };

        static object Snapshot(Dictionary<string, object> a)
        {
            RequireGame();
            var map = Find.CurrentMap ?? Find.AnyPlayerHomeMap;
            var r = Obj();
            var st = (Dictionary<string, object>)Status(a);
            r["t"] = $"{st.GetValueOrDefault("date")} {st.GetValueOrDefault("hour")}h | {st.GetValueOrDefault("weather")} {st.GetValueOrDefault("outdoorTempC")}C | wealth {st.GetValueOrDefault("wealth")} | {st.GetValueOrDefault("speed")}";
            r["ticks"] = Find.TickManager.TicksGame;
            r["colonists"] = AllColonists().Select(p => (object)PawnLine(p)).ToList();
            if (map != null)
            {
                var items = Obj();
                foreach (var dn in snapshotItems)
                {
                    var d = DefDatabase<ThingDef>.GetNamedSilentFail(dn);
                    if (d == null) continue;
                    var n = map.resourceCounter.GetCount(d);
                    if (n > 0) items[d.label] = n;
                }
                int blocks = map.resourceCounter.AllCountedAmounts.Where(kv => kv.Key.IsStuff && kv.Key.stuffProps.categories.Contains(StuffCategoryDefOf.Stony)).Sum(kv => kv.Value);
                if (blocks > 0) items["stone blocks"] = blocks;
                r["stock"] = items;
                var res = (Dictionary<string, object>)Resources(a);
                r["food"] = $"{res["humanEdibleNutrition"]} nutrition, ~{res["approxFoodDays"]} days";
                var forb = (Dictionary<string, object>)res["forbiddenOutsideStorage"];
                if (forb.Count > 0) r["forbiddenLoose"] = string.Join(", ", forb.Take(6).Select(kv => $"{kv.Key} {kv.Value}"));
            }
            var al = (Dictionary<string, object>)Alerts(a);
            if (al.TryGetValue("alerts", out var alv) && alv is List<object> alerts && alerts.Count > 0)
                r["alerts"] = alerts.Select(x => (object)(((Dictionary<string, object>)x)["alert"] as string)).ToList();
            var dl = (Dictionary<string, object>)Dialogs(a);
            var wins = ((List<object>)dl["windows"]).Cast<Dictionary<string, object>>().ToList();
            if (wins.Count > 0)
                r["windows"] = wins.Select(w => (object)$"{w["window"]}: {w["type"]} {Trim((w.GetValueOrDefault("text") ?? w.GetValueOrDefault("title"))?.ToString() ?? "", 140)}").ToList();
            var letters = ((List<object>)dl["letters"]).Cast<Dictionary<string, object>>().ToList();
            if (letters.Count > 0)
                r["letters"] = letters.Select(l => (object)($"{l["id"]}: {l["label"]} [{l["type"]}]" + (l.ContainsKey("mustAnswer") ? " MUST ANSWER" : "") + (l.ContainsKey("expiresInHours") ? $" exp {l["expiresInHours"]}h" : ""))).ToList();
            var quests = ((List<object>)dl["questOffers"]).Cast<Dictionary<string, object>>().ToList();
            if (quests.Count > 0)
                r["quests"] = quests.Select(q => (object)$"{q["id"]}: {q["name"]} exp {q.GetValueOrDefault("expiresInHours")}h | {Trim(q.GetValueOrDefault("rewards")?.ToString() ?? "", 100)}").ToList();
            var proj = Find.ResearchManager.GetProject();
            r["research"] = proj == null ? "NONE" : $"{proj.defName} {Pct(proj.ProgressPercent)}%";
            var ignore = new HashSet<string>(GovernorPolicy.Current.ignoreFactions ?? new List<string>());
            foreach (var f in StrList(a, "ignoreFactions") ?? new List<string>()) ignore.Add(f);
            var threats = new List<object>();
            foreach (var m in Find.Maps)
            {
                var hostiles = m.mapPawns.AllPawnsSpawned
                    .Where(p => !p.Dead && !p.Downed && p.HostileTo(Faction.OfPlayer) && !p.Fogged() && !ignore.Contains(p.Faction?.Name ?? "wild"))
                    .GroupBy(p => (p.Faction?.Name ?? "wild") + " " + p.KindLabel)
                    .Select(g => (object)$"{g.Count()}x {g.Key} @" + string.Join(" ", g.Take(6).Select(p => $"{p.Position.x},{p.Position.z}")))
                    .ToList();
                int homeFires = m.listerThings.ThingsOfDef(ThingDefOf.Fire).Count(f => m.areaManager.Home[f.Position]);
                var conds = m.gameConditionManager.ActiveConditions.Select(c => (object)c.LabelCap.ToString()).ToList();
                if (hostiles.Count == 0 && homeFires == 0 && conds.Count == 0) continue;
                var t = Obj();
                t["map"] = m.Parent?.LabelCap.ToString();
                if (hostiles.Count > 0) t["hostiles"] = hostiles;
                if (homeFires > 0) t["homeFires"] = homeFires;
                if (conds.Count > 0) t["conditions"] = conds;
                threats.Add(t);
            }
            if (threats.Count > 0) r["threats"] = threats;
            r["eventsSeq"] = GovernorEvents.Seq;
            return r;
        }

        // "Jos 54/35 hp100 @120,150 bleeding food18 | Cooking simple meal | Plague sev 40% imm 55% tended"
        static string PawnLine(Pawn p)
        {
            var sb = new System.Text.StringBuilder(p.LabelShort);
            if (p.needs?.mood != null) sb.Append($" {Pct(p.needs.mood.CurLevelPercentage)}/{Pct(p.mindState.mentalBreaker.BreakThresholdMinor)}");
            var hp = Pct(p.health.summaryHealth.SummaryHealthPercent);
            if (hp < 100) sb.Append($" hp{hp}");
            sb.Append($" @{p.Position.x},{p.Position.z}");
            if (p.Downed) sb.Append(" DOWNED");
            if (p.health.hediffSet.BleedRateTotal > 0f) sb.Append(" BLEEDING");
            if (p.Drafted) sb.Append(" drafted");
            if (p.CurJob != null && p.CurJob.playerForced) sb.Append(" FORCED");
            if (p.InBed()) sb.Append(" inBed");
            if (p.needs?.food != null && p.needs.food.CurLevelPercentage < 0.25f) sb.Append($" food{Pct(p.needs.food.CurLevelPercentage)}");
            if (p.MentalStateDef != null) sb.Append(" MENTAL:" + p.MentalStateDef.label);
            var job = Clean(p.jobs?.curDriver?.GetReport());
            if (!job.NullOrEmpty()) sb.Append(" | ").Append(Trim(job, 50));
            foreach (var h in p.health.hediffSet.hediffs.Where(h => h.Visible && h.def.lethalSeverity > 0f && h.TryGetComp<HediffComp_Immunizable>() != null))
                sb.Append(" | ").Append(DiseaseInfo(h));
            return sb.ToString();
        }

        // ------------------------------------------------------------------ map summary

        // Clusters (4-connected) of cells that pass a test, largest first: bounding box, cell count,
        // and the largest full rectangle inside (what a zone or a room can actually use).
        static List<object> Clusters(Map map, CellRect area, Func<IntVec3, bool> test, int minCells, int max, bool innerRect)
        {
            int w = area.Width, h = area.Height;
            var on = new bool[w, h];
            foreach (var c in area) if (!c.Fogged(map) && test(c)) on[c.x - area.minX, c.z - area.minZ] = true;
            var seen = new bool[w, h];
            var found = new List<(int n, int x1, int z1, int x2, int z2, long sx, long sz)>();
            var stack = new Stack<(int, int)>();
            for (int x = 0; x < w; x++)
                for (int z = 0; z < h; z++)
                {
                    if (!on[x, z] || seen[x, z]) continue;
                    int n = 0, x1 = x, x2 = x, z1 = z, z2 = z; long sx = 0, sz = 0;
                    stack.Push((x, z)); seen[x, z] = true;
                    while (stack.Count > 0)
                    {
                        var (cx, cz) = stack.Pop();
                        n++; sx += cx; sz += cz;
                        if (cx < x1) x1 = cx; if (cx > x2) x2 = cx; if (cz < z1) z1 = cz; if (cz > z2) z2 = cz;
                        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                        {
                            int nx = cx + dx, nz = cz + dz;
                            if (nx < 0 || nz < 0 || nx >= w || nz >= h || seen[nx, nz] || !on[nx, nz]) continue;
                            seen[nx, nz] = true; stack.Push((nx, nz));
                        }
                    }
                    if (n >= minCells) found.Add((n, x1, z1, x2, z2, sx, sz));
                }
            var res = new List<object>();
            foreach (var f in found.OrderByDescending(f => f.n).Take(max))
            {
                var o = new Dictionary<string, object>
                {
                    ["cells"] = f.n,
                    ["bbox"] = new List<object> { f.x1 + area.minX, f.z1 + area.minZ, f.x2 + area.minX, f.z2 + area.minZ },
                    ["center"] = new List<object> { (int)(f.sx / f.n) + area.minX, (int)(f.sz / f.n) + area.minZ },
                };
                if (innerRect)
                {
                    var best = LargestRect(on, f.x1, f.z1, f.x2, f.z2);
                    if (best.area > 0)
                        o["rect"] = new List<object> { best.x1 + area.minX, best.z1 + area.minZ, best.x2 + area.minX, best.z2 + area.minZ };
                }
                res.Add(o);
            }
            return res;
        }

        // largest all-true rectangle in on[] within a box (histogram + stack, O(w*h))
        static (int area, int x1, int z1, int x2, int z2) LargestRect(bool[,] on, int bx1, int bz1, int bx2, int bz2)
        {
            int w = bx2 - bx1 + 1;
            var hgt = new int[w];
            (int area, int x1, int z1, int x2, int z2) best = (0, 0, 0, 0, 0);
            for (int z = bz1; z <= bz2; z++)
            {
                for (int i = 0; i < w; i++) hgt[i] = on[bx1 + i, z] ? hgt[i] + 1 : 0;
                var st = new Stack<int>();
                for (int i = 0; i <= w; i++)
                {
                    int cur = i == w ? 0 : hgt[i];
                    while (st.Count > 0 && hgt[st.Peek()] >= cur)
                    {
                        int top = st.Pop();
                        int height = hgt[top];
                        int left = st.Count == 0 ? 0 : st.Peek() + 1;
                        int ar = height * (i - left);
                        // skip slivers: a 1-wide strip is no use for a room or a field
                        if (ar > best.area && height >= 2 && i - left >= 2)
                            best = (ar, bx1 + left, z - height + 1, bx1 + i - 1, z);
                    }
                    st.Push(i);
                }
            }
            return best;
        }

        static bool IsNaturalRock(Map map, IntVec3 c)
        {
            var e = c.GetEdifice(map);
            return e != null && e.def.building != null && e.def.building.isNaturalRock && !e.def.building.isResourceRock;
        }

        static bool IsMovingWater(TerrainDef t) => t.IsRiver || t.defName.IndexOf("Moving", StringComparison.OrdinalIgnoreCase) >= 0;

        static object MapSummary(Dictionary<string, object> a)
        {
            var map = CurMap();
            var area = a.ContainsKey("x") ? Rect(a, map, 1000000) : CellRect.WholeMap(map);
            int max = Int(a, "max", 8);
            var r = Obj();
            r["mapSize"] = Pos(map.Size);
            r["area"] = new List<object> { area.minX, area.minZ, area.maxX, area.maxZ };
            r["note"] = "boxes are [x1,z1,x2,z2]; rect = largest full rectangle inside a cluster; fogged cells are left out";
            int fogged = area.Cells.Count(c => c.Fogged(map));
            if (fogged > 0) r["foggedCells"] = fogged;

            bool Open(IntVec3 c) => c.Standable(map) && c.GetEdifice(map) == null;
            r["mountains"] = Clusters(map, area, c => IsNaturalRock(map, c), 30, max, false)
                .Select(o =>
                {
                    var d = (Dictionary<string, object>)o;
                    var b = (List<object>)d["bbox"];
                    var rect = CellRect.FromLimits((int)b[0], (int)b[1], (int)b[2], (int)b[3]);
                    d["overheadMountainCells"] = rect.Cells.Count(c => !c.Fogged(map) && map.roofGrid.RoofAt(c)?.isThickRoof == true);
                    return o;
                }).ToList();
            // flat, open, wall-carrying ground (rooms with stone walls go here)
            r["heavyGround"] = Clusters(map, area, c => Open(c) && c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy) && !c.GetTerrain(map).IsWater, 60, max, true);
            // ground next to a mountain face: cheap walls on one side, rooms can be dug in
            r["mountainEdge"] = Clusters(map, area, c => Open(c) && c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy) &&
                                                     GenAdj.CellsAdjacent8Way(new TargetInfo(c, map)).Any(n => n.InBounds(map) && IsNaturalRock(map, n)), 20, max, false);
            r["richSoil"] = Clusters(map, area, c => Open(c) && c.GetFertility(map) >= 1.39f, 12, max, true);
            r["soil"] = Clusters(map, area, c => Open(c) && c.GetFertility(map) >= 0.99f, 40, max, true);
            r["movingWater"] = Clusters(map, area, c => IsMovingWater(c.GetTerrain(map)), 10, max, false);
            r["stillWater"] = Clusters(map, area, c => c.GetTerrain(map).IsWater && !IsMovingWater(c.GetTerrain(map)), 10, max, false);
            r["mudMarsh"] = Clusters(map, area, c => !c.GetTerrain(map).IsWater && !c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy) &&
                                                  c.GetTerrain(map).passability != Traversability.Impassable && c.GetEdifice(map) == null, 15, max, false);
            // ruins: buildings that belong to nobody and are not rock (ancient walls, junk)
            r["ruins"] = Clusters(map, area, c =>
            {
                var e = c.GetEdifice(map);
                return e != null && e.Faction == null && e.def.building != null && !e.def.building.isNaturalRock && !e.def.building.isResourceRock;
            }, 4, max, false);
            // resources: every visible resource rock and ship chunk, grouped by def
            var res = new List<object>();
            foreach (var g in map.listerThings.AllThings
                         .Where(t => t.Spawned && !t.Fogged() && area.Contains(t.Position) &&
                                     (t.def.building?.isResourceRock == true || t.def.defName.IndexOf("ShipChunk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      t.def.defName == "SteamGeyser"))
                         .GroupBy(t => t.def))
            {
                var cells = g.Select(t => t.Position).ToList();
                // nearby veins as groups: positions of the first cell of each 4-connected group
                var set = new HashSet<IntVec3>(cells);
                var groups = new List<object>();
                var seen = new HashSet<IntVec3>();
                foreach (var c in cells)
                {
                    if (!seen.Add(c)) continue;
                    int n = 0; var q = new Queue<IntVec3>(); q.Enqueue(c);
                    while (q.Count > 0)
                    {
                        var cc = q.Dequeue(); n++;
                        foreach (var d in GenAdj.CardinalDirections)
                        {
                            var nb = cc + d;
                            if (set.Contains(nb) && seen.Add(nb)) q.Enqueue(nb);
                        }
                    }
                    groups.Add($"{c.x},{c.z}" + (n > 1 ? $"x{n}" : ""));
                }
                var o = new Dictionary<string, object> { ["def"] = g.Key.defName, ["label"] = g.Key.label, ["cells"] = cells.Count };
                var yield = g.Key.building?.mineableThing;
                if (yield != null) o["yields"] = $"{g.Key.building.EffectiveMineableYield} {yield.label} per cell";
                o["at"] = groups.Take(20).ToList();
                if (groups.Count > 20) o["more"] = groups.Count - 20;
                res.Add(o);
            }
            r["resources"] = res.OrderBy(o => ((Dictionary<string, object>)o)["def"]).ToList();
            // what is built and where (the colony footprint)
            var mine = map.listerBuildings.allBuildingsColonist;
            if (mine.Count > 0)
            {
                var xs = mine.Select(b => b.Position.x).ToList(); var zs = mine.Select(b => b.Position.z).ToList();
                r["colonyBuildings"] = new Dictionary<string, object> { ["count"] = mine.Count, ["bbox"] = new List<object> { xs.Min(), zs.Min(), xs.Max(), zs.Max() } };
            }
            var home = map.areaManager.Home.ActiveCells.ToList();
            if (home.Count > 0)
                r["homeArea"] = new List<object> { home.Min(c => c.x), home.Min(c => c.z), home.Max(c => c.x), home.Max(c => c.z) };
            return r;
        }
    }

    static class DictExt
    {
        public static object GetValueOrDefault(this Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v : null;
    }
}
