using System.Collections.Generic;
using TerrainGrassDemo.Terrain;
using UnityEngine;

namespace TerrainGrassDemo.DebugTools
{
    /// <summary>
    /// Gizmos 可视化(需求 1.10): Chunk 边界 + 四叉树叶节点矩形。
    /// 独立组件, 从 DemoWorld 读数据 —— DemoWorld 保持专注, 调试代码全部隔离在 Debug/ 目录。
    ///
    /// 颜色约定:
    ///   白线   = 已生成地形的 Chunk 边界(贴地表高度)
    ///   灰线   = 尚未生成(未加载)的 Chunk 边界
    ///   黄矩形 = 四叉树叶节点(平坦区域), 矩形越大说明合并得越狠
    /// </summary>
    public class DemoGizmos : MonoBehaviour
    {
        public DemoWorld target;

        private static readonly List<TerrainQuadTree.Node> s_LeafCache = new List<TerrainQuadTree.Node>();

        public void Bind(DemoWorld world)
        {
            target = world;
        }

        void OnDrawGizmos()
        {
            if (target == null) return;
            if (!target.settings.showGizmos) return;
            var heightMap = target.HeightMap;
            if (heightMap == null) return;

            int res = heightMap.ChunkGridRes;
            float cell = heightMap.CellSize;
            var streamer = target.Streamer;

            if (streamer != null)
            {
                // 已构建 Chunk: 白线 + 黄色四叉树叶节点
                foreach (var c in streamer.AllBuiltChunks)
                {
                    DrawChunkFrame(heightMap, c.ChunkX, c.ChunkZ, new Color(1f, 1f, 1f, 0.55f));
                    if (c.QuadRoot == null) continue;

                    Gizmos.color = new Color(1f, 0.85f, 0.1f, 0.65f);
                    s_LeafCache.Clear();
                    TerrainQuadTree.CollectLeaves(c.QuadRoot, s_LeafCache);
                    Vector3 o = heightMap.GetChunkOrigin(c.ChunkX, c.ChunkZ);
                    int baseX = c.ChunkX * res, baseZ = c.ChunkZ * res;
                    for (int k = 0; k < s_LeafCache.Count; k++)
                    {
                        var leaf = s_LeafCache[k];
                        // 叶节点矩形的四个角分别采样各自高度(+0.3m 抬高避免 z-fight)。
                        // 踩坑: 不能只采左下角一个高度 —— 叶节点可能是带坡度的平面,
                        // 四个角高度不同, 用单一高度画矩形会一半悬空一半插地。
                        float h00 = heightMap.GetHeightClamped(leaf.x, leaf.z) + 0.3f;
                        float h10 = heightMap.GetHeightClamped(leaf.x + leaf.w, leaf.z) + 0.3f;
                        float h01 = heightMap.GetHeightClamped(leaf.x, leaf.z + leaf.h) + 0.3f;
                        float h11 = heightMap.GetHeightClamped(leaf.x + leaf.w, leaf.z + leaf.h) + 0.3f;
                        Vector3 p0 = new Vector3(o.x + (leaf.x - baseX) * cell, h00, o.z + (leaf.z - baseZ) * cell);
                        Vector3 p1 = new Vector3(o.x + (leaf.x + leaf.w - baseX) * cell, h10, o.z + (leaf.z - baseZ) * cell);
                        Vector3 p2 = new Vector3(o.x + (leaf.x - baseX) * cell, h01, o.z + (leaf.z + leaf.h - baseZ) * cell);
                        Vector3 p3 = new Vector3(o.x + (leaf.x + leaf.w - baseX) * cell, h11, o.z + (leaf.z + leaf.h - baseZ) * cell);
                        Gizmos.DrawLine(p0, p1);
                        Gizmos.DrawLine(p0, p2);
                        Gizmos.DrawLine(p1, p3);
                        Gizmos.DrawLine(p2, p3);
                    }
                }

                // 未加载 Chunk: 灰线
                for (int cz = 0; cz < target.settings.chunksZ; cz++)
                {
                    for (int cx = 0; cx < target.settings.chunksX; cx++)
                    {
                        if (streamer.GetChunk(cx, cz) != null) continue;
                        DrawChunkFrame(heightMap, cx, cz, new Color(0.45f, 0.45f, 0.45f, 0.3f));
                    }
                }
            }
        }

        private static void DrawChunkFrame(HeightMapData heightMap, int cx, int cz, Color color)
        {
            Vector3 o = heightMap.GetChunkOrigin(cx, cz);
            int res = heightMap.ChunkGridRes;
            int baseX = cx * res, baseZ = cz * res;
            Gizmos.color = color;
            Vector3 a = new Vector3(o.x, heightMap.GetHeightClamped(baseX, baseZ), o.z);
            Vector3 b = new Vector3(o.x + heightMap.ChunkWorldSize, heightMap.GetHeightClamped(baseX + res, baseZ), o.z);
            Vector3 d = new Vector3(o.x, heightMap.GetHeightClamped(baseX, baseZ + res), o.z + heightMap.ChunkWorldSize);
            Vector3 e = new Vector3(o.x + heightMap.ChunkWorldSize, heightMap.GetHeightClamped(baseX + res, baseZ + res), o.z + heightMap.ChunkWorldSize);
            Gizmos.DrawLine(a, b);
            Gizmos.DrawLine(a, d);
            Gizmos.DrawLine(b, e);
            Gizmos.DrawLine(d, e);
        }
    }
}
