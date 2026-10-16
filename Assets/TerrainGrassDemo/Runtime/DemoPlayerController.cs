using TerrainGrassDemo.Interaction;
using TerrainGrassDemo.Terrain;
using UnityEngine;

namespace TerrainGrassDemo
{
    /// <summary>
    /// 玩家控制器 + 第三人称相机。
    ///
    /// 行走方案: QnMobile 式"高度图贴地", 不用 CharacterController/重力。
    ///   每帧: 输入 → 水平位移 → 采样高度图得到目标高度 → y 直接设为地表高度。
    ///   玩家坐标永远是"地表高度 + 站立偏移", 从结构上不存在"穿透/自由落体"状态,
    ///   也就不需要任何"回拉纠正"(上一版 SimpleMove+穿透恢复的观感问题根源)。
    ///
    /// 地形行走规则(参考 QnMobile CoreScene 的相邻格判定):
    ///   - 目标高度 - 当前高度 > maxClimbHeight → 爬不上去, 挡下(等效"台阶上限");
    ///   - 当前高度 - 目标高度 > maxFallHeight → 悬崖, 挡下(等效"可掉落高度上限");
    ///   - 完整位移被挡时, 尝试"只走 X 轴 / 只走 Z 轴"(轴分离) → 沿悬崖/坡脚滑动而非卡死。
    ///
    /// 相机: 默认第三人称跟随(玩家后上方), 右键拖拽旋转; 按 F 切换自由飞行相机。
    /// </summary>
    public class DemoPlayerController : MonoBehaviour
    {
        public float moveSpeed = 4.5f;  // 散步速度(9m/s 冲刺太快, 草倒伏/回弹效果一晃而过不易观察)
        public float maxClimbHeight = 0.6f;   // 单步可上高度(米), 超过视为墙/陡坎
        public float maxFallHeight = 5f;      // 单步可下落高度(米), 超过视为悬崖
        public float cameraDistance = 10.5f;
        public float cameraHeight = 1.45f;    // 视线焦点高度
        public float cameraPitch = 18f;       // 低机位保留地平线与天空，适合作品集展示
        public float cameraLookSpeed = 3f;
        public float freeFlySpeed = 24f;

        private HeightMapData m_HeightMap;
        private GrassInteractor m_Interactor;
        private Vector3 m_LastPosition;
        private Vector2 m_CameraRotation;
        private Vector3 m_CameraVelocity;     // SmoothDamp 跟随用
        private Vector3 m_SimulatedMove; // 冒烟测试注入的移动方向(编辑器工具用)
        private bool m_FreeFly;
        private const float StandOffset = 0.1f; // 脚底离地高度(视觉胶囊中心在 +1, 贴地后刚好站住)

        public GrassInteractor Interactor => m_Interactor;

        /// <summary>冒烟测试专用: 注入一帧的世界空间移动方向(等效按住 WASD)。</summary>
        public void SimulateMove(Vector3 worldDir)
        {
            m_SimulatedMove = worldDir;
        }

        public void Initialize(HeightMapData heightMap, Vector3 spawnPosition)
        {
            m_HeightMap = heightMap;

            // 出生即贴地: 直接采样地表高度, 不经过任何"先落下"过程
            float h = heightMap.SampleHeight(spawnPosition.x, spawnPosition.z);
            transform.position = new Vector3(spawnPosition.x, h + StandOffset, spawnPosition.z);
            m_LastPosition = transform.position;
            m_Interactor = new GrassInteractor { Radius = 1.8f, Strength = 1f };

            m_CameraRotation = new Vector2(0f, cameraPitch); // yaw, pitch

            // 相机初始位置直接摆到玩家身后, 避免从原点 Lerp 过来期间
            // 流式加载仍以远处相机为中心(导致玩家脚下 Chunk 迟迟不加载)
            Camera cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = ComputeCameraTarget();
                cam.transform.LookAt(transform.position + Vector3.up * cameraHeight);
            }
        }

        /// <summary>
        /// 标准第三人称相机目标位置:
        ///   1. 固定偏移: 玩家位置 + yaw/pitch 旋转的 (0, cameraHeight, -cameraDistance);
        ///   2. 遮挡拉近: 从玩家头部向相机方向打一条 Physics.Raycast(命中地形 MeshCollider/
        ///      TerrainCollider 时), 相机放到命中点之前 —— 这是标准第三人称相机的标配
        ///      (Cinemachine ThirdPersonFollow 的 body collision 也是同样的原理)。
        /// 不做任何高度图/追高花活: 相机永远在玩家后上方固定距离, 主控始终在画面里。
        /// </summary>
        private Vector3 ComputeCameraTarget()
        {
            Quaternion rot = Quaternion.Euler(m_CameraRotation.y, m_CameraRotation.x, 0f);
            Vector3 focus = transform.position + Vector3.up * cameraHeight;
            Vector3 target = focus + rot * (Vector3.back * cameraDistance);

            // 遮挡拉近: 从玩家头部(1.5m 高)向相机方向射线, 命中则放到命中点前 0.3m
            Vector3 from = focus;
            Vector3 dir = (target - from).normalized;
            float dist = Vector3.Distance(from, target);
            if (Physics.Raycast(from, dir, out RaycastHit hit, dist))
            {
                target = hit.point - dir * 0.3f;
            }
            return target;
        }

        void Update()
        {
            UpdateInternal(Time.deltaTime);
        }

        /// <summary>手动驱动一帧(编辑器截图/冒烟工具用, 等效一次 Update; dt 由外部传入,
        /// 因为 EditMode 下 Time.deltaTime 恒为 0, 玩家会原地不动)。</summary>
        public void DriveUpdate(float dt)
        {
            UpdateInternal(dt);
        }

        private void UpdateInternal(float dt)
        {
            if (m_HeightMap == null) return;

            if (Input.GetKeyDown(KeyCode.F))
            {
                m_FreeFly = !m_FreeFly;
            }

            if (m_FreeFly)
            {
                UpdateFreeFlyCamera();
            }
            else
            {
                UpdatePlayerMovement(dt);
                UpdateFollowCamera();
            }
        }

        private void UpdatePlayerMovement(float dt)
        {
            if (Input.GetMouseButton(1))
            {
                m_CameraRotation.x += Input.GetAxis("Mouse X") * cameraLookSpeed;
                m_CameraRotation.y -= Input.GetAxis("Mouse Y") * cameraLookSpeed;
                m_CameraRotation.y = Mathf.Clamp(m_CameraRotation.y, 5f, 85f);
            }

            float h = 0f, v = 0f;
            if (Input.GetKey(KeyCode.W)) v += 1f;
            if (Input.GetKey(KeyCode.S)) v -= 1f;
            if (Input.GetKey(KeyCode.D)) h += 1f;
            if (Input.GetKey(KeyCode.A)) h -= 1f;

            Quaternion yaw = Quaternion.Euler(0f, m_CameraRotation.x, 0f);
            Vector3 move;
            if (m_SimulatedMove.sqrMagnitude > 0.001f)
            {
                // 测试注入: 世界空间方向, 直接使用
                move = m_SimulatedMove.normalized * moveSpeed;
                m_SimulatedMove = Vector3.zero;
            }
            else
            {
                move = yaw * new Vector3(h, 0f, v);
                if (move.sqrMagnitude > 0.001f)
                {
                    move = move.normalized * moveSpeed;
                }
            }

            if (move.sqrMagnitude <= 0.001f)
            {
                // 站立: 速度归零、强度归零(草只衰减回弹, 不往任何方向倒)
                m_Interactor.Position = transform.position;
                m_Interactor.Velocity = Vector3.zero;
                m_Interactor.Strength = 0f;
                m_LastPosition = transform.position;
                return;
            }

            // 高度图行走: 水平移动量直接贴地, 不存在"穿透"状态
            Vector3 next = TryMoveStep(transform.position, move * dt);
            transform.position = next;

            // 速度(草交互的倒伏方向)
            Vector3 velocity = (next - m_LastPosition) / Mathf.Max(dt, 0.0001f);
            m_LastPosition = next;
            m_Interactor.Position = next;
            m_Interactor.Velocity = velocity;
            // 速度 → 交互强度(3A 手感: 跑过草地压倒得比走过狠, 站定不压)
            m_Interactor.Strength = Mathf.Clamp01(0.55f + velocity.magnitude / moveSpeed * 0.45f);
        }

        /// <summary>
        /// 高度图行走一步(参考 QnMobile 相邻格判定):
        /// 完整位移 → 被挡则 X 轴单独走 → 被挡则 Z 轴单独走(沿障碍滑动) → 全挡则原地。
        /// </summary>
        private Vector3 TryMoveStep(Vector3 cur, Vector3 step)
        {
            float curH = m_HeightMap.SampleHeight(cur.x, cur.z);
            if (CanWalk(cur, curH, step.x, step.z, out float hFull)) return Land(cur, step.x, step.z, hFull);
            if (CanWalk(cur, curH, step.x, 0f, out float hX)) return Land(cur, step.x, 0f, hX);
            if (CanWalk(cur, curH, 0f, step.z, out float hZ)) return Land(cur, 0f, step.z, hZ);
            return cur; // 完全被地形挡住
        }

        /// <summary>目标格是否"走得过去": 地图内 + 高度差在爬坡/跳崖上限内。</summary>
        private bool CanWalk(Vector3 cur, float curH, float dx, float dz, out float targetH)
        {
            float limit = (m_HeightMap.VertsX - 1) * m_HeightMap.CellSize;
            float nx = Mathf.Clamp(cur.x + dx, 0f, limit);
            float nz = Mathf.Clamp(cur.z + dz, 0f, limit);
            targetH = m_HeightMap.SampleHeight(nx, nz);
            float dh = targetH - curH;
            if (dh > maxClimbHeight) return false;  // 爬不上去(等效墙/陡坎)
            if (dh < -maxFallHeight) return false;  // 悬崖超限(等效不可跳落)
            return true;
        }

        /// <summary>落点: 水平新坐标 + 地表高度 + 站立偏移。</summary>
        private Vector3 Land(Vector3 cur, float dx, float dz, float targetH)
        {
            float limit = (m_HeightMap.VertsX - 1) * m_HeightMap.CellSize;
            float nx = Mathf.Clamp(cur.x + dx, 0f, limit);
            float nz = Mathf.Clamp(cur.z + dz, 0f, limit);
            return new Vector3(nx, targetH + StandOffset, nz);
        }

        private void UpdateFollowCamera()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 target = ComputeCameraTarget();
            // SmoothDamp 跟随: 起步/刹停无 overshoot, 手感更接近标准第三人称
            cam.transform.position = Vector3.SmoothDamp(cam.transform.position, target, ref m_CameraVelocity, 0.12f);
            // 始终注视角色上半身，避免固定欧拉角造成角色沉到画面底边。
            cam.transform.LookAt(transform.position + Vector3.up * cameraHeight);
        }

        private void UpdateFreeFlyCamera()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            if (Input.GetMouseButton(1))
            {
                m_CameraRotation.x += Input.GetAxis("Mouse X") * cameraLookSpeed;
                m_CameraRotation.y -= Input.GetAxis("Mouse Y") * cameraLookSpeed;
                m_CameraRotation.y = Mathf.Clamp(m_CameraRotation.y, -85f, 85f);
            }
            cam.transform.rotation = Quaternion.Euler(m_CameraRotation.y, m_CameraRotation.x, 0f);

            Vector3 dir = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) dir += cam.transform.forward;
            if (Input.GetKey(KeyCode.S)) dir -= cam.transform.forward;
            if (Input.GetKey(KeyCode.D)) dir += cam.transform.right;
            if (Input.GetKey(KeyCode.A)) dir -= cam.transform.right;
            if (Input.GetKey(KeyCode.E)) dir += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) dir -= Vector3.up;
            if (dir.sqrMagnitude > 0.001f)
            {
                cam.transform.position += dir.normalized * freeFlySpeed * Time.deltaTime;
            }
        }
    }
}
