using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimMcp
{
    // Drives the vanilla "New colony" pages (scenario, storyteller, world, site, ideoligion,
    // starting pawns) from code: the same page objects, the same CanDoNext/DoNext checks.
    public static partial class Tools
    {
        static readonly MethodInfo pageCanDoNext = typeof(Page).GetMethod("CanDoNext", Inst);
        static readonly MethodInfo pageDoNext = typeof(Page).GetMethod("DoNext", Inst);
        static readonly MethodInfo pageCanDoBack = typeof(Page).GetMethod("CanDoBack", Inst);
        static readonly MethodInfo pageDoBack = typeof(Page).GetMethod("DoBack", Inst);

        static void SetField(object o, string name, object value)
        {
            var f = o.GetType().GetField(name, Inst) ?? throw new ToolError($"{o.GetType().Name}.{name} not found (game version?)");
            f.SetValue(o, value);
        }

        static object GetField(object o, string name) => o.GetType().GetField(name, Inst)?.GetValue(o);

        static Page CurrentPage() => Find.WindowStack.Windows.OfType<Page>().LastOrDefault();

        static object NewGame(Dictionary<string, object> a)
        {
            var action = (Str(a, "action", false) ?? "state").ToLowerInvariant();
            if (LongEventHandler.AnyEventNowOrWaiting && action != "state")
                throw new ToolError("the game is busy (loading or generating); call state again in a moment");
            switch (action)
            {
                case "state": return PageState();
                case "devmode_off":
                    Prefs.DevMode = false;
                    Prefs.Save();
                    return "development mode is off";
                case "open":
                {
                    if (Current.ProgramState != ProgramState.Entry) throw new ToolError("only from the main menu");
                    if (CurrentPage() != null) throw new ToolError("a setup page is already open: " + CurrentPage().GetType().Name);
                    Find.WindowStack.Add(new Page_SelectScenario());
                    return PageState();
                }
                case "next":
                {
                    var page = CurrentPage() ?? throw new ToolError("no setup page open");
                    // a map-preview mod can clear the world selection after `set tile`: put it back
                    if (page is Page_SelectStartingSite && !Find.WorldInterface.SelectedTile.Valid && Find.GameInitData.startingTile.Valid)
                        Find.WorldInterface.SelectedTile = Find.GameInitData.startingTile;
                    if (!(bool)pageCanDoNext.Invoke(page, null))
                        throw new ToolError(page.GetType().Name + " refused to continue: " + string.Join(" | ", RecentMessages()));
                    pageDoNext.Invoke(page, null);
                    return PageState();
                }
                case "back":
                {
                    var page = CurrentPage() ?? throw new ToolError("no setup page open");
                    if (!(bool)pageCanDoBack.Invoke(page, null)) throw new ToolError("cannot go back");
                    pageDoBack.Invoke(page, null);
                    return PageState();
                }
                case "confirm": case "cancel":
                {
                    var mb = Find.WindowStack.Windows.OfType<Dialog_MessageBox>().LastOrDefault() ?? throw new ToolError("no confirmation box open");
                    if (action == "confirm") { mb.buttonAAction?.Invoke(); mb.Close(); }
                    else { mb.buttonBAction?.Invoke(); mb.Close(); }
                    return PageState();
                }
                case "set": return PageSet(a);
                case "saves":
                    return GenFilePaths.AllSavedGameFiles.OrderByDescending(f => f.LastWriteTime).Take(15)
                        .Select(f => (object)$"{System.IO.Path.GetFileNameWithoutExtension(f.Name)} ({f.LastWriteTime:yyyy-MM-dd HH:mm})").ToList();
                case "load":
                {
                    if (Current.ProgramState != ProgramState.Entry) throw new ToolError("load only from the main menu");
                    var name = Str(a, "name");
                    if (!SaveGameFilesUtility.SavedGameNamedExists(name)) throw new ToolError("no save named " + name);
                    GameDataSaveLoader.CheckVersionAndLoadGame(name);
                    // a save made with another mod list opens Dialog_ModMismatch and waits for a click;
                    // "Load anyway" is what restart/load means (anyway:false leaves the dialog open)
                    var mismatch = Find.WindowStack.Windows.OfType<Dialog_ModMismatch>().FirstOrDefault();
                    if (mismatch != null)
                    {
                        if (a.ContainsKey("anyway") && a["anyway"] is bool b && !b)
                            return "mod list differs from the save: Dialog_ModMismatch is open (action load_anyway to continue)";
                        mismatch.OnAcceptKeyPressed();
                        return "loading " + name + " (mod list differs from the save: loaded anyway)";
                    }
                    return "loading " + name;
                }
                case "load_anyway":
                {
                    var mismatch = Find.WindowStack.Windows.OfType<Dialog_ModMismatch>().FirstOrDefault()
                        ?? throw new ToolError("no mod mismatch dialog open");
                    mismatch.OnAcceptKeyPressed();
                    return "loading anyway";
                }
                case "tiles": return FindTiles(a);
                default: throw new ToolError("action: state, devmode_off, open, set, tiles, next, back, confirm, cancel");
            }
        }

        static List<string> RecentMessages() =>
            liveMessagesField?.GetValue(null) is List<Message> msgs ? msgs.Select(m => Clean(m.text)).Reverse().Take(3).ToList() : new List<string>();

        static object PageState()
        {
            var r = Obj();
            r["programState"] = Current.ProgramState.ToString();
            r["busy"] = LongEventHandler.AnyEventNowOrWaiting;
            r["devMode"] = Prefs.DevMode;
            r["windows"] = Find.WindowStack.Windows.Where(w => !(w is ImmediateWindow)).Select(w => (object)w.GetType().Name).ToList();
            var mb = Find.WindowStack.Windows.OfType<Dialog_MessageBox>().LastOrDefault();
            if (mb != null) r["confirmation"] = new Dictionary<string, object> { ["text"] = Trim(Clean(mb.text.Resolve()), 1500), ["A"] = Clean(mb.buttonAText), ["B"] = Clean(mb.buttonBText) };
            var page = CurrentPage();
            if (page == null) return r;
            r["page"] = page.GetType().Name;
            switch (page)
            {
                case Page_SelectScenario p:
                    r["selected"] = (GetField(p, "curScen") as Scenario)?.name;
                    r["scenarios"] = ScenarioLister.AllScenarios().Where(s => s.showInUI).Select(s => (object)s.name).ToList();
                    break;
                case Page_SelectStoryteller p:
                    r["storyteller"] = (GetField(p, "storyteller") as StorytellerDef)?.defName;
                    r["difficulty"] = (GetField(p, "difficulty") as DifficultyDef)?.defName;
                    r["permadeath"] = Find.GameInitData.permadeathChosen ? (object)Find.GameInitData.permadeath : "not chosen";
                    r["storytellers"] = DefDatabase<StorytellerDef>.AllDefs.Where(d => d.listVisible).Select(d => (object)$"{d.defName} ({d.label})").ToList();
                    r["difficulties"] = DefDatabase<DifficultyDef>.AllDefs.Where(d => !d.isCustom).Select(d => (object)$"{d.defName} ({d.label})").ToList();
                    break;
                case Page_CreateWorldParams p:
                    r["seed"] = GetField(p, "seedString");
                    r["planetCoverage"] = GetField(p, "planetCoverage");
                    r["rainfall"] = GetField(p, "rainfall")?.ToString();
                    r["temperature"] = GetField(p, "temperature")?.ToString();
                    r["population"] = GetField(p, "population")?.ToString();
                    break;
                case Page_SelectStartingSite _:
                    var t = Find.WorldInterface.SelectedTile;
                    r["selectedTile"] = t.Valid ? (object)TileInfo(t) : null;
                    break;
                case Page_ChooseIdeoPreset p:
                    r["selection"] = GetField(p, "presetSelection")?.ToString();
                    r["presets"] = DefDatabase<IdeoPresetDef>.AllDefs.Select(d => (object)$"{d.defName} ({d.label})").ToList();
                    break;
                case Page_ConfigureStartingPawns _:
                    var gi = Find.GameInitData;
                    r["startingPawnCount"] = gi.startingPawnCount;
                    r["note"] = $"the first {gi.startingPawnCount} pawns land; the rest stay behind (world pawns)";
                    r["pawns"] = gi.startingAndOptionalPawns.Select((p2, i) => (object)PawnSheet(p2, i)).ToList();
                    break;
            }
            return r;
        }

        static Dictionary<string, object> PawnSheet(Pawn p, int i)
        {
            var o = new Dictionary<string, object>
            {
                ["index"] = i, ["name"] = p.Name?.ToStringFull, ["gender"] = p.gender.ToString(), ["age"] = p.ageTracker.AgeBiologicalYears,
                ["xenotype"] = p.genes?.XenotypeLabel,
                ["childhood"] = p.story?.Childhood?.TitleCapFor(p.gender), ["adulthood"] = p.story?.Adulthood?.TitleCapFor(p.gender),
                ["traits"] = p.story?.traits?.allTraits.Select(t => (object)t.LabelCap).ToList(),
                ["skills"] = p.skills?.skills.Where(s => !s.TotallyDisabled).Select(s => (object)$"{s.def.label} {s.Level}{(s.passion == Passion.Major ? "!!" : s.passion == Passion.Minor ? "!" : "")}").ToList(),
                ["incapable"] = p.CombinedDisabledWorkTags == WorkTags.None ? null : p.CombinedDisabledWorkTags.ToString(),
            };
            var hed = p.health.hediffSet.hediffs.Where(h => h.Visible).Select(h => (object)Clean(h.LabelCap)).ToList();
            if (hed.Count > 0) o["health"] = hed;
            var rel = p.relations?.DirectRelations.Select(r => (object)$"{r.def.label}: {r.otherPawn.LabelShort}").ToList();
            if (rel != null && rel.Count > 0) o["relations"] = rel;
            return o;
        }

        static object PageSet(Dictionary<string, object> a)
        {
            var page = CurrentPage() ?? throw new ToolError("no setup page open");
            switch (page)
            {
                case Page_SelectScenario p:
                {
                    var n = Str(a, "scenario");
                    var scen = ScenarioLister.AllScenarios().FirstOrDefault(s => string.Equals(s.name, n, StringComparison.OrdinalIgnoreCase))
                               ?? throw new ToolError("no scenario '" + n + "'");
                    SetField(p, "curScen", scen);
                    break;
                }
                case Page_SelectStoryteller p:
                {
                    var st = Str(a, "storyteller", false);
                    if (st != null) SetField(p, "storyteller", DefDatabase<StorytellerDef>.GetNamedSilentFail(st) ?? throw new ToolError("no storyteller " + st));
                    var df = Str(a, "difficulty", false);
                    if (df != null)
                    {
                        var d = DefDatabase<DifficultyDef>.GetNamedSilentFail(df) ?? throw new ToolError("no difficulty " + df);
                        SetField(p, "difficulty", d);
                        SetField(p, "difficultyValues", new Difficulty(d));
                    }
                    if (a.ContainsKey("permadeath"))
                    {
                        Find.GameInitData.permadeathChosen = true;
                        Find.GameInitData.permadeath = Bool(a, "permadeath", false);
                    }
                    break;
                }
                case Page_CreateWorldParams p:
                {
                    var seed = Str(a, "seed", false);
                    if (seed != null) SetField(p, "seedString", seed);
                    if (a.TryGetValue("planetCoverage", out var cv) && cv is double c)
                    {
                        if (c != 0.3 && c != 0.5 && c != 1.0) throw new ToolError("planetCoverage: 0.3, 0.5 or 1");
                        SetField(p, "planetCoverage", (float)c);
                    }
                    break;
                }
                case Page_SelectStartingSite _:
                {
                    var id = Int(a, "tile");
                    var layer = Find.WorldGrid.Surface;
                    if (id < 0 || id >= layer.TilesCount) throw new ToolError("tile out of range");
                    var tile = new PlanetTile(id, layer);
                    var sb = new StringBuilder();
                    if (!TileFinder.IsValidTileForNewSettlement(tile, sb)) throw new ToolError("cannot settle there: " + sb);
                    Find.WorldInterface.SelectedTile = tile;
                    // the page copies SelectedTile into startingTile every frame; a map-preview mod can clear the
                    // selection before that happens ("Please select a site"): set what the page checks too
                    Find.GameInitData.startingTile = tile;
                    Find.WorldCameraDriver.JumpTo(tile);
                    break;
                }
                case Page_ChooseIdeoPreset p:
                {
                    var sel = Str(a, "ideo");
                    var f = p.GetType().GetField("presetSelection", Inst);
                    if (!string.Equals(sel, "classic", StringComparison.OrdinalIgnoreCase)) throw new ToolError("only ideo=classic is supported from here");
                    f.SetValue(p, Enum.Parse(f.FieldType, "Classic"));
                    break;
                }
                case Page_ConfigureStartingPawns _:
                {
                    var gi = Find.GameInitData;
                    if (a.ContainsKey("reroll"))
                    {
                        foreach (var s in StrList(a, "reroll"))
                        {
                            int i = int.Parse(s);
                            if (i < 0 || i >= gi.startingAndOptionalPawns.Count) throw new ToolError("pawn index out of range");
                            StartingPawnUtility.RandomizePawn(i);
                        }
                    }
                    if (a.ContainsKey("swap"))
                    {
                        // move a pawn into the landing party: swap positions i and j
                        var l = StrList(a, "swap").Select(int.Parse).ToList();
                        if (l.Count != 2) throw new ToolError("swap: [i, j]");
                        StartingPawnUtility.ReorderRequests(l[0], l[1]);
                        var list = gi.startingAndOptionalPawns;
                        var tmp = list[l[0]]; list.RemoveAt(l[0]); list.Insert(l[1], tmp);
                    }
                    if (a.ContainsKey("template"))
                    {
                        // Colony Template (nahuel.colonytemplate), soft dependency: same call as its
                        // "Load template" button on this page
                        var files = GenTypes.GetTypeInAnyAssembly("ColonyTemplate.TemplateFiles")
                            ?? throw new ToolError("Colony Template mod is not active");
                        var apply = GenTypes.GetTypeInAnyAssembly("ColonyTemplate.CrewApply");
                        object crew;
                        try { crew = files.GetMethod("Load").Invoke(null, new object[] { Str(a, "template") }); }
                        catch (TargetInvocationException e) { throw new ToolError("template load failed: " + e.InnerException?.Message); }
                        if (crew == null) throw new ToolError("no template named " + Str(a, "template"));
                        var problems = new StringBuilder();
                        apply.GetMethod("ApplyToStartingPawns").Invoke(null, new object[] { crew, problems });
                        if (problems.Length > 0) throw new ToolError("template applied with problems:\n" + problems);
                    }
                    if (a.ContainsKey("nick"))
                    {
                        int i = Int(a, "index");
                        var p2 = gi.startingAndOptionalPawns[i];
                        if (p2.Name is NameTriple nt) p2.Name = new NameTriple(nt.First, Str(a, "nick"), nt.Last);
                    }
                    break;
                }
                default: throw new ToolError("nothing to set on " + page.GetType().Name);
            }
            return PageState();
        }

        static Dictionary<string, object> TileInfo(PlanetTile t)
        {
            var tile = Find.WorldGrid[t];
            var o = new Dictionary<string, object>
            {
                ["tile"] = t.tileId, ["biome"] = tile.PrimaryBiome.defName, ["hilliness"] = tile.hilliness.ToString(),
                ["avgTemp"] = Math.Round(tile.temperature, 1),
                ["minTemp"] = Math.Round(GenTemperature.MinTemperatureAtTile(t), 1),
                ["maxTemp"] = Math.Round(GenTemperature.MaxTemperatureAtTile(t), 1),
                ["rainfall"] = Math.Round(tile.rainfall),
                ["growingTwelfths"] = GenTemperature.TwelfthsInAverageTemperatureRange(t, 6f, 42f).Count,
                ["coastal"] = tile.IsCoastal,
                ["latLong"] = Find.WorldGrid.LongLatOf(t).ToString(),
            };
            if (tile is SurfaceTile st)
            {
                if (st.Rivers != null && st.Rivers.Count > 0) o["river"] = st.Rivers[0].river.defName;
                if (st.Roads != null && st.Roads.Count > 0) o["road"] = st.Roads[0].road.defName;
            }
            if (tile.mutatorsNullable != null && tile.mutatorsNullable.Count > 0) o["features"] = tile.mutatorsNullable.Select(m => (object)m.defName).ToList();
            if (tile.Landmark != null) o["landmark"] = tile.Landmark.name;
            return o;
        }

        static object FindTiles(Dictionary<string, object> a)
        {
            if (Find.World == null) throw new ToolError("no world generated yet");
            var layer = Find.WorldGrid.Surface;
            var biome = Str(a, "biome", false);
            var hills = StrList(a, "hilliness");
            int minGrow = Int(a, "minGrowingTwelfths", 0);
            int maxGrow = Int(a, "maxGrowingTwelfths", 12);   // a long winter: few growing months
            float? minT = a.TryGetValue("minTemp", out var mt) && mt is double mtd ? (float?)mtd : null;
            float? maxT = a.TryGetValue("maxTemp", out var xt) && xt is double xtd ? (float?)xtd : null;
            bool needRiver = Bool(a, "river", false), needCoast = Bool(a, "coastal", false);
            var feature = Str(a, "feature", false);
            var res = new List<object>();
            for (int i = 0; i < layer.TilesCount && res.Count < Int(a, "limit", 15); i++)
            {
                var tile = layer[i];
                if (!tile.PrimaryBiome.canBuildBase || !tile.PrimaryBiome.implemented || tile.hilliness == Hilliness.Impassable) continue;
                if (biome != null && !string.Equals(tile.PrimaryBiome.defName, biome, StringComparison.OrdinalIgnoreCase)) continue;
                if (hills != null && !hills.Any(h => string.Equals(h, tile.hilliness.ToString(), StringComparison.OrdinalIgnoreCase))) continue;
                if (needCoast && !tile.IsCoastal) continue;
                var st = tile as SurfaceTile;
                if (needRiver && (st?.Rivers == null || st.Rivers.Count == 0)) continue;
                if (feature != null && (tile.mutatorsNullable == null || !tile.mutatorsNullable.Any(m => m.defName.IndexOf(feature, StringComparison.OrdinalIgnoreCase) >= 0))) continue;
                var pt = tile.tile;
                if (minT.HasValue && GenTemperature.MinTemperatureAtTile(pt) < minT.Value) continue;
                if (maxT.HasValue && GenTemperature.MaxTemperatureAtTile(pt) > maxT.Value) continue;
                if ((minGrow > 0 || maxGrow < 12) && GenTemperature.TwelfthsInAverageTemperatureRange(pt, 6f, 42f).Count is int g && (g < minGrow || g > maxGrow)) continue;
                if (!TileFinder.IsValidTileForNewSettlement(pt)) continue;
                res.Add(TileInfo(pt));
            }
            return new Dictionary<string, object>
            {
                ["biomes"] = DefDatabase<BiomeDef>.AllDefs.Where(b => b.canBuildBase && b.implemented).Select(b => (object)b.defName).ToList(),
                ["tiles"] = res,
            };
        }
    }
}
