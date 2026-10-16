using System.IO;
using System.Text;
using TerrainGrassDemo.Grass;
using UnityEngine;

namespace TerrainGrassDemo.DebugTools
{
    /// <summary>
    /// PlayMode 冒烟测试驱动 —— 必须放在 Runtime 程序集!
    ///
    /// 踩坑记录: 同样的类放在 Editor 程序集(Assembly-CSharp-Editor)时,
    /// EnterPlaymode 的场景加载会报 "The referenced script (Unknown) on this Behaviour is missing",
    /// 组件恢复失败、测试永远不启动。Runtime 程序集的组件可以正常被 PlayMode 场景反序列化。
    ///
    /// 所以这里不能用 UnityEditor API:
    ///   - 退出 PlayMode/进程: Application.Quit(exitCode)(PlayMode 下=退出播放, batchmode 下=退出进程);
    ///   - 写报告: 纯 File IO。
    ///
    /// 生命周期: 由 Editor 工具(PlayModeSmokeTest.Run)写入 Demo 场景后进入 PlayMode 启动;
    /// 非 batchmode 下 Start 时自毁, 不影响用户正常 Play。
    /// 注意必须 public: PlayModeSmokeTest 在 Assembly-CSharp-Editor 程序集,
    /// 跨程序集引用 internal 类型会 CS0122。
    /// </summary>
    public sealed class SmokeTestDriver : MonoBehaviour
    {
        internal const int TotalFrames = 900;
        internal const string ReportPath = "Assets/TerrainGrassDemo/SmokeTestReport.txt";

        private int m_Frame;
        private int m_WaitCounter; // 等待世界构建的帧计数(诊断用)
        private float m_FpsSum;
        private float m_MinFps = float.MaxValue;
        private float m_MinGroundDelta = float.MaxValue; // 玩家底部与地表的最小高度差(负=曾穿透)
        private DemoWorld m_World;
        private DemoPlayerController m_Player;
        private float m_StartTime;
        private bool m_Finished;

        void Start()
        {
            // 用户正常 Play 时如果场景里残留了 Driver(测试中途被杀), 立刻自毁, 不留痕迹。
            if (!Application.isBatchMode)
            {
                Destroy(gameObject);
                return;
            }
            m_StartTime = Time.realtimeSinceStartup;
            Debug.Log("[PlayModeSmokeTest] Driver 启动, 等待世界构建...");
        }

        void Update()
        {
            if (m_Finished) return; // 报告只写一次: 重复 WriteAllText 会把文件反复截断

            m_WaitCounter++;
            if (m_World == null)
            {
                m_World = FindFirstObjectByType<DemoWorld>();
                m_Player = FindFirstObjectByType<DemoPlayerController>();
                if (m_WaitCounter % 120 == 0)
                {
                    Debug.Log("[PlayModeSmokeTest] 等待帧 " + m_WaitCounter + ": 未找到 DemoWorld");
                }
                return;
            }
            if (m_World.HeightMap == null)
            {
                // 世界构建中或 Start 未执行 —— 每 120 帧打一条, 区分"构建慢"与"没启动"
                if (m_WaitCounter % 120 == 0)
                {
                    Debug.Log("[PlayModeSmokeTest] 等待帧 " + m_WaitCounter + ": DemoWorld 存在但 HeightMap 未构建"
                              + " (m_Initialized=" + m_World.IsInitialized + ")");
                }
                return; // 第一帧世界还没构建完
            }

            m_Frame++;
            float dt = Time.deltaTime;
            if (dt > 0.0001f)
            {
                float fps = 1f / dt;
                m_FpsSum += fps;
                if (fps < m_MinFps) m_MinFps = fps;
            }

            // 玩家走折线: 触发流式加载 + 草地交互 + LOD
            if (m_Player != null && m_Frame % 60 < 40)
            {
                float t = m_Frame * 0.25f;
                m_Player.SimulateMove(new Vector3(Mathf.Sin(t), 0f, Mathf.Cos(t)).normalized);
            }

            // 贴地验证(高度图行走): 玩家 y - 地表高度 应恒等于站立偏移(约 0.1 米)。
            // 记录最小值: 若出现负值说明 y 低于地表(结构上不应发生)。
            if (m_Player != null)
            {
                Vector3 pos = m_Player.transform.position;
                float groundY = m_World.HeightMap.SampleHeight(pos.x, pos.z);
                float delta = pos.y - groundY;
                if (delta < m_MinGroundDelta) m_MinGroundDelta = delta;
            }

            // 自动重合诊断(实例位置唯一性): 第 300 帧触发, 报告里汇总结果。
            // 这是"无叠点"的自动化证据 —— 曾因分桶渲染 bug 出现"一个点位多次重合"。
            if (m_Frame == 300 && m_World.Grass != null)
            {
                m_World.Grass.RequestOverlapDiagnostics();
            }

            // 自动高清截图(简历素材 + 画面证据, 不依赖窗口分辨率):
            //   120 帧 = 草原全景; 400 帧 = 近景草叶; 700 帧 = 交互倒伏(玩家一直在走)
            if (m_Frame == 120 || m_Frame == 400 || m_Frame == 700)
            {
                string name = m_Frame == 120 ? "overview" : m_Frame == 400 ? "closeup" : "interaction";
                CaptureScreenshot(name);
            }

            if (m_Frame % 60 == 0 || m_Frame == 1)
            {
                Debug.Log("[PlayModeSmokeTest] frame " + m_Frame + "/" + TotalFrames
                    + " 已加载Chunk=" + m_World.Streamer.LoadedCount
                    + " 草实例=" + (m_World.Grass != null ? m_World.Grass.TotalInstances : 0));
            }

            if (m_Frame >= TotalFrames)
            {
                m_Finished = true;
                WriteReport();
                Finish(0);
            }
            else if (Time.realtimeSinceStartup - m_StartTime > 180f)
            {
                m_Finished = true;
                Debug.LogError("[PlayModeSmokeTest] 超时(180s), 仅完成 " + m_Frame + " 帧");
                Finish(1);
            }
        }

        private static void Finish(int exitCode)
        {
            // PlayMode 下退出播放模式; batchmode 下退出进程。
            // 注意: batchmode 的 null 图形环境下 Application.Quit 可能无法真正结束进程,
            // 外部 runner 检测到报告文件更新后应主动结束 Unity 进程。
            Application.Quit(exitCode);
        }

        /// <summary>
        /// 主相机渲染到 1920×1080 RenderTexture 再编码 PNG —— 与窗口分辨率无关,
        /// batchmode 下也能出图(简历截图/画面证据)。
        /// </summary>
        private static void CaptureScreenshot(string name)
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            try
            {
                const int w = 1920, h = 1080;
                var rt = new RenderTexture(w, h, 24);
                var prev = cam.targetTexture;
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = prev;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                string dir = "Assets/TerrainGrassDemo/Documentation/Screenshots";
                Directory.CreateDirectory(dir);
                string path = dir + "/" + name + ".png";
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.Destroy(rt);
                Object.Destroy(tex);
                Debug.Log("[PlayModeSmokeTest] 截图已保存: " + path);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[PlayModeSmokeTest] 截图失败(" + name + "): " + e.Message);
            }
        }

        private void WriteReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== TerrainGrassDemo PlayMode Smoke Test Report =====");
            sb.AppendLine("时间: " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("帧数: " + m_Frame + "   平均 FPS: " + (m_FpsSum / Mathf.Max(1, m_Frame)).ToString("F1")
                          + "   最低 FPS: " + (m_MinFps < float.MaxValue ? m_MinFps.ToString("F1") : "n/a")
                          + "   (注: batchmode 无窗口/无 vsync, FPS 为逻辑帧速率, 不代表交互环境渲染帧率)");
            sb.AppendLine("玩家贴地: 最小高度差 " + (m_MinGroundDelta < float.MaxValue ? m_MinGroundDelta.ToString("F2") : "n/a")
                          + " 米(恒为站立偏移≈0.1 说明全程贴地; 负值=出现穿透, 需修复)");
            sb.AppendLine();

            if (m_World == null)
            {
                sb.AppendLine("[错误] 未找到 DemoWorld。");
                File.WriteAllText(ReportPath, sb.ToString(), Encoding.UTF8);
                return;
            }

            var settings = m_World.settings;
            m_World.GatherTerrainStats(out int fv, out int ft, out int ov, out int ot, out int av, out int at, out int loaded);

            sb.AppendLine("---- 场景配置 ----");
            sb.AppendLine("地图: " + settings.chunksX + "x" + settings.chunksZ + " Chunk, Chunk 边长 " + settings.chunkWorldSize
                          + "m, 网格分辨率 " + settings.chunkGridRes);
            sb.AppendLine("地形风格: " + (settings.terrainStyle == 0 ? "丘陵草原(程序)" : settings.terrainStyle == 1 ? "山丘高原(程序)" : "真实高度图"));
            sb.AppendLine("平坦误差阈值: " + settings.flatnessError + "m");
            sb.AppendLine("流式: " + (settings.enableStreaming ? "开" : "关")
                          + "  近/中/远环: " + settings.nearRingChunks + "/" + settings.midRingChunks + "/" + settings.farRingChunks);
            sb.AppendLine();

            sb.AppendLine("---- 地形统计(全部已生成 Chunk) ----");
            sb.AppendLine("全网格:     顶点 " + fv + "   三角形 " + ft);
            sb.AppendLine("四叉树优化: 顶点 " + ov + "   三角形 " + ot);
            sb.AppendLine("当前生效:   顶点 " + av + "   三角形 " + at + "   (已加载 " + loaded + " Chunk)");
            if (fv > 0) sb.AppendLine("顶点压缩率: " + (1f - (float)ov / fv).ToString("P1"));
            if (ft > 0) sb.AppendLine("三角形压缩率: " + (1f - (float)ot / ft).ToString("P1"));
            sb.AppendLine();

            GrassChunkManager grass = m_World.Grass;
            sb.AppendLine("---- 草地统计 ----");
            sb.AppendLine("草形态: " + (grass != null ? grass.GrassModeInfo : "n/a")
                          + (settings.useModelGrass ? "(模型草)" : "(面片草)"));
            sb.AppendLine("草实例总数: " + (grass != null ? grass.TotalInstances.ToString() : "n/a"));
            sb.AppendLine("重合诊断: " + (grass != null ? grass.DiagResult : "n/a")
                          + "  (1mm 量化下最大同点=1 说明 GPU 数据层无叠点)");
            sb.AppendLine("可见实例(LOD0/LOD1, AsyncGPUReadback 读回): "
                          + (grass != null && grass.VisibleLOD0 >= 0 ? grass.VisibleLOD0 + " / " + Mathf.Max(0, grass.VisibleLOD1) : "未采集到"));
            sb.AppendLine("草 DrawCall: " + (grass != null ? grass.LastDrawCalls.ToString() : "n/a")
                          + "   地形 DrawCall: " + loaded + "   合计: " + ((grass != null ? grass.LastDrawCalls : 0) + loaded));
            sb.AppendLine("GPU Culling: " + (settings.enableGpuCulling ? "开" : "关")
                          + "   草地 LOD: " + (settings.enableGrassLod ? "开" : "关")
                          + "   密度档: " + settings.grassDensity);
            sb.AppendLine();

            sb.AppendLine("---- 验证提示 ----");
            sb.AppendLine("1. 顶点压缩率反映四叉树效果: 平原塌缩为 2 三角形/叶, 山丘保持全密度;");
            sb.AppendLine("2. 自由相机横穿地图可观察 Chunk 按环加载/延迟卸载;");
            sb.AppendLine("3. 关闭 GPU Culling 后对比可见实例数与 FPS 即为 GPU 裁剪收益;");
            sb.AppendLine("4. 风场/交互/密度档均可通过面板开关实时验证。");
            sb.AppendLine("5. 自动截图: Documentation/Screenshots/{overview,closeup,interaction}.png(1920x1080)。");
            sb.AppendLine("========================================================");

            File.WriteAllText(ReportPath, sb.ToString(), Encoding.UTF8);
            Debug.Log("[PlayModeSmokeTest] 报告已写入: " + ReportPath);
        }
    }
}
