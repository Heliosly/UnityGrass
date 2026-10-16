using System;
using UnityEngine;

namespace TerrainGrassDemo
{
    /// <summary>
    /// 草地密度档位。参考 QnMobile CGrassFast 的做法:
    /// 每 skip 个网格单元才放置一根草, skip 越大草越稀疏。
    /// Low=4 / Normal=2 / High=1(每格一根)。
    /// </summary>
    public enum GrassDensityLevel
    {
        Low = 0,
        Normal = 1,
        High = 2,
    }

    /// <summary>
    /// Demo 的全部可调参数。
    /// 放在一个 [Serializable] 类里(而不是散落在各个组件)的原因:
    /// 1. Inspector 上集中可见、可调;
    /// 2. 运行时面板的开关/滑条直接改这些字段, 不需要反射;
    /// 3. 所有子系统(地形/流式/草地/风/交互)共享同一份配置, 不会各存一份造成不一致。
    /// </summary>
    [Serializable]
    public sealed class TerrainGrassDemoSettings
    {
        [Header("地图")]
        public int chunksX = 32;                      // X 方向 Chunk 数(32×32=1024m 大场景)
        public int chunksZ = 32;                      // Z 方向 Chunk 数
        public float chunkWorldSize = 32f;            // 每个 Chunk 的世界边长(米)
        public int chunkGridRes = 64;                 // 每个 Chunk 的网格分辨率(64: 全网格 420 万顶点,
                                                      // 四叉树压缩后 ~80 万 —— 开/关差异肉眼可见)
        public uint worldSeed = 12345u;               // 全局种子, 保证地形/草地可重复生成
        public int terrainStyle = 0;                  // 地形风格: 0=丘陵草原(默认, 程序生成, 缓坡适合草地)
                                                      //            1=山丘+平顶高原(四叉树压缩展示)
                                                      //            2=真实高度图(Art/Resources/Heightmap.png)
        public float grassCellSize = 1f;              // 草格尺寸(与地形网格解耦: 调 chunkGridRes 不影响草密度)

        [Header("地形 / 四叉树")]
        // 平坦判定误差阈值(世界单位): 区域内"四角高度最大差"不超过它即视为平坦、可合并。
        // 为什么默认 0.3 而不是 0.05: 8bit 灰度高度图的量化台阶约 20m/255 ≈ 0.078m,
        // 阈值小于量化台阶时任何相邻格都不"平坦", 四叉树退化为完全不合并(真实地图的坑)。
        // 0.3m 的起伏在视觉上完全看不出来(32m 区块内 0.3m ≈ 1% 坡度), 却能正确合并平坦地块。
        // 想更激进/更保守, 直接改这里或 Inspector。
        public float flatnessError = 0.3f;
        public int quadtreeMinLeafSize = 2;           // 四叉树最小叶节点边长(网格数), 太小会让树过深
        public bool enableQuadtree = true;            // 运行时开关: true 用优化网格, false 用全分辨率网格
        public bool showGizmos = true;                // 是否画 Chunk 边界和四叉树叶节点

        [Header("流式加载(距离环, 单位: Chunk 数)")]
        public bool enableStreaming = true;
        public int nearRingChunks = 2;                // 近环: 地形 + 满密度草
        public int midRingChunks = 4;                 // 中环: 地形 + 低密度草
        public int farRingChunks = 6;                 // 远环: 仅地形
        public float unloadHysteresisChunks = 1f;     // 卸载半径比加载半径多 N 圈, 避免来回抖动
        public float unloadDelaySeconds = 5f;         // 进入待卸载状态后延迟这么久才真正回收
        public int maxLoadsPerFrame = 2;              // 每帧最多生成几个 Chunk(防止卡帧)
        public int maxUnloadsPerFrame = 1;            // 每帧最多卸载几个 Chunk
        public float streamingCheckInterval = 0.25f;  // 相机不动时至少每隔这么久重新检查一次
        public float streamingMoveThreshold = 2f;     // 相机移动超过这个距离才重新计算加载集合

        [Header("草地")]
        public GrassDensityLevel grassDensity = GrassDensityLevel.Normal;
        public float grassLod0Distance = 60f;         // LOD0(完整顶点动画+交互) 的最大距离
        public float grassLod1Distance = 160f;        // LOD1(简化动画, 不采样交互纹理) 的最大距离
        public float grassFadeRange = 20f;            // 在最大距离前开始噪声抖动淡出, 减少突然消失
        public float grassFullDensityDistance = 60f;  // 近密远疏: 此距离内 100% 密度(草原不露地),
                                                      // 之外按 seed01 平滑稀疏到 LOD1 距离
        public float grassMinHeight = 0.45f;
        public float grassMaxHeight = 1.45f;
        public float grassMinWidth = 0.28f;
        public float grassMaxWidth = 0.52f;
        public float grassSlopeLimitCos = 0.15f;      // 坡度过滤: 法线 y 低于此值(约 81° 陡坡)才不长草。
                                                     // 踩坑: 原值 0.55(约 57°)会把真实地图(大峡谷)的峡谷壁
                                                     // 几乎全部滤掉, 只剩零星平坦格子有草 → "每个区块固定位置一两棵"。
        public bool enableGpuCulling = true;          // false 时退化为 CPU 距离剔除, 用于对照
        public bool enableGrassLod = true;            // false 时全部按 LOD0 绘制
        public bool enableGrassRender = true;         // 草渲染总开关(性能矩阵"无草"基线用)
        public bool useModelGrass = true;             // true=美术模型植被(草+花丛混合, 默认);
                                                      // false=程序面片草(原神式弧形草叶+alpha 贴图)

        [Header("场景构图")]
        public bool enableMeadowPath = true;
        public float meadowPathHalfWidth = 0.45f;
        public float meadowPathAmplitude = 8f;
        public float meadowPathFrequency = 0.018f;

        [Header("风场")]
        public bool enableWind = true;                 // 风(默认开)
        public float windDirectionDeg = 45f;          // 风向(度, 实际用 XZ 平面向量)
        public float windStrength = 0.35f;            // 基础风力(弯曲幅度)
        public float windSpeed = 1.2f;                // 主波相位推进速度
        public float gustStrength = 0.5f;             // 阵风强度
        public float gustFrequency = 0.8f;            // 阵风频率(与时间相乘)
        public float windNoiseScale = 0.05f;          // 世界坐标噪声采样缩放(空间相位打散)

        [Header("交互")]
        public bool enableInteraction = true;
        public float interactionExtent = 24f;         // 交互纹理覆盖半宽(米) → 覆盖边长 48m。
                                                      // 踩坑: 原 48 作半宽 → 覆盖 96m/128px = 0.75m/px,
                                                      // 玩家半径 1.8m 只有 2.4 像素 → 笔刷块状生硬
        public int interactionResolution = 256;       // 交互纹理分辨率(256: 48m/256px = 0.19m/px,
                                                      // 笔刷半径 ~9.5 像素, 圆润)
        public float interactionDecayRate = 0.28f;    // 衰减速率: 每帧乘 exp(-rate*dt)。
                                                     // 0.28/秒 ≈ 草 3.5 秒悠悠站直(弹性回弹);
                                                     // 参考 QnMobile: 触摸每帧 ×0.992(~0.62/秒)。
        public float interactionStrength = 0.75f;     // 笔刷最大强度(0..1), 原 0.9 倒得太狠
        public float interactorRadius = 1.8f;         // 玩家笔刷半径(米)
    }
}
