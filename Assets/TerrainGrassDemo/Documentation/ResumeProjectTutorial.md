# Unity 6 GPU 草地项目：简历要点与代码教学

这份文档只解释简历中的四条项目描述。阅读时先理解“解决了什么问题”，再看代码如何实现，最后记住面试表达。

## 0. 项目整体数据流

```text
全局高度场
   ├─ 四叉树生成简化地形
   ├─ Chunk Streamer 决定附近加载等级
   └─ 草实例生成器计算位置和高度
                     ↓
各 Chunk 草缓存 → 两个全局草型桶 → GPU 常驻实例缓冲
                                      ↓
ComputeShader：距离、视锥、LOD 分类
                                      ↓
Append Buffer → CopyCount → RenderMeshIndirect
                                      ↓
草 Shader：实例变换、风、交互倒伏、光照和雾
```

入口是 [`DemoWorld.cs`](../Runtime/DemoWorld.cs)。`BuildWorld()` 创建所有系统，`DriveFrame()` 规定每帧顺序：

```csharp
m_Player.DriveUpdate(dt);
m_Streamer.Update(cam.transform.position);
m_Wind.Tick(settings);
m_Interaction.Tick(settings, m_Player.transform.position);
m_Grass.LateTick(cam);
m_Decor.Render(cam);
```

顺序的意义是：相机先更新，流式系统再决定 Chunk，风和交互随后更新，草最后使用本帧最新数据绘制。

---

## 1. 千米级地形、四叉树与 Chunk 流式加载

对应简历：

> 基于 Unity 6 URP 构建千米级程序草甸，使用全局高度场、四叉树网格简化及 Chunk 分级流式加载，结合加载预算、卸载延迟与迟滞机制控制资源开销。

### 1.1 为什么是千米级

[`TerrainGrassDemoSettings.cs`](../Runtime/TerrainGrassDemoSettings.cs) 的默认配置为：

```csharp
public int chunksX = 32;
public int chunksZ = 32;
public float chunkWorldSize = 32f;
```

地图边长为：

```text
32 Chunk × 32 米 = 1024 米
```

所以“千米级”描述的是地图尺寸，不代表所有对象同时加载。

### 1.2 全局高度场解决什么问题

[`HeightMapData.cs`](../Runtime/Terrain/HeightMapData.cs) 为整张地图保存一份高度与法线数组：

```csharp
public float[] Heights { get; }
public Vector3[] Normals { get; }

public int Idx(int x, int z) => z * VertsX + x;
```

Chunk 只是全局数组中的一个窗口。相邻 Chunk 的边缘读取相同的高度数据，因此不会因为各自生成误差产生裂缝。

草、地形和玩家都调用同一套双线性高度采样：

```csharp
float h00 = Heights[Idx(x0, z0)];
float h10 = Heights[Idx(x0 + 1, z0)];
float h01 = Heights[Idx(x0, z0 + 1)];
float h11 = Heights[Idx(x0 + 1, z0 + 1)];

return Mathf.Lerp(
    Mathf.Lerp(h00, h10, tx),
    Mathf.Lerp(h01, h11, tx),
    tz);
```

这里先沿 X 插值两次，再沿 Z 插值一次。它保证任意世界坐标都能获得连续高度。

### 1.3 四叉树如何减少地形三角形

核心文件是 [`TerrainQuadTreeSimplifier.cs`](../Runtime/Terrain/TerrainQuadTreeSimplifier.cs)。

算法从一个 Chunk 的完整矩形开始，递归分成四块。若四个子区域都足够平整，就合并回一个大区域。

项目使用共面误差，而不是要求四角等高：

```csharp
return Mathf.Abs(h0 + h3 - h1 - h2) <= 2f * flatnessError;
```

原因是斜坡虽然四角高度不同，但仍可能处于同一平面。平面只需两个三角形；真正弯曲的山脊才需要继续细分。

所有 Chunk 共用一张 `useful` 顶点表：

```csharp
var useful = new bool[hm.VertsX * hm.VertsZ];
```

邻居可以看到共享边上的顶点标记，构建三角形时会沿这些点拆边，从而避免 T 型接缝。

### 1.4 流式加载的四个等级

[`ChunkStreamingState.cs`](../Runtime/Streaming/ChunkStreamingState.cs) 定义四级状态：

```text
Unloaded          不显示
TerrainOnly       只有地形
TerrainLowGrass   地形和低密度草
TerrainFullGrass  地形和完整密度草
```

[`TerrainChunkStreamer.cs`](../Runtime/Streaming/TerrainChunkStreamer.cs) 根据相机所在 Chunk 计算目标等级：

```csharp
if (dist <= nearRingChunks) return TerrainFullGrass;
if (dist <= midRingChunks)  return TerrainLowGrass;
if (dist <= farRingChunks)  return TerrainOnly;
return Unloaded;
```

距离采用 Chebyshev 距离：

```csharp
return Mathf.Max(Mathf.Abs(cx - centerCx), Mathf.Abs(cz - centerCz));
```

它形成方形距离环，计算简单，正好对应规则 Chunk 网格。

### 1.5 预算、延迟和迟滞分别解决什么

每帧只从队列取有限操作：

```csharp
m_Scheduler.Drain(
    settings.maxLoadsPerFrame,
    settings.maxUnloadsPerFrame,
    loads,
    unloads);
```

- 加载预算：避免一帧同时构建大量 Mesh 引起卡顿。
- 卸载延迟：相机短暂离开时先等待，减少反复切换。
- 迟滞：卸载半径比加载半径更远，避免相机在边界附近来回抖动状态。

项目中的 `Unloaded` 会隐藏已经构建的地形对象，`EnsureBuilt()` 保证同一 Chunk 的 Mesh 只构建一次。这里属于缓存式流送；它控制激活对象和草 GPU 数据，但不会持续释放已经构建过的地形 Mesh 内存。

### 1.6 面试讲法

> 我把整张地图保存在一份全局高度场中，保证地形、草和玩家共享相同采样结果。每个 Chunk 生成完整网格与四叉树简化网格，运行时按相机距离分成完整草、低密度草、纯地形和隐藏四级。加载队列设置每帧预算，卸载使用延迟和迟滞，主要解决瞬时卡顿和边界反复切换。

常见追问：

- 为什么不用每 Chunk 独立高度图？共享边容易出现高度和法线不一致。
- 迟滞是什么？加载阈值和卸载阈值不相同，形成稳定缓冲区。
- 是否真正释放了所有远处内存？地形 Mesh 当前构建后缓存；草缓存会随等级移除并重新合并。

---

## 2. ComputeShader 裁剪、LOD 与 GPU 间接绘制

对应简历：

> 实现 ComputeShader 距离与视锥裁剪、LOD 分桶，通过 Append Buffer、CopyCount 和 RenderMeshIndirect 完成 GPU 驱动绘制；将多个 Chunk 的草实例合并为两个全局桶，约 48 万加载实例使用最多 4 次草绘制提交。

### 2.1 草实例里保存什么

[`GrassInstanceData.cs`](../Runtime/Grass/GrassInstanceData.cs) 定义单实例数据：

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct GrassInstanceData
{
    public Vector3 position; // 12 B
    public float yaw;        // 4 B
    public float height;     // 4 B
    public float width;      // 4 B
    public float type01;     // 4 B
    public float seed01;     // 4 B
}
```

总大小为 32 B。C#、ComputeShader 和绘制 Shader 中的字段顺序必须完全一致，否则 GPU 会把一个字段的字节解释成另一个字段。

### 2.2 实例为何能稳定重建

[`GrassInstanceGenerator.cs`](../Runtime/Grass/GrassInstanceGenerator.cs) 使用 Chunk 坐标、世界种子、格子坐标和实例编号生成随机序列：

```csharp
var rng = new SimpleRandom((int)(
    chunkSeed
    + (uint)gx * 73856093u
    + (uint)gz * 19349663u
    + (uint)b  * 2654435761u));
```

结果只由输入决定，与 Chunk 的加载顺序无关。因此卸载再加载后，草的位置、大小和风相位保持一致。

每格实例数量决定密度档：

```csharp
High   => 12;
Normal => 9;
Low    => 4;
```

低频世界噪声继续调制局部疏密，格内抖动打散规则网格。草的位置最终通过 `hm.SampleHeight(wx, wz)` 贴到地表。

### 2.3 为什么先合并全局桶

[`GrassChunkManager.cs`](../Runtime/Grass/GrassChunkManager.cs) 为每个 Chunk 保存 CPU 缓存，但绘制前按 `type01` 合并为两个全局桶：

```csharp
foreach (var chunk in m_ChunkCache)
    m_GlobalBuckets[b].AddRange(chunk.Value.Buckets[b]);
```

若每个 Chunk 分别绘制，DrawCall 会随加载 Chunk 数增长。合并后只剩：

```text
2 个草型 × 2 个 LOD = 最多 4 次草绘制
```

代价是 Chunk 加载、卸载或密度改变时，需要重新拼接并上传对应的全局实例缓冲。它适合变化频率远低于渲染帧率的静态植被。

### 2.4 GPU 缓冲分别负责什么

[`GrassChunkRenderer.cs`](../Runtime/Grass/GrassChunkRenderer.cs) 保存五类核心缓冲：

```csharp
ComputeBuffer  m_Instances;   // 全部实例
ComputeBuffer  m_VisibleLOD0; // 可见近景实例，Append
ComputeBuffer  m_VisibleLOD1; // 可见远景实例，Append
GraphicsBuffer m_ArgsLOD0;    // LOD0 间接绘制参数
GraphicsBuffer m_ArgsLOD1;    // LOD1 间接绘制参数
```

`m_Instances` 只在生成或重建时上传。每帧清空两个 Append Buffer 的计数器：

```csharp
m_VisibleLOD0.SetCounterValue(0);
m_VisibleLOD1.SetCounterValue(0);
```

然后启动 ComputeShader：

```csharp
culling.Dispatch(
    kernel,
    Mathf.CeilToInt(m_InstanceCount / 64f),
    1,
    1);
```

每个线程处理一个实例，因此线程组数是实例数除以 64 后向上取整。

### 2.5 ComputeShader 如何裁剪

[`GrassCulling.compute`](../ComputeShaders/GrassCulling.compute) 的流程如下。

距离裁剪：

```hlsl
float dist = distance(_CameraPos.xyz, g.position);
float maxDist = lerp(
    _FullDensityDistance,
    _CullingParams.y,
    g.seed01);
if (dist > maxDist) return;
```

近处全部保留，远处每根草根据固定 `seed01` 获得不同最大距离，形成连续稀疏效果。

视锥裁剪使用草根、中点和尖端三个点：

```hlsl
float4 c0 = mul(_VPMatrix, float4(g.position, 1));
float4 c1 = mul(_VPMatrix, float4(g.position + float3(0, g.height * .5, 0), 1));
float4 c2 = mul(_VPMatrix, float4(g.position + float3(0, g.height, 0), 1));

if (!InsideClip(c0) && !InsideClip(c1) && !InsideClip(c2)) return;
```

只测试根部会误删“根在屏幕外、叶尖仍在屏幕内”的草，三点测试更保守。

LOD 分类：

```hlsl
if (_CullingParams.w < 0.5 || dist <= _CullingParams.x)
    _VisibleLOD0.Append(g);
else
    _VisibleLOD1.Append(g);
```

这里直接 Append 完整实例，而不是 Append 索引。顶点 Shader 可以通过 `SV_InstanceID` 直接访问可见缓冲，避免再次间接索引。

### 2.6 CopyCount 与 RenderMeshIndirect

Append Buffer 的实际元素数量只在 GPU 上。项目使用：

```csharp
GraphicsBuffer.CopyCount(
    m_VisibleLOD0,
    m_ArgsLOD0,
    4);
```

偏移 4 字节对应间接参数中的第二个 `uint`：

```text
Args[0] indexCountPerInstance
Args[1] instanceCount          ← CopyCount 写这里
Args[2] startIndex
Args[3] baseVertex
Args[4] startInstance
```

随后直接绘制：

```csharp
Graphics.RenderMeshIndirect(
    renderParams,
    mesh,
    args,
    1,
    0);
```

CPU 不需要把可见数量读回来，也不会为了等待 GPU 结果而阻塞。这就是本项目“GPU 驱动”的核心含义。

### 2.7 数据和表述边界

当前实机验证记录：

```text
加载实例：480,749
可见 LOD0：29,602
可见 LOD1：41,981
草 DrawCall：4
```

简历应写“约 48 万加载实例，最多 4 次草绘制提交”。不能写“48 万草全部可见”或“整个场景只有 4 DrawCall”，因为地形和装饰还有各自的绘制提交。

### 2.8 面试讲法

> CPU 确定性生成草实例并上传到常驻 Structured Buffer。每帧 ComputeShader 为每个实例执行距离和三点视锥判断，再 Append 到 LOD0 或 LOD1 缓冲。CopyCount 把可见数直接写入 Indirect Args，RenderMeshIndirect 根据该数量绘制，全程没有同步 CPU 读回。Chunk 数据在 CPU 侧缓存，再合并成两个草型桶，因此 DrawCall 与当前加载 Chunk 数无关。

常见追问：

- 为什么不用 CPU 裁剪？几十万实例逐帧遍历和提交会增加主线程开销。
- 为什么用 Append Buffer？线程通过裁剪后可直接并行写入，无需 CPU 预先知道数量。
- 为什么 Args 使用 GraphicsBuffer？Unity 6 的 `RenderMeshIndirect` 接收 GraphicsBuffer。
- 全局合批的缺点？Chunk 变化时需要重建大缓冲，并且绘制包围盒更粗。

---

## 3. 草叶 Shader、风场和交互倒伏

对应简历：

> 编写草地 Shader，实现分段弧形叶片、风场变形、背光透射和距离淡出；使用双缓冲 RenderTexture 完成交互场重映射、笔刷写入与痕迹衰减，实现玩家经过时的草地倒伏与恢复。

### 3.1 草叶几何从哪里来

[`GrassChunkManager.CreateGrassClumpMesh()`](../Runtime/Grass/GrassChunkManager.cs) 程序生成草簇。每个实例有 5 根叶片，每根分成 4 段：

```csharp
const int bladeCount = 5;
const int segments = 4;
```

叶片沿高度逐渐变窄，并向自身方向弯曲：

```csharp
float halfWidth = baseHalfWidth
    * Mathf.Pow(1f - t, 0.72f) + 0.006f;

Vector3 center = offset
    + Vector3.up * (bladeHeight * t)
    + dir * (bend * t * t);
```

`t²` 让根部变化小、尖端弯曲明显。五根叶片使用黄金角分布，减少整齐十字面片的人工感。

LOD1 使用三片更简单的交叉叶片，减少远景顶点量。

### 3.2 顶点 Shader 如何还原实例

[`GrassIndirect.shader`](../Shaders/GrassIndirect.shader) 通过 `SV_InstanceID` 读取 ComputeShader 输出的可见实例：

```hlsl
GrassInstance grass = _VisibleIndices[input.instanceID];
```

然后应用宽度、高度和 Y 轴旋转：

```hlsl
localPos = float3(
    input.positionOS.x * grass.width,
    input.positionOS.y * grass.height,
    input.positionOS.z * grass.width);
```

这样所有实例共用一个 Mesh，仅实例参数不同。

### 3.3 风为什么使用多个尺度

风相位由时间、世界位置和实例 seed 共同决定：

```hlsl
float phase = _GrassWindTime.x
    + dot(grass.position.xz, windXZ) * 0.085
    + grass.seed01 * 2.4;
```

- 时间推动整体动画。
- 世界位置形成连续风浪。
- 实例 seed 避免所有草完全同步。

LOD0 还会采样低频阵风纹理：

```hlsl
float gust = SAMPLE_TEXTURE2D_LOD(
    _GrassGustTex,
    sampler_GrassGustTex,
    gustUV,
    0).r;
```

叶片弯曲权重为：

```hlsl
float bendWeight = hFrac * hFrac;
```

所以根部接近固定，尖端摆动最大，符合草叶受力直觉。

### 3.4 双缓冲交互纹理为何必要

[`GrassInteractionController.cs`](../Runtime/Interaction/GrassInteractionController.cs) 创建两张 RenderTexture。每帧一张读取上一帧，另一张写入新结果：

```csharp
RenderTexture read  = m_Flip ? m_RT0 : m_RT1;
RenderTexture write = m_Flip ? m_RT1 : m_RT0;
```

同一张纹理不能在一次 Compute Dispatch 中既稳定读取旧值又覆盖写入新值，所以使用 Ping-Pong 双缓冲。

交互纹理只覆盖玩家附近区域。中心移动后，[`GrassInteraction.compute`](../ComputeShaders/GrassInteraction.compute) 把新纹理像素转回世界坐标，再去旧中心的纹理采样：

```hlsl
float2 world = UVToWorld(uv, _CenterNew.xy, _CenterNew.z);
float2 oldUV = WorldToUV(world, _CenterOld.xy, _CenterOld.z);
float4 v = _InteractionRead.SampleLevel(
    sampler_InteractionRead,
    oldUV,
    0);
```

这一步叫重映射。玩家移动时，已经产生的草痕仍停留在原世界位置。

### 3.5 痕迹如何衰减和恢复

强度存放在 B 通道：

```hlsl
v.b *= _Decay;
```

C# 计算指数衰减系数：

```csharp
float decay = Mathf.Exp(
    -settings.interactionDecayRate * Time.deltaTime);
```

指数衰减对帧率不敏感。帧率改变时，相同真实时间后的结果接近一致。

R/G 通道编码倒伏方向，0.5 表示零方向。强度耗尽后方向归中：

```hlsl
if (v.b < 0.01f) v.rg = 0.5;
```

若直接让 RG 一起乘衰减，它们会趋向 0，解码后变成固定的负方向，草恢复时会产生错误漂移。

### 3.6 圆形笔刷如何写入

每个交互体上传位置、速度、半径和强度。ComputeShader 根据像素世界坐标计算距离：

```hlsl
float dist = length(world - _InteractorPos[i].xz);
float fall = saturate(
    1.0 - dist / _InteractorParams[i].x);
fall = fall * fall * (3.0 - 2.0 * fall);
```

随后渐进提高倒伏强度：

```hlsl
float target = max(v.b, strength);
v.b = lerp(v.b, target, 0.18);
```

这会产生被逐渐压弯的感觉，而不是瞬间翻倒。

草顶点 Shader 最终采样交互场：

```hlsl
float4 interaction = SAMPLE_TEXTURE2D_LOD(
    _GrassInteractionTex,
    sampler_GrassInteractionTex,
    interactionUV,
    0);

float2 push = (interaction.rg - 0.5) * 2.0;
bend += float3(push.x, 0, push.y)
    * interaction.b
    * _InteractionStrength
    * hFrac * hFrac;
```

仍然是根部稳定、尖端弯曲。

### 3.7 光照与距离淡出

片元阶段从根色插值到尖端色，并加入世界空间色块变化。光照包含环境光、主光、软化后的 Lambert、背光透射、少量高光与雾。

距离淡出由顶点阶段计算：

```hlsl
float individualLimit = lerp(
    _GrassFullDensity,
    _MaxDrawDistance,
    grass.seed01);

fade = 1.0 - saturate(
    (distanceToCamera - (individualLimit - _FadeRange))
    / _FadeRange);
```

片元阶段再用稳定哈希裁剪：

```hlsl
clip(input.fade - Hash21(input.positionCS.xy));
```

这属于抖动淡出，避免透明混合造成的大量排序和过度绘制问题。

### 3.8 面试讲法

> 草簇 Mesh 由五根分段弧形叶片组成，所有实例共用网格，在顶点阶段读取实例宽高和旋转。风由世界空间波、实例相位和阵风纹理组成，使用高度平方固定根部。交互使用玩家附近的双缓冲 RenderTexture，一次 Compute Dispatch 完成旧纹理重映射、指数衰减和圆形笔刷写入，再由草 Shader 按世界坐标采样并产生倒伏。

常见追问：

- 为什么不用碰撞体控制每根草？几十万 GameObject、Collider 和 CPU 更新成本过高。
- 为什么需要双缓冲？要同时读取上一帧状态并生成本帧状态。
- 为什么交互纹理跟随玩家？固定覆盖整张千米地图需要更高分辨率和内存。

---

## 4. 调试面板、性能矩阵与自动验证

对应简历：

> 搭建运行时调试面板与性能对照工具，支持裁剪、LOD、风场和交互开关，并通过异步 GPU 读回统计可见实例数，验证 CPU/HLSL 数据布局及实例位置唯一性。

### 4.1 面板读的是真实运行数据

[`RenderingStatsPanel.cs`](../Runtime/Debug/RenderingStatsPanel.cs) 读取：

```text
TotalInstances       CPU 草缓存总数
VisibleLOD0/LOD1     GPU 间接参数异步读回
LastDrawCalls        草绘制提交数
地形顶点/三角形      Mesh 实际数据
加载 Chunk 数        Streamer 当前激活数量
```

面板只展示系统已有统计，不参与渲染决策。

### 4.2 为什么统计必须异步读回

GPU 裁剪后，可见数量位于 Args Buffer。若每帧同步读取，CPU 会等待 GPU 完成前面的工作。

项目使用：

```csharp
AsyncGPUReadback.Request(
    args,
    sizeof(uint),
    InstanceCountByteOffset);
```

随后在未来帧检查：

```csharp
if (!request.done) continue;
if (!request.hasError)
    visible = (int)request.GetData<uint>()[0];
```

统计每 0.1 秒轮流读取草型桶。它有延迟，但不会阻塞正常渲染。可见数量用于观察和诊断，不参与下一帧绘制。

### 4.3 性能矩阵如何工作

[`DemoWorld.RunPerfMatrix()`](../Runtime/DemoWorld.cs) 依次切换：

```text
无草基线
四叉树开/关
LOD 开/关
GPU Culling 开/关
风和交互
Low / Normal / High 密度
```

每个阶段先预热 1 秒，再采样 3 秒：

```csharp
if (stageTime < PerfWarmup) return;
sum += Time.unscaledDeltaTime;
frames++;
```

平均帧耗时为：

```csharp
float avgMs = sum / frames * 1000f;
```

工具开始前保存原开关，结束后恢复，防止性能测试污染场景状态。

`PerfMatrix.txt` 是 Editor 内的相对对照。正式性能结论还应在固定分辨率的 Standalone Player 中使用 Unity Profiler 或 GPU 工具验证。

### 4.4 数据布局如何验证

[`Tools/LayoutVerify/Program.cs`](../../../Tools/LayoutVerify/Program.cs) 使用 `Marshal.SizeOf` 和 `Marshal.OffsetOf` 验证：

```text
position  0
yaw      12
height   16
width    20
type01   24
seed01   28
总大小   32 B
```

运行命令：

```powershell
dotnet run --project Tools/LayoutVerify/LayoutVerify.csproj
```

### 4.5 实例唯一性如何验证

运行时 `RequestOverlapDiagnostics()` 把 X/Z 位置按 1mm 量化为整数键，再统计每个键出现次数：

```csharp
long key =
    (long)Math.Round(position.x * 1000) * 10000000L
    + (long)Math.Round(position.z * 1000);
```

最终验证结果：

```text
实例数 480,749
唯一位置 480,749
最大同点 1
```

独立命令行生成验证位于 [`Tools/GrassGenVerify`](../../../Tools/GrassGenVerify/Program.cs)：

```powershell
dotnet run --project Tools/GrassGenVerify/GrassGenVerify.csproj
```

### 4.6 无暂停截图为何单独实现

MCP 相机截图会暂停 Editor，而此项目暂停时不会继续提交草绘制。页面截图因此可能只有地形。

[`LiveFrameCapture.cs`](../Runtime/Debug/LiveFrameCapture.cs) 等待帧末后调用 `ScreenCapture.CaptureScreenshot()`，不修改 `EditorApplication.isPaused`，所以能捕获真实运行状态。

### 4.7 面试讲法

> 我为所有优化项提供运行时开关和真实统计。可见实例数来自间接参数缓冲，但只以 AsyncGPUReadback 低频读取，避免同步阻塞。性能矩阵逐项控制变量，包含预热、采样和状态恢复。数据层还验证了 32 字节 CPU/HLSL 布局和 1mm 量化下的实例唯一性，防止渲染异常只靠肉眼判断。

常见追问：

- AsyncGPUReadback 是否实时？结果通常滞后若干帧，适合统计，不适合立即参与玩法判断。
- Editor FPS 能否写进简历？只能作同机相对对照，正式结论应来自 Player 构建。
- 为什么检查实例重合？错误分桶或缓冲绑定可能把大量实例画到相同位置。

---

## 5. “熟悉 GPU Instancing、ComputeShader 与间接绘制”如何由代码支撑

对应技能：

> 熟悉 Unity GPU Instancing、ComputeShader 与间接绘制，具备 GPU 实例裁剪、LOD、植被 Shader、RenderTexture 交互及 Chunk 流式加载的实践经验。

这句话分别对应：

| 技能 | 代码证据 |
|---|---|
| GPU Instancing | `MeadowDecorRenderer.Render()` 使用 `Graphics.RenderMeshInstanced` 合并花、石头、树等装饰 |
| ComputeShader | `GrassCulling.compute` 和 `GrassInteraction.compute` |
| 间接绘制 | `GrassChunkRenderer` 的 `Graphics.RenderMeshIndirect` |
| GPU 裁剪与 LOD | `GrassCulling.compute::CSMain` |
| 植被 Shader | `GrassIndirect.shader` 的顶点变形和片元光照 |
| RenderTexture 交互 | `GrassInteractionController` 的 Ping-Pong RT |
| Chunk 流式加载 | `TerrainChunkStreamer` 的距离环、预算、迟滞和延迟 |

`MeadowDecorRenderer` 的普通实例化调用：

```csharp
Graphics.RenderMeshInstanced(
    renderParams,
    mesh,
    0,
    matrices,
    matrices.Length);
```

它与草的间接绘制区别如下：

| 方式 | 实例数由谁提供 | 适用场景 |
|---|---|---|
| `RenderMeshInstanced` | CPU 提供矩阵数组和数量 | 数量较少、变化简单的装饰 |
| `RenderMeshIndirect` | GPU Args Buffer 提供数量 | 大量实例、GPU 裁剪和 LOD |

面试时可以主动说明你在同一项目中使用了两种方式，并能根据数量、更新频率和裁剪位置选择。

---

## 6. 建议的实际学习步骤

第一次阅读：只顺着调用链定位代码，不研究公式。

```text
DemoWorld.BuildWorld
→ TerrainChunkStreamer.Update
→ GrassChunkManager.LateTick
→ GrassChunkRenderer.Draw
→ GrassCulling.compute::CSMain
→ GrassIndirect.shader::BuildGrassVertex
```

第二次阅读：在 Game 视图按 `I` 打开面板，依次关闭 LOD、GPU Culling、风和交互，观察代码开关与画面、统计的对应关系。

第三次阅读：独立回答以下问题，答不出再回到对应章节：

1. 为什么全局高度场能减少 Chunk 接缝？
2. 四叉树为什么用共面误差，而不是四角等高？
3. Append Buffer 的计数如何进入 Draw Args？
4. 为什么可见数不能每帧同步读回 CPU？
5. 双缓冲交互纹理如何保留世界空间草痕？
6. 全局草型桶减少 DrawCall 的代价是什么？

能完整回答这六题，已经足以讲清简历上的四个项目要点。
