// LWF Eye Drops — 使い魔の Spine（マップ上のちびキャラ）を GL で描く
//
// 偽社員証（BuyFamsPact）の絵のゴブリンは、マップ上の使い魔の正面の待機ポーズ（f/act_idle の 0 秒）そのもの。
// 候補の使い魔を同じ描き方で並べるため、Spine のデータから同じポーズを作って描く。
//
// -- 本体の仕様（2026-11-10 に 0.31.2 で確認）---------------------------
// - 使い魔の Spine は、置く前の見本のプレハブ（Addressables）の SkeletonRenderer が SkeletonDataAsset を持つ。
//   プレハブのアドレスは FamMaster.ParamsOnBoot[fam].PreviewAddress（置き場所が使い魔ごとにまちまち）。
//   FamMaster.Instance はタイトル画面ではまだ無い → 無いときは写しの表を使う
// - スキンは向きごと（f=正面・l=横・b=背面）。正面の待機は f/act_idle
// - spine-csharp は 4.3 系の改造版。スロットの今の状態は slot.AppliedPose、描く順は DrawOrder.AppliedPose、
//   UV は attachment.Sequence.GetUVs(ResolveIndex(pose))
// - ページのマテリアルは多くが URP のライト付き（Skeleton Lit）で、画面外で描くとライトが無く真っ黒になる。
//   テクスチャだけ借りて、ライト無しの Spine/Skeleton（乗算済みアルファ）で描く

using System;
using System.Collections.Generic;
using Fams.EditIfFamAdd;
using HarmonyLib;
using Spine;
using Spine.Unity;
using UnityEngine;
using BlendMode = Spine.BlendMode;

namespace LwfEyeDrops
{
    internal static class SpineFigure
    {
        internal sealed class Batch
        {
            public Material Material;
            public float[] Vertices;  // x,y（Spine のワールド座標）
            public float[] UVs;
            public int[] Triangles;
            public Color Color;       // 乗算済み
            public string Slot;       // スロットの名前（体のどの部分かを知りたい側が使う。例 f_head）
            public string Attachment; // 貼ってある絵の名前
        }

        // 1ポーズぶんの描くもの。範囲は Spine のワールド座標（上向き）
        internal sealed class Pose
        {
            internal List<Batch> Batches;
            internal float MinX, MinY, MaxX, MaxY;
            internal float Width { get { return MaxX - MinX; } }
            internal float Height { get { return MaxY - MinY; } }
        }

        // FamMaster の写し（タイトル画面など FamMaster.Instance が無いとき用）
        private static readonly Dictionary<FamType, string> FallbackAddress = new Dictionary<FamType, string>
        {
            { FamType.Worker, "Assets/Prefab/Fams/GoblinPivotPreview.prefab" },
            { FamType.Crafter, "Assets/Prefab/Fams/Previews/SalamanderPivotPreview.prefab" },
            { FamType.Smelter, "Assets/Prefab/Fams/SmelterPivotPreview.prefab" },
            { FamType.Farmer, "Assets/Prefab/Fams/Previews/AlraunePivotPreview.prefab" },
            { FamType.Transporter, "Assets/Prefab/Fams/RavenPivotPreview.prefab" },
        };

        private static readonly Dictionary<FamType, SkeletonData> Loaded = new Dictionary<FamType, SkeletonData>();
        private static readonly Dictionary<string, Pose> Poses = new Dictionary<string, Pose>();
        private static readonly Dictionary<Texture, Material> UnlitCache = new Dictionary<Texture, Material>();
        private static readonly string[] UnlitShaders = { "Spine/Skeleton", "Universal Render Pipeline/Spine/Skeleton" };

        // 使い魔の骨格を読む（同期）。プレハブは解放しない（解放するとアトラスのテクスチャも消える）
        internal static SkeletonData Load(FamType fam)
        {
            SkeletonData data;
            if (Loaded.TryGetValue(fam, out data) && data != null) return data;
            string address = null;
            var master = FamMaster.Instance;
            if (master != null && master.ParamsOnBoot.ContainsKey(fam))
            {
                // init 付きのプロパティは C# 5 から直接読めない
                address = Traverse.Create(master.ParamsOnBoot[fam]).Property("PreviewAddress").GetValue<string>();
            }
            if (string.IsNullOrEmpty(address) && !FallbackAddress.TryGetValue(fam, out address))
                throw new InvalidOperationException("no preview prefab for " + fam);
            var go = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>(address).WaitForCompletion();
            var sr = go != null ? go.GetComponentInChildren<SkeletonRenderer>(true) : null;
            if (sr == null || sr.skeletonDataAsset == null) throw new InvalidOperationException("no skeleton in " + address);
            data = sr.skeletonDataAsset.GetSkeletonData(true);
            if (data == null) throw new InvalidOperationException("skeleton data not read: " + address);
            Loaded[fam] = data;
            return data;
        }

        internal static Pose GetPose(FamType fam, string skin, string animation, float time)
        {
            var key = fam + "/" + skin + "/" + animation + "/" + time;
            Pose p;
            if (Poses.TryGetValue(key, out p)) return p;
            p = Collect(Load(fam), skin, animation, time);
            Poses[key] = p;
            return p;
        }

        // GL の描画先に描く。scale は Spine の1単位あたりの px、(left, bottom) はポーズの範囲の左下を置く位置（px）
        internal static void Draw(Pose p, float scale, float left, float bottom)
        {
            foreach (var b in p.Batches)
            {
                b.Material.SetPass(0);
                GL.Begin(GL.TRIANGLES);
                GL.Color(b.Color);
                foreach (int t in b.Triangles)
                {
                    GL.TexCoord2(b.UVs[t * 2], b.UVs[t * 2 + 1]);
                    GL.Vertex3(left + (b.Vertices[t * 2] - p.MinX) * scale, bottom + (b.Vertices[t * 2 + 1] - p.MinY) * scale, 0);
                }
                GL.End();
            }
        }

        private static Pose Collect(SkeletonData data, string skin, string animation, float time)
        {
            var sk = new Skeleton(data);
            if (data.FindSkin(skin) == null) throw new InvalidOperationException(data.Name + ": skin not found: " + skin);
            sk.SetSkin(skin);
            sk.SetupPose();
            var anim = data.FindAnimation(animation);
            if (anim == null) throw new InvalidOperationException(data.Name + ": animation not found: " + animation);
            anim.Apply(sk, 0, time, true, null, 1, MixFrom.Setup, false, false, false);
            sk.UpdateWorldTransform(Spine.Physics.Pose);

            var batches = CollectBatches(sk);
            if (batches.Count == 0) throw new InvalidOperationException(data.Name + ": nothing to draw");
            var p = new Pose { Batches = batches, MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue };
            foreach (var b in batches)
            {
                for (int i = 0; i + 1 < b.Vertices.Length; i += 2)
                {
                    p.MinX = Math.Min(p.MinX, b.Vertices[i]); p.MaxX = Math.Max(p.MaxX, b.Vertices[i]);
                    p.MinY = Math.Min(p.MinY, b.Vertices[i + 1]); p.MaxY = Math.Max(p.MaxY, b.Vertices[i + 1]);
                }
            }
            return p;
        }

        private static Material Unlit(Texture tex, bool pma)
        {
            Material m;
            if (UnlitCache.TryGetValue(tex, out m) && m != null) return m;
            Shader shader = null;
            foreach (var n in UnlitShaders) { shader = Shader.Find(n); if (shader != null) break; }
            if (shader == null) throw new InvalidOperationException("unlit Spine shader not found");
            m = new Material(shader);
            m.hideFlags = HideFlags.HideAndDontSave;
            m.mainTexture = tex;
            if (!pma) { m.SetFloat("_StraightAlphaInput", 1); m.EnableKeyword("_STRAIGHT_ALPHA_INPUT"); }
            UnlitCache[tex] = m;
            return m;
        }

        private static List<Batch> CollectBatches(Skeleton sk)
        {
            var result = new List<Batch>();
            var clipper = new SkeletonClipping();
            var skColor = sk.GetColor();
            var order = sk.DrawOrder.AppliedPose;
            for (int s = 0; s < order.Count; s++)
            {
                var slot = order.Items[s];
                if (!slot.Bone.Active) { clipper.ClipEnd(slot); continue; }
                var pose = slot.AppliedPose;
                var att = pose.Attachment;
                float[] verts = null, uvs = null; int[] tris = null; TextureRegion region = null; Color attColor = Color.white;

                var ra = att as RegionAttachment;
                var ma = att as MeshAttachment;
                var ca = att as ClippingAttachment;
                if (ra != null)
                {
                    verts = new float[8];
                    ra.ComputeWorldVertices(slot, ra.GetOffsets(pose), verts, 0, 2);
                    int i = ra.Sequence.ResolveIndex(pose);
                    uvs = ra.Sequence.GetUVs(i);
                    region = ra.Sequence.GetRegion(i);
                    tris = new[] { 0, 1, 2, 2, 3, 0 };
                    attColor = ra.GetColor();
                }
                else if (ma != null)
                {
                    int n = ma.WorldVerticesLength;
                    verts = new float[n];
                    ma.ComputeWorldVertices(sk, slot, 0, n, verts, 0, 2);
                    int i = ma.Sequence.ResolveIndex(pose);
                    uvs = ma.Sequence.GetUVs(i);
                    region = ma.Sequence.GetRegion(i);
                    tris = ma.Triangles;
                    attColor = ma.GetColor();
                }
                else if (ca != null)
                {
                    clipper.ClipStart(sk, slot, ca);
                    continue;
                }

                if (verts != null && region != null)
                {
                    var c = skColor * pose.GetColor() * attColor;
                    if (c.a > 0)
                    {
                        if (clipper.IsClipping)
                        {
                            clipper.ClipTriangles(verts, tris, tris.Length, uvs);
                            verts = clipper.ClippedVertices.ToArray();
                            uvs = clipper.ClippedUVs.ToArray();
                            tris = clipper.ClippedTriangles.ToArray();
                        }
                        var ar = region as AtlasRegion;
                        var pageMat = ar != null && ar.page != null ? ar.page.rendererObject as Material : null;
                        var tex = pageMat != null ? pageMat.mainTexture : null;
                        if (tex != null && tris.Length > 0)
                        {
                            bool pma = ar.page.pma;
                            // 乗算済みの描き方では、加算はアルファを 0 にして表す
                            float a = slot.Data.BlendMode == BlendMode.Additive ? 0 : c.a;
                            var vc = pma ? new Color(c.r * c.a, c.g * c.a, c.b * c.a, a) : new Color(c.r, c.g, c.b, a);
                            result.Add(new Batch { Material = Unlit(tex, pma), Vertices = verts, UVs = uvs, Triangles = tris, Color = vc,
                                Slot = slot.Data.Name, Attachment = att.Name });
                        }
                    }
                }
                clipper.ClipEnd(slot);
            }
            clipper.ClipEnd();
            return result;
        }
    }
}
