using System.Collections;
using System.IO;
using UnityEngine;

namespace TerrainGrassDemo.DebugTools
{
    /// <summary>
    /// 在正常 PlayMode 帧末捕获，不修改 Editor pause 状态，也不抢占系统窗口焦点。
    /// F12 可手动保存；自动化通过 Request 调用。
    /// </summary>
    public sealed class LiveFrameCapture : MonoBehaviour
    {
        private bool m_Capturing;

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F12))
            {
                string path = Path.GetFullPath(Path.Combine(Application.dataPath,
                    "TerrainGrassDemo/Captures/FairyMeadow_Manual.png"));
                BeginCapture(path);
            }
        }

        public static bool Request(string absolutePath)
        {
            Camera camera = Camera.main;
            if (camera == null) return false;
            var capture = camera.GetComponent<LiveFrameCapture>();
            if (capture == null) capture = camera.gameObject.AddComponent<LiveFrameCapture>();
            capture.BeginCapture(absolutePath);
            return true;
        }

        private void BeginCapture(string absolutePath)
        {
            if (m_Capturing) return;
            string directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            StartCoroutine(CaptureAtEndOfFrame(absolutePath));
        }

        private IEnumerator CaptureAtEndOfFrame(string path)
        {
            m_Capturing = true;
            // 两个正常帧让 GPU culling/indirect draw 与 Overlay UI 都完成提交。
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(path, 1);
            yield return new WaitForEndOfFrame();
            m_Capturing = false;
            Debug.Log("[LiveFrameCapture] 已保存无暂停运行帧: " + path);
        }
    }
}
