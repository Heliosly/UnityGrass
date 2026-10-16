using System.IO;
using UnityEditor;
using UnityEngine;

namespace TerrainGrassDemo.EditorTools
{
    /// <summary>
    /// 素材导入设置工具(菜单 "TerrainGrassDemo/06 Setup Art Import Settings")。
    ///
    /// 为什么需要:
    ///   - Heightmap.png 运行时要用 GetPixels 读取 → 必须 isReadable = true;
    ///   - 高度图是"数据"不是"颜色" → 关闭 sRGB 转换(Linear), 否则伽马会扭曲高度值;
    ///   - GrassAtlas / Ground 是美术贴图 → 保持 sRGB, readable 无碍。
    /// 导入任何新 PNG 素材后运行一次即可。
    /// </summary>
    public static class ImportTools
    {
        private const string ArtResources = "Assets/TerrainGrassDemo/Art/Resources";

        [MenuItem("TerrainGrassDemo/06 Setup Art Import Settings")]
        public static void SetupArtImportSettings()
        {
            if (!AssetDatabase.IsValidFolder(ArtResources)) return;
            foreach (string path in Directory.GetFiles(ArtResources, "*.png"))
            {
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;
                importer.isReadable = true;
                importer.textureType = TextureImporterType.Default;
                // 高度图: Linear(数据), 其他贴图: sRGB(颜色)
                importer.sRGBTexture = !Path.GetFileName(path).StartsWith("Heightmap");
                importer.SaveAndReimport();
                Debug.Log("[ImportTools] " + Path.GetFileName(path) + " readable=true, sRGB=" + !importer.sRGBTexture);
            }
            Debug.Log("[ImportTools] 素材导入设置完成。");
        }
    }
}
