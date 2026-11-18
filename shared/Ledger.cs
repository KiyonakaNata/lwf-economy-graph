// 台帳（データ）。ゲームにも BepInEx にも依存しない。
//
// シフト 1 回分の「課税で失ったもの」を、期（slot）と課税（EffectID）ごとに持つ。
// 数え方は課税の性質で分かれる（README の A/B/C）。

using System;
using System.Collections.Generic;

namespace LwfTaxLedger
{
    /// <summary>あるアイテムが減った分。換算（売却で得られたはずの資源）は減った時点の値で積む。</summary>
    internal sealed class ItemLoss
    {
        public string ItemID;
        public int Count;
        public readonly Dictionary<string, int> Resources = new Dictionary<string, int>();

        public void Add(int count, string tag1, int c1, string tag2, int c2, string tag3, int c3)
        {
            Count += count;
            AddTag(tag1, c1 * count);
            AddTag(tag2, c2 * count);
            AddTag(tag3, c3 * count);
        }

        public void AddTag(string tag, int amount)
        {
            if (string.IsNullOrEmpty(tag) || tag == "None" || amount == 0) { return; }
            int cur;
            Resources.TryGetValue(tag, out cur);
            Resources[tag] = cur + amount;
        }
    }

    /// <summary>1 つの課税（= 1 つの期）の累計。使う欄は課税の種類で違う。</summary>
    internal sealed class TaxEntry
    {
        public string EffectID;
        public int Slot;

        // A: 固定費用の点数（人頭税・固定資産税・消費税）
        public int Points;

        // B: 消えた資源（投資課税・損害賠償・特産品税）。tag -> 量
        public readonly Dictionary<string, int> LostResources = new Dictionary<string, int>();

        // C: 減ったバフ・レリック（成果物がタグのレシピを関税などが削ったとき）。tag -> 点
        // 返済には使えないので、見出しには出さず内訳だけに出す
        public readonly Dictionary<string, int> LostTags = new Dictionary<string, int>();

        // B: 無効になった売却の回数（中央集権）。中身は Items に積む
        public int CancelledSales;

        // 対象になったスポンサー（損害賠償・逮捕）。-1 は無し
        public int PatronID = -1;

        // C: 減ったアイテム（サンプル提供・関税・トリプルチェック・逮捕）。itemID -> 減った分
        public readonly Dictionary<string, ItemLoss> Items = new Dictionary<string, ItemLoss>();

        // C: 増えた打数（鉱産税・農産税）
        public int ExtraHits;

        // C: 率だけで示すもの（節水令のコンベア速度）。本体の GetValueText をそのまま持つ
        public string RateText;

        // 効果が掛かっていた秒数
        public float ActiveSeconds;

        public ItemLoss GetItem(string itemID)
        {
            ItemLoss loss;
            if (!Items.TryGetValue(itemID, out loss))
            {
                loss = new ItemLoss();
                loss.ItemID = itemID;
                Items[itemID] = loss;
            }
            return loss;
        }

        public void AddLostResource(string tag, int amount)
        {
            if (string.IsNullOrEmpty(tag) || tag == "None" || amount == 0) { return; }
            int cur;
            LostResources.TryGetValue(tag, out cur);
            LostResources[tag] = cur + amount;
        }

        public void AddLostTag(string tag, int amount)
        {
            if (string.IsNullOrEmpty(tag) || tag == "None" || amount == 0) { return; }
            int cur;
            LostTags.TryGetValue(tag, out cur);
            LostTags[tag] = cur + amount;
        }

        public bool IsEmpty
        {
            get
            {
                return Points == 0 && LostResources.Count == 0 && CancelledSales == 0
                    && Items.Count == 0 && LostTags.Count == 0
                    && ExtraHits == 0 && string.IsNullOrEmpty(RateText);
            }
        }
    }

    internal static class Ledger
    {
        public static readonly List<TaxEntry> Entries = new List<TaxEntry>();

        /// <summary>いまの中身が見た目確認用のダミーか。実データが来たら捨てる</summary>
        public static bool IsDummy;

        /// <summary>ダミーが支払いに使う資源。特別任務では現金とは限らないので、
        /// 呼ぶ側が本体の値を入れる（この中ではゲームを参照しない）</summary>
        public static string DummyPayTag = "Cash";

        public static void Reset()
        {
            Entries.Clear();
            IsDummy = false;
        }

        /// <summary>実データを積む前に呼ぶ。ダミーが入っていれば流す。</summary>
        public static void DropDummy()
        {
            if (!IsDummy) { return; }
            Reset();
            LedgerHost.Trace("[ledger] dropped the dummy; recording the real shift");
        }

        public static TaxEntry Get(string effectID, int slot)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].Slot == slot && Entries[i].EffectID == effectID) { return Entries[i]; }
            }
            TaxEntry entry = new TaxEntry();
            entry.EffectID = effectID;
            entry.Slot = slot;
            Entries.Add(entry);
            Entries.Sort(delegate (TaxEntry a, TaxEntry b) { return a.Slot.CompareTo(b.Slot); });
            return entry;
        }

        /// <summary>いまの中身をログに書く（開発用。実データの検算に使う）。</summary>
        public static void Dump()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("[ledger] entries=").Append(Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                TaxEntry e = Entries[i];
                sb.Append('\n').Append("  slot").Append(e.Slot).Append(' ').Append(e.EffectID);
                if (e.Points != 0) { sb.Append(" points=").Append(e.Points); }
                if (e.CancelledSales != 0) { sb.Append(" sales=").Append(e.CancelledSales); }
                if (e.ExtraHits != 0) { sb.Append(" hits=").Append(e.ExtraHits); }
                if (!string.IsNullOrEmpty(e.RateText)) { sb.Append(" rate=").Append(e.RateText); }
                if (e.PatronID > 0) { sb.Append(" patron=").Append(e.PatronID); }
                foreach (KeyValuePair<string, int> kv in e.LostResources)
                {
                    sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
                }
                foreach (KeyValuePair<string, int> kv in e.LostTags)
                {
                    sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
                }
                foreach (KeyValuePair<string, ItemLoss> kv in e.Items)
                {
                    sb.Append('\n').Append("    ").Append(kv.Key).Append(" x").Append(kv.Value.Count);
                    foreach (KeyValuePair<string, int> r in kv.Value.Resources)
                    {
                        sb.Append(' ').Append(r.Key).Append('=').Append(r.Value);
                    }
                }
            }
            LedgerHost.Info(sb.ToString());
        }

        // ---------------------------------------------------------------
        // ダミーデータ。表示の方向が固まるまで、結果画面へ行かずに枠を確かめるためのもの。
        // 15 種すべてがどこかの組に入るよう 5 組用意し、押すたびに次の組へ回す。
        // ---------------------------------------------------------------

        private static readonly string[][] DummySets = new string[][]
        {
            // 1 組目は名前の長い課税を並べる（アイコンや数字と当たらないか見るため）
            new string[] { "LongerCraft", "ReduceItemCraftResult", "SlowConveyor" },
            new string[] { "LandCountPenalty", "CutBuffCash", "HarvestRockPenalty" },
            new string[] { "OrderExecutePenalty", "Centralization", "LockRandomPatron" },
            new string[] { "CutLocalTag", "SampleProvision", "HarvestTreePenalty" },
            new string[] { "NoTagRandomPatron", "ReduceTagCraftResult", "FamCountPenalty" },
            // 0.30 の 6 種
            new string[] { "BlockTopSales", "LowPriceCashCut", "ResourceDeficit" },
            new string[] { "CutBuffResource", "ReducePactRarity", "HighPriceCashCut" },
        };

        public static int DummySetCount { get { return DummySets.Length; } }

        public static void FillDummy(int setIndex)
        {
            Reset();
            IsDummy = true;
            string[] set = DummySets[((setIndex % DummySets.Length) + DummySets.Length) % DummySets.Length];
            System.Random rng = new System.Random(setIndex * 7919 + 17);
            for (int slot = 0; slot < set.Length; slot++)
            {
                TaxEntry e = Get(set[slot], slot);
                e.ActiveSeconds = 600f;
                FillDummyEntry(e, rng);
            }
        }

        private static void FillDummyEntry(TaxEntry e, System.Random rng)
        {
            switch (e.EffectID)
            {
                case "FamCountPenalty":
                    e.Points = 160 * (40 + rng.Next(60));
                    e.AddLostResource(DummyPayTag, e.Points * (1 + rng.Next(6)));
                    break;
                case "LandCountPenalty":
                    e.Points = 800 * (20 + rng.Next(30));
                    e.AddLostResource(DummyPayTag, e.Points * (1 + rng.Next(6)));
                    break;
                case "OrderExecutePenalty":
                    e.Points = 500 * (8 + rng.Next(30));
                    e.AddLostResource(DummyPayTag, e.Points * (1 + rng.Next(4)));
                    break;
                case "CutBuffCash":
                    e.AddLostResource("Cash", 8000 + rng.Next(90000));
                    break;
                case "NoTagRandomPatron":
                    e.PatronID = 2; // Lucifer（建材）
                    e.AddLostResource("Construction", 4000 + rng.Next(60000));
                    break;
                case "CutLocalTag":
                    e.AddLostResource("Luxury", 2000 + rng.Next(40000));
                    break;
                case "CutBuffResource":
                    // バフが出す資源すべてが対象＝実物は数種類並ぶ
                    e.AddLostResource("Construction", 2000 + rng.Next(20000));
                    e.AddLostResource("Chemical", 3000 + rng.Next(30000));
                    e.AddLostResource("Magic", 1000 + rng.Next(20000));
                    e.AddLostResource("Fuel", 4000 + rng.Next(40000));
                    break;
                case "LowPriceCashCut":
                    // 安いアイテムの現金だけが消える（他の資源は入る）。内訳は現金の分だけ
                    AddDummyItem(e, rng, "Iron", 200, "Cash", 1, "None", 0, "None", 0);
                    AddDummyItem(e, rng, "Lemonade", 60, "Cash", 5, "None", 0, "None", 0);
                    AddDummyItem(e, rng, "Lye", 45, "Cash", 1, "None", 0, "None", 0);
                    AddDummyItem(e, rng, "Compost", 120, "Cash", 1, "None", 0, "None", 0);
                    break;
                case "HighPriceCashCut":
                    AddDummyItem(e, rng, "Gold", 40, "Cash", 10, "None", 0, "None", 0);
                    AddDummyItem(e, rng, "Pickaxe", 8, "Cash", 10, "None", 0, "None", 0);
                    AddDummyItem(e, rng, "Adamantite", 6, "Cash", 500, "None", 0, "None", 0);
                    break;
                case "ResourceDeficit":
                    // 現金以外の全資源が目標値まで削られる＝実物は 7 つ並ぶ
                    e.AddLostResource("Construction", 20000 + rng.Next(60000));
                    e.AddLostResource("Grocery", 1000 + rng.Next(20000));
                    e.AddLostResource("Luxury", 5000 + rng.Next(30000));
                    e.AddLostResource("Fertilizer", 2000 + rng.Next(20000));
                    e.AddLostResource("Chemical", 3000 + rng.Next(40000));
                    e.AddLostResource("Magic", 1000 + rng.Next(30000));
                    e.AddLostResource("Fuel", 10000 + rng.Next(60000));
                    break;
                case "BlockTopSales":
                    FillDummyItems(e, rng);
                    foreach (KeyValuePair<string, ItemLoss> kv in e.Items) { e.CancelledSales += kv.Value.Count; }
                    break;
                case "ReducePactRarity":
                    e.RateText = "-20%";
                    break;
                case "Centralization":
                    FillDummyItems(e, rng);
                    // 無効化 1 回につきアイテム 1 個として、内訳と数を合わせる
                    foreach (KeyValuePair<string, ItemLoss> kv in e.Items) { e.CancelledSales += kv.Value.Count; }
                    break;
                case "LockRandomPatron":
                    e.PatronID = 8; // Satan（クラフター）
                    FillDummyItems(e, rng);
                    break;
                case "ReduceItemCraftResult":
                    FillDummyItems(e, rng);
                    // 成果物がバフのレシピも削られる。返済にならないので内訳だけに出る
                    e.AddLostTag("RelicCobalt", 100 + rng.Next(400));
                    e.AddLostTag("RelicAmethyst", 50 + rng.Next(300));
                    e.AddLostTag("RelicRuby", 10 + rng.Next(80));
                    e.AddLostTag("RelicJade", 5 + rng.Next(60));
                    break;
                case "HarvestRockPenalty":
                case "HarvestTreePenalty":
                    e.ExtraHits = 3000 + rng.Next(90000);
                    break;
                case "SlowConveyor":
                    e.RateText = "-90%";
                    break;
                default:
                    FillDummyItems(e, rng);
                    break;
            }
        }

        /// <summary>減った（または無効になった）アイテムの内訳。
        /// 縦に伸びたときの見え方を確かめるため、種類を多めに入れる。</summary>
        /// <summary>内訳のダミー。ID とタグは items.csv の実物に合わせる
        /// （入手できないアイテムを出すと、見本として嘘になる）。</summary>
        private static void FillDummyItems(TaxEntry e, System.Random rng)
        {
            AddDummyItem(e, rng, "Gold", 40, "Luxury", 50, "Cash", 10, "None", 0);
            AddDummyItem(e, rng, "Iron", 200, "Construction", 20, "Cash", 1, "None", 0);
            AddDummyItem(e, rng, "Lemonade", 60, "Grocery", 1, "Cash", 5, "None", 0);
            AddDummyItem(e, rng, "Nitro", 30, "Fuel", 100, "Cash", 10, "None", 0);
            AddDummyItem(e, rng, "Soda", 25, "Chemical", 100, "Cash", 10, "None", 0);
            AddDummyItem(e, rng, "Wax", 80, "Chemical", 1, "Grocery", 1, "Fuel", 1);
            AddDummyItem(e, rng, "Mercury", 90, "Magic", 20, "Cash", 1, "None", 0);
            AddDummyItem(e, rng, "Lye", 45, "Chemical", 20, "Cash", 1, "None", 0);
            AddDummyItem(e, rng, "MagicChunk", 55, "Magic", 20, "Cash", 1, "None", 0);
            AddDummyItem(e, rng, "Gunpowder", 35, "Fuel", 20, "Cash", 1, "None", 0);
            AddDummyItem(e, rng, "Compost", 120, "Fertilizer", 20, "Cash", 1, "None", 0);
            AddDummyItem(e, rng, "GemPowder", 20, "Magic", 20, "Cash", 1, "None", 0);
            AddDummyItem(e, rng, "Pickaxe", 8, "Construction", 100, "Cash", 10, "None", 0);
            AddDummyItem(e, rng, "Adamantite", 6, "Fuel", 2000, "Cash", 500, "None", 0);
            // 現金を持たないアイテム（値の欄が 0 になる場合の見え方）
            AddDummyItem(e, rng, "Summon-Worker", 12, "None", 0, "None", 0, "None", 0);
        }

        private static void AddDummyItem(TaxEntry e, System.Random rng, string itemID, int scale,
            string t1, int c1, string t2, int c2, string t3, int c3)
        {
            int count = scale / 2 + rng.Next(scale * 2);
            e.GetItem(itemID).Add(count, t1, c1, t2, c2, t3, c3);
        }
    }
}
