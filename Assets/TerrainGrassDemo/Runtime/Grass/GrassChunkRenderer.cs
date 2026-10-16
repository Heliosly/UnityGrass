using System;
using System.Collections.Generic;
using TerrainGrassDemo.Terrain;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerrainGrassDemo.Grass
{
    /// <summary>
    /// 单个草 Chunk 的 GPU 渲染器。
    ///
    /// 管线的每块拼图(需求 3.6~3.11, 按执行顺序):
    ///   1. _Instances          : 实例数据(StructuredBuffer), 生成一次, 常驻 GPU;
    ///   2. _VisibleLOD0/LOD1   : Append 缓冲, ComputeShader 把"通过裁剪"的实例索引 Append 进去;
    ///   3. CopyCount           : 把 Append 缓冲的 Count 拷到 Args 缓冲的 InstanceCount 槽
    ///                            (Args 布局 = {indexCountPerInstance, instanceCount, startIndex,
    ///                             baseVertex, startInstance}, InstanceCount 在字节偏移 4);
    ///   4. RenderMeshIndirect  : Unity 6 当前的间接绘制 API。GPU 只读 Args,
    ///                            一次 DrawCall 画 N 个实例,
    ///                            顶点 Shader 里用 SV_InstanceID 重映射回原始实例索引。
    ///
    /// 为什么 Append + CopyCount 而不是 CPU 读回数量再填 Args:
    ///   读回(GPU→CPU)要等 GPU 完成并产生同步停顿, 每帧做一次会严重拖慢流水线;
    ///   CopyCount 全程在 GPU 端完成, CPU 不碰任何裁剪结果 —— 这就是"GPU 驱动渲染"的核心。
    ///
    /// 资源生命周期(配合流式加载):
    ///   Generate() 分配全部 5 个缓冲; Release() 全部释放(ComputeBuffer.Release,
    ///   不是 Destroy —— Release 立即归还 GPU 资源, 不依赖帧尾 GC)。
    ///   Chunk 被卸载时流式层先调 Release 再隐藏地形, 满足需求 2.8 的顺序。
    /// </summary>
    public sealed class GrassChunkRenderer : IDisposable
    {
        private ComputeBuffer m_Instances;
        private ComputeBuffer m_VisibleLOD0;
        private ComputeBuffer m_VisibleLOD1;
        // Unity 6 的 Graphics.RenderMeshIndirect 要求 GraphicsBuffer 参数缓冲。
        // 实例与 Append 缓冲仍保留 ComputeBuffer，避免改动稳定的 ComputeShader 数据契约。
        private GraphicsBuffer m_ArgsLOD0;
        private GraphicsBuffer m_ArgsLOD1;
        private readonly MaterialPropertyBlock m_MPBLOD0 = new MaterialPropertyBlock();
        private readonly MaterialPropertyBlock m_MPBLOD1 = new MaterialPropertyBlock();
        private Bounds m_Bounds;
        private Mesh m_MeshLOD0; // LOD0: 本桶的草模型(多模型混合)
        private Mesh m_MeshLOD1; // LOD1: 统一用细叶模型(远处细节不重要, 省顶点/带宽)
        private float m_ModelScaleY = 1f;  // 本桶模型归一化: 1/模型高(顶点变换时乘)
        private float m_ModelScaleXZ = 1f; // 本桶模型归一化: 1/模型直径
        private int m_InstanceCount;
        private int m_LastDrawCalls;
        private List<GrassInstanceData> m_DataRef; // CPU 侧实例数据引用(诊断用; SetData 上传的就是它)
        private readonly uint[] m_FullArgs = new uint[5]; // CPU 对照路径的全量 Args(缓存, 防每帧分配)

        /// <summary>CPU 侧实例数据(与提交 GPU 的完全一致), 重合诊断用。</summary>
        public List<GrassInstanceData> DataRef => m_DataRef;

        /// <summary>每桶的颜色(2 桶面片草: 主体冷绿 / 少量暖色高草)。</summary>
        private static readonly Color[] BucketBottom =
        {
            new Color(0.07f, 0.27f, 0.15f, 1f),
            new Color(0.11f, 0.29f, 0.14f, 1f),
        };
        private static readonly Color[] BucketTop =
        {
            new Color(0.30f, 0.69f, 0.36f, 1f),
            new Color(0.43f, 0.72f, 0.30f, 1f),
        };

        /// <summary>设置本 renderer 的草样式桶(0/1/2), 决定颜色。MPB 独立, 一次设置永久生效。</summary>
        public void SetBucketStyle(int bucket)
        {
            int b = Mathf.Clamp(bucket, 0, BucketBottom.Length - 1);
            m_MPBLOD0.SetColor("_ColorBottom", BucketBottom[b]);
            m_MPBLOD0.SetColor("_ColorTop", BucketTop[b]);
            m_MPBLOD1.SetColor("_ColorBottom", BucketBottom[b]);
            m_MPBLOD1.SetColor("_ColorTop", BucketTop[b]);
        }

        /// <summary>设置本桶模型的归一化缩放因子(多模型混合: 各桶 scale 不同, 经 MPB 传给 shader)。</summary>
        public void SetModelScale(float scaleY, float scaleXZ)
        {
            m_ModelScaleY = scaleY;
            m_ModelScaleXZ = scaleXZ;
        }

        public int InstanceCount => m_InstanceCount;
        public bool HasGrass => m_Initialized && m_InstanceCount > 0;
        public int LastDrawCalls => m_LastDrawCalls;

        /// <summary>最近一次异步读回的可视实例数; 无数据时 -1。</summary>
        public int LastVisibleLOD0 { get; set; } = -1;
        public int LastVisibleLOD1 { get; set; } = -1;

        private bool m_Initialized;

        private const int ArgsStrideBytes = 5 * sizeof(uint);
        private const int InstanceCountByteOffset = 4; // Args[1] = instanceCount

        /// <summary>
        /// 生成(或重新生成)草实例。
        /// instances = 本桶的分好类的实例列表(manager 已按 type01 分桶)。
        /// 踩坑1: 旧版在此内部重新调用 GrassInstanceGenerator.Generate 生成【全部】实例,
        /// 每个桶的 renderer 都上传/渲染全量 → 同一位置被 5 种模型各画一遍,
        /// 数量虚高 5 倍、视觉"一个点位多次重合"、总量 35 万实为 5×7 万。
        /// 踩坑2(叠点/多边形块): 旧版每次 Generate 都 Release 全部缓冲再重建 ——
        /// 全局合并后加载期间每帧重建, 渲染循环中释放缓冲会让排队中的 Draw 命令
        /// 访问已释放/重建的资源 → GPU 数据错乱。修复: 缓冲容量复用,
        /// 实例数在容量内只 SetData(命令排队, 按序执行, 安全)。
        /// </summary>
        public void Generate(List<GrassInstanceData> instances, Mesh lod0Mesh, Mesh lod1Mesh, Bounds bounds)
        {
            m_InstanceCount = instances.Count;
            m_DataRef = new List<GrassInstanceData>(instances); // 深拷贝: 诊断数据不被共享列表复用污染
            m_MeshLOD0 = lod0Mesh;
            m_MeshLOD1 = lod1Mesh != null ? lod1Mesh : lod0Mesh;
            if (m_InstanceCount == 0)
            {
                Release();
                return;
            }

            // 容量复用: 只有实例数超出容量才重建(扩容罕见; 常见路径 = 只 SetData)
            EnsureCapacity(ref m_Instances, m_InstanceCount, GrassInstanceData.SizeBytes, ComputeBufferType.Default);
            EnsureCapacity(ref m_VisibleLOD0, m_InstanceCount, GrassInstanceData.SizeBytes, ComputeBufferType.Append);
            EnsureCapacity(ref m_VisibleLOD1, m_InstanceCount, GrassInstanceData.SizeBytes, ComputeBufferType.Append);
            EnsureArgsBuffer(ref m_ArgsLOD0);
            EnsureArgsBuffer(ref m_ArgsLOD1);

            m_Instances.SetData(instances);
            SetArgsBuffer(m_ArgsLOD0, lod0Mesh);
            SetArgsBuffer(m_ArgsLOD1, lod1Mesh);

            // CPU 对照路径的全量 Args(缓存, 每帧只改 InstanceCount, 避免每帧分配数组)
            m_FullArgs[0] = lod0Mesh != null ? lod0Mesh.GetIndexCount(0) : 0;
            m_FullArgs[1] = (uint)m_InstanceCount;
            m_FullArgs[2] = lod0Mesh != null ? lod0Mesh.GetIndexStart(0) : 0;
            m_FullArgs[3] = lod0Mesh != null ? lod0Mesh.GetBaseVertex(0) : 0;
            m_FullArgs[4] = 0;

            // MPB 是每 renderer 独立对象(不是共享状态): buffer 引用和模型 scale
            // 可以持久绑定, Draw 每帧不需要再 Set。ComputeShader 的绑定不能这么做
            // (共享对象, 会被其他 renderer 覆盖), 见 Draw 里的注释。
            m_MPBLOD0.SetBuffer("_VisibleIndices", m_VisibleLOD0);
            m_MPBLOD1.SetBuffer("_VisibleIndices", m_VisibleLOD1);
            m_MPBLOD0.SetVector("_GrassModelScale", new Vector4(m_ModelScaleXZ, m_ModelScaleY, 0f, 0f));
            m_MPBLOD1.SetVector("_GrassModelScale", new Vector4(m_ModelScaleXZ, m_ModelScaleY, 0f, 0f));

            m_Bounds = bounds;
            m_Initialized = true;
        }

        /// <summary>容量不足时重建缓冲(释放旧 + 创建新); 容量够则原样复用。</summary>
        private static void EnsureCapacity(ref ComputeBuffer buffer, int count, int stride, ComputeBufferType type)
        {
            if (buffer != null && buffer.count >= count) return;
            if (buffer != null) buffer.Release();
            buffer = new ComputeBuffer(count, stride, type);
        }

        private static void EnsureArgsBuffer(ref GraphicsBuffer buffer)
        {
            if (buffer != null && buffer.count >= 1) return;
            if (buffer != null) buffer.Release();
            buffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, ArgsStrideBytes);
        }

        /// <summary>填 Args 缓冲的固定部分(InstanceCount 每帧由 CopyCount 覆写)。</summary>
        private static void SetArgsBuffer(GraphicsBuffer buffer, Mesh mesh)
        {
            uint[] args = { mesh.GetIndexCount(0), 0, mesh.GetIndexStart(0), mesh.GetBaseVertex(0), 0 };
            buffer.SetData(args);
        }

        /// <summary>
        /// CPU 粗粒度视锥裁剪(需求 3.7): 用 Chunk 包围盒先挡掉视野外的整个 Chunk,
        /// 省掉一次毫无意义的 Dispatch。逐实例的精确裁剪交给 ComputeShader。
        /// planes 由 Manager 每帧算一次传入(避免 180 个 renderer 重复计算)。
        /// </summary>
        public bool IsChunkVisible(Plane[] frustumPlanes)
        {
            return m_Initialized && GeometryUtility.TestPlanesAABB(frustumPlanes, m_Bounds);
        }

        /// <summary>
        /// 每帧绘制。返回本 Chunk 的草 DrawCall 数(0/1/2)。
        ///
        /// GPU 路径: Dispatch(裁剪) → Append 可见实例 → CopyCount → RenderMeshIndirect。
        /// CPU 对照路径(enableGpuCulling=false): 跳过裁剪, 全量实例直接绘制 ——
        /// 用于量化"GPU 裁剪省了多少"(帧率/DrawCall 对比), 不是正常渲染路径。
        /// </summary>
        public int Draw(Camera cam, ComputeShader culling, Material matLOD0, Material matLOD1,
            TerrainGrassDemoSettings s, int renderQueueOffset, Plane[] frustumPlanes)
        {
            m_LastDrawCalls = 0;
            if (!IsChunkVisible(frustumPlanes)) return 0;
            Mesh meshLOD0 = m_MeshLOD0;
            Mesh meshLOD1 = m_MeshLOD1;
            if (meshLOD0 == null) return 0;

            if (!s.enableGpuCulling)
            {
                // ---------- CPU 对照路径: 全量绘制(不裁剪) ----------
                // 实例数据全量可见: MPB 改绑 _Instances(数据), Args 计数 = 实例总数
                m_MPBLOD0.SetBuffer("_VisibleIndices", m_Instances);
                m_FullArgs[1] = (uint)m_InstanceCount; // 每帧只改计数槽, 其余在 Generate 时填好
                m_ArgsLOD0.SetData(m_FullArgs);
                RenderIndirect(cam, meshLOD0, matLOD0, m_ArgsLOD0, m_MPBLOD0);
                m_LastDrawCalls++;
                return m_LastDrawCalls;
            }

            // ---------- GPU 裁剪: 只保留可见实例(距离/视锥/LOD), 每帧重写 Append ----------
            // 踩坑: 曾把 SetBuffer 做成"首次绑定后持久"——但 ComputeShader 是全部 chunk
            // 共享的同一个对象, 后绘制 chunk 的 SetBuffer 会覆盖先绘制 chunk 的绑定,
            // 导致先加载的 chunk 读到错误 buffer → 草整片消失。必须每次 Dispatch 前绑定。
            // 另一个踩坑: CPU 对照路径会把 MPB 的 _VisibleIndices 改绑到全量数据缓冲,
            // 切回 GPU 路径时必须重新绑回可见缓冲, 否则 shader 读的是"原始顺序的前 N 个"
            // (脚底下的草排在后面 → 被画不到, 用户看到"脚底下被剔除")。
            m_MPBLOD0.SetBuffer("_VisibleIndices", m_VisibleLOD0);
            m_MPBLOD1.SetBuffer("_VisibleIndices", m_VisibleLOD1);
            int kernel = 0; // CSMain
            culling.SetBuffer(kernel, "_Instances", m_Instances);
            culling.SetBuffer(kernel, "_VisibleLOD0", m_VisibleLOD0);
            culling.SetBuffer(kernel, "_VisibleLOD1", m_VisibleLOD1);
            culling.SetInt("_InstanceCount", m_InstanceCount);
            m_VisibleLOD0.SetCounterValue(0);
            m_VisibleLOD1.SetCounterValue(0);
            culling.Dispatch(kernel, Mathf.CeilToInt(m_InstanceCount / 64f), 1, 1);
            GraphicsBuffer.CopyCount(m_VisibleLOD0, m_ArgsLOD0, InstanceCountByteOffset);
            GraphicsBuffer.CopyCount(m_VisibleLOD1, m_ArgsLOD1, InstanceCountByteOffset);

            // ---------- LOD0 ----------
            RenderIndirect(cam, meshLOD0, matLOD0, m_ArgsLOD0, m_MPBLOD0);
            m_LastDrawCalls++;

            // ---------- LOD1 ----------
            if (s.enableGrassLod && meshLOD1 != null)
            {
                RenderIndirect(cam, meshLOD1, matLOD1, m_ArgsLOD1, m_MPBLOD1);
                m_LastDrawCalls++;
            }

            return m_LastDrawCalls;
        }

        private void RenderIndirect(Camera cam, Mesh mesh, Material material, GraphicsBuffer args,
            MaterialPropertyBlock properties)
        {
            var renderParams = new RenderParams(material)
            {
                camera = cam,
                worldBounds = m_Bounds,
                matProps = properties,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                layer = 0,
                lightProbeUsage = LightProbeUsage.Off
            };
            Graphics.RenderMeshIndirect(renderParams, mesh, args, 1, 0);
        }

        /// <summary>异步读回可见实例数(调试统计用, 低频调用, 不阻塞主线程)。</summary>
        public AsyncGPUReadbackRequest RequestVisibleCountReadback(int lod)
        {
            GraphicsBuffer args = lod == 0 ? m_ArgsLOD0 : m_ArgsLOD1;
            return args == null ? default : AsyncGPUReadback.Request(args, sizeof(uint), InstanceCountByteOffset);
        }

        /// <summary>诊断用: 读回整个 LOD0 可见缓冲(一次性, 定位"点位多次重合")。
        /// 注意: AsyncGPUReadback 对 Append buffer 读回会失败, 此方法已废弃,
        /// 改用 CPU 侧 DataRef 统计(见 GrassChunkManager.TickDiagnostics)。</summary>
        public AsyncGPUReadbackRequest RequestFullVisibleReadback()
        {
            return default; // Append buffer 读回不支持, 返回空请求(永不 done)
        }

        /// <summary>释放全部 GPU 缓冲(幂等)。卸载 Chunk 时先于地形回收调用。</summary>
        public void Release()
        {
            if (m_Instances != null) { m_Instances.Release(); m_Instances = null; }
            if (m_VisibleLOD0 != null) { m_VisibleLOD0.Release(); m_VisibleLOD0 = null; }
            if (m_VisibleLOD1 != null) { m_VisibleLOD1.Release(); m_VisibleLOD1 = null; }
            if (m_ArgsLOD0 != null) { m_ArgsLOD0.Release(); m_ArgsLOD0 = null; }
            if (m_ArgsLOD1 != null) { m_ArgsLOD1.Release(); m_ArgsLOD1 = null; }
            m_InstanceCount = 0;
            m_Initialized = false;
            m_DataRef = null;
            LastVisibleLOD0 = -1;
            LastVisibleLOD1 = -1;
        }

        public void Dispose()
        {
            Release();
        }
    }
}
