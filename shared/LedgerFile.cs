// 台帳をテキストに落とす。使い道は2つ。
//
// 1. Run ごとの控え（SaveRun）。Economy Graph の業務日誌が、過去の Run の課税で取られた分を出すのに読む。
//    置き場は Economy Graph と同じ BepInEx/LwfEconomy/runs/ で、名前は「記録を始めた時刻（UTC）.tax.txt」。
//    業務日誌はこの時刻が本体の Run の記録の開始〜終了の間に入っているもので対応させる
//    （本体の Run の ID は internal の奥にあり、取りに行くと版上げに弱い。mods/economy_graph/RunLog.cs と同じ決め）。
//    書くのは、結果画面に課税欄を出したとき・次の Run が始まるとき・ゲームを終えるとき。同じ Run は同じ名前で上書きする。
//    Economy Graph が入っていなくても書く（後から入れたときに過去の分が見られる）。cfg の Save ledger で切れる。
//
// 2. 開発用（Save / Load）。台帳を 1 つのテキストに落とし、読み直したあとに同じ内容で組み直せるようにする。
//
// ScriptEngine の F6 は静的な値を全部作り直すので、読み直すと台帳が空になる。
// 結果画面は空なら触らない作りなので、そのままでは「結果画面を出したまま見た目を詰める」が
// できない。そこで結果画面を組んだ時点の中身をファイルに残し、読み直したあとの F7 で読ませる。
//
// 2 は配布版には要らない（[9. Dev] Enabled が切なら書きも読みもしない）。
//
// 形は1行1件のタブ区切り。先頭の語で種類を分ける（知らない語の行は読み飛ばす）:
//   pay      返済に使う資源のタグ（Run ごとの控えだけ）
//   entry    枠 / 課税ID / 固定費用の点数 / 無効になった売却の回数 / スポンサー / 増えた打数 / 掛かっていた秒数 / 率の文
//   res      その課税で消えた資源：タグ / 量
//   tag      その課税で減ったバフ・レリック：タグ / 点
//   item     その課税で減ったアイテム：ID / 個数
//   itemres  直前の item の資源換算：タグ / 量

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;

namespace LwfTaxLedger
{
    internal static class LedgerFile
    {
        private const string FileName = "LwfTaxLedger-last.txt";
        private const char Sep = '\t';

        private static string Path()
        {
            try { return System.IO.Path.Combine(Paths.BepInExRootPath, FileName); }
            catch (Exception) { return FileName; }
        }

        // ---------------------------------------------------------------
        // Run ごとの控え
        // ---------------------------------------------------------------

        private const string RunStartKey = "lwf.taxledger.runstart";
        private const string RunSuffix = ".tax.txt";
        private static DateTime _runStartUtc;

        /// <summary>新しい Run の台帳を始めたときに呼ぶ。</summary>
        internal static void MarkRunStart()
        {
            _runStartUtc = DateTime.UtcNow;
            // F6 の読み直しをまたいでも同じ Run の名前で書けるよう、AppDomain に置く
            try { AppDomain.CurrentDomain.SetData(RunStartKey, _runStartUtc.Ticks); }
            catch (Exception) { }
        }

        /// <summary>いまの Run の台帳を控えに書く。課税が1つも無い Run では何も書かない。</summary>
        internal static void SaveRun(string why)
        {
            if (LedgerHost.SaveEnabled == null || !LedgerHost.SaveEnabled()) { return; }
            if (Ledger.Entries.Count == 0 || Ledger.IsDummy) { return; }
            try
            {
                if (_runStartUtc.Ticks == 0L)
                {
                    object ticks = AppDomain.CurrentDomain.GetData(RunStartKey);
                    if (ticks is long) { _runStartUtc = new DateTime((long)ticks, DateTimeKind.Utc); }
                }
                // 始まりを見ていない（Run の途中で入れた）ときは、いまの時刻で代える。Run の間に入っていれば対応は付く
                if (_runStartUtc.Ticks == 0L) { MarkRunStart(); }

                string dir = System.IO.Path.Combine(System.IO.Path.Combine(Paths.BepInExRootPath, "LwfEconomy"), "runs");
                Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir,
                    _runStartUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + RunSuffix);
                string text = "pay" + Sep + (LedgerHost.RunPayTag ?? LedgerHost.PayTag()) + '\n' + OrderLine() + Serialize();
                File.WriteAllText(path, text, new UTF8Encoding(false));
                LedgerHost.Info("[ledger] saved (" + why + "): " + System.IO.Path.GetFileName(path)
                    + "  entries=" + Ledger.Entries.Count);
            }
            catch (Exception e)
            {
                LedgerHost.Warn("[ledger] cannot save the ledger: " + e.Message);
            }
        }

        // 課税の並び（第一期〜第三期に何が入っているか）。
        // 本体は Run の途中でも、まだ発動していない課税の順番を入れ替えられる（TaxSlotService.TryMove）。ところが本体の
        // Run の記録（run.json の taxSlots）は Run の始まりの並びのままで、入れ替えが反映されない。
        // 業務日誌が正しい並びを出せるよう、Run が終わった時点の並びをここに残す。
        // 同じ Run の控えは何度か書き直される（リザルト・次の Run の始まり・終了）。後から執務室で並びを変えても
        // 引きずらないよう、その Run で最初に書いたときの並びを使い回す。
        private static long _orderFor;
        private static string _order = "";

        private static string OrderLine()
        {
            if (_orderFor != _runStartUtc.Ticks || _order.Length == 0)
            {
                _orderFor = _runStartUtc.Ticks;
                _order = "";
                try
                {
                    string[] ids = Utility.SaveData.LwfSaveDataAccessor.GetOfficeTaxEffectIDs();
                    if (ids != null && ids.Length > 0) { _order = "order" + Sep + string.Join(Sep.ToString(), ids) + '\n'; }
                }
                catch (Exception) { _order = ""; }
            }
            return _order;
        }

        // ---------------------------------------------------------------
        // 開発用
        // ---------------------------------------------------------------

        /// <summary>いまの台帳を残す。中身が無いときは何もしない。</summary>
        internal static void Save()
        {
            if (Ledger.Entries.Count == 0 || Ledger.IsDummy) { return; }
            try
            {
                File.WriteAllText(Path(), Serialize(), new UTF8Encoding(false));
                LedgerHost.Trace("[dev] saved the ledger to " + FileName);
            }
            catch (Exception e)
            {
                LedgerHost.Warn("[dev] cannot save the ledger: " + e.Message);
            }
        }

        private static string Serialize()
        {
            {
                StringBuilder sb = new StringBuilder(1024);
                for (int i = 0; i < Ledger.Entries.Count; i++)
                {
                    TaxEntry e = Ledger.Entries[i];
                    sb.Append("entry").Append(Sep).Append(e.Slot).Append(Sep).Append(e.EffectID)
                        .Append(Sep).Append(e.Points).Append(Sep).Append(e.CancelledSales)
                        .Append(Sep).Append(e.PatronID).Append(Sep).Append(e.ExtraHits)
                        .Append(Sep).Append(e.ActiveSeconds.ToString("0.###", CultureInfo.InvariantCulture))
                        .Append(Sep).Append(e.RateText == null ? "" : e.RateText).Append('\n');
                    foreach (KeyValuePair<string, int> kv in e.LostResources)
                    {
                        sb.Append("res").Append(Sep).Append(kv.Key).Append(Sep).Append(kv.Value).Append('\n');
                    }
                    foreach (KeyValuePair<string, int> kv in e.LostTags)
                    {
                        sb.Append("tag").Append(Sep).Append(kv.Key).Append(Sep).Append(kv.Value).Append('\n');
                    }
                    foreach (KeyValuePair<string, ItemLoss> kv in e.Items)
                    {
                        ItemLoss loss = kv.Value;
                        sb.Append("item").Append(Sep).Append(loss.ItemID).Append(Sep).Append(loss.Count).Append('\n');
                        foreach (KeyValuePair<string, int> r in loss.Resources)
                        {
                            sb.Append("itemres").Append(Sep).Append(r.Key).Append(Sep).Append(r.Value).Append('\n');
                        }
                    }
                }
                return sb.ToString();
            }
        }

        /// <summary>残してあった台帳を読み込む。いまの中身は捨てる。</summary>
        internal static bool Load()
        {
            string path = Path();
            try
            {
                if (!File.Exists(path)) { return false; }
                string[] lines = File.ReadAllLines(path);
                Ledger.Reset();
                TaxEntry entry = null;
                ItemLoss item = null;
                for (int i = 0; i < lines.Length; i++)
                {
                    string[] f = lines[i].Split(Sep);
                    if (f.Length < 2) { continue; }
                    switch (f[0])
                    {
                        case "entry":
                            if (f.Length < 9) { break; }
                            entry = Ledger.Get(f[2], Int(f[1]));
                            entry.Points = Int(f[3]);
                            entry.CancelledSales = Int(f[4]);
                            entry.PatronID = Int(f[5]);
                            entry.ExtraHits = Int(f[6]);
                            entry.ActiveSeconds = Float(f[7]);
                            entry.RateText = f[8].Length > 0 ? f[8] : null;
                            item = null;
                            break;
                        case "res":
                            if (entry != null && f.Length >= 3) { entry.AddLostResource(f[1], Int(f[2])); }
                            break;
                        case "tag":
                            if (entry != null && f.Length >= 3) { entry.AddLostTag(f[1], Int(f[2])); }
                            break;
                        case "item":
                            if (entry == null || f.Length < 3) { break; }
                            item = entry.GetItem(f[1]);
                            item.Count += Int(f[2]);
                            break;
                        case "itemres":
                            if (item != null && f.Length >= 3) { item.AddTag(f[1], Int(f[2])); }
                            break;
                    }
                }
                LedgerHost.Info("[dev] loaded the last ledger: entries=" + Ledger.Entries.Count);
                return Ledger.Entries.Count > 0;
            }
            catch (Exception e)
            {
                LedgerHost.Warn("[dev] cannot load the ledger: " + e.Message);
                return false;
            }
        }

        private static int Int(string s)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static float Float(string s)
        {
            float v;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0f;
        }
    }
}
