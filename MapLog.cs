// 業務日誌 — 工場の配置の記録。プラグイン本体の続き（partial）。
//
// 返済のたび・Run の終わり・その合間の 30 秒ごと（配置が変わっていたときだけ）に、
// 「どの土地を持っていて、どのマスに何が置いてあるか」を控える。
// 絵（マップビューの画像）ではなくデータで持つ。画像は1枚 0.2〜0.4MB、返済 30 回の Run で 10MB ほどに
// なる見積もりで、データなら圧縮後で1回あたり数 KB〜十数 KB に収まる（2026-11-17 の概算。未実測）。
//
// 読むのは本体の公開の口だけ:
//   GameStateManager.GetGridManager() → GetParentGrid(番地)        土地（11×11。バイオーム・購入済みか）
//   GridManager.GetMapObjectDictionary().GetMapObject(マスの番地)  置かれた物（1つが複数マスを占めることがある）
//   ConnectableObject.GetCurrentDirection() / FamObject.GetFamType() /
//   CrafterFamObjectBase.OnGoingRecipe / HarvestableObject.GetHarvestableType()
//
// 向きは本体の値（Direction4：西 0・北 1・東 2・南 3）をそのまま残す。
// **コンベアは、物が流れるのは向きの逆**（2026-11-17 ユーザーが実機と見比べて確認。向きの側が入口）。
// 採掘の使い魔は向きの先の岩を掘る（こちらは向きのとおり）。描く側で読み替えること。
//
// 記録は Run の間メモリに持ち、グラフの記録と同じときに `<開始時刻>.map.gz` へ書く。
// F6 の読み直しをまたぐ持ち越しはしていない（開発中に読み直すと、それまでの回の配置は抜ける）。
// Run を途中で離れたとき（タイトルへ戻る）は、離れた後に気づくので最後の1回は撮れない。
//
// ファイルの形（gzip の中身・リトルエンディアン）:
//   int 版, int 回数, 回ごとに { float 経過秒, int 返済回数, int 長さ, byte[] 中身 }
//   中身: int 土地の数, 土地ごとに { short x, short y, int バイオーム, bool 購入済み }
//         int 物の数, 物ごとに { string 型名, string 名前の鍵, int 使い魔の種類(-1), int 向き(-1),
//                               string レシピID, int 採取物の種類(-1), ushort マスの数, マスごとに { short x, short y },
//                               （版 2 から）short 行き先 x, short 行き先 y（レイヴンの荷下ろし地点。無ければ short.MinValue）,
//                               string 物のID, int レア度(-1), int スポンサー(-1), string 報酬のID,
//                               ushort 要求の数, 要求ごとに { string ID, int いまの数, int 目標の数 },
//                               （版 3 から）int 報酬の数, string 説明文（ポータルの効果。撮ったときの言語のまま）, bool 完了済み（依頼看板）,
//                               （版 4 から）要求ごとに { string 種類（本体の型名）, string 要求の文（撮ったときの言語のまま） },
//                               （版 5 から）ushort 毎秒の数, 毎秒ごとに { byte 種類（0 生産・1 消費・2 輸送）, string アイテムか資源の ID, float 毎秒 },
//                               （版 6 から）ushort エンチャントの数, エンチャントごとに { string 効果の ID（マゾヒズムは HarvestDropBurst） },
//                               （版 7 から）float 運ぶ速さ（毎秒の個数。コンベア・分岐など。それ以外は 0）,
//                               （版 8 から）bool レシピの中身があるか, あれば { float 製造秒数, string 成果物の ID, int 成果物の数,
//                                             ushort 材料の数, 材料ごとに { string ID, int 数 } }（契約などで変わった後の、その時点の値） }
//   要求の ID だけでは何を求めているか決まらない（依頼看板にはアイテムの納品のほかに、レシピを手放す・
//   何回クラフトする・使い魔を持っている、がある。レシピの ID は資源と同じ綴りのことがある）ので、
//   本体が画面に出す文をそのまま控える。
//   版 1 は 2026-11-17 の試作の間だけ書いていた形で、読む側（LogMapView.cs）は両方読める。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using Items.Craft;
using Items.IDemands;
using UI.Information.ItemPerSec;
using Map.MapObjects.PureClass.UniqueObjects;
using Map;
using Map.GridManagerHelpers;
using Map.MapObjects.PureClass;
using Map.MapObjects.PureClass.FamObjects;
using UnityEngine;

namespace LwfEconomyGraph
{
    public sealed partial class EconomyGraphPlugin
    {
        private const string MapSuffix = ".map.gz";
        private const int MapFileVersion = 8;

        /// <summary>この秒数より近い回は、同じ瞬間とみなして1回にまとめる（読む側の LogMapView.cs も同じ値）</summary>
        internal const float SameMoment = 1f;

        private readonly List<byte[]> _mapShots = new List<byte[]>();
        private readonly List<float> _mapShotTimes = new List<float>();
        private readonly List<int> _mapShotRepaid = new List<int>();
        private int _mapShotFrame = -1;
        private bool _mapBroken;

        /// <summary>返済の合間にも、この秒数（ゲーム内の経過）ごとに撮る。序盤の組み立てが返済の前に済んでしまい、
        /// 返済のたびだけでは途中が残らないため（2026-11-18）。配置が前の回と同じなら足さない</summary>
        private const double MapEvery = 30.0;
        private double _mapNextTimed;
        /// <summary>最後に控えた回の「配置だけ」（土地・物の種類・向き・レシピ・マス）。ポータルの納品数や毎秒の値は
        /// 手を止めていても動くので、間隔で撮る回はこちらで比べる</summary>
        private byte[] _mapLastLayout;

        private void ResetMapShots()
        {
            _mapShots.Clear();
            _mapShotTimes.Clear();
            _mapShotRepaid.Clear();
            _mapShotFrame = -1;
            _mapNextTimed = 0.0;
            _mapLastLayout = null;
        }

        /// <summary>間隔で撮る番が来ていたら撮る。毎フレーム呼ぶ。</summary>
        private void TickMapTimed()
        {
            if (_elapsed < _mapNextTimed) { return; }
            // 最初の1回は Run の始まり（0:00）。これが無いと再生が 0:30 から始まり、シークバーの左端が空く。
            // 始まった直後は本体の盤面がまだ無くて撮れないことがあるので、撮れるまで毎フレーム試す
            bool first = _mapShots.Count == 0;
            CaptureMap(true);
            if (first && _mapShots.Count == 0 && _elapsed < MapEvery) { return; }
            // 次は 30 秒の区切り（0:30・1:00…）にそろえる
            _mapNextTimed = (Math.Floor(_elapsed / MapEvery) + 1.0) * MapEvery;
        }

        private void CaptureMap() { CaptureMap(false); }

        /// <summary>いまの工場の配置を1回分控える。timed は間隔で撮る回（配置が前の回と同じなら足さない）。</summary>
        private void CaptureMap(bool timed)
        {
            if (_mapBroken || _replaying || _boundGame == null) { return; }
            if (LogEnabled == null || !LogEnabled.Value) { return; }

            // 同じフレームに続けて返済したときは、1回にまとめて返済回数だけ進める
            if (Time.frameCount == _mapShotFrame && _mapShotRepaid.Count > 0)
            {
                _mapShotRepaid[_mapShotRepaid.Count - 1] = _closedColumns;
                return;
            }

            try
            {
                Stopwatch watch = Stopwatch.StartNew();
                GridManager grid = _boundGame.GetGridManager();
                if (grid == null) { return; }
                MapObjectDictionary objects = grid.GetMapObjectDictionary();
                if (objects == null) { return; }

                List<ParentGrid> lands = new List<ParentGrid>();
                for (int x = -5; x <= 5; x++)
                {
                    for (int y = -5; y <= 5; y++)
                    {
                        ParentGrid land = null;
                        try { land = grid.GetParentGrid(new Vector2Int(x, y)); }
                        catch (Exception) { }
                        if (land != null) { lands.Add(land); }
                    }
                }

                // Run が始まった直後は、盤面はあっても土地がまだ組まれていない（持っている土地が 0）。そのまま撮ると
                // 何も無い真っ黒な回が 0:00 として残るので、土地が出来るまで待つ（間隔で撮る側が毎フレーム試し直す）
                bool anyOwn = false;
                for (int i = 0; i < lands.Count && !anyOwn; i++) { anyOwn = lands[i].IsOwn; }
                if (!anyOwn) { return; }

                // 物は購入済みの土地のぶんだけ。1つが複数のマスを占めるので、物ごとにマスをまとめる
                List<MapObject> order = new List<MapObject>();
                Dictionary<MapObject, List<Vector2Int>> cells = new Dictionary<MapObject, List<Vector2Int>>();
                for (int i = 0; i < lands.Count; i++)
                {
                    if (!lands[i].IsOwn || lands[i].ChildGrids == null) { continue; }
                    foreach (ChildGrid cell in lands[i].ChildGrids)
                    {
                        if (cell == null) { continue; }
                        MapObject found = objects.GetMapObject(cell.GlobalAddress);
                        if (found == null) { continue; }
                        List<Vector2Int> list;
                        if (!cells.TryGetValue(found, out list))
                        {
                            list = new List<Vector2Int>();
                            cells[found] = list;
                            order.Add(found);
                        }
                        list.Add(cell.GlobalAddress);
                    }
                }

                MemoryStream buffer = new MemoryStream();
                BinaryWriter w = new BinaryWriter(buffer);
                MemoryStream layoutBuffer = new MemoryStream();
                BinaryWriter layout = new BinaryWriter(layoutBuffer);
                w.Write(lands.Count);
                for (int i = 0; i < lands.Count; i++)
                {
                    w.Write((short)lands[i].Address.x);
                    w.Write((short)lands[i].Address.y);
                    w.Write((int)lands[i].Biome);
                    w.Write(lands[i].IsOwn);
                    layout.Write(lands[i].IsOwn);
                }

                w.Write(order.Count);
                for (int i = 0; i < order.Count; i++)
                {
                    MapObject o = order[i];
                    int famType = -1;
                    int direction = -1;
                    int harvestable = -1;
                    string recipe = "";

                    ConnectableObject connectable = o as ConnectableObject;
                    if (connectable != null) { direction = (int)connectable.GetCurrentDirection(); }
                    FamObject fam = o as FamObject;
                    if (fam != null) { famType = (int)fam.GetFamType(); }
                    CrafterFamObjectBase crafter = o as CrafterFamObjectBase;
                    if (crafter != null)
                    {
                        Recipe r = crafter.OnGoingRecipe;
                        if (r != null && r.RecipeID != null) { recipe = r.RecipeID; }
                    }
                    HarvestableObject harvest = o as HarvestableObject;
                    if (harvest != null) { harvestable = (int)harvest.GetHarvestableType(); }

                    w.Write(o.GetType().Name);
                    w.Write(o.GetNameKey() ?? "");
                    w.Write(famType);
                    w.Write(direction);
                    w.Write(recipe);
                    w.Write(harvestable);
                    List<Vector2Int> at = cells[o];
                    w.Write((ushort)at.Count);
                    for (int c = 0; c < at.Count; c++)
                    {
                        w.Write((short)at[c].x);
                        w.Write((short)at[c].y);
                    }
                    layout.Write(o.GetType().Name);
                    layout.Write(famType);
                    layout.Write(direction);
                    layout.Write(recipe);
                    layout.Write(harvestable);
                    layout.Write((short)at[0].x);
                    layout.Write((short)at[0].y);
                    WriteMapExtras(w, o, cells, layout);
                }
                w.Flush();
                layout.Flush();

                byte[] layoutNow = layoutBuffer.ToArray();
                if (timed && _mapLastLayout != null && SameBytes(_mapLastLayout, layoutNow)) { return; }
                _mapLastLayout = layoutNow;

                // 前の回と中身が同じなら足さない（最後の返済と同時に Run が終わると、同じ配置を2回撮ることになる）
                byte[] shot = buffer.ToArray();
                if (_mapShots.Count > 0 && SameBytes(_mapShots[_mapShots.Count - 1], shot))
                {
                    _mapShotRepaid[_mapShotRepaid.Count - 1] = _closedColumns;
                    return;
                }

                // 前の回と同じ時刻なら、足さずに前の回を置き換える（まとめて返済したとき・最後の返済と同時に Run が
                // 終わったとき）。中身はポータルの納品数などが少し違うことがあり、上の「中身が同じ」では拾えない。
                // 置き換えるのは、同じ瞬間の状態としては後のほうが正しいため
                if (_mapShots.Count > 0 && Mathf.Abs((float)_elapsed - _mapShotTimes[_mapShotTimes.Count - 1]) < SameMoment)
                {
                    int last = _mapShots.Count - 1;
                    _mapShots[last] = shot;
                    _mapShotRepaid[last] = _closedColumns;
                    _mapShotFrame = Time.frameCount;
                    return;
                }

                _mapShots.Add(shot);
                _mapShotTimes.Add((float)_elapsed);
                _mapShotRepaid.Add(_closedColumns);
                _mapShotFrame = Time.frameCount;
                watch.Stop();
                Logger.LogInfo("[map] captured #" + _mapShots.Count + (timed ? " (timed)" : "") + " at " + FormatTime(_elapsed)
                    + ": " + order.Count + " objects, " + buffer.Length + " bytes, "
                    + watch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
            }
            catch (Exception e)
            {
                // 本体の作りが変わったら、配置の記録だけやめる（グラフの記録は続ける）
                _mapBroken = true;
                Logger.LogWarning("[map] cannot capture the factory; map recording is off: " + e);
            }
        }

        /// <summary>レイヴンの行き先と、ポータル・取引の中身（要求と報酬）。取れないものは空で書く。</summary>
        private static void WriteMapExtras(BinaryWriter w, MapObject o, Dictionary<MapObject, List<Vector2Int>> cells, BinaryWriter layout)
        {
            short linkX = short.MinValue, linkY = short.MinValue;
            string id = "";
            int rarity = -1, patron = -1;
            string reward = "";
            int rewardCount = 0;
            string description = "";
            bool done = false;
            List<IDemand> demands = new List<IDemand>();
            try
            {
                TransporterObject raven = o as TransporterObject;
                if (raven != null)
                {
                    TransporterExitObject exit = raven.GetExitObject();
                    List<Vector2Int> at;
                    if (exit != null && cells.TryGetValue(exit, out at) && at.Count > 0)
                    {
                        linkX = (short)at[0].x;
                        linkY = (short)at[0].y;
                    }
                }
                QuestPortalObject portal = o as QuestPortalObject;
                if (portal != null)
                {
                    id = portal.ID ?? "";
                    rarity = (int)portal.Rarity;
                    patron = (int)portal.Patron;
                    if (portal.Quest != null)
                    {
                        if (portal.Quest.Demand != null) { demands.Add(portal.Quest.Demand); }
                        if (portal.Quest.Reward != null) { reward = portal.Quest.Reward.GetRewardID() ?? ""; }
                        description = portal.Quest.GetDescription() ?? "";
                        if (description.Length == 0 && portal.Quest.Reward != null) { description = portal.Quest.Reward.Description ?? ""; }
                    }
                }
                TradeObject trade = o as TradeObject;
                if (trade != null)
                {
                    id = trade.ID ?? "";
                    rarity = (int)trade.Rarity;
                    patron = (int)trade.Patron;
                    done = trade.IsCompleted;
                    if (trade.Demands != null)
                    {
                        foreach (IDemand d in trade.Demands) { if (d != null) { demands.Add(d); } }
                    }
                    // 依頼看板の報酬は看板の種類ごとに別の表にある（本体の記録係と同じ引き方）
                    TradeObjectMasterRepository master = TradeObjectMaster.GetRepository();
                    if (master != null && trade.Property != null)
                    {
                        TradePactRewardDefinition pact;
                        TradeRecipeRewardDefinition recipe;
                        TradeValueCountRewardDefinition count;
                        switch (trade.Property.TradeType)
                        {
                            case TradeObjectType.TradePact:
                                if (master.TryGetPactReward(id, out pact)) { reward = pact.PactID ?? ""; }
                                break;
                            case TradeObjectType.TradeRecipe:
                                if (master.TryGetRecipeReward(id, out recipe)) { reward = recipe.RecipeID ?? ""; }
                                break;
                            case TradeObjectType.TradeFam:
                                if (master.TryGetFamReward(id, out count)) { reward = count.RewardID ?? ""; rewardCount = count.Count; }
                                break;
                            case TradeObjectType.TradeTag:
                                if (master.TryGetTagReward(id, out count)) { reward = count.RewardID ?? ""; rewardCount = count.Count; }
                                break;
                        }
                    }
                }
            }
            catch (Exception) { }

            w.Write(linkX);
            w.Write(linkY);
            w.Write(id);
            w.Write(rarity);
            w.Write(patron);
            w.Write(reward);
            w.Write((ushort)demands.Count);
            for (int i = 0; i < demands.Count; i++)
            {
                string demanded = "";
                int current = 0, goal = 0;
                try
                {
                    demanded = demands[i].GetDemandedID() ?? "";
                    current = demands[i].GetCurrentCount();
                    goal = demands[i].GetGoalCount();
                }
                catch (Exception) { }
                w.Write(demanded);
                w.Write(current);
                w.Write(goal);
            }
            w.Write(rewardCount);
            w.Write(description);
            w.Write(done);
            // 版 4：要求ごとの種類と、本体が出している要求の文（上の要求と同じ並び）
            for (int i = 0; i < demands.Count; i++)
            {
                string text = "";
                try { text = demands[i].GetDescription() ?? ""; }
                catch (Exception) { }
                w.Write(demands[i].GetType().Name);
                w.Write(text);
            }
            WriteMapRates(w, o);
            // ここから先（エンチャント・運ぶ速さ・レシピの中身）は、契約を取ったときなどにしか変わらない値。
            // 「配置が同じか」の比べにも入れる（入れないと、契約でレシピが変わっても、30 秒ごとの回が
            // 「前と同じ」で捨てられ、次に物を置くまで古い値のまま再生される）
            w.Flush();
            MemoryStream stream = (MemoryStream)w.BaseStream;
            int from = (int)stream.Position;
            WriteMapEnchants(w, o);
            WriteMapSpeed(w, o);
            WriteMapRecipe(w, o);
            w.Flush();
            layout.Write(stream.GetBuffer(), from, (int)stream.Position - from);
        }

        /// <summary>版 8：クラフトの使い魔が回しているレシピの、その時点の中身（材料・成果物・製造秒数）。
        /// 本体のレシピ表の元の値ではなく、契約などで変わった後の値（Recipe の今の値）。</summary>
        private static void WriteMapRecipe(BinaryWriter w, MapObject o)
        {
            Recipe recipe = null;
            try
            {
                CrafterFamObjectBase crafter = o as CrafterFamObjectBase;
                if (crafter != null) { recipe = crafter.OnGoingRecipe; }
            }
            catch (Exception) { recipe = null; }
            if (recipe == null || recipe.Result == null || recipe.Result.Key == null)
            {
                w.Write(false);
                return;
            }
            w.Write(true);
            w.Write(recipe.RequireTime);
            w.Write(recipe.Result.Key.GetID() ?? "");
            w.Write((int)recipe.Result.Count);
            int count = recipe.Ingredients != null ? recipe.Ingredients.Count : 0;
            w.Write((ushort)count);
            for (int i = 0; i < count; i++)
            {
                w.Write(recipe.Ingredients[i] != null && recipe.Ingredients[i].Key != null ? (recipe.Ingredients[i].Key.GetID() ?? "") : "");
                w.Write(recipe.Ingredients[i] != null ? (int)recipe.Ingredients[i].Count : 0);
            }
        }

        /// <summary>版 7：コンベアや分岐が運ぶ速さ（毎秒の個数）。契約やポータルの効果で変わるので、その時点の値を控える。
        /// 本体がカーソルを載せたときに出している値と同じ式（FactoryFamTransportItemPerSecProvider.MaxSpeedPerSec）。
        /// 上の「毎秒いくつ」は載っているアイテムごとの値で、空のコンベアでは出ないため、速さだけ別に持つ。</summary>
        private static void WriteMapSpeed(BinaryWriter w, MapObject o)
        {
            float speed = 0f;
            try
            {
                FactoryFamObject factory = o as FactoryFamObject;
                if (factory != null) { speed = new FactoryFamTransportItemPerSecProvider(factory).MaxSpeedPerSec; }
            }
            catch (Exception) { speed = 0f; }
            w.Write(speed);
        }

        /// <summary>版 6：その物に掛かっているエンチャント（採掘ポイントのマゾヒズムなど）。効果の ID だけ控える。</summary>
        private static void WriteMapEnchants(BinaryWriter w, MapObject o)
        {
            List<string> ids = new List<string>();
            try
            {
                foreach (Enchant.EnchantStateSnapshot state in o.CaptureEnchantsForClone())
                {
                    if (!string.IsNullOrEmpty(state.Application.EffectID)) { ids.Add(state.Application.EffectID); }
                }
            }
            catch (Exception) { ids.Clear(); }
            w.Write((ushort)ids.Count);
            for (int i = 0; i < ids.Count; i++) { w.Write(ids[i]); }
        }

        private static readonly ItemPerSecInfo[] RateBuffer = new ItemPerSecInfo[32];

        /// <summary>版 5：その時点の「毎秒いくつ」の理論値。本体がカーソルを載せたときの情報に出している値を、
        /// 同じ部品（UI.Information.ItemPerSec の Provider）で計算して控える。自前で式を書くと、採掘ボーナスや
        /// 確定クリティカルなどを取りこぼす。部品の選び方は本体（CursorHitInfoManager）と同じ:
        /// クラフトできる使い魔は 消費と生産、採掘の使い魔は 生産、レイヴンとポータルは 輸送。
        /// コンベアは数が多いので控えない。種類は 0 = 生産・1 = 消費・2 = 輸送。</summary>
        private static void WriteMapRates(BinaryWriter w, MapObject o)
        {
            List<KeyValuePair<byte, ItemPerSecInfo>> rates = new List<KeyValuePair<byte, ItemPerSecInfo>>();
            try
            {
                ICraftAble craft = o as ICraftAble;
                WorkerObject worker = o as WorkerObject;
                TransporterObject raven = o as TransporterObject;
                QuestPortalObject portal = o as QuestPortalObject;
                if (craft != null)
                {
                    CollectRates(rates, 1, new CraftConsumeItemPerSecProvider(craft));
                    CollectRates(rates, 0, new CraftProduceItemPerSecProvider(craft));
                }
                else if (worker != null) { CollectRates(rates, 0, new WorkerProduceItemPerSecProvider(worker)); }
                else if (raven != null) { CollectRates(rates, 2, new TransporterItemPerSecProvider(raven)); }
                else if (portal != null) { CollectRates(rates, 2, new QuestPortalTransportItemPerSecProvider(portal)); }
            }
            catch (Exception) { rates.Clear(); }

            w.Write((ushort)rates.Count);
            for (int i = 0; i < rates.Count; i++)
            {
                w.Write(rates[i].Key);
                w.Write(rates[i].Value.Key ?? "");
                w.Write(rates[i].Value.PerSec);
            }
        }

        private static void CollectRates(List<KeyValuePair<byte, ItemPerSecInfo>> into, byte kind, IItemPerSecProvider provider)
        {
            int count = provider.WriteItems(RateBuffer);
            for (int i = 0; i < count && i < RateBuffer.Length; i++)
            {
                if (RateBuffer[i].PerSec > 0f) { into.Add(new KeyValuePair<byte, ItemPerSecInfo>(kind, RateBuffer[i])); }
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) { return false; }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) { return false; }
            }
            return true;
        }

        /// <summary>控えた配置をファイルへ書く。グラフの記録を残すときに一緒に呼ぶ。</summary>
        private void SaveMapShots()
        {
            // 終わりの配置。Run を離れた後だと本体の盤面はもう無いので、撮れなければそのまま
            CaptureMap();
            if (_mapShots.Count == 0) { return; }

            try
            {
                string dir = GraphDirectory();
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, _runStartUtc.ToString(GraphStamp, CultureInfo.InvariantCulture) + MapSuffix);
                string temp = path + ".tmp";
                using (FileStream file = new FileStream(temp, FileMode.Create, FileAccess.Write))
                using (GZipStream zip = new GZipStream(file, CompressionMode.Compress))
                using (BinaryWriter w = new BinaryWriter(zip))
                {
                    w.Write(MapFileVersion);
                    w.Write(_mapShots.Count);
                    for (int i = 0; i < _mapShots.Count; i++)
                    {
                        w.Write(_mapShotTimes[i]);
                        w.Write(_mapShotRepaid[i]);
                        w.Write(_mapShots[i].Length);
                        w.Write(_mapShots[i]);
                    }
                }
                if (File.Exists(path)) { File.Delete(path); }
                File.Move(temp, path);
                Logger.LogInfo("[map] saved " + _mapShots.Count + " snapshots: " + Path.GetFileName(path)
                    + "  " + new FileInfo(path).Length + " bytes");
            }
            catch (Exception e)
            {
                Logger.LogWarning("[map] cannot save the factory: " + e);
            }
        }
    }
}
