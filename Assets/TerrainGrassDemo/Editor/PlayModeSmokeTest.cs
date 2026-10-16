using System.IO;
using TerrainGrassDemo.DebugTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TerrainGrassDemo.EditorTools
{
    /// <summary>
    /// PlayMode 冒烟测试入口(Editor 程序集, 菜单 "TerrainGrassDemo/02 Run PlayMode Smoke Test")。
    ///
    /// 只负责编排, 测试主体在 Runtime 程序集的 SmokeTestDriver
    /// (Assets/TerrainGrassDemo/Runtime/Debug/SmokeTestDriver.cs)。
    /// Driver 必须在 Runtime 程序集的原因见该文件注释(Editor 程序集组件无法被
    /// PlayMode 场景反序列化, 会报 "referenced script is missing")。
    ///
    /// 流程: 打开/构建 Demo 场景 → 把 Driver 保存进磁盘场景 → EnterPlaymode。
    /// 测试完成后 Driver 自行 Application.Quit; 若 batchmode 下 playModeStateChanged
    /// 事件可用, 这里再补一次 EditorApplication.Exit, 防止进程悬挂。
    /// </summary>
    public static class PlayModeSmokeTest
    {
        [MenuItem("TerrainGrassDemo/02 Run PlayMode Smoke Test")]
        public static void Run()
        {
            // 确保场景存在
            if (EditorSceneManager.GetActiveScene().path != DemoSceneBuilder.ScenePath)
            {
                if (!File.Exists(DemoSceneBuilder.ScenePath))
                {
                    DemoSceneBuilder.BuildDemoScene();
                }
                EditorSceneManager.OpenScene(DemoSceneBuilder.ScenePath);
            }

            // 清理旧 Driver, 放新 Driver, 保存进磁盘场景(未保存的场景对象在
            // EnterPlaymode 的 backup/restore 中有竞态, 保存到磁盘最稳)
            var scene = EditorSceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == "__SmokeTestDriver") Object.DestroyImmediate(root);
            }
            var driverGo = new GameObject("__SmokeTestDriver");
            driverGo.AddComponent<SmokeTestDriver>();
            EditorSceneManager.SaveScene(scene);

            // batchmode 下播放结束后尽力退出编辑器进程(若事件可用)
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            EditorApplication.EnterPlaymode();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredEditMode && Application.isBatchMode)
            {
                Debug.Log("[PlayModeSmokeTest] PlayMode 已退出, 结束 batchmode 进程。");
                EditorApplication.Exit(0);
            }
        }
    }
}
