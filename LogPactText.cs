// 業務日誌 — 契約のカードの文を、いま表示している言語で出し直す。画面（LogView）の続き（partial）。
//
// カードの文は、契約を取った瞬間の言語のまま控えてある（PactLog.cs）。別の言語に切り替えると、文だけ元の言語で残る。
// 文には Run の状況で決まる数字が入るので、後から丸ごと作り直すことはできない。
//
// やり方（2026-11-18 ユーザー案）:
//   1. 本体は、契約の ID から図鑑用の文を、いまの言語で作れる（PactDisplayService.TryCreateCatalogSnapshot）。
//      言い回しは本体のものなので、一字一句本体と同じになる。Run の状況で決まる所だけが穴（「-」）や元の値になる
//   2. 控えには、取った瞬間の文（saved）と一緒に、同じ言語の図鑑用の文（catalog）も残しておく
//   3. saved と catalog を行ごとに見比べれば、どの数字が Run の状況で決まった値かが分かる。
//      それを、いまの言語の図鑑用の文の同じ所へ入れる
//
// 実測（#401 の 26 種類・日本語と英語）: 19 種類は saved と catalog が完全に同じ（＝いまの言語の図鑑用の文がそのまま答え）。
// 残りは、穴「-」に数字が入る（日雇い〜の獲得数・直近の売却回数・固定費用の点数）か、レシピの数が契約で変わった後の値
// （爆薬製造の燃料 40 → 160）。どちらも数字だけの違いで、この方法で埋まる。
// 文の形ごと違う行（異物混入の「各資源」と資源の絵）は埋めず、その行だけ図鑑用の文のままにする（間違った文は出さない）。
//
// 古い控え（catalog が無い）は、控えたのと同じ言語で一度その Run を開いたときに catalog を足して書き直す。
// 同じ言語かどうかは、言語の名前ではなく「いまの図鑑用の文と控えた文が、数字を除いて一致するか」で見分ける。

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Pact;

namespace LwfEconomyGraph
{
    internal static partial class LogView
    {
        private static readonly Regex LineBreak = new Regex(@"(\\n|\n)");
        private static readonly Regex RichTag = new Regex("<[^>]*>");
        // 数字（桁区切り・小数つき）か、穴の「-」（英数字に挟まれていない・後ろが数字でないハイフン）
        private static readonly Regex NumberOrHole = new Regex(
            @"\d[\d,]*(?:\.\d+)?|(?<![A-Za-z0-9])-(?![A-Za-z0-9])|(?<![A-Za-z0-9])-(?=[^\x00-\x7F])|(?<=[^\x00-\x7F])-(?![0-9])");

        /// <summary>契約を取った瞬間に、文を出し直すための手がかりをカードに足す（PactLog.cs から）。</summary>
        internal static void AddRecastHints(JObject card)
        {
            string catalog = CatalogText(RunData.Str(card, "id"));
            if (catalog == null) { return; }
            card["catalog"] = catalog;
            card["lang"] = CurrentLanguage();
            card["vars"] = TakeVariables(RunData.Str(card, "id"), RunData.Str(card, "description") ?? "", catalog);
        }

        internal static string CurrentLanguage()
        {
            try { return UnityEngine.Localization.Settings.LocalizationSettings.SelectedLocale.Identifier.Code; }
            catch (Exception) { return ""; }
        }

        /// <summary>契約の図鑑用の文（いまの言語）。作れなければ null。</summary>
        internal static string CatalogText(string pactID)
        {
            try
            {
                PactDisplaySnapshot snapshot;
                if (pactID != null && PactDisplayService.TryCreateCatalogSnapshot(pactID, out snapshot) && snapshot != null)
                {
                    return snapshot.DescriptionText;
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>カードの文を、いまの言語で。控えた言語と同じなら控えのまま。出し直せないときも控えのまま。</summary>
        private static string CardDescription(JObject card)
        {
            string saved = RunData.Str(card, "description") ?? "";
            string from = RunData.Str(card, "catalog");
            string language = RunData.Str(card, "lang");
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(language) || language == CurrentLanguage()) { return saved; }
            string to = CatalogText(RunData.Str(card, "id"));
            if (string.IsNullOrEmpty(to)) { return saved; }
            try { return PutVariables(Recast(saved, from, to), card); }
            catch (Exception) { return saved; }
        }

        // ---------------------------------------------------------------
        // 数字ではない穴（絵など）
        //
        // 図鑑用の文では、Run の状況で決まる所が「言葉」に差し替わることがある。異物混入の「売却するたび、〔資源の絵3つ〕x20」
        // は、図鑑では「今回の開発スポンサーに対応する各資源x20」になる。数字の見比べでは埋まらない。
        // 契約に雛形（言語表の「<契約ID>D」。<target_resources> のような穴の名前が入った文）があるものは、
        //   控えたとき：雛形を、控えた文と図鑑用の文の両方に当てて、穴ごとの中身を取り出す。中身が違っていて、
        //               控えた側が言葉を含まない（絵・数字・記号だけ＝どの言語でも同じ）穴を、名前つきで控える（vars）
        //   出すとき　：いまの言語の雛形を、いまの言語の図鑑用の文に当てて、その名前の穴の場所を見つけ、控えた中身を入れる
        // 言葉を含む中身（名前の一覧など）は控えない。言語が変わると合わなくなるため、図鑑用の文の言い方のままにする。
        // ---------------------------------------------------------------
        private static readonly Regex AnyTag = new Regex("<(/?)([A-Za-z_][A-Za-z0-9_\\-]*(?:/[A-Za-z0-9_\\-]+)?)([^>]*)>");
        private static readonly HashSet<string> RichNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "color", "u", "b", "i", "s", "size", "sprite", "link", "nobr", "align", "indent", "mark", "sub", "sup", "pos",
            "voffset", "line-height", "cspace", "font", "material", "br", "alpha", "style", "uppercase", "lowercase",
            "smallcaps", "margin", "width", "noparse", "page", "rotate", "mspace", "gradient", "allcaps", "font-weight"
        };

        private static string Lines(string text)
        {
            return (text ?? "").Replace("\\n", "\n");
        }

        /// <summary>雛形を、穴を「何でも」に置き換えた正規表現にする。names は穴の名前（出てくる順。同じ名前が2度出ることがある）。</summary>
        private static Regex TemplatePattern(string template, List<string> names)
        {
            StringBuilder sb = new StringBuilder("^");
            int at = 0;
            foreach (Match m in AnyTag.Matches(template))
            {
                // 書式タグ（色・リンク・絵）は地の文。閉じタグ・属性つきのものも穴ではない
                if (m.Groups[1].Length > 0 || m.Groups[3].Length > 0 || RichNames.Contains(m.Groups[2].Value)) { continue; }
                sb.Append(Regex.Escape(template.Substring(at, m.Index - at)));
                sb.Append("(.*?)");
                names.Add(m.Groups[2].Value);
                at = m.Index + m.Length;
            }
            sb.Append(Regex.Escape(template.Substring(at))).Append("$");
            return names.Count == 0 ? null : new Regex(sb.ToString(), RegexOptions.Singleline);
        }

        private static string PactTemplate(string pactID)
        {
            try { return Lines(Utility.Localization.LocalizedTextGetter.GetPactData(pactID + "D")); }
            catch (Exception) { return null; }
        }

        /// <summary>控えたときの言語で呼ぶ。図鑑用の文と控えた文で中身が違う穴のうち、言葉を含まないものを返す。</summary>
        private static JObject TakeVariables(string pactID, string saved, string catalog)
        {
            JObject found = new JObject();
            string template = PactTemplate(pactID);
            if (string.IsNullOrEmpty(template) || template == pactID + "D") { return found; }
            List<string> names = new List<string>();
            Regex pattern = TemplatePattern(template, names);
            if (pattern == null) { return found; }
            Match s = pattern.Match(Lines(saved)), c = pattern.Match(Lines(catalog));
            if (!s.Success || !c.Success) { return found; }
            for (int i = 0; i < names.Count; i++)
            {
                string value = s.Groups[i + 1].Value;
                if (value == c.Groups[i + 1].Value || found[names[i]] != null) { continue; }
                // 書式タグを除いた残りに字（どの言語のものでも）があれば、言葉。控えない
                bool words = false;
                foreach (char ch in RichTag.Replace(value, "")) { if (char.IsLetter(ch) && ch != 'x' && ch != 'X') { words = true; break; } }
                if (!words && value.Length > 0) { found[names[i]] = value; }
            }
            return found;
        }

        /// <summary>いまの言語の文（数字は入れ直し済み）に、控えた穴の中身を入れる。</summary>
        private static string PutVariables(string text, JObject card)
        {
            JObject vars = RunData.Obj(card, "vars");
            if (vars == null || vars.Count == 0) { return text; }
            string template = PactTemplate(RunData.Str(card, "id"));
            if (string.IsNullOrEmpty(template)) { return text; }
            List<string> names = new List<string>();
            Regex pattern = TemplatePattern(template, names);
            if (pattern == null) { return text; }
            string body = Lines(text);
            Match m = pattern.Match(body);
            if (!m.Success) { return text; }
            // 後ろの穴から差し替える（前から替えると、後ろの穴の位置がずれる）
            StringBuilder sb = new StringBuilder(body);
            for (int i = names.Count - 1; i >= 0; i--)
            {
                JToken value = vars[names[i]];
                if (value == null) { continue; }
                Group g = m.Groups[i + 1];
                sb.Remove(g.Index, g.Length).Insert(g.Index, (string)value);
            }
            return sb.ToString();
        }

        /// <summary>saved（控えた文）と from（同じ言語の図鑑用の文）の違いを、to（いまの言語の図鑑用の文）へ写す。</summary>
        internal static string Recast(string saved, string from, string to)
        {
            if (saved == from) { return to; }
            string[] s = LineBreak.Split(saved), f = LineBreak.Split(from), t = LineBreak.Split(to);
            // 行の数が合わなければ、写す先が決められない。いまの言語の図鑑用の文をそのまま出す
            if (s.Length != f.Length || f.Length != t.Length) { return to; }
            StringBuilder sb = new StringBuilder(to.Length + 16);
            for (int i = 0; i < t.Length; i++)
            {
                // 奇数番めは改行そのもの
                sb.Append(i % 2 == 1 || s[i] == f[i] ? t[i] : RecastLine(s[i], f[i], t[i]));
            }
            return sb.ToString();
        }

        private static string RecastLine(string saved, string from, string to)
        {
            List<string> sv = new List<string>(), fv = new List<string>(), tv = new List<string>();
            // 数字と穴を除いた地の文が同じでなければ、文の形が違う。その行は図鑑用の文のまま
            if (Skeleton(saved, sv) != Skeleton(from, fv) || sv.Count != fv.Count) { return to; }
            Skeleton(to, tv);

            string[] replace = new string[tv.Count];
            bool sameOrder = fv.Count == tv.Count;
            for (int k = 0; sameOrder && k < fv.Count; k++) { sameOrder = fv[k] == tv[k]; }
            if (sameOrder)
            {
                // 2つの言語で数字の並びが同じ → 同じ番めの値を入れる
                for (int k = 0; k < tv.Count; k++) { replace[k] = sv[k]; }
            }
            else
            {
                // 並びが違う（語順が違う言語）→ 値で対応を取る。同じ値が別々の値に変わっているものは、取り違えるので入れない。
                // 穴「-」どうしは、穴の中での順番で対応させる（穴の数が同じときだけ）
                Dictionary<string, string> byValue = new Dictionary<string, string>(StringComparer.Ordinal);
                HashSet<string> mixed = new HashSet<string>(StringComparer.Ordinal);
                List<string> holes = new List<string>();
                for (int k = 0; k < fv.Count; k++)
                {
                    if (fv[k] == "-") { holes.Add(sv[k]); continue; }
                    string had;
                    if (byValue.TryGetValue(fv[k], out had) && had != sv[k]) { mixed.Add(fv[k]); }
                    byValue[fv[k]] = sv[k];
                }
                int holeCount = 0;
                for (int k = 0; k < tv.Count; k++) { if (tv[k] == "-") { holeCount++; } }
                int hole = 0;
                for (int k = 0; k < tv.Count; k++)
                {
                    if (tv[k] == "-")
                    {
                        replace[k] = holeCount == holes.Count ? holes[hole] : null;
                        hole++;
                        continue;
                    }
                    string value;
                    replace[k] = !mixed.Contains(tv[k]) && byValue.TryGetValue(tv[k], out value) ? value : null;
                }
            }

            int index = 0;
            return Outside(to, delegate(string text)
            {
                return NumberOrHole.Replace(text, delegate(Match m)
                {
                    string value = index < replace.Length ? replace[index] : null;
                    index++;
                    return value ?? m.Value;
                });
            });
        }

        /// <summary>書式タグの外だけに f を掛ける（タグの中の数字＝色や絵の番号は触らない）。</summary>
        private static string Outside(string line, Func<string, string> f)
        {
            StringBuilder sb = new StringBuilder(line.Length + 8);
            int at = 0;
            foreach (Match tag in RichTag.Matches(line))
            {
                if (tag.Index > at) { sb.Append(f(line.Substring(at, tag.Index - at))); }
                sb.Append(tag.Value);
                at = tag.Index + tag.Length;
            }
            if (at < line.Length) { sb.Append(f(line.Substring(at))); }
            return sb.ToString();
        }

        /// <summary>数字と穴を印に置き換えた地の文を返し、取り出した数字と穴を values に並べる。</summary>
        private static string Skeleton(string line, List<string> values)
        {
            return Outside(line, delegate(string text)
            {
                return NumberOrHole.Replace(text, delegate(Match m)
                {
                    values.Add(m.Value);
                    return "\u0001";
                });
            });
        }

        /// <summary>古い控え（図鑑用の文と言語が入っていない）に、それを足す。いまの言語が控えた言語と同じときだけ。
        /// 足したら true（呼んだ側がファイルを書き直す）。</summary>
        private static bool FillCatalog(List<JObject> cards)
        {
            // 図鑑用の文はあるが穴の中身（vars）がまだ無い控え。同じ言語のときに足す
            bool added = false;
            string now = CurrentLanguage();
            for (int i = 0; i < cards.Count; i++)
            {
                if (!RunData.Has(cards[i], "catalog") || RunData.Has(cards[i], "vars") || RunData.Str(cards[i], "lang") != now) { continue; }
                cards[i]["vars"] = TakeVariables(RunData.Str(cards[i], "id"), RunData.Str(cards[i], "description") ?? "", RunData.Str(cards[i], "catalog"));
                added = true;
            }

            int missing = 0, same = 0;
            List<string> catalogs = new List<string>();
            for (int i = 0; i < cards.Count; i++)
            {
                string catalog = RunData.Has(cards[i], "catalog") ? null : CatalogText(RunData.Str(cards[i], "id"));
                catalogs.Add(catalog);
                if (catalog == null) { continue; }
                missing++;
                // 同じ言語なら、数字を除いた地の文がほとんどの契約で一致する
                string saved = RunData.Str(cards[i], "description") ?? "";
                if (Skeleton(saved, new List<string>()) == Skeleton(catalog, new List<string>())) { same++; }
            }
            if (missing == 0 || same * 2 < missing) { return added; }
            string language = CurrentLanguage();
            for (int i = 0; i < cards.Count; i++)
            {
                if (catalogs[i] == null) { continue; }
                cards[i]["catalog"] = catalogs[i];
                cards[i]["lang"] = language;
                cards[i]["vars"] = TakeVariables(RunData.Str(cards[i], "id"), RunData.Str(cards[i], "description") ?? "", catalogs[i]);
            }
            return true;
        }
    }
}
