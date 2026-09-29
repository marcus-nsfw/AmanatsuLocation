using BepInEx.Logging;

namespace Amanatsu.Uncensor
{
    // Info/Debug so vao para o LogOutput.log em build Debug; Warning/Error (LogWarning/LogError,
    // sem wrapper) continuam sempre ativos.
    internal static class DebugLog
    {
        [System.Diagnostics.Conditional("DEBUG")]
        public static void Info(this ManualLogSource log, object data) => log.LogInfo(data);

        [System.Diagnostics.Conditional("DEBUG")]
        public static void Debug(this ManualLogSource log, object data) => log.LogDebug(data);
    }
}
