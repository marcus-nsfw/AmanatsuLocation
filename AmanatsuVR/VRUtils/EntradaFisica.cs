using System.Runtime.InteropServices;
using UnityEngine;

namespace AmanatsuVR.VRUtils
{
    /// <summary>
    /// Entrada de verdade do Windows. Os getters de Input do jogo foram inlinados na compilacao
    /// AOT e o Harmony nao os alcanca (mousePosition, botao do ADV, tiro da pistola); o unico
    /// jeito de o jogo ler uma tecla, um botao ou a roda e ela existir no sistema.
    /// So age com a janela em foco, senao a entrada vai parar em outro programa.
    /// </summary>
    public static class EntradaFisica
    {
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, System.IntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, System.IntPtr extra);

        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const int WHEEL_DELTA = 120;

        /// <summary>
        /// Aperta ou solta a tecla e o botao do mouse juntos (qualquer um pode ser None/-1).
        /// Devolve false se nao apertou por falta de foco, para quem chama tentar de novo.
        /// </summary>
        public static bool Segura(KeyCode tecla, int botao, bool apertado)
        {
            if (apertado && !Application.isFocused) return false;
            try
            {
                int vk = VirtualKeyDe(tecla);
                if (vk != 0) keybd_event((byte)vk, 0, apertado ? 0u : KEYEVENTF_KEYUP, System.IntPtr.Zero);

                uint flag = FlagDoBotao(botao, apertado);
                if (flag != 0) mouse_event(flag, 0, 0, 0, System.IntPtr.Zero);
            }
            catch { }
            return true;
        }

        /// <summary>Um degrau da roda do mouse; positivo = para cima.</summary>
        public static void Roda(int degraus)
        {
            if (!Application.isFocused) return;
            try { mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(degraus * WHEEL_DELTA)), System.IntPtr.Zero); }
            catch { }
        }

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, System.IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(System.IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(System.IntPtr hWnd, System.Text.StringBuilder s, int max);
        [DllImport("user32.dll")] private static extern bool IsIconic(System.IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(System.IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(System.IntPtr hWnd);
        [DllImport("user32.dll")] private static extern System.IntPtr GetForegroundWindow();
        private delegate bool EnumWindowsProc(System.IntPtr hWnd, System.IntPtr lParam);

        private const int SW_RESTORE = 9;
        private const byte VK_MENU = 0x12;
        private static System.IntPtr _windowHandle;

        /// <summary>
        /// A janela principal do jogo (UnityWndClass deste processo). GetActiveWindow nao serve:
        /// devolve zero justamente quando o jogo esta sem foco, que e quando precisamos dela.
        /// </summary>
        public static System.IntPtr WindowHandle
        {
            get
            {
                if (_windowHandle != System.IntPtr.Zero) return _windowHandle;
                uint meu = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                EnumWindows((h, _) =>
                {
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pid != meu || !IsWindowVisible(h)) return true;
                    var sb = new System.Text.StringBuilder(64);
                    GetClassName(h, sb, sb.Capacity);
                    if (sb.ToString() != "UnityWndClass") return true;
                    _windowHandle = h;
                    return false;
                }, System.IntPtr.Zero);
                return _windowHandle;
            }
        }

        /// <summary>
        /// Restaura (se minimizada) e traz a janela do jogo para frente. Sem isso o jogo abria
        /// as vezes minimizado ou atras de outra janela, e o cursor e os cliques do controle
        /// iam parar no Windows. O Windows so deixa trocar o foco de quem recebeu a ultima
        /// entrada; um Alt sintetico antes do SetForegroundWindow cumpre essa regra.
        /// </summary>
        public static bool FocaJanela(string motivo)
        {
            try
            {
                var h = WindowHandle;
                if (h == System.IntPtr.Zero) return false;
                if (GetForegroundWindow() == h && !IsIconic(h)) return true;

                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                keybd_event(VK_MENU, 0, 0, System.IntPtr.Zero);
                bool ok = SetForegroundWindow(h);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, System.IntPtr.Zero);
                AmanatsuVR.Logging.PluginLog.Info($"[AmanatsuVR][FOCO] {motivo}: janela trazida para frente ok={ok}");
                return ok;
            }
            catch { return false; }
        }

        /// <summary>KeyCode do Unity para virtual-key do Windows. So o necessario para as teclas de tiro.</summary>
        private static int VirtualKeyDe(KeyCode k)
        {
            if (k == KeyCode.Space) return 0x20;
            if (k >= KeyCode.A && k <= KeyCode.Z) return 'A' + (k - KeyCode.A);
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return '0' + (k - KeyCode.Alpha0);
            if (k == KeyCode.Return) return 0x0D;
            if (k == KeyCode.LeftControl || k == KeyCode.RightControl) return 0x11;
            if (k == KeyCode.LeftShift || k == KeyCode.RightShift) return 0x10;
            return 0;
        }

        /// <summary>Indice de botao do Unity (0/1/2) para a flag do mouse_event.</summary>
        private static uint FlagDoBotao(int botao, bool apertado)
        {
            switch (botao)
            {
                case 0: return apertado ? 0x0002u : 0x0004u; // esquerdo
                case 1: return apertado ? 0x0008u : 0x0010u; // direito
                case 2: return apertado ? 0x0020u : 0x0040u; // meio
                default: return 0u;
            }
        }
    }
}
