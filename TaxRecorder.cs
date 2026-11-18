// 業務日誌 — 課税で取られた分の記録。プラグイン本体の続き（partial）。
//
// 課税通知書（LogTaxView.cs）に出す「取られた分」は、Run の最中に数えておかないと後から分からない。
// 数える処理は Tax Ledger（mods/tax_ledger）のものを、写しを作らずそのまま取り込んでビルドしている
// （build.ps1。Ledger.cs・TaxHooks.cs・LedgerFile.cs・LedgerHost.cs）。Tax Ledger を入れていない人でも、
// Economy Graph だけで課税通知書が全部出る（2026-11-18。それまでは Tax Ledger が無いと金額が空欄だった）。
//
// **両方入っているときの振り分け**（数えるのも、控えを書くのも、必ずどちらか一方だけにする）:
//
//   Tax Ledger の状態                                   数えて控えを書くのは
//   ─────────────────────────────────────────────────  ────────────────────
//   入っていない／起動に失敗して止まっている            Economy Graph
//   入っているが古い（控えを書く機能が無い。1.1.0 まで）  Economy Graph（Tax Ledger は結果画面の欄だけ）
//   入っていて、控えを書く設定（Save ledger）が切         Economy Graph
//   入っていて、控えを書く                               Tax Ledger（Economy Graph は割り込みを当てない）
//
// 2つとも数える場合（上の2・3行目）、同じ本体のメソッドに2つの MOD の割り込みが掛かる。割り込みは読むだけで
// 本体の値を変えないので、ゲームの動きは変わらない（台帳は MOD ごとに別々に持つ）。控えを書くのは片方だけ。
//
// 決めるのは起動して最初の Update。Awake の時点では、後から読み込まれる Tax Ledger がまだ居ないことがある
// （BepInEx は全部のプラグインの Awake を済ませてから最初の Update を回す。ScriptEngine の F6 も同じ）。
// 途中で Tax Ledger の設定を変えても、振り分けは次に起動する（か F6 を押す）まで変わらない。
//
// 割り込みが1つでも当たらなければ、当てた分も全部外して記録をやめる
// （一部の課税だけ拾う台帳は数字が狂う。Tax Ledger と同じ決め）。

using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using LwfTaxLedger;
using UnityEngine;

namespace LwfEconomyGraph
{
    public sealed partial class EconomyGraphPlugin
    {
        private const string TaxLedgerGuid = "kiyonakanata.lwftaxledger";

        private Harmony _taxHarmony;
        private bool _taxDecided;
        private bool _taxOwn;        // 課税を Economy Graph が数えている
        private int _taxBootFrame = -1;

        /// <summary>Update から毎フレーム呼ぶ。最初の1回だけ、課税を誰が数えるかを決める。</summary>
        private void TrackTaxRecorder()
        {
            if (_taxDecided) { return; }
            if (_taxBootFrame < 0) { _taxBootFrame = Time.frameCount; return; }
            if (Time.frameCount <= _taxBootFrame) { return; }
            _taxDecided = true;

            if (LogEnabled == null || !LogEnabled.Value) { return; }
            try
            {
                string state = TaxLedgerState();
                if (state == null)
                {
                    Logger.LogInfo("[tax] Tax Ledger records the taxes; Economy Graph leaves them to it");
                    return;
                }
                StartTaxRecorder();
                Logger.LogInfo("[tax] Economy Graph records the taxes (" + state + ")");
            }
            catch (Exception e)
            {
                StopTaxRecorder();
                Logger.LogWarning("[tax] cannot record the taxes; the tax notice shows names only: " + e);
            }
        }

        /// <summary>Tax Ledger が課税を数えて控えを書いてくれるなら null。そうでなければ、その理由（ログ用）。</summary>
        /// <summary>いま動いている、その GUID のプラグイン。無ければ null。
        /// FindObjectsByType では見つからない：BepInEx も ScriptEngine も、プラグインを載せる GameObject を
        /// 隠し物（HideFlags.HideAndDontSave）にしていて、隠し物は FindObjectsByType の対象外。
        /// 2026-11-18 まで FindObjectsByType で探していて、Tax Ledger が入っているのに「入っていない」と判定し、
        /// 両方が課税を数えて同じ控えを書いていた。隠し物も返す Resources.FindObjectsOfTypeAll で探す。</summary>
        internal static BaseUnityPlugin FindPlugin(string guid)
        {
            BaseUnityPlugin[] plugins = Resources.FindObjectsOfTypeAll<BaseUnityPlugin>();
            BaseUnityPlugin found = null;
            for (int i = 0; i < plugins.Length; i++)
            {
                BaseUnityPlugin plugin = plugins[i];
                if (plugin == null || plugin.gameObject == null) { continue; }
                object[] attributes = plugin.GetType().GetCustomAttributes(typeof(BepInPlugin), false);
                if (attributes.Length == 0 || ((BepInPlugin)attributes[0]).GUID != guid) { continue; }
                // 読み直し（F6）の直後は、消える途中の古いものが残っていることがある。動いているほうを優先
                if (found == null || (plugin.enabled && plugin.gameObject.activeInHierarchy)) { found = plugin; }
            }
            return found;
        }

        private string TaxLedgerState()
        {
            BaseUnityPlugin[] plugins = new BaseUnityPlugin[] { FindPlugin(TaxLedgerGuid) };
            for (int i = 0; i < plugins.Length; i++)
            {
                BaseUnityPlugin plugin = plugins[i];
                if (plugin == null || plugin == this) { continue; }
                Type type = plugin.GetType();

                // 起動に失敗した Tax Ledger は、割り込みを全部外して自分を止めている
                if (!plugin.enabled) { return "Tax Ledger is disabled"; }

                // 控えを書く機能があるか（公開済みの 1.1.0 までには無い）。版の番号ではなく、機能そのものを見る
                Type file = type.Assembly.GetType("LwfTaxLedger.LedgerFile");
                MethodInfo save = file != null
                    ? file.GetMethod("SaveRun", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    : null;
                if (save == null) { return "Tax Ledger is too old to save its ledger"; }
                _taxLedgerSave = save;

                FieldInfo field = type.GetField("SaveLedger", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                ConfigEntry<bool> entry = field != null ? field.GetValue(null) as ConfigEntry<bool> : null;
                if (entry == null || !entry.Value) { return "Tax Ledger has Save ledger off"; }
                return null;
            }
            return "Tax Ledger is not installed";
        }

        private void StartTaxRecorder()
        {
            LedgerHost.Log = Logger;
            LedgerHost.TraceEnabled = null;
            LedgerHost.SaveEnabled = delegate { return LogEnabled != null && LogEnabled.Value; };
            LedgerHost.RunStarting = null;

            _taxHarmony = new Harmony(PluginGuid + ".tax");
            TaxHooks.Init(_taxHarmony);
            // 割り込みは TaxHooks の中の入れ子のクラス。このアセンブリ全体に PatchAll を掛けると、
            // 取り込んでいない開発用の割り込み（DevTax）を前提にした作りに引きずられるので、TaxHooks の分だけ当てる
            Type[] nested = typeof(TaxHooks).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public);
            for (int i = 0; i < nested.Length; i++)
            {
                if (nested[i].GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) { continue; }
                _taxHarmony.CreateClassProcessor(nested[i]).Patch();
            }
            int patched = 0;
            foreach (MethodBase m in _taxHarmony.GetPatchedMethods()) { patched++; }
            Logger.LogInfo("[tax] patches=" + patched);
            _taxOwn = true;
        }

        private void StopTaxRecorder()
        {
            _taxOwn = false;
            if (_taxHarmony == null) { return; }
            try { _taxHarmony.UnpatchSelf(); }
            catch (Exception e) { Logger.LogWarning("[tax] unpatch failed: " + e.Message); }
            _taxHarmony = null;
        }

        /// <summary>Tax Ledger の「控えを書く」（LedgerFile.SaveRun）。Tax Ledger が数えているときに、こちらから呼ぶ</summary>
        private MethodInfo _taxLedgerSave;

        /// <summary>Run の終わりに、数えた分を控えへ書く。
        /// Tax Ledger が数えているときは、Tax Ledger に書かせる。Tax Ledger が自分で書くのはリザルト画面・次の Run の
        /// 始まり・ゲームの終了のときだけで、Run を途中で離れる（タイトルへ戻る）と、次の Run が始まるまで控えが無く、
        /// 業務日誌の課税通知書に内訳が出ない。同じ中身を後で Tax Ledger がもう一度書いても、上書きになるだけ。</summary>
        private void SaveRunTaxes(string why)
        {
            if (_taxOwn)
            {
                LedgerFile.SaveRun(why);
                return;
            }
            if (_taxLedgerSave == null) { return; }
            try { _taxLedgerSave.Invoke(null, new object[] { why }); }
            catch (Exception e) { Logger.LogWarning("[tax] cannot ask Tax Ledger to save: " + e.Message); }
        }
    }
}
