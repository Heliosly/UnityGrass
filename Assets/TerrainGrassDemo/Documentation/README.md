# Aether Meadow — Unity 6 GPU 草地与大世界渲染 Demo

想快速理解代码：阅读 [`QuickCodeGuide.md`](QuickCodeGuide.md)。

想逐条学习简历项目描述及对应实现：阅读 [`ResumeProjectTutorial.md`](ResumeProjectTutorial.md)。

面向秋招作品集的可运行技术 Demo：明亮仙境草甸、1024×1024 米程序地形、Chunk 流式加载、GPU 草实例裁剪/LOD、风场与玩家交互倒伏。当前展示版基于 **Unity 6000.3.23f1 LTS + URP 17.3 + Forward+**。

## 快速开始

打开 `Assets/TerrainGrassDemo/Scenes/Demo.unity` 后进入 Play Mode。

- `WASD`：移动交互体，压倒附近草叶
- 鼠标右键拖拽：旋转第三人称镜头
- `F`：第三人称/自由飞行切换；自由飞行时 `Q/E` 升降
- `I`：展开或收起运行时技术面板

项目启动后默认显示干净画面；技术指标隐藏在 `I` 面板中，不遮挡作品集截图。

## 当前视觉方案

- URP 主光源、软阴影、SSAO、ACES、轻量 Bloom、颜色分级与线性空气透视。
- 程序渐变天空和静态 FBM 柔云，无第三方天空素材依赖。
- 五叶片弧形几何草簇；每实例独立高度、宽度、朝向、类型、风相位。
- 连续世界空间色域与高度分区，避免 Chunk 边界和单色“草刷”观感。
- 道路边缘概率侵入、花簇、微光粒子、岩石、低密度远景树群和远端遗迹环共同构图。
- 装饰层使用 `Graphics.RenderMeshInstanced`，没有数百个装饰 GameObject。

## GPU 驱动管线

```text
CPU 确定性生成 GrassInstanceData（32 B/实例）
       ↓ 一次上传，常驻 GPU
ComputeShader：距离 + 三点视锥 + 密度 + LOD 分类
       ↓ AppendStructuredBuffer<GrassInstance>
GraphicsBuffer.CopyCount → IndirectArguments.instanceCount
       ↓ 全程不做同步 CPU 读回
Graphics.RenderMeshIndirect（LOD0 / LOD1）
       ↓
URP 草 Shader：几何变形 + 风 + 交互 RT + 光照/透射/雾
```

Unity 6 展示版已从旧的 `DrawMeshInstancedIndirect + ComputeBuffer args` 迁移到当前的 `RenderMeshIndirect + GraphicsBuffer args`。实例缓冲与 Append 缓冲继续使用 ComputeBuffer，以保持 ComputeShader 数据契约稳定。

## 系统结构

```text
DemoWorld
├─ HeightMapData                 全局连续高度场与中心差分法线
├─ TerrainQuadTreeSimplifier     可配置误差的地形网格简化
├─ TerrainChunkStreamer          近/中/远距离环、迟滞、卸载延迟、帧预算
├─ GrassChunkManager             两种草型全局分桶，DrawCall 不随 Chunk 数增长
│  └─ GrassChunkRenderer         GPU 裁剪、LOD、现代间接绘制与异步统计
├─ GrassWindController           连续世界风与阵风纹理
├─ GrassInteractionController    256² 双缓冲交互场，衰减/重映射/笔刷合并
├─ MeadowDecorRenderer           花、岩石、微光、树群、遗迹环实例化层
└─ RenderingStatsPanel           实例、可见数、DrawCall、压缩率与性能矩阵
```

## 关键工程结论

1. Append 缓冲直接保存完整实例，顶点阶段以 `SV_InstanceID` 读取，避免二次索引失配与叠点。
2. 草实例按世界坐标确定性生成；Chunk 卸载重载后位置、尺寸与风相位不变。
3. 草渲染按类型合并为两个全局桶，典型帧仅 2 次裁剪 Dispatch 和最多 4 次草 Draw。
4. LOD/密度以连续距离和实例 seed 决定，Chunk 环边界不再形成棋盘状稀疏断层。
5. 交互使用随玩家移动的 RenderTexture 场；旧区域重映射并指数衰减，不需要逐草 CPU 碰撞。
6. MCP 截图会暂停编辑器帧循环，本项目提供 `LiveFrameCapture`，在 `WaitForEndOfFrame` 后无暂停抓取真实运行帧。

## 验证

```powershell
dotnet run --project Tools/LayoutVerify/LayoutVerify.csproj
dotnet run --project Tools/GrassGenVerify/GrassGenVerify.csproj
```

运行时展开 `I` 面板可执行 11 阶段性能矩阵，结果写入 `Assets/TerrainGrassDemo/PerfMatrix.txt`。矩阵用于同机同分辨率相对比较；Editor FPS 不能代替 Standalone Player 的 GPU/CPU Profiler 数据。

完整 900 帧 batchmode 冒烟测试会验证自动移动、流式加载、贴地、交互、LOD、实例重合和截图。先关闭当前项目的 Unity 编辑器，再执行：

```powershell
powershell -ExecutionPolicy Bypass -File Tools/run_smoke_test.ps1
```

## 作品集截图

可靠的实时运行画面保存在 `Assets/TerrainGrassDemo/Captures/`。`LiveFrameCapture` 不设置 `EditorApplication.isPaused`，因此截图中包含真实的草地渲染状态。

性能结论只保留可复现数据，不把 Editor 单次 FPS 写成硬件无关结论。面试展示建议同时打开 Game 视图与 `I` 指标面板，依次切换 GPU Culling、LOD、四叉树、风和交互。
