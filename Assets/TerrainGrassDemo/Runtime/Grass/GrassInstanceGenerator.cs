using System.Collections.Generic;
using TerrainGrassDemo.Terrain;
using UnityEngine;

namespace TerrainGrassDemo.Grass
{
    /// <summary>
    /// 草实例生成器(纯 CPU, 确定性)。
    ///
    /// 三个关键设计:
    /// 1. 每 Chunk 固定种子(需求 3.4): 种子只由 (chunkX, chunkZ, worldSeed) 决定。
    ///    Chunk 被流式卸载后重新加载, 草的位置/朝向/尺寸必须和上次一模一样,
    ///    否则玩家走远再回来会看到"草被换了一茬"。注意不能用 UnityEngine.Random ——
    ///    它是全局状态, 顺序一变结果就变。这里用参考实现同款 LCG(CSimpleRandom)。
    /// 2. 密度分档(需求 3.14): 参考 QnMobile s_GrassSkip —— 每 skip 个网格单元放一根草:
    ///    High=1(每格一根) / Normal=2 / Low=4。密度只改变"跳过多少格", 不改随机序列,
    ///    所以切到 Low 后切回 High, 草的位置与原来完全一致。
    /// 3. 坡度过滤: 过陡的坡(法线 y 分量小)不长草, 避免草横着长在悬崖上。
    ///    坡度来自高度图法线, 同样确定。
    /// </summary>
    public static class GrassInstanceGenerator
    {
        /// <summary>LCG 伪随机(无符号 32 位运算, 取高 24 位, 无符号移位无边界问题)。</summary>
        private struct SimpleRandom
        {
            private uint m_State;
            public SimpleRandom(int seed) { m_State = (uint)seed; }
            /// <summary>[0,1) 均匀分布。</summary>
            public float Next01()
            {
                m_State = m_State * 1664525u + 1013904223u;
                return (m_State >> 8) / 16777216.0f;
            }
        }

        /// <summary>密度档 → 跳格数。</summary>
        public static int SkipForDensity(GrassDensityLevel density)
        {
            // 三档都覆盖每个草格，靠每格数量降级，避免 Streaming 环边界出现棋盘状断层。
            return 1;
        }

        /// <summary>
        /// Chunk 固定种子。乘法哈希, 每个 Chunk 的种子互不相同且与加载顺序无关。
        /// </summary>
        public static uint GetChunkSeed(int chunkX, int chunkZ, uint worldSeed)
        {
            uint h = worldSeed ^ (uint)chunkX * 0x9E3779B1u ^ (uint)chunkZ * 0x85EBCA6Bu;
            h = (h ^ (h >> 13)) * 0xC2B2AE35u;
            h ^= h >> 16;
            return h;
        }

        /// <summary>整数坐标哈希 → [0,1)。确定性, 与加载顺序无关。</summary>
        private static float Hash2(int x, int z, uint seed)
        {
            uint h = seed;
            h ^= (uint)x * 0x27d4eb2du;
            h ^= (uint)z * 0x165667b1u;
            h = (h ^ (h >> 15)) * 0x85ebca6bu;
            h = (h ^ (h >> 13)) * 0xc2b2ae35u;
            h ^= h >> 16;
            return (h & 0xFFFFFFu) / 16777216.0f;
        }

        /// <summary>
        /// 双线性插值 value noise(世界坐标)。平滑 → 相邻格子的密度相关 →
        /// 草地成片成丛, 而不是处处等密。跨 chunk 连续: 采样点用世界坐标。
        /// </summary>
        private static float ValueNoise(float x, float z, uint seed)
        {
            int ix = Mathf.FloorToInt(x), iz = Mathf.FloorToInt(z);
            float fx = x - ix, fz = z - iz;
            float h00 = Hash2(ix, iz, seed);
            float h10 = Hash2(ix + 1, iz, seed);
            float h01 = Hash2(ix, iz + 1, seed);
            float h11 = Hash2(ix + 1, iz + 1, seed);
            fx = fx * fx * (3f - 2f * fx); // smoothstep: 消除线性插值的折痕
            fz = fz * fz * (3f - 2f * fz);
            return Mathf.Lerp(Mathf.Lerp(h00, h10, fx), Mathf.Lerp(h01, h11, fx), fz);
        }

        /// <summary>
        /// 生成一个 Chunk 的全部草实例, 返回实例数。结果写入 output(不复用列表会反复分配, 所以传入)。
        ///
        /// 每格草数(参照 QnMobile CGrassFast 的密度语义):
        ///   参考实现里 m_Type 的高 3 位存"每格草片数 = 1 + density"(0~7),
        ///   密度不是只靠跳格, 而是"跳格 × 每格多根"两维控制。
        /// 这里等价实现:
        ///   High  → skip=1, 每格 3 根(密集草地)
        ///   Normal→ skip=2, 每格 2 根
        ///   Low   → skip=4, 每格 1 根
        /// 每根草在格内独立抖动, 种子 = 格子种子 + 根索引(确定性, 重载一致)。
        ///
        /// 密度分布(3A 草地感): 每格用低频 value noise(世界坐标, 斑块 ~8m)调制 ——
        ///   最低 12% 的斑块是裸露土区(完全不长草), 其余区域稀密斑块化
        ///   (densityScale ∈ [0.35, 1.65], 每根草按概率跳过)。平均密度 ≈ 不变量,
        ///   视觉上却有"这里密一丛、那里疏几根、远处一片秃"的自然起伏。
        /// </summary>
        public static int Generate(HeightMapData hm, int chunkX, int chunkZ, TerrainGrassDemoSettings s,
            GrassDensityLevel density, List<GrassInstanceData> output)
        {
            output.Clear();

            int skip = SkipForDensity(density);
            int bladesPerCell = BladesPerCell(density);
            // 草格分辨率独立于地形网格(grassCellSize, 默认 1m):
            // 地形 chunkGridRes 调高不会让草密度暴涨(解耦)。
            int res = Mathf.RoundToInt(hm.ChunkWorldSize / Mathf.Max(0.1f, s.grassCellSize));
            float cell = s.grassCellSize;
            Vector3 origin = hm.GetChunkOrigin(chunkX, chunkZ);
            uint chunkSeed = GetChunkSeed(chunkX, chunkZ, s.worldSeed);

            for (int gz = 0; gz < res; gz++)
            {
                if (gz % skip != 0) continue;
                for (int gx = 0; gx < res; gx++)
                {
                    if (gx % skip != 0) continue;

                    // 坡度过滤在格中心做一次(整格一起判, 减少采样次数)
                    float cx = origin.x + (gx + 0.5f) * cell;
                    float cz = origin.z + (gz + 0.5f) * cell;
                    if (s.enableMeadowPath)
                    {
                        float centerX = hm.ChunksX * hm.ChunkWorldSize * 0.5f;
                        float centerZ = hm.ChunksZ * hm.ChunkWorldSize * 0.5f;
                        float pathX = centerX + Mathf.Sin((cz - centerZ) * s.meadowPathFrequency) * s.meadowPathAmplitude;
                        float pathDistance = Mathf.Abs(cx - pathX);
                        // 只让道路中线完全裸露，边缘允许少量草叶侵入，避免尺子切出来的硬边。
                        if (pathDistance < s.meadowPathHalfWidth * 0.58f) continue;
                        if (pathDistance < s.meadowPathHalfWidth)
                        {
                            float innerKeep = Mathf.InverseLerp(s.meadowPathHalfWidth * 0.58f,
                                s.meadowPathHalfWidth, pathDistance) * 0.46f;
                            if (Hash2(gx + chunkX * res, gz + chunkZ * res,
                                s.worldSeed ^ 0xA511E9B3u) > innerKeep) continue;
                        }
                        if (pathDistance < s.meadowPathHalfWidth + 1.25f)
                        {
                            float edgeKeep = Mathf.InverseLerp(s.meadowPathHalfWidth,
                                s.meadowPathHalfWidth + 1.25f, pathDistance);
                            if (Hash2(gx + chunkX * res, gz + chunkZ * res, s.worldSeed ^ 0x7F4A7C15u) > edgeKeep)
                                continue;
                        }
                    }
                    Vector3 normal = hm.SampleNormal(cx, cz);
                    if (normal.y < s.grassSlopeLimitCos) continue;
                    float baseH = hm.SampleHeight(cx, cz);

                    // 低频密度噪声: 斑块 ~8m(世界坐标 → 跨 chunk 无缝)
                    // 草原感: 裸露区收窄(12% → 8%), 稀密起伏收窄(0.35~1.65 → 0.55~1.4)
                    // —— 草地整体连片, 只有轻微疏密, 而不是大块秃斑。
                    float n = ValueNoise(cx * 0.125f, cz * 0.125f, s.worldSeed);
                    if (n < 0.08f) continue;               // 小面积裸露区
                    float densityScale = 0.55f + n * 0.85f; // [0.55, 1.40] 轻微稀密

                    int blades = bladesPerCell;

                    for (int b = 0; b < blades; b++)
                    {
                        // 每格独立种子 + 根索引: 格内/根间随机序列完全解耦
                        var rng = new SimpleRandom((int)(chunkSeed + (uint)gx * 73856093u + (uint)gz * 19349663u + (uint)b * 2654435761u));
                        // 暖色高草只作为散点层，不再整片替换主体草，消除肉眼可见的区域边界。
                        bool isBigClump = b == 0 && rng.Next01() > 0.91f;

                        // 按密度概率跳过 → 同区域内部也有稀密起伏(且不破坏确定性)
                        if (rng.Next01() > densityScale) continue;

                        // 抖动: 细草跨格大范围随机(±1.2 格)打散网格感;
                        // 大草用格内小抖动(±0.45 格)—— 1 棵聚成一墩。
                        float jitter = isBigClump ? 0.55f : 1.75f;
                        float jx = (rng.Next01() - 0.5f) * jitter;
                        float jz = (rng.Next01() - 0.5f) * jitter;
                        float wx = cx + jx * cell;
                        float wz = cz + jz * cell;

                        // type01: [0, 0.85) 桶0 细草, [0.85, 1.0) 桶1 大草
                        float type01 = isBigClump
                            ? 0.85f + rng.Next01() * 0.15f
                            : rng.Next01() * 0.85f;

                        float h = hm.SampleHeight(wx, wz);

                        // 高度分层(3A 草地层次感): 不是均匀随机 ——
                        //   25% 矮草(贴地, 密实底层) / 55% 主体 / 20% 高草穗。
                        // 高草穗让风肉眼可见(短草摆幅小, 风感全靠高草), 也是
                        // 交互倒伏"高草软、矮草硬"的素材来源。
                        float h01 = rng.Next01();
                        float minH = s.grassMinHeight, maxH = s.grassMaxHeight;
                        float midLo = minH + (maxH - minH) * 0.25f;
                        float midHi = minH + (maxH - minH) * 0.75f;
                        float height;
                        if (h01 < 0.25f) height = Mathf.Lerp(minH, midLo, rng.Next01());        // 矮层
                        else if (h01 < 0.8f) height = Mathf.Lerp(midLo, midHi, rng.Next01());   // 主体
                        else height = Mathf.Lerp(midHi, maxH, rng.Next01());                    // 高层
                        height *= Mathf.Lerp(0.84f, 1.13f, n); // 连续的簇状高低变化，打破“统一草刷”质感

                        float width = Mathf.Lerp(s.grassMinWidth, s.grassMaxWidth, rng.Next01());
                        // 大草丛格: 实例本身放大(面片草模式的形态差异来源 ——
                        // atlas 分支不读 _GrassModelScale, 桶间大小只能靠实例数据;
                        // 模型模式同样受益, styleXZ 已相应调小避免双重放大)
                        if (isBigClump)
                        {
                            height *= 1.3f;
                            width *= 1.4f;
                        }

                        output.Add(new GrassInstanceData
                        {
                            position = new Vector3(wx, h, wz),
                            yaw = rng.Next01() * Mathf.PI * 2f,
                            height = height,
                            width = width,
                            type01 = type01,
                            seed01 = rng.Next01(),
                        });
                    }
                }
            }
            return output.Count;
        }

        /// <summary>密度档 → 每格草根数。</summary>
        private static int BladesPerCell(GrassDensityLevel density)
        {
            // 近中远逐级减量，但空间采样连续，LOD 切换不会形成方形稀疏块。
            switch (density)
            {
                case GrassDensityLevel.High: return 12;
                case GrassDensityLevel.Normal: return 9;
                default: return 4;
            }
        }

        /// <summary>type01 → 植被样式桶(0=细草, 1=大草), 与 Generate 的分桶区间一致。</summary>
        public static int ModelBucketOf(float type01)
        {
            return type01 < 0.85f ? 0 : 1;
        }
    }
}
