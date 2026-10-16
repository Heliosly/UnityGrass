# LearningGuide —— 按渲染链路从底层往上读

> 建议阅读顺序: 先按下面的链路把"数据怎么流"读通, 再回头逐文件精读。
> 每个系统都回答三个问题: **用什么数据结构、为什么这么设计、热路径上发生了什么**。

## 0. 全局架构一句话

一个 `DemoWorld`(组装根) 串起五个子系统。数据流只有一个方向:

```
HeightMapData(一张全局 float[])
   ├─> TerrainChunkStreamer(决定哪些 Chunk 该存在) ──> TerrainChunkMeshBuilder(生成两份 Mesh)
   └─> GrassChunkManager(等级回调) ──> GrassInstanceGenerator(生成实例) ──> ComputeBuffer
                                                          │
                              GrassCulling.compute(裁剪) ─┘ Append 缓冲
                                                          │
                              CopyCount ──> Args 缓冲 ──> DrawMeshInstancedIndirect
                                                          │
                              GrassIndirect.shader(顶点: 风 + 交互; 片元: 光照 + 淡出)
```

---

## 1. 高度数据: `HeightMapData.cs` + `NoiseUtils.cs`

**数据结构**: 整张地图只有**一张一维 `float[]`**(索引 = `z * vertsX + x`)和同尺寸的 `Vector3[]` 法线。没有 per-Chunk 的高度数组。

**为什么是"一张全局大数组"而不是每 Chunk 一份**:
- 相邻 Chunk 的共享边顶点读的是**同一个数组元素** → 高度、法线天然一致, 裂缝和光照断层的根因(两套数据两套结果)从数据结构上被消除;
- Chunk 只是这张图上的窗口 `[chunkX*res, chunkX*res+res]`, 生成 Mesh 时切片即可;
- 草实例贴地、玩家贴地、双线性采样全部走同一个函数, 不存在多套高度来源打架。

**高度函数**(`FillHeights(seed, style)`, 三种风格):
- `terrainStyle=0` **丘陵草原(默认)**: 三层低频噪声叠加(100m 缓丘 ±6m / 20m 小丘 ±1.5m / 4m 微起伏 ±0.3m)。缓坡为主(坡度 1~3%), 配合四叉树**共面判定**(见 §3)大区域可合并, 又没有精确平坦的"人造感"——这是"一片草原"的地形基础;
- `terrainStyle=1` **山丘+平顶高原**: 两个高斯山丘(连续起伏) + 平顶高原(顶部**精确常数**, 四叉树整片塌缩) + 涟漪噪声(只乘在山丘 mask 上)——验收"平坦 vs 起伏"的悬殊对比用;
- `terrainStyle=2` **真实高度图**: 任意灰度 PNG 双线性重采样(工程自带大峡谷真实地图)。

噪声是**整数格点哈希 + 双线性插值**(`NoiseUtils`), 种子可复现——这是 Chunk 卸载后重新生成必须一致的前提(Mathf.PerlinNoise 不可控种子, 不能用)。

**法线**: 高度图**中心差分**求梯度 → `normalize(-dh/dx, 1, -dh/dz)`, 而不是 `Mesh.RecalculateNormals`。为什么: 优化网格的三角形巨大, 面法线平均会让平面上的法线跟随三角形走样; 差分法线只依赖顶点周围 4 个高度采样, 与"这个顶点属于全网格还是优化网格"无关 → 切换网格时光照不跳变。

---

## 2. Chunk Mesh: `TerrainChunkMeshBuilder.cs`

每个 Chunk 生成**两份** Mesh(需求 1.8 的实时切换 = 换 `sharedMesh` 引用, 零重建):

- **全网格**: `(res+1)²` 顶点, 每格 2 三角形, Unity Terrain 的标准绕序;
- **优化网格**: 只发射"有用顶点", 索引由四叉树决定(见下节)。

**顶点发射范围是闭区间 `[baseX, baseX+res]`**: 共享边顶点被两个 Chunk 各发射一次, 但位置/法线/UV 来自同一份全局数组 → 完全一致, 接缝处顶点重合、无缝。代价是边界顶点存两遍, 换来每个 Chunk 完全独立绘制(可单独剔除/卸载)。

**UV**: `(x/res, z/res)`, 每 Chunk 重复 0..1, 配合材质平铺。

**碰撞**: MeshCollider 永远用全网格。优化网格的 T 型接缝对渲染无碍(Cull Off + 共享边), 但物理会把缝隙当洞踩, 所以碰撞不参与优化。

---

## 3. 四叉树简化: `TerrainQuadTreeSimplifier.cs`(核心算法)

**问题**: 平原上的中间顶点是冗余的——一个平面只需要 4 个角点、2 个三角形。地形顶点里"能删的"远比"要留的"多。

**数据结构**: 一棵四叉树, 叶节点 = **一个平坦矩形**(存全局网格坐标 + 宽高), 内部节点只是递归容器。树**不是存储**, 是**决策树**: 它不存顶点, 只回答"哪些顶点有用"。

**三步走**:

1. `BuildRoot`: 递归四等分。到最小叶尺寸后判平坦: **共面判定 `|h0+h3-h1-h2| ≤ 2·flatnessError`**(h0..h3 = 矩形四角高度)。为什么不是"四角最大差": 斜平面四角差很大但**共面**(一个平面, 2 个三角形就能精确表示)——坡度 1~3% 的缓坡草原照常合并, 只有真正弯曲的区域才细分; 这是丘陵草原能被大块压缩的关键。四子全平坦且高度一致 → 合并成一个大叶; 否则尝试**成对合并**(左右/上下两兄弟合并, 参考 CTerrainFast 的 childa/childb/childc 逻辑) → 平原上出现大矩形, 三角形更少。注意 `flatnessError` 必须大于 8bit 高度图的量化台阶(~0.078m), 否则任何格都不"平坦", 树退化为不合并。
2. `MarkUsefulVertices`: 每个叶节点的 4 个角点标记进**全局** `bool[]`。注意是全局数组: 相邻 Chunk 的标记互相可见——这是 T 型裂缝解法的前半。
3. `BuildTriangles`: 对每个叶节点, 两个大三角形覆盖整个矩形, 但**沿每条边检查有没有被标记的中间顶点**, 有就把三角形"折"过去。为什么: 一个大平坦矩形贴着邻居的细碎区域时, 邻居沿共享边标记了顶点, 大矩形若不理会, 两个三角形直接跨过这些点 → 接缝处高度采样密度不同 → **T 型裂缝**(视觉破洞)。沿边拆分后共享边两侧顶点完全重合。

**数值验证方法**: 面板里对比全网格 vs 优化网格的顶点数(平原塌缩成 2 三角形/叶, 山丘保持全密度), Gizmos 黄矩形直观显示合并结果。

---

## 4. Chunk Streaming: `TerrainChunkStreamer.cs` + `ChunkLoadScheduler.cs` + `ChunkStreamingState.cs`

**状态**: 每个 Chunk 一个 `ChunkRuntimeState`(目标等级/当前等级/调度状态/卸载倒计时)。等级四级: `Unloaded / TerrainOnly / TerrainLowGrass / TerrainFullGrass`。

**距离环**: 以相机所在 Chunk 为中心, **Chebyshev 距离**(`max(|dx|,|dz|)`, 正方形环——计算零成本, 视觉上也合理)分三环。

**防抖的两道闸**(需求 2.3/2.4, 都是"状态机延迟"而不是"距离魔法"):
1. **迟滞**: 加载判定用"加载环"半径, 卸载判定用"加载环 + hysteresis"半径。相机在环边界来回踱步时, Chunk 不会反复加载-卸载颠簸;
2. **延迟卸载**: 进入卸载半径后不是立刻回收, 而是启动 `unloadDelaySeconds` 倒计时, 期间相机折返则取消。`TickPendingUnloads` 每帧走倒计时, 到期才提交卸载队列。

**重算节流**(需求 2.6): 加载集合是"集合", 只有相机移动超过阈值或超时(`streamingCheckInterval`)才全量重算 64 个 Chunk 的目标等级, 其余帧只走倒计时和执行队列。

**每帧预算**(需求 2.5): `ChunkLoadScheduler` 把请求攒在 `List` 里, 每帧按**距离升序**排序后各取 N 条执行(加载 2/卸载 1)。为什么: 程序化生成 Chunk 有毫秒级开销, 相机高速移动时一帧可能需要十几个 Chunk, 不设预算会掉帧; 距离最近优先是因为"相机旁边的 Chunk 最需要立刻可见"。Chunk 只有几十个, `List.Sort` 足够; 上万 Chunk 的世界应换二叉堆。

**资源策略**(需求 2.7/2.8): Mesh 和 GameObject **归槽位所有、常驻复用**——卸载只是 `SetActive(false)`, 重新加载直接激活, 不 Destroy/Instantiate; 草地 ComputeBuffer 由草地层通过 `OnChunkLevelChanged` 回调管理。顺序保证: 升级先地形后草, 降级先草后地形(见 `SetLevel`)。

---

## 5. 草实例生成: `GrassInstanceGenerator.cs` + `GrassInstanceData.cs`

**数据结构**: `GrassInstanceData` 是 32 字节的 struct(位置/朝向/高/宽/类型/种子), CPU 生成、GPU 消费。**布局必须与 compute/shader 里的 HLSL struct 逐字段一致**(float3 对齐 16 字节, 恰好排满 32, 无需 padding)。

**三个关键设计**:

1. **每 Chunk 固定种子**(需求 3.4): 种子只由 `(chunkX, chunkZ, worldSeed)` 决定。Chunk 卸载后重载, 草必须和上次一模一样——所以**不能用 `UnityEngine.Random`**(全局状态, 顺序一变结果就变), 用 LCG(`state = state*1664525+1013904223`, 取高 24 位), 每格一个独立种子, 随机序列互相解耦。
2. **密度分档 + 格级形态**: `skip × bladesPerCell` 两维控制(High = 每格 10 丛)。**一格的草统一为一种形态**(格级随机数决定): 16% 的格是大草丛格(只长 1 棵, 因为草墩直径 > 格宽, 多了必然穿模), 84% 的格是细草格(10 棵)。细草位置**跨格抖动 ±1.2 格**——近似泊松盘(随机但均匀), 消除"每格固定 N 簇"的网格纹理; 大草丛格用小抖动(1~3 棵聚成一墩)。
3. **坡度过滤 + 密度噪声**: 法线 y 分量过小的陡坡不长草(斜率来自高度图差分法线, 同样确定); 低频 value noise(斑块 ~8m, 世界坐标采样 → 跨 Chunk 无缝)调制每格密度, 最低 8% 的斑块裸露、其余稀密 0.55~1.4 起伏——"这里密一丛、那里疏几根"的草原斑驳。

**细草避让大草丛**: 细草跨格抖动可能落进大草丛格, 生成时查周围 2 格内的大草丛格, 距草墩中心 <0.5 格则跳过——不会"草插在草墩上"。

**高度分层**: 25% 矮草(贴地层) / 55% 主体 / 20% 高草穗(1.2~1.5m, 风感与倒伏的载体——短草摆幅小, 风肉眼看全靠高草)。

**踩坑记录(分桶渲染的致命 bug)**: 曾让每个桶的 Renderer 在 `Generate` 内部"重新生成全部实例"——每个桶都上传/渲染全量实例(5 桶 = 同一位置被 5 种模型各画一遍), 表现为总量虚高 5 倍、视觉"一个点位多次重合"。修法: 分桶后的列表直接 `SetData`, Renderer 不再自行生成。

一个 32×32 格的 Chunk 在 High 档约 2500 丛(全部形态)。

---

## 6. Compute Shader 裁剪: `GrassCulling.compute` + `GrassChunkRenderer.cs`

**管线**(需求 3.6~3.11):

1. `_Instances`(StructuredBuffer, 32B/实例)常驻 GPU, 生成一次;
2. 每帧 `SetCounterValue(0)` 清空两个 **Append 缓冲**(`ComputeBufferType.Append`), Dispatch 64 线程一组, 每线程处理一根草:
   - **距离剔除 + 近密远疏**: 每棵草由 `seed01` 确定性决定最大显示距离 `maxDist = lerp(全密度距离, LOD1距离, seed01)`(全密度距离默认 60m, 面板可调 30~120m)——全密度距离内 100% 密度(草原不露地面), 越远越稀疏, 到 160m 只剩极少数。这是 3A 草地"身边密、远处疏"的密度方案;
   - **视锥剔除**: 草是一根竖线, 只测草根会误杀"根在屏外、尖在屏内"的草——保守取根/中/尖三点, 任一点在视锥内即保留;
   - **LOD 分类**: 近 → `_VisibleLOD0.Append`, 远 → `_VisibleLOD1.Append`;
   - **直接 Append 完整实例数据**(不是索引!): 间接寻址在 procedural instancing 下不稳(曾出现所有实例读到同一份数据叠在一点), Append 数据 + Shader 用 SV_InstanceID 直接索引, 零间接层必然正确;
3. `ComputeBuffer.CopyCount(append, args, 4)` 把 Append 计数拷进 IndirectArguments 的 InstanceCount 槽(字节偏移 4)。

**为什么 Append + CopyCount 而不是 CPU 读回**: 读回(GPU→CPU)要等 GPU 完成并同步停顿, 每帧做会卡流水线; CopyCount 全程 GPU 端, CPU 完全不碰裁剪结果——这就是"GPU 驱动渲染"的核心。

**淡出无闪烁**: 草在"自己的 maxDist"前 12m 由 Shader alpha 柔和淡出(与 Compute 的剔除公式一致)——之前"无淡出直接剔除"在相机移动时草不断出现/消失, 用户反馈过闪烁, 这是根治方案。

**CPU 粗裁剪**(需求 3.7): 每 Chunk 先做 `GeometryUtility.TestPlanesAABB`(Chunk 包围盒 vs 视锥), 视野外的整个 Chunk 连 Dispatch 都省了。

**对照组**: 关闭"GPU Culling"后走 CPU 路径(全量实例直接绘制, 见 GrassChunkRenderer.Draw), 面板上的可见数/帧率差异就是 GPU 裁剪的价值。

**统计读回**(需求 3 末尾): 渲染路径**从不**读回; 调试面板每 0.1 秒对活跃 Chunk **轮流**发起一次 `AsyncGPUReadback`, 完成后更新可见计数, 不阻塞主线程。

**踩坑(共享状态)**: ComputeShader 是所有 Chunk **共享的同一个对象**——`SetBuffer` 绑定会被后调用的 Chunk 覆盖, 必须每次 Dispatch 前设置。曾把绑定做持久化, 结果后加载的 Chunk 覆盖先加载的绑定, 先加载的草整片消失。

---

## 7. Indirect Draw: `GrassChunkRenderer.Draw` + `GrassIndirect.shader`

**调用**: `Graphics.DrawMeshInstancedIndirect(mesh, 0, material, bounds, argsBuffer, 0, mpb, ShadowCastingMode.Off, ...)`。一个 DrawCall 画 N 个实例, **N 由 GPU 在 Args 缓冲里写好**, CPU 不知道也不需要知道。

**实例寻址(shader 里最关键的一行)**:

```hlsl
GrassInstance g = _VisibleIndices[instanceID];  // instanceID = SV_InstanceID
```

DrawMeshInstancedIndirect 给每个实例一个 0..count-1 的硬件 `SV_InstanceID`; Append 缓冲里存的就是**完整的可见实例数据**(裁剪时直接 Append 数据)。`SV_InstanceID` 直接索引, 零间接层。
**踩坑**: 曾用 `unity_InstanceID` 在 `ConfigureProcedural` 里做重映射——该值在本 procedural 配置下**恒为 0**, 所有实例读到第一份数据、几千根草叠在一点(用户长期看到的"稀疏/几株"真凶)。SV_InstanceID 由硬件保证逐实例递增。

**每桶一个全局 Renderer(全局合并)**: 植被样式按 `type01` 分成 **8 桶**(草×4/白花丛/黄花丛/树/石头), 全部 Chunk 的实例按桶拼接进 8 个全局缓冲(Chunk 级缓存 + 脏标记帧末统一重建), 每帧 8 Dispatch + ≤16 Draw —— CPU 提交与 Chunk 数无关(全局合并是"草地 60fps 瓶颈"的解法: 原 400 renderer × 13 API/帧 吃满主线程)。树/石头由 Chunk 级独立生成(每 Chunk 固定 3 树 2 石), 且 shader 按 `type01≥0.94` 免除风弯与倒伏。

**LOD0/LOD1**: LOD0 = 36 顶点弧形草簇(完整动画+交互采样); LOD1 = **8 顶点十字片**(简化, 不采样交互纹理、阵风用近似)——开 LOD 后远处几何真实降到 1/4.5。踩坑: 曾用同 mesh 做 LOD1(只省 shader 指令), Draw 翻倍却无几何收益 → "开了反而慢"; 也曾让模型模式的 LOD1 变成完整 Grass2 模型(石头 19 顶点反而变 104)。

**草形态(面片草/模型草)**: 默认"程序面片草"——弧形草叶面片(6 片交叉, 中段尖部弯曲) + 程序草叶 alpha 图集(弯曲叶形/叶脉/分层色), 原神式; 面板可切"美术模型草"(Quaternius 立体草丛)。两种形态共用同一条 Indirect 管线, 差异只在 Mesh 和 Shader 关键字 `GRASS_MODEL_SHAPE`。

---

## 8. 风场: `GrassWindController.cs` + `GrassIndirect.shader` 顶点段

**C# 只做两件事**(需求 4.1: 风动全部在顶点 Shader):
1. 每帧把参数打包成**全局 Shader 属性**(`_GrassWindDirection/_GrassWindGust/_GrassWindTime`), 所有草材质共享, 改参数零遍历;
2. 程序生成一张**可平铺**阵风噪声纹理(格点哈希对尺寸取模 → 环面采样, 边界首尾相接, 不然每隔 6400 米一道缝)。

**顶点计算(微风配方, 踩坑记录)**: 曾出现"草疯狂抖动"——真凶是**高频阵风噪声**(纹理世界缩放 0.02 → 每 0.4m 一个噪声格点, 相邻顶点噪声差大)和过大的空间相位系数。微风 = 低频 + 小摆幅 + 同相:

- **弯曲权重 = hFrac²**: 草根(hFrac=0)位移为 0, 草尖最大——"根固定、尖摆动"由幂次直接表达;
- **相位 = 时间 + 世界坐标投影 + 实例随机种子**: 空间项波长 63m(大片草同相缓缓摆, 风浪一波波掠过), 实例项 `seed01*2.5`(相邻草近同相, 只打破完全一致——原 `seed01*40` 让相邻草几乎反相, 各自乱晃);
- **风偏置 + 摆动**: `bend = (windBias + wave + gustWave) · hFrac²`, `windBias` 让草持续偏向风侧缓缓回弹——微风的特征是在风侧回弹, 而不是在中线两侧高频抖;
- **阵风 = 低频噪声纹理采样**(250m 波长缓变气流), 时间项沿风向平移, 速度 0.005/0.004 ≈ 1.25 m/s(风速级) —— 踩坑: 原时间项让 UV 每秒推进 0.8 周期, 折算 200 m/s 扫过草地 = "鬼畜抖动"的真凶。阵风应理解为"缓慢移动的风力包络", 不是另一层快速摆动。

**法线近似修正**(需求 4.6): 顶点被弯走后原法线失真; 精确修正需要访问邻居顶点(顶点 Shader 做不到)。工程近似: 弯曲位移沿高度的**解析导数**(弯曲量 ∝ hFrac², 斜率 ∝ 2·hFrac)决定法线该朝倒伏方向倾斜多少, 用它旋转法线。代价是法线在剧烈摆动时略偏软, 对程序化草片完全可接受。

---

## 9. 角色交互纹理: `GrassInteractionController.cs` + `GrassInteraction.compute` + Shader 采样段

**数据格式**(需求 5.1): 一张 128×128 `ARGBFloat` RT。R/G = 倒伏方向(0.5 居中编码), B = 强度。为什么 ARGBFloat: UNorm 8bit 写入 Compute 会截断, 多帧衰减累积出色带; 128² 的 32bit 浮点代价可忽略。

**双缓冲**: Compute 需要"读上一帧写这一帧", 同一张 RT 既做 SRV 又做 UAV 有读写竞争(未定义行为)。两张 RT 轮流: 读 A 写 B, 下帧交换, 零拷贝。

**每帧一趟 Dispatch**(需求 5.2/5.3/5.8):
1. **衰减(只衰减强度)**: `v.b *= exp(-decayRate·dt)`; 方向 RG 保持最后有效方向, 强度 <0.01 时复位中性 0.5 —— 踩坑: 原 `v *= _Decay` 连 RG 一起衰减, 解码后方向趋向 (-1,-1), 草恢复时朝固定斜向漂移(交互生硬原因之一);
2. **中心重映射**: 用**旧中心**的 UV 公式去读旧纹理、用**新中心**算本像素世界坐标——玩家移动后整个交互场在纹理里平移, 倒伏痕迹留在"世界的那个地方"而不是纹理像素上;
3. **笔刷**: 每个交互体画圆形印记(移动方向 = 倒伏方向, smoothstep 软边), 方向按"新旧强度占比"混合——玩家刚离开后草继续朝原方向倒, 不瞬间转向; **速度 <0.1 m/s 跳过笔刷**(静止不压草, 原实现站着不动也朝固定 +X 压)。

**世界 ↔ UV 映射**(需求 5.4): `uv = (world - center)/extent * 0.5 + 0.5`。**这条公式在 compute 与 Shader 里必须逐字一致**, 否则笔刷画的位置和草采样的位置对不上。extent 是**半宽**(覆盖边长 = 2×extent = 48m); 256×256 分辨率下 0.19m/px, 玩家半径 1.8m ≈ 9.5 像素, 笔刷圆润不块状(原 128px/48 半宽 = 0.75m/px 只有 2.4 像素半径, 块状生硬)。

**Shader 采样**(仅 LOD0): 草片顶点在弯曲前采样交互纹理, R/G 解码方向、B 乘强度、同样乘 hFrac², 与风弯曲**相加**——"局部倒伏与风场弯曲叠加"(需求 5.5)。

**干净运动曲线**(整改结论, 交互自然的关键):
1. **渐进压入**: 强度向目标每帧靠近 18%(不是 max 硬写)——草是被"慢慢压弯"的, 不是瞬移倒伏;
2. **慢弹回**: 衰减 `exp(-0.28·dt)`, 草约 3.5 秒悠悠站直;
3. **倒伏与草高相关**: `heightFactor = saturate((height-0.4)/0.8)`——矮草硬、几乎不被压弯, 高草软、大幅倒伏;
4. **很小的一次过冲**: 恢复期草尖单一低频(0.4Hz)小幅度竖直弹起, 随强度衰减消失 —— 踩坑: 曾叠加 jitter(12rad/s)/sway(6rad/s)/bounce 多层正弦摇动, 草像弹簧玩具, 全部删除后只保留这一项。

**多交互体**(需求 5.10): 控制器持 `List<GrassInteractor>`, Compute 里循环最多 4 个(数组 uniform)。Demo 只有一个(玩家), 加更多只需 `AddInteractor`。

---

## 10. 建议的验证路线

1. 先跑通场景, 开 Gizmos 看黄矩形(§3 的直观结果; 草原地形上大块合并);
2. 自由相机横穿地图, 盯面板"已加载/待生成/待卸载"(§4);
3. 开关 GPU Culling 对比可见数与帧耗时(§6);
4. 观察近密远疏: 走近草地身边密集、远处逐渐稀疏且融进雾里(§6);
5. 拉风滑条看摆动方向(应是大片草同相缓缓摆), 走进草丛看倒伏与恢复(§8/§9);
6. 面板"跑性能矩阵"输出 7 阶段帧耗时报告(PerfMatrix.txt)——性能结论以此为准(Editor 帧数不作数);
7. 最后进 Profiler 看 CPU 主线程是否空闲(地形生成在加载帧、裁剪全在 GPU)。
