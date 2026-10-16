using UnityEngine;

namespace TerrainGrassDemo
{
    /// <summary>
    /// 确定性噪声工具。
    /// 为什么自己写而不是用 Mathf.PerlinNoise:
    /// 1. Mathf.PerlinNoise 的种子不可控, 而"同一个种子生成同一张地图"是 Chunk 流式加载的前提
    ///    (Chunk 卸载后重新生成, 高度/草地必须和之前完全一致);
    /// 2. 整数格点哈希 + 值噪声足够本 Demo 使用, 实现只有几十行, 便于学习;
    /// 3. 纯整数哈希可以按 Chunk 坐标独立计算, 不存在跨 Chunk 的全局状态。
    /// </summary>
    public static class NoiseUtils
    {
        /// <summary>
        /// 整数格点 → [0,1) 的确定性哈希。
        /// 输入是"整数网格坐标", 输出在 [0,1) 均匀分布 —— 这是值噪声的基础:
        /// 把空间切成整数格, 每格顶点一个伪随机值, 格内双线性插值。
        /// </summary>
        public static float Hash01(int x, int z, uint seed)
        {
            uint h = (uint)x * 0x9E3779B1u + (uint)z * 0x85EBCA6Bu + seed * 0xC2B2AE35u;
            h = (h ^ (h >> 15)) * 0x2C1B3C6Du;
            h = (h ^ (h >> 12)) * 0x297A2D39u;
            h = h ^ (h >> 15);
            return h / 4294967295.0f;
        }

        /// <summary>
        /// 2D 值噪声: 4 个格点哈希值做双线性插值, 平滑阶用 3t²-2t³。
        /// 为什么这个平滑函数: 它保证一阶导在格点处为 0, 否则插值出来的噪声会出现
        /// "格子"伪影, 而地形高度求导(法线/坡度)时会放大这些折痕。
        /// </summary>
        public static float ValueNoise(float x, float z, uint seed)
        {
            int xi = Mathf.FloorToInt(x);
            int zi = Mathf.FloorToInt(z);
            float tx = x - xi;
            float tz = z - zi;
            tx = tx * tx * (3f - 2f * tx);
            tz = tz * tz * (3f - 2f * tz);

            float a = Hash01(xi, zi, seed);
            float b = Hash01(xi + 1, zi, seed);
            float c = Hash01(xi, zi + 1, seed);
            float d = Hash01(xi + 1, zi + 1, seed);

            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
        }

        /// <summary>
        /// 分形叠加(fBm): 多个 octave 的噪声按频率倍增、振幅减半叠加。
        /// 低频决定地形的大起伏, 高频只做小褶皱 —— 一层噪声只有单一尺度, 叠加后才像自然地形。
        /// 每层 seed 偏移一个素数, 避免各层采样到同一组哈希值。
        /// </summary>
        public static float Fbm(float x, float z, int octaves, uint seed)
        {
            float sum = 0f;
            float amp = 0.5f;
            float freq = 1f;
            float norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * ValueNoise(x * freq, z * freq, seed + (uint)i * 101u);
                norm += amp;
                amp *= 0.5f;
                freq *= 2.03f;
            }
            return sum / norm;
        }
    }
}
