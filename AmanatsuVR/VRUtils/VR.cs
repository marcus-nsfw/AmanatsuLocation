using System;
using System.Collections;
using BepInEx.Unity.IL2CPP.Utils;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Unity.XR.OpenVR;
using UnityEngine;
using Valve.VR;
using AmanatsuVR.Logging;

namespace AmanatsuVR.VRUtils
{
    public class VR : MonoBehaviour
    {
        static VR() { ClassInjector.RegisterTypeInIl2Cpp<VR>(); }

        public static bool Initialized { get; private set; } = false;

        [HideFromIl2Cpp]
        public static void Initialize(Action actionAfterInitialization, bool force = false)
        {
            if (force || !Initialized)
            {
                Initialized = false;
                ActionAfterInitialization = actionAfterInitialization;
                new GameObject(nameof(VR)) { hideFlags = HideFlags.HideAndDontSave }.AddComponent<VR>();
            }
        }

        private static Action ActionAfterInitialization { get; set; }

        void Start()
        {
            this.StartCoroutine(Setup());
        }

        [HideFromIl2Cpp]
        private IEnumerator Setup()
        {
            PluginLog.Info("[AmanatsuVR] Iniciando inicialização do subsistema OpenVR / SteamVR...");

            try
            {
                try
                {
                    try
                    {
                        var settings = OpenVRSettings.GetSettings(true);
                        if (settings != null)
                        {
                            // Entities Graphics (BRG) desenha o cenário e não suporta multipass.
                            bool instanced = AmanatsuVR.Config.PluginConfig.UseSinglePassInstanced.Value;
                            settings.StereoRenderingMode = instanced
                                ? OpenVRSettings.StereoRenderingModes.SinglePassInstanced
                                : OpenVRSettings.StereoRenderingModes.MultiPass;

                            // GetSettings(true) devolve uma instância NOVA sem guardá-la em s_Settings
                            // quando o jogo não foi compilado com o plugin XR. Resultado: o loader
                            // chama GetSettings() de novo, recebe outra instância com o padrão, e a
                            // nossa escolha é descartada em silêncio. Fixar o campo estático resolve.
                            var field = typeof(OpenVRSettings).GetField("s_Settings",
                                System.Reflection.BindingFlags.Static
                                | System.Reflection.BindingFlags.NonPublic
                                | System.Reflection.BindingFlags.Public);
                            if (field != null)
                            {
                                field.SetValue(null, settings);
                            }
                            else
                            {
                                PluginLog.Warning("[AmanatsuVR] campo OpenVRSettings.s_Settings não encontrado.");
                            }

                            var applied = OpenVRSettings.GetSettings(false);
                            PluginLog.Info($"[AmanatsuVR] StereoRenderingMode pedido={settings.StereoRenderingMode}"
                                + $" | o loader vai ler={(applied != null ? applied.StereoRenderingMode.ToString() : "NULO")}");
                        }
                    }
                    catch (Exception setEx)
                    {
                        PluginLog.Warning($"[AmanatsuVR] Não foi possível definir StereoRenderingMode: {setEx.Message}");
                    }

                    var vrLoader = ScriptableObject.CreateInstance<OpenVRLoader>();
                    if (vrLoader.Initialize())
                    {
                        PluginLog.Info("[AmanatsuVR] OpenVRLoader.Initialize bem-sucedido!");
                    }
                    else
                    {
                        PluginLog.Error("[AmanatsuVR] Falha em OpenVRLoader.Initialize.");
                        yield break;
                    }

                    if (vrLoader.Start())
                    {
                        PluginLog.Info("[AmanatsuVR] OpenVRLoader.Start bem-sucedido!");
                    }
                    else
                    {
                        PluginLog.Error("[AmanatsuVR] Falha em OpenVRLoader.Start.");
                        yield break;
                    }

                    SteamVR_Behaviour.Initialize(false);
                }
                catch (Exception e)
                {
                    PluginLog.Error($"[AmanatsuVR] Erro durante inicialização do SteamVR: {e}");
                    yield break;
                }

                // Espera por inicialização do SteamVR com timeout de 20 segundos
                float elapsed = 0f;
                const float timeout = 20f;

                while (elapsed < timeout)
                {
                    switch (SteamVR.initializedState)
                    {
                        case SteamVR.InitializedStates.InitializeSuccess:
                            PluginLog.Info("[AmanatsuVR] SteamVR inicializado com sucesso total!");
                            Initialized = true;
                            ActionAfterInitialization?.Invoke();
                            yield break;

                        case SteamVR.InitializedStates.InitializeFailure:
                            PluginLog.Error("[AmanatsuVR] Falha na inicialização do SteamVR.");
                            yield break;

                        default:
                            yield return new WaitForSeconds(0.1f);
                            elapsed += 0.1f;
                            continue;
                    }
                }

                PluginLog.Warning($"[AmanatsuVR] Timeout ({timeout}s) aguardando prontidão do SteamVR.");
            }
            finally
            {
                PluginLog.Info("[AmanatsuVR] Concluindo inicializador temporário VR.");
                Destroy(gameObject);
            }
        }
    }
}
