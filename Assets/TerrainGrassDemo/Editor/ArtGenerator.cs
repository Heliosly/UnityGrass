using System.IO;
using UnityEditor;
using UnityEngine;

namespace TerrainGrassDemo.EditorTools
{
    /// <summary>
    /// 素材生成器(菜单 "TerrainGrassDemo/03 Generate Sample Art")。
    /// 在 Assets/TerrainGrassDemo/Art/Resources/ 生成两张可替换的 PNG:
    ///   GrassAtlas.png : 4 根草叶横向并排的 RGBA 图集(透明背景)
    ///   Ground.png     : 可平铺地面贴图
    /// 运行时 DemoWorld 会自动加载它们; 想用自己的素材, 直接替换这两个文件即可:
    ///   - 草叶图集: RGBA、透明背景、N 根草叶横向等宽并排(N 改 DemoWorld.grassAtlasColumns)
    ///   - 地面贴图: 任意可平铺贴图(平铺由 Shader 的 _Tiling 控制)
    /// </summary>
    public static class ArtGenerator
    {
        private const string ArtDir = "Assets/TerrainGrassDemo/Art/Resources";

        [MenuItem("TerrainGrassDemo/03 Generate Sample Art")]
        public static void Generate()
        {
            if (!AssetDatabase.IsValidFolder("Assets/TerrainGrassDemo/Art"))
            {
                AssetDatabase.CreateFolder("Assets/TerrainGrassDemo", "Art");
            }
            if (!AssetDatabase.IsValidFolder(ArtDir))
            {
                AssetDatabase.CreateFolder("Assets/TerrainGrassDemo/Art", "Resources");
            }

            var atlas = ProceduralTextures.CreateGrassAtlas(512, 256, 4, 12345u);
            // 生成后自检: 采样草叶中心线像素, alpha 必须 > 0
            Color center = atlas.GetPixel(64, 128);
            Color edge = atlas.GetPixel(20, 128);
            Debug.Log("[ArtGenerator] 草叶图集自检: 中心(64,128) alpha=" + center.a + ", 边缘(20,128) alpha=" + edge.a);

            WritePng(atlas, ArtDir + "/GrassAtlas.png");
            WritePng(ProceduralTextures.CreateGroundTexture(256, 54321u), ArtDir + "/Ground.png");
            AssetDatabase.Refresh();

            Debug.Log("[ArtGenerator] 素材已生成到 " + ArtDir
                      + "\n想用自己的素材, 直接替换这两个 PNG 文件即可(规格见该文件注释/README)。");
        }

        private static void WritePng(Texture2D tex, string path)
        {
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }
    }
}
