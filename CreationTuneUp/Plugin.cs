using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.UI;
using CharacterCreation;
using CharacterCreation.UI;
using AnimKeyController = ILLGAMES.Unity.AnimationKeyInfo.Controller;

namespace CreationTuneUp
{
    /// <summary>
    /// Character creation sliders go past their limits: a 0..100 field accepts -100..200
    /// (minimum = -maximum, maximum doubled).
    ///
    /// How the game limits them (read in IDA):
    /// - typed text: HumanCustom.&lt;&gt;c__DisplayClass200_0.&lt;EntryUndo&gt;b__4 parses int/100 and clamps to [0,1] inline;
    /// - the uGUI Slider clamps to its minValue/maxValue (0..1);
    /// - HumanCustom.ConvertTextFromRate01 clamps before printing;
    /// - AnimationKeyInfo.Controller.GetInfo(name, rate) indexes keys by rate*(count-1): outside [0,1] it reads
    ///   past the key list. The value itself is not clamped anywhere else (SetShapeFaceValue/SetShapeBodyValue
    ///   -> ShapeInfoBase.ChangeValue -> GetInfo).
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "com.marcus.amanatsu.creationtuneup";
        public const string Name = "CreationTuneUp";
        public const string Version = "1.0.0";

        internal const float MinRate = -1f, MaxRate = 2f;
        internal static ManualLogSource Logger;

        public override void Load()
        {
            Logger = Log;
            try { new Harmony(Guid).PatchAll(typeof(Patches)); }
            catch (Exception ex) { Logger.LogError($"Patch failed: {ex}"); return; }
            Logger.Info($"Creation sliders unlocked to {MinRate * 100:0}..{MaxRate * 100:0}.");
        }
    }

    internal static class Patches
    {
        // Sliders owned by InputSliderButton (creation fields). Other sliders in the game keep their limits.
        private static readonly HashSet<IntPtr> _creationSliders = new HashSet<IntPtr>();
        private static readonly HashSet<string> _seen = new HashSet<string>();

        private static void FirstCall(string patch)
        {
            if (_seen.Add(patch)) Plugin.Logger.Info($"[{patch}] active.");
        }

        private static void Widen(Slider slider)
        {
            if (slider == null) return;
            _creationSliders.Add(slider.Pointer);
            slider.minValue = Plugin.MinRate;
            slider.maxValue = Plugin.MaxRate;
        }

        /// <summary>Creation sliders span -100..200 from the start, so dragging goes past the vanilla limits too.</summary>
        [HarmonyPatch(typeof(InputSliderButton), nameof(InputSliderButton.Initialize))]
        [HarmonyPostfix]
        private static void RegisterSlider(InputSliderButton __instance)
        {
            FirstCall("InputSliderButton.Initialize");
            Widen(__instance.Slider);
        }

        /// <summary>Safety net for sliders that bypass Initialize: widen on the way in instead of clamping.</summary>
        [HarmonyPatch(typeof(Slider), "Set", typeof(float), typeof(bool))]
        [HarmonyPrefix]
        private static void WidenBeforeSet(Slider __instance, float input)
        {
            if ((input >= __instance.minValue && input <= __instance.maxValue) || !_creationSliders.Contains(__instance.Pointer)) return;
            Widen(__instance);
        }

        /// <summary>The game already applied the clamped value; apply the real one through the slider.</summary>
        [HarmonyPatch(typeof(HumanCustom.__c__DisplayClass200_0), nameof(HumanCustom.__c__DisplayClass200_0._EntryUndo_b__4))]
        [HarmonyPostfix]
        private static void ApplyTypedValue(HumanCustom.__c__DisplayClass200_0 __instance, string buf)
        {
            FirstCall("typed value");
            var slider = __instance.slider;
            Widen(slider);
            if (slider == null || !int.TryParse(buf, out int typed) || (typed >= 0 && typed <= 100)) return;
            slider.value = Mathf.Clamp(typed / 100f, Plugin.MinRate, Plugin.MaxRate);   // onValueChanged -> set + text
        }


        /// <summary>Same formatting as the game (Math.Round of value*100), without the [0,1] clamp.</summary>
        [HarmonyPatch(typeof(HumanCustom), nameof(HumanCustom.ConvertTextFromRate01))]
        [HarmonyPrefix]
        private static bool TextWithoutClamp(float value, ref string __result)
        {
            FirstCall("ConvertTextFromRate01");
            __result = ((int)Math.Round(value * 100.0)).ToString();
            return false;
        }

        /// <summary>
        /// Outside [0,1] the shape keeps the slope of the nearest key segment: result = edge + (edge - inner) * k,
        /// with both samples taken by the game's own GetInfo inside its range.
        /// </summary>
        [HarmonyPatch(typeof(AnimKeyController), nameof(AnimKeyController.GetInfo),
            new[] { typeof(string), typeof(float), typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(Il2CppStructArray<bool>) },
            new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out, ArgumentType.Out, ArgumentType.Normal })]
        [HarmonyPrefix]
        private static bool Extrapolate(AnimKeyController __instance, string name, float rate,
            ref Vector3 pos, ref Vector3 rot, ref Vector3 scale, Il2CppStructArray<bool> flags, ref bool __result)
        {
            if (rate >= 0f && rate <= 1f) return true;
            FirstCall("shape extrapolation");
            int keys = __instance.GetKeyCount();
            if (keys < 2) return true;

            float step = 1f / (keys - 1);
            bool above = rate > 1f;
            float edge = above ? 1f : 0f, inner = above ? 1f - step : step;
            float k = (above ? rate - 1f : -rate) / step;   // segments past the edge

            if (!__instance.GetInfo(name, edge, out var pe, out var re, out var se, flags)) { __result = false; return false; }
            __instance.GetInfo(name, inner, out var pi, out var ri, out var si, flags);
            pos = pe + (pe - pi) * k;
            scale = se + (se - si) * k;
            rot = new Vector3(re.x + Mathf.DeltaAngle(ri.x, re.x) * k,
                              re.y + Mathf.DeltaAngle(ri.y, re.y) * k,
                              re.z + Mathf.DeltaAngle(ri.z, re.z) * k);
            __result = true;
            return false;
        }
    }
}
