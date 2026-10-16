using System.Collections.Generic;
using TerrainGrassDemo.DebugTools;
using TerrainGrassDemo.Grass;
using TerrainGrassDemo.Interaction;
using TerrainGrassDemo.Streaming;
using TerrainGrassDemo.Terrain;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace TerrainGrassDemo
{
    /// <summary>
    /// Demo 的组装根(Composition Root)。
    /// 一个空场景里只放这一个组件就能跑起来:
    ///   - 保证相机/平行光/玩家存在(没有就自动创建);
    ///   - 创建全局高度图;
    ///   - TerrainChunkStreamer: 按距离环加载/卸载 Chunk;
    ///   - GrassChunkManager: GPU 裁剪 + Indirect 绘制;
    ///   - GrassWindController: 顶点风场(全局 Shader 属性);
    ///   - GrassInteractionController: 低分辨率交互纹理;
    ///   - 运行时开关/统计面板 + Gizmos。
    /// </summary>
    public class DemoWorld : MonoBehaviour
    {
        public TerrainGrassDemoSettings settings = new TerrainGrassDemoSettings();

        /// <summary>视觉版本(面板显示, 确认代码是否生效: 看不到本行 = 没刷新)。</summary>
        public const string VisualVersion = "v9 Aether Meadow · Unity 6 URP";

        [Header("资源引用(留空则启动时自动查找)")]
        public ComputeShader grassCullingCompute;
        public ComputeShader grassInteractionCompute;

        [Header("素材(留空则自动加载 Art/Resources, 再没有则程序生成占位)")]
        public Texture2D heightmapTexture; // 灰度高度图(通用地图入口, 任意尺寸)
        public float heightmapHeightScale = 20f; // 灰度 [0,1] → 世界米 的缩放
        public Texture2D terrainTexture;   // 地面平铺贴图
        public Texture2D grassTexture;     // 草叶图集(RGBA, N 根草叶横向并排)
        public int grassAtlasColumns = 4;  // 草叶图集列数, 与贴图对应

        [Header("对比试验(工具切换, 正常使用不要开)")]
        public bool unityTerrainMode = false; // true = 用 Unity 内置 Terrain 渲染同高度图

        private HeightMapData m_HeightMap;
        private TerrainChunkStreamer m_Streamer;
        private GrassChunkManager m_Grass;
        private GrassWindController m_Wind;
        private GrassInteractionController m_Interaction;
        private MeadowDecorRenderer m_Decor;
        private DemoPlayerController m_Player;
        private GameObject m_PlayerGo;
        private Material m_TerrainMaterial;
        private UnityEngine.Terrain m_UnityTerrain; // 对比模式: Unity 内置地形(全限定避免与 Terrain 子命名空间歧义)
        private bool m_Initialized;

        public HeightMapData HeightMap => m_HeightMap;
        public TerrainChunkStreamer Streamer => m_Streamer;
        public GrassChunkManager Grass => m_Grass;
        public GrassInteractionController Interaction => m_Interaction;
        public DemoPlayerController Player => m_Player;
        /// <summary>世界是否已完成构建(冒烟测试诊断用)。</summary>
        public bool IsInitialized => m_Initialized;

        void Start()
        {
            if (!m_Initialized)
            {
                BuildWorld();
            }
        }

        void Update()
        {
            TickPerfMatrix();
            DriveFrame(Time.deltaTime);
        }

        /// <summary>
        /// 手动驱动一帧(编辑器截图/冒烟工具用, 不依赖 PlayMode 帧循环):
        /// 玩家 + 流式加载 + 风 + 交互 + 草绘制 的完整一帧。
        /// batchmode 下 EnterPlaymode 场景机制不可靠(backup/missing script),
        /// 截图工具用 EditMode 手动驱动替代 —— 同样能产出真实渲染结果。
        /// </summary>
        public void DriveFrame(float dt)
        {
            if (!m_Initialized) return;

            if (m_Player != null) m_Player.DriveUpdate(dt);

            Camera cam = Camera.main;
            if (m_Streamer != null && cam != null)
            {
                m_Streamer.Update(cam.transform.position);
            }
            if (m_Wind != null)
            {
                m_Wind.Tick(settings);
            }
            if (m_Interaction != null && settings.enableInteraction && m_Player != null)
            {
                m_Interaction.Tick(settings, m_Player.transform.position);
            }
            if (m_Grass != null && cam != null)
            {
                m_Grass.LateTick(cam);
            }
            if (m_Decor != null && cam != null)
            {
                m_Decor.Render(cam);
            }
        }

        void LateUpdate()
        {
            // 草绘制已在 DriveFrame 内完成(Update 路径); LateUpdate 保持空实现避免双绘。
            // (原实现在此调 m_Grass.LateTick —— 手动驱动模式与 PlayMode 不能双调)
        }

        void OnDestroy()
        {
            CleanupWorld();
        }

        /// <summary>
        /// 参数迁移: 场景里的 DemoWorld.settings 是序列化保存的, 代码改了默认值不会生效。
        /// 旧场景仍带着旧参数(坡度过滤 0.55 → 草被峡谷壁滤掉; 草片宽 0.06~0.14 → 细不可见),
        /// 表现为"草稀疏"。检测旧特征值并自动修正; 完整参数建议菜单 01 重建场景。
        /// </summary>
        private void MigrateLegacySettings()
        {
            bool changed = false;
            // 旧交互纹理参数(128px/48 半宽): 迁移到 256px/24 半宽(笔刷 9.5px 圆润)
            if (settings.interactionResolution < 256)
            {
                settings.interactionResolution = 256;
                changed = true;
            }
            if (settings.interactionExtent > 40f)
            {
                settings.interactionExtent = 24f;
                changed = true;
            }
            // 旧场景序列化 useModelGrass=true(模型植被时代): 收缩后恒程序草
            if (settings.useModelGrass)
            {
                settings.useModelGrass = false;
                changed = true;
            }
            // 旧地图 16×16 及以下: 迁移到 32×32(1024m, 大场景 + 流式/四叉树对比性)
            if (settings.chunksX <= 16 && settings.chunksZ <= 16)
            {
                settings.chunksX = 32;
                settings.chunksZ = 32;
                changed = true;
            }
            if (settings.grassSlopeLimitCos >= 0.5f)
            {
                settings.grassSlopeLimitCos = 0.15f;
                changed = true;
            }
            // 旧版草簇过宽、过高，在近景呈现规则三角锥；迁移到更纤细的自然比例。
            if (settings.grassMaxWidth > 0.65f)
            {
                settings.grassMinWidth = 0.28f;
                settings.grassMaxWidth = 0.52f;
                changed = true;
            }
            if (settings.grassMaxHeight > 1.65f)
            {
                settings.grassMinHeight = 0.45f;
                settings.grassMaxHeight = 1.45f;
                changed = true;
            }
            // 旧交互参数偏硬(弹回快/倒伏狠): 迁移到柔和手感
            if (settings.interactionDecayRate >= 0.5f)
            {
                settings.interactionDecayRate = 0.35f;
                changed = true;
            }
            if (settings.interactionStrength >= 0.85f)
            {
                settings.interactionStrength = 0.75f;
                changed = true;
            }
            if (changed)
            {
                Debug.Log("[DemoWorld] 已将旧场景参数迁移到 Unity 6 展示预设。");
            }
        }

        /// <summary>创建(或复用)整个场景。</summary>
        public void BuildWorld()
        {
            Debug.Log("[DemoWorld] " + VisualVersion + " (看到本行说明已编译最新代码)");
            // 解锁帧率 + 4x MSAA(草叶细, 抗锯齿对观感重要)
            QualitySettings.vSyncCount = 0;
            QualitySettings.antiAliasing = 4;
            MigrateLegacySettings();

            EnsureCameraAndLight();

            // 高度图来源: 仅"真实高度图"风格(terrainStyle=2)才加载外部图;
            // 0/1 用程序地形(丘陵草原/山丘高原)。
            Texture2D heightTex = settings.terrainStyle == 2
                ? ResolveTexture(heightmapTexture, "Heightmap", null)
                : null;
            if (heightTex != null && !heightTex.isReadable)
            {
                // GetPixels 需要可读纹理: PNG 导入设置需勾选 Read/Write Enabled
                Debug.LogError("[DemoWorld] 高度图 " + heightTex.name + " 不可读, 请在导入设置勾选 Read/Write Enabled, 将回退程序生成地形。");
                heightTex = null;
            }
            m_HeightMap = heightTex != null
                ? HeightMapData.CreateFromTexture(heightTex, settings, heightmapHeightScale)
                : HeightMapData.Create(settings);

            EnsurePlayer(); // 依赖高度图贴地, 必须在 HeightMapData 之后

            // 对比试验模式: 用 Unity 内置 Terrain 渲染同一张高度图(见 TerrainCompare 工具),
            // 用于和"分块 Mesh + 四叉树"方案做同地图对比
            if (unityTerrainMode)
            {
                BuildUnityTerrain();
                m_Initialized = true;
                Debug.Log("[DemoWorld] 对比模式: 使用 Unity 内置 Terrain, 高度图 "
                          + m_HeightMap.VertsX + "x" + m_HeightMap.VertsZ);
                return;
            }

            // 素材解析: Inspector 字段 > Art/Resources > 程序生成占位
            Texture2D groundTex = ResolveTexture(terrainTexture, "Ground", () => ProceduralTextures.CreateGroundTexture(256, settings.worldSeed));

            // 收缩(整改第1步): 一律程序面片草(2 桶: 细草+大草), 不再加载模型植被
            Texture2D grassTex = ResolveTexture(grassTexture, "GrassAtlas", () => ProceduralTextures.CreateGrassAtlas(512, 256, grassAtlasColumns, settings.worldSeed));

            m_TerrainMaterial = CreateTerrainMaterial(groundTex);

            // 全地形共享的"有用顶点"表: 所有 Chunk 的叶角点标记进同一张表,
            // 相邻 Chunk 共享边互相可见 → 无 T 型裂缝, 且不额外保留任何顶点(见 BuildGlobalUsefulTable)。
            bool[] globalUseful = TerrainQuadTree.BuildGlobalUsefulTable(m_HeightMap, settings);

            m_Streamer = new TerrainChunkStreamer(m_HeightMap, settings, m_TerrainMaterial, globalUseful, transform);

            m_Grass = new GrassChunkManager(m_HeightMap, settings, ResolveComputeShader("GrassCulling", ref grassCullingCompute),
                grassTex, grassAtlasColumns, null, null, null);
            m_Streamer.OnChunkLevelChanged += m_Grass.HandleChunkLevelChanged;

            m_Wind = new GrassWindController();
            m_Wind.ApplyEnabled(settings.enableWind);
            m_Decor = new MeadowDecorRenderer(m_HeightMap, settings);

            ComputeShader interactionCompute = ResolveComputeShader("GrassInteraction", ref grassInteractionCompute);
            if (interactionCompute != null)
            {
                m_Interaction = new GrassInteractionController(interactionCompute,
                    settings.interactionResolution, settings.interactionExtent);
                m_Interaction.ApplyEnabled(settings.enableInteraction);
                if (m_Player != null)
                {
                    m_Interaction.AddInteractor(m_Player.Interactor);
                }
            }
            else
            {
                m_Interaction = null;
            }

            // 流式关闭时: 一次性全量加载; 流式开启时: 从空开始, 由 Update 按距离环驱动
            m_Streamer.Initialize(!settings.enableStreaming);

            // 调试组件(面板 + Gizmos)挂在同一个 GameObject 上
            var panel = GetComponent<RenderingStatsPanel>();
            if (panel == null) panel = gameObject.AddComponent<RenderingStatsPanel>();
            panel.Bind(this);
            var gizmos = GetComponent<DemoGizmos>();
            if (gizmos == null) gizmos = gameObject.AddComponent<DemoGizmos>();
            gizmos.Bind(this);

            m_Initialized = true;
            Debug.Log("[DemoWorld] 场景系统就绪: " + (settings.chunksX * settings.chunksZ) + " chunks, 高度图 "
                      + m_HeightMap.VertsX + "x" + m_HeightMap.VertsZ
                      + ", 流式加载 " + (settings.enableStreaming ? "开" : "关"));
        }

        /// <summary>
        /// ComputeShader 解析: 优先 Inspector 引用, 否则遍历已加载资源按名字找。
        /// (ComputeShader 没有 Shader.Find 等价物; 正式项目应走 Resources/Addressables)
        /// </summary>
        private static ComputeShader ResolveComputeShader(string name, ref ComputeShader field)
        {
            if (field != null) return field;
            var all = Resources.FindObjectsOfTypeAll<ComputeShader>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name == name)
                {
                    field = all[i];
                    return field;
                }
            }
            Debug.LogError("[DemoWorld] 找不到 " + name + ".compute。请在 Inspector 手动拖入, 或确认 ComputeShaders 目录已导入。");
            return null;
        }

        public void CleanupWorld()
        {
            // 先草后地形(与流式卸载的顺序约定一致)
            if (m_Grass != null)
            {
                m_Grass.Dispose();
                m_Grass = null;
            }
            if (m_Streamer != null)
            {
                m_Streamer.Dispose();
                m_Streamer = null;
            }
            if (m_Wind != null)
            {
                m_Wind.Dispose();
                m_Wind = null;
            }
            if (m_Interaction != null)
            {
                m_Interaction.Dispose();
                m_Interaction = null;
            }
            if (m_UnityTerrain != null)
            {
                var td = m_UnityTerrain.terrainData;
                SafeDestroy(m_UnityTerrain.gameObject);
                if (td != null) SafeDestroy(td);
                m_UnityTerrain = null;
            }
            if (m_TerrainMaterial != null)
            {
                SafeDestroy(m_TerrainMaterial);
                m_TerrainMaterial = null;
            }
            if (m_Decor != null)
            {
                m_Decor.Dispose();
                m_Decor = null;
            }
            if (m_PlayerGo != null)
            {
                SafeDestroy(m_PlayerGo);
                m_PlayerGo = null;
                m_Player = null;
            }
            m_Initialized = false;
        }

        /// <summary>
        /// EditMode 兼容销毁: PlayMode 用 Destroy(帧尾), EditMode 用 DestroyImmediate。
        /// (EditMode 截图/冒烟工具在 Editor 模式下调用 BuildWorld/CleanupWorld,
        /// 直接 Destroy 会抛 "Destroy may not be called from edit mode")。
        /// </summary>
        private static void SafeDestroy(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
            else UnityEngine.Object.DestroyImmediate(obj);
        }

        private void EnsureCameraAndLight()
        {
            if (Camera.main == null)
            {
                var camGo = new GameObject("Main Camera");
                camGo.tag = "MainCamera";
                var cam = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
                cam.farClipPlane = 800f;
                cam.nearClipPlane = 0.08f;
                cam.fieldOfView = 54f;
                cam.allowHDR = true;

                // 关键: 相机初始位置必须放在地图中心上方, 而不是默认原点 (0,0,0)。
                // 流式加载以"相机位置"为中心计算距离环 —— 相机在原点会导致玩家出生点
                // (地图中心)的 Chunk 不在加载范围, 玩家脚下没有 MeshCollider → 自由落体穿地。
                // 高度 35m + 俯角 50°: 草原地平线 + 天空的构图(旧 70m 俯瞰像地图编辑器)。
                float mapX = settings.chunksX * settings.chunkWorldSize;
                float mapZ = settings.chunksZ * settings.chunkWorldSize;
                camGo.transform.position = new Vector3(mapX * 0.5f, 35f, mapZ * 0.5f - 40f);
                camGo.transform.rotation = Quaternion.Euler(35f, 0f, 0f);
            }

            if (FindFirstObjectByType<Light>() == null)
            {
                var lightGo = new GameObject("Directional Light");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
            }

            // 方向光统一顺光(初始相机在南侧看北): 旧场景序列化的侧光角度 (50,-30,0)
            // 会让草地背光发暗(画面分析实测平均亮度仅 ~70/255)。想要自定义光照,
            // 面板/Inspector 随时可改。
            var existingSun = FindFirstObjectByType<Light>();
            if (existingSun != null)
            {
                existingSun.transform.rotation = Quaternion.Euler(48f, -8f, 0f);
            }

            // 方向光阴影: 峡谷壁给谷底投阴影 = 大世界立体感(草 Cull Off 不参与)
            var sun = FindFirstObjectByType<Light>();
            if (sun != null)
            {
                sun.shadows = LightShadows.Soft;
                sun.shadowStrength = 0.68f;
                sun.intensity = 1.52f;
                sun.color = new Color(1.0f, 0.95f, 0.84f, 1f);
                sun.useColorTemperature = true;
                sun.colorTemperature = 5700f;
            }

            // 程序渐变天空盒(与雾色/空气透视同色系), 太阳方向跟随方向光
            var skyShader = Shader.Find("TerrainGrassDemo/SkyGradient");
            if (skyShader != null)
            {
                var skyMat = new Material(skyShader);
                RenderSettings.skybox = skyMat;
                skyMat.SetColor("_TopColor", new Color(0.16f, 0.46f, 0.78f, 1f));
                skyMat.SetColor("_HorizonColor", new Color(0.76f, 0.90f, 0.97f, 1f));
                skyMat.SetColor("_BottomColor", new Color(0.48f, 0.69f, 0.62f, 1f));
                skyMat.SetColor("_SunColor", new Color(1.0f, 0.84f, 0.50f, 1f));
                skyMat.SetFloat("_Exposure", 1.12f);
                if (sun != null) skyMat.SetVector("_SunDirection", -sun.transform.forward);
            }

            // 大世界氛围: 淡雾 + 环境光, 与草/地形 shader 的空气透视同色系(浅蓝灰)
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 125f;
            RenderSettings.fogEndDistance = 510f;
            RenderSettings.fogColor = new Color(0.70f, 0.84f, 0.91f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.52f, 0.68f, 0.86f);
            RenderSettings.ambientEquatorColor = new Color(0.50f, 0.62f, 0.53f);
            RenderSettings.ambientGroundColor = new Color(0.20f, 0.29f, 0.22f);
            RenderSettings.ambientIntensity = 1.08f;
            RenderSettings.reflectionIntensity = 0.82f;

            SetupPostProcessing(Camera.main);
        }

        private static void SetupPostProcessing(Camera camera)
        {
            if (camera != null)
            {
                var cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
                if (cameraData == null) cameraData = camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
                cameraData.renderPostProcessing = true;
                cameraData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                cameraData.antialiasingQuality = AntialiasingQuality.High;
            }

            const string volumeName = "Meadow Global Volume";
            var volumeObject = GameObject.Find(volumeName);
            if (volumeObject == null) volumeObject = new GameObject(volumeName);
            var volume = volumeObject.GetComponent<Volume>();
            if (volume == null) volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 20f;

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "Meadow Runtime Look";

            var tonemapping = profile.Add<Tonemapping>(true);
            tonemapping.mode.Override(TonemappingMode.ACES);

            var bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(0.27f);
            bloom.threshold.Override(0.92f);
            bloom.scatter.Override(0.68f);

            var color = profile.Add<ColorAdjustments>(true);
            color.postExposure.Override(0.20f);
            color.contrast.Override(3f);
            color.saturation.Override(5f);
            color.colorFilter.Override(new Color(0.98f, 1.0f, 0.985f, 1f));

            var whiteBalance = profile.Add<WhiteBalance>(true);
            whiteBalance.temperature.Override(4f);
            whiteBalance.tint.Override(3f);

            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.12f);
            vignette.smoothness.Override(0.38f);
            volume.sharedProfile = profile;
        }

        /// <summary>
        /// 玩家: 纯视觉胶囊 + DemoPlayerController(高度图贴地行走, 无 CharacterController/物理碰撞)。
        /// 相机控制也由 DemoPlayerController 负责(第三人称跟随 / F 自由飞行)。
        /// </summary>
        private void EnsurePlayer()
        {
            if (m_Player != null) return;

            m_PlayerGo = new GameObject("Player");

            // 仙境主题的悬浮精灵：既是尺度参照，也是玩家交互中心，避免占位人偶破坏画面。
            var core = CreatePlayerPart("Wisp Core", PrimitiveType.Sphere, new Vector3(0f, 1.25f, 0f),
                new Vector3(0.42f, 0.42f, 0.42f), new Color(1.0f, 0.90f, 0.62f, 1f));
            var wingL = CreatePlayerPart("Wisp Wing L", PrimitiveType.Sphere, new Vector3(-0.34f, 1.28f, 0.02f),
                new Vector3(0.48f, 0.09f, 0.28f), new Color(0.74f, 0.58f, 1.0f, 1f));
            var wingR = CreatePlayerPart("Wisp Wing R", PrimitiveType.Sphere, new Vector3(0.34f, 1.28f, 0.02f),
                new Vector3(0.48f, 0.09f, 0.28f), new Color(1.0f, 0.57f, 0.83f, 1f));
            wingL.transform.localRotation = Quaternion.Euler(8f, 18f, -22f);
            wingR.transform.localRotation = Quaternion.Euler(-8f, -18f, 22f);
            CreatePlayerPart("Wisp Tail", PrimitiveType.Sphere, new Vector3(0f, 0.90f, -0.04f),
                new Vector3(0.16f, 0.38f, 0.16f), new Color(0.42f, 0.90f, 0.92f, 1f));

            var glow = new GameObject("Wisp Glow");
            glow.transform.SetParent(m_PlayerGo.transform, false);
            glow.transform.localPosition = new Vector3(0f, 1.28f, 0f);
            var glowLight = glow.AddComponent<Light>();
            glowLight.type = LightType.Point;
            glowLight.color = new Color(0.55f, 0.88f, 1f);
            glowLight.intensity = 1.6f;
            glowLight.range = 5.5f;
            glowLight.shadows = LightShadows.None;

            m_Player = m_PlayerGo.AddComponent<DemoPlayerController>();
            Vector3 spawn = FindSpawnPosition(); // 出生在相对平坦处, 避免峡谷底/峭壁上出生

            m_PlayerGo.transform.position = spawn;
            if (m_HeightMap != null)
            {
                m_Player.Initialize(m_HeightMap, spawn);
            }
        }

        private GameObject CreatePlayerPart(string partName, PrimitiveType primitive, Vector3 localPosition,
            Vector3 localScale, Color color)
        {
            var part = GameObject.CreatePrimitive(primitive);
            part.name = partName;
            part.transform.SetParent(m_PlayerGo.transform, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = localScale;
            var collider = part.GetComponent<Collider>();
            if (collider != null) SafeDestroy(collider);

            var renderer = part.GetComponent<Renderer>();
            if (renderer == null) return part;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var material = new Material(shader) { name = "Traveler " + partName };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.24f);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            if (material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color * 0.14f);
            }
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            return part;
        }

        /// <summary>
        /// 选择出生点: 遍历高度图网格, 找"3x3 邻域高度差最小"的位置。
        /// 为什么: 程序地图中心是缓坡没问题, 但真实地图(大峡谷)中心可能是峡谷底或峭壁,
        /// 出生在那种地方, 玩家行动受限、相机追高后视角也很怪。
        /// 遍历带 8 格步长 + 避开边缘, 6.6 万个点只需几千次采样, 毫秒级。
        /// </summary>
        private Vector3 FindSpawnPosition()
        {
            // 程序草原从世界中心出发，镜头能顺着构图路径看到远方；
            // 外部真实高度图仍使用下方的平坦点搜索，避免出生在悬崖。
            if (settings.terrainStyle != 2)
            {
                return new Vector3(
                    m_HeightMap.ChunksX * m_HeightMap.ChunkWorldSize * 0.5f,
                    0f,
                    m_HeightMap.ChunksZ * m_HeightMap.ChunkWorldSize * 0.5f);
            }

            int vx = m_HeightMap.VertsX;
            int vz = m_HeightMap.VertsZ;
            int margin = 48; // 避开地图边缘(边缘高度数据/四叉树边界效果不稳)
            int step = 8;
            float bestSpread = float.MaxValue;
            int bestX = vx / 2, bestZ = vz / 2;
            for (int z = margin; z < vz - margin; z += step)
            {
                for (int x = margin; x < vx - margin; x += step)
                {
                    float hmin = float.MaxValue, hmax = float.MinValue;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            float v = m_HeightMap.Heights[(z + dz) * vx + (x + dx)];
                            if (v < hmin) hmin = v;
                            if (v > hmax) hmax = v;
                        }
                    }
                    float spread = hmax - hmin;
                    if (spread < bestSpread)
                    {
                        bestSpread = spread;
                        bestX = x;
                        bestZ = z;
                    }
                }
            }
            return new Vector3(bestX * m_HeightMap.CellSize, 0f, bestZ * m_HeightMap.CellSize);
        }

        /// <summary>
        /// 对比模式: 用 Unity 内置 Terrain 渲染同一张高度图。
        /// TerrainData 整块常驻(无流式/无 LOD 简化), 用于与分块方案做同地图实测对比。
        /// </summary>
        private void BuildUnityTerrain()
        {
            var td = new TerrainData();
            int res = m_HeightMap.VertsX; // 257x257 高度采样点 = 256x256 网格
            td.heightmapResolution = res;

            // 高度范围: 全图最大高度(遍历一次 6.6 万个采样, 微秒级)
            float maxH = 1f;
            for (int i = 0; i < m_HeightMap.Heights.Length; i++)
            {
                if (m_HeightMap.Heights[i] > maxH) maxH = m_HeightMap.Heights[i];
            }
            maxH = Mathf.Max(maxH, 16f);

            td.size = new Vector3(m_HeightMap.VertsX * m_HeightMap.CellSize, maxH, m_HeightMap.VertsZ * m_HeightMap.CellSize);

            var heights = new float[res, res];
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    heights[z, x] = Mathf.Clamp01(m_HeightMap.Heights[z * m_HeightMap.VertsX + x] / maxH);
                }
            }
            td.SetHeights(0, 0, heights);

            var go = UnityEngine.Terrain.CreateTerrainGameObject(td);
            m_UnityTerrain = go.GetComponent<UnityEngine.Terrain>();
            m_UnityTerrain.transform.position = Vector3.zero;
        }

        /// <summary>
        /// 美术草模型组: 3 种草混合(细叶草/大丛草/高杆草), 形成草地感而不是单一草形。
        /// 每个模型独立归一化(scaleY = 1/模型高, scaleXZ = 1/模型直径, 读 bounds 不需 Read/Write)。
        /// 全部缺失 → 返回 null → 回退程序生成草簇。
        /// </summary>
        private static void ResolveGrassMeshes(out Mesh[] meshes, out float[] scaleY, out float[] scaleXZ)
        {
            // Quaternius Nature Pack (CC0): 草 ×4 + 灌木着色成花丛 ×2 + 树 + 石头。
            // 8 桶: 矮草 / 标准草 / 高草 / 大草 / 白花丛 / 黄花丛 / 树 / 石头。
            string[] names =
            {
                "Models/Grass2", "Models/Grass3", "Models/Grass3", "Models/Grass3",
                "Models/Bush2", "Models/Bush3", "Models/Tree1", "Models/Rock1",
            };
            // 桶风格系数: 归一化后再乘(树 2.4m, 石头 ~1m)
            float[] styleY = { 0.7f, 1.0f, 1.35f, 1.5f, 0.9f, 1.1f, 0.75f, 0.65f };
            float[] styleXZ = { 1.25f, 1.3f, 1.0f, 1.3f, 1.3f, 1.2f, 0.75f, 0.65f };
            meshes = new Mesh[names.Length];
            scaleY = new float[names.Length];
            scaleXZ = new float[names.Length];
            bool any = false;
            for (int i = 0; i < names.Length; i++)
            {
                var m = Resources.Load<Mesh>(names[i]);
                if (m == null)
                {
                    // 逐个报告: 缺失 = OBJ 未导入(需要 Ctrl+R 刷新)或不在 Resources 下
                    Debug.LogWarning("[DemoWorld] 草模型加载失败: Resources/" + names[i]
                        + " → 该桶将复用其他模型。若刚添加 OBJ 文件, 请 Ctrl+R 刷新后再试。");
                    continue;
                }
                float h = m.bounds.size.y;
                if (h <= 0.001f) continue;
                meshes[i] = m;
                scaleY[i] = (1f / h) * styleY[i];
                scaleXZ[i] = (1f / h) * styleXZ[i];
                any = true;
                Debug.Log("[DemoWorld] 草模型加载成功: Resources/" + names[i] + " (高 " + h.ToString("F3") + ")");
            }
            if (!any)
            {
                meshes = null;
                return;
            }
            // 缺失的桶用第一个可用模型兜底, 保证三桶都有 mesh
            for (int i = 0; i < names.Length; i++)
            {
                if (meshes[i] == null)
                {
                    meshes[i] = meshes[0];
                    scaleY[i] = scaleY[0];
                    scaleXZ[i] = scaleXZ[0];
                }
            }
            Debug.Log("[DemoWorld] 使用 3 种美术草模型混合 (Kenney CC0): 细叶草/大丛草/高杆草");
        }

        /// <summary>素材解析: 显式引用 → Resources 约定路径 → 程序生成占位(fallback 可为 null)。</summary>
        private static Texture2D ResolveTexture(Texture2D explicitField, string resourceName, System.Func<Texture2D> fallback)
        {
            if (explicitField != null) return explicitField;
            var fromResources = Resources.Load<Texture2D>(resourceName);
            if (fromResources != null)
            {
                Debug.Log("[DemoWorld] 从 Art/Resources 加载素材: " + resourceName);
                return fromResources;
            }
            if (fallback == null) return null;
            Debug.Log("[DemoWorld] 未找到素材 " + resourceName + ", 使用程序生成占位纹理(菜单 03 可生成可替换的 PNG)。");
            return fallback();
        }

        private Material CreateTerrainMaterial(Texture2D groundTex)
        {
            Shader shader = Shader.Find("TerrainGrassDemo/TerrainGround");
            if (shader == null)
            {
                Debug.LogError("[DemoWorld] 找不到 TerrainGrassDemo/TerrainGround Shader, 请确认 Shaders 目录已导入");
                return null;
            }
            var mat = new Material(shader);
            mat.SetFloat("_LowHeight", 0f);
            mat.SetFloat("_HighHeight", 16f);
            mat.SetFloat("_Tiling", 4.5f);
            mat.SetFloat("_TexTint", 0.58f);
            mat.SetFloat("_NormalStrength", 0.72f);
            mat.SetFloat("_Roughness", 0.90f);
            mat.SetColor("_ColorLow", new Color(0.055f, 0.21f, 0.13f, 1f));
            mat.SetColor("_ColorMid", new Color(0.18f, 0.39f, 0.20f, 1f));
            mat.SetColor("_ColorHigh", new Color(0.38f, 0.50f, 0.32f, 1f));
            mat.SetVector("_WorldCenter", new Vector4(
                m_HeightMap.ChunksX * m_HeightMap.ChunkWorldSize * 0.5f,
                m_HeightMap.ChunksZ * m_HeightMap.ChunkWorldSize * 0.5f, 0f, 0f));
            mat.SetVector("_PathParams", new Vector4(settings.meadowPathHalfWidth,
                settings.meadowPathAmplitude, settings.meadowPathFrequency,
                settings.enableMeadowPath ? 1f : 0f));
            mat.SetColor("_PathColor", new Color(0.52f, 0.40f, 0.24f, 1f));
            if (groundTex != null)
            {
                // 各向异性过滤: 斜视时地面保持清晰(默认 Bilinear 斜视会糊)
                groundTex.anisoLevel = 8;
                mat.SetTexture("_MainTex", groundTex);
            }
            // 地面细节贴图(ambientCG/Quixel CC0): AO 遮蔽 + 法线(现代地形感的关键)
            var groundAO = Resources.Load<Texture2D>("GroundAO");
            if (groundAO != null)
            {
                mat.SetTexture("_GroundAO", groundAO);
            }
            else
            {
                Debug.LogWarning("[DemoWorld] 未找到 Resources/GroundAO(地面 AO 缺失, 明暗细节会弱)");
            }
            var groundN = Resources.Load<Texture2D>("GroundN");
            if (groundN != null)
            {
                mat.SetTexture("_GroundNormal", groundN);
            }
            else
            {
                Debug.LogWarning("[DemoWorld] 未找到 Resources/GroundN(地面法线缺失, 起伏感会弱)");
            }
            return mat;
        }

        // ---------------- 运行时开关 ----------------

        public void SetQuadtreeEnabled(bool enabled)
        {
            settings.enableQuadtree = enabled;
            if (m_Streamer == null) return;
            foreach (var chunk in m_Streamer.AllBuiltChunks)
            {
                chunk.SetOptimized(enabled);
            }
        }

        public void SetStreamingEnabled(bool enabled)
        {
            settings.enableStreaming = enabled;
            if (m_Streamer != null)
            {
                m_Streamer.SetStreamingEnabled(enabled);
            }
        }

        public void SetGrassDensity(GrassDensityLevel density)
        {
            settings.grassDensity = density;
            if (m_Grass != null)
            {
                m_Grass.SetDensity(density);
            }
        }

        public void SetWindEnabled(bool enabled)
        {
            settings.enableWind = enabled;
            if (m_Wind != null) m_Wind.ApplyEnabled(enabled);
        }

        public void SetInteractionEnabled(bool enabled)
        {
            settings.enableInteraction = enabled;
            if (m_Interaction != null) m_Interaction.ApplyEnabled(enabled);
        }

        // ---- 性能矩阵(Player 内一键跑, 预热后取平均, 输出帧耗时/FPS 报告) ----
        private static readonly string[] PerfStageNames =
        {
            "1 无草基线(地形四叉树)", "2 无草+四叉树关(全网格)", "3 草+LOD关", "4 草+LOD开",
            "5 GPU Culling关", "6 GPU Culling开", "7 风开", "8 交互开(全开)",
            "9 密度 Low", "10 密度 Normal", "11 密度 High",
        };
        private const float PerfWarmup = 1.0f; // 切换后预热(不计入), 等生成/管线稳定
        private const float PerfSample = 3.0f; // 稳定后采样时长(求平均)
        private int m_PerfStage = -1; // -1=未运行
        private float m_PerfStageTime;
        private float m_PerfStageSum;
        private int m_PerfStageFrames;
        private readonly System.Text.StringBuilder m_PerfReport = new System.Text.StringBuilder();
        private bool m_PerfPrevGrass, m_PerfPrevLod, m_PerfPrevGpu, m_PerfPrevWind, m_PerfPrevInteraction, m_PerfPrevQuadtree;
        private GrassDensityLevel m_PerfPrevDensity;

        /// <summary>运行性能矩阵: 11 个阶段 × (1s 预热 + 3s 采样), 结果写 PerfMatrix.txt。</summary>
        public void RunPerfMatrix()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[PerfMatrix] 只能在 Play Mode 运行，已忽略本次请求，避免修改场景序列化参数。");
                return;
            }
            if (m_PerfStage >= 0) return; // 已在运行
            m_PerfPrevGrass = settings.enableGrassRender;
            m_PerfPrevLod = settings.enableGrassLod;
            m_PerfPrevGpu = settings.enableGpuCulling;
            m_PerfPrevWind = settings.enableWind;
            m_PerfPrevInteraction = settings.enableInteraction;
            m_PerfPrevQuadtree = settings.enableQuadtree;
            m_PerfPrevDensity = settings.grassDensity;
            m_PerfStage = 0;
            m_PerfReport.Clear();
            m_PerfReport.AppendLine("=== TerrainGrassDemo 性能矩阵 (预热1s+采样3s, 平均帧耗时/FPS)===");
            m_PerfReport.AppendLine("时间: " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            m_PerfReport.AppendLine("阶段                     帧耗时 ms   平均 FPS");
            ApplyPerfStage(0);
            Debug.Log("[PerfMatrix] 开始: 11 阶段 × 4 秒 ≈ 44 秒, 结果写 PerfMatrix.txt");
        }

        /// <summary>每个阶段显式设置全部开关(不依赖前一阶段状态)。</summary>
        private void ApplyPerfStage(int stage)
        {
            bool grassOn = stage >= 2;                                  // 1/2 无草
            bool lodOn = stage >= 3;                                    // 3 LOD 关
            bool gpuCullOn = stage >= 5;                                // 5 GPU Culling 关
            bool windOn = stage >= 6;                                   // 7 风开
            bool interactOn = stage >= 7;                               // 8 交互开
            bool quadtreeOn = stage != 1;                               // 2 四叉树关
            GrassDensityLevel density = stage < 8 ? GrassDensityLevel.High
                : stage == 8 ? GrassDensityLevel.Low
                : stage == 9 ? GrassDensityLevel.Normal
                : GrassDensityLevel.High;

            settings.enableGrassRender = grassOn;
            settings.enableGrassLod = lodOn;
            settings.enableGpuCulling = gpuCullOn;
            settings.enableWind = windOn;
            settings.enableInteraction = interactOn;
            SetQuadtreeEnabled(quadtreeOn);
            if (grassOn && m_Grass != null && settings.grassDensity != density)
            {
                m_Grass.SetDensity(density); // 密度切换触发重建(预热期覆盖)
            }
            settings.grassDensity = density;
            if (m_Wind != null) m_Wind.ApplyEnabled(windOn);
            if (m_Interaction != null) m_Interaction.ApplyEnabled(interactOn);
            m_PerfStageTime = 0f;
            m_PerfStageSum = 0f;
            m_PerfStageFrames = 0;
        }

        private void TickPerfMatrix()
        {
            if (m_PerfStage < 0) return;
            float dt = Time.unscaledDeltaTime;
            m_PerfStageTime += dt;
            if (m_PerfStageTime < PerfWarmup) return; // 预热期不计入采样

            m_PerfStageSum += dt;
            m_PerfStageFrames++;

            if (m_PerfStageTime < PerfWarmup + PerfSample) return;
            // 本阶段结束: 记录平均帧耗时 + 平均 FPS
            float avgMs = m_PerfStageSum / Mathf.Max(1, m_PerfStageFrames) * 1000f;
            float avgFps = 1000f / Mathf.Max(0.0001f, avgMs);
            m_PerfReport.AppendLine(string.Format("{0,-26} {1,8:F2}   {2,8:F0}", PerfStageNames[m_PerfStage], avgMs, avgFps));
            Debug.Log($"[PerfMatrix] {PerfStageNames[m_PerfStage]}: {avgMs:F2} ms ({avgFps:F0} fps)");

            m_PerfStage++;
            if (m_PerfStage >= PerfStageNames.Length)
            {
                // 完成后恢复调用前的全部状态，性能工具不得污染用户场景或后续截图。
                settings.enableGrassRender = m_PerfPrevGrass;
                settings.enableGrassLod = m_PerfPrevLod;
                settings.enableGpuCulling = m_PerfPrevGpu;
                settings.enableWind = m_PerfPrevWind;
                settings.enableInteraction = m_PerfPrevInteraction;
                settings.grassDensity = m_PerfPrevDensity;
                SetQuadtreeEnabled(m_PerfPrevQuadtree);
                if (m_Grass != null) m_Grass.SetDensity(m_PerfPrevDensity);
                if (m_Wind != null) m_Wind.ApplyEnabled(m_PerfPrevWind);
                if (m_Interaction != null) m_Interaction.ApplyEnabled(m_PerfPrevInteraction);
                m_PerfStage = -1;
                string path = "Assets/TerrainGrassDemo/PerfMatrix.txt";
                System.IO.File.WriteAllText(path, m_PerfReport.ToString(), System.Text.Encoding.UTF8);
                Debug.Log("[PerfMatrix] 完成: " + path);
                return;
            }
            ApplyPerfStage(m_PerfStage);
        }

        /// <summary>
        /// 运行时切换草形态(程序面片草 ↔ 美术模型草):
        /// 重建草地管理器(材质 keyword + 模型数组), 并对已加载 Chunk 重新生成草。
        /// </summary>
        public void SetGrassShape(bool model)
        {
            if (settings.useModelGrass == model) return;
            settings.useModelGrass = model;

            if (m_Grass != null)
            {
                m_Streamer.OnChunkLevelChanged -= m_Grass.HandleChunkLevelChanged;
                m_Grass.Dispose();
                m_Grass = null;
            }
            ResolveGrassMeshes(out Mesh[] grassMeshes, out float[] grassScaleY, out float[] grassScaleXZ);
            if (!model) grassMeshes = null;
            Texture2D grassTex = grassMeshes == null
                ? ResolveTexture(grassTexture, "GrassAtlas", () => ProceduralTextures.CreateGrassAtlas(512, 256, grassAtlasColumns, settings.worldSeed))
                : null;
            m_Grass = new GrassChunkManager(m_HeightMap, settings, ResolveComputeShader("GrassCulling", ref grassCullingCompute),
                grassTex, grassAtlasColumns, grassMeshes, grassScaleY, grassScaleXZ);
            m_Streamer.OnChunkLevelChanged += m_Grass.HandleChunkLevelChanged;
            m_Streamer.ReapplyLevels();
            Debug.Log("[DemoWorld] 草形态切换: " + (model ? "美术模型草" : "程序面片草(原神式)"));
        }

        /// <summary>
        /// 按当前四叉树参数(叶大小/平坦误差)重建全部地形。
        /// 面板滑条改 settings 后调用: 重建 globalUseful 表 + 销毁全部 Chunk 重新构建。
        /// </summary>
        public void RebuildTerrain()
        {
            if (m_Streamer == null) return;
            bool[] globalUseful = TerrainQuadTree.BuildGlobalUsefulTable(m_HeightMap, settings);
            m_Streamer.SetGlobalUseful(globalUseful);
            m_Streamer.RebuildAll();
            Debug.Log("[DemoWorld] 地形已按新四叉树参数重建: 叶大小=" + settings.quadtreeMinLeafSize
                + " 平坦误差=" + settings.flatnessError.ToString("F3"));
        }

        /// <summary>
        /// 地形统计:
        ///   full/opt = 所有已构建 Chunk 的全网格/优化网格总量;
        ///   active = 当前实际生效(已加载 Chunk × 当前开关)的总量。
        /// 数据全部来自真实生成的 Mesh, 不经过任何估算。
        /// 对比模式(Unity Terrain)下返回 Terrain 的全分辨率数据(无 LOD 优化)。
        /// </summary>
        public void GatherTerrainStats(out int fullVerts, out int fullTris, out int optVerts, out int optTris,
            out int activeVerts, out int activeTris, out int loadedChunks)
        {
            fullVerts = 0; fullTris = 0; optVerts = 0; optTris = 0; activeVerts = 0; activeTris = 0;
            if (unityTerrainMode)
            {
                // Unity Terrain: 全分辨率常驻, 无 LOD 简化, 1 个 DrawCall(引擎合并)
                int res = m_HeightMap != null ? m_HeightMap.VertsX : 0;
                fullVerts = res * res;
                fullTris = (res - 1) * (res - 1) * 2;
                optVerts = fullVerts;
                optTris = fullTris;
                activeVerts = fullVerts;
                activeTris = fullTris;
                loadedChunks = 1;
                return;
            }
            if (m_Streamer == null)
            {
                loadedChunks = 0;
                return;
            }
            foreach (var c in m_Streamer.AllBuiltChunks)
            {
                fullVerts += c.FullVertexCount;
                fullTris += c.FullTriangleCount;
                optVerts += c.OptimizedVertexCount;
                optTris += c.OptimizedTriangleCount;
            }
            foreach (var c in m_Streamer.ActiveChunks)
            {
                activeVerts += c.ActiveVertexCount;
                activeTris += c.ActiveTriangleCount;
            }
            loadedChunks = m_Streamer.LoadedCount;
        }
    }
}
