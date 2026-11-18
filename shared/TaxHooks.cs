// 課税の実データを拾う割り込み。
//
// いまの範囲:
//   A. 固定費用の点数（人頭税・固定資産税・消費税）… 本体が持つ確定値をそのまま読む
//   B. 確率で消える収入（投資課税・損害賠償・特産品税・中央集権）… 消えた瞬間に量を積む
//      0.30 の新課税: 源泉徴収（資源）・保管手数料／盗難保険料（現金）・もう結構（売却の無効）は同じ形、
//      徴発は開始時の差分、印紙税は率だけ
//   C. 製作数の差分（サンプル提供命令・関税）と遅れの換算（トリプルチェック・逮捕）、
//      増えた打数（鉱産税・農産税）、下げられた率（節水令）

using System;
using System.Collections.Generic;
using System.Reflection;
using BaseSystem;
using Buff.Debuffs;
using HarmonyLib;
using Fams.EditIfFamAdd;
using Harvestable;
using Harvestable.HarvestableManagerHelper;
using Items;
using Items.Craft;
using Items.Tag;
using Items.Delivery;
using Items.ValueCountSettings;
using Map.MapObjects.PureClass.FamObjects;
using Tax;

namespace LwfTaxLedger
{
    internal static class TaxHooks
    {
        /// <summary>いま効いている課税がどの期に入っているか。EffectID -> slot。
        /// 本体から読めないときの控え（読み直し直後は本体を見る）</summary>
        private static readonly Dictionary<string, int> ActiveSlots = new Dictionary<string, int>();

        /// <summary>効果の実体から期を引く（期を引数で受け取らない割り込み用）</summary>
        private static readonly Dictionary<object, int> SlotByEffect = new Dictionary<object, int>();

        // 本体が持つ「いまの期の一覧」と「効いている課税」。読み直しで控えが消えても、
        // ここを見れば拾い直せる
        private static TaxRuntimeController _controller;
        private static FieldInfo _cachedSlotsField;
        private static FieldInfo _contextField;
        private static FieldInfo _activeEffectIDField;

        private static FieldInfo _appliedAmountField;
        private static FieldInfo _penaltyTagField;
        private static FieldInfo _repayTagField;
        private static FieldInfo _localTagTargetField;
        private static FieldInfo _snapshotsField;
        private static FieldInfo _propField;
        private static PropertyInfo _baseResultCountProp;

        /// <summary>本体が持つレシピの素の製作数を引くための入口</summary>
        private static RecipeTemporaryModifierService _recipeModifiers;

        /// <summary>製作数が減っているレシピ。素の数はここに控えて、毎回の反射を避ける</summary>
        private static readonly Dictionary<Recipe, int> BaseCountCache = new Dictionary<Recipe, int>();
        private static readonly Dictionary<Recipe, int> LastSeenCount = new Dictionary<Recipe, int>();



        // 売却の処理に入っているアイテム。確率で消えたときに量を出すのに使う
        private static string _depositItemID;
        private static int _depositCount;

        // 損害賠償（スポンサー）の判定が真だったか。外側の割り込みと切り分ける
        private static bool _patronCutHit;

        // 採掘の必要量が本当に増えているかを 1 回だけログに出すための印
        private static bool _loggedHarvest;

        // 検算用。確率で消える課税は「判定した回数」と「消えた回数」を数えておく。
        // 出る率が本体の設定と合っていれば、拾い方が正しいと分かる
        private static readonly Dictionary<string, int> Tries = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> Hits = new Dictionary<string, int>();

        private static void Count(string effectID, bool hit)
        {
            // 掛かっていないあいだの判定は数えない。バフの現金収入はシフト開始直後から入るので、
            // 分母に混ぜると率が下がって、拾えていないのか掛かっていないのか分からなくなる
            if (!IsActive(effectID)) { return; }
            int n;
            Tries.TryGetValue(effectID, out n);
            Tries[effectID] = n + 1;
            if (!hit) { return; }
            Hits.TryGetValue(effectID, out n);
            Hits[effectID] = n + 1;
        }

        internal static void Init(Harmony harmony)
        {
            _cachedSlotsField = AccessTools.Field(typeof(TaxRuntimeController), "_cachedSlots");
            _contextField = AccessTools.Field(typeof(TaxRuntimeController), "_context");
            _activeEffectIDField = AccessTools.Field(typeof(TaxRuntimeController), "_activeEffectID");
            _appliedAmountField = AccessTools.Field(typeof(RepaymentLoseCashPenaltyTaxEffectBase), "_appliedAmount");
            _penaltyTagField = AccessTools.Field(typeof(OrderExecutePenaltyTaxEffect), "_penaltyTagCount");
            _repayTagField = AccessTools.Field(typeof(DebuffRelicRepaymentLoseCash), "_penaltyTagCount");
            _localTagTargetField = AccessTools.Field(typeof(TaxResourceRecordCutService), "_localTagTargetID");
            _snapshotsField = AccessTools.Field(typeof(RecipeTemporaryModifierService), "_snapshots");
            _propField = AccessTools.Field(typeof(TakenDamageCalculator), "_prop");
            Type snapshot = AccessTools.Inner(typeof(RecipeTemporaryModifierService), "RecipeSnapshot");
            _baseResultCountProp = snapshot != null ? AccessTools.Property(snapshot, "BaseResultCount") : null;
            if (_appliedAmountField == null) { LedgerHost.Warn("[hook] cannot read _appliedAmount"); }
            if (_penaltyTagField == null) { LedgerHost.Warn("[hook] cannot read _penaltyTagCount"); }
            if (_snapshotsField == null || _baseResultCountProp == null)
            {
                LedgerHost.Warn("[hook] cannot read the recipe snapshots; craft losses stay 0");
            }
            if (_propField == null) { LedgerHost.Warn("[hook] cannot read _prop; extra hits stay 0"); }
            if (_repayTagField == null) { LedgerHost.Warn("[hook] cannot read the repayment penalty"); }
        }

        internal static int SlotOf(string effectID)
        {
            TaxSlotData[] slots = Slots();
            if (slots != null)
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    if (slots[i].EffectID == effectID) { return i; }
                }
            }
            int slot;
            if (ActiveSlots.TryGetValue(effectID, out slot)) { return slot; }
            return 0;
        }

        /// <summary>その課税がいま効いているか。読み直しで控えが消えても本体を見て答える。</summary>
        internal static bool IsActive(string effectID)
        {
            TaxRuntimeController controller = Controller();
            if (controller != null)
            {
                try
                {
                    if (_activeEffectIDField != null)
                    {
                        string id = _activeEffectIDField.GetValue(controller) as string;
                        if (id == effectID) { return true; }
                    }
                }
                catch (Exception) { }
            }
            return ActiveSlots.ContainsKey(effectID);
        }

        private static TaxRuntimeController Controller()
        {
            if (_controller != null) { return _controller; }
            try { _controller = UnityEngine.Object.FindAnyObjectByType<TaxRuntimeController>(); }
            catch (Exception) { _controller = null; }
            return _controller;
        }

        /// <summary>本体が課税に渡している文脈。開発用に、狙った課税をその場で掛けるのに使う。</summary>
        internal static TaxEffectContext Context()
        {
            TaxRuntimeController controller = Controller();
            if (controller == null || _contextField == null) { return null; }
            try { return _contextField.GetValue(controller) as TaxEffectContext; }
            catch (Exception) { return null; }
        }

        private static TaxSlotData[] Slots()
        {
            TaxRuntimeController controller = Controller();
            if (controller == null || _cachedSlotsField == null) { return null; }
            try { return _cachedSlotsField.GetValue(controller) as TaxSlotData[]; }
            catch (Exception) { return null; }
        }

        private static TaxEntry Entry(string effectID)
        {
            Ledger.DropDummy();
            return Ledger.Get(effectID, SlotOf(effectID));
        }

        // ---------------------------------------------------------------
        // シフトの区切り
        // ---------------------------------------------------------------

        [HarmonyPatch(typeof(TaxRuntimeController), "StartRuntime")]
        private static class StartRuntimePatch
        {
            private static void Prefix()
            {
                if (LedgerHost.RunStarting != null) { LedgerHost.RunStarting(); }
                // 前の Run を途中で離れていたら、台帳を消す前に控えへ書く
                LedgerFile.SaveRun("next run");
                Ledger.Reset();
                LedgerHost.RunPayTag = null;
                LedgerFile.MarkRunStart();
                _controller = null;
                _loggedHarvest = false;
                Tries.Clear();
                Hits.Clear();
                ActiveSlots.Clear();
                SlotByEffect.Clear();
                BaseCountCache.Clear();
                SlowCarry.Clear();
                LockCarry.Clear();
                LastSeenCount.Clear();
                LedgerHost.Trace("[ledger] cleared for a new shift");
            }
        }

        // ---------------------------------------------------------------
        // どの課税がどの期に入ったか
        // ---------------------------------------------------------------

        /// <summary>ITaxEffect を実装する型（抽象クラスも含む）のうち、その名前のメソッドを
        /// 自分で持つものを全部返す。派生が基底に任せているぶんは基底側で 1 回だけ掛かる。
        /// 0.30 で保管手数料・盗難保険料の Apply/Remove が抽象の CashPriceCutTaxEffect に移り、
        /// 具象だけを見ていた 1.0.x では取りこぼした。</summary>
        private static List<MethodBase> DeclaredOnTaxEffects(string name, Type[] args)
        {
            List<MethodBase> found = new List<MethodBase>();
            Type[] types = typeof(ITaxEffect).Assembly.GetTypes();
            for (int i = 0; i < types.Length; i++)
            {
                Type t = types[i];
                if (t.IsInterface) { continue; }
                if (!typeof(ITaxEffect).IsAssignableFrom(t)) { continue; }
                // DeclaredOnly で探す。AccessTools.DeclaredMethod は見つからないたびに警告を吐く
                MethodInfo m = t.GetMethod(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, args, null);
                if (m != null && !m.IsAbstract) { found.Add(m); }
            }
            return found;
        }

        [HarmonyPatch]
        private static class ApplyPatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                return DeclaredOnTaxEffects("Apply", new Type[] { typeof(TaxEffectContext), typeof(int) });
            }

            private static void Postfix(object __instance, TaxEffectContext context, int slotIndex)
            {
                ITaxEffect effect = __instance as ITaxEffect;
                if (effect == null) { return; }
                if (context != null && context.RecipeModifiers != null) { _recipeModifiers = context.RecipeModifiers; }
                string id = effect.EffectID;
                LedgerHost.RunPayTag = LedgerHost.PayTag();
                ActiveSlots[id] = slotIndex;
                SlotByEffect[__instance] = slotIndex;

                Ledger.DropDummy();
                TaxEntry entry = Ledger.Get(id, slotIndex);

                // 率でしか示せないものは、本体の表示用の値をそのまま持つ。
                // 節水令のほか、逮捕は使い魔の種類によって効き先が製作とは限らない
                // （マモン = 運搬役なら運搬が遅くなるだけで、製作数は 1 個も減らない）
                if (id == "SlowConveyor" || id == "LockRandomPatron" || id == "ReducePactRarity")
                {
                    try { entry.RateText = effect.GetValueText(slotIndex); }
                    catch (Exception) { }
                }

                // 一度きりで確定する点数（人頭税・固定資産税）
                if (_appliedAmountField != null && __instance is RepaymentLoseCashPenaltyTaxEffectBase)
                {
                    try { entry.Points = (int)_appliedAmountField.GetValue(__instance); }
                    catch (Exception) { }
                }

                // 相手が決まるもの（損害賠償・逮捕）
                if (id == "NoTagRandomPatron")
                {
                    entry.PatronID = (int)TaxPatronResourceCutService.TargetPatron;
                }
                else if (id == "LockRandomPatron")
                {
                    entry.PatronID = (int)TaxPatronLockService.LockedPatron;
                }

                LedgerHost.Trace("[tax] apply " + id + " slot=" + slotIndex
                    + (entry.Points != 0 ? " points=" + entry.Points : ""));
            }
        }

        /// <summary>効果が切れたら、以後の出来事を課税のせいにしない。</summary>
        [HarmonyPatch]
        private static class RemovePatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                return DeclaredOnTaxEffects("Remove", new Type[] { typeof(TaxEffectContext) });
            }

            private static void Postfix(object __instance)
            {
                ITaxEffect effect = __instance as ITaxEffect;
                if (effect == null) { return; }
                if (ActiveSlots.Remove(effect.EffectID))
                {
                    LedgerHost.Trace("[tax] remove " + effect.EffectID);
                }
                SlotByEffect.Remove(__instance);
            }
        }

        // ---------------------------------------------------------------
        // A. 注文ごとに積まれる点数（消費税）
        // ---------------------------------------------------------------

        [HarmonyPatch(typeof(OrderExecutePenaltyTaxEffect), "AddPenalty")]
        private static class OrderPenaltyPatch
        {
            private static void Postfix(object __instance)
            {
                if (_penaltyTagField == null) { return; }
                try
                {
                    TagCount tag = _penaltyTagField.GetValue(__instance) as TagCount;
                    if (tag == null || tag.Count <= 0) { return; }
                    Entry("OrderExecutePenalty").Points += tag.Count;
                }
                catch (Exception) { }
            }
        }

        /// <summary>返済のたびに、たまっている固定費用の点数と同じだけ資源が余分に減る。
        /// 課税が積んだ点数の分がそのままその課税の払った額になる。</summary>
        [HarmonyPatch(typeof(DebuffRelicRepaymentLoseCash), "Execute")]
        private static class RepaymentPenaltyPatch
        {
            private static void Postfix(object __instance)
            {
                if (_repayTagField == null) { return; }
                TagCount charged = null;
                try { charged = _repayTagField.GetValue(__instance) as TagCount; }
                catch (Exception) { return; }
                if (charged == null || charged.Count >= 0) { return; }

                int remaining = -charged.Count;
                string tagID = charged.TagID;
                List<TaxEntry> entries = Ledger.Entries;
                for (int i = 0; i < entries.Count && remaining > 0; i++)
                {
                    TaxEntry e = entries[i];
                    if (e.Points <= 0) { continue; }
                    int share = e.Points < remaining ? e.Points : remaining;
                    e.AddLostResource(tagID, share);
                    remaining -= share;
                }
            }
        }

        // ---------------------------------------------------------------
        // B. 確率で消える収入
        // ---------------------------------------------------------------

        [HarmonyPatch(typeof(TaxPatronResourceCutService), "ShouldCancelResourceAdd")]
        private static class PatronCutPatch
        {
            private static void Postfix(bool __result, string tagID, int actualDelta)
            {
                _patronCutHit = __result;
                if (IsTargetTag(TaxPatronResourceCutService.TargetPatron, tagID))
                {
                    Count("NoTagRandomPatron", __result);
                }
                if (!__result) { return; }
                TaxEntry entry = Entry("NoTagRandomPatron");
                entry.AddLostResource(tagID, actualDelta);
                entry.PatronID = (int)TaxPatronResourceCutService.TargetPatron;
            }
        }

        [HarmonyPatch(typeof(TaxResourceRecordCutService), "ShouldCancelResourceAdd")]
        private static class BuffCashCutPatch
        {
            private static void Prefix()
            {
                _patronCutHit = false;
            }

            private static void Postfix(bool __result, string tagID, int actualDelta,
                StatsCashReason statsCashReason)
            {
                // 検算の分母は、本体が対象にしている「バフ・レリックの収入」だけ。
                // 売却の入金まで数えると率が薄まって意味が無くなる。
                // 現金は投資課税、現金以外は源泉徴収（0.30）が消す
                string id = tagID == "Cash" ? "CutBuffCash" : "CutBuffResource";
                if (actualDelta > 0 && statsCashReason == StatsCashReason.BuffRelicIncome)
                {
                    Count(id, __result && !_patronCutHit);
                }
                // 中で損害賠償が当たった分は、そちらで既に積んである
                if (!__result || _patronCutHit) { return; }
                Entry(id).AddLostResource(tagID, actualDelta);
            }
        }

        [HarmonyPatch(typeof(TaxResourceRecordCutService), "ShouldCancelSaleTag")]
        private static class SaleTagCutPatch
        {
            private static void Postfix(bool __result, string tagID)
            {
                if (tagID == LocalTagTarget()) { Count("CutLocalTag", __result); }
                if (!__result) { return; }
                // 量は引数に無い。売却の処理に入っているアイテムから出す
                Entry("CutLocalTag").AddLostResource(tagID, TagAmountOfDepositItem(tagID));
            }
        }

        [HarmonyPatch(typeof(TaxDeliveryCancellationService), "ShouldCancelDelivery")]
        private static class DeliveryCancelPatch
        {
            private static void Postfix(bool __result, bool isDirectDeliveryBoxSale)
            {
                if (!isDirectDeliveryBoxSale) { Count("Centralization", __result); }
                if (!__result || _depositItemID == null) { return; }
                TaxEntry entry = Entry("Centralization");
                entry.CancelledSales++;
                AddItemLoss(entry, _depositItemID, _depositCount);
            }
        }

        /// <summary>もう結構（0.30）: 累計売却個数の上位にいるアイテムの売却が無効になる。
        /// DepositInternal の冒頭で判定されるので、控えたアイテムがそのまま消えた分。</summary>
        [HarmonyPatch(typeof(TaxSaleRestrictionService), "ShouldCancelSale")]
        private static class SaleRestrictionPatch
        {
            private static void Postfix(bool __result, string itemID)
            {
                Count("BlockTopSales", __result);
                if (!__result || string.IsNullOrEmpty(itemID)) { return; }
                TaxEntry entry = Entry("BlockTopSales");
                entry.CancelledSales++;
                AddItemLoss(entry, itemID, _depositCount > 0 ? _depositCount : 1);
            }
        }

        /// <summary>保管手数料・盗難保険料（0.30）: 現金価格が閾値の外にあるアイテムの売却で現金が入らない。
        /// 本体は価格の枠ごとに判定するので、消えた現金は「枠の価格 × 売った個数」。
        /// 売却自体は通り他の資源は入るので、アイテムの内訳には現金の分だけを積む（中央集権と同じ見た目）</summary>
        [HarmonyPatch(typeof(TaxCashPriceCutService), "ShouldCancelSaleTag")]
        private static class CashPriceCutPatch
        {
            private static void Postfix(bool __result, string originalTag, int price, string recordedTag)
            {
                if (originalTag != "Cash" || recordedTag != "Cash") { return; }
                string id = IsActive("LowPriceCashCut") ? "LowPriceCashCut"
                    : (IsActive("HighPriceCashCut") ? "HighPriceCashCut" : null);
                if (id == null) { return; }
                Count(id, __result);
                if (!__result || price <= 0) { return; }
                int count = _depositCount > 0 ? _depositCount : 1;
                if (_depositItemID != null)
                {
                    Entry(id).GetItem(_depositItemID).Add(count, "Cash", price, "None", 0, "None", 0);
                }
                else
                {
                    Entry(id).AddLostResource("Cash", price * count);
                }
            }
        }

        /// <summary>徴発（0.30）: 適用の瞬間に、現金以外の資源が目標値まで削られる。
        /// 前後の残高の差が失った量。</summary>
        [HarmonyPatch(typeof(ResourceDeficitTaxEffect), "Apply")]
        private static class ResourceDeficitPatch
        {
            private static void Prefix(TaxEffectContext context, out Dictionary<string, int> __state)
            {
                __state = null;
                ResourceTagsRecorder recorder = context != null ? context.ResourceTagsRecorder : null;
                if (recorder == null) { return; }
                __state = new Dictionary<string, int>();
                foreach (string tag in RecordableTags.COMMON)
                {
                    try { __state[tag] = recorder.GetCount(tag); }
                    catch (Exception) { }
                }
            }

            private static void Postfix(TaxEffectContext context, Dictionary<string, int> __state)
            {
                ResourceTagsRecorder recorder = context != null ? context.ResourceTagsRecorder : null;
                if (recorder == null || __state == null) { return; }
                foreach (KeyValuePair<string, int> kv in __state)
                {
                    int after;
                    try { after = recorder.GetCount(kv.Key); }
                    catch (Exception) { continue; }
                    if (after < kv.Value) { Entry("ResourceDeficit").AddLostResource(kv.Key, kv.Value - after); }
                }
            }
        }

        /// <summary>売却の処理に入っているアイテムを控える。消えた量を出すのに要る。</summary>
        [HarmonyPatch(typeof(DeliveryDepositor), "DepositInternal")]
        private static class DepositInternalPatch
        {
            private static void Prefix(ItemCount itemCount)
            {
                if (itemCount == null) { _depositItemID = null; _depositCount = 0; return; }
                _depositItemID = itemCount.ItemID;
                _depositCount = itemCount.Count;
            }

            private static void Postfix()
            {
                _depositItemID = null;
                _depositCount = 0;
            }
        }

        // ---------------------------------------------------------------
        // C. 製作数が減った分（サンプル提供命令・関税）と、遅くされた分（トリプルチェック・逮捕）
        // ---------------------------------------------------------------

        /// <summary>1 回の製作が終わるたび、減った分と遅れた分を積む。</summary>
        [HarmonyPatch(typeof(CrafterFamObjectBase), "TryExecuteCraft")]
        private static class CraftPatch
        {
            private static void Postfix(bool __result, CrafterFamObjectBase __instance)
            {
                if (!__result || __instance == null) { return; }
                Recipe recipe = __instance.OnGoingRecipe;
                if (recipe == null || recipe.Result == null) { return; }

                int actual = recipe.Result.Count;
                int shortfall = BaseResultCount(recipe) - actual;
                if (shortfall > 0)
                {
                    string id = ActiveResultCutTax();
                    if (id != null) { AddResultLoss(Entry(id), recipe, shortfall); }
                }

                // トリプルチェック: 製作時間が m 倍になった分だけ回数が減る。
                // 対象かどうかは、本体の計算をこの場で 1 回呼んで確かめる
                if (actual > 0 && IsActive("LongerCraft") && IsSlowedNow(__instance, recipe))
                {
                    float m = TaxEffectParameters.LONGER_CRAFT_REQUIRE_TIME_MULTIPLIERS[
                        Clamp3(SlotOf("LongerCraft"))];
                    AddResultLoss(Entry("LongerCraft"), recipe,
                        Carry(SlowCarry, recipe, actual * (m - 1f)));
                }

                // 逮捕: 対象のスポンサーの使い魔だけ作業速度が落ちる
                if (actual > 0 && IsActive("LockRandomPatron") && IsLockedFam(__instance))
                {
                    float w = LockRandomPatronTaxEffect.GetWorkSpeedMultiplier(Clamp3(SlotOf("LockRandomPatron")));
                    if (w > 0f)
                    {
                        AddResultLoss(Entry("LockRandomPatron"), recipe,
                            Carry(LockCarry, recipe, actual * (1f / w - 1f)));
                    }
                }
            }
        }

        /// <summary>このレシピが、いま製作時間を延ばされているか。</summary>
        private static bool IsSlowedNow(CrafterFamObjectBase crafter, Recipe recipe)
        {
            try
            {
                float workSpeed = crafter.GetWorkSpeedPerSec();
                if (workSpeed <= 0f) { return false; }
                return TaxRecipeCraftTimeService.GetEffectiveRequireTime(recipe, workSpeed) > recipe.RequireTime;
            }
            catch (Exception) { return false; }
        }

        private static bool IsLockedFam(CrafterFamObjectBase crafter)
        {
            Patron locked = TaxPatronLockService.LockedPatron;
            if (locked == Patron.Undefined) { return false; }
            FamType famType;
            if (!Patrons.FAM.TryGetValue(locked, out famType)) { return false; }
            return crafter.GetFamType() == famType;
        }

        /// <summary>いま効いている「製作数を減らす課税」。同時に複数効くのは追撃だけ。</summary>
        private static string ActiveResultCutTax()
        {
            if (IsActive("SampleProvision")) { return "SampleProvision"; }
            if (IsActive("ReduceItemCraftResult")) { return "ReduceItemCraftResult"; }
            if (IsActive("ReduceTagCraftResult")) { return "ReduceTagCraftResult"; }
            return null;
        }

        /// <summary>本体が控えている素の製作数。毎回反射すると重いので、値が動いたときだけ引き直す。</summary>
        private static int BaseResultCount(Recipe recipe)
        {
            int actual = recipe.Result.Count;
            int seen;
            int cached;
            if (LastSeenCount.TryGetValue(recipe, out seen) && seen == actual
                && BaseCountCache.TryGetValue(recipe, out cached))
            {
                return cached;
            }
            int result = actual;
            if (_snapshotsField != null && _baseResultCountProp != null && _recipeModifiers != null)
            {
                try
                {
                    System.Collections.IDictionary snapshots =
                        _snapshotsField.GetValue(_recipeModifiers) as System.Collections.IDictionary;
                    if (snapshots != null && snapshots.Contains(recipe))
                    {
                        object snapshot = snapshots[recipe];
                        if (snapshot != null) { result = (int)_baseResultCountProp.GetValue(snapshot, null); }
                    }
                }
                catch (Exception) { }
            }
            LastSeenCount[recipe] = actual;
            BaseCountCache[recipe] = result;
            return result;
        }

        /// <summary>減った成果物を内訳に積む。アイテム・資源・召喚物で入る先が違う。</summary>
        private static readonly Dictionary<Recipe, float> SlowCarry = new Dictionary<Recipe, float>();
        private static readonly Dictionary<Recipe, float> LockCarry = new Dictionary<Recipe, float>();

        private static void AddResultLoss(TaxEntry entry, Recipe recipe, int lostCount)
        {
            if (lostCount <= 0) { return; }
            ItemCount itemCount = recipe.Result as ItemCount;
            if (itemCount != null)
            {
                AddItemLoss(entry, itemCount.ItemID, lostCount);
                return;
            }
            TagCount tagCount = recipe.Result as TagCount;
            if (tagCount != null)
            {
                // 成果物がタグのレシピ。資源なら返済に効くので見出しの「消えた資源」に、
                // バフ・レリックは返済にならないので内訳に回す。
                // 分けないと、刻印を何種類も削られた行が横に伸びて枠からはみ出す
                if (IsPayableTag(tagCount.TagID)) { entry.AddLostResource(tagCount.TagID, lostCount); }
                else { entry.AddLostTag(tagCount.TagID, lostCount); }
            }
        }

        /// <summary>返済に使える資源のタグか（現金・建材などと、特別任務の返済金）。
        /// 引けなかったときは資源として扱う（今までの出し方に倒す）。</summary>
        private static bool IsPayableTag(string tagID)
        {
            try
            {
                ItemTag.Type type = TagParamGetter.GetType(tagID);
                return type == ItemTag.Type.Resource || type == ItemTag.Type.Currency;
            }
            catch (Exception) { return true; }
        }

        private static int Clamp3(int slot)
        {
            if (slot < 0) { return 0; }
            if (slot > 2) { return 2; }
            return slot;
        }

        /// <summary>1 回あたりの減り分は 1 個に満たないことが多い（第一期の逮捕なら 0.11 個）。
        /// 毎回丸めると永久に 0 になるので、端数を持ち越して 1 個に届いた分だけ積む。</summary>
        private static int Carry(Dictionary<Recipe, float> carried, Recipe recipe, float lost)
        {
            if (lost <= 0f) { return 0; }
            float sum;
            carried.TryGetValue(recipe, out sum);
            sum += lost;
            int whole = (int)sum;
            carried[recipe] = sum - whole;
            return whole;
        }

        // ---------------------------------------------------------------
        // C. 増えた打数（鉱産税・農産税）
        // ---------------------------------------------------------------

        [HarmonyPatch(typeof(TakenDamageCalculator), "CheckProduceTimes")]
        private static class HarvestPatch
        {
            private static void Postfix(int __result, TakenDamageCalculator __instance)
            {
                if (__result <= 0 || _propField == null) { return; }
                try
                {
                    HarvestablePropertyKeeper prop = _propField.GetValue(__instance) as HarvestablePropertyKeeper;
                    if (prop == null) { return; }
                    HarvestableParameters p = prop.GetParameters();
                    if (p == null) { return; }
                    int extra = p.GetEffectiveToughness() - p.Toughness;
                    if (extra <= 0) { return; }
                    string id = HarvestableDB.IsTypeRock(p.Type) ? "HarvestRockPenalty"
                        : (HarvestableDB.IsTypePlant(p.Type) ? "HarvestTreePenalty" : null);
                    if (id == null || !IsActive(id)) { return; }
                    Entry(id).ExtraHits += extra * __result;

                    // 最初の 1 回だけ、実際の値をログに出す（本当に増えているかの確認用）
                    if (!_loggedHarvest)
                    {
                        _loggedHarvest = true;
                        LedgerHost.Trace("[harvest] " + p.Type + " toughness " + p.Toughness
                            + " -> " + p.GetEffectiveToughness() + " (+" + extra + " per item)");
                    }
                }
                catch (Exception) { }
            }
        }

        // ---------------------------------------------------------------
        // 検算（F5 で台帳と一緒に出す）
        // ---------------------------------------------------------------

        /// <summary>本体の設定値と、こちらが観測した値を並べて出す。
        /// 率が合っていれば拾い方が正しい。0 のままなら拾えていない。</summary>
        internal static void LogCheck()
        {
            // 期の一覧と、実際に掛かっているものを合わせて並べる。
            // 開発用に足した課税は一覧に載らないので、両方見ないと抜ける
            List<string> ids = new List<string>();
            List<int> slots = new List<int>();
            TaxSlotData[] scheduled = Slots();
            if (scheduled != null)
            {
                for (int i = 0; i < scheduled.Length; i++)
                {
                    ids.Add(scheduled[i].EffectID);
                    slots.Add(i);
                }
            }
            foreach (KeyValuePair<string, int> kv in ActiveSlots)
            {
                if (ids.Contains(kv.Key)) { continue; }
                ids.Add(kv.Key);
                slots.Add(kv.Value);
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder("[check]");
            if (ids.Count == 0)
            {
                sb.Append(" nothing applied (not in a shift?)");
                LedgerHost.Info(sb.ToString());
                return;
            }
            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                sb.Append('\n').Append("  ").Append(Verdict(id)).Append(' ')
                    .Append(id).Append(" slot").Append(slots[i])
                    .Append(IsActive(id) ? "" : " [off]");
                sb.Append(" expected=").Append(Expected(id, slots[i]));
                int tries, hits;
                Tries.TryGetValue(id, out tries);
                Hits.TryGetValue(id, out hits);
                if (tries > 0)
                {
                    sb.Append(" observed=").Append(hits).Append('/').Append(tries)
                        .Append(" (").Append((100f * hits / tries).ToString("0.0")).Append("%)");
                }
            }
            LedgerHost.Info(sb.ToString());
        }

        /// <summary>台帳にその課税の数字が入ったか。o なら拾えている、. はまだ 0。</summary>
        private static string Verdict(string effectID)
        {
            for (int i = 0; i < Ledger.Entries.Count; i++)
            {
                TaxEntry e = Ledger.Entries[i];
                if (e.EffectID != effectID) { continue; }
                // 率は掛けた時点で入るので、拾えた印にはしない。実測だけを見る
                if (e.Points != 0 || e.ExtraHits != 0 || e.CancelledSales != 0
                    || e.LostResources.Count > 0 || e.Items.Count > 0 || e.LostTags.Count > 0)
                {
                    return "o";
                }
            }
            return ".";
        }

        /// <summary>本体のパラメータ表から、その期の設定値を引く。</summary>
        private static string Expected(string effectID, int slot)
        {
            int i = slot < 0 ? 0 : (slot > 2 ? 2 : slot);
            switch (effectID)
            {
                case "CutLocalTag":
                    return TaxEffectParameters.CUT_LOCAL_TAG_CHANCE_PERCENTS[i] + "% of " + LocalTagTarget();
                case "CutBuffCash":
                    return TaxEffectParameters.CUT_BUFF_CASH_CHANCE_PERCENTS[i] + "% of buff cash";
                case "NoTagRandomPatron":
                    return TaxEffectParameters.NO_TAG_RANDOM_PATRON_CHANCE_PERCENTS[i] + "% of "
                        + TaxPatronResourceCutService.TargetPatron;
                case "Centralization":
                    return TaxEffectParameters.CENTRALIZATION_DELIVERY_CANCELLATION_CHANCE_PERCENTS[i] + "% of sales";
                case "HarvestRockPenalty":
                    return "toughness x" + TaxEffectParameters.HARVEST_ROCK_TOUGHNESS_MULTIPLIERS[i];
                case "HarvestTreePenalty":
                    return "toughness x" + TaxEffectParameters.HARVEST_TREE_TOUGHNESS_MULTIPLIERS[i];
                case "LongerCraft":
                    return "time x" + TaxEffectParameters.LONGER_CRAFT_REQUIRE_TIME_MULTIPLIERS[i]
                        + " over " + TaxEffectParameters.LONGER_CRAFT_REQUIRE_TIME_THRESHOLDS[i] + "s";
                case "SampleProvision":
                    return "result -> 1 when >= "
                        + TaxEffectParameters.SAMPLE_PROVISION_RESULT_COUNT_THRESHOLDS[i];
                case "ReduceTagCraftResult":
                    return "result " + TaxEffectParameters.REDUCE_TAG_CRAFT_RESULT_COUNT_DELTAS[i];
                case "ReduceItemCraftResult":
                    return "result " + TaxEffectParameters.REDUCE_ITEM_CRAFT_RESULT_COUNT_DELTAS[i];
                case "SlowConveyor":
                    return "conveyor -" + (TaxEffectParameters.SLOW_CONVEYOR_REDUCTION_RATES[i] * 100f) + "%";
                case "LockRandomPatron":
                    return "work speed x" + TaxEffectParameters.LOCK_RANDOM_PATRON_WORK_SPEED_MULTIPLIERS[i]
                        + " on " + TaxPatronLockService.LockedPatron;
                case "FamCountPenalty":
                    return "+" + TaxEffectParameters.FAM_COUNT_PENALTIES_PER_FAM[i] + " per fam";
                case "LandCountPenalty":
                    return "+" + TaxEffectParameters.LAND_COUNT_PENALTIES_PER_LAND[i] + " per land";
                case "OrderExecutePenalty":
                    return "+" + TaxEffectParameters.ORDER_EXECUTE_PENALTIES_PER_ORDER[i] + " per order";
                case "CutBuffResource":
                    return TaxEffectParameters.CUT_BUFF_RESOURCE_CHANCE_PERCENTS[i] + "% of buff resources";
                case "LowPriceCashCut":
                    return "no cash at price <= " + TaxEffectParameters.LOW_PRICE_CASH_THRESHOLDS[i];
                case "HighPriceCashCut":
                    return "no cash at price >= " + TaxEffectParameters.HIGH_PRICE_CASH_THRESHOLDS[i];
                case "BlockTopSales":
                    return "top " + TaxEffectParameters.BLOCK_TOP_SALES_RANK_COUNTS[i] + " blocked";
                case "ResourceDeficit":
                    return "balance -> " + TaxEffectParameters.RESOURCE_DEFICIT_TARGET_BALANCES[i];
                case "ReducePactRarity":
                    return "-" + TaxEffectParameters.PACT_RARITY_REDUCTION_POINTS[i] + "% rare pacts";
            }
            return "-";
        }

        private static string LocalTagTarget()
        {
            if (_localTagTargetField == null) { return "None"; }
            try { return _localTagTargetField.GetValue(null) as string; }
            catch (Exception) { return "None"; }
        }

        private static bool IsTargetTag(Patron patron, string tagID)
        {
            if (patron == Patron.Undefined) { return false; }
            string target;
            if (!Patrons.TAG.TryGetValue(patron, out target)) { return false; }
            return target == tagID;
        }

        // ---------------------------------------------------------------
        // アイテムの内訳（売却で得られたはずの資源を、そのときの値で積む）
        // ---------------------------------------------------------------

        internal static void AddItemLoss(TaxEntry entry, string itemID, int count)
        {
            if (string.IsNullOrEmpty(itemID) || count <= 0) { return; }
            Item item = null;
            try { item = ItemDB.GetItemByID(itemID); }
            catch (Exception) { }
            ItemLoss loss = entry.GetItem(itemID);
            if (item == null)
            {
                loss.Count += count;
                return;
            }
            loss.Add(count,
                item.tag1Runtime, item.tag1CountRuntime,
                item.tag2Runtime, item.tag2CountRuntime,
                item.tag3Runtime, item.tag3CountRuntime);
        }

        private static int TagAmountOfDepositItem(string tagID)
        {
            if (_depositItemID == null || _depositCount <= 0) { return 0; }
            Item item = null;
            try { item = ItemDB.GetItemByID(_depositItemID); }
            catch (Exception) { }
            if (item == null) { return 0; }
            int per = 0;
            if (item.tag1Runtime == tagID) { per += item.tag1CountRuntime; }
            if (item.tag2Runtime == tagID) { per += item.tag2CountRuntime; }
            if (item.tag3Runtime == tagID) { per += item.tag3CountRuntime; }
            return per * _depositCount;
        }
    }
}
