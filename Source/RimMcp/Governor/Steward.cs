using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMcp
{
    // The steward: peacetime reflexes inside the game, so they happen when the thing happens and
    // not every few game hours between Claude's turns. Same rule as the governor: only what a
    // player could do (prioritized work orders, forbid flags, thermostat, area, research choice,
    // closing informational windows); every action emits a "steward" event, every reflex has a
    // toggle in governor.json "steward.reflexes". Things that need Claude emit "needs_attention"
    // (no stop) or "decision_needed" (stops a run).
    public static class Steward
    {
        static readonly Dictionary<Pawn, int> lastOrder = new Dictionary<Pawn, int>();
        static readonly Dictionary<string, int> lastAlarm = new Dictionary<string, int>();
        static readonly HashSet<int> flagged = new HashSet<int>();      // thing ids already reported
        static readonly Dictionary<Pawn, ThingDef> lastWeapon = new Dictionary<Pawn, ThingDef>();
        static readonly FieldInfo curNodeField = typeof(Dialog_NodeTree).GetField("curNode", BindingFlags.Instance | BindingFlags.NonPublic);
        static float lastFrame;

        static StewardPolicy P => GovernorPolicy.Current.steward;
        static bool On(string reflex) => P != null && P.enabled && P.On(reflex);

        public static void Reset()
        {
            lastOrder.Clear(); lastAlarm.Clear(); flagged.Clear(); lastWeapon.Clear();
        }

        static void Did(Map map, string reflex, string what, Dictionary<string, object> more = null)
        {
            var d = new Dictionary<string, object> { ["reflex"] = reflex, ["did"] = what };
            if (more != null) foreach (var kv in more) d[kv.Key] = kv.Value;
            GovernorEvents.Emit("steward", map, d);
        }

        static void Attention(Map map, string reflex, string what, IntVec3? at = null)
        {
            var d = new Dictionary<string, object> { ["reflex"] = reflex, ["what"] = what };
            if (at.HasValue) d["at"] = new List<object> { at.Value.x, at.Value.z };
            GovernorEvents.Emit("needs_attention", map, d);
        }

        // ------------------------------------------------------------------ every frame (the game may be paused)

        // Informational windows pause the game; ticks do not run while they are open, so this runs per frame.
        public static void Frame()
        {
            if (Current.ProgramState != ProgramState.Playing || !GovernorPolicy.Current.enabled || !On("closeRoutineWindows")) return;
            if (UnityEngine.Time.realtimeSinceStartup - lastFrame < 0.5f) return;
            lastFrame = UnityEngine.Time.realtimeSinceStartup;
            foreach (var w in Find.WindowStack.Windows.ToList())
            {
                string text = null;
                if (w is Dialog_NodeTree nt && curNodeField?.GetValue(nt) is DiaNode node) text = node.text.Resolve()?.StripTags();
                else if (w is Dialog_MessageBox mb) text = mb.text.Resolve()?.StripTags();
                if (text == null) continue;
                bool routine = w is Dialog_NodeTree && text.StartsWith("Research finished")
                               || w is Dialog_MessageBox && text.IndexOf("cannot build", StringComparison.OrdinalIgnoreCase) >= 0
                                  && text.IndexOf("meme", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!routine) continue;
                w.Close();
                Did(null, "closeRoutineWindows", "closed: " + text.Split('\n')[0]);
            }
        }

        // ------------------------------------------------------------------ ticks

        // ~every 250 ticks from the governor, home maps only, also during threats
        public static void Slow(Map map)
        {
            if (P == null || !P.enabled) return;
            int now = Find.TickManager.TicksGame;
            var pawns = map.mapPawns.FreeColonistsSpawned.ToList();
            bool threat = Governor.Instance?.StateFor(map)?.active ?? false;
            foreach (var p in pawns)
            {
                if (lastOrder.TryGetValue(p, out var t) && now - t < 600) continue;
                if (On("rescue") && !threat && p.Downed && !p.InBed() && !Targeted(map, p, JobDefOf.Rescue))
                {
                    if (Order(map, p, "DoctorRescue", "rescue", "rescue " + p.LabelShort)) continue;
                }
                if (On("tend") && p.InBed() && HealthAIUtility.ShouldBeTendedNowByPlayer(p) && !Targeted(map, p, JobDefOf.TendPatient))
                {
                    if (Order(map, p, p.health.hediffSet.BleedRateTotal > 0.01f ? "DoctorTendEmergency" : "DoctorTendToHumanlikes", "tend", "tend " + p.LabelShort)) continue;
                }
                var food = p.needs?.food?.CurLevelPercentage ?? 1f;
                if (On("clearForcedRest") && p.InBed() && !p.Downed && p.CurJob != null && p.CurJob.playerForced && food < P.forcedRestFoodPct)
                {
                    // "Rest until healed" on a walking patient: nobody feeds a forced patient (two near-starvations)
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    lastOrder[p] = now;
                    Did(map, "clearForcedRest", $"cleared forced bed rest of {p.LabelShort} (food {Math.Round(food * 100)}%)");
                    continue;
                }
                if (On("feedPatients") && p.InBed() && food < P.feedBelowPct && !Targeted(map, p, JobDefOf.FeedPatient))
                {
                    if (Order(map, p, "DoctorFeedHumanlikes", "feedPatients", $"feed {p.LabelShort} (food {Math.Round(food * 100)}%)")) continue;
                }
            }
            if (On("diseaseAlarm")) DiseaseAlarm(map, pawns, now);
            if (On("research")) Research();
            if (On("dismissRoutineLetters")) Letters();
            if (On("power")) Power(map);
            if (On("thermostats")) Thermostats(map);
            if (On("newColonistArea")) Areas(map, pawns);
            if (On("unforbidWeapons") && !threat) Weapons(map, pawns);
            if (On("roofValuables") && now % 15000 < 250) Valuables(map);
        }

        static bool Targeted(Map map, Pawn patient, JobDef job) =>
            map.mapPawns.FreeColonistsSpawned.Any(c => c.CurJob != null && c.CurJob.def == job && c.CurJob.targetA.Thing == patient);

        // the player's "Prioritize ..." right-click: a forced job from the same WorkGiver the menu uses
        static bool Order(Map map, Pawn patient, string workGiver, string reflex, string what)
        {
            var wg = DefDatabase<WorkGiverDef>.GetNamedSilentFail(workGiver);
            if (!(wg?.Worker is WorkGiver_Scanner scanner)) return false;
            var helpers = map.mapPawns.FreeColonistsSpawned
                .Where(c => c != patient && !c.Downed && !c.Drafted && !c.InMentalState && c.workSettings != null &&
                            !c.WorkTypeIsDisabled(wg.workType) && c.workSettings.WorkIsActive(wg.workType) &&
                            !(Governor.Instance?.Manual.Contains(c) ?? false) && !(c.CurJob?.playerForced ?? false))
                .ToList();
            if (helpers.Count == 0) return false;
            // the closest of the good doctors: a far one reserves the patient and blocks the one next to it
            int best = helpers.Max(c => c.skills?.GetSkill(SkillDefOf.Medicine)?.Level ?? 0);
            var pick = helpers.Where(c => (c.skills?.GetSkill(SkillDefOf.Medicine)?.Level ?? 0) >= best - 3 || reflex == "rescue" || reflex == "feedPatients")
                .OrderBy(c => c.Position.DistanceToSquared(patient.Position));
            foreach (var c in pick)
            {
                if (!scanner.HasJobOnThing(c, patient, true)) continue;
                var job = scanner.JobOnThing(c, patient, true);
                if (job == null) continue;
                if (!c.jobs.TryTakeOrderedJobPrioritizedWork(job, scanner, patient.Position)) continue;
                lastOrder[patient] = Find.TickManager.TicksGame;
                Did(map, reflex, $"{c.LabelShort}: {what}", new Dictionary<string, object> { ["pawn"] = patient.LabelShort, ["by"] = c.LabelShort });
                return true;
            }
            return false;
        }

        static void DiseaseAlarm(Map map, List<Pawn> pawns, int now)
        {
            foreach (var p in pawns)
                foreach (var h in p.health.hediffSet.hediffs)
                {
                    var imm = h.TryGetComp<HediffComp_Immunizable>();
                    if (imm == null || h.def.lethalSeverity <= 0f || !h.Visible) continue;
                    float sev = h.Severity / h.def.lethalSeverity;
                    if (sev < 0.5f || sev < imm.Immunity) continue;
                    var key = p.ThingID + h.def.defName;
                    if (lastAlarm.TryGetValue(key, out var t) && now - t < 15000) continue;
                    lastAlarm[key] = now;
                    GovernorEvents.Emit("decision_needed", map, new Dictionary<string, object>
                    {
                        ["reason"] = $"{p.LabelShort}: {h.LabelBaseCap} severity {Math.Round(sev * 100)}% >= immunity {Math.Round(imm.Immunity * 100)}%",
                        ["pawn"] = p.LabelShort, ["hint"] = "best doctor + best medicine, medical bed, keep fed; buy medicine",
                    });
                }
        }

        static void Research()
        {
            var rm = Find.ResearchManager;
            if (rm.GetProject() != null) return;
            foreach (var name in P.researchQueue)
            {
                var proj = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(name);
                if (proj == null || proj.IsFinished || !proj.CanStartNow) continue;
                rm.SetCurrentProject(proj);
                Did(null, "research", "research -> " + proj.defName);
                return;
            }
            if (!flagged.Contains(-1))
            {
                flagged.Add(-1);
                Attention(null, "research", "no research project and nothing startable left in steward.researchQueue");
            }
        }

        static void Letters()
        {
            foreach (var l in Find.LetterStack.LettersListForReading.ToList())
            {
                if (!(l.def == LetterDefOf.NeutralEvent || l.def == LetterDefOf.PositiveEvent)) continue;
                if (!l.CanDismissWithRightClick || l is ChoiceLetter && !(l is StandardLetter)) continue;
                var label = l.Label.ToString().StripTags();
                if (label.StartsWith("Join offer") || label.StartsWith("Quest")) continue;
                Find.LetterStack.RemoveLetter(l);
                Did(null, "dismissRoutineLetters", "dismissed letter: " + label);
            }
        }

        static void Power(Map map)
        {
            int comps = map.resourceCounter.GetCount(ThingDefOf.ComponentIndustrial);
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                string issue = null;
                var fuel = b.TryGetComp<CompRefuelable>();
                if (b.TryGetComp<CompPowerPlant>() != null && fuel != null && !fuel.HasFuel) issue = $"{b.LabelCap} out of fuel (haulers refuel it: is fuel in stock?)";
                var br = b.TryGetComp<CompBreakdownable>();
                if (br != null && br.BrokenDown && comps == 0) issue = $"{b.LabelCap} broken down and no component in stock";
                if (b is Building_TurretGun && b.TryGetComp<CompPowerTrader>() is CompPowerTrader pt && !pt.PowerOn) issue = $"{b.LabelCap} has no power";
                if (issue == null) { flagged.Remove(b.thingIDNumber); continue; }
                if (!flagged.Add(b.thingIDNumber)) continue;
                Attention(map, "power", issue, b.Position);
            }
        }

        static void Thermostats(Map map)
        {
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                var tc = b.TryGetComp<CompTempControl>();
                if (tc == null || b.def.defName.IndexOf("Cooler", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (Math.Abs(tc.TargetTemperature - tc.Props.defaultTargetTemperature) > 0.1f || flagged.Contains(b.thingIDNumber)) continue;
                var cold = b.Position + IntVec3.South.RotatedBy(b.Rotation);
                var room = cold.InBounds(map) ? cold.GetRoom(map) : null;
                bool freezer = room != null && !room.PsychologicallyOutdoors && room.Cells.Any(c =>
                    c.GetSlotGroup(map) is SlotGroup sg && sg.Settings.filter.Allows(ThingDefOf.MealSimple));
                flagged.Add(b.thingIDNumber);
                if (freezer)
                {
                    tc.TargetTemperature = P.freezerTargetC;
                    Did(map, "thermostats", $"{b.LabelCap} at ({b.Position.x},{b.Position.z}) -> {P.freezerTargetC}C (cold side is a food store)");
                }
                else Attention(map, "thermostats", $"{b.LabelCap} at ({b.Position.x},{b.Position.z}) still at 21C (default): set a target", b.Position);
            }
        }

        static void Areas(Map map, List<Pawn> pawns)
        {
            var areas = pawns.Select(p => p.playerSettings?.AreaRestrictionInPawnCurrentMap).Where(a => a != null).ToList();
            if (areas.Count == 0) return;
            var common = areas.GroupBy(a => a).OrderByDescending(g => g.Count()).First().Key;
            foreach (var p in pawns)
            {
                if (p.playerSettings == null || p.playerSettings.AreaRestrictionInPawnCurrentMap != null || flagged.Contains(p.thingIDNumber)) continue;
                flagged.Add(p.thingIDNumber);
                p.playerSettings.AreaRestrictionInPawnCurrentMap = common;
                Did(map, "newColonistArea", $"{p.LabelShort} had no allowed area -> {common.Label} (like the others)");
            }
        }

        static void Weapons(Map map, List<Pawn> pawns)
        {
            foreach (var p in pawns)
            {
                var prim = p.equipment?.Primary;
                if (prim != null) { lastWeapon[p] = prim.def; continue; }
                if (p.Downed || p.Drafted || !lastWeapon.TryGetValue(p, out var def)) continue;
                var w = GenClosest.ClosestThing_Global(p.Position, map.listerThings.ThingsOfDef(def).Where(t => t.Spawned && t.IsForbidden(Faction.OfPlayer)), 12f);
                if (w == null) continue;
                w.SetForbidden(false, false);
                var job = JobMaker.MakeJob(JobDefOf.Equip, w);
                bool ok = p.CanReserveAndReach(w, PathEndMode.ClosestTouch, Danger.Deadly) && p.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                lastWeapon.Remove(p);
                Did(map, "unforbidWeapons", $"{p.LabelShort}: unforbade the dropped {w.LabelCap}" + (ok ? " and went to equip it" : ""));
            }
        }

        static void Valuables(Map map)
        {
            foreach (var z in map.zoneManager.AllZones.OfType<Zone_Stockpile>())
            {
                int open = 0; string example = null;
                foreach (var c in z.Cells)
                {
                    if (c.Roofed(map)) continue;
                    foreach (var t in c.GetThingList(map))
                        if (t.def.category == ThingCategory.Item && (t.def.IsMedicine || t.def.IsApparel || t.def.IsWeapon || t.def == ThingDefOf.ComponentIndustrial || t.def.BaseMarketValue >= 8f))
                        { open++; example = example ?? t.LabelCap; }
                }
                if (open > 0) Attention(map, "roofValuables", $"{z.label}: {open} valuable stacks under open sky ({example}...): they deteriorate", z.Cells.First());
            }
        }
    }

    public class StewardPolicy
    {
        public bool enabled;      // off until a policy turns it on (playbook day1 / governor steward on:true)
        public static readonly string[] Reflexes =
        {
            "rescue", "tend", "feedPatients", "clearForcedRest", "diseaseAlarm", "research", "closeRoutineWindows",
            "dismissRoutineLetters", "power", "thermostats", "newColonistArea", "unforbidWeapons", "roofValuables", "stopRun",
        };
        public Dictionary<string, bool> reflexes = Reflexes.ToDictionary(r => r, r => true);
        public List<string> researchQueue = new List<string>();
        public float freezerTargetC = -10f;
        public float forcedRestFoodPct = 0.4f;
        public float feedBelowPct = 0.3f;
        // events that pause a running `run` at once (the run returns and Claude decides)
        public List<string> stopOn = new List<string> { "decision_needed", "threat_start", "colonist_died", "colonist_downed", "mental_break", "fire_in_home", "quest_offer" };

        public bool On(string r) => !reflexes.TryGetValue(r, out var v) || v;
    }
}
