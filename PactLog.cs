// 業務日誌 — 取った契約のカードの控え。プラグイン本体の続き（partial）。
//
// 契約の効果の文には、取った時点の状況で決まる数字が入る（「獲得数 = 返済完了数 + 1」の獲得数など）。
// 本体の Run の記録に残るのは契約の ID だけで、後から図鑑用の口で文を作ると数字の所が「-」になる。
// 本体は Run 中、契約を取るたびに「そのとき表示したカード」を契約履歴用に持ち、取ったことを
// PactHistoryStore.Added で知らせている。そのカードの文を控えて、Run の終わりに `<開始時刻>.pacts.json` へ書く。
//
// 形: JSON の配列。取った順に { id, name, description, rarity, time, catalog, lang }。
//   catalog は同じ言語で本体が作る図鑑用の文、lang は言語（en・ja…）。別の言語で文を出し直すのに使う（LogPactText.cs）。
//   2026-11-18 より前の控えには無く、同じ言語でその Run を開いたときに足される。
//   description と rarity は本体の書式タグ（色・リンク・資源アイコン）が付いたまま、撮ったときの言語のまま。
//   time は取った時点の経過秒。
// F6 の読み直しをまたぐ持ち越しはしていない（開発中に読み直すと、それまでに取った分は抜ける）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UI.PactHistory;
using UI.SelectableWindow.Cell;

namespace LwfEconomyGraph
{
    public sealed partial class EconomyGraphPlugin
    {
        internal const string PactSuffix = ".pacts.json";

        private readonly List<JObject> _pactCards = new List<JObject>();

        private void HookPactCards()
        {
            PactHistoryStore.Added += OnPactCard;
        }

        private void UnhookPactCards()
        {
            PactHistoryStore.Added -= OnPactCard;
        }

        private void OnPactCard(PactCellSnapshot card)
        {
            if (card == null || _boundGame == null) { return; }
            if (LogEnabled == null || !LogEnabled.Value) { return; }
            try
            {
                JObject o = new JObject();
                o["id"] = card.PactID;
                o["name"] = card.BuffNameText;
                o["description"] = card.DescriptionText;
                o["rarity"] = card.RarityText;
                // 文を別の言語で出し直すための手がかり（LogPactText.cs）：同じ言語の図鑑用の文と、言語
                // （catalog＝同じ言語の図鑑用の文、lang＝言語、vars＝数字ではない穴の中身）
                o["time"] = _elapsed;
                LogView.AddRecastHints(o);
                _pactCards.Add(o);
            }
            catch (Exception e)
            {
                Logger.LogWarning("[log] cannot keep the contract card: " + e.Message);
            }
        }

        /// <summary>控えたカードをファイルへ書く。グラフの記録を残すときに一緒に呼ぶ。</summary>
        private void SavePactCards()
        {
            if (_pactCards.Count == 0) { return; }
            try
            {
                string dir = GraphDirectory();
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, _runStartUtc.ToString(GraphStamp, CultureInfo.InvariantCulture) + PactSuffix);
                JArray all = new JArray();
                for (int i = 0; i < _pactCards.Count; i++) { all.Add(_pactCards[i]); }
                File.WriteAllText(path, all.ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false));
                Logger.LogInfo("[log] saved " + _pactCards.Count + " contract cards: " + Path.GetFileName(path));
            }
            catch (Exception e)
            {
                Logger.LogWarning("[log] cannot save the contract cards: " + e.Message);
            }
        }
    }
}
