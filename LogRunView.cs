// 業務日誌の画面の続き（partial）：選んだ Run の詳細。
//
// 並びは本体の結果画面に合わせてある（2026-11-17。縦に長い1枚の表が見づらかったため）:
//   上の段   結果（返済完了／破産！…）・日時・プレイ時間・難易度・スポンサー・返済回数
//   切り替え 統計 / 契約履歴 / 工場を確認 / 課税通知書（結果画面のボタンと同じ4つ）
// 「統計」は本体の統計窓と同じく、左に出所ごとのカード、右にグラフ。上で 収入／支出／資源／アイテム／クラフト を切り替える。
// 本体の統計窓そのものは Run の中のシーンにしか無く、中身も Run 中の生データを読むので持ってこられない。同じ並びで組み直している。
//
// カードの内訳（出所ごとの額）は本体の Run の記録の cashFlows からで、現金のぶんしか無い。
// グラフは RunLog.cs が読み戻した記録を Economy Graph 本体が描く（この画面は枠を空けるだけ）。

using System;
using System.Collections.Generic;
using Items;
using Items.Tag;
using Newtonsoft.Json.Linq;
using BaseSystem;
using Pact;
using Pact.MasterData;
using Utility.LWFApp;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;

namespace LwfEconomyGraph
{
    internal static partial class LogView
    {
        private static RunSummary _run;
        private static JObject _json;

        // 0 = 統計・1 = 契約履歴・2 = 工場を確認・3 = 課税通知書。Run を選び直しても保つ
        private static int _tab;
        // 統計の中の切り替え。0 = 収入・1 = 支出・2 = 資源・3 = アイテム・4 = クラフト
        private static int _statTab;

        private static RectTransform _summaryRow;
        private static RectTransform _statTabRow;
        private static TextMeshProUGUI _resultTitle;

        private const float ContentTop = 160f;   // 切り替えの段の下
        private const float StatTop = 204f;      // 統計の中の切り替えの段の下
        private const float GraphLeft = 988f;
        private const float GraphHeight = 600f;

        private static void Select(Image row, RunSummary run)
        {
            // 押した行の色は、縞の地の色に戻しておく。詳細は全幅で開くので「選んでいる行」の印は要らず、
            // 一覧を隠すと「カーソルが外れた」の知らせも来ないので、載せたときの色のまま一覧へ戻ることになっていた
            RowHover hover = row != null ? row.GetComponentInParent<RowHover>() : null;
            if (row != null) { row.color = hover != null ? hover.Base : ClearColor; }
            _selectedRow = null;

            _run = run;
            _json = null;
            _hasGraph = EconomyGraphPlugin.Instance != null && EconomyGraphPlugin.Instance.LoadRunGraph(run);
            _mapShots = LoadMap(EconomyGraphPlugin.FindRunFile(run, MapSuffix));
            _mapStep = _mapShots.Count - 1;
            _mapPlaying = false;
            ResetMapFor(null);
            try
            {
                _json = RunData.Read(run.Path);
                ResetMapFor(_json);
            }
            catch (Exception e) { EconomyGraphPlugin.Log.LogWarning("[view] cannot read #" + run.Sequence + ": " + e.Message); }

            BuildSummary();
            ShowPage(1);
        }

        /// <summary>上の段。結果と、その Run が何だったかを1行で。</summary>
        private static void BuildSummary()
        {
            _resultTitle.text = ResultText(_run);
            _resultTitle.color = ResultColor(_run);

            Clear(_summaryRow);
            Cell(_summaryRow, "#" + _run.Sequence, Font, DimColor, 0f, false);
            Cell(_summaryRow, _run.StartedLocal.ToString("yyyy-MM-dd HH:mm"), Font, DimColor, 0f, false);
            Gap(_summaryRow, 16f);
            Cell(_summaryRow, Clock(_run.ElapsedSeconds), Font, TextColor, 0f, false);
            Gap(_summaryRow, 16f);
            Cell(_summaryRow, RunName(_run), Font, TextColor, 0f, false);
            Cell(_summaryRow, DifficultyName(_run.Difficulty), Font, DimColor, 0f, false);
            Gap(_summaryRow, 16f);
            for (int i = 0; i < _run.Patrons.Length; i++)
            {
                Icon(_summaryRow, PatronSprite(_run.Patrons[i]), IconSize);
                Cell(_summaryRow, PatronName(_run.Patrons[i]), Font, TextColor, 0f, false);
            }
            Gap(_summaryRow, 16f);
            Cell(_summaryRow, Message("Repayment"), Font, DimColor, 0f, false);
            Cell(_summaryRow, Repayments(_run), Font, TextColor, 0f, false);
            Flex(_summaryRow);
            Cell(_summaryRow, RunData.Str(_json, "writerAppVersion"), SmallFont, DimColor, 0f, false);
        }

        /// <summary>詳細の中身を、選んでいる切り替えに合わせて組み直す。記録の無い切り替えを選んでいたら統計に戻す。</summary>
        private static void ApplyTab()
        {
            bool hasPacts = Objects(_json, "pactHistory").Count > 0;
            bool hasMap = _mapShots.Count > 0;
            bool hasTaxes = Objects(_json, "taxSlots").Count > 0;
            if ((_tab == 1 && !hasPacts) || (_tab == 2 && !hasMap) || (_tab == 3 && !hasTaxes)) { _tab = 0; }

            Clear(_tabRow);
            AddTab(_tabRow, Message("Statistics"), _tab == 0, true, delegate { _tab = 0; ApplyTab(); });
            AddTab(_tabRow, Message("PactHistory"), _tab == 1, hasPacts, delegate { _tab = 1; ApplyTab(); });
            AddTab(_tabRow, Message("CheckFactory"), _tab == 2, hasMap, delegate { _tab = 2; ApplyTab(); });
            AddTab(_tabRow, Message("TaxHeader"), _tab == 3, hasTaxes, delegate { _tab = 3; ApplyTab(); });
            Flex(_tabRow);
            SeedCell(_tabRow, _run, _json, SmallFont);

            bool stats = _tab == 0;
            bool map = _tab == 2;
            _statTabRow.gameObject.SetActive(stats);
            // 統計は左半分が中身・右半分がグラフ。ただしクラフトの表は列が多く半分の幅に収まらないので、
            // グラフを出さずに全幅を使う（グラフは資源の収支で、クラフトの表とは見るものが違う）
            bool graph = stats && _hasGraph && _statTab != 4;
            bool half = stats && _statTab != 4;
            _graphArea.gameObject.SetActive(graph);
            _detailScroll.gameObject.SetActive(!map);
            _mapPane.gameObject.SetActive(map);

            RectTransform frame = _detailScroll.transform as RectTransform;
            frame.offsetMax = new Vector2(half ? -(1800f - GraphLeft + 24f) : -24f, stats ? -StatTop : -ContentTop);

            Clear(_detail);
            try
            {
                if (stats) { BuildStatTab(); }
                else if (_tab == 1) { BuildPactCards(); }
                else if (_tab == 3) { BuildTaxes(); }
                else { _mapPlaying = false; BuildMap(); }
            }
            catch (Exception e) { EconomyGraphPlugin.Log.LogWarning("[view] cannot show #" + _run.Sequence + ": " + e); }
            _detailScroll.verticalNormalizedPosition = 1f;
        }

        /// <summary>マップシード。切り替えの段の右端に出し、押すとクリップボードへ写す
        /// （執務室の難易度選択の画面と同じ。写したら本体の「コピー完了」の語に替える）。
        /// シードは Run の終わりに控えたもの（RunLog.cs の SaveRunSeed）。控えの無い Run では、
        /// 本体の記録にあるカスタムシード（貼って始めた Run だけ）を出す。どちらも無ければ出さない。</summary>
        private static void SeedCell(RectTransform row, RunSummary run, JObject json, int size)
        {
            string seed = SeedOf(run, json);
            if (seed == null) { return; }

            Cell(row, Message("MapSeed"), size, DimColor, 0f, false);
            TextMeshProUGUI code = Cell(row, seed, size, TextColor, 0f, false);
            code.raycastTarget = true;
            Button button = code.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = code;
            string captured = seed;
            button.onClick.AddListener(delegate
            {
                GUIUtility.systemCopyBuffer = captured;
                code.text = Message("MapSeedCopyCompleted");
                code.color = HeadColor;
            });
        }

        /// <summary>その Run のマップシード。控え（無ければ本体の記録のカスタムシード）。どちらも無ければ null。</summary>
        private static string SeedOf(RunSummary run, JObject json)
        {
            string seed = null;
            try
            {
                string file = EconomyGraphPlugin.FindRunFile(run, EconomyGraphPlugin.SeedSuffix);
                if (file != null) { seed = System.IO.File.ReadAllText(file).Trim(); }
            }
            catch (Exception) { seed = null; }
            if (string.IsNullOrEmpty(seed)) { seed = RunData.Str(json, "customSeed"); }
            return string.IsNullOrEmpty(seed) ? null : seed;
        }

        /// <summary>マップシードを写すボタン（コードは出さない）。押すと字が本体の「コピー完了」に替わる。
        /// シードの無い Run では置かない。</summary>
        private static void SeedButton(RectTransform row, RunSummary run, JObject json)
        {
            string seed = SeedOf(run, json);
            if (seed == null) { return; }

            RectTransform cell = Rect("SeedButton", row);
            Image back = cell.gameObject.AddComponent<Image>();
            back.color = new Color(1f, 1f, 1f, 0.14f);
            HorizontalLayoutGroup layout = cell.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(14, 14, 0, 0);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.MiddleCenter;
            LayoutElement le = cell.gameObject.AddComponent<LayoutElement>();
            le.minHeight = 32f;
            le.preferredHeight = 32f;
            TextMeshProUGUI label = Cell(cell, Message("MapSeed") + " " + StripRichText(Message("Copy")), SmallFont, TextColor, 0f, false);
            Button button = cell.gameObject.AddComponent<Button>();
            button.targetGraphic = back;
            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(1.8f, 1.8f, 1.8f, 1f);
            button.colors = colors;
            button.onClick.AddListener(delegate
            {
                GUIUtility.systemCopyBuffer = seed;
                label.text = Message("MapSeedCopyCompleted");
                label.color = HeadColor;
            });
        }

        private static void AddTab(RectTransform row, string title, bool active, bool usable, UnityEngine.Events.UnityAction onClick)
        {
            // 押せる札に見えるよう、1つずつ地を敷く。選んでいる札は明るい地に下線、ほかは暗い地。
            // （最初は字を並べただけで、切り替えだと分からなかった。2026-11-17）
            Color color = active ? TextColor : DimColor;
            if (!usable) { color.a = 0.35f; }

            RectTransform tab = Rect("Tab", row);
            LayoutElement le = tab.gameObject.AddComponent<LayoutElement>();
            le.minHeight = 38f;
            le.preferredHeight = 38f;
            le.flexibleWidth = 0f;
            Image back = tab.gameObject.AddComponent<Image>();
            Color ground = HeadColor;
            ground.a = active ? 0.30f : (usable ? 0.10f : 0.04f);
            back.color = ground;
            HorizontalLayoutGroup layout = tab.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(18, 18, 0, 0);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.MiddleCenter;
            Cell(tab, StripRichText(title), 24, color, 0f, false);

            if (active)
            {
                RectTransform line = Rect("Line", tab);
                line.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                line.anchorMin = new Vector2(0f, 0f);
                line.anchorMax = new Vector2(1f, 0f);
                line.pivot = new Vector2(0.5f, 0f);
                line.offsetMin = new Vector2(0f, 0f);
                line.offsetMax = new Vector2(0f, 3f);
                Image lineImage = line.gameObject.AddComponent<Image>();
                lineImage.color = HeadColor;
                lineImage.raycastTarget = false;
            }
            if (!usable) { return; }
            Button button = tab.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = back;
            button.onClick.AddListener(onClick);
        }

        // ---------------------------------------------------------------
        // 統計
        // ---------------------------------------------------------------

        private static void BuildStatTab()
        {
            string[] keys = new string[] { "Income", "Expense", "Resources", "StatsColumnItem", "StatsTabCraft" };
            Clear(_statTabRow);
            for (int i = 0; i < keys.Length; i++)
            {
                int captured = i;
                AddTab(_statTabRow, Message(keys[i]), _statTab == i, true, delegate { _statTab = captured; ApplyTab(); });
            }
            // その札に効く切り替えを右端に。収入・支出は数の書き方（K・M に丸める／全部の桁）、
            // クラフトは時間の出し方（1回の秒数／毎秒の数）
            Flex(_statTabRow);
            string perSecond = "/" + Safe(delegate { return Utility.Localization.LocalizedTextGetter.GetPactData("sec"); }, "s");
            if (_statTab <= 1) { Switch(_statTabRow, "1.2K", "1,234", EconomyGraphPlugin.ShowFullNumbers); }
            else if (_statTab == 4) { Switch(_statTabRow, perSecond.Substring(1), perSecond, EconomyGraphPlugin.ShowCraftRate); }
            // 右のグラフも収入／支出を合わせる
            if (EconomyGraphPlugin.Instance != null) { EconomyGraphPlugin.Instance.SetLogSide(_statTab == 1); }

            JObject stats = RunData.Obj(_json, "statsSnapshot");
            if (stats == null) { NoData(); return; }
            if (_statTab == 0) { BuildFlowCards(stats, "Income"); }
            else if (_statTab == 1) { BuildFlowCards(stats, "Expense"); }
            else if (_statTab == 2) { BuildResourceTable(stats); }
            else if (_statTab == 3) { BuildItemTable(stats); }
            else { BuildCraftTable(stats); }
        }

        /// <summary>2択の切り替え。枠で囲み、選んでいる側を塗る。左が切（false）・右が入（true）。
        /// 札（タブ）と同じ形にすると切り替えの札の続きに見えるので、枠つき・小さめの別の形にしてある。
        /// 2択の中身は書き方の見本（本体の言語表に当たる語が無い）。</summary>
        private static void Switch(RectTransform row, string off, string on, BepInEx.Configuration.ConfigEntry<bool> entry)
        {
            RectTransform frame = Rect("Switch", row);
            LayoutElement le = frame.gameObject.AddComponent<LayoutElement>();
            le.minHeight = 32f;
            le.preferredHeight = 32f;
            le.flexibleWidth = 0f;
            Image border = frame.gameObject.AddComponent<Image>();
            border.color = HeadColor;
            border.raycastTarget = false;
            HorizontalLayoutGroup layout = frame.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(2, 2, 2, 2);
            layout.spacing = 2f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            SwitchChoice(frame, off, !entry.Value, false, entry);
            SwitchChoice(frame, on, entry.Value, true, entry);
        }

        private static void SwitchChoice(RectTransform frame, string sample, bool selected, bool value,
            BepInEx.Configuration.ConfigEntry<bool> entry)
        {
            RectTransform cell = Rect("Choice", frame);
            Image back = cell.gameObject.AddComponent<Image>();
            back.color = selected ? HeadColor : LWFColors.BLACK_COLOR;
            HorizontalLayoutGroup layout = cell.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 0, 0);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.MiddleCenter;
            Cell(cell, sample, 18, selected ? LWFColors.BLACK_COLOR : DimColor, 0f, false);
            if (selected) { return; }
            Button button = cell.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = back;
            bool captured = value;
            BepInEx.Configuration.ConfigEntry<bool> capturedEntry = entry;
            button.onClick.AddListener(delegate
            {
                capturedEntry.Value = captured;
                ApplyTab();
            });
        }

        private static void NoData()
        {
            Cell(Row(_detail, RowHeight), Message("StatsNoData"), Font, DimColor, 0f, false);
        }

        /// <summary>現金の出所ごとのカード。本体の統計窓と同じく、理由（売却・レリック／バフ…）で段を分ける。</summary>
        private static void BuildFlowCards(JObject stats, string side)
        {
            List<string> titles = new List<string>();
            Dictionary<string, List<JObject>> groups = new Dictionary<string, List<JObject>>();
            foreach (JObject flow in Objects(stats, "cashFlows"))
            {
                string kind = RunData.Str(flow, "kind");
                if (kind == null || !kind.StartsWith(side, StringComparison.Ordinal)) { continue; }
                string title = FlowTitle(kind);
                List<JObject> group;
                if (!groups.TryGetValue(title, out group))
                {
                    group = new List<JObject>();
                    groups[title] = group;
                    titles.Add(title);
                }
                group.Add(flow);
            }
            if (titles.Count == 0) { NoData(); return; }

            const int perRow = 4;
            for (int g = 0; g < titles.Count; g++)
            {
                List<JObject> group = groups[titles[g]];
                group.Sort(delegate(JObject a, JObject b)
                {
                    return RunData.Long(b, "totalAmount").CompareTo(RunData.Long(a, "totalAmount"));
                });
                Cell(Row(_detail, 36), titles[g], Font, HeadColor, 0f, false);
                RectTransform row = null;
                for (int i = 0; i < group.Count; i++)
                {
                    if (i % perRow == 0) { row = Row(_detail, 68); }
                    string source = RunData.Str(group[i], "sourceId");
                    string kind = RunData.Str(group[i], "kind");
                    // 出所の無い理由のうち、注文は電話の絵（統計窓のグラフと同じ当て方）
                    if (source == null && kind.IndexOf("Order", StringComparison.Ordinal) >= 0) { source = "FasterOrder"; }
                    Card(row, AnySprite(source), Short(RunData.Long(group[i], "totalAmount")));
                }
                Gap(Row(_detail, 8), 1f);
            }
        }

        /// <summary>現金の流れの種類（本体の記録の kind）を、本体の統計窓の段の名前にする。</summary>
        private static string FlowTitle(string kind)
        {
            if (kind.IndexOf("Sale", StringComparison.Ordinal) >= 0) { return Message("StatsColumnSale"); }
            if (kind.IndexOf("Debuff", StringComparison.Ordinal) >= 0) { return Message("DebuffRelic"); }
            if (kind.IndexOf("BuffRelic", StringComparison.Ordinal) >= 0) { return Message("Relic") + " / " + Message("Buff"); }
            if (kind.IndexOf("Order", StringComparison.Ordinal) >= 0) { return Message("Order"); }
            if (kind.IndexOf("Repayment", StringComparison.Ordinal) >= 0) { return Message("Repayment"); }
            if (kind.IndexOf("Pact", StringComparison.Ordinal) >= 0) { return Message("Pact"); }
            if (kind.IndexOf("Tax", StringComparison.Ordinal) >= 0) { return Message("Tax"); }
            if (kind.IndexOf("Craft", StringComparison.Ordinal) >= 0) { return Message("StatsTabCraft"); }
            return Message("StatsReasonOther");
        }

        private static void Card(RectTransform row, Sprite icon, string amount)
        {
            RectTransform card = Rect("Card", row);
            LayoutElement le = card.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 214f;
            le.preferredWidth = 214f;
            le.flexibleWidth = 0f;   // 段が埋まらないときに引き伸ばされないように（契約のカードと同じ）
            le.minHeight = 60f;
            le.preferredHeight = 60f;
            Image back = card.gameObject.AddComponent<Image>();
            back.color = new Color(1f, 1f, 1f, 0.08f);
            back.raycastTarget = false;
            HorizontalLayoutGroup layout = card.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(10, 12, 0, 0);
            layout.spacing = 6f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.MiddleLeft;
            Icon(card, icon, 44);
            Flex(card);
            Icon(card, AnySprite("Cash"), 24);
            Cell(card, amount, Font, TextColor, 0f, true);
        }

        /// <summary>カードの数。大きい数は K・M に丸める。全部の桁で出す設定のときは丸めない。</summary>
        private static string Short(long value)
        {
            long abs = value < 0 ? -value : value;
            string sign = value < 0 ? "-" : "";
            if (EconomyGraphPlugin.ShowFullNumbers.Value || abs < 10000) { return value.ToString("N0"); }
            if (abs < 1000000) { return sign + (abs / 1000f).ToString("0.#") + "K"; }
            return sign + (abs / 1000000f).ToString("0.##") + "M";
        }

        /// <summary>幅の決まった欄に入れる数。表示の切り替え（全桁）に関係なく、1万から K・M に縮める。</summary>
        private static string Compact(long value)
        {
            if (value < 10000) { return value.ToString("N0"); }
            if (value < 1000000) { return (value / 1000f).ToString("0.#") + "K"; }
            return (value / 1000000f).ToString("0.##") + "M";
        }

        private const float NumberWidth = 130f;

        private static void BuildResourceTable(JObject stats)
        {
            List<JObject> resources = Objects(stats, "resources");
            if (resources.Count == 0) { NoData(); return; }
            RectTransform head = Row(_detail, RowHeight);
            Gap(head, IconSize);
            Cell(head, Message("Resources"), SmallFont, DimColor, -1f, false);
            Cell(head, Message("StatsColumnAdd"), SmallFont, DimColor, NumberWidth, true);
            Cell(head, Message("StatsColumnConsume"), SmallFont, DimColor, NumberWidth, true);
            Cell(head, Message("StatsColumnStock"), SmallFont, DimColor, NumberWidth, true);
            for (int i = 0; i < resources.Count; i++)
            {
                JObject r = resources[i];
                string id = RunData.Str(r, "tagId");
                RectTransform row = Row(_detail, RowHeight);
                Icon(row, AnySprite(id), IconSize);
                Cell(row, AnyName(id), SmallFont, TextColor, -1f, false);
                Cell(row, Count(r, "addedTotal"), SmallFont, TextColor, NumberWidth, true);
                Cell(row, Count(r, "consumedTotal"), SmallFont, TextColor, NumberWidth, true);
                Cell(row, Count(r, "stockAtEnd"), SmallFont, TextColor, NumberWidth, true);
            }
        }

        private static void BuildItemTable(JObject stats)
        {
            List<JObject> items = Objects(stats, "items");
            if (items.Count == 0) { NoData(); return; }
            RectTransform head = Row(_detail, RowHeight);
            Gap(head, IconSize);
            Cell(head, Message("StatsColumnItem"), SmallFont, DimColor, -1f, false);
            Cell(head, Message("StatsColumnProduction"), SmallFont, DimColor, NumberWidth, true);
            Cell(head, Message("Deliver"), SmallFont, DimColor, NumberWidth, true);
            Cell(head, Message("StatsColumnConsume"), SmallFont, DimColor, NumberWidth, true);
            for (int i = 0; i < items.Count; i++)
            {
                JObject it = items[i];
                string id = RunData.Str(it, "itemId");
                RectTransform row = Row(_detail, RowHeight);
                Icon(row, AnySprite(id), IconSize);
                Cell(row, AnyName(id), SmallFont, TextColor, -1f, false);
                Cell(row, Count(it, "producedTotal"), SmallFont, TextColor, NumberWidth, true);
                Cell(row, Count(it, "deliveredTotal"), SmallFont, TextColor, NumberWidth, true);
                Cell(row, Count(it, "consumedTotal"), SmallFont, TextColor, NumberWidth, true);
            }
        }

        /// <summary>クラフト。Run の終わりのレシピの控え（RecipeLog.cs）があれば、使い魔の種類ごとに
        /// 終了時に回していたレシピを「何体が回していたか・材料 = 成果物（秒）」で並べる。数は契約や返済特典で変わった後のもの。
        /// 控えの無い Run では、本体の記録にある「どのレシピを何体で回していたか」だけの表になる。</summary>
        private static void BuildCraftTable(JObject stats)
        {
            List<JObject> crafts = Objects(stats, "craftConfiguration");
            List<JObject> recipes = LoadPactCards(EconomyGraphPlugin.FindRunFile(_run, EconomyGraphPlugin.RecipeSuffix));
            if (recipes.Count > 0) { BuildRecipeList(recipes, crafts, stats); return; }
            if (crafts.Count == 0) { NoData(); return; }
            RectTransform head = Row(_detail, RowHeight);
            Gap(head, IconSize);
            Flex(head);
            Cell(head, Message("StatsColumnPlacedCount"), SmallFont, DimColor, NumberWidth, true);
            for (int i = 0; i < crafts.Count; i++)
            {
                JObject c = crafts[i];
                // レシピを持たない行は、未設定のまま置かれている数
                string recipe = RunData.Str(c, "recipeId");
                long count = RunData.Long(c, recipe != null ? "activeCrafterCount" : "unassignedCrafterCount");
                RectTransform row = Row(_detail, RowHeight);
                Icon(row, RecipeSprite(recipe), IconSize);
                Cell(row, recipe != null ? RecipeName(recipe) : "-", SmallFont, TextColor, 300f, false);
                Cell(row, CrafterName(RunData.Str(c, "crafterKind")), SmallFont, DimColor, -1f, false);
                Cell(row, count.ToString("N0"), SmallFont, TextColor, NumberWidth, true);
            }
        }

        // クラフトの表の列幅。見出しと行で同じ値を使い、列をそろえる
        private const float CraftCountWidth = 90f;    // 配置数
        private const float CraftItemWidth = 250f;    // 製作アイテム（絵・名前）
        private const float CraftYieldWidth = 96f;    // 1回に出来る数（x10,000 まで入る幅）
        private const float CraftIngredientWidth = 96f;   // 材料1つの数（x10,000 まで入る幅。62 では x1,000 が「…」になった）
        private const float CraftRecipeWidth = 430f;  // レシピ（材料は多くても3つ。絵 30 + 数 96 が3組と、その間）
        private const float CraftTimeWidth = 90f;     // 製造秒数（または毎秒の数）
        private const float CraftTotalWidth = 120f;   // 総製造数

        /// <summary>クラフト（控えのある Run）。使い魔の種類ごとに、顔つきの見出しと表を出す。
        /// 列は 配置数・製作アイテム・レシピ（材料）・製造秒数・総製造数。列幅は決め打ちで、行ごとにずれない。
        /// 製造秒数は、右上の切り替えで「毎秒の数」（1回に出来る数 ÷ 秒数。使い魔1体・作業速度の補正なし）にできる。
        /// 総製造数は、控えにあるレシピごとの製作数（クラフトで出来た分だけ。本体の統計の「生産」は
        /// 採掘ポイントや金庫から出た分も含むので使わない）。</summary>
        private static void BuildRecipeList(List<JObject> recipes, List<JObject> crafts, JObject stats)
        {
            // 何体が回していたか（本体の記録）。鍵は「使い魔の種類/レシピ」
            Dictionary<string, long> working = new Dictionary<string, long>();
            foreach (JObject c in crafts)
            {
                string recipe = RunData.Str(c, "recipeId");
                if (recipe == null) { continue; }
                working[RunData.Str(c, "crafterKind") + "/" + recipe] = RunData.Long(c, "activeCrafterCount");
            }
            bool rate = EconomyGraphPlugin.ShowCraftRate.Value;

            List<string> kinds = new List<string>();
            foreach (JObject r in recipes)
            {
                string kind = RunData.Str(r, "kind");
                if (kind != null && !kinds.Contains(kind)) { kinds.Add(kind); }
            }
            string seconds = Safe(delegate { return Utility.Localization.LocalizedTextGetter.GetPactData("sec"); }, "s");
            bool any = false;
            for (int k = 0; k < kinds.Count; k++)
            {
                string kind = kinds[k];
                // 出すのは、終了時に使い魔が回していたレシピだけ（控えには解禁済みの全部が入っているが、
                // 使っていないものまで並べると長い）。配置数の多い順
                List<JObject> mine = recipes.FindAll(delegate(JObject r)
                {
                    long used;
                    return RunData.Str(r, "kind") == kind
                        && working.TryGetValue(kind + "/" + RunData.Str(r, "id"), out used) && used > 0;
                });
                if (mine.Count == 0) { continue; }
                mine.Sort(delegate(JObject a, JObject b)
                {
                    return working[kind + "/" + RunData.Str(b, "id")].CompareTo(working[kind + "/" + RunData.Str(a, "id")]);
                });

                // 見出し：使い魔の顔と名前
                if (any) { Gap(Row(_detail, 14), 1f); }
                any = true;
                RectTransform title = Row(_detail, 44);
                Icon(title, CrafterHead(kind) ?? CrafterSprite(kind), 40);
                Cell(title, CrafterName(kind), 24, HeadColor, 0f, false);

                RectTransform head = Row(_detail, 32);
                Cell(head, Message("StatsColumnPlacedCount"), 18, DimColor, CraftCountWidth, true);
                Gap(head, 16f);
                Cell(head, Message("StatsColumnItem"), 18, DimColor, IconSize + 8f + CraftItemWidth + 8f + CraftYieldWidth, false);
                Cell(head, Message("FamRecipeCurrent"), 18, DimColor, CraftRecipeWidth, false);
                Cell(head, rate ? "/" + seconds : seconds, 18, DimColor, CraftTimeWidth, true);
                Cell(head, Message("StatsColumnProduction"), 18, DimColor, CraftTotalWidth, true);

                for (int i = 0; i < mine.Count; i++)
                {
                    JObject r = mine[i];
                    JObject result = RunData.Obj(r, "result");
                    string resultID = RunData.Str(result, "id");

                    RectTransform row = Row(_detail, RowHeight);
                    Cell(row, working[kind + "/" + RunData.Str(r, "id")].ToString("N0"), Font, TextColor, CraftCountWidth, true);
                    Gap(row, 16f);
                    Icon(row, AnySprite(resultID), IconSize);
                    Cell(row, AnyName(resultID), SmallFont, TextColor, CraftItemWidth, false);
                    Cell(row, "x" + Compact(RunData.Long(result, "count")), SmallFont, TextColor, CraftYieldWidth, false);

                    // 材料は1つの欄にまとめ、欄の幅を決め打ちにする（中身の数で後ろの列がずれない）
                    RectTransform ingredients = Row(row, RowHeight);
                    HorizontalLayoutGroup inner = ingredients.GetComponent<HorizontalLayoutGroup>();
                    inner.padding = new RectOffset(0, 0, 0, 0);
                    LayoutElement fill = ingredients.GetComponent<LayoutElement>();
                    fill.minWidth = CraftRecipeWidth;
                    fill.preferredWidth = CraftRecipeWidth;
                    fill.flexibleWidth = 0f;
                    foreach (JObject ingredient in Objects(r, "ingredients"))
                    {
                        string id = RunData.Str(ingredient, "id");
                        if (id == null) { continue; }
                        Icon(ingredients, AnySprite(id), IconSize);
                        Cell(ingredients, "x" + Compact(RunData.Long(ingredient, "count")), SmallFont, TextColor, CraftIngredientWidth, false);
                    }

                    double time = RunData.Num(r, "time");
                    string timeText = rate
                        ? (time > 0.0 ? (RunData.Long(result, "count") / time).ToString("0.##") : "-")
                        : time.ToString("0.##");
                    Cell(row, timeText, SmallFont, TextColor, CraftTimeWidth, true);
                    Cell(row, RunData.Has(r, "made") ? RunData.Long(r, "made").ToString("N0") : "-",
                        SmallFont, TextColor, CraftTotalWidth, true);
                }
            }
            if (!any) { NoData(); }
        }

        /// <summary>使い魔の種類（Crafter・Smelter…）の顔アイコン。</summary>
        private static Sprite CrafterSprite(string kind)
        {
            try
            {
                Items.Craft.CrafterKind parsed = (Items.Craft.CrafterKind)Enum.Parse(typeof(Items.Craft.CrafterKind), kind);
                return Fams.FamTypes.GetFaceIcon(Fams.FamTypes.GetFamTypeFromCrafterKind(parsed));
            }
            catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------
        // 契約履歴
        // ---------------------------------------------------------------

        /// <summary>取得した契約を、取った順にカードで並べる。カードは本体の契約のカードに寄せてある:
        /// 地の色はレア度、左上の角にスポンサーの顔、上から 名前・絵・効果の文。一番下に、どこで手に入れたかと時刻。
        /// 名前・レア度・スポンサーは本体の図鑑と同じ口（PactDisplayService）から引く。
        /// 効果の文は、取った瞬間のカードの控え（PactLog.cs）がある Run でだけ出す。図鑑の口で作る文は、
        /// 取った時点の状況で決まる数字の所が「-」になる（歯抜け）ので使わない。
        /// 効果の文を出すかどうかは、左上のボタン（文の絵）で切り替える。</summary>
        private static void BuildPactCards()
        {
            // 手に入れた経路（取得順 → 経路）。古い版の記録には無い
            Dictionary<long, JObject> contexts = new Dictionary<long, JObject>();
            foreach (JObject c in Objects(_json, "pactAcquisitionContexts")) { contexts[RunData.Long(c, "acquisitionOrder")] = c; }

            // 取った瞬間のカードの控え。同じ契約を何度も取ることがあるので、使った控えには印を付けて順に当てる
            string keptPath = EconomyGraphPlugin.FindRunFile(_run, EconomyGraphPlugin.PactSuffix);
            List<JObject> kept = LoadPactCards(keptPath);
            // 古い控えには、文を別の言語で出し直すための図鑑用の文が無い。いまの言語が控えたときと同じなら、足して書き直す
            // （LogPactText.cs）。次に別の言語で開いたとき、その言語で文が出る
            try
            {
                if (keptPath != null && FillCatalog(kept))
                {
                    JArray all = new JArray();
                    for (int i = 0; i < kept.Count; i++) { all.Add(kept[i]); }
                    System.IO.File.WriteAllText(keptPath, all.ToString(Newtonsoft.Json.Formatting.None), new System.Text.UTF8Encoding(false));
                    EconomyGraphPlugin.Log.LogInfo("[view] added the catalog text to " + System.IO.Path.GetFileName(keptPath));
                }
            }
            catch (Exception e) { EconomyGraphPlugin.Log.LogWarning("[view] cannot add the catalog text: " + e.Message); }
            bool[] used = new bool[kept.Count];
            bool effects = EconomyGraphPlugin.ShowPactEffects.Value && kept.Count > 0;

            RectTransform tools = Row(_detail, 38);
            EffectButton(tools, kept.Count > 0);

            List<JObject> pacts = Objects(_json, "pactHistory");
            JObject[] cards = new JObject[pacts.Count];
            for (int i = 0; i < pacts.Count; i++)
            {
                string wanted = RunData.Str(pacts[i], "pactId");
                for (int k = 0; k < kept.Count; k++)
                {
                    if (used[k] || RunData.Str(kept[k], "id") != wanted) { continue; }
                    used[k] = true;
                    cards[i] = kept[k];
                    break;
                }
            }

            // 効果の文の板の高さは、その Run で取った契約のうち一番長い文に合わせる（カードの高さを揃えるため。
            // レシピを追加する契約は材料の数だけ行が増え、決め打ちの4行では収まらない）
            float plateHeight = 0f;
            if (effects)
            {
                TextMeshProUGUI ruler = NewText(tools, "", 15, TextColor);
                ruler.richText = true;
                ruler.textWrappingMode = TextWrappingModes.Normal;
                ruler.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                for (int i = 0; i < cards.Length; i++)
                {
                    string text = cards[i] != null ? CardDescription(cards[i]) : null;
                    if (string.IsNullOrEmpty(text)) { continue; }
                    plateHeight = Mathf.Max(plateHeight, ruler.GetPreferredValues(text, PlateTextWidth, 0f).y);
                }
                UnityEngine.Object.Destroy(ruler.gameObject);
                plateHeight = Mathf.Ceil(plateHeight) + 12f;   // 上下の余白
            }

            const int perRow = 5;
            float cardHeight = effects ? 252f + plateHeight : 248f;
            RectTransform row = null;
            for (int i = 0; i < pacts.Count; i++)
            {
                if (i % perRow == 0) { row = Row(_detail, (int)cardHeight + 8); }
                JObject p = pacts[i];
                string id = RunData.Str(p, "pactId");
                JObject card = cards[i];

                string name = PactName(id);
                string description = "";
                Color ground = new Color(0.49f, 0.49f, 0.49f, 1f);
                Sprite patron = null;
                try
                {
                    PactDisplaySnapshot snapshot;
                    if (id != null && PactDisplayService.TryCreateCatalogSnapshot(id, out snapshot) && snapshot != null)
                    {
                        if (!string.IsNullOrEmpty(snapshot.NameText)) { name = StripRichText(snapshot.NameText); }
                        ground = PactDisplayService.GetBackgroundColor(snapshot.Rarity);
                        ground.a = 1f;
                        patron = Patrons.GetFaceIcon(snapshot.Patron);
                    }
                }
                catch (Exception) { }
                if (card != null) { description = CardDescription(card); }

                RectTransform cardRect = Rect("Pact", row);
                LayoutElement le = cardRect.gameObject.AddComponent<LayoutElement>();
                le.minWidth = 336f;
                le.preferredWidth = 336f;
                // 中に「余りを全部もらう」部品があると、カード自身も余りを欲しがる。段が埋まらない最後の行で
                // カードが横に引き伸ばされるので、欲しがらないと明示する
                le.flexibleWidth = 0f;
                le.minHeight = cardHeight;
                le.preferredHeight = cardHeight;
                // 地の色は本体のカードと同じ作り方。本体はレア度の色を「カード全面」に塗るのではなく、
                // 枠の絵（Sprite "frame"：縁が銅色・中が暗い茶の半透明）に掛け合わせている。
                // 同じ掛け算を自前でやる（枠の絵そのものは使わない）。縁 = 色 × (140,101,57)、中 = 色 × (35,29,22)
                MapMesh back = cardRect.gameObject.AddComponent<MapMesh>();
                back.raycastTarget = false;
                back.Quad(0f, 0f, 336f, cardHeight, Tint(ground, 140, 101, 57));
                back.Quad(3f, 3f, 330f, cardHeight - 6f, Tint(ground, 35, 29, 22));
                VerticalLayoutGroup layout = cardRect.gameObject.AddComponent<VerticalLayoutGroup>();
                layout.padding = new RectOffset(12, 12, 8, 8);
                layout.spacing = 4f;
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandWidth = false;
                layout.childForceExpandHeight = false;
                layout.childAlignment = TextAnchor.UpperCenter;

                // 名前は長いと収まらない（英語で顕著）。2行まで折り返し、それでも入らなければ字を小さくする。
                // 左右は、左上の角のスポンサーの顔に掛からないよう同じだけ空ける（中央ぞろえを保つ）
                TextMeshProUGUI title = CardText(cardRect, name, 21, TextColor, 46f);
                title.fontStyle = FontStyles.Bold;
                title.alignment = TextAlignmentOptions.Center;
                title.textWrappingMode = TextWrappingModes.Normal;
                title.margin = new Vector4(52f, 0f, 52f, 0f);
                title.enableAutoSizing = true;
                title.fontSizeMax = 21f;
                title.fontSizeMin = 11f;
                ArtFrame(cardRect, PactSprite(_run, id));

                // 誰との契約かは、左上の角にスポンサーの顔で。レア度は地の色で分かるので字にしない
                if (patron != null)
                {
                    RectTransform face = Rect("Patron", cardRect);
                    face.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                    face.anchorMin = new Vector2(0f, 1f);
                    face.anchorMax = new Vector2(0f, 1f);
                    face.pivot = new Vector2(0f, 1f);
                    face.anchoredPosition = new Vector2(6f, -6f);
                    face.sizeDelta = new Vector2(54f, 54f);
                    Image faceImage = face.gameObject.AddComponent<Image>();
                    faceImage.sprite = patron;
                    faceImage.preserveAspect = true;
                    faceImage.raycastTarget = false;
                }

                if (effects)
                {
                    // 効果の文は、本体のカードと同じく暗い板の上に置く
                    RectTransform plate = Rect("Text", cardRect);
                    LayoutElement plateSize = plate.gameObject.AddComponent<LayoutElement>();
                    plateSize.preferredWidth = 312f;
                    plateSize.minHeight = plateHeight;
                    plateSize.preferredHeight = plateHeight;
                    Image plateBack = plate.gameObject.AddComponent<Image>();
                    plateBack.color = LWFColors.BLACK_COLOR;
                    plateBack.raycastTarget = false;
                    TextMeshProUGUI body = NewText(plate, description, 15, TextColor);
                    Stretch(body.rectTransform);
                    body.rectTransform.offsetMin = new Vector2(8f, 6f);
                    body.rectTransform.offsetMax = new Vector2(-8f, -6f);
                    body.overflowMode = TextOverflowModes.Overflow;
                    body.richText = true;
                    body.alignment = TextAlignmentOptions.TopLeft;
                    body.textWrappingMode = TextWrappingModes.Normal;
                }

                // どこで手に入れたか（依頼看板か、返済の報酬の契約か）と時刻
                RectTransform foot = Row(cardRect, 26);
                foot.GetComponent<LayoutElement>().preferredWidth = 312f;
                JObject context;
                contexts.TryGetValue(RunData.Long(p, "acquisitionOrder"), out context);
                Cell(foot, PactSource(p, context), 15, HeadColor, -1f, false);
                Cell(foot, Time(p, "acquiredAtElapsedGameplaySeconds"), 15, HeadColor, 60f, true);
            }
        }

        /// <summary>効果の文の幅（板の幅 312 から左右の余白 8 ずつを引いたもの）</summary>
        private const float PlateTextWidth = 296f;
        private const float ArtWidth = 312f;
        private const float ArtHeight = 142f;

        /// <summary>契約の絵の枠。どのカードでも同じ大きさの暗い板を置き、絵はその中に収める（本体のカードと同じ）。
        /// 絵は板に収まるまで縮めるが、元の大きさより大きくはしない（小さいアイコンを引き伸ばすと、
        /// 大きい絵と並んだときに粗さが目立つ）。</summary>
        private static void ArtFrame(RectTransform card, Sprite sprite)
        {
            RectTransform frame = Rect("Art", card);
            LayoutElement le = frame.gameObject.AddComponent<LayoutElement>();
            le.minWidth = ArtWidth;
            le.preferredWidth = ArtWidth;
            le.minHeight = ArtHeight;
            le.preferredHeight = ArtHeight;
            Image back = frame.gameObject.AddComponent<Image>();
            back.color = LWFColors.BLACK_COLOR;
            back.raycastTarget = false;
            if (sprite == null) { return; }

            Vector2 native = sprite.rect.size;
            float fit = Mathf.Min((ArtWidth - 8f) / native.x, (ArtHeight - 8f) / native.y);
            float scale = Mathf.Min(1f, fit);
            RectTransform art = Rect("Sprite", frame);
            art.anchorMin = new Vector2(0.5f, 0.5f);
            art.anchorMax = new Vector2(0.5f, 0.5f);
            art.sizeDelta = native * scale;
            Image image = art.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
        }

        /// <summary>アイテム・資源の大きい版の絵（本体が契約の絵に使うもの。アドレスは「ID_large」）。無ければ null。
        /// 在るかを先に確かめる（無い鍵を読ませると Addressables がエラーをログに出す）。</summary>
        private static Sprite LargeSprite(string address)
        {
            try
            {
                if (Addressables.LoadResourceLocationsAsync(address, typeof(Sprite)).WaitForCompletion().Count == 0) { return null; }
                return Addressables.LoadAssetAsync<Sprite>(address).WaitForCompletion();
            }
            catch (Exception) { return null; }
        }

        /// <summary>色どうしの掛け算（本体の枠の絵にレア度の色を掛けるのと同じ）。</summary>
        private static Color32 Tint(Color color, int r, int g, int b)
        {
            return new Color32((byte)(color.r * r), (byte)(color.g * g), (byte)(color.b * b), 255);
        }

        private static List<JObject> LoadPactCards(string path)
        {
            List<JObject> cards = new List<JObject>();
            if (path == null) { return cards; }
            try
            {
                foreach (JToken t in JArray.Parse(System.IO.File.ReadAllText(path)))
                {
                    JObject o = t as JObject;
                    if (o != null) { cards.Add(o); }
                }
            }
            catch (Exception e)
            {
                EconomyGraphPlugin.Log.LogWarning("[view] cannot read " + System.IO.Path.GetFileName(path) + ": " + e.Message);
                cards.Clear();
            }
            return cards;
        }

        /// <summary>効果の文を出すかどうかのボタン（文の絵）。本体の言語表に当たる語が無いので絵にしている。
        /// 控えの無い Run では出す文が無いので、薄くして押せなくする。</summary>
        private static void EffectButton(RectTransform row, bool usable)
        {
            bool on = usable && EconomyGraphPlugin.ShowPactEffects.Value;
            RectTransform r = Rect("Effects", row);
            LayoutElement le = r.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 30f;
            le.preferredWidth = 30f;
            le.minHeight = 30f;
            le.preferredHeight = 30f;
            MapMesh mesh = r.gameObject.AddComponent<MapMesh>();
            Color32 color = on ? (Color32)TextColor : new Color32(255, 255, 255, usable ? (byte)90 : (byte)36);
            mesh.Quad(0f, 0f, 30f, 30f, new Color32(255, 255, 255, on ? (byte)40 : (byte)12));
            mesh.Quad(5f, 7f, 20f, 3f, color);
            mesh.Quad(5f, 14f, 20f, 3f, color);
            mesh.Quad(5f, 21f, 13f, 3f, color);
            if (!usable) { return; }
            Button button = r.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = mesh;
            button.onClick.AddListener(delegate
            {
                EconomyGraphPlugin.ShowPactEffects.Value = !EconomyGraphPlugin.ShowPactEffects.Value;
                ApplyTab();
            });
        }

        /// <summary>カードの中の文字。枠の高さを決め打ちにする（高さが行の高さに足りないと、字が丸ごと消える）。</summary>
        private static TextMeshProUGUI CardText(RectTransform card, string value, int size, Color color, float height)
        {
            TextMeshProUGUI t = NewText(card, value, size, color);
            LayoutElement le = t.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = 312f;
            le.minHeight = height;
            le.preferredHeight = height;
            return t;
        }

        /// <summary>契約を手に入れた経路。依頼看板なら看板の名前、返済の報酬なら「返済」と何回目の後か。</summary>
        private static string PactSource(JObject pact, JObject context)
        {
            string type = RunData.Str(context, "sourceType");
            if (type == "TradeReward") { return ObjectName("TradePact"); }
            return Message("Repayment") + " " + RunData.Long(pact, "acquiredAfterRepaymentCount");
        }

        private static readonly Dictionary<string, Sprite> PactSprites = new Dictionary<string, Sprite>();

        /// <summary>契約の絵。本体は Run の中で都度読み込むが、絵の在りかは契約の表にあるので、ここで読む。
        /// 在りかはアイテムの ID・資源の ID・Addressables のアドレスのどれか（PactProperty.LoadSpriteAsync と同じ順）。</summary>
        // ---------------------------------------------------------------
        // Eye Drops（別の MOD）が描き替える契約の絵
        //
        // Eye Drops は、契約「コイントス」「偽社員証」の絵を、その Run で当たる候補の使い魔に描き替える。
        // 業務日誌には Run 中の契約そのものが無いので、候補は取った瞬間のカードの文（PactLog.cs の控え）から読む。
        // 文には候補が <link=Summon-…> の形で、Eye Drops が使うのと同じ並びで入っている。
        // 描くのは Eye Drops の処理（CoinTossIcon.Make / FakeIdIcon.Make）をそのまま呼ぶ。Eye Drops が入っていない・
        // 設定で切ってある・カードの文の控えが無い Run では、元の絵のまま。
        // ---------------------------------------------------------------
        private const string EyeDropsGuid = "kiyonakanata.lwfeyedrops";
        private static readonly Dictionary<string, Dictionary<string, string>> PactTexts =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Sprite> RedrawnPacts = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private static readonly System.Text.RegularExpressions.Regex SummonLink =
            new System.Text.RegularExpressions.Regex("<link=(Summon-[A-Za-z0-9]+)>");

        /// <summary>その Run で取った契約のカードの文（契約の ID → 文。同じ契約を何度も取っていれば最初の1つ）。</summary>
        private static string PactText(RunSummary run, string id)
        {
            Dictionary<string, string> texts;
            if (!PactTexts.TryGetValue(run.Path, out texts))
            {
                if (PactTexts.Count >= 300) { PactTexts.Clear(); }
                texts = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (JObject card in LoadPactCards(EconomyGraphPlugin.FindRunFile(run, EconomyGraphPlugin.PactSuffix)))
                {
                    string cardID = RunData.Str(card, "id");
                    if (cardID != null && !texts.ContainsKey(cardID)) { texts[cardID] = RunData.Str(card, "description") ?? ""; }
                }
                PactTexts[run.Path] = texts;
            }
            string text;
            return texts.TryGetValue(id, out text) ? text : null;
        }

        /// <summary>契約の絵。その Run の候補に合わせて Eye Drops が描き替える契約は、描き替えた絵。</summary>
        private static Sprite PactSprite(RunSummary run, string id)
        {
            Sprite original = PactSprite(id);
            string maker = id == "SponsorFamLotteryPact" ? "CoinTossIcon" : (id == "BuyFams" ? "FakeIdIcon" : null);
            if (original == null || run == null || maker == null) { return original; }
            try
            {
                // 候補の使い魔。取った瞬間のカードの文の控えがあれば、そこから読む（その時の本体の版での候補）。
                // 控えが無い Run（業務日誌が入る前の Run）は、本体と同じ決め方で、その Run のスポンサーから出す
                // （コイントス＝スポンサーの使い魔、偽社員証＝スポンサー外の使い魔。SponsorFamLotteryPact.GetCandidates）
                List<Fams.EditIfFamAdd.FamType> fams = new List<Fams.EditIfFamAdd.FamType>();
                string text = PactText(run, id);
                if (!string.IsNullOrEmpty(text))
                {
                    foreach (System.Text.RegularExpressions.Match m in SummonLink.Matches(text))
                    {
                        Fams.EditIfFamAdd.FamType fam;
                        if (!Fams.FamTypes.CATALYST_FAM.TryGetValue(m.Groups[1].Value, out fam) || fams.Contains(fam)) { continue; }
                        fams.Add(fam);
                    }
                }
                if (fams.Count == 0 && run.Patrons != null)
                {
                    List<Patron> patrons = new List<Patron>();
                    for (int i = 0; i < run.Patrons.Length; i++)
                    {
                        try { patrons.Add((Patron)Enum.Parse(typeof(Patron), run.Patrons[i])); }
                        catch (Exception) { }
                    }
                    fams.AddRange(id == "BuyFams"
                        ? global::Pact.Pacts.GetItemPacts.CharacterFamRewards.GetNonSponsorFams(patrons)
                        : global::Pact.Pacts.GetItemPacts.CharacterFamRewards.GetSponsorFams(patrons));
                }
                if (fams.Count == 0) { PactArtNote(run.Path + "/" + id, id + ": no candidate familiars for run #" + run.Sequence); return original; }
                string key = id;
                for (int i = 0; i < fams.Count; i++) { key += ":" + (int)fams[i]; }
                Sprite made;
                if (RedrawnPacts.TryGetValue(key, out made)) { return made ?? original; }

                made = null;
                BepInEx.BaseUnityPlugin eyeDrops = EconomyGraphPlugin.FindPlugin(EyeDropsGuid);
                foreach (BepInEx.BaseUnityPlugin plugin in new BepInEx.BaseUnityPlugin[] { eyeDrops })
                {
                    if (plugin == null || !plugin.enabled) { continue; }
                    Type type = plugin.GetType();

                    const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    string toggle = maker == "CoinTossIcon" ? "CoinToss" : "FakeEmployeeId";
                    if (!EyeDropsOn(type, "Enabled", any) || !EyeDropsOn(type, toggle, any)) { break; }
                    Type drawer = type.Assembly.GetType("LwfEyeDrops." + maker);
                    System.Reflection.MethodInfo make = drawer != null ? drawer.GetMethod("Make", any) : null;
                    if (make != null) { made = make.Invoke(null, new object[] { original, fams }) as Sprite; }
                    break;
                }
                RedrawnPacts[key] = made;
                PactArtNote(key, key + ": Eye Drops " + (eyeDrops == null ? "not found" : (made != null ? "redrew it" : "left it as is")));
                return made ?? original;
            }
            catch (Exception e)
            {
                EconomyGraphPlugin.Log.LogWarning("[view] cannot redraw the pact art of " + id + ": " + e.Message);
                return original;
            }
        }

        private static readonly HashSet<string> PactArtNoted = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>契約の絵を描き替えたか・なぜ描き替えなかったかを、同じ件につき1回だけログに出す。</summary>
        private static void PactArtNote(string key, string message)
        {
            if (!PactArtNoted.Add(key)) { return; }
            EconomyGraphPlugin.Log.LogInfo("[view] pact art " + message);
        }

        private static bool EyeDropsOn(Type plugin, string name, System.Reflection.BindingFlags flags)
        {
            System.Reflection.FieldInfo field = plugin.GetField(name, flags);
            BepInEx.Configuration.ConfigEntry<bool> entry = field != null ? field.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool> : null;
            return entry != null && entry.Value;
        }

        private static Sprite PactSprite(string id)
        {
            if (id == null) { return null; }
            Sprite sprite;
            if (PactSprites.TryGetValue(id, out sprite)) { return sprite; }
            try
            {
                PactPropertyDefinition property;
                if (PactMaster.IsInitialized && PactMaster.TryGetProperty(id, out property)
                    && !string.IsNullOrEmpty(property.SpriteAddress))
                {
                    string address = property.SpriteAddress;
                    // アイテム・資源の絵は、本体と同じく大きい版を先に当たる（小さいアイコンだと粗い）
                    // 使い魔を呼ぶアイテム（日雇い〜の契約）は、本体と同じく使い魔の顔を使う
                    // （ItemParamGetter.LoadLargeSpriteAsync の TryGetSummonCatalystSprite。アイテムの絵は 64px しか無い）
                    Fams.EditIfFamAdd.FamType summoned;
                    if (Fams.FamTypes.CATALYST_FAM.TryGetValue(address, out summoned))
                    {
                        sprite = Fams.FamTypes.GetFaceIcon(summoned) ?? ItemParamGetter.GetSprite(address);
                    }
                    else if (ItemParamGetter.HasItem(address))
                    {
                        sprite = LargeSprite(ItemParamGetter.GetLargeSpriteAddress(address)) ?? ItemParamGetter.GetSprite(address);
                    }
                    else if (TagParamGetter.HasTag(address))
                    {
                        sprite = LargeSprite(TagParamGetter.GetLargeSpriteAddress(address)) ?? TagParamGetter.GetSprite(address);
                    }
                    else { sprite = Addressables.LoadAssetAsync<Sprite>(address).WaitForCompletion(); }
                }
            }
            catch (Exception) { sprite = null; }
            PactSprites[id] = sprite;
            return sprite;
        }
    }
}
