using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using AmanatsuVR.Logging;

namespace AmanatsuVR.VRUtils
{
    public class UIScreen : MonoBehaviour
    {
        static UIScreen() { ClassInjector.RegisterTypeInIl2Cpp<UIScreen>(); }

        private const string COLOR_SHADER_NAME = "Hidden/Internal-Colored";

        [HideFromIl2Cpp]
        public static UIScreen Create(
            GameObject parentGameObject,
            string name,
            int screenLayer,
            UIScreenPanel[] panels
        )
        {
            var gameObject = new GameObject($"{parentGameObject.name}{name}");
            gameObject.transform.parent = parentGameObject.transform;
            gameObject.SetActive(false);
            var result = gameObject.AddComponent<UIScreen>();
            result.Panels = panels;
            result.ScreenLayer = screenLayer;
            gameObject.SetActive(true);
            return result;
        }

        [HideFromIl2Cpp] public UIScreenPanel[] Panels { get; private set; }
        private int ScreenLayer { get; set; }

        private GameObject ScreenObject { get; set; }
        private GameObject[] PanelObjects { get; set; }
        [HideFromIl2Cpp] public GameObject MainPanelObject { get => (PanelObjects != null && 0 < PanelObjects.Length) ? PanelObjects[0] : null; }

        private static Mesh _cachedQuadMesh;
        [HideFromIl2Cpp] public static Mesh GetQuadMesh() => GetOrCreateQuadMesh();

        private static Mesh GetOrCreateQuadMesh()
        {
            if (_cachedQuadMesh != null) return _cachedQuadMesh;

            var mesh = new Mesh
            {
                name = "Procedural_UI_Quad",
                vertices = new Vector3[]
                {
                    new(-0.5f, -0.5f, 0f),
                    new( 0.5f, -0.5f, 0f),
                    new(-0.5f,  0.5f, 0f),
                    new( 0.5f,  0.5f, 0f)
                },
                uv = new Vector2[]
                {
                    new(0f, 0f),
                    new(1f, 0f),
                    new(0f, 1f),
                    new(1f, 1f)
                },
                triangles = new int[] { 0, 2, 1, 2, 3, 1 }
            };
            mesh.RecalculateNormals();
            _cachedQuadMesh = mesh;
            return _cachedQuadMesh;
        }

        private void Setup()
        {
            if (!ScreenObject)
            {
                ScreenObject = new GameObject($"{gameObject.name}{nameof(ScreenObject)}");
                ScreenObject.transform.parent = transform;
                ScreenObject.transform.localPosition = Vector3.zero;
                ScreenObject.transform.localScale = Vector3.one * AmanatsuVR.Config.PluginConfig.UIScreenScale.Value;
            }

            if (Panels != null)
            {
                PanelObjects ??= new GameObject[Panels.Length];
                for (var i = 0; i < Panels.Length; ++i)
                {
                    if (!PanelObjects[i])
                    {
                        var panelObject = new GameObject($"{gameObject.name}PanelObject{i}");
                        var panel = Panels[i];
                        panelObject.transform.parent = ScreenObject.transform;
                        panelObject.transform.localPosition = panel.Offset;
                        panelObject.transform.localScale = new Vector3(panel.Texture.width / (float)panel.Texture.height * panel.Scale.x, panel.Scale.y, panel.Scale.z);
                        panelObject.layer = ScreenLayer;

                        var meshFilter = panelObject.AddComponent<MeshFilter>();
                        meshFilter.mesh = GetOrCreateQuadMesh();

                        var material = CustomAssetManager.CreateUiMaterial(panel.Texture);
                        var meshRenderer = panelObject.AddComponent<MeshRenderer>();
                        meshRenderer.material = material;
                        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        meshRenderer.receiveShadows = false;
                        meshRenderer.enabled = !AmanatsuVR.Config.PluginConfig.HideUIPanel.Value;

                        PanelObjects[i] = panelObject;
                    }
                }
            }

        }

        void Awake()
        {
            PluginLog.Debug($"[AmanatsuVR] UIScreen Awake: {name}");
            Setup();
        }

        public Vector3? GetWorldPositionFromScreen(float x, float y)
        {
            var mainPanelObject = MainPanelObject;
            if (mainPanelObject == null) return null;

            float normX = (x / UnityEngine.Screen.width) - 0.5f;
            float normY = (y / UnityEngine.Screen.height) - 0.5f;
            return mainPanelObject.transform.TransformPoint(new Vector3(normX, normY, -0.01f));
        }

        [HideFromIl2Cpp]
        public void LinkToFront(VRCamera targetCamera, float distance)
        {
            Setup();

            if (targetCamera != null && targetCamera.VR != null && targetCamera.VR.origin != null)
            {
                // Parenta ao origin da câmera principal para acompanhar o jogador pelo cenário
                ScreenObject.transform.parent = targetCamera.VR.origin;

                Vector3 headPos = VRCamera.BaseHeadPosition;
                Quaternion headYaw = VRCamera.BaseHeadYaw;

                ScreenObject.transform.localPosition = headPos + headYaw * (distance * Vector3.forward);
                ScreenObject.transform.localRotation = headYaw;
                ScreenObject.transform.localScale = Vector3.one * AmanatsuVR.Config.PluginConfig.UIScreenScale.Value;
            }
        }

        /// <summary>
        /// Como LinkToFront, mas diante da cabeca de AGORA (posicao e yaw atuais do HMD) e nao da
        /// pose de recentralizacao. Na H o jogador anda pela cena e o painel tem que vir ate ele.
        /// </summary>
        [HideFromIl2Cpp]
        public void LinkToHead(VRCamera targetCamera, float distance)
        {
            Setup();
            if (targetCamera == null || targetCamera.VR == null || targetCamera.VR.origin == null || targetCamera.VR.head == null) return;

            var origin = targetCamera.VR.origin;
            var head = targetCamera.VR.head;
            ScreenObject.transform.parent = origin;

            Vector3 headPos = head.localPosition;
            Quaternion headYaw = Quaternion.Euler(0f, head.localRotation.eulerAngles.y, 0f);
            ScreenObject.transform.localPosition = headPos + headYaw * (distance * Vector3.forward);
            ScreenObject.transform.localRotation = headYaw;
            ScreenObject.transform.localScale = Vector3.one * AmanatsuVR.Config.PluginConfig.UIScreenScale.Value;
            // Fica preso a origem de proposito: na primeira pessoa a camera anda com a cabeca do
            // ator, e um painel parado no mundo escapava da mira do laser (Marcus nao conseguia
            // clicar). Preso ao rig, ele acompanha a camera e so a cabeca real o move.
        }
    }
}
