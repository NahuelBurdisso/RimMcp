using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimMcp
{
    public enum GovRole { Ranged, Melee }

    public class GovAssignment
    {
        public IntVec3 cell;
        public GovRole role;
        public int arrivedTick = -1;
    }

    public class GovTask
    {
        public Pawn pawn;     // who does it
        public int tick;      // when ordered
        public int jobId;
    }

    // Per home map. Not saved: rebuilt from the map after a load (threats are re-detected).
    public class GovMapState
    {
        public Map map;
        public int createdTick;
        public bool fresh = true;          // no threat handled yet since the game was loaded
        public int postUntil = -1;         // keep rescuing/capturing after the end until then
        public readonly List<Pawn> postEnemies = new List<Pawn>();
        public bool active;
        public int startTick, lastThreatTick = -1, firstDeployTick = -1, lastPlanTick = -100000;
        public IntVec3 home = IntVec3.Invalid, enemyCentroid = IntVec3.Invalid, planCentroid = IntVec3.Invalid;
        public float enemyPower, ourPower;
        public IntVec3 enemyFront = IntVec3.Invalid;   // the threat nearest to home: what positions face
        public bool closeCombat;                       // an enemy within 12 cells of our line: hold, no re-planning
        public bool farHold;                           // enemies beyond engageDistance: nobody drafted yet
        public bool skirmishSent;                      // colonists near a far enemy were sent at it (event once)
        public float rangePull = 0.1f, planRangePull = 0.1f;
        public readonly Dictionary<Pawn, int> outOfRangeTicks = new Dictionary<Pawn, int>();
        public bool overwhelmed, planOverwhelmed, decisionSent, noFightersSent, manualHoldSent;
        public string planMode;
        public readonly List<Pawn> threats = new List<Pawn>();
        public readonly HashSet<Pawn> seen = new HashSet<Pawn>();
        public readonly Dictionary<Pawn, GovAssignment> assign = new Dictionary<Pawn, GovAssignment>();
        public readonly HashSet<Pawn> sheltered = new HashSet<Pawn>();
        public readonly HashSet<Pawn> retreated = new HashSet<Pawn>();
        public readonly Dictionary<Pawn, int> lastOrderTick = new Dictionary<Pawn, int>();
        public readonly Dictionary<Pawn, int> lastJobId = new Dictionary<Pawn, int>();
        public readonly HashSet<int> ourJobs = new HashSet<int>();
        public readonly Dictionary<Pawn, Thing> weapons = new Dictionary<Pawn, Thing>();
        public readonly Dictionary<IntVec3, int> badCells = new Dictionary<IntVec3, int>();
        public readonly HashSet<Pawn> startDowned = new HashSet<Pawn>();
        public readonly HashSet<Pawn> ourDowned = new HashSet<Pawn>();
        public readonly List<Pawn> startColonists = new List<Pawn>();
        public readonly HashSet<Pawn> warnedNoBed = new HashSet<Pawn>();
        public List<IntVec3> shelterCells;
        public HashSet<int> forbiddenSnapshot;
        // survive the end of a threat until they resolve
        public readonly Dictionary<Pawn, GovTask> rescues = new Dictionary<Pawn, GovTask>();
        public readonly Dictionary<Pawn, GovTask> captures = new Dictionary<Pawn, GovTask>();
        public int rescuedCount, capturedCount;

        public void ResetThreat()
        {
            postEnemies.Clear(); postUntil = -1;
            threats.Clear(); seen.Clear(); assign.Clear(); sheltered.Clear(); retreated.Clear();
            lastOrderTick.Clear(); lastJobId.Clear(); ourJobs.Clear(); weapons.Clear(); badCells.Clear();
            startDowned.Clear(); ourDowned.Clear(); startColonists.Clear(); warnedNoBed.Clear();
            shelterCells = null; forbiddenSnapshot = null;
            overwhelmed = planOverwhelmed = decisionSent = noFightersSent = skirmishSent = manualHoldSent = false;
            firstDeployTick = -1; lastPlanTick = -100000; planMode = null;
            enemyCentroid = planCentroid = enemyFront = IntVec3.Invalid;
            closeCombat = false; rangePull = planRangePull = 0.1f; outOfRangeTicks.Clear();
            rescuedCount = capturedCount = 0;
        }
    }

    // Deterministic reflexes that run inside the game: draft and position fighters when a threat
    // shows up on a home map, retreat the wounded, shelter non-combatants, rescue and capture the
    // downed, clean up afterwards. Configured by governor.json (GovernorPolicy), reports to
    // events.jsonl (GovernorEvents). Only player actions: draft, ordered jobs, forbid flags.
    public class Governor : GameComponent
    {
        public static Governor Instance => Current.Game?.GetComponent<Governor>();

        public readonly HashSet<Pawn> Drafted = new HashSet<Pawn>();   // drafted by the governor, still ours
        public readonly HashSet<Pawn> Manual = new HashSet<Pawn>();    // the player took them: hands off until the threat ends
        // ...or until they stand undrafted with no forced job for manualReleaseTicks: a driver that undrafted
        // everyone mid-raid (after_raid run too early) left 3 healthy colonists "unable to fight" a drifter
        readonly Dictionary<Pawn, int> manualSince = new Dictionary<Pawn, int>();
        readonly Dictionary<int, GovMapState> states = new Dictionary<int, GovMapState>();
        readonly GovernorWatch watch = new GovernorWatch();
        bool wasEnabled, policyPolled, ioWarned;
        readonly HashSet<string> loggedErrors = new HashSet<string>();
        public int Errors;

        List<Pawn> tmpSave1, tmpSave2;
        readonly List<Pawn> tmpPawns = new List<Pawn>();
        readonly List<Pawn> tmpPawns2 = new List<Pawn>();
        readonly List<Thing> tmpThings = new List<Thing>();
        readonly List<IAttackTarget> tmpTargets = new List<IAttackTarget>();

        public Governor(Game game) { }

        public IEnumerable<GovMapState> States => states.Values;

        public GovMapState StateFor(Map map)
        {
            if (map == null) return null;
            if (!states.TryGetValue(map.uniqueID, out var st))
            {
                st = new GovMapState { map = map, createdTick = Find.TickManager.TicksGame, lastThreatTick = Find.TickManager.TicksGame };
                states[map.uniqueID] = st;
            }
            return st;
        }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                tmpSave1 = Drafted.Where(p => p != null && !p.Dead && !p.Destroyed).ToList();
                tmpSave2 = Manual.Where(p => p != null && !p.Dead && !p.Destroyed).ToList();
            }
            Scribe_Collections.Look(ref tmpSave1, "draftedByGovernor", LookMode.Reference);
            Scribe_Collections.Look(ref tmpSave2, "manualPawns", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                Drafted.Clear();
                Manual.Clear();
                if (tmpSave1 != null) foreach (var p in tmpSave1) if (p != null) Drafted.Add(p);
                if (tmpSave2 != null) foreach (var p in tmpSave2) if (p != null) Manual.Add(p);
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit || Scribe.mode == LoadSaveMode.Saving)
            {
                tmpSave1 = null;
                tmpSave2 = null;
            }
        }

        public override void FinalizeInit()
        {
            Steward.Reset();
            wasEnabled = false;
            policyPolled = false;
            states.Clear();
        }

        public override void GameComponentTick()
        {
            try { DoTick(); }
            catch (Exception e)
            {
                Errors++;
                var key = e.GetType().Name + e.Message + e.TargetSite;
                if (loggedErrors.Count >= 10 || !loggedErrors.Add(key)) return; // each distinct error once, never spam
                Log.Error("[RimMcp] governor tick failed (each distinct error is logged once; the governor keeps running): " + e);
                try { GovernorEvents.Emit("governor_error", null, new Dictionary<string, object> { ["error"] = e.GetType().Name + ": " + e.Message }); }
                catch { }
            }
        }

        void DoTick()
        {
            int t = Find.TickManager.TicksGame;
            if (!policyPolled || t % 250 == 0)
            {
                policyPolled = true;
                try { GovernorPolicy.Poll(); }
                catch (Exception e)
                {
                    if (!ioWarned) Log.Warning("[RimMcp] governor could not read governor.json: " + e.Message);
                    ioWarned = true;
                }
            }
            var pol = GovernorPolicy.Current;
            if (!pol.enabled)
            {
                if (wasEnabled) OnDisabled();
                wasEnabled = false;
                return;
            }
            if (!wasEnabled)
            {
                wasEnabled = true;
                watch.Baseline();
                GovernorEvents.Emit("governor_active", null, new Dictionary<string, object>
                {
                    ["colony"] = Faction.OfPlayer?.Name, ["policy"] = GovernorPolicy.FilePath,
                });
            }
            if (t % 60 == 0)
            {
                watch.Fast();
                var maps = Find.Maps;
                for (int i = 0; i < maps.Count; i++)
                    if (maps[i].IsPlayerHome) Scan(maps[i], pol);
            }
            if (t % 120 == 30)
                foreach (var st in states.Values.ToList())
                    if (Find.Maps.Contains(st.map)) Heavy(st, pol);
            if (t % 250 == 125)
            {
                watch.Slow();
                Prune();
                var homes = Find.Maps;
                for (int i = 0; i < homes.Count; i++)
                    if (homes[i].IsPlayerHome) Steward.Slow(homes[i]);
            }
        }

        void OnDisabled()
        {
            var left = Drafted.Where(p => p != null && p.Spawned && p.Drafted).Select(p => (object)p.LabelShort).ToList();
            GovernorEvents.Emit("governor_disabled", null, new Dictionary<string, object> { ["leftDrafted"] = left });
            Drafted.Clear();
            Manual.Clear();
            states.Clear();
        }

        void Prune()
        {
            Drafted.RemoveWhere(p => p == null || p.Dead || p.Destroyed || !p.Spawned || p.Downed || p.InMentalState || p.Faction != Faction.OfPlayer);
            Manual.RemoveWhere(p => p == null || p.Dead || p.Destroyed || !p.Spawned);
            foreach (var p in manualSince.Keys.Where(k => !Manual.Contains(k)).ToList()) manualSince.Remove(p);
            foreach (var id in states.Keys.ToList())
            {
                var st = states[id];
                if (!Find.Maps.Contains(st.map) || !st.map.IsPlayerHome) states.Remove(id);
            }
        }

        // ------------------------------------------------------------------ threat detection (cheap)

        public static bool Ignored(Faction f, GovernorPolicy pol)
        {
            if (f == null || pol.ignoreFactions.Count == 0) return false;
            foreach (var s in pol.ignoreFactions)
                if (string.Equals(s, f.Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s, f.def.defName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        void Scan(Map map, GovernorPolicy pol)
        {
            tmpTargets.Clear();
            tmpTargets.AddRange(map.attackTargetsCache.TargetsHostileToColony);
            var st = StateFor(map);
            st.threats.Clear();
            for (int i = 0; i < tmpTargets.Count; i++)
            {
                var target = tmpTargets[i];
                if (!(target?.Thing is Pawn p) || !p.Spawned || p.Dead || p.Downed) continue;
                if (p.Faction == Faction.OfPlayer || p.HostFaction == Faction.OfPlayer) continue; // own berserkers, prison breaks
                if (Ignored(p.Faction, pol)) continue;
                if (!GenHostility.IsActiveThreatToPlayer(target)) continue;                     // fogged, dormant, threat-disabled
                st.threats.Add(p);
            }
            tmpTargets.Clear();
            // a wild predator hunting a colonist is not in the colony's attack-target cache until it
            // has bitten someone (run15: the cougar was seen only when Tomas was already down)
            var wild = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < wild.Count; i++)
            {
                var p = wild[i];
                if (p.Faction != null || p.Dead || p.Downed || !p.RaceProps.predator || st.threats.Contains(p) || p.Fogged()) continue;
                var job = p.CurJob;
                if (job == null || job.def != JobDefOf.PredatorHunt || !(job.targetA.Thing is Pawn prey) || prey.Faction != Faction.OfPlayer) continue;
                st.threats.Add(p);
            }
            if (st.active) DetectOverrides(st);
            if (st.threats.Count == 0) return;
            st.lastThreatTick = Find.TickManager.TicksGame;
            foreach (var p in st.threats) st.seen.Add(p);
            if (!st.active) StartThreat(st, pol);
            else if (pol.autoDraft) PointBlank(st);
        }

        void StartThreat(GovMapState st, GovernorPolicy pol)
        {
            var map = st.map;
            var found = st.threats.ToList();
            st.ResetThreat();
            st.threats.AddRange(found);
            foreach (var p in found) st.seen.Add(p);
            // rescue/capture jobs still running from the last fight stay ours
            foreach (var task in st.rescues.Values.Concat(st.captures.Values)) st.ourJobs.Add(task.jobId);
            st.active = true;
            st.startTick = Find.TickManager.TicksGame;
            if (!st.fresh) Manual.RemoveWhere(p => p == null || p.Map == map); // a new threat: whoever the player drafted gets re-detected
            st.fresh = false;
            st.home = GovernorPositions.HomeCenter(map, pol);
            tmpPawns.Clear();
            tmpPawns.AddRange(map.mapPawns.FreeColonistsSpawned);
            st.startColonists.AddRange(tmpPawns);
            foreach (var p in tmpPawns) if (p.Downed) st.startDowned.Add(p);
            st.forbiddenSnapshot = new HashSet<int>();
            ForEachLootCandidate(st, t => { if (t.IsForbidden(Faction.OfPlayer)) st.forbiddenSnapshot.Add(t.thingIDNumber); });
            st.enemyCentroid = Centroid(st.threats);
            st.enemyPower = st.threats.Sum(PowerOf);
            var nearest = st.threats.Min(p => (p.Position - st.home).LengthHorizontal);
            var factions = st.threats.GroupBy(FactionLabel).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
            GovernorEvents.Emit("threat_start", map, new Dictionary<string, object>
            {
                ["faction"] = factions.FirstOrDefault(),
                ["factions"] = factions.Count > 1 ? factions.Cast<object>().ToList() : null,
                ["kinds"] = st.threats.GroupBy(p => p.KindLabel).ToDictionary(g => g.Key, g => (object)g.Count()),
                ["count"] = st.threats.Count,
                ["combatPower"] = Mathf.Round(st.enemyPower),
                ["direction"] = GovernorPositions.Compass(st.home, st.enemyCentroid),
                ["edge"] = GovernorPositions.Edge(map, st.enemyCentroid),
                ["nearest"] = Mathf.Round(nearest),
                ["home"] = Pos(st.home),
                ["governor"] = pol.autoDraft ? "deploying" : "autoDraft off: not drafting",
            });
            if (pol.steward.enabled) Steward.PreRaidSave(map);
            Heavy(st, pol);
        }

        static string FactionLabel(Pawn p)
        {
            if (p.InAggroMentalState && p.RaceProps.Animal) return "manhunter";
            return p.Faction?.Name ?? (p.RaceProps.Animal ? "wild animal" : "no faction");
        }

        static IntVec3 Centroid(List<Pawn> ps)
        {
            if (ps.Count == 0) return IntVec3.Invalid;
            long x = 0, z = 0;
            foreach (var p in ps) { x += p.Position.x; z += p.Position.z; }
            return new IntVec3((int)(x / ps.Count), 0, (int)(z / ps.Count));
        }

        static List<object> Pos(IntVec3 c) => c.IsValid ? new List<object> { c.x, c.z } : null;

        // ------------------------------------------------------------------ heavy logic (every 120 ticks while needed)

        void Heavy(GovMapState st, GovernorPolicy pol)
        {
            var map = st.map;
            int t = Find.TickManager.TicksGame;
            bool leftovers = false;
            foreach (var p in Drafted) if (p.Map == map) { leftovers = true; break; }
            bool post = !st.active && t < st.postUntil;
            if (!st.active && st.rescues.Count == 0 && st.captures.Count == 0 && !leftovers && !post) return;

            st.threats.RemoveAll(p => p.Dead || !p.Spawned || p.Downed || p.Map != map);
            DetectOverrides(st);
            if (st.active)
            {
                if (st.threats.Count > 0) Fight(st, pol);
                else
                {
                    var quiet = t - st.lastThreatTick;
                    if (pol.rescueDowned) RescueDowned(st, pol, false);
                    if (pol.captureDowned) CaptureDowned(st, st.seen);
                    if (quiet >= pol.undraftAfterQuietTicks) EndThreat(st, pol);
                }
            }
            else if (post)
            {
                if (pol.rescueDowned) RescueDowned(st, pol, false);
                if (pol.captureDowned) CaptureDowned(st, st.postEnemies);
            }
            else if (leftovers && t - Math.Max(st.createdTick, st.lastThreatTick) >= pol.undraftAfterQuietTicks)
                ReleaseAll(st);
            TrackTasks(st);
        }

        // The player undrafted one of ours, or gave it an order of their own: hands off.
        void DetectOverrides(GovMapState st)
        {
            tmpPawns.Clear();
            foreach (var p in Drafted) if (p.Map == st.map) tmpPawns.Add(p);
            foreach (var p in tmpPawns)
            {
                if (p.Dead || !p.Spawned || p.Downed || p.InMentalState)
                {
                    Drafted.Remove(p);
                    st.assign.Remove(p);
                    continue;
                }
                bool takenOver = !p.Drafted;
                var job = p.CurJob;
                if (!takenOver && job != null && job.playerForced && st.lastJobId.ContainsKey(p) && !st.ourJobs.Contains(job.loadID))
                    takenOver = true;
                if (!takenOver) continue;
                Drafted.Remove(p);
                st.assign.Remove(p);
                Manual.Add(p);
                manualSince[p] = Find.TickManager.TicksGame;
                GovernorEvents.Emit("player_override", st.map, new Dictionary<string, object>
                {
                    ["pawn"] = p.LabelShort, ["drafted"] = p.Drafted,
                });
            }
        }

        // Called by the bridge tools (draft, drafted_move, orders, gizmos) before Claude acts on a
        // colonist: while a threat is active on its map, the governor leaves that pawn alone.
        public static void NotePlayerOrder(Pawn p)
        {
            var g = Instance;
            if (g == null || p?.Map == null || p.Faction != Faction.OfPlayer || !GovernorPolicy.Current.enabled) return;
            if (!g.states.TryGetValue(p.Map.uniqueID, out var st) || !st.active && !g.Drafted.Contains(p)) return;
            bool wasOurs = g.Drafted.Remove(p);
            st.assign.Remove(p);
            g.manualSince[p] = Find.TickManager.TicksGame;
            if (!g.Manual.Add(p) || !wasOurs) return;
            GovernorEvents.Emit("player_override", p.Map, new Dictionary<string, object> { ["pawn"] = p.LabelShort, ["by"] = "tool" });
        }

        // A player-held pawn standing undrafted with no forced job for manualReleaseTicks goes back to the governor.
        bool ReleaseManual(Pawn p, GovernorPolicy pol, int t)
        {
            if (!manualSince.TryGetValue(p, out var since)) { manualSince[p] = since = t; } // after a load: the clock starts now
            if (pol.manualReleaseTicks <= 0 || p.Drafted || (p.CurJob?.playerForced ?? false) || t - since < pol.manualReleaseTicks) return false;
            Manual.Remove(p);
            manualSince.Remove(p);
            GovernorEvents.Emit("manual_released", p.Map, new Dictionary<string, object> { ["pawn"] = p.LabelShort, ["idleTicks"] = t - since });
            return true;
        }

        static bool CanOrder(Pawn p) => p.Spawned && !p.Dead && !p.Downed && p.IsColonistPlayerControlled && !p.InMentalState;

        static bool CanFight(Pawn p) =>
            CanOrder(p) && !p.IsSlave && p.drafter != null && p.drafter.ShowDraftGizmo && !p.Deathresting &&
            !p.WorkTagIsDisabled(WorkTags.Violent) && p.health.capacities.CapableOf(PawnCapacityDefOf.Moving);

        // Blood loss grows by BleedRateTotal per day (HediffGiver_Bleeding: +rate*0.001 every 60 ticks), moderate
        // at 0.30, severe 0.45. One revolver wound bleeds ~1.4/day (+0.06 an hour): the old 0.5 threshold sent
        // every shooter to bed at the first hit (harness D: 3 colonists retreated from one raider and lost).
        static bool NeedsRetreat(Pawn p, GovernorPolicy pol) =>
            p.health.summaryHealth.SummaryHealthPercent < pol.retreatHealthPct ||
            p.health.hediffSet.BleedRateTotal >= pol.retreatBleedRate ||
            (p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.BloodLoss)?.Severity ?? 0f) >= 0.3f;

        // Same yardstick for both sides: people by what they carry (kind combat power assumes a
        // weapon, so unarmed raiders would look as strong as armed ones), animals and mechs by kind.
        public static float PowerOf(Pawn p)
        {
            float h = p.health.summaryHealth.SummaryHealthPercent;
            if (!p.RaceProps.Humanlike) return p.kindDef.combatPower * h;
            var w = p.equipment?.Primary;
            return (w == null ? 25f : w.def.IsRangedWeapon ? 65f : 50f) * h;
        }

        static Pawn Front(GovMapState st)
        {
            Pawn best = null;
            float bd = float.MaxValue;
            foreach (var e in st.threats)
            {
                float d = (e.Position - st.home).LengthHorizontalSquared;
                if (d < bd) { bd = d; best = e; }
            }
            return best;
        }

        static bool InContact(GovMapState st, Pawn p, float r)
        {
            float r2 = r * r;
            foreach (var e in st.threats)
                if (e.Spawned && !e.Downed && (e.Position - p.Position).LengthHorizontalSquared <= r2) return true;
            return false;
        }

        public static bool IsRanged(Pawn p) => p.equipment?.Primary != null && p.equipment.Primary.def.IsRangedWeapon;

        static bool MeleeRole(Pawn p)
        {
            if (!IsRanged(p)) return true;
            int melee = p.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 0;
            int shoot = p.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0;
            return melee - shoot >= 8;
        }

        static float WeaponRange(Pawn p)
        {
            var verbs = p.equipment?.Primary?.def.Verbs;
            return verbs != null && verbs.Count > 0 && verbs[0].range > 0f ? verbs[0].range : 25f;
        }

        bool BusyWithOurTask(GovMapState st, Pawn p)
        {
            var job = p.CurJob;
            if (job == null) return false;
            if (!st.ourJobs.Contains(job.loadID) && !st.rescues.Values.Any(x => x.jobId == job.loadID) && !st.captures.Values.Any(x => x.jobId == job.loadID))
                return false;
            return job.def == JobDefOf.Rescue || job.def == JobDefOf.Capture || job.def == JobDefOf.Equip;
        }

        bool Order(GovMapState st, Pawn p, Job job)
        {
            if (!p.jobs.TryTakeOrderedJob(job, JobTag.Misc)) return false;
            st.ourJobs.Add(job.loadID);
            st.lastJobId[p] = job.loadID;
            st.lastOrderTick[p] = Find.TickManager.TicksGame;
            return true;
        }

        bool OurCurrentJob(GovMapState st, Pawn p) => p.CurJob != null && st.ourJobs.Contains(p.CurJob.loadID);

        void Draft(GovMapState st, Pawn p)
        {
            if (p.Drafted) return;
            if (!st.weapons.ContainsKey(p) && p.equipment?.Primary != null) st.weapons[p] = p.equipment.Primary;
            Drafted.Add(p);
            p.drafter.Drafted = true;
            p.drafter.FireAtWill = true;
        }

        void Undraft(Pawn p)
        {
            Drafted.Remove(p);
            if (p.Drafted && p.drafter != null) p.drafter.Drafted = false;
        }

        // ------------------------------------------------------------------ the fight

        void Fight(GovMapState st, GovernorPolicy pol)
        {
            var map = st.map;
            int t = Find.TickManager.TicksGame;
            st.enemyCentroid = Centroid(st.threats);
            st.enemyFront = Front(st)?.Position ?? st.enemyCentroid;
            st.enemyPower = st.threats.Sum(PowerOf);

            var ranged = new List<Pawn>();
            var melee = new List<Pawn>();
            var nonCombat = new List<Pawn>();
            var downed = new List<Pawn>();
            var manualAble = new List<Pawn>();
            tmpPawns2.Clear();
            tmpPawns2.AddRange(map.mapPawns.FreeColonistsSpawned);
            foreach (var p in tmpPawns2)
            {
                if (p.Dead || !p.Spawned) continue;
                if (p.Downed)
                {
                    downed.Add(p);
                    if (!st.startDowned.Contains(p)) st.ourDowned.Add(p);
                    continue;
                }
                if (Manual.Contains(p))
                {
                    if (!ReleaseManual(p, pol, t)) { if (CanFight(p)) manualAble.Add(p); continue; }
                }
                if (p.Drafted && !Drafted.Contains(p)) { Manual.Add(p); manualSince[p] = t; continue; } // drafted by the player before us
                if (st.retreated.Contains(p)) { KeepRetreated(st, p); continue; }
                if (BusyWithOurTask(st, p)) continue;
                if (!p.Drafted && BusyCarrying(p)) continue; // finishing a rescue/tend/capture first
                if (!CanFight(p))
                {
                    if (CanOrder(p)) nonCombat.Add(p);
                    continue;
                }
                if (NeedsRetreat(p, pol))
                {
                    if (Drafted.Contains(p) || st.assign.ContainsKey(p)) StartRetreat(st, p);
                    else nonCombat.Add(p); // already wounded before deploying: keep out of it
                    continue;
                }
                (MeleeRole(p) ? melee : ranged).Add(p);
            }

            st.ourPower = ranged.Sum(PowerOf) + melee.Sum(PowerOf);
            float ratio = st.enemyPower / Mathf.Max(1f, st.ourPower);
            if (!st.overwhelmed && ratio > pol.defendIndoorsIfRatioAbove) st.overwhelmed = true;
            else if (st.overwhelmed && ratio < pol.defendIndoorsIfRatioAbove * 0.8f) st.overwhelmed = false;

            if (ranged.Count + melee.Count == 0 && manualAble.Count > 0)
            {
                // not a decision: the player has them (or will hand them back after manualReleaseTicks idle)
                if (!st.manualHoldSent)
                {
                    st.manualHoldSent = true;
                    GovernorEvents.Emit("manual_hold", map, new Dictionary<string, object>
                    {
                        ["reason"] = "every able colonist is under manual control",
                        ["pawns"] = manualAble.Select(p => (object)p.LabelShort).ToList(),
                        ["releaseAfterIdleTicks"] = pol.manualReleaseTicks, ["enemyPower"] = Mathf.Round(st.enemyPower),
                    });
                }
            }
            else if (ranged.Count + melee.Count == 0)
            {
                if (!st.noFightersSent)
                {
                    st.noFightersSent = true;
                    GovernorEvents.Emit("decision_needed", map, new Dictionary<string, object>
                    {
                        ["reason"] = "no colonist can fight", ["enemyPower"] = Mathf.Round(st.enemyPower),
                    });
                }
            }
            else if (st.overwhelmed && !st.decisionSent)
            {
                st.decisionSent = true;
                GovernorEvents.Emit("decision_needed", map, new Dictionary<string, object>
                {
                    ["reason"] = "overwhelming threat: falling back to the main building",
                    ["enemyPower"] = Mathf.Round(st.enemyPower), ["ourPower"] = Mathf.Round(st.ourPower),
                    ["ratio"] = (float)Math.Round(ratio, 2),
                });
            }

            // a lone drifter wandering at the map edge kept every colonist drafted for hours (no work, no
            // cleaning): while the nearest enemy is farther than engageDistance from home and we are not
            // outnumbered, nobody is drafted; deployment starts when it comes closer
            float nearestHome = st.threats.Count > 0 ? st.threats.Min(p => (p.Position - st.home).LengthHorizontal) : 0f;
            bool far = pol.engageDistance > 0 && nearestHome > pol.engageDistance && !st.overwhelmed;
            if (far)
            {
                if (!st.farHold)
                {
                    st.farHold = true;
                    var released = Drafted.Where(p => p.Map == map && p.Drafted && !Manual.Contains(p)).ToList();
                    foreach (var p in released) { p.drafter.Drafted = false; Drafted.Remove(p); st.assign.Remove(p); }
                    GovernorEvents.Emit("threat_far", map, new Dictionary<string, object>
                    {
                        ["nearest"] = Mathf.Round(nearestHome), ["engageDistance"] = pol.engageDistance,
                        ["released"] = released.Select(p => (object)p.LabelShort).ToList(),
                    });
                }
            }
            else
            {
                st.farHold = false;
                if (pol.autoDraft && ranged.Count + melee.Count > 0) Deploy(st, pol, ranged, melee);
            }
            if (far) Skirmish(st, pol, ranged, melee, nonCombat);
            if (pol.shelter && !far) Shelter(st, nonCombat);
            if (pol.rescueDowned) RescueDowned(st, pol, true);
        }

        void Deploy(GovMapState st, GovernorPolicy pol, List<Pawn> ranged, List<Pawn> melee)
        {
            var map = st.map;
            int t = Find.TickManager.TicksGame;
            foreach (var k in st.badCells.Where(kv => kv.Value < t).Select(kv => kv.Key).ToList()) st.badCells.Remove(k);

            // flanked: an enemy shooter within 3 cells that this cell gives (almost) no cover against
            // (melee attackers that close are a brawl, not a flank: moving away would not help)
            bool flanked = false;
            foreach (var kv in st.assign)
            {
                if (kv.Value.role != GovRole.Ranged) continue;
                if (FireNear(kv.Value.cell, map))
                {
                    st.badCells[kv.Value.cell] = t + 900; // drafted pawns beat adjacent fires instead of shooting
                    flanked = true;
                    continue;
                }
                foreach (var e in st.threats)
                {
                    if ((e.Position - kv.Value.cell).LengthHorizontalSquared > 9 || !IsRanged(e)) continue;
                    if (CoverUtility.CalculateOverallBlockChance(kv.Value.cell, e.Position, map) < 0.2f)
                    {
                        st.badCells[kv.Value.cell] = t + 900;
                        flanked = true;
                        break;
                    }
                }
            }

            bool roster = ranged.Any(p => !st.assign.TryGetValue(p, out var a) || a.role != GovRole.Ranged) ||
                          melee.Any(p => !st.assign.TryGetValue(p, out var a) || a.role != GovRole.Melee) ||
                          st.assign.Keys.Any(p => !ranged.Contains(p) && !melee.Contains(p));
            // close combat: someone is within 12 cells of our line. Then nobody walks around any more
            // (moving pawns do not shoot); they hold and fight where they are.
            st.closeCombat = false;
            foreach (var e in st.threats)
            {
                if (!e.Spawned || e.Downed) continue;
                foreach (var kv in st.assign)
                    if ((e.Position - kv.Value.cell).LengthHorizontalSquared <= 144 || (e.Position - kv.Key.Position).LengthHorizontalSquared <= 144)
                    { st.closeCombat = true; break; }
                if (st.closeCombat) break;
            }
            // outranged: shooters on station with nobody in range and line of fire for a while
            bool outranged = false;
            foreach (var p in ranged)
            {
                if (!st.assign.TryGetValue(p, out var a) || p.Position != a.cell) { st.outOfRangeTicks.Remove(p); continue; }
                float r = WeaponRange(p), r2 = r * r;
                bool any = false;
                foreach (var e in st.threats)
                    if (e.Spawned && !e.Downed && (e.Position - p.Position).LengthHorizontalSquared <= r2 &&
                        GovernorPositions.HasLineOfFire(p.Position, e.Position, map)) { any = true; break; }
                st.outOfRangeTicks[p] = any ? 0 : (st.outOfRangeTicks.TryGetValue(p, out var n) ? n : 0) + 120;
                if (st.outOfRangeTicks[p] >= 480) outranged = true;
            }
            if (outranged && !st.closeCombat && st.rangePull < 1f) st.rangePull = 3f;
            bool moved = !st.closeCombat && st.planCentroid.IsValid && (st.enemyCentroid - st.planCentroid).LengthHorizontal > 12f && t - st.lastPlanTick >= 300;
            bool replan = st.assign.Count == 0 || roster || moved || st.overwhelmed != st.planOverwhelmed ||
                          st.rangePull != st.planRangePull || flanked && t - st.lastPlanTick >= 250;
            if (replan) Plan(st, pol, ranged, melee);

            if (st.firstDeployTick < 0) st.firstDeployTick = t;
            foreach (var p in ranged.Concat(melee))
            {
                if (!st.assign.TryGetValue(p, out var a)) continue;
                Draft(st, p);
                if (!st.lastJobId.ContainsKey(p))
                {
                    // managed from now on; a forced job it already has (e.g. ours, from before a load) is adopted
                    st.lastJobId[p] = -1;
                    if (p.CurJob != null && p.CurJob.playerForced) st.ourJobs.Add(p.CurJob.loadID);
                }
                if (a.role == GovRole.Melee && Engage(st, pol, p, ranged)) continue;
                if (p.Position == a.cell)
                {
                    if (a.arrivedTick < 0) a.arrivedTick = t;
                    continue;
                }
                if (InContact(st, p, 6f)) continue; // fight where you stand
                MoveTo(st, p, a.cell);
            }
        }

        // Vanilla's drafted auto-attack swings the gun butt at an adjacent enemy. A shot at point
        // blank does more: order the shooter to fire at it (as a player's attack click would).
        // far from home is not far from everyone: in run15 a cougar hunted Tomas 90 cells out while the
        // far hold kept the whole colony working, and he died alone. Colonists within skirmishRadius of
        // an enemy fight it (ranged shoot, melee charge); unarmed ones there run home; the rest keep working.
        void Skirmish(GovMapState st, GovernorPolicy pol, List<Pawn> ranged, List<Pawn> melee, List<Pawn> nonCombat)
        {
            if (pol.skirmishRadius <= 0f || st.threats.Count == 0 || !pol.autoDraft) return;
            float r2 = pol.skirmishRadius * pol.skirmishRadius;
            Pawn Near(Pawn p) => st.threats.Where(e => e.Spawned && !e.Downed && (e.Position - p.Position).LengthHorizontalSquared <= r2)
                .OrderBy(e => (e.Position - p.Position).LengthHorizontalSquared).FirstOrDefault();
            int t = Find.TickManager.TicksGame;
            var fighters = new List<object>();
            foreach (var p in ranged.Concat(melee))
            {
                var e = Near(p);
                if (e == null) continue;
                var job = p.CurJob;
                if (p.Drafted && job != null && (job.def == JobDefOf.AttackStatic || job.def == JobDefOf.AttackMelee) && job.targetA.Thing == e) continue;
                if (st.lastOrderTick.TryGetValue(p, out var lt) && t - lt < 120 && p.Drafted) continue;
                Draft(st, p);
                bool meleeRole = melee.Contains(p) || !IsRanged(p);
                var attack = JobMaker.MakeJob(meleeRole ? JobDefOf.AttackMelee : JobDefOf.AttackStatic, e);
                if (!meleeRole) attack.endIfCantShootTargetFromCurPos = false;
                attack.killIncappedTarget = false;
                if (Order(st, p, attack)) fighters.Add(p.LabelShort);
            }
            var runners = nonCombat.Where(p => Near(p) != null).ToList();
            if (runners.Count > 0 && pol.shelter) Shelter(st, runners);
            if (fighters.Count > 0 && !st.skirmishSent)
            {
                st.skirmishSent = true;
                GovernorEvents.Emit("skirmish", st.map, new Dictionary<string, object>
                {
                    ["fighters"] = fighters, ["enemy"] = st.threats.Select(e => (object)e.KindLabel).Distinct().ToList(),
                    ["runners"] = runners.Select(p => (object)p.LabelShort).ToList(),
                });
            }
        }

        void PointBlank(GovMapState st)
        {
            foreach (var kv in st.assign)
            {
                var p = kv.Key;
                if (kv.Value.role != GovRole.Ranged || !p.Spawned || p.Downed || !p.Drafted || !Drafted.Contains(p) || !IsRanged(p)) continue;
                if (p.stances.FullBodyBusy) continue;
                var job = p.CurJob;
                if (job != null && job.def == JobDefOf.AttackStatic && job.targetA.Thing is Pawn cur && cur.Spawned && !cur.Downed) continue;
                if (job != null && (job.def == JobDefOf.Rescue || job.def == JobDefOf.Capture)) continue;
                Pawn target = null;
                foreach (var e in st.threats)
                    if (e.Spawned && !e.Downed && GovernorPositions.Cheb(e.Position, p.Position) <= 1) { target = e; break; }
                if (target == null) continue;
                var attack = JobMaker.MakeJob(JobDefOf.AttackStatic, target);
                attack.endIfCantShootTargetFromCurPos = true;
                Order(st, p, attack);
            }
        }

        static bool FireNear(IntVec3 c, Map map)
        {
            for (int i = 0; i < 9; i++)
            {
                var n = c + GenAdj.AdjacentCellsAndInside[i];
                if (n.InBounds(map) && n.ContainsStaticFire(map)) return true;
            }
            return false;
        }

        void Plan(GovMapState st, GovernorPolicy pol, List<Pawn> ranged, List<Pawn> melee)
        {
            var map = st.map;
            int t = Find.TickManager.TicksGame;
            var enemy = st.enemyFront.IsValid ? st.enemyFront : st.enemyCentroid;
            var home = st.home;
            bool modeChanged = st.overwhelmed != st.planOverwhelmed || st.rangePull != st.planRangePull;
            var polStations = st.overwhelmed ? null : GovernorPositions.PolicyStations(map, pol);
            float range = ranged.Count > 0 ? ranged.Average(WeaponRange) : 25f;
            List<GovernorPositions.Cand> cands;
            string mode;
            if (polStations != null)
            {
                mode = "policy";
                cands = polStations.Where(c => c.InBounds(map) && c.Standable(map) && !st.badCells.ContainsKey(c))
                    .Select(c => new GovernorPositions.Cand { cell = c, score = 100f }).ToList();
            }
            else
            {
                mode = st.overwhelmed ? "fallback" : st.rangePull > 1f ? "auto, closing range" : "auto";
                cands = GovernorPositions.FiringCells(map, home, enemy, st.overwhelmed, range, st.rangePull);
                if (cands.Count == 0 && st.overwhelmed)
                {
                    mode = "auto";
                    cands = GovernorPositions.FiringCells(map, home, enemy, false, range, st.rangePull);
                }
                cands.RemoveAll(c => st.badCells.ContainsKey(c.cell));
            }
            float best = cands.Count > 0 ? cands[0].score : 0f;

            var next = new Dictionary<Pawn, GovAssignment>();
            var taken = new List<IntVec3>();
            // keep good stations (hysteresis)
            if (!modeChanged)
                foreach (var p in ranged)
                {
                    if (!st.assign.TryGetValue(p, out var old) || old.role != GovRole.Ranged || st.badCells.ContainsKey(old.cell)) continue;
                    bool keep = polStations != null
                        ? polStations.Contains(old.cell)
                        : GovernorPositions.ScoreOf(map, home, enemy, old.cell, st.overwhelmed, range, st.rangePull) >= best - Mathf.Max(10f, Mathf.Abs(best) * 0.2f);
                    if (!keep) continue;
                    next[p] = old;
                    taken.Add(old.cell);
                }
            var pool = polStations != null
                ? cands.Where(c => !taken.Contains(c.cell)).ToList()
                : GovernorPositions.Spaced(cands, Math.Max(ranged.Count * 3, 8), taken);
            var pairs = new List<(Pawn p, GovernorPositions.Cand c, float cost)>();
            foreach (var p in ranged)
                if (!next.ContainsKey(p))
                    foreach (var c in pool)
                        pairs.Add((p, c, polStations != null ? (p.Position - c.cell).LengthHorizontal
                                                             : (p.Position - c.cell).LengthHorizontal * 0.5f - c.score));
            pairs.Sort((a, b) => a.cost.CompareTo(b.cost));
            foreach (var (p, c, _) in pairs)
            {
                if (next.ContainsKey(p) || taken.Contains(c.cell)) continue;
                if (polStations == null && taken.Any(x => GovernorPositions.Cheb(x, c.cell) < 2)) continue;
                if (!p.CanReach(c.cell, PathEndMode.OnCell, Danger.Deadly)) continue;
                next[p] = Reuse(st, p, c.cell, GovRole.Ranged);
                taken.Add(c.cell);
            }
            // more shooters than cells: stand near home, toward the enemy
            foreach (var p in ranged)
            {
                if (next.ContainsKey(p)) continue;
                var c = CellFinder.StandableCellNear(home, map, 8f, x => !taken.Contains(x) && x.GetDoor(map) == null);
                if (!c.IsValid) continue;
                next[p] = Reuse(st, p, c, GovRole.Ranged);
                taken.Add(c);
            }
            // melee: hold just behind the shooters
            var dir = new Vector3(enemy.x - home.x, 0f, enemy.z - home.z);
            if (dir.sqrMagnitude < 1f) dir = new Vector3(1f, 0f, 0f);
            dir.Normalize();
            var shooterCells = next.Where(kv => kv.Value.role == GovRole.Ranged).Select(kv => kv.Value.cell).ToList();
            IntVec3 anchor = shooterCells.Count > 0
                ? new IntVec3((int)shooterCells.Average(c => c.x), 0, (int)shooterCells.Average(c => c.z))
                : home + new IntVec3(Mathf.RoundToInt(dir.x * 4f), 0, Mathf.RoundToInt(dir.z * 4f));
            var hold = (anchor - new IntVec3(Mathf.RoundToInt(dir.x * 2f), 0, Mathf.RoundToInt(dir.z * 2f))).ClampInsideMap(map);
            foreach (var p in melee)
            {
                if (!modeChanged && st.assign.TryGetValue(p, out var old) && old.role == GovRole.Melee &&
                    (old.cell - hold).LengthHorizontal <= 4f && !taken.Contains(old.cell))
                {
                    next[p] = old;
                    taken.Add(old.cell);
                    continue;
                }
                var c = CellFinder.StandableCellNear(hold, map, 5f, x => !taken.Contains(x) && x.GetDoor(map) == null);
                if (!c.IsValid) c = p.Position;
                next[p] = Reuse(st, p, c, GovRole.Melee);
                taken.Add(c);
            }

            bool changed = next.Count != st.assign.Count || next.Any(kv => !st.assign.TryGetValue(kv.Key, out var o) || o.cell != kv.Value.cell || o.role != kv.Value.role);
            st.assign.Clear();
            foreach (var kv in next) st.assign[kv.Key] = kv.Value;
            st.planCentroid = st.enemyCentroid;
            st.planOverwhelmed = st.overwhelmed;
            st.planRangePull = st.rangePull;
            st.lastPlanTick = t;
            st.planMode = mode;
            st.shelterCells = null;
            if (changed)
                GovernorEvents.Emit("governor_deploy", map, new Dictionary<string, object>
                {
                    ["mode"] = mode,
                    ["enemyAt"] = Pos(enemy),
                    ["assignments"] = st.assign.Select(kv => (object)new Dictionary<string, object>
                    {
                        ["pawn"] = kv.Key.LabelShort, ["role"] = kv.Value.role == GovRole.Ranged ? "ranged" : "melee",
                        ["cell"] = Pos(kv.Value.cell),
                    }).ToList(),
                });
        }

        static GovAssignment Reuse(GovMapState st, Pawn p, IntVec3 c, GovRole role) =>
            st.assign.TryGetValue(p, out var old) && old.cell == c && old.role == role ? old : new GovAssignment { cell = c, role = role };

        void MoveTo(GovMapState st, Pawn p, IntVec3 cell)
        {
            var job = p.CurJob;
            if (job != null && job.def == JobDefOf.Goto && job.targetA.Cell == cell) return;
            if (!p.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) return;
            Order(st, p, JobMaker.MakeJob(JobDefOf.Goto, cell));
        }

        // Melee fighters attack enemies that get within engageMeleeRadius of a shooter.
        bool Engage(GovMapState st, GovernorPolicy pol, Pawn p, List<Pawn> shooters)
        {
            float r = pol.engageMeleeRadius, r2 = r * r;
            Pawn target = null;
            float bestD = float.MaxValue;
            foreach (var e in st.threats)
            {
                if (!e.Spawned || e.Downed) continue;
                bool near = false;
                if (shooters.Count > 0)
                {
                    foreach (var s in shooters)
                        if ((e.Position - s.Position).LengthHorizontalSquared <= r2) { near = true; break; }
                }
                else if (st.assign.TryGetValue(p, out var a))
                    near = (e.Position - a.cell).LengthHorizontalSquared <= r2 * 2.25f;
                if (!near) continue;
                float d = (e.Position - p.Position).LengthHorizontalSquared;
                if (d < bestD) { bestD = d; target = e; }
            }
            if (target == null) return false;
            var job = p.CurJob;
            if (job != null && job.def == JobDefOf.AttackMelee && job.targetA.Thing == target) return true;
            if (!p.CanReach(target, PathEndMode.Touch, Danger.Deadly)) return false;
            return Order(st, p, JobMaker.MakeJob(JobDefOf.AttackMelee, target));
        }

        // ------------------------------------------------------------------ retreat, shelter

        void StartRetreat(GovMapState st, Pawn p)
        {
            st.assign.Remove(p);
            Undraft(p);
            st.retreated.Add(p);
            var to = SendToSafety(st, p);
            GovernorEvents.Emit("retreat", st.map, new Dictionary<string, object>
            {
                ["pawn"] = p.LabelShort,
                ["healthPct"] = (float)Math.Round(p.health.summaryHealth.SummaryHealthPercent * 100f, 1),
                ["bleedRate"] = (float)Math.Round(p.health.hediffSet.BleedRateTotal, 2),
                ["to"] = to,
            });
        }

        void KeepRetreated(GovMapState st, Pawn p)
        {
            if (!CanOrder(p) || p.InBed()) return;
            var job = p.CurJob;
            if (job != null && (job.def == JobDefOf.LayDown || job.def == JobDefOf.TendPatient)) return;
            if (OurCurrentJob(st, p)) return;
            if (st.lastOrderTick.TryGetValue(p, out var last) && Find.TickManager.TicksGame - last < 300) return;
            if (GovernorPositions.Indoor(p.Position, st.map)) return;
            SendToSafety(st, p);
        }

        string SendToSafety(GovMapState st, Pawn p)
        {
            if (p.InBed()) return "bed";
            var bed = RestUtility.FindBedFor(p, p, checkSocialProperness: false);
            if (bed != null && Order(st, p, JobMaker.MakeJob(JobDefOf.LayDown, bed))) return "bed";
            var cell = NearestShelterCell(st, p, null);
            if (cell.IsValid && Order(st, p, JobMaker.MakeJob(JobDefOf.Goto, cell))) return "indoors";
            return "nowhere (no bed or roofed room reachable)";
        }

        IntVec3 NearestShelterCell(GovMapState st, Pawn p, HashSet<IntVec3> used)
        {
            if (st.shelterCells == null)
                st.shelterCells = GovernorPositions.ShelterCells(st.map, st.home.IsValid ? st.home : p.Position,
                    st.enemyCentroid.IsValid ? st.enemyCentroid : st.map.Center, 60);
            IntVec3 best = IntVec3.Invalid;
            float bestD = float.MaxValue;
            foreach (var c in st.shelterCells)
            {
                if (used != null && used.Contains(c)) continue;
                float d = (c - p.Position).LengthHorizontalSquared;
                if (d < bestD && p.CanReach(c, PathEndMode.OnCell, Danger.Deadly)) { bestD = d; best = c; }
                if (bestD < 4f) break;
            }
            return best;
        }

        // A doctor tending a patient who is not bleeding is not busy: the harness's raid A came right after the
        // baseline fight and Kyle "TendPatient" (the wounded from the last fight, one wound after another) sat
        // out the whole raid, 2 of 3 fighters drafted, the line fell back and lost. Only an emergency waits.
        static bool BusyCarrying(Pawn p)
        {
            var d = p.CurJobDef;
            if (d == JobDefOf.TendPatient)
                return p.CurJob.targetA.Thing is Pawn pt && pt != p && pt.health.hediffSet.BleedRateTotal >= 0.1f;
            return d == JobDefOf.Rescue || d == JobDefOf.Capture || p.carryTracker?.CarriedThing is Pawn;
        }

        static bool BusyWithCare(Pawn p)
        {
            var d = p.CurJobDef;
            return d == JobDefOf.Rescue || d == JobDefOf.Capture || d == JobDefOf.TendPatient || d == JobDefOf.LayDown ||
                   d == JobDefOf.Equip || p.InBed() || p.carryTracker?.CarriedThing is Pawn;
        }

        void Shelter(GovMapState st, List<Pawn> pawns)
        {
            int t = Find.TickManager.TicksGame;
            var used = new HashSet<IntVec3>();
            foreach (var p in pawns)
            {
                if (!CanOrder(p) || BusyWithCare(p)) continue;
                if (GovernorPositions.Indoor(p.Position, st.map))
                {
                    // ones we brought in stay put until it is quiet; the others keep working indoors
                    if (st.sheltered.Contains(p) && !OurCurrentJob(st, p))
                    {
                        Order(st, p, JobMaker.MakeJob(JobDefOf.Wait, 600, checkOverrideOnExpiry: true));
                    }
                    continue;
                }
                if (OurCurrentJob(st, p) && st.lastOrderTick.TryGetValue(p, out var last) && t - last < 240) continue;
                var cell = NearestShelterCell(st, p, used);
                if (!cell.IsValid) continue;
                used.Add(cell);
                if (Order(st, p, JobMaker.MakeJob(JobDefOf.Goto, cell))) st.sheltered.Add(p);
            }
        }

        // ------------------------------------------------------------------ rescue, capture

        Pawn PickHelper(GovMapState st, Pawn patient, Func<Pawn, bool> ok)
        {
            Pawn best = null;
            float bestCost = float.MaxValue;
            tmpPawns.Clear();
            tmpPawns.AddRange(st.map.mapPawns.FreeColonistsSpawned);
            foreach (var r in tmpPawns)
            {
                if (r == patient || !CanOrder(r) || Manual.Contains(r)) continue;
                if (st.retreated.Contains(r) && (st.threats.Count > 0 || NeedsRetreat(r, GovernorPolicy.Current))) continue;
                if (!r.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) continue;
                if (BusyWithOurTask(st, r) || r.carryTracker?.CarriedThing != null) continue;
                float cost = (r.Position - patient.Position).LengthHorizontal;
                if (st.active && st.threats.Count > 0 && st.assign.ContainsKey(r)) cost += 30f; // prefer non-fighters mid-fight
                if (cost >= bestCost || !ok(r)) continue;
                best = r;
                bestCost = cost;
            }
            return best;
        }

        bool TaskRunning(Dictionary<Pawn, GovTask> tasks, Pawn target, JobDef def)
        {
            if (!tasks.TryGetValue(target, out var task)) return false;
            var job = task.pawn.CurJob;
            if (task.pawn.Spawned && job != null && job.def == def && job.targetA.Thing == target) return true;
            if (task.pawn.carryTracker?.CarriedThing == target) return true;
            return Find.TickManager.TicksGame - task.tick < 120; // give a fresh order a moment to start
        }

        void RescueDowned(GovMapState st, GovernorPolicy pol, bool fighting)
        {
            tmpPawns2.Clear();
            tmpPawns2.AddRange(st.map.mapPawns.FreeColonistsSpawned);
            foreach (var patient in tmpPawns2)
            {
                if (!patient.Downed || patient.Dead || !patient.Spawned || patient.InBed()) continue;
                if (TaskRunning(st.rescues, patient, JobDefOf.Rescue)) continue;
                if (fighting && st.threats.Any(e => (e.Position - patient.Position).LengthHorizontalSquared < 100)) continue;
                var rescuer = PickHelper(st, patient, r => HealthAIUtility.CanRescueNow(r, patient, forced: true));
                if (rescuer == null) continue;
                var bed = RestUtility.FindBedFor(patient, rescuer, checkSocialProperness: false)
                          ?? RestUtility.FindBedFor(patient, rescuer, checkSocialProperness: false, ignoreOtherReservations: true);
                if (bed == null)
                {
                    if (st.warnedNoBed.Add(patient))
                        GovernorEvents.Emit("decision_needed", st.map, new Dictionary<string, object>
                        {
                            ["reason"] = "downed colonist and no free bed to rescue them to", ["pawn"] = patient.LabelShort,
                        });
                    continue;
                }
                if (!fighting && Drafted.Contains(rescuer)) { st.assign.Remove(rescuer); Undraft(rescuer); }
                var job = JobMaker.MakeJob(JobDefOf.Rescue, patient, bed);
                job.count = 1;
                if (Order(st, rescuer, job))
                    st.rescues[patient] = new GovTask { pawn = rescuer, tick = Find.TickManager.TicksGame, jobId = job.loadID };
            }
        }

        void CaptureDowned(GovMapState st, IEnumerable<Pawn> enemies)
        {
            foreach (var e in enemies)
            {
                if (e.Dead || !e.Spawned || !e.Downed || e.Map != st.map || e.IsPrisonerOfColony || e.Fogged()) continue;
                if (e.Faction == Faction.OfPlayer || !e.CanBeCaptured()) continue;
                if (TaskRunning(st.captures, e, JobDefOf.Capture)) continue;
                var capturer = PickHelper(st, e, c => HealthAIUtility.CanRescueNow(c, e, forced: true));
                if (capturer == null) continue;
                var bed = RestUtility.FindBedFor(e, capturer, checkSocialProperness: false, ignoreOtherReservations: false, GuestStatus.Prisoner)
                          ?? RestUtility.FindBedFor(e, capturer, checkSocialProperness: false, ignoreOtherReservations: true, GuestStatus.Prisoner);
                if (bed == null) continue; // no free prisoner bed: leave them (counted in threat_end)
                if (Drafted.Contains(capturer)) { st.assign.Remove(capturer); Undraft(capturer); }
                var job = JobMaker.MakeJob(JobDefOf.Capture, e, bed);
                job.count = 1;
                if (Order(st, capturer, job))
                    st.captures[e] = new GovTask { pawn = capturer, tick = Find.TickManager.TicksGame, jobId = job.loadID };
            }
        }

        void TrackTasks(GovMapState st)
        {
            int t = Find.TickManager.TicksGame;
            foreach (var patient in st.rescues.Keys.ToList())
            {
                var task = st.rescues[patient];
                if (patient.InBed() && patient.Spawned)
                {
                    st.rescuedCount++;
                    GovernorEvents.Emit("rescued", st.map, new Dictionary<string, object>
                    {
                        ["pawn"] = patient.LabelShort, ["by"] = task.pawn.LabelShort, ["bed"] = Pos(patient.Position),
                    });
                    st.rescues.Remove(patient);
                }
                else if (patient.Dead || patient.Destroyed || !patient.Downed || t - task.tick > 5000) st.rescues.Remove(patient);
            }
            foreach (var e in st.captures.Keys.ToList())
            {
                var task = st.captures[e];
                if (e.IsPrisonerOfColony)
                {
                    st.capturedCount++;
                    GovernorEvents.Emit("captured", st.map, new Dictionary<string, object>
                    {
                        ["pawn"] = e.LabelShort, ["faction"] = e.Faction?.Name, ["by"] = task.pawn.LabelShort,
                    });
                    st.captures.Remove(e);
                }
                else if (e.Dead || e.Destroyed || !e.Downed || t - task.tick > 5000) st.captures.Remove(e);
            }
        }

        // ------------------------------------------------------------------ the end

        void EndThreat(GovMapState st, GovernorPolicy pol)
        {
            var map = st.map;
            int t = Find.TickManager.TicksGame;
            ReleaseAll(st);
            foreach (var p in st.sheltered)
            {
                if (!p.Spawned || p.Map != map || p.Dead) continue;
                var job = p.CurJob;
                if (job != null && st.ourJobs.Contains(job.loadID) && (job.def == JobDefOf.Wait || job.def == JobDefOf.Goto))
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }
            int reequipped = ReEquip(st);
            int unforbidden = UnforbidLoot(st);
            int killed = 0, downedE = 0, fled = 0, captured = 0, remaining = 0;
            foreach (var e in st.seen)
            {
                if (e.Dead) killed++;
                else if (e.IsPrisonerOfColony) captured++;
                else if (!e.Spawned || e.Map != map) fled++;
                else if (e.Downed) downedE++;
                else remaining++;
            }
            int died = st.startColonists.Count(p => p.Dead);
            GovernorEvents.Emit("threat_end", map, new Dictionary<string, object>
            {
                ["durationTicks"] = st.lastThreatTick - st.startTick,
                ["enemies"] = new Dictionary<string, object>
                {
                    ["total"] = st.seen.Count, ["killed"] = killed, ["downed"] = downedE, ["captured"] = captured,
                    ["fled"] = fled, ["stillHereNotThreat"] = remaining,
                },
                ["colonists"] = new Dictionary<string, object>
                {
                    ["downed"] = st.ourDowned.Select(p => (object)p.LabelShort).ToList(),
                    ["died"] = died, ["retreated"] = st.retreated.Select(p => (object)p.LabelShort).ToList(),
                },
                ["rescued"] = st.rescuedCount,
                ["reequipped"] = reequipped,
                ["unforbidden"] = unforbidden,
                ["capturing"] = st.captures.Count,
                ["rescuing"] = st.rescues.Count,
            });
            Manual.RemoveWhere(p => p == null || !p.Spawned || p.Map == map);
            var downedEnemies = st.seen.Where(e => !e.Dead && e.Spawned && e.Downed && e.Map == map && !e.IsPrisonerOfColony).ToList();
            var rescues = st.rescues.ToList();
            var captures = st.captures.ToList();
            var jobs = st.ourJobs.ToList();
            st.ResetThreat();
            foreach (var kv in rescues) st.rescues[kv.Key] = kv.Value;
            foreach (var kv in captures) st.captures[kv.Key] = kv.Value;
            foreach (var j in jobs) st.ourJobs.Add(j); // rescue/capture jobs still running are ours
            st.active = false;
            st.lastThreatTick = t;
            st.postEnemies.Clear();
            st.postEnemies.AddRange(downedEnemies);
            st.postUntil = t + 2500;
        }

        // Undraft every pawn the governor drafted on this map, except ones carrying out a rescue or capture.
        void ReleaseAll(GovMapState st)
        {
            tmpPawns.Clear();
            foreach (var p in Drafted) if (p.Map == st.map) tmpPawns.Add(p);
            foreach (var p in tmpPawns)
            {
                if (BusyWithOurTask(st, p)) continue;
                st.assign.Remove(p);
                Undraft(p);
            }
        }

        int ReEquip(GovMapState st)
        {
            int n = 0;
            tmpPawns2.Clear();
            tmpPawns2.AddRange(st.map.mapPawns.FreeColonistsSpawned);
            foreach (var p in tmpPawns2)
            {
                if (p.equipment == null || p.equipment.Primary != null || Manual.Contains(p)) continue;
                st.weapons.TryGetValue(p, out var w);
                w = w ?? p.mindState?.droppedWeapon;
                if (w == null || w.Destroyed || !w.Spawned || w.Map != st.map || w.Fogged() || w.IsBurning()) continue;
                w.SetForbidden(false, warnOnFail: false);
                if (!CanOrder(p) || p.Drafted || !p.CanReserveAndReach(w, PathEndMode.ClosestTouch, Danger.Deadly)) continue;
                if (Order(st, p, JobMaker.MakeJob(JobDefOf.Equip, w))) n++;
            }
            return n;
        }

        void ForEachLootCandidate(GovMapState st, Action<Thing> act)
        {
            var map = st.map;
            var homeArea = map.areaManager.Home;
            tmpThings.Clear();
            tmpThings.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver));
            foreach (var t in tmpThings)
            {
                if (!t.Spawned || t is Corpse || t is Pawn || t.def.category != ThingCategory.Item) continue;
                bool near = homeArea != null && homeArea[t.Position] || st.home.IsValid && (t.Position - st.home).LengthHorizontalSquared <= 400;
                if (!near || t.Fogged()) continue;
                act(t);
            }
            tmpThings.Clear();
        }

        // Unforbid items near the base that became forbidden during the fight (dropped weapons,
        // raider gear); items the player had forbidden before it started stay forbidden.
        int UnforbidLoot(GovMapState st)
        {
            if (st.forbiddenSnapshot == null) return 0;
            int n = 0;
            ForEachLootCandidate(st, t =>
            {
                if (n >= 500 || !t.IsForbidden(Faction.OfPlayer) || st.forbiddenSnapshot.Contains(t.thingIDNumber)) return;
                t.SetForbidden(false, warnOnFail: false);
                n++;
            });
            return n;
        }

        // ------------------------------------------------------------------ status for the tool

        public Dictionary<string, object> Status(GovMapState st)
        {
            int t = Find.TickManager.TicksGame;
            var r = new Dictionary<string, object>
            {
                ["map"] = GovernorEvents.MapLabel(st.map),
                ["mapId"] = st.map.uniqueID,
                ["stationKey"] = GovernorPositions.StationKey(st.map),
                ["threat"] = st.active ? (st.threats.Count > 0 ? "active" : "quiet, waiting to stand down") : "none",
            };
            if (st.active)
            {
                r["enemies"] = st.threats.Count;
                r["enemyPower"] = Mathf.Round(st.enemyPower);
                r["ourPower"] = Mathf.Round(st.ourPower);
                r["overwhelmed"] = st.overwhelmed;
                r["enemyCentroid"] = Pos(st.enemyCentroid);
                r["enemyFront"] = Pos(st.enemyFront);
                r["closeCombat"] = st.closeCombat;
                r["home"] = Pos(st.home);
                r["mode"] = st.planMode;
                r["sinceTicks"] = t - st.startTick;
                r["quietTicks"] = st.threats.Count > 0 ? 0 : t - st.lastThreatTick;
                r["assignments"] = st.assign.Select(kv => (object)new Dictionary<string, object>
                {
                    ["pawn"] = kv.Key.LabelShort, ["role"] = kv.Value.role == GovRole.Ranged ? "ranged" : "melee",
                    ["cell"] = Pos(kv.Value.cell), ["at"] = Pos(kv.Key.Position), ["inPosition"] = kv.Key.Position == kv.Value.cell,
                    ["drafted"] = kv.Key.Drafted,
                }).ToList();
                r["sheltered"] = st.sheltered.Select(p => (object)p.LabelShort).ToList();
                r["retreated"] = st.retreated.Select(p => (object)p.LabelShort).ToList();
            }
            r["manual"] = Manual.Where(p => p.Map == st.map).Select(p => (object)p.LabelShort).ToList();
            if (st.rescues.Count > 0) r["rescuing"] = st.rescues.Select(kv => (object)(kv.Value.pawn.LabelShort + " -> " + kv.Key.LabelShort)).ToList();
            if (st.captures.Count > 0) r["capturing"] = st.captures.Select(kv => (object)(kv.Value.pawn.LabelShort + " -> " + kv.Key.LabelShort)).ToList();
            return r;
        }
    }
}
