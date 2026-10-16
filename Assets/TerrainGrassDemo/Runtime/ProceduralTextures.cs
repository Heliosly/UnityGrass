using UnityEngine;

namespace TerrainGrassDemo
{
    /// <summary>
    /// 程序生成的占位素材 —— 保证"零素材也能看到效果", 同时作为素材规格的参考。
    ///
    /// 素材接入约定(三种方式, 优先级从高到低):
    ///   1. DemoWorld Inspector 上的 terrainTexture / grassTexture 字段(拖入你的 Texture2D);
    ///   2. Assets/TerrainGrassDemo/Art/Resources/Ground.png 与 GrassAtlas.png
    ///      (菜单 TerrainGrassDemo → 03 Generate Sample Art 会生成它们, 直接替换文件即可换素材);
    ///   3. 都没有 → 运行时用本文件程序生成占位纹理。
    ///
    /// 素材规格:
    ///   Ground.png    : 任意尺寸可平铺贴图(建议 512x512), 会被世界坐标平铺采样;
    ///   GrassAtlas.png: RGBA, N 根草叶横向等宽并排(默认 4 根, 改 DemoWorld 的
    ///                   atlasColumns), 透明背景, 叶尖朝上、叶根在下。
    /// </summary>
    public static class ProceduralTextures
    {
        /// <summary>
        /// 程序生成草叶图集 v2(原神式面片草的细节来源):
        ///   每列 3 片草叶(1 直 + 2 弯, 弯曲集中在上半段 —— 真实草叶的形态),
        ///   分层颜色(根深绿 → 中草绿 → 尖黄绿), 叶脉亮线 + 叶缘微暗,
        ///   边缘 alpha 宽渐变羽化(风中不闪)。
        /// </summary>
        public static Texture2D CreateGrassAtlas(int width, int height, int blades, uint seed)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "GrassAtlas(Procedural)",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color[width * height];
            float bladeWidth = width / (float)blades;
            // 接近中性的叶片明度图。最终季节与植被色由材质统一控制，
            // 避免“纹理高饱和绿 × 材质绿”叠乘后出现荧光色和死黑叶根。
            Color root = new Color(0.60f, 0.68f, 0.50f, 1f);
            Color mid = new Color(0.76f, 0.84f, 0.63f, 1f);
            Color tip = new Color(0.89f, 0.93f, 0.73f, 1f);
            Color tipYellow = new Color(0.96f, 0.94f, 0.72f, 1f);

            for (int b = 0; b < blades; b++)
            {
                int colStart = Mathf.RoundToInt(b * bladeWidth);
                int colEnd = Mathf.RoundToInt((b + 1) * bladeWidth);
                // 每列 3 片: 直立 + 左弯 + 右弯(确定性哈希)
                float bendA = (NoiseUtils.Hash01(b, 77, seed) - 0.5f) * 0.3f;   // 左弯
                float bendB = (NoiseUtils.Hash01(b, 173, seed) - 0.5f) * 0.55f; // 右弯(更弯)
                float lenA = 0.9f + NoiseUtils.Hash01(b, 211, seed) * 0.3f;     // 叶长差异
                float lenB = 0.8f + NoiseUtils.Hash01(b, 311, seed) * 0.35f;
                DrawBlade(pixels, width, height, bladeWidth, colStart, colEnd, 0f, lenA, 0.32f,
                    (b + 0.28f) * bladeWidth, 0f, root, mid, tip, tipYellow);
                DrawBlade(pixels, width, height, bladeWidth, colStart, colEnd, 0f, 0.95f, 0.30f,
                    (b + 0.50f) * bladeWidth, bendA, root, mid, tip, tipYellow);
                DrawBlade(pixels, width, height, bladeWidth, colStart, colEnd, 0f, lenB, 0.28f,
                    (b + 0.72f) * bladeWidth, bendB, root, mid, tip, tipYellow);
            }
            tex.SetPixels(pixels);
            tex.Apply(false, false); // 保持可读: Editor 工具要 EncodeToPNG
            return tex;
        }

        /// <summary>
        /// 画一片弯曲草叶。t 从根(0)到尖(1):
        ///   中心线随 t² 弯弧(叶根直立, 上半段弯 —— 真实草叶),
        ///   宽度随 t² 收窄(根宽 → 尖细),
        ///   颜色三段(根深绿/中草绿/尖黄绿),
        ///   叶脉 = 中心 45% 亮 12%, 叶缘暗 8%。
        /// 边缘 alpha 宽渐变羽化(smoothstep): 风中边缘像素不闪。
        /// </summary>
        private static void DrawBlade(Color[] pixels, int width, int height, float bladeWidth,
            int colStart, int colEnd, float t0, float len,
            float rootHalfW, float baseX, float bend, Color root, Color mid, Color tip, Color tipYellow)
        {
            int yRoot = 0;
            int yTip = Mathf.RoundToInt((height - 1) * len);
            if (yTip > height) yTip = height; // 防御: len 可能 >1, 越界会毁掉整张图
            int written = 0;
            for (int y = yRoot; y < yTip; y++)
            {
                float t = (y - yRoot) / (float)Mathf.Max(1, yTip - yRoot); // 0=根 1=尖
                float tt = t * t;
                float halfW = rootHalfW * bladeWidth * (1f - tt * 0.92f); // 根宽 → 尖细
                float centerX = baseX + bend * tt * bladeWidth * 0.9f;    // 弯曲集中在 t²
                for (int x = colStart; x < colEnd; x++)
                {
                    float dist = Mathf.Abs(x - centerX);
                    // 极窄羽化带 [0.82h, 1.0h]: 边缘只留 2~4 像素过渡。
                    // 踩坑: 宽羽化(0.35~1.08h)让边缘大片 alpha 渐变像素,
                    // AlphaToMask 硬件覆盖混合下露出深色背景 = 每根草一圈黑边。
                    float k = Mathf.Clamp01((dist - halfW * 0.82f) / Mathf.Max(0.0001f, halfW * 0.18f));
                    float alpha = 1f - (k * k * (3f - 2f * k));
                    if (alpha <= 0.01f) continue;
                    written++;
                    // 分层明度: 根部压暗、叶尖略暖，颜色交给草材质完成。
                    Color c;
                    if (t < 0.2f) c = Color.Lerp(root, mid, t / 0.2f);
                    else if (t < 0.7f) c = Color.Lerp(mid, tip, (t - 0.2f) / 0.5f);
                    else c = Color.Lerp(tip, tipYellow, (t - 0.7f) / 0.3f);
                    // 叶脉: 中心亮; 叶缘【提亮】(dist > 0.55h 时 core 为负 → 乘 >1):
                    // 边缘提亮抵消"黑边"(半透明边缘与深色背景/叶根深色混合的观感)
                    float core = 1f - dist / Mathf.Max(halfW * 0.45f, 0.001f);
                    c.r *= 1f + core * 0.12f;
                    c.g *= 1f + core * 0.12f;
                    c.b *= 1f + core * 0.06f;
                    c.a = alpha;
                    pixels[y * width + x] = c;
                }
            }
            if (written == 0 && Mathf.RoundToInt((height - 1) * len) > 0)
            {
                Debug.LogWarning($"[AtlasDebug] 一片叶未写入任何像素! yTip={yTip} halfW={rootHalfW * bladeWidth:F1} baseX={baseX} bend={bend}");
            }
        }

        /// <summary>
        /// 程序生成可平铺地面纹理: 环面值噪声(格点哈希取模) → 无接缝,
        /// 泥土底 + 草绿斑块, 3 个 octave 叠加出自然的斑驳感。
        /// </summary>
        public static Texture2D CreateGroundTexture(int size, uint seed)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Ground(Procedural)",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color[size * size];

            // 环面噪声格点分辨率: 8x8 个格点, 取模保证首尾相接(可平铺)
            const int grid = 8;
            float[,] gridNoise = new float[grid, grid];
            for (int gy = 0; gy < grid; gy++)
                for (int gx = 0; gx < grid; gx++)
                    gridNoise[gx, gy] = NoiseUtils.Hash01(gx, gy, seed);

            Color dirt = new Color(0.30f, 0.26f, 0.17f, 1f);
            Color grass = new Color(0.24f, 0.42f, 0.17f, 1f);
            Color light = new Color(0.45f, 0.52f, 0.26f, 1f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // 连续坐标 → 格点索引(取模=环面)
                    float fx = x / (float)size * grid;
                    float fz = y / (float)size * grid;
                    int gx = Mathf.FloorToInt(fx) % grid;
                    int gz = Mathf.FloorToInt(fz) % grid;
                    int gx1 = (gx + 1) % grid;
                    int gz1 = (gz + 1) % grid;
                    float tx = fx - Mathf.Floor(fx);
                    float tz = fz - Mathf.Floor(fz);
                    tx = tx * tx * (3f - 2f * tx);
                    tz = tz * tz * (3f - 2f * tz);

                    float n0 = Mathf.Lerp(gridNoise[gx, gz], gridNoise[gx1, gz], tx);
                    float n1 = Mathf.Lerp(gridNoise[gx, gz1], gridNoise[gx1, gz1], tx);
                    float n = Mathf.Lerp(n0, n1, tz);

                    // 细粒度高频斑驳(第二层环面噪声, 直接在像素尺度哈希)
                    float speck = NoiseUtils.Hash01(x % 16, y % 16, seed + 999u);

                    Color c = Color.Lerp(dirt, grass, n);
                    c = Color.Lerp(c, light, speck * 0.25f * n);
                    pixels[y * size + x] = c;
                }
            }
            tex.SetPixels(pixels);
            tex.Apply(false, false); // 保持可读: Editor 工具要 EncodeToPNG
            return tex;
        }
    }
}
