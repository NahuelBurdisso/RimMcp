using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimMcp
{
    // Every tool runs on the main thread (see Bridge). Results are plain dictionaries/lists.
    public static partial class Tools
    {
        static readonly Dictionary<string, Func<Dictionary<string, object>, object>> handlers =
            new Dictionary<string, Func<Dictionary<string, object>, object>>
            {
                ["status"] = Status,
                ["colonists"] = Colonists,
                ["colonist"] = Colonist,
                ["resources"] = Resources,
                ["alerts"] = Alerts,
                ["threats"] = Threats,
                ["set_speed"] = SetSpeed,
                ["draft"] = Draft,
                ["set_work_priority"] = SetWorkPriority,
                ["message"] = ShowMessage,
                ["focus"] = Focus,
            };

        public static object Run(string tool, Dictionary<string, object> args)
        {
            if (tool == null || !handlers.TryGetValue(tool, out var h))
                throw new ToolError("unknown tool: " + tool + ". Known: " + string.Join(", ", handlers.Keys));
            return h(args);
        }

        // ------------------------------------------------------------------ helpers

        static string Str(Dictionary<string, object> a, string key, bool required = true)
        {
            if (a.TryGetValue(key, out var v) && v != null) return v.ToString();
            if (required) throw new ToolError("missing argument: " + key);
            return null;
        }

        static void RequireGame()
        {
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null)
                throw new ToolError("no game loaded (state: " + Current.ProgramState + ")");
        }

        static IEnumerable<Pawn> AllColonists() =>
            Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned);

        static Pawn FindColonist(string name)
        {
            var all = AllColonists().ToList();
            var hit = all.FirstOrDefault(p => string.Equals(p.LabelShort, name, StringComparison.OrdinalIgnoreCase))
                      ?? all.FirstOrDefault(p => p.Name != null &&
                                                 p.Name.ToStringFull.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (hit == null)
                throw new ToolError($"no colonist named '{name}'. Colonists: " +
                                    string.Join(", ", all.Select(p => p.LabelShort)));
            return hit;
        }

        static float Pct(float f) => (float)Math.Round(f * 100f, 1);

        static string Clean(string s) => s?.StripTags().Trim();

        static Dictionary<string, object> Obj() => new Dictionary<string, object>();

        // ------------------------------------------------------------------ reads

        static object Status(Dictionary<string, object> a)
        {
            var r = Obj();
            r["programState"] = Current.ProgramState.ToString();
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null) return r;
            var tm = Find.TickManager;
            var map = Find.CurrentMap ?? Find.AnyPlayerHomeMap;
            r["colony"] = Faction.OfPlayer.Name;
            r["ticks"] = tm.TicksGame;
            r["speed"] = tm.CurTimeSpeed.ToString();
            r["paused"] = tm.Paused;
            if (map != null)
            {
                var longLat = Find.WorldGrid.LongLatOf(map.Tile);
                r["date"] = GenDate.DateFullStringAt(tm.TicksAbs, longLat);
                r["hour"] = GenLocalDate.HourOfDay(map);
                r["currentMap"] = map.Parent?.LabelCap.ToString();
                r["biome"] = map.Biome?.LabelCap.ToString();
                r["outdoorTempC"] = (float)Math.Round(map.mapTemperature.OutdoorTemp, 1);
                r["weather"] = map.weatherManager.curWeather?.LabelCap.ToString();
                r["wealth"] = (float)Math.Round(map.wealthWatcher.WealthTotal);
            }
            r["maps"] = Find.Maps.Select(m => m.Parent?.LabelCap.ToString()).ToList();
            r["colonists"] = AllColonists().Count();
            r["manualWorkPriorities"] = Find.PlaySettings.useWorkPriorities;
            r["storyteller"] = Find.Storyteller.def.LabelCap.ToString();
            r["difficulty"] = Find.Storyteller.difficultyDef?.LabelCap.ToString();
            return r;
        }

        static Dictionary<string, object> Summary(Pawn p)
        {
            var r = Obj();
            r["name"] = p.LabelShort;
            r["fullName"] = p.Name?.ToStringFull;
            r["age"] = p.ageTracker.AgeBiologicalYears;
            r["map"] = p.Map?.Parent?.LabelCap.ToString();
            r["position"] = new List<object> { p.Position.x, p.Position.z };
            if (p.needs?.mood != null)
            {
                r["moodPct"] = Pct(p.needs.mood.CurLevelPercentage);
                r["breakMinorPct"] = Pct(p.mindState.mentalBreaker.BreakThresholdMinor);
            }
            r["mentalState"] = p.MentalStateDef?.label;
            r["healthPct"] = Pct(p.health.summaryHealth.SummaryHealthPercent);
            r["bleeding"] = p.health.hediffSet.BleedRateTotal > 0f;
            r["downed"] = p.Downed;
            r["drafted"] = p.Drafted;
            r["job"] = Clean(p.jobs?.curDriver?.GetReport());
            return r;
        }

        static object Colonists(Dictionary<string, object> a)
        {
            RequireGame();
            return AllColonists().Select(p => (object)Summary(p)).ToList();
        }

        static object Colonist(Dictionary<string, object> a)
        {
            RequireGame();
            var p = FindColonist(Str(a, "name"));
            var r = Summary(p);
            r["gender"] = p.gender.ToString();
            r["xenotype"] = p.genes?.XenotypeLabelCap;
            r["traits"] = p.story?.traits?.allTraits.Select(t => (object)t.LabelCap).ToList();
            r["skills"] = p.skills?.skills.Select(s => (object)new Dictionary<string, object>
            {
                ["skill"] = s.def.LabelCap.ToString(),
                ["level"] = s.TotallyDisabled ? -1 : s.Level,
                ["passion"] = s.passion.ToString(),
            }).ToList();
            r["health"] = p.health.hediffSet.hediffs.Where(h => h.Visible).Select(h => (object)new Dictionary<string, object>
            {
                ["condition"] = h.LabelCap,
                ["part"] = h.Part?.Label,
                ["bleeding"] = h.Bleeding,
                ["needsTending"] = h.TendableNow(),
            }).ToList();
            if (p.needs?.mood != null)
            {
                var groups = new List<Thought>();
                p.needs.mood.thoughts.GetDistinctMoodThoughtGroups(groups);
                r["moodThoughts"] = groups
                    .Select(t => (t, off: p.needs.mood.thoughts.MoodOffsetOfGroup(t)))
                    .Where(x => Math.Abs(x.off) > 0.01f)
                    .OrderBy(x => x.off)
                    .Select(x => (object)new Dictionary<string, object>
                    {
                        ["thought"] = Clean(x.t.LabelCap),
                        ["mood"] = (float)Math.Round(x.off, 1),
                    }).ToList();
            }
            r["needs"] = p.needs?.AllNeeds.Select(n => (object)new Dictionary<string, object>
            {
                ["need"] = n.LabelCap.ToString(),
                ["pct"] = Pct(n.CurLevelPercentage),
            }).ToList();
            if (p.workSettings != null && p.workSettings.EverWork)
            {
                var work = Obj();
                foreach (var w in DefDatabase<WorkTypeDef>.AllDefsListForReading.OrderByDescending(w => w.naturalPriority))
                    work[w.defName] = p.WorkTypeIsDisabled(w) ? (object)"disabled" : p.workSettings.GetPriority(w);
                r["workPriorities"] = work;
            }
            r["weapon"] = p.equipment?.Primary?.LabelCap;
            r["apparel"] = p.apparel?.WornApparel.Select(t => (object)t.LabelCap).ToList();
            r["inventory"] = p.inventory?.innerContainer.Select(t => (object)t.LabelCap).ToList();
            return r;
        }

        static object Resources(Dictionary<string, object> a)
        {
            RequireGame();
            var map = Find.CurrentMap ?? Find.AnyPlayerHomeMap;
            if (map == null) throw new ToolError("no map");
            var counts = map.resourceCounter.AllCountedAmounts
                .Where(kv => kv.Value > 0)
                .OrderByDescending(kv => kv.Key.BaseMarketValue * kv.Value)
                .Take(80)
                .ToDictionary(kv => kv.Key.LabelCap.ToString(), kv => (object)kv.Value);
            // Loose items the colony can see but has not stored (a new colony starts with all of them here).
            var looseThings = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                .Where(t => !t.IsInAnyStorage() && !t.Fogged()).ToList();
            Dictionary<string, object> Tally(IEnumerable<Thing> things) => things
                .GroupBy(t => t.def)
                .Select(g => (def: g.Key, count: g.Sum(t => t.stackCount)))
                .OrderByDescending(x => x.def.BaseMarketValue * x.count)
                .Take(40)
                .ToDictionary(x => x.def.LabelCap.ToString(), x => (object)x.count);
            var loose = Tally(looseThings.Where(t => !t.IsForbidden(Faction.OfPlayer)));
            var looseForbidden = Tally(looseThings.Where(t => t.IsForbidden(Faction.OfPlayer)));
            var colonists = map.mapPawns.FreeColonistsSpawnedCount;
            // stored food (what the game counts) + loose, allowed, human-edible food lying around
            var looseFood = looseThings.Where(t => !t.IsForbidden(Faction.OfPlayer) && t.def.IsNutritionGivingIngestible &&
                                                   t.def.ingestible.HumanEdible && !(t is Corpse))
                .Sum(t => t.GetStatValue(StatDefOf.Nutrition) * t.stackCount);
            var nutrition = map.resourceCounter.TotalHumanEdibleNutrition + looseFood;
            return new Dictionary<string, object>
            {
                ["map"] = map.Parent?.LabelCap.ToString(),
                ["note"] = "items = in storage (what the game counts); outsideStorage = loose and visible; " +
                           "forbiddenOutsideStorage = loose but forbidden (pawns will not haul or use them)",
                ["humanEdibleNutrition"] = (float)Math.Round(nutrition, 1),
                // ~1.6 nutrition per colonist per day in vanilla; a rough guide, not exact
                ["approxFoodDays"] = colonists > 0 ? (float)Math.Round(nutrition / (colonists * 1.6f), 1) : 0f,
                ["items"] = counts,
                ["outsideStorage"] = loose,
                ["forbiddenOutsideStorage"] = looseForbidden,
            };
        }

        static readonly FieldInfo activeAlertsField =
            typeof(AlertsReadout).GetField("activeAlerts", BindingFlags.Instance | BindingFlags.NonPublic);

        static readonly FieldInfo liveMessagesField =
            typeof(Messages).GetField("liveMessages", BindingFlags.Static | BindingFlags.NonPublic);

        static object Alerts(Dictionary<string, object> a)
        {
            RequireGame();
            var r = Obj();
            if (Find.UIRoot is UIRoot_Play && activeAlertsField?.GetValue(Find.Alerts) is List<Alert> alerts)
                r["alerts"] = alerts.Select(al => (object)new Dictionary<string, object>
                {
                    ["alert"] = Clean(al.GetLabel()),
                    ["priority"] = al.Priority.ToString(),
                    ["explanation"] = Clean(al.GetExplanation().ToString()),
                }).ToList();
            r["letters"] = Find.LetterStack.LettersListForReading.Select(l => (object)new Dictionary<string, object>
            {
                ["letter"] = Clean(l.Label.ToString()),
                ["type"] = l.def.defName,
                ["hoursAgo"] = (float)Math.Round((Find.TickManager.TicksGame - l.arrivalTick) / 2500f, 1),
            }).ToList();
            if (liveMessagesField?.GetValue(null) is List<Message> msgs)
                r["messages"] = msgs.Select(m => (object)Clean(m.text)).ToList();
            return r;
        }

        static object Threats(Dictionary<string, object> a)
        {
            RequireGame();
            var r = new List<object>();
            foreach (var map in Find.Maps)
            {
                var hostiles = map.mapPawns.AllPawnsSpawned
                    .Where(p => !p.Dead && !p.Downed && p.HostileTo(Faction.OfPlayer) && !p.Fogged())
                    .GroupBy(p => (faction: p.Faction?.Name ?? "wild", kind: p.KindLabel))
                    .Select(g => (object)new Dictionary<string, object>
                    {
                        ["faction"] = g.Key.faction,
                        ["kind"] = g.Key.kind,
                        ["count"] = g.Count(),
                    }).ToList();
                r.Add(new Dictionary<string, object>
                {
                    ["map"] = map.Parent?.LabelCap.ToString(),
                    ["hostiles"] = hostiles,
                    ["fires"] = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count,
                    ["conditions"] = map.gameConditionManager.ActiveConditions
                        .Select(c => (object)c.LabelCap.ToString()).ToList(),
                });
            }
            return r;
        }

        // ------------------------------------------------------------------ actions

        static object SetSpeed(Dictionary<string, object> a)
        {
            RequireGame();
            var s = Str(a, "speed").ToLowerInvariant();
            TimeSpeed speed;
            switch (s)
            {
                case "pause": case "paused": case "0": speed = TimeSpeed.Paused; break;
                case "normal": case "1": speed = TimeSpeed.Normal; break;
                case "fast": case "2": speed = TimeSpeed.Fast; break;
                case "superfast": case "3": speed = TimeSpeed.Superfast; break;
                default: throw new ToolError("speed must be pause, normal, fast or superfast");
            }
            Find.TickManager.CurTimeSpeed = speed;
            return new Dictionary<string, object> { ["speed"] = Find.TickManager.CurTimeSpeed.ToString() };
        }

        static object Draft(Dictionary<string, object> a)
        {
            RequireGame();
            var drafted = !a.TryGetValue("drafted", out var d) || d is bool b && b;
            var names = a.TryGetValue("names", out var n) && n is List<object> list
                ? list.Select(x => x.ToString()).ToList()
                : null;
            var pawns = names == null || names.Count == 0
                ? AllColonists().Where(p => p.drafter != null && !p.Downed).ToList()
                : names.Select(FindColonist).ToList();
            var result = new List<object>();
            foreach (var p in pawns)
            {
                if (p.drafter == null) { result.Add(p.LabelShort + ": cannot be drafted"); continue; }
                p.drafter.Drafted = drafted;
                result.Add($"{p.LabelShort}: {(p.Drafted ? "drafted" : "undrafted")}");
            }
            return result;
        }

        static object SetWorkPriority(Dictionary<string, object> a)
        {
            RequireGame();
            var p = FindColonist(Str(a, "name"));
            var wname = Str(a, "work");
            var w = DefDatabase<WorkTypeDef>.AllDefsListForReading.FirstOrDefault(x =>
                        string.Equals(x.defName, wname, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.labelShort, wname, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.label, wname, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ToolError("unknown work type '" + wname + "'. Known: " +
                                           string.Join(", ", DefDatabase<WorkTypeDef>.AllDefsListForReading.Select(x => x.defName)));
            if (!a.TryGetValue("priority", out var pv) || !(pv is double pd) || pd < 0 || pd > 4)
                throw new ToolError("priority must be 0 (off) to 4");
            if (p.WorkTypeIsDisabled(w)) throw new ToolError($"{p.LabelShort} is incapable of {w.labelShort}");
            if (a.TryGetValue("manual", out var mv) && mv is bool manual && manual != Find.PlaySettings.useWorkPriorities)
            {
                // same as ticking "Manual priorities" in the Work tab
                Find.PlaySettings.useWorkPriorities = manual;
                foreach (var pawn in PawnsFinder.AllMapsWorldAndTemporary_Alive)
                    if (pawn.Faction == Faction.OfPlayer && pawn.workSettings != null)
                        pawn.workSettings.Notify_UseWorkPrioritiesChanged();
            }
            p.workSettings.SetPriority(w, (int)pd);
            // Without manual priorities the game treats any priority > 0 as plain "on" (shown as 3).
            // Switching that mode is the player's call, so only report it.
            return Find.PlaySettings.useWorkPriorities
                ? $"{p.LabelShort}: {w.labelShort} = {(int)pd}"
                : pd > 0
                    ? $"{p.LabelShort}: {w.labelShort} enabled (manual priorities are off in this game, so 1-4 all mean 'on')"
                    : $"{p.LabelShort}: {w.labelShort} disabled";
        }

        static object ShowMessage(Dictionary<string, object> a)
        {
            RequireGame();
            var text = Str(a, "text");
            Messages.Message("[Claude] " + text, MessageTypeDefOf.NeutralEvent, historical: false);
            return "shown";
        }

        static object Focus(Dictionary<string, object> a)
        {
            RequireGame();
            var p = FindColonist(Str(a, "name"));
            CameraJumper.TryJumpAndSelect(p);
            return "camera on " + p.LabelShort;
        }
    }
}
