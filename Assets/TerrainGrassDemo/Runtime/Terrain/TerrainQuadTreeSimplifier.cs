using System.Collections.Generic;
using UnityEngine;

namespace TerrainGrassDemo.Terrain
{
    /// <summary>
    /// 地形四叉树简化器 —— 本 Demo 的核心算法之一(参考 QnMobile CTerrainFast.CreateLODNode)。
    ///
    /// 一句话原理:
    ///   地形顶点里, 大片平坦区域的"中间顶点"是冗余的 —— 一个平面只需要 4 个角点 + 2 个三角形。
    ///   四叉树递归地把 Chunk 四等分, 如果一个区域 4 个角的高度差小于误差阈值, 就判定为"平坦",
    ///   不再细分; 只有起伏区域继续分下去。最后只把每个平坦叶节点的 4 个角点标记为"有用顶点",
    ///   网格就只由这些顶点构成。
    ///
    /// 与参考实现的差异(有意为之):
    ///   参考实现用 h0==h1==h2==h3 精确判等, 本 Demo 改成"最大-最小 <= flatnessError",
    ///   因为程序生成的地形在平原上噪声 mask 为 0 时是精确平坦的, 但现实高度图几乎不会精确相等,
    ///   可配置阈值才是工程上可用的判据(需求 1: "平坦判断应使用可配置误差阈值")。
    /// </summary>
    public sealed class TerrainQuadTree
    {
        /// <summary>
        /// 树节点。叶节点是"一个平坦矩形区域", 内部节点只是递归容器。
        /// 坐标用全局网格坐标(不是 Chunk 局部), 这样跨 Chunk 共享边上的标记能互相看见(T 型裂缝解法)。
        /// </summary>
        public sealed class Node
        {
            public int x, z;                 // 区域左下角(全局网格坐标)
            public int w, h;                 // 区域尺寸(网格数)
            public bool isLeaf;              // 叶节点
            public bool flat;                // 叶节点是否平坦(四角极差 <= 阈值)。
                                             // 关键: 合并条件要求"子节点自身平坦", 不是只看大区域四角 ——
                                             // 否则"四角低平、中间有个鼓包"的大区域会被误合并, 鼓包被拍平(视觉错误)。
            public Node[] children;          // 内部节点最多 4 个子节点
        }

        /// <summary>
        /// 递归构建一棵 Chunk 的 LOD 树。
        /// gx,gz: 区域左下角全局网格坐标; size: 区域边长; minLeafSize: 停止细分的最小边长。
        ///
        /// 合并策略(参考实现):
        /// 1. 四个子区域都是平坦叶节点, 且代表高度一致 → 合并成一个更大的叶节点;
        /// 2. 否则尝试"成对合并": 左边两兄弟合并(横向), 或下边两兄弟合并(纵向),
        ///    产生 {大矩形, 另两个兄弟} 的三分结构 —— 平原上会出现大矩形, 三角形更少;
        /// 3. 都不行 → 保留 4 个子节点。
        /// </summary>
        public static Node BuildRoot(int gx, int gz, int size, int minLeafSize, HeightMapData hm, float flatnessError)
        {
            var n = new Node { x = gx, z = gz, w = size, h = size };

            if (size <= minLeafSize)
            {
                n.isLeaf = true;
                // 叶子也要判平坦(参考实现同款): 合并时必须"子节点自身平坦",
                // 否则内部有起伏的最小叶子也会被上层合并掉
                n.flat = IsFlatRegion(gx, gz, size, size, hm, flatnessError);
                return n;
            }

            int s1 = size / 2;
            int s2 = size - s1;

            Node c0 = BuildRoot(gx, gz, s1, minLeafSize, hm, flatnessError);
            Node c1 = BuildRoot(gx + s1, gz, s2, minLeafSize, hm, flatnessError);
            Node c2 = BuildRoot(gx, gz + s1, s1, minLeafSize, hm, flatnessError);
            Node c3 = BuildRoot(gx + s1, gz + s1, s2, minLeafSize, hm, flatnessError);

            // 策略 1: 四子全平坦且整体共面 → 合并
            if (c0.flat && c1.flat && c2.flat && c3.flat
                && IsFlatRegion(gx, gz, size, size, hm, flatnessError))
            {
                n.isLeaf = true;
                n.flat = true;
                return n;
            }

            // 策略 2: 成对合并(参考实现的 childa/childb/childc 逻辑)
            // 合并条件 = 两个兄弟各自平坦 + 合并后的矩形整体共面
            Node ca = null, cb = null, cc = null;
            if (c0.flat && c1.flat && IsFlatRegion(gx, gz, s1 + s2, s1, hm, flatnessError))
            {
                // 左半横向合并: {c0+c1, c2, c3}
                ca = c0; cb = c2; cc = c3;
                ca.w += c1.w;
                ca.flat = true;
            }
            else if (c0.flat && c2.flat && IsFlatRegion(gx, gz, s1, s1 + s2, hm, flatnessError))
            {
                // 下边纵向合并: {c0+c2, c1, c3}
                ca = c0; cb = c1; cc = c3;
                ca.h += c2.h;
                ca.flat = true;
            }
            if (ca == null && c3.flat && c1.flat && IsFlatRegion(gx + s1, gz, s2, s1 + s2, hm, flatnessError))
            {
                // 右上横向合并: {c1+c3, c0, c2}
                ca = c1; cb = c0; cc = c2;
                ca.h += c3.h;
                ca.flat = true;
            }
            if (ca == null && c3.flat && c2.flat && IsFlatRegion(gx, gz + s1, s1 + s2, s2, hm, flatnessError))
            {
                // 右边纵向合并: {c2+c3, c0, c1}
                ca = c2; cb = c0; cc = c1;
                ca.w += c3.w;
                ca.flat = true;
            }

            n.isLeaf = false;
            n.children = ca == null ? new[] { c0, c1, c2, c3 } : new[] { ca, cb, cc };
            return n;
        }

        /// <summary>
        /// 平坦(共面)判定: 矩形四角共面 ⟺ 对角和相等, 即 |h0 + h3 - h1 - h2| <= 2*阈值。
        ///
        /// 为什么是"共面"而不是"四角等高"(踩坑记录):
        /// 四角极差判定把"带坡度的平面"也判为不平坦 —— 真实地形(大峡谷高原)处处有 1~2%
        /// 连续坡度, 四角极差 = 坡度 × 尺寸, 尺寸越大越"不平坦", 0.3m 阈值下 8x8 区域就超限,
        /// 结果整片平原都合并不出大块(Gizmos 上小矩形密集)。
        /// 共面判定容忍任意方向的线性坡度, 只拒绝"弯曲"(峡谷壁/山脊) —— 这正是四叉树
        /// 该合并的对象: 平面(哪怕斜的)用 2 个三角形就能精确表示, 弯曲才需要细分。
        ///
        /// 正确性保证: 内部起伏仍由"子节点 flat"挡住 —— 四角共面只代表四个角在同一平面,
        /// 中间若藏鼓包, 子节点判定会拒绝合并(鼓包叶 flat=false)。
        /// </summary>
        private static bool IsFlatRegion(int gx, int gz, int w, int h, HeightMapData hm, float flatnessError)
        {
            float h0 = hm.Heights[hm.Idx(gx, gz)];
            float h1 = hm.Heights[hm.Idx(gx + w, gz)];
            float h2 = hm.Heights[hm.Idx(gx, gz + h)];
            float h3 = hm.Heights[hm.Idx(gx + w, gz + h)];
            // 四角共面 ⟺ 两条对角线的中点高度一致(对角和相等)
            // 容差 2*阈值: 允许对角中点偏差 ≤ 阈值(相当于把阈值语义从"单角极差"迁移到"共面残差")
            return Mathf.Abs(h0 + h3 - h1 - h2) <= 2f * flatnessError;
        }

        /// <summary>
        /// 把每个叶节点的 4 个角点标记进"有用顶点"表。
        /// 本函数只做标记, 不关心表属于谁 —— 由调用方决定是 Chunk 局部表还是全地形表。
        /// </summary>
        public static void MarkUsefulVertices(Node node, bool[] useful, int vertsX)
        {
            if (node.isLeaf)
            {
                useful[node.z * vertsX + node.x] = true;
                useful[node.z * vertsX + node.x + node.w] = true;
                useful[(node.z + node.h) * vertsX + node.x] = true;
                useful[(node.z + node.h) * vertsX + node.x + node.w] = true;
                return;
            }
            for (int i = 0; i < node.children.Length; i++)
            {
                MarkUsefulVertices(node.children[i], useful, vertsX);
            }
        }

        /// <summary>
        /// 构建全地形共享的"有用顶点"表(参考实现 m_IsVertexUseful 的做法)。
        ///
        /// 为什么必须共享一张表而不是每 Chunk 一张(踩坑记录):
        /// 相邻 Chunk 的树在共享边上各有自己的叶角点标记。若表不共享, 大叶三角形看不到
        /// 邻居的中间顶点, 会直接跨过去 → T 型裂缝(看穿地表)。
        /// 此前用"边界顶点强制保留"修补, 但那让 Chunk 边界 4×33 个顶点全部保留,
        /// 四叉树在边界上完全失效, 违背"平坦区域只留四角"的算法初衷 —— 用户指出的问题。
        /// 正确做法: 高度图常驻, 全地形一次构建这张表(所有 Chunk 的叶角点都标记进来),
        /// 每个 Chunk 构建 Mesh 时按自己的窗口切片, FillIndexBuffer 也查同一张表,
        /// 邻居的标记天然可见 → 三角形沿共享边被拆分, 严丝合缝, 且不多保留任何顶点。
        ///
        /// 与流式加载兼容: 表只依赖高度图(常驻), 不依赖 Chunk 加载状态。
        /// 构建成本: 每个 Chunk 一次 BuildRoot(数百次递归), 64 Chunk 毫秒级, 一次构建常驻。
        /// </summary>
        public static bool[] BuildGlobalUsefulTable(HeightMapData hm, TerrainGrassDemoSettings s)
        {
            var useful = new bool[hm.VertsX * hm.VertsZ];
            int res = hm.ChunkGridRes;
            for (int cz = 0; cz < s.chunksZ; cz++)
            {
                for (int cx = 0; cx < s.chunksX; cx++)
                {
                    var root = BuildRoot(cx * res, cz * res, res, s.quadtreeMinLeafSize, hm, s.flatnessError);
                    MarkUsefulVertices(root, useful, hm.VertsX);
                }
            }
            return useful;
        }

        /// <summary>
        /// 三角形索引生成(参考实现 FillIndexBuffer)。
        ///
        /// 为什么"平坦区域 2 个三角形"不够, 要沿边拆分:
        /// 一个大平坦矩形可能贴着邻居的细碎区域。邻居沿共享边标记了中间顶点,
        /// 如果大矩形不理会, 两个三角形直接跨过这些点, 就会在接缝处形成 T 型裂缝
        /// (两边高度采样密度不同 → 视觉上出现洞)。
        /// 所以每走到一条边, 都检查这条边上有没有被标记的顶点, 有就把三角形"折"过去 ——
        /// 两个大三角形被拆成沿边的三角扇, 保证共享边两侧的顶点完全重合。
        ///
        /// vertexToLocal: 当前 Chunk 内 (localX, localZ) → 本地顶点索引(未标记的槽是 -1)。
        /// 本 Chunk 内的叶节点标记互相可见; Chunk 边界的一致性由
        /// TerrainChunkMeshBuilder 的"边界顶点强制保留"保证(边界顶点全部标记)。
        /// </summary>
        public static void BuildTriangles(Node root, bool[] useful, int vertsX, int baseX, int baseZ, int[,] vertexToLocal, List<int> tris)
        {
            if (!root.isLeaf)
            {
                for (int i = 0; i < root.children.Length; i++)
                {
                    BuildTriangles(root.children[i], useful, vertsX, baseX, baseZ, vertexToLocal, tris);
                }
                return;
            }

            int startX = root.x, endX = root.x + root.w;
            int startZ = root.z, endZ = root.z + root.h;
            int i0, i1, i2, i3;

            // ---- 三角形 1: 从左下角出发, 沿下边向右扫, 再沿右边向上扫 ----
            i0 = vertexToLocal[startX - baseX, startZ - baseZ];
            i3 = vertexToLocal[endX - baseX, endZ - baseZ];
            for (int x = startX + 1; x < endX; x++)
            {
                if (useful[startZ * vertsX + x])
                {
                    i1 = vertexToLocal[x - baseX, startZ - baseZ];
                    tris.Add(i1); tris.Add(i0); tris.Add(i3);
                    i0 = i1;
                }
            }
            for (int z = endZ - 1; z > startZ; z--)
            {
                if (useful[z * vertsX + endX])
                {
                    i1 = vertexToLocal[endX - baseX, z - baseZ];
                    tris.Add(i1); tris.Add(i0); tris.Add(i3);
                    i3 = i1;
                }
            }
            i1 = vertexToLocal[endX - baseX, startZ - baseZ];
            tris.Add(i1); tris.Add(i0); tris.Add(i3);

            // ---- 三角形 2: 从右上角出发, 沿上边向左扫, 再沿左边向下扫 ----
            i0 = vertexToLocal[startX - baseX, startZ - baseZ];
            i3 = vertexToLocal[endX - baseX, endZ - baseZ];
            for (int x = endX - 1; x > startX; x--)
            {
                if (useful[endZ * vertsX + x])
                {
                    i2 = vertexToLocal[x - baseX, endZ - baseZ];
                    tris.Add(i2); tris.Add(i3); tris.Add(i0);
                    i3 = i2;
                }
            }
            for (int z = startZ + 1; z < endZ; z++)
            {
                if (useful[z * vertsX + startX])
                {
                    i2 = vertexToLocal[startX - baseX, z - baseZ];
                    tris.Add(i2); tris.Add(i3); tris.Add(i0);
                    i0 = i2;
                }
            }
            i2 = vertexToLocal[startX - baseX, endZ - baseZ];
            tris.Add(i2); tris.Add(i3); tris.Add(i0);
        }

        /// <summary>收集一棵树的所有叶节点(给 Gizmos 画矩形用)。</summary>
        public static void CollectLeaves(Node node, List<Node> leaves)
        {
            if (node.isLeaf)
            {
                leaves.Add(node);
                return;
            }
            for (int i = 0; i < node.children.Length; i++)
            {
                CollectLeaves(node.children[i], leaves);
            }
        }
    }
}
