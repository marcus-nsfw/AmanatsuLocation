using BepInEx.Logging;

namespace AmanatsuVR.Logging
{
    public static class PluginLog
    {
        private static ManualLogSource Log { get; set; }

        /// <summary>
        /// O BepInEx sobrescreve LogOutput.log a cada início, então abrir o jogo de novo apaga
        /// o registro da execução anterior — foi o que aconteceu e custou uma rodada de teste.
        /// Este arquivo é em ANEXO: cada execução acrescenta, nada é perdido.
        /// </summary>
        private static string DiagPath { get; set; }

        public static void Setup(ManualLogSource log)
        {
            Log = log;
            try
            {
                string dir = System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "AmanatsuVR_diag");
                System.IO.Directory.CreateDirectory(dir);
                DiagPath = System.IO.Path.Combine(dir, "vr_historico.log");
                System.IO.File.AppendAllText(DiagPath,
                    $"\n===== sessão iniciada {System.DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\n");
            }
            catch { DiagPath = null; }
        }

        private static void Append(string level, string message)
        {
            if (DiagPath == null) return;
            try
            {
                System.IO.File.AppendAllText(DiagPath, $"{System.DateTime.Now:HH:mm:ss} [{level}] {message}\n");
            }
            catch { }
        }

        // Info/Debug sao o log verboso (um lugar so, ligado por Debug/Release em vez de comentar
        // chamada por chamada): so vao para o LogOutput.log e para o vr_historico.log em build Debug.
        [System.Diagnostics.Conditional("DEBUG")]
        public static void Info(string message) { Log?.LogInfo(message); Append("INFO", message); }
        [System.Diagnostics.Conditional("DEBUG")]
        public static void Debug(string message) { Log?.LogDebug(message); Append("DEBUG", message); }
        public static void Warning(string message) { Log?.LogWarning(message); Append("AVISO", message); }
        public static void Error(string message) { Log?.LogError(message); Append("ERRO", message); }
    }
}
