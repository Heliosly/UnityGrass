using System.IO;
using UnityEditor;
using UnityEngine;

namespace TerrainGrassDemo.EditorTools
{
    /// <summary>
    /// EditMode 截图工具(菜单 "TerrainGrassDemo/05 Capture Screenshots",
    /// 或 batchmode: -executeMethod TerrainGrassDemo.EditorTools.ScreenshotCapture.Capture)。
    ///
    /// 为什么不用 PlayMode: batchmode 下 EnterPlaymode 的场景机制不可靠
    /// (backup 场景/missing script/帧循环卡死)。这里在 EditMode 手动构建世界
    /// (DemoWorld.BuildWorld + DriveFrame 手动驱动 N 帧), 再用 Camera.Render
    /// 渲染到 RenderTexture 出图 —— 渲染管线(流式/裁剪/风/交互/草绘制)与
    /// PlayMode 完全一致, 且全程可控。
    /// </summary>
    public static class ScreenshotCapture
    {
        [MenuItem("TerrainGrassDemo/05 Capture Screenshots")]
        public static void Capture()
        {
            const string outDir = "Assets/TerrainGrassDemo/Documentation/Screenshots";
            Directory.CreateDirectory(outDir);
            Debug.Log("[ScreenshotCapture] 开始: 构建世界 + 手动驱动...");

            var worldGo = new GameObject("DemoWorld");
            var world = worldGo.AddComponent<DemoWorld>();
            world.BuildWorld();

            // 1. 驱动 160 帧: 流式加载近环 + 草生成 + 风/交互初始化
            for (int i = 0; i < 160; i++)
            {
                world.DriveFrame(0.016f);
            }
            Debug.Log("[ScreenshotCapture] 世界就绪: 草实例=" + world.Grass.TotalInstances);
            world.GatherTerrainStats(out int fv, out int ft, out int ov, out int ot, out int av, out int at, out int loaded);
            Debug.Log($"[ScreenshotCapture] 地形: 全网格 顶点{fv}/三角{ft}  优化 顶点{ov}/三角{ot}  压缩率 {1f - (float)ov / Mathf.Max(1, fv):P1}  已加载{loaded}");

            var cam = Camera.main;
            if (cam == null)
            {
                Debug.LogError("[ScreenshotCapture] 没有相机");
                Object.DestroyImmediate(worldGo);
                return;
            }
            // 诊断: 天空盒/clearFlags 状态(batchmode 下 Shader.Find 可能找不到未加载 shader)
            Debug.Log("[ScreenshotCapture] skybox=" + (RenderSettings.skybox != null ? RenderSettings.skybox.name : "null")
                      + " clearFlags=" + cam.clearFlags
                      + " shaderFind=" + (Shader.Find("TerrainGrassDemo/SkyGradient") != null));

            // 截图机位统一顺光: 方向光从正南来(相机看南 = 顺光, 草地不背光发暗)
            var sun = Object.FindFirstObjectByType<Light>();
            if (sun != null)
            {
                sun.transform.rotation = Quaternion.Euler(50f, 0f, 0f);
            }
            if (RenderSettings.skybox != null)
            {
                RenderSettings.skybox.SetVector("_SunDirection", new Vector4(0f, 0.6f, -0.6f, 1f));
            }

            // 2. 全景: 玩家北侧高处远眺南方(顺光: 光从南来) —— 天空 + 地平线 + 草原
            if (world.Player != null)
            {
                Vector3 p = world.Player.transform.position;
                cam.transform.position = p + new Vector3(0f, 22f, 28f);
                cam.transform.rotation = Quaternion.Euler(14f, 0f, 0f);
                for (int i = 0; i < 10; i++) world.DriveFrame(0.016f);
            }
            RenderToFile(cam, outDir + "/overview.png", 1920, 1080);
            Debug.Log("[ScreenshotCapture] overview.png 已保存");

            // 3. 近景: 玩家北侧低机位看南(顺光), 草叶细节
            if (world.Player != null)
            {
                Vector3 p = world.Player.transform.position;
                cam.transform.position = p + new Vector3(0.8f, 1.2f, 2.2f);
                cam.transform.rotation = Quaternion.Euler(18f, 0f, 0f);
                for (int i = 0; i < 10; i++) world.DriveFrame(0.016f);
                RenderToFile(cam, outDir + "/closeup.png", 1920, 1080);
                Debug.Log("[ScreenshotCapture] closeup.png 已保存");

                // 4. 交互: 玩家朝北偏东走(顺光侧), 草倒伏, 跟随相机
                world.Player.SimulateMove(new Vector3(0.5f, 0f, 1f).normalized);
                for (int i = 0; i < 60; i++) world.DriveFrame(0.016f);
                world.Player.SimulateMove(new Vector3(0.2f, 0f, 1f).normalized);
                for (int i = 0; i < 60; i++) world.DriveFrame(0.016f);
                RenderToFile(cam, outDir + "/interaction.png", 1920, 1080);
                Debug.Log("[ScreenshotCapture] interaction.png 已保存");
            }

            Object.DestroyImmediate(worldGo);
            Debug.Log("[ScreenshotCapture] 完成: " + outDir);
            EditorApplication.Exit(0);
        }

        /// <summary>主相机渲染到指定分辨率的 RenderTexture, 编码 PNG(与窗口分辨率无关)。</summary>
        private static void RenderToFile(Camera cam, string path, int w, int h)
        {
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
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
        }
    }
}
