using System;
using System.Collections.Generic;
using TerrainGrassDemo.Terrain;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerrainGrassDemo
{
    /// <summary>
    /// 少量环境点缀的现代实例化渲染层。石块与花头各自合并为一次
    /// Graphics.RenderMeshInstanced 调用，不创建数百个 GameObject。
    /// </summary>
    public sealed class MeadowDecorRenderer : IDisposable
    {
        private readonly Matrix4x4[] m_Rocks;
        private readonly Matrix4x4[] m_FlowersPink;
        private readonly Matrix4x4[] m_FlowersGold;
        private readonly Matrix4x4[] m_Motes;
        private readonly Matrix4x4[] m_Crystals;
        private readonly Matrix4x4[] m_TreeTrunks;
        private readonly Matrix4x4[] m_TreeCanopies;
        private readonly Matrix4x4[] m_Portal;
        private readonly Mesh m_RockMesh;
        private readonly Mesh m_FlowerMesh;
        private readonly Mesh m_OctahedronMesh;
        private readonly Mesh m_TrunkMesh;
        private readonly Mesh m_CanopyMesh;
        private readonly Mesh m_PortalMesh;
        private readonly Material m_RockMaterial;
        private readonly Material m_FlowerPinkMaterial;
        private readonly Material m_FlowerGoldMaterial;
        private readonly Material m_MoteMaterial;
        private readonly Material m_CrystalMaterial;
        private readonly Material m_TrunkMaterial;
        private readonly Material m_CanopyMaterial;
        private readonly Material m_PortalMaterial;
        private RenderParams m_RockParams;
        private RenderParams m_FlowerPinkParams;
        private RenderParams m_FlowerGoldParams;
        private RenderParams m_MoteParams;
        private RenderParams m_CrystalParams;
        private RenderParams m_TrunkParams;
        private RenderParams m_CanopyParams;
        private RenderParams m_PortalParams;

        public int RockCount => m_Rocks.Length;
        public int FlowerCount => m_FlowersPink.Length + m_FlowersGold.Length;

        public MeadowDecorRenderer(HeightMapData heightMap, TerrainGrassDemoSettings settings)
        {
            m_RockMesh = CreateRockMesh();
            m_FlowerMesh = CreateFlowerHeadMesh();
            m_OctahedronMesh = CreateOctahedronMesh();
            m_TrunkMesh = CreateTrunkMesh();
            m_CanopyMesh = CreateCanopyMesh();
            m_PortalMesh = CreatePortalRingMesh();
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");

            m_RockMaterial = CreateMaterial(shader, "Meadow Stones", new Color(0.34f, 0.42f, 0.46f, 1f), 0.16f);
            m_FlowerPinkMaterial = CreateMaterial(shader, "Rose Fairy Flowers", new Color(1.0f, 0.40f, 0.76f, 1f), 0.28f);
            m_FlowerGoldMaterial = CreateMaterial(shader, "Golden Fairy Flowers", new Color(1.0f, 0.82f, 0.28f, 1f), 0.25f);
            m_MoteMaterial = CreateMaterial(shader, "Aether Motes", new Color(0.46f, 0.95f, 1.0f, 1f), 0.55f,
                new Color(0.25f, 1.4f, 1.8f, 1f));
            m_CrystalMaterial = CreateMaterial(shader, "Lavender Crystals", new Color(0.62f, 0.55f, 0.90f, 1f), 0.62f,
                new Color(0.12f, 0.07f, 0.28f, 1f));
            m_TrunkMaterial = CreateMaterial(shader, "Silver Birch Trunks", new Color(0.34f, 0.45f, 0.39f, 1f), 0.18f);
            m_CanopyMaterial = CreateMaterial(shader, "Aether Willow Canopies", new Color(0.19f, 0.52f, 0.37f, 1f), 0.12f,
                new Color(0.012f, 0.045f, 0.027f, 1f));
            m_PortalMaterial = CreateMaterial(shader, "Sunlit Aether Gate", new Color(0.80f, 0.70f, 0.46f, 1f), 0.48f,
                new Color(1.15f, 0.62f, 0.18f, 1f));
            if (m_FlowerPinkMaterial.HasProperty("_Cull")) m_FlowerPinkMaterial.SetFloat("_Cull", 0f);
            if (m_FlowerGoldMaterial.HasProperty("_Cull")) m_FlowerGoldMaterial.SetFloat("_Cull", 0f);

            GeneratePlacements(heightMap, settings, out m_Rocks, out m_FlowersPink, out m_FlowersGold,
                out m_Motes, out m_Crystals, out m_TreeTrunks, out m_TreeCanopies, out m_Portal);
            float mapX = heightMap.ChunksX * heightMap.ChunkWorldSize;
            float mapZ = heightMap.ChunksZ * heightMap.ChunkWorldSize;
            var bounds = new Bounds(new Vector3(mapX * 0.5f, 20f, mapZ * 0.5f), new Vector3(mapX, 80f, mapZ));

            m_RockParams = new RenderParams(m_RockMaterial)
            {
                worldBounds = bounds,
                shadowCastingMode = ShadowCastingMode.On,
                receiveShadows = true
            };
            m_FlowerPinkParams = new RenderParams(m_FlowerPinkMaterial)
            {
                worldBounds = bounds,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = true
            };
            m_FlowerGoldParams = new RenderParams(m_FlowerGoldMaterial)
            {
                worldBounds = bounds,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = true
            };
            m_MoteParams = new RenderParams(m_MoteMaterial)
            {
                worldBounds = bounds,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false
            };
            m_CrystalParams = new RenderParams(m_CrystalMaterial)
            {
                worldBounds = bounds,
                shadowCastingMode = ShadowCastingMode.On,
                receiveShadows = true
            };
            m_TrunkParams = new RenderParams(m_TrunkMaterial)
            {
                worldBounds = bounds,
                shadowCastingMode = ShadowCastingMode.On,
                receiveShadows = true
            };
            m_CanopyParams = new RenderParams(m_CanopyMaterial)
            {
                worldBounds = bounds,
                shadowCastingMode = ShadowCastingMode.On,
                receiveShadows = true
            };
            m_PortalParams = new RenderParams(m_PortalMaterial)
            {
                worldBounds = bounds,
                shadowCastingMode = ShadowCastingMode.On,
                receiveShadows = true
            };
        }

        public void Render(Camera camera)
        {
            m_RockParams.camera = camera;
            m_FlowerPinkParams.camera = camera;
            m_FlowerGoldParams.camera = camera;
            m_MoteParams.camera = camera;
            m_CrystalParams.camera = camera;
            m_TrunkParams.camera = camera;
            m_CanopyParams.camera = camera;
            m_PortalParams.camera = camera;
            if (m_Rocks.Length > 0)
                Graphics.RenderMeshInstanced(m_RockParams, m_RockMesh, 0, m_Rocks, m_Rocks.Length);
            if (m_FlowersPink.Length > 0)
                Graphics.RenderMeshInstanced(m_FlowerPinkParams, m_FlowerMesh, 0, m_FlowersPink, m_FlowersPink.Length);
            if (m_FlowersGold.Length > 0)
                Graphics.RenderMeshInstanced(m_FlowerGoldParams, m_FlowerMesh, 0, m_FlowersGold, m_FlowersGold.Length);
            if (m_Motes.Length > 0)
                Graphics.RenderMeshInstanced(m_MoteParams, m_OctahedronMesh, 0, m_Motes, m_Motes.Length);
            if (m_Crystals.Length > 0)
                Graphics.RenderMeshInstanced(m_CrystalParams, m_OctahedronMesh, 0, m_Crystals, m_Crystals.Length);
            if (m_TreeTrunks.Length > 0)
                Graphics.RenderMeshInstanced(m_TrunkParams, m_TrunkMesh, 0, m_TreeTrunks, m_TreeTrunks.Length);
            if (m_TreeCanopies.Length > 0)
                Graphics.RenderMeshInstanced(m_CanopyParams, m_CanopyMesh, 0, m_TreeCanopies, m_TreeCanopies.Length);
            if (m_Portal.Length > 0)
                Graphics.RenderMeshInstanced(m_PortalParams, m_PortalMesh, 0, m_Portal, m_Portal.Length);
        }

        private static void GeneratePlacements(HeightMapData hm, TerrainGrassDemoSettings settings,
            out Matrix4x4[] rocks, out Matrix4x4[] flowersPink, out Matrix4x4[] flowersGold,
            out Matrix4x4[] motes, out Matrix4x4[] crystals,
            out Matrix4x4[] treeTrunks, out Matrix4x4[] treeCanopies, out Matrix4x4[] portal)
        {
            var rockList = new List<Matrix4x4>(90);
            var pinkList = new List<Matrix4x4>(320);
            var goldList = new List<Matrix4x4>(220);
            var moteList = new List<Matrix4x4>(160);
            var crystalList = new List<Matrix4x4>(6);
            var trunkList = new List<Matrix4x4>(12);
            var canopyList = new List<Matrix4x4>(36);
            var random = new System.Random(unchecked((int)settings.worldSeed ^ 0x4D454144));
            float centerX = hm.ChunksX * hm.ChunkWorldSize * 0.5f;
            float centerZ = hm.ChunksZ * hm.ChunkWorldSize * 0.5f;

            for (int i = 0; i < 1600 && (rockList.Count < 90 || pinkList.Count < 320 || goldList.Count < 220); i++)
            {
                float z = centerZ + Mathf.Lerp(-115f, 150f, (float)random.NextDouble());
                float pathX = centerX + Mathf.Sin((z - centerZ) * settings.meadowPathFrequency) * settings.meadowPathAmplitude;
                float side = random.NextDouble() < 0.5 ? -1f : 1f;
                float edgeDistance = Mathf.Lerp(settings.meadowPathHalfWidth + 0.8f, 18f,
                    Mathf.Pow((float)random.NextDouble(), 1.7f));
                float x = pathX + side * edgeDistance + Mathf.Lerp(-1.2f, 1.2f, (float)random.NextDouble());
                if (x < 2f || x > hm.ChunksX * hm.ChunkWorldSize - 2f) continue;
                float y = hm.SampleHeight(x, z);

                if (rockList.Count < 90 && random.NextDouble() < 0.17)
                {
                    float scale = Mathf.Lerp(0.35f, 1.15f, Mathf.Pow((float)random.NextDouble(), 2f));
                    var rotation = Quaternion.Euler(Mathf.Lerp(-8f, 8f, (float)random.NextDouble()),
                        (float)random.NextDouble() * 360f, Mathf.Lerp(-8f, 8f, (float)random.NextDouble()));
                    rockList.Add(Matrix4x4.TRS(new Vector3(x, y + scale * 0.18f, z), rotation,
                        new Vector3(scale * 1.35f, scale * 0.72f, scale)));
                }

                if ((pinkList.Count < 320 || goldList.Count < 220) && random.NextDouble() < 0.74)
                {
                    // 三朵小花组成不规则簇；远处形成色点，近处仍有几何轮廓。
                    int cluster = 1 + random.Next(3);
                    for (int f = 0; f < cluster; f++)
                    {
                        float fx = x + Mathf.Lerp(-0.45f, 0.45f, (float)random.NextDouble());
                        float fz = z + Mathf.Lerp(-0.45f, 0.45f, (float)random.NextDouble());
                        float fy = hm.SampleHeight(fx, fz) + Mathf.Lerp(0.42f, 0.85f, (float)random.NextDouble());
                        float scale = Mathf.Lerp(0.65f, 1.2f, (float)random.NextDouble());
                        var matrix = Matrix4x4.TRS(new Vector3(fx, fy, fz),
                            Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 0f), Vector3.one * scale);
                        if (random.NextDouble() < 0.62 && pinkList.Count < 320) pinkList.Add(matrix);
                        else if (goldList.Count < 220) goldList.Add(matrix);
                    }
                }
            }

            for (int i = 0; i < 160; i++)
            {
                float z = centerZ + Mathf.Lerp(-45f, 120f, (float)random.NextDouble());
                float pathX = centerX + Mathf.Sin((z - centerZ) * settings.meadowPathFrequency) * settings.meadowPathAmplitude;
                float x = pathX + Mathf.Lerp(-28f, 28f, (float)random.NextDouble());
                float y = hm.SampleHeight(x, z) + Mathf.Lerp(0.65f, 2.8f, (float)random.NextDouble());
                float scale = Mathf.Lerp(0.035f, 0.085f, (float)random.NextDouble());
                moteList.Add(Matrix4x4.TRS(new Vector3(x, y, z), Quaternion.identity, Vector3.one * scale));
            }

            for (int i = 0; i < 6; i++)
            {
                float z = centerZ + Mathf.Lerp(58f, 128f, i / 5f) + Mathf.Lerp(-4f, 4f, (float)random.NextDouble());
                float pathX = centerX + Mathf.Sin((z - centerZ) * settings.meadowPathFrequency) * settings.meadowPathAmplitude;
                float side = (i & 1) == 0 ? -1f : 1f;
                float x = pathX + side * Mathf.Lerp(7f, 22f, (float)random.NextDouble());
                float y = hm.SampleHeight(x, z);
                float height = Mathf.Lerp(0.65f, 1.45f, Mathf.Pow((float)random.NextDouble(), 1.35f));
                crystalList.Add(Matrix4x4.TRS(new Vector3(x, y + height * 0.45f, z),
                    Quaternion.Euler(Mathf.Lerp(-10f, 10f, (float)random.NextDouble()),
                        (float)random.NextDouble() * 360f, Mathf.Lerp(-12f, 12f, (float)random.NextDouble())),
                    new Vector3(height * 0.15f, height, height * 0.15f)));
            }

            // 远景树群承担构图框景：树干和三团树冠分别实例化，仅增加两个 draw call。
            // 分布在道路两侧且避开中心视线，给草原一个明确的纵深终点。
            for (int i = 0; i < 12; i++)
            {
                float z = centerZ + Mathf.Lerp(66f, 150f, (float)random.NextDouble());
                float pathX = centerX + Mathf.Sin((z - centerZ) * settings.meadowPathFrequency) * settings.meadowPathAmplitude;
                float side = (i & 1) == 0 ? -1f : 1f;
                float x = pathX + side * Mathf.Lerp(22f, 52f, (float)random.NextDouble());
                float y = hm.SampleHeight(x, z);
                float height = Mathf.Lerp(5.8f, 10.5f, (float)random.NextDouble());
                float crown = Mathf.Lerp(2.8f, 4.8f, (float)random.NextDouble());
                var yaw = Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 0f);
                trunkList.Add(Matrix4x4.TRS(new Vector3(x, y, z), yaw,
                    new Vector3(height * 0.075f, height, height * 0.075f)));
                for (int c = 0; c < 3; c++)
                {
                    float angle = c * 2.0944f + (float)random.NextDouble() * 0.7f;
                    Vector3 offset = new Vector3(Mathf.Cos(angle), Mathf.Lerp(-0.15f, 0.45f, (float)random.NextDouble()),
                        Mathf.Sin(angle)) * crown * 0.38f;
                    var scale = new Vector3(crown * Mathf.Lerp(0.78f, 1.15f, (float)random.NextDouble()),
                        crown * Mathf.Lerp(0.85f, 1.3f, (float)random.NextDouble()),
                        crown * Mathf.Lerp(0.78f, 1.15f, (float)random.NextDouble()));
                    canopyList.Add(Matrix4x4.TRS(new Vector3(x, y + height * 0.86f, z) + offset,
                        Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 0f), scale));
                }
            }

            rocks = rockList.ToArray();
            flowersPink = pinkList.ToArray();
            flowersGold = goldList.ToArray();
            motes = moteList.ToArray();
            crystals = crystalList.ToArray();
            treeTrunks = trunkList.ToArray();
            treeCanopies = canopyList.ToArray();
            float portalZ = centerZ + 112f;
            float portalX = centerX + Mathf.Sin((portalZ - centerZ) * settings.meadowPathFrequency) * settings.meadowPathAmplitude;
            float portalY = hm.SampleHeight(portalX, portalZ);
            portal = new[] { Matrix4x4.TRS(new Vector3(portalX, portalY + 5.2f, portalZ), Quaternion.identity, Vector3.one) };
        }

        private static Material CreateMaterial(Shader shader, string materialName, Color color, float smoothness,
            Color? emission = null)
        {
            var material = new Material(shader) { name = materialName, enableInstancing = true };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            if (emission.HasValue && material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", emission.Value);
            }
            return material;
        }

        private static Mesh CreateRockMesh()
        {
            var mesh = new Mesh { name = "Meadow Rock (Instanced)" };
            mesh.vertices = new[]
            {
                new Vector3(-0.52f, 0f, -0.40f), new Vector3(0.48f, 0.02f, -0.44f),
                new Vector3(0.55f, 0f, 0.36f), new Vector3(-0.46f, 0.03f, 0.48f),
                new Vector3(-0.22f, 0.62f, -0.18f), new Vector3(0.28f, 0.55f, -0.16f),
                new Vector3(0.24f, 0.50f, 0.22f), new Vector3(-0.20f, 0.58f, 0.25f)
            };
            mesh.triangles = new[]
            {
                0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7,
                4,5,6, 4,6,7, 3,2,1, 3,1,0
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateFlowerHeadMesh()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            const int petals = 6;
            vertices.Add(Vector3.zero);
            for (int i = 0; i < petals; i++)
            {
                float angle = i * Mathf.PI * 2f / petals;
                float radius = (i & 1) == 0 ? 0.16f : 0.12f;
                vertices.Add(new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle * 2f) * 0.025f,
                    Mathf.Sin(angle) * radius));
            }
            for (int i = 0; i < petals; i++)
            {
                int next = (i + 1) % petals;
                triangles.Add(0); triangles.Add(i + 1); triangles.Add(next + 1);
            }
            var mesh = new Mesh { name = "Meadow Flower Heads (Instanced)" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            var normals = new Vector3[vertices.Count];
            for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            mesh.normals = normals;
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateOctahedronMesh()
        {
            var mesh = new Mesh { name = "Aether Octahedron (Instanced)" };
            mesh.vertices = new[]
            {
                Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back
            };
            mesh.triangles = new[]
            {
                0,4,3, 0,3,5, 0,5,2, 0,2,4,
                1,3,4, 1,5,3, 1,2,5, 1,4,2
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateTrunkMesh()
        {
            const int sides = 7;
            var vertices = new Vector3[sides * 2];
            var triangles = new int[sides * 6];
            for (int i = 0; i < sides; i++)
            {
                float a = i * Mathf.PI * 2f / sides;
                vertices[i] = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                vertices[i + sides] = new Vector3(Mathf.Cos(a) * 0.58f, 1f, Mathf.Sin(a) * 0.58f);
                int n = (i + 1) % sides;
                int t = i * 6;
                triangles[t] = i; triangles[t + 1] = n; triangles[t + 2] = i + sides;
                triangles[t + 3] = n; triangles[t + 4] = n + sides; triangles[t + 5] = i + sides;
            }
            var mesh = new Mesh { name = "Aether Tree Trunk (Instanced)", vertices = vertices, triangles = triangles };
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateCanopyMesh()
        {
            // 闭合的低模椭球，比八面体更柔和，但仍保留适合作品集的 stylized 切面。
            const int rings = 7;
            const int sides = 12;
            var vertices = new List<Vector3>((rings + 1) * sides);
            var triangles = new List<int>(rings * sides * 6);
            for (int r = 0; r <= rings; r++)
            {
                float v = r / (float)rings;
                float theta = v * Mathf.PI;
                float radius = Mathf.Sin(theta);
                float y = Mathf.Cos(theta);
                for (int s = 0; s < sides; s++)
                {
                    float u = s / (float)sides;
                    float angle = u * Mathf.PI * 2f;
                    float organic = 1f + Mathf.Sin(angle * 3f + r * 1.7f) * 0.055f;
                    vertices.Add(new Vector3(Mathf.Cos(angle) * radius * organic,
                        y - Mathf.Pow(Mathf.Max(0f, -y), 2f) * 0.12f,
                        Mathf.Sin(angle) * radius * organic));
                    if (r == rings) continue;
                    int next = (s + 1) % sides;
                    int row = r * sides;
                    int nextRow = (r + 1) * sides;
                    triangles.Add(row + s); triangles.Add(nextRow + next); triangles.Add(row + next);
                    triangles.Add(row + s); triangles.Add(nextRow + s); triangles.Add(nextRow + next);
                }
            }
            var mesh = new Mesh
            {
                name = "Aether Tree Canopy (Instanced)",
                vertices = vertices.ToArray(),
                triangles = triangles.ToArray()
            };
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreatePortalRingMesh()
        {
            const int ringSegments = 40;
            const int tubeSegments = 8;
            const float radius = 4.6f;
            const float tube = 0.28f;
            var vertices = new Vector3[ringSegments * tubeSegments];
            var triangles = new int[ringSegments * tubeSegments * 6];
            for (int r = 0; r < ringSegments; r++)
            {
                float a = r * Mathf.PI * 2f / ringSegments;
                Vector3 radial = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                Vector3 center = radial * radius;
                for (int t = 0; t < tubeSegments; t++)
                {
                    float b = t * Mathf.PI * 2f / tubeSegments;
                    vertices[r * tubeSegments + t] = center + radial * (Mathf.Cos(b) * tube) + Vector3.forward * (Mathf.Sin(b) * tube);
                    int rn = (r + 1) % ringSegments;
                    int tn = (t + 1) % tubeSegments;
                    int q = (r * tubeSegments + t) * 6;
                    triangles[q] = r * tubeSegments + t;
                    triangles[q + 1] = rn * tubeSegments + t;
                    triangles[q + 2] = rn * tubeSegments + tn;
                    triangles[q + 3] = r * tubeSegments + t;
                    triangles[q + 4] = rn * tubeSegments + tn;
                    triangles[q + 5] = r * tubeSegments + tn;
                }
            }
            var mesh = new Mesh { name = "Aether Gate Ring", vertices = vertices, triangles = triangles };
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }

        public void Dispose()
        {
            DestroyObject(m_RockMesh);
            DestroyObject(m_FlowerMesh);
            DestroyObject(m_OctahedronMesh);
            DestroyObject(m_TrunkMesh);
            DestroyObject(m_CanopyMesh);
            DestroyObject(m_PortalMesh);
            DestroyObject(m_RockMaterial);
            DestroyObject(m_FlowerPinkMaterial);
            DestroyObject(m_FlowerGoldMaterial);
            DestroyObject(m_MoteMaterial);
            DestroyObject(m_CrystalMaterial);
            DestroyObject(m_TrunkMaterial);
            DestroyObject(m_CanopyMaterial);
            DestroyObject(m_PortalMaterial);
        }

        private static void DestroyObject(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
            else UnityEngine.Object.DestroyImmediate(obj);
        }
    }
}
