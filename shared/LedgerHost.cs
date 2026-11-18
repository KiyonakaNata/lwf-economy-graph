// 数える部分（Ledger.cs・TaxHooks.cs・LedgerFile.cs）が、自分を載せている MOD に頼むこと。
//
// 数える部分は2つの MOD がビルドする（写しは作らず、同じソースを取り込む）:
//   Tax Ledger     … 結果画面に課税の欄を出す。Run ごとの控えも書く
//   Economy Graph  … 業務日誌の課税通知書に取られた分を出すため、Tax Ledger が入っていないときに自分で数える
//                    （mods/economy_graph/TaxRecorder.cs。両方入っているときの振り分けもそこ）
// どちらに載っても同じに動くよう、ログの出し先・設定・開発用の後始末は、載せている側がここに差し込む。
// 数える部分からは TaxLedgerPlugin・LedgerView・DevTax を直に呼ばないこと（Economy Graph には無い）。

using System;
using BepInEx.Logging;
using GameState;

namespace LwfTaxLedger
{
    internal static class LedgerHost
    {
        internal static ManualLogSource Log;

        /// <summary>開発中だけ見たい細かいログを出すか。null なら出さない。</summary>
        internal static Func<bool> TraceEnabled;

        /// <summary>Run ごとの控えを書くか。null なら書かない。</summary>
        internal static Func<bool> SaveEnabled;

        /// <summary>新しい Run が始まる直前に呼ぶ（Tax Ledger は開発用に掛けた課税を外す）。null なら何もしない。</summary>
        internal static Action RunStarting;

        internal static void Trace(string line)
        {
            if (Log != null && TraceEnabled != null && TraceEnabled()) { Log.LogInfo(line); }
        }

        internal static void Info(string line)
        {
            if (Log != null) { Log.LogInfo(line); }
        }

        internal static void Warn(string line)
        {
            if (Log != null) { Log.LogWarning(line); }
        }

        /// <summary>いまの Run で返済に使う資源の控え。課税が掛かったときに覚え、新しい Run の開始で捨てる。
        /// 控えのファイルを書くのは Run を離れた後や次の Run の開始時のことがあり、そのときに引くと
        /// 別の Run の値になる（か、引けずに現金になる）。</summary>
        internal static string RunPayTag;

        /// <summary>そのシフトで返済に使う資源。特別任務では現金とは限らない（培養プラントは薬品）。
        /// 最後の1回だけアイテム納品になる任務ではタグが無い（"None"）ので、その場合は現金に倒す。</summary>
        internal static string PayTag()
        {
            try
            {
                GameStateManager manager = GameStateManager.Instance;
                WinCondition win = manager != null ? manager.GetWinCondition() : null;
                string tag = win != null ? win.GetTargetTag() : null;
                if (string.IsNullOrEmpty(tag) || tag == "None") { return "Cash"; }
                return tag;
            }
            catch (Exception) { return "Cash"; }
        }
    }
}
