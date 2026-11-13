using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;

namespace RimMcp
{
    // Apparel, food and drug policies: the same objects the Assign tab's "Manage" dialogs edit.
    public static partial class Tools
    {
        static object Policies(Dictionary<string, object> a)
        {
            RequireGame();
            var action = Str(a, "action", false) ?? "list";
            var kind = (Str(a, "kind", false) ?? "apparel").ToLowerInvariant();
            if (kind != "apparel" && kind != "food" && kind != "drug") throw new ToolError("kind: apparel, food or drug");
            switch (action)
            {
                case "list": return PolicyList(kind, Bool(a, "detail", false));
                case "new":
                {
                    var label = Str(a, "policy");
                    if (AllPolicies(kind).Any(p => Same(p.label, label))) throw new ToolError("a " + kind + " policy named " + label + " exists");
                    Policy made = kind == "apparel" ? (Policy)Current.Game.outfitDatabase.MakeNewOutfit()
                        : kind == "food" ? Current.Game.foodRestrictionDatabase.MakeNewFoodRestriction()
                        : Current.Game.drugPolicyDatabase.MakeNewDrugPolicy();
                    var copy = Str(a, "copy", false);
                    if (copy != null) made.CopyFrom(FindPolicy(kind, copy));
                    made.label = label;
                    return PolicyDetail(kind, made);
                }
                case "rename":
                {
                    var p = FindPolicy(kind, Str(a, "policy"));
                    p.label = Str(a, "new_name");
                    return PolicyDetail(kind, p);
                }
                case "delete":
                {
                    var p = FindPolicy(kind, Str(a, "policy"));
                    var rep = kind == "apparel" ? Current.Game.outfitDatabase.TryDelete((ApparelPolicy)p)
                        : kind == "food" ? Current.Game.foodRestrictionDatabase.TryDelete((FoodPolicy)p)
                        : Current.Game.drugPolicyDatabase.TryDelete((DrugPolicy)p);
                    if (!rep.Accepted) throw new ToolError(rep.Reason);
                    return PolicyList(kind, false);
                }
                case "default":
                {
                    var p = FindPolicy(kind, Str(a, "policy"));
                    if (kind == "apparel") Current.Game.outfitDatabase.SetDefault((ApparelPolicy)p);
                    else if (kind == "food") Current.Game.foodRestrictionDatabase.SetDefault((FoodPolicy)p);
                    else Current.Game.drugPolicyDatabase.SetDefault((DrugPolicy)p);
                    return PolicyList(kind, false);
                }
                case "filter": return PolicyFilter(kind, a);
                case "drug": return PolicyDrug(a);
                case "assign":
                {
                    var p = FindPolicy(kind, Str(a, "policy"));
                    foreach (var pawn in PawnsArg(a))
                    {
                        if (kind == "apparel" && pawn.outfits != null) pawn.outfits.CurrentApparelPolicy = (ApparelPolicy)p;
                        else if (kind == "food" && pawn.foodRestriction != null) pawn.foodRestriction.CurrentFoodPolicy = (FoodPolicy)p;
                        else if (kind == "drug" && pawn.drugs != null) pawn.drugs.CurrentPolicy = (DrugPolicy)p;
                    }
                    return PolicyList(kind, false);
                }
                case "colony_setup":
                {
                    // Colony Template (nahuel.colonytemplate), soft dependency: the same call as its "setup"
                    // button - schedule, work priorities from skills/passions, medicine, self-tend, hostility
                    var t = GenTypes.GetTypeInAnyAssembly("ColonyTemplate.ColonySetup")
                        ?? throw new ToolError("Colony Template mod is not active");
                    int n;
                    try { n = (int)t.GetMethod("Apply").Invoke(null, new object[] { CurMap() }); }
                    catch (TargetInvocationException e) { throw new ToolError("colony setup failed: " + e.InnerException?.Message); }
                    return new Dictionary<string, object> { ["colonistsSetUp"] = n };
                }
                default: throw new ToolError("action: list, new, rename, delete, default, filter, drug, assign, colony_setup");
            }
        }

        static bool Same(string x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

        static IEnumerable<Policy> AllPolicies(string kind) =>
            kind == "apparel" ? Current.Game.outfitDatabase.AllOutfits.Cast<Policy>()
            : kind == "food" ? Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Cast<Policy>()
            : Current.Game.drugPolicyDatabase.AllPolicies.Cast<Policy>();

        static Policy FindPolicy(string kind, string label) =>
            AllPolicies(kind).FirstOrDefault(p => Same(p.label, label))
            ?? throw new ToolError($"no {kind} policy '{label}'. Policies: {string.Join(", ", AllPolicies(kind).Select(p => p.label))}");

        static object PolicyList(string kind, bool detail)
        {
            var r = Obj();
            r["kind"] = kind;
            r["policies"] = AllPolicies(kind).Select(p => detail ? PolicyDetail(kind, p) : (object)p.label).ToList();
            r["assigned"] = AllColonists().ToDictionary(p => p.LabelShort, p => (object)(
                kind == "apparel" ? p.outfits?.CurrentApparelPolicy?.label
                : kind == "food" ? p.foodRestriction?.CurrentFoodPolicy?.label
                : p.drugs?.CurrentPolicy?.label));
            return r;
        }

        static Dictionary<string, object> PolicyDetail(string kind, Policy p)
        {
            var o = new Dictionary<string, object> { ["policy"] = p.label };
            ThingFilter f = (p as ApparelPolicy)?.filter ?? (p as FoodPolicy)?.filter;
            if (f != null)
            {
                o["allowedDefs"] = f.AllowedDefCount;
                if (kind == "apparel")
                {
                    o["hitPoints"] = $"{f.AllowedHitPointsPercents.min:P0}-{f.AllowedHitPointsPercents.max:P0}";
                    o["quality"] = $"{f.AllowedQualityLevels.min}-{f.AllowedQualityLevels.max}";
                    o["tainted"] = f.Allows(SpecialThingFilterDefOf.AllowDeadmansApparel);
                }
            }
            if (p is DrugPolicy d)
            {
                var entries = new List<object>();
                for (int i = 0; i < d.Count; i++)
                {
                    var e = d[i];
                    var uses = new List<string>();
                    if (e.allowedForJoy) uses.Add("joy");
                    if (e.allowedForAddiction) uses.Add("addiction");
                    if (e.allowScheduled) uses.Add($"every {e.daysFrequency:0.#} d");
                    if (e.takeToInventory > 0) uses.Add($"carry {e.takeToInventory}");
                    if (uses.Count > 0) entries.Add($"{e.drug.defName}: {string.Join(", ", uses)}");
                }
                o["drugsUsed"] = entries;
            }
            return o;
        }

        // allow / disallow: ThingDef, ThingCategoryDef, SpecialThingFilterDef or StuffCategoryDef defNames.
        // allow_all / disallow_all first, then disallow, then allow; hp [min,max] 0..1; quality [min,max].
        static object PolicyFilter(string kind, Dictionary<string, object> a)
        {
            if (kind == "drug") throw new ToolError("drug policies have no filter: use action drug");
            var p = FindPolicy(kind, Str(a, "policy"));
            var f = ((p as ApparelPolicy)?.filter ?? (p as FoodPolicy)?.filter);
            var unknown = new List<string>();
            if (Bool(a, "disallow_all", false)) f.SetDisallowAll();
            if (Bool(a, "allow_all", false))
            {
                if (kind == "apparel") f.SetAllow(ThingCategoryDefOf.Apparel, true);
                else foreach (var d in DefDatabase<ThingDef>.AllDefs.Where(x => x.GetStatValueAbstract(StatDefOf.Nutrition) > 0f)) f.SetAllow(d, true);
            }
            foreach (var (key, allow) in new[] { ("disallow", false), ("allow", true) })
            {
                foreach (var n in StrList(a, key) ?? new List<string>())
                {
                    var td = DefDatabase<ThingDef>.GetNamedSilentFail(n);
                    if (td != null) { f.SetAllow(td, allow); continue; }
                    var cat = DefDatabase<ThingCategoryDef>.GetNamedSilentFail(n);
                    if (cat != null) { f.SetAllow(cat, allow); continue; }
                    var sp = DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail(n);
                    if (sp != null) { f.SetAllow(sp, allow); continue; }
                    var sc = DefDatabase<StuffCategoryDef>.GetNamedSilentFail(n);
                    if (sc != null) { f.SetAllow(sc, allow); continue; }
                    unknown.Add(n);
                }
            }
            var hp = StrList(a, "hp");
            if (hp != null && hp.Count == 2)
                f.AllowedHitPointsPercents = new FloatRange(float.Parse(hp[0], System.Globalization.CultureInfo.InvariantCulture),
                                                            float.Parse(hp[1], System.Globalization.CultureInfo.InvariantCulture));
            var q = StrList(a, "quality");
            if (q != null && q.Count == 2)
            {
                if (!Enum.TryParse<QualityCategory>(q[0], true, out var qmin) || !Enum.TryParse<QualityCategory>(q[1], true, out var qmax))
                    throw new ToolError("quality: Awful, Poor, Normal, Good, Excellent, Masterwork, Legendary");
                f.AllowedQualityLevels = new QualityRange(qmin, qmax);
            }
            var r = PolicyDetail(kind, p);
            if (unknown.Count > 0) r["unknownDefs"] = unknown;
            return r;
        }

        // one drug entry of a drug policy: joy, addiction, scheduled + days, carry (take to inventory)
        static object PolicyDrug(Dictionary<string, object> a)
        {
            var p = (DrugPolicy)FindPolicy("drug", Str(a, "policy"));
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(Str(a, "drug")) ?? throw new ToolError("no drug " + Str(a, "drug"));
            var e = p[def] ?? throw new ToolError(def.defName + " is not in drug policies");
            if (a.ContainsKey("joy")) e.allowedForJoy = Bool(a, "joy", false);
            if (a.ContainsKey("addiction")) e.allowedForAddiction = Bool(a, "addiction", false);
            if (a.ContainsKey("scheduled")) e.allowScheduled = Bool(a, "scheduled", false);
            if (a.ContainsKey("days")) e.daysFrequency = Int(a, "days");
            if (a.ContainsKey("carry")) e.takeToInventory = Int(a, "carry");
            return PolicyDetail("drug", p);
        }
    }
}
