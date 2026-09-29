using System;
using System.Diagnostics;
using System.Linq;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using AmanatsuVR.Logging;
using AmanatsuVR.Config;
using AmanatsuVR.VRUtils;

namespace AmanatsuVR
{
    [BepInPlugin("com.marcus.amanatsu.vr", "Amanatsu VR Mod", "1.0.0")]
    public class Plugin : BasePlugin
    {
        public static bool IsVRModeActive { get; private set; } = false;

        // Mantém referência estática do delegate para impedir coleta prematura pelo GC no IL2CPP
        private static UnityAction<Scene, LoadSceneMode> _sceneLoadedDelegate;
        private static VRController _activeVRController;

        public override void Load()
        {
            PluginLog.Setup(Log);

            // ========================================================================
            // Verificação de Inicialização: Desktop vs VR
            // O jogo principal deve continuar 100% funcional no desktop.
            // O modo VR só é ativado se for passado o argumento '--vr' ou 'AMANATSU_VR=1'.
            // ========================================================================
            var cmdArgs = Environment.GetCommandLineArgs();
            bool hasVrArg = cmdArgs.Any(arg =>
                arg.Equals("--vr", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-vr", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-vrmode", StringComparison.OrdinalIgnoreCase));
            bool hasVrEnv = Environment.GetEnvironmentVariable("AMANATSU_VR") == "1";

            if (!hasVrArg && !hasVrEnv)
            {
                PluginLog.Info("---------------------------------------------------------------");
                PluginLog.Info("[AmanatsuVR] Modo Desktop detectado (VR inativo).");
                PluginLog.Info("[AmanatsuVR] O jogo continuara funcionando normalmente no monitor.");
                PluginLog.Info("[AmanatsuVR] Para jogar em VR, inicie pelo atalho 'Iniciar_VR.bat'.");
                PluginLog.Info("---------------------------------------------------------------");

                // Unica coisa que roda sem VR: o dump da pele na tela de criacao. E o controle -
                // sem ele nao da para saber se a garota branca e culpa do VR ou do jogo.
                try
                {
                    HarmonyLib.Harmony.CreateAndPatchAll(
                        typeof(VRUtils.HumanCustomUpdatePatch), "com.marcus.amanatsu.vr.diag");
                    HarmonyLib.Harmony.CreateAndPatchAll(
                        typeof(VRUtils.CharInitializePatch), "com.marcus.amanatsu.vr.diag");
                    HarmonyLib.Harmony.CreateAndPatchAll(
                        typeof(VRUtils.CharStartPatch), "com.marcus.amanatsu.vr.diag");
                    HarmonyLib.Harmony.CreateAndPatchAll(
                        typeof(VRUtils.CharLoadCharaPatch), "com.marcus.amanatsu.vr.diag");
                    HarmonyLib.Harmony.CreateAndPatchAll(
                        typeof(VRUtils.CharSetReceiverPatch), "com.marcus.amanatsu.vr.diag");
                }
                catch (Exception ex) { PluginLog.Warning($"[AmanatsuVR] patch de diagnostico falhou: {ex.Message}"); }
                return;
            }

            IsVRModeActive = true;
            PluginLog.Info("===============================================================");
            PluginLog.Info("[AmanatsuVR] Modo VR ATIVADO via inicializador!");
            PluginLog.Info("===============================================================");

            PluginConfig.Setup(Config);

            // Com o headset na cabeça a janela do desktop perde o foco o tempo todo.
            // Sem isto o Unity congela Input e Update inteiros e NENHUM controle responde.
            Application.runInBackground = true;

            try
            {
                HarmonyLib.Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, "com.marcus.amanatsu.vr");
                PluginLog.Info("[AmanatsuVR] Patches Harmony de VR aplicados com sucesso.");
                VRUtils.TimeScaleGuard.PatchSetters();
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"[AmanatsuVR] Aviso ao aplicar patches Harmony: {ex.Message}");
            }

            // Inicializar o subsistema OpenVR e SteamVR
            VR.Initialize(() =>
            {
                PluginLog.Info("[AmanatsuVR] Subsistema VR pronto! Criando VRController persistente...");

                // volumeDepth > 1 => texture array => Single Pass Instanced. Os shaders do jogo
                // foram compilados sem estéreo e só escrevem na fatia 0 (olho esquerdo) nesse modo.
                var desc = UnityEngine.XR.XRSettings.eyeTextureDesc;
                PluginLog.Info($"[AmanatsuVR] XR: enabled={UnityEngine.XR.XRSettings.enabled}, " +
                    $"stereoRenderingMode={UnityEngine.XR.XRSettings.stereoRenderingMode}, " +
                    $"eyeTexture={desc.width}x{desc.height} volumeDepth={desc.volumeDepth}, " +
                    $"device='{UnityEngine.XR.XRSettings.loadedDeviceName}'");

                SteamVRInput.Initialize();

                // Cria o VRController persistente através das cenas
                EnsureVRController();

                _sceneLoadedDelegate = (UnityAction<Scene, LoadSceneMode>)OnSceneLoaded;
                SceneManager.sceneLoaded += _sceneLoadedDelegate;
            });
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!IsVRModeActive) return;

            PluginLog.Info($"[AmanatsuVR] Cena carregada: '{scene.name}' (Modo: {mode})");
            EnsureVRController();

            if (_activeVRController != null)
            {
                _activeVRController.NotifySceneLoaded(scene);
            }
        }

        private static void EnsureVRController()
        {
            if (_activeVRController == null)
            {
                var existing = GameObject.Find("AmanatsuVR_Root");
                if (existing == null)
                {
                    var root = new GameObject("AmanatsuVR_Root");
                    GameObject.DontDestroyOnLoad(root);
                    _activeVRController = root.AddComponent<VRController>();
                    PluginLog.Info("[AmanatsuVR] AmanatsuVR_Root persistente criado com sucesso.");
                }
                else
                {
                    _activeVRController = existing.GetComponent<VRController>();
                }
            }
        }
    }
}
