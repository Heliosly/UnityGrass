using TerrainGrassDemo.Grass;
using TerrainGrassDemo.Interaction;
using TerrainGrassDemo.Streaming;
using UnityEngine;

namespace TerrainGrassDemo.DebugTools
{
    /// <summary>
    /// 运行时调试面板(需求 6)。
    /// 独立成组件的理由: DemoWorld 已经是"组装根", 再塞 300 行 IMGUI 会让它失去焦点;
    /// 面板只读数据、调开关, 全部通过 DemoWorld 的公开 API, 数据流单向清晰。
    /// 用 IMGUI(OnGUI)而不是 uGUI 的原因: 零预制体、零 Canvas 依赖, 纯代码即可显示,
    /// 对"教学 Demo"最友好 —— 需要美化时换成 UI Toolkit 即可, 数据接口不变。
    /// </summary>
    public class RenderingStatsPanel : MonoBehaviour
    {
        public DemoWorld target;

        private bool m_ShowPanel = false;
        private Vector2 m_Scroll;

        public void Bind(DemoWorld world)
        {
            target = world;
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.I)) m_ShowPanel = !m_ShowPanel;
        }

        void OnGUI()
        {
            if (target == null || target.HeightMap == null) return;

            var settings = target.settings;
            var streamer = target.Streamer;
            var grass = target.Grass;
            var interaction = target.Interaction;

            target.GatherTerrainStats(out int fv, out int ft, out int ov, out int ot, out int av, out int at, out int loaded);
            float vertRatio = fv > 0 ? 1f - (float)ov / fv : 0f;
            float triRatio = ft > 0 ? 1f - (float)ot / ft : 0f;

            var center = streamer != null ? streamer.CenterChunk : (0, 0);
            int pendingLoad = streamer != null ? streamer.PendingLoadCount : 0;
            int pendingUnload = streamer != null ? streamer.PendingUnloadCount : 0;
            int grassTotal = grass != null ? grass.TotalInstances : 0;
            int grassDraw = grass != null ? grass.LastDrawCalls : 0;
            int vis0 = grass != null ? grass.VisibleLOD0 : -1;
            int vis1 = grass != null ? grass.VisibleLOD1 : -1;
            int totalDrawCalls = grassDraw + loaded; // 地形每 Chunk 1 个 DrawCall

            GUIStyle toggleStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 12,
                fontStyle = FontStyle.Bold
            };
            toggleStyle.normal.textColor = new Color(0.96f, 0.99f, 1f, 0.88f);
            GUI.Label(new Rect(20, 14, 280, 28), "AETHER MEADOW  /  [I] METRICS", toggleStyle);
            if (!m_ShowPanel) return;

            GUILayout.BeginArea(new Rect(16, 56, 460, 640), GUI.skin.box);
            m_Scroll = GUILayout.BeginScrollView(m_Scroll, GUILayout.Width(445), GUILayout.Height(620));
            GUILayout.Label("大场景地形 + 交互植被渲染 Demo");
            GUILayout.Label("视觉版本: " + DemoWorld.VisualVersion);
            GUILayout.Space(4);

            // ---- 流式加载 ----
            GUILayout.Label(string.Format("中心 Chunk: ({0},{1})    已加载: {2}/{3}",
                center.Item1, center.Item2, loaded, settings.chunksX * settings.chunksZ));
            GUILayout.Label(string.Format("队列: 等待生成 {0}   等待卸载 {1}", pendingLoad, pendingUnload));

            // ---- 地形统计 ----
            GUILayout.Label(string.Format("地形顶点   全网格 {0}   优化 {1}   当前 {2}", fv, ov, av));
            GUILayout.Label(string.Format("地形三角形 全网格 {0}   优化 {1}   当前 {2}", ft, ot, at));
            GUILayout.Label(string.Format("压缩率  顶点 {0:P1}   三角形 {1:P1}", vertRatio, triRatio));

            // ---- 草地统计 ----
            GUILayout.Label(string.Format("草形态: {0}", grass != null ? grass.GrassModeInfo : "无"));
            GUILayout.Label(string.Format("草实例: 总数 {0}   可见 {1}", grassTotal,
                vis0 >= 0 ? (vis0 + Mathf.Max(0, vis1)).ToString() : "采集中..."));
            GUILayout.Label(string.Format("DrawCall: 草 {0} + 地形 {1} = 共 {2}", grassDraw, loaded, totalDrawCalls));
            GUILayout.Label(string.Format("帧耗时 {0:F2} ms   (FPS {1:F0})",
                Time.unscaledDeltaTime * 1000f, 1f / Mathf.Max(0.0001f, Time.unscaledDeltaTime)));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("重合诊断", GUILayout.Width(80)))
            {
                if (grass != null) grass.RequestOverlapDiagnostics();
            }
            GUILayout.Label(grass != null ? grass.DiagResult : "无");
            GUILayout.EndHorizontal();
            if (GUILayout.Button("跑性能矩阵(11阶段×4秒 → PerfMatrix.txt)", GUILayout.Width(280)))
            {
                target.RunPerfMatrix();
            }
            GUILayout.Space(6);

            // ---- 开关 ----
            bool quad = GUILayout.Toggle(settings.enableQuadtree, "四叉树优化 (QuadTree)");
            if (quad != settings.enableQuadtree) target.SetQuadtreeEnabled(quad);
            bool stream = GUILayout.Toggle(settings.enableStreaming, "Chunk Streaming");
            if (stream != settings.enableStreaming) target.SetStreamingEnabled(stream);
            bool gpuCull = GUILayout.Toggle(settings.enableGpuCulling, "GPU Culling");
            if (gpuCull != settings.enableGpuCulling) settings.enableGpuCulling = gpuCull;
            bool lod = GUILayout.Toggle(settings.enableGrassLod, "草地 LOD");
            if (lod != settings.enableGrassLod) { settings.enableGrassLod = lod; }
            if (lod != settings.enableGrassLod) settings.enableGrassLod = lod;
            bool wind = GUILayout.Toggle(settings.enableWind, "风场");
            if (wind != settings.enableWind) target.SetWindEnabled(wind);
            bool inter = GUILayout.Toggle(settings.enableInteraction, "玩家交互");
            if (inter != settings.enableInteraction) target.SetInteractionEnabled(inter);
            settings.showGizmos = GUILayout.Toggle(settings.showGizmos, "Gizmos (Chunk/四叉树)");

            // ---- 四叉树参数(滑条改 settings, 点"重建地形"应用) ----
            GUILayout.Space(4);
            GUILayout.Label("四叉树: 叶大小(格)");
            GUILayout.BeginHorizontal();
            int leafSize = Mathf.RoundToInt(GUILayout.HorizontalSlider(settings.quadtreeMinLeafSize, 1f, 8f, GUILayout.Width(140)));
            if (leafSize != settings.quadtreeMinLeafSize) settings.quadtreeMinLeafSize = leafSize;
            GUILayout.Label(settings.quadtreeMinLeafSize.ToString(), GUILayout.Width(30));
            GUILayout.EndHorizontal();
            GUILayout.Label("四叉树: 平坦判定误差(米)");
            GUILayout.BeginHorizontal();
            float flatErr = GUILayout.HorizontalSlider(settings.flatnessError, 0.01f, 1f, GUILayout.Width(140));
            settings.flatnessError = flatErr;
            GUILayout.Label(settings.flatnessError.ToString("F2"), GUILayout.Width(30));
            GUILayout.EndHorizontal();
            if (GUILayout.Button("重建地形(应用四叉树参数)"))
            {
                target.RebuildTerrain();
            }

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label("草地密度:");
            if (GUILayout.Button("高")) target.SetGrassDensity(GrassDensityLevel.High);
            if (GUILayout.Button("中")) target.SetGrassDensity(GrassDensityLevel.Normal);
            if (GUILayout.Button("低")) target.SetGrassDensity(GrassDensityLevel.Low);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("全密度距离", GUILayout.Width(64));
            settings.grassFullDensityDistance = GUILayout.HorizontalSlider(settings.grassFullDensityDistance, 30f, 120f, GUILayout.Width(120));
            GUILayout.Label(settings.grassFullDensityDistance.ToString("F0") + "m", GUILayout.Width(40));
            GUILayout.EndHorizontal();

            // ---- 玩家 ----
            GUILayout.Space(4);
            if (target.Player != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("移动速度", GUILayout.Width(64));
                target.Player.moveSpeed = GUILayout.HorizontalSlider(target.Player.moveSpeed, 2f, 12f, GUILayout.Width(120));
                GUILayout.Label(target.Player.moveSpeed.ToString("F1") + "m/s", GUILayout.Width(50));
                GUILayout.EndHorizontal();
            }

            // ---- 风场参数 ----
            GUILayout.Space(4);
            GUILayout.Label(string.Format("风向 {0:F0}°   风力 {1:F2}   阵风 {2:F2}   风速 {3:F1}",
                settings.windDirectionDeg, settings.windStrength, settings.gustStrength, settings.windSpeed));
            GUILayout.BeginHorizontal();
            GUILayout.Label("风向", GUILayout.Width(32));
            settings.windDirectionDeg = GUILayout.HorizontalSlider(settings.windDirectionDeg, 0f, 360f, GUILayout.Width(140));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("风力", GUILayout.Width(32));
            settings.windStrength = GUILayout.HorizontalSlider(settings.windStrength, 0f, 1.2f, GUILayout.Width(140));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("阵风", GUILayout.Width(32));
            settings.gustStrength = GUILayout.HorizontalSlider(settings.gustStrength, 0f, 1.5f, GUILayout.Width(140));
            GUILayout.EndHorizontal();

            // ---- 交互纹理预览 ----
            GUILayout.Space(4);
            GUILayout.Label("交互纹理预览 (R/G=倒伏方向, B=强度):");
            if (interaction != null && interaction.ActiveTexture != null)
            {
                GUI.DrawTexture(GUILayoutUtility.GetRect(128, 128), interaction.ActiveTexture, ScaleMode.StretchToFill);
            }

            GUILayout.Space(4);
            GUILayout.Label("操作: WASD 移动(附近草倒伏) | 右键拖拽转视角 | F 自由相机 | I 收起面板");
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
