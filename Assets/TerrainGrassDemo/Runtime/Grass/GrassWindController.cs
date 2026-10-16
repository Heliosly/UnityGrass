using UnityEngine;

namespace TerrainGrassDemo.Grass
{
    /// <summary>
    /// 草地风场控制器(参考 QnMobile WindComponent 的参数组织, 独立重写)。
    ///
    /// 风动全部在顶点 Shader 完成 —— C# 侧只做两件事(需求 4):
    ///   1. 每帧把参数打包成全局 Shader 属性(_GrassWindDirection/_GrassWindGust/_GrassWindTime);
    ///   2. 程序生成一张可平铺的阵风噪声纹理(_GrassGustTex)。
    /// 没有任何顶点/缓冲上传, 所以草再多风也不增加 CPU 开销。
    ///
    /// 时间累积放 C# 而不是 Shader 的 _Time: 需要把"速度×频率"先乘好,
    /// 让 Shader 里的相位公式保持最简单的加法。
    /// </summary>
    public sealed class GrassWindController
    {
        private const int GustNoiseSize = 128;

        private float m_WindTime;   // 主波累积时间(已乘 windSpeed)
        private float m_GustTime;   // 阵风累积时间(已乘 gustFrequency)
        private readonly Texture2D m_GustNoiseTex;

        public GrassWindController()
        {
            m_GustNoiseTex = CreateGustNoiseTexture();
        }

        /// <summary>
        /// 每帧把风参数写入全局属性。
        /// 注意: 即使 enableWind 关闭也照常累积时间并更新全局参数 ——
        /// 交互倒伏的"颤动"效果依赖 _GrassWindTime 作为时间源;
        /// enableWind 只控制 Shader keyword(风是否作用于顶点), 不影响时间推进。
        /// </summary>
        public void Tick(TerrainGrassDemoSettings s)
        {
            m_WindTime += Time.deltaTime * s.windSpeed;
            m_GustTime += Time.deltaTime * s.gustFrequency;

            // 风向: 度 → xz 平面单位向量
            float rad = s.windDirectionDeg * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

            // xy=风向, z=基础风力, w=风速(保留, Shader 用 _GrassWindTime.x 已含速度)
            Shader.SetGlobalVector("_GrassWindDirection", new Vector4(dir.x, dir.y, s.windStrength, s.windSpeed));
            // x=阵风强度, y=阵风频率, z=空间噪声缩放(世界坐标相位打散, 需求 4.5)
            Shader.SetGlobalVector("_GrassWindGust", new Vector4(s.gustStrength, s.gustFrequency, s.windNoiseScale, 0f));
            // x=主波累积时间, y=阵风累积时间
            Shader.SetGlobalVector("_GrassWindTime", new Vector4(m_WindTime, m_GustTime, 0f, 0f));
            Shader.SetGlobalTexture("_GrassGustTex", m_GustNoiseTex);
        }

        /// <summary>运行时开关: 切换 Shader keyword, 关掉后顶点 Shader 里整段风计算被编译掉。</summary>
        public void ApplyEnabled(bool enabled)
        {
            if (enabled) Shader.EnableKeyword("_GRASS_WIND_ON");
            else Shader.DisableKeyword("_GRASS_WIND_ON");
        }

        /// <summary>
        /// 程序生成可平铺阵风噪声纹理。
        /// 为什么"可平铺"重要: 顶点 Shader 用世界坐标无限采样这张纹理,
        /// 若边界不连续, 每隔 128/0.02=6400 米会出现一道接缝;
        /// 做法: 噪声格点坐标对纹理尺寸取模(环面采样), 边缘自动首尾相接。
        /// 每 2x2 像素对应一个噪声格点, Bilinear 过滤后自然平滑。
        /// </summary>
        private static Texture2D CreateGustNoiseTexture()
        {
            const int grid = GustNoiseSize / 2;
            var tex = new Texture2D(GustNoiseSize, GustNoiseSize, TextureFormat.R8, false)
            {
                name = "GustNoise",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            var pixels = new byte[GustNoiseSize * GustNoiseSize];
            for (int y = 0; y < GustNoiseSize; y++)
            {
                for (int x = 0; x < GustNoiseSize; x++)
                {
                    int gx = (x / 2) % grid;
                    int gy = (y / 2) % grid;
                    float n = NoiseUtils.Hash01(gx, gy, 7789u);
                    pixels[y * GustNoiseSize + x] = (byte)Mathf.RoundToInt(n * 255f);
                }
            }
            tex.SetPixelData(pixels, 0);
            tex.Apply(false, true);
            return tex;
        }

        public void Dispose()
        {
            if (m_GustNoiseTex != null) Object.Destroy(m_GustNoiseTex);
        }
    }
}
