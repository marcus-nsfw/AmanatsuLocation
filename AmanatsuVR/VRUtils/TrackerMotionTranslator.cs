using System;
using UnityEngine;
using Valve.VR;
using AmanatsuVR.Logging;
using AmanatsuVR.Config;
using ILLGAMES.Unity;

namespace AmanatsuVR.VRUtils
{
    /// <summary>
    /// Tradutor de movimento dos Trackers (HMD e controles 6DoF) para a visão de primeira pessoa
    /// e para o CameraControllerEX / CameraController do jogo Amanatsu Location.
    /// Classe puramente gerenciada para evitar erros de trampolim Il2Cpp.
    /// </summary>
    public class TrackerMotionTranslator
    {
        private VRCamera MainVRCamera { get; set; }
        private VRController OwnerVRController { get; set; }
        private CameraController ActiveCameraController { get; set; }
        public float SmoothTurnYawOffset { get; set; } = 0f;
        private float LastClickTime { get; set; } = -1f;
        private float LastCameraSearchTime { get; set; } = 0f;

        public void Setup(VRCamera vrCamera, VRController owner = null)
        {
            MainVRCamera = vrCamera;
            OwnerVRController = owner;
            PluginLog.Info("[AmanatsuVR] TrackerMotionTranslator inicializado com sucesso.");
        }

        public void Update()
        {
            HandleRecenterInput();
            HandleSmoothTurnInput();
        }

        public void LateUpdate()
        {
            TranslateTrackersToGameCamera();
        }

        /// <summary>
        /// Verifica se o usuário solicitou recentralizar a visão (duplo clique direito, tecla configurada ou controle VR).
        /// </summary>
        private void HandleRecenterInput()
        {
            bool triggerRecenter = false;

            // 1. Tecla do teclado (padrão: 'R')
            if (Input.GetKeyDown(PluginConfig.RecenterKey.Value))
            {
                triggerRecenter = true;
            }

            // 2. Duplo clique com botão direito do mouse
            if (0f < PluginConfig.DoubleClickIntervalToUpdateViewport.Value && Input.GetMouseButtonDown(1))
            {
                float now = Time.unscaledTime;
                if (now - LastClickTime <= PluginConfig.DoubleClickIntervalToUpdateViewport.Value)
                {
                    triggerRecenter = true;
                    LastClickTime = 0f;
                }
                else
                {
                    LastClickTime = now;
                }
            }

            // 3. Botão do controle SteamVR (Menu button ou clique no analógico)
            if (SteamVR.active && OpenVR.System != null)
            {
                for (uint i = 1; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
                {
                    var devClass = OpenVR.System.GetTrackedDeviceClass(i);
                    if (devClass == ETrackedDeviceClass.Controller)
                    {
                        VRControllerState_t state = default;
                        if (OpenVR.System.GetControllerState(i, ref state, (uint)System.Runtime.InteropServices.Marshal.SizeOf<VRControllerState_t>()))
                        {
                            ulong buttonMask = (1UL << (int)EVRButtonId.k_EButton_ApplicationMenu);
                            if ((state.ulButtonPressed & buttonMask) != 0)
                            {
                                triggerRecenter = true;
                                break;
                            }
                        }
                    }
                }
            }

            if (triggerRecenter)
            {
                SmoothTurnYawOffset = 0f;
                MainVRCamera?.ResetUserOffset();
                if (OwnerVRController != null)
                {
                    OwnerVRController.Recenter();
                }
                else if (MainVRCamera != null)
                {
                    VRCamera.UpdateViewport(MainVRCamera);
                }
            }
        }

        /// <summary>
        /// Suporte a rotação suave (smooth turn) através do analógico da mão direita e setas do teclado.
        /// </summary>
        private void HandleSmoothTurnInput()
        {
            // Na H o analogico e da camera livre e dos atalhos.
            if (!PluginConfig.EnableControllerSmoothTurn.Value || VRHScene.Ativo || VRCharCreation.Ativo) return;

            float turnDelta = 0f;

            // Setas do teclado (Esquerda / Direita)
            if (Input.GetKey(KeyCode.LeftArrow))
            {
                turnDelta -= 1f;
            }
            if (Input.GetKey(KeyCode.RightArrow))
            {
                turnDelta += 1f;
            }

            // The stick now moves/turns the view (VRController.MoveWithStick); only the arrow keys turn here.

            if (Mathf.Abs(turnDelta) > 0.05f)
            {
                float speed = PluginConfig.SmoothTurnSpeed.Value;
                SmoothTurnYawOffset += turnDelta * speed * Time.deltaTime;
            }
        }

        /// <summary>
        /// Traduz a orientação do jogador para o CameraController do jogo se configurado.
        /// Não injeta a rotação do HMD em CameraRot para evitar loop de rotação dupla.
        /// </summary>
        private void TranslateTrackersToGameCamera()
        {
            if (!PluginConfig.SyncHeadRotationToCameraController.Value) return;

            if (ActiveCameraController == null || !ActiveCameraController.isActiveAndEnabled)
            {
                if (Time.unscaledTime - LastCameraSearchTime > 1.0f)
                {
                    LastCameraSearchTime = Time.unscaledTime;
                    ActiveCameraController = UnityEngine.Object.FindObjectOfType<CameraController>();
                }
            }

            if (ActiveCameraController != null)
            {
                // Sincroniza apenas o yaw de giro com o corpo do jogador sem forçar a rotação do HMD
                ActiveCameraController.CameraRot = new Vector3(0f, SmoothTurnYawOffset, 0f);
            }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(CameraController), nameof(CameraController.InputMouseProc))]
    public static class CameraController_InputMouseProc_Patch
    {
        public static bool Prefix()
        {
            if (Plugin.IsVRModeActive && !PluginConfig.EnableMouseCameraDragInVR.Value)
            {
                return false;
            }
            return true;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(CameraControllerEX), nameof(CameraControllerEX.InputMouseProc))]
    public static class CameraControllerEX_InputMouseProc_Patch
    {
        public static bool Prefix()
        {
            if (Plugin.IsVRModeActive && !PluginConfig.EnableMouseCameraDragInVR.Value)
            {
                return false;
            }
            return true;
        }
    }
}
