// 業務日誌 — Run の終わりのレシピの控え。プラグイン本体の続き（partial）。
//
// レシピの材料・成果物の数・時間は、契約や返済特典で Run の間に変わっていく。
// 契約の履歴から計算し直すこともできるが、効果の種類が多く取りこぼしやすいので、
// 終わった時点で本体が持っているレシピをそのまま控える（RecipeManagers.instance）。
// 控えるのは、その Run で解禁されているレシピ全部（使っていないものも含む）。
//
// 書くのは Run の終わり（グラフの記録と同じとき）で、`<開始時刻>.recipes.json`。
// 形: JSON の配列。{ kind（使い魔の種類：Crafter・Smelter…）, id, time（秒）,
//                    result { id, count, type }, ingredients [ { id, count, type } ],
//                    crafted（そのレシピを完了した回数）, made（出来た数の合計） }
//   type は本体の値の型名（ItemCount・TagCount など。アイテムか資源かの見分け）
//
// -- 製作数を自前で数える理由 -------------------------------------------
// 本体の統計の「生産」はアイテム単位で、クラフトの成果物のほかに、採掘ポイントから出た分も足している
// （HarvestableObject.TryHarvestOut。金庫 SafeObject も採掘ポイントの一種で、金庫から出したアイテムも入る）。
// レシピの表に出すと「クラフトで作った数」に見えてしまうので、クラフトの完了
// （StatsItemFlowRecorder.RecordCraftCompleted）だけをレシピごとに数える。成果物が資源のレシピも数えられる。
// F6 の読み直しをまたぐ持ち越しはしていない（開発中に読み直すと、それまでの分は抜ける）。

using System;
using System.Globalization;
using System.IO;
using System.Text;
using Items;
using Items.Craft;
using Items.ValueCountSettings;
using Newtonsoft.Json.Linq;

namespace LwfEconomyGraph
{
    public sealed partial class EconomyGraphPlugin
    {
        internal const string RecipeSuffix = ".recipes.json";

        // レシピごとの、完了した回数と出来た数の合計（Run の間だけ持つ）
        private static readonly System.Collections.Generic.Dictionary<string, long> CraftedTimes =
            new System.Collections.Generic.Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly System.Collections.Generic.Dictionary<string, long> CraftedMade =
            new System.Collections.Generic.Dictionary<string, long>(StringComparer.Ordinal);

        private void PatchCraftCompleted()
        {
            try
            {
                System.Reflection.MethodInfo target = HarmonyLib.AccessTools.Method(typeof(Stats.StatsItemFlowRecorder), "RecordCraftCompleted");
                System.Reflection.MethodInfo prefix = typeof(EconomyGraphPlugin).GetMethod("OnCraftCompleted",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (target == null || prefix == null)
                {
                    Logger.LogWarning("[log] StatsItemFlowRecorder.RecordCraftCompleted not found; crafted totals are not counted");
                    return;
                }
                if (_harmony == null) { _harmony = new HarmonyLib.Harmony(PluginGuid); }
                _harmony.Patch(target, prefix: new HarmonyLib.HarmonyMethod(prefix));
            }
            catch (Exception e)
            {
                Logger.LogWarning("[log] cannot count crafts: " + e.Message);
            }
        }

        /// <summary>クラフトが1回終わった。引数の名前は本体と同じ recipe（Harmony は名前で結ぶ）。</summary>
        private static void OnCraftCompleted(Recipe recipe)
        {
            try
            {
                if (recipe == null || recipe.RecipeID == null) { return; }
                long times, made;
                CraftedTimes.TryGetValue(recipe.RecipeID, out times);
                CraftedMade.TryGetValue(recipe.RecipeID, out made);
                CraftedTimes[recipe.RecipeID] = times + 1;
                CraftedMade[recipe.RecipeID] = made + (recipe.Result != null ? recipe.Result.Count : 0);
            }
            catch (Exception) { }
        }

        private static void ResetCraftCounts()
        {
            CraftedTimes.Clear();
            CraftedMade.Clear();
        }

        /// <summary>いま解禁されているレシピを全部ファイルへ書く。盤面が生きているうち（Run の終わり）に呼ぶ。</summary>
        private void SaveRecipes()
        {
            try
            {
                RecipeManagers managers = RecipeManagers.instance;
                if (managers == null || _boundGame == null) { return; }

                JArray all = new JArray();
                for (int k = 0; k < (int)CrafterKind.Num; k++)
                {
                    CrafterKind kind = (CrafterKind)k;
                    foreach (Recipe recipe in managers.GetAllAvailableRecipes(kind))
                    {
                        if (recipe == null) { continue; }
                        JObject o = new JObject();
                        o["kind"] = kind.ToString();
                        o["id"] = recipe.RecipeID;
                        o["time"] = recipe.RequireTime;
                        o["result"] = RecipeValue(recipe.Result);
                        JArray ingredients = new JArray();
                        if (recipe.Ingredients != null)
                        {
                            foreach (ValueCount v in recipe.Ingredients) { ingredients.Add(RecipeValue(v)); }
                        }
                        o["ingredients"] = ingredients;
                        long times, made;
                        CraftedTimes.TryGetValue(recipe.RecipeID ?? "", out times);
                        CraftedMade.TryGetValue(recipe.RecipeID ?? "", out made);
                        o["crafted"] = times;
                        o["made"] = made;
                        all.Add(o);
                    }
                }
                if (all.Count == 0) { return; }

                string dir = GraphDirectory();
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, _runStartUtc.ToString(GraphStamp, CultureInfo.InvariantCulture) + RecipeSuffix);
                File.WriteAllText(path, all.ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false));
                Logger.LogInfo("[log] saved " + all.Count + " recipes: " + Path.GetFileName(path));
            }
            catch (Exception e)
            {
                Logger.LogWarning("[log] cannot save the recipes: " + e.Message);
            }
        }

        private static JToken RecipeValue(ValueCount value)
        {
            JObject o = new JObject();
            if (value == null || value.Key == null) { return o; }
            o["id"] = value.Key.GetID();
            o["count"] = value.Count;
            o["type"] = value.GetType().Name;
            return o;
        }
    }
}
