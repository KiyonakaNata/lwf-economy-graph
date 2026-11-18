// 業務日誌の画面の続き（partial）：課税通知書。課税ごとに、取られた分とその内訳を出す。
//
// 数えているのは Tax Ledger（mods/tax_ledger）で、Run ごとに `<開始時刻>.tax.txt` を
// グラフと同じフォルダへ書く。形は tax_ledger/LedgerFile.cs の冒頭コメント。ここはそれを読むだけで、
// Tax Ledger が入っていない・その Run に控えが無いときは、期と課税の名前だけになる。
//
// 並べ方は Tax Ledger の結果画面の欄（tax_ledger/LedgerView.cs の BuildRows・AddHeadValue）と同じにしてある。
//   見出しの行: 期・課税の名前・（対象のスポンサー）・その課税の合計
//   内訳の行:   減ったアイテムごとに「絵 x個数・現金以外の資源の換算・返済に使う資源の換算」
//               返済にならないバフやレリックの減り、資源が多い課税（徴発・源泉徴収）の資源ごとの額
// 見出しの行を押すと内訳を畳める（左端の印が - と +）。
// 最初は合計を1行に並べるだけで、アイテムの個数を種類をまたいで足した数（x946 など）を出していた。
// 意味の無い数で、何がどれだけ減ったかも分からないので、Tax Ledger と同じ形に作り直した（2026-11-18）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BaseSystem;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LwfEconomyGraph
{
    internal static partial class LogView
    {
        internal const string TaxSuffix = ".tax.txt";
        private const string FixedCostTag = "RepaymentLoseCash";
        private const string TaxHitItem = "Pickaxe";
        private const string TaxConveyorItem = "Summon-Conveyor";

        /// <summary>内訳に並べる資源の順（Tax Ledger と同じ）。行ごとに順が変わると読めなくなる</summary>
        private static readonly string[] TaxResourceOrder = new string[]
        {
            "Construction", "Grocery", "Luxury", "Fertilizer", "Chemical", "Magic", "Fuel"
        };

        /// <summary>内訳の行に並べる、返済に使う資源以外の資源の枠の数。超える課税は資源ごとに1行ずつ出す</summary>
        private const int TaxResourceSlots = 2;
        private const float TaxNumberWidth = 120f;
        private const float TaxRightPad = 520f;   // 行は全幅なので、右を空けて数字を名前の近くに寄せる
        private const int TaxSubFont = 19;
        private const int TaxSubIcon = 26;

        private sealed class TaxItem
        {
            public string Id;
            public int Count;
            public readonly Dictionary<string, int> Resources = new Dictionary<string, int>();
        }

        private sealed class TaxLoss
        {
            public int Points, CancelledSales, ExtraHits;
            public int Patron = -1;
            public string RateText;
            public readonly Dictionary<string, int> Resources = new Dictionary<string, int>();   // 消えた資源
            public readonly Dictionary<string, int> Tags = new Dictionary<string, int>();        // 減ったバフ・レリック
            public readonly List<TaxItem> Items = new List<TaxItem>();                           // 減ったアイテム
        }

        // 第N期に入っている課税。本体の記録（run.json の taxSlots）は Run の始まりの並びで、Run の途中で順番を
        // 入れ替えても変わらない。控え（.tax.txt）には、終わった時点の並び（order の行）と、実際に発動した課税
        // （entry の行。期の番号つき）がある。あるものは控えを優先する：発動した課税 ＞ 終わりの並び ＞ 本体の記録
        private static string _taxSlotsFor;
        private static readonly Dictionary<int, string> _taxSlots = new Dictionary<int, string>();

        /// <summary>その期に入っていた課税の ID。</summary>
        private static string TaxEffect(JObject slot)
        {
            string path = _run != null ? _run.Path : "";
            if (_taxSlotsFor != path)
            {
                _taxSlotsFor = path;
                _taxSlots.Clear();
                string file = _run != null ? EconomyGraphPlugin.FindRunFile(_run, TaxSuffix) : null;
                try
                {
                    if (file != null)
                    {
                        string[] lines = File.ReadAllLines(file);
                        foreach (string line in lines)
                        {
                            string[] f = line.Split('\t');
                            if (f[0] != "order") { continue; }
                            for (int i = 1; i < f.Length; i++) { if (f[i].Length > 0) { _taxSlots[i - 1] = f[i]; } }
                        }
                        foreach (string line in lines)
                        {
                            string[] f = line.Split('\t');
                            if (f[0] == "entry" && f.Length >= 3 && f[2].Length > 0) { _taxSlots[Int(f[1])] = f[2]; }
                        }
                    }
                }
                catch (Exception) { _taxSlots.Clear(); }

                // 終わりの並び（order の行）が無い控え（2026-11-18 より前に書いたもの）で、発動した課税が本体の記録と
                // 食い違うとき。残りの期は、本体の記録にある課税のうちまだ出ていないものを、記録の順に詰める
                // （入れ替えた相手がそこへ移った、と見る。同じ課税が2つの期に並ぶのを防ぐ）
                if (_taxSlots.Count > 0)
                {
                    List<JObject> slots = Objects(_json, "taxSlots");
                    List<string> left = new List<string>();
                    foreach (JObject recorded in slots)
                    {
                        string id = RunData.Str(recorded, "effectId");
                        if (id != null && !_taxSlots.ContainsValue(id)) { left.Add(id); }
                    }
                    foreach (JObject recorded in slots)
                    {
                        int index = (int)RunData.Long(recorded, "slotIndex");
                        if (_taxSlots.ContainsKey(index) || left.Count == 0) { continue; }
                        _taxSlots[index] = left[0];
                        left.RemoveAt(0);
                    }
                }
            }
            string effect;
            return _taxSlots.TryGetValue((int)RunData.Long(slot, "slotIndex"), out effect) ? effect : RunData.Str(slot, "effectId");
        }

        /// <summary>控えを読む。課税の ID ごと。無ければ空。</summary>
        private static Dictionary<string, TaxLoss> LoadTaxes(string path, out string payTag)
        {
            payTag = "Cash";
            Dictionary<string, TaxLoss> taxes = new Dictionary<string, TaxLoss>();
            if (path == null) { return taxes; }
            try
            {
                TaxLoss current = null;
                TaxItem item = null;
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] f = line.Split('\t');
                    if (f.Length < 2) { continue; }
                    switch (f[0])
                    {
                        case "pay":
                            payTag = f[1];
                            break;
                        case "entry":
                            if (f.Length < 9) { break; }
                            current = new TaxLoss();
                            item = null;
                            current.Points = Int(f[3]);
                            current.CancelledSales = Int(f[4]);
                            current.Patron = Int(f[5]);
                            current.ExtraHits = Int(f[6]);
                            current.RateText = f[8].Length > 0 ? f[8] : null;
                            taxes[f[2]] = current;
                            break;
                        case "res":
                            if (current != null && f.Length >= 3) { Add(current.Resources, f[1], Int(f[2])); }
                            break;
                        case "tag":
                            if (current != null && f.Length >= 3) { Add(current.Tags, f[1], Int(f[2])); }
                            break;
                        case "item":
                            if (current != null && f.Length >= 3)
                            {
                                item = new TaxItem();
                                item.Id = f[1];
                                item.Count = Int(f[2]);
                                current.Items.Add(item);
                            }
                            break;
                        case "itemres":
                            if (item != null && f.Length >= 3) { Add(item.Resources, f[1], Int(f[2])); }
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                EconomyGraphPlugin.Log.LogWarning("[view] cannot read " + Path.GetFileName(path) + ": " + e.Message);
                taxes.Clear();
            }
            return taxes;
        }

        private static void Add(Dictionary<string, int> into, string key, int amount)
        {
            int have;
            into.TryGetValue(key, out have);
            into[key] = have + amount;
        }

        private static int Int(string s)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static int Amount(Dictionary<string, int> from, string key)
        {
            int v;
            from.TryGetValue(key, out v);
            return v;
        }

        // ---------------------------------------------------------------
        // 描く
        // ---------------------------------------------------------------

        private static void BuildTaxes()
        {
            string payTag;
            Dictionary<string, TaxLoss> losses = LoadTaxes(EconomyGraphPlugin.FindRunFile(_run, TaxSuffix), out payTag);
            bool first = true;
            foreach (JObject t in Objects(_json, "taxSlots"))
            {
                if (!first) { TaxSeparator(); }
                first = false;

                string effect = TaxEffect(t);
                TaxLoss loss = null;
                if (effect != null) { losses.TryGetValue(effect, out loss); }
                // その期まで届かずに Run が終わった課税は効いていない（本体の記録に発動の時刻が無い）。
                // 名前を薄くし、値は「-」だけにする。何が設定されていたかは分かるよう、行は残す
                bool applied = RunData.Has(t, "activatedAtElapsedGameplaySeconds");
                if (!applied) { loss = null; }

                RectTransform head = Row(_detail, 48);
                TextMeshProUGUI marker = Cell(head, " ", Font, DimColor, 22f, false);
                marker.alignment = TextAlignmentOptions.Center;
                // いつ効いたかは、時刻ではなく本体と同じ「第N期」で（効く時刻は期ごとに決まっている）
                Cell(head, Message("TaxSection" + RunData.Long(t, "slotIndex")), Font, DimColor, 110f, false);
                Cell(head, TaxName(effect), Font, applied ? TextColor : DimColor, 0f, false);
                Flex(head);
                // 狙われたスポンサーの顔は、右の数字の手前に置く（名前の横だと、顔が大きい分だけ左に寄って見える）
                if (loss != null && loss.Patron > 0)
                {
                    // 発動時の顔は、表情が読めるよう絵の元の大きさで出す（上限 128）。行の高さも顔に合わせて広げる。
                    // ふつうの顔アイコン（Run に入る前）は、今までの大きさ
                    Sprite face = TaxDialogueFace(effect, loss.Patron);
                    int size = IconSize;
                    if (face != null && DialogueFaces.ContainsValue(face))
                    {
                        size = Mathf.Clamp(Mathf.RoundToInt(face.rect.height), 64, 128);   // 本体の絵は 100x100
                        LayoutElement tall = head.GetComponent<LayoutElement>();
                        tall.minHeight = size + 8;
                        tall.preferredHeight = size + 8;
                    }
                    Icon(head, face, size);
                }
                if (loss != null) { TaxHeadValues(head, loss, payTag, effect); }
                else if (!applied) { TaxNumber(head, null, "-", Font, IconSize, DimColor); }
                Gap(head, TaxRightPad);

                if (loss == null) { continue; }
                List<GameObject> breakdown = new List<GameObject>();

                // 減ったアイテムごと
                for (int i = 0; i < loss.Items.Count; i++)
                {
                    TaxItem item = loss.Items[i];
                    RectTransform row = TaxSubRow();
                    Icon(row, AnySprite(item.Id), TaxSubIcon);
                    Cell(row, "x" + item.Count.ToString("N0"), TaxSubFont, TextColor, 0f, false);
                    Cell(row, AnyName(item.Id), TaxSubFont, DimColor, 0f, false);
                    Flex(row);
                    TaxOtherResources(row, item.Resources, payTag);
                    TaxNumber(row, AnySprite(payTag), "-" + Amount(item.Resources, payTag).ToString("N0"), TaxSubFont, TaxSubIcon, TextColor);
                    Gap(row, TaxRightPad);
                    breakdown.Add(row.gameObject);
                }

                // アイテムが減る課税で、資源そのものも減った分（製作数が減る課税での、燃料のレシピの分など）。
                // アイテムと同じ形の行にする。これが無いと、減った資源がどこにも出ない（見出しはアイテムの課税では
                // 返済の資源だけ）。返済の資源の欄は「-」
                if (loss.Items.Count > 0 && !TaxResourcesInBreakdown(loss))
                {
                    foreach (string tag in TaxResourceList(loss))
                    {
                        if (tag == payTag) { continue; }
                        Dictionary<string, int> own = new Dictionary<string, int>();
                        own[tag] = loss.Resources[tag];
                        RectTransform row = TaxSubRow();
                        Icon(row, AnySprite(tag), TaxSubIcon);
                        Cell(row, "x" + loss.Resources[tag].ToString("N0"), TaxSubFont, TextColor, 0f, false);
                        Cell(row, AnyName(tag), TaxSubFont, DimColor, 0f, false);
                        Flex(row);
                        TaxOtherResources(row, own, payTag);
                        TaxNumber(row, AnySprite(payTag), "-", TaxSubFont, TaxSubIcon, DimColor);
                        Gap(row, TaxRightPad);
                        breakdown.Add(row.gameObject);
                    }
                }

                // 返済にならないバフ・レリックの減り。返済の資源の欄は「-」にして、払った額と読み違えないようにする
                foreach (KeyValuePair<string, int> kv in loss.Tags)
                {
                    RectTransform row = TaxSubRow();
                    Icon(row, AnySprite(kv.Key), TaxSubIcon);
                    Cell(row, "x" + kv.Value.ToString("N0"), TaxSubFont, TextColor, 0f, false);
                    Cell(row, AnyName(kv.Key), TaxSubFont, DimColor, 0f, false);
                    Flex(row);
                    TaxOtherResources(row, null, payTag);
                    TaxNumber(row, AnySprite(payTag), "-", TaxSubFont, TaxSubIcon, DimColor);
                    Gap(row, TaxRightPad);
                    breakdown.Add(row.gameObject);
                }

                // 資源が多い課税（徴発・源泉徴収）は、資源ごとに1行ずつ
                if (TaxResourcesInBreakdown(loss))
                {
                    foreach (string tag in TaxResourceList(loss))
                    {
                        RectTransform row = TaxSubRow();
                        Cell(row, AnyName(tag), TaxSubFont, DimColor, 0f, false);
                        Flex(row);
                        TaxNumber(row, AnySprite(tag), "-" + loss.Resources[tag].ToString("N0"), TaxSubFont, TaxSubIcon, LoseColor);
                        Gap(row, TaxRightPad);
                        breakdown.Add(row.gameObject);
                    }
                }

                TaxExpandable(head, marker, breakdown);
            }
        }

        /// <summary>見出しの行の右側。課税の種類で出るものが違う（Tax Ledger の AddHeadValue と同じ決め）。</summary>
        private static void TaxHeadValues(RectTransform row, TaxLoss loss, string payTag, string effect)
        {
            int before = row.childCount;

            // 固定費用の点数（返済のたびにこの数だけ資源が減る）。払った額は消えた資源のほうに入っている
            if (loss.Points != 0) { TaxNumber(row, AnySprite(FixedCostTag), "+" + loss.Points.ToString("N0"), Font, IconSize, TextColor); }

            if (TaxResourcesInBreakdown(loss))
            {
                // 資源ごとの額は内訳にある。見出しには、返済に使う資源が減っていればその分だけ
                int pay = Amount(loss.Resources, payTag);
                if (pay != 0) { TaxNumber(row, AnySprite(payTag), "-" + pay.ToString("N0"), Font, IconSize, LoseColor); }
            }
            else
            {
                foreach (string tag in TaxResourceList(loss))
                {
                    // アイテムが減る課税（関税など）では、見出しは返済に使う資源だけにする。資源そのものが少し減った分
                    // （燃料のレシピの製作数が減った分など）を並べると、内訳のどの行とも合わない数字が見出しに出る
                    // （2026-11-18 ユーザー判断）
                    if (loss.Items.Count > 0 && tag != payTag) { continue; }
                    TaxNumber(row, AnySprite(tag), "-" + loss.Resources[tag].ToString("N0"), Font, IconSize, LoseColor);
                }
            }
            // 無効になった売却の回数
            if (loss.CancelledSales != 0) { TaxNumber(row, null, "x" + loss.CancelledSales.ToString("N0"), Font, IconSize, LoseColor); }
            // 増えた打数
            if (loss.ExtraHits != 0)
            {
                Cell(row, StripRichText(Message("RequireMiningHit")), SmallFont, DimColor, 0f, false);
                TaxNumber(row, AnySprite(TaxHitItem), "+" + loss.ExtraHits.ToString("N0"), Font, IconSize, LoseColor);
            }
            // 個数で測れないものは、課税が下げた率
            if (!string.IsNullOrEmpty(loss.RateText))
            {
                TaxNumber(row, effect == "SlowConveyor" ? AnySprite(TaxConveyorItem) : null, loss.RateText, Font, IconSize, LoseColor);
            }
            // 減ったアイテムの、返済に使う資源への換算の合計
            if (loss.Items.Count > 0)
            {
                int total = 0;
                for (int i = 0; i < loss.Items.Count; i++) { total += Amount(loss.Items[i].Resources, payTag); }
                TaxNumber(row, AnySprite(payTag), "-" + total.ToString("N0"), Font, IconSize, LoseColor);
            }
            // 何も出なかった行（まだ払っていない固定費用など）は空欄にしない
            if (row.childCount == before) { TaxNumber(row, null, "-", Font, IconSize, DimColor); }
        }

        private static bool TaxResourcesInBreakdown(TaxLoss loss)
        {
            return loss.Resources.Count > TaxResourceSlots;
        }

        /// <summary>消えた資源を決まった順（建材…燃料、最後に現金など）で返す。</summary>
        private static List<string> TaxResourceList(TaxLoss loss)
        {
            List<string> tags = new List<string>();
            for (int i = 0; i < TaxResourceOrder.Length; i++)
            {
                if (loss.Resources.ContainsKey(TaxResourceOrder[i])) { tags.Add(TaxResourceOrder[i]); }
            }
            foreach (KeyValuePair<string, int> kv in loss.Resources)
            {
                if (!tags.Contains(kv.Key)) { tags.Add(kv.Key); }
            }
            return tags;
        }

        /// <summary>内訳の行。見出しの課税の名前の下から始まるよう、左を空ける。</summary>
        private static RectTransform TaxSubRow()
        {
            RectTransform row = Row(_detail, 34);
            Gap(row, 22f + 8f + 110f);
            return row;
        }

        /// <summary>返済に使う資源以外の資源を、決まった順・決まった数の枠に並べる。枠が余れば空ける
        /// （行ごとに数が違っても、右端の返済の資源の位置が揃う）。</summary>
        private static void TaxOtherResources(RectTransform row, Dictionary<string, int> resources, string payTag)
        {
            int used = 0;
            for (int i = 0; resources != null && i < TaxResourceOrder.Length && used < TaxResourceSlots; i++)
            {
                string tag = TaxResourceOrder[i];
                if (tag == payTag) { continue; }
                int amount = Amount(resources, tag);
                if (amount == 0) { continue; }
                TaxNumber(row, AnySprite(tag), "-" + amount.ToString("N0"), TaxSubFont, TaxSubIcon, DimColor);
                used++;
            }
            for (int i = used; i < TaxResourceSlots; i++) { Gap(row, TaxSubIcon + 8f + TaxNumberWidth); }
        }

        /// <summary>「絵 数字」。数字は決め打ちの幅で右に寄せる（行どうしで桁が揃う）。絵が無いときも幅は空ける。</summary>
        private static void TaxNumber(RectTransform row, Sprite sprite, string value, int font, int icon, Color color)
        {
            Icon(row, sprite, icon);
            Cell(row, value, font, color, TaxNumberWidth, true);
        }

        private static void TaxSeparator()
        {
            RectTransform line = Row(_detail, 9);
            RectTransform bar = Rect("Line", line);
            Image image = bar.gameObject.AddComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.12f);
            image.raycastTarget = false;
            LayoutElement le = bar.gameObject.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;
            le.minHeight = 1f;
            le.preferredHeight = 1f;
            Gap(line, TaxRightPad);
        }

        /// <summary>見出しの行を押すと内訳を畳む・開く。内訳のある課税だけ、左端に - と + が出る。最初は開いておく。</summary>
        private static void TaxExpandable(RectTransform head, TextMeshProUGUI marker, List<GameObject> rows)
        {
            if (rows.Count == 0) { return; }
            marker.text = "-";
            Image hit = head.gameObject.AddComponent<Image>();
            hit.color = new Color(1f, 1f, 1f, 0f);
            Button button = head.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = hit;
            button.onClick.AddListener(delegate
            {
                bool next = !rows[0].activeSelf;
                for (int i = 0; i < rows.Count; i++) { rows[i].SetActive(next); }
                marker.text = next ? "-" : "+";
            });
        }

        // ---------------------------------------------------------------
        // 逮捕・損害賠償の顔
        //
        // この2つの課税は、発動したときに、狙われたスポンサーがひとこと喋る（InGameDialogueService.ShowByUsage）。
        // そのときの顔（スポンサーごとに決まった表情。InGameDialogueMaster の IconExpression）を、課税通知書にも出す。
        // 絵は本体の「顔の絵の一覧」（FaceIconCatalog）にあるが、この表情が入っているのは Run 中の一覧
        // （InGameFaceIconCatalog）で、執務室の一覧には無い。執務室で確実に読める口が無いので、次の順で探す:
        //   1. Run 中に見つけて覚えておいたもの（CacheTaxFaces。プラグインが Run 中にときどき呼ぶ）
        //   2. いま読み込まれている一覧
        //   3. いま読み込まれている絵を名前で
        //   4. アドレスで読む
        // どれでも見つからなければ、ふつうの顔アイコン。
        //
        // この絵は Run の場面に付いた資産（sharedassets3 の InGameFaceIcons）で、アドレスでも Resources でも引けない。
        // 執務室から読み込むには Run の場面そのものを読み込むしかなく、それはできない（2026-11-18 に確認）。
        // 契約の絵が執務室で読めるのは、あちらはアドレスで引ける資産だから。
        // 絵をファイルに写しておく案は採らない（本体の絵を抜き出して保存することになる。2026-11-18 ユーザー判断）。
        // なので、ゲームを起動してから1度も Run に入っていない間は、ふつうの顔アイコンになる。
        // ---------------------------------------------------------------
        private static readonly Dictionary<string, Sprite> DialogueFaces = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private static readonly HashSet<string> DialogueFaceMisses = new HashSet<string>(StringComparer.Ordinal);
        private static readonly string[] TaxDialogueEffects = new string[] { "LockRandomPatron", "NoTagRandomPatron" };

        /// <summary>その課税が発動したときにスポンサーが見せる顔の絵の名前（pic_patron_名前_表情）。無ければ null。</summary>
        private static string TaxDialogueFaceName(string effect, int patron)
        {
            try
            {
                UI.InGameDialogue.InGameDialogueDefinition definition;
                if (UI.InGameDialogue.InGameDialogueMaster.TryGetByUsage(effect + "Comment", (Patron)patron, out definition) && definition != null)
                {
                    return definition.UtageIconName;
                }
            }
            catch (Exception) { }
            return null;
        }

        private static Sprite FromLoadedCatalogs(string name)
        {
            foreach (UI.FaceIconCatalog catalog in Resources.FindObjectsOfTypeAll<UI.FaceIconCatalog>())
            {
                Sprite sprite;
                if (catalog != null && catalog.TryGet(name, out sprite) && sprite != null) { return sprite; }
            }
            return null;
        }

        /// <summary>Run 中に呼ぶ。発動時の顔の絵を、読み込まれている一覧から拾って覚えておく（執務室へ戻っても使えるように）。</summary>
        internal static void CacheTaxFaces()
        {
            try
            {
                foreach (Patron patron in Enum.GetValues(typeof(Patron)))
                {
                    for (int i = 0; i < TaxDialogueEffects.Length; i++)
                    {
                        string name = TaxDialogueFaceName(TaxDialogueEffects[i], (int)patron);
                        Sprite have;
                        if (name == null || (DialogueFaces.TryGetValue(name, out have) && have != null)) { continue; }
                        Sprite found = FromLoadedCatalogs(name);
                        if (found != null) { DialogueFaces[name] = found; DialogueFaceMisses.Remove(name); }
                    }
                }
            }
            catch (Exception) { }
        }

        private static Sprite TaxDialogueFace(string effect, int patron)
        {
            string name = TaxDialogueFaceName(effect, patron);
            if (name == null) { return TaxPatronSprite(patron); }
            Sprite sprite;
            if (DialogueFaces.TryGetValue(name, out sprite) && sprite != null) { return sprite; }
            if (DialogueFaceMisses.Contains(name)) { return TaxPatronSprite(patron); }
            try
            {
                sprite = FromLoadedCatalogs(name);
                if (sprite == null)
                {
                    foreach (Sprite loaded in Resources.FindObjectsOfTypeAll<Sprite>())
                    {
                        if (loaded != null && loaded.name == name) { sprite = loaded; break; }
                    }
                }
                if (sprite == null) { sprite = LargeSprite(name); }
            }
            catch (Exception) { sprite = null; }
            if (sprite != null)
            {
                DialogueFaces[name] = sprite;
                EconomyGraphPlugin.Log.LogInfo("[view] tax face " + name);
                return sprite;
            }
            // 探すのは重いので、この画面を開いている間は探し直さない
            DialogueFaceMisses.Add(name);
            EconomyGraphPlugin.Log.LogInfo("[view] tax face " + name + " is not loaded; using the plain face");
            return TaxPatronSprite(patron);
        }

        private static Sprite TaxPatronSprite(int patron)
        {
            try { return Patrons.GetFaceIcon((Patron)patron); }
            catch (Exception) { return null; }
        }
    }
}
