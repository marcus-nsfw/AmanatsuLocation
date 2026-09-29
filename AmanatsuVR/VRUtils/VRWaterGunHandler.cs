using System;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Attributes;
using AmanatsuVR.Logging;
using AmanatsuVR.Config;
using AL;

namespace AmanatsuVR.VRUtils
{
    /// <summary>
    /// Gerenciador da Pistola d'Água em Realidade Virtual (6DoF Hand Tracking).
    /// Desacopla a pistola d'água da câmera de cabeça do desktop e anexa-a ao controle
    /// físico da mão VR, permitindo mirar livremente em qualquer direção e disparar pelo gatilho.
    /// </summary>
    public class VRWaterGunHandler
    {
        public static bool IsTriggerHeld { get; private set; } = false;
        public static bool LastTriggerHeld { get; private set; } = false;
        public static bool IsWaterGunSceneActive { get; private set; } = false;

        /// <summary>
        /// A tecla que o jogo le para atirar, vinda de WaterGunSceneParam.ShootKey. O patch de
        /// Input.GetKey so responde a ela: forcar true para qualquer tecla apertaria meio jogo.
        /// </summary>
        public static KeyCode TeclaDeTiro { get; private set; } = KeyCode.None;

        /// <summary>
        /// O outro botao de tiro, de WaterGunSceneParam.ShootMouseKey. Medido: Space e 2. Os
        /// patches de mouse so respondiam ao botao 0, entao esse caminho nunca era coberto.
        /// -1 enquanto nao lido.
        /// </summary>
        public static int BotaoDeTiro { get; private set; } = -1;

        private VRCamera MainVRCamera;
        private VRControllerLaser Laser;
        private VRController Controlador;
        private Transform OriginalGunParent;
        private Transform AttachedGunTransform;
        private bool WasWaterGunOpen = false;
        private Vector3 _desvioLocal = Vector3.zero;
        private float LastHapticTime = 0f;

        public void Setup(VRCamera vrCamera, VRControllerLaser laser, VRController controlador)
        {
            MainVRCamera = vrCamera;
            Laser = laser;
            Controlador = controlador;
            PluginLog.Info("[AmanatsuVR] VRWaterGunHandler inicializado com sucesso.");
        }

        /// <summary>
        /// A mao que segura a arma, tirada das ancoras do laser. A copia local que existia aqui
        /// tinha um fallback proprio de pose (TryGetFeatureValue por nome de feature) que falha
        /// calado; quando ele falhava a ancora ficava parada na origem do rig e a arma aparecia
        /// a uma distancia fixa em vez de na mao.
        /// </summary>
        private Transform Mao
        {
            get
            {
                bool isLeft = PluginConfig.WaterGunHand.Value.Equals("Left", StringComparison.OrdinalIgnoreCase);
                return Laser != null ? Laser.GetAnchor(isLeft) : null;
            }
        }

        public void Update()
        {
            LastTriggerHeld = IsTriggerHeld;

            if (!Plugin.IsVRModeActive || !PluginConfig.EnableWaterGunVR.Value)
            {
                IsTriggerHeld = false;
                IsWaterGunSceneActive = false;
                return;
            }

            bool isOpen = false;
            try
            {
                isOpen = WaterGunScene.IsStaticOpen();
            }
            catch
            {
                isOpen = false;
            }

            IsWaterGunSceneActive = isOpen;

            if (isOpen)
            {
                if (!WasWaterGunOpen)
                {
                    OnWaterGunModeEntered();
                }

                HandleTriggerInput();
            }
            else
            {
                if (WasWaterGunOpen)
                {
                    OnWaterGunModeExited();
                }

                IsTriggerHeld = false;
            }

            WasWaterGunOpen = isOpen;
        }

        public void LateUpdate()
        {
            var mao = Mao;
            if (!WasWaterGunOpen || AttachedGunTransform == null || mao == null) return;

            // Alinha a pistola à pose 6DoF física do controle na mão do jogador
            Vector3 forwardOffset = mao.forward * PluginConfig.WaterGunForwardOffset.Value;
            Vector3 upOffset = mao.up * PluginConfig.WaterGunUpOffset.Value;
            Vector3 alvo = mao.position + forwardOffset + upOffset;

            AttachedGunTransform.rotation = mao.rotation
                * Quaternion.Euler(PluginConfig.WaterGunPitchOffset.Value, 0f, 0f);
            AttachedGunTransform.position = alvo;

            // O transform que movemos e um container: o modelo visivel fica deslocado dentro
            // dele. Por isso a arma seguia o controle certinho e mesmo assim aparecia longe da
            // mao. _desvioLocal e a distancia da origem do container ate o centro do modelo,
            // medida uma vez ao acoplar, entao aqui basta descontar.
            if (_desvioLocal != Vector3.zero)
            {
                AttachedGunTransform.position += alvo - AttachedGunTransform.TransformPoint(_desvioLocal);
            }
        }

        private void OnWaterGunModeEntered()
        {
            PluginLog.Info("[AmanatsuVR] Modo Pistolinha d'Água detectado! Acoplando pistola ao controle VR...");

            // Entrar na pistola ja cai no modo de mira: o painel some e o gatilho passa a
            // atirar. O grip traz o painel de volta, que e como se alcanca o botao de sair.
            Controlador?.MostrarPainel(false);

            try
            {
                var scene = WaterGunScene.Instance;
                if (scene != null)
                {
                    Transform gunTrans = scene._transWatergunMove;
                    if (gunTrans == null)
                    {
                        var found = GameObject.Find("WaterGunMove") ?? GameObject.Find("WaterGun") ?? GameObject.Find("transWatergunMove");
                        if (found != null) gunTrans = found.transform;
                    }

                    if (gunTrans != null)
                    {
                        AttachedGunTransform = gunTrans;
                        OriginalGunParent = gunTrans.parent;
                        _desvioLocal = MedeDesvioDoModelo(gunTrans);

                        PluginLog.Info($"[AmanatsuVR] Pistola d'água '{gunTrans.name}' acoplada com sucesso ao controle VR!"
                            + $" desvioLocal={_desvioLocal} pai='{(OriginalGunParent != null ? OriginalGunParent.name : "-")}'");
                    }

                    // Qual botao o jogo le para atirar. Se for ShootKey (um KeyCode), os nossos
                    // patches de mouse nao alcancam e por isso nao saia agua.
                    var p = scene._wgParam;
                    if (p != null)
                    {
                        TeclaDeTiro = p.ShootKey;
                        BotaoDeTiro = p.ShootMouseKey;
                        PluginLog.Info($"[AmanatsuVR][AGUA] ShootKey={p.ShootKey} ShootMouseKey={p.ShootMouseKey}");
                    }
                    else
                    {
                        PluginLog.Warning("[AmanatsuVR] Transform da pistola d'água não encontrado na cena.");
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"[AmanatsuVR] Erro ao vincular pistola d'água: {ex.Message}");
            }
        }

        /// <summary>
        /// Onde o modelo visivel esta, em relacao a origem do container que movemos.
        /// Devolve zero se nao houver renderizador, e entao nada e descontado.
        /// </summary>
        private Vector3 MedeDesvioDoModelo(Transform container)
        {
            try
            {
                var rends = container.GetComponentsInChildren<Renderer>(true);
                if (rends == null || rends.Length == 0) return Vector3.zero;

                Bounds b = rends[0].bounds;
                for (int i = 1; i < rends.Length; ++i) b.Encapsulate(rends[i].bounds);

                return container.InverseTransformPoint(b.center);
            }
            catch { return Vector3.zero; }
        }

        private void OnWaterGunModeExited()
        {
            PluginLog.Info("[AmanatsuVR] Modo Pistolinha d'Água encerrado. Restaurando transform...");

            // Solta o que estiver preso: sair do modo com o gatilho apertado deixaria a tecla
            // Space e o botao do meio pressionados no Windows inteiro.
            InjetaTiroFisico(false);

            if (AttachedGunTransform != null && OriginalGunParent != null)
            {
                AttachedGunTransform.parent = OriginalGunParent;
            }

            AttachedGunTransform = null;
            OriginalGunParent = null;
            _desvioLocal = Vector3.zero;
            IsTriggerHeld = false;
        }

        private bool _tiroFisicoAtivo = false;

        /// <summary>
        /// Os patches em Input nao chegam aqui, pela mesma razao de sempre: o il2cpp inlinou os
        /// getters na compilacao AOT e o call site do jogo nunca passa pelo Harmony. Foi assim
        /// com o mousePosition e com o botao do mouse do ADV; a agua e o terceiro caso.
        ///
        /// Entao mandamos entrada de verdade. Medido: ShootKey=Space, ShootMouseKey=2 (o botao
        /// do meio). Nao sabemos qual dos dois o WaterGunObiFFEmitKeyPress le, e disparar os
        /// dois custa duas linhas contra um ciclo de teste do Marcus.
        /// </summary>
        private void InjetaTiroFisico(bool apertado)
        {
            if (apertado == _tiroFisicoAtivo) return;
            if (EntradaFisica.Segura(TeclaDeTiro, BotaoDeTiro, apertado)) _tiroFisicoAtivo = apertado;
        }

        private void HandleTriggerInput()
        {
            // Painel visivel manda o cursor, escondido manda a arma. Sem esta regra o mesmo
            // gatilho clicava na UI e atirava ao mesmo tempo.
            if (VRController.PainelVisivel)
            {
                IsTriggerHeld = false;
                InjetaTiroFisico(false);
                return;
            }

            var role = PluginConfig.WaterGunHand.Value.Equals("Left", StringComparison.OrdinalIgnoreCase)
                ? ETrackedControllerRole.LeftHand
                : ETrackedControllerRole.RightHand;
            bool isLeft = (role == ETrackedControllerRole.LeftHand);

            uint devIndex = OpenVR.k_unTrackedDeviceIndexInvalid;
            if (SteamVR.active && OpenVR.System != null)
            {
                devIndex = OpenVR.System.GetTrackedDeviceIndexForControllerRole(role);
            }

            // Leitura unificada de gatilho via XR, JoyCodes e OpenVR
            bool triggerDown = VRControllerLaser.ReadTriggerCombined(isLeft, devIndex);

            IsTriggerHeld = triggerDown;
            InjetaTiroFisico(triggerDown);

            // Feedback háptico (vibração no controle ao disparar água)
            if (triggerDown && PluginConfig.WaterGunHaptics.Value)
            {
                if (Time.unscaledTime - LastHapticTime > 0.05f)
                {
                    LastHapticTime = Time.unscaledTime;
                    if (devIndex != OpenVR.k_unTrackedDeviceIndexInvalid && OpenVR.System != null)
                    {
                        try { OpenVR.System.TriggerHapticPulse(devIndex, 0, 1200); } catch { }
                    }
                    try
                    {
                        var node = isLeft ? UnityEngine.XR.XRNode.LeftHand : UnityEngine.XR.XRNode.RightHand;
                        var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
                        if (dev.isValid)
                        {
                            dev.SendHapticImpulse(0, 0.8f, 0.05f);
                        }
                    }
                    catch { }
                }
            }
        }
    }
}
