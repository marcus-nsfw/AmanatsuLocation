using UnityEngine;
using UnityEngine.UI;
using AmanatsuVR.Logging;

namespace AmanatsuVR.VRUtils
{
    /// <summary>
    /// Arriving at the waterfall in story mode left Time.timeScale at 0 with no UI and no ADV line in the log, and
    /// nothing in the log said what the game was waiting on. After 5 s of pause this dumps, once per pause, what is
    /// on screen: active canvases with their visible graphics and any ADV scenario object.
    /// </summary>
    internal static class PauseWatch
    {
        private const float Threshold = 5f;
        private static float _pausedSince = -1f;
        private static bool _dumped;

        public static void Update()
        {
            if (Time.timeScale > 0f) { _pausedSince = -1f; _dumped = false; TimeScaleGuard.ResetReport(); return; }
            if (_pausedSince < 0f) _pausedSince = Time.unscaledTime;
            if (Time.unscaledTime - _pausedSince >= TimeScaleGuard.Grace) TimeScaleGuard.RestoreIfOrphaned();
            if (_dumped || Time.unscaledTime - _pausedSince < Threshold) return;
            _dumped = true;
            try { Dump(); }
            catch (System.Exception ex) { PluginLog.Warning($"[AmanatsuVR][PAUSA] dump falhou: {ex.Message}"); }
        }

        private static void Dump()
        {
            PluginLog.Info($"[AmanatsuVR][PAUSA] timeScale=0 ha {Threshold:F0}s; cena='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}'");
            foreach (var c in Object.FindObjectsOfType<Canvas>())
            {
                if (c == null || !c.isActiveAndEnabled || !c.isRootCanvas) continue;
                int visible = 0;
                string sample = "";
                foreach (var g in c.GetComponentsInChildren<Graphic>(false))
                {
                    if (g == null || !g.isActiveAndEnabled || g.color.a <= 0.01f) continue;
                    if (visible++ < 4) sample += $" '{g.name}'";
                }
                var cg = c.GetComponent<CanvasGroup>();
                PluginLog.Info($"[AmanatsuVR][PAUSA]   canvas '{c.name}' modo={c.renderMode} cam={(c.worldCamera != null ? c.worldCamera.name : "-")}"
                    + $" ordem={c.sortingOrder} grupoAlfa={(cg != null ? cg.alpha : 1f):F2} graficos={visible}{sample}");
            }
            foreach (var s in Object.FindObjectsOfType<ILLGAMES.ADV.TextScenario>(true))
                if (s != null) PluginLog.Info($"[AmanatsuVR][PAUSA]   ADV '{s.gameObject.name}' ativo={s.gameObject.activeInHierarchy} habilitado={s.enabled}");
            foreach (var cam in Object.FindObjectsOfType<Camera>())
                if (cam != null) PluginLog.Info($"[AmanatsuVR][PAUSA]   camera '{cam.name}' ligada={cam.enabled} far={cam.farClipPlane:F1} alvo={(cam.targetTexture != null ? cam.targetTexture.name : "-")}");
        }
    }

    /// <summary>
    /// MEASURED (IDA, xrefs of Time.set_timeScale): only MapSelectUI, SaveLoadUI, PlayerSelectUI and CharaSelectUI
    /// pause the game (plus the title). Their SetOpenCloseEvent(open) saves timeScale and sets 0; on close it restores
    /// the saved value ONLY if the component is still alive. Two ways to stay frozen forever (the waterfall freeze,
    /// map004, PauseWatch: timeScale 0 with the map UI up and none of these screens open):
    ///  - closed together with the scene change, already destroyed -> restore skipped;
    ///  - opened twice (a double click) -> the second open saves 0 as the "original" value.
    /// </summary>
    internal static class TimeScaleGuard
    {
        public const float Grace = 3f;
        private static bool _reportedOpen;
        public static void ResetReport() => _reportedOpen = false;
        private static readonly System.Collections.Generic.Dictionary<System.IntPtr, Component> _open = new();

        /// <summary>Prefix of every SetOpenCloseEvent. false = skip the game's method (duplicate open).</summary>
        public static bool OnOpenClose(Component ui, bool open)
        {
            if (ui == null) return true;
            string name = ui.GetType().Name;
            if (open)
            {
                if (_open.ContainsKey(ui.Pointer))
                {
                    PluginLog.Warning($"[AmanatsuVR][TIME] {name} aberta de novo sem fechar: ignorado (guardaria timeScale 0).");
                    return false;
                }
                if (Time.timeScale == 0f && _open.Count == 0)
                {
                    PluginLog.Warning($"[AmanatsuVR][TIME] {name} abrindo com timeScale ja 0 e nenhuma tela aberta: volta para 1 antes.");
                    Time.timeScale = 1f;
                }
                _open[ui.Pointer] = ui;
            }
            else _open.Remove(ui.Pointer);
            return true;
        }

        /// <summary>Postfix: the game skipped the restore (component gone) and nothing else is open.</summary>
        public static void AfterOpenClose(Component ui, bool open)
        {
            if (open || _open.Count > 0 || Time.timeScale != 0f) return;
            PluginLog.Warning($"[AmanatsuVR][TIME] {(ui != null ? ui.GetType().Name : "?")} fechou e o timeScale ficou 0: volta para 1.");
            Time.timeScale = 1f;
        }

        // MEASURED (IDA, 2nd pass: refs to the cached set_timeScale icall pointer - the wrapper xrefs missed them):
        // HelpWindow and ShortcutViewDialog also pause (OnAdded saves + sets, OnRemoved lambda restores), as do a
        // Timeline TimeScaleBehaviour and Manager.Timekeeper. Every one of them is logged when it changes timeScale,
        // and the two overlay windows count as "open" screens for the orphan check.
        private static readonly (System.Type type, string method)[] _setters =
        {
            (typeof(AL.HelpWindow), "Manager_Scene_IOverlap_OnAdded"),
            (typeof(AL.HelpWindow), "Manager_Scene_IOverlap_OnRemoved"),
            (typeof(AL.HelpWindow), "_Manager_Scene_IOverlap_OnRemoved_b__61_0"),
            (typeof(AL.HelpWindow), "SceneEnd"),
            (typeof(AL.ShortcutViewDialog), "Manager_Scene_IOverlap_OnAdded"),
            (typeof(AL.ShortcutViewDialog), "Manager_Scene_IOverlap_OnRemoved"),
            (typeof(AL.ShortcutViewDialog), "_ctor_b__29_1"),
            (typeof(AL.TimeLine.TimeScaleBehaviour), "OnBehaviourPlay"),
            (typeof(AL.TimeLine.TimeScaleBehaviour), "OnBehaviourPause"),
            (typeof(Manager.Timekeeper), "set_TimeScale"),
            (typeof(AL.MapSelectUI), "SetOpenCloseEvent"),
            (typeof(AL.UI.SaveLoad.SaveLoadUI), "SetOpenCloseEvent"),
            (typeof(AL.UI.PlayerSelect.PlayerSelectUI), "SetOpenCloseEvent"),
            (typeof(AL.UI.CharaSelect.CharaSelectUI), "SetOpenCloseEvent"),
        };

        public static void PatchSetters()
        {
            var h = new HarmonyLib.Harmony("com.marcus.amanatsu.vr.time");
            var pre = new HarmonyLib.HarmonyMethod(typeof(TimeScaleGuard), nameof(LogPrefix));
            var post = new HarmonyLib.HarmonyMethod(typeof(TimeScaleGuard), nameof(LogPostfix));
            int ok = 0;
            foreach (var (type, method) in _setters)
            {
                try
                {
                    var m = HarmonyLib.AccessTools.Method(type, method);
                    if (m == null) { PluginLog.Warning($"[AmanatsuVR][TIME] {type.Name}.{method} nao existe"); continue; }
                    h.Patch(m, pre, post); ok++;
                }
                catch (System.Exception ex) { PluginLog.Warning($"[AmanatsuVR][TIME] patch {type.Name}.{method} falhou: {ex.Message}"); }
            }
            PluginLog.Info($"[AmanatsuVR][TIME] {ok}/{_setters.Length} pontos que mexem no timeScale monitorados.");
        }

        public static void LogPrefix(out float __state) => __state = Time.timeScale;

        public static void LogPostfix(System.Reflection.MethodBase __originalMethod, object __instance, float __state)
        {
            string who = $"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}";
            if (__instance is Component c && (who.StartsWith("HelpWindow.Manager") || who.StartsWith("ShortcutViewDialog.Manager")))
            {
                if (__originalMethod.Name.EndsWith("OnAdded")) _open[c.Pointer] = c; else _open.Remove(c.Pointer);
            }
            if (Time.timeScale != __state)
                PluginLog.Info($"[AmanatsuVR][TIME] {who}: timeScale {__state:F2} -> {Time.timeScale:F2} (telas abertas {_open.Count})");
        }

        /// <summary>Paused for a while outside the title: drop destroyed screens; nothing left open -> unfreeze.</summary>
        public static void RestoreIfOrphaned()
        {
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Title") return;
            var dead = new System.Collections.Generic.List<System.IntPtr>();
            // Last freeze: no [TIME] line at all -> a screen was still registered open and alive (opened, never closed).
            // A registered screen whose object is no longer active in the scene is treated as closed too.
            foreach (var kv in _open)
                if (kv.Value == null || !kv.Value.gameObject.activeInHierarchy)
                {
                    dead.Add(kv.Key);
                    PluginLog.Info($"[AmanatsuVR][TIME] tela registrada aberta mas {(kv.Value == null ? "destruida" : $"inativa: {kv.Value.GetType().Name}")}");
                }
            foreach (var k in dead) _open.Remove(k);
            if (_open.Count > 0)
            {
                if (!_reportedOpen)
                {
                    _reportedOpen = true;
                    foreach (var kv in _open)
                        PluginLog.Info($"[AmanatsuVR][TIME] pausado com tela aberta: {kv.Value.GetType().Name} '{kv.Value.name}' ativo={kv.Value.gameObject.activeInHierarchy}");
                }
                return;
            }
            PluginLog.Warning($"[AmanatsuVR][TIME] timeScale 0 ha {Grace:F0}s sem nenhuma tela de pausa aberta ({dead.Count} destruida(s)): volta para 1.");
            Time.timeScale = 1f;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(AL.MapSelectUI), nameof(AL.MapSelectUI.SetOpenCloseEvent))]
    internal static class MapSelectUI_SetOpenCloseEvent_Patch
    {
        public static bool Prefix(AL.MapSelectUI __instance, bool open) => TimeScaleGuard.OnOpenClose(__instance, open);
        public static void Postfix(AL.MapSelectUI __instance, bool open) => TimeScaleGuard.AfterOpenClose(__instance, open);
    }

    [HarmonyLib.HarmonyPatch(typeof(AL.UI.SaveLoad.SaveLoadUI), nameof(AL.UI.SaveLoad.SaveLoadUI.SetOpenCloseEvent))]
    internal static class SaveLoadUI_SetOpenCloseEvent_Patch
    {
        public static bool Prefix(AL.UI.SaveLoad.SaveLoadUI __instance, bool open) => TimeScaleGuard.OnOpenClose(__instance, open);
        public static void Postfix(AL.UI.SaveLoad.SaveLoadUI __instance, bool open) => TimeScaleGuard.AfterOpenClose(__instance, open);
    }

    [HarmonyLib.HarmonyPatch(typeof(AL.UI.PlayerSelect.PlayerSelectUI), nameof(AL.UI.PlayerSelect.PlayerSelectUI.SetOpenCloseEvent))]
    internal static class PlayerSelectUI_SetOpenCloseEvent_Patch
    {
        public static bool Prefix(AL.UI.PlayerSelect.PlayerSelectUI __instance, bool open) => TimeScaleGuard.OnOpenClose(__instance, open);
        public static void Postfix(AL.UI.PlayerSelect.PlayerSelectUI __instance, bool open) => TimeScaleGuard.AfterOpenClose(__instance, open);
    }

    [HarmonyLib.HarmonyPatch(typeof(AL.UI.CharaSelect.CharaSelectUI), nameof(AL.UI.CharaSelect.CharaSelectUI.SetOpenCloseEvent))]
    internal static class CharaSelectUI_SetOpenCloseEvent_Patch
    {
        public static bool Prefix(AL.UI.CharaSelect.CharaSelectUI __instance, bool open) => TimeScaleGuard.OnOpenClose(__instance, open);
        public static void Postfix(AL.UI.CharaSelect.CharaSelectUI __instance, bool open) => TimeScaleGuard.AfterOpenClose(__instance, open);
    }
}
