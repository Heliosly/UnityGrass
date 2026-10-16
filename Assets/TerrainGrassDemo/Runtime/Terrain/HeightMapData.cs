using UnityEngine;

namespace TerrainGrassDemo.Terrain
{
    /// <summary>
    /// 全局高度图。
    ///
    /// 数据结构选型(参考 QnMobile CTerrainFast):
    /// 整张地图只有一张全局顶点网格 float[](一维数组, 索引 = z * vertsX + x),
    /// 而不是每个 Chunk 各存一份。原因:
    /// 1. 相邻 Chunk 的共享边顶点读的是数组里同一个元素 —— 高度、法线天然一致,
    ///    不会出现"裂缝"或"光照断层"(需求 1.7 的根源解法);
    /// 2. Chunk 只是这张大图上的一个"窗口" [chunkX*res, chunkX*res+res],
    ///    生成 Chunk Mesh 时按窗口范围切片即可;
    /// 3. 草实例生成、玩家贴地、高度采样全都走同一个双线性插值函数, 没有多套高度来源。
    ///
    /// 内存量级: 8x8 Chunk x 32 分辨率 → 257x257 = 66049 个 float, 完全可以忽略。
    /// </summary>
    public sealed class HeightMapData
    {
        public int ChunksX { get; }
        public int ChunksZ { get; }
        public float ChunkWorldSize { get; }
        public int ChunkGridRes { get; }

        /// <summary>全局顶点网格宽度(顶点数), 即 chunksX * res + 1。</summary>
        public int VertsX { get; }
        public int VertsZ { get; }

        /// <summary>高度数组, 索引 = z * VertsX + x, 单位: 世界米。</summary>
        public float[] Heights { get; }

        /// <summary>法线数组, 与 Heights 同索引, 由高度图中心差分求出。</summary>
        public Vector3[] Normals { get; }

        /// <summary>网格单元的世界边长 = chunkWorldSize / chunkGridRes。</summary>
        public float CellSize { get; }

        public HeightMapData(int chunksX, int chunksZ, float chunkWorldSize, int chunkGridRes)
        {
            ChunksX = chunksX;
            ChunksZ = chunksZ;
            ChunkWorldSize = chunkWorldSize;
            ChunkGridRes = chunkGridRes;
            VertsX = chunksX * chunkGridRes + 1;
            VertsZ = chunksZ * chunkGridRes + 1;
            CellSize = chunkWorldSize / chunkGridRes;
            Heights = new float[VertsX * VertsZ];
            Normals = new Vector3[VertsX * VertsZ];
        }

        public static HeightMapData Create(TerrainGrassDemoSettings s)
        {
            var hm = new HeightMapData(s.chunksX, s.chunksZ, s.chunkWorldSize, s.chunkGridRes);
            hm.FillHeights(s.worldSeed, s.terrainStyle);
            hm.FillNormals();
            return hm;
        }

        /// <summary>
        /// 从灰度高度图(Texture2D)填充高度 —— 通用地图入口(需求: 加载外部真实地图)。
        ///
        /// 通用性设计:
        ///   - 任意尺寸的图都能用: 按"归一化 UV"双线性重采样到本工程的分块网格,
        ///     不需要图片尺寸与 Chunk 布局对齐(257x257 的图可以, 1024x512 的也可以);
        ///   - 任意亮度范围都能用: 亮度 [0,1] × heightScale 映射到世界米;
        ///   - 只取亮度(R 通道): 灰度图、灰度+Alpha 的图、彩色图的亮度都能直接当高度。
        ///
        /// 采样方式与高度函数共用 FillNormals → 后续所有系统(草/玩家/流式)行为完全一致。
        /// </summary>
        public static HeightMapData CreateFromTexture(Texture2D source, TerrainGrassDemoSettings s, float heightScale)
        {
            var hm = new HeightMapData(s.chunksX, s.chunksZ, s.chunkWorldSize, s.chunkGridRes);
            if (source == null)
            {
                hm.FillHeights(s.worldSeed, s.terrainStyle);
            }
            else
            {
                // 像素 → 高度: 采样源图(双线性, 取 R 通道亮度)
                var pixels = source.GetPixels(); // 一次性读整张, 避免逐像素 GetPixel 的边界检查开销
                int sw = source.width;
                int sh = source.height;
                for (int z = 0; z < hm.VertsZ; z++)
                {
                    // 目标网格 → 源图 UV(注意 V 轴翻转: Unity 纹理坐标原点在左下,
                    // 高度图习惯"上方=高处", 这里翻转让图片上边缘对应 Z 正方向)
                    float v = 1f - z / (float)(hm.VertsZ - 1);
                    float sy = Mathf.Clamp(v * (sh - 1), 0f, sh - 1);
                    int y0 = Mathf.FloorToInt(sy);
                    int y1 = Mathf.Min(sh - 1, y0 + 1);
                    float ty = sy - y0;
                    for (int x = 0; x < hm.VertsX; x++)
                    {
                        float u = x / (float)(hm.VertsX - 1);
                        float sx = Mathf.Clamp(u * (sw - 1), 0f, sw - 1);
                        int x0 = Mathf.FloorToInt(sx);
                        int x1 = Mathf.Min(sw - 1, x0 + 1);
                        float tx = sx - x0;

                        float h00 = pixels[y0 * sw + x0].r;
                        float h10 = pixels[y0 * sw + x1].r;
                        float h01 = pixels[y1 * sw + x0].r;
                        float h11 = pixels[y1 * sw + x1].r;
                        float lum = Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), ty);
                        hm.Heights[z * hm.VertsX + x] = lum * heightScale;
                    }
                }
            }
            hm.FillNormals();
            return hm;
        }

        private void FillHeights(uint seed, int style)
        {
            if (style == 0) FillHeightsGrassland(seed);
            else FillHeightsHills(seed);
        }

        /// <summary>
        /// 丘陵草原(默认地形, 草地友好): 三层低频噪声叠加 ——
        ///   100m 波长缓丘(±3m 相对均值) + 20m 波长小丘(±0.75m) + 4m 波长微起伏(±0.15m),
        ///   整体高度 [6, 14] 左右(Fbm 归一化 [0,1], 均值 0.5)。
        /// 缓坡为主(坡度 1~3%), 配合四叉树的共面判定 → 大区域可合并,
        /// 又没有精确平坦的"人造感" —— 草原的起伏就该是这样。
        /// </summary>
        private void FillHeightsGrassland(uint seed)
        {
            for (int z = 0; z < VertsZ; z++)
            {
                float wz = z * CellSize;
                for (int x = 0; x < VertsX; x++)
                {
                    float wx = x * CellSize;
                    float h = NoiseUtils.Fbm(wx * 0.01f, wz * 0.01f, 4, seed) * 6f;       // 缓丘
                    h += NoiseUtils.Fbm(wx * 0.05f, wz * 0.05f, 3, seed + 7u) * 1.5f;     // 小丘
                    h += NoiseUtils.Fbm(wx * 0.25f, wz * 0.25f, 2, seed + 13u) * 0.3f;    // 微起伏
                    Heights[z * VertsX + x] = h + 6f; // 抬升到 [0, 14] 左右, 地面在 0 以上
                }
            }
        }

        /// <summary>
        /// 山丘 + 平顶高原(四叉树压缩展示用):
        /// 1. 两个高斯山丘 —— 连续起伏, 四叉树必须保留细节;
        /// 2. 一个平顶高原 —— 顶部高度精确为常数, 四叉树整片塌缩成 2 个三角形;
        /// 3. 微弱涟漪噪声 —— 只叠加在山丘影响区(乘 mask), 平原保持精确平坦,
        ///    这样"平坦 vs 起伏"的对比在顶点数上非常悬殊, 一眼可见。
        /// 全部由确定性噪声/解析式构成, 不需要任何外部高度图资源。
        /// </summary>
        private void FillHeightsHills(uint seed)
        {
            float mapX = ChunksX * ChunkWorldSize;
            float mapZ = ChunksZ * ChunkWorldSize;

            // 山丘参数(世界坐标)
            float ax = 0.30f * mapX, az = 0.32f * mapZ, aSigma = 34f, aAmp = 12f;
            float bx = 0.60f * mapX, bz = 0.68f * mapZ, bSigma = 26f, bAmp = 9f;
            float px = 0.72f * mapX, pz = 0.22f * mapZ, pRadius = 24f, pEdge = 10f, pHeight = 7f;

            for (int z = 0; z < VertsZ; z++)
            {
                float wz = z * CellSize;
                for (int x = 0; x < VertsX; x++)
                {
                    float wx = x * CellSize;
                    float h = 0f;

                    h += Gauss(wx, wz, ax, az, aSigma, aAmp);
                    h += Gauss(wx, wz, bx, bz, bSigma, bAmp);
                    h += Plateau(wx, wz, px, pz, pRadius, pEdge, pHeight);

                    // 涟漪噪声只加在山丘附近: 山丘"密度"归一化后作为 mask。
                    // 平原上 mask == 0, 高度精确等于 0 —— 四叉树才能整片合并。
                    float mask = Mathf.Clamp01((GaussDensity(wx, wz, ax, az, aSigma) + GaussDensity(wx, wz, bx, bz, bSigma)) * 6f);
                    float ripple = NoiseUtils.Fbm(wx, wz, 3, seed) * 2f - 1f;
                    h += ripple * 0.5f * mask;

                    Heights[z * VertsX + x] = h;
                }
            }
        }

        private static float Gauss(float x, float z, float cx, float cz, float sigma, float amp)
        {
            float dx = x - cx, dz = z - cz;
            float d2 = dx * dx + dz * dz;
            return amp * Mathf.Exp(-d2 / (2f * sigma * sigma));
        }

        private static float GaussDensity(float x, float z, float cx, float cz, float sigma)
        {
            float dx = x - cx, dz = z - cz;
            float d2 = dx * dx + dz * dz;
            return Mathf.Exp(-d2 / (2f * sigma * sigma));
        }

        /// <summary>
        /// 平顶高原: 边缘用 smoothstep 过渡, 顶部在 r <= radius - edge 范围内
        /// smoothstep 恰好饱和为 1 → 高度精确等于常量, 满足"可配置误差阈值"下的平坦判定。
        /// </summary>
        private static float Plateau(float x, float z, float cx, float cz, float radius, float edge, float height)
        {
            float dx = x - cx, dz = z - cz;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            float t = Mathf.Clamp01((radius - d) / edge);
            float s = t * t * (3f - 2f * t);
            return height * s;
        }

        /// <summary>
        /// 法线: 中心差分。为什么从全局高度图差分而不是 Mesh.RecalculateNormals:
        /// 1. 优化网格的三角形很大, 面法线 + 角度平均会让平坦大平面上的法线跟随三角形走样;
        /// 2. 从高度图差分, 顶点法线只依赖它周围的四个高度采样,
        ///    无论这个顶点属于全网格还是优化网格, 法线都一致 → 切换网格时光照不会跳变;
        /// 3. 相邻 Chunk 边界共享顶点读的是同一份高度 → 边界两侧法线一致, 无光照断层。
        /// </summary>
        private void FillNormals()
        {
            float inv2Cell = 1f / (2f * CellSize);
            for (int z = 0; z < VertsZ; z++)
            {
                for (int x = 0; x < VertsX; x++)
                {
                    float hl = Heights[Idx(Mathf.Max(0, x - 1), z)];
                    float hr = Heights[Idx(Mathf.Min(VertsX - 1, x + 1), z)];
                    float hd = Heights[Idx(x, Mathf.Max(0, z - 1))];
                    float hu = Heights[Idx(x, Mathf.Min(VertsZ - 1, z + 1))];
                    float dhdx = (hr - hl) * inv2Cell;
                    float dhdz = (hu - hd) * inv2Cell;
                    // 高度场 y = h(x,z), 表面法线 = normalize(-dh/dx, 1, -dh/dz)
                    Normals[Idx(x, z)] = new Vector3(-dhdx, 1f, -dhdz).normalized;
                }
            }
        }

        public int Idx(int x, int z) => z * VertsX + x;

        /// <summary>边界外钳制到边缘(采样函数约定: 永远不越界)。</summary>
        public float GetHeightClamped(int x, int z)
        {
            return Heights[Idx(Mathf.Clamp(x, 0, VertsX - 1), Mathf.Clamp(z, 0, VertsZ - 1))];
        }

        /// <summary>
        /// 双线性插值采样世界坐标高度。
        /// 草实例、玩家贴地都走这里: 先反算所在网格单元, 再对 4 个顶点高度做双线性插值。
        /// </summary>
        public float SampleHeight(float worldX, float worldZ)
        {
            float gx = worldX / CellSize;
            float gz = worldZ / CellSize;
            int x0 = Mathf.FloorToInt(gx);
            int z0 = Mathf.FloorToInt(gz);
            x0 = Mathf.Clamp(x0, 0, VertsX - 2);
            z0 = Mathf.Clamp(z0, 0, VertsZ - 2);
            float tx = gx - x0;
            float tz = gz - z0;

            float h00 = Heights[Idx(x0, z0)];
            float h10 = Heights[Idx(x0 + 1, z0)];
            float h01 = Heights[Idx(x0, z0 + 1)];
            float h11 = Heights[Idx(x0 + 1, z0 + 1)];

            return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz);
        }

        /// <summary>采样世界坐标处的表面法线(双线性), 玩家贴地/草地放置用。</summary>
        public Vector3 SampleNormal(float worldX, float worldZ)
        {
            float gx = worldX / CellSize;
            float gz = worldZ / CellSize;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(gx), 0, VertsX - 2);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(gz), 0, VertsZ - 2);
            float tx = gx - x0;
            float tz = gz - z0;
            Vector3 n00 = Normals[Idx(x0, z0)];
            Vector3 n10 = Normals[Idx(x0 + 1, z0)];
            Vector3 n01 = Normals[Idx(x0, z0 + 1)];
            Vector3 n11 = Normals[Idx(x0 + 1, z0 + 1)];
            return Vector3.Lerp(Vector3.Lerp(n00, n10, tx), Vector3.Lerp(n01, n11, tx), tz).normalized;
        }

        /// <summary>Chunk 原点世界坐标(Chunk 内局部 x/z = 0 的世界位置)。</summary>
        public Vector3 GetChunkOrigin(int chunkX, int chunkZ)
        {
            return new Vector3(chunkX * ChunkWorldSize, 0f, chunkZ * ChunkWorldSize);
        }

        /// <summary>整个 Chunk 的世界包围盒(高度取该 Chunk 顶点范围)。</summary>
        public Bounds GetChunkBounds(int chunkX, int chunkZ)
        {
            int baseX = chunkX * ChunkGridRes;
            int baseZ = chunkZ * ChunkGridRes;
            float minH = float.MaxValue, maxH = float.MinValue;
            for (int z = 0; z <= ChunkGridRes; z++)
            {
                for (int x = 0; x <= ChunkGridRes; x++)
                {
                    float h = Heights[Idx(baseX + x, baseZ + z)];
                    if (h < minH) minH = h;
                    if (h > maxH) maxH = h;
                }
            }
            Vector3 origin = GetChunkOrigin(chunkX, chunkZ);
            Vector3 center = new Vector3(origin.x + ChunkWorldSize * 0.5f, (minH + maxH) * 0.5f, origin.z + ChunkWorldSize * 0.5f);
            Vector3 size = new Vector3(ChunkWorldSize, Mathf.Max(0.01f, maxH - minH), ChunkWorldSize);
            return new Bounds(center, size);
        }
    }
}
