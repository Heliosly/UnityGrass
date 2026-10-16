using UnityEngine;

namespace TerrainGrassDemo.Terrain
{
    /// <summary>
    /// 单个地形 Chunk 的运行时数据。
    /// 同时持有"全分辨率网格"和"四叉树优化网格"两份 Mesh —— 需求 1.8 的实时切换
    /// 只是换 MeshFilter.sharedMesh 的引用, 不需要重新生成, 所以切换是零成本瞬时的。
    /// </summary>
    public sealed class TerrainChunkData
    {
        public int ChunkX { get; }
        public int ChunkZ { get; }
        public Bounds Bounds { get; }

        public GameObject GameObject { get; set; }
        public MeshFilter Filter { get; set; }
        public MeshRenderer Renderer { get; set; }
        public MeshCollider Collider { get; set; }

        /// <summary>全分辨率网格: (res+1)x(res+1) 顶点, 每格 2 个三角形。</summary>
        public Mesh FullMesh { get; set; }

        /// <summary>四叉树优化网格: 只包含"有用顶点"。</summary>
        public Mesh OptimizedMesh { get; set; }

        /// <summary>四叉树根节点, 保留给 Gizmos 画叶节点矩形。</summary>
        public TerrainQuadTree.Node QuadRoot { get; set; }

        public int FullVertexCount { get; set; }
        public int FullTriangleCount { get; set; }
        public int OptimizedVertexCount { get; set; }
        public int OptimizedTriangleCount { get; set; }

        public bool UseOptimized { get; private set; }

        public int ActiveVertexCount => UseOptimized ? OptimizedVertexCount : FullVertexCount;
        public int ActiveTriangleCount => UseOptimized ? OptimizedTriangleCount : FullTriangleCount;

        public TerrainChunkData(int chunkX, int chunkZ, Bounds bounds)
        {
            ChunkX = chunkX;
            ChunkZ = chunkZ;
            Bounds = bounds;
        }

        /// <summary>
        /// 切换优化/原始网格。只换引用, 不重建。
        /// 碰撞体始终用全分辨率网格: 碰撞关心"脚下的精确形状",
        /// 优化网格的 T 型接缝对渲染无碍(Cull Off + 共享边), 但会让物理行走踩到缝隙, 所以不切。
        /// </summary>
        public void SetOptimized(bool useOptimized)
        {
            UseOptimized = useOptimized;
            if (Filter != null)
            {
                Filter.sharedMesh = useOptimized ? OptimizedMesh : FullMesh;
            }
        }
    }
}
