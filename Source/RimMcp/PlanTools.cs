using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;

namespace RimMcp
{
    // Planning without building. Two layers:
    //  - planned blueprints: real blueprints kept forbidden, so nobody hauls to or builds them until
    //    approved (what the Plan On The Fly mod does to every new blueprint while its toggle is on);
    //  - plan designations: coloured marks that never become construction (vanilla 1.6 plans, or
    //    Planning Extended's typed plans when that mod is active).
    // Both optional mods are reached by reflection so RimMcp loads without them.
    public static partial class Tools
    {
        // ------------------------------------------------------------------ Plan On The Fly

        static FieldInfo potfField;
        static object potfInstance;
        static bool potfLooked;

        static bool PotfReady()
        {
            if (!potfLooked)
            {
                potfLooked = true;
                var t = GenTypes.GetTypeInAnyAssembly("Sov.PlanOnTheFly.PlanOnTheFlyToggle");
                potfField = t?.GetField("PlanningMode", BindingFlags.Public | BindingFlags.Instance);
                potfInstance = t?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            }
            return potfField != null && potfInstance != null;
        }

        // null when Plan On The Fly is not loaded
        static bool? PlanOnTheFlyMode() => PotfReady() ? (bool?)(bool)potfField.GetValue(potfInstance) : null;

        static void SetPlanned(Map map, IntVec3 c, BuildableDef def, bool forbid)
        {
            foreach (var t in c.GetThingList(map).ToList())
                if (t is Blueprint_Build bp && bp.def.entityDefToBuild == def)
                    bp.SetForbidden(forbid, warnOnFail: false);
        }

        static List<Blueprint> PlayerBlueprints(Map map, CellRect? rect) =>
            map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).OfType<Blueprint>()
                .Where(b => b.Faction == Faction.OfPlayer && (rect == null || rect.Value.Contains(b.Position))).ToList();

        static object PlannedBlueprints(Map map, CellRect? rect)
        {
            var planned = PlayerBlueprints(map, rect).Where(b => b.IsForbidden(Faction.OfPlayer)).ToList();
            return planned.GroupBy(b => b.def.entityDefToBuild.defName + (b.EntityToBuildStuff() is ThingDef s ? "/" + s.defName : ""))
                .OrderByDescending(g => g.Count())
                .Select(g =>
                {
                    var bounds = CellRect.FromCellList(g.Select(b => b.Position));
                    var o = new Dictionary<string, object>
                    {
                        ["def"] = g.Key, ["count"] = g.Count(),
                        ["bounds"] = new Dictionary<string, object> { ["x"] = bounds.minX, ["z"] = bounds.minZ, ["x2"] = bounds.maxX, ["z2"] = bounds.maxZ },
                    };
                    if (g.Count() <= 4) o["cells"] = g.Select(b => (object)Pos(b.Position)).ToList();
                    return (object)o;
                }).ToList();
        }

        static int ForbidBlueprints(Map map, CellRect? rect, string q, bool forbid)
        {
            int n = 0;
            foreach (var b in PlayerBlueprints(map, rect))
            {
                if (q != null && b.def.entityDefToBuild.defName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (b.def.entityDefToBuild.label ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (b.IsForbidden(Faction.OfPlayer) == forbid) continue;
                b.SetForbidden(forbid, warnOnFail: false);
                n++;
            }
            return n;
        }

        // ------------------------------------------------------------------ plan designations

        // Planning Extended keeps one PlanDesignation per cell: 4 types, each with a plain (white) and a Colored def
        static readonly string[] PeTypes = { "wall", "door", "object", "floor" };
        static readonly string[] PeDefNames = { "PlanWalls", "PlanDoors", "PlanObjects", "PlanFloors" };
        const string PeWhite = "Structure_White";

        static MethodInfo peDesignate;
        static bool peLooked;

        static bool PeActive() => DefDatabase<DesignationDef>.GetNamedSilentFail("PlanWalls") != null;

        static MethodInfo PeDesignate()
        {
            if (!peLooked)
            {
                peLooked = true;
                // internal class: look it up in the loaded assemblies by full name
                var t = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(asm => asm.GetType("PlanningExtended.Designations.PlanDesignationPlacerUtilities", false))
                    .FirstOrDefault(x => x != null);
                peDesignate = t?.GetMethod("Designate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
                    new[] { typeof(Map), typeof(IntVec3), typeof(DesignationDef), typeof(ColorDef) }, null);
            }
            return peDesignate ?? throw new ToolError("Planning Extended is loaded but its Designate method was not found (mod updated?)");
        }

        static IEnumerable<DesignationDef> PeDefs(string type)
        {
            for (int i = 0; i < PeTypes.Length; i++)
                if (type == null || PeTypes[i] == type)
                {
                    yield return DefDatabase<DesignationDef>.GetNamed(PeDefNames[i]);
                    yield return DefDatabase<DesignationDef>.GetNamed(PeDefNames[i] + "Colored");
                }
        }

        static string PeType(DesignationDef d) => PeTypes[Array.FindIndex(PeDefNames, n => d.defName.StartsWith(n))];

        static List<Designation> PeDesignations(Map map, CellRect? rect, string type) =>
            PeDefs(type).SelectMany(d => map.designationManager.designationsByDef[d])
                .Where(d => rect == null || rect.Value.Contains(d.target.Cell)).ToList();

        // Planning Extended draws with the Structure palette (Structure_Blue...), vanilla plans with PlanGray..PlanPink
        static ColorDef FindColor(string name, ColorType type, string prefix, string def)
        {
            var all = DefDatabase<ColorDef>.AllDefsListForReading.Where(c => c.colorType == type).ToList();
            if (name == null) return all.FirstOrDefault(c => c.defName == def) ?? all.First();
            var hit = all.FirstOrDefault(c => string.Equals(c.defName, name, StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(c.defName, prefix + name, StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(c.label, name, StringComparison.OrdinalIgnoreCase));
            if (hit == null)
                throw new ToolError($"no {type} color '{name}'. Colors: " +
                                    string.Join(", ", all.OrderBy(c => c.displayOrder).Select(c => c.defName.StartsWith(prefix) ? c.defName.Substring(prefix.Length) : c.defName)));
            return hit;
        }

        class PlanAddDesignator : Designator_Plan_Add
        {
            public PlanAddDesignator(ColorDef c) { colorDef = c; }
        }

        static object DrawPlan(Dictionary<string, object> a, Map map, CellRect rect)
        {
            var type = (Str(a, "type", false) ?? "area").ToLowerInvariant().TrimEnd('s');
            var cells = (Bool(a, "outline", false) ? rect.EdgeCells : rect.Cells)
                .Where(c => c.InBounds(map) && !c.InNoBuildEdgeArea(map)).ToList();
            if (cells.Count == 0) throw new ToolError("every cell is outside the map or in the no-build edge");
            if (type == "area")
            {
                // vanilla 1.6 plan: one named, coloured area; touching plans of the same colour merge into it
                var color = FindColor(Str(a, "color", false), ColorType.Planning, "Plan", "PlanGray");
                var des = new PlanAddDesignator(color);
                Find.Selector.ClearSelection();
                des.DesignateMultiCell(cells);
                var plan = Find.Selector.SelectedPlan ?? map.planManager.PlanAt(cells[0]);
                Find.Selector.ClearSelection();
                var name = Str(a, "name", false);
                if (plan != null && name != null) plan.RenamableLabel = name;
                return new Dictionary<string, object>
                {
                    ["kind"] = "vanilla plan", ["plan"] = plan?.RenamableLabel, ["color"] = color.defName,
                    ["cells"] = cells.Count, ["planCells"] = plan?.CellCount ?? 0,
                };
            }
            if (Array.IndexOf(PeTypes, type) < 0) throw new ToolError("type: wall, door, object, floor (Planning Extended) or area (vanilla plan)");
            if (!PeActive()) throw new ToolError("typed plans need the Planning Extended mod; use type=area for a vanilla plan");
            var col = FindColor(Str(a, "color", false), ColorType.Structure, "Structure_", PeWhite);
            var defName = PeDefNames[Array.IndexOf(PeTypes, type)] + (col.defName == PeWhite ? "" : "Colored");
            var ddef = DefDatabase<DesignationDef>.GetNamed(defName);
            var m = PeDesignate();
            foreach (var c in cells) m.Invoke(null, new object[] { map, c, ddef, col });   // replaces any plan already on the cell
            return new Dictionary<string, object> { ["kind"] = "Planning Extended", ["type"] = type, ["color"] = col.defName, ["cells"] = cells.Count };
        }

        static object ErasePlan(Dictionary<string, object> a, Map map, CellRect rect)
        {
            var type = Str(a, "type", false)?.ToLowerInvariant().TrimEnd('s');
            var r = new Dictionary<string, object>();
            if (type == null || type == "area")
            {
                var des = new Designator_Plan_Remove();
                var cells = rect.Cells.Where(c => des.CanDesignateCell(c).Accepted).ToList();
                if (cells.Count > 0) des.DesignateMultiCell(cells);
                r["vanillaCells"] = cells.Count;
            }
            if (type != "area" && PeActive())
            {
                if (type != null && Array.IndexOf(PeTypes, type) < 0) throw new ToolError("type: wall, door, object, floor, area");
                var ds = PeDesignations(map, rect, type);
                foreach (var d in ds) map.designationManager.RemoveDesignation(d);
                r["typedCells"] = ds.Count;
            }
            return r;
        }

        static Dictionary<string, object> Bounds(IEnumerable<IntVec3> cells)
        {
            var b = CellRect.FromCellList(cells);
            return new Dictionary<string, object> { ["x"] = b.minX, ["z"] = b.minZ, ["x2"] = b.maxX, ["z2"] = b.maxZ };
        }

        static Dictionary<string, object> PlanDesignations(Map map, CellRect? rect)
        {
            var r = new Dictionary<string, object>();
            r["vanillaPlans"] = map.planManager.AllPlans
                .Select(p => (p, cells: p.Cells.Where(c => rect == null || rect.Value.Contains(c)).ToList()))
                .Where(x => x.cells.Count > 0)
                .Select(x => (object)new Dictionary<string, object>
                {
                    ["name"] = x.p.RenamableLabel, ["color"] = x.p.Color?.defName, ["cells"] = x.cells.Count, ["bounds"] = Bounds(x.cells),
                    ["hidden"] = x.p.Hidden,
                }).ToList();
            if (PeActive())
                r["typedPlans"] = PeDesignations(map, rect, null)
                    .GroupBy(d => (type: PeType(d.def), color: d.colorDef?.defName ?? PeWhite))
                    .OrderBy(g => Array.IndexOf(PeTypes, g.Key.type))
                    .Select(g => (object)new Dictionary<string, object>
                    {
                        ["type"] = g.Key.type, ["color"] = g.Key.color, ["cells"] = g.Count(), ["bounds"] = Bounds(g.Select(d => d.target.Cell)),
                    }).ToList();
            else r["typedPlans"] = "Planning Extended not loaded";
            return r;
        }

        // map_view layer: W/D/O/F typed plans (Planning Extended), P vanilla plan
        static char PlanChar(Map map, IntVec3 c)
        {
            if (c.Fogged(map)) return '?';
            foreach (var d in map.designationManager.AllDesignationsAt(c))
            {
                int i = Array.FindIndex(PeDefNames, n => d.def.defName.StartsWith(n));
                if (i >= 0) return "WDOF"[i];
            }
            return map.planManager.PlanAt(c) != null ? 'P' : '.';
        }

        const string PlanLegend = "W=planned wall D=planned door O=planned object F=planned floor (Planning Extended) P=vanilla plan .=no plan";

        // ------------------------------------------------------------------ plan tool

        static object PlanTool(Dictionary<string, object> a)
        {
            var map = CurMap();
            var action = (Str(a, "action", false) ?? "list").ToLowerInvariant();
            CellRect? rect = a.ContainsKey("x") ? Rect(a, map, 1000000) : (CellRect?)null;
            switch (action)
            {
                case "list":
                {
                    var r = PlanDesignations(map, rect);
                    r["plannedBlueprints"] = PlannedBlueprints(map, rect);
                    r["planOnTheFly"] = PlanOnTheFlyMode() is bool m ? (object)(m ? "on" : "off") : "not loaded";
                    return r;
                }
                case "draw":
                    if (rect == null) throw new ToolError("draw needs a rectangle (x,z[,x2,z2 | w,h])");
                    return DrawPlan(a, map, rect.Value);
                case "erase":
                    if (rect == null) throw new ToolError("erase needs a rectangle (x,z[,x2,z2 | w,h])");
                    return ErasePlan(a, map, rect.Value);
                case "mode":
                {
                    if (!PotfReady()) throw new ToolError("Plan On The Fly is not loaded; use build planned=true instead");
                    if (a.ContainsKey("on")) potfField.SetValue(potfInstance, Bool(a, "on", true));
                    var on = PlanOnTheFlyMode() == true;
                    return new Dictionary<string, object>
                    {
                        ["planOnTheFly"] = on ? "on" : "off",
                        ["note"] = on ? "every new blueprint (yours or the player's) is placed forbidden until approved"
                                      : "new blueprints get built as usual",
                    };
                }
                case "approve":
                case "shelve":
                {
                    bool forbid = action == "shelve";
                    if (rect == null && !Bool(a, "all", false)) throw new ToolError("pass a rectangle (x,z[,x2,z2]) or all=true");
                    int n = ForbidBlueprints(map, rect, Str(a, "query", false), forbid);
                    return forbid ? $"shelved {n} blueprints (forbidden, nobody builds them)"
                                  : $"approved {n} blueprints: colonists can now haul to and build them";
                }
                default:
                    throw new ToolError("action: list | draw (type, color, rect, outline, name) | erase (rect, type) | " +
                                        "mode (on) | approve / shelve (rect or all, query)");
            }
        }
    }
}
