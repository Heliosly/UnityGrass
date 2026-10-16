using System;
using System.Collections.Generic;

class GenVerify
{
    struct Rng { public uint s; public Rng(int seed) { s = (uint)seed; } public float Next01() { s = s * 1664525u + 1013904223u; return (s >> 8) / 16777216.0f; } }

    static float Hash2(int x, int z, uint seed)
    {
        uint h = seed;
        h ^= (uint)x * 0x27d4eb2du;
        h ^= (uint)z * 0x165667b1u;
        h = (h ^ (h >> 15)) * 0x85ebca6bu;
        h = (h ^ (h >> 13)) * 0xc2b2ae35u;
        h ^= h >> 16;
        return (h & 0xFFFFFFu) / 16777216.0f;
    }

    static float ValueNoise(float x, float z, uint seed)
    {
        int ix = (int)MathF.Floor(x), iz = (int)MathF.Floor(z);
        float fx = x - ix, fz = z - iz;
        float h00 = Hash2(ix, iz, seed), h10 = Hash2(ix + 1, iz, seed);
        float h01 = Hash2(ix, iz + 1, seed), h11 = Hash2(ix + 1, iz + 1, seed);
        fx = fx * fx * (3f - 2f * fx);
        fz = fz * fz * (3f - 2f * fz);
        float a = h00 + (h10 - h00) * fx;
        float b = h01 + (h11 - h01) * fx;
        return a + (b - a) * fz;
    }

    static uint GetChunkSeed(int chunkX, int chunkZ, uint worldSeed)
    {
        uint h = worldSeed ^ (uint)chunkX * 0x9E3779B1u ^ (uint)chunkZ * 0x85EBCA6Bu;
        h = (h ^ (h >> 13)) * 0xC2B2AE35u;
        h ^= h >> 16;
        return h;
    }

    static void Main()
    {
        // 复刻当前 Generate 的 High 档核心随机序列: 每格最多 12 实例。
        const int res = 32; const float cell = 1f;
        uint chunkSeed = GetChunkSeed(3, 5, 12345u);
        float originX = 3 * 32f, originZ = 5 * 32f;
        int total = 0;
        var positions = new Dictionary<long, int>(); // 位置量化 1mm → 计数
        int bigClumpCells = 0, fineCells = 0;
        for (int gz = 0; gz < res; gz++)
        for (int gx = 0; gx < res; gx++)
        {
            float cx = originX + (gx + 0.5f) * cell;
            float cz = originZ + (gz + 0.5f) * cell;
            float n = ValueNoise(cx * 0.125f, cz * 0.125f, 12345u);
            if (n < 0.08f) continue;
            float densityScale = 0.55f + n * 0.85f;

            bool cellHasBigClump = false;
            for (int b = 0; b < 12; b++)
            {
                var rng = new Rng((int)(chunkSeed + (uint)gx * 73856093u + (uint)gz * 19349663u + (uint)b * 2654435761u));
                bool isBigClump = b == 0 && rng.Next01() > 0.91f;
                if (rng.Next01() > densityScale) continue;
                if (isBigClump) cellHasBigClump = true;
                float jitter = isBigClump ? 0.55f : 1.75f;
                float jx = (rng.Next01() - 0.5f) * jitter;
                float jz = (rng.Next01() - 0.5f) * jitter;
                float wx = cx + jx * cell;
                float wz = cz + jz * cell;
                long key = (long)Math.Round(wx * 1000) * 10000000L + (long)Math.Round(wz * 1000);
                positions.TryGetValue(key, out int c);
                positions[key] = c + 1;
                total++;
            }
            if (cellHasBigClump) bigClumpCells++; else fineCells++;
        }
        // 统计
        int dup = 0, maxDup = 1, dupPositions = 0;
        foreach (var kv in positions) { if (kv.Value > 1) { dup += kv.Value - 1; dupPositions++; if (kv.Value > maxDup) maxDup = kv.Value; } }
        Console.WriteLine($"总实例: {total}  唯一位置: {positions.Count}  重复实例: {dup}  重复位置数: {dupPositions}  最大同点: {maxDup}");
        Console.WriteLine($"大草丛格: {bigClumpCells}  细草格: {fineCells}");
        // 近距检查(2cm 内 = 视觉重叠)
        int closePairs = 0;
        var keys = new List<long>(positions.Keys);
        for (int i = 0; i < keys.Count; i++)
            for (int j = i + 1; j < keys.Count; j++)
            {
                long a = keys[i], b = keys[j];
                float ax = (a / 10000000L) / 1000f, az = (a % 10000000L) / 1000f;
                float bx = (b / 10000000L) / 1000f, bz = (b % 10000000L) / 1000f;
                float dx = ax - bx, dz = az - bz;
                if (dx * dx + dz * dz < 0.02f * 0.02f) closePairs++;
            }
        Console.WriteLine($"2cm 内近距草对数: {closePairs}");
    }
}



