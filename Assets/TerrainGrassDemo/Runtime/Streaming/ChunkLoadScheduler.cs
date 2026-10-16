using System.Collections.Generic;
using UnityEngine;

namespace TerrainGrassDemo.Streaming
{
    /// <summary>一条待执行的加载/卸载请求。</summary>
    public struct PendingChunkOp
    {
        public int ChunkX;
        public int ChunkZ;
        public ChunkLoadLevel Level; // 加载目标等级 / 卸载目标等级
        public float Distance;       // 到相机的距离, 用于排序
    }

    /// <summary>
    /// Chunk 加载调度器。
    ///
    /// 为什么需要调度器而不是"发现需要就立刻生成":
    /// 程序化生成一个 Chunk(地形网格 + 上千草实例)有毫秒级开销,
    /// 如果相机高速移动, 一帧内可能突然需要十几个 Chunk, 直接全做会掉帧。
    /// 调度器把请求攒在队列里, 每帧只执行"每帧预算"个:
    ///   - 预算内只挑距离最近的先做(相机旁边的 Chunk 最需要立刻可见);
    ///   - 剩下的下帧继续, 分摊到多帧, 帧时间平稳。
    ///
    /// 数据结构选型: List + 每帧 Sort。
    /// Chunk 总数只有几十个, 排序开销可忽略; 真正的大世界(上万 Chunk)
    /// 这里应换成二叉堆或按距离分桶, 但 Demo 以可读性优先。
    /// </summary>
    public sealed class ChunkLoadScheduler
    {
        private readonly List<PendingChunkOp> m_Loads = new List<PendingChunkOp>();
        private readonly List<PendingChunkOp> m_Unloads = new List<PendingChunkOp>();

        public int LoadQueueCount => m_Loads.Count;
        public int UnloadQueueCount => m_Unloads.Count;

        /// <summary>
        /// 提交一个加载请求。同一个 Chunk 重复提交时:
        ///   - 等级取更高者(相机折返时队列里可能已有 TerrainOnly, 又要求 FullGrass);
        ///   - 距离取更小者(排序优先级更准)。
        /// </summary>
        public void SubmitLoad(int cx, int cz, ChunkLoadLevel level, float dist)
        {
            for (int i = 0; i < m_Loads.Count; i++)
            {
                var op = m_Loads[i];
                if (op.ChunkX == cx && op.ChunkZ == cz)
                {
                    if (level > op.Level) op.Level = level;
                    if (dist < op.Distance) op.Distance = dist;
                    m_Loads[i] = op;
                    return;
                }
            }
            m_Loads.Add(new PendingChunkOp { ChunkX = cx, ChunkZ = cz, Level = level, Distance = dist });
        }

        /// <summary>提交一个卸载请求(已过延迟期)。同 Chunk 以更低的等级为准。</summary>
        public void SubmitUnload(int cx, int cz, ChunkLoadLevel targetLevel, float dist)
        {
            for (int i = 0; i < m_Unloads.Count; i++)
            {
                var op = m_Unloads[i];
                if (op.ChunkX == cx && op.ChunkZ == cz)
                {
                    if (targetLevel < op.Level) op.Level = targetLevel;
                    if (dist < op.Distance) op.Distance = dist;
                    m_Unloads[i] = op;
                    return;
                }
            }
            m_Unloads.Add(new PendingChunkOp { ChunkX = cx, ChunkZ = cz, Level = targetLevel, Distance = dist });
        }

        public void RemoveLoad(int cx, int cz)
        {
            for (int i = m_Loads.Count - 1; i >= 0; i--)
            {
                if (m_Loads[i].ChunkX == cx && m_Loads[i].ChunkZ == cz) m_Loads.RemoveAt(i);
            }
        }

        public void RemoveUnload(int cx, int cz)
        {
            for (int i = m_Unloads.Count - 1; i >= 0; i--)
            {
                if (m_Unloads[i].ChunkX == cx && m_Unloads[i].ChunkZ == cz) m_Unloads.RemoveAt(i);
            }
        }

        /// <summary>清空全部队列(关闭流式模式时用)。</summary>
        public void Clear()
        {
            m_Loads.Clear();
            m_Unloads.Clear();
        }

        /// <summary>
        /// 取出本帧要执行的请求: 原地按距离升序排序, 各取前 N 条并出队。
        /// 出队后剩下的保持已排序状态, 下次 Drain 只需要处理新提交的部分,
        /// 但为了代码简单这里每次都重排(几十个元素无所谓)。
        /// </summary>
        public void Drain(int maxLoads, int maxUnloads, List<PendingChunkOp> outLoads, List<PendingChunkOp> outUnloads)
        {
            outLoads.Clear();
            outUnloads.Clear();

            if (m_Loads.Count > 0)
            {
                m_Loads.Sort(CompareByDistance);
                int take = Mathf.Min(maxLoads, m_Loads.Count);
                for (int i = 0; i < take; i++) outLoads.Add(m_Loads[i]);
                m_Loads.RemoveRange(0, take);
            }

            if (m_Unloads.Count > 0)
            {
                m_Unloads.Sort(CompareByDistance);
                int take = Mathf.Min(maxUnloads, m_Unloads.Count);
                for (int i = 0; i < take; i++) outUnloads.Add(m_Unloads[i]);
                m_Unloads.RemoveRange(0, take);
            }
        }

        private static int CompareByDistance(PendingChunkOp a, PendingChunkOp b)
        {
            return a.Distance.CompareTo(b.Distance);
        }
    }
}
