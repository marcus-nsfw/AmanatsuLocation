using System.Collections;
using BepInEx.Unity.IL2CPP.Utils;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;
using AmanatsuVR.Logging;

namespace AmanatsuVR.VRUtils
{
    /// <summary>
    /// A passada estéreo da URP entrega o olho esquerdo e perde a geometria opaca no direito.
    /// Foram descartadas por medição: culling (os dois olhos veem ~500 objetos cada), matrizes,
    /// layout de textura, single/multipass, depth priming, native render pass e Forward+.
    /// O que ficou provado é que uma câmera MONO, com as matrizes de um olho, desenha a cena
    /// inteira e correta — é o que os PNGs PROBE_esquerdo/PROBE_direito mostram.
    ///
    /// Então esta classe ignora a passada estéreo: pega os alvos de render que o Unity vai
    /// entregar ao compositor, renderiza cada olho como câmera mono e copia o resultado para
    /// o alvo do olho correspondente.
    /// </summary>
    public class XREyeRenderer : MonoBehaviour
    {
        static XREyeRenderer() { ClassInjector.RegisterTypeInIl2Cpp<XREyeRenderer>(); }

        [HideFromIl2Cpp] public Camera Source { get; set; }

        private Camera Probe { get; set; }
        private RenderTexture Buffer { get; set; }
        private XRDisplaySubsystem Display { get; set; }
        private bool Described { get; set; } = false;

        [HideFromIl2Cpp]
        public static XREyeRenderer Create(GameObject parent, Camera source)
        {
            var go = new GameObject("AmanatsuVR_XREyeRenderer");
            go.transform.parent = parent.transform;
            go.SetActive(false);
            var result = go.AddComponent<XREyeRenderer>();
            result.Source = source;
            go.SetActive(true);
            return result;
        }

        void OnEnable()
        {
            this.StartCoroutine(RenderLoop());
        }

        void OnDestroy()
        {
            if (Buffer != null) { Buffer.Release(); Buffer = null; }
        }

        [HideFromIl2Cpp]
        private XRDisplaySubsystem FindDisplay()
        {
            var list = new Il2CppSystem.Collections.Generic.List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(list);
            foreach (var d in list)
            {
                if (d != null && d.running) return d;
            }
            return null;
        }

        [HideFromIl2Cpp]
        private void EnsureProbe()
        {
            if (Probe != null) return;

            var go = new GameObject("AmanatsuVR_XREyeProbe");
            go.transform.parent = transform;
            Probe = go.AddComponent<Camera>();
            Probe.enabled = false;
            Probe.stereoTargetEye = StereoTargetEyeMask.None;
            Probe.useOcclusionCulling = false;

            var addData = go.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            if (addData != null)
            {
                // XR desligado de propósito: é exatamente por ser mono que este caminho funciona.
                addData.allowXRRendering = false;
                addData.renderPostProcessing = false;
                addData.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.None;
                addData.renderType = UnityEngine.Rendering.Universal.CameraRenderType.Base;
            }
        }

        [HideFromIl2Cpp]
        private IEnumerator RenderLoop()
        {
            while (true)
            {
                // Depois que todas as câmeras renderizaram e antes da entrega ao compositor.
                yield return new WaitForEndOfFrame();

                if (Source == null) continue;

                Display ??= FindDisplay();
                if (Display == null) continue;

                EnsureProbe();

                try
                {
                    DrawEyes();
                }
                catch (System.Exception ex)
                {
                    PluginLog.Error($"[AmanatsuVR][EYE] falha: {ex}");
                    yield break;
                }
            }
        }

        [HideFromIl2Cpp]
        private void DrawEyes()
        {
            int passes = Display.GetRenderPassCount();
            if (passes <= 0) return;

            Probe.CopyFrom(Source);
            Probe.enabled = false;
            Probe.stereoTargetEye = StereoTargetEyeMask.None;
            Probe.useOcclusionCulling = false;
            Probe.cullingMask = Source.cullingMask;
            Probe.clearFlags = Source.clearFlags;
            Probe.backgroundColor = Source.backgroundColor;

            for (int i = 0; i < passes; i++)
            {
                Display.GetRenderPass(i, out var pass);
                int paramCount = pass.GetRenderParameterCount();

                for (int j = 0; j < paramCount; j++)
                {
                    pass.GetRenderParameter(Source, j, out var prm);

                    var desc = pass.renderTargetDesc;
                    if (Buffer == null || Buffer.width != desc.width || Buffer.height != desc.height)
                    {
                        if (Buffer != null) Buffer.Release();
                        Buffer = new RenderTexture(desc.width, desc.height, 24, RenderTextureFormat.ARGB32);
                        Buffer.Create();
                    }

                    if (!Described)
                    {
                        Described = true;
                        PluginLog.Info($"[AmanatsuVR][EYE] passadas={passes} parametros={paramCount}"
                            + $" alvo={desc.width}x{desc.height} volumeDepth={desc.volumeDepth}"
                            + $" fatia={prm.textureArraySlice} viewport={prm.viewport}");
                    }

                    Probe.worldToCameraMatrix = prm.view;
                    Probe.projectionMatrix = prm.projection;
                    Probe.targetTexture = Buffer;
                    Probe.Render();
                    Probe.targetTexture = null;

                    var cb = new CommandBuffer { name = "AmanatsuVR_EyeBlit" };
                    if (prm.textureArraySlice >= 0)
                    {
                        cb.SetRenderTarget(pass.renderTarget, 0, CubemapFace.Unknown, prm.textureArraySlice);
                    }
                    else
                    {
                        cb.SetRenderTarget(pass.renderTarget);
                    }
                    cb.SetViewport(prm.viewport);
                    cb.Blit(Buffer, BuiltinRenderTextureType.CurrentActive);
                    Graphics.ExecuteCommandBuffer(cb);
                    cb.Release();
                }
            }
        }
    }
}
