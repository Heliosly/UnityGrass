using System.Runtime.InteropServices;
using UnityEngine;

namespace TerrainGrassDemo.Grass
{
    /// <summary>
    /// 单根草的实例数据 —— CPU 生成、写进 ComputeBuffer、由 ComputeShader 裁剪、
    /// 由顶点 Shader 消费。只保存 5 类信息(需求 3.2: 位置/旋转/尺寸/类型/随机种子):
    ///
    ///   position : 草根世界坐标(已贴地, 含高度采样结果)
    ///   yaw      : 绕 Y 轴朝向(弧度)
    ///   height   : 草高(米), 决定顶点拉伸
    ///   width    : 草宽(米)
    ///   type01   : 类型混合系数 [0,1], 决定颜色(未来可扩展成纹理图集索引)
    ///   seed01   : 随机种子 [0,1], 决定风的相位 → 每根草摆动不同步
    ///
    /// 布局约束: C# 端 32 字节, 必须与 GrassCulling.compute / GrassIndirect.shader 里的
    /// HLSL struct 逐字段一致。float3 在 HLSL StructuredBuffer 中对齐到 16 字节边界,
    /// C# 端 Vector3 是 12 字节紧跟 4 字节 float —— 两者恰好都排成 32 字节, 无需手动 padding。
    /// (若增加字段, 先在这里改, 再去 compute/shader 同步改)
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GrassInstanceData
    {
        public Vector3 position;
        public float yaw;
        public float height;
        public float width;
        public float type01;
        public float seed01;

        public const int SizeBytes = 32;
    }
}
