using System;
using System.Collections.Generic;
using TerrainGrassDemo.Streaming;
using TerrainGrassDemo.Terrain;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerrainGrassDemo.Grass
{
    /// <summary>
    /// 草地子系统总管(全局合并管线, 不挂 MonoBehaviour, 由 DemoWorld 持有并驱动)。
    ///
    /// 为什么全局合并(性能核心, 踩坑史):
    ///   早期实现每个 Chunk × 每桶一个 Renderer(400+ 个), 每 Renderer 每帧
    ///   SetBuffer×3 + SetInt + SetCounter×2 + Dispatch + CopyCount×2 + Draw×2
    ///   ≈ 13 次 C++ API → 每帧 5000+ 次调用, 主线程被 API 提交吃满(60fps 都不稳)。
    ///   原生游戏(原神等)的做法是 GPU 驱动 + 全局合并: 全部草合并进少量大缓冲,
    ///   每帧 1 次 Dispatch + 每桶 1~2 次 Draw —— CPU 提交开销与草的数量无关。
    ///   本类即此方案: 5 个样式桶各一个全局实例缓冲 + Append 缓冲,
    ///   每帧 5 Dispatch + 10 Draw(约 65 次 API), 与激活 Chunk 数无关。
    ///
    /// 数据流:
    ///   Chunk 加载 → 确定性生成 + 分桶 → Chunk 级缓存(List[5])
    ///   → 脏标记 → 帧末把全部缓存拼接成 5 个全局桶列表 → SetData 重建全局缓冲
    ///   → 每帧 2 桶各 Dispatch(裁剪) + CopyCount + RenderMeshIndirect × 2
    /// </summary>
    public sealed class GrassChunkManager : IDisposable
    {
        private const int BucketCount = 2; // 收缩: 细草 + 大草(整改第1步)

        private sealed class ChunkGrassCache
        {
            public readonly List<GrassInstanceData>[] Buckets = new List<GrassInstanceData>[BucketCount];
            public ChunkGrassCache()
            {
                for (int i = 0; i < BucketCount; i++) Buckets[i] = new List<GrassInstanceData>(4096);
            }
        }

        private readonly HeightMapData m_HeightMap;
        private readonly TerrainGrassDemoSettings m_Settings;
        private readonly Mesh[] m_GrassMeshes;
        private readonly bool m_UseModelMeshes;
        private readonly float[] m_ModelScaleY;
        private readonly float[] m_ModelScaleXZ;
        private readonly ComputeShader m_CullingShader;
        private readonly Material m_MatLOD0;
        private readonly Material m_MatLOD1;

        // Chunk 级实例缓存(生成一次, 全局缓冲重建时直接拼接, 不重新生成)
        private readonly Dictionary<TerrainChunkData, ChunkGrassCache> m_ChunkCache = new Dictionary<TerrainChunkData, ChunkGrassCache>();
        // 全局桶列表(所有激活 Chunk 的拼接)与全局 Renderer(每桶一个)
        private readonly List<GrassInstanceData>[] m_GlobalBuckets = new List<GrassInstanceData>[BucketCount];
        private readonly GrassChunkRenderer[] m_GlobalRenderers = new GrassChunkRenderer[BucketCount];
        private readonly List<GrassInstanceData> m_Scratch = new List<GrassInstanceData>(8192);
        private readonly Bounds m_GlobalBounds;
        private readonly Mesh m_Lod1Mesh; // 面片草模式: LOD1 专用简化草簇(4 片梯形 16 顶点, 顶点减半)

        private bool m_RebuildDirty; // 本帧有 Chunk 加载/卸载, 帧末统一重建全局缓冲(节流)

        // ---- 统计(面板用, 全部来自真实数据) ----
        public int TotalInstances { get; private set; }
        public int ActiveChunkCount => m_ChunkCache.Count;
        public int LastDrawCalls { get; private set; }
        /// <summary>草形态描述(面板显示)。</summary>
        public string GrassModeInfo { get; private set; } = "程序草簇";
        /// <summary>最近一次异步读回汇总的可见实例数; 尚无数据时为 -1。</summary>
        public int VisibleLOD0 { get; private set; } = -1;
        public int VisibleLOD1 { get; private set; } = -1;

        // ---- 重合诊断(定位"一个点位多次重合") ----
        private int m_DiagPending;
        private string m_DiagResult = "未诊断";
        /// <summary>诊断结果: 实例数 / 唯一位置数 / 最大同点次数(1mm 量化)。</summary>
        public string DiagResult => m_DiagResult;

        public GrassChunkManager(HeightMapData heightMap, TerrainGrassDemoSettings settings, ComputeShader cullingShader,
            Texture2D grassAtlas, int atlasColumns, Mesh[] grassMeshes = null, float[] modelScaleY = null, float[] modelScaleXZ = null)
        {
            m_HeightMap = heightMap;
            m_Settings = settings;
            m_CullingShader = cullingShader;

            // 收缩(整改第1步): 一律程序面片草, 2 桶共享同一个弧形草簇 mesh。
            // 模型模式(Quaternius OBJ)已移除 —— 树/石/花以独立管线重做时再引入。
            var clumpMesh = CreateGrassClumpMesh();
            m_Lod1Mesh = CreateGrassClumpMeshLOD1();
            m_GrassMeshes = new Mesh[BucketCount];
            m_ModelScaleY = new float[BucketCount];
            m_ModelScaleXZ = new float[BucketCount];
            for (int i = 0; i < BucketCount; i++)
            {
                m_GrassMeshes[i] = clumpMesh;
                m_ModelScaleY[i] = 1f;
                m_ModelScaleXZ[i] = 1f;
            }
            m_UseModelMeshes = false;

            // 全局桶列表 + 全局 Renderer(每桶一个, 全图共享: 性能核心)
            for (int i = 0; i < BucketCount; i++)
            {
                m_GlobalBuckets[i] = new List<GrassInstanceData>(16384);
                m_GlobalRenderers[i] = new GrassChunkRenderer();
                m_GlobalRenderers[i].SetBucketStyle(i);
                m_GlobalRenderers[i].SetModelScale(m_ModelScaleY[i], m_ModelScaleXZ[i]);
            }

            // 全局包围盒(整个地图): 视锥内恒真, 交给 GPU 精确裁剪
            float mapX = settings.chunksX * settings.chunkWorldSize;
            float mapZ = settings.chunksZ * settings.chunkWorldSize;
            m_GlobalBounds = new Bounds(new Vector3(mapX * 0.5f, 50f, mapZ * 0.5f), new Vector3(mapX, 120f, mapZ));

            Shader shader = Shader.Find("TerrainGrassDemo/GrassIndirect");
            if (shader != null)
            {
                m_MatLOD0 = new Material(shader) { name = "GrassLOD0" };
                m_MatLOD0.enableInstancing = true;
                m_MatLOD1 = new Material(shader) { name = "GrassLOD1" };
                m_MatLOD1.enableInstancing = true;
                m_MatLOD1.EnableKeyword("GRASS_LOD1"); // LOD1: 跳过交互采样 + 简化阵风

                // A restrained meadow palette is shared by both procedural and model blades.
                // The previous white tint made the generated atlas read as chalk in URP.
                Color root = new Color(0.045f, 0.12f, 0.018f, 1f);
                Color tip = new Color(0.29f, 0.56f, 0.075f, 1f);
                Color dry = new Color(0.76f, 0.58f, 0.20f, 1f);
                m_MatLOD0.SetColor("_ColorBottom", root);
                m_MatLOD0.SetColor("_ColorTop", tip);
                m_MatLOD0.SetColor("_DryColor", dry);
                m_MatLOD0.SetFloat("_Transmission", 0.72f);
                m_MatLOD0.SetFloat("_Smoothness", 0.18f);
                m_MatLOD0.SetFloat("_AlphaCutoff", 0.42f);
                m_MatLOD1.SetColor("_ColorBottom", root);
                m_MatLOD1.SetColor("_ColorTop", tip);
                m_MatLOD1.SetColor("_DryColor", dry);
                m_MatLOD1.SetFloat("_Transmission", 0.58f);
                m_MatLOD1.SetFloat("_Smoothness", 0.12f);
                m_MatLOD1.SetFloat("_AlphaCutoff", 0.45f);
                // 新版近景使用真实弯曲几何叶片，不再依赖透明草图集；
                // 该关键字让 shader 直接使用几何轮廓，同时保留原 GPU 实例数据协议。
                m_MatLOD0.EnableKeyword("GRASS_MODEL_SHAPE");
                m_MatLOD1.EnableKeyword("GRASS_MODEL_SHAPE");
                m_MatLOD0.SetFloat("_MaxDrawDistance", settings.grassLod1Distance);
                m_MatLOD0.SetFloat("_FadeRange", settings.grassFadeRange);
                m_MatLOD0.SetFloat("_InteractionStrength", settings.interactionStrength);
                m_MatLOD1.SetFloat("_MaxDrawDistance", settings.grassLod1Distance);
                m_MatLOD1.SetFloat("_FadeRange", settings.grassFadeRange);
                m_MatLOD1.SetFloat("_InteractionStrength", settings.interactionStrength);
                if (grassAtlas != null && !m_UseModelMeshes)
                {
                    m_MatLOD0.SetTexture("_MainTex", grassAtlas);
                    m_MatLOD0.SetFloat("_AtlasColumns", atlasColumns);
                    m_MatLOD1.SetTexture("_MainTex", grassAtlas);
                    m_MatLOD1.SetFloat("_AtlasColumns", atlasColumns);
                }
            }
            else
            {
                Debug.LogError("[GrassChunkManager] 找不到 TerrainGrassDemo/GrassIndirect Shader");
            }

            // 形态信息(面板显示)
            GrassModeInfo = "几何叶片草(2桶 · GPU 全局合批)";
        }

        /// <summary>程序弧形草簇 mesh(面片草形态; public = 诊断/工具可复用)。</summary>
        public static Mesh CreateGrassClumpMesh()
        {
            // 5 根独立窄叶，每根 4 段形成弧线。用更多、更小的实例均匀铺开，
            // 避免传统十字透明面片在近景形成锥形/纸片感。
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colors = new List<Color>();
            var tris = new List<int>();

            const int bladeCount = 5;
            const int segments = 4;
            for (int k = 0; k < bladeCount; k++)
            {
                float a = (k * 137.50776f) * Mathf.Deg2Rad;
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                Vector3 dir = new Vector3(c, 0f, s);
                Vector3 side = new Vector3(-s, 0f, c);
                float radius = Mathf.Sqrt((k + 0.5f) / bladeCount) * 0.72f;
                Vector3 offset = dir * radius;
                float bladeHeight = 0.68f + ((k * 37) % 31) / 100f;
                float bend = 0.10f + ((k * 17) % 13) / 100f;
                float baseHalfWidth = 0.085f + ((k * 11) % 5) * 0.007f;
                int baseV = verts.Count;
                float slice = k / (float)bladeCount;
                for (int j = 0; j <= segments; j++)
                {
                    float t = j / (float)segments;
                    float halfWidth = baseHalfWidth * Mathf.Pow(1f - t, 0.72f) + 0.006f;
                    Vector3 center = offset + Vector3.up * (bladeHeight * t) + dir * (bend * t * t);
                    verts.Add(center - side * halfWidth);
                    verts.Add(center + side * halfWidth);
                    normals.Add(Vector3.Normalize(dir * 0.78f + Vector3.up * 0.22f));
                    normals.Add(Vector3.Normalize(dir * 0.78f + Vector3.up * 0.22f));
                    uvs.Add(new Vector2(0f, t));
                    uvs.Add(new Vector2(1f, t));
                    colors.Add(new Color(slice, 0f, 0f, 1f));
                    colors.Add(new Color(slice, 0f, 0f, 1f));
                    if (j == segments) continue;
                    int row = baseV + j * 2;
                    tris.Add(row); tris.Add(row + 1); tris.Add(row + 3);
                    tris.Add(row); tris.Add(row + 3); tris.Add(row + 2);
                }
            }

            var mesh = new Mesh { name = "GrassClump" };
            mesh.vertices = verts.ToArray();
            mesh.normals = normals.ToArray();
            mesh.uv = uvs.ToArray();
            mesh.colors = colors.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.bounds = new Bounds(new Vector3(0f, 0.5f, 0f), new Vector3(1.5f, 1.4f, 1.5f));
            return mesh;
        }

        /// <summary>
        /// LOD1 专用十字片: 2 片交叉矩形(8 顶点 4 三角形)。
        /// 远处 60~160m 只认草色轮廓, 8 顶点足够; LOD0 是 36 顶点弧形草簇 ——
        /// 开 LOD 后几何负载真实下降(36→8), 不会再出现"开了反而慢"。
        /// </summary>
        public static Mesh CreateGrassClumpMeshLOD1()
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colors = new List<Color>();
            var tris = new List<int>();

            for (int k = 0; k < 3; k++)
            {
                float a = k * 120f * Mathf.Deg2Rad;
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                Vector3 dir = new Vector3(c, 0f, s);
                Vector3 side = new Vector3(-s, 0f, c);
                Vector3 offset = dir * 0.004f;

                int baseV = verts.Count;
                verts.Add(side * -0.12f + offset);
                verts.Add(side * 0.12f + offset);
                verts.Add(side * 0.025f + dir * 0.12f + Vector3.up * 0.88f + offset);
                verts.Add(side * -0.025f + dir * 0.12f + Vector3.up * 0.88f + offset);
                for (int i = 0; i < 4; i++) normals.Add(dir);
                uvs.Add(new Vector2(0f, 0f));
                uvs.Add(new Vector2(1f, 0f));
                uvs.Add(new Vector2(1f, 1f));
                uvs.Add(new Vector2(0f, 1f));
                float slice = k / 2f;
                for (int i = 0; i < 4; i++) colors.Add(new Color(slice, 0f, 0f, 1f));
                tris.Add(baseV); tris.Add(baseV + 1); tris.Add(baseV + 2);
                tris.Add(baseV); tris.Add(baseV + 2); tris.Add(baseV + 3);
            }

            var mesh = new Mesh { name = "GrassClumpLOD1" };
            mesh.vertices = verts.ToArray();
            mesh.normals = normals.ToArray();
            mesh.uv = uvs.ToArray();
            mesh.colors = colors.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(2f, 2f, 2f));
            return mesh;
        }

        /// <summary>Streamer 等级变化回调(先地形后草 / 先草后地形 的顺序由 Streamer 保证)。</summary>
        public void HandleChunkLevelChanged(TerrainChunkData chunk, ChunkLoadLevel oldLevel, ChunkLoadLevel newLevel)
        {
            if (newLevel >= ChunkLoadLevel.TerrainLowGrass)
            {
                // 满密度环用全局密度档, 中环强制低密度
                GrassDensityLevel density = newLevel == ChunkLoadLevel.TerrainFullGrass
                    ? m_Settings.grassDensity
                    : GrassDensityLevel.Low;

                // 生成 + 分桶 → Chunk 级缓存(不直接上传, 等帧末统一重建全局缓冲)
                var cache = new ChunkGrassCache();
                GrassInstanceGenerator.Generate(m_HeightMap, chunk.ChunkX, chunk.ChunkZ, m_Settings, density, m_Scratch);
                for (int i = 0; i < m_Scratch.Count; i++)
                {
                    cache.Buckets[GrassInstanceGenerator.ModelBucketOf(m_Scratch[i].type01)].Add(m_Scratch[i]);
                }
                m_ChunkCache[chunk] = cache;
                m_RebuildDirty = true;
            }
            else
            {
                // TerrainOnly / Unloaded: 移除缓存(先于地形回收, Streamer 顺序保证)
                if (m_ChunkCache.Remove(chunk))
                {
                    m_RebuildDirty = true;
                }
            }
            RefreshTotals();
        }

        /// <summary>
        /// 把全部 Chunk 缓存拼接成 2 个全局桶列表并重建全局 GPU 缓冲。
        /// 节流: 由脏标记驱动, 每帧至多一次(加载 2 Chunk/帧也只重建一次)。
        /// </summary>
        private void RebuildGlobalBuffers()
        {
            m_RebuildDirty = false;
            for (int b = 0; b < BucketCount; b++) m_GlobalBuckets[b].Clear();
            foreach (var kv in m_ChunkCache)
            {
                for (int b = 0; b < BucketCount; b++)
                {
                    var list = kv.Value.Buckets[b];
                    if (list.Count > 0) m_GlobalBuckets[b].AddRange(list);
                }
            }
            for (int b = 0; b < BucketCount; b++)
            {
                Mesh lod1 = m_UseModelMeshes ? m_GrassMeshes[0] : m_Lod1Mesh;
                m_GlobalRenderers[b].Generate(m_GlobalBuckets[b], m_GrassMeshes[b], lod1, m_GlobalBounds);
            }
        }

        private void RefreshTotals()
        {
            int total = 0;
            foreach (var kv in m_ChunkCache)
            {
                for (int b = 0; b < BucketCount; b++) total += kv.Value.Buckets[b].Count;
            }
            TotalInstances = total;
            VisibleLOD0 = -1;
            VisibleLOD1 = -1;
        }

        /// <summary>全局密度切换: 全部 Chunk 重新生成(等级为中环的仍保持 Low)。</summary>
        public void SetDensity(GrassDensityLevel density)
        {
            m_Settings.grassDensity = density;
            foreach (var kv in m_ChunkCache)
            {
                var chunk = kv.Key;
                // 该 chunk 的等级决定密度档(近环满密度, 中环低密度)——从缓存反推:
                // 简化: 用当前全局密度(SetDensity 语义 = 全局档; 中环 chunk 的等级
                // 在 Streamer 里, 这里按"该 chunk 是否有满密度资格"无法直接得知,
                // 保守按全局档重生成; 中环的低密度在下次等级回调时自然恢复)
                GrassInstanceGenerator.Generate(m_HeightMap, chunk.ChunkX, chunk.ChunkZ, m_Settings, density, m_Scratch);
                for (int b = 0; b < BucketCount; b++) kv.Value.Buckets[b].Clear();
                for (int i = 0; i < m_Scratch.Count; i++)
                {
                    kv.Value.Buckets[GrassInstanceGenerator.ModelBucketOf(m_Scratch[i].type01)].Add(m_Scratch[i]);
                }
            }
            m_RebuildDirty = true;
            RefreshTotals();
        }

        /// <summary>每帧绘制(在相机移动之后、渲染之前调用)。</summary>
        public void LateTick(Camera cam)
        {
            if (m_CullingShader == null || m_MatLOD0 == null || m_MatLOD1 == null) return;
            if (!m_Settings.enableGrassRender)
            {
                LastDrawCalls = 0;
                return; // 草渲染总开关(性能矩阵"无草"基线)
            }

            // 帧末统一重建(加载/卸载/密度变化的节流点)
            if (m_RebuildDirty) RebuildGlobalBuffers();

            // 共享裁剪参数: 全局一次(与 Chunk 数无关 —— 性能核心)
            Vector3 camPos = cam.transform.position;
            m_CullingShader.SetVector("_CameraPos", camPos);
            m_CullingShader.SetMatrix("_VPMatrix",
                GL.GetGPUProjectionMatrix(cam.projectionMatrix, false) * cam.worldToCameraMatrix);
            m_CullingShader.SetVector("_CullingParams", new Vector4(
                m_Settings.grassLod0Distance, m_Settings.grassLod1Distance, m_Settings.grassFadeRange,
                m_Settings.enableGrassLod ? 1f : 0f));
            m_CullingShader.SetFloat("_FullDensityDistance", m_Settings.grassFullDensityDistance);
            Shader.SetGlobalFloat("_GrassFullDensity", m_Settings.grassFullDensityDistance);

            // 视锥平面每帧算一次(全局 bounds 之下, per-chunk 粗裁剪已无必要)
            var frustum = GeometryUtility.CalculateFrustumPlanes(cam);

            LastDrawCalls = 0;
            for (int b = 0; b < BucketCount; b++)
            {
                if (!m_GlobalRenderers[b].HasGrass) continue;
                LastDrawCalls += m_GlobalRenderers[b].Draw(cam, m_CullingShader, m_MatLOD0, m_MatLOD1, m_Settings, 0, frustum);
            }

            TickStatsReadback();
            TickDiagnostics();
        }

        /// <summary>低频统计读回: 每 0.1 秒对全局桶轮流发起 AsyncGPUReadback。</summary>
        private void TickStatsReadback()
        {
            // 1) 消费已完成的请求
            for (int i = m_PendingReadbacks.Count - 1; i >= 0; i--)
            {
                var pending = m_PendingReadbacks[i];
                if (!pending.Request.done) continue;
                if (!pending.Request.hasError)
                {
                    var data = pending.Request.GetData<uint>();
                    if (data.Length > 0)
                    {
                        if (pending.Lod == 0) pending.Renderer.LastVisibleLOD0 = (int)data[0];
                        else pending.Renderer.LastVisibleLOD1 = (int)data[0];
                    }
                }
                m_PendingReadbacks.RemoveAt(i);
            }

            // 2) 定时发起新请求(0.1s 一个桶)
            m_StatsTimer += Time.deltaTime;
            if (m_StatsTimer < 0.1f) return;
            m_StatsTimer = 0f;

            m_ReadbackCursor %= BucketCount;
            GrassChunkRenderer target = m_GlobalRenderers[m_ReadbackCursor];
            m_ReadbackCursor++;
            if (!target.HasGrass) return;

            var req0 = target.RequestVisibleCountReadback(0);
            if (req0.done == false) m_PendingReadbacks.Add(new PendingReadback { Request = req0, Renderer = target, Lod = 0 });
            var req1 = target.RequestVisibleCountReadback(1);
            if (req1.done == false) m_PendingReadbacks.Add(new PendingReadback { Request = req1, Renderer = target, Lod = 1 });

            // 3) 汇总可见数
            int v0 = 0, v1 = 0;
            bool any0 = false, any1 = false;
            for (int b = 0; b < BucketCount; b++)
            {
                if (m_GlobalRenderers[b].LastVisibleLOD0 >= 0) { v0 += m_GlobalRenderers[b].LastVisibleLOD0; any0 = true; }
                if (m_GlobalRenderers[b].LastVisibleLOD1 >= 0) { v1 += m_GlobalRenderers[b].LastVisibleLOD1; any1 = true; }
            }
            if (any0) VisibleLOD0 = v0;
            if (any1) VisibleLOD1 = v1;
        }

        private sealed class PendingReadback
        {
            public AsyncGPUReadbackRequest Request;
            public GrassChunkRenderer Renderer;
            public int Lod;
        }

        private float m_StatsTimer;
        private int m_ReadbackCursor;
        private readonly List<PendingReadback> m_PendingReadbacks = new List<PendingReadback>();

        /// <summary>统计全部全局实例(与提交 GPU 的完全一致)的位置重合。</summary>
        public void RequestOverlapDiagnostics()
        {
            if (m_GlobalBuckets[0].Count == 0) { m_DiagResult = "无实例"; return; }
            m_DiagResult = "统计中...";
            m_DiagPending = 1;
        }

        private void TickDiagnostics()
        {
            if (m_DiagPending == 0) return;
            m_DiagPending = 0;
            var counts = new Dictionary<long, int>();
            int maxDup = 1;
            int total = 0;
            for (int b = 0; b < BucketCount; b++)
            {
                var data = m_GlobalBuckets[b];
                for (int i = 0; i < data.Count; i++)
                {
                    long key = (long)Math.Round(data[i].position.x * 1000) * 10000000L
                             + (long)Math.Round(data[i].position.z * 1000);
                    counts.TryGetValue(key, out int c);
                    c++;
                    counts[key] = c;
                    if (c > maxDup) maxDup = c;
                    total++;
                }
            }
            m_DiagResult = $"实例={total} 唯一位置={counts.Count} 最大同点={maxDup}(1mm 量化)";
        }

        public void Dispose()
        {
            m_PendingReadbacks.Clear();
            for (int b = 0; b < BucketCount; b++)
            {
                if (m_GlobalRenderers[b] != null) m_GlobalRenderers[b].Dispose();
            }
            m_ChunkCache.Clear();
            // 程序创建的草簇 mesh 由我们销毁(仅面片草模式有; 模型模式全为 Resources 资源)
            if (!m_UseModelMeshes && m_GrassMeshes != null && m_GrassMeshes.Length > 0 && m_GrassMeshes[0] != null)
            {
                UnityEngine.Object.Destroy(m_GrassMeshes[0]);
            }
            if (m_Lod1Mesh != null) UnityEngine.Object.Destroy(m_Lod1Mesh);
            if (m_MatLOD0 != null) UnityEngine.Object.Destroy(m_MatLOD0);
            if (m_MatLOD1 != null) UnityEngine.Object.Destroy(m_MatLOD1);
        }
    }
}
