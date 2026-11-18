// 業務日誌の画面の続き（partial）：配置図に置く使い魔の絵（マップ上のちびキャラ・向きつき）。
//
// 顔アイコンでは向きが分からない。本体のマップ上の使い魔（Spine）は向きごとにスキンを持っているので、
// 同じ絵を待機ポーズで1枚の画像にして貼る。描き方は Eye Drops の SpineFigure.cs をそのまま使う
// （build.ps1 で同じソースを取り込んでいる。写しは作らない）。
//
// 向きの決め方は本体（FamSkinSetter.SetSkinByDirection）と同じ:
//   向きごとの絵を持つ使い魔（"l/act_idle" がある）: 南 = 正面 f・北 = 背面 b・東西 = 横 l。北と東は左右反転
//   持たない使い魔: 絵は1種類で、東と南は左右反転
//
// 絵が作れない（骨格が読めない・スキンが無い）ときは null を返し、呼ぶ側は顔アイコンに戻す。

using System;
using System.Collections.Generic;
using Fams.EditIfFamAdd;
using LwfEyeDrops;
using Spine;
using UnityEngine;

namespace LwfEconomyGraph
{
    internal static partial class LogView
    {
        /// <summary>絵の高さ（px）。寄って見ても 1 マスは 60px ほどなので、これで足りる</summary>
        private const int FigureHeight = 128;

        private sealed class Figure
        {
            public Sprite Sprite;
            public bool Flip;
        }

        private static readonly Dictionary<int, Sprite> FigureSprites = new Dictionary<int, Sprite>();
        private static readonly HashSet<int> FigureFailed = new HashSet<int>();
        private static bool _figureFailLogged;

        /// <summary>使い魔の種類と向き（西 0・北 1・東 2・南 3）から、貼る絵と左右反転を決める。</summary>
        private static Figure FamFigure(int famType, int direction)
        {
            if (direction < 0 || direction > 3 || FigureFailed.Contains(famType)) { return null; }
            try
            {
                SkeletonData data = SpineFigure.Load((FamType)famType);
                bool directional = data.FindAnimation("l/act_idle") != null;

                string skin, animation;
                bool flip;
                if (directional)
                {
                    skin = direction == 3 ? "f" : (direction == 1 ? "b" : "l");
                    animation = skin + "/act_idle";
                    flip = direction == 1 || direction == 2;
                }
                else
                {
                    skin = data.FindSkin("f") != null ? "f" : data.DefaultSkin.Name;
                    animation = data.FindAnimation("f/act_idle") != null ? "f/act_idle" : "act_idle";
                    flip = direction == 2 || direction == 3;
                }

                // 同じ種類・同じスキンの絵は使い回す
                int key = famType * 8 + (skin == "f" ? 0 : (skin == "b" ? 1 : (skin == "l" ? 2 : 3)));
                Sprite sprite;
                if (!FigureSprites.TryGetValue(key, out sprite) || sprite == null)
                {
                    sprite = RenderFigure(SpineFigure.GetPose((FamType)famType, skin, animation, 0f));
                    FigureSprites[key] = sprite;
                }
                Figure figure = new Figure();
                figure.Sprite = sprite;
                figure.Flip = flip;
                return figure;
            }
            catch (Exception e)
            {
                FigureFailed.Add(famType);
                if (!_figureFailLogged)
                {
                    _figureFailLogged = true;
                    EconomyGraphPlugin.Log.LogWarning("[view] cannot draw the figure of fam " + famType + ": " + e.Message);
                }
                return null;
            }
        }

        private static readonly Dictionary<int, Sprite> HeadSprites = new Dictionary<int, Sprite>();
        private const int HeadSize = 96;

        /// <summary>使い魔のちびキャラの顔。正面の絵のうち、頭の部品（スロット f_head）の範囲だけを描いた画像。
        /// 小さい絵（説明の箱・表の見出し）では、全身や顔アイコンより見分けやすい。
        /// 骨格が無い（コンベアなど）・頭の部品が見つからないときは null（呼ぶ側は別の絵に戻す）。</summary>
        private static Sprite FamHead(int famType)
        {
            Sprite head;
            if (HeadSprites.TryGetValue(famType, out head)) { return head; }
            head = null;
            if (famType > 0 && !FigureFailed.Contains(famType))
            {
                try
                {
                    SkeletonData data = SpineFigure.Load((FamType)famType);
                    bool directional = data.FindAnimation("l/act_idle") != null;
                    string skin = (directional || data.FindSkin("f") != null) ? "f" : data.DefaultSkin.Name;
                    string animation = data.FindAnimation("f/act_idle") != null ? "f/act_idle" : "act_idle";
                    SpineFigure.Pose pose = SpineFigure.GetPose((FamType)famType, skin, animation, 0f);

                    // 頭の部品の範囲（Spine の座標）。使い魔6種とも、正面の頭は f_head という名前
                    float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                    foreach (SpineFigure.Batch b in pose.Batches)
                    {
                        if (!IsHeadPart(b.Slot) && !IsHeadPart(b.Attachment)) { continue; }
                        for (int k = 0; k + 1 < b.Vertices.Length; k += 2)
                        {
                            minX = Mathf.Min(minX, b.Vertices[k]);
                            maxX = Mathf.Max(maxX, b.Vertices[k]);
                            minY = Mathf.Min(minY, b.Vertices[k + 1]);
                            maxY = Mathf.Max(maxY, b.Vertices[k + 1]);
                        }
                    }
                    if (minX < maxX && minY < maxY)
                    {
                        // 頭の範囲を少し広げた正方形（前髪や耳が切れないように）
                        float side = Mathf.Max(maxX - minX, maxY - minY) * 1.15f;
                        float cx = (minX + maxX) * 0.5f, cy = (minY + maxY) * 0.5f;
                        head = RenderRegion(pose, cx - side * 0.5f, cy - side * 0.5f, side, HeadSize);
                    }
                }
                catch (Exception) { head = null; }
            }
            HeadSprites[famType] = head;
            return head;
        }

        private static bool IsHeadPart(string name)
        {
            return name != null && (name == "f_head" || name.EndsWith("/head", StringComparison.Ordinal) || name == "head");
        }

        /// <summary>使い魔の種類（Crafter・Smelter…）のちびキャラの顔。</summary>
        private static Sprite CrafterHead(string kind)
        {
            try
            {
                Items.Craft.CrafterKind parsed = (Items.Craft.CrafterKind)Enum.Parse(typeof(Items.Craft.CrafterKind), kind);
                return FamHead((int)Fams.FamTypes.GetFamTypeFromCrafterKind(parsed));
            }
            catch (Exception) { return null; }
        }

        /// <summary>アイテムや資源の ID が使い魔を指していれば、そのちびキャラの顔（召喚アイテム「Summon-Worker」や
        /// 使い魔のタグ「Worker」）。使い魔でなければ null。</summary>
        private static Sprite FamHeadByID(string id)
        {
            if (string.IsNullOrEmpty(id)) { return null; }
            try
            {
                string tag = id.StartsWith("Summon-", StringComparison.Ordinal) ? id.Substring(7) : id;
                FamType fam = Fams.FamTypes.GetFamTypeFromTagID(tag);
                return fam == FamType.None ? null : FamHead((int)fam);
            }
            catch (Exception) { return null; }
        }

        /// <summary>説明の箱に出す絵。使い魔ならちびキャラの顔、それ以外は本体のアイコン。</summary>
        private static Sprite InfoSprite(string id)
        {
            return FamHeadByID(id) ?? AnySprite(id);
        }

        /// <summary>ポーズの一部（Spine の座標で、左下 (x, y) から一辺 side の正方形）を、一辺 size px の画像にする。</summary>
        private static Sprite RenderRegion(SpineFigure.Pose pose, float x, float y, float side, int size)
        {
            float scale = size / Mathf.Max(0.001f, side);
            RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.clear);
                GL.PushMatrix();
                try
                {
                    GL.LoadPixelMatrix(0, size, 0, size);
                    // Draw は「ポーズの範囲の左下」を置く位置を取る。切り出す正方形の左下が (0,0) に来るようにずらす
                    SpineFigure.Draw(pose, scale, (pose.MinX - x) * scale, (pose.MinY - y) * scale);
                }
                finally { GL.PopMatrix(); }
                texture.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                texture.Apply(false, false);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.filterMode = FilterMode.Bilinear;
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        }

        /// <summary>1ポーズを画面外の描画先に GL で描いて、画像（Sprite）にする。</summary>
        private static Sprite RenderFigure(SpineFigure.Pose pose)
        {
            float scale = FigureHeight / Mathf.Max(0.001f, pose.Height);
            int w = Mathf.Max(1, Mathf.CeilToInt(pose.Width * scale));
            int h = FigureHeight;

            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            Texture2D texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.clear);
                GL.PushMatrix();
                try
                {
                    GL.LoadPixelMatrix(0, w, 0, h);
                    SpineFigure.Draw(pose, scale, 0f, 0f);
                }
                finally { GL.PopMatrix(); }
                texture.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                texture.Apply(false, false);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.filterMode = FilterMode.Bilinear;
            return Sprite.Create(texture, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        }
    }
}
