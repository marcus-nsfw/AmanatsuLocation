using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using Valve.VR;
using AmanatsuVR.Logging;
using AmanatsuVR.Config;

namespace AmanatsuVR.VRUtils
{
    public class VRCamera : MonoBehaviour
    {
        static VRCamera() { ClassInjector.RegisterTypeInIl2Cpp<VRCamera>(); }

        [HideFromIl2Cpp]
        public static VRCamera Create(GameObject parentGameObject, string name, int depth)
        {
            var gameObject = new GameObject($"{parentGameObject.name}{name}Origin");
            gameObject.transform.parent = parentGameObject.transform;
            gameObject.SetActive(false);
            var result = gameObject.AddComponent<VRCamera>();
            result.Depth = depth;
            gameObject.SetActive(true);
            return result;
        }

        public static bool IsBaseHeadSet { get; private set; } = false;
        public static Vector3 BaseHeadPosition { get; private set; } = Vector3.zero;
        public static Quaternion BaseHeadRotation { get; private set; } = Quaternion.identity;
        public static Quaternion BaseHeadYaw => Quaternion.Euler(0f, BaseHeadRotation.eulerAngles.y, 0f);

        [HideFromIl2Cpp]
        public static void UpdateViewport(VRCamera vrCamera)
        {
            IsBaseHeadSet = true;
            if (vrCamera != null && vrCamera.VR != null && vrCamera.VR.head != null)
            {
                BaseHeadPosition = vrCamera.VR.head.localPosition;
                var angles = vrCamera.VR.head.localRotation.eulerAngles;
                BaseHeadRotation = Quaternion.Euler(
                    PluginConfig.ReflectHMDRotationX.Value ? angles.x : 0f,
                    PluginConfig.ReflectHMDRotationY.Value ? angles.y : 0f,
                    PluginConfig.ReflectHMDRotationZ.Value ? angles.z : 0f
                );
                PluginLog.Info($"[AmanatsuVR] Viewport recentered! BaseHead: Pos={BaseHeadPosition}, Rot={BaseHeadRotation.eulerAngles}");
            }
        }

        private int Depth { get; set; }
        private GameObject CameraObject { get; set; }
        public Camera Normal { get; private set; }
        [HideFromIl2Cpp] public SteamVR_Camera VR { get; private set; }
        public Camera TargetCamera { get; private set; }

        /// <summary>Pose (mundo) que a cabeca em repouso deve ocupar no lugar da camera do jogo.</summary>
        [HideFromIl2Cpp] public (Vector3 position, Quaternion rotation)? PoseForcada { get; set; }

        /// <summary>
        /// Player offset on top of the game camera outside H (map, massage): stick moves, grip + stick turns
        /// and rises. Cleared when the game camera jumps (viewpoint buttons, scene change) and on recenter.
        /// </summary>
        [HideFromIl2Cpp] public Vector3 UserOffset { get; set; }
        [HideFromIl2Cpp] public float UserYaw { get; set; }
        private Camera _offsetCam;
        private Vector3 _offsetCamPos;
        private const float CameraJump = 3f;

        [HideFromIl2Cpp] public void ResetUserOffset() { UserOffset = Vector3.zero; UserYaw = 0f; _forcedHeadRef = null; }

        /// <summary>Real head position (rig local) that the forced pose maps to; captured on first use.</summary>
        private Vector3? _forcedHeadRef;
        /// <summary>HMD center pinned to PoseForcada every frame (no positional tracking): first person.</summary>
        [HideFromIl2Cpp] public bool LockHeadToPose { get; set; }
        [HideFromIl2Cpp] public void ResetForcedHeadRef() => _forcedHeadRef = null;

        /// <summary>Se o rig ja se ancorou a alguma camera do jogo pelo menos uma vez.</summary>
        public bool JaAncorou { get; private set; }

        /// <summary>
        /// O vinculo morreu: a camera que estavamos seguindo foi destruida ou desligada. Acontece
        /// ao sair da massagem, quando a cena do oleo leva a MainCamera dela embora. Sem detectar
        /// isso o LastHijackedCamera ficava apontando para um objeto morto e o
        /// BlankOtherGameCameras apagava a camera NOVA do mapa por ela ser 'outra' - o preto.
        /// </summary>
        [HideFromIl2Cpp]
        public bool HijackMorto => JaAncorou
            && (TargetCamera == null || !TargetCamera.gameObject.activeInHierarchy
                || (!TargetCamera.enabled && !DesligadaPorNos(TargetCamera)));

        /// <summary>
        /// Com DisableHijackedCamera quem desliga a camera alvo somos nos (CameraHijacker com
        /// Destination). Contar isso como morte fazia revincular a cada 0,5 s desde o titulo,
        /// e cada revinculo puxava o painel de volta para a pose de recentralizacao.
        /// </summary>
        [HideFromIl2Cpp]
        private static bool DesligadaPorNos(Camera c)
        {
            var h = c.GetComponent<CameraHijacker>();
            return h != null && h.Destination != null;
        }

        void Awake()
        {
            PluginLog.Debug($"[AmanatsuVR] VRCamera Awake: {name}");
            Setup();
        }

        private void Setup()
        {
            if (!CameraObject)
            {
                CameraObject = new GameObject($"{name}Camera");
                CameraObject.transform.parent = gameObject.transform;
            }

            if (!CameraObject.GetComponent<Camera>())
            {
                Normal = CameraObject.AddComponent<Camera>();
                Normal.depth = Depth;
                Normal.stereoTargetEye = StereoTargetEyeMask.Both;
                Normal.allowHDR = false;
                Normal.allowMSAA = false;

                var addData = CameraObject.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                if (addData == null)
                {
                    addData = CameraObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                }
                if (addData != null)
                {
                    addData.allowXRRendering = true;
                    addData.renderPostProcessing = false;
                    addData.requiresDepthTexture = false;
                    addData.requiresColorTexture = false;
                    addData.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.None;
                    addData.dithering = false;
                    addData.stopNaN = false;
                }
            }

            if (!CameraObject.GetComponent<SteamVR_Camera>())
            {
                VR = CameraObject.AddComponent<SteamVR_Camera>();
            }

            if (!CameraObject.GetComponent<SteamVR_TrackedObject>())
            {
                CameraObject.AddComponent<SteamVR_TrackedObject>();
            }
        }

        private int _logsEscala;

        /// <summary>
        /// A escala do rig so pode vir do WorldSize, aplicado na origem. Na criacao de
        /// personagem a camera do jogo tem transform em escala x10, e logo depois do vinculo a
        /// distancia entre os olhos da nossa camera virava 1,14 m (x10) - o menu "vesgo", que
        /// persistia nas telas seguintes. Nosso codigo nao copia escala; entao algo a escreve na
        /// cabeca ou acima da origem. Aqui a cabeca volta a 1, a cadeia acima da origem tambem,
        /// e o log diz quem estava errado.
        /// </summary>
        [HideFromIl2Cpp]
        private void GaranteEscalaDoRig()
        {
            if (VR == null) return;
            try
            {
                var head = VR.head;
                if (head != null && (head.localScale - Vector3.one).sqrMagnitude > 1e-6f)
                {
                    if (_logsEscala++ < 5) PluginLog.Warning($"[AmanatsuVR][SCALE] VR camera head has scale {head.localScale}; resetting to 1.");
                    head.localScale = Vector3.one;
                }
                for (var t = VR.origin != null ? VR.origin.parent : null; t != null; t = t.parent)
                {
                    if ((t.localScale - Vector3.one).sqrMagnitude <= 1e-6f) continue;
                    if (_logsEscala++ < 5) PluginLog.Warning($"[AmanatsuVR][SCALE] '{t.name}' above the rig with scale {t.localScale}; resetting to 1.");
                    t.localScale = Vector3.one;
                }
            }
            catch { }
        }

        [HideFromIl2Cpp]
        public void Hijack(Camera targetCamera, bool useCopyFrom = true, bool synchronization = true)
        {
            Setup();

            TargetCamera = targetCamera;
            if (targetCamera != null)
            {
                CameraHijacker.Hijack(targetCamera, Normal, useCopyFrom, synchronization);
                UpdateOriginPose();
            }

            if (Normal != null)
            {
                Normal.depth = Depth;
                Normal.stereoTargetEye = StereoTargetEyeMask.Both;
                Normal.allowHDR = false;
                Normal.allowMSAA = false;

                var addData = Normal.gameObject.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                if (addData != null)
                {
                    addData.allowXRRendering = true;
                    addData.renderPostProcessing = false;
                    addData.requiresDepthTexture = false;
                    addData.requiresColorTexture = false;
                    addData.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.None;
                }
            }
        }

        /// <summary>
        /// Atualiza a posição e rotação da origem do VR para acompanhar a câmera do jogo em tempo real.
        /// NÃO utiliza SetParent para evitar que a destruição da câmera alvo destrua o sistema VR.
        /// </summary>
        public void UpdateOriginPose(float smoothTurnYaw = 0f)
        {
            // Não exigir isActiveAndEnabled: o teste de desligar a câmera do jogo desativa o
            // componente, e a origem do VR ainda precisa seguir o transform dela.
            // Escala de mundo: o unico jeito honesto de mudar o tamanho aparente e escalar o
            // rig, porque a distancia entre os olhos e fixa em metros. Rig maior = mundo menor.
            // A cabeca e filha da origem, entao o offset dela tambem sai escalado - por isso
            // BaseHeadPosition entra multiplicada aqui, senao o corpo afunda ou flutua.
            // O mundo esta em escala x10 (Human.MODEL_SCALE): rig x10 = tamanho real. Antes era
            // 1/WorldSize sem esse fator, e 0.3 ainda deixava tudo ~3x gigante.
            float rig = Character.Human.MODEL_SCALE / Mathf.Clamp(PluginConfig.WorldSize.Value, 0.1f, 10f);
            GaranteEscalaDoRig();

            // Na H quem manda na ancora e o VRHScene (camera livre ou cabeca do ator). A camera
            // do jogo continua sequestrada so para render; a pose dela deixa de importar.
            if (PoseForcada.HasValue && VR != null && VR.origin != null)
            {
                var p = PoseForcada.Value;
                // Referencia = cabeca real ao entrar no modo, nao a do recentralizar: este foi feito
                // em pe (y 1,46) e o Marcus joga a 1,04; em tamanho real os 0,42 m de diferenca
                // punham a camera da primeira pessoa na barriga do ator.
                _forcedHeadRef ??= VR.head != null ? VR.head.localPosition : BaseHeadPosition;
                // Travado (primeira pessoa): o centro do HMD fica sempre no ponto da pose - o meio
                // dos olhos do ator -, e a cabeca real so gira.
                Vector3 headRef = LockHeadToPose && VR.head != null ? VR.head.localPosition : _forcedHeadRef.Value;
                VR.origin.localScale = Vector3.one * rig;
                VR.origin.rotation = p.rotation * Quaternion.Inverse(BaseHeadRotation);
                VR.origin.position = p.position - VR.origin.rotation * (headRef * rig);
                return;
            }
            _forcedHeadRef = null;

            if (TargetCamera != null && TargetCamera.gameObject.activeInHierarchy && VR != null && VR.origin != null)
            {
                Quaternion turnRot = Mathf.Abs(smoothTurnYaw) > 0.001f ? Quaternion.Euler(0f, smoothTurnYaw, 0f) : Quaternion.identity;
                var camPos = TargetCamera.transform.position;
                if (_offsetCam != TargetCamera || (camPos - _offsetCamPos).magnitude > CameraJump) ResetUserOffset();
                _offsetCam = TargetCamera; _offsetCamPos = camPos;
                VR.origin.localScale = Vector3.one * rig;
                VR.origin.rotation = Quaternion.Euler(0f, UserYaw, 0f) * TargetCamera.transform.rotation * turnRot * Quaternion.Inverse(BaseHeadRotation);
                VR.origin.position = camPos + UserOffset - VR.origin.rotation * (BaseHeadPosition * rig);
                JaAncorou = true;
            }
            // So voltar para a origem do mundo antes da PRIMEIRA ancoragem, na tela de titulo.
            // Depois dela, TargetCamera nulo quer dizer que a camera do jogo foi destruida - e
            // largar o rig em Vector3.zero teleportava o jogador para o chao ao sair da massagem.
            // Melhor ficar onde estava ate a proxima camera aparecer.
            else if (VR != null && VR.origin != null && TargetCamera == null && !JaAncorou)
            {
                Quaternion turnRot = Mathf.Abs(smoothTurnYaw) > 0.001f ? Quaternion.Euler(0f, smoothTurnYaw, 0f) : Quaternion.identity;
                VR.origin.localScale = Vector3.one * rig;
                VR.origin.rotation = turnRot;
                VR.origin.position = Vector3.zero;
            }
        }
    }
}
