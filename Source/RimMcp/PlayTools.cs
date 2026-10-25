using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimMcp
{
    // Tools that play the game: map reading, designations, building, zones, research,
    // bills, restrictions, orders. Everything goes through the same checks the UI uses
    // (designators, GenConstruct, research gates), so nothing here can do what a player
    // could not.
    public static partial class Tools
    {
        static Tools()
        {
            var more = new Dictionary<string, Func<Dictionary<string, object>, object>>
            {
                ["map_view"] = MapView,
                ["things"] = Things,
                ["designate"] = Designate,
                ["buildable"] = Buildable,
                ["build"] = Build,
                ["zone"] = ZoneTool,
                ["zones"] = ZonesList,
                ["research"] = Research,
                ["bills"] = Bills,
                ["forbid"] = Forbid,
                ["allow_area"] = AllowArea,
                ["schedule"] = Schedule,
                ["pawn_settings"] = PawnSettings,
                ["drafted_move"] = DraftedMove,
                ["orders"] = Orders,
                ["run"] = RunFor,
                ["save"] = Save,
                ["dialogs"] = Dialogs,
                ["choose"] = Choose,
                ["trade"] = Trade,
                ["newgame"] = NewGame,
                ["bed"] = Bed,
                ["prisoners"] = Prisoners,
                ["rooms"] = Rooms,
                ["power"] = Power,
                ["thermostat"] = Thermostat,
                ["switch"] = Switch,
                ["gizmos"] = Gizmos,
                ["governor"] = GovernorTool,
            };
            foreach (var kv in more) handlers[kv.Key] = kv.Value;
        }

        // ------------------------------------------------------------------ arg helpers

        static int Int(Dictionary<string, object> a, string key, int? def = null)
        {
            if (a.TryGetValue(key, out var v) && v != null)
            {
                if (v is double d) return (int)Math.Round(d);
                if (int.TryParse(v.ToString(), out var i)) return i;
                throw new ToolError($"argument {key} must be an integer");
            }
            if (def.HasValue) return def.Value;
            throw new ToolError("missing argument: " + key);
        }

        static bool Bool(Dictionary<string, object> a, string key, bool def)
        {
            if (!a.TryGetValue(key, out var v) || v == null) return def;
            if (v is bool b) return b;
            return string.Equals(v.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        static List<string> StrList(Dictionary<string, object> a, string key)
        {
            if (!a.TryGetValue(key, out var v) || v == null) return null;
            if (v is List<object> l) return l.Select(x => x.ToString()).ToList();
            return v.ToString().Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }

        static Map CurMap()
        {
            RequireGame();
            return Find.CurrentMap ?? throw new ToolError("no current map");
        }

        static IntVec3 Cell(Dictionary<string, object> a, Map map, string xk = "x", string zk = "z")
        {
            var c = new IntVec3(Int(a, xk), 0, Int(a, zk));
            if (!c.InBounds(map)) throw new ToolError($"cell ({c.x},{c.z}) is outside the map (size {map.Size.x}x{map.Size.z})");
            return c;
        }

        // Rectangle from x,z (+ optional x2,z2 or w,h). Clipped to the map.
        static CellRect Rect(Dictionary<string, object> a, Map map, int maxCells = 40000)
        {
            int x = Int(a, "x"), z = Int(a, "z");
            int x2, z2;
            if (a.ContainsKey("x2") || a.ContainsKey("z2")) { x2 = Int(a, "x2", x); z2 = Int(a, "z2", z); }
            else { x2 = x + Int(a, "w", 1) - 1; z2 = z + Int(a, "h", 1) - 1; }
            var r = CellRect.FromLimits(Math.Min(x, x2), Math.Min(z, z2), Math.Max(x, x2), Math.Max(z, z2));
            r = r.ClipInsideMap(map);
            if (r.Area == 0) throw new ToolError("rectangle is outside the map");
            if (r.Area > maxCells) throw new ToolError($"rectangle too big ({r.Area} cells, max {maxCells})");
            return r;
        }

        static List<object> Pos(IntVec3 c) => new List<object> { c.x, c.z };

        static ThingDef FindThingDef(string name, bool required = true)
        {
            if (name == null) return null;
            var d = DefDatabase<ThingDef>.GetNamedSilentFail(name)
                    ?? DefDatabase<ThingDef>.AllDefsListForReading.FirstOrDefault(t =>
                        string.Equals(t.label, name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.defName, name, StringComparison.OrdinalIgnoreCase));
            if (d == null && required) throw new ToolError($"no ThingDef '{name}' (use defName, e.g. Wall, Steel, Plant_Rice)");
            return d;
        }

        static void AddReason(Dictionary<string, int> reasons, AcceptanceReport rep)
        {
            var key = rep.Reason.NullOrEmpty() ? "not allowed here" : Clean(rep.Reason);
            reasons[key] = reasons.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        static Dictionary<string, object> ReasonsObj(Dictionary<string, int> reasons) =>
            reasons.OrderByDescending(kv => kv.Value).Take(8).ToDictionary(kv => kv.Key, kv => (object)kv.Value);

        // ------------------------------------------------------------------ map_view

        static char CellChar(Map map, IntVec3 c)
        {
            if (c.Fogged(map)) return '?';
            var things = c.GetThingList(map);
            char best = '\0';
            int bestRank = -1;
            foreach (var t in things)
            {
                char ch; int rank;
                if (t is Pawn p)
                {
                    if (p.Dead) continue;
                    rank = 9;
                    ch = p.IsColonist ? '@' : p.HostileTo(Faction.OfPlayer) ? 'E' : p.RaceProps.Animal ? (p.Faction == Faction.OfPlayer ? 'A' : 'a') : 'h';
                }
                else if (t.def == ThingDefOf.Fire) { rank = 8; ch = 'F'; }
                else if (t is Blueprint) { rank = 6; ch = 'b'; }
                else if (t is Frame) { rank = 6; ch = 'f'; }
                else if (t.def.category == ThingCategory.Building)
                {
                    rank = 7;
                    var bp = t.def.building;
                    if (bp != null && bp.isNaturalRock) ch = bp.isResourceRock ? 'O' : 'R';
                    else if (t is Building_Door) ch = '+';
                    else if (t.def.IsEdifice() && t.def.passability == Traversability.Impassable && t.def.fillPercent >= 0.99f) ch = '#';
                    else if (bp != null && bp.isPowerConduit) { rank = 1; ch = '\''; }
                    else ch = 'B';
                }
                else if (t is Plant pl)
                {
                    if (pl.def.plant.IsTree) { rank = 5; ch = 'T'; }
                    else if (pl.sown) { rank = 4; ch = 'c'; }
                    else continue;
                }
                else if (t.def.category == ThingCategory.Item)
                {
                    rank = 3;
                    ch = t.def.thingCategories != null && t.def.thingCategories.Contains(ThingCategoryDefOf.StoneChunks) ? 'k' : 'i';
                }
                else continue;
                if (rank > bestRank) { bestRank = rank; best = ch; }
            }
            if (bestRank >= 3) return best;
            var zone = map.zoneManager.ZoneAt(c);
            if (zone is Zone_Growing) return 'g';
            if (zone is Zone_Stockpile) return 's';
            if (zone != null) return 'z';
            if (bestRank >= 0) return best;
            var terr = c.GetTerrain(map);
            if (terr.bridge) return '=';
            if (terr.passability == Traversability.Impassable) return 'X';
            if (terr.IsWater) return terr.passability == Traversability.Standable ? '~' : 'W';
            if (!terr.natural) return '_';
            var fert = c.GetFertility(map);
            if (fert >= 1.1f) return ',';
            if (fert >= 0.95f) return '.';
            if (fert > 0f) return ':';
            return '-';
        }

        static char RoofChar(Map map, IntVec3 c)
        {
            if (c.Fogged(map)) return '?';
            var r = map.roofGrid.RoofAt(c);
            if (r == null) return '.';
            if (r.isThickRoof) return 'M';
            if (r.isNatural) return 't';
            return 'c';
        }

        // what the terrain can carry, as the player sees when inspecting it (walls/turrets need heavy)
        static char BuildChar(Map map, IntVec3 c)
        {
            if (c.Fogged(map)) return '?';
            var t = c.GetTerrain(map);
            if (t.affordances.Contains(TerrainAffordanceDefOf.Heavy)) return 'H';
            if (t.affordances.Contains(TerrainAffordanceDefOf.Medium)) return 'm';
            if (t.affordances.Contains(TerrainAffordanceDefOf.Light)) return 'l';
            return t.passability == Traversability.Impassable ? 'X' : 'x';
        }

        static char FertChar(Map map, IntVec3 c)
        {
            if (c.Fogged(map)) return '?';
            var f = c.GetFertility(map);
            if (f <= 0f) return '.';
            return (char)('0' + Mathf.Clamp(Mathf.RoundToInt(f * 5f), 1, 9)); // 5 = 100%
        }

        const string MapLegend =
            "north is up. ?=unexplored @=colonist A=colony animal a=wild animal E=hostile h=other person " +
            "F=fire #=wall/solid building +=door B=other building '=conduit b=blueprint f=frame(under construction) " +
            "R=natural rock O=ore/resource rock T=tree c=crop i=item k=stone chunk g=growing zone s=stockpile z=other zone " +
            "terrain: ,=rich soil .=soil :=poor soil(gravel/sand) -=infertile ground _=floor ~=shallow water/marsh " +
            "W=deep water X=impassable ==bridge";

        static object MapView(Dictionary<string, object> a)
        {
            var map = CurMap();
            int step = Math.Max(1, Int(a, "step", 1));
            CellRect rect;
            if (!a.ContainsKey("x")) rect = CellRect.WholeMap(map);
            else rect = Rect(a, map, 1000000);
            int cols = (rect.Width + step - 1) / step, rows = (rect.Height + step - 1) / step;
            if (cols * rows > 12000) throw new ToolError($"view too big ({cols}x{rows}); use a smaller area or a bigger step (whole map: step 4)");
            var layers = StrList(a, "layers") ?? new List<string> { "main" };
            var r = Obj();
            r["mapSize"] = Pos(map.Size);
            r["area"] = new Dictionary<string, object> { ["x"] = rect.minX, ["z"] = rect.minZ, ["x2"] = rect.maxX, ["z2"] = rect.maxZ, ["step"] = step };
            r["rowFormat"] = "each row starts with its z, then one char per x from x=" + rect.minX + " step " + step;
            foreach (var layer in layers)
            {
                Func<Map, IntVec3, char> f;
                switch (layer)
                {
                    case "main": f = CellChar; r["legend"] = MapLegend; break;
                    case "roof": f = RoofChar; r["roofLegend"] = ".=open sky c=constructed roof t=thin rock roof M=overhead mountain (thick, cannot be removed; nothing drops through)"; break;
                    case "fertility": f = FertChar; r["fertilityLegend"] = ".=0 (cannot grow), 5=100%, 7=140% (rich soil)"; break;
                    case "build": f = BuildChar; r["buildLegend"] = "H=heavy (walls, turrets) m=medium l=light only x=nothing (mud, water) X=impassable; terrain only, ignores what stands on it"; break;
                    default: throw new ToolError("layers: main, roof, fertility, build");
                }
                var lines = new List<object>();
                var axis = new StringBuilder("     ");
                for (int x = rect.minX; x <= rect.maxX; x += step) axis.Append((x / step) % 10 == 0 ? '|' : ' ');
                lines.Add(axis.ToString() + "  (| = x multiple of " + 10 * step + ")");
                for (int z = rect.maxZ; z >= rect.minZ; z -= step)
                {
                    var sb = new StringBuilder(z.ToString().PadLeft(4)).Append(' ');
                    for (int x = rect.minX; x <= rect.maxX; x += step) sb.Append(f(map, new IntVec3(x, 0, z)));
                    lines.Add(sb.ToString());
                }
                r[layer] = lines;
            }
            if (Bool(a, "objects", step == 1))
            {
                var seen = new HashSet<Thing>();
                var objs = new List<object>();
                foreach (var c in rect)
                {
                    if (c.Fogged(map)) continue;
                    foreach (var t in c.GetThingList(map))
                    {
                        if (!seen.Add(t)) continue;
                        string kind = null;
                        if (t is Pawn p) { if (p.Dead) continue; kind = p.IsColonist ? "colonist" : p.HostileTo(Faction.OfPlayer) ? "hostile" : p.RaceProps.Animal ? "animal" : "person"; }
                        else if (t is Blueprint) kind = "blueprint";
                        else if (t is Frame) kind = "frame";
                        else if (t.def.category == ThingCategory.Building)
                        {
                            var bp = t.def.building;
                            if (bp != null && (bp.isNaturalRock && !bp.isResourceRock || bp.isPowerConduit)) continue;
                            if (t.def.IsEdifice() && t.def.fillPercent >= 0.99f && t.def.passability == Traversability.Impassable && t.def.building?.isResourceRock != true && !(t is Building_Door) && t.def.size == IntVec2.One) continue;
                            kind = bp?.isResourceRock == true ? "ore" : "building";
                        }
                        else if (t.def.category == ThingCategory.Item) { if (t.def.thingCategories != null && t.def.thingCategories.Contains(ThingCategoryDefOf.StoneChunks)) continue; kind = "item"; }
                        else continue;
                        var o = new Dictionary<string, object> { ["label"] = Clean(t.LabelCap), ["kind"] = kind, ["pos"] = Pos(t.Position) };
                        if (t.def.size != IntVec2.One && t.def.category == ThingCategory.Building) o["size"] = new List<object> { t.RotatedSize.x, t.RotatedSize.z };
                        if (kind == "item" && t.IsForbidden(Faction.OfPlayer)) o["forbidden"] = true;
                        if (kind == "building" || kind == "blueprint" || kind == "frame") o["id"] = t.ThingID;
                        objs.Add(o);
                    }
                }
                r["objectCount"] = objs.Count;
                r["objects"] = objs.Take(Int(a, "maxObjects", 200)).ToList();
            }
            return r;
        }

        // ------------------------------------------------------------------ things

        static object Things(Dictionary<string, object> a)
        {
            var map = CurMap();
            var q = Str(a, "query", false);
            var kind = Str(a, "kind", false); // item, building, plant, pawn, animal, hostile
            IEnumerable<Thing> all = map.listerThings.AllThings.Where(t => t.Spawned && !t.Fogged());
            if (a.ContainsKey("x")) { var rect = Rect(a, map, 1000000); all = all.Where(t => rect.Contains(t.Position)); }
            switch (kind)
            {
                case null: break;
                case "item": all = all.Where(t => t.def.category == ThingCategory.Item); break;
                case "building": all = all.Where(t => t.def.category == ThingCategory.Building && t.Faction == Faction.OfPlayer); break;
                case "plant": all = all.Where(t => t is Plant); break;
                case "pawn": all = all.Where(t => t is Pawn); break;
                case "animal": all = all.Where(t => t is Pawn p && p.RaceProps.Animal && !p.Dead); break;
                case "hostile": all = all.Where(t => t is Pawn p && !p.Dead && p.HostileTo(Faction.OfPlayer)); break;
                case "blueprint": all = all.Where(t => t is Blueprint || t is Frame); break;
                default: throw new ToolError("kind: item, building, plant, pawn, animal, hostile, blueprint");
            }
            if (q != null)
                all = all.Where(t => t.def.defName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     t.LabelNoCount.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            var list = all.ToList();
            var total = list.Count;
            return new Dictionary<string, object>
            {
                ["count"] = total,
                ["things"] = list.Take(Int(a, "limit", 60)).Select(t =>
                {
                    var o = new Dictionary<string, object>
                    {
                        ["label"] = Clean(t.LabelCap), ["def"] = t.def.defName, ["id"] = t.ThingID, ["pos"] = Pos(t.Position),
                    };
                    if (t.def.category == ThingCategory.Item)
                    {
                        if (t.IsForbidden(Faction.OfPlayer)) o["forbidden"] = true;
                        o["stored"] = t.IsInAnyStorage();
                    }
                    if (t is Pawn p)
                    {
                        if (p.Faction != null) o["faction"] = p.Faction.Name;
                        if (p.Downed) o["downed"] = true;
                        if (p.RaceProps.Animal) o["wildnessPct"] = Pct(p.GetStatValue(StatDefOf.Wildness));
                        var des = map.designationManager.AllDesignationsOn(p).Select(d => (object)d.def.defName).ToList();
                        if (des.Count > 0) o["designations"] = des;
                    }
                    if (t is Plant pl) { o["growth"] = Pct(pl.Growth); o["harvestable"] = pl.HarvestableNow; }
                    if (t.def.useHitPoints && t.HitPoints < t.MaxHitPoints) o["hpPct"] = Pct((float)t.HitPoints / t.MaxHitPoints);
                    return (object)o;
                }).ToList(),
            };
        }

        // ------------------------------------------------------------------ designate

        static Designator MakeDesignator(string type)
        {
            switch (type)
            {
                case "mine": return new Designator_Mine();
                case "cut": case "cut_plants": return new Designator_PlantsCut();
                case "chop": case "chop_wood": return new Designator_PlantsHarvestWood();
                case "harvest": return new Designator_PlantsHarvest();
                case "hunt": return new Designator_Hunt();
                case "tame": return new Designator_Tame();
                case "slaughter": return new Designator_Slaughter();
                case "deconstruct": return new Designator_Deconstruct();
                case "uninstall": return new Designator_Uninstall();
                case "cancel": return new Designator_Cancel();
                case "haul": return new Designator_Haul();
                case "smooth": return new Designator_SmoothSurface();
                case "remove_floor": return new Designator_RemoveFloor();
                case "claim": return new Designator_Claim();
                case "strip": return new Designator_Strip();
                case "forbid": return new Designator_Forbid();
                case "unforbid": case "allow": return new Designator_Unforbid();
                case "roof_build": return new Designator_AreaBuildRoof();
                case "roof_remove": return new Designator_AreaNoRoof();
                case "home_add": return new Designator_AreaHomeExpand();
                case "home_remove": return new Designator_AreaHomeClear();
                default:
                    throw new ToolError("type must be one of: mine, cut, chop, harvest, hunt, tame, slaughter, deconstruct, " +
                                        "uninstall, cancel, haul, smooth, remove_floor, claim, strip, forbid, unforbid, " +
                                        "roof_build, roof_remove, home_add, home_remove");
            }
        }

        static object Designate(Dictionary<string, object> a)
        {
            var map = CurMap();
            var type = Str(a, "type").ToLowerInvariant();
            var des = MakeDesignator(type);
            var rect = Rect(a, map, 200000);
            var ok = new List<IntVec3>();
            var reasons = new Dictionary<string, int>();
            foreach (var c in rect)
            {
                var rep = des.CanDesignateCell(c);
                if (rep.Accepted) ok.Add(c);
                else if (!rep.Reason.NullOrEmpty()) AddReason(reasons, rep);
            }
            if (ok.Count > 0) des.DesignateMultiCell(ok);
            var r = new Dictionary<string, object>
            {
                ["type"] = type, ["cellsChecked"] = rect.Area, ["cellsDesignated"] = ok.Count,
            };
            if (reasons.Count > 0) r["rejected"] = ReasonsObj(reasons);
            if (ok.Count == 0) r["note"] = "nothing in that rectangle could take this designation (cells without a target are skipped silently)";
            return r;
        }

        // ------------------------------------------------------------------ build

        class BuildDesignator : Designator_Build
        {
            public BuildDesignator(BuildableDef d) : base(d) { }
            public Rot4 Rot { get => placingRot; set => placingRot = value; }
        }

        static BuildableDef FindBuildable(string name)
        {
            BuildableDef d = (BuildableDef)DefDatabase<ThingDef>.GetNamedSilentFail(name) ?? DefDatabase<TerrainDef>.GetNamedSilentFail(name);
            if (d == null)
                d = DefDatabase<ThingDef>.AllDefsListForReading.Where(t => t.designationCategory != null)
                        .Cast<BuildableDef>()
                        .Concat(DefDatabase<TerrainDef>.AllDefsListForReading.Where(t => t.designationCategory != null))
                        .FirstOrDefault(t => string.Equals(t.label, name, StringComparison.OrdinalIgnoreCase));
            if (d == null) throw new ToolError($"nothing buildable named '{name}' (see the buildable tool)");
            if (d.designationCategory == null) throw new ToolError($"{d.defName} cannot be built by players (no designation category)");
            return d;
        }

        static string CostText(BuildableDef d, ThingDef stuff)
        {
            var parts = new List<string>();
            if (d is ThingDef td && td.MadeFromStuff)
                parts.Add($"{td.CostStuffCount} {(stuff != null ? stuff.label : "stuff (" + string.Join("/", td.stuffCategories.Select(s => s.defName)) + ")")}");
            if (d.CostList != null) parts.AddRange(d.CostList.Select(c => $"{c.count} {c.thingDef.label}"));
            return string.Join(", ", parts);
        }

        static object Buildable(Dictionary<string, object> a)
        {
            var map = CurMap();
            var cat = Str(a, "category", false);
            var q = Str(a, "query", false);
            IEnumerable<BuildableDef> defs = DefDatabase<ThingDef>.AllDefsListForReading.Where(t => t.designationCategory != null).Cast<BuildableDef>()
                .Concat(DefDatabase<TerrainDef>.AllDefsListForReading.Where(t => t.designationCategory != null));
            if (cat != null) defs = defs.Where(d => string.Equals(d.designationCategory.defName, cat, StringComparison.OrdinalIgnoreCase) || string.Equals(d.designationCategory.label, cat, StringComparison.OrdinalIgnoreCase));
            if (q != null) defs = defs.Where(d => d.defName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (d.label ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            var res = new List<object>();
            var cats = new Dictionary<string, int>();
            foreach (var d in defs)
            {
                if (!new Designator_Build(d).Visible) continue;
                cats[d.designationCategory.defName] = cats.TryGetValue(d.designationCategory.defName, out var n) ? n + 1 : 1;
                if (cat == null && q == null) continue;
                var o = new Dictionary<string, object>
                {
                    ["def"] = d.defName, ["label"] = d.label, ["category"] = d.designationCategory.defName,
                    ["cost"] = CostText(d, null),
                };
                if (d is ThingDef td)
                {
                    if (td.size != IntVec2.One) o["size"] = new List<object> { td.size.x, td.size.z };
                    if (td.MadeFromStuff) o["stuff"] = GenStuff.AllowedStuffsFor(td).Where(s => map.resourceCounter.GetCount(s) > 0).Select(s => (object)s.defName).ToList();
                    if (td.rotatable) o["rotatable"] = true;
                    if (td.hasInteractionCell) o["interactionCell"] = new List<object> { td.interactionCellOffset.x, td.interactionCellOffset.z };
                }
                res.Add(o);
            }
            if (cat == null && q == null)
                return new Dictionary<string, object> { ["note"] = "pass category or query to list defs", ["categories"] = cats.ToDictionary(k => k.Key, k => (object)k.Value) };
            return res;
        }

        static object Build(Dictionary<string, object> a)
        {
            var map = CurMap();
            var def = FindBuildable(Str(a, "def"));
            var des = new BuildDesignator(def);
            if (!des.Visible) throw new ToolError($"{def.defName} is not available yet (research, tech level or a required building is missing)");
            var rotS = Str(a, "rot", false);
            if (rotS != null)
            {
                switch (rotS.ToLowerInvariant())
                {
                    case "north": case "n": case "0": des.Rot = Rot4.North; break;
                    case "east": case "e": case "1": des.Rot = Rot4.East; break;
                    case "south": case "s": case "2": des.Rot = Rot4.South; break;
                    case "west": case "w": case "3": des.Rot = Rot4.West; break;
                    default: throw new ToolError("rot: north, east, south, west");
                }
            }
            var stuffName = Str(a, "stuff", false);
            if (def is ThingDef td && td.MadeFromStuff)
            {
                if (stuffName != null)
                {
                    var stuff = FindThingDef(stuffName);
                    if (!stuff.IsStuff || !stuff.stuffProps.CanMake(td))
                        throw new ToolError($"{def.defName} cannot be made from {stuff.defName}. Allowed: " +
                                            string.Join(", ", GenStuff.AllowedStuffsFor(td).Select(s => s.defName)));
                    des.SetStuffDef(stuff);
                }
            }
            else if (stuffName != null) throw new ToolError($"{def.defName} is not made from stuff; drop the stuff argument");
            var rect = Rect(a, map, 5000);
            bool outline = Bool(a, "outline", false);
            var size = def is ThingDef t2 ? t2.size : IntVec2.One;
            var placed = new List<object>();
            var reasons = new Dictionary<string, int>();
            IEnumerable<IntVec3> cells;
            if (size != IntVec2.One) cells = new[] { rect.Min };   // one multi-cell building at x,z (its center for odd sizes)
            else cells = outline ? rect.EdgeCells : rect.Cells;
            foreach (var c in cells.ToList())
            {
                var rep = des.CanDesignateCell(c);
                if (rep.Accepted) { des.DesignateSingleCell(c); placed.Add(Pos(c)); }
                else AddReason(reasons, rep);
            }
            var r = new Dictionary<string, object>
            {
                ["def"] = def.defName, ["stuff"] = des.StuffDef?.defName, ["rot"] = des.Rot.ToStringHuman(),
                ["placed"] = placed.Count, ["costEach"] = CostText(def, des.StuffDef),
            };
            if (size != IntVec2.One)
            {
                r["note"] = $"multi-cell ({size.x}x{size.z}); x,z is the building's center cell" +
                            (size.x % 2 == 0 || size.z % 2 == 0 ? " (even sizes extend toward +x/+z depending on rotation)" : "");
                if (placed.Count == 1) r["occupies"] = GenAdj.OccupiedRect(rect.Min, des.Rot, size) is var occ ? new Dictionary<string, object> { ["x"] = occ.minX, ["z"] = occ.minZ, ["x2"] = occ.maxX, ["z2"] = occ.maxZ } : null;
            }
            if (placed.Count <= 6) r["cells"] = placed;
            if (des.StuffDef != null && !map.listerThings.ThingsOfDef(des.StuffDef).Any(t => !t.IsForbidden(Faction.OfPlayer))) r["warning"] = $"no usable {des.StuffDef.label} on the map: these blueprints wait until you get some";
            if (reasons.Count > 0) r["rejected"] = ReasonsObj(reasons);
            return r;
        }

        // ------------------------------------------------------------------ zones and areas

        static object ZonesList(Dictionary<string, object> a)
        {
            var map = CurMap();
            var res = new List<object>();
            foreach (var z in map.zoneManager.AllZones)
            {
                var cr = CellRect.FromCellList(z.cells);
                var o = new Dictionary<string, object>
                {
                    ["label"] = z.label, ["type"] = z.GetType().Name, ["cells"] = z.cells.Count,
                    ["bounds"] = new Dictionary<string, object> { ["x"] = cr.minX, ["z"] = cr.minZ, ["x2"] = cr.maxX, ["z2"] = cr.maxZ },
                };
                if (z is Zone_Growing g) { o["plant"] = g.GetPlantDefToGrow()?.defName; o["allowSow"] = g.allowSow; }
                if (z is Zone_Stockpile s) o["priority"] = s.settings.Priority.ToString();
                res.Add(o);
            }
            var areas = map.areaManager.AllAreas.Select(ar => (object)new Dictionary<string, object>
            {
                ["label"] = ar.Label, ["type"] = ar.GetType().Name, ["cells"] = ar.TrueCount, ["assignable"] = ar.AssignableAsAllowed(),
            }).ToList();
            return new Dictionary<string, object> { ["zones"] = res, ["areas"] = areas };
        }

        static Zone FindZone(Map map, string label) =>
            map.zoneManager.AllZones.FirstOrDefault(z => string.Equals(z.label, label, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolError($"no zone '{label}'. Zones: " + string.Join(", ", map.zoneManager.AllZones.Select(z => z.label)));

        static object ZoneTool(Dictionary<string, object> a)
        {
            var map = CurMap();
            var action = Str(a, "action").ToLowerInvariant();
            switch (action)
            {
                case "grow": case "growing": return MakeGrowing(a, map);
                case "stockpile": case "dumping": return MakeStockpile(a, map, action == "dumping");
                case "fishing":
                {
                    if (!ModsConfig.OdysseyActive) throw new ToolError("fishing zones need Odyssey");
                    var des = new Designator_ZoneAdd_Fishing();
                    if (!des.Visible) throw new ToolError("fishing zones need the Fishing research");
                    var cells = new List<IntVec3>();
                    var reasons = new Dictionary<string, int>();
                    foreach (var c in Rect(a, map, 5000))
                    {
                        var rep = des.CanDesignateCell(c);
                        if (rep.Accepted && map.zoneManager.ZoneAt(c) == null) cells.Add(c); else if (!rep.Accepted) AddReason(reasons, rep);
                    }
                    if (cells.Count == 0) throw new ToolError("no valid fishing cells. " + string.Join("; ", reasons.Select(kv => kv.Key + ": " + kv.Value)));
                    Find.Selector.ClearSelection();
                    var before = new HashSet<Zone>(map.zoneManager.AllZones);
                    des.DesignateMultiCell(cells);
                    var z = map.zoneManager.AllZones.FirstOrDefault(x => !before.Contains(x));
                    Find.Selector.ClearSelection();
                    return new Dictionary<string, object> { ["zone"] = z?.label, ["cells"] = z?.cells.Count ?? 0, ["skipped"] = ReasonsObj(reasons) };
                }
                case "set_plant":
                {
                    var z = FindZone(map, Str(a, "zone")) as Zone_Growing ?? throw new ToolError("not a growing zone");
                    var plant = CheckPlant(Str(a, "plant"), map);
                    z.SetPlantDefToGrow(plant);
                    return $"{z.label} now grows {plant.label}";
                }
                case "set_filter":
                {
                    // x,z (+x2,z2): storage buildings (shelves...) in that rect instead of a zone
                    if (a.ContainsKey("x"))
                    {
                        var stores = Rect(a, map).Cells.SelectMany(c => c.GetThingList(map)).OfType<Building_Storage>().Distinct().ToList();
                        if (stores.Count == 0) throw new ToolError("no storage building (shelf...) in that rectangle");
                        return stores.Select(b => SetFilter(b.settings, $"{b.LabelCap} ({b.Position.x},{b.Position.z})", a)).ToList();
                    }
                    var zone = FindZone(map, Str(a, "zone")) as Zone_Stockpile ?? throw new ToolError("not a stockpile");
                    return SetFilter(zone.settings, zone.label, a);
                }
                case "delete":
                {
                    var z = FindZone(map, Str(a, "zone"));
                    z.Delete();
                    return "deleted " + z.label;
                }
                case "remove_cells":
                {
                    var rect = Rect(a, map);
                    int n = 0;
                    foreach (var c in rect)
                    {
                        var z = map.zoneManager.ZoneAt(c);
                        if (z == null) continue;
                        z.RemoveCell(c);
                        n++;
                    }
                    foreach (var z in map.zoneManager.AllZones.ToList()) if (z.cells.Count == 0) z.Delete(); else z.CheckContiguous();
                    return $"removed {n} zone cells";
                }
                case "area_new":
                {
                    if (!map.areaManager.TryMakeNewAllowed(out var area)) throw new ToolError("too many allowed areas");
                    var label = Str(a, "label", false);
                    if (label != null) area.SetLabel(label);
                    if (a.ContainsKey("x")) foreach (var c in Rect(a, map)) area[c] = true;
                    return new Dictionary<string, object> { ["area"] = area.Label, ["cells"] = area.TrueCount };
                }
                case "area_add": case "area_remove":
                {
                    var area = FindArea(map, Str(a, "area"));
                    int n = 0;
                    foreach (var c in Rect(a, map)) { if (area[c] != (action == "area_add")) { area[c] = action == "area_add"; n++; } }
                    return new Dictionary<string, object> { ["area"] = area.Label, ["changed"] = n, ["cells"] = area.TrueCount };
                }
                case "area_delete":
                {
                    var area = FindArea(map, Str(a, "area"));
                    if (!area.Mutable) throw new ToolError(area.Label + " cannot be deleted");
                    area.Delete();
                    return "deleted area " + area.Label;
                }
                default:
                    throw new ToolError("action: grow, stockpile, dumping, set_plant, set_filter, delete, remove_cells, " +
                                        "area_new, area_add, area_remove, area_delete (home area and roofs: use designate home_add/home_remove/roof_build/roof_remove)");
            }
        }

        static Area FindArea(Map map, string label) =>
            map.areaManager.AllAreas.FirstOrDefault(x => string.Equals(x.Label, label, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolError($"no area '{label}'. Areas: " + string.Join(", ", map.areaManager.AllAreas.Select(x => x.Label)));

        static ThingDef CheckPlant(string name, Map map)
        {
            var plant = FindThingDef(name);
            if (plant.plant == null) throw new ToolError(plant.defName + " is not a plant");
            if (!plant.plant.sowTags.Contains("Ground")) throw new ToolError(plant.defName + " cannot be sown in a growing zone");
            if (!Command_SetPlantToGrow.IsPlantAvailable(plant, map))
                throw new ToolError(plant.defName + " is not available (needs research" +
                                    (plant.plant.sowResearchPrerequisites != null ? ": " + string.Join(", ", plant.plant.sowResearchPrerequisites.Select(r => r.label)) : "") + ")");
            return plant;
        }

        static object MakeGrowing(Dictionary<string, object> a, Map map)
        {
            var plant = CheckPlant(Str(a, "plant", false) ?? "Plant_Potato", map);
            var rect = Rect(a, map, 5000);
            var des = new Designator_ZoneAdd_Growing();
            var cells = new List<IntVec3>();
            var reasons = new Dictionary<string, int>();
            int lowFert = 0;
            foreach (var c in rect)
            {
                var rep = des.CanDesignateCell(c);
                if (!rep.Accepted) { if (!rep.Reason.NullOrEmpty()) AddReason(reasons, rep); else if (c.GetFertility(map) < plant.plant.fertilityMin) lowFert++; continue; }
                if (map.zoneManager.ZoneAt(c) != null) { reasons["already in a zone"] = reasons.TryGetValue("already in a zone", out var n) ? n + 1 : 1; continue; }
                if (c.GetFertility(map) < plant.plant.fertilityMin) { lowFert++; continue; }
                cells.Add(c);
            }
            if (lowFert > 0) reasons["fertility too low / blocked"] = lowFert;
            if (cells.Count == 0) throw new ToolError("no valid cells for a growing zone. " + string.Join("; ", reasons.Select(kv => kv.Key + ": " + kv.Value)));
            var zone = new Zone_Growing(map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (var c in cells) zone.AddCell(c);
            zone.SetPlantDefToGrow(plant);
            zone.CheckContiguous();
            var label = Str(a, "label", false);
            if (label != null) zone.label = label;
            var r = new Dictionary<string, object> { ["zone"] = zone.label, ["plant"] = plant.defName, ["cells"] = zone.cells.Count };
            if (reasons.Count > 0) r["skipped"] = ReasonsObj(reasons);
            return r;
        }

        static object MakeStockpile(Dictionary<string, object> a, Map map, bool dumping)
        {
            var rect = Rect(a, map, 5000);
            Designator_ZoneAddStockpile des = dumping ? (Designator_ZoneAddStockpile)new Designator_ZoneAddStockpile_Dumping() : new Designator_ZoneAddStockpile_Resources();
            var cells = new List<IntVec3>();
            var reasons = new Dictionary<string, int>();
            foreach (var c in rect)
            {
                var rep = des.CanDesignateCell(c);
                if (!rep.Accepted) { AddReason(reasons, rep); continue; }
                if (map.zoneManager.ZoneAt(c) != null) { reasons["already in a zone"] = reasons.TryGetValue("already in a zone", out var n) ? n + 1 : 1; continue; }
                cells.Add(c);
            }
            if (cells.Count == 0) throw new ToolError("no valid cells for a stockpile. " + string.Join("; ", reasons.Select(kv => kv.Key + ": " + kv.Value)));
            var zone = new Zone_Stockpile(dumping ? StorageSettingsPreset.DumpingStockpile : StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (var c in cells) zone.AddCell(c);
            zone.CheckContiguous();
            var label = Str(a, "label", false);
            if (label != null) zone.label = label;
            if (a.ContainsKey("allow") || a.ContainsKey("disallow") || a.ContainsKey("priority") || a.ContainsKey("only")) SetFilter(zone.settings, zone.label, a);
            zone.slotGroup.RemoveHaulDesignationOnStoredThings();
            var r = new Dictionary<string, object> { ["zone"] = zone.label, ["cells"] = zone.cells.Count, ["priority"] = zone.settings.Priority.ToString() };
            if (reasons.Count > 0) r["skipped"] = ReasonsObj(reasons);
            return r;
        }

        // only/allow/disallow: ThingCategoryDef or ThingDef defNames (e.g. Foods, Manufactured, ResourcesRaw, Weapons, Apparel, Corpses, Steel, WoodLog)
        static object SetFilter(StorageSettings settings, string label, Dictionary<string, object> a)
        {
            var filter = settings.filter;
            void Apply(List<string> names, bool allow)
            {
                if (names == null) return;
                foreach (var n in names)
                {
                    var cat = DefDatabase<ThingCategoryDef>.GetNamedSilentFail(n)
                              ?? DefDatabase<ThingCategoryDef>.AllDefsListForReading.FirstOrDefault(c => string.Equals(c.label, n, StringComparison.OrdinalIgnoreCase));
                    if (cat != null) { filter.SetAllow(cat, allow); continue; }
                    var td = FindThingDef(n, false);
                    if (td != null) { filter.SetAllow(td, allow); continue; }
                    var sf = DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail(n);
                    if (sf != null) { filter.SetAllow(sf, allow); continue; }
                    throw new ToolError($"'{n}' is not a ThingCategoryDef, ThingDef or SpecialThingFilterDef. Top categories: " +
                                        string.Join(", ", ThingCategoryDefOf.Root.childCategories.Select(c => c.defName)));
                }
            }
            var only = StrList(a, "only");
            if (only != null) { filter.SetDisallowAll(); Apply(only, true); }
            Apply(StrList(a, "allow"), true);
            Apply(StrList(a, "disallow"), false);
            var pr = Str(a, "priority", false);
            if (pr != null)
            {
                if (!Enum.TryParse<StoragePriority>(pr, true, out var p) || p == StoragePriority.Unstored)
                    throw new ToolError("priority: Low, Normal, Preferred, Important, Critical");
                settings.Priority = p;
            }
            return new Dictionary<string, object>
            {
                ["zone"] = label, ["priority"] = settings.Priority.ToString(),
                ["allowedCount"] = filter.AllowedDefCount, ["summary"] = Clean(filter.Summary),
            };
        }

        // ------------------------------------------------------------------ research

        static object Research(Dictionary<string, object> a)
        {
            RequireGame();
            var rm = Find.ResearchManager;
            var set = Str(a, "set", false);
            if (set != null)
            {
                var proj = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(set)
                           ?? DefDatabase<ResearchProjectDef>.AllDefsListForReading.FirstOrDefault(p => string.Equals(p.label, set, StringComparison.OrdinalIgnoreCase))
                           ?? throw new ToolError($"no research project '{set}'");
                if (proj.IsFinished) throw new ToolError(proj.label + " is already finished");
                if (!proj.CanStartNow)
                {
                    var why = new List<string>();
                    if (!proj.PrerequisitesCompleted) why.Add("prerequisites: " + string.Join(", ", (proj.prerequisites ?? new List<ResearchProjectDef>()).Concat(proj.hiddenPrerequisites ?? new List<ResearchProjectDef>()).Where(p => !p.IsFinished).Select(p => p.label)));
                    if (!proj.TechprintRequirementMet) why.Add($"needs techprints ({rm.GetTechprints(proj)}/{proj.TechprintCount})");
                    if (proj.requiredResearchBuilding != null && !proj.PlayerHasAnyAppropriateResearchBench) why.Add("needs " + proj.requiredResearchBuilding.label);
                    if (!proj.PlayerMechanitorRequirementMet) why.Add("needs a mechanitor");
                    throw new ToolError($"{proj.label} cannot start now: " + (why.Count > 0 ? string.Join("; ", why) : "requirements not met"));
                }
                if (proj.knowledgeCategory != null) throw new ToolError(proj.label + " is an anomaly (knowledge) project, studied from entities");
                rm.SetCurrentProject(proj);
                return $"researching {proj.label} ({proj.CostApparent:0} points)";
            }
            var cur = rm.GetProject();
            var avail = DefDatabase<ResearchProjectDef>.AllDefsListForReading
                .Where(p => p.CanStartNow && p.knowledgeCategory == null && p.baseCost > 0)
                .OrderBy(p => p.techLevel).ThenBy(p => p.CostApparent)
                .Select(p => (object)new Dictionary<string, object>
                {
                    ["def"] = p.defName, ["label"] = p.label, ["cost"] = Math.Round(p.CostApparent), ["progressPct"] = Pct(p.ProgressPercent),
                    ["tech"] = p.techLevel.ToString(), ["bench"] = p.requiredResearchBuilding?.label,
                    ["unlocks"] = Clean(p.description)?.Split('\n').FirstOrDefault(),
                }).ToList();
            var r = new Dictionary<string, object>
            {
                ["current"] = cur == null ? null : new Dictionary<string, object>
                {
                    ["def"] = cur.defName, ["label"] = cur.label, ["progressPct"] = Pct(cur.ProgressPercent), ["cost"] = Math.Round(cur.CostApparent),
                },
                ["available"] = avail,
                ["finishedCount"] = DefDatabase<ResearchProjectDef>.AllDefsListForReading.Count(p => p.IsFinished),
            };
            if (Bool(a, "finished", false))
                r["finished"] = DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(p => p.IsFinished).Select(p => (object)p.defName).ToList();
            return r;
        }

        // ------------------------------------------------------------------ bills

        static Building FindWorkbench(Dictionary<string, object> a, Map map)
        {
            var id = Str(a, "bench", false);
            var benches = map.listerBuildings.allBuildingsColonist.Where(b => b is IBillGiver).ToList();
            Building hit = null;
            if (id != null)
                hit = benches.FirstOrDefault(b => b.ThingID == id) ??
                      benches.FirstOrDefault(b => string.Equals(b.def.defName, id, StringComparison.OrdinalIgnoreCase) || string.Equals(b.def.label, id, StringComparison.OrdinalIgnoreCase));
            else if (a.ContainsKey("x"))
            {
                var c = Cell(a, map);
                hit = benches.FirstOrDefault(b => b.OccupiedRect().Contains(c));
            }
            if (hit == null)
                throw new ToolError("workbench not found (bench = id or defName, or x,z). Workbenches: " +
                                    string.Join(", ", benches.Select(b => $"{b.ThingID} {b.def.defName} at ({b.Position.x},{b.Position.z})")));
            return hit;
        }

        static WorkTypeDef BillWorkType(Building bench, RecipeDef recipe) =>
            recipe.requiredGiverWorkType ?? DefDatabase<WorkGiverDef>.AllDefsListForReading
                .FirstOrDefault(g => g.fixedBillGiverDefs != null && g.fixedBillGiverDefs.Contains(bench.def))?.workType;

        static object BillsOf(Building bench)
        {
            var giver = (IBillGiver)bench;
            return new Dictionary<string, object>
            {
                ["bench"] = bench.ThingID, ["def"] = bench.def.defName, ["pos"] = Pos(bench.Position),
                ["bills"] = giver.BillStack.Bills.Select((b, i) =>
                {
                    var o = new Dictionary<string, object> { ["index"] = i, ["recipe"] = b.recipe.defName, ["label"] = Clean(b.LabelCap), ["suspended"] = b.suspended };
                    if (b is Bill_Production bp)
                    {
                        o["mode"] = bp.repeatMode.defName;
                        o["info"] = Clean(bp.RepeatInfoText);
                    }
                    if (!b.recipe.skillRequirements.NullOrEmpty())
                        o["needs"] = string.Join(", ", b.recipe.skillRequirements.Select(sr => $"{sr.skill.label} {sr.minLevel}"));
                    // who would actually do it: work type enabled and skills high enough; empty = the bill never runs
                    var work = BillWorkType(bench, b.recipe);
                    o["doers"] = AllColonists().Where(p => !p.Downed && p.workSettings != null && p.workSettings.EverWork &&
                            (work == null || (!p.WorkTypeIsDisabled(work) && p.workSettings.GetPriority(work) > 0)) &&
                            b.recipe.PawnSatisfiesSkillRequirements(p))
                        .Select(p => (object)(work == null ? p.LabelShort : $"{p.LabelShort}({p.workSettings.GetPriority(work)})")).ToList();
                    return (object)o;
                }).ToList(),
                ["recipes"] = bench.def.AllRecipes.Where(rc => rc.AvailableNow && rc.AvailableOnNow(bench)).Select(rc => (object)rc.defName).ToList(),
            };
        }

        // Only one material for a stuff-made product (steel longsword, not silver); fixed ingredients stay allowed.
        static void RestrictToStuff(Bill bill, string stuffName)
        {
            if (!(bill.recipe.ProducedThingDef?.MadeFromStuff ?? false)) throw new ToolError(bill.recipe.defName + " is not made from a material");
            var stuff = DefDatabase<ThingDef>.AllDefs.FirstOrDefault(d => d.IsStuff && bill.recipe.fixedIngredientFilter.Allows(d) &&
                            (string.Equals(d.defName, stuffName, StringComparison.OrdinalIgnoreCase) || string.Equals(d.label, stuffName, StringComparison.OrdinalIgnoreCase)))
                        ?? throw new ToolError($"'{stuffName}' is not a material for {bill.recipe.defName}. Options: " +
                            string.Join(", ", bill.recipe.fixedIngredientFilter.AllowedThingDefs.Where(d => d.IsStuff).Select(d => d.defName)));
            bill.ingredientFilter.SetDisallowAll();
            bill.ingredientFilter.SetAllow(stuff, true);
        }

        static object Bills(Dictionary<string, object> a)
        {
            var map = CurMap();
            var action = (Str(a, "action", false) ?? "list").ToLowerInvariant();
            if (action == "list" && !a.ContainsKey("bench") && !a.ContainsKey("x"))
                return map.listerBuildings.allBuildingsColonist.Where(b => b is IBillGiver && b.def.AllRecipes.Count > 0).Select(BillsOf).ToList();
            var bench = FindWorkbench(a, map);
            var stack = ((IBillGiver)bench).BillStack;
            switch (action)
            {
                case "list": return BillsOf(bench);
                case "add":
                {
                    var rname = Str(a, "recipe");
                    var recipe = bench.def.AllRecipes.FirstOrDefault(rc => string.Equals(rc.defName, rname, StringComparison.OrdinalIgnoreCase) || string.Equals(rc.label, rname, StringComparison.OrdinalIgnoreCase))
                                 ?? throw new ToolError($"{bench.def.defName} has no recipe '{rname}'. Recipes: " + string.Join(", ", bench.def.AllRecipes.Select(rc => rc.defName)));
                    if (!recipe.AvailableNow || !recipe.AvailableOnNow(bench)) throw new ToolError(recipe.defName + " is not available yet (research?)");
                    if (stack.Count >= 15) throw new ToolError("bench already has 15 bills");
                    var bill = recipe.MakeNewBill();
                    if (bill is Bill_Production bp)
                    {
                        if (a.ContainsKey("target"))
                        {
                            bp.repeatMode = BillRepeatModeDefOf.TargetCount;
                            if (!recipe.WorkerCounter.CanCountProducts(bp)) throw new ToolError(recipe.defName + " cannot use 'until you have X'; use count or forever");
                            bp.targetCount = Int(a, "target");
                        }
                        else if (a.ContainsKey("count")) { bp.repeatMode = BillRepeatModeDefOf.RepeatCount; bp.repeatCount = Int(a, "count"); }
                        else if (Bool(a, "forever", false)) bp.repeatMode = BillRepeatModeDefOf.Forever;
                    }
                    if (a.ContainsKey("stuff")) RestrictToStuff(bill, Str(a, "stuff"));
                    stack.AddBill(bill);
                    return BillsOf(bench);
                }
                case "remove": case "suspend": case "resume": case "set":
                {
                    int i = Int(a, "index");
                    if (i < 0 || i >= stack.Count) throw new ToolError($"index out of range (0..{stack.Count - 1})");
                    var bill = stack.Bills[i];
                    if (action == "remove") stack.Delete(bill);
                    else if (action == "suspend") bill.suspended = true;
                    else if (action == "resume") bill.suspended = false;
                    else if (bill is Bill_Production bp)
                    {
                        if (a.ContainsKey("target")) { bp.repeatMode = BillRepeatModeDefOf.TargetCount; bp.targetCount = Int(a, "target"); }
                        if (a.ContainsKey("count")) { bp.repeatMode = BillRepeatModeDefOf.RepeatCount; bp.repeatCount = Int(a, "count"); }
                        if (Bool(a, "forever", false)) bp.repeatMode = BillRepeatModeDefOf.Forever;
                    }
                    if (a.ContainsKey("stuff")) RestrictToStuff(bill, Str(a, "stuff"));
                    return BillsOf(bench);
                }
                default: throw new ToolError("action: list, add, remove, suspend, resume, set");
            }
        }

        // ------------------------------------------------------------------ forbid

        static object Forbid(Dictionary<string, object> a)
        {
            var map = CurMap();
            bool forbid = Bool(a, "forbidden", true);
            var id = Str(a, "id", false);
            var things = new List<Thing>();
            if (id != null) things.AddRange(map.listerThings.AllThings.Where(t => t.ThingID == id));
            else
            {
                var rect = Rect(a, map, 1000000);
                var q = Str(a, "query", false);
                foreach (var c in rect)
                    if (!c.Fogged(map))
                        things.AddRange(c.GetThingList(map).Where(t => t.def.HasComp(typeof(CompForbiddable)) &&
                            (q == null || t.def.defName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || t.LabelNoCount.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)));
            }
            int n = 0;
            foreach (var t in things.Distinct())
            {
                var comp = t.TryGetComp<CompForbiddable>();
                if (comp == null || comp.Forbidden == forbid) continue;
                t.SetForbidden(forbid, warnOnFail: false);
                n++;
            }
            return $"{(forbid ? "forbade" : "allowed")} {n} things";
        }

        // ------------------------------------------------------------------ restrictions

        static List<Pawn> PawnsArg(Dictionary<string, object> a)
        {
            var names = StrList(a, "names") ?? (a.ContainsKey("name") ? new List<string> { Str(a, "name") } : null);
            if (names == null || names.Count == 0 || names.Count == 1 && names[0] == "all") return AllColonists().ToList();
            return names.Select(FindColonist).ToList();
        }

        static object AllowArea(Dictionary<string, object> a)
        {
            var map = CurMap();
            var label = Str(a, "area", false);
            Area area = null;
            if (label != null && label != "none" && label != "unrestricted")
            {
                area = FindArea(map, label);
                if (!area.AssignableAsAllowed()) throw new ToolError(area.Label + " cannot be used as an allowed area");
            }
            // names may also be colony animals (a pet or work animal kept out of the kitchen)
            var names = StrList(a, "names") ?? (a.ContainsKey("name") ? new List<string> { Str(a, "name") } : null);
            var pawns = names == null || names.Count == 0 || names.Count == 1 && names[0] == "all"
                ? AllColonists().ToList()
                : names.Select(n => map.mapPawns.SpawnedColonyAnimals.FirstOrDefault(an =>
                      string.Equals(an.LabelShort, n, StringComparison.OrdinalIgnoreCase)) ?? FindColonist(n)).ToList();
            var res = new List<object>();
            foreach (var p in pawns)
            {
                if (p.playerSettings == null) { res.Add(p.LabelShort + ": no settings"); continue; }
                p.playerSettings.AreaRestrictionInPawnCurrentMap = area;
                res.Add($"{p.LabelShort}: {(area == null ? "unrestricted" : area.Label)}");
            }
            return res;
        }

        static TimeAssignmentDef Assignment(char ch)
        {
            switch (char.ToUpperInvariant(ch))
            {
                case 'S': return TimeAssignmentDefOf.Sleep;
                case 'W': return TimeAssignmentDefOf.Work;
                case 'J': case 'R': return TimeAssignmentDefOf.Joy;
                case 'A': return TimeAssignmentDefOf.Anything;
                case 'M': return TimeAssignmentDefOf.Meditate ?? throw new ToolError("meditate needs Royalty");
                default: throw new ToolError($"schedule char '{ch}': S=sleep W=work J=joy(recreation) A=anything M=meditate");
            }
        }

        static string ScheduleString(Pawn p)
        {
            var sb = new StringBuilder();
            for (int h = 0; h < 24; h++)
            {
                var t = p.timetable.GetAssignment(h);
                sb.Append(t == TimeAssignmentDefOf.Sleep ? 'S' : t == TimeAssignmentDefOf.Work ? 'W' : t == TimeAssignmentDefOf.Joy ? 'J' : t == TimeAssignmentDefOf.Meditate ? 'M' : 'A');
            }
            return sb.ToString();
        }

        // schedule: 24 chars, hour 0 first, e.g. "SSSSSSAWWWWWWWWWWWWJJSSS"
        static object Schedule(Dictionary<string, object> a)
        {
            RequireGame();
            var s = Str(a, "schedule", false);
            if (s != null && s.Length != 24) throw new ToolError("schedule must be 24 chars (hour 0..23): S=sleep W=work J=joy A=anything M=meditate");
            var res = Obj();
            foreach (var p in PawnsArg(a))
            {
                if (p.timetable == null) continue;
                if (s != null) for (int h = 0; h < 24; h++) p.timetable.SetAssignment(h, Assignment(s[h]));
                res[p.LabelShort] = ScheduleString(p);
            }
            return res;
        }

        static object PawnSettings(Dictionary<string, object> a)
        {
            RequireGame();
            var res = Obj();
            foreach (var p in PawnsArg(a))
            {
                var ps = p.playerSettings;
                if (ps == null) continue;
                var h = Str(a, "hostility", false);
                if (h != null)
                {
                    if (!Enum.TryParse<HostilityResponseMode>(h, true, out var m)) throw new ToolError("hostility: Ignore, Attack, Flee");
                    ps.hostilityResponse = m;
                }
                var med = Str(a, "medicine", false);
                if (med != null)
                {
                    if (!Enum.TryParse<MedicalCareCategory>(med, true, out var mc)) throw new ToolError("medicine: NoCare, NoMeds, HerbalOrWorse, NormalOrWorse, Best");
                    ps.medCare = mc;
                }
                if (a.ContainsKey("selfTend")) ps.selfTend = Bool(a, "selfTend", false);
                var outfit = Str(a, "outfit", false);
                if (outfit != null && p.outfits != null)
                {
                    var all = Current.Game.outfitDatabase.AllOutfits;
                    p.outfits.CurrentApparelPolicy = all.FirstOrDefault(o => string.Equals(o.label, outfit, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ToolError("outfit: " + string.Join(", ", all.Select(o => o.label)));
                }
                res[p.LabelShort] = new Dictionary<string, object>
                {
                    ["hostility"] = ps.hostilityResponse.ToString(), ["medicine"] = ps.medCare.ToString(), ["selfTend"] = ps.selfTend,
                    ["outfit"] = p.outfits?.CurrentApparelPolicy?.label,
                    ["area"] = ps.AreaRestrictionInPawnCurrentMap?.Label ?? "unrestricted",
                    ["schedule"] = p.timetable != null ? ScheduleString(p) : null,
                };
            }
            return res;
        }

        // ------------------------------------------------------------------ orders

        static object DraftedMove(Dictionary<string, object> a)
        {
            var map = CurMap();
            var pawns = PawnsArg(a);
            var target = Cell(a, map);
            var res = new List<object>();
            // spread several pawns over nearby standable cells, like the multi-pawn goto
            var used = new HashSet<IntVec3>();
            foreach (var p in pawns)
            {
                if (p.Map != map) { res.Add(p.LabelShort + ": on another map"); continue; }
                if (p.Downed) { res.Add(p.LabelShort + ": downed"); continue; }
                if (!p.Drafted)
                {
                    if (p.drafter == null) { res.Add(p.LabelShort + ": cannot be drafted"); continue; }
                    p.drafter.Drafted = true;
                }
                var dest = RCellFinder.BestOrderedGotoDestNear(target, p, c => !used.Contains(c));
                if (!dest.IsValid) { res.Add(p.LabelShort + ": no standable cell near target"); continue; }
                var rep = FloatMenuOptionProvider_DraftedMove.PawnCanGoto(p, dest);
                if (!rep.Accepted) { res.Add(p.LabelShort + ": " + Clean(rep.Reason)); continue; }
                used.Add(dest);
                Governor.NotePlayerOrder(p);
                FloatMenuOptionProvider_DraftedMove.PawnGotoAction(dest, p, dest);
                res.Add($"{p.LabelShort}: moving to ({dest.x},{dest.z})");
            }
            return res;
        }

        // The right-click menu: list what a colonist can do at a cell, or pick an option.
        static object Orders(Dictionary<string, object> a)
        {
            var map = CurMap();
            var p = FindColonist(Str(a, "name"));
            if (p.Map != map) throw new ToolError(p.LabelShort + " is not on the current map");
            var c = Cell(a, map);
            var opts = FloatMenuMakerMap.GetOptions(new List<Pawn> { p }, c.ToVector3Shifted(), out _);
            var choose = Str(a, "choose", false);
            if (choose == null)
                return new Dictionary<string, object>
                {
                    ["pawn"] = p.LabelShort, ["cell"] = Pos(c),
                    ["options"] = opts.Select((o, i) => (object)(i + ": " + Clean(o.Label) + (o.Disabled ? " [disabled]" : ""))).ToList(),
                };
            FloatMenuOption pick = int.TryParse(choose, out var idx)
                ? (idx >= 0 && idx < opts.Count ? opts[idx] : null)
                : opts.FirstOrDefault(o => Clean(o.Label).IndexOf(choose, StringComparison.OrdinalIgnoreCase) >= 0);
            if (pick == null) throw new ToolError($"no option '{choose}'. Options: " + string.Join(" | ", opts.Select((o, i) => i + ": " + Clean(o.Label))));
            if (pick.Disabled) throw new ToolError("option disabled: " + Clean(pick.Label));
            Governor.NotePlayerOrder(p);
            pick.Chosen(true, null);
            return $"{p.LabelShort}: {Clean(pick.Label)}";
        }

        // ------------------------------------------------------------------ rooms and power

        static Dictionary<string, object> RoomInfo(Room room)
        {
            var cr = CellRect.FromCellList(room.Cells.ToList());
            return new Dictionary<string, object>
            {
                ["role"] = room.Role?.label, ["cells"] = room.CellCount, ["tempC"] = Math.Round(room.Temperature, 1),
                ["impressiveness"] = Math.Round(room.GetStat(RoomStatDefOf.Impressiveness), 1),
                ["beauty"] = Math.Round(room.GetStat(RoomStatDefOf.Beauty), 2),
                ["cleanliness"] = Math.Round(room.GetStat(RoomStatDefOf.Cleanliness), 2),
                ["space"] = Math.Round(room.GetStat(RoomStatDefOf.Space), 1),
                ["owners"] = room.Owners.Select(p => (object)p.LabelShort).ToList(),
                ["bounds"] = new Dictionary<string, object> { ["x"] = cr.minX, ["z"] = cr.minZ, ["x2"] = cr.maxX, ["z2"] = cr.maxZ },
            };
        }

        // x,z: the room at that cell; otherwise every enclosed room of the colony (home area).
        static object Rooms(Dictionary<string, object> a)
        {
            var map = CurMap();
            if (a.ContainsKey("x"))
            {
                var room = Cell(a, map).GetRoom(map) ?? throw new ToolError("no room there");
                return RoomInfo(room);
            }
            var seen = new HashSet<Room>();
            var res = new List<object>();
            foreach (var c in map.areaManager.Home.ActiveCells)
            {
                var room = c.GetRoom(map);
                if (room == null || room.PsychologicallyOutdoors || room.IsHuge || room.CellCount <= 1 || !seen.Add(room)) continue;
                res.Add(RoomInfo(room));
            }
            return res;
        }

        // heater/cooler at x,z: set target temperature (C)
        static object Thermostat(Dictionary<string, object> a)
        {
            var map = CurMap();
            var c = Cell(a, map);
            var comp = c.GetThingList(map).Select(t => t.TryGetComp<CompTempControl>()).FirstOrDefault(tc => tc != null)
                       ?? throw new ToolError("no heater/cooler/vent with a thermostat at that cell");
            if (a.TryGetValue("target", out var tv) && tv is double t)
                comp.TargetTemperature = Mathf.Clamp((float)t, -50f, 50f);
            return new Dictionary<string, object> { ["thing"] = comp.parent.LabelCap.ToString(), ["targetC"] = comp.TargetTemperature };
        }

        static string OffReason(CompPowerTrader c)
        {
            var flick = c.parent.TryGetComp<CompFlickable>();
            if (flick != null && !flick.SwitchIsOn) return "switched off";
            var br = c.parent.TryGetComp<CompBreakdownable>();
            if (br != null && br.BrokenDown) return "broken down (repair needs 1 component)";
            var fuel = c.parent.TryGetComp<CompRefuelable>();
            if (fuel != null && !fuel.HasFuel) return "out of fuel";
            return "no power";
        }

        // x,z: turn a switchable building on/off. Like the gizmo: a colonist walks over and flicks it.
        static object Switch(Dictionary<string, object> a)
        {
            var map = CurMap();
            var c = Cell(a, map);
            var flick = c.GetThingList(map).Select(t => t.TryGetComp<CompFlickable>()).FirstOrDefault(f => f != null)
                        ?? throw new ToolError("nothing switchable at that cell");
            bool on = Bool(a, "on", true);
            typeof(CompFlickable).GetField("wantSwitchOn", Inst).SetValue(flick, on);
            FlickUtility.UpdateFlickDesignation(flick.parent);
            return new Dictionary<string, object>
            {
                ["thing"] = flick.parent.LabelCap.ToString(), ["switchIsOn"] = flick.SwitchIsOn, ["wantOn"] = on,
                ["note"] = flick.SwitchIsOn == on ? "already in that state" : "a colonist will go and flick it",
            };
        }

        static object Power(Dictionary<string, object> a)
        {
            var map = CurMap();
            return map.powerNetManager.AllNetsListForReading.Where(n => n.powerComps.Count > 0 || n.batteryComps.Count > 0).Select(n => (object)new Dictionary<string, object>
            {
                ["gainWatts"] = Math.Round(n.CurrentEnergyGainRate() / CompPower.WattsToWattDaysPerTick),
                ["storedWd"] = Math.Round(n.CurrentStoredEnergy()),
                ["batteries"] = n.batteryComps.Count,
                ["producers"] = n.powerComps.Where(c => c.PowerOutput > 0).Select(c => (object)$"{c.parent.LabelCap} {Math.Round(c.PowerOutput)}W").ToList(),
                ["consumers"] = n.powerComps.Where(c => c.PowerOutput < 0 || !c.PowerOn && c.Props.PowerConsumption > 0).GroupBy(c => c.parent.def.label).Select(g => (object)$"{g.Count()}x {g.Key} {Math.Round(g.Sum(c => -c.PowerOutput))}W").ToList(),
                ["off"] = n.powerComps.Where(c => !c.PowerOn).Select(c => (object)$"{c.parent.LabelCap} at ({c.parent.Position.x},{c.parent.Position.z}): {OffReason(c)}").ToList(),
                // a generator out of fuel or broken stays PowerOn but produces 0 W: list it, or an outage looks like a missing producer
                ["idleProducers"] = n.powerComps.Where(c => c is CompPowerPlant && c.PowerOn && c.PowerOutput <= 0)
                    .Select(c => (object)$"{c.parent.LabelCap} at ({c.parent.Position.x},{c.parent.Position.z}): {OffReason(c)}").ToList(),
            }).ToList();
        }

        // ------------------------------------------------------------------ prisoners

        static object Prisoners(Dictionary<string, object> a)
        {
            var map = CurMap();
            var all = map.mapPawns.PrisonersOfColonySpawned.ToList();
            var name = Str(a, "name", false);
            var mode = Str(a, "mode", false);
            if (name != null && mode != null)
            {
                var p = all.FirstOrDefault(x => x.LabelShort.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ToolError("no prisoner " + name + ". Prisoners: " + string.Join(", ", all.Select(x => x.LabelShort)));
                var def = DefDatabase<PrisonerInteractionModeDef>.AllDefs.FirstOrDefault(d =>
                              d.defName.Equals(mode, StringComparison.OrdinalIgnoreCase) || (d.label ?? "").Equals(mode, StringComparison.OrdinalIgnoreCase))
                          ?? throw new ToolError("modes: " + string.Join(", ", DefDatabase<PrisonerInteractionModeDef>.AllDefs.Select(d => d.defName)));
                if (def == PrisonerInteractionModeDefOf.Execution) throw new ToolError("execution is not done through this tool");
                if (!def.allowOnWildMan && p.IsWildMan()) throw new ToolError(def.label + " is not allowed on wild people");
                p.guest.SetExclusiveInteraction(def);
            }
            return all.Select(p => (object)new Dictionary<string, object>
            {
                ["name"] = p.LabelShort, ["faction"] = p.Faction?.Name, ["pos"] = Pos(p.Position),
                ["mode"] = p.guest.ExclusiveInteractionMode?.defName,
                ["resistance"] = Math.Round(p.guest.resistance, 1), ["will"] = Math.Round(p.guest.will, 1),
                ["recruitable"] = p.guest.Recruitable,
                ["healthPct"] = Pct(p.health.summaryHealth.SummaryHealthPercent),
                ["traits"] = p.story?.traits?.allTraits.Select(t => (object)t.LabelCap).ToList(),
                ["skills"] = p.skills?.skills.Where(sk => !sk.TotallyDisabled && sk.Level >= 5).Select(sk => (object)$"{sk.def.label} {sk.Level}").ToList(),
            }).ToList();
        }

        // ------------------------------------------------------------------ beds

        // type: colonist | prisoner | slave (applies to every bed in the room, like the bed gizmo),
        // medical: true/false, owner: colonist name (or "none").
        static object Bed(Dictionary<string, object> a)
        {
            var map = CurMap();
            var beds = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().ToList();
            Building_Bed bed;
            var id = Str(a, "id", false);
            if (id != null) bed = beds.FirstOrDefault(b => b.ThingID == id);
            else { var c = Cell(a, map); bed = beds.FirstOrDefault(b => b.OccupiedRect().Contains(c)); }
            if (bed == null)
                throw new ToolError("no finished bed there. Beds: " + string.Join(", ", beds.Select(b => $"{b.ThingID} at ({b.Position.x},{b.Position.z}) {b.ForOwnerType}")));
            var type = Str(a, "type", false);
            if (type != null)
            {
                if (!Enum.TryParse<BedOwnerType>(type, true, out var ot)) throw new ToolError("type: colonist, prisoner, slave");
                var room = bed.GetRoom();
                var affected = room != null && !room.PsychologicallyOutdoors ? room.ContainedBeds.ToList() : new List<Building_Bed> { bed };
                foreach (var b in affected) b.ForOwnerType = ot;
                // as the bed gizmo does: only a room-shape refresh recomputes "prison cell"
                if (room != null && !room.TouchesMapEdge)
                {
                    bed.GetDistrict()?.Notify_RoomShapeOrContainedBedsChanged();
                    room.Notify_RoomShapeChanged();
                }
            }
            if (a.ContainsKey("medical")) bed.Medical = Bool(a, "medical", false);
            var owner = Str(a, "owner", false);
            var comp = bed.CompAssignableToPawn;
            if (owner != null && comp != null)
            {
                if (owner == "none") foreach (var p in comp.AssignedPawnsForReading.ToList()) comp.TryUnassignPawn(p);
                else
                {
                    var p = FindColonist(owner);
                    var rep = comp.CanAssignTo(p);
                    if (!rep.Accepted) throw new ToolError($"cannot assign {p.LabelShort}: {Clean(rep.Reason)}");
                    comp.TryAssignPawn(p);
                }
            }
            return new Dictionary<string, object>
            {
                ["bed"] = bed.ThingID, ["pos"] = Pos(bed.Position), ["type"] = bed.ForOwnerType.ToString(), ["medical"] = bed.Medical,
                ["owners"] = comp?.AssignedPawnsForReading.Select(p => (object)p.LabelShort).ToList(),
                ["room"] = bed.GetRoom()?.Role?.label,
            };
        }

        // ------------------------------------------------------------------ time and saving

        public static int pauseAtTick = -1;
        public static int lastLetterId = -1;
        public static string pauseReason;

        // Unpause for N in-game hours, then pause again (checked every frame by the pump).
        static object RunFor(Dictionary<string, object> a)
        {
            RequireGame();
            var hours = a.TryGetValue("hours", out var hv) && hv is double hd ? hd : 1.0;
            if (hours <= 0 || hours > 72) throw new ToolError("hours: 0 < hours <= 72");
            var speed = (Str(a, "speed", false) ?? "fast").ToLowerInvariant();
            if (Find.WindowStack.WindowsForcePause)
                throw new ToolError("a dialog is open and pauses the game; answer it first (dialogs tool)");
            pauseAtTick = Find.TickManager.TicksGame + (int)(hours * GenDate.TicksPerHour);
            lastLetterId = Find.LetterStack.LettersListForReading.Count > 0 ? Find.LetterStack.LettersListForReading.Max(l => l.ID) : lastLetterId;
            pauseReason = null;
            SetSpeed(new Dictionary<string, object> { ["speed"] = speed });
            return new Dictionary<string, object> { ["until"] = pauseAtTick, ["speed"] = Find.TickManager.CurTimeSpeed.ToString() };
        }

        // Called every frame on the main thread.
        public static void Tick()
        {
            if (pauseAtTick < 0 || Current.ProgramState != ProgramState.Playing || Find.TickManager == null) return;
            var tm = Find.TickManager;
            string reason = null;
            if (tm.TicksGame >= pauseAtTick) reason = "time up";
            else
            {
                foreach (var l in Find.LetterStack.LettersListForReading)
                {
                    if (l.ID <= lastLetterId) continue;
                    lastLetterId = l.ID;
                    if (l.def == LetterDefOf.ThreatBig || l.def == LetterDefOf.ThreatSmall || l.def == LetterDefOf.Death ||
                        l.def == LetterDefOf.NegativeEvent || (l is ChoiceLetter && !(l is StandardLetter) && !(l is NewQuestLetter)))
                    { reason = "letter: " + Clean(l.Label); break; }
                }
                if (reason == null && Find.WindowStack.WindowsForcePause) reason = "dialog opened";
            }
            if (reason == null) return;
            tm.CurTimeSpeed = TimeSpeed.Paused;
            pauseAtTick = -1;
            pauseReason = reason;
        }

        static object Save(Dictionary<string, object> a)
        {
            RequireGame();
            var name = Str(a, "name", false) ?? (Faction.OfPlayer.Name + "-claude");
            name = GenFile.SanitizedFileName(name);
            LongEventHandler.QueueLongEvent(() => GameDataSaveLoader.SaveGame(name), "SavingLongEvent", false, null);
            return "saving as " + name;
        }
    }
}
