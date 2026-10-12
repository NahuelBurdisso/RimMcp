using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace RimMcp
{
    // The command buttons (gizmos) the player sees with one thing selected: list them or
    // click one. God mode is off while this runs and dev gizmos are left out, so it can only
    // do what a player without dev mode could do. Targeted commands are resolved the way the
    // game's Targeter resolves a left click on a cell.
    public static partial class Tools
    {
        static readonly FieldInfo targeterAction = typeof(Targeter).GetField("action", Inst);
        static readonly FieldInfo targeterParams = typeof(Targeter).GetField("targetParams", Inst);
        static readonly FieldInfo targeterValidator = typeof(Targeter).GetField("targetValidator", Inst);
        static readonly FieldInfo floatMenuOptions = typeof(FloatMenu).GetField("options", Inst);

        static object Gizmos(Dictionary<string, object> a)
        {
            var map = CurMap();
            var t = GizmoTarget(a, map, out var others);
            var god = DebugSettings.godMode;
            DebugSettings.godMode = false;
            try
            {
                // what the player sees depends on the selection (single selection, like a click)
                if (Find.Selector.NumSelected != 1 || !Find.Selector.IsSelected(t))
                {
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(t, playSound: false);
                }
                var gizmos = PlayerGizmos(t);
                var res = new Dictionary<string, object> { ["thing"] = Clean(t.LabelCap), ["id"] = t.ThingID, ["pos"] = Pos(t.Position) };
                if (others != null && others.Count > 0) res["otherThingsAtCell"] = others;
                if (!a.ContainsKey("index") && !a.ContainsKey("label"))
                {
                    res["gizmos"] = gizmos.Select((g, i) => (object)DescribeGizmo(g, i)).ToList();
                    res["note"] = "activate with index (or label); targeted ones also need target_x,target_z or target_name";
                    return res;
                }
                Gizmo pick;
                if (a.ContainsKey("index"))
                {
                    int i = Int(a, "index");
                    if (i < 0 || i >= gizmos.Count) throw new ToolError($"no gizmo {i} ({gizmos.Count} gizmos); list them first");
                    pick = gizmos[i];
                    var expect = Str(a, "label", false);
                    if (expect != null && (GizmoLabel(pick) ?? "").IndexOf(expect, StringComparison.OrdinalIgnoreCase) < 0)
                        throw new ToolError($"gizmo {i} is now '{GizmoLabel(pick)}', not '{expect}'; list them again");
                }
                else
                {
                    var q = Str(a, "label");
                    pick = gizmos.FirstOrDefault(g => string.Equals(GizmoLabel(g), q, StringComparison.OrdinalIgnoreCase))
                           ?? gizmos.FirstOrDefault(g => (GizmoLabel(g) ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                           ?? throw new ToolError($"no gizmo '{q}'. Gizmos: " + string.Join(" | ", gizmos.Select((g, k) => k + ": " + GizmoLabel(g))));
                }
                foreach (var kv in ActivateGizmo(pick, a, map)) res[kv.Key] = kv.Value;
                return res;
            }
            finally
            {
                DebugSettings.godMode = god;
            }
        }

        // ------------------------------------------------------------------ target thing

        static int SelectRank(Thing t) => t is Pawn ? 0 : t is Building ? 1 : t is Plant ? 3 : t is Filth ? 4 : 2;

        static Thing GizmoTarget(Dictionary<string, object> a, Map map, out List<object> others)
        {
            others = null;
            Thing t;
            var name = Str(a, "name", false);
            var id = Str(a, "id", false);
            if (name != null) t = FindAnyPawn(name);
            else if (id != null)
                t = Find.Maps.SelectMany(m => m.listerThings.AllThings).FirstOrDefault(x => x.ThingID == id)
                    ?? throw new ToolError($"no spawned thing with id '{id}'");
            else if (a.ContainsKey("x"))
            {
                var c = Cell(a, map);
                var cands = c.GetThingList(map).Where(ThingSelectionUtility.SelectableByMapClick).Distinct().OrderBy(SelectRank).ToList();
                if (cands.Count == 0) throw new ToolError($"nothing selectable at ({c.x},{c.z})");
                t = cands[0];
                others = cands.Skip(1).Select(x => (object)$"{x.ThingID} ({Clean(x.LabelCap)})").ToList();
            }
            else throw new ToolError("pass name (a pawn), id (a thing) or x,z");
            if (!t.Spawned || t.Map != map) throw new ToolError(Clean(t.LabelCap) + " is not on the current map");
            if (!ThingSelectionUtility.SelectableByMapClick(t)) throw new ToolError(Clean(t.LabelCap) + " cannot be selected (hidden or fogged)");
            return t;
        }

        // Colonists first (short name), then any visible pawn on a map by short name or id, then full names.
        static Pawn FindAnyPawn(string name)
        {
            var colonists = AllColonists().ToList();
            var all = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned).Where(ThingSelectionUtility.SelectableByMapClick).ToList();
            bool Full(Pawn p) => p.Name != null && p.Name.ToStringFull.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
            return colonists.FirstOrDefault(p => string.Equals(p.LabelShort, name, StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(p => string.Equals(p.LabelShort, name, StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(p => p.ThingID == name)
                   ?? colonists.FirstOrDefault(Full)
                   ?? all.FirstOrDefault(Full)
                   ?? throw new ToolError($"no visible pawn named '{name}'. Colonists: " + string.Join(", ", colonists.Select(p => p.LabelShort)));
        }

        // ------------------------------------------------------------------ listing

        // Same set as GizmoGridDrawer.DrawGizmoGridFor for one selected thing: its gizmos plus the
        // reverse designators (deconstruct, uninstall, mine...), visible ones only, sorted by Order.
        static List<Gizmo> PlayerGizmos(Thing t)
        {
            var list = new List<Gizmo>(t.GetGizmos());
            foreach (var des in Find.ReverseDesignatorDatabase.AllDesignators)
            {
                var g = des.CreateReverseDesignationGizmo(t);
                if (g != null) list.Add(g);
            }
            return list.Where(g => g != null && g.Visible && !IsDevGizmo(g)).OrderBy(g => g.Order).ToList();
        }

        static bool IsDevGizmo(Gizmo g)
        {
            if (g is Command_Ability ca && ca.devGizmo) return true;
            var l = (g as Command)?.defaultLabel;
            if (l == null) return false;
            return l.StartsWith("DEV:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("DEV ", StringComparison.Ordinal) ||
                   l.StartsWith("Debug:", StringComparison.OrdinalIgnoreCase);
        }

        static string GizmoLabel(Gizmo g)
        {
            if (!(g is Command c)) return g.GetType().Name;
            try { return Clean(c.LabelCap); } catch { return Clean(c.defaultLabel); }
        }

        static string GizmoDesc(Command c)
        {
            try
            {
                var s = c is Command_Ability ca ? ca.Tooltip : c.Desc;
                return Trim(Clean(s)?.Replace("\n", " "), 200);
            }
            catch { return null; }
        }

        // "map" (needs target_x,target_z / target_name), "map+dest" (also dest_x,dest_z), "world" (not supported), or null
        static string TargetKind(Gizmo g)
        {
            if (g is Command_Ability ca)
            {
                if (!ca.Ability.def.targetRequired) return null;
                if (ca.Ability.def.targetWorldCell) return "world";
                return ca.Ability.verb?.DestinationSelector != null ? "map+dest" : "map";
            }
            if (g is Command_VerbTarget vt) return vt.verb != null && !vt.verb.Targetable ? null : "map";
            if (g is Command_Target) return "map";
            return null;
        }

        static string DisabledReason(Gizmo g, out bool disabled)
        {
            string reason;
            try { disabled = g.Disabled; reason = g.disabledReason; }
            catch (Exception e) { disabled = true; reason = "error checking: " + e.Message; }
            // this one only works out its state while drawn (GizmoOnGUI): same checks here
            if (!disabled && g is Command_BestowerCeremony bc)
            {
                reason = BestowerBlock(bc);
                disabled = reason != null;
            }
            if (disabled && reason.NullOrEmpty() && g is Command c) reason = c.defaultDesc; // reverse designators put it there
            return disabled ? Clean(reason) : null;
        }

        static string BestowerBlock(Command_BestowerCeremony c)
        {
            var bestower = GetField(c, "bestower") as Pawn;
            var forPawn = GetField(c, "forPawn") as Pawn;
            var job = GetField(c, "job") as LordJob_BestowingCeremony;
            if (bestower == null || forPawn == null || job == null) return null;
            if (!JobDriver_BestowingCeremony.AnalyzeThroneRoom(bestower, forPawn))
            {
                var title = forPawn.royalty?.GetTitleAwardedWhenUpdating(bestower.Faction, forPawn.royalty.GetFavor(bestower.Faction));
                return "BestowingCeremonyThroneroomRequirementsNotSatisfiedShort".Translate(forPawn.Named("PAWN"), (title?.label ?? "?").Named("TITLE")).Resolve();
            }
            if (!job.GetSpot().IsValid) return "MessageBestowerUnreachable".Translate().Resolve();
            var lord = forPawn.GetLord();
            if (lord != null)
                return (lord.LordJob is LordJob_Ritual
                    ? "CantStartRitualTargetIsAlreadyInRitual".Translate(forPawn.LabelShort)
                    : "MessageBestowingTargetIsBusy".Translate(forPawn.LabelShort)).Resolve();
            return null;
        }

        static Dictionary<string, object> DescribeGizmo(Gizmo g, int i)
        {
            var d = new Dictionary<string, object> { ["index"] = i, ["label"] = GizmoLabel(g), ["type"] = g.GetType().Name };
            if (!(g is Command c))
            {
                d["button"] = false;   // status widget or slider: nothing to click
                return d;
            }
            var desc = GizmoDesc(c);
            if (!desc.NullOrEmpty()) d["desc"] = desc;
            var reason = DisabledReason(g, out var disabled);
            if (disabled) { d["disabled"] = true; d["reason"] = reason; }
            if (g is Command_Toggle ct)
            {
                d["toggle"] = true;
                try { d["on"] = ct.isActive(); } catch { }
            }
            var tk = TargetKind(g);
            if (tk != null) d["target"] = tk;
            return d;
        }

        // ------------------------------------------------------------------ activation

        static Dictionary<string, object> ActivateGizmo(Gizmo g, Dictionary<string, object> a, Map map)
        {
            var label = GizmoLabel(g);
            if (!(g is Command cmd)) throw new ToolError($"'{label}' is a {g.GetType().Name}, not a button");
            var reason = DisabledReason(g, out var disabled);
            if (disabled) throw new ToolError($"'{label}' is disabled: {reason ?? "no reason given"}");
            if (!TutorSystem.AllowAction(cmd.TutorTagSelect)) throw new ToolError("the tutorial does not allow this now");
            var kind = TargetKind(g);
            bool hasTarget = a.ContainsKey("target_name") || a.ContainsKey("target_x");
            if (kind == "world") throw new ToolError($"'{label}' targets the world map; not supported");
            if (kind != null && !hasTarget) throw new ToolError($"'{label}' needs a target: pass target_x,target_z or target_name" + (kind == "map+dest" ? " (and dest_x,dest_z)" : ""));

            var before = Find.WindowStack.Windows.ToList();
            var ev = Event.current ?? new Event();
            g.ProcessInput(ev);
            g.ProcessGroupInput(ev, new List<Gizmo> { g });
            var res = new Dictionary<string, object> { ["activated"] = label };

            if (Find.WorldTargeter.IsTargeting)
            {
                Find.WorldTargeter.StopTargeting();
                CameraJumper.TryHideWorld();
                throw new ToolError($"'{label}' asks for a world-map target; not supported (targeting cancelled)");
            }
            if (Find.Targeter.IsTargeting)
            {
                if (!hasTarget)
                {
                    Find.Targeter.StopTargeting();
                    throw new ToolError($"'{label}' asks for a target: call again with target_x,target_z or target_name (targeting cancelled)");
                }
                res["target"] = ResolveTargeting(a, map);
            }
            else if (hasTarget) res["note"] = "the command did not ask for a target; target ignored";
            if (g is Command_Toggle ct) { try { res["on"] = ct.isActive(); } catch { } }

            var opened = Find.WindowStack.Windows.Where(w => !before.Contains(w) && !(w is ImmediateWindow)).ToList();
            var menu = opened.OfType<FloatMenu>().FirstOrDefault();
            if (menu != null)
            {
                var opts = floatMenuOptions?.GetValue(menu) as List<FloatMenuOption> ?? new List<FloatMenuOption>();
                var o = Str(a, "option", false);
                if (o == null)
                {
                    menu.Close(false);
                    res["menu"] = opts.Select((x, k) => (object)(k + ": " + Clean(x.Label) + (x.Disabled ? " [disabled]" : ""))).ToList();
                    res["note"] = "the command opens a menu (closed again): call again with option = index or text";
                    return res;
                }
                var pick = int.TryParse(o, out var oi)
                    ? (oi >= 0 && oi < opts.Count ? opts[oi] : null)
                    : opts.FirstOrDefault(x => (Clean(x.Label) ?? "").IndexOf(o, StringComparison.OrdinalIgnoreCase) >= 0);
                if (pick == null || pick.Disabled)
                {
                    menu.Close(false);
                    throw new ToolError((pick == null ? $"no menu option '{o}'" : $"menu option '{Clean(pick.Label)}' is disabled") +
                                        ". Options: " + string.Join(" | ", opts.Select((x, k) => k + ": " + Clean(x.Label))));
                }
                pick.Chosen(true, menu);
                if (menu.IsOpen) menu.Close(false);
                res["chose"] = Clean(pick.Label);
                if (Find.Targeter.IsTargeting)
                {
                    if (!hasTarget)
                    {
                        Find.Targeter.StopTargeting();
                        throw new ToolError("that option asks for a target: call again with target_x,target_z or target_name (targeting cancelled)");
                    }
                    res["target"] = ResolveTargeting(a, map);
                }
                opened = Find.WindowStack.Windows.Where(w => !before.Contains(w) && !(w is ImmediateWindow)).ToList();
            }
            if (opened.Count > 0)
            {
                var decisions = DecisionWindows();
                res["openedWindows"] = opened.Select(w =>
                {
                    var d = new Dictionary<string, object> { ["type"] = w.GetType().Name };
                    int wi = decisions.IndexOf(w);
                    if (wi >= 0) d["window"] = wi;
                    return (object)d;
                }).ToList();
                res["note"] = "a window opened: read it with dialogs, answer with choose window=N";
            }
            return res;
        }

        // The targeter is armed: do what its left-click handling does (Targeter.ProcessInputEvents).
        static object ResolveTargeting(Dictionary<string, object> a, Map map)
        {
            var tg = Find.Targeter;
            try
            {
                var src = tg.targetingSource;
                if (src != null)
                {
                    var target = ResolveTarget(a, map, src.targetParams, src, "target");
                    CheckSourceTarget(src, target);
                    var dest = src.DestinationSelector;
                    if (src.CasterIsPawn) src.OrderForceTarget(target);
                    else if (src.Caster is Building_Turret turret) turret.OrderAttack(target);
                    else throw new ToolError("unsupported targeting source " + src.GetType().Name);
                    if (dest == null) return DescribeTarget(target);
                    if (!a.ContainsKey("dest_x")) throw new ToolError("this also needs a destination: pass dest_x,dest_z (nothing was ordered)");
                    var d = ResolveTarget(a, map, dest.targetParams, dest, "dest");
                    CheckSourceTarget(dest, d);
                    dest.OrderForceTarget(d);
                    return new Dictionary<string, object> { ["target"] = DescribeTarget(target), ["destination"] = DescribeTarget(d) };
                }
                var act = targeterAction?.GetValue(tg) as Action<LocalTargetInfo> ?? throw new ToolError("targeter has no action (game version?)");
                var prm = targeterParams?.GetValue(tg) as TargetingParameters ?? throw new ToolError("targeter has no parameters (game version?)");
                var validator = targeterValidator?.GetValue(tg) as Func<LocalTargetInfo, bool>;
                var t = ResolveTarget(a, map, prm, null, "target");
                if (validator != null ? !validator(t) : !t.IsValid) throw new ToolError("not a valid target for this command");
                act(t);
                return DescribeTarget(t);
            }
            finally
            {
                if (tg.IsTargeting) tg.StopTargeting();
            }
        }

        // What a click at a cell (or on a pawn) would target: things first, then the cell (GenUI.TargetsAt).
        static LocalTargetInfo ResolveTarget(Dictionary<string, object> a, Map map, TargetingParameters prm, ITargetingSource src, string prefix)
        {
            var name = Str(a, prefix + "_name", false);
            if (name != null)
            {
                var p = FindAnyPawn(name);
                if (p.Map != map) throw new ToolError(p.LabelShort + " is not on the current map");
                if (!prm.CanTarget(p, src)) throw new ToolError(p.LabelShort + " is not a valid target for this command");
                return p;
            }
            var c = Cell(a, map, prefix + "_x", prefix + "_z");
            var hit = GenUI.TargetsAt(c.ToVector3Shifted(), prm, thingsOnly: true, src).FirstOrFallback(LocalTargetInfo.Invalid);
            if (hit.IsValid) return hit;
            if (c.InBounds(map, prm.mapBoundsContractedBy) && prm.CanTarget(new TargetInfo(c, map), src)) return c;
            throw new ToolError($"nothing at ({c.x},{c.z}) that this command can target");
        }

        static void CheckSourceTarget(ITargetingSource src, LocalTargetInfo t)
        {
            if (t.HasThing && t.Thing == src.Caster && !src.targetParams.canTargetSelf) throw new ToolError("cannot target itself");
            var msgs = liveMessagesField?.GetValue(null) as List<Message>;
            int n = msgs?.Count ?? 0;
            if (!src.ValidateTarget(t))
            {
                var why = msgs != null && msgs.Count > n ? Clean(msgs[msgs.Count - 1].text) : null;
                throw new ToolError("invalid target" + (why != null ? ": " + why : ""));
            }
            if (!(t.Thing is Pawn) && !src.IsMeleeAttack && !src.CanHitTarget(t))
                throw new ToolError("cannot hit that target from here (range or line of sight)");
        }

        static object DescribeTarget(LocalTargetInfo t) =>
            t.HasThing ? (object)$"{Clean(t.Thing.LabelCap)} at ({t.Cell.x},{t.Cell.z})" : $"cell ({t.Cell.x},{t.Cell.z})";

        // ------------------------------------------------------------------ ritual / lord job start windows

        static readonly MethodInfo lordJobIssues = typeof(Dialog_BeginLordJob).GetMethod("BlockingIssues", Inst);
        static readonly MethodInfo lordJobStart = typeof(Dialog_BeginLordJob).GetMethod("Start", Inst);
        static readonly MethodInfo lordJobCancel = typeof(Dialog_BeginLordJob).GetMethod("Cancel", Inst);
        static readonly MethodInfo ritualPredictedQuality = typeof(Dialog_BeginRitual).GetMethod("PredictedQuality", Inst);

        static List<string> LordJobIssues(Dialog_BeginLordJob lj) =>
            (lordJobIssues?.Invoke(lj, null) as IEnumerable<string>)?.Select(Clean).Where(s => !s.NullOrEmpty()).ToList() ?? new List<string>();

        static void DescribeLordJob(Dialog_BeginLordJob lj, Dictionary<string, object> d)
        {
            d["title"] = Clean(lj.HeaderLabel.Resolve());
            d["text"] = Trim(Clean(lj.DescriptionLabel.Resolve()), 1500);
            d["beginButton"] = Clean(lj.OkButtonLabel.Resolve());
            d["cancelButton"] = Clean(lj.CancelButtonLabel.Resolve());
            var issues = LordJobIssues(lj);
            d["canBegin"] = lj.CanBegin;
            if (issues.Count > 0) d["blockingIssues"] = issues;
            if (lj is Dialog_BeginRitual br && GetField(br, "assignments") is RitualRoleAssignments asg)
            {
                d["roles"] = asg.AllRolesForReading.Select(r => (object)new Dictionary<string, object>
                {
                    ["role"] = Clean(r.LabelCap.Resolve()), ["required"] = r.required,
                    ["pawns"] = asg.AssignedPawns(r).Select(p => (object)p.LabelShort).ToList(),
                }).ToList();
                d["participants"] = asg.Participants.Select(p => (object)p.LabelShort).ToList();
                d["spectators"] = asg.SpectatorsForReading.Select(p => (object)p.LabelShort).ToList();
                try
                {
                    if (ritualPredictedQuality?.Invoke(br, new object[] { null }) is FloatRange q)
                        d["expectedQuality"] = q.min == q.max ? q.min.ToStringPercent("F0") : q.min.ToStringPercent("F0") + "-" + q.max.ToStringPercent("F0");
                }
                catch { }
            }
            d["note"] = "choose window=N option=begin starts it with the game's default role assignment shown here; option=cancel closes it";
        }

        static object ChooseLordJob(Dialog_BeginLordJob lj, string opt)
        {
            var ok = Clean(lj.OkButtonLabel.Resolve());
            var cancel = Clean(lj.CancelButtonLabel.Resolve());
            var o = (opt ?? "").Trim().ToLowerInvariant();
            if (o == "begin" || o == "start" || o == "ok" || string.Equals(opt, ok, StringComparison.OrdinalIgnoreCase))
            {
                if (!lj.CanBegin) throw new ToolError("cannot begin: " + string.Join("; ", LordJobIssues(lj)));
                var before = Find.WindowStack.Windows.ToList();
                lordJobStart.Invoke(lj, null);
                var res = new Dictionary<string, object> { ["pressed"] = ok, ["windowClosed"] = !lj.IsOpen };
                var decisions = DecisionWindows();
                var confirm = decisions.Where(w => !before.Contains(w)).ToList();
                if (confirm.Count > 0)
                {
                    var w = confirm[0];
                    res["confirmWindow"] = decisions.IndexOf(w);
                    if (w is Dialog_MessageBox mb) res["confirmText"] = Trim(Clean(mb.text.Resolve()), 600);
                    res["note"] = "the game asks for confirmation: choose window=N option=A";
                }
                return res;
            }
            if (o == "cancel" || string.Equals(opt, cancel, StringComparison.OrdinalIgnoreCase))
            {
                lordJobCancel.Invoke(lj, null);
                return "cancelled " + lj.GetType().Name;
            }
            throw new ToolError($"options for this window: begin (button '{ok}'), cancel, close");
        }
    }
}
