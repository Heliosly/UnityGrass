using UnityEngine;

namespace TerrainGrassDemo.Interaction
{
    /// <summary>
    /// 一个草地交互体: 有位置、移动方向、影响半径和强度。
    /// Demo 里只有一个(玩家), 但控制器按列表处理 —— 结构上预留多交互体支持(需求 5.10)。
    /// </summary>
    public sealed class GrassInteractor
    {
        /// <summary>世界坐标(脚底)。</summary>
        public Vector3 Position;

        /// <summary>移动速度(m/s), xy 分量被当作倒伏方向。</summary>
        public Vector3 Velocity;

        /// <summary>笔刷半径(米)。</summary>
        public float Radius = 1.8f;

        /// <summary>笔刷强度 0..1。</summary>
        public float Strength = 1f;
    }
}
