using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TerrainGrassDemo.EditorTools
{
    /// <summary>
    /// Demo 场景生成器(Editor 工具)。
    /// 为什么用菜单而不是手写 .unity 文件:
    ///   - 场景 YAML 里的资源引用靠 GUID, 手写极易出错;
    ///   - 菜单方式可以同时完成"创建空场景 + 挂 DemoWorld + 自动绑定两个 ComputeShader 引用
    ///     + 存盘 + 加入 Build Settings", 一步到位。
    /// DemoWorld 本身是自举的: 运行时它会自动创建相机/平行光/玩家,
    /// 所以场景里只需要一个带 DemoWorld 组件的 GameObject。
    /// </summary>
    public static class DemoSceneBuilder
    {
        public const string SceneDir = "Assets/TerrainGrassDemo/Scenes";
        public const string ScenePath = SceneDir + "/Demo.unity";

        [MenuItem("TerrainGrassDemo/01 Build Demo Scene")]
        public static void BuildDemoScene()
        {
            // 1. 全新空场景
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 2. 唯一的场景对象: DemoWorld(组装根)
            var worldGo = new GameObject("DemoWorld");
            var world = worldGo.AddComponent<DemoWorld>();

            // 3. 绑定 ComputeShader 引用(运行时也有名字查找兜底, 这里显式绑定更稳)
            world.grassCullingCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/TerrainGrassDemo/ComputeShaders/GrassCulling.compute");
            world.grassInteractionCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/TerrainGrassDemo/ComputeShaders/GrassInteraction.compute");

            // 3.5 场景默认配置: 高密度草地(用户要"一片草原"的观感);
            //     其余参数用代码默认(丘陵草原 + 面片草 + 近密远疏)。
            world.settings.grassDensity = GrassDensityLevel.High;

            // 4. 存盘 + 加入 Build Settings
            if (!AssetDatabase.IsValidFolder(SceneDir))
            {
                AssetDatabase.CreateFolder("Assets/TerrainGrassDemo", "Scenes");
            }
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            Debug.Log("[DemoSceneBuilder] 场景已生成: " + ScenePath + " — 打开后按 Play 即可运行。");
            Selection.activeGameObject = worldGo;
        }

        /// <summary>
        /// 移除冒烟测试遗留的 __SmokeTestDriver 对象并保存场景。
        /// (batchmode 清理入口: -executeMethod TerrainGrassDemo.EditorTools.DemoSceneBuilder.CleanupSmokeTestDriver)
        /// </summary>
        [MenuItem("TerrainGrassDemo/06 Cleanup Scene (清理测试残留)")]
        public static void CleanupSmokeTestDriver()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath);
            int removed = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == "__SmokeTestDriver")
                {
                    Object.DestroyImmediate(root);
                    removed++;
                }
            }
            if (removed > 0)
            {
                EditorSceneManager.SaveScene(scene);
                Debug.Log("[DemoSceneBuilder] 已清理 " + removed + " 个冒烟测试残留对象并保存场景。");
            }
            else
            {
                Debug.Log("[DemoSceneBuilder] 场景干净, 无需清理。");
            }
        }
    }
}
