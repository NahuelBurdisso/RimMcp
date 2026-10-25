using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;

namespace RimMcp
{
    // Decisions the game asks the player for: node-tree dialogs (letters, quest dialogs,
    // comms), message boxes, choice letters, quest offers and the trade screen.
    public static partial class Tools
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo curNodeField = typeof(Dialog_NodeTree).GetField("curNode", Inst);
        static readonly FieldInfo nodeTitleField = typeof(Dialog_NodeTree).GetField("title", Inst);
        static readonly FieldInfo diaTextField = typeof(DiaOption).GetField("text", Inst);
        static readonly MethodInfo diaActivate = typeof(DiaOption).GetMethod("Activate", Inst);

        static string Trim(string s, int max) => s == null ? null : s.Length <= max ? s : s.Substring(0, max) + "...";

        static string OptText(DiaOption o) => Clean(diaTextField?.GetValue(o) as string);

        static List<Window> DecisionWindows() =>
            Find.WindowStack.Windows.Where(w => !(w is ImmediateWindow) &&
                (w is Dialog_NodeTree || w is Dialog_MessageBox || w is Dialog_Trade || w is Dialog_BeginLordJob || w.forcePause)).ToList();

        static List<object> DescribeOptions(IEnumerable<DiaOption> opts) =>
            opts.Select((o, i) =>
            {
                var d = new Dictionary<string, object> { ["index"] = i, ["text"] = OptText(o) };
                if (o.disabled) { d["disabled"] = true; d["reason"] = Clean(o.disabledReason); }
                if (o.hyperlink.def != null || o.hyperlink.quest != null) d["infoOnly"] = true;
                return (object)d;
            }).ToList();

        static string QuestRewards(Quest q)
        {
            var parts = new List<string>();
            foreach (var pc in q.PartsListForReading.OfType<QuestPart_Choice>())
            {
                for (int i = 0; i < pc.choices.Count; i++)
                {
                    var rs = new List<string>();
                    foreach (var rw in pc.choices[i].rewards)
                    {
                        string txt;
                        try { txt = Clean(rw.GetDescription(default(RewardsGeneratorParams))); }
                        catch { txt = rw.GetType().Name; }
                        if (rw is Reward_Items ri && ri.items.Count > 0)
                            txt = string.Join(", ", ri.items.Select(t => Clean(t.LabelCap)));
                        rs.Add(Trim(txt, 200));
                    }
                    parts.Add((pc.choices.Count > 1 ? $"[choice {i}] " : "") + string.Join("; ", rs));
                }
            }
            return string.Join(" | ", parts);
        }

        static object Dialogs(Dictionary<string, object> a)
        {
            RequireGame();
            var r = Obj();
            var wins = new List<object>();
            var all = DecisionWindows();
            for (int i = 0; i < all.Count; i++)
            {
                var w = all[i];
                var d = new Dictionary<string, object> { ["window"] = i, ["type"] = w.GetType().Name, ["pausesGame"] = w.forcePause };
                if (w is Dialog_NodeTree nt && curNodeField?.GetValue(nt) is DiaNode node)
                {
                    d["title"] = Clean(nodeTitleField?.GetValue(nt) as string);
                    d["text"] = Trim(Clean(node.text.Resolve()), 2500);
                    d["options"] = DescribeOptions(node.options);
                }
                else if (w is Dialog_MessageBox mb)
                {
                    d["title"] = Clean(mb.title);
                    d["text"] = Trim(Clean(mb.text.Resolve()), 2500);
                    var b = Obj();
                    b["A"] = Clean(mb.buttonAText);
                    if (mb.buttonBText != null) b["B"] = Clean(mb.buttonBText);
                    if (mb.buttonCText != null) b["C"] = Clean(mb.buttonCText);
                    d["buttons"] = b;
                }
                else if (w is Dialog_BeginLordJob lj) DescribeLordJob(lj, d);
                else if (w is Dialog_GrowthMomentChoices gm) DescribeGrowthMoment(gm, d);
                else if (w is Dialog_Trade || TradeSession.Active && w.GetType().Name.Contains("Trade")) d["note"] = "trade screen open: use the trade tool";
                else if (w is Dialog_GiveName gn)
                {
                    d["suggestedName"] = GetField(gn, "curName");
                    if ((bool)GetField(gn, "useSecondName")) d["suggestedSecondName"] = GetField(gn, "curSecondName");
                    d["note"] = "answer with choose window=N option=name name=... (second=... when there is a second name)";
                }
                else
                {
                    d["title"] = Clean(w.optionalTitle);
                    d["note"] = "no automatic answer for this window type; choose with option 'close' to close it, or use the screen";
                }
                wins.Add(d);
            }
            r["windows"] = wins;
            r["letters"] = Find.LetterStack.LettersListForReading.Select(l =>
            {
                var d = new Dictionary<string, object> { ["id"] = l.ID, ["label"] = Clean(l.Label.ToString()), ["type"] = l.def.defName };
                if (l is ChoiceLetter cl)
                {
                    d["text"] = Trim(Clean(cl.Text.Resolve()), 1500);
                    try { d["options"] = DescribeOptions(cl.Choices); } catch (Exception e) { d["options"] = "error: " + e.Message; }
                    if (cl.quest != null) d["quest"] = cl.quest.id;
                    if (cl.TimeoutActive) d["expiresInHours"] = Math.Round((cl.disappearAtTick - Find.TickManager.TicksGame) / 2500f, 1);
                }
                if (!l.CanDismissWithRightClick) d["mustAnswer"] = true;
                return (object)d;
            }).ToList();
            r["questOffers"] = Find.QuestManager.QuestsListForReading
                .Where(q => q.State == QuestState.NotYetAccepted && !q.hidden && !q.Historical && !q.dismissed)
                .Select(q =>
                {
                    var d = new Dictionary<string, object>
                    {
                        ["id"] = q.id, ["name"] = Clean(q.name), ["description"] = Trim(Clean(q.description.Resolve()), 1500),
                        ["rewards"] = QuestRewards(q),
                    };
                    if (q.TicksUntilExpiry >= 0) d["expiresInHours"] = Math.Round(q.TicksUntilExpiry / 2500f, 1);
                    var can = QuestUtility.CanAcceptQuest(q);
                    if (!can.Accepted) d["cannotAccept"] = Clean(can.Reason);
                    if (q.RequiresAccepter) d["needsAccepter"] = true;
                    return (object)d;
                }).ToList();
            r["ongoingQuests"] = Find.QuestManager.QuestsListForReading
                .Where(q => q.State == QuestState.Ongoing && !q.hidden && !q.hiddenInUI)
                .Select(q => (object)$"{q.id}: {Clean(q.name)}").ToList();
            if (pauseReason != null) r["lastAutoPause"] = pauseReason;
            return r;
        }

        // Growth moment (child birthday): pick passions and a trait, like the dialog's OK button.
        static ChoiceLetter_GrowthMoment GrowthLetter(Dialog_GrowthMomentChoices gm) =>
            (ChoiceLetter_GrowthMoment)GetField(gm, "letter");

        static void DescribeGrowthMoment(Dialog_GrowthMomentChoices gm, Dictionary<string, object> d)
        {
            var l = GrowthLetter(gm);
            d["title"] = Clean(l.Label.ToString());
            d["pawn"] = l.pawn?.LabelShort;
            d["text"] = Trim(Clean(l.text.Resolve()), 1500);
            d["passionGains"] = l.passionGainsCount;
            if (!l.passionChoices.NullOrEmpty())
                d["passionChoices"] = l.passionChoices.Select(s => (object)$"{s.defName} (now {l.pawn.skills.GetSkill(s).passion}, level {l.pawn.skills.GetSkill(s).Level})").ToList();
            if (!l.traitChoices.NullOrEmpty())
                d["traitChoices"] = l.traitChoices.Select((t, i) => (object)$"{i}: {(t == ChoiceLetter_GrowthMoment.NoTrait ? "no trait" : Clean(t.LabelCap))}").ToList();
            d["note"] = "answer with choose window=N option='passions=SkillA,SkillB;trait=INDEX' (either part may be left out when there is nothing to choose)";
        }

        static object ChooseGrowthMoment(Dialog_GrowthMomentChoices gm, string opt)
        {
            var l = GrowthLetter(gm);
            var parts = opt.Split(';').Select(p => p.Split(new[] { '=' }, 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());
            var skills = new List<SkillDef>();
            if (parts.TryGetValue("passions", out var ps) && ps.Length > 0)
                foreach (var name in ps.Split(','))
                    skills.Add(l.passionChoices?.FirstOrDefault(s => string.Equals(s.defName, name.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? throw new ToolError($"'{name.Trim()}' is not a passion choice: " + string.Join(", ", (l.passionChoices ?? new List<SkillDef>()).Select(s => s.defName))));
            if (!l.passionChoices.NullOrEmpty() && skills.Count != l.passionGainsCount)
                throw new ToolError($"pick exactly {l.passionGainsCount} passion(s)");
            Trait trait = null;
            if (!l.traitChoices.NullOrEmpty())
            {
                if (!parts.TryGetValue("trait", out var ts) || !int.TryParse(ts, out var ti) || ti < 0 || ti >= l.traitChoices.Count)
                    throw new ToolError($"trait=INDEX required, 0..{l.traitChoices.Count - 1}");
                trait = l.traitChoices[ti];
            }
            l.MakeChoices(skills, trait);
            gm.Close();
            Find.LetterStack.RemoveLetter(l);
            return $"{l.pawn?.LabelShort}: passions {(skills.Count == 0 ? "none" : string.Join(", ", skills.Select(s => s.defName)))}, trait {(trait == null || trait == ChoiceLetter_GrowthMoment.NoTrait ? "none" : Clean(trait.LabelCap))}";
        }

        static DiaOption PickOption(List<DiaOption> opts, Dictionary<string, object> a)
        {
            var o = Str(a, "option");
            DiaOption pick = int.TryParse(o, out var i)
                ? (i >= 0 && i < opts.Count ? opts[i] : null)
                : opts.FirstOrDefault(x => (OptText(x) ?? "").IndexOf(o, StringComparison.OrdinalIgnoreCase) >= 0);
            if (pick == null) throw new ToolError($"no option '{o}'. Options: " + string.Join(" | ", opts.Select((x, k) => k + ": " + OptText(x))));
            if (pick.disabled) throw new ToolError($"option '{OptText(pick)}' is disabled: {Clean(pick.disabledReason)}");
            return pick;
        }

        static object Choose(Dictionary<string, object> a)
        {
            RequireGame();
            if (a.ContainsKey("window"))
            {
                var all = DecisionWindows();
                int wi = Int(a, "window");
                if (wi < 0 || wi >= all.Count) throw new ToolError($"no window {wi} (open: {all.Count}); call dialogs first");
                var w = all[wi];
                var opt = Str(a, "option");
                if (opt == "close") { w.Close(); return "closed " + w.GetType().Name; }
                if (w is Dialog_NodeTree nt)
                {
                    var node = (DiaNode)curNodeField.GetValue(nt);
                    var pick = PickOption(node.options, a);
                    var label = OptText(pick);
                    if (pick.dialog == null) pick.dialog = nt;
                    diaActivate.Invoke(pick, null);
                    var res = new Dictionary<string, object> { ["chose"] = label };
                    if (nt.IsOpen && curNodeField.GetValue(nt) is DiaNode next && next != node)
                    {
                        res["nextText"] = Trim(Clean(next.text.Resolve()), 2000);
                        res["nextOptions"] = DescribeOptions(next.options);
                    }
                    else res["windowClosed"] = !nt.IsOpen;
                    return res;
                }
                if (w is Dialog_GiveName gn && opt == "name")
                {
                    var t = gn.GetType();
                    var first = (Str(a, "name", false) ?? (string)GetField(gn, "curName"))?.Trim();
                    var useSecond = (bool)GetField(gn, "useSecondName");
                    var second = (Str(a, "second", false) ?? (string)GetField(gn, "curSecondName"))?.Trim();
                    bool Valid(string m, string v) => (bool)t.GetMethod(m, Inst).Invoke(gn, new object[] { v });
                    if (!Valid("IsValidName", first)) throw new ToolError($"invalid name '{first}'");
                    if (useSecond && !Valid("IsValidSecondName", second)) throw new ToolError($"invalid second name '{second}'");
                    t.GetMethod("Named", Inst).Invoke(gn, new object[] { first });
                    if (useSecond) t.GetMethod("NamedSecond", Inst).Invoke(gn, new object[] { second });
                    Find.WindowStack.TryRemove(gn);
                    return "named " + first + (useSecond ? " / " + second : "");
                }
                if (w is Dialog_MessageBox mb)
                {
                    var key = opt.ToUpperInvariant();
                    if (key != "A" && key != "B" && key != "C")
                    {
                        if (string.Equals(Clean(mb.buttonAText), opt, StringComparison.OrdinalIgnoreCase)) key = "A";
                        else if (string.Equals(Clean(mb.buttonBText), opt, StringComparison.OrdinalIgnoreCase)) key = "B";
                        else if (string.Equals(Clean(mb.buttonCText), opt, StringComparison.OrdinalIgnoreCase)) key = "C";
                        else throw new ToolError("option for a message box: A, B, C or the button text");
                    }
                    if (key == "A") { mb.buttonAAction?.Invoke(); mb.Close(); }
                    else if (key == "B") { if (mb.buttonBText == null) throw new ToolError("no button B"); mb.buttonBAction?.Invoke(); mb.Close(); }
                    else { if (mb.buttonCText == null) throw new ToolError("no button C"); mb.buttonCAction?.Invoke(); if (mb.buttonCClose) mb.Close(); }
                    return "pressed " + key;
                }
                if (w is Dialog_BeginLordJob lj) return ChooseLordJob(lj, opt);
                if (w is Dialog_GrowthMomentChoices gm) return ChooseGrowthMoment(gm, opt);
                throw new ToolError(w.GetType().Name + " has no automatic answer; option 'close' closes it");
            }
            if (a.ContainsKey("letter"))
            {
                int id = Int(a, "letter");
                var l = Find.LetterStack.LettersListForReading.FirstOrDefault(x => x.ID == id) ?? throw new ToolError($"no letter {id}");
                var opt = Str(a, "option");
                if (opt == "open") { l.OpenLetter(); return "opened; answer it as a window (dialogs)"; }
                if (opt == "dismiss")
                {
                    if (!l.CanDismissWithRightClick) throw new ToolError("this letter must be answered with one of its options");
                    Find.LetterStack.RemoveLetter(l);
                    return "dismissed";
                }
                if (!(l is ChoiceLetter cl)) throw new ToolError("not a choice letter; option 'open' or 'dismiss'");
                var opts = cl.Choices.ToList();
                var pick = PickOption(opts, a);
                if (pick.action == null && (pick.link != null || pick.linkLateBind != null))
                {
                    l.OpenLetter();
                    return "that option leads to more text: the letter is now open as a window; call dialogs and choose there";
                }
                pick.action?.Invoke();
                return new Dictionary<string, object> { ["chose"] = OptText(pick), ["letterStillThere"] = Find.LetterStack.LettersListForReading.Contains(l) };
            }
            if (a.ContainsKey("quest"))
            {
                int id = Int(a, "quest");
                var q = Find.QuestManager.QuestsListForReading.FirstOrDefault(x => x.id == id) ?? throw new ToolError($"no quest {id}");
                var act = (Str(a, "option", false) ?? "accept").ToLowerInvariant();
                if (act == "dismiss")
                {
                    q.dismissed = true;
                    foreach (var sq in q.GetSubquests()) sq.dismissed = true;
                    return "dismissed " + Clean(q.name);
                }
                if (act != "accept") throw new ToolError("quest option: accept or dismiss");
                if (q.State != QuestState.NotYetAccepted) throw new ToolError("quest is not an open offer (" + q.State + ")");
                var can = QuestUtility.CanAcceptQuest(q);
                if (!can.Accepted) throw new ToolError("cannot accept: " + Clean(can.Reason));
                var pc = q.PartsListForReading.OfType<QuestPart_Choice>().FirstOrDefault();
                if (pc != null && pc.choices.Count > 1)
                {
                    if (!a.ContainsKey("reward")) throw new ToolError($"this quest has {pc.choices.Count} reward choices; pass reward (0..{pc.choices.Count - 1}). " + QuestRewards(q));
                    int ri = Int(a, "reward");
                    if (ri < 0 || ri >= pc.choices.Count) throw new ToolError("reward out of range");
                    pc.Choose(pc.choices[ri]);
                }
                Pawn accepter = null;
                if (q.RequiresAccepter)
                {
                    var name = Str(a, "accepter", false);
                    var cands = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoSuspended.Where(p => QuestUtility.CanPawnAcceptQuest(p, q)).ToList();
                    accepter = name != null ? cands.FirstOrDefault(p => string.Equals(p.LabelShort, name, StringComparison.OrdinalIgnoreCase)) : cands.FirstOrDefault();
                    if (accepter == null) throw new ToolError("needs an accepter. Candidates: " + string.Join(", ", cands.Select(p => p.LabelShort)));
                }
                SoundDefOf.Quest_Accepted.PlayOneShotOnCamera();
                q.Accept(accepter);
                return "accepted " + Clean(q.name) + (accepter != null ? " (by " + accepter.LabelShort + ")" : "");
            }
            throw new ToolError("pass window, letter or quest (see dialogs)");
        }

        // ------------------------------------------------------------------ trade

        static object Trade(Dictionary<string, object> a)
        {
            RequireGame();
            var action = (Str(a, "action", false) ?? "list").ToLowerInvariant();
            // vanilla Dialog_Trade, or a mod's replacement (e.g. Dynamic Trade Interface's Window_DynamicTrade) on the same TradeSession
            var dlg = (Window)Find.WindowStack.WindowOfType<Dialog_Trade>()
                      ?? Find.WindowStack.Windows.FirstOrDefault(w => w.GetType().Name.Contains("Trade"));
            if (dlg == null || !TradeSession.Active)
                throw new ToolError("no trade screen open. Open one with orders (a colonist + 'Trade with' on a trader, or a comms console)");
            var deal = TradeSession.deal;
            var items = deal.AllTradeables.Where(t => !t.IsCurrency && t.Interactive).ToList();
            switch (action)
            {
                case "list":
                {
                    deal.UpdateCurrencyCount();
                    var cur = deal.CurrencyTradeable;
                    var q = Str(a, "query", false);
                    return new Dictionary<string, object>
                    {
                        ["trader"] = Clean(TradeSession.trader.TraderName),
                        ["negotiator"] = TradeSession.playerNegotiator?.LabelShort,
                        ["giftMode"] = TradeSession.giftMode,
                        ["colonySilver"] = cur?.CountHeldBy(Transactor.Colony),
                        ["traderSilver"] = cur?.CountHeldBy(Transactor.Trader),
                        ["silverAfterDeal"] = cur?.CountPostDealFor(Transactor.Colony),
                        ["note"] = "count > 0 = buy from trader, < 0 = sell; set then execute",
                        ["items"] = items.Select((t, i) => (i, t))
                            .Where(x => q == null || (x.t.Label ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                            .Select(x => (object)new Dictionary<string, object>
                            {
                                ["index"] = x.i, ["label"] = Clean(x.t.LabelCap), ["colony"] = x.t.CountHeldBy(Transactor.Colony),
                                ["trader"] = x.t.CountHeldBy(Transactor.Trader),
                                ["buyPrice"] = x.t.CountHeldBy(Transactor.Trader) > 0 ? (object)Math.Round(x.t.GetPriceFor(TradeAction.PlayerBuys), 1) : null,
                                ["sellPrice"] = x.t.CountHeldBy(Transactor.Colony) > 0 ? (object)Math.Round(x.t.GetPriceFor(TradeAction.PlayerSells), 1) : null,
                                ["count"] = x.t.CountToTransfer,
                            }).ToList(),
                    };
                }
                case "set":
                {
                    int i = Int(a, "index");
                    if (i < 0 || i >= items.Count) throw new ToolError("index out of range");
                    var t = items[i];
                    int n = Int(a, "count");
                    var rep = t.CanAdjustTo(n);
                    if (!rep.Accepted) throw new ToolError($"cannot set {Clean(t.LabelCap)} to {n}: {Clean(rep.Reason)} (range {t.GetMinimumToTransfer()}..{t.GetMaximumToTransfer()})");
                    t.AdjustTo(n);
                    deal.UpdateCurrencyCount();
                    return new Dictionary<string, object> { ["item"] = Clean(t.LabelCap), ["count"] = t.CountToTransfer, ["silverAfterDeal"] = deal.CurrencyTradeable?.CountPostDealFor(Transactor.Colony) };
                }
                case "reset": deal.Reset(); return "deal reset";
                case "execute":
                {
                    deal.UpdateCurrencyCount();
                    if (!TradeSession.giftMode && deal.CurrencyTradeable != null && deal.CurrencyTradeable.CountPostDealFor(Transactor.Colony) < 0)
                        throw new ToolError("colony cannot afford this deal");
                    if (!deal.TryExecute(out var traded)) throw new ToolError("trade refused: " + string.Join("; ", deal.cannotSellReasons));
                    TradeSession.playerNegotiator?.GetCaravan()?.RecacheInventory();
                    dlg.Close(doCloseSound: false);
                    return traded ? "trade done" : "nothing was traded; screen closed";
                }
                case "cancel": dlg.Close(); return "trade cancelled";
                default: throw new ToolError("action: list, set, reset, execute, cancel");
            }
        }
    }
}
