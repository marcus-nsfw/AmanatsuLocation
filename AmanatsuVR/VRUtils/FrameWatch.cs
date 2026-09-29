using UnityEngine;
using AmanatsuVR.Logging;

namespace AmanatsuVR.VRUtils
{
    /// <summary>
    /// Logs (as a warning, so Release builds keep it) when the frame rate stays low, with what is
    /// alive at that moment: GPU vs CPU time and how many cameras/renderers/our own objects exist.
    /// Meant to explain the FPS drop seen after loading a save twice.
    /// </summary>
    internal static class FrameWatch
    {
        private const float LowFps = 30f, Window = 2f, Cooldown = 30f;
        private static float _windowStart, _lastReport = -100f;
        private static int _frames;
        private static readonly FrameTiming[] _timing = new FrameTiming[1];

        public static void Update()
        {
            FrameTimingManager.CaptureFrameTimings();
            _frames++;
            float now = Time.realtimeSinceStartup, elapsed = now - _windowStart;
            if (elapsed < Window) return;
            float fps = _frames / elapsed;
            _frames = 0;
            _windowStart = now;
            if (fps >= LowFps || now - _lastReport < Cooldown) return;
            _lastReport = now;

            string gpu = FrameTimingManager.GetLatestTimings(1, _timing) > 0
                ? $"cpu={_timing[0].cpuFrameTime:F1}ms gpu={_timing[0].gpuFrameTime:F1}ms" : "timing=n/a";
            int cams = 0, ours = 0;
            foreach (var c in Object.FindObjectsOfType<Camera>()) { cams++; if (c.name.StartsWith("AmanatsuVR")) ours++; }
            PluginLog.Warning($"[AmanatsuVR][FPS] {fps:F0} fps for {Window:F0}s | {gpu} | cameras={cams} (ours {ours})"
                + $" hijackers={Object.FindObjectsOfType<CameraHijacker>().Length}"
                + $" uiScreens={Object.FindObjectsOfType<UIScreen>().Length}"
                + $" renderers={Object.FindObjectsOfType<Renderer>().Length}"
                + $" rts={Resources.FindObjectsOfTypeAll<RenderTexture>().Length}"
                + $" tex2d={Resources.FindObjectsOfTypeAll<Texture2D>().Length}"
                + $" scene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}'"
                + $" scenes={UnityEngine.SceneManagement.SceneManager.sceneCount} timeScale={Time.timeScale:F2}");
        }
    }
}
