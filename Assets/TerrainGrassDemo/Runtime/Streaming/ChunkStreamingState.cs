namespace TerrainGrassDemo.Streaming
{
    /// <summary>
    /// Chunk 的加载等级(距离环)。
    /// 等级越高 = 离相机越近 = 内容越完整。
    /// </summary>
    public enum ChunkLoadLevel
    {
        /// <summary>范围外: 全部卸载(GameObject 回收, 草 Buffer 释放)。</summary>
        Unloaded = 0,
        /// <summary>远环: 只有地形 Mesh, 没有草。</summary>
        TerrainOnly = 1,
        /// <summary>中环: 地形 + 低密度草。</summary>
        TerrainLowGrass = 2,
        /// <summary>近环: 地形 + 满密度草。</summary>
        TerrainFullGrass = 3,
    }

    /// <summary>Chunk 在调度器里的状态。</summary>
    public enum ChunkStatus
    {
        /// <summary>已稳定在当前等级, 无事发生。</summary>
        Idle = 0,
        /// <summary>已进入加载队列, 等待本帧或后续帧生成。</summary>
        PendingLoad = 1,
        /// <summary>等待卸载倒计时(延迟卸载), 倒计时结束前随时可被取消。</summary>
        PendingUnload = 2,
    }

    /// <summary>
    /// 单个 Chunk 的运行时状态记录。Chunk 数据(Mesh)本身在 TerrainChunkData 里。
    /// </summary>
    public sealed class ChunkRuntimeState
    {
        public int ChunkX;
        public int ChunkZ;
        public ChunkLoadLevel CurrentLevel = ChunkLoadLevel.Unloaded;
        public ChunkLoadLevel TargetLevel = ChunkLoadLevel.Unloaded;   // 最近一次重算的结果
        public ChunkLoadLevel UnloadTargetLevel = ChunkLoadLevel.Unloaded; // 进入 PendingUnload 时锁定的卸载目标
        public ChunkStatus Status = ChunkStatus.Idle;
        public float UnloadTimer;          // PendingUnload 倒计时(秒)
        public float DistanceToCamera;     // 最近一次重算时的距离(供排序和调试面板)
    }
}
