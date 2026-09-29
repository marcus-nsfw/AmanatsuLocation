using System.IO;
using UnityEngine;
using AmanatsuVR.Logging;

namespace AmanatsuVR.VRUtils
{
    public class CustomAssetManager
    {
        private static AssetBundle Bundle { get; set; }
        private static Shader CachedShader { get; set; }

        public static AssetBundle GetBundle()
        {
            if (Bundle == null)
            {
                try
                {
                    var bytes = ReadAllBytes("AmanatsuVR.AssetBundles.custom_asset_bundle");
                    if (bytes != null && bytes.Length > 0)
                    {
                        Bundle = AssetBundle.LoadFromMemory(bytes);
                        PluginLog.Info("[AmanatsuVR] Custom VR shader AssetBundle loaded successfully!");
                    }
                }
                catch (System.Exception ex)
                {
                    PluginLog.Warning($"[AmanatsuVR] Could not load embedded custom_asset_bundle: {ex.Message}");
                }
            }
            return Bundle;
        }

        public static Shader GetUrpUnlitShader()
        {
            var s = Shader.Find("Universal Render Pipeline/Unlit")
                 ?? Shader.Find("Unlit/Transparent Tint")
                 ?? Shader.Find("Unlit/Texture");

            if (s != null) return s;

            try
            {
                var allShaders = Resources.FindObjectsOfTypeAll<Shader>();
                if (allShaders != null)
                {
                    foreach (var sh in allShaders)
                    {
                        if (sh != null && sh.name == "Universal Render Pipeline/Unlit")
                        {
                            return sh;
                        }
                    }
                }
            }
            catch { }

            return Shader.Find("Hidden/Internal-Colored");
        }

        public static Material CreateUiMaterial(Texture texture)
        {
            var shader = GetUrpUnlitShader();
            var material = new Material(shader);

            if (material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", texture);
            }
            if (material.HasProperty("_MainTex"))
            {
                material.SetTexture("_MainTex", texture);
            }
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", Color.white);
            }
            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", Color.white);
            }

            // Modo transparente nativo do URP
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1.0f); // 1 = Transparent
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0.0f); // 0 = Alpha
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");

            material.renderQueue = 4000;
            // A captura de uGUI tem alpha DIRETO, não premultiplicado. Com Blend One a cor
            // era somada por cima dela mesma e o painel estourava para branco (visto no
            // OLHO_map000). SrcAlpha é a composição correta, e é a do laser, que funciona.
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            DisableDepthPasses(material);

            // O painel desenha magenta enquanto laser e retículo, com ESTE MESMO shader, saem
            // certos. A diferença é a RenderTexture: se ela vier como array (o display XR está
            // em Texture2DArray) o shader amostra como Texture2D e o resultado é magenta.
            var rt = texture as RenderTexture;
            PluginLog.Info($"[AmanatsuVR][MAT] panel shader='{shader?.name}' supported={shader?.isSupported}"
                + $" passes={material.passCount} queue={material.renderQueue}"
                + $" | texture='{texture?.name}' type={texture?.GetType().Name} dimension={texture?.dimension}"
                + (rt != null ? $" volumeDepth={rt.volumeDepth} created={rt.IsCreated()} format={rt.graphicsFormat}" : ""));
            return material;
        }

        /// <summary>
        /// `_ZWrite = 0` só vale para o passe principal. O URP/Unlit tem os passes DepthOnly e
        /// DepthNormals com `ZWrite On` fixo, que carimbam o depth buffer no prepass e fazem
        /// nossos objetos de HUD ocluírem o mundo inteiro atrás deles. Nada nosso deve escrever
        /// profundidade: são sobreposições, desenhadas com ZTest Always de propósito.
        /// </summary>
        private static void DisableDepthPasses(Material mat)
        {
            mat.SetShaderPassEnabled("DepthOnly", false);
            mat.SetShaderPassEnabled("DepthNormals", false);
            mat.SetShaderPassEnabled("DepthNormalsOnly", false);
            mat.SetShaderPassEnabled("ShadowCaster", false);
            mat.SetShaderPassEnabled("MotionVectors", false);
        }

        public static Material CreateLaserMaterial(Color color)
        {
            var shader = GetUrpUnlitShader();
            var mat = new Material(shader);

            if (mat.HasProperty("_BaseMap"))
            {
                mat.SetTexture("_BaseMap", Texture2D.whiteTexture);
            }
            if (mat.HasProperty("_MainTex"))
            {
                mat.SetTexture("_MainTex", Texture2D.whiteTexture);
            }

            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", color);
            }
            if (mat.HasProperty("_Color"))
            {
                mat.SetColor("_Color", color);
            }

            // Modo transparente nativo do URP
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1.0f);
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0.0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");

            mat.renderQueue = 5000;
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            DisableDepthPasses(mat);

            PluginLog.Info($"[AmanatsuVR] CreateLaserMaterial created with shader '{shader?.name}' (color={color})");
            return mat;
        }

        public static Shader UiUnlitTransparentShader
        {
            get
            {
                if (CachedShader != null) return CachedShader;

                var bundle = GetBundle();
                if (bundle != null)
                {
                    try
                    {
                        var loaded = bundle.LoadAsset<Shader>("assets/assetbundles/ui-unlit-transparent.shader");
                        if (loaded != null)
                        {
                            CachedShader = loaded;
                            return CachedShader;
                        }
                    }
                    catch { }
                }

                CachedShader = Shader.Find("Universal Render Pipeline/Unlit")
                            ?? Shader.Find("Unlit/Transparent")
                            ?? Shader.Find("UI/Default");

                return CachedShader;
            }
        }

        private static byte[] ReadAllBytes(string resourceName)
        {
            var assembly = typeof(CustomAssetManager).Assembly;
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return null;
            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            return memoryStream.ToArray();
        }
    }
}
