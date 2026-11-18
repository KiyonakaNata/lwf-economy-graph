// 業務日誌の画面。一覧から Run を選ぶと、その Run の詳細に全幅で切り替わる（「一覧に戻る」か Esc で戻る）。
// ここは骨組みと一覧と共通の部品。詳細の中身は LogRunView.cs（統計・契約履歴・課税通知書）と LogMapView.cs（工場の配置図）。
// （最初は左に一覧・右に詳細の2分割だったが、配置図が半分の幅では狭すぎた。2026-11-17）
//
// グラフは統計画面と同じもの。この画面は枠（GraphArea）を空けるだけで、描くのはプラグイン本体
// （IMGUI。RunLog.cs の DrawLogGraph）。グラフの記録が無い Run では枠を畳む。
//
// 自前の Canvas に組む（本体の画面の部品は借りない。執務室の作りが変わっても画面は出る）。
// 借りるのは字の書体だけで、その場面にある文字（業務日誌ボタンの字）から取る。
// 画面に出す語は本体の言語表から引く。自前の文字列は焼き込まない（mods/CLAUDE.md）。
//
// 契約は名前だけ。契約の絵は本体が Run の中で都度読み込むもので、執務室では手元に無い。

using System;
using System.Collections.Generic;
using BaseSystem;
using GameState;
using Items;
using Items.Craft;
using Items.Tag;
using Newtonsoft.Json.Linq;
using Pact;
using Pact.MasterData;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Utility.LWFApp;
using Utility.Localization;

namespace LwfEconomyGraph
{
    internal static partial class LogView
    {
        private const string RootName = "BusinessLogView";
        private const int Font = 22;
        private const int SmallFont = 20;
        private const int RowHeight = 40;
        private const int IconSize = 30;

        private static readonly Color TextColor = LWFColors.WHITE_COLOR;
        private static readonly Color DimColor = LWFColors.DESC_COLOR;
        private static readonly Color HeadColor = LWFColors.HEADER_COLOR;
        private static readonly Color LoseColor = LWFColors.RED_COLOR;
        private static readonly Color WinColor = LWFColors.ORANGE_COLOR;
        private static readonly Color SelectedColor = new Color(1f, 1f, 1f, 0.12f);
        private static readonly Color ClearColor = new Color(1f, 1f, 1f, 0f);

        private static GameObject _root;
        private static TMP_FontAsset _font;
        private static RectTransform _detail;
        private static ScrollRect _detailScroll;
        private static RectTransform _graphArea;
        private static RectTransform _tabRow;
        private static GameObject _title;
        private static GameObject _back;
        private static GameObject _listFrame;

        // 0 = 一覧・1 = 選んだ Run の詳細（LogRunView.cs）
        private static int _page;
        private static bool _hasGraph;
        private static Image _selectedRow;
        private static List<RunSummary> _runs;
        private static RectTransform _filterRow;
        private static RectTransform _headRow;
        private static RectTransform _list;

        // 並べ替え。0 = 通し番号・1 = 難易度・2 = プレイ時間・3 = 返済回数・4 = ゲームモード
        private const float DateWidth = 70f;
        private const float NumberColumn = 90f;   // 「#1234」まで入る幅
        private const float DifficultyWidth = 190f;
        private static int _sortColumn;
        private static bool _sortAscending;

        internal static bool IsOpen { get { return _root != null; } }

        /// <summary>グラフを描く枠。出していないときは null。</summary>
        internal static RectTransform GraphArea
        {
            get { return (_graphArea != null && _graphArea.gameObject.activeInHierarchy) ? _graphArea : null; }
        }

        internal static void Close()
        {
            if (_root != null) { UnityEngine.Object.Destroy(_root); }
            _root = null;
            _detail = null;
            _detailScroll = null;
            _graphArea = null;
            _tabRow = null;
            _mapPane = null;
            _title = null;
            _back = null;
            _listFrame = null;
            _summaryRow = null;
            _statTabRow = null;
            _resultTitle = null;
            _selectedRow = null;
            _preview = null;
            _previewRun = null;
            // 見つからなかった顔は、次に開いたときに探し直す（その間に Run を遊んで読み込まれているかもしれない）
            DialogueFaceMisses.Clear();
            _filterRow = null;
            _headRow = null;
            _list = null;
        }

        internal static void Open()
        {
            Close();
            try
            {
                List<RunSummary> runs = RunData.LoadAll();
                Build(runs);
                EconomyGraphPlugin.Log.LogInfo("[view] opened, runs=" + runs.Count);
            }
            catch (Exception e)
            {
                EconomyGraphPlugin.Log.LogWarning("[view] cannot open: " + e);
                Close();
            }
        }

        // ---------------------------------------------------------------
        // 画面の骨組み
        // ---------------------------------------------------------------

        private static void Build(List<RunSummary> runs)
        {
            FindFont();

            GameObject stale = GameObject.Find(RootName);
            if (stale != null) { UnityEngine.Object.DestroyImmediate(stale); }

            _root = new GameObject(RootName);
            Canvas canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000;
            CanvasScaler scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            // 高さに合わせる。横長の画面でも板の大きさが変わらない
            scaler.matchWidthOrHeight = 1f;
            _root.AddComponent<GraphicRaycaster>();

            // 後ろの執務室を押せないよう、全面に幕を張る
            RectTransform dim = Rect("Dim", _root.transform);
            Stretch(dim);
            dim.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.8f);

            RectTransform panel = Rect("Panel", _root.transform);
            panel.anchorMin = new Vector2(0.5f, 0.5f);
            panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.sizeDelta = new Vector2(1800f, 980f);
            panel.gameObject.AddComponent<Image>().color = LWFColors.BLACK_COLOR;

            TextMeshProUGUI title = NewText(panel, StripRichText(Message("Archives")), 32, HeadColor);
            RectTransform tr = title.rectTransform;
            tr.anchorMin = new Vector2(0f, 1f);
            tr.anchorMax = new Vector2(0f, 1f);
            tr.pivot = new Vector2(0f, 1f);
            tr.anchoredPosition = new Vector2(28f, -16f);
            tr.sizeDelta = new Vector2(600f, 44f);
            // 枠より行の高さが大きいと字が丸ごと消える（省略の扱い）ので、はみ出しを許す
            title.overflowMode = TextOverflowModes.Overflow;
            _title = title.gameObject;

            TextMeshProUGUI back = NewText(panel, StripRichText(Message("BackToList")), 26, TextColor);
            back.raycastTarget = true;
            RectTransform br = back.rectTransform;
            br.anchorMin = new Vector2(0f, 1f);
            br.anchorMax = new Vector2(0f, 1f);
            br.pivot = new Vector2(0f, 1f);
            br.anchoredPosition = new Vector2(28f, -16f);
            br.sizeDelta = new Vector2(280f, 44f);
            Button backButton = back.gameObject.AddComponent<Button>();
            backButton.targetGraphic = back;
            backButton.onClick.AddListener(delegate { ShowPage(0); });
            _back = back.gameObject;

            TextMeshProUGUI close = NewText(panel, Message("Close"), 26, TextColor);
            close.alignment = TextAlignmentOptions.MidlineRight;
            close.raycastTarget = true;
            RectTransform cr = close.rectTransform;
            cr.anchorMin = new Vector2(1f, 1f);
            cr.anchorMax = new Vector2(1f, 1f);
            cr.pivot = new Vector2(1f, 1f);
            cr.anchoredPosition = new Vector2(-28f, -16f);
            cr.sizeDelta = new Vector2(200f, 44f);
            Button closeButton = close.gameObject.AddComponent<Button>();
            closeButton.targetGraphic = close;
            closeButton.onClick.AddListener(delegate { Close(); });

            _filterRow = Row(panel, RowHeight);
            PlaceAbove(_filterRow, 72f, 12);
            // この段は部品が多い（チェック4つ・星・スポンサーの顔・戻す・送り）。段の幅は一覧と同じ（約 1100）で、
            // 部品の間を行の既定（8）のままにすると入りきらず、語が押しつぶされて「…」になった。間を詰める
            _filterRow.GetComponent<HorizontalLayoutGroup>().spacing = 4f;
            _headRow = Row(panel, 32);
            // 一覧の行と同じ位置に列が来るよう、送り枠の余白（12）と行の余白（6）を足す
            PlaceAbove(_headRow, 116f, 18);

            ScrollRect listScroll;
            _list = Scroll(panel, out listScroll);
            RectTransform lr = listScroll.transform as RectTransform;
            lr.anchorMin = new Vector2(0f, 0f);
            lr.anchorMax = new Vector2(1f, 1f);
            lr.offsetMin = new Vector2(24f, 24f);
            // 右はカーソルを載せた Run のあらまし（LogListView.cs）
            lr.offsetMax = new Vector2(-24f - PreviewWidth - 12f, -150f);
            _listFrame = listScroll.gameObject;
            _headRow.sizeDelta = new Vector2(1752f - PreviewWidth - 12f, _headRow.sizeDelta.y);
            _filterRow.sizeDelta = new Vector2(1752f - PreviewWidth - 12f, _filterRow.sizeDelta.y);
            BuildPreviewPane(panel);

            _graphArea = Rect("GraphArea", panel);
            _graphArea.anchorMin = new Vector2(0f, 1f);
            _graphArea.anchorMax = new Vector2(1f, 1f);
            _graphArea.offsetMin = new Vector2(GraphLeft, -StatTop - GraphHeight);
            _graphArea.offsetMax = new Vector2(-24f, -StatTop);
            _graphArea.gameObject.SetActive(false);

            // 詳細の上の3段：結果（中央）・その Run の要約・切り替え
            _resultTitle = NewText(panel, "", 34, HeadColor);
            _resultTitle.alignment = TextAlignmentOptions.Center;
            _resultTitle.fontStyle = FontStyles.Bold;
            _resultTitle.overflowMode = TextOverflowModes.Overflow;
            RectTransform rt = _resultTitle.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -12f);
            rt.sizeDelta = new Vector2(700f, 48f);

            _summaryRow = Row(panel, RowHeight);
            PlaceAbove(_summaryRow, 64f, 6);
            _tabRow = Row(panel, RowHeight);
            PlaceAbove(_tabRow, 112f, 6);
            _statTabRow = Row(panel, RowHeight);
            PlaceAbove(_statTabRow, ContentTop, 6);

            _mapPane = Rect("MapPane", panel);
            _mapPane.anchorMin = new Vector2(0f, 0f);
            _mapPane.anchorMax = new Vector2(1f, 1f);
            _mapPane.offsetMin = new Vector2(24f, 24f);
            _mapPane.offsetMax = new Vector2(-24f, -ContentTop);
            _mapPane.gameObject.SetActive(false);

            _detail = Scroll(panel, out _detailScroll);
            RectTransform dr = _detailScroll.transform as RectTransform;
            dr.anchorMin = new Vector2(0f, 0f);
            dr.anchorMax = new Vector2(1f, 1f);
            dr.offsetMin = new Vector2(24f, 24f);
            dr.offsetMax = new Vector2(-24f, -ContentTop);

            _runs = runs;
            FillMissingTargets(_runs);
            RefreshList();
            ShowPage(0);
        }

        /// <summary>一覧と詳細を切り替える。</summary>
        private static void ShowPage(int page)
        {
            _page = page;
            bool list = page == 0;
            _title.SetActive(list);
            _filterRow.gameObject.SetActive(list);
            _headRow.gameObject.SetActive(list);
            _listFrame.SetActive(list);
            _preview.gameObject.SetActive(list);
            _back.SetActive(!list);
            _tabRow.gameObject.SetActive(!list);
            _summaryRow.gameObject.SetActive(!list);
            _resultTitle.gameObject.SetActive(!list);
            if (list)
            {
                _statTabRow.gameObject.SetActive(false);
                _graphArea.gameObject.SetActive(false);
                _detailScroll.gameObject.SetActive(false);
                _mapPane.gameObject.SetActive(false);
            }
            else { ApplyTab(); }
        }

        /// <summary>Esc。詳細なら一覧へ戻り、一覧なら画面を閉じる。</summary>
        internal static void OnEscape()
        {
            if (_root == null) { return; }
            if (_page == 1) { ShowPage(0); }
            else { Close(); }
        }

        /// <summary>一覧の上に置く行（絞り込み・列の見出し）。板の左上から決め打ちで置く。</summary>
        private static void PlaceAbove(RectTransform row, float top, int sidePadding)
        {
            row.anchorMin = new Vector2(0f, 1f);
            row.anchorMax = new Vector2(0f, 1f);
            row.pivot = new Vector2(0f, 1f);
            row.anchoredPosition = new Vector2(24f, -top);
            row.sizeDelta = new Vector2(1752f, row.GetComponent<LayoutElement>().minHeight);
            HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(sidePadding, sidePadding, 0, 0);
        }

        // ---------------------------------------------------------------
        // 絞り込みと並べ替え
        // ---------------------------------------------------------------

        /// <summary>結果の3分け。0 = 返済完了・1 = 破産・2 = ゲームを放棄（タイトルへ戻る・終了・中断）</summary>
        private static int Category(RunSummary run)
        {
            if (run.Result == "Victory") { return 0; }
            if (run.EndReason == "DefeatTimeUp") { return 1; }
            return 2;
        }

        private static BepInEx.Configuration.ConfigEntry<bool> Shown(int category)
        {
            if (category == 0) { return EconomyGraphPlugin.ShowCleared; }
            if (category == 1) { return EconomyGraphPlugin.ShowBankrupt; }
            return EconomyGraphPlugin.ShowAbandoned;
        }

        private static readonly string[] CategoryKeys = new string[] { "DebtRepaid", "YouLose", "AbortGame" };
        private static readonly Color[] CategoryColors = new Color[] { WinColor, LoseColor, DimColor };

        /// <summary>絞り込み・見出し・一覧を、いまの設定で組み直す。</summary>
        private static void RefreshList()
        {
            Clear(_filterRow);
            Clear(_headRow);
            Clear(_list);
            _selectedRow = null;

            MeasureColumns();
            for (int c = 0; c < 3; c++) { AddFilter(c); }
            // 隠した行も出すか。語が無いので、行の印と同じ「×」で
            bool cross = _font != null && _font.HasCharacter('\u00D7', true);
            AddCheck(cross ? "\u00D7" : "x", DimColor, EconomyGraphPlugin.ShowHidden);
            // ここから右は「抜き出す」側（星・スポンサー）。左の □（出す・出さない）とは働きが違うので、
            // 縦線で区切り、□ は付けない。押した絵のものだけに絞る。右端に、絞りを全部外すボタン
            FilterDivider();
            StarToggle();
            AddPatronFilters();
            ResetFilters();

            Gap(_headRow, 30f);
            AddSortHead("#", 0, NumberColumn, false);
            AddSortHead(Message("GameMode"), 4, _modeWidth, false);
            AddSortHead(Message("Difficulty"), 1, _difficultyWidth, false);
            Gap(_headRow, _resultWidth);
            // 本体の言語表に「プレイ時間」に当たる語が無いので、値の形をそのまま見出しにする
            AddSortHead("m:ss", 2, 66f, true);
            // 見出しは「返済」。「返済回数」は英語で Repayment Frequency、ロシア語で Количество погашений になり、
            // 数字の列（110）に入らない
            AddSortHead(Message("Repayment"), 3, 110f, true);
            Gap(_headRow, IconSize * 3 + 16f + 6f + 30f + 8f);

            List<RunSummary> shown = new List<RunSummary>();
            for (int i = 0; i < _runs.Count; i++)
            {
                if (!Shown(Category(_runs[i])).Value) { continue; }
                if (IsHidden(_runs[i]) && !EconomyGraphPlugin.ShowHidden.Value) { continue; }
                if (EconomyGraphPlugin.OnlyFavorites.Value && !IsFavorite(_runs[i])) { continue; }
                if (!HasPatrons(_runs[i])) { continue; }
                shown.Add(_runs[i]);
            }
            shown.Sort(Compare);

            // 一度に組むのは PageSize 行まで。それより多ければ、上の段の右に送りのボタンを出す
            int pages = Mathf.Max(1, (shown.Count + PageSize - 1) / PageSize);
            _listPage = Mathf.Clamp(_listPage, 0, pages - 1);
            if (pages > 1) { AddPager(pages); }
            int first = _listPage * PageSize;
            for (int i = first; i < shown.Count && i < first + PageSize; i++) { AddListRow(_list, shown[i], (i - first) % 2 == 1); }
        }

        private static int Compare(RunSummary a, RunSummary b)
        {
            int order = 0;
            if (_sortColumn == 1) { order = DifficultyRank(a).CompareTo(DifficultyRank(b)); }
            else if (_sortColumn == 2) { order = a.ElapsedSeconds.CompareTo(b.ElapsedSeconds); }
            else if (_sortColumn == 3) { order = a.RepaymentsDone.CompareTo(b.RepaymentsDone); }
            else if (_sortColumn == 4) { order = string.CompareOrdinal(b.Mission ?? "", a.Mission ?? ""); }
            else { order = a.Sequence.CompareTo(b.Sequence); }
            // 同じ値どうしは新しい順のまま
            if (order == 0) { return b.Sequence.CompareTo(a.Sequence); }
            return _sortAscending ? order : -order;
        }

        /// <summary>難易度の並び。本体の難易度の番号順。</summary>
        private static int DifficultyRank(RunSummary run)
        {
            int rank = -1;
            try { rank = (int)Enum.Parse(typeof(Difficulty), run.Difficulty); }
            catch (Exception) { }
            return rank;
        }

        private static void AddFilter(int category)
        {
            AddCheck(Message(CategoryKeys[category]), CategoryColors[category], Shown(category));
        }

        // 語の列（ゲームモード・難易度・結果）の幅。日本語の長さで決め打ちにすると、ほかの言語で「…」だらけになる
        // （英語の Develop Factory は「工場開発」の倍）。一覧に出てくる語を実際の書体で測って決める。
        // 3つの合計が使える幅を超えるときは、同じ割合で縮める（その列は Cell が字を小さくして収める）。
        // 余るときは、余りをゲームモードの列に回す（前は、この列が余りを全部もらう作りだった）
        private const float ColumnGap = 28f;
        private static float _modeWidth = 200f, _difficultyWidth = DifficultyWidth, _resultWidth = 150f;

        private static void MeasureColumns()
        {
            TextMeshProUGUI ruler = NewText(_headRow, "", Font, TextColor);
            ruler.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            float mode = 0f, difficulty = 0f, result = 0f;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _runs.Count; i++)
            {
                string m = RunName(_runs[i]), d = DifficultyName(_runs[i].Difficulty), r = ResultText(_runs[i]);
                if (!string.IsNullOrEmpty(m) && seen.Add("m" + m)) { mode = Mathf.Max(mode, ruler.GetPreferredValues(m).x); }
                if (!string.IsNullOrEmpty(d) && seen.Add("d" + d)) { difficulty = Mathf.Max(difficulty, ruler.GetPreferredValues(d).x); }
                if (!string.IsNullOrEmpty(r) && seen.Add("r" + r)) { result = Mathf.Max(result, ruler.GetPreferredValues(r).x); }
            }
            ruler.fontSize = SmallFont;
            mode = Mathf.Max(mode, ruler.GetPreferredValues(Message("GameMode") + " \u25BC").x);
            difficulty = Mathf.Max(difficulty, ruler.GetPreferredValues(Message("Difficulty") + " \u25BC").x);
            UnityEngine.Object.Destroy(ruler.gameObject);
            // 語の後ろに空きを付ける。測った幅ちょうどにすると、隣の列の語とくっついて見える
            mode = Mathf.Ceil(mode) + ColumnGap;
            difficulty = Mathf.Ceil(difficulty) + ColumnGap;
            result = Mathf.Ceil(result) + ColumnGap;

            // 使える幅：行の幅から、幅の決まった部品（×・#・日付・時間・返済・顔3つ・星）と、部品の間・左右の余白を引く
            Canvas.ForceUpdateCanvases();
            float row = _headRow != null ? _headRow.rect.width : 0f;
            if (row < 400f) { row = 1100f; }
            // 末尾の 20 は、行と見出しの幅の差（送り枠の余白）と、端数のゆとり。足りないと右端の星が切れる
            float fixedParts = 12f + 30f + NumberColumn + 66f + 110f + IconSize * 3 + 6f + 30f + 8f * 11 + 20f;
            float budget = Mathf.Max(300f, row - fixedParts);
            float total = mode + difficulty + result;
            if (total > budget)
            {
                float k = budget / total;
                mode *= k;
                difficulty *= k;
                result *= k;
            }
            else { mode += budget - total; }
            _modeWidth = Mathf.Floor(mode);
            _difficultyWidth = Mathf.Floor(difficulty);
            _resultWidth = Mathf.Floor(result);
        }

        /// <summary>1回も返済していない Run は、本体の記録に目標の回数が残らない（返済のたびにしか書かれない）。
        /// 「0/0」にならないよう補う。同じ難易度・同じ任務で返済のあった Run があれば、その目標のいちばん小さい数
        /// （目標は追加融資で途中から増えることがあるので、小さいほうが始めの値）。無ければ、任務なしの Run に限り
        /// 本体の式（WinCondition.CalcTargetProgress）。それも無理なら 0 のままで、表示は「0/-」。</summary>
        private static void FillMissingTargets(List<RunSummary> runs)
        {
            for (int i = 0; i < runs.Count; i++)
            {
                RunSummary run = runs[i];
                if (run.RepaymentsTarget > 0) { continue; }
                int found = 0;
                for (int k = 0; k < runs.Count; k++)
                {
                    RunSummary other = runs[k];
                    if (other.RepaymentsTarget <= 0 || other.Difficulty != run.Difficulty || other.Mission != run.Mission) { continue; }
                    if (found == 0 || other.RepaymentsTarget < found) { found = other.RepaymentsTarget; }
                }
                if (found == 0 && string.IsNullOrEmpty(run.Mission) && !string.IsNullOrEmpty(run.Difficulty))
                {
                    try
                    {
                        found = GameState.WinCondition.CalcTargetProgress(
                            (GameState.Difficulty)Enum.Parse(typeof(GameState.Difficulty), run.Difficulty));
                    }
                    catch (Exception) { found = 0; }
                }
                run.RepaymentsTarget = found;
            }
        }

        /// <summary>「済んだ回数/目標」。目標が分からない Run は「0/-」。</summary>
        private static string Repayments(RunSummary run)
        {
            return run.RepaymentsDone + "/" + (run.RepaymentsTarget > 0 ? run.RepaymentsTarget.ToString() : "-");
        }

        /// <summary>絞り込みのチェック。「□ 語」。押すと設定を入れ替えて一覧を組み直す。</summary>
        private static void AddCheck(string text, Color color, BepInEx.Configuration.ConfigEntry<bool> entry)
        {
            bool on = entry.Value;
            if (!on) { color.a = 0.35f; }

            RectTransform box = Rect("Box", _filterRow);
            Image frame = box.gameObject.AddComponent<Image>();
            frame.color = color;
            LayoutElement le = box.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 20f;
            le.preferredWidth = 20f;
            le.preferredHeight = 20f;
            if (!on)
            {
                // 切のときは中を抜いて、枠だけに見せる
                RectTransform hole = Rect("Hole", box);
                hole.anchorMin = Vector2.zero;
                hole.anchorMax = Vector2.one;
                hole.offsetMin = new Vector2(3f, 3f);
                hole.offsetMax = new Vector2(-3f, -3f);
                Image inner = hole.gameObject.AddComponent<Image>();
                inner.color = LWFColors.BLACK_COLOR;
                inner.raycastTarget = false;
            }

            TextMeshProUGUI label = NewText(_filterRow, text, Font, color);
            label.raycastTarget = true;
            // 語は縮めさせない（段が混んでも「…」にしない）。字の幅をそのまま最小の幅にする
            LayoutElement labelSize = label.gameObject.AddComponent<LayoutElement>();
            float labelWidth = string.IsNullOrEmpty(text) ? 0f : Mathf.Ceil(TextWidth(text, Font, FontStyles.Normal));
            labelSize.minWidth = labelWidth;
            labelSize.preferredWidth = labelWidth;
            Gap(_filterRow, 14f);

            BepInEx.Configuration.ConfigEntry<bool> captured = entry;
            UnityEngine.Events.UnityAction toggle = delegate
            {
                captured.Value = !captured.Value;
                RefreshList();
            };
            Button a = box.gameObject.AddComponent<Button>();
            a.transition = Selectable.Transition.None;
            a.targetGraphic = frame;
            a.onClick.AddListener(toggle);
            Button b = label.gameObject.AddComponent<Button>();
            b.transition = Selectable.Transition.None;
            b.targetGraphic = label;
            b.onClick.AddListener(toggle);
        }

        private static void AddSortHead(string title, int column, float width, bool right)
        {
            bool active = _sortColumn == column;
            string mark = "";
            if (active)
            {
                // 書体に三角が無いときは英字で代える
                bool glyph = _font != null && _font.HasCharacter('\u25BC', true);
                if (_sortAscending) { mark = glyph ? " \u25B2" : " ^"; }
                else { mark = glyph ? " \u25BC" : " v"; }
            }
            TextMeshProUGUI t = Cell(_headRow, title + mark, SmallFont, active ? HeadColor : DimColor, width, right);
            t.raycastTarget = true;
            Button button = t.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = t;
            int captured = column;
            button.onClick.AddListener(delegate
            {
                if (_sortColumn == captured) { _sortAscending = !_sortAscending; }
                else { _sortColumn = captured; _sortAscending = false; }
                _listPage = 0;
                RefreshList();
            });
        }

        private static void Clear(RectTransform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                GameObject child = parent.GetChild(i).gameObject;
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }
        }

        private static void FindFont()
        {
            TextMeshProUGUI source = null;
            GameObject button = GameObject.Find("ButtonArchives");
            if (button != null) { source = button.GetComponentInChildren<TextMeshProUGUI>(true); }
            if (source == null)
            {
                TextMeshProUGUI[] all = UnityEngine.Object.FindObjectsByType<TextMeshProUGUI>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (all.Length > 0) { source = all[0]; }
            }
            _font = source != null ? source.font : null;
        }

        /// <summary>縦に送れる枠を作り、行を積む入れ物を返す。</summary>
        private static RectTransform Scroll(Transform parent, out ScrollRect scroll)
        {
            RectTransform frame = Rect("Scroll", parent);
            // ホイールは絵のある所でしか受けられないので、枠に地を持たせる
            frame.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.04f);
            scroll = frame.gameObject.AddComponent<ScrollRect>();

            RectTransform viewport = Rect("Viewport", frame);
            Stretch(viewport);
            viewport.gameObject.AddComponent<RectMask2D>();

            RectTransform content = Rect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;
            VerticalLayoutGroup column = content.gameObject.AddComponent<VerticalLayoutGroup>();
            column.padding = new RectOffset(12, 12, 8, 8);
            column.spacing = 2f;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;
            ContentSizeFitter fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = false;
            scroll.scrollSensitivity = 40f;
            return content;
        }

        // ---------------------------------------------------------------
        // 一覧
        // ---------------------------------------------------------------

        private static Image AddListRow(RectTransform list, RunSummary run, bool stripe)
        {
            RectTransform row = Row(list, RowHeight);
            Image back = row.gameObject.AddComponent<Image>();
            // 1行おきに薄く塗って、横に目で追えるようにする。カーソルを載せた行は明るくする（押せる合図）
            back.color = stripe ? StripeColor : ClearColor;
            RowHover hover = row.gameObject.AddComponent<RowHover>();
            hover.Back = back;
            hover.Base = back.color;
            hover.Run = run;
            bool hidden = IsHidden(run);
            if (hidden) { row.gameObject.AddComponent<CanvasGroup>().alpha = 0.45f; }
            Button button = row.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = back;
            RunSummary captured = run;
            Image capturedBack = back;
            button.onClick.AddListener(delegate { Select(capturedBack, captured); });

            HideButton(row, run, hidden);
            Cell(row, "#" + run.Sequence, Font, DimColor, NumberColumn, false);
            // 日付は出さない（2026-11-18 ユーザー判断。新旧は通し番号で分かり、日時は右のあらましと詳細の上の段に出る。
            // 列を1つ減らした分を、語の列の間の空きに回す）
            Cell(row, RunName(run), Font, TextColor, _modeWidth, false);
            Cell(row, DifficultyName(run.Difficulty), Font, TextColor, _difficultyWidth, false);
            Cell(row, ResultText(run), Font, ResultColor(run), _resultWidth, false);
            Cell(row, Clock(run.ElapsedSeconds), Font, TextColor, 66f, true);
            Cell(row, Repayments(run), Font, TextColor, 110f, true);
            for (int i = 0; i < 3; i++) { Icon(row, i < run.Patrons.Length ? PatronSprite(run.Patrons[i]) : null, IconSize); }
            // 星は行の右端。隠す（×）は行の頭にあり、隣に置くと押し間違える（2026-11-18）
            Gap(row, 6f);
            StarButton(row, run, IsFavorite(run));
            return back;
        }

        private static List<JObject> Objects(JObject o, string key)
        {
            List<JObject> list = new List<JObject>();
            foreach (JToken t in RunData.List(o, key))
            {
                JObject item = t as JObject;
                if (item != null) { list.Add(item); }
            }
            return list;
        }

        // ---------------------------------------------------------------
        // 値の書き方
        // ---------------------------------------------------------------

        private static string Clock(double seconds)
        {
            int total = (int)seconds;
            return (total / 60) + ":" + (total % 60).ToString("00");
        }

        private static string Time(JObject o, string key)
        {
            return RunData.Has(o, key) ? Clock(RunData.Num(o, key)) : "-";
        }

        private static string Count(JObject o, string key)
        {
            return RunData.Long(o, key).ToString("N0");
        }

        /// <summary>ゲームモード。特別任務はその任務の名前、通常は「工場開発」。難易度は別に出す。</summary>
        private static string RunName(RunSummary run)
        {
            if (run.Mission != null) { return MissionName(run.Mission); }
            return StripRichText(Message("FactoryDevelop"));
        }

        private static string ResultText(RunSummary run)
        {
            if (run.Result == "Victory") { return Message("DebtRepaid"); }
            if (run.EndReason == "DefeatTimeUp") { return Message("YouLose"); }
            return Message("AbortGame");
        }

        private static Color ResultColor(RunSummary run)
        {
            if (run.Result == "Victory") { return WinColor; }
            if (run.EndReason == "DefeatTimeUp") { return LoseColor; }
            return DimColor;
        }

        // ---------------------------------------------------------------
        // ゲームの文字と絵。どれも引けなければ ID のまま出す
        // ---------------------------------------------------------------

        private static string Message(string key)
        {
            return Safe(delegate { return LocalizedTextGetter.GetMessageData(key); }, key);
        }

        private static string DifficultyName(string id)
        {
            if (id == null) { return ""; }
            return Safe(delegate
            {
                // 本体の名前は「1. New Customer」のように頭に番号が付く。一覧では並べ替えの用を成さず幅だけ食うので外す
                // （2026-11-18 ユーザー判断。Inferno1 のように番号が名前の一部のものは、そのまま）
                string name = StripRichText(LocalizedTextGetter.GetDifficultyName((Difficulty)Enum.Parse(typeof(Difficulty), id)));
                return System.Text.RegularExpressions.Regex.Replace(name, @"^\s*\d+\s*[.．]\s*", "");
            }, id);
        }

        private static string MissionName(string id)
        {
            return Safe(delegate { return StripRichText(LocalizedTextGetter.GetSpecialMissionData(id)); }, id);
        }

        private static string TaxName(string id)
        {
            if (id == null) { return ""; }
            return Safe(delegate { return StripRichText(LocalizedTextGetter.GetTaxData(id)); }, id);
        }

        private static string PatronName(string id)
        {
            return Safe(delegate
            {
                return StripRichText(LocalizedTextGetter.GetPatronName((Patron)Enum.Parse(typeof(Patron), id)));
            }, id);
        }

        private static string CrafterName(string id)
        {
            if (id == null) { return ""; }
            return Safe(delegate
            {
                return StripRichText(LocalizedTextGetter.GetCharacterData((CrafterKind)Enum.Parse(typeof(CrafterKind), id)));
            }, id);
        }

        private static Sprite PatronSprite(string id)
        {
            try { return Patrons.GetFaceIcon((Patron)Enum.Parse(typeof(Patron), id)); }
            catch (Exception) { return null; }
        }

        private static string PactName(string id)
        {
            if (id == null) { return ""; }
            return Safe(delegate
            {
                // 名前のキーは契約の ID と同じとは限らないので、本体の表から引く
                string nameKey = id;
                PactPropertyDefinition property;
                if (PactMaster.IsInitialized && PactMaster.TryGetProperty(id, out property)) { nameKey = property.NameKey; }
                return StripRichText(PactNameFormatter.GetDisplayName(id, nameKey));
            }, id);
        }

        private static string AnyName(string id)
        {
            if (id == null) { return ""; }
            return Safe(delegate
            {
                return StripRichText(TagParamGetter.HasTag(id) ? TagParamGetter.GetName(id) : ItemParamGetter.GetName(id));
            }, id);
        }

        /// <summary>レシピが作る物の ID。レシピ ID はアイテムでも資源でもなく、同じ綴りの資源があると取り違える
        /// （召喚士ウィッチの「Magic」はマジックペレットを作るレシピで、資源の魔力ではない）。引けなければ null。</summary>
        private static string RecipeResult(string recipeID)
        {
            if (recipeID == null) { return null; }
            try
            {
                RecipeDefinition definition;
                if (!RecipeDatabase.TryGetDefinition(recipeID, out definition)) { return null; }
                if (definition == null || definition.Result == null) { return null; }
                return string.IsNullOrEmpty(definition.Result.Key) ? null : definition.Result.Key;
            }
            catch (Exception) { return null; }
        }

        private static Sprite RecipeSprite(string recipeID)
        {
            return AnySprite(RecipeResult(recipeID) ?? recipeID);
        }

        private static string RecipeName(string recipeID)
        {
            return AnyName(RecipeResult(recipeID) ?? recipeID);
        }

        private static Sprite AnySprite(string id)
        {
            if (id == null) { return null; }
            try { return TagParamGetter.HasTag(id) ? TagParamGetter.GetSprite(id) : ItemParamGetter.GetSprite(id); }
            catch (Exception) { return null; }
        }

        /// <summary>ゲームの名前には色や下線の指定が付く。行では邪魔なので外す。</summary>
        private static string StripRichText(string s)
        {
            if (string.IsNullOrEmpty(s)) { return s; }
            System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
            bool inTag = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) { sb.Append(c); }
            }
            return sb.ToString();
        }

        private static string Safe(Func<string> get, string fallback)
        {
            try
            {
                string s = get();
                return string.IsNullOrEmpty(s) ? fallback : s;
            }
            catch (Exception) { return fallback; }
        }

        // ---------------------------------------------------------------
        // 部品
        // ---------------------------------------------------------------

        private static RectTransform Rect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform r = go.GetComponent<RectTransform>();
            r.SetParent(parent, false);
            return r;
        }

        private static void Stretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero;
            r.offsetMax = Vector2.zero;
        }

        private static RectTransform Row(RectTransform parent, int height)
        {
            RectTransform r = Rect("Row", parent);
            HorizontalLayoutGroup layout = r.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.padding = new RectOffset(6, 6, 0, 0);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.MiddleLeft;
            LayoutElement le = r.gameObject.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            return r;
        }

        private static TextMeshProUGUI NewText(Transform parent, string value, int size, Color color)
        {
            RectTransform r = Rect("Text", parent);
            TextMeshProUGUI t = r.gameObject.AddComponent<TextMeshProUGUI>();
            if (_font != null) { t.font = _font; }
            t.text = value ?? "";
            t.fontSize = size;
            t.color = color;
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            return t;
        }

        /// <summary>行に文字を1つ置く。width が正なら決め打ちの幅、0 なら字の幅、負なら余りを全部もらう。</summary>
        private static TextMeshProUGUI Cell(RectTransform row, string value, int size, Color color, float width, bool right)
        {
            TextMeshProUGUI t = NewText(row, value, size, color);
            if (right) { t.alignment = TextAlignmentOptions.MidlineRight; }
            if (width == 0f)
            {
                // 幅を字に任せる欄。行が混むと、字の欄から先に押しつぶされて「…」になる（最小の幅が 0 のため）。
                // 字の幅をそのまま最小の幅にして、縮めさせない。入りきらない分は行の外へはみ出す
                if (!string.IsNullOrEmpty(value))
                {
                    LayoutElement natural = t.gameObject.AddComponent<LayoutElement>();
                    float w = Mathf.Ceil(TextWidth(value, size, FontStyles.Normal));
                    natural.minWidth = w;
                    natural.preferredWidth = w;
                }
                return t;
            }
            LayoutElement le = t.gameObject.AddComponent<LayoutElement>();
            if (width > 0f)
            {
                le.minWidth = width;
                le.preferredWidth = width;
                // 幅に入りきらない語は、まず字を小さくして収める（「…」で切るのは、それでも入らないときだけ）。
                // 欄の幅は日本語で決めてあり、ほかの言語では同じ語が倍の長さになる
                // （「返済回数」は英語で Repayment Frequency、ロシア語で Количество погашений）。
                // 入りきる語には掛けない（字の大きさを探る計算が、一覧の全部の欄に乗ってしまう）
                if (!string.IsNullOrEmpty(value) && TextWidth(value, size, FontStyles.Normal) > width)
                {
                    t.enableAutoSizing = true;
                    t.fontSizeMax = size;
                    t.fontSizeMin = Mathf.Max(10f, size * 0.5f);
                }
            }
            else
            {
                le.minWidth = 0f;
                le.preferredWidth = 0f;
                le.flexibleWidth = 1f;
            }
            return t;
        }

        // 字の幅を測るための、画面に出さない字の部品。
        // 測りたい字そのもの（t.GetPreferredValues）で測ると、その部品がまだ隠れている親の下にあるときに 0 が返る
        // （TextMeshPro は、有効になるまで書体を読み込まない）。Run の上の段は隠れたページの中で組むので、
        // 全部の欄の幅が 0 になり、字が消えた。いつも有効な根の下に物差しを1つ置いて、それで測る
        private static TextMeshProUGUI _ruler;

        private static float TextWidth(string value, float size, FontStyles style)
        {
            if (string.IsNullOrEmpty(value) || _root == null) { return 0f; }
            if (_ruler == null)
            {
                _ruler = NewText(_root.transform, "", Font, new Color(0f, 0f, 0f, 0f));
                _ruler.gameObject.name = "Ruler";
                _ruler.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                _ruler.rectTransform.sizeDelta = Vector2.zero;
            }
            _ruler.fontSize = size;
            _ruler.fontStyle = style;
            return _ruler.GetPreferredValues(value).x;
        }

        /// <summary>字の見た目（太字など）を変えた後に、幅を字に任せた欄の幅を測り直す。</summary>
        private static void Refit(TextMeshProUGUI t)
        {
            LayoutElement le = t.GetComponent<LayoutElement>();
            if (le == null) { return; }
            float w = Mathf.Ceil(TextWidth(t.text, t.fontSize, t.fontStyle)) + 2f;
            le.minWidth = w;
            le.preferredWidth = w;
        }

        /// <summary>絵を1つ置く。絵が無くても同じ幅を空ける（列がずれない）。</summary>
        private static void Icon(RectTransform row, Sprite sprite, int size)
        {
            RectTransform r = Rect("Icon", row);
            if (sprite != null)
            {
                Image image = r.gameObject.AddComponent<Image>();
                image.sprite = sprite;
                image.preserveAspect = true;
                image.raycastTarget = false;
            }
            LayoutElement le = r.gameObject.AddComponent<LayoutElement>();
            le.minWidth = size;
            le.preferredWidth = size;
            le.preferredHeight = size;
        }

        private static void Gap(RectTransform row, float width)
        {
            LayoutElement le = Rect("Gap", row).gameObject.AddComponent<LayoutElement>();
            le.minWidth = width;
            le.preferredWidth = width;
        }

        private static void Flex(RectTransform row)
        {
            Rect("Flex", row).gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        }
    }
}
