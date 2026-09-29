using System;
using System.Runtime.InteropServices;
using AmanatsuVR.Logging;

namespace AmanatsuVR.VRUtils
{
    /// <summary>
    /// Dispara capturas do RenderDoc por codigo.
    ///
    /// O renderdoc.dll ja foi carregado pelo patcher (AmanatsuRdoc), antes do Unity
    /// criar o device. Aqui so pegamos o modulo que ja esta no processo - sem isso
    /// dependeriamos do F12, que exige foco da janela do desktop e portanto nao
    /// funciona com o headset na cabeca (mesmo problema que matou o F10).
    /// </summary>
    public static class RenderDocApi
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        [DllImport("kernel32")]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private const int ApiVersion140 = 10400;

        // Indices na struct RENDERDOC_API_1_4_0 (array de ponteiros de funcao).
        private const int IdxGetNumCaptures = 13;
        private const int IdxTriggerMultiFrameCapture = 22;

        private delegate int GetApiDelegate(int version, out IntPtr outApiPointers);
        private delegate uint GetNumCapturesDelegate();
        private delegate void TriggerMultiFrameCaptureDelegate(uint numFrames);

        private static IntPtr _api = IntPtr.Zero;
        private static bool _resolved;

        public static bool Available => Resolve() != IntPtr.Zero;

        private static IntPtr Resolve()
        {
            if (_resolved) return _api;
            _resolved = true;

            var module = GetModuleHandleW("renderdoc.dll");
            if (module == IntPtr.Zero) return IntPtr.Zero;

            var getApiPtr = GetProcAddress(module, "RENDERDOC_GetAPI");
            if (getApiPtr == IntPtr.Zero) return IntPtr.Zero;

            var getApi = Marshal.GetDelegateForFunctionPointer<GetApiDelegate>(getApiPtr);
            if (getApi(ApiVersion140, out var api) != 1) return IntPtr.Zero;

            _api = api;
            return _api;
        }

        private static T Fn<T>(int index) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(_api, index * IntPtr.Size));

        /// <summary>
        /// Captura os proximos quadros inteiros. Em estereo multipass, um quadro cobre
        /// os dois olhos, mas pedimos alguns para nao cair num quadro de transicao.
        /// </summary>
        public static void Capture(uint frames = 3)
        {
            if (Resolve() == IntPtr.Zero)
            {
                PluginLog.Warning("[RDOC] renderdoc.dll nao esta no processo. Inicie por Iniciar_VR_RenderDoc.bat.");
                return;
            }

            Fn<TriggerMultiFrameCaptureDelegate>(IdxTriggerMultiFrameCapture)(frames);
            PluginLog.Info($"[RDOC] captura de {frames} quadros pedida (total ate agora: {Fn<GetNumCapturesDelegate>(IdxGetNumCaptures)()})");
        }
    }
}
