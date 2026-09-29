using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ILLGAMES.Unity.UI;
using ILLGAMES.ADV;
using CharacterCreation.UI.View;

namespace Amanatsu.Translation
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class TranslationPlugin : BasePlugin
    {
        public const string PluginGuid = "com.amanatsu.translation";
        public const string PluginName = "Amanatsu Translation Plugin";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Logger;
        private static readonly Dictionary<string, string> _translations = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _normalizedTranslations = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _strippedTranslations = new(StringComparer.Ordinal);
        private static readonly Dictionary<IntPtr, float> _originalFontSizes = new();

        public override void Load()
        {
            Logger = Log;
            Logger.Info("Amanatsu Translation Plugin initializing...");

            LoadTranslations();

            var harmony = new Harmony(PluginGuid);

            // 1. TextMeshPro Hooks (Runtime e Prefab Lifecycle)
            SafePatch(() => harmony.Patch(
                AccessTools.Method(typeof(TMP_Text), nameof(TMP_Text.SetText), new Type[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(TranslationPlugin), nameof(TMP_Text_SetText_Prefix))
            ), "TMP_Text.SetText(string)");

            SafePatch(() => harmony.Patch(
                AccessTools.PropertySetter(typeof(TMP_Text), nameof(TMP_Text.text)),
                prefix: new HarmonyMethod(typeof(TranslationPlugin), nameof(TMP_Text_set_text_Prefix))
            ), "TMP_Text.text (Setter)");

            SafePatch(() => harmony.Patch(
                AccessTools.Method(typeof(TextMeshProUGUI), "OnEnable"),
                postfix: new HarmonyMethod(typeof(TranslationPlugin), nameof(TextMeshProUGUI_OnEnable_Postfix))
            ), "TMPro.TextMeshProUGUI.OnEnable");

            SafePatch(() => harmony.Patch(
                AccessTools.Method(typeof(TextMeshProUGUI), "Awake"),
                postfix: new HarmonyMethod(typeof(TranslationPlugin), nameof(TextMeshProUGUI_Awake_Postfix))
            ), "TMPro.TextMeshProUGUI.Awake");

            // 2. Unity UI Text Hooks
            SafePatch(() => harmony.Patch(
                AccessTools.PropertySetter(typeof(Text), nameof(Text.text)),
                prefix: new HarmonyMethod(typeof(TranslationPlugin), nameof(Text_set_text_Prefix))
            ), "UnityEngine.UI.Text.text (Setter)");

            SafePatch(() => harmony.Patch(
                AccessTools.Method(typeof(Text), "OnEnable"),
                postfix: new HarmonyMethod(typeof(TranslationPlugin), nameof(Text_OnEnable_Postfix))
            ), "UnityEngine.UI.Text.OnEnable");

            // 3. ILLGAMES Specific UI Hooks
            SafePatch(() => harmony.Patch(
                AccessTools.Method(typeof(UIText), nameof(UIText.SetText), new Type[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(TranslationPlugin), nameof(UIText_SetText_Prefix))
            ), "ILLGAMES.Unity.UI.UIText.SetText(string)");

            SafePatch(() => harmony.Patch(
                AccessTools.Method(typeof(TextScenario), nameof(TextScenario.ReplaceText)),
                postfix: new HarmonyMethod(typeof(TranslationPlugin), nameof(TextScenario_ReplaceText_Postfix))
            ), "ILLGAMES.ADV.TextScenario.ReplaceText");

            SafePatch(() => harmony.Patch(
                AccessTools.Method(typeof(LocalizeTranslater), nameof(LocalizeTranslater.GetText)),
                postfix: new HarmonyMethod(typeof(TranslationPlugin), nameof(LocalizeTranslater_GetText_Postfix))
            ), "CharacterCreation.UI.View.LocalizeTranslater.GetText");

            // 4. ADV TextController.Set (Diálogos Principais e Diálogos de Sistema)
            SafePatch(() => harmony.Patch(
                AccessTools.Method(typeof(TextController), nameof(TextController.Set), new Type[] { typeof(string), typeof(string) }),
                prefix: new HarmonyMethod(typeof(TranslationPlugin), nameof(TextController_Set_Prefix))
            ), "ILLGAMES.ADV.TextController.Set");

            // 5. ADV Choice (Opções de Escolha)
            SafePatch(() => harmony.Patch(
                AccessTools.PropertySetter(typeof(Choice), nameof(Choice.Text)),
                prefix: new HarmonyMethod(typeof(TranslationPlugin), nameof(Choice_set_Text_Prefix))
            ), "ILLGAMES.ADV.Choice.Text (Setter)");

            // 6. ButtonText e ToggleText
            SafePatch(() => {
                var method = AccessTools.Method("ButtonText:set_Text", new Type[] { typeof(string) });
                if (method != null)
                {
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(TranslationPlugin), nameof(ButtonText_set_Text_Prefix)));
                }
            }, "ButtonText.set_Text");

            SafePatch(() => {
                var method = AccessTools.Method("ToggleText:set_text", new Type[] { typeof(string) });
                if (method != null)
                {
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(TranslationPlugin), nameof(ToggleText_set_text_Prefix)));
                }
            }, "ToggleText.set_text");

            // 7. Localize.Translate.Manager.get_IsTranslate (Sempre ativo)
            SafePatch(() => {
                var method = AccessTools.Method("Localize.Translate.Manager:get_IsTranslate");
                if (method != null)
                {
                    harmony.Patch(method, postfix: new HarmonyMethod(typeof(TranslationPlugin), nameof(Manager_get_IsTranslate_Postfix)));
                }
            }, "Localize.Translate.Manager.get_IsTranslate");

            Logger.Info("Amanatsu Translation initialization complete!");
        }

        private static void SafePatch(Action patchAction, string name)
        {
            try
            {
                patchAction();
                Logger.Info($"Hook [{name}] applied successfully!");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Hook [{name}] unavailable: {ex.Message}");
            }
        }

        private void LoadTranslations()
        {
            try
            {
                string gameDir = Paths.GameRootPath;
                string transDir = Path.Combine(gameDir, "translation");

                if (!Directory.Exists(transDir))
                {
                    transDir = Path.Combine(Paths.BepInExRootPath, "Translation");
                }

                if (!Directory.Exists(transDir))
                {
                    Logger.LogWarning($"Translation directory not found at: {transDir}");
                    return;
                }

                // 1. Carrega UI Menus
                string uiPath = Path.Combine(transDir, "ui_menus.json");
                if (File.Exists(uiPath))
                {
                    string json = File.ReadAllText(uiPath);
                    using var doc = JsonDocument.Parse(json);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        var elem = prop.Value;
                        string jp = elem.TryGetProperty("jp", out var jpProp) ? jpProp.GetString() ?? "" : "";
                        string en = elem.TryGetProperty("en", out var enProp) ? enProp.GetString() ?? "" : "";
                        string tl = elem.TryGetProperty("translation", out var tlProp) ? tlProp.GetString() ?? "" : "";

                        string chosen = !string.IsNullOrWhiteSpace(tl) ? tl : (!string.IsNullOrWhiteSpace(en) ? en : jp);

                        if (!string.IsNullOrEmpty(jp) && !string.IsNullOrEmpty(chosen) && jp != chosen)
                        {
                            AddTranslation(jp, chosen);
                        }

                        if (!string.IsNullOrEmpty(en) && !string.IsNullOrEmpty(tl) && en != tl)
                        {
                            AddTranslation(en, tl);
                        }
                    }
                }

                // 2. Carrega Tutorial e ui_extra (textos fora do localization/info: poses da H, criacao de
                // personagem, listas e strings do codigo) - mesmo formato: lista de {jp, translation}
                foreach (string tutPath in new[] { Path.Combine(transDir, "tutorial.json"), Path.Combine(transDir, "ui_extra.json") })
                if (File.Exists(tutPath))
                {
                    string tutJson = File.ReadAllText(tutPath);
                    using var tutDoc = JsonDocument.Parse(tutJson);
                    if (tutDoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in tutDoc.RootElement.EnumerateArray())
                        {
                            string jp = item.TryGetProperty("jp", out var jpP) ? jpP.GetString() ?? "" : "";
                            string tl = item.TryGetProperty("translation", out var tlP) ? tlP.GetString() ?? "" : "";
                            if (!string.IsNullOrEmpty(jp) && !string.IsNullOrEmpty(tl) && jp != tl)
                            {
                                AddTranslation(jp, tl);
                            }
                        }
                    }
                }

                // 3. Carrega Cenas ADV
                string advDir = Path.Combine(transDir, "adv");
                if (Directory.Exists(advDir))
                {
                    foreach (var file in Directory.GetFiles(advDir, "*.json"))
                    {
                        try
                        {
                            string json = File.ReadAllText(file);
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in doc.RootElement.EnumerateArray())
                                {
                                    string jp = item.TryGetProperty("jp", out var jpProp) ? jpProp.GetString() ?? "" : "";
                                    string tl = item.TryGetProperty("translation", out var tlProp) ? tlProp.GetString() ?? "" : "";

                                    if (!string.IsNullOrEmpty(jp) && !string.IsNullOrEmpty(tl) && jp != tl)
                                    {
                                        AddTranslation(jp, tl);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"Error reading ADV scene {file}: {ex.Message}");
                        }
                    }
                }

                Logger.Info($"Total unique translations loaded: {_translations.Count} (Normalized: {_normalizedTranslations.Count}, Stripped: {_strippedTranslations.Count})");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Fatal error loading translations: {ex}");
            }
        }

        private static void AddTranslation(string original, string translation)
        {
            if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(translation))
                return;

            _translations[original] = translation;

            string norm = NormalizeText(original);
            if (!string.IsNullOrEmpty(norm))
                _normalizedTranslations[norm] = translation;

            string stripped = StripWhitespace(original);
            if (!string.IsNullOrEmpty(stripped))
                _strippedTranslations[stripped] = translation;
        }

        private static string NormalizeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new System.Text.StringBuilder(text.Length);
            bool lastWasSpace = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r') continue;
                if (c == '\u3000' || c == '\u00A0' || c == '\t' || c == ' ')
                {
                    if (!lastWasSpace)
                    {
                        sb.Append(' ');
                        lastWasSpace = true;
                    }
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
            }
            return sb.ToString().Trim();
        }

        private static string StripWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\u3000' || c == '\u00A0')
                    continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool HasJapanese(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if ((c >= 0x3040 && c <= 0x30FF) || (c >= 0x4E00 && c <= 0x9FFF))
                    return true;
            }
            return false;
        }

        public static void EnsureAutoSizing(TMP_Text textComponent, float minRatio = 0.60f, float minAbsolute = 14f)
        {
            if (textComponent == null) return;
            try
            {
                IntPtr ptr = textComponent.Pointer;
                if (!_originalFontSizes.TryGetValue(ptr, out float origSize))
                {
                    origSize = textComponent.fontSize;
                    if (origSize <= 0) origSize = 28f;
                    _originalFontSizes[ptr] = origSize;
                }

                textComponent.enableAutoSizing = true;
                textComponent.fontSizeMax = origSize;
                textComponent.fontSizeMin = Math.Max(minAbsolute, origSize * minRatio);
                textComponent.enableWordWrapping = true;
            }
            catch { }
        }

        public static bool TryTranslate(string original, out string translated)
        {
            translated = string.Empty;
            if (string.IsNullOrWhiteSpace(original))
                return false;

            // 1. Correspondência exata
            if (_translations.TryGetValue(original, out translated))
                return true;

            // 2. Correspondência normalizada (converte \u3000 e colapsa espaços)
            string norm = NormalizeText(original);
            if (_normalizedTranslations.TryGetValue(norm, out translated))
                return true;

            // 3. Correspondência sem espaços (tolerância a qualquer diferença de formatação)
            string stripped = StripWhitespace(original);
            if (_strippedTranslations.TryGetValue(stripped, out translated))
                return true;

            // 4. Rotulo composto pelo jogo ("輪郭：頬", "目：まつ毛"): traduz parte a parte se todas existirem
            if (TryTranslateParts(stripped, out translated))
                return true;

            RecordMissing(original);
            return false;
        }

        private static readonly char[] PartSeparators = { '：', '・', '/', '／' };

        private static bool TryTranslateParts(string text, out string translated)
        {
            translated = null;
            if (text.IndexOfAny(PartSeparators) < 0) return false;
            // Parts come from the whitespace-stripped text, so only Japanese labels qualify: a date like
            // "2026/09/23  09:04" or an already translated "cima/baixo" would come back without its spaces.
            if (!HasJapanese(text)) return false;
            var sb = new System.Text.StringBuilder();
            int start = 0;
            bool any = false;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && Array.IndexOf(PartSeparators, text[i]) < 0) continue;
                string part = text.Substring(start, i - start);
                if (part.Length > 0)
                {
                    if (_strippedTranslations.TryGetValue(part, out string t)) { sb.Append(t); any = true; }
                    else if (HasJapanese(part)) return false;
                    else sb.Append(part);
                }
                if (i < text.Length) sb.Append(text[i] == '：' ? ": " : text[i].ToString());
                start = i + 1;
            }
            if (!any) return false;
            translated = sb.ToString();
            return true;
        }

        // Textos japoneses que passaram pelos ganchos sem traducao: um por linha em Translation/_missing.txt,
        // para completar o ui_extra.json com o que o jogo realmente mostra.
        private static readonly HashSet<string> _missing = new(StringComparer.Ordinal);
        private static string _missingPath;

        private static void RecordMissing(string text)
        {
            if (!HasJapanese(text) || text.Length > 300 || !_missing.Add(text)) return;
            try
            {
                _missingPath ??= Path.Combine(Paths.BepInExRootPath, "Translation", "_missing.txt");
                File.AppendAllText(_missingPath, text.Replace("\r", "").Replace("\n", "\\n") + "\n");
            }
            catch { }
        }

        // --- Harmony Hook Callbacks ---

        public static void TMP_Text_SetText_Prefix(TMP_Text __instance, ref string sourceText)
        {
            if (TryTranslate(sourceText, out string translated))
            {
                Logger.Info($"[TMP_Text.SetText] '{sourceText}' -> '{translated}'");
                sourceText = translated;
            }
            if (__instance != null && (__instance.overflowMode == TextOverflowModes.Ellipsis || __instance.overflowMode == TextOverflowModes.Truncate))
            {
                EnsureAutoSizing(__instance, 0.65f, 12f);
            }
        }

        public static void TMP_Text_set_text_Prefix(TMP_Text __instance, ref string value)
        {
            if (TryTranslate(value, out string translated))
            {
                Logger.Info($"[TMP_Text.text] '{value}' -> '{translated}'");
                value = translated;
            }
            if (__instance != null && (__instance.overflowMode == TextOverflowModes.Ellipsis || __instance.overflowMode == TextOverflowModes.Truncate))
            {
                EnsureAutoSizing(__instance, 0.65f, 12f);
            }
        }

        public static void TextMeshProUGUI_OnEnable_Postfix(TextMeshProUGUI __instance)
        {
            if (__instance == null) return;
            try
            {
                if (__instance.overflowMode == TextOverflowModes.Ellipsis || __instance.overflowMode == TextOverflowModes.Truncate)
                {
                    EnsureAutoSizing(__instance, 0.65f, 12f);
                }

                string text = __instance.text;
                if (!string.IsNullOrEmpty(text) && TryTranslate(text, out string translated))
                {
                    __instance.text = translated;
                }
            }
            catch { }
        }

        public static void TextMeshProUGUI_Awake_Postfix(TextMeshProUGUI __instance)
        {
            if (__instance == null) return;
            try
            {
                if (__instance.overflowMode == TextOverflowModes.Ellipsis || __instance.overflowMode == TextOverflowModes.Truncate)
                {
                    EnsureAutoSizing(__instance, 0.65f, 12f);
                }

                string text = __instance.text;
                if (!string.IsNullOrEmpty(text) && TryTranslate(text, out string translated))
                {
                    __instance.text = translated;
                }
            }
            catch { }
        }

        public static void UIText_SetText_Prefix(ref string text)
        {
            if (TryTranslate(text, out string translated))
            {
                Logger.Info($"[UIText.SetText] '{text}' -> '{translated}'");
                text = translated;
            }
        }

        public static void TextScenario_ReplaceText_Postfix(ref string __result)
        {
            if (TryTranslate(__result, out string translated))
            {
                Logger.Info($"[ADV Line] '{__result}' -> '{translated}'");
                __result = translated;
            }
        }

        public static void LocalizeTranslater_GetText_Postfix(ref string __result)
        {
            if (TryTranslate(__result, out string translated))
            {
                Logger.Info($"[LocalizeTranslater] '{__result}' -> '{translated}'");
                __result = translated;
            }
        }

        public static void Text_set_text_Prefix(ref string value)
        {
            if (TryTranslate(value, out string translated))
            {
                Logger.Info($"[UI.Text] '{value}' -> '{translated}'");
                value = translated;
            }
        }

        public static void Text_OnEnable_Postfix(Text __instance)
        {
            if (__instance == null) return;
            try
            {
                string text = __instance.text;
                if (!string.IsNullOrEmpty(text) && TryTranslate(text, out string translated))
                {
                    __instance.text = translated;
                }
            }
            catch { }
        }

        public static void TextController_Set_Prefix(TextController __instance, ref string nameText, ref string messageText)
        {
            try
            {
                if (__instance != null)
                {
                    if (__instance.MessageWindow != null)
                    {
                        EnsureAutoSizing(__instance.MessageWindow, 0.55f, 14f);
                    }
                    if (__instance.NameWindow != null)
                    {
                        EnsureAutoSizing(__instance.NameWindow, 0.70f, 16f);
                    }
                }

                if (!string.IsNullOrEmpty(nameText) && TryTranslate(nameText, out string transName))
                {
                    Logger.Info($"[TextController.Name] '{nameText}' -> '{transName}'");
                    nameText = transName;
                }

                if (!string.IsNullOrEmpty(messageText))
                {
                    if (TryTranslate(messageText, out string transMsg))
                    {
                        Logger.Info($"[TextController.Msg] '{messageText}' -> '{transMsg}'");
                        messageText = transMsg;
                    }
                    else if (HasJapanese(messageText))
                    {
                        Logger.LogWarning($"[TextController.ADV MISS] '{messageText}' (normalized: '{NormalizeText(messageText)}')");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in TextController_Set_Prefix: {ex}");
            }
        }

        public static void Choice_set_Text_Prefix(Choice __instance, ref string value)
        {
            try
            {
                if (TryTranslate(value, out string translated))
                {
                    Logger.Info($"[ADV Choice] '{value}' -> '{translated}'");
                    value = translated;
                }
                if (__instance != null && __instance._text != null)
                {
                    EnsureAutoSizing(__instance._text, 0.65f, 14f);
                }
            }
            catch { }
        }

        public static void ButtonText_set_Text_Prefix(ref string value)
        {
            if (TryTranslate(value, out string translated))
            {
                Logger.Info($"[ButtonText] '{value}' -> '{translated}'");
                value = translated;
            }
        }

        public static void ToggleText_set_text_Prefix(ref string value)
        {
            if (TryTranslate(value, out string translated))
            {
                Logger.Info($"[ToggleText] '{value}' -> '{translated}'");
                value = translated;
            }
        }

        public static void Manager_get_IsTranslate_Postfix(ref bool __result)
        {
            __result = true;
        }
    }
}
