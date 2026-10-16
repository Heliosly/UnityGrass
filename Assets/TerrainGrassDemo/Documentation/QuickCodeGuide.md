# Aether Meadow 代码速读

目标：用 15 分钟理解项目，不要求逐行阅读。

## 1. 先记住这一条链路

```text
DemoWorld
  → 生成全局高度场
  → 根据相机加载附近 Chunk
  → CPU 只生成一次草实例
  → ComputeShader 每帧筛出可见草并划分 LOD
  → GPU 间接绘制
  → 顶点 Shader 添加风和倒伏
```

核心思想：**CPU 管理世界，GPU 决定本帧画哪些草。**

## 2. 推荐阅读顺序

只读下面 6 个文件即可掌握主干：

1. `Runtime/DemoWorld.cs`：系统入口和每帧调用顺序。
2. `Runtime/TerrainGrassDemoSettings.cs`：所有可调参数。
3. `Runtime/Grass/GrassInstanceGenerator.cs`：草从哪里生成。
4. `Runtime/Grass/GrassChunkManager.cs`：Chunk 草数据如何合并。
5. `Runtime/Grass/GrassChunkRenderer.cs`：GPU 裁剪和间接绘制。
6. `Shaders/GrassIndirect.shader`：草如何变形、受光和着色。

看完再按兴趣阅读 Streaming、Terrain、Interaction。

## 3. 程序入口：DemoWorld

`DemoWorld.BuildWorld()` 只负责组装：

```text
HeightMapData             全局高度
TerrainQuadTree           地形简化
TerrainChunkStreamer      加载范围
GrassChunkManager         草地
GrassWindController       风
GrassInteractionController 倒伏纹理
MeadowDecorRenderer       花、石头、树和遗迹
```

每帧调用集中在 `DriveFrame()`：

```csharp
玩家移动和相机更新
→ Streamer.Update()
→ Wind.Tick()
→ Interaction.Tick()
→ Grass.LateTick()
→ Decor.Render()
```

排查“整个场景没运行”时，先看 `BuildWorld()`；排查“运行一会儿才出问题”时，先看 `DriveFrame()`。

## 4. 地形与流式加载

### HeightMapData

整张地图共用一份 `float[] Heights` 和 `Vector3[] Normals`。

这样做的意义：

- 相邻 Chunk 读取同一份边界高度，不会开裂。
- 地形、草和玩家使用同一个高度采样函数。
- Chunk 卸载后可以确定性重建。

### TerrainChunkStreamer

相机附近分为四个等级：

```text
Unloaded
TerrainOnly
TerrainLowGrass
TerrainFullGrass
```

它还包含加载预算、卸载延迟和迟滞范围，避免相机在边界移动时 Chunk 反复加载。

### TerrainQuadTree

平缓区域使用少量大三角形，起伏区域保留细网格。碰撞仍使用完整网格，因此视觉优化不会影响玩家贴地。

## 5. 草实例如何产生

`GrassInstanceGenerator.Generate()` 遍历草格，为每根草生成：

```csharp
position  // 世界坐标和地面高度
yaw       // 朝向
height    // 高度
width     // 宽度
type01    // 草型/颜色参数
seed01    // 风相位和距离随机数
```

单实例严格为 32 字节，与 ComputeShader 和 Shader 的 `GrassInstance` 完全一致。

生成器的重要规则：

- 每个 Chunk 使用固定种子，重新加载后结果不变。
- 世界空间低频噪声控制疏密、高低和颜色区域。
- 道路中心不长草，边缘允许少量草侵入。
- High、Normal、Low 改变每格实例数，不改变采样网格边界。

## 6. GPU 草地管线

`GrassChunkManager` 先缓存各 Chunk 的实例，再合并为两个全局草型桶。DrawCall 因此不随 Chunk 数增加。

每个桶由 `GrassChunkRenderer.Draw()` 执行：

```text
_Instances：全部实例，生成后常驻 GPU
      ↓
GrassCulling.compute：距离、视锥、密度、LOD 判断
      ↓
_VisibleLOD0 / _VisibleLOD1：Append 缓冲
      ↓
GraphicsBuffer.CopyCount：把可见数量写入绘制参数
      ↓
Graphics.RenderMeshIndirect：GPU 间接绘制
```

关键点：`CopyCount` 在 GPU 内完成，CPU 不读取可见数量，因此不会产生逐帧 GPU→CPU 同步等待。

当前典型运行数据：

- 总实例约 48 万。
- 可见实例约 7 万。
- 两个草型、两个 LOD，最多 4 个草 DrawCall。

## 7. 草 Shader 做什么

`GrassIndirect.shader` 的顶点阶段：

1. 用 `SV_InstanceID` 读取可见实例。
2. 应用实例高度、宽度和旋转。
3. 叠加连续风场。
4. 读取交互纹理，让玩家附近的草倒伏。
5. 根据距离执行随机淡出。

片元阶段负责根部压暗、顶部渐变、世界空间颜色分区、主光、环境光、背光透射、阴影和雾。

草形状来自 `GrassChunkManager.CreateGrassClumpMesh()`：每个实例包含 5 根分段弧形叶片；远处 LOD 使用更少顶点。

## 8. 交互倒伏

`GrassInteractionController` 维护两张 256×256 RenderTexture：

```text
上一帧纹理
→ 根据玩家移动重映射
→ 旧痕迹衰减
→ 写入本帧玩家笔刷
→ 交换两张纹理
```

Shader 根据草的世界坐标采样这张纹理。没有为每根草创建碰撞体，也没有逐草 CPU 更新。

## 9. 最常用的调试入口

- `I`：展开运行时指标面板。
- `GrassChunkManager.TotalInstances`：CPU 缓存实例数。
- `VisibleLOD0 / VisibleLOD1`：异步读回的可见数，仅用于低频统计。
- `LastDrawCalls`：本帧草 DrawCall。
- `RequestOverlapDiagnostics()`：检查生成数据是否叠点。
- `LiveFrameCapture.Request(path)`：不暂停帧循环的真实运行截图。
- `PerfMatrix.txt`：同机相对性能对照，不作为跨设备绝对结论。

如果“有实例但没有草”，按此顺序检查：

```text
enableGrassRender
→ GrassChunkManager 是否完成全局缓冲重建
→ ComputeShader buffer 是否在每次 Dispatch 前重新绑定
→ Args 的 instanceCount
→ Shader keyword 与 _VisibleIndices
```

## 10. 面试时两分钟讲法

> 这是一个 Unity 6 URP 的 GPU 驱动草地 Demo。地形由全局高度场生成，并通过 Chunk 距离环流式加载；平缓区域使用四叉树简化。草实例在 CPU 确定性生成一次，之后常驻 GPU。每帧由 ComputeShader 完成距离、视锥和 LOD 分类，结果写入 Append Buffer，再通过 CopyCount 和 RenderMeshIndirect 绘制，所以 CPU 不需要逐草遍历或同步读回。风和玩家倒伏都在顶点阶段完成，交互数据来自一张随玩家移动的双缓冲 RenderTexture。最终约 48 万实例只需要最多 4 个草 DrawCall。

如果面试官继续追问，优先展开三个点：**为什么实例常驻 GPU、为什么使用 Append + CopyCount、如何保证 Chunk 重载结果一致。**
