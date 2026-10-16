using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TerrainGrassDemo.EditorTools
{
    /// <summary>
    /// 一键启动(菜单 "TerrainGrassDemo/04 Quick Play", 或命令行:
    ///   Unity.exe -projectPath <工程> -executeMethod TerrainGrassDemo.EditorTools.QuickLaunch.AutoPlay
    /// 工程根目录的 启动Demo.bat 就是干这个的: 双击 → 打开工程 → 自动进入 PlayMode)。
    /// </summary>
    public static class QuickLaunch
    {
        [MenuItem("TerrainGrassDemo/04 Quick Play")]
        public static void AutoPlay()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.Log("[QuickLaunch] 已经在播放中。");
                return;
            }

            string scenePath = DemoSceneBuilder.ScenePath;
            if (!File.Exists(scenePath))
            {
                DemoSceneBuilder.BuildDemoScene();
            }
            if (EditorSceneManager.GetActiveScene().path != scenePath)
            {
                EditorSceneManager.OpenScene(scenePath);
            }

            EditorApplication.EnterPlaymode();
            Debug.Log("[QuickLaunch] 已进入 PlayMode。操作: WASD 移动 / 右键转视角 / F 自由相机 / I 面板。");
        }
    }
}
