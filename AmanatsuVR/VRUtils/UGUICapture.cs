using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UI.Collections;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Attributes;
using AmanatsuVR.Logging;

namespace AmanatsuVR.VRUtils
{
    public class UGUICapture : MonoBehaviour
    {
        static UGUICapture() { ClassInjector.RegisterTypeInIl2Cpp<UGUICapture>(); }

        [HideFromIl2Cpp]
        public static UGUICapture Create(GameObject parentGameObject, string name, int layer)
        {
            var gameObject = new GameObject($"{parentGameObject.name}{name}");
            gameObject.transform.parent = parentGameObject.transform;
            gameObject.SetActive(false);
            var result = gameObject.AddComponent<UGUICapture>();
            result.Layer = layer;
            gameObject.SetActive(true);
            return result;
        }

        private int Layer { get; set; }
        public RenderTexture Texture { get; private set; }
        private Il2CppSystem.Collections.Generic.Dictionary<Canvas, IndexedSet<Graphic>> CanvasGraphics { get; set; }
        [HideFromIl2Cpp]
        private HashSet<Canvas> ProcessedCanvas { get; set; } = new HashSet<Canvas>();
        private float LastCanvasCheckTime { get; set; } = 0f;
        private float LastFadeSweepTime { get; set; } = 0f;

        /// <summary>
        /// Os fades de transicao de cena, achados uma vez por canvas e mantidos desligados.
        /// O jogo os reativa quando troca de cena, entao nao basta desligar na varredura.
        /// </summary>
        [HideFromIl2Cpp]
        private List<Graphic> FadeTransitions { get; set; } = new List<Graphic>();

        void Awake()
        {
            PluginLog.Debug($"[AmanatsuVR] UGUICapture Awake: {name}");

            Texture = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);

            var camera = gameObject.AddComponent<Camera>();
            // Captura tanto a camada dedicada de VR (Layer 15) quanto a camada padrao de UI (Layer 5)
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer < 0) uiLayer = 5;
            camera.cullingMask = (1 << Layer) | (1 << uiLayer);

            // Renderiza ANTES da VRCamera, não depois. Com depth=float.MaxValue esta câmera
            // escrevia na RenderTexture no mesmo frame em que o painel a amostrava; o
            // RenderGraph do Unity 6 reordena e faz aliasing de recursos, e ler uma textura
            // que é alvo de render no mesmo frame dá resultado indefinido — magenta.
            camera.depth = -1000f;
            camera.nearClipPlane = -1000f;
            camera.farClipPlane = 1000f;
            camera.targetTexture = Texture;
            camera.backgroundColor = Color.clear;
            camera.clearFlags = CameraClearFlags.Color;
            camera.orthographic = true;
            camera.orthographicSize = Screen.height * 0.5f;
            camera.useOcclusionCulling = false;
            camera.stereoTargetEye = StereoTargetEyeMask.None;

            try
            {
                var addData = UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(camera);
                if (addData != null)
                {
                    addData.renderShadows = false;
                    addData.renderType = UnityEngine.Rendering.Universal.CameraRenderType.Base;
                }
            }
            catch { }

            try
            {
                if (GraphicRegistry.instance != null)
                {
                    CanvasGraphics = GraphicRegistry.instance.m_Graphics;
                }
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning($"[AmanatsuVR] Falha ao registrar GraphicRegistry: {ex.Message}");
            }
        }

        void OnDestroy()
        {
            if (Texture != null)
            {
                Texture.Release();
                Texture = null;
            }
        }

        void Update()
        {
            var camera = GetComponent<Camera>();
            if (camera == null) return;

            // Limpa referências nulas da lista de processados
            ProcessedCanvas.RemoveWhere(c => c == null);

            for (int i = FadeTransitions.Count - 1; i >= 0; --i)
            {
                var fade = FadeTransitions[i];
                if (fade == null) FadeTransitions.RemoveAt(i);
                else if (fade.enabled) fade.enabled = false;
            }

            // Verifica novos canvas
            if (CanvasGraphics != null)
            {
                foreach (var canvas in CanvasGraphics.Keys)
                {
                    if (NeedsSetup(canvas, camera))
                    {
                        SetupCanvas(canvas, camera);
                    }
                }

                // Os fades precisam de uma varredura periodica propria. SetupCanvas roda uma
                // unica vez por canvas e nem chega a sanitizar os WorldSpace, entao um fade
                // criado depois - ou num canvas que pulamos - escapava. Foi por isso que o
                // quadrado com o buraco no meio voltou no menu principal e no mapa.
                if (Time.unscaledTime - LastFadeSweepTime > 0.5f)
                {
                    LastFadeSweepTime = Time.unscaledTime;
                    ParedesAgora.Clear();
                    foreach (var par in CanvasGraphics)
                    {
                        var lista = par.Value;
                        if (lista == null) continue;
                        for (int i = 0; i < lista.Count; ++i)
                        {
                            if (CapturaFade(lista[i])) continue;
                            DenunciaParede(lista[i]);
                        }
                    }
                    DiffParedes();
                }
            }
            else
            {
                // Fallback periódico para evitar varredura em todo frame (inclui inativos)
                if (Time.unscaledTime - LastCanvasCheckTime > 0.5f)
                {
                    LastCanvasCheckTime = Time.unscaledTime;
                    foreach (var canvas in FindObjectsOfType<Canvas>(true))
                    {
                        if (NeedsSetup(canvas, camera))
                        {
                            SetupCanvas(canvas, camera);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Um canvas WorldSpace continua WorldSpace: sanitiza uma vez e nunca mais.
        /// Sem isto ele reentraria em SetupCanvas todo frame.
        /// </summary>
        private bool NeedsSetup(Canvas canvas, Camera camera)
        {
            if (canvas == null) return false;
            if (!ProcessedCanvas.Contains(canvas)) return true;
            if (canvas.renderMode == RenderMode.WorldSpace) return false;
            return canvas.renderMode != RenderMode.ScreenSpaceCamera || canvas.worldCamera != camera;
        }

        private void SetupCanvas(Canvas canvas, Camera captureCamera)
        {
            if (canvas == null) return;

            // Ignora se o canvas pertence ao mod VR
            if (canvas.transform.root == transform.root) return;

            ProcessedCanvas.Add(canvas);

            // Preserva WorldSpace canvases (como marcadores 3D no cenário).
            // NÃO sanitizar material aqui: anular o material deixa o buraco do sprite preto.
            if (canvas.renderMode == RenderMode.WorldSpace) return;

            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = captureCamera;
            canvas.planeDistance = 100f;

            var transforms = canvas.gameObject.GetComponentsInChildren<Transform>(true);
            if (transforms != null)
            {
                foreach (var t in transforms)
                {
                    if (t != null && t.gameObject != null)
                    {
                        t.gameObject.layer = Layer;
                    }
                }
            }

            SanitizeCanvas(canvas);
        }

        /// <summary>
        /// O fade de transicao de cena e um RawImage de tela cheia com um shader de mascara
        /// animada. No desktop e um piscar; capturado para a RT ele vira uma parede tingida de
        /// 2 m na cara do jogador, com o buraco da mascara no meio. Medido: com ele ativo,
        /// UI_map000.png saiu branco solido e so o cursor apareceu.
        ///
        /// Devolve true se o grafico era um fade. Uma vez achado ele entra em FadeTransitions e
        /// o Update o mantem desligado, porque o jogo o reativa a cada transicao.
        /// </summary>
        private bool CapturaFade(Graphic g)
        {
            if (g == null || g.material == null || g.material.shader == null) return false;
            if (!g.material.shader.name.Contains("Easy Masking Transition")) return false;

            g.enabled = false;
            if (!FadeTransitions.Contains(g))
            {
                FadeTransitions.Add(g);
                PluginLog.Info($"[AmanatsuVR][FADE] desligado '{g.name}' shader='{g.material.shader.name}'");
            }
            return true;
        }

        /// <summary>
        /// Ao avancar o dia a UI escurece e nunca volta, mas continua clicavel: sobrou um
        /// grafico opaco de tela cheia na frente de tudo. O fade do EMTransition nao explica -
        /// ele e dirigido por um MonoBehaviour separado, que a nossa desativacao do RawImage nao
        /// para. Entao esta linha diz QUEM e a parede, em vez de eu adivinhar de novo.
        ///
        /// So loga uma vez por nome, senao viraria uma enxurrada a cada varredura.
        /// </summary>
        [HideFromIl2Cpp]
        private HashSet<string> ParedesVistas { get; set; } = new HashSet<string>();
        [HideFromIl2Cpp]
        private HashSet<string> ParedesAgora { get; set; } = new HashSet<string>();

        private void DenunciaParede(Graphic g)
        {
            if (g == null || !g.enabled || !g.gameObject.activeInHierarchy) return;

            // A opacidade real nao esta so na cor: o FadeCanvas do jogo anima um CanvasGroup,
            // e por isso o 'Fade' do SceneCanvas aparece com cor branca a=1 mesmo invisivel.
            float alfa = g.color.a;
            var cg = g.GetComponentInParent<CanvasGroup>();
            if (cg != null) alfa *= cg.alpha;
            if (g.canvasRenderer != null) alfa *= g.canvasRenderer.GetAlpha();
            if (alfa < 0.9f) return;

            var rt = g.rectTransform;
            if (rt == null) return;

            float larg = rt.rect.width * Mathf.Abs(rt.lossyScale.x);
            float alt = rt.rect.height * Mathf.Abs(rt.lossyScale.y);
            if (larg < Screen.width * 0.8f || alt < Screen.height * 0.8f) return;

            var canvas = g.canvas;
            var tex = g.mainTexture;
            var shader = g.materialForRendering != null && g.materialForRendering.shader != null ? g.materialForRendering.shader.name : "-";
            ParedesAgora.Add($"'{g.name}' cor={g.color} alfa={alfa:F2} tam={larg:F0}x{alt:F0}"
                + $" canvas='{(canvas != null ? canvas.name : "?")}' ordem={(canvas != null ? canvas.sortingOrder : 0)}"
                + $" pai='{(rt.parent != null ? rt.parent.name : "?")}' tex='{(tex != null ? tex.name : "-")}' shader='{shader}'");
        }

        /// <summary>
        /// Loga entrada e saida, nao a primeira aparicao. Da primeira vez eu so registrava
        /// uma vez por nome e o 'Fade' gastou o registro dele na tela de titulo - quando a
        /// parede voltou no avanco do dia, o log ja estava cego.
        /// </summary>
        private void DiffParedes()
        {
            foreach (var p in ParedesAgora)
            {
                if (!ParedesVistas.Contains(p)) PluginLog.Info($"[AmanatsuVR][PAREDE] + {p}");
            }
            foreach (var p in ParedesVistas)
            {
                if (!ParedesAgora.Contains(p)) PluginLog.Info($"[AmanatsuVR][PAREDE] - {p}");
            }

            ParedesVistas.Clear();
            foreach (var p in ParedesAgora) ParedesVistas.Add(p);
        }

        private void SanitizeCanvas(Canvas canvas)
        {
            // Desativa renderizadores de pós-processamento 2D (CompositeCanvas) que causam artefatos rosa
            try
            {
                var compositeRenderers = canvas.gameObject.GetComponentsInChildren<CompositeCanvas.CompositeCanvasRenderer>(true);
                if (compositeRenderers != null)
                {
                    foreach (var cr in compositeRenderers)
                    {
                        if (cr != null)
                        {
                            cr.enabled = false;
                            if (cr.gameObject != null && cr.gameObject.name.Contains("Composite"))
                            {
                                cr.gameObject.SetActive(false);
                            }
                        }
                    }
                }
            }
            catch { }

            // Sanitiza materiais de gráficos que possuam shaders com erro ou materiais de CompositeCanvas
            try
            {
                var graphics = canvas.gameObject.GetComponentsInChildren<Graphic>(true);
                if (graphics != null)
                {
                    foreach (var g in graphics)
                    {
                        if (g != null && g.material != null && g.material.shader != null)
                        {
                            if (CapturaFade(g)) continue;

                            string sName = g.material.shader.name;
                            if (sName.Contains("Error") || sName.Contains("InternalError") || sName.Contains("CompositeCanvas"))
                            {
                                g.material = null;
                            }
                        }
                    }
                }
            }
            catch { }
        }
    }
}
