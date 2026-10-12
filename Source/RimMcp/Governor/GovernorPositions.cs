using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMcp
{
    // Where to stand: home center, firing positions with cover (vanilla CoverUtility math),
    // indoor fallback cells and shelter cells. Only called while a threat is active.
    public static class GovernorPositions
    {
        public struct Cand
        {
            public IntVec3 cell;
            public float score;
            public float cover;
        }

        public const float AutoRadius = 25f;

        public static Map PrimaryHome => Find.Maps.FirstOrDefault(m => m.IsPlayerHome);

        public static string StationKey(Map map) => map == PrimaryHome ? "home" : map.uniqueID.ToString();

        public static List<IntVec3> PolicyStations(Map map, GovernorPolicy pol)
        {
            if (pol.stations.TryGetValue(map.uniqueID.ToString(), out var l) && l.Count > 0) return l;
            if (map == PrimaryHome && pol.stations.TryGetValue("home", out l) && l.Count > 0) return l;
            return null;
        }

        public static IntVec3 HomeCenter(Map map, GovernorPolicy pol)
        {
            if (map == PrimaryHome && pol.homeCenter.IsValid && pol.homeCenter.InBounds(map)) return pol.homeCenter;
            long x = 0, z = 0;
            int n = 0;
            foreach (var bed in map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>())
            {
                if (bed.ForPrisoners || bed.Medical || !bed.def.building.bed_humanlike) continue;
                x += bed.Position.x; z += bed.Position.z; n++;
            }
            if (n == 0)
            {
                var home = map.areaManager.Home;
                if (home != null && home.TrueCount > 0 && home.TrueCount < 20000)
                    foreach (var c in home.ActiveCells) { x += c.x; z += c.z; n++; }
            }
            if (n == 0)
                foreach (var p in map.mapPawns.FreeColonistsSpawned) { x += p.Position.x; z += p.Position.z; n++; }
            if (n == 0) return map.Center;
            var center = new IntVec3((int)(x / n), 0, (int)(z / n));
            if (!center.Standable(map))
            {
                var near = CellFinder.StandableCellNear(center, map, 8f);
                if (near.IsValid) center = near;
            }
            return center;
        }

        public static bool Indoor(IntVec3 c, Map map)
        {
            if (!c.Roofed(map)) return false;
            var room = c.GetRoom(map);
            return room != null && !room.PsychologicallyOutdoors && !room.IsDoorway;
        }

        static bool UsableCell(IntVec3 c, Map map) =>
            c.InBounds(map) && c.Standable(map) && !c.Fogged(map) && c.GetDoor(map) == null && !c.ContainsStaticFire(map);

        static Vector3 Dir(IntVec3 home, IntVec3 enemy)
        {
            var dir = new Vector3(enemy.x - home.x, 0f, enemy.z - home.z);
            if (dir.sqrMagnitude < 1f) dir = new Vector3(1f, 0f, 0f);
            dir.Normalize();
            return dir;
        }

        // float.MinValue = not usable. Cover is vanilla's block chance against a shot from the
        // enemy centroid; then home area, distance to home and "about 8 cells in front of home".
        static float Score(Map map, IntVec3 home, IntVec3 enemy, Vector3 dir, IntVec3 c, bool indoorOnly, float range, out float cover)
        {
            cover = 0f;
            if (!UsableCell(c, map)) return float.MinValue;
            float along = (c.x - home.x) * dir.x + (c.z - home.z) * dir.z;
            bool indoor = Indoor(c, map);
            if (indoorOnly ? !indoor : along < -2f || indoor) return float.MinValue; // outside a fallback, never behind closed walls
            float distEnemy = (c - enemy).LengthHorizontal;
            if (distEnemy < 6f) return float.MinValue;
            cover = CoverUtility.CalculateOverallBlockChance(c, enemy, map);
            float score = cover * 100f;
            float distHome = (c - home).LengthHorizontal;
            var homeArea = map.areaManager.Home;
            if (homeArea != null && homeArea[c]) score += 8f;
            else if (distHome > 15f) score -= 10f;
            score -= indoorOnly ? distHome * 0.5f - Mathf.Max(0f, along) : Mathf.Abs(along - 8f);
            score -= Mathf.Max(0f, distEnemy - range) * 0.1f; // they come closer anyway: a light pull into range
            if (!HasLineOfFire(c, enemy, map)) score -= 60f;  // a wall that blocks the shot is no firing position
            return score;
        }

        static readonly List<IntVec3> leanTmp = new List<IntVec3>();

        // Straight line of sight to the enemy centroid, or from one of the cells a shooter leans
        // out to (vanilla ShootLeanUtility), like a pawn shooting around a corner.
        static bool HasLineOfFire(IntVec3 c, IntVec3 enemy, Map map)
        {
            if (!enemy.InBounds(map) || GenSight.LineOfSight(c, enemy, map, skipFirstCell: true)) return true;
            leanTmp.Clear();
            ShootLeanUtility.LeanShootingSourcesFromTo(c, enemy, map, leanTmp);
            foreach (var s in leanTmp)
                if (s != c && GenSight.LineOfSight(s, enemy, map, skipFirstCell: true)) return true;
            return false;
        }

        public static float ScoreOf(Map map, IntVec3 home, IntVec3 enemy, IntVec3 c, bool indoorOnly, float range) =>
            Score(map, home, enemy, Dir(home, enemy), c, indoorOnly, range, out _);

        // Firing positions around home facing the enemy centroid, best first. indoorOnly = the
        // fallback when the enemy is overwhelming (roofed rooms near home, still facing them).
        public static List<Cand> FiringCells(Map map, IntVec3 home, IntVec3 enemy, bool indoorOnly, float range)
        {
            var res = new List<Cand>();
            var dir = Dir(home, enemy);
            foreach (var c in GenRadial.RadialCellsAround(home, AutoRadius, true))
            {
                if (!c.InBounds(map)) continue;
                float score = Score(map, home, enemy, dir, c, indoorOnly, range, out var cover);
                if (score > float.MinValue) res.Add(new Cand { cell = c, score = score, cover = cover });
            }
            res.Sort((a, b) => b.score.CompareTo(a.score));
            return res;
        }

        // Best cells at least one free cell apart (Chebyshev distance >= 2).
        public static List<Cand> Spaced(List<Cand> sorted, int max, ICollection<IntVec3> taken)
        {
            var res = new List<Cand>();
            foreach (var c in sorted)
            {
                if (res.Count >= max) break;
                bool ok = true;
                foreach (var t in taken) if (Cheb(t, c.cell) < 2) { ok = false; break; }
                if (ok) foreach (var r in res) if (Cheb(r.cell, c.cell) < 2) { ok = false; break; }
                if (ok) res.Add(c);
            }
            return res;
        }

        public static int Cheb(IntVec3 a, IntVec3 b) => Math.Max(Math.Abs(a.x - b.x), Math.Abs(a.z - b.z));

        // Roofed indoor cells near home, away from the enemy. Best first.
        public static List<IntVec3> ShelterCells(Map map, IntVec3 home, IntVec3 enemy, int max)
        {
            var cands = new List<Cand>();
            foreach (var c in GenRadial.RadialCellsAround(home, 30f, true))
            {
                if (!UsableCell(c, map) || !Indoor(c, map)) continue;
                cands.Add(new Cand { cell = c, score = -(c - home).LengthHorizontal + 0.5f * (c - enemy).LengthHorizontal });
            }
            cands.Sort((a, b) => b.score.CompareTo(a.score));
            return cands.Take(max).Select(c => c.cell).ToList();
        }

        public static string Compass(IntVec3 from, IntVec3 to)
        {
            int dx = to.x - from.x, dz = to.z - from.z;
            if (dx == 0 && dz == 0) return "here";
            var a = Mathf.Atan2(dz, dx) * Mathf.Rad2Deg; // 0 = east, 90 = north
            string[] names = { "E", "NE", "N", "NW", "W", "SW", "S", "SE" };
            int i = Mathf.RoundToInt(((a % 360f) + 360f) % 360f / 45f) % 8;
            return names[i];
        }

        public static string Edge(Map map, IntVec3 c)
        {
            int w = c.x, e = map.Size.x - 1 - c.x, s = c.z, n = map.Size.z - 1 - c.z;
            int m = Math.Min(Math.Min(w, e), Math.Min(s, n));
            return m == n ? "north" : m == s ? "south" : m == e ? "east" : "west";
        }
    }
}
