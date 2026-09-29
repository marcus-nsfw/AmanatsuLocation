using System;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.Rendering;
using AmanatsuVR.Logging;

namespace AmanatsuVR.VRUtils
{
    public class CameraHijacker : MonoBehaviour
    {
        static CameraHijacker() { ClassInjector.RegisterTypeInIl2Cpp<CameraHijacker>(); }

        private const float MinFarClip = 1000f;

        [HideFromIl2Cpp]
        public static void Hijack(Camera source, Camera destination = null, bool useCopyFrom = true, bool synchronization = true)
        {
            if (source == null) return;

            // NUNCA sequestrar câmeras internas do AmanatsuVR ou câmeras com targetTexture
            if (source.name.Contains("VR") || source.name.Contains("UGUI") || source.name.Contains("UIScreen") || source.name.Contains("Capture"))
            {
                PluginLog.Warning($"[AmanatsuVR] Tentativa de hijack bloqueada em câmera interna: '{source.name}'");
                return;
            }
            if (source.targetTexture != null)
            {
                PluginLog.Warning($"[AmanatsuVR] Tentativa de hijack bloqueada em câmera com targetTexture: '{source.name}'");
                return;
            }

            PluginLog.Info(destination ? $"[AmanatsuVR] Hijack {source.name} para {destination?.name}" : $"[AmanatsuVR] Hijack {source.name}");

            var srcDistances = source.layerCullDistances;
            if (srcDistances != null)
            {
                var naoZero = "";
                for (int i = 0; i < srcDistances.Length; i++)
                {
                    if (srcDistances[i] != 0f) naoZero += $" layer{i}={srcDistances[i]:F1}";
                }
                PluginLog.Info($"[AmanatsuVR][LAYERCULL] '{source.name}' esferico={source.layerCullSpherical}"
                    + (naoZero.Length > 0 ? $" distâncias:{naoZero}" : " todas zero (usa far plane)"));
            }
            var srcAddData = source.gameObject.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            if (destination && useCopyFrom)
            {
                GuardaEstereoOriginal(destination);
                PluginLog.Info($"[AmanatsuVR][ESTEREO] origem '{source.name}': separacao={source.stereoSeparation:F4}"
                    + $" convergencia={source.stereoConvergence:F2} fisica={source.usePhysicalProperties}"
                    + $" lensShift={source.lensShift} orto={source.orthographic} escala={source.transform.lossyScale}");

                destination.CopyFrom(source);

                // CopyFrom traz junto o que e da camera do jogo e nao tem sentido num HMD: a
                // separacao/convergencia estereo e a camera fisica (lente). A da criacao de
                // personagem (fov 23, mundo x10) deixava os olhos a 1,14 m um do outro - o menu
                // "vesgo" - e o valor ficava ate o proximo vinculo, contaminando as outras telas.
                RestauraEstereo(destination);

                // CopyFrom traz junto a matriz de projeção/view EXPLÍCITA do CameraControllerEX,
                // que recalcula o frustum na mão. Com matriz explícita o XR não consegue montar
                // as matrizes por olho e o olho direito fica sem geometria (sobra skybox e o que
                // tem ZTest Always). Sempre devolver as matrizes ao controle da engine.
                ResetMatrices(destination);
                ClearLayerCull(destination);
                destination.useOcclusionCulling = false;

                destination.stereoTargetEye = StereoTargetEyeMask.Both;
                destination.allowHDR = false;
                destination.allowMSAA = false;
                if (destination.nearClipPlane > 0.1f)
                {
                    destination.nearClipPlane = 0.05f;
                }
                // Story events (ADV, e.g. the intimacy 'event' at the waterfall) drive a camera with far=1: the scene is a
                // 2D picture on a canvas and the camera only needs a few centimeters. Copied as is, the headset clipped
                // everything past 1 m, the UI panel (2 m away) included - only the skybox was left.
                if (destination.farClipPlane < MinFarClip)
                {
                    PluginLog.Info($"[AmanatsuVR][PROJ] far da camera '{source.name}' = {destination.farClipPlane:F2}; usando {MinFarClip:F0}.");
                    destination.farClipPlane = MinFarClip;
                }

                var destAddData = destination.gameObject.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                if (destAddData == null)
                {
                    destAddData = destination.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                }
                if (destAddData != null)
                {
                    destAddData.allowXRRendering = true;
                    destAddData.renderPostProcessing = false;
                    destAddData.requiresDepthTexture = false;
                    destAddData.requiresColorTexture = false;
                    destAddData.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.None;
                    destAddData.dithering = false;
                    destAddData.stopNaN = false;

                    if (srcAddData != null)
                    {
                        destAddData.SetRenderer(srcAddData.m_RendererIndex);
                        destAddData.volumeLayerMask = srcAddData.volumeLayerMask;
                        destAddData.volumeTrigger = srcAddData.volumeTrigger;
                        destAddData.renderType = UnityEngine.Rendering.Universal.CameraRenderType.Base;
                    }
                }
            }

            if (srcAddData != null)
            {
                srcAddData.allowXRRendering = false;
                srcAddData.renderPostProcessing = false;
                srcAddData.requiresDepthTexture = false;
                srcAddData.requiresColorTexture = false;
            }
            source.stereoTargetEye = StereoTargetEyeMask.None;
            source.allowHDR = false;
            source.allowMSAA = false;

            var hijacker = source.GetComponent<CameraHijacker>();
            if (hijacker == null) hijacker = source.gameObject.AddComponent<CameraHijacker>();
            if (destination && synchronization)
            {
                // One writer per VR camera. The previous source (the massage camera survives the massage) kept
                // syncing into the same destination every render context - overwriting its mask with the old
                // camera's - and the frame rate fell to ~3 fps until the map changed.
                foreach (var other in FindObjectsOfType<CameraHijacker>())
                {
                    if (other == null || other == hijacker || other.Destination != destination) continue;
                    PluginLog.Info($"[AmanatsuVR] hijacker antigo em '{other.name}' desligado (destino passou para '{source.name}').");
                    other.Destination = null;
                }
                hijacker.Destination = destination;
            }
        }

        // Valores da camera VR como ela nasceu, antes do primeiro CopyFrom.
        private static bool _temEstereo;
        private static float _separacao, _convergencia;

        [HideFromIl2Cpp]
        private static void GuardaEstereoOriginal(Camera destino)
        {
            if (_temEstereo) return;
            _temEstereo = true;
            _separacao = destino.stereoSeparation;
            _convergencia = destino.stereoConvergence;
        }

        [HideFromIl2Cpp]
        private static void RestauraEstereo(Camera destino)
        {
            destino.stereoSeparation = _separacao;
            destino.stereoConvergence = _convergencia;
            destino.usePhysicalProperties = false;
            destino.lensShift = Vector2.zero;
            destino.orthographic = false;
        }

        [HideFromIl2Cpp]
        /// <summary>
        /// layerCullDistances: distância máxima de render POR LAYER, também copiada pelo CopyFrom. É o único
        /// mecanismo do Unity que descarta objeto por layer mesmo com a layer presente na cullingMask (o quad
        /// amarelo, layer 0, visivel=False). Zero = usar o far plane.
        ///
        /// Only right after CopyFrom, which is the only thing that brings them. It ran every render context
        /// inside ResetMatrices: under URP every layerCullSpherical write logs a warning with a stack trace
        /// (77k of them in one session in Player.log) plus a new float[32] each time.
        /// </summary>
        private static void ClearLayerCull(Camera camera)
        {
            var d = camera.layerCullDistances;
            bool any = false;
            if (d != null) for (int i = 0; i < d.Length; i++) if (d[i] != 0f) { any = true; break; }
            if (any) camera.layerCullDistances = new float[32];
            if (camera.layerCullSpherical) camera.layerCullSpherical = false;
        }

        public static void ResetMatrices(Camera camera)
        {
            if (camera == null) return;
            camera.ResetWorldToCameraMatrix();
            camera.ResetProjectionMatrix();
            // Faltava esta. CopyFrom traz a cullingMatrix explícita do jogo junto, e ela manda
            // no culling independente das matrizes de view: dá render posicionado certo num olho
            // e conjunto visível vazio no outro. Sobrevive só quem tem bounds enormes (o laser).
            camera.ResetCullingMatrix();

            camera.ResetStereoViewMatrices();
            camera.ResetStereoProjectionMatrices();
        }

        /// <summary>
        /// Espia o estado DENTRO do laço de render, por olho. Todo diagnóstico até agora leu
        /// o estado fora do render e sempre pareceu saudável; layer mudando comportamento
        /// entre olhos só pode aparecer aqui.
        /// </summary>
        public static class RenderSpy
        {
            private static Il2CppSystem.Action<ScriptableRenderContext, Camera> _onBeginCamera;
            private static int _restantes = 0;

            [HideFromIl2Cpp]
            public static void Arm(int amostras)
            {
                _restantes = amostras;
                if (_onBeginCamera != null) return;

                _onBeginCamera = (Il2CppSystem.Action<ScriptableRenderContext, Camera>)(
                    (System.Action<ScriptableRenderContext, Camera>)OnBeginCamera);
                RenderPipelineManager.beginCameraRendering += _onBeginCamera;
            }

            [HideFromIl2Cpp]
            private static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
            {
                if (_restantes <= 0 || cam == null) return;
                if (!cam.name.Contains("eye")) return;
                _restantes--;

                PluginLog.Info($"[AmanatsuVR][SPY] frame={Time.frameCount} '{cam.name}'"
                    + $" olhoAtivo={cam.stereoActiveEye} alvoOlho={cam.stereoTargetEye}"
                    + $" mask=0x{cam.cullingMask:X8} estereoAtivo={cam.stereoEnabled}"
                    + $" pos={cam.transform.position}");
            }
        }

        public Camera Destination { get; set; }
        public int LastCullingMask { get; private set; }
        private CameraClearFlags LastClearFlags { get; set; }
        private bool HasCapturedMask { get; set; } = false;

        private Il2CppSystem.Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>> onBeginContextRendering;
        private Il2CppSystem.Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>> onEndContextRendering;

        void Awake()
        {
            onBeginContextRendering = (Il2CppSystem.Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>)OnBeginContextRendering;
            onEndContextRendering = (Il2CppSystem.Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>)OnEndContextRendering;
        }

        void OnEnable()
        {
            RenderPipelineManager.beginContextRendering += onBeginContextRendering;
            RenderPipelineManager.endContextRendering += onEndContextRendering;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginContextRendering -= onBeginContextRendering;
            RenderPipelineManager.endContextRendering -= onEndContextRendering;

            // Restaura o estado original da câmera caso o hijacker seja desativado
            var camera = GetComponent<Camera>();
            if (camera != null && HasCapturedMask && LastCullingMask != 0)
            {
                camera.cullingMask = LastCullingMask;
                camera.clearFlags = LastClearFlags;
            }
        }

        [HideFromIl2Cpp]
        void OnBeginContextRendering(ScriptableRenderContext context, Il2CppSystem.Collections.Generic.List<Camera> cameras)
        {
            var camera = GetComponent<Camera>();
            if (camera != null)
            {
                // Apenas salva a máscara se a câmera estiver atualmente com culling ativo
                if (camera.cullingMask != 0)
                {
                    LastCullingMask = camera.cullingMask;
                    LastClearFlags = camera.clearFlags;
                    HasCapturedMask = true;
                }

                // TESTE: apagar por cullingMask pode não estar bastando. Se a câmera mono do
                // jogo continuar desenhando, o mapa chega só no olho esquerdo e a nossa câmera
                // nunca foi a autora daquela imagem. Desligar o componente responde isso.
                if (AmanatsuVR.Config.PluginConfig.DisableHijackedCamera.Value && Destination != null)
                {
                    Synchronize();
                    camera.enabled = false;
                    return;
                }

                camera.cullingMask = 0;
                camera.clearFlags = CameraClearFlags.Nothing;
                if (Destination != null) Synchronize();
            }
        }

        [HideFromIl2Cpp]
        void OnEndContextRendering(ScriptableRenderContext context, Il2CppSystem.Collections.Generic.List<Camera> cameras)
        {
            var camera = GetComponent<Camera>();
            if (camera != null && HasCapturedMask)
            {
                camera.cullingMask = LastCullingMask;
                camera.clearFlags = LastClearFlags;
            }
        }

        private void Synchronize()
        {
            if (Destination != null && HasCapturedMask)
            {
                // O jogo reaplica matriz explícita na câmera dele todo frame; garantimos que a
                // nossa nunca herde uma, senão o olho direito volta a ficar sem geometria.
                ResetMatrices(Destination);

                // Inclui a camada da UI virtual (31) e as camadas das câmeras mono que apagamos,
                // e exclui a camada de captura uGUI (15)
                Destination.cullingMask = ((LastCullingMask | VRController.ExtraGameCullingMask)
                    & ~((1 << 15) | VRController.MascaraOculta)) | (1 << 31);
                var flags = LastClearFlags;
                if (flags == CameraClearFlags.Nothing || flags == CameraClearFlags.Depth)
                {
                    flags = CameraClearFlags.Skybox;
                }
                Destination.clearFlags = flags;

                var cam = GetComponent<Camera>();
                if (cam != null)
                {
                    Destination.backgroundColor = cam.backgroundColor;
                }
            }
        }
    }

    /// <summary>
    /// O interruptor unico de todos os patches que desligam features de render da URP em VR.
    ///
    /// Eles existem porque cada uma dessas features faz um blit mono que apaga o olho direito.
    /// Mas o personagem da criacao sai branco chapado com olhos pretos, e 'AL/skin_head' tem 5
    /// passes: um shader toon que perde o passe certo desenha exatamente isso. Nunca testei se
    /// alguma dessas features e quem desenha a pele - entao agora da para desligar o conjunto e
    /// olhar, em vez de eu continuar supondo de fora.
    /// </summary>
    internal static class PatchesURP
    {
        [HideFromIl2Cpp]
        public static bool Active => Plugin.IsVRModeActive
            && !AmanatsuVR.Config.PluginConfig.RestoreURPFeatures.Value;
    }

    /// <summary>
    /// Desabilita os passes do Beautify em VR para evitar que a tela do olho direito fique preta.
    /// Em Multi-Pass estéreo, os shaders de blit do Beautify 2 não suportam múltiplos olhos
    /// e acabam limpando ou renderizando preto no segundo olho.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(Beautify.Universal.BeautifyRendererFeature), nameof(Beautify.Universal.BeautifyRendererFeature.AddRenderPasses))]
    public static class Beautify_AddRenderPasses_Patch
    {
        public static bool Prefix() => !PatchesURP.Active;
    }

    /// <summary>
    /// Desabilita o feature de outline (Easy Performant Outline) em VR para evitar blit mono que apaga o olho secundário.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(EPOOutline.URPOutlineFeature), nameof(EPOOutline.URPOutlineFeature.AddRenderPasses))]
    public static class EPOOutline_AddRenderPasses_Patch
    {
        public static bool Prefix() => !PatchesURP.Active;
    }

    /// <summary>
    /// Desabilita o recurso de névoa e espalhamento atmosférico (AzureSky Fog Scattering) em VR.
    /// Em Multi-Pass estéreo, os shaders do AzureFog realizam blit mono e destroem a renderização 3D do segundo olho (olho direito).
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(UnityEngine.AzureSky.AzureFogScatteringFeature), nameof(UnityEngine.AzureSky.AzureFogScatteringFeature.AddRenderPasses))]
    public static class AzureFogScattering_AddRenderPasses_Patch
    {
        public static bool Prefix() => !PatchesURP.Active;
    }

    /// <summary>
    /// Desabilita o Screen Space Ambient Occlusion (SSAO) em VR para evitar artefatos mono e escurecimento do olho direito.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion), nameof(UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion.AddRenderPasses))]
    public static class SSAO_AddRenderPasses_Patch
    {
        public static bool Prefix() => !PatchesURP.Active;
    }

    /// <summary>
    /// Desabilita o Screen Space Shadows em VR para evitar sombreamento preto total no olho direito.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(UnityEngine.Rendering.Universal.ScreenSpaceShadows), nameof(UnityEngine.Rendering.Universal.ScreenSpaceShadows.AddRenderPasses))]
    public static class ScreenSpaceShadows_AddRenderPasses_Patch
    {
        public static bool Prefix() => !PatchesURP.Active;
    }

    /// <summary>
    /// Desabilita o FullScreenPassRendererFeature em VR para prevenir blits de tela cheia que apagam o olho secundário.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(UnityEngine.Rendering.Universal.FullScreenPassRendererFeature), nameof(UnityEngine.Rendering.Universal.FullScreenPassRendererFeature.AddRenderPasses))]
    public static class FullScreenPass_AddRenderPasses_Patch
    {
        public static bool Prefix() => !PatchesURP.Active;
    }

    /// <summary>
    /// Desabilita o CrossFadeRenderer em VR (transições de cena mono da ILLGAMES).
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(ILLGAMES.URP.CrossFadeRenderer), nameof(ILLGAMES.URP.CrossFadeRenderer.AddRenderPasses))]
    public static class CrossFadeRenderer_AddRenderPasses_Patch
    {
        public static bool Prefix() => !PatchesURP.Active;
    }

    /// <summary>
    /// Desabilita o OverlayXFadeRenderer em VR (transições de cena pós-processadas da ILLGAMES).
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(ILLGAMES.URP.PostProcessing.OverlayXFadeRenderer), nameof(ILLGAMES.URP.PostProcessing.OverlayXFadeRenderer.AddRenderPasses))]
    public static class OverlayXFadeRenderer_AddRenderPasses_Patch
    {
        public static bool Prefix() => !PatchesURP.Active;
    }

    /// <summary>
    /// Desabilita o CompositeCanvasRenderer em VR (efeitos de pós-processamento 2D que geram texturas rosa/magenta com shaders incompatíveis).
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(CompositeCanvas.CompositeCanvasRenderer), nameof(CompositeCanvas.CompositeCanvasRenderer.OnEnable))]
    public static class CompositeCanvasRenderer_OnEnable_Patch
    {
        public static bool Prefix(CompositeCanvas.CompositeCanvasRenderer __instance)
        {
            if (PatchesURP.Active)
            {
                __instance.enabled = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(CompositeCanvas.CompositeCanvasRenderer), "Bake", new Type[0])]
    public static class CompositeCanvasRenderer_Bake_Patch
    {
        public static bool Prefix() => !PatchesURP.Active;
    }
}
