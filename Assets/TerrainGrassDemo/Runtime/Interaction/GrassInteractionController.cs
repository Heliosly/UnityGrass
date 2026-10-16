using System.Collections.Generic;
using UnityEngine;

namespace TerrainGrassDemo.Interaction
{
    /// <summary>
    /// 草地交互控制器 —— 低分辨率交互纹理的 CPU 侧管理(需求 5)。
    ///
    /// 双缓冲设计:
    ///   ComputeShader 需要"读上一帧 + 写这一帧", 而同一张 RT 在同一趟 Dispatch 里
    ///   既做 SRV 又做 UAV 会产生未定义的读写竞争(纹理里的线程写像素, 其他线程同时读它)。
    ///   所以用两张 RT 轮流: 这帧读 A 写 B, 下帧读 B 写 A。交换只是换两个引用, 零拷贝。
    ///
    /// 纹理格式: ARGBFloat。
    ///   UNorm(R8G8B8A8) 也能开 UAV, 但 Compute 写入浮点值时会被截到 [0,1] 且精度 8bit,
    ///   衰减多次累积后会有明显的色带; ARGBFloat 全程 32bit 浮点, 衰减曲线干净。
    ///   分辨率只有 128x128, 显存代价可忽略。
    /// </summary>
    public sealed class GrassInteractionController
    {
        private const int MaxInteractors = 4;

        private readonly ComputeShader m_Compute;
        private readonly int m_Kernel;
        private readonly RenderTexture m_RT0;
        private readonly RenderTexture m_RT1;
        private readonly List<GrassInteractor> m_Interactors = new List<GrassInteractor>();
        private readonly Vector4[] m_PosArray = new Vector4[MaxInteractors];
        private readonly Vector4[] m_DirArray = new Vector4[MaxInteractors];
        private readonly Vector4[] m_ParamArray = new Vector4[MaxInteractors];

        private bool m_Flip;
        private Vector2 m_Center;
        private float m_Extent;

        /// <summary>当前生效(可被草地 Shader 采样)的交互纹理 —— 面板预览用。</summary>
        public RenderTexture ActiveTexture => m_Flip ? m_RT0 : m_RT1;

        public GrassInteractionController(ComputeShader compute, int resolution, float extent)
        {
            m_Compute = compute;
            m_Kernel = compute.FindKernel("CSMain");
            m_Extent = extent;
            m_RT0 = CreateRT(resolution);
            m_RT1 = CreateRT(resolution);
            ClearTexture(m_RT0);
            ClearTexture(m_RT1);
        }

        private static RenderTexture CreateRT(int resolution)
        {
            var rt = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBFloat)
            {
                name = "GrassInteractionRT",
                enableRandomWrite = true,   // 允许 ComputeShader 作为 UAV 写
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        private static void ClearTexture(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, new Color(0.5f, 0.5f, 0f, 0f)); // 方向居中, 强度 0
            RenderTexture.active = prev;
        }

        public void AddInteractor(GrassInteractor interactor) => m_Interactors.Add(interactor);
        public void RemoveInteractor(GrassInteractor interactor) => m_Interactors.Remove(interactor);

        /// <summary>
        /// 每帧: 衰减上一帧 + 画笔刷 + 交换缓冲 + 发布全局 Shader 属性。
        /// centerWorld: 玩家(或交互场中心)世界坐标, 纹理跟随它移动。
        /// </summary>
        public void Tick(TerrainGrassDemoSettings s, Vector3 centerWorld)
        {
            if (m_Compute == null) return;

            // 中心贴到 0.5m 网格: 纹理像素覆盖 48m/128 ≈ 0.375m, 贴网格能避免
            // 玩家小步移动时所有像素世界坐标连续漂移造成的"游泳"感(可选优化)。
            Vector2 newCenter = new Vector2(
                Mathf.Round(centerWorld.x * 2f) * 0.5f,
                Mathf.Round(centerWorld.z * 2f) * 0.5f);

            float decay = Mathf.Exp(-s.interactionDecayRate * Time.deltaTime);

            RenderTexture read = m_Flip ? m_RT0 : m_RT1;
            RenderTexture write = m_Flip ? m_RT1 : m_RT0;

            m_Compute.SetTexture(m_Kernel, "_InteractionRead", read);
            m_Compute.SetTexture(m_Kernel, "_InteractionWrite", write);
            m_Compute.SetVector("_CenterOld", new Vector4(m_Center.x, m_Center.y, m_Extent, 0f));
            m_Compute.SetVector("_CenterNew", new Vector4(newCenter.x, newCenter.y, m_Extent, 0f));
            m_Compute.SetFloat("_Decay", decay);
            m_Compute.SetInt("_Res", m_RT0.width);

            int count = Mathf.Min(m_Interactors.Count, MaxInteractors);
            for (int i = 0; i < MaxInteractors; i++)
            {
                if (i < count)
                {
                    var it = m_Interactors[i];
                    m_PosArray[i] = new Vector4(it.Position.x, it.Position.y, it.Position.z, 0f);
                    m_DirArray[i] = new Vector4(it.Velocity.x, it.Velocity.z, 0f, it.Velocity.magnitude);
                    m_ParamArray[i] = new Vector4(it.Radius, it.Strength, 0f, 0f);
                }
                else
                {
                    m_PosArray[i] = Vector4.zero;
                    m_DirArray[i] = Vector4.zero;
                    m_ParamArray[i] = Vector4.zero;
                }
            }
            m_Compute.SetInt("_InteractorCount", count);
            m_Compute.SetVectorArray("_InteractorPos", m_PosArray);
            m_Compute.SetVectorArray("_InteractorDir", m_DirArray);
            m_Compute.SetVectorArray("_InteractorParams", m_ParamArray);

            m_Compute.Dispatch(m_Kernel, Mathf.CeilToInt(m_RT0.width / 8f), Mathf.CeilToInt(m_RT0.height / 8f), 1);

            m_Flip = !m_Flip;
            m_Center = newCenter;

            // 发布给草地 Shader(所有草 Chunk 共用)
            Shader.SetGlobalTexture("_GrassInteractionTex", write);
            Shader.SetGlobalVector("_GrassInteractionCenter", new Vector4(newCenter.x, newCenter.y, m_Extent, 0f));
        }

        /// <summary>运行时开关: 切换 Shader keyword。</summary>
        public void ApplyEnabled(bool enabled)
        {
            if (enabled) Shader.EnableKeyword("_GRASS_INTERACTION_ON");
            else Shader.DisableKeyword("_GRASS_INTERACTION_ON");
        }

        public void Dispose()
        {
            if (m_RT0 != null) m_RT0.Release();
            if (m_RT1 != null) m_RT1.Release();
            m_Interactors.Clear();
        }
    }
}
