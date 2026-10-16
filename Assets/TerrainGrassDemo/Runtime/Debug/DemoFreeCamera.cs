using UnityEngine;

namespace TerrainGrassDemo.DebugTools
{
    /// <summary>
    /// 最简单的自由飞行相机: WASD 移动, 右键拖拽旋转, 滚轮/Shift 调速。
    /// 用于观察 Chunk 流式加载、草地 LOD 等距离相关的行为。
    /// </summary>
    public class DemoFreeCamera : MonoBehaviour
    {
        public float moveSpeed = 24f;
        public float lookSpeed = 3f;
        public float minSpeed = 4f;
        public float maxSpeed = 120f;

        private Vector2 m_Rotation;

        void Start()
        {
            Vector3 e = transform.eulerAngles;
            m_Rotation = new Vector2(e.y, e.x);
        }

        void Update()
        {
            // 右键拖拽旋转
            if (Input.GetMouseButton(1))
            {
                m_Rotation.x += Input.GetAxis("Mouse X") * lookSpeed;
                m_Rotation.y -= Input.GetAxis("Mouse Y") * lookSpeed;
                m_Rotation.y = Mathf.Clamp(m_Rotation.y, -85f, 85f);
            }
            transform.rotation = Quaternion.Euler(m_Rotation.y, m_Rotation.x, 0f);

            // 滚轮调整速度
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.001f)
            {
                moveSpeed = Mathf.Clamp(moveSpeed * Mathf.Pow(1.6f, scroll), minSpeed, maxSpeed);
            }

            // WASD + Q/E 升降
            float boost = Input.GetKey(KeyCode.LeftShift) ? 3f : 1f;
            Vector3 dir = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) dir += transform.forward;
            if (Input.GetKey(KeyCode.S)) dir -= transform.forward;
            if (Input.GetKey(KeyCode.D)) dir += transform.right;
            if (Input.GetKey(KeyCode.A)) dir -= transform.right;
            if (Input.GetKey(KeyCode.E)) dir += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) dir -= Vector3.up;
            if (dir.sqrMagnitude > 0.001f)
            {
                transform.position += dir.normalized * moveSpeed * boost * Time.deltaTime;
            }
        }
    }
}
