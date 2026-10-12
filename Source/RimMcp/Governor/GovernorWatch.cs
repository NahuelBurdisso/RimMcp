using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;

namespace RimMcp
{
    // Non-combat events by polling (no patches): new letters, quest offers, important alerts,
    // fires in the home area, colonists downed / dead / breaking. Baseline() records what is
    // already there so enabling the governor or loading a save does not flood the stream.
    public class GovernorWatch
    {
        int lastLetterId = int.MinValue;
        int lastQuestId = int.MinValue;
        readonly Dictionary<Pawn, ColonistSeen> colonists = new Dictionary<Pawn, ColonistSeen>();
        readonly HashSet<string> alerts = new HashSet<string>();
        readonly Dictionary<string, string> alertLabels = new Dictionary<string, string>();
        readonly Dictionary<int, int> firesByMap = new Dictionary<int, int>();
        readonly List<Pawn> tmpPawns = new List<Pawn>();
        readonly List<Pawn> tmpGone = new List<Pawn>();

        class ColonistSeen
        {
            public bool downed;
            public MentalStateDef mental;
            public Map map;
        }

        static readonly FieldInfo activeAlertsField =
            typeof(AlertsReadout).GetField("activeAlerts", BindingFlags.Instance | BindingFlags.NonPublic);

        public void Baseline()
        {
            var letters = Find.LetterStack.LettersListForReading;
            lastLetterId = letters.Count > 0 ? letters.Max(l => l.ID) : -1;
            var quests = Find.QuestManager.QuestsListForReading;
            lastQuestId = quests.Count > 0 ? quests.Max(q => q.id) : -1;
            colonists.Clear();
            foreach (var map in Find.Maps)
            {
                tmpPawns.Clear();
                tmpPawns.AddRange(map.mapPawns.FreeColonistsSpawned);
                foreach (var p in tmpPawns)
                    colonists[p] = new ColonistSeen { downed = p.Downed, mental = p.MentalStateDef, map = map };
            }
            alerts.Clear();
            foreach (var al in ImportantAlerts()) alerts.Add(al.GetType().Name);
            firesByMap.Clear();
            foreach (var map in Find.Maps) if (map.IsPlayerHome) firesByMap[map.uniqueID] = HomeFires(map, out _);
        }

        // ~every 60 ticks
        public void Fast()
        {
            foreach (var l in Find.LetterStack.LettersListForReading)
            {
                if (l.ID <= lastLetterId) continue;
                lastLetterId = l.ID;
                GovernorEvents.Emit("letter_received", l.lookTargets != null && l.lookTargets.IsValid ? l.lookTargets.PrimaryTarget.Map : null, new Dictionary<string, object>
                {
                    ["id"] = l.ID, ["label"] = l.Label.ToString().StripTags(), ["letterType"] = l.def.defName,
                    ["choice"] = l is ChoiceLetter && !(l is StandardLetter),
                });
            }
            foreach (var map in Find.Maps)
            {
                tmpPawns.Clear();
                tmpPawns.AddRange(map.mapPawns.FreeColonistsSpawned);
                foreach (var p in tmpPawns)
                {
                    if (!colonists.TryGetValue(p, out var seen))
                    {
                        colonists[p] = new ColonistSeen { downed = p.Downed, mental = p.MentalStateDef, map = map };
                        continue;
                    }
                    seen.map = map;
                    if (p.Downed && !seen.downed)
                        GovernorEvents.Emit("colonist_downed", map, new Dictionary<string, object>
                        {
                            ["pawn"] = p.LabelShort, ["pos"] = new List<object> { p.Position.x, p.Position.z },
                            ["bleeding"] = p.health.hediffSet.BleedRateTotal > 0.01f,
                        });
                    seen.downed = p.Downed;
                    var ms = p.MentalStateDef;
                    if (ms != null && ms != seen.mental)
                        GovernorEvents.Emit("mental_break", map, new Dictionary<string, object>
                        {
                            ["pawn"] = p.LabelShort, ["state"] = ms.label, ["aggro"] = p.InAggroMentalState,
                        });
                    seen.mental = ms;
                }
            }
            tmpGone.Clear();
            foreach (var kv in colonists)
            {
                var p = kv.Key;
                if (p.Dead)
                {
                    GovernorEvents.Emit("colonist_died", kv.Value.map != null && Find.Maps.Contains(kv.Value.map) ? kv.Value.map : null,
                        new Dictionary<string, object> { ["pawn"] = p.LabelShort });
                    tmpGone.Add(p);
                }
                else if (!p.Spawned || p.Faction != Faction.OfPlayer) tmpGone.Add(p); // caravan, kidnapped, left
            }
            foreach (var p in tmpGone) colonists.Remove(p);
        }

        // ~every 250 ticks
        public void Slow()
        {
            foreach (var q in Find.QuestManager.QuestsListForReading)
            {
                if (q.id <= lastQuestId) continue;
                lastQuestId = q.id;
                if (q.State != QuestState.NotYetAccepted || q.hidden || q.dismissed || q.Historical) continue;
                GovernorEvents.Emit("quest_offer", null, new Dictionary<string, object>
                {
                    ["quest"] = q.name?.StripTags(), ["questId"] = q.id,
                    ["expiresInHours"] = q.TicksUntilExpiry > 0 ? (object)(float)Math.Round(q.TicksUntilExpiry / 2500f, 1) : null,
                });
            }
            var now = new HashSet<string>();
            foreach (var al in ImportantAlerts())
            {
                var key = al.GetType().Name;
                if (!now.Add(key)) continue;
                string label;
                try { label = al.GetLabel().StripTags(); } catch { label = key; }
                alertLabels[key] = label;
                if (!alerts.Contains(key))
                    GovernorEvents.Emit("alert_new", null, new Dictionary<string, object> { ["alert"] = label, ["priority"] = al.Priority.ToString() });
            }
            foreach (var key in alerts)
                if (!now.Contains(key))
                    GovernorEvents.Emit("alert_cleared", null, new Dictionary<string, object>
                    {
                        ["alert"] = alertLabels.TryGetValue(key, out var l) ? l : key,
                    });
            alerts.Clear();
            foreach (var k in now) alerts.Add(k);
            foreach (var map in Find.Maps)
            {
                if (!map.IsPlayerHome) continue;
                var n = HomeFires(map, out var at);
                firesByMap.TryGetValue(map.uniqueID, out var before);
                if (n > 0 && before == 0)
                    GovernorEvents.Emit("fire_in_home", map, new Dictionary<string, object>
                    {
                        ["fires"] = n, ["at"] = new List<object> { at.x, at.z },
                    });
                else if (n == 0 && before > 0) GovernorEvents.Emit("fire_out", map);
                firesByMap[map.uniqueID] = n;
            }
        }

        static int HomeFires(Map map, out IntVec3 at)
        {
            at = IntVec3.Invalid;
            int n = 0;
            var home = map.areaManager.Home;
            foreach (var f in map.listerThings.ThingsOfDef(ThingDefOf.Fire))
            {
                if (!f.Spawned || home == null || !home[f.Position] || f.Position.Fogged(map)) continue;
                if (n == 0) at = f.Position;
                n++;
            }
            return n;
        }

        static IEnumerable<Alert> ImportantAlerts()
        {
            if (!(Find.UIRoot is UIRoot_Play) || !(activeAlertsField?.GetValue(Find.Alerts) is List<Alert> list)) yield break;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].Priority >= AlertPriority.High) yield return list[i];
        }
    }
}
