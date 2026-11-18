// 業務日誌の画面の続き（partial）：一覧の行の手触り（縞・カーソルを載せた行の強調）、
// 載せた Run のあらまし（右の枠）、行を隠す仕組み。
//
// 隠すのは一覧に出さないだけで、記録のファイルは消さない（本体の記録も写しも触らない）。
// 隠した Run の名前は runs/hidden.txt に1行ずつ持つ。「×」の絞り込みを入れると隠した行も薄く出て、
// 行の「+」で戻せる。「隠す」に当たる語が本体の言語表に無いので、印だけで出している。

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LwfEconomyGraph
{
    internal static partial class LogView
    {
        private const float PreviewWidth = 640f;
        private const int PreviewIcon = 52;

        private static readonly Color StripeColor = new Color(1f, 1f, 1f, 0.035f);
        private static readonly Color HoverColor = new Color(1f, 1f, 1f, 0.16f);

        private static RectTransform _preview;
        private static RunSummary _previewRun;
        private static HashSet<string> _hidden;

        // ---------------------------------------------------------------
        // 隠した Run
        // ---------------------------------------------------------------

        private static string HiddenPath()
        {
            return Path.Combine(RunData.CopyDirectory(), "hidden.txt");
        }

        private static bool IsHidden(RunSummary run)
        {
            if (_hidden == null)
            {
                _hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(HiddenPath()))
                    {
                        foreach (string line in File.ReadAllLines(HiddenPath()))
                        {
                            if (line.Trim().Length > 0) { _hidden.Add(line.Trim()); }
                        }
                    }
                }
                catch (Exception e) { EconomyGraphPlugin.Log.LogWarning("[view] cannot read the hidden list: " + e.Message); }
            }
            return _hidden.Contains(Path.GetFileName(run.Path));
        }

        private static void SetHidden(RunSummary run, bool hidden)
        {
            IsHidden(run);
            string name = Path.GetFileName(run.Path);
            if (hidden) { _hidden.Add(name); } else { _hidden.Remove(name); }
            try
            {
                Directory.CreateDirectory(RunData.CopyDirectory());
                File.WriteAllLines(HiddenPath(), new List<string>(_hidden).ToArray());
            }
            catch (Exception e) { EconomyGraphPlugin.Log.LogWarning("[view] cannot save the hidden list: " + e.Message); }
        }

        // ---------------------------------------------------------------
        // スポンサーで絞る。絞り込みの段に、記録に出てくるスポンサーの顔を並べる。押した顔のスポンサーが
        // 全員いる Run だけにする（2人押せば、その2人が両方いる Run）。もう一度押すと外れる。
        // 選んだ状態は画面を閉じるまで（設定には残さない）
        // ---------------------------------------------------------------
        private static readonly HashSet<string> _patronFilter = new HashSet<string>(StringComparer.Ordinal);

        private const int MaxPatronFilter = 3;

        private static bool HasPatrons(RunSummary run)
        {
            if (_patronFilter.Count == 0) { return true; }
            foreach (string wanted in _patronFilter)
            {
                if (run.Patrons == null || Array.IndexOf(run.Patrons, wanted) < 0) { return false; }
            }
            return true;
        }

        private static void AddPatronFilters()
        {
            // 記録に出てくるスポンサーを、本体の並び（Patron の番号）で
            List<string> ids = new List<string>();
            for (int i = 0; i < _runs.Count; i++)
            {
                if (_runs[i].Patrons == null) { continue; }
                foreach (string id in _runs[i].Patrons)
                {
                    if (!string.IsNullOrEmpty(id) && !ids.Contains(id)) { ids.Add(id); }
                }
            }
            if (ids.Count == 0) { return; }
            ids.Sort(delegate(string a, string b) { return PatronOrder(a).CompareTo(PatronOrder(b)); });

            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                bool on = _patronFilter.Contains(id);
                RectTransform r = Rect("Patron", _filterRow);
                LayoutElement le = r.gameObject.AddComponent<LayoutElement>();
                le.minWidth = 34f;
                le.preferredWidth = 34f;
                le.minHeight = 34f;
                le.preferredHeight = 34f;
                Image face = r.gameObject.AddComponent<Image>();
                face.sprite = PatronSprite(id);
                face.preserveAspect = true;
                // 選んでいない顔は薄く。1つも選んでいないときは、全部ふつうの濃さ（絞っていない）
                // 1つの Run のスポンサーは3人まで。3人選んだら、ほかの顔は押せなくする（選べると必ず 0 件になる）
                bool full = !on && _patronFilter.Count >= MaxPatronFilter;
                face.color = new Color(1f, 1f, 1f, on || _patronFilter.Count == 0 ? 1f : (full ? 0.12f : 0.3f));
                if (on)
                {
                    RectTransform line = Rect("On", r);
                    line.anchorMin = new Vector2(0f, 0f);
                    line.anchorMax = new Vector2(1f, 0f);
                    line.pivot = new Vector2(0.5f, 1f);
                    line.anchoredPosition = new Vector2(0f, 1f);
                    line.sizeDelta = new Vector2(0f, 3f);
                    Image li = line.gameObject.AddComponent<Image>();
                    li.color = HeadColor;
                    li.raycastTarget = false;
                }
                if (full) { face.raycastTarget = false; continue; }
                Button button = r.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.targetGraphic = face;
                string captured = id;
                button.onClick.AddListener(delegate
                {
                    if (!_patronFilter.Remove(captured)) { _patronFilter.Add(captured); }
                    _listPage = 0;
                    RefreshList();
                });
            }
        }

        private static int PatronOrder(string id)
        {
            try { return (int)Enum.Parse(typeof(BaseSystem.Patron), id); }
            catch (Exception) { return int.MaxValue; }
        }

        // ---------------------------------------------------------------
        // お気に入り（星）。印を付けた Run の名前は runs/favorites.txt に1行ずつ持つ（隠す、と同じ持ち方）
        // ---------------------------------------------------------------
        private static readonly Dictionary<string, JObject> _previewJson = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> _favorites;
        private static readonly Color32 StarColor = new Color32(255, 205, 70, 255);

        private static string FavoritesPath()
        {
            return Path.Combine(RunData.CopyDirectory(), "favorites.txt");
        }

        private static bool IsFavorite(RunSummary run)
        {
            if (_favorites == null)
            {
                _favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(FavoritesPath()))
                    {
                        foreach (string line in File.ReadAllLines(FavoritesPath()))
                        {
                            if (line.Trim().Length > 0) { _favorites.Add(line.Trim()); }
                        }
                    }
                }
                catch (Exception e) { EconomyGraphPlugin.Log.LogWarning("[view] cannot read the favorites: " + e.Message); }
            }
            return _favorites.Contains(Path.GetFileName(run.Path));
        }

        private static void SetFavorite(RunSummary run, bool favorite)
        {
            IsFavorite(run);
            string name = Path.GetFileName(run.Path);
            if (favorite) { _favorites.Add(name); } else { _favorites.Remove(name); }
            try
            {
                Directory.CreateDirectory(RunData.CopyDirectory());
                File.WriteAllLines(FavoritesPath(), new List<string>(_favorites).ToArray());
            }
            catch (Exception e) { EconomyGraphPlugin.Log.LogWarning("[view] cannot save the favorites: " + e.Message); }
        }

        /// <summary>行の頭の星。押すと付ける・外す。付いていれば金色、無ければ薄い。字（★）は書体に無い言語が
        /// あるので絵で描く。</summary>
        private static void StarButton(RectTransform row, RunSummary run, bool favorite)
        {
            RectTransform r = Rect("Star", row);
            LayoutElement le = r.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 30f;
            le.preferredWidth = 30f;
            le.minHeight = 30f;
            le.preferredHeight = 30f;
            MapMesh mesh = r.gameObject.AddComponent<MapMesh>();
            mesh.Quad(0f, 0f, 30f, 30f, new Color32(255, 255, 255, 0));   // 押せる面
            mesh.Star(new Vector2(15f, 15f), 11f, 4.6f, favorite ? StarColor : new Color32(255, 255, 255, 50));
            Button button = r.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = mesh;
            RunSummary captured = run;
            bool to = !favorite;
            button.onClick.AddListener(delegate { SetFavorite(captured, to); RefreshList(); });
        }

        /// <summary>絞り込みの段の区切り（縦線）。左の □ と、右の抜き出し（星・スポンサー）を分ける。</summary>
        private static void FilterDivider()
        {
            Gap(_filterRow, 6f);
            RectTransform r = Rect("Divider", _filterRow);
            LayoutElement le = r.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 2f;
            le.preferredWidth = 2f;
            le.minHeight = 30f;
            le.preferredHeight = 30f;
            Image line = r.gameObject.AddComponent<Image>();
            line.color = new Color(1f, 1f, 1f, 0.25f);
            line.raycastTarget = false;
            Gap(_filterRow, 18f);
        }

        /// <summary>星を付けた Run だけにする切り替え。星そのものを押す（金色＝入、薄い＝切）。
        /// スポンサーの顔と同じく、入のときは下線。</summary>
        private static void StarToggle()
        {
            bool on = EconomyGraphPlugin.OnlyFavorites.Value;
            RectTransform r = Rect("StarToggle", _filterRow);
            LayoutElement le = r.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 36f;
            le.preferredWidth = 36f;
            le.minHeight = 36f;
            le.preferredHeight = 36f;
            MapMesh mesh = r.gameObject.AddComponent<MapMesh>();
            mesh.Quad(0f, 0f, 36f, 36f, new Color32(255, 255, 255, 0));   // 押せる面
            Color32 color = StarColor;
            if (!on) { color.a = 80; }
            mesh.Star(new Vector2(18f, 17f), 14f, 5.8f, color);
            if (on) { mesh.Quad(0f, 34f, 36f, 3f, HeadColor); }
            Button button = r.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = mesh;
            button.onClick.AddListener(delegate
            {
                EconomyGraphPlugin.OnlyFavorites.Value = !EconomyGraphPlugin.OnlyFavorites.Value;
                _listPage = 0;
                RefreshList();
            });
            Gap(_filterRow, 10f);
        }

        /// <summary>抜き出し（星・スポンサー）を全部外すボタン。何も絞っていないときは薄くして押せなくする。
        /// 絵は「ぐるっと回って戻る矢印」。語（本体の「デフォルトに戻す」）だと何を戻すのか伝わらなかった。</summary>
        private static void ResetFilters()
        {
            bool any = EconomyGraphPlugin.OnlyFavorites.Value || _patronFilter.Count > 0;
            Gap(_filterRow, 10f);
            RectTransform r = Rect("Reset", _filterRow);
            LayoutElement le = r.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 36f;
            le.preferredWidth = 36f;
            le.minHeight = 36f;
            le.preferredHeight = 36f;
            MapMesh mesh = r.gameObject.AddComponent<MapMesh>();
            Color32 mark = TextColor;
            if (!any) { mark.a = 60; }
            mesh.Quad(0f, 0f, 36f, 36f, new Color32(255, 255, 255, any ? (byte)24 : (byte)8));
            // 輪（上の右寄りを切り欠く）。角度は画面の向き（y は下向き）で、真上が -90 度
            Vector2 mid = new Vector2(18f, 19f);
            const float radius = 10f;
            const int steps = 14;
            float from = -50f * Mathf.Deg2Rad, to = 230f * Mathf.Deg2Rad;
            Vector2 before = mid + new Vector2(Mathf.Cos(from), Mathf.Sin(from)) * radius;
            for (int i = 1; i <= steps; i++)
            {
                float angle = Mathf.Lerp(from, to, i / (float)steps);
                Vector2 next = mid + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                mesh.Line(before, next, 3.2f, mark);
                before = next;
            }
            // 矢じり（輪の終わり＝左上で、輪の進む向きを指す）
            Vector2 along = new Vector2(-Mathf.Sin(to), Mathf.Cos(to));
            Vector2 side = new Vector2(Mathf.Cos(to), Mathf.Sin(to));
            mesh.Triangle(before + along * 8f, before + side * 6f, before - side * 6f, mark);
            if (!any) { return; }
            Button button = r.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = mesh;
            button.onClick.AddListener(delegate
            {
                EconomyGraphPlugin.OnlyFavorites.Value = false;
                _patronFilter.Clear();
                _listPage = 0;
                RefreshList();
            });
        }

        /// <summary>行の右端の印。押すと隠す（隠した行では戻す）。行そのもののボタンより先に押下を受ける。</summary>
        private static void HideButton(RectTransform row, RunSummary run, bool hidden)
        {
            bool cross = _font != null && _font.HasCharacter('×', true);
            TextMeshProUGUI t = Cell(row, hidden ? "+" : (cross ? "×" : "x"), Font, DimColor, 30f, false);
            t.alignment = TextAlignmentOptions.Center;
            t.raycastTarget = true;
            Button button = t.gameObject.AddComponent<Button>();
            button.targetGraphic = t;
            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(1.6f, 1.6f, 1.6f, 1f);
            button.colors = colors;
            RunSummary captured = run;
            bool to = !hidden;
            button.onClick.AddListener(delegate { SetHidden(captured, to); RefreshList(); });
        }

        // ---------------------------------------------------------------
        // 送り（一覧が PageSize 行を超えたとき）
        // ---------------------------------------------------------------

        private const int PageSize = 100;
        private static int _listPage;

        /// <summary>絞り込みの段の右端に、前へ・「1/3」・次へ。</summary>
        private static void AddPager(int pages)
        {
            Flex(_filterRow);
            PageButton(-1, pages);
            Cell(_filterRow, (_listPage + 1) + "/" + pages, Font, TextColor, 60f, false).alignment = TextAlignmentOptions.Center;
            PageButton(1, pages);
        }

        private static void PageButton(int by, int pages)
        {
            RectTransform r = Rect("Page", _filterRow);
            LayoutElement le = r.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 34f;
            le.preferredWidth = 34f;
            le.minHeight = 30f;
            le.preferredHeight = 30f;
            int to = _listPage + by;
            bool usable = to >= 0 && to < pages;
            MapMesh mesh = r.gameObject.AddComponent<MapMesh>();
            mesh.Quad(0f, 0f, 34f, 30f, new Color32(255, 255, 255, 24));
            Color32 arrow = usable ? TextColor : new Color(1f, 1f, 1f, 0.2f);
            mesh.Arrow(2f, 0f, 30f, new Vector2(by, 0f), arrow);
            if (!usable) { return; }
            Button button = r.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = mesh;
            button.onClick.AddListener(delegate
            {
                _listPage = to;
                RefreshList();
                ScrollRect scroll = _listFrame != null ? _listFrame.GetComponent<ScrollRect>() : null;
                if (scroll != null) { scroll.verticalNormalizedPosition = 1f; }
            });
        }

        // ---------------------------------------------------------------
        // 行の強調
        // ---------------------------------------------------------------

        internal static void OnRowEnter(RowHover row)
        {
            if (row.Back != null) { row.Back.color = HoverColor; }
            ShowPreview(row.Run);
        }

        internal static void OnRowExit(RowHover row)
        {
            if (row.Back != null) { row.Back.color = row.Back == _selectedRow ? SelectedColor : row.Base; }
        }

        // ---------------------------------------------------------------
        // 載せた Run のあらまし
        // ---------------------------------------------------------------

        private static void BuildPreviewPane(RectTransform panel)
        {
            _preview = Rect("Preview", panel);
            _preview.anchorMin = new Vector2(1f, 0f);
            _preview.anchorMax = new Vector2(1f, 1f);
            _preview.pivot = new Vector2(1f, 0.5f);
            _preview.offsetMin = new Vector2(-24f - PreviewWidth, 24f);
            _preview.offsetMax = new Vector2(-24f, -150f);
            Image back = _preview.gameObject.AddComponent<Image>();
            back.color = new Color(1f, 1f, 1f, 0.04f);
            back.raycastTarget = false;
            _preview.gameObject.AddComponent<RectMask2D>();
            VerticalLayoutGroup column = _preview.gameObject.AddComponent<VerticalLayoutGroup>();
            column.padding = new RectOffset(12, 12, 10, 10);
            column.spacing = 4f;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;
            _previewRun = null;
        }

        /// <summary>カーソルを載せた Run のあらまし。取った契約（取った順）・いちばん多く動いていたクラフト・
        /// 収入の大きい出所。カーソルを外しても、次の行に載せるまで残す。</summary>
        private static void ShowPreview(RunSummary run)
        {
            if (_preview == null || run == null || run == _previewRun) { return; }
            _previewRun = run;
            Clear(_preview);
            try
            {
                // 行に載せるたびにファイルを読み直さない（一覧を上から下へなぞると、行ごとに数十 KB を読んでいた）
                JObject json;
                if (!_previewJson.TryGetValue(run.Path, out json))
                {
                    if (_previewJson.Count >= 300) { _previewJson.Clear(); }
                    json = RunData.Read(run.Path);
                    _previewJson[run.Path] = json;
                }

                RectTransform head = Row(_preview, 44);
                // 太字にすると字の幅が増える。Cell が測った幅（細字）のままだと足りずに「…」になるので、測り直す
                TextMeshProUGUI resultTitle = Cell(head, ResultText(run), 28, ResultColor(run), 0f, false);
                resultTitle.fontStyle = FontStyles.Bold;
                Refit(resultTitle);
                Gap(head, 12f);
                Cell(head, RunName(run), Font, TextColor, 0f, false);
                Cell(head, DifficultyName(run.Difficulty), Font, DimColor, 0f, false);

                RectTransform line = Row(_preview, RowHeight);
                Cell(line, "#" + run.Sequence, Font, DimColor, 0f, false);
                Cell(line, run.StartedLocal.ToString("yyyy-MM-dd HH:mm"), Font, DimColor, 0f, false);
                Gap(line, 12f);
                Cell(line, Clock(run.ElapsedSeconds), Font, TextColor, 0f, false);
                Gap(line, 12f);
                Cell(line, Message("Repayment"), Font, DimColor, 0f, false);
                Cell(line, Repayments(run), Font, TextColor, 0f, false);

                // スポンサーと、マップシードを写すボタン
                RectTransform third = Row(_preview, RowHeight);
                for (int i = 0; i < run.Patrons.Length; i++) { Icon(third, PatronSprite(run.Patrons[i]), IconSize + 6); }
                Flex(third);
                SeedButton(third, run, json);

                // 契約（取った順）
                List<JObject> pacts = Objects(json, "pactHistory");
                if (pacts.Count > 0)
                {
                    PreviewHead(Message("PactHistory") + "  " + pacts.Count);
                    const int perRow = 10;
                    RectTransform row = null;
                    for (int i = 0; i < pacts.Count; i++)
                    {
                        if (i % perRow == 0) { row = Row(_preview, PreviewIcon + 4); }
                        Icon(row, PactSprite(run, RunData.Str(pacts[i], "pactId")), PreviewIcon);
                    }
                }

                JObject stats = RunData.Obj(json, "statsSnapshot");

                // クラフト（終わったときに多く動いていた順）
                List<JObject> crafts = Objects(stats, "craftConfiguration").FindAll(delegate(JObject c)
                {
                    return RunData.Str(c, "recipeId") != null && RunData.Long(c, "activeCrafterCount") > 0;
                });
                crafts.Sort(delegate(JObject a, JObject b)
                {
                    return RunData.Long(b, "activeCrafterCount").CompareTo(RunData.Long(a, "activeCrafterCount"));
                });
                if (crafts.Count > 0)
                {
                    PreviewHead(Message("StatsTabCraft"));
                    RectTransform row = null;
                    for (int i = 0; i < crafts.Count && i < 12; i++)
                    {
                        if (i % 4 == 0) { row = Row(_preview, 50); }
                        string kind = RunData.Str(crafts[i], "crafterKind");
                        Chip(row, CrafterHead(kind) ?? CrafterSprite(kind), RecipeSprite(RunData.Str(crafts[i], "recipeId")),
                            "x" + RunData.Long(crafts[i], "activeCrafterCount").ToString("N0"), 144f);
                    }
                }

                // 返済に現金以外を使う Run（特別任務）は、現金の出所ではなく、返済に使った資源・アイテムごとの
                // 入った量を返済の順に並べる。何で返すかは任務の名前ではなく、その Run の返済の記録（repaymentMilestones）
                // から取るので、任務や版で資源が変わっても追従する。本体の記録には、現金以外の出所ごとの内訳は無い
                List<string> paid = new List<string>();
                List<bool> paidIsItem = new List<bool>();
                bool otherThanCash = false;
                foreach (JObject m in Objects(json, "repaymentMilestones"))
                {
                    string id = RunData.Str(m, "requirementId");
                    if (string.IsNullOrEmpty(id) || paid.Contains(id)) { continue; }
                    paid.Add(id);
                    paidIsItem.Add(RunData.Str(m, "requirementType") == "Item");
                    if (id != "Cash") { otherThanCash = true; }
                }
                if (otherThanCash)
                {
                    PreviewHead(Message("Income"));
                    RectTransform row = null;
                    int shown = 0;
                    for (int i = 0; i < paid.Count && shown < 8; i++)
                    {
                        long amount = -1;
                        if (paidIsItem[i])
                        {
                            foreach (JObject item in Objects(stats, "items"))
                            {
                                if (RunData.Str(item, "itemId") == paid[i]) { amount = RunData.Long(item, "producedTotal"); break; }
                            }
                        }
                        else
                        {
                            foreach (JObject resource in Objects(stats, "resources"))
                            {
                                if (RunData.Str(resource, "tagId") == paid[i]) { amount = RunData.Long(resource, "addedTotal"); break; }
                            }
                        }
                        if (amount < 0) { continue; }
                        if (shown % 4 == 0) { row = Row(_preview, 50); }
                        shown++;
                        Chip(row, AnySprite(paid[i]), null, Short(amount), 144f);
                    }
                    return;
                }

                // 収入（大きい順）
                List<JObject> income = Objects(stats, "cashFlows").FindAll(delegate(JObject f)
                {
                    string kind = RunData.Str(f, "kind");
                    return kind != null && kind.StartsWith("Income", StringComparison.Ordinal) && RunData.Long(f, "totalAmount") > 0;
                });
                income.Sort(delegate(JObject a, JObject b)
                {
                    return RunData.Long(b, "totalAmount").CompareTo(RunData.Long(a, "totalAmount"));
                });
                if (income.Count > 0)
                {
                    PreviewHead(Message("Income"));
                    RectTransform row = null;
                    for (int i = 0; i < income.Count && i < 8; i++)
                    {
                        if (i % 4 == 0) { row = Row(_preview, 50); }
                        string source = RunData.Str(income[i], "sourceId");
                        string kind = RunData.Str(income[i], "kind");
                        if (source == null && kind.IndexOf("Order", StringComparison.Ordinal) >= 0) { source = "FasterOrder"; }
                        Chip(row, AnySprite(source), null, Short(RunData.Long(income[i], "totalAmount")), 144f);
                    }
                }
            }
            catch (Exception e) { EconomyGraphPlugin.Log.LogWarning("[view] cannot preview #" + run.Sequence + ": " + e.Message); }
        }

        private static void PreviewHead(string title)
        {
            Gap(Row(_preview, 6), 1f);
            Cell(Row(_preview, 32), title, SmallFont, HeadColor, 0f, false);
        }

        /// <summary>小さい札。「[絵][絵] 字」。2つ目の絵は無ければ詰める。</summary>
        private static void Chip(RectTransform row, Sprite first, Sprite second, string text, float width)
        {
            RectTransform chip = Rect("Chip", row);
            LayoutElement le = chip.gameObject.AddComponent<LayoutElement>();
            le.minWidth = width;
            le.preferredWidth = width;
            le.flexibleWidth = 0f;
            le.minHeight = 44f;
            le.preferredHeight = 44f;
            Image back = chip.gameObject.AddComponent<Image>();
            back.color = new Color(1f, 1f, 1f, 0.08f);
            back.raycastTarget = false;
            HorizontalLayoutGroup layout = chip.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(6, 8, 0, 0);
            layout.spacing = 4f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.MiddleLeft;
            Icon(chip, first, 36);
            if (second != null) { Icon(chip, second, 36); }
            Flex(chip);
            Cell(chip, text, SmallFont, TextColor, 0f, true);
        }
    }

    /// <summary>一覧の行。カーソルが載った・外れたを画面に伝える。</summary>
    internal sealed class RowHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Image Back;
        public Color Base;
        public RunSummary Run;

        public void OnPointerEnter(PointerEventData e) { LogView.OnRowEnter(this); }
        public void OnPointerExit(PointerEventData e) { LogView.OnRowExit(this); }
    }
}
