using System;
using System.Collections.Generic;
using TerrainGrassDemo.Terrain;
using UnityEngine;

namespace TerrainGrassDemo.Streaming
{
    /// <summary>
    /// Chunk 流式加载器(需求 2 的实现主体)。
    ///
    /// 核心机制:
    /// 1. 以相机所在 Chunk 为中心, 按 Chebyshev 距离(max(|dx|,|dz|))把地图分成三个距离环:
    ///      近环(≤nearRing): 地形 + 满密度草
    ///      中环(≤midRing):  地形 + 低密度草
    ///      远环(≤farRing):  仅地形
    ///      环外:            卸载
    /// 2. 加载/卸载用不同的半径(卸载半径 = 环半径 + hysteresis):
    ///      相机在环边界来回踱步时, Chunk 不会被反复"加载-卸载-加载"颠簸;
    ///      只有真正走出卸载半径才会开始卸载流程。
    /// 3. 卸载延迟: 进入卸载半径后不是立刻回收, 而是启动 unloadDelaySeconds 倒计时,
    ///      期间相机折返则取消 —— 双保险, 进一步减少抖动。
    /// 4. 重算节流: 相机移动不足 streamingMoveThreshold 且距上次重算不足
    ///      streamingCheckInterval 时, 完全跳过集合重算(加载/卸载集合是"集合",
    ///      只有中心变化时才需要重算, 没必要每帧全量遍历)。
    /// 5. 每帧预算: 通过 ChunkLoadScheduler 限制每帧生成/卸载数量, 距离最近优先。
    ///
    /// 资源策略(需求 2.7):
    ///   - Mesh 与 GameObject 归槽位所有、常驻复用: 卸载只是 SetActive(false),
    ///     重新加载直接激活, 不 Destroy/Instantiate, 也没有碎片化分配;
    ///   - 草地 ComputeBuffer 由草地层管理, 通过 OnChunkLevelChanged 回调通知,
    ///     加载顺序"先地形后草", 卸载顺序"先草后地形"(见 SetLevel)。
    /// </summary>
    public sealed class TerrainChunkStreamer : IDisposable
    {
        private sealed class Slot
        {
            public int ChunkX;
            public int ChunkZ;
            public TerrainChunkData Data;                 // 首次加载时惰性构建, 之后常驻
            public ChunkRuntimeState State = new ChunkRuntimeState();
        }

        private readonly HeightMapData m_HeightMap;
        private readonly TerrainGrassDemoSettings m_Settings;
        private readonly Material m_TerrainMaterial;
        private bool[] m_GlobalUseful; // 全地形共享"有用顶点"表(参考实现 m_IsVertexUseful); 可运行时替换(四叉树参数调整)
        private readonly Transform m_Parent;
        private readonly Dictionary<(int, int), Slot> m_Slots = new Dictionary<(int, int), Slot>();
        private readonly List<TerrainChunkData> m_AllBuilt = new List<TerrainChunkData>();
        private readonly List<TerrainChunkData> m_Active = new List<TerrainChunkData>();
        private readonly ChunkLoadScheduler m_Scheduler = new ChunkLoadScheduler();
        private readonly List<PendingChunkOp> m_DrainLoads = new List<PendingChunkOp>();
        private readonly List<PendingChunkOp> m_DrainUnloads = new List<PendingChunkOp>();

        private float m_TimeSinceCheck;
        private Vector3 m_LastCheckPos;
        private bool m_HasLastPos;
        private bool m_StreamingEnabled;
        private (int, int) m_CenterChunk;

        /// <summary>
        /// Chunk 等级变化回调: (chunk, 旧等级, 新等级)。
        /// 草地层监听它: 新等级 ≥ TerrainLowGrass 时生成草(密度按等级),
        /// 降到 TerrainOnly 时释放草。Streamer 自己只负责地形和 GameObject。
        /// </summary>
        public event Action<TerrainChunkData, ChunkLoadLevel, ChunkLoadLevel> OnChunkLevelChanged;

        public int LoadedCount { get; private set; }
        public int PendingLoadCount => m_Scheduler.LoadQueueCount;
        public int PendingUnloadCount { get; private set; }
        public (int, int) CenterChunk => m_CenterChunk;

        /// <summary>所有已构建过 Mesh 的 Chunk(常驻)。</summary>
        public IReadOnlyList<TerrainChunkData> AllBuiltChunks => m_AllBuilt;

        /// <summary>当前激活(等级 > Unloaded)的 Chunk。</summary>
        public IReadOnlyList<TerrainChunkData> ActiveChunks => m_Active;

        public TerrainChunkStreamer(HeightMapData heightMap, TerrainGrassDemoSettings settings, Material terrainMaterial,
            bool[] globalUseful, Transform parent)
        {
            m_HeightMap = heightMap;
            m_Settings = settings;
            m_TerrainMaterial = terrainMaterial;
            m_GlobalUseful = globalUseful;
            m_Parent = parent;
            m_StreamingEnabled = settings.enableStreaming;

            // 为所有 Chunk 预建状态槽位。注意这里只建"状态", 不建 Mesh —— Mesh 惰性构建。
            for (int cz = 0; cz < settings.chunksZ; cz++)
            {
                for (int cx = 0; cx < settings.chunksX; cx++)
                {
                    var slot = new Slot { ChunkX = cx, ChunkZ = cz };
                    slot.State.ChunkX = cx;
                    slot.State.ChunkZ = cz;
                    m_Slots[(cx, cz)] = slot;
                }
            }
        }

        /// <summary>
        /// loadAll = true: 流式关闭模式, 所有 Chunk 直接升到满级(不受预算限制)。
        /// loadAll = false: 从空开始, 等 Update 驱动按需加载。
        /// </summary>
        public void Initialize(bool loadAll)
        {
            if (loadAll)
            {
                for (int cz = 0; cz < m_Settings.chunksZ; cz++)
                {
                    for (int cx = 0; cx < m_Settings.chunksX; cx++)
                    {
                        SetLevel(m_Slots[(cx, cz)], ChunkLoadLevel.TerrainFullGrass);
                    }
                }
                m_StreamingEnabled = false;
            }
            else
            {
                m_StreamingEnabled = true;
                m_HasLastPos = false; // 强制下一帧立刻全量重算
            }
        }

        /// <summary>运行时开关流式加载(需求 2 / 面板开关)。</summary>
        public void SetStreamingEnabled(bool enabled)
        {
            if (enabled == m_StreamingEnabled) return;
            m_StreamingEnabled = enabled;
            if (!enabled)
            {
                // 关闭流式 → 全部升满
                for (int cz = 0; cz < m_Settings.chunksZ; cz++)
                {
                    for (int cx = 0; cx < m_Settings.chunksX; cx++)
                    {
                        var slot = m_Slots[(cx, cz)];
                        slot.State.Status = ChunkStatus.Idle;
                        slot.State.UnloadTimer = 0f;
                        SetLevel(slot, ChunkLoadLevel.TerrainFullGrass);
                    }
                }
                m_Scheduler.Clear();
            }
            else
            {
                m_HasLastPos = false; // 强制重算, 让距离环立刻生效
            }
        }

        /// <summary>每帧驱动。centerPos 取相机位置。</summary>
        public void Update(Vector3 centerPos)
        {
            m_TimeSinceCheck += Time.deltaTime;

            bool needRecompute = !m_StreamingEnabled
                || !m_HasLastPos
                || m_TimeSinceCheck >= m_Settings.streamingCheckInterval
                || Vector3.SqrMagnitude(centerPos - m_LastCheckPos) >= m_Settings.streamingMoveThreshold * m_Settings.streamingMoveThreshold;

            if (needRecompute)
            {
                m_TimeSinceCheck = 0f;
                m_LastCheckPos = centerPos;
                m_HasLastPos = true;
                RecomputeLoadSet(centerPos);
            }

            // PendingUnload 倒计时与执行(即使不重算集合也要走)
            TickPendingUnloads();

            // 从调度器抽预算, 执行加载/卸载
            m_Scheduler.Drain(m_Settings.maxLoadsPerFrame, m_Settings.maxUnloadsPerFrame, m_DrainLoads, m_DrainUnloads);
            for (int i = 0; i < m_DrainLoads.Count; i++)
            {
                var op = m_DrainLoads[i];
                if (!m_Slots.TryGetValue((op.ChunkX, op.ChunkZ), out Slot slot)) continue;
                // 防御: 排队期间等级可能已被更高优先级的操作改变, 过期请求直接丢弃
                if (slot.State.CurrentLevel >= op.Level) continue;
                slot.State.Status = ChunkStatus.Idle;
                SetLevel(slot, op.Level);
            }
            for (int i = 0; i < m_DrainUnloads.Count; i++)
            {
                var op = m_DrainUnloads[i];
                if (!m_Slots.TryGetValue((op.ChunkX, op.ChunkZ), out Slot slot)) continue;
                // 防御: 排队期间相机可能已折返, 等级被重新拉起, 过期卸载丢弃
                if (slot.State.CurrentLevel <= op.Level) continue;
                slot.State.Status = ChunkStatus.Idle;
                slot.State.UnloadTimer = 0f;
                SetLevel(slot, op.Level);
            }
        }

        /// <summary>
        /// 全量重算所有 Chunk 的目标等级(节流后才执行)。
        /// 加载判定用"加载环", 卸载判定用"卸载环(环半径 + 迟滞)" —— 两套阈值防抖。
        /// </summary>
        private void RecomputeLoadSet(Vector3 centerPos)
        {
            int centerCx = Mathf.FloorToInt(centerPos.x / m_HeightMap.ChunkWorldSize);
            int centerCz = Mathf.FloorToInt(centerPos.z / m_HeightMap.ChunkWorldSize);
            m_CenterChunk = (centerCx, centerCz);

            foreach (var kv in m_Slots)
            {
                Slot slot = kv.Value;
                var st = slot.State;
                float dist = ChebyshevChunkDist(slot.ChunkX, slot.ChunkZ, centerCx, centerCz);
                st.DistanceToCamera = dist;

                ChunkLoadLevel loadLevel = ComputeLoadLevel(dist);
                st.TargetLevel = loadLevel;

                if (loadLevel > st.CurrentLevel)
                {
                    // 需要升级: 立刻排队加载(取消可能存在的卸载倒计时)
                    if (st.Status == ChunkStatus.PendingUnload)
                    {
                        m_Scheduler.RemoveUnload(slot.ChunkX, slot.ChunkZ);
                        st.Status = ChunkStatus.Idle;
                        st.UnloadTimer = 0f;
                    }
                    // st.Status == PendingLoad 说明已排队, 不必重复提交
                    if (st.Status == ChunkStatus.Idle)
                    {
                        st.Status = ChunkStatus.PendingLoad;
                        m_Scheduler.SubmitLoad(slot.ChunkX, slot.ChunkZ, loadLevel, dist);
                    }
                }
                else if (loadLevel < st.CurrentLevel)
                {
                    // 需要降级: 用"卸载环"再确认一次(迟滞), 通过后才进入延迟卸载
                    ChunkLoadLevel unloadLevel = ComputeUnloadLevel(dist);
                    if (unloadLevel < st.CurrentLevel)
                    {
                        if (st.Status == ChunkStatus.Idle)
                        {
                            st.Status = ChunkStatus.PendingUnload;
                            st.UnloadTargetLevel = unloadLevel;
                            st.UnloadTimer = m_Settings.unloadDelaySeconds;
                        }
                        else if (st.Status == ChunkStatus.PendingUnload && unloadLevel < st.UnloadTargetLevel)
                        {
                            // 倒计时期间相机继续远离: 卸载目标再降一级, 倒计时重置
                            // (不重置会导致先卸载到旧目标, 下一轮才能再降一级, 多拖一轮)
                            st.UnloadTargetLevel = unloadLevel;
                            st.UnloadTimer = m_Settings.unloadDelaySeconds;
                        }
                    }
                    else
                    {
                        // 还在迟滞区内: 保持现状(不加载也不卸载)
                        st.Status = ChunkStatus.Idle;
                        st.UnloadTimer = 0f;
                    }
                }
                else
                {
                    // 等级不变: 清掉悬而未决的卸载
                    if (st.Status == ChunkStatus.PendingUnload)
                    {
                        m_Scheduler.RemoveUnload(slot.ChunkX, slot.ChunkZ);
                        st.Status = ChunkStatus.Idle;
                        st.UnloadTimer = 0f;
                    }
                }
            }
        }

        /// <summary>PendingUnload 倒计时到期 → 提交到卸载队列。</summary>
        private void TickPendingUnloads()
        {
            PendingUnloadCount = 0;
            foreach (var kv in m_Slots)
            {
                var st = kv.Value.State;
                if (st.Status != ChunkStatus.PendingUnload) continue;
                PendingUnloadCount++;
                st.UnloadTimer -= Time.deltaTime;
                if (st.UnloadTimer <= 0f)
                {
                    // 提交到卸载队列。保持 PendingUnload 状态: 若本帧预算不足,
                    // 条目留在队列里, 后续 Tick 会重复 SubmitUnload —— 调度器按等级去重, 幂等。
                    // Drain 执行 SetLevel 后才回到 Idle。
                    m_Scheduler.SubmitUnload(st.ChunkX, st.ChunkZ, st.UnloadTargetLevel, st.DistanceToCamera);
                }
            }
        }

        /// <summary>加载环判定(加载阈值)。dist 单位: Chunk 数(Chebyshev)。</summary>
        private ChunkLoadLevel ComputeLoadLevel(float dist)
        {
            if (dist <= m_Settings.nearRingChunks) return ChunkLoadLevel.TerrainFullGrass;
            if (dist <= m_Settings.midRingChunks) return ChunkLoadLevel.TerrainLowGrass;
            if (dist <= m_Settings.farRingChunks) return ChunkLoadLevel.TerrainOnly;
            return ChunkLoadLevel.Unloaded;
        }

        /// <summary>卸载环判定(卸载阈值 = 加载阈值 + 迟滞)。</summary>
        private ChunkLoadLevel ComputeUnloadLevel(float dist)
        {
            float hyst = m_Settings.unloadHysteresisChunks;
            if (dist <= m_Settings.nearRingChunks + hyst) return ChunkLoadLevel.TerrainFullGrass;
            if (dist <= m_Settings.midRingChunks + hyst) return ChunkLoadLevel.TerrainLowGrass;
            if (dist <= m_Settings.farRingChunks + hyst) return ChunkLoadLevel.TerrainOnly;
            return ChunkLoadLevel.Unloaded;
        }

        private static float ChebyshevChunkDist(int cx, int cz, int centerCx, int centerCz)
        {
            return Mathf.Max(Mathf.Abs(cx - centerCx), Mathf.Abs(cz - centerCz));
        }

        /// <summary>
        /// 把槽位切换到新等级。这是唯一修改等级的地方, 顺序保证(需求 2.8):
        ///   升级: 先地形(激活 GO / 挂 Mesh), 后草(回调通知草地层生成);
        ///   降级: 先草(回调通知草地层释放), 后地形(SetActive(false))。
        /// </summary>
        private void SetLevel(Slot slot, ChunkLoadLevel newLevel)
        {
            var st = slot.State;
            ChunkLoadLevel oldLevel = st.CurrentLevel;
            if (oldLevel == newLevel) return;

            if (newLevel > oldLevel)
            {
                // ---------- 升级: 先地形 ----------
                if (oldLevel == ChunkLoadLevel.Unloaded)
                {
                    EnsureBuilt(slot);
                    slot.Data.GameObject.SetActive(true);
                    m_Active.Add(slot.Data);
                }
                // ---------- 后草 ----------
                OnChunkLevelChanged?.Invoke(slot.Data, oldLevel, newLevel);
            }
            else
            {
                // ---------- 降级: 先草 ----------
                OnChunkLevelChanged?.Invoke(slot.Data, oldLevel, newLevel);
                // ---------- 后地形 ----------
                if (newLevel == ChunkLoadLevel.Unloaded)
                {
                    slot.Data.GameObject.SetActive(false);
                    m_Active.Remove(slot.Data);
                }
            }

            st.CurrentLevel = newLevel;
            st.Status = ChunkStatus.Idle;
            st.UnloadTimer = 0f;
            LoadedCount = m_Active.Count;
        }

        /// <summary>惰性构建 Chunk(Mesh + GameObject)。构建一次后常驻, 卸载只是隐藏。</summary>
        private void EnsureBuilt(Slot slot)
        {
            if (slot.Data != null) return;
            slot.Data = TerrainChunkMeshBuilder.BuildChunk(m_HeightMap, slot.ChunkX, slot.ChunkZ, m_Settings, m_GlobalUseful, m_TerrainMaterial, m_Parent);
            slot.Data.GameObject.SetActive(false); // 先造好但隐藏, 等 SetLevel 激活
            m_AllBuilt.Add(slot.Data);
        }

        /// <summary>替换全局"有用顶点"表(四叉树参数调整后重建地形用)。</summary>
        public void SetGlobalUseful(bool[] table)
        {
            m_GlobalUseful = table;
        }

        /// <summary>
        /// 对所有已加载(等级 ≥ TerrainLowGrass)的 Chunk 重新触发一次升级回调 ——
        /// 草地层会按回调重新生成(草形态切换后调用)。
        /// </summary>
        public void ReapplyLevels()
        {
            foreach (var kv in m_Slots)
            {
                Slot slot = kv.Value;
                if (slot.Data == null) continue;
                if (slot.State.CurrentLevel >= ChunkLoadLevel.TerrainLowGrass)
                {
                    OnChunkLevelChanged?.Invoke(slot.Data, ChunkLoadLevel.Unloaded, slot.State.CurrentLevel);
                }
            }
        }

        /// <summary>
        /// 重建全部地形: 所有 Chunk 降到 Unloaded(先草后地形), 销毁已构建的 Mesh/GO,
        /// 清空惰性缓存 —— 下一帧按当前设置(新四叉树参数)重新构建加载。
        /// </summary>
        public void RebuildAll()
        {
            foreach (var kv in m_Slots)
            {
                Slot slot = kv.Value;
                if (slot.State.CurrentLevel != ChunkLoadLevel.Unloaded)
                {
                    SetLevel(slot, ChunkLoadLevel.Unloaded); // 先草后地形, 走正常降级链
                }
                if (slot.Data != null)
                {
                    if (slot.Data.FullMesh != null) UnityEngine.Object.Destroy(slot.Data.FullMesh);
                    if (slot.Data.OptimizedMesh != null) UnityEngine.Object.Destroy(slot.Data.OptimizedMesh);
                    if (slot.Data.GameObject != null) UnityEngine.Object.Destroy(slot.Data.GameObject);
                    m_AllBuilt.Remove(slot.Data);
                    slot.Data = null; // 下次 EnsureBuilt 用新参数重建
                }
            }
            m_Active.Clear();
            LoadedCount = 0;
            m_HasLastPos = false; // 强制下一帧全量重算并按距离重新加载
        }

        public TerrainChunkData GetChunk(int cx, int cz)
        {
            return m_Slots.TryGetValue((cx, cz), out Slot slot) ? slot.Data : null;
        }

        public void Dispose()
        {
            m_Active.Clear();
            foreach (var kv in m_Slots)
            {
                var slot = kv.Value;
                if (slot.Data == null) continue;
                if (slot.Data.FullMesh != null) UnityEngine.Object.Destroy(slot.Data.FullMesh);
                if (slot.Data.OptimizedMesh != null) UnityEngine.Object.Destroy(slot.Data.OptimizedMesh);
                if (slot.Data.GameObject != null) UnityEngine.Object.Destroy(slot.Data.GameObject);
            }
            m_Slots.Clear();
            m_AllBuilt.Clear();
        }
    }
}
