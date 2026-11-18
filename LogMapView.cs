// 業務日誌の画面の続き（partial）：工場の配置図。
//
// MapLog.cs が残した `.map.gz` を読み、選んだ回（返済のたび・Run の終わり）の配置を描く。
//
// 重ね方（下から）:
//   Base    土地・コンベア・荷下ろし地点・ポータルなどの四角（1枚のメッシュ。終盤はコンベアが数千本になるので
//           1本ずつ Image にしない）
//   Symbols 使い魔と採取物を色の四角で（引いて見るとき用）
//   Icons   使い魔の顔・レシピの絵・採取物の絵・ポータルが求めている物の絵（寄って見るとき用）
//   Over    レイヴンから荷下ろし地点への線
// 引いて見ると顔の絵は潰れて見分けられないので、1マスが一定より小さくなったら Icons を消して Symbols を出す。
// ホイールで拡大・ドラッグで移動。マスに載せると、左下の箱にその物の名前・レシピ・要求・効果が出る。
// 回の切り替えは上の段の数字・＜＞・左右の矢印キー。切り替えても拡大と位置は保つ
// （そのために、描く範囲は「その Run で最後までに買った土地すべて」で固定している）。
//
// ポータルの達成回数は記録に無い。本体は達成のたびに目標の数を定数倍する（Quest.TryResolveCurrentStage。
// 倍率は難易度がヘル未満なら 2、以上なら 3）ので、出現時の目標（本体の Run の記録にある）との比から数える。
//
// 向きは本体の値のまま入っている（西 0・北 1・東 2・南 3）。コンベアは向きの逆へ流れる（MapLog.cs）。
// 荷下ろし地点は本体の顔アイコンがレイヴンと同じなので、顔は使わず枠だけの四角にしている。

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Fams;
using Fams.EditIfFamAdd;
using GameState;
using Harvestable;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Utility.Localization;

namespace LwfEconomyGraph
{
    internal static partial class LogView
    {
        internal const string MapSuffix = ".map.gz";
        private const float CellSize = 28f;

        /// <summary>1マスがこの大きさ（画面の基準で）より小さくなったら、絵をやめて色の四角にする</summary>
        private const float IconMinCell = 20f;

        private sealed class MapLand
        {
            public int X, Y, Biome;
            public bool Own;
        }

        /// <summary>その時点の理論値（本体が計算した「毎秒いくつ」）。Kind は 0 = 生産・1 = 消費・2 = 輸送</summary>
        private sealed class MapRate
        {
            public int Kind;
            public string Id;
            public float PerSec;
        }

        private sealed class MapDemand
        {
            public string Id;
            public int Current, Goal;
            public string Type = "", Text = "";
        }

        private sealed class MapObj
        {
            public string Kind, Name, Recipe;
            public int Fam, Dir, Harvest;
            public List<Vector2Int> Cells = new List<Vector2Int>();
            public bool HasLink;
            public Vector2Int Link;
            public string Id = "", Reward = "", Description = "";
            public int RewardCount;
            public bool Done;
            public int Rarity = -1, Patron = -1;
            public List<MapDemand> Demands = new List<MapDemand>();
            public List<MapRate> Rates = new List<MapRate>();
            public List<string> Enchants = new List<string>();
            public float Speed;
            // 版 8：その時点のレシピの中身（契約などで変わった後の値）。無ければ RecipeResult は null
            public float RecipeTime;
            public string RecipeResult;
            public int RecipeResultCount;
            public List<KeyValuePair<string, int>> RecipeIngredients = new List<KeyValuePair<string, int>>();
        }

        private sealed class MapShot
        {
            public float Time;
            public int Repaid;
            public List<MapLand> Lands = new List<MapLand>();
            public List<MapObj> Objs = new List<MapObj>();
        }

        private static List<MapShot> _mapShots = new List<MapShot>();
        private static int _mapStep;
        // 再生（回を一定の間隔で順に送る）。画面は回ごとに組み直すので、状態はここに持つ
        private static bool _mapPlaying;
        private static float _mapPlayNext;
        private const float PlayInterval = 0.7f;
        private static readonly Color32 EnchantColor = new Color32(255, 96, 176, 255);
        private static RectTransform _mapPane;

        // 回を切り替えても同じ場所を見続けるための控え（Run を選び直したら捨てる）
        private static RectTransform _mapContent;
        // 引きずりと拡大を受ける枠と、その中身の入れ物。回を切り替えても作り直さない（BuildMap の冒頭を見よ）
        private static RectTransform _mapFrame;
        private static RectTransform _mapFrameContent;
        private static float _mapScale;
        private static Vector2 _mapPosition;

        // ポータルの達成回数を数えるための、出現時の目標の数と倍率（選んだ Run の記録から）
        private static readonly Dictionary<string, int> PortalSpawnGoals = new Dictionary<string, int>();
        private static int _portalMultiplier = 3;

        /// <summary>Run を選び直したときに呼ぶ。本体の記録から、ポータルの出現時の目標を控える。</summary>
        private static void ResetMapFor(Newtonsoft.Json.Linq.JObject json)
        {
            _mapContent = null;
            _mapScale = 0f;
            PortalSpawnGoals.Clear();
            _portalMultiplier = 3;
            if (json == null) { return; }
            try
            {
                Difficulty difficulty = (Difficulty)Enum.Parse(typeof(Difficulty), RunData.Str(json, "difficultyId"));
                _portalMultiplier = difficulty < Difficulty.Hell1 ? 2 : 3;
            }
            catch (Exception) { }
            foreach (Newtonsoft.Json.Linq.JObject instance in Objects(json, "uniqueObjectInstances"))
            {
                string id = RunData.Str(instance, "objectId");
                if (id == null) { continue; }
                foreach (Newtonsoft.Json.Linq.JObject demand in Objects(instance, "demands"))
                {
                    int goal = (int)RunData.Long(demand, "goalCount");
                    int known;
                    if (goal > 0 && (!PortalSpawnGoals.TryGetValue(id, out known) || goal < known)) { PortalSpawnGoals[id] = goal; }
                    break;
                }
            }
        }

        /// <summary>ポータルが要求を達成した回数。数えられなければ -1。</summary>
        private static int PortalCompletions(MapObj o)
        {
            int spawn;
            if (o.Kind != "QuestPortalObject" || o.Demands.Count == 0) { return -1; }
            if (!PortalSpawnGoals.TryGetValue(o.Id, out spawn) || spawn <= 0) { return -1; }
            int goal = o.Demands[0].Goal;
            if (goal < spawn) { return -1; }
            return Mathf.RoundToInt(Mathf.Log((float)goal / spawn) / Mathf.Log(_portalMultiplier));
        }

        // 自前で決めた色（本体に土地の色の表は無い）。並びは BiomeType：森・リゾート・崖・草原・沼・タイガ・火山
        private static readonly Color32[] BiomeColors = new Color32[]
        {
            new Color32(58, 92, 60, 255), new Color32(70, 120, 130, 255), new Color32(110, 100, 90, 255),
            new Color32(96, 122, 62, 255), new Color32(70, 84, 74, 255), new Color32(120, 136, 140, 255),
            new Color32(120, 62, 50, 255)
        };

        // 西・北・東・南（画面の向き。y は下が正）
        private static readonly Vector2[] DirVectors = new Vector2[]
        {
            new Vector2(-1f, 0f), new Vector2(0f, -1f), new Vector2(1f, 0f), new Vector2(0f, 1f)
        };

        // ---------------------------------------------------------------
        // 読む
        // ---------------------------------------------------------------

        private static List<MapShot> LoadMap(string path)
        {
            List<MapShot> shots = new List<MapShot>();
            if (path == null) { return shots; }
            try
            {
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (GZipStream zip = new GZipStream(file, CompressionMode.Decompress))
                using (BinaryReader r = new BinaryReader(zip))
                {
                    int version = r.ReadInt32();
                    if (version < 1 || version > 8) { throw new InvalidDataException("version " + version); }
                    int count = r.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        MapShot shot = new MapShot();
                        shot.Time = r.ReadSingle();
                        shot.Repaid = r.ReadInt32();
                        ParseShot(shot, r.ReadBytes(r.ReadInt32()), version);
                        // 持っている土地が1つも無い回は捨てる（Run の始まりに、土地が組まれる前に撮ってしまった回。
                        // 2026-11-18 の途中までの記録にある。出すと最初の回が真っ黒になる）
                        bool anyOwn = false;
                        for (int k = 0; k < shot.Lands.Count && !anyOwn; k++) { anyOwn = shot.Lands[k].Own; }
                        if (!anyOwn) { continue; }
                        // 同じ瞬間の回は1つにまとめ、後のほうを残す（記録する側と同じ決め。まとめる前の版で
                        // 書いたファイルでは、最後の返済と Run の終わりが同じ数字で2つ並んでいた）
                        if (shots.Count > 0
                            && Mathf.Abs(shot.Time - shots[shots.Count - 1].Time) < EconomyGraphPlugin.SameMoment)
                        {
                            shots[shots.Count - 1] = shot;
                        }
                        else { shots.Add(shot); }
                    }
                }
            }
            catch (Exception e)
            {
                EconomyGraphPlugin.Log.LogWarning("[view] cannot read " + Path.GetFileName(path) + ": " + e.Message);
                shots.Clear();
            }
            return shots;
        }

        private static void ParseShot(MapShot shot, byte[] bytes, int version)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(bytes)))
            {
                int lands = r.ReadInt32();
                for (int i = 0; i < lands; i++)
                {
                    MapLand land = new MapLand();
                    land.X = r.ReadInt16();
                    land.Y = r.ReadInt16();
                    land.Biome = r.ReadInt32();
                    land.Own = r.ReadBoolean();
                    shot.Lands.Add(land);
                }
                int objects = r.ReadInt32();
                for (int i = 0; i < objects; i++)
                {
                    MapObj o = new MapObj();
                    o.Kind = r.ReadString();
                    o.Name = r.ReadString();
                    o.Fam = r.ReadInt32();
                    o.Dir = r.ReadInt32();
                    o.Recipe = r.ReadString();
                    o.Harvest = r.ReadInt32();
                    int cells = r.ReadUInt16();
                    for (int c = 0; c < cells; c++)
                    {
                        int x = r.ReadInt16();
                        int y = r.ReadInt16();
                        o.Cells.Add(new Vector2Int(x, y));
                    }
                    if (version >= 2)
                    {
                        int linkX = r.ReadInt16();
                        int linkY = r.ReadInt16();
                        o.HasLink = linkX != short.MinValue;
                        o.Link = new Vector2Int(linkX, linkY);
                        o.Id = r.ReadString();
                        o.Rarity = r.ReadInt32();
                        o.Patron = r.ReadInt32();
                        o.Reward = r.ReadString();
                        int demands = r.ReadUInt16();
                        for (int d = 0; d < demands; d++)
                        {
                            MapDemand demand = new MapDemand();
                            demand.Id = r.ReadString();
                            demand.Current = r.ReadInt32();
                            demand.Goal = r.ReadInt32();
                            o.Demands.Add(demand);
                        }
                    }
                    if (version >= 3)
                    {
                        o.RewardCount = r.ReadInt32();
                        o.Description = r.ReadString();
                        o.Done = r.ReadBoolean();
                    }
                    if (version >= 4)
                    {
                        for (int d = 0; d < o.Demands.Count; d++)
                        {
                            o.Demands[d].Type = r.ReadString();
                            o.Demands[d].Text = r.ReadString();
                        }
                    }
                    if (version >= 5)
                    {
                        int rates = r.ReadUInt16();
                        for (int k = 0; k < rates; k++)
                        {
                            MapRate rate = new MapRate();
                            rate.Kind = r.ReadByte();
                            rate.Id = r.ReadString();
                            rate.PerSec = r.ReadSingle();
                            o.Rates.Add(rate);
                        }
                    }
                    if (version >= 6)
                    {
                        int enchants = r.ReadUInt16();
                        for (int k = 0; k < enchants; k++) { o.Enchants.Add(r.ReadString()); }
                    }
                    if (version >= 7) { o.Speed = r.ReadSingle(); }
                    if (version >= 8 && r.ReadBoolean())
                    {
                        o.RecipeTime = r.ReadSingle();
                        o.RecipeResult = r.ReadString();
                        o.RecipeResultCount = r.ReadInt32();
                        int ingredients = r.ReadUInt16();
                        for (int k = 0; k < ingredients; k++)
                        {
                            string key = r.ReadString();
                            o.RecipeIngredients.Add(new KeyValuePair<string, int>(key, r.ReadInt32()));
                        }
                    }
                    shot.Objs.Add(o);
                }
            }
        }

        // ---------------------------------------------------------------
        // 描く
        // ---------------------------------------------------------------

        private static void BuildMap()
        {
            // 組み直す前に、いま見ている拡大と位置を控える
            if (_mapContent != null)
            {
                _mapScale = _mapContent.localScale.x;
                _mapPosition = _mapContent.anchoredPosition;
            }
            _mapContent = null;
            // 枠（引きずり・拡大を受ける部品）は、回を切り替えても作り直さず、中の絵だけ入れ替える。
            // 作り直すと、引きずっている最中の部品が消えて、再生で回が切り替わるたびに引きずりが途切れる
            RectTransform kept = _mapShots.Count > 0 ? _mapFrame : null;
            for (int i = _mapPane.childCount - 1; i >= 0; i--)
            {
                Transform child = _mapPane.GetChild(i);
                if (kept != null && child == kept) { continue; }
                child.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(child.gameObject);
            }
            if (kept == null) { _mapFrame = null; _mapFrameContent = null; }
            if (_mapShots.Count == 0) { return; }
            if (_mapStep < 0 || _mapStep >= _mapShots.Count) { _mapStep = _mapShots.Count - 1; }
            MapShot shot = _mapShots[_mapStep];

            // 上の段：マス目の切り替えと、全体に戻すボタン。回の選択は下の再生の帯（BuildPlayer）
            RectTransform steps = Row(_mapPane, 36);
            steps.anchorMin = new Vector2(0f, 1f);
            steps.anchorMax = new Vector2(1f, 1f);
            steps.pivot = new Vector2(0f, 1f);
            steps.anchoredPosition = Vector2.zero;
            steps.sizeDelta = new Vector2(0f, 36f);
            GridButton(steps);
            Gap(steps, 6f);
            FitButton(steps);

            // 描く範囲は、この Run で最後までに買った土地すべてを囲む四角。
            // 回ごとに変えると、切り替えるたびに同じ工場が違う場所へ動いてしまう
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            for (int k = 0; k < _mapShots.Count; k++)
            {
                List<MapLand> all = _mapShots[k].Lands;
                for (int i = 0; i < all.Count; i++)
                {
                    MapLand land = all[i];
                    if (!land.Own) { continue; }
                    minX = Math.Min(minX, land.X);
                    maxX = Math.Max(maxX, land.X);
                    minY = Math.Min(minY, land.Y);
                    maxY = Math.Max(maxY, land.Y);
                }
            }
            if (minX > maxX)
            {
                if (_mapFrameContent != null) { Clear(_mapFrameContent); }
                return;
            }
            float s = CellSize;
            float width = (maxX - minX + 1) * 20 * s;
            float height = (maxY - minY + 1) * 20 * s;

            RectTransform frame = _mapFrame;
            RectTransform content = _mapFrameContent;
            if (frame == null || content == null)
            {
                frame = Rect("MapScroll", _mapPane);
                Stretch(frame);
                frame.offsetMax = new Vector2(0f, -40f);
                // ドラッグとホイールは絵のある所でしか受けられない
                frame.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);
                frame.gameObject.AddComponent<RectMask2D>();

                content = Rect("Content", frame);
                content.anchorMin = new Vector2(0f, 1f);
                content.anchorMax = new Vector2(0f, 1f);
                // 軸は中身の中央。枠より小さいとき、ScrollRect は軸の位置を基準に中身を収めるので、
                // 左上の軸だと左上に寄る（横長の工場は下が、縦長の工場は右が空く）。中央の軸なら枠の真ん中に来る
                content.pivot = new Vector2(0.5f, 0.5f);

                ScrollRect scroll = frame.gameObject.AddComponent<ScrollRect>();
                scroll.content = content;
                scroll.viewport = frame;
                scroll.horizontal = true;
                scroll.vertical = true;
                // 動かせる範囲は縛らない。縛る（Clamped）と、中身が枠より小さい間は ScrollRect が毎フレーム
                // 真ん中へ戻すので、「カーソルの下を動かさずに拡大」が効かず、拡大がいつも真ん中へ向かってしまう。
                // 見失ったら、上の段の「全体」ボタンで戻せる
                scroll.movementType = ScrollRect.MovementType.Unrestricted;
                scroll.inertia = false;
                scroll.scrollSensitivity = 0f;   // ホイールは拡大に使う（MapZoom）
                _mapFrame = frame;
                _mapFrameContent = content;
            }
            else { Clear(content); }   // 前の回の絵だけ捨てる
            content.sizeDelta = new Vector2(width, height);

            MapMesh ground = Layer<MapMesh>("Base", content);
            MapMesh grid = Layer<MapMesh>("Grid", content);
            bool showGrid = EconomyGraphPlugin.ShowGrid.Value;
            MapMesh symbols = Layer<MapMesh>("Symbols", content);
            RectTransform icons = Rect("Icons", content);
            Stretch(icons);
            MapMesh over = Layer<MapMesh>("Over", content);

            // 土地
            List<Vector2> landCenters = new List<Vector2>();
            for (int i = 0; i < shot.Lands.Count; i++)
            {
                MapLand land = shot.Lands[i];
                if (!land.Own) { continue; }
                Color32 color = (land.Biome >= 0 && land.Biome < BiomeColors.Length)
                    ? BiomeColors[land.Biome] : new Color32(80, 80, 80, 255);
                float x = (land.X - minX) * 20 * s;
                float y = (maxY - land.Y) * 20 * s;
                ground.Quad(x, y, 20 * s, 20 * s, new Color32(0, 0, 0, 255));
                ground.Quad(x + 2f, y + 2f, 20 * s - 4f, 20 * s - 4f, color);
                // 土地の真ん中（中身の軸＝中央からの位置・y は上が正）。ダブルクリックで寄る先になる
                landCenters.Add(new Vector2(x + 10 * s - width * 0.5f, height * 0.5f - (y + 10 * s)));
                if (showGrid)
                {
                    // マス目の線（土地の中の 19 本ずつ）
                    Color32 line = new Color32(0, 0, 0, 56);
                    for (int k = 1; k < 20; k++)
                    {
                        grid.Quad(x + k * s - 0.5f, y + 2f, 1f, 20 * s - 4f, line);
                        grid.Quad(x + 2f, y + k * s - 0.5f, 20 * s - 4f, 1f, line);
                    }
                }
            }

            // 物
            Dictionary<Vector2Int, MapObj> byCell = new Dictionary<Vector2Int, MapObj>();
            Color32 belt = new Color32(40, 44, 52, 255);
            Color32 flow = new Color32(120, 200, 255, 255);
            Color32 fastBelt = new Color32(24, 74, 44, 255);
            Color32 fastFlow = new Color32(120, 240, 130, 255);
            Color32 white = new Color32(255, 255, 255, 255);
            Color32 exitColor = new Color32(235, 235, 245, 255);
            Sprite exitSprite = AnySprite("Summon-TransporterExit");
            for (int i = 0; i < shot.Objs.Count; i++)
            {
                MapObj o = shot.Objs[i];
                bool conveyor = o.Kind == "ConveyorObject" || o.Kind == "Conveyor2Object";
                // 高速コンベア（本体の型は Conveyor2Object）は緑
                bool fast = o.Kind == "Conveyor2Object";
                bool exit = o.Kind == "TransporterExitObject";
                bool harvest = o.Kind == "HarvestableObject";
                Sprite face = null;
                if (harvest) { face = HarvestSprite(o.Harvest); }
                else if (!conveyor && !exit && o.Fam > 0) { face = FaceSprite(o.Fam); }

                for (int c = 0; c < o.Cells.Count; c++)
                {
                    byCell[o.Cells[c]] = o;
                    float x = (o.Cells[c].x - minX * 20) * s;
                    float y = (maxY * 20 + 19 - o.Cells[c].y) * s;
                    if (conveyor)
                    {
                        ground.Quad(x + 2f, y + 2f, s - 4f, s - 4f, fast ? fastBelt : belt);
                        if (o.Dir >= 0 && o.Dir < 4) { ground.Arrow(x, y, s, DirVectors[(o.Dir + 2) % 4], fast ? fastFlow : flow); }
                        continue;
                    }
                    if (exit)
                    {
                        // 寄ったときは荷下ろし地点そのものの絵（本体の顔アイコンはレイヴンと同じ絵なので使わない）。
                        // 引いたとき・絵が引けないときは枠だけの四角
                        if (exitSprite != null)
                        {
                            MapIcon(icons, exitSprite, x + 1f, y + 1f, s - 2f);
                            symbols.Frame(x + 3f, y + 3f, s - 6f, s - 6f, 4f, exitColor);
                        }
                        else { ground.Frame(x + 3f, y + 3f, s - 6f, s - 6f, 4f, exitColor); }
                        continue;
                    }
                    if (harvest)
                    {
                        symbols.Quad(x + 7f, y + 7f, s - 14f, s - 14f, new Color32(170, 170, 170, 255));
                        if (face != null) { MapIcon(icons, face, x + 1f, y + 1f, s - 2f); }
                        else { ground.Quad(x + 7f, y + 7f, s - 14f, s - 14f, new Color32(170, 170, 170, 255)); }
                        continue;
                    }
                    if (o.Fam > 0)
                    {
                        // 引いて見るときの印。使い魔は白い縁の丸（四角の置き物・コンベアと形で分ける。地面や近い色の
                        // 物に埋もれないよう縁を付ける。2026-11-18）。分岐はコンベアの仲間なので四角のまま
                        bool round = o.Fam < 100;
                        if (round) { FamDisc(symbols, x, y, s, FamColor(o.Fam)); }
                        else { symbols.Quad(x + 2f, y + 2f, s - 4f, s - 4f, FamColor(o.Fam)); }
                        // 分岐コンベアの絵は「左が入口・右へ出る」向きで描かれている。本体の向きは入口の側
                        // （IOPattern.MainInOtherOut）なので、絵の左が入口の側に来るよう回す。
                        // 向きは西 0 から時計回りに数えるので、時計回りに 90 度 × 向き（Unity の角度は反時計回りが正）
                        bool splitter = o.Fam == 103 || o.Fam == 104;
                        float turn = (splitter && o.Dir >= 0 && o.Dir < 4) ? -90f * o.Dir : 0f;
                        // 寄ったときの絵は、マップ上のちびキャラを向きつきで（LogFamFigure.cs）。作れなければ顔アイコン
                        Figure figure = splitter ? null : FamFigure(o.Fam, o.Dir);
                        if (figure != null) { MapIcon(icons, figure.Sprite, x, y, s, 0f, figure.Flip); }
                        else if (face != null) { MapIcon(icons, face, x + 1f, y + 1f, s - 2f, turn); }
                        else if (round) { FamDisc(ground, x, y, s, FamColor(o.Fam)); }
                        else { ground.Quad(x + 2f, y + 2f, s - 4f, s - 4f, FamColor(o.Fam)); }
                        // 使い魔の向きの印（縁の白い点）は付けない。種類ごとに「向き」の意味が違って読み方が揃わず、
                        // 無くても配置から分かる（2026-11-17 ユーザー判断で一旦撤去。向きの値は記録に残っている）
                        continue;
                    }
                    ground.Quad(x + 2f, y + 2f, s - 4f, s - 4f, KindColor(o.Kind));
                }

                // エンチャント（マゾヒズムなど）が掛かっている物は、マスを桃色の枠で囲む。引いても寄っても見える層に描く
                if (o.Enchants.Count > 0)
                {
                    for (int c = 0; c < o.Cells.Count; c++)
                    {
                        float ex = (o.Cells[c].x - minX * 20) * s;
                        float ey = (maxY * 20 + 19 - o.Cells[c].y) * s;
                        over.Frame(ex + 1f, ey + 1f, s - 2f, s - 2f, 4f, EnchantColor);
                    }
                }

                if (o.Cells.Count == 0) { continue; }
                float fx = (o.Cells[0].x - minX * 20) * s;
                float fy = (maxY * 20 + 19 - o.Cells[0].y) * s;

                // 回しているレシピ（使い魔の右下に小さく）
                if (!string.IsNullOrEmpty(o.Recipe))
                {
                    Sprite recipe = RecipeSprite(o.Recipe);
                    if (recipe != null) { MapIcon(icons, recipe, fx + s * 0.45f, fy + s * 0.45f, s * 0.6f); }
                }
                // 上に載せる絵。依頼看板は手に入る物（契約の絵・レシピの成果物・アイテムや資源）、
                // ポータルは求めている物（何を納めるポータルかが分かるように）
                Sprite mark = null;
                if (o.Kind == "TradeObject") { mark = TradeRewardSprite(o); }
                if (mark == null && o.Demands.Count > 0) { mark = AnySprite(o.Demands[0].Id); }
                if (mark != null) { MapIcon(icons, mark, fx + 3f, fy + 3f, s - 6f); }
                // ポータルが要求を達成した回数（右下に数字）
                int completions = PortalCompletions(o);
                if (completions > 0)
                {
                    TextMeshProUGUI badge = NewText(icons, completions.ToString(), 15, Color.white);
                    badge.fontStyle = FontStyles.Bold;
                    badge.alignment = TextAlignmentOptions.BottomRight;
                    badge.overflowMode = TextOverflowModes.Overflow;
                    badge.outlineWidth = 0.25f;
                    badge.outlineColor = new Color32(0, 0, 0, 255);
                    RectTransform br = badge.rectTransform;
                    br.anchorMin = new Vector2(0f, 1f);
                    br.anchorMax = new Vector2(0f, 1f);
                    br.pivot = new Vector2(0f, 1f);
                    br.anchoredPosition = new Vector2(fx, -fy);
                    br.sizeDelta = new Vector2(s, s);
                }
                // レイヴンの行き先
                if (o.HasLink)
                {
                    Vector2 from = new Vector2(fx + s * 0.5f, fy + s * 0.5f);
                    Vector2 to = new Vector2((o.Link.x - minX * 20) * s + s * 0.5f, (maxY * 20 + 19 - o.Link.y) * s + s * 0.5f);
                    over.Line(from, to, 3f, new Color32(235, 235, 245, 200));
                }
            }

            // 最初は全体が入る大きさ。1マスが大きくなりすぎる狭い工場は等倍で止める
            Vector2 view = frame.rect.size;
            if (view.x < 10f || view.y < 10f) { view = new Vector2(1752f, 840f); }
            float fit = Mathf.Min(1f, Mathf.Min(view.x / width, view.y / height));
            if (_mapScale > 0f)
            {
                // 回を切り替えただけなら、見ていた拡大と位置のまま
                content.localScale = new Vector3(_mapScale, _mapScale, 1f);
                content.anchoredPosition = _mapPosition;
            }
            else
            {
                content.localScale = new Vector3(fit, fit, 1f);
                content.anchoredPosition = new Vector2(view.x * 0.5f, -view.y * 0.5f);   // 枠の真ん中
            }
            _mapContent = content;
            MapZoom zoom = frame.GetComponent<MapZoom>();
            if (zoom == null) { zoom = frame.gameObject.AddComponent<MapZoom>(); }
            zoom.Content = content;
            zoom.Min = fit;
            // 拡大の上限。全体図が分かれば足りるので、絵が見分けられる大きさを少し超える所で止める
            zoom.Max = 1.25f;
            zoom.Icons = icons.gameObject;
            zoom.Symbols = symbols.gameObject;
            zoom.IconScale = IconMinCell / s;
            zoom.LandCenters = landCenters;
            zoom.Reset = delegate
            {
                _mapContent = null;   // 控えた拡大と位置を捨てて組み直すと、全体・真ん中に戻る
                _mapScale = 0f;
                BuildMap();
            };
            zoom.Apply();

            // 載せている物の説明（左下の箱。中身が無いときは消す）
            RectTransform box = Rect("Info", _mapPane);
            box.anchorMin = Vector2.zero;
            box.anchorMax = Vector2.zero;
            box.pivot = Vector2.zero;
            box.anchoredPosition = new Vector2(12f, 76f);
            box.sizeDelta = new Vector2(620f, 0f);
            Image boxBack = box.gameObject.AddComponent<Image>();
            boxBack.color = new Color(0f, 0f, 0f, 0.85f);
            boxBack.raycastTarget = false;
            VerticalLayoutGroup boxLayout = box.gameObject.AddComponent<VerticalLayoutGroup>();
            boxLayout.padding = new RectOffset(14, 14, 10, 10);
            boxLayout.childControlWidth = true;
            boxLayout.childControlHeight = true;
            boxLayout.childForceExpandHeight = false;
            box.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            boxLayout.spacing = 2f;
            box.gameObject.SetActive(false);

            MapHover hover = frame.GetComponent<MapHover>();
            if (hover == null) { hover = frame.gameObject.AddComponent<MapHover>(); }
            hover.Invalidate();
            hover.Content = content;
            hover.Step = delegate(int by) { StepMap(by); };
            hover.Cell = s;
            hover.OriginX = minX * 20;
            hover.TopY = maxY * 20 + 19;
            hover.Show = delegate(int x, int y)
            {
                MapObj found;
                bool any = x != int.MinValue && byCell.TryGetValue(new Vector2Int(x, y), out found);
                if (any) { BuildInfo(box, byCell[new Vector2Int(x, y)]); }
                box.gameObject.SetActive(any);
            };
            // 回を切り替えたそのフレームのうちに、カーソルの下の説明を出し直す（ちらつき防止）
            hover.Refresh();

            BuildPactBox(shot);
            BuildPlayer();
        }

        /// <summary>課税の印の絵（ゲヘナの笑顔）。グラフ用に切り出した絵が読めていなければ、本体の顔アイコン。</summary>
        private static Sprite TaxFaceSprite()
        {
            Sprite face = null;
            try { face = EconomyGraphPlugin.Instance != null ? EconomyGraphPlugin.Instance.TaxFace() : null; }
            catch (Exception) { face = null; }
            return face ?? TaxPatronSprite((int)BaseSystem.Patron.Gehenna);
        }

        private const int PactBoxRows = 12;
        /// <summary>まだ取っていない契約を、いまの位置の下に何件見せるか</summary>
        private const int PactBoxAhead = 4;

        /// <summary>契約の年表（右上の箱）。その Run で取った契約を取った順（上が古い）に並べ、行を押すとその契約を
        /// 取った回へ飛ぶ。全部は入らないので、いまの位置の前後 PactBoxRows 件だけ出す（いまの位置の下に、
        /// まだ取っていない契約を PactBoxAhead 件）。
        /// 色: いまの返済の区切りで取ったものは見出しの色、それより前は普通、まだ取っていないものは薄く。
        /// 契約を取った時刻は返済の少し後になることがあるので、回の時刻に SameMoment だけ幅を持たせる。</summary>
        private static void BuildPactBox(MapShot shot)
        {
            List<Newtonsoft.Json.Linq.JObject> pacts = Objects(_json, "pactHistory");
            int pactCount = pacts.Count;
            // 課税も同じ年表に並べる（発動した時刻があるものだけ）。契約と同じ形の行にするため、同じ鍵に時刻を入れた
            // 仮の入れ物を作る。見出しの数（取った数/全部）には入れない
            foreach (Newtonsoft.Json.Linq.JObject tax in Objects(_json, "taxSlots"))
            {
                if (!RunData.Has(tax, "activatedAtElapsedGameplaySeconds")) { continue; }
                Newtonsoft.Json.Linq.JObject entry = new Newtonsoft.Json.Linq.JObject();
                entry["taxEffect"] = TaxEffect(tax);
                entry["taxSlot"] = RunData.Long(tax, "slotIndex");
                entry["acquiredAtElapsedGameplaySeconds"] = RunData.Num(tax, "activatedAtElapsedGameplaySeconds");
                pacts.Add(entry);
            }
            if (pacts.Count == 0) { return; }
            pacts.Sort(delegate(Newtonsoft.Json.Linq.JObject x, Newtonsoft.Json.Linq.JObject y)
            {
                return RunData.Num(x, "acquiredAtElapsedGameplaySeconds").CompareTo(RunData.Num(y, "acquiredAtElapsedGameplaySeconds"));
            });

            // いまの区切りが始まった時刻（返済回数がいまと同じになった最初の回）
            float since = 0f;
            for (int i = _mapStep; i >= 0 && _mapShots[i].Repaid == shot.Repaid; i--) { since = _mapShots[i].Time; }
            if (shot.Repaid == 0) { since = 0f; }

            int taken = 0;
            while (taken < pacts.Count
                && RunData.Num(pacts[taken], "acquiredAtElapsedGameplaySeconds") <= shot.Time + EconomyGraphPlugin.SameMoment)
            {
                taken++;
            }
            int pactsTaken = 0;
            for (int i = 0; i < taken; i++) { if (!RunData.Has(pacts[i], "taxEffect")) { pactsTaken++; } }

            const float rowHeight = 38f;   // 行 36 + 間 2
            float viewHeight = Mathf.Min(pacts.Count, PactBoxRows) * rowHeight;

            RectTransform box = Rect("Pacts", _mapPane);
            box.anchorMin = Vector2.one;
            box.anchorMax = Vector2.one;
            box.pivot = Vector2.one;
            box.anchoredPosition = new Vector2(-12f, -48f);
            box.sizeDelta = new Vector2(_pactBoxFolded ? 250f : 400f, 0f);
            Image back = box.gameObject.AddComponent<Image>();
            back.color = new Color(0f, 0f, 0f, 0.85f);
            VerticalLayoutGroup layout = box.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 8, 6, 6);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            layout.spacing = 2f;
            box.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // 見出し。右端のボタン（横棒＝たたむ・四角＝開く）で、一覧を出す・しまう
            RectTransform head = Row(box, 30);
            // 課税も並ぶが、見出しは「契約履歴」（本体の言語表に「履歴」だけの語が無い。数は契約の数。2026-11-18 ユーザー判断）
            Cell(head, Message("PactHistory") + "  " + pactsTaken + "/" + pactCount, 18, DimColor, -1f, false);
            RectTransform fold = Rect("Fold", head);
            LayoutElement fe = fold.gameObject.AddComponent<LayoutElement>();
            fe.minWidth = 28f;
            fe.preferredWidth = 28f;
            fe.minHeight = 28f;
            fe.preferredHeight = 28f;
            MapMesh foldMesh = fold.gameObject.AddComponent<MapMesh>();
            foldMesh.Quad(0f, 0f, 28f, 28f, new Color32(255, 255, 255, 24));
            if (_pactBoxFolded) { foldMesh.Frame(7f, 7f, 14f, 14f, 2f, TextColor); }
            else { foldMesh.Quad(7f, 18f, 14f, 3f, TextColor); }
            Button foldButton = fold.gameObject.AddComponent<Button>();
            foldButton.transition = Selectable.Transition.None;
            foldButton.targetGraphic = foldMesh;
            foldButton.onClick.AddListener(delegate
            {
                _pactBoxFolded = !_pactBoxFolded;
                BuildMap();
            });
            if (_pactBoxFolded) { return; }

            // 一覧（全部の契約。入りきらない分はホイールか引きずりで送る）
            RectTransform view = Rect("View", box);
            LayoutElement ve = view.gameObject.AddComponent<LayoutElement>();
            ve.minHeight = viewHeight;
            ve.preferredHeight = viewHeight;
            view.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);   // ホイールと引きずりを受ける面
            view.gameObject.AddComponent<RectMask2D>();
            RectTransform list = Rect("List", view);
            list.anchorMin = new Vector2(0f, 1f);
            list.anchorMax = new Vector2(1f, 1f);
            list.pivot = new Vector2(0.5f, 1f);
            list.sizeDelta = Vector2.zero;
            VerticalLayoutGroup listLayout = list.gameObject.AddComponent<VerticalLayoutGroup>();
            listLayout.childControlWidth = true;
            listLayout.childControlHeight = true;
            listLayout.childForceExpandHeight = false;
            listLayout.spacing = 2f;
            list.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            ScrollRect scroll = view.gameObject.AddComponent<ScrollRect>();
            scroll.content = list;
            scroll.viewport = view;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;
            scroll.inertia = false;

            for (int i = 0; i < pacts.Count; i++)
            {
                string id = RunData.Str(pacts[i], "pactId");
                float at = (float)RunData.Num(pacts[i], "acquiredAtElapsedGameplaySeconds");
                bool done = i < taken;
                bool fresh = done && at >= since - EconomyGraphPlugin.SameMoment;
                Color color = fresh ? HeadColor : (done ? TextColor : new Color(DimColor.r, DimColor.g, DimColor.b, 0.45f));

                RectTransform row = Row(list, 36);
                Image hit = row.gameObject.AddComponent<Image>();
                hit.color = new Color(1f, 1f, 1f, fresh ? 0.08f : 0f);
                if (RunData.Has(pacts[i], "taxEffect"))
                {
                    // 課税の行：絵はゲヘナの笑顔、名前は赤（まだの分は薄く）。字の位置は契約の行とそろえる
                    Color taxColor = done ? LoseColor : new Color(LoseColor.r, LoseColor.g, LoseColor.b, 0.45f);
                    Icon(row, TaxFaceSprite(), 32);
                    Cell(row, TaxName(RunData.Str(pacts[i], "taxEffect")), 18, taxColor, 262f, false);
                }
                else
                {
                    Icon(row, PactSprite(_run, id), 32);
                    Cell(row, PactName(id), 18, color, 262f, false);
                }
                Cell(row, Clock(at), 15, done ? DimColor : color, 56f, true);

                // いまの位置の印（取った契約と、まだの契約の境目）。行の上の端に線を引く
                if (i == taken && taken > 0)
                {
                    RectTransform line = Rect("Now", row);
                    line.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                    line.anchorMin = new Vector2(0f, 1f);
                    line.anchorMax = new Vector2(1f, 1f);
                    line.pivot = new Vector2(0.5f, 1f);
                    line.anchoredPosition = new Vector2(0f, 1f);
                    line.sizeDelta = new Vector2(0f, 2f);
                    Image li = line.gameObject.AddComponent<Image>();
                    li.color = HeadColor;
                    li.raycastTarget = false;
                }

                // 押すと、その契約を取った回（取った時刻を含む最初の回）へ飛ぶ
                int target = _mapShots.Count - 1;
                for (int k = 0; k < _mapShots.Count; k++)
                {
                    if (at <= _mapShots[k].Time + EconomyGraphPlugin.SameMoment) { target = k; break; }
                }
                Button button = row.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.targetGraphic = hit;
                int captured = target;
                button.onClick.AddListener(delegate
                {
                    _mapPlaying = false;
                    if (captured == _mapStep) { return; }
                    _mapStep = captured;
                    BuildMap();
                });
            }

            // 回を切り替えるたびに作り直すので、送りの位置もここで決める：いまの位置の下に、まだ取っていない契約が
            // PactBoxAhead 件見える所。手で送った位置は、次に回が変わるまで保たれる
            float bottom = Mathf.Min(pacts.Count, taken + PactBoxAhead) * rowHeight;
            float most = Mathf.Max(0f, pacts.Count * rowHeight - viewHeight);
            // 先に大きさを確定させる。確定前（中身の高さ 0）のまま位置を入れると、ScrollRect が範囲外とみなして先頭へ戻す
            LayoutRebuilder.ForceRebuildLayoutImmediate(box);
            LayoutRebuilder.ForceRebuildLayoutImmediate(list);
            list.anchoredPosition = new Vector2(0f, Mathf.Clamp(bottom - viewHeight, 0f, most));
        }

        /// <summary>契約の年表をたたんでいるか（見出しだけにする）。画面を閉じるまで覚えておく</summary>
        private static bool _pactBoxFolded;

        // ---------------------------------------------------------------
        // 再生の帯（画面の下の真ん中）。動画の再生と同じ並び：
        //   前の回・再生／一時停止・次の回・シークバー・返済回数・その回の時刻 / Run の長さ
        // シークバーは時間に比例。目盛りが各回（返済した時刻）で、押す・引きずると、いちばん近い回へ移る。
        // ---------------------------------------------------------------

        private const float SeekWidth = 460f;
        private static RectTransform _seekTrack;
        private static bool _seekDragging;

        /// <summary>最後の回が返済ではなく Run の終わりに撮ったもの（破産・中断）か。返済で終わった Run は、
        /// 最後の返済と終わりが同じ瞬間で1回にまとまっているので false。</summary>
        private static bool IsEndingShot(int i)
        {
            int before = i > 0 ? _mapShots[i - 1].Repaid : 0;
            return i == _mapShots.Count - 1 && _mapShots[i].Repaid == before;
        }

        /// <summary>再生で、いまの回を出しておく秒数。次の回までのゲーム内の時間に比例させる（速さは設定 PlaybackSpeed。
        /// 既定は 100 倍速）。100 倍速のとき 0.2〜PlayInterval 秒に収め、30 秒おきの回は 0.3 秒、返済だけの古い記録は
        /// 長くても PlayInterval。速さを変えると、この下限と上限も同じ割合で縮む・伸びる。</summary>
        private static float PlayWait()
        {
            float speed = Mathf.Clamp(EconomyGraphPlugin.PlaybackSpeed.Value, 10, 1000);
            float scale = 100f / speed;
            if (_mapStep >= _mapShots.Count - 1) { return PlayInterval * scale; }
            return Mathf.Clamp((_mapShots[_mapStep + 1].Time - _mapShots[_mapStep].Time) / speed, 0.2f * scale, PlayInterval * scale);
        }

        /// <summary>シークバーの上の位置（0〜1）。左端は最初の回、右端は最後の回。
        /// 2026-11-18 より前の記録は最初の回が 0:00 ではない（最初の返済か 0:30）ので、0 秒を左端にすると
        /// つまみが左端まで届かない。</summary>
        private static float SeekPosition(float time)
        {
            float from = _mapShots[0].Time;
            float span = PlayTotal() - from;
            return span <= 0.01f ? 1f : Mathf.Clamp01((time - from) / span);
        }

        private static float PlayTotal()
        {
            return Mathf.Max(1f, _mapShots[_mapShots.Count - 1].Time);
        }

        private static void BuildPlayer()
        {
            MapShot shot = _mapShots[_mapStep];
            float total = PlayTotal();

            RectTransform bar = Row(_mapPane, 52);
            bar.anchorMin = new Vector2(0.5f, 0f);
            bar.anchorMax = new Vector2(0.5f, 0f);
            bar.pivot = new Vector2(0.5f, 0f);
            bar.anchoredPosition = new Vector2(0f, 12f);
            bar.sizeDelta = new Vector2(0f, 52f);
            bar.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            bar.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.88f);
            HorizontalLayoutGroup layout = bar.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(14, 18, 0, 0);
            layout.spacing = 8f;

            RewindButton(bar);
            SkipButton(bar, -1);
            PlayButton(bar);
            SkipButton(bar, 1);
            Gap(bar, 6f);

            // シークバー
            RectTransform track = Rect("Seek", bar);
            LayoutElement le = track.gameObject.AddComponent<LayoutElement>();
            le.minWidth = SeekWidth;
            le.preferredWidth = SeekWidth;
            le.minHeight = 36f;
            le.preferredHeight = 36f;
            MapMesh mesh = track.gameObject.AddComponent<MapMesh>();
            Color32 accent = HeadColor;
            float knob = SeekWidth * SeekPosition(shot.Time);
            mesh.Quad(0f, 16f, SeekWidth, 4f, new Color32(255, 255, 255, 60));
            mesh.Quad(0f, 16f, knob, 4f, accent);
            // 目盛りは返済した回だけ（合間に 30 秒ごとに撮った回は、目盛りなしで通る）
            for (int i = 0; i < _mapShots.Count; i++)
            {
                if (_mapShots[i].Repaid <= (i > 0 ? _mapShots[i - 1].Repaid : 0)) { continue; }
                float x = SeekWidth * SeekPosition(_mapShots[i].Time);
                mesh.Quad(x - 1f, 11f, 2f, 14f, i <= _mapStep ? accent : new Color32(255, 255, 255, 130));
            }
            // 課税が発動した時刻の目盛りは赤（返済の目盛りと色で分ける）
            foreach (Newtonsoft.Json.Linq.JObject tax in Objects(_json, "taxSlots"))
            {
                if (!RunData.Has(tax, "activatedAtElapsedGameplaySeconds")) { continue; }
                float at = (float)RunData.Num(tax, "activatedAtElapsedGameplaySeconds");
                Color32 red = LoseColor;
                if (at > shot.Time + EconomyGraphPlugin.SameMoment) { red.a = 150; }
                mesh.Quad(SeekWidth * SeekPosition(at) - 1.5f, 8f, 3f, 20f, red);
            }
            mesh.Quad(knob - 5f, 6f, 10f, 24f, new Color32(255, 255, 255, 255));
            track.gameObject.AddComponent<SeekBar>();
            _seekTrack = track;
            Gap(bar, 6f);

            // いま何回目か（破産・中断で終わった最後の回は、結果の語）と、その回の時刻 / Run の長さ
            bool ending = IsEndingShot(_mapStep);
            Cell(bar, ending ? ResultText(_run) : Message("Repayment") + " " + shot.Repaid,
                SmallFont, ending ? ResultColor(_run) : TextColor, 170f, false);
            Cell(bar, Clock(shot.Time) + " / " + Clock(total), SmallFont, DimColor, 150f, true);
            // 速さは時刻の右。回を送るボタン（左の三角）と離し、形も − ＋ にして、送るボタンと取り違えないようにする
            Gap(bar, 10f);
            SpeedButton(bar);
        }

        internal static void OnSeekDown()
        {
            _mapPlaying = false;
            _seekDragging = true;
        }

        /// <summary>シークバーを押している間、カーソルの位置にいちばん近い回へ移る。押している間は true。
        /// 画面は回ごとに組み直すので、押下は帯の部品ではなくここ（毎フレーム呼ばれる）で追う。</summary>
        internal static bool TickSeek()
        {
            if (!_seekDragging) { return false; }
            Mouse mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.isPressed || _seekTrack == null || _mapShots.Count == 0)
            {
                _seekDragging = false;
                return false;
            }
            Vector2 local;
            Rect area = _seekTrack.rect;
            if (area.width < 1f
                || !RectTransformUtility.ScreenPointToLocalPointInRectangle(_seekTrack, mouse.position.ReadValue(), null, out local))
            {
                return true;
            }
            float time = Mathf.Lerp(_mapShots[0].Time, PlayTotal(), Mathf.Clamp01((local.x - area.xMin) / area.width));
            int nearest = 0;
            for (int i = 1; i < _mapShots.Count; i++)
            {
                if (Mathf.Abs(_mapShots[i].Time - time) < Mathf.Abs(_mapShots[nearest].Time - time)) { nearest = i; }
            }
            if (nearest != _mapStep)
            {
                _mapStep = nearest;
                BuildMap();
            }
            return true;
        }

        /// <summary>最初の回へ戻るボタン（縦棒と、左向きの三角2つ）。最初の回では薄くして押せなくする。再生は止める。</summary>
        private static void RewindButton(RectTransform row)
        {
            RectTransform r = Rect("Rewind", row);
            LayoutElement le = r.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 38f;
            le.preferredWidth = 38f;
            le.minHeight = 32f;
            le.preferredHeight = 32f;
            bool usable = _mapStep > 0;
            MapMesh mesh = r.gameObject.AddComponent<MapMesh>();
            Color32 mark = usable ? TextColor : new Color(1f, 1f, 1f, 0.2f);
            mesh.Quad(0f, 0f, 38f, 32f, new Color32(255, 255, 255, 24));
            mesh.Quad(5f, 9f, 3f, 14f, mark);
            mesh.Arrow(4f, 4f, 24f, new Vector2(-1f, 0f), mark);
            mesh.Arrow(14f, 4f, 24f, new Vector2(-1f, 0f), mark);
            if (!usable) { return; }
            Button button = r.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = mesh;
            button.onClick.AddListener(delegate
            {
                _mapPlaying = false;
                _mapStep = 0;
                BuildMap();
            });
        }

        /// <summary>再生の速さの段。100 がふつう（ゲーム内の時間の 100 倍速）で、帯には「×1」と出す</summary>
        private static readonly int[] PlaySpeeds = { 25, 50, 100, 200, 400 };

        private static string SpeedLabel(int speed)
        {
            return "\u00d7" + (speed / 100f).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>再生の速さ（「×1」）と、その左右の遅くする（−）・速くする（＋）ボタン。端の段では、その向きのボタンを薄くして
        /// 押せなくする。再生は止めない。</summary>
        private static void SpeedButton(RectTransform row)
        {
            int at = System.Array.IndexOf(PlaySpeeds, EconomyGraphPlugin.PlaybackSpeed.Value);
            if (at < 0) { at = System.Array.IndexOf(PlaySpeeds, 100); }
            SpeedStep(row, at, -1);
            // 幅は、いちばん長い段（×0.25）の字に合わせる。段を変えても帯の幅が動かない
            float width = 0f;
            foreach (int speed in PlaySpeeds) { width = Mathf.Max(width, TextWidth(SpeedLabel(speed), SmallFont, FontStyles.Normal)); }
            TextMeshProUGUI label = Cell(row, SpeedLabel(PlaySpeeds[at]), SmallFont, TextColor, Mathf.Ceil(width) + 4f, false);
            label.alignment = TextAlignmentOptions.Center;
            SpeedStep(row, at, 1);
        }

        private static void SpeedStep(RectTransform row, int at, int by)
        {
            RectTransform r = Rect("SpeedStep", row);
            LayoutElement le = r.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 24f;
            le.preferredWidth = 24f;
            le.minHeight = 32f;
            le.preferredHeight = 32f;
            int to = at + by;
            bool usable = to >= 0 && to < PlaySpeeds.Length;
            MapMesh mesh = r.gameObject.AddComponent<MapMesh>();
            Color32 mark = usable ? TextColor : new Color(1f, 1f, 1f, 0.2f);
            mesh.Quad(0f, 0f, 24f, 32f, new Color32(255, 255, 255, 24));
            // − と ＋
            mesh.Quad(6f, 15f, 12f, 2f, mark);
            if (by > 0) { mesh.Quad(11f, 10f, 2f, 12f, mark); }
            if (!usable) { return; }
            Button button = r.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = mesh;
            int next = PlaySpeeds[to];
            button.onClick.AddListener(delegate
            {
                EconomyGraphPlugin.PlaybackSpeed.Value = next;
                if (_mapPlaying) { _mapPlayNext = UnityEngine.Time.unscaledTime + PlayWait(); }
                BuildMap();
            });
        }

        /// <summary>前の回・次の回へ移るボタン（三角と縦棒）。端では薄くして押せなくする。</summary>
        private static void SkipButton(RectTransform row, int by)
        {
            RectTransform r = Rect("Skip", row);
            LayoutElement le = r.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 32f;
            le.preferredWidth = 32f;
            le.minHeight = 32f;
            le.preferredHeight = 32f;
            int to = _mapStep + by;
            bool usable = to >= 0 && to < _mapShots.Count;
            MapMesh mesh = r.gameObject.AddComponent<MapMesh>();
            Color32 mark = usable ? TextColor : new Color(1f, 1f, 1f, 0.2f);
            mesh.Quad(0f, 0f, 32f, 32f, new Color32(255, 255, 255, 24));
            mesh.Arrow(by > 0 ? -1f : 3f, 1f, 30f, new Vector2(by, 0f), mark);
            mesh.Quad(by > 0 ? 23f : 6f, 9f, 3f, 14f, mark);
            if (!usable) { return; }
            Button button = r.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = mesh;
            int captured = by;
            button.onClick.AddListener(delegate { StepMap(captured); });
        }

        /// <summary>再生の進み。回を送ったら true（画面を組み直したので、呼んだ側はそのフレームの残りをやめる）。</summary>
        internal static bool TickPlay()
        {
            if (!_mapPlaying || _mapPane == null || !_mapPane.gameObject.activeInHierarchy) { return false; }
            if (UnityEngine.Time.unscaledTime < _mapPlayNext) { return false; }
            if (_mapStep < _mapShots.Count - 1) { _mapStep++; }
            // 最後の回まで来たら止める
            if (_mapStep >= _mapShots.Count - 1) { _mapPlaying = false; }
            _mapPlayNext = UnityEngine.Time.unscaledTime + PlayWait();
            BuildMap();
            return true;
        }

        /// <summary>再生のボタン。止まっているときは三角、再生中は縦棒2本。最後の回で押すと最初から。</summary>
        private static void PlayButton(RectTransform row)
        {
            RectTransform r = Rect("Play", row);
            LayoutElement le = r.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 48f;
            le.preferredWidth = 48f;
            le.minHeight = 38f;
            le.preferredHeight = 38f;
            MapMesh mesh = r.gameObject.AddComponent<MapMesh>();
            bool usable = _mapShots.Count >= 2;
            Color32 fill = HeadColor;
            if (!usable) { fill.a = 70; }
            Color32 mark = new Color32(20, 16, 12, 255);
            mesh.Quad(0f, 0f, 48f, 38f, fill);
            if (_mapPlaying)
            {
                mesh.Quad(16f, 10f, 6f, 18f, mark);
                mesh.Quad(26f, 10f, 6f, 18f, mark);
            }
            else { mesh.Arrow(7f, 2f, 34f, new Vector2(1f, 0f), mark); }
            if (!usable) { return; }
            Button button = r.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = mesh;
            button.onClick.AddListener(delegate
            {
                _mapPlaying = !_mapPlaying;
                if (_mapPlaying)
                {
                    if (_mapStep >= _mapShots.Count - 1) { _mapStep = 0; }
                    _mapPlayNext = UnityEngine.Time.unscaledTime + PlayWait();
                }
                BuildMap();
            });
        }

        /// <summary>前後の回へ移る。端では止まる。手で送ったら再生は止める。</summary>
        private static void StepMap(int by)
        {
            _mapPlaying = false;
            int next = Mathf.Clamp(_mapStep + by, 0, _mapShots.Count - 1);
            if (next == _mapStep) { return; }
            _mapStep = next;
            BuildMap();
        }

        /// <summary>全体が入る大きさ・真ん中に戻すボタン（四隅の絵）。拡大や移動で見失ったとき用。</summary>
        private static void FitButton(RectTransform row)
        {
            RectTransform r = Rect("Fit", row);
            LayoutElement le = r.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 30f;
            le.preferredWidth = 30f;
            le.minHeight = 30f;
            le.preferredHeight = 30f;
            MapMesh mesh = r.gameObject.AddComponent<MapMesh>();
            Color32 color = TextColor;
            mesh.Quad(0f, 0f, 30f, 30f, new Color32(255, 255, 255, 12));
            // 四隅のかぎ（枠いっぱい、の絵）
            float[] xs = new float[] { 4f, 18f };
            float[] ys = new float[] { 4f, 18f };
            for (int i = 0; i < 2; i++)
            {
                for (int j = 0; j < 2; j++)
                {
                    float x = xs[i], y = ys[j];
                    mesh.Quad(x, j == 0 ? y : y + 6f, 8f, 2f, color);
                    mesh.Quad(i == 0 ? x : x + 6f, y, 2f, 8f, color);
                }
            }
            Button button = r.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = mesh;
            button.onClick.AddListener(delegate
            {
                _mapContent = null;   // 控えた拡大と位置を捨てて組み直すと、全体・真ん中に戻る
                _mapScale = 0f;
                BuildMap();
            });
        }

        /// <summary>マス目の線を出すかどうかのボタン（マス目の絵）。本体の言語表に当たる語が無いので絵にしている。</summary>
        private static void GridButton(RectTransform row)
        {
            bool on = EconomyGraphPlugin.ShowGrid.Value;
            RectTransform r = Rect("Grid", row);
            LayoutElement le = r.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 30f;
            le.preferredWidth = 30f;
            le.minHeight = 30f;
            le.preferredHeight = 30f;
            MapMesh mesh = r.gameObject.AddComponent<MapMesh>();
            Color32 color = on ? (Color32)TextColor : new Color32(255, 255, 255, 70);
            mesh.Quad(0f, 0f, 30f, 30f, new Color32(255, 255, 255, on ? (byte)40 : (byte)12));
            mesh.Frame(3f, 3f, 24f, 24f, 2f, color);
            mesh.Quad(10f, 3f, 2f, 24f, color);
            mesh.Quad(18f, 3f, 2f, 24f, color);
            mesh.Quad(3f, 10f, 24f, 2f, color);
            mesh.Quad(3f, 18f, 24f, 2f, color);
            Button button = r.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = mesh;
            button.onClick.AddListener(delegate
            {
                EconomyGraphPlugin.ShowGrid.Value = !EconomyGraphPlugin.ShowGrid.Value;
                BuildMap();
            });
        }

        private static T Layer<T>(string name, RectTransform content) where T : MaskableGraphic
        {
            RectTransform r = Rect(name, content);
            Stretch(r);
            T graphic = r.gameObject.AddComponent<T>();
            graphic.raycastTarget = false;
            return graphic;
        }

        private static void MapIcon(RectTransform parent, Sprite sprite, float x, float y, float size)
        {
            MapIcon(parent, sprite, x, y, size, 0f);
        }

        /// <summary>絵を1つ置く。angle は反時計回りの度数（絵の中心で回す）。</summary>
        private static void MapIcon(RectTransform parent, Sprite sprite, float x, float y, float size, float angle)
        {
            MapIcon(parent, sprite, x, y, size, angle, false);
        }

        private static void MapIcon(RectTransform parent, Sprite sprite, float x, float y, float size, float angle, bool flip)
        {
            RectTransform r = Rect("Icon", parent);
            r.anchorMin = new Vector2(0f, 1f);
            r.anchorMax = new Vector2(0f, 1f);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = new Vector2(x + size * 0.5f, -y - size * 0.5f);
            r.sizeDelta = new Vector2(size, size);
            if (angle != 0f) { r.localEulerAngles = new Vector3(0f, 0f, angle); }
            if (flip) { r.localScale = new Vector3(-1f, 1f, 1f); }
            Image image = r.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
        }

        private const int InfoFont = 18;
        private const int InfoIcon = 26;

        /// <summary>マスに載せたときの説明を、左下の箱に組む。上から 名前・レシピ・要求（いまの数/目標）・
        /// 達成回数・手に入る物・効果の文。レシピは字で書くと長くてはみ出すので、絵で並べる
        /// （使い魔の顔・材料の絵 x数 = 成果物の絵 x数）。</summary>
        private static void BuildInfo(RectTransform box, MapObj o)
        {
            Clear(box);

            string name;
            if (o.Kind == "HarvestableObject") { name = HarvestName(o.Harvest, o.Name); }
            else if (o.Fam > 0) { name = FamName(o.Fam, o.Name); }
            else { name = ObjectName(o.Name); }
            if (o.Done) { name += "  " + Message("Completed"); }
            // 名前の前に絵。使い魔はちびキャラの顔、採掘ポイントはその資源の絵（骨格の無いコンベアなどは字だけ）
            Sprite head = o.Kind == "HarvestableObject" ? HarvestSprite(o.Harvest) : (o.Fam > 0 ? FamHead(o.Fam) : null);
            if (head != null)
            {
                RectTransform title = Row(box, InfoIcon + 12);
                HorizontalLayoutGroup titleLayout = title.GetComponent<HorizontalLayoutGroup>();
                titleLayout.padding = new RectOffset(0, 0, 0, 0);
                titleLayout.spacing = 6f;
                Icon(title, head, InfoIcon + 10);
                Cell(title, name, InfoFont, HeadColor, 0f, false);
            }
            else { InfoText(box, name, HeadColor); }
            // 掛かっているエンチャント（マゾヒズムなど）
            for (int i = 0; i < o.Enchants.Count; i++)
            {
                string enchant = o.Enchants[i];
                InfoText(box, Safe(delegate { return StripRichText(Enchant.EnchantCatalog.GetName(enchant)); }, enchant), EnchantColor);
            }

            if (!string.IsNullOrEmpty(o.Recipe)) { InfoRecipe(box, "", o.Recipe, null, o); }

            // コンベアや分岐が運ぶ速さ（その時点の値。契約やポータルの効果で変わる）
            if (o.Speed > 0f)
            {
                string seconds = Safe(delegate { return LocalizedTextGetter.GetPactData("sec"); }, "s");
                RectTransform speed = Row(box, InfoIcon + 6);
                speed.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(0, 0, 0, 0);
                Cell(speed, StripRichText(Message("TransportAmount")), InfoFont, DimColor, 0f, false);
                Cell(speed, o.Speed.ToString("0.##") + "/" + seconds, InfoFont, TextColor, 0f, false);
            }

            // その時点の理論値（本体がカーソルを載せたときに出しているのと同じ計算）
            InfoRates(box, o, 1, Message("Consume"));
            InfoRates(box, o, 0, Message("Produce"));
            InfoRates(box, o, 2, StripRichText(Message("TransportAmount")));

            for (int i = 0; i < o.Demands.Count; i++)
            {
                MapDemand demand = o.Demands[i];
                if (demand.Type == "RecipeRemoveDemand")
                {
                    // レシピを要求する看板。そのレシピは依頼を完了すると消える（手に入るレシピと入れ替わる：
                    // RecipeRemoveDemand.OnComplete・TradeRecipeRewardExecutor）。本体はこの要求に文を持たせておらず
                    // （GetDescription が空）、看板の画面ではレシピの枠を置くだけで語を添えていない。
                    // 「消費」の語（本体の Consume）を付けて、レシピの中身を出す。
                    // （言語表に「前提レシピ : 」という語はあるが、本体のどこからも使われておらず、
                    //   意味も「持っていればよい」になって実際と違う。最初それを使って誤った。2026-11-17）
                    // 数は 1/1 か 0/1 にしかならないので出さず、持っていないときだけ「未完了」を添える
                    InfoRecipe(box, Message("Consume") + " :", demand.Id, demand.Current < demand.Goal ? Message("Unmet") : null);
                    continue;
                }
                // 要求は、本体が出している文があればそれを使う（何を求めているかは要求の種類で違い、
                // ID からは決められない）。無い古い記録では ID をアイテムか資源の名前として出す
                InfoDemand(box, demand);
            }

            int completions = PortalCompletions(o);
            if (completions >= 0) { InfoText(box, Message("Complete") + "  " + completions, TextColor); }

            // 何が手に入るか。ポータルは効果の文（下の Description）がそれを言っているので、依頼看板だけ足す
            if (o.Kind == "TradeObject" && !string.IsNullOrEmpty(o.Reward)) { InfoTradeReward(box, o); }
            if (!string.IsNullOrEmpty(o.Description)) { InfoText(box, StripRichText(o.Description), DimColor); }
        }

        // 本体の文の中の、名前の部分。「<link=ID>…名前…</link>」の形で入っている
        private static readonly System.Text.RegularExpressions.Regex LinkPattern =
            new System.Text.RegularExpressions.Regex("<link=([^>]+)>(.*?)</link>", System.Text.RegularExpressions.RegexOptions.Singleline);

        /// <summary>箱の中の要求の行。本体の文のうち、アイテムや資源の名前の所は絵に置き換える
        /// （「[食品の絵]の在庫が2000に到達  326 / 2,000」）。絵が引けない名前は字のまま。
        /// 文が無い古い記録では、絵（引けなければ名前）と数だけ。</summary>
        private static void InfoDemand(RectTransform box, MapDemand demand)
        {
            RectTransform row = Row(box, InfoIcon + 6);
            HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 2f;

            if (string.IsNullOrEmpty(demand.Text))
            {
                Sprite only = InfoSprite(demand.Id);
                if (only != null) { Icon(row, only, InfoIcon); }
                else { Cell(row, AnyName(demand.Id), InfoFont, TextColor, 0f, false); }
            }
            else
            {
                string text = demand.Text.Replace('\n', ' ');
                int at = 0;
                foreach (System.Text.RegularExpressions.Match m in LinkPattern.Matches(text))
                {
                    InfoPiece(row, text.Substring(at, m.Index - at));
                    Sprite sprite = InfoSprite(m.Groups[1].Value);
                    if (sprite != null) { Icon(row, sprite, InfoIcon); }
                    else { InfoPiece(row, m.Groups[2].Value); }
                    at = m.Index + m.Length;
                }
                InfoPiece(row, text.Substring(at));
            }
            Gap(row, 10f);
            Cell(row, demand.Current.ToString("N0") + " / " + demand.Goal.ToString("N0"), InfoFont, HeadColor, 0f, false);
        }

        private static void InfoPiece(RectTransform row, string raw)
        {
            string value = StripRichText(raw);
            if (value.Length > 0) { Cell(row, value, InfoFont, TextColor, 0f, false); }
        }

        /// <summary>箱の中の理論値の行。「生産  [絵] 0.75/秒  [絵] 0.05/秒」。その種類の値が無ければ行ごと出さない。</summary>
        private static void InfoRates(RectTransform box, MapObj o, int kind, string label)
        {
            RectTransform row = null;
            string seconds = Safe(delegate { return LocalizedTextGetter.GetPactData("sec"); }, "s");
            for (int i = 0; i < o.Rates.Count; i++)
            {
                MapRate rate = o.Rates[i];
                if (rate.Kind != kind) { continue; }
                if (row == null)
                {
                    row = Row(box, InfoIcon + 6);
                    HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
                    layout.padding = new RectOffset(0, 0, 0, 0);
                    layout.spacing = 4f;
                    Cell(row, label, InfoFont, DimColor, 0f, false);
                    Gap(row, 6f);
                }
                Icon(row, InfoSprite(rate.Id), InfoIcon);
                // 小さい値でも読めるよう、1未満は小数3桁まで
                string value = rate.PerSec >= 1f ? rate.PerSec.ToString("0.##") : rate.PerSec.ToString("0.###");
                Cell(row, value + "/" + seconds, InfoFont, TextColor, 0f, false);
                Gap(row, 8f);
            }
        }

        /// <summary>箱の中の字の行。長ければ折り返す。</summary>
        private static void InfoText(RectTransform box, string value, Color color)
        {
            TextMeshProUGUI t = NewText(box, value, InfoFont, color);
            t.alignment = TextAlignmentOptions.TopLeft;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Overflow;
        }

        /// <summary>箱の中のレシピの行。「頭の語・使い魔の顔・材料の絵 x数 … = 成果物の絵 x数・末尾の語」。
        /// recorded に版 8 の控え（その時点の中身）があればそれを出す。無ければ本体のレシピ表の元の値で、
        /// その Run で契約によって変わった後の数ではない（古い記録・依頼看板の報酬のレシピ）。
        /// 表に無いレシピは、名前の字だけにする。</summary>
        private static void InfoRecipe(RectTransform box, string head, string recipeID, string tail)
        {
            InfoRecipe(box, head, recipeID, tail, null);
        }

        /// <summary>レシピの行の「材料 ▶ 成果物」の三角。字（→ や ▶）は書体に入っていない言語だと □ になるので、絵で描く。</summary>
        private static bool IsNoIngredient(string key)
        {
            return string.IsNullOrEmpty(key) || key == "None";
        }

        private static void RecipeArrow(RectTransform row)
        {
            const float size = 18f;
            RectTransform r = Rect("Arrow", row);
            LayoutElement le = r.gameObject.AddComponent<LayoutElement>();
            le.minWidth = size;
            le.preferredWidth = size;
            le.minHeight = size;
            le.preferredHeight = size;
            MapMesh mesh = r.gameObject.AddComponent<MapMesh>();
            mesh.raycastTarget = false;
            mesh.Arrow(0f, 0f, size, new Vector2(1f, 0f), DimColor);
        }

        private static void InfoRecipe(RectTransform box, string head, string recipeID, string tail, MapObj recorded)
        {
            Items.Craft.RecipeDefinition definition = null;
            try { Items.Craft.RecipeDatabase.TryGetDefinition(recipeID, out definition); }
            catch (Exception) { definition = null; }
            if (definition == null)
            {
                InfoText(box, (head + " " + RecipeName(recipeID) + (tail != null ? "  " + tail : "")).Trim(), TextColor);
                return;
            }

            RectTransform row = Row(box, InfoIcon + 6);
            HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 4f;
            if (!string.IsNullOrEmpty(head)) { Cell(row, head, InfoFont, TextColor, 0f, false); }
            // 使い魔は顔アイコンではなく、ちびキャラの顔（この大きさだと顔アイコンは潰れて見分けにくい）。
            // 使い魔自身の説明の箱（recorded あり）では出さない。すぐ上の行に同じ使い魔の顔と名前がある
            if (recorded == null)
            {
                Icon(row, CrafterHead(definition.Crafter.ToString()) ?? CrafterSprite(definition.Crafter.ToString()), InfoIcon + 6);
                Gap(row, 4f);
            }
            if (recorded != null && recorded.RecipeResult != null)
            {
                int shown = 0;
                for (int i = 0; i < recorded.RecipeIngredients.Count; i++)
                {
                    // 本体は「材料なし」を None という名前の材料で表すことがある。それは材料に数えない
                    if (IsNoIngredient(recorded.RecipeIngredients[i].Key)) { continue; }
                    shown++;
                    Icon(row, InfoSprite(recorded.RecipeIngredients[i].Key), InfoIcon);
                    Cell(row, "x" + recorded.RecipeIngredients[i].Value.ToString("N0"), InfoFont, TextColor, 0f, false);
                    Gap(row, 4f);
                }
                // 材料の無いレシピ（何も使わずに出来る）は、三角を出さず成果物と秒数だけ。数が 0 の材料は残す（契約で消えたと分かる）
                if (shown > 0) { RecipeArrow(row); }
                Icon(row, InfoSprite(recorded.RecipeResult), InfoIcon);
                Cell(row, "x" + recorded.RecipeResultCount.ToString("N0"), InfoFont, TextColor, 0f, false);
                // 製造秒数。毎秒の数から逆算しなくて済むように。
                // レシピが持つ秒数（RecipeTime）は契約で変わった後の値だが、使い魔の側に掛かる速さの補正
                // （レリック「総裁の監視」の +50% など）は入っていない。本体が計算した毎秒の数にはそれが入っているので、
                // 「1回に使う数 ÷ 毎秒使う数」で実際の1回の秒数を出す。材料が無いレシピは成果物で同じ計算。
                // 毎秒の数が無い（止まっている）ときはレシピの秒数。
                float cycle = recorded.RecipeTime;
                bool measured = false;
                for (int i = 0; i < recorded.RecipeIngredients.Count && !measured; i++)
                {
                    if (recorded.RecipeIngredients[i].Value <= 0) { continue; }
                    for (int k = 0; k < recorded.Rates.Count; k++)
                    {
                        MapRate rate = recorded.Rates[k];
                        if (rate.Kind != 1 || rate.Id != recorded.RecipeIngredients[i].Key || rate.PerSec <= 0f) { continue; }
                        cycle = recorded.RecipeIngredients[i].Value / rate.PerSec;
                        measured = true;
                        break;
                    }
                }
                if (!measured && shown == 0 && recorded.RecipeResultCount > 0)
                {
                    for (int k = 0; k < recorded.Rates.Count; k++)
                    {
                        MapRate rate = recorded.Rates[k];
                        if (rate.Kind != 0 || rate.Id != recorded.RecipeResult || rate.PerSec <= 0f) { continue; }
                        cycle = recorded.RecipeResultCount / rate.PerSec;
                        break;
                    }
                }
                if (cycle > 0f)
                {
                    string seconds = Safe(delegate { return LocalizedTextGetter.GetPactData("sec"); }, "s");
                    Gap(row, 10f);
                    Cell(row, cycle.ToString("0.##") + seconds, InfoFont, DimColor, 0f, false);
                }
            }
            else
            {
                int shown = 0;
                foreach (Items.Craft.RecipeValueDefinition v in definition.Ingredients)
                {
                    if (IsNoIngredient(v.Key)) { continue; }
                    shown++;
                    Icon(row, InfoSprite(v.Key), InfoIcon);
                    Cell(row, "x" + v.Count.ToString("N0"), InfoFont, TextColor, 0f, false);
                    Gap(row, 4f);
                }
                if (shown > 0) { RecipeArrow(row); }
                Icon(row, InfoSprite(definition.Result.Key), InfoIcon);
                Cell(row, "x" + definition.Result.Count.ToString("N0"), InfoFont, TextColor, 0f, false);
            }
            if (tail != null)
            {
                Gap(row, 8f);
                Cell(row, tail, InfoFont, LoseColor, 0f, false);
            }
        }

        /// <summary>依頼看板で手に入るものの行。看板の種類（名前の鍵）で、報酬の ID が指すものが違う:
        /// TradePact = 契約・TradeRecipe = レシピ・TradeFam と TradeTag = アイテムか資源（数つき）。</summary>
        private static void InfoTradeReward(RectTransform box, MapObj o)
        {
            if (o.Name == "TradeRecipe")
            {
                InfoRecipe(box, StripRichText(Message("RewardNewRecipe")).Trim(), o.Reward, null);
                return;
            }
            RectTransform row = Row(box, InfoIcon + 6);
            HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 4f;
            if (o.Name == "TradePact")
            {
                Cell(row, Message("Pact") + " :", InfoFont, TextColor, 0f, false);
                Icon(row, PactSprite(o.Reward), InfoIcon);
                Cell(row, PactName(o.Reward), InfoFont, TextColor, 0f, false);
                return;
            }
            string get = Safe(delegate { return LocalizedTextGetter.GetPactData("Get"); }, ">");
            Cell(row, get + " :", InfoFont, TextColor, 0f, false);
            Icon(row, InfoSprite(o.Reward), InfoIcon);
            Cell(row, AnyName(o.Reward) + (o.RewardCount > 0 ? " x" + o.RewardCount.ToString("N0") : ""), InfoFont, TextColor, 0f, false);
        }

        /// <summary>依頼看板で手に入るものの絵。引けなければ null。</summary>
        private static Sprite TradeRewardSprite(MapObj o)
        {
            if (string.IsNullOrEmpty(o.Reward)) { return null; }
            if (o.Name == "TradePact") { return PactSprite(o.Reward); }
            if (o.Name == "TradeRecipe") { return RecipeSprite(o.Reward); }
            return AnySprite(o.Reward);
        }

        private static string FamName(int famType, string fallback)
        {
            return Safe(delegate { return StripRichText(LocalizedTextGetter.GetCharacterData((FamType)famType)); }, fallback);
        }

        private static string HarvestName(int type, string fallback)
        {
            return Safe(delegate { return StripRichText(LocalizedTextGetter.GetHarvestableData((HarvestableType)type, false)); }, fallback);
        }

        private static string ObjectName(string key)
        {
            return Safe(delegate { return StripRichText(LocalizedTextGetter.GetMapObjectData(key)); }, key);
        }

        private static Sprite FaceSprite(int famType)
        {
            try { return FamTypes.GetFaceIcon((FamType)famType); }
            catch (Exception) { return null; }
        }

        private static Sprite HarvestSprite(int type)
        {
            try { return HarvestableDB.Instance != null ? HarvestableDB.Instance.GetIcon((HarvestableType)type) : null; }
            catch (Exception) { return null; }
        }

        /// <summary>使い魔の印：白い縁の丸（マスいっぱい）。</summary>
        private static void FamDisc(MapMesh mesh, float x, float y, float s, Color32 color)
        {
            Vector2 mid = new Vector2(x + s * 0.5f, y + s * 0.5f);
            mesh.Disc(mid, s * 0.5f - 1f, new Color32(255, 255, 255, 255));
            mesh.Disc(mid, s * 0.5f - 1f - Mathf.Max(2f, s * 0.09f), color);
        }

        // 引いて見るときの使い魔の色（FamType の番号。色はキャラクターの印象に合わせる。2026-11-18 ユーザー指定）
        private static Color32 FamColor(int famType)
        {
            switch (famType)
            {
                case 2: return new Color32(80, 190, 90, 255);   // ゴブリン（採掘）：緑
                case 3: return new Color32(230, 200, 90, 255);   // 世話：黄
                case 4: return new Color32(160, 100, 230, 255);   // レイヴン（輸送）：紫
                case 5: return new Color32(255, 130, 190, 255);   // アルラウネ（農耕）：桃色
                case 6: return new Color32(70, 120, 235, 255);   // スライム（蒸留スライム＝本体の型は Smelter）：青
                case 7: return new Color32(110, 215, 240, 255);   // ウィッチ（召喚）：水色
                case 8: return new Color32(225, 70, 60, 255);   // サラマンダー（鍛冶師サラマンダー＝本体の型は Crafter）：赤
                default: return new Color32(150, 160, 170, 255);  // 分岐など
            }
        }

        private static Color32 KindColor(string kind)
        {
            // 納品箱（ゲヘナ）もポータルなので、ポータルと同じ色にする
            if (kind == "DeliveryBox" || kind == "QuestPortalObject") { return new Color32(200, 90, 200, 255); }
            if (kind == "TradeObject") { return new Color32(90, 200, 190, 255); }
            if (kind == "TelephoneObject") { return new Color32(230, 120, 60, 255); }
            if (kind == "SafeObject" || kind == "SafeOpenedObject") { return new Color32(240, 220, 120, 255); }
            return new Color32(150, 160, 170, 255);
        }
    }

    /// <summary>シークバー。押されたことを画面に伝える（その後の追従は LogView.TickSeek）。</summary>
    internal sealed class SeekBar : MonoBehaviour, UnityEngine.EventSystems.IPointerDownHandler
    {
        public void OnPointerDown(UnityEngine.EventSystems.PointerEventData e) { LogView.OnSeekDown(); }
    }

    /// <summary>色付きの三角形の集まりを1枚で描く。座標は枠の左上が原点・y は下が正（画素）。</summary>
    internal sealed class MapMesh : MaskableGraphic
    {
        private readonly List<Vector2> _points = new List<Vector2>();
        private readonly List<Color32> _colors = new List<Color32>();

        // uGUI の1枚のメッシュは頂点 65000 まで。超える分は描かない
        private const int Limit = 64000;

        internal void Triangle(Vector2 a, Vector2 b, Vector2 c, Color32 color)
        {
            if (_points.Count + 3 > Limit) { return; }
            _points.Add(a);
            _points.Add(b);
            _points.Add(c);
            _colors.Add(color);
            SetVerticesDirty();
        }

        /// <summary>塗りつぶした星（5つの角）。outer は角の先まで、inner はくぼみまでの半径。上は y が小さい側。</summary>
        internal void Star(Vector2 mid, float outer, float inner, Color32 color)
        {
            Vector2[] points = new Vector2[10];
            for (int i = 0; i < 10; i++)
            {
                float a = Mathf.PI * 2f * i / 10f - Mathf.PI * 0.5f;
                float radius = (i % 2 == 0) ? outer : inner;
                points[i] = mid + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            }
            for (int i = 0; i < 10; i++) { Triangle(mid, points[i], points[(i + 1) % 10], color); }
        }

        /// <summary>塗りつぶした丸（16 角形）。</summary>
        internal void Disc(Vector2 mid, float radius, Color32 color)
        {
            const int sides = 16;
            Vector2 before = mid + new Vector2(radius, 0f);
            for (int i = 1; i <= sides; i++)
            {
                float a = Mathf.PI * 2f * i / sides;
                Vector2 next = mid + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
                Triangle(mid, before, next, color);
                before = next;
            }
        }

        internal void Quad(float x, float y, float w, float h, Color32 color)
        {
            Vector2 a = new Vector2(x, y), b = new Vector2(x + w, y), c = new Vector2(x + w, y + h), d = new Vector2(x, y + h);
            Triangle(a, b, c, color);
            Triangle(c, d, a, color);
        }

        /// <summary>中を抜いた四角（枠だけ）。</summary>
        internal void Frame(float x, float y, float w, float h, float thick, Color32 color)
        {
            Quad(x, y, w, thick, color);
            Quad(x, y + h - thick, w, thick, color);
            Quad(x, y + thick, thick, h - thick * 2f, color);
            Quad(x + w - thick, y + thick, thick, h - thick * 2f, color);
        }

        internal void Line(Vector2 a, Vector2 b, float width, Color32 color)
        {
            Vector2 along = b - a;
            if (along.sqrMagnitude < 0.01f) { return; }
            along.Normalize();
            Vector2 side = new Vector2(-along.y, along.x) * (width * 0.5f);
            Triangle(a - side, a + side, b + side, color);
            Triangle(b + side, b - side, a - side, color);
        }

        /// <summary>マスの中に、direction の向きを指す三角を描く。</summary>
        internal void Arrow(float x, float y, float size, Vector2 direction, Color32 color)
        {
            Vector2 mid = new Vector2(x + size * 0.5f, y + size * 0.5f);
            Vector2 side = new Vector2(-direction.y, direction.x);
            Triangle(mid + direction * size * 0.34f,
                mid - direction * size * 0.14f + side * size * 0.24f,
                mid - direction * size * 0.14f - side * size * 0.24f, color);
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = GetPixelAdjustedRect();
            for (int i = 0; i + 2 < _points.Count; i += 3)
            {
                Color32 color = _colors[i / 3];
                for (int k = 0; k < 3; k++)
                {
                    Vector2 p = _points[i + k];
                    vh.AddVert(new Vector3(r.xMin + p.x, r.yMax - p.y, 0f), color, Vector4.zero);
                }
                vh.AddTriangle(i, i + 1, i + 2);
            }
        }
    }

    /// <summary>ホイールで中身を拡大・縮小する（移動は同じ枠の ScrollRect のドラッグ）。
    /// 引いたら絵（Icons）を消して色の四角（Symbols）に替える。</summary>
    internal sealed class MapZoom : MonoBehaviour, IScrollHandler, IPointerClickHandler
    {

        internal RectTransform Content;
        internal float Min = 0.1f;
        internal float Max = 2f;
        internal GameObject Icons;
        internal GameObject Symbols;
        internal float IconScale = 0.7f;

        internal void Apply()
        {
            if (Content == null) { return; }
            bool near = Content.localScale.x >= IconScale;
            if (Icons != null && Icons.activeSelf != near) { Icons.SetActive(near); }
            if (Symbols != null && Symbols.activeSelf == near) { Symbols.SetActive(!near); }
        }

        public void OnScroll(PointerEventData data)
        {
            if (Content == null) { return; }
            float before = Content.localScale.x;
            float after = Mathf.Clamp(before * (data.scrollDelta.y > 0f ? 1.2f : 1f / 1.2f), Min, Max);
            if (Mathf.Approximately(before, after)) { return; }

            // カーソルの下の場所が動かないように、拡大のぶんだけ中身をずらす
            RectTransform view = transform as RectTransform;
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(view, data.position, data.pressEventCamera, out local))
            {
                local = Vector2.zero;
            }
            Vector2 anchor = new Vector2(local.x - view.rect.xMin, local.y - view.rect.yMax);   // 枠の左上から
            Vector2 offset = Content.anchoredPosition;
            Vector2 point = (anchor - offset) / before;                                           // 中身の中の場所
            Content.localScale = new Vector3(after, after, 1f);
            Content.anchoredPosition = anchor - point * after;
            Apply();
        }

        /// <summary>土地の真ん中の一覧（中身の軸からの位置）。ダブルクリックで寄る先</summary>
        internal List<Vector2> LandCenters;
        /// <summary>全体が入る大きさ・真ん中に戻す</summary>
        internal Action Reset;

        private float _lastClick = -1f;
        private PointerEventData.InputButton _lastButton;

        /// <summary>左のダブルクリック: いちばん近い土地の真ん中を枠の真ん中に持ってきて、上限まで寄る。
        /// 押した場所そのものに寄ると、土地の無い所を押したときに何も無い黒い所へ寄ってしまう。
        /// 右のダブルクリック: 拡大をやめて全体に戻す。</summary>
        public void OnPointerClick(PointerEventData data)
        {
            if (Content == null) { return; }
            // 引きずって離したときも、uGUI は「クリック」を送ってくる（押した部品と引きずる部品が同じとき）。
            // 本体の数え方（clickCount）はそれも1回に数えるので、続けて2回引きずるとダブルクリック扱いになった。
            // 動かさずに離したものだけを自前で数える
            bool moved = data.dragging || (data.position - data.pressPosition).sqrMagnitude > 36f;
            float now = Time.unscaledTime;
            bool second = !moved && data.button == _lastButton && now - _lastClick < 0.4f;
            _lastClick = moved || second ? -1f : now;
            _lastButton = data.button;
            if (!second) { return; }
            if (data.button == PointerEventData.InputButton.Right)
            {
                if (Reset != null) { Reset(); }
                return;
            }
            if (data.button != PointerEventData.InputButton.Left) { return; }
            RectTransform view = transform as RectTransform;
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(view, data.position, data.pressEventCamera, out local)) { return; }
            float before = Content.localScale.x;
            float after = Max;   // 1回で上限まで寄る
            Vector2 anchor = new Vector2(local.x - view.rect.xMin, local.y - view.rect.yMax);   // 枠の左上から
            Vector2 point = (anchor - Content.anchoredPosition) / before;                        // 中身の中の場所
            if (LandCenters != null && LandCenters.Count > 0)
            {
                Vector2 nearest = LandCenters[0];
                for (int i = 1; i < LandCenters.Count; i++)
                {
                    if ((LandCenters[i] - point).sqrMagnitude < (nearest - point).sqrMagnitude) { nearest = LandCenters[i]; }
                }
                point = nearest;
            }
            Vector2 middle = new Vector2(view.rect.width * 0.5f, -view.rect.height * 0.5f);
            Content.localScale = new Vector3(after, after, 1f);
            Content.anchoredPosition = middle - point * after;
            Apply();
        }
    }

    /// <summary>カーソルの下のマスを調べて、その物の説明を上の段に出す。</summary>
    internal sealed class MapHover : MonoBehaviour
    {
        internal RectTransform Content;
        internal float Cell = 28f;
        internal int OriginX;   // 中身の左端のマスの番地（x）
        internal int TopY;      // 中身の上端のマスの番地（y）
        /// <summary>カーソルの下のマスが変わったら呼ぶ（マスの番地。枠の外なら int.MinValue）</summary>
        internal Action<int, int> Show;
        internal Action<int> Step;

        private int _lastX = int.MinValue, _lastY = int.MinValue;

        /// <summary>回を切り替えた後に呼ぶ。同じマスに載せたままでも、新しい回の中身で説明を出し直させる。</summary>
        internal void Invalidate()
        {
            _lastX = int.MaxValue;
            _lastY = int.MaxValue;
        }

        // 本体のマップビューと同じく、移動キーは土地の半分（10 マス）ずつ動かす。
        // 押した瞬間に1回、押し続けると少し待ってから繰り返す
        private const int PanCells = 10;
        private const float RepeatWait = 0.35f;
        private const float RepeatEvery = 0.12f;
        private Vector2 _held;
        private float _nextRepeat;

        /// <summary>本体の移動キー（キー設定で変えた後のもの。既定は WASD）でマップを動かす。
        /// 押した向きへ視点が動く＝中身は逆へ動く。</summary>
        private void Pan(Keyboard kb)
        {
            if (Content == null) { return; }
            Vector2 move = MoveInput(kb);
            Vector2 direction = new Vector2(
                move.x > 0.5f ? 1f : (move.x < -0.5f ? -1f : 0f),
                move.y > 0.5f ? 1f : (move.y < -0.5f ? -1f : 0f));
            if (direction == Vector2.zero) { _held = direction; return; }

            float now = Time.unscaledTime;
            if (direction != _held)
            {
                _held = direction;
                _nextRepeat = now + RepeatWait;
            }
            else if (now >= _nextRepeat) { _nextRepeat = now + RepeatEvery; }
            else { return; }

            Content.anchoredPosition -= direction * (PanCells * Cell * Content.localScale.x);
        }

        /// <summary>本体の移動の入力。執務室では本体の移動の操作が止まっていて値が読めないことがあるので、
        /// そのときは操作に割り当ててあるキーを直に見る。本体から取れなければ WASD。</summary>
        private static Vector2 MoveInput(Keyboard kb)
        {
            try
            {
                InputAction action = global::Input.PlayerAction.PlayerActionMap.Instance.GetMoveAction();
                if (action != null)
                {
                    if (action.enabled)
                    {
                        Vector2 value = action.ReadValue<Vector2>();
                        if (value.sqrMagnitude > 0.01f) { return value; }
                    }
                    Vector2 fromKeys = Vector2.zero;
                    foreach (InputBinding binding in action.bindings)
                    {
                        if (binding.isComposite || string.IsNullOrEmpty(binding.effectivePath)) { continue; }
                        UnityEngine.InputSystem.Controls.ButtonControl control =
                            InputSystem.FindControl(binding.effectivePath) as UnityEngine.InputSystem.Controls.ButtonControl;
                        if (control == null || !control.isPressed) { continue; }
                        string part = (binding.name ?? "").ToLowerInvariant();
                        if (part == "up") { fromKeys.y += 1f; }
                        else if (part == "down") { fromKeys.y -= 1f; }
                        else if (part == "left") { fromKeys.x -= 1f; }
                        else if (part == "right") { fromKeys.x += 1f; }
                    }
                    return fromKeys;
                }
            }
            catch (Exception) { }

            if (kb == null) { return Vector2.zero; }
            Vector2 fallback = Vector2.zero;
            if (kb.wKey.isPressed) { fallback.y += 1f; }
            if (kb.sKey.isPressed) { fallback.y -= 1f; }
            if (kb.aKey.isPressed) { fallback.x -= 1f; }
            if (kb.dKey.isPressed) { fallback.x += 1f; }
            return fallback;
        }

        private void Update()
        {
            // シークバーを引きずっている間は、カーソルに近い回へ
            if (LogView.TickSeek()) { return; }
            // 再生中なら、間隔ごとに次の回へ
            if (LogView.TickPlay()) { return; }

            // 左右の矢印キーで前後の回へ
            Keyboard kb = Keyboard.current;
            if (kb != null && Step != null)
            {
                if (kb.leftArrowKey.wasPressedThisFrame) { Step(-1); return; }
                if (kb.rightArrowKey.wasPressedThisFrame) { Step(1); return; }
            }

            Pan(kb);
            Refresh();
        }

        /// <summary>カーソルの下のマスを調べて、変わっていたら説明を出し直す。毎フレームと、回を切り替えた直後に呼ぶ。
        /// 切り替えた直後に呼ばないと、作り直した説明の箱が次のフレームまで出ず、再生中に載せたままだと
        /// 回が変わるたびに箱が1フレーム消えてちらつく。</summary>
        internal void Refresh()
        {
            if (Content == null || Show == null) { return; }
            Mouse mouse = Mouse.current;
            if (mouse == null) { return; }
            Vector2 screen = mouse.position.ReadValue();

            int x = int.MinValue, y = int.MinValue;
            Vector2 local;
            if (RectTransformUtility.RectangleContainsScreenPoint(transform as RectTransform, screen, null)
                && RectTransformUtility.ScreenPointToLocalPointInRectangle(Content, screen, null, out local))
            {
                // local は中身の軸からの位置（拡大前の大きさ）。中身の左上からの位置に直してマスを出す
                Rect area = Content.rect;
                x = OriginX + Mathf.FloorToInt((local.x - area.xMin) / Cell);
                y = TopY - Mathf.FloorToInt((area.yMax - local.y) / Cell);
            }
            if (x == _lastX && y == _lastY) { return; }
            _lastX = x;
            _lastY = y;
            try { Show(x, y); }
            catch (Exception) { }
        }
    }
}
