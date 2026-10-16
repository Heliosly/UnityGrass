using System.Collections.Generic;
using UnityEngine;

namespace TerrainGrassDemo.Terrain
{
    /// <summary>
    /// Chunk 网格构建器。纯函数式: 输入高度图 + Chunk 坐标, 输出两份 Mesh 和四叉树根。
    ///
    /// 关键点(为什么这样切顶点):
    /// 1. 顶点发射范围是 [baseX, baseX+res] 闭区间 —— 相邻 Chunk 的共享边顶点各发射一次,
    ///    但位置/法线/UV 都来自同一份全局高度图, 完全一致。两个 Mesh 在接缝处顶点重合,
    ///    渲染上无缝; 代价是每列/每行边界顶点被存储两遍(可以接受, 每个 Chunk 独立绘制)。
    /// 2. 优化网格的"有用顶点"判定来自全局 useful 数组(四叉树标记),
    ///    而索引生成时也会看到邻居 Chunk 标记在共享边上的点 → T 型裂缝被三角形拆分消化。
    /// 3. 法线用高度图中心差分的结果, 不用 RecalculateNormals, 保证切换网格时光照一致。
    /// </summary>
    public static class TerrainChunkMeshBuilder
    {
        /// <summary>
        /// 生成 Chunk 的两份网格 + 四叉树。
        /// globalUseful: 全地形共享的"有用顶点"表(由 TerrainQuadTree.BuildGlobalUsefulTable 构建,
        /// 所有 Chunk 的叶角点都在同一张表里) —— 邻居的标记对当前 Chunk 可见,
        /// FillIndexBuffer 沿共享边拆分三角形, 天然无 T 型裂缝, 不需要额外保留任何顶点。
        /// </summary>
        public static void BuildMeshes(HeightMapData hm, int chunkX, int chunkZ, TerrainGrassDemoSettings s,
            bool[] globalUseful, out Mesh fullMesh, out Mesh optimizedMesh, out TerrainQuadTree.Node quadRoot)
        {
            int res = hm.ChunkGridRes;
            int baseX = chunkX * res;
            int baseZ = chunkZ * res;
            Vector3 origin = hm.GetChunkOrigin(chunkX, chunkZ);
            Bounds chunkBounds = hm.GetChunkBounds(chunkX, chunkZ);

            // ---------- 全分辨率网格 ----------
            var verts = new Vector3[(res + 1) * (res + 1)];
            var normals = new Vector3[verts.Length];
            var uv = new Vector2[verts.Length];
            for (int z = 0; z <= res; z++)
            {
                for (int x = 0; x <= res; x++)
                {
                    int gi = hm.Idx(baseX + x, baseZ + z);
                    int li = z * (res + 1) + x;
                    verts[li] = new Vector3(origin.x + x * hm.CellSize, hm.Heights[gi], origin.z + z * hm.CellSize);
                    normals[li] = hm.Normals[gi];
                    uv[li] = new Vector2((float)x / res, (float)z / res);
                }
            }
            // Unity Terrain 的标准绕序: (v00, v10, v11), (v00, v11, v01)
            var tris = new List<int>(res * res * 6);
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    int v00 = z * (res + 1) + x;
                    int v10 = v00 + 1;
                    int v01 = v00 + (res + 1);
                    int v11 = v01 + 1;
                    tris.Add(v00); tris.Add(v10); tris.Add(v11);
                    tris.Add(v00); tris.Add(v11); tris.Add(v01);
                }
            }

            fullMesh = CreateMesh("TerrainChunk(" + chunkZ + "," + chunkX + ")-Full", verts, normals, uv, tris, chunkBounds);

            // ---------- 四叉树优化网格 ----------
            // 用全地形共享表(见方法注释): 邻居 Chunk 在共享边上的叶角点标记可见,
            // 三角形沿边拆分 → 无缝; 且本 Chunk 只发射自己窗口 [baseX, baseX+res] 内的标记。
            quadRoot = TerrainQuadTree.BuildRoot(baseX, baseZ, res, s.quadtreeMinLeafSize, hm, s.flatnessError);
            bool[] useful = globalUseful;

            // 1) 只发射"有用顶点", 并建立 网格坐标 -> 本地索引 映射
            var localMap = new int[res + 1, res + 1];
            for (int x = 0; x <= res; x++)
                for (int z = 0; z <= res; z++)
                    localMap[x, z] = -1;

            var optVerts = new List<Vector3>();
            var optNormals = new List<Vector3>();
            var optUv = new List<Vector2>();
            for (int z = 0; z <= res; z++)
            {
                for (int x = 0; x <= res; x++)
                {
                    int gx = baseX + x, gz = baseZ + z;
                    if (!useful[gz * hm.VertsX + gx]) continue;
                    int gi = hm.Idx(gx, gz);
                    localMap[x, z] = optVerts.Count;
                    optVerts.Add(new Vector3(origin.x + x * hm.CellSize, hm.Heights[gi], origin.z + z * hm.CellSize));
                    optNormals.Add(hm.Normals[gi]);
                    optUv.Add(new Vector2((float)x / res, (float)z / res));
                }
            }

            // 2) 索引生成: 平坦区域 2 个大三角形, 沿标记点拆分(见 BuildTriangles 注释)
            var optTris = new List<int>();
            TerrainQuadTree.BuildTriangles(quadRoot, useful, hm.VertsX, baseX, baseZ, localMap, optTris);

            optimizedMesh = CreateMesh("TerrainChunk(" + chunkZ + "," + chunkX + ")-Optimized",
                optVerts.ToArray(), optNormals.ToArray(), optUv.ToArray(), optTris, chunkBounds);
        }

        private static Mesh CreateMesh(string name, Vector3[] verts, Vector3[] normals, Vector2[] uv, List<int> tris, Bounds bounds)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.triangles = tris.ToArray();
            mesh.bounds = bounds; // 用整个 Chunk 的包围盒而不是顶点包围盒: 草与地形共用, 视锥剔除更保守稳定
            return mesh;
        }

        /// <summary>
        /// 给 Chunk GameObject 挂渲染/碰撞组件(Chunk 池复用时会重复调用, 所以写成幂等)。
        /// </summary>
        public static void EnsureComponents(GameObject go, out MeshFilter filter, out MeshRenderer renderer, out MeshCollider collider)
        {
            filter = go.GetComponent<MeshFilter>();
            if (filter == null) filter = go.AddComponent<MeshFilter>();
            renderer = go.GetComponent<MeshRenderer>();
            if (renderer == null) renderer = go.AddComponent<MeshRenderer>();
            collider = go.GetComponent<MeshCollider>();
            if (collider == null) collider = go.AddComponent<MeshCollider>();
        }

        /// <summary>构建一个完整的 Chunk 数据(含 GameObject), eager 模式和流式加载共用。</summary>
        public static TerrainChunkData BuildChunk(HeightMapData hm, int chunkX, int chunkZ, TerrainGrassDemoSettings s,
            bool[] globalUseful, Material terrainMaterial, Transform parent)
        {
            BuildMeshes(hm, chunkX, chunkZ, s, globalUseful, out Mesh full, out Mesh opt, out TerrainQuadTree.Node root);

            var go = new GameObject("TerrainChunk(" + chunkZ + "," + chunkX + ")");
            go.transform.SetParent(parent, false);
            go.transform.position = Vector3.zero;

            EnsureComponents(go, out MeshFilter filter, out MeshRenderer renderer, out MeshCollider collider);
            renderer.sharedMaterial = terrainMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            collider.sharedMesh = full; // 碰撞永远用全网格, 见 TerrainChunkData.SetOptimized 注释

            var data = new TerrainChunkData(chunkX, chunkZ, hm.GetChunkBounds(chunkX, chunkZ))
            {
                GameObject = go,
                Filter = filter,
                Renderer = renderer,
                Collider = collider,
                FullMesh = full,
                OptimizedMesh = opt,
                QuadRoot = root,
                FullVertexCount = full.vertexCount,
                FullTriangleCount = full.triangles.Length / 3,
                OptimizedVertexCount = opt.vertexCount,
                OptimizedTriangleCount = opt.triangles.Length / 3,
            };
            data.SetOptimized(s.enableQuadtree);
            return data;
        }
    }
}
