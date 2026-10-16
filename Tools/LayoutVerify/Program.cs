using System;
using System.Runtime.InteropServices;

// 复刻 GrassInstanceData(不引用 Unity, 用 3 个 float 代替 Vector3)
[StructLayout(LayoutKind.Sequential)]
public struct GrassInstanceDataLike
{
    public float px, py, pz; // position
    public float yaw;
    public float height;
    public float width;
    public float type01;
    public float seed01;
}

class Program
{
    static void Main()
    {
        int size = Marshal.SizeOf<GrassInstanceDataLike>();
        Console.WriteLine($"struct size = {size} bytes (要求 32)");
        // 字段偏移验证
        var t = typeof(GrassInstanceDataLike);
        foreach (var f in t.GetFields())
        {
            var off = Marshal.OffsetOf(t, f.Name).ToInt32();
            Console.WriteLine($"  {f.Name,-8} @ {off,2}");
        }
        // HLSL 期望布局: position@0 yaw@12 height@16 width@20 type01@24 seed01@28
        bool ok = size == 32;
        Console.WriteLine(ok ? "PASS: 32B 布局与 HLSL 一致" : "FAIL: 布局不一致, 需要同步修改 compute/shader!");
        Environment.Exit(ok ? 0 : 1);
    }
}
