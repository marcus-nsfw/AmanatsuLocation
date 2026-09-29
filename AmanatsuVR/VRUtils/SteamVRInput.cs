using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;
using AmanatsuVR.Logging;

namespace AmanatsuVR.VRUtils
{
    /// <summary>
    /// Leitura de gatilho pelo SteamVR Input v2 (IVRInput).
    /// A entrada legada está morta neste jogo: GetControllerState retorna sucesso com
    /// ulButtonPressed sempre 0x0 (o manifesto de ações já está registrado), e o caminho
    /// UnityEngine.XR joga 'Method not found: Il2CppSystem.ReadOnlySpan.GetPinnableReference'.
    /// O IVRInput é P/Invoke puro para openvr_api.dll, sem interop Il2Cpp no meio.
    /// </summary>
    public static class SteamVRInput
    {
        private const string ACTION_SET = "/actions/default";
        private const string ACTION_INTERACT_UI = "/actions/default/in/InteractUI";
        private const string ACTION_GRAB_PINCH = "/actions/default/in/GrabPinch";
        private const string ACTION_GRAB_GRIP = "/actions/default/in/GrabGrip";
        private const string ACTION_HAPTIC = "/actions/default/out/Haptic";

        // O default so traz gatilho e grip. O analogico (vetor 2D) e o clique dele so existem
        // no conjunto 'platformer' do proprio manifesto do jogo, ligado nas duas maos em todos
        // os bindings; ativa-lo junto com o default e o que da acesso a eles.
        private const string ACTION_SET_PLATFORMER = "/actions/platformer";
        private const string ACTION_MOVE = "/actions/platformer/in/Move";
        private const string ACTION_JUMP = "/actions/platformer/in/Jump";

        public static bool Available { get; private set; } = false;

        private static ulong _actionSet;
        private static ulong _interactUI;
        private static ulong _grabPinch;
        private static ulong _grabGrip;
        private static ulong _haptic;
        private static ulong _actionSetPlatformer;
        private static ulong _move;
        private static ulong _jump;
        private static ulong _sourceLeft;
        private static ulong _sourceRight;

        private static VRActiveActionSet_t[] _activeSets;
        private static int _lastUpdateFrame = -1;

        public static void Initialize()
        {
            Available = false;

            if (OpenVR.Input == null)
            {
                PluginLog.Warning("[AmanatsuVR] IVRInput unavailable; the VR trigger will not respond.");
                return;
            }

            // O manifesto já vem instalado com o jogo (StreamingAssets/SteamVR), com bindings
            // padrão para oculus_touch, knuckles, vive e cosmos. Só precisamos registrá-lo.
            string manifest = Path.Combine(Application.streamingAssetsPath, "SteamVR", "actions.json");
            if (!File.Exists(manifest))
            {
                PluginLog.Error($"[AmanatsuVR] actions.json not found at '{manifest}'.");
                return;
            }

            var err = OpenVR.Input.SetActionManifestPath(manifest);
            PluginLog.Info($"[AmanatsuVR] SetActionManifestPath('{manifest}') => {err}");

            LogHandle(OpenVR.Input.GetActionSetHandle(ACTION_SET, ref _actionSet), ACTION_SET, _actionSet);
            LogHandle(OpenVR.Input.GetActionHandle(ACTION_INTERACT_UI, ref _interactUI), ACTION_INTERACT_UI, _interactUI);
            LogHandle(OpenVR.Input.GetActionHandle(ACTION_GRAB_PINCH, ref _grabPinch), ACTION_GRAB_PINCH, _grabPinch);
            LogHandle(OpenVR.Input.GetActionHandle(ACTION_GRAB_GRIP, ref _grabGrip), ACTION_GRAB_GRIP, _grabGrip);
            LogHandle(OpenVR.Input.GetActionHandle(ACTION_HAPTIC, ref _haptic), ACTION_HAPTIC, _haptic);
            LogHandle(OpenVR.Input.GetInputSourceHandle("/user/hand/left", ref _sourceLeft), "/user/hand/left", _sourceLeft);
            LogHandle(OpenVR.Input.GetInputSourceHandle("/user/hand/right", ref _sourceRight), "/user/hand/right", _sourceRight);

            LogHandle(OpenVR.Input.GetActionSetHandle(ACTION_SET_PLATFORMER, ref _actionSetPlatformer), ACTION_SET_PLATFORMER, _actionSetPlatformer);
            LogHandle(OpenVR.Input.GetActionHandle(ACTION_MOVE, ref _move), ACTION_MOVE, _move);
            LogHandle(OpenVR.Input.GetActionHandle(ACTION_JUMP, ref _jump), ACTION_JUMP, _jump);

            _activeSets = _actionSetPlatformer != 0
                ? new VRActiveActionSet_t[] {
                    new VRActiveActionSet_t { ulActionSet = _actionSet },
                    new VRActiveActionSet_t { ulActionSet = _actionSetPlatformer } }
                : new VRActiveActionSet_t[] { new VRActiveActionSet_t { ulActionSet = _actionSet } };

            Available = _actionSet != 0 && (_interactUI != 0 || _grabPinch != 0);
            PluginLog.Info($"[AmanatsuVR] SteamVR Input v2 {(Available ? "READY" : "UNAVAILABLE")}.");
        }

        private static void LogHandle(EVRInputError err, string name, ulong handle)
        {
            if (err != EVRInputError.None || handle == 0)
            {
                PluginLog.Warning($"[AmanatsuVR] handle '{name}' => {err} (0x{handle:X})");
            }
        }

        /// <summary>
        /// UpdateActionState precisa rodar uma vez por frame antes de qualquer leitura.
        /// </summary>
        private static void EnsureActionState()
        {
            if (!Available || _lastUpdateFrame == Time.frameCount) return;
            _lastUpdateFrame = Time.frameCount;

            var err = OpenVR.Input.UpdateActionState(_activeSets, (uint)Marshal.SizeOf<VRActiveActionSet_t>());
            if (err != EVRInputError.None && Time.frameCount % 600 == 0)
            {
                PluginLog.Warning($"[AmanatsuVR] UpdateActionState => {err}");
            }
        }

        public static bool ReadTrigger(bool isLeft)
        {
            if (!Available) return false;
            EnsureActionState();

            ulong source = isLeft ? _sourceLeft : _sourceRight;
            return ReadDigital(_interactUI, source) || ReadDigital(_grabPinch, source);
        }

        /// <summary>
        /// O grip fisico, para ligar e desligar o painel de UI. Tem que ser GrabGrip e nao
        /// GrabPinch: em bindings_oculus_touch.json o GrabPinch esta ligado ao GATILHO
        /// (/user/hand/right/input/trigger -> grabpinch), entao usa-lo fazia o painel piscar a
        /// cada clique. Quem esta no grip e o GrabGrip.
        ///
        /// B/Y nao da: a entrada legada esta morta neste jogo e o manifesto nao declara acao
        /// nenhuma nesses botoes.
        /// </summary>
        public static bool ReadGrab(bool isLeft)
        {
            if (!Available) return false;
            EnsureActionState();

            return ReadDigital(_grabGrip, isLeft ? _sourceLeft : _sourceRight);
        }

        /// <summary>Analogico da mao, x = direita, y = frente. Zero se o conjunto platformer nao pegou.</summary>
        public static Vector2 ReadStick(bool isLeft)
        {
            if (!Available || _move == 0) return Vector2.zero;
            EnsureActionState();

            InputAnalogActionData_t data = default;
            var err = OpenVR.Input.GetAnalogActionData(_move, ref data,
                (uint)Marshal.SizeOf<InputAnalogActionData_t>(), isLeft ? _sourceLeft : _sourceRight);
            return err == EVRInputError.None && data.bActive ? new Vector2(data.x, data.y) : Vector2.zero;
        }

        /// <summary>Clique do analogico (L3/R3). Nao existe no Index nem no WMR.</summary>
        public static bool ReadStickClick(bool isLeft)
        {
            if (!Available) return false;
            EnsureActionState();
            return ReadDigital(_jump, isLeft ? _sourceLeft : _sourceRight);
        }

        private static bool ReadDigital(ulong action, ulong source)
        {
            if (action == 0) return false;

            InputDigitalActionData_t data = default;
            var err = OpenVR.Input.GetDigitalActionData(action, ref data,
                (uint)Marshal.SizeOf<InputDigitalActionData_t>(), source);

            return err == EVRInputError.None && data.bActive && data.bState;
        }

        public static void Haptic(bool isLeft, float durationSeconds, float amplitude)
        {
            if (!Available || _haptic == 0) return;
            OpenVR.Input.TriggerHapticVibrationAction(_haptic, 0f, durationSeconds, 150f, amplitude,
                isLeft ? _sourceLeft : _sourceRight);
        }

        public static string Describe(bool isLeft)
        {
            string tag = isLeft ? "acao-L" : "acao-R";
            if (!Available) return $"{tag}=indisponivel";

            EnsureActionState();
            ulong source = isLeft ? _sourceLeft : _sourceRight;
            InputDigitalActionData_t data = default;
            var err = OpenVR.Input.GetDigitalActionData(_interactUI, ref data,
                (uint)Marshal.SizeOf<InputDigitalActionData_t>(), source);
            return $"{tag} InteractUI err={err} ativo={data.bActive} estado={data.bState} origem=0x{data.activeOrigin:X}";
        }
    }
}
