// LWF Economy Graph — グラフの中身
//
// ここには「何をどう描くか」だけを置く。ゲームにも BepInEx にも依存しない。
//
// -- なぜ分けてあるか ---------------------------------------------------
// 見た目を直すたびにゲームを起動していると往復が遅い。
// 描画を IChartPainter 越しに書いておけば、
//   ・ゲーム内では IMGUI で描く（EconomyGraphMod.cs）
//   ・確認用にはダミーデータを流して PNG に描く（手元の確認用ツール）
// と、**同じコードのまま**両方に出せる。
// モックを別に作ると「モックでは良かったのに本物は違う」が起きるので、それを避けている。
//
// UnityEngine の Rect / Color / Mathf は、ゲームが動いていなくても
// ただの構造体と算術として使える（実測済み）。ただし Color.ToString() は落ちるので呼ばない。
//
// C# 5 でビルドするため、文字列補間・?. 演算子・式形式メンバ・out var は使えない。

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace LwfEconomyGraph
{
    /// <summary>
    /// 出所（売却したアイテム・レリック・レシピ）1つぶんの記録。
    /// 返済1回ぶんを1枠として貯める。
    /// </summary>
    internal sealed class SourceSeries
    {
        internal readonly string Key;      // statsSourceID。無いものは "#理由番号"
        internal readonly int Reason;      // 代表の理由（色と呼び名の手掛かり）
        internal readonly bool HasSource;  // false なら理由そのもの（受注・返済など）
        internal long Total;

        /// <summary>
        /// この出所で稼いだぶんのうち、**いま手元に残っている額**。
        /// 収入で増え、支出のたびに手元の構成比のまま目減りする。
        /// 進行中の期は、その期の収入ではなくこれを積む
        /// （ノルマ超過で返済したときの持ち越しが、稼いだ出所のまま残る）。
        /// </summary>
        internal long Held;

        internal readonly List<long> ByPeriod = new List<long>();

        /// <summary>
        /// その列の返済に、この出所の金がいくら使われたか。
        /// 列は「返済1回」なので、収入ではなく**何で返したか**で埋めたい。
        /// 持ち越しだけで返した回も、これなら中身が出る。
        /// </summary>
        internal readonly List<long> RepaidByPeriod = new List<long>();

        internal SourceSeries(string key, int reason, bool hasSource)
        {
            Key = key;
            Reason = reason;
            HasSource = hasSource;
        }

        internal long At(int period)
        {
            if (period < 0 || period >= ByPeriod.Count) { return 0L; }
            return ByPeriod[period];
        }

        internal long RepaidAt(int period)
        {
            if (period < 0 || period >= RepaidByPeriod.Count) { return 0L; }
            return RepaidByPeriod[period];
        }

        internal void AddRepaid(int period, long value)
        {
            while (RepaidByPeriod.Count <= period) { RepaidByPeriod.Add(0L); }
            RepaidByPeriod[period] += value;
        }

        internal void Add(int period, long value)
        {
            while (ByPeriod.Count <= period) { ByPeriod.Add(0L); }
            ByPeriod[period] += value;
            Total += value;
        }
    }

    /// <summary>
    /// タグ1種類ぶんの記録。バケット列（時系列）と、返済期ごとの集計を持つ。
    /// Flows のストライドは ChartRenderer.SlotsPerBucket で、前半が収入・後半が支出。
    /// </summary>
    internal sealed class TagSeries
    {
        internal readonly string TagID;
        internal readonly int Index;    // 生ログが指す添字。作った順に振って動かさない
        internal string DisplayName;

        internal long Balance;          // 最新の残高
        internal long InitialBalance;   // 購読した時点の残高（収支には数えない）
        internal long PeakBalance;
        internal double PeakAt;

        internal long IncomeTotal;
        internal long ExpenseTotal;
        internal long RecordCount;

        internal readonly long[] IncomeByReason;
        internal readonly long[] ExpenseByReason;
        internal readonly Dictionary<string, long> IncomeBySource = new Dictionary<string, long>(StringComparer.Ordinal);
        internal readonly Dictionary<string, long> ExpenseBySource = new Dictionary<string, long>(StringComparer.Ordinal);

        internal List<long> Balances = new List<long>();
        internal readonly List<long> Flows = new List<long>();
        internal readonly List<long> PeriodFlows = new List<long>();

        // 出所ごと・返済期ごと。構成グラフはこちらを使う
        /// <summary>台帳のうち出所が辿れないぶん（開始時の所持など）。</summary>
        internal long HeldUnattributed;

        internal readonly List<SourceSeries> IncomeSources = new List<SourceSeries>();
        internal readonly List<SourceSeries> ExpenseSources = new List<SourceSeries>();
        private readonly Dictionary<string, SourceSeries> _incomeIndex = new Dictionary<string, SourceSeries>(StringComparer.Ordinal);
        private readonly Dictionary<string, SourceSeries> _expenseIndex = new Dictionary<string, SourceSeries>(StringComparer.Ordinal);

        internal TagSeries(string tagID, int index, int reasonCount)
        {
            TagID = tagID;
            Index = index;
            DisplayName = tagID;
            IncomeByReason = new long[reasonCount];
            ExpenseByReason = new long[reasonCount];
        }

        internal SourceSeries GetOrCreateSource(bool income, string key, int reason, bool hasSource)
        {
            Dictionary<string, SourceSeries> index = income ? _incomeIndex : _expenseIndex;
            SourceSeries found;
            if (index.TryGetValue(key, out found)) { return found; }

            found = new SourceSeries(key, reason, hasSource);
            index.Add(key, found);
            (income ? IncomeSources : ExpenseSources).Add(found);
            return found;
        }

        internal long HeldTotal()
        {
            long total = HeldUnattributed;
            for (int i = 0; i < IncomeSources.Count; i++) { total += IncomeSources[i].Held; }
            return total;
        }

        // 手元の金を「入った順の山」で持つ。新しい山から先に使う。
        //
        // 比例配分（手元の構成比を保ったまま全体を縮める）にすると、
        // どの山も減りきらないので、あらゆる列に過去の出所が薄く混ざって読めなくなる。
        // 一括で返済した列に、直前の収入と関係ないアイテムが乗るのはこれが理由。
        //
        // 新しい順にしたのは、稼いでから返す遊び方に合うため——
        // 直前に入った金で返した、と読むのが自然。
        private readonly List<SourceSeries> _lotSource = new List<SourceSeries>();
        private readonly List<long> _lotAmount = new List<long>();

        /// <summary>収入を山に積む。直前と同じ出所なら1つにまとめる（山が増えすぎないように）。</summary>
        internal void AddHolding(SourceSeries src, long amount)
        {
            if (src == null || amount <= 0) { return; }
            src.Held += amount;

            int last = _lotSource.Count - 1;
            if (last >= 0 && ReferenceEquals(_lotSource[last], src))
            {
                _lotAmount[last] += amount;
                return;
            }

            _lotSource.Add(src);
            _lotAmount.Add(amount);
        }

        /// <summary>
        /// 新しい山から取り崩す。何をいくら使ったかを srcOut / amtOut に並べて返す
        /// （要らなければ null でよい）。出所の辿れないぶんは srcOut に null が入る。
        /// </summary>
        internal void TakeFromHolding(long amount, List<SourceSeries> srcOut, List<long> amtOut)
        {
            if (amount <= 0) { return; }
            long left = amount;

            for (int i = _lotSource.Count - 1; i >= 0 && left > 0; i--)
            {
                long take = _lotAmount[i] < left ? _lotAmount[i] : left;
                if (take <= 0) { continue; }

                _lotAmount[i] -= take;
                _lotSource[i].Held -= take;
                left -= take;

                if (srcOut != null) { srcOut.Add(_lotSource[i]); amtOut.Add(take); }
            }

            // 空になった山を畳む（取り崩しは後ろからなので、空は必ず後ろに寄る）
            while (_lotAmount.Count > 0 && _lotAmount[_lotAmount.Count - 1] <= 0)
            {
                _lotAmount.RemoveAt(_lotAmount.Count - 1);
                _lotSource.RemoveAt(_lotSource.Count - 1);
            }

            if (left > 0 && HeldUnattributed > 0)
            {
                long take = HeldUnattributed < left ? HeldUnattributed : left;
                HeldUnattributed -= take;
                left -= take;
                if (srcOut != null) { srcOut.Add(null); amtOut.Add(take); }
            }
        }

        /// <summary>支出のぶん、台帳を目減りさせる。中身は要らないので捨てる。</summary>
        internal void SpendFromHolding(long amount)
        {
            TakeFromHolding(amount, null, null);
        }

        internal long BalanceAt(int bucket)
        {
            int n = Balances.Count;
            if (n == 0) { return InitialBalance; }
            if (bucket < 0) { return Balances[0]; }
            if (bucket >= n) { return Balances[n - 1]; }
            return Balances[bucket];
        }

        internal long FlowAt(int bucket, int slot)
        {
            int index = bucket * (IncomeByReason.Length * 2) + slot;
            if (bucket < 0 || index < 0 || index >= Flows.Count) { return 0L; }
            return Flows[index];
        }

        internal long PeriodFlowAt(int period, int slot)
        {
            int index = period * (IncomeByReason.Length * 2) + slot;
            if (period < 0 || index < 0 || index >= PeriodFlows.Count) { return 0L; }
            return PeriodFlows[index];
        }
    }

    /// <summary>
    /// 描く先。ゲーム内は IMGUI、確認用は System.Drawing が実装する。
    /// 座標は左上が原点（IMGUI と同じ向き）。
    /// </summary>
    internal interface IChartPainter
    {
        /// <summary>枠の大きさに対する文字の拡縮。1.0 が基準（枠幅 494 のとき）。</summary>
        void SetScale(float scale);

        float FontSize { get; }
        float LineHeight { get; }

        float Measure(string text);
        float MeasureTitle(string text);

        void Fill(Rect r, Color color);
        void Text(float x, float y, string text, Color color);
        void Title(float x, float y, string text, Color color);

        /// <summary>
        /// 出所のアイコンを描く。絵が用意できなければ false を返すこと。
        /// sourceKey が null ならタグそのもののアイコン。
        /// "#理由番号" のときは理由ごとの絵（注文なら電話、返済ならゲヘナ）。
        ///
        /// outline はアイコンの縁取り。下地と同系色の絵が溶けてしまうので、
        /// 絵のシルエットを1画素ずらして先に描いてから本体を重ねる。
        /// outline.a が 0 なら縁取り無し。
        /// </summary>
        bool Icon(Rect r, string sourceKey, string tagID, Color tint, Color outline);

        /// <summary>
        /// 出所IDを持たない項目（注文・返済・契約など）の短い名前。
        /// これらはタグのアイコンに落ちるので、絵だけでは互いに区別できない。
        /// グラフ本体には出さず、ホバーのときだけ添える。
        /// </summary>
        string ReasonLabel(int reason);
    }

    /// <summary>描くのに要るものを一式。プラグイン側は毎フレーム参照を差し替えるだけ。</summary>
    internal sealed class ChartData
    {
        internal List<TagSeries> Ordered = new List<TagSeries>();
        internal TagSeries Selected;

        internal float BucketSeconds = 1f;
        internal int BucketCount;
        internal int MaxColumns = 480;

        /// <summary>いま描いている列の番号（0起点）。返済の回数とは一致しない。</summary>
        internal int PeriodIndex;

        /// <summary>閉じた列ごとの「そこまでの返済回数」。同時に複数返済すると一度に増える。</summary>
        internal List<int> PeriodRepaid = new List<int>();

        /// <summary>返済の総回数（本体の CurrentProgress）。</summary>
        internal int RepaidTotal;

        internal int TargetProgress;

        // 次の返済のノルマ。要求がアイテムのときは RequiredCount を 0 にして渡す
        internal string RequiredTagID;
        internal int RequiredCount;
        internal long RequiredCurrent;   // 固定費用のデバフで負になることがある
        internal List<double> PeriodEnds = new List<double>();
        internal HashSet<string> HiddenTags = new HashSet<string>(StringComparer.Ordinal);

        internal int Mode;
        internal bool LastMinute;
        internal bool ExpenseSide;

        // マウスの位置（左上が原点＝IMGUI と同じ向き）。ホバー表示に使う
        internal Vector2 Mouse;
        internal bool MouseValid;

        // グラフ下の帯に描いたボタン。押した判定はプラグイン側で行う
        // （IMGUI のボタンにすると、押した瞬間にゲーム側へも入力が渡ってしまう）。
        internal readonly List<Rect> IconRects = new List<Rect>();
        internal readonly List<string> IconTagIDs = new List<string>();
        internal readonly List<Rect> ModeRects = new List<Rect>();
        internal readonly List<int> ModeIDs = new List<int>();
        internal readonly List<Rect> ToggleRects = new List<Rect>();
        internal readonly List<int> ToggleIDs = new List<int>();

        // 直近1分の速度。構成グラフを1分表示にしたときだけ使う。
        // 3つは同じ並び（多い順）。値は1コマあたりの毎秒量
        internal readonly List<SourceSeries> RateSources = new List<SourceSeries>();
        internal readonly List<float[]> RateValues = new List<float[]>();
        internal readonly List<long> RateTotals = new List<long>();
        internal float RateSlotSeconds = 1f;

        /// <summary>直近1分の総量。線に出していない出所も含めた全部の合計。</summary>
        internal long RateTotal;
    }

    /// <summary>
    /// グラフを描く。状態は持たない（作業用の配列だけ使い回す）。
    ///
    /// 作りは calc の v0.19 再現版のタイムラインに寄せてある。
    ///   ・残高は時間軸の折れ線
    ///   ・構成は「返済1回ごとに1列」の100%積み上げ（横軸は時間ではなく回数）
    ///   ・貢献度はラン全体の内訳を横1本の帯で
    /// 文字は数字と記号だけにして、言葉は最小限に。
    /// </summary>
    internal sealed class ChartRenderer
    {
        /// <summary>
        /// 本体の StatsCashReason は0〜14。最後の1つはこちらで足した「土地購入」で、
        /// 本体は理由を付けずに引くので「その他」に混ざってしまう（EconomyGraphMod を見よ）。
        /// </summary>
        internal const int ReasonCount = 16;
        internal const int SlotsPerBucket = ReasonCount * 2;

        /// <summary>枠の設計値（本体が空けている StatsBalanceGraphFrame の内側）。</summary>
        internal const float DesignWidth = 494f;

        // 残高そのものより「傾き」が知りたい。返済に間に合うかは、いくら持っているかより
        // 増えているか減っているかで決まる。1つの資源の残高だけを見たい場面は、
        // 重ね表示で他を消せば足りる
        internal const int ModeNetFlow = 0;      // 収支の純増減（選ばれているタグ）
        internal const int ModeBalanceAll = 1;   // 残高（全タグ重ね）
        internal const int ModeComposition = 2;  // 構成：返済1回ごとの100%積み上げ
        internal const int ModeCount = 3;

        // グラフ下の帯に出す切り替え。いまの表示に効くものだけ出す
        internal const int ToggleRange = 0;   // 全体 ↔ 直近1分（残高のとき）
        internal const int ToggleSide = 1;    // 収入 ↔ 支出（構成のとき）

        /// <summary>
        /// 工場の収支のボタンに使う絵。本体のクラフトタブの絵（MenuCraft）を借りる。
        /// 筆が用意できなければ、四角を並べた工場の絵に落ちる。
        /// </summary>
        internal const string CraftIconKey = "#craft";

        internal static readonly string[] KnownTagIDs = new string[8]
        {
            "Cash", "Construction", "Grocery", "Luxury", "Fertilizer", "Chemical", "Magic", "Fuel"
        };

        internal static readonly string[] KnownTagNamesJa = new string[8]
        {
            "現金", "建材", "食品", "高級品", "肥料", "薬品", "魔力", "燃料"
        };

        /// <summary>アイコンから色が引けなかったときの予備（KnownTagIDs の並び＋末尾はそれ以外）。</summary>
        private static readonly Color[] TagColors = new Color[9]
        {
            new Color(1.00f, 0.82f, 0.25f),   // 現金
            new Color(0.72f, 0.72f, 0.76f),   // 建材
            new Color(0.55f, 0.85f, 0.45f),   // 食品
            new Color(0.95f, 0.55f, 0.85f),   // 高級品
            new Color(0.78f, 0.60f, 0.32f),   // 肥料
            new Color(0.45f, 0.85f, 0.80f),   // 薬品
            new Color(0.65f, 0.55f, 0.98f),   // 魔力
            new Color(0.98f, 0.55f, 0.30f),   // 燃料
            new Color(0.60f, 0.60f, 0.62f)    // それ以外
        };

        /// <summary>
        /// 出所の色が引けなかったときの予備。本数の上限も兼ねていて、
        /// これを超えた出所は「その他」にまとめる。
        /// </summary>
        private static readonly Color[] SourcePalette = new Color[8]
        {
            new Color(1.00f, 0.62f, 0.20f),
            new Color(0.42f, 0.80f, 0.45f),
            new Color(0.40f, 0.68f, 0.98f),
            new Color(0.95f, 0.45f, 0.55f),
            new Color(0.75f, 0.55f, 0.95f),
            new Color(0.35f, 0.85f, 0.82f),
            new Color(0.95f, 0.85f, 0.35f),
            new Color(0.90f, 0.55f, 0.85f)
        };

        private static readonly Color OthersColor = new Color(0.55f, 0.55f, 0.58f);

        /// <summary>
        /// アイコンの縁取り。下地の明暗で白黒を選び分けることも試したが、
        /// 黒縁は小さいアイコンだと潰れて汚くなるので白で通す。
        /// </summary>
        private static readonly Color IconOutline = new Color(1f, 1f, 1f, 0.95f);
        private static readonly Color Faint = new Color(1f, 1f, 1f, 0.14f);
        private static readonly Color Fainter = new Color(1f, 1f, 1f, 0.10f);
        private static readonly Color Ink = Color.white;
        private static readonly Color Accent = new Color(1f, 0.68f, 0.25f, 1f);
        private static readonly Color DeficitColor = new Color(0.95f, 0.32f, 0.34f, 1f);

        /// <summary>収入も支出も無かった列に敷く「高さ0の棒」の色。</summary>
        private static readonly Color ZeroColor = new Color(0.66f, 0.63f, 0.60f, 0.9f);

        // 純増減。増えている側と減っている側で色を変える
        private static readonly Color NetLineUp = new Color(0.55f, 0.95f, 0.6f, 1f);
        private static readonly Color NetLineDown = new Color(0.98f, 0.55f, 0.55f, 1f);
        private static readonly Color NetFillUp = new Color(0.35f, 0.85f, 0.45f, 0.26f);
        private static readonly Color NetFillDown = new Color(0.95f, 0.35f, 0.38f, 0.26f);

        /// <summary>
        /// いまの期の高さを「ノルマに対して何%か」で決められるか。
        ///
        /// 支出側では使わない。高さが「いま持っている額」になってしまい、
        /// まだ返済に充てていない時点では中身（何に使ったか）と意味がねじれるため。
        /// 返済の要求がアイテムのとき（GetTargetTag が "None"）や、
        /// 見ているタグが要求と違うときも、従来どおり100%に伸ばす。
        /// </summary>
        private static bool UsesNorm(ChartData d)
        {
            return !d.ExpenseSide
                && d.RequiredCount > 0
                && d.Selected != null
                && string.Equals(d.RequiredTagID, d.Selected.TagID, StringComparison.Ordinal);
        }

        private static float NormRatio(ChartData d)
        {
            if (d.RequiredCount <= 0) { return 1f; }
            return (float)((double)d.RequiredCurrent / d.RequiredCount);
        }

        private readonly List<SourceSeries> _rank = new List<SourceSeries>();
        private readonly List<SourceSeries> _hover = new List<SourceSeries>();
        private readonly List<long> _rankTotals = new List<long>();
        private readonly List<float[]> _rateLines = new List<float[]>();
        private readonly List<int> _rateOrder = new List<int>();
        private bool _hoverByRepaid;
        private bool _hoverHeld;
        private int _hoverPeriod;
        private readonly List<Color> _rankColors = new List<Color>();
        private readonly List<Color> _lineColors = new List<Color>();
        private readonly List<TagSeries> _capOrder = new List<TagSeries>();
        private readonly List<float> _capY = new List<float>();

        // ------------------------------------------------------------------
        // 入口
        // ------------------------------------------------------------------

        /// <summary>枠いっぱいに描く。area は余白を除いた内側。</summary>
        internal void Draw(Rect area, ChartData d, IChartPainter p)
        {
            p.SetScale(Mathf.Clamp(area.width / DesignWidth, 0.8f, 2.2f));

            d.IconRects.Clear();
            d.IconTagIDs.Clear();
            d.ModeRects.Clear();
            d.ModeIDs.Clear();
            d.ToggleRects.Clear();
            d.ToggleIDs.Clear();

            float line = p.LineHeight;
            float iconSize = line + 4f;
            float x = area.x;
            float y = area.y;

            // 見出しがいまの純増減を出すので、描く前に組んでおく
            if (d.Mode == ModeNetFlow && d.Selected != null) { BuildNet(d, d.Selected); }

            // ---- 下の帯：タグの選び替えと表示の切り替え ----
            // グラフの上に出すとキー操作を覚える必要があるので、絵のまま押せる場所を下に置く
            float barH = Mathf.Max(16f, line) + 8f;
            Rect bar = new Rect(area.x, area.yMax - barH, area.width, barH);
            if (area.height > barH + 40f)
            {
                DrawToolbar(bar, d, p);
                area = new Rect(area.x, area.y, area.width, bar.y - area.y - 2f);
            }

            if (d.Ordered.Count == 0)
            {
                DrawZeroLine(area, p);
                return;
            }

            // ---- 見出し：アイコンと数字だけ ----
            if (d.Mode != ModeBalanceAll && d.Selected != null)
            {
                p.Icon(new Rect(x, y, iconSize, iconSize), null, d.Selected.TagID, Ink, IconOutline);
                x += iconSize + 6f;

                if (d.Mode == ModeNetFlow)
                {
                    long net = _netLast;
                    string amount = (net >= 0 ? "+" : "-") + N(Math.Abs(net)) + "/min";
                    p.Title(x, y + 2f, amount, net >= 0 ? NetLineUp : NetLineDown);
                }
                else
                {
                    // 1分表示のときは走行の総額ではなく、その1分に入った量を出す。
                    // グラフが直近1分の話をしているので、見出しも揃える
                    bool rate = d.LastMinute;
                    long total = rate
                        ? d.RateTotal
                        : (d.ExpenseSide ? d.Selected.ExpenseTotal : d.Selected.IncomeTotal);
                    string amount = (d.ExpenseSide ? "-" : "+") + N(total) + (rate ? "/min" : "");
                    p.Title(x, y + 2f, amount,
                        d.ExpenseSide ? new Color(0.98f, 0.55f, 0.55f) : new Color(0.55f, 0.95f, 0.6f));

                    // 完済までの回数は難易度で変わり、追加融資でも増える。
                    // 列は返した回数ぶんしか並べないので、進み具合はここの数字で示す
                    float tx = x + p.MeasureTitle(amount) + 8f;
                    if (d.TargetProgress > 0)
                    {
                        string progress = d.RepaidTotal.ToString(CultureInfo.InvariantCulture)
                            + "/" + d.TargetProgress.ToString(CultureInfo.InvariantCulture);
                        p.Text(tx, y + 6f, progress, new Color(0.75f, 0.72f, 0.68f));
                        tx += p.Measure(progress) + 8f;
                    }
                    if (UsesNorm(d))
                    {
                        int percent = Mathf.RoundToInt(NormRatio(d) * 100f);
                        string norm = percent.ToString(CultureInfo.InvariantCulture) + "%";
                        p.Text(tx, y + 6f, norm, percent < 0 ? DeficitColor : Accent);
                    }
                }
            }

            if (d.Mode != ModeBalanceAll) { y += iconSize + 4f; }

            Rect rest = new Rect(area.x, y, area.width, area.yMax - y);
            if (rest.height < 40f) { return; }

            if (d.Mode == ModeComposition)
            {
                if (d.Selected == null) { DrawZeroLine(rest, p); return; }

                // 1分だけ切り取ると100%積み上げは割合がぶれて読めない。
                // その範囲では量そのもの（毎秒）を折れ線で出す
                if (d.LastMinute) { DrawRates(rest, d, p); }
                else { DrawComposition(rest, d, p); }
            }
            else if (d.Mode == ModeBalanceAll)
            {
                DrawBalanceChart(rest, d, null, p);
            }
            else
            {
                if (d.Selected == null) { DrawZeroLine(rest, p); return; }
                DrawNetChart(rest, d, p);
            }
        }

        /// <summary>
        /// グラフ下の帯。左はタグ、右は表示の切り替え。
        ///
        /// 左のタグは押すと選び替え。重ね表示のときだけ意味が変わって、見せる／隠すになる
        /// （重ねているので「1つを選ぶ」が無い）。どちらも下線の色でいまの状態を示す。
        /// </summary>
        /// <summary>
        /// グラフ下の帯。使う順に左から並べる——
        /// **全部見る → 資源を選ぶ → その資源を見る → 収支と範囲 → 内訳**。
        ///
        /// 表示の3ボタンをまとめて置かないのは、それぞれが隣の物と組で意味を持つため。
        /// 重ねた線は資源の一覧と、1つの残高は選んだ資源と、内訳は収支や範囲と続く。
        /// いまどれを見ているかは、選ばれているボタンが橙になるので分かる。
        /// </summary>
        private void DrawToolbar(Rect r, ChartData d, IChartPainter p)
        {
            float icon = Mathf.Max(14f, p.LineHeight);
            float bw = icon * 1.9f;
            float gap = 4f;
            float group = 10f;   // まとまりとまとまりの間

            int tags = d.Ordered.Count;
            float need = bw * 4f + icon * tags + gap * (tags + 1) + group * 7.5f;
            if (need > r.width && need > 0f)
            {
                float k = r.width / need;
                icon = Mathf.Max(11f, icon * k);
                bw = Mathf.Max(13f, bw * k);
                gap = Mathf.Max(2f, gap * k);
                group = Mathf.Max(4f, group * k);
            }

            float y = r.y + (r.height - icon) * 0.5f - 1f;

            // 両端に余白。枠にくっついていると窮屈に見える
            float inset = group;
            float x = r.x + inset;

            // 残高までがひと続き。収支と範囲はそこから離して別のまとまりにする
            x = DrawModeButton(x, y, bw, icon, ModeBalanceAll, d, p) + group;
            x = DrawTagRow(x, y, icon, gap, d, p) + group;
            x = DrawModeButton(x, y, bw, icon, ModeNetFlow, d, p) + group * 2.5f;
            x = DrawToggleButton(x, y, bw, icon, ToggleSide, d, p) + gap;
            DrawToggleButton(x, y, bw, icon, ToggleRange, d, p);

            // 構成は行の反対の端。積み上げは別の見方なので、並びからも離す
            DrawModeButton(r.xMax - inset - bw, y, bw, icon, ModeComposition, d, p);
        }

        /// <summary>
        /// 資源の並び。押すとその資源を見る。
        /// 重ね表示のときだけ意味が変わって、見せる／隠すになる（重ねているので「1つを選ぶ」が無い）。
        /// </summary>
        private float DrawTagRow(float x, float y, float icon, float gap, ChartData d, IChartPainter p)
        {
            bool overlay = d.Mode == ModeBalanceAll;

            for (int i = 0; i < d.Ordered.Count; i++)
            {
                string tagID = d.Ordered[i].TagID;
                bool on = overlay
                    ? !d.HiddenTags.Contains(tagID)
                    : (d.Selected != null && d.Selected.TagID == tagID);

                Rect box = new Rect(x, y, icon, icon);
                p.Icon(box, null, tagID, on ? Ink : new Color(1f, 1f, 1f, 0.3f),
                    on ? IconOutline : new Color(0f, 0f, 0f, 0f));
                p.Fill(new Rect(x, y + icon + 1f, icon, 2f), on ? TagColor(tagID) : Fainter);

                d.IconRects.Add(box);
                d.IconTagIDs.Add(tagID);
                x += icon + gap;
            }
            return x - gap;
        }

        private float DrawModeButton(float x, float y, float bw, float icon, int mode,
            ChartData d, IChartPainter p)
        {
            Rect box = new Rect(x, y - 2f, bw, icon + 4f);
            bool on = mode == d.Mode;

            p.Fill(box, on ? new Color(1f, 1f, 1f, 0.15f) : new Color(1f, 1f, 1f, 0.05f));
            DrawModeGlyph(box, mode, on ? Accent : new Color(1f, 1f, 1f, 0.45f), p);

            d.ModeRects.Add(box);
            d.ModeIDs.Add(mode);
            return box.xMax;
        }

        private float DrawToggleButton(float x, float y, float bw, float icon, int id,
            ChartData d, IChartPainter p)
        {
            Rect box = new Rect(x, y - 2f, bw, icon + 4f);
            AddToggle(box, id, d, p);
            return box.xMax;
        }

        /// <summary>
        /// 切り替えボタンの絵。言葉を使わずに「線1本」「線を重ねる」「積み上げ」を表す。
        /// 絵は四角の塗りだけで組む（線を引く手立てが Fill しか無いため）。
        /// </summary>
        private static void DrawModeGlyph(Rect box, int mode, Color c, IChartPainter p)
        {
            float pad = Mathf.Max(2f, box.height * 0.24f);
            Rect g = new Rect(box.x + pad, box.y + pad, box.width - pad * 2f, box.height - pad * 2f);
            if (g.width < 4f || g.height < 4f) { return; }

            if (mode == ModeComposition)
            {
                float cw = g.width / 3f;
                for (int i = 0; i < 3; i++)
                {
                    float w = Mathf.Max(1f, cw - 1.5f);
                    float lower = g.height * (0.34f + i * 0.13f);
                    p.Fill(new Rect(g.x + i * cw, g.yMax - lower, w, lower), c);
                    p.Fill(new Rect(g.x + i * cw, g.y, w, g.height - lower - 1f),
                        new Color(c.r, c.g, c.b, c.a * 0.38f));
                }
                return;
            }

            float thick = Mathf.Max(1f, g.height * 0.16f);

            if (mode == ModeNetFlow)
            {
                // 本体のクラフトタブと同じ絵が使えるならそれを。
                // 四角を並べた工場より、見慣れた絵のほうが「生産の話」と伝わる
                // 絵には元から余白があるので、内側の枠ではなくボタンいっぱいに置く
                float side = Mathf.Min(box.width, box.height) - 2f;
                Rect fit = new Rect(box.x + (box.width - side) * 0.5f,
                    box.y + (box.height - side) * 0.5f, side, side);
                if (!p.Icon(fit, CraftIconKey, null, c, new Color(0f, 0f, 0f, 0f)))
                {
                    DrawFactoryGlyph(g, c, p);
                }
                return;
            }

            DrawGlyphLine(g, RisingLevels, thick, c, p);

            if (mode != ModeBalanceAll) { return; }

            // もう1本、逆向きに重ねる＝複数のタグ
            DrawGlyphLine(g, FallingLevels, thick, new Color(c.r, c.g, c.b, c.a * 0.5f), p);
        }

        private static void AddToggle(Rect box, int id, ChartData d, IChartPainter p)
        {
            p.Fill(box, new Color(1f, 1f, 1f, 0.08f));
            if (id == ToggleSide) { DrawSideGlyph(box, d.ExpenseSide, p); }
            else { DrawRangeGlyph(box, d.LastMinute, p); }

            d.ToggleRects.Add(box);
            d.ToggleIDs.Add(id);
        }

        /// <summary>
        /// 収入か支出か。上向きの三角＝入る、下向き＝出る。
        /// 色は見出しの金額と同じにして、どちらを見ているかを色でも分かるようにする。
        /// </summary>
        private static void DrawSideGlyph(Rect box, bool expense, IChartPainter p)
        {
            float pad = Mathf.Max(2f, box.height * 0.24f);
            Rect g = new Rect(box.x + pad, box.y + pad, box.width - pad * 2f, box.height - pad * 2f);
            if (g.width < 4f || g.height < 4f) { return; }

            Color c = expense ? new Color(0.98f, 0.55f, 0.55f) : new Color(0.55f, 0.95f, 0.6f);

            int rows = Mathf.Max(3, Mathf.RoundToInt(g.height / 2f));
            float rh = g.height / rows;
            for (int i = 0; i < rows; i++)
            {
                float t = (float)i / (rows - 1);
                float k = expense ? (1f - t) : t;    // 支出は下ほど細い＝下向き
                float w = Mathf.Max(1f, g.width * (0.12f + 0.88f * k));
                p.Fill(new Rect(g.x + (g.width - w) * 0.5f, g.y + i * rh, w, Mathf.Max(1f, rh)), c);
            }
        }

        /// <summary>
        /// 横軸の範囲。走行全体を表す帯のうち、いま見ている側だけを明るくする。
        /// 直近1分なら右の端だけが光る。
        /// </summary>
        private static void DrawRangeGlyph(Rect box, bool lastMinute, IChartPainter p)
        {
            float pad = Mathf.Max(2f, box.height * 0.28f);
            Rect g = new Rect(box.x + pad, box.y + pad, box.width - pad * 2f, box.height - pad * 2f);
            if (g.width < 4f || g.height < 4f) { return; }

            float h = Mathf.Max(2f, g.height * 0.36f);
            float yy = g.y + (g.height - h) * 0.5f;

            p.Fill(new Rect(g.x, yy, g.width, h), new Color(1f, 1f, 1f, 0.22f));
            float w = lastMinute ? g.width * 0.3f : g.width;
            p.Fill(new Rect(g.xMax - w, yy, w, h), Accent);
        }

        /// <summary>
        /// 工場。煙突と建屋と煙で表す。
        /// この表示が見せるのは残高そのものではなく**工場が回っているか**なので、
        /// 折れ線ではなく工場そのものを描く。
        /// </summary>
        private static void DrawFactoryGlyph(Rect g, Color c, IChartPainter p)
        {
            float w = g.width;
            float h = g.height;
            float t = Mathf.Max(1f, h * 0.14f);

            Color smoke = new Color(c.r, c.g, c.b, c.a * 0.45f);

            // 煙突
            float stackW = Mathf.Max(2f, w * 0.16f);
            float stackX = g.x + w * 0.12f;
            p.Fill(new Rect(stackX, g.y + h * 0.34f, stackW, h * 0.66f), c);

            // 煙。煙突から離して立ち上らせる（くっつけると帽子に見える）
            float puff = Mathf.Max(2f, w * 0.11f);
            p.Fill(new Rect(stackX + stackW * 0.15f, g.y + h * 0.14f, puff, puff * 0.8f), smoke);
            p.Fill(new Rect(stackX + stackW * 0.9f, g.y, puff * 0.9f, puff * 0.7f), smoke);

            // 建屋。屋根を一段下げて工場らしく
            float bodyX = g.x + w * 0.4f;
            float bodyW = g.xMax - bodyX;
            p.Fill(new Rect(bodyX, g.y + h * 0.5f, bodyW * 0.5f, h * 0.5f), c);
            p.Fill(new Rect(bodyX + bodyW * 0.5f, g.y + h * 0.66f, bodyW * 0.5f, h * 0.34f), c);

            // 地面
            p.Fill(new Rect(g.x, g.yMax - t, w, t), c);
        }

        private static readonly float[] RisingLevels = new float[] { 0.16f, 0.4f, 0.28f, 0.72f };
        private static readonly float[] NetLevels = new float[] { 0.2f, 0.72f, 0.34f, 0.78f };
        private static readonly float[] FallingLevels = new float[] { 0.82f, 0.6f, 0.66f, 0.34f };

        /// <summary>
        /// 折れ線の絵。段と段の間を縦の帯で繋ぐ。
        /// 繋がないと点が散っているようにしか見えない（この大きさでは特に）。
        /// </summary>
        private static void DrawGlyphLine(Rect g, float[] levels, float thick, Color c, IChartPainter p)
        {
            float sw = g.width / levels.Length;
            float prev = 0f;

            for (int i = 0; i < levels.Length; i++)
            {
                float y = g.yMax - g.height * levels[i] - thick;
                p.Fill(new Rect(g.x + i * sw, y, Mathf.Max(1f, sw), thick), c);

                if (i > 0)
                {
                    float top = Mathf.Min(prev, y);
                    p.Fill(new Rect(g.x + i * sw, top, thick, Mathf.Abs(prev - y) + thick), c);
                }
                prev = y;
            }
        }

        /// <summary>
        /// 収支の純増減。「毎分どれだけ増えているか（減っているか）」を折れ線で出す。
        ///
        /// 残高そのものは本体の画面にも出ているし、返済に間に合うかは
        /// いくら持っているかより**どちらへ傾いているか**で決まる。
        /// ゼロとの間を塗るので、赤字に転んだ瞬間が形で分かる。
        ///
        /// 縦軸は他の表示と同じ数え方（直近60秒の合計）。数えるのは納品と生産の増減
        /// （IsSteadyFlow を見よ）。範囲を全体にすれば「いつ転んだか」、
        /// 直近1分にすれば「いまどうか」が読める。
        /// </summary>
        private void DrawNetChart(Rect r, ChartData d, IChartPainter p)
        {
            TagSeries s = d.Selected;
            int count = TotalBuckets(d);
            if (s == null || count <= 0) { DrawZeroLine(r, p); return; }

            BuildNet(d, s);

            int from = 0;
            if (d.LastMinute)
            {
                int span = Mathf.Max(2, Mathf.CeilToInt(60f / d.BucketSeconds));
                from = Mathf.Max(0, count - span);
            }
            if (count - from < 2) { from = Mathf.Max(0, count - 2); }

            long max = 0;
            long min = 0;
            for (int b = from; b < count && b < _netCount; b++)
            {
                if (_net[b] > max) { max = _net[b]; }
                if (_net[b] < min) { min = _net[b]; }
            }
            if (max == min) { max = min + 1; }

            float labelH = p.FontSize + 4f;
            float axisW = Mathf.Max(p.Measure(N(max)), p.Measure("-" + N(-min))) + 10f;

            Rect plot = new Rect(r.x + axisW, r.y + 2f, r.width - axisW - 6f, r.height - labelH - 6f);
            if (plot.width < 20f || plot.height < 20f) { return; }

            float zeroY = ValueToY(plot, min, max, 0);
            int shown = count - from;

            int px = Mathf.Max(2, Mathf.CeilToInt(plot.width));
            float prevY = 0f;

            for (int k = 0; k < px; k++)
            {
                int b = from + (int)((long)k * (shown - 1) / (px - 1));
                long v = (b < _netCount) ? _net[b] : 0L;
                float yy = ValueToY(plot, min, max, v);

                // ゼロとの間を塗る。どちらへ傾いているかが色で分かる
                float top = Mathf.Min(yy, zeroY);
                float h = Mathf.Abs(yy - zeroY);
                if (h >= 1f)
                {
                    p.Fill(new Rect(plot.x + k, top, 1f, h), v >= 0 ? NetFillUp : NetFillDown);
                }

                if (k == 0) { prevY = yy; }
                float lineTop = Mathf.Min(prevY, yy);
                p.Fill(new Rect(plot.x + k, lineTop, 1.6f, Mathf.Abs(prevY - yy) + 1.6f),
                    v >= 0 ? NetLineUp : NetLineDown);
                prevY = yy;
            }

            // ゼロの線は塗りの上に。ここが境目
            p.Fill(new Rect(plot.x, zeroY, plot.width, 1f), new Color(1f, 1f, 1f, 0.35f));

            p.Text(r.x, plot.y - 2f, N(max), Ink);
            p.Text(r.x, zeroY - p.FontSize * 0.6f, "0", new Color(0.75f, 0.72f, 0.68f));
            if (min < 0) { p.Text(r.x, plot.yMax - p.FontSize, "-" + N(-min), Ink); }

            DrawRepaymentMarks(plot, d, from, count, p);

            double t0 = from * (double)d.BucketSeconds;
            double t1 = count * (double)d.BucketSeconds;
            p.Text(plot.x, plot.yMax + 2f, FormatTime(t0), Ink);
            string right = FormatTime(t1);
            p.Text(plot.xMax - p.Measure(right), plot.yMax + 2f, right, Ink);
        }

        /// <summary>
        /// 純増減に数える流れ。**納品と生産の増減だけ**。
        ///
        /// 60秒の移動窓で見ているので、一度の大きな出入りが**60秒間そのまま居座る**。
        /// 返済のたびに「毎分5Kの赤字」と読める帯が1分続くが、それは
        /// 「1分前に返済した」であって傾きではない。
        ///
        /// そこで外す物を数え上げる形にすると際限がない——返済・土地・注文に加えて、
        /// 杖も電話もリロールも契約も、一度きりかどうかを1つずつ決める羽目になる。
        /// **数えるほうを決める。** 見たいのは「工場が回っているか」なので、
        /// 使い魔が掘って納めたぶん・作って納めたぶんと、作るのに使ったぶんを数える。
        /// 納品は DeliveryDepositor が SaleIncome で記録していて、
        /// 採掘物もクラフト物もポータルへ納めた時点でここに入る。
        ///
        /// 注文を外すのには別の理由もある。貢物を払って報酬を受け取る対なので
        /// （TributeDemand が OrderExpense で消費し、報酬が OrderIncome で入る）、
        /// 片側だけ外すと線が偏る。
        /// </summary>
        private static bool IsSteadyFlow(int reason)
        {
            return reason == 2      // SaleIncome     納品（採掘物もクラフト物もここ）
                || reason == 13     // CraftIncome    作って増えたぶん
                || reason == 14;    // CraftExpense   作るのに消えたぶん
        }

        /// <summary>
        /// 純増減をバケットごとに組む。値は「そこまでの60秒の収入合計 − 支出合計」。
        /// 何を数えるかは IsSteadyFlow を見よ。
        ///
        /// 走行が伸びると全部を組み直すのは高くつく（毎フレーム 1800×32 回の参照になる）ので、
        /// 長さや対象が変わったときだけ組み直し、あとは末尾のバケットだけ取り直す。
        /// 同じ秒の中で増減が来ても、末尾を直せば足りる。
        /// </summary>
        private void BuildNet(ChartData d, TagSeries s)
        {
            int count = TotalBuckets(d);
            if (count <= 0) { _netCount = 0; _netLast = 0; return; }

            int window = Mathf.Max(1, Mathf.CeilToInt(60f / d.BucketSeconds));

            if (_net == null || _net.Length < count)
            {
                int size = Mathf.Max(count, 256);
                _net = new long[size];
                _bucketNet = new long[size];
                _netTag = null;
            }

            bool rebuild = _netTag != s.TagID || _netCount != count || _netWindow != window;
            int start = rebuild ? 0 : count - 1;

            for (int b = start; b < count; b++)
            {
                long net = 0;
                for (int i = 0; i < ReasonCount; i++)
                {
                    if (!IsSteadyFlow(i)) { continue; }
                    net += s.FlowAt(b, i);
                    net -= s.FlowAt(b, ReasonCount + i);
                }
                _bucketNet[b] = net;

                long run = 0;
                int lo = b - window + 1;
                if (lo < 0) { lo = 0; }

                if (rebuild && b > 0 && lo > 0)
                {
                    // 直前の窓から出入りしたぶんだけ動かす
                    run = _net[b - 1] + net - _bucketNet[lo - 1];
                }
                else if (rebuild && b > 0)
                {
                    run = _net[b - 1] + net;
                }
                else
                {
                    for (int k = lo; k <= b; k++) { run += _bucketNet[k]; }
                }
                _net[b] = run;
            }

            _netTag = s.TagID;
            _netCount = count;
            _netWindow = window;
            _netLast = _net[count - 1];
        }

        private long[] _net;
        private long[] _bucketNet;
        private string _netTag;
        private int _netCount;
        private int _netWindow;
        private long _netLast;

        /// <summary>
        /// 直近1分の速度。出所ごとに「1秒あたりいくら入っているか」を折れ線で重ねる。
        ///
        /// 構成グラフは「何で稼いだか」の割合なので、1分だけ切り取ると
        /// たまたま1件入っただけで100%になってしまい読めない。
        /// 範囲を1分にしたときは割合ではなく**量そのもの**を見たいはずなので、
        /// ここだけ別の絵に差し替える。
        /// </summary>
        private void DrawRates(Rect r, ChartData d, IChartPainter p)
        {
            if (d.RateSources.Count == 0 || d.Selected == null) { DrawZeroLine(r, p); return; }

            float line = p.LineHeight;
            float legendIcon = Mathf.Max(12f, line);

            _rank.Clear();
            _rankTotals.Clear();
            _rateLines.Clear();

            long grand = 0;
            for (int i = 0; i < d.RateSources.Count && i < d.RateValues.Count; i++)
            {
                _rank.Add(d.RateSources[i]);
                _rankTotals.Add(d.RateTotals[i]);
                _rateLines.Add(d.RateValues[i]);
                grand += d.RateTotals[i];
            }
            if (grand <= 0) { DrawZeroLine(r, p); return; }

            AssignRankColors(d.Selected.TagID);

            // 凡例は必ず1行。入りきらないぶんは落とす（線も一緒に落とす）
            while (_rank.Count > 1 && LegendWidth(p, legendIcon, grand) > r.width)
            {
                _rank.RemoveAt(_rank.Count - 1);
                _rankColors.RemoveAt(_rankColors.Count - 1);
                _rankTotals.RemoveAt(_rankTotals.Count - 1);
                _rateLines.RemoveAt(_rateLines.Count - 1);
            }

            long shown = 0;
            for (int i = 0; i < _rankTotals.Count; i++) { shown += _rankTotals[i]; }
            float y = DrawSourceLegend(r.x, r.y, r.width, line, legendIcon, d.Selected.TagID,
                grand, grand - shown, p);

            // ---- 折れ線 ----
            float peak = 0f;
            int slots = 0;
            for (int i = 0; i < _rateLines.Count; i++)
            {
                float[] v = _rateLines[i];
                if (v.Length > slots) { slots = v.Length; }
                for (int k = 0; k < v.Length; k++) { if (v[k] > peak) { peak = v[k]; } }
            }
            if (peak <= 0f || slots < 2) { DrawZeroLine(new Rect(r.x, y, r.width, r.yMax - y), p); return; }

            float labelH = p.FontSize + 4f;
            float axisW = p.Measure(N((long)peak)) + 10f;
            float rightGap = line + 4f;

            Rect plot = new Rect(r.x + axisW, y + 2f,
                r.width - axisW - rightGap, r.yMax - y - labelH - 6f);
            if (plot.width < 20f || plot.height < 20f) { return; }

            p.Fill(new Rect(plot.x, plot.yMax, plot.width, 1f), Faint);
            p.Fill(new Rect(plot.x, plot.y + plot.height * 0.5f, plot.width, 1f), Fainter);

            p.Text(r.x, plot.y - 2f, N((long)peak), Ink);
            p.Text(r.x, plot.yMax - p.FontSize * 0.6f, "0", Ink);

            float span = slots - 1;
            float step = plot.width / span;
            float scale = plot.height / peak;

            for (int i = 0; i < _rateLines.Count; i++)
            {
                float[] v = _rateLines[i];
                Color c = _rankColors[i];

                // コマは60個しかないので、そのまま繋ぐと段々に見える。
                // 画素ごとに間を取って引く
                int px = Mathf.Max(2, Mathf.CeilToInt(plot.width));
                float prevY = 0f;

                for (int k = 0; k < px; k++)
                {
                    float at = (float)k / (px - 1) * span;
                    int i0 = Mathf.Clamp((int)at, 0, v.Length - 1);
                    int i1 = Mathf.Min(i0 + 1, v.Length - 1);

                    float yy = plot.yMax - Mathf.Lerp(v[i0], v[i1], at - i0) * scale;
                    if (k == 0) { prevY = yy; }

                    float top = Mathf.Min(prevY, yy);
                    p.Fill(new Rect(plot.x + k, top, 1.6f, Mathf.Abs(prevY - yy) + 1.6f), c);
                    prevY = yy;
                }

                // 線の右端にその出所の絵。どの線が何かを言葉なしで示す
                float iconSize = Mathf.Min(rightGap, line);
                if (iconSize >= 10f)
                {
                    Rect box = new Rect(plot.xMax + 2f, prevY - iconSize * 0.5f, iconSize, iconSize);
                    box.y = Mathf.Clamp(box.y, plot.y, plot.yMax - iconSize);
                    DrawSourceIcon(box, _rank[i], d.Selected.TagID, c, p);
                }
            }

            // 横軸は「何秒前か」。0 が現在
            string left = "-" + ((int)(span * d.RateSlotSeconds)).ToString(CultureInfo.InvariantCulture) + "s";
            p.Text(plot.x, plot.yMax + 2f, left, Ink);
            p.Text(plot.xMax - p.Measure("0"), plot.yMax + 2f, "0", Ink);

            // ---- カーソルの位置 ----
            if (!d.MouseValid) { return; }
            if (d.Mouse.x < plot.x || d.Mouse.x > plot.xMax) { return; }
            if (d.Mouse.y < plot.y || d.Mouse.y > plot.yMax) { return; }

            int hoverAt = Mathf.Clamp(Mathf.RoundToInt((d.Mouse.x - plot.x) / plot.width * span), 0, slots - 1);
            float gx = plot.x + hoverAt / span * plot.width;

            // どの時点を読んでいるかが分かるように縦線を引く
            p.Fill(new Rect(gx, plot.y, 1f, plot.height), new Color(1f, 1f, 1f, 0.35f));

            for (int i = 0; i < _rateLines.Count; i++)
            {
                float[] v = _rateLines[i];
                if (hoverAt >= v.Length) { continue; }
                float yy = plot.yMax - v[hoverAt] * scale;
                p.Fill(new Rect(gx - 2f, yy - 2f, 5f, 5f), _rankColors[i]);
            }

            DrawRateHover(plot, d, hoverAt, slots, p);
        }

        /// <summary>
        /// 折れ線のホバー。カーソルの時点の値を、出所ごとに多い順で出す。
        /// 線が重なっていると絵だけでは読めないので、ここは数字を添える。
        /// </summary>
        private void DrawRateHover(Rect plot, ChartData d, int at, int slots, IChartPainter p)
        {
            _rateOrder.Clear();
            long sum = 0;
            for (int i = 0; i < _rateLines.Count; i++)
            {
                if (at >= _rateLines[i].Length || _rateLines[i][at] <= 0f) { continue; }
                _rateOrder.Add(i);
                sum += (long)_rateLines[i][at];
            }
            if (_rateOrder.Count == 0) { return; }

            float[][] lines = _rateLines.ToArray();
            _rateOrder.Sort(delegate (int a, int b) { return lines[b][at].CompareTo(lines[a][at]); });

            float iconSize = Mathf.Max(12f, p.LineHeight);
            int rows = Mathf.Min(_rateOrder.Count, HoverRows);
            float line = Mathf.Max(iconSize, p.LineHeight) + 2f;
            float pad = 6f;

            float valueWidth = 0f;
            float percentWidth = 0f;
            float nameWidth = 0f;
            for (int i = 0; i < rows; i++)
            {
                SourceSeries src = _rank[_rateOrder[i]];
                long v = (long)_rateLines[_rateOrder[i]][at];
                valueWidth = Mathf.Max(valueWidth, p.Measure(N(v)));
                percentWidth = Mathf.Max(percentWidth, p.Measure(Percent(v, sum)));
                if (!src.HasSource)
                {
                    nameWidth = Mathf.Max(nameWidth, p.Measure(p.ReasonLabel(src.Reason)) + 8f);
                }
            }

            // 1行目は「何秒前 → タグのアイコン → その時点の合計」
            int ago = (int)((slots - 1 - at) * d.RateSlotSeconds);
            string when = (ago > 0)
                ? "-" + ago.ToString(CultureInfo.InvariantCulture) + "s"
                : "0";
            string sumLabel = N(sum);
            float headerWidth = p.Measure(when) + 6f + iconSize + 4f + p.Measure(sumLabel);

            float width = Mathf.Max(headerWidth,
                iconSize + 6f + nameWidth + valueWidth + 6f + percentWidth) + pad * 2f;
            float height = pad * 2f + line + rows * line;
            if (_rateOrder.Count > rows) { height += p.LineHeight; }

            float x = d.Mouse.x + 14f;
            float y = d.Mouse.y + 10f;
            if (x + width > plot.xMax) { x = d.Mouse.x - 14f - width; }
            if (y + height > plot.yMax) { y = plot.yMax - height; }
            if (x < plot.x) { x = plot.x; }
            if (y < plot.y) { y = plot.y; }

            p.Fill(new Rect(x, y, width, height), new Color(0.04f, 0.04f, 0.05f, 0.94f));
            p.Fill(new Rect(x, y, width, 1f), new Color(1f, 1f, 1f, 0.25f));
            p.Fill(new Rect(x, y + height - 1f, width, 1f), new Color(1f, 1f, 1f, 0.25f));
            p.Fill(new Rect(x, y, 1f, height), new Color(1f, 1f, 1f, 0.25f));
            p.Fill(new Rect(x + width - 1f, y, 1f, height), new Color(1f, 1f, 1f, 0.25f));

            float textOffset = (iconSize - p.FontSize) * 0.5f;
            float ty = y + pad;
            float hx = x + pad;

            p.Text(hx, ty + textOffset, when, Accent);
            hx += p.Measure(when) + 6f;
            p.Icon(new Rect(hx, ty, iconSize, iconSize), null, d.Selected.TagID, Ink, IconOutline);
            hx += iconSize + 4f;
            p.Text(hx, ty + textOffset, sumLabel, Ink);

            ty += line;

            for (int i = 0; i < rows; i++)
            {
                int k = _rateOrder[i];
                SourceSeries src = _rank[k];
                long v = (long)_rateLines[k][at];

                DrawSourceIcon(new Rect(x + pad, ty, iconSize, iconSize), src, d.Selected.TagID, _rankColors[k], p);

                if (!src.HasSource)
                {
                    string name = p.ReasonLabel(src.Reason);
                    if (!string.IsNullOrEmpty(name))
                    {
                        p.Text(x + pad + iconSize + 6f, ty + textOffset, name,
                            new Color(0.78f, 0.75f, 0.70f));
                    }
                }

                string value = N(v);
                p.Text(x + pad + iconSize + 6f + nameWidth + (valueWidth - p.Measure(value)),
                    ty + textOffset, value, Ink);

                string percent = Percent(v, sum);
                p.Text(x + width - pad - p.Measure(percent), ty + textOffset, percent,
                    new Color(0.75f, 0.72f, 0.68f));

                ty += line;
            }

            if (_rateOrder.Count > rows)
            {
                p.Text(x + pad, ty, "+" + (_rateOrder.Count - rows).ToString(CultureInfo.InvariantCulture),
                    new Color(0.65f, 0.62f, 0.58f));
            }
        }

        /// <summary>
        /// 記録が1件も無いときの残高。空白にせず、軸と**底を這う線**を描く。
        /// ずっと0だったという意味がそのまま形になる。
        /// </summary>
        private static void DrawZeroLine(Rect r, IChartPainter p)
        {
            float labelH = p.FontSize + 4f;
            float axisW = p.Measure("0") + 10f;

            Rect plot = new Rect(r.x + axisW, r.y + 2f, r.width - axisW - 4f, r.height - labelH - 6f);
            if (plot.width < 8f || plot.height < 8f) { return; }

            p.Fill(new Rect(plot.x, plot.y, 1f, plot.height), Fainter);
            p.Fill(new Rect(plot.x, plot.yMax, plot.width, 2f), ZeroColor);
            p.Text(r.x, plot.yMax - p.FontSize * 0.6f, "0", Ink);
        }

        /// <summary>
        /// その列に収入も支出も無かったことを示す「高さ0の棒」。
        ///
        /// 何も描かないと列が抜けているようにしか見えず、
        /// 描画が壊れているのと区別が付かない。棒の場所を薄い枠で示したうえで、
        /// 底に短い帯を敷く（＝高さ0の棒）。
        /// </summary>
        private static void DrawZeroColumn(float cx, float bw, float baseline, float fullHeight, IChartPainter p)
        {
            Rect slot = new Rect(cx + 1f, baseline - fullHeight, bw, fullHeight);

            p.Fill(new Rect(slot.x, slot.y, slot.width, 1f), Fainter);
            p.Fill(new Rect(slot.x, slot.y, 1f, slot.height), Fainter);
            p.Fill(new Rect(slot.xMax - 1f, slot.y, 1f, slot.height), Fainter);

            float h = Mathf.Max(4f, fullHeight * 0.018f);
            p.Fill(new Rect(slot.x, baseline - h, slot.width, h), ZeroColor);
        }

        // ------------------------------------------------------------------
        // 残高
        // ------------------------------------------------------------------

        /// <summary>
        /// 残高の折れ線。single が null なら全タグを重ねる。
        /// 横軸は「全体」か「直近1分」。
        /// </summary>
        private void DrawBalanceChart(Rect r, ChartData d, TagSeries single, IChartPainter p)
        {
            int count = TotalBuckets(d);
            if (count <= 0) { DrawZeroLine(r, p); return; }

            int from = 0;
            if (d.LastMinute)
            {
                int span = Mathf.Max(2, Mathf.CeilToInt(60f / d.BucketSeconds));
                from = Mathf.Max(0, count - span);
            }
            if (count - from < 2) { from = Mathf.Max(0, count - 2); }

            float axisWidth = p.Measure("00,000") + 10f;
            float labelH = p.FontSize + 4f;

            // 重ねるときは右端に線のアイコンを置くので、そのぶん場所を空ける
            float rightGap = (single == null) ? p.LineHeight + 4f : 4f;
            Rect plot = new Rect(r.x + axisWidth, r.y + 2f, r.width - axisWidth - rightGap, r.height - labelH - 6f);
            if (plot.width < 20f || plot.height < 20f) { return; }

            // 縦軸は「見えている範囲」の上下で取る。0 に固定すると、
            // 直近1分のように値が高止まりしている場面で全部が塗り潰しになって何も読めない。
            long min = long.MaxValue, max = long.MinValue;
            if (single != null) { ScanRange(single, from, count, ref min, ref max); }
            else
            {
                for (int i = 0; i < d.Ordered.Count; i++)
                {
                    if (d.HiddenTags.Contains(d.Ordered[i].TagID)) { continue; }
                    ScanRange(d.Ordered[i], from, count, ref min, ref max);
                }
            }
            if (min == long.MaxValue) { min = 0; max = 1; }
            if (max <= min) { max = min + 1; }

            // 上下が枠に張り付くと読めないので、両側に余白を足す
            long pad = (long)Math.Max(1.0, (max - min) * 0.06);
            max += pad;
            min -= pad;
            if (min < 0 && !HasNegative(d, single, from, count)) { min = 0; }

            // 目盛り（上端・中央・下端）
            p.Fill(new Rect(plot.x, plot.yMax, plot.width, 1f), Faint);
            p.Fill(new Rect(plot.x, plot.y + plot.height * 0.5f, plot.width, 1f), Fainter);

            float axisRight = r.x + axisWidth - 6f;
            long half = min + (max - min) / 2;
            p.Text(axisRight - p.Measure(N(max)), plot.y - 2f, N(max), Ink);
            p.Text(axisRight - p.Measure(N(half)), plot.y + plot.height * 0.5f - p.FontSize * 0.5f, N(half), Ink);
            p.Text(axisRight - p.Measure(N(min)), plot.yMax - p.FontSize, N(min), Ink);

            DrawRepaymentMarks(plot, d, from, count, p);

            int cols = ColumnCount(plot.width, d.MaxColumns);
            float colW = plot.width / cols;

            if (single != null)
            {
                DrawBalanceLine(plot, single, from, count, cols, colW, min, max, TagColor(single.TagID), p);
            }
            else
            {
                // 重ねるときは、アイコンの色が近いタグ同士（現金と食品など）が
                // 見分けられなくなるので、明るさだけずらして離す
                _lineColors.Clear();
                for (int i = 0; i < d.Ordered.Count; i++)
                {
                    if (d.HiddenTags.Contains(d.Ordered[i].TagID)) { continue; }
                    _lineColors.Add(Separate(TagColor(d.Ordered[i].TagID), _lineColors));
                }

                int slot = 0;
                for (int i = 0; i < d.Ordered.Count; i++)
                {
                    if (d.HiddenTags.Contains(d.Ordered[i].TagID)) { continue; }
                    DrawBalanceLine(plot, d.Ordered[i], from, count, cols, colW, min, max, _lineColors[slot], p);
                    slot++;
                }

                DrawLineEndCaps(plot, d, count, min, max, p);
            }

            // 時間の目盛り（数字だけ）
            double t0 = from * (double)d.BucketSeconds;
            double t1 = count * (double)d.BucketSeconds;
            p.Text(plot.x, plot.yMax + 2f, FormatTime(t0), Ink);
            string right = FormatTime(t1);
            p.Text(plot.xMax - p.Measure(right), plot.yMax + 2f, right, Ink);
            string mid = FormatTime((t0 + t1) * 0.5);
            p.Fill(new Rect(plot.x + plot.width * 0.5f, plot.yMax, 1f, 4f), Faint);
            p.Text(plot.x + (plot.width - p.Measure(mid)) * 0.5f, plot.yMax + 2f, mid, Ink);
        }

        /// <summary>実際に負の値があるか。無いのに軸を負まで伸ばすと、下に無駄な空きができる。</summary>
        private static bool HasNegative(ChartData d, TagSeries single, int from, int count)
        {
            if (single != null) { return HasNegative(single, from, count); }
            for (int i = 0; i < d.Ordered.Count; i++)
            {
                if (d.HiddenTags.Contains(d.Ordered[i].TagID)) { continue; }
                if (HasNegative(d.Ordered[i], from, count)) { return true; }
            }
            return false;
        }

        private static bool HasNegative(TagSeries s, int from, int count)
        {
            for (int b = from; b < count; b++)
            {
                if (s.BalanceAt(b) < 0) { return true; }
            }
            return false;
        }

        /// <summary>
        /// 重ね表示のとき、線の右端にそのタグのアイコンを置く。
        /// 8本を色だけで追うのは無理があるので、線とタグを直に結びつける。
        /// 終値が近い線同士はアイコンが重なるので、上下に押し分ける。
        /// </summary>
        private void DrawLineEndCaps(Rect plot, ChartData d, int count, long min, long max, IChartPainter p)
        {
            float size = p.LineHeight;
            if (plot.height < size * 2f) { return; }

            _capOrder.Clear();
            _capY.Clear();
            for (int i = 0; i < d.Ordered.Count; i++)
            {
                if (d.HiddenTags.Contains(d.Ordered[i].TagID)) { continue; }
                _capOrder.Add(d.Ordered[i]);
                _capY.Add(ValueToY(plot, min, max, d.Ordered[i].BalanceAt(count - 1)));
            }
            if (_capOrder.Count == 0) { return; }

            // 上から順に、間隔が足りなければ下へずらす
            SortCapsByY();
            float previous = float.MinValue;
            for (int i = 0; i < _capY.Count; i++)
            {
                float y = Mathf.Max(_capY[i], previous + size + 1f);
                _capY[i] = Mathf.Min(y, plot.yMax - size);
                previous = _capY[i];
            }

            float x = plot.xMax - size;
            for (int i = 0; i < _capOrder.Count; i++)
            {
                Rect box = new Rect(x, _capY[i] - size * 0.5f, size, size);
                p.Icon(box, null, _capOrder[i].TagID, Ink, IconOutline);

                // 線の端の絵も押せるようにする。目で追っている線をその場で消せる
                // （下の帯まで視線を戻さずに済む）。
                // 消した線には端が無くなるので、戻すのは下の帯から
                d.IconRects.Add(box);
                d.IconTagIDs.Add(_capOrder[i].TagID);
            }
        }

        /// <summary>アイコンと位置を、位置の小さい順に並べ替える（対の並べ替えなので手で書く）。</summary>
        private void SortCapsByY()
        {
            for (int i = 1; i < _capY.Count; i++)
            {
                float y = _capY[i];
                TagSeries series = _capOrder[i];
                int j = i - 1;
                while (j >= 0 && _capY[j] > y)
                {
                    _capY[j + 1] = _capY[j];
                    _capOrder[j + 1] = _capOrder[j];
                    j--;
                }
                _capY[j + 1] = y;
                _capOrder[j + 1] = series;
            }
        }

        private static void ScanRange(TagSeries s, int from, int count, ref long min, ref long max)
        {
            for (int b = from; b < count; b++)
            {
                long v = s.BalanceAt(b);
                if (v < min) { min = v; }
                if (v > max) { max = v; }
            }
        }

        /// <summary>
        /// 折れ線。描き方を2通り使い分ける。
        ///
        /// 点が列より多いとき（ランの全体表示など）は、1列が受け持つバケットの
        /// 最小〜最大を縦棒で結ぶ。山と谷が潰れず、線も途切れない。
        ///
        /// 点が列より少ないとき（直近1分など。1秒の点が1本6〜7画素に広がる）は、
        /// 同じやり方だと値が横に平らに伸びて**階段状**になる。
        /// こちらは点と点の間を線で結んで、素直な折れ線にする。
        /// </summary>
        private static void DrawBalanceLine(Rect plot, TagSeries s, int from, int count, int cols, float colW,
            long min, long max, Color stroke, IChartPainter p)
        {
            int shown = count - from;
            if (shown <= 0) { return; }

            if (shown < cols)
            {
                DrawInterpolatedLine(plot, s, from, count, cols, colW, min, max, stroke, p);
                return;
            }

            // 前の列の終わりの高さ。ここまで伸ばして繋ぐ。
            // 繋がないと、急に増えた／返済で急に減った所で列と列の間が空いて点線になる
            float previousY = float.NaN;

            for (int i = 0; i < cols; i++)
            {
                int b0 = from + (int)((long)i * shown / cols);
                int b1 = from + (int)((long)(i + 1) * shown / cols);
                if (b1 <= b0) { b1 = b0 + 1; }

                long lo = long.MaxValue, hi = long.MinValue;
                long last = 0;
                for (int b = b0; b < b1 && b < count; b++)
                {
                    long v = s.BalanceAt(b);
                    if (v < lo) { lo = v; }
                    if (v > hi) { hi = v; }
                    last = v;
                }
                if (lo == long.MaxValue) { continue; }

                float yHi = ValueToY(plot, min, max, hi);
                float yLo = ValueToY(plot, min, max, lo);

                if (!float.IsNaN(previousY))
                {
                    yHi = Mathf.Min(yHi, previousY);
                    yLo = Mathf.Max(yLo, previousY);
                }

                float cx = plot.x + i * colW;
                p.Fill(new Rect(cx, yHi, colW, Mathf.Max(2f, yLo - yHi + 2f)), stroke);

                previousY = ValueToY(plot, min, max, last);
            }
        }

        /// <summary>点の間を線で結ぶ。1列ごとに、前の列の高さから今の高さまでを縦棒で埋める。</summary>
        private static void DrawInterpolatedLine(Rect plot, TagSeries s, int from, int count, int cols, float colW,
            long min, long max, Color stroke, IChartPainter p)
        {
            int shown = count - from;
            float previous = float.NaN;

            for (int i = 0; i < cols; i++)
            {
                // 列の位置をバケット空間の連続値に写して、前後の点を線形に混ぜる
                float position = from + (float)i * (shown - 1) / (cols - 1);
                int b0 = (int)position;
                int b1 = Mathf.Min(b0 + 1, count - 1);
                float t = position - b0;

                float value = s.BalanceAt(b0) * (1f - t) + s.BalanceAt(b1) * t;
                float y = ValueToYFloat(plot, min, max, value);
                float cx = plot.x + i * colW;

                float top = float.IsNaN(previous) ? y : Mathf.Min(previous, y);
                float bottom = float.IsNaN(previous) ? y : Mathf.Max(previous, y);
                p.Fill(new Rect(cx, top, colW, Mathf.Max(2f, bottom - top + 2f)), stroke);

                previous = y;
            }
        }

        private static float ValueToYFloat(Rect plot, long min, long max, float value)
        {
            double t = (value - (double)min) / (double)(max - min);
            if (t < 0.0) { t = 0.0; }
            if (t > 1.0) { t = 1.0; }
            return plot.yMax - (float)(t * plot.height);
        }

        /// <summary>返済の区切りを縦線と回数の数字で。</summary>
        private static void DrawRepaymentMarks(Rect plot, ChartData d, int from, int count, IChartPainter p)
        {
            double t0 = from * (double)d.BucketSeconds;
            double t1 = count * (double)d.BucketSeconds;
            double span = t1 - t0;
            if (span <= 0.0) { return; }

            // 全部に番号を振ると上端が数字で埋まるので、**5の倍数だけ**目立たせる。
            // 区切り自体は全部に線を引くが、5の倍数は濃く、それ以外は薄く。
            // 番号が重なるときは出さない（まとめて返済されると区切りが詰まる）。
            float usedUntil = float.MinValue;

            Color minor = new Color(0.92f, 0.34f, 0.36f, 0.28f);
            Color major = new Color(0.95f, 0.38f, 0.40f, 0.85f);

            for (int i = 0; i < d.PeriodEnds.Count; i++)
            {
                double t = d.PeriodEnds[i];
                if (t < t0 || t > t1) { continue; }

                // 同時に複数返済すると回数が飛ぶ。5の倍数を跨いだ区切りを目立たせる
                int number = (i < d.PeriodRepaid.Count) ? d.PeriodRepaid[i] : i + 1;
                int before = (i > 0 && i - 1 < d.PeriodRepaid.Count) ? d.PeriodRepaid[i - 1] : 0;
                bool isMajor = (number / 5) != (before / 5);
                float cx = plot.x + (float)((t - t0) / span) * plot.width;

                p.Fill(new Rect(cx, plot.y, isMajor ? 2f : 1f, plot.height), isMajor ? major : minor);
                if (!isMajor) { continue; }

                string label = number.ToString(CultureInfo.InvariantCulture);
                if (cx + 3f < usedUntil) { continue; }

                p.Text(cx + 3f, plot.y, label, new Color(0.98f, 0.62f, 0.62f));
                usedUntil = cx + 3f + p.Measure(label) + 3f;
            }
        }

        // ------------------------------------------------------------------
        // 構成（返済1回ごとの100%積み上げ）
        // ------------------------------------------------------------------

        /// <summary>
        /// 収入（支出タブなら支出）の構成。理由ではなく**出所**で割る。
        /// 売却ならアイテム、レリックならそのレリック、クラフトならレシピ。
        ///
        /// 種類が多くなるので、
        ///   ・上位 N 本だけ描いて、残りは「その他」1本にまとめる
        ///   ・色は順位で固定（1位はいつも同じ色）
        ///   ・積み上げの中にアイコンを直接描く（高さが足りる段だけ）
        ///   ・凡例もアイコンと % だけ
        /// として、言葉なしでも何がどれか分かるようにしてある。
        /// </summary>
        private void DrawComposition(Rect r, ChartData d, IChartPainter p)
        {
            TagSeries s = d.Selected;
            List<SourceSeries> all = d.ExpenseSide ? s.ExpenseSources : s.IncomeSources;

            float line = p.LineHeight;
            float barH = Mathf.Max(10f, line * 0.7f);

            // 段の中のアイコンは一回り大きくする。顔（納品＝ゲヘナ）のように
            // 描き込みの多い絵は、行の高さと同じ大きさだと潰れて読めない。
            float iconSize = Mathf.Max(16f, line * 1.6f);

            // 凡例は文字と並ぶ行なので、行の高さに合わせる。
            // ここまで大きくすると1行に5本しか入らず、残りが「その他」に化けてしまう。
            float legendIcon = Mathf.Max(12f, line);

            long grand = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Total > 0) { grand += all[i].Total; }
            }
            if (grand <= 0) { grand = 1; }

            // 凡例は必ず1行に収める。まず上限まで並べてから、実際の幅を測って
            // 入りきらないぶんを小さいものから「その他」へ落とす。
            // 見積もりで決め打つと（% の桁が読めないので）余計に削ってしまう。
            RankSources(all, s.TagID, SourcePalette.Length);
            while (_rank.Count > 1 && LegendWidth(p, legendIcon, grand) > r.width)
            {
                _rank.RemoveAt(_rank.Count - 1);
                _rankColors.RemoveAt(_rankColors.Count - 1);
            }

            // 収入も支出も1件も無いことはある（支出タブを開いた直後など）。
            // 帯と凡例は出しようが無いが、列は並べて「高さ0の棒」で埋める。
            // 空白にすると、記録が0なのか描画が壊れたのか分からない
            if (_rank.Count == 0)
            {
                DrawPeriodColumns(r, d, all, iconSize, p);
                return;
            }

            long shownTotal = 0;
            for (int i = 0; i < _rank.Count; i++) { shownTotal += _rank[i].Total; }
            long others = grand - shownTotal;

            // ---- ラン全体の内訳を1本の帯で ----
            float bx = r.x;
            for (int i = 0; i < _rank.Count; i++)
            {
                float w = r.width * (float)((double)_rank[i].Total / grand);
                p.Fill(new Rect(bx, r.y, Mathf.Max(1f, w - 1f), barH), _rankColors[i]);
                bx += w;
            }
            if (others > 0)
            {
                p.Fill(new Rect(bx, r.y, Mathf.Max(1f, r.xMax - bx), barH), OthersColor);
            }
            float y = r.y + barH + 4f;

            // ---- 凡例（アイコンと % だけ）----
            y = DrawSourceLegend(r.x, y, r.width, line, legendIcon, s.TagID, grand, others, p);

            // ---- 返済1回ごとの100%積み上げ ----
            Rect cols = new Rect(r.x, y, r.width, r.yMax - y);
            if (cols.height > 40f) { DrawPeriodColumns(cols, d, all, iconSize, p); }
        }

        /// <summary>その列の返済に使われた総額。0 なら「何で返したか」が分からない列。</summary>
        private static long ColumnRepaidSum(List<SourceSeries> all, int period)
        {
            long total = 0;
            for (int i = 0; i < all.Count; i++) { total += all[i].RepaidAt(period); }
            return total;
        }

        private static long Value(SourceSeries src, int period, bool byRepaid)
        {
            return byRepaid ? src.RepaidAt(period) : src.At(period);
        }

        /// <summary>凡例1行ぶんの実寸。入りきるかの判定に使う。</summary>
        private float LegendWidth(IChartPainter p, float iconSize, long grand)
        {
            float total = 0f;
            long shown = 0;
            for (int i = 0; i < _rank.Count; i++)
            {
                shown += _rankTotals[i];
                total += iconSize + 2f + p.Measure(Percent(_rankTotals[i], grand)) + 10f;
            }

            long others = grand - shown;
            if (others > 0) { total += iconSize + 2f + p.Measure(Percent(others, grand)) + 10f; }
            return total;
        }

        private float DrawSourceLegend(float x, float y, float w, float line, float iconSize,
            string tagID, long grand, long others, IChartPainter p)
        {
            float cx = x;
            float cy = y;
            int rows = 1;

            for (int i = 0; i <= _rank.Count; i++)
            {
                bool isOthers = i == _rank.Count;
                if (isOthers && others <= 0) { break; }

                long value = isOthers ? others : _rankTotals[i];
                int pct = (int)Math.Round((double)value * 100.0 / grand);
                string label = pct.ToString(CultureInfo.InvariantCulture) + "%";

                float cellW = iconSize + 2f + p.Measure(label) + 10f;

                if (cx + cellW > x + w)
                {
                    if (rows >= 2) { break; }
                    rows++;
                    cx = x;
                    cy += line + 2f;
                }

                Color color = isOthers ? OthersColor : _rankColors[i];
                Rect iconRect = new Rect(cx, cy, iconSize, iconSize);
                if (isOthers) { p.Fill(iconRect, color); }
                else { DrawSourceIcon(iconRect, _rank[i], tagID, color, p); }

                // 色と結びつくように、アイコンの下に色の帯を敷く
                p.Fill(new Rect(cx, cy + iconSize, iconSize, 2f), color);

                p.Text(cx + iconSize + 2f, cy + (iconSize - p.FontSize) * 0.5f, label, Ink);
                cx += cellW;
            }
            return cy + iconSize + 6f;
        }

        /// <summary>
        /// 返済1回ぶんが1列。列の中はその期の内訳を100%に伸ばして積む。
        /// 横軸は時間ではなく回数なので、長い期も短い期も同じ幅の1列になる。
        ///
        /// 列は**返すたびに増える**。完済までの回数は難易度で変わり
        /// （WinCondition.CalcTargetProgress ＝ 難易度+5）、しかも追加融資で走行中に増えるので、
        /// 先に全部の枠を取ると序盤は空きだらけになるし、増えたときに幅が変わってしまう。
        /// 総回数のほうは見出しに「いま/全体」の数字で出している。
        /// </summary>
        private void DrawPeriodColumns(Rect r, ChartData d, List<SourceSeries> all, float iconSize, IChartPainter p)
        {
            int total = Mathf.Max(1, d.PeriodIndex + 1);

            float labelH = p.FontSize + 4f;

            // 100%積み上げだと分かれば 0/50/100% の数字は用を成さないので置かない。
            // その幅（40px近い）を列に回すと、既定の25回でもアイコンが入る大きさになる。
            float axisW = 2f;
            Rect plot = new Rect(r.x + axisW, r.y, r.width - axisW - 2f, r.height - labelH - 2f);
            if (plot.width < 20f || plot.height < 20f) { return; }

            // いまの期だけは 100% に伸ばさず「ノルマに対して何%か」で高さを決める。
            // 固定費用のデバフで現金が負になることがあるが、そのときは 0% 扱いにして
            // 列を空にするだけにする（記録している内訳そのものは正しいままで、表示だけの話）。
            bool useNorm = UsesNorm(d);
            float normRatio = useNorm ? NormRatio(d) : 1f;

            float baseline = plot.yMax;
            float fullHeight = plot.height;

            // 目盛りは真ん中の線だけ。数字は置かない
            p.Fill(new Rect(plot.x, baseline - fullHeight * 0.5f, plot.width, 1f), Faint);
            p.Fill(new Rect(plot.x, baseline, plot.width, 1f), Faint);

            float cw = plot.width / total;
            float bw = Mathf.Max(1f, cw - 2f);
            int current = total - 1;

            // 段の中に描くアイコンの大きさは、このグラフの中では**ひとつに固定**する。
            // 段ごとに変えると不揃いに見えるし、段の大きさに合わせて伸ばすと大味になる。
            // 基準は凡例と同じ小さい大きさで、列が細いときだけそれに合わせて縮める。
            // 背が足りない段はアイコンを出さない。
            // 列の左右には既に1pxずつ隙間があるので、幅はそのまま使ってよい。
            // ここを削ると既定の25回でちょうど下限を割ってアイコンが消える
            float iconFit = Mathf.Min(iconSize, bw);

            for (int period = 0; period < total; period++)
            {
                // 列は返済1回ぶん。何で返したかが分かるならそれで埋める
                // （持ち越しだけで返した回でも中身が出る）。
                // 分からない列（返済がまだ／支出側）は、その期の収入で埋める。
                bool byRepaid = ColumnRepaidSum(all, period) > 0;

                long sum = 0;
                for (int i = 0; i < all.Count; i++) { sum += Value(all[i], period, byRepaid); }

                float cx = plot.x + period * cw;
                bool isCurrent = period == current;

                // 進行中の期は、その列だけ高さの意味が違うので枠で囲って区別する
                if (isCurrent) { DrawCurrentFrame(new Rect(cx, plot.y, cw, baseline - plot.y), p); }

                // 進行中の期は「いま手元にある額」を出所別に積む（台帳）。
                // その期の収入だけで積むと、ノルマ超過で返済したときの持ち越しが消える。
                bool useHolding = isCurrent && useNorm;
                if (useHolding)
                {
                    DrawHoldingColumn(cx, bw, baseline, fullHeight, d, all, p);
                    continue;
                }

                if (sum <= 0)
                {
                    // その列では何も動かなかった。高さ0の棒として描く
                    DrawZeroColumn(cx, bw, baseline, fullHeight, p);
                    continue;
                }

                // 終わった期はその期の収入を 100% に伸ばす
                float columnHeight = fullHeight;
                float bandScale = (sum > 0) ? columnHeight / sum : 0f;

                float yy = baseline;
                long shown = 0;
                for (int i = 0; i < _rank.Count; i++)
                {
                    long v = Value(_rank[i], period, byRepaid);
                    if (v <= 0) { continue; }
                    shown += v;

                    float h = v * bandScale;
                    if (h <= 0f) { continue; }
                    yy -= h;
                    p.Fill(new Rect(cx + 1f, yy, bw, Mathf.Max(1f, h)), _rankColors[i]);

                    // 段が十分に高ければ、その段のアイコンを中に描く。
                    // 列は返済回数ぶんに割るので細い。アイコンは列幅まで縮めて入れる。
                    // 読めない大きさなら描かない。返済が最大（30回）まで進むと
                    // 1列が12px程度になり、そこへ絵を押し込んでも色の塊にしかならない
                    if (iconFit >= 16f && h >= iconFit + 4f)
                    {
                        float fit = iconFit;
                        // 段の色はそのアイコンの代表色そのものなので絵が下地に溶けやすいが、
                        // 下に座（四角）を敷くと今度はそれが目立ちすぎる。分離は白縁だけで取る。
                        Rect iconRect = new Rect(cx + 1f + (bw - fit) * 0.5f, yy + (h - fit) * 0.5f, fit, fit);
                        DrawSourceIcon(iconRect, _rank[i], d.Selected.TagID, _rankColors[i], p);
                    }
                }

                long rest = sum - shown;
                if (rest > 0)
                {
                    float h = rest * bandScale;
                    yy -= h;
                    p.Fill(new Rect(cx + 1f, yy, bw, Mathf.Max(1f, h)), OthersColor);
                }
            }

            // 回数の目盛り。少ないうちは全部、増えたら5回ごと。
            // 同時に複数返済した列は回数が飛ぶので、跨いだところに出す
            bool everyColumn = total <= 12;
            for (int period = 0; period < total; period++)
            {
                // 進行中の列（まだ返済していない）には番号を出さない。
                // 出すと「もう返した回」と見分けがつかなくなる
                if (period >= d.PeriodRepaid.Count) { continue; }

                int done = d.PeriodRepaid[period];
                int before = (period > 0) ? d.PeriodRepaid[period - 1] : 0;

                bool show = everyColumn || period == 0 || (done / 5) != (before / 5);
                if (!show) { continue; }

                string label = done.ToString(CultureInfo.InvariantCulture);
                p.Text(plot.x + period * cw + cw * 0.5f - p.Measure(label) * 0.5f, plot.yMax + 2f, label, Ink);
            }

            DrawHover(plot, d, all, total, cw, iconSize, useNorm, p);
        }

        /// <summary>
        /// 列にマウスが乗っていたら、その回の内訳を出す。
        /// グラフに描けるのは上位だけ（残りは「その他」に寄せている）なので、
        /// ここでは**その回に効いたものを全部**、多い順に並べる。
        /// </summary>
        private void DrawHover(Rect plot, ChartData d, List<SourceSeries> all, int total, float cw,
            float iconSize, bool useNorm, IChartPainter p)
        {
            if (!d.MouseValid) { return; }
            if (!plot.Contains(d.Mouse)) { return; }

            int period = (int)((d.Mouse.x - plot.x) / cw);
            if (period < 0 || period >= total) { return; }

            // 進行中の列は台帳（いま手元にある額）で描いている。
            // ホバーもそれに合わせないと、棒と中身が食い違う
            _hoverHeld = useNorm && period == total - 1;
            _hoverByRepaid = ColumnRepaidSum(all, period) > 0;
            _hoverPeriod = period;

            _hover.Clear();
            long sum = 0;
            for (int i = 0; i < all.Count; i++)
            {
                long v = HoverValue(all[i]);
                if (v <= 0) { continue; }
                _hover.Add(all[i]);
                sum += v;
            }
            if (_hover.Count == 0 || sum <= 0) { return; }

            _hover.Sort(CompareHover);

            // 乗っている列を明るくして、どれを見ているか分かるようにする
            float hx = plot.x + period * cw;
            p.Fill(new Rect(hx, plot.y, cw, plot.height), new Color(1f, 1f, 1f, 0.10f));

            DrawHoverBox(plot, d, period, sum, iconSize, p);
        }

        private int CompareHover(SourceSeries a, SourceSeries b)
        {
            return HoverValue(b).CompareTo(HoverValue(a));
        }

        /// <summary>ホバーが読む値。進行中の列だけは台帳（手元にある額）を見る。</summary>
        private long HoverValue(SourceSeries src)
        {
            if (_hoverHeld) { return src.Held; }
            return Value(src, _hoverPeriod, _hoverByRepaid);
        }

        private void DrawHoverBox(Rect plot, ChartData d, int period, long sum, float iconSize, IChartPainter p)
        {
            int rows = Mathf.Min(_hover.Count, HoverRows);
            float line = Mathf.Max(iconSize, p.LineHeight) + 2f;
            float pad = 6f;

            // 幅は中身の実寸から決める
            float valueWidth = 0f;
            float percentWidth = 0f;
            float nameWidth = 0f;
            for (int i = 0; i < rows; i++)
            {
                long v = HoverValue(_hover[i]);
                valueWidth = Mathf.Max(valueWidth, p.Measure(N(v)));
                percentWidth = Mathf.Max(percentWidth, p.Measure(Percent(v, sum)));
                if (!_hover[i].HasSource)
                {
                    nameWidth = Mathf.Max(nameWidth, p.Measure(p.ReasonLabel(_hover[i].Reason)) + 8f);
                }
            }

            // 1行目は「回数 → タグのアイコン → 合計」。文字だけだと読み取りづらいので絵を挟む。
            //
            // 進行中の列は「まだ返していない回」なので、済んだ回数ではなく**次の回**を出す。
            // ここで RepaidTotal をそのまま使うと、8回目を進めているのに 7 と出てしまう。
            int doneAt = (period < d.PeriodRepaid.Count) ? d.PeriodRepaid[period] : d.RepaidTotal + 1;
            int doneBefore = (period > 0 && period - 1 < d.PeriodRepaid.Count) ? d.PeriodRepaid[period - 1] : 0;
            string periodLabel = (doneAt - doneBefore > 1)
                ? (doneBefore + 1).ToString(CultureInfo.InvariantCulture) + "-" + doneAt.ToString(CultureInfo.InvariantCulture)
                : Math.Max(1, doneAt).ToString(CultureInfo.InvariantCulture);
            string sumLabel = N(sum);
            float headerWidth = p.Measure(periodLabel) + 6f + iconSize + 4f + p.Measure(sumLabel);

            float width = Mathf.Max(headerWidth,
                iconSize + 6f + nameWidth + valueWidth + 6f + percentWidth) + pad * 2f;
            float height = pad * 2f + line + rows * line;
            if (_hover.Count > rows) { height += p.LineHeight; }

            // カーソルの右下に出す。はみ出すなら内側へ折り返す
            float x = d.Mouse.x + 14f;
            float y = d.Mouse.y + 10f;
            if (x + width > plot.xMax) { x = d.Mouse.x - 14f - width; }
            if (y + height > plot.yMax) { y = plot.yMax - height; }
            if (x < plot.x) { x = plot.x; }
            if (y < plot.y) { y = plot.y; }

            p.Fill(new Rect(x, y, width, height), new Color(0.04f, 0.04f, 0.05f, 0.94f));
            p.Fill(new Rect(x, y, width, 1f), new Color(1f, 1f, 1f, 0.25f));
            p.Fill(new Rect(x, y + height - 1f, width, 1f), new Color(1f, 1f, 1f, 0.25f));
            p.Fill(new Rect(x, y, 1f, height), new Color(1f, 1f, 1f, 0.25f));
            p.Fill(new Rect(x + width - 1f, y, 1f, height), new Color(1f, 1f, 1f, 0.25f));

            float ty = y + pad;
            float hx = x + pad;
            float textOffset = (iconSize - p.FontSize) * 0.5f;

            p.Text(hx, ty + textOffset, periodLabel, Accent);
            hx += p.Measure(periodLabel) + 6f;
            p.Icon(new Rect(hx, ty, iconSize, iconSize), null, d.Selected.TagID, Ink, IconOutline);
            hx += iconSize + 4f;
            p.Text(hx, ty + textOffset, sumLabel, Ink);

            ty += line;

            for (int i = 0; i < rows; i++)
            {
                long v = HoverValue(_hover[i]);
                Rect iconRect = new Rect(x + pad, ty, iconSize, iconSize);
                DrawSourceIcon(iconRect, _hover[i], d.Selected.TagID, OthersColor, p);

                // 絵で区別できない項目だけ名前を添える
                if (!_hover[i].HasSource)
                {
                    string name = p.ReasonLabel(_hover[i].Reason);
                    if (!string.IsNullOrEmpty(name))
                    {
                        p.Text(x + pad + iconSize + 6f, ty + (iconSize - p.FontSize) * 0.5f, name,
                            new Color(0.78f, 0.75f, 0.70f));
                    }
                }

                string value = N(v);
                p.Text(x + pad + iconSize + 6f + nameWidth + (valueWidth - p.Measure(value)),
                    ty + (iconSize - p.FontSize) * 0.5f, value, Ink);

                string percent = Percent(v, sum);
                p.Text(x + width - pad - p.Measure(percent), ty + (iconSize - p.FontSize) * 0.5f,
                    percent, new Color(0.75f, 0.72f, 0.68f));

                ty += line;
            }

            if (_hover.Count > rows)
            {
                p.Text(x + pad, ty, "+" + (_hover.Count - rows).ToString(CultureInfo.InvariantCulture),
                    new Color(0.65f, 0.62f, 0.58f));
            }
        }

        private static string Percent(long value, long sum)
        {
            if (sum <= 0) { return "0%"; }
            return Mathf.RoundToInt((float)((double)value * 100.0 / sum)).ToString(CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>
        /// 進行中の期の列。いま手元にある額を出所別に積む。
        ///
        /// その期の収入で積むと、ノルマを超えて返済したときの持ち越しが消えるし、
        /// 一気に複数のノルマを達成すると列が空になる。
        /// 台帳（SourceSeries.Held）は収入で増えて支出で目減りするので、
        /// **いま持っている金を何で稼いだか**がそのまま出る。
        /// </summary>
        private void DrawHoldingColumn(float cx, float bw, float baseline, float fullHeight,
            ChartData d, List<SourceSeries> all, IChartPainter p)
        {
            TagSeries s = d.Selected;
            long held = s.HeldTotal();
            if (held <= 0 || d.RequiredCount <= 0)
            {
                DrawZeroColumn(cx, bw, baseline, fullHeight, p);
                return;
            }

            // 台帳は端数の切り捨てで実際の残高から少しずれる。実残高に合わせて伸縮する
            double fit = (double)Math.Max(0L, d.RequiredCurrent) / held;
            float unit = fullHeight / d.RequiredCount;
            float top = baseline - fullHeight;

            float yy = baseline;
            long shown = 0;
            for (int i = 0; i < _rank.Count; i++)
            {
                long v = _rank[i].Held;
                if (v <= 0) { continue; }
                shown += v;

                float h = (float)(v * fit) * unit;
                if (h <= 0f) { continue; }
                yy = Mathf.Max(top, yy - h);
                p.Fill(new Rect(cx + 1f, yy, bw, Mathf.Max(1f, h)), _rankColors[i]);
            }

            long rest = held - shown;
            if (rest > 0)
            {
                float h = (float)(rest * fit) * unit;
                yy = Mathf.Max(top, yy - h);
                p.Fill(new Rect(cx + 1f, yy, bw, Mathf.Max(1f, h)), OthersColor);
            }
        }

        /// <summary>進行中の期の枠。縦線1本だと他の目盛りに紛れるので、列そのものを囲う。</summary>
        private static void DrawCurrentFrame(Rect slot, IChartPainter p)
        {
            Color line = new Color(Accent.r, Accent.g, Accent.b, 0.75f);
            p.Fill(new Rect(slot.x, slot.y, slot.width, 1f), line);            // 100% の位置
            p.Fill(new Rect(slot.x, slot.yMax, slot.width, 1f), line);
            p.Fill(new Rect(slot.x, slot.y, 1f, slot.height), line);
            p.Fill(new Rect(slot.xMax - 1f, slot.y, 1f, slot.height), line);
        }

        /// <summary>
        /// 金額の大きい順。上位だけ描いて、残りは「その他」1本にまとめる。
        /// 全体の MinShare 未満のものも「その他」へ寄せる
        /// （1% の欄が凡例を1行ぶん余計に食うのを避けるため）。
        /// </summary>
        private const float MinShare = 0.03f;

        /// <summary>ホバーで出す行数の上限。溢れたぶんは末尾に件数だけ出す。</summary>
        private const int HoverRows = 10;

        private void RankSources(List<SourceSeries> all, string tagID, int maxEntries)
        {
            _rank.Clear();
            _rankColors.Clear();
            _rankTotals.Clear();

            long grand = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Total > 0) { grand += all[i].Total; }
            }
            if (grand <= 0) { return; }

            long floor = (long)(grand * MinShare);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Total > 0 && all[i].Total >= floor) { _rank.Add(all[i]); }
            }
            _rank.Sort(delegate (SourceSeries a, SourceSeries b) { return b.Total.CompareTo(a.Total); });

            int cap = Mathf.Min(SourcePalette.Length, Mathf.Max(1, maxEntries));
            while (_rank.Count > cap) { _rank.RemoveAt(_rank.Count - 1); }

            for (int i = 0; i < _rank.Count; i++) { _rankTotals.Add(_rank[i].Total); }
            AssignRankColors(tagID);
        }

        /// <summary>
        /// 色はアイコンから取る（出所IDが無いものはタグの色）。
        /// 引けなかったものだけ予備の配色に落ちる。
        /// </summary>
        private void AssignRankColors(string tagID)
        {
            _rankColors.Clear();
            for (int i = 0; i < _rank.Count; i++)
            {
                Color color;
                string key = _rank[i].HasSource ? _rank[i].Key : tagID;
                if (!ChartColors.TryGet(key, out color)) { color = SourcePalette[i % SourcePalette.Length]; }
                _rankColors.Add(Separate(color, _rankColors));
            }
        }

        /// <summary>
        /// 積み上げた段の境目が消えないように、すでに使った色と近すぎるときだけ明度をずらす。
        /// 色相は動かさないので「アイコンの色」からは外れない。
        /// </summary>
        private static Color Separate(Color color, List<Color> used)
        {
            for (int attempt = 1; attempt <= 6; attempt++)
            {
                bool clash = false;
                for (int i = 0; i < used.Count; i++)
                {
                    if (Distance(used[i], color) < 0.20f) { clash = true; break; }
                }
                if (!clash) { return color; }

                float shift = 0.16f * ((attempt + 1) / 2);
                if (attempt % 2 == 0) { shift = -shift; }
                color = ShiftValue(color, shift);
            }
            return color;
        }

        private static float Distance(Color a, Color b)
        {
            float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
            return Mathf.Sqrt(dr * dr + dg * dg + db * db);
        }

        /// <summary>明るさだけ動かす。色相・彩度はそのまま。</summary>
        private static Color ShiftValue(Color c, float delta)
        {
            float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (max <= 0.001f) { return new Color(0.5f, 0.5f, 0.5f, c.a); }

            float wanted = Mathf.Clamp(max + delta, 0.22f, 1f);
            float scale = wanted / max;
            return new Color(Mathf.Clamp01(c.r * scale), Mathf.Clamp01(c.g * scale), Mathf.Clamp01(c.b * scale), c.a);
        }

        private static void DrawSourceIcon(Rect r, SourceSeries src, string tagID, Color fallback, IChartPainter p)
        {
            // 出所IDが無い項目（注文・返済など）も鍵をそのまま渡す。
            // 鍵は "#理由番号" になっていて、筆側が理由ごとの絵に読み替える。
            if (!p.Icon(r, src.Key, tagID, Ink, IconOutline))
            {
                p.Fill(r, fallback);
            }
        }

        // ------------------------------------------------------------------
        // 小物
        // ------------------------------------------------------------------

        internal static int KnownIndex(string tagID)
        {
            for (int i = 0; i < KnownTagIDs.Length; i++)
            {
                if (string.Equals(KnownTagIDs[i], tagID, StringComparison.Ordinal)) { return i; }
            }
            return KnownTagIDs.Length;
        }

        internal static Color TagColor(string tagID)
        {
            Color color;
            if (ChartColors.TryGet(tagID, out color)) { return color; }

            int i = KnownIndex(tagID);
            if (i < 0 || i >= TagColors.Length) { i = TagColors.Length - 1; }
            return TagColors[i];
        }

        private static int ColumnCount(float width, int cap)
        {
            int cols = (int)width;
            if (cap >= 32 && cols > cap) { cols = cap; }
            if (cols < 2) { cols = 2; }
            return cols;
        }

        private static float ValueToY(Rect plot, long min, long max, long value)
        {
            double t = (double)(value - min) / (double)(max - min);
            if (t < 0.0) { t = 0.0; }
            if (t > 1.0) { t = 1.0; }
            return plot.yMax - (float)(t * plot.height);
        }

        private static int TotalBuckets(ChartData d)
        {
            int count = d.BucketCount;
            for (int i = 0; i < d.Ordered.Count; i++)
            {
                if (d.Ordered[i].Balances.Count > count) { count = d.Ordered[i].Balances.Count; }
            }
            return count;
        }

        /// <summary>
        /// 数字の書式。本体の Utility.StringFormatter.NumberFormatter に合わせてあるが、
        /// **小数1桁は常に出す**（本体は "0.#" で 4K、こちらは "0.0" で 4.0K）。
        /// 桁数が揃っていないとグラフの目盛りが縦に不揃いになって読みづらいため。
        ///   1000 未満はそのまま、以降は K / M / B / T
        /// </summary>
        internal static string N(long v)
        {
            bool negative = v < 0L;
            double n = negative ? -(double)v : v;

            string text;
            if (n >= 1000000000000.0) { text = (n / 1000000000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "T"; }
            else if (n >= 1000000000.0) { text = (n / 1000000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "B"; }
            else if (n >= 1000000.0) { text = (n / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M"; }
            else if (n >= 1000.0) { text = (n / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "K"; }
            else { text = n.ToString("0", CultureInfo.InvariantCulture); }

            return negative ? "-" + text : text;
        }

        internal static string F(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        internal static string FormatTime(double seconds)
        {
            if (seconds < 0.0) { seconds = 0.0; }
            int totalSeconds = (int)seconds;
            int h = totalSeconds / 3600;
            int m = (totalSeconds % 3600) / 60;
            int sec = totalSeconds % 60;
            if (h > 0)
            {
                return h.ToString(CultureInfo.InvariantCulture) + ":"
                    + m.ToString("00", CultureInfo.InvariantCulture) + ":"
                    + sec.ToString("00", CultureInfo.InvariantCulture);
            }
            return m.ToString(CultureInfo.InvariantCulture) + ":" + sec.ToString("00", CultureInfo.InvariantCulture);
        }
    }
}
