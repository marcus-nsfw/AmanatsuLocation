using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using Character;

namespace Amanatsu.Uncensor
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class UncensorPlugin : BasePlugin
    {
        public const string PluginGuid = "com.amanatsu.uncensor";
        public const string PluginName = "Amanatsu Uncensor Plugin";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Logger;

        public override void Load()
        {
            Logger = Log;
            Logger.Info("Amanatsu Uncensor Plugin initializing...");

            try
            {
                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll(typeof(UncensorPlugin));
                Logger.Info("Real-time uncensor hooks applied successfully!");
                TosSkip.Aplica(harmony);
                Freemode.Aplica(harmony);
                Genitais.ExtraDepth = Config.Bind("Genitals", "ExtraDepth", 0.15f,
                    "Fraction of the length the animation takes from the penis during penetration (it shortens it so it " +
                    "does not pass through the female body) that the mod gives back. 0 = as the game; 1 = full length.");
                Genitais.MaxDepth = Config.Bind("Genitals", "MaxDepth", 0.35f,
                    "Vagina: the penis keeps its full length until the tip goes this deep past the entrance " +
                    "(fraction of its length); only then does the animation's shortening show. Higher = goes deeper before compressing.");
                Genitais.AnalDepth = Config.Bind("Genitals", "AnalDepth", 0.15f,
                    "Anus: like MaxDepth, but for anal (the game points the penis almost straight up her body; " +
                    "too deep and the tip comes out through the skin). Fraction of the penis length past the entrance.");
                Genitais.MouthLength = Config.Bind("Genitals", "MouthLength", 1.35f,
                    "In the mouth the penis is this much of its rest length (1 = normal). The oral animation was made for " +
                    "the original penis: without this the tip shows at the lips mid-motion.");
                Genitais.MouthDepth = Config.Bind("Genitals", "MouthDepth", 0.4f,
                    "Like ExtraDepth, but when the tip is in the mouth (closer to her head than to the vulva). " +
                    "Higher = goes in further and the shrinking shows less.");
                Genitais.Collision = Config.Bind("Genitals", "Collision", true,
                    "Enables fingers/penis pushing the vulva. Off, the vulva bones stay at rest.");
                Config.Save(); // existing .cfg files pick up the current descriptions (values are kept)
                Genitais.Carrega();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error applying Uncensor hooks: {ex}");
            }
        }

        /// <summary>
        /// No oral quem encurta o penis nao e (so) a animacao: o LookAtPenis do jogo, no LateUpdate dele, poe a
        /// ponta no alvo da boca - depois do nosso postfix do Human, desfazendo o alongamento (MouthDepth
        /// nao mudava nada). Reaplica depois dele; o AlongaPenis e idempotente dentro do quadro.
        /// </summary>
        [HarmonyPatch(typeof(AL.H.LookAtPenis), "LateUpdate")]
        [HarmonyPostfix]
        public static void LookAtPenis_LateUpdate_Postfix()
        {
            try { Genitais.ReaplicaAlongamento(); }
            catch (Exception ex) { Logger.LogWarning($"[GEN] stretch after LookAtPenis: {ex.Message}"); }
        }

        [HarmonyPatch(typeof(Human), nameof(Human.LateUpdate))]
        [HarmonyPostfix]
        public static void Human_LateUpdate_Postfix(Human __instance)
        {
            if (__instance == null) return;
            try { Genitais.Aplica(__instance); }
            catch (Exception ex) { Logger.LogWarning($"[GEN] {ex.Message}"); }
            try
            {
                var mozObj = __instance.GetRefObject(Table.RefObjKey.S_MOZ_ALL);
                if (mozObj != null && mozObj.activeSelf)
                {
                    mozObj.SetActive(false);
                }

                // the other uncensor sets its own genital materials: only the mosaic toggle above stays ours
                if (Genitais.OtherUncensor) return;

                var bodyObj = __instance.GetRefObject(Table.RefObjKey.ObjBody);
                if (bodyObj == null) return;

                var bodyRenderer = bodyObj.GetComponent<Renderer>();
                if (bodyRenderer == null || bodyRenderer.sharedMaterial == null) return;

                Material bodySkinMat = bodyRenderer.sharedMaterial;

                FixGenitalRenderer(__instance.GetRefObject(Table.RefObjKey.S_MNPA), bodySkinMat);
                FixGenitalRenderer(__instance.GetRefObject(Table.RefObjKey.S_MNPB), bodySkinMat);
                FixGenitalRenderer(__instance.GetRefObject(Table.RefObjKey.S_DANKON), bodySkinMat);
            }
            catch
            {
            }
        }

        private static void FixGenitalRenderer(GameObject obj, Material targetSkinMat)
        {
            if (obj == null) return;

            var renderer = obj.GetComponent<Renderer>();
            if (renderer == null || renderer.sharedMaterial == null) return;

            string matName = renderer.sharedMaterial.name;
            if (matName.Contains("mnpb") || (renderer.sharedMaterial.shader != null && renderer.sharedMaterial.shader.name.Contains("mnpb")))
            {
                renderer.sharedMaterial = targetSkinMat;
            }
        }
    }
}
