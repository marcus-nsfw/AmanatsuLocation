using System.Collections;
using BepInEx.Unity.IL2CPP.Utils;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.SceneManagement;
using AmanatsuVR.Logging;
using AmanatsuVR.Config;
using AmanatsuVR.VRUtils;
using Valve.VR;

namespace AmanatsuVR
{
    public class VRController : MonoBehaviour
    {
        static VRController() { ClassInjector.RegisterTypeInIl2Cpp<VRController>(); }

        public const int UGUI_CAPTURE_LAYER = 15;
        public const int UI_SCREEN_LAYER = 31;
        public const int MAIN_VR_CAMERA_DEPTH = 1000;

        [HideFromIl2Cpp] public VRCamera MainVRCamera { get; private set; }
        [HideFromIl2Cpp] public UIScreen UIScreen { get; private set; }
        [HideFromIl2Cpp] public TrackerMotionTranslator MotionTranslator { get; private set; }
        [HideFromIl2Cpp] public VRWaterGunHandler WaterGunHandler { get; private set; }
        [HideFromIl2Cpp] public VRControllerLaser ControllerLaser { get; private set; }
        [HideFromIl2Cpp] public VRCharCreation CharCreation { get; private set; }
        [HideFromIl2Cpp] public VRHScene HScene { get; private set; }

        /// <summary>
        /// União das cullingMasks das câmeras de cena que apagamos por serem mono.
        /// A VRCamera estéreo precisa desenhar o que elas desenhariam.
        /// </summary>
        [HideFromIl2Cpp] public static int ExtraGameCullingMask { get; private set; } = 0;

        /// <summary>
        /// Layers que a VRCamera NÃO deve desenhar, mesmo estando na máscara do jogo. Aplicada
        /// dentro do CameraHijacker.Synchronize porque é lá que a máscara é reescrita todo
        /// frame; mexer na câmera direto não sobrevive a um quadro.
        /// </summary>
        [HideFromIl2Cpp] public static int MascaraOculta { get; set; } = 0;
        private bool EyeProbeDone { get; set; } = false;

        private Camera LastHijackedCamera { get; set; }
        private float LastOptimizationTime { get; set; } = 0f;
        private float LastCameraCheckTime { get; set; } = 0f;

        void Awake()
        {
            PluginLog.Info("[AmanatsuVR] Initializing global VRController...");

            // 1. Câmera principal de renderização 3D estéreo VR
            MainVRCamera = VRCamera.Create(gameObject, nameof(MainVRCamera), MAIN_VR_CAMERA_DEPTH);

            // 2. Tela de UI 3D virtual flutuante (uGUI)
            UIScreen = UIScreen.Create(gameObject, nameof(UIScreen), UI_SCREEN_LAYER,
                new UIScreenPanel[] {
                    new(UGUICapture.Create(gameObject, nameof(UGUICapture), UGUI_CAPTURE_LAYER).Texture),
                });

            // 3. Tradutor de rotação e translação dos trackers para primeira pessoa (classe puramente gerenciada)
            MotionTranslator = new TrackerMotionTranslator();
            MotionTranslator.Setup(MainVRCamera, this);

            // 4. Gerenciador da pistolinha d'água 6DoF (objeto puramente gerenciado)
            WaterGunHandler = new VRWaterGunHandler();

            // 5. Mira a laser e cursor virtual dos controles VR (objeto puramente gerenciado)
            ControllerLaser = new VRControllerLaser();
            ControllerLaser.Setup(MainVRCamera, UIScreen);

            WaterGunHandler.Setup(MainVRCamera, ControllerLaser, this);

            // Cena H: camera livre / primeira pessoa e atalhos nos controles
            HScene = new VRHScene();
            HScene.Setup(MainVRCamera, this, ControllerLaser);

            // 6. Tela de criacao de personagem: modelo dentro da UI 2D, 3D com o painel oculto
            CharCreation = new VRCharCreation();

            this.StartCoroutine(SetupRoutine());
        }

        [HideFromIl2Cpp]
        private IEnumerator SetupRoutine()
        {
            yield return new WaitForSeconds(0.15f);
            // O jogo as vezes abre minimizado ou atras de outra janela, e ai o cursor e os
            // cliques do controle vao parar no Windows.
            EntradaFisica.FocaJanela("inicio do VR");
            DisableGpuResidentDrawer();
            XRCullingFix.Install();
            ApplyTextureLayout();
            TuneRendererForStereo();
            UpdateCamera(false);

            if (PluginConfig.ManualEyeRender.Value && MainVRCamera != null && MainVRCamera.Normal != null)
            {
                XREyeRenderer.Create(gameObject, MainVRCamera.Normal);
                PluginLog.Info("[AmanatsuVR][EYE] manual per-eye rendering enabled.");
            }

            // A janela pode perder o foco durante o carregamento (logo, SteamVR abrindo o dashboard).
            yield return new WaitForSeconds(3f);
            EntradaFisica.FocaJanela("3 s depois do inicio");
        }

        [HideFromIl2Cpp]
        public void NotifySceneLoaded(Scene scene)
        {
            this.StartCoroutine(DelayedCameraUpdateRoutine());
        }

        [HideFromIl2Cpp]
        private IEnumerator DelayedCameraUpdateRoutine()
        {
            yield return new WaitForSeconds(0.1f);
            UpdateCamera(false);

            // A cena precisa terminar de instanciar antes da varredura de objetos rosa
            yield return new WaitForSeconds(1.0f);
            DisableErrorShaderRenderers();
            BlankOtherGameCameras(LastHijackedCamera);
            DisableGpuResidentDrawer();
            XRCullingFix.Install();
            DumpSceneDiagnostics();
            DumpWorldSpaceCanvases();
            DumpUnusualGraphics();
            SaveUICaptureToPng(SceneManager.GetActiveScene().name);
            ApplyTextureLayout();
            TuneRendererForStereo();
            CameraHijacker.RenderSpy.Arm(12);
            yield return this.StartCoroutine(CaptureBothEyesRoutine(SceneManager.GetActiveScene().name));
        }

        [HideFromIl2Cpp]
        public void UpdateCamera(bool forceUpdateOrientation)
        {
            if (!VRCamera.IsBaseHeadSet || forceUpdateOrientation)
            {
                VRCamera.UpdateViewport(MainVRCamera);
            }

            Camera targetCam = FindTargetCamera();
            if (targetCam != null && (targetCam != LastHijackedCamera || MainVRCamera.HijackMorto))
            {
                // Solta a anterior. O DisableHijackedCamera a mantem desligada todo frame
                // enquanto tiver Destination; sem soltar, a camera que deixamos para tras ficava
                // apagada para sempre e nunca podia voltar a ser o alvo.
                if (LastHijackedCamera != null && LastHijackedCamera != targetCam)
                {
                    var anterior = LastHijackedCamera.GetComponent<CameraHijacker>();
                    if (anterior != null) anterior.Destination = null;
                }

                LastHijackedCamera = targetCam;
                PluginLog.Info($"[AmanatsuVR] Binding VRCamera to new scene camera: '{targetCam.name}'");
                MainVRCamera.Hijack(targetCam);
            }
            else if (targetCam == null && LastHijackedCamera == null)
            {
                // Sem câmera 3D ainda (ex: tela de título inicial)
                // Configura MainVRCamera para renderizar o espaço vazio com a UI virtual
                if (MainVRCamera != null && MainVRCamera.Normal != null)
                {
                    MainVRCamera.Normal.cullingMask = (1 << UI_SCREEN_LAYER);
                    MainVRCamera.Normal.clearFlags = CameraClearFlags.Color;
                    MainVRCamera.Normal.backgroundColor = new Color(0.04f, 0.04f, 0.06f, 1.0f);
                    MainVRCamera.Normal.stereoTargetEye = StereoTargetEyeMask.Both;
                }
            }

            BlankOtherGameCameras(targetCam);

            // Na H quem posiciona o painel e o grip + gatilho (LinkToHead); uma troca de camera
            // do jogo no meio da cena nao pode arrasta-lo para outro angulo.
            if (UIScreen != null && MainVRCamera != null && !VRHScene.Ativo)
            {
                // before the HMD head exists (first camera), LinkToHead cannot place it yet
                if (MainVRCamera.VR != null && MainVRCamera.VR.head != null) UIScreen.LinkToHead(MainVRCamera, PluginConfig.UIScreenDistance.Value);
                else UIScreen.LinkToFront(MainVRCamera, PluginConfig.UIScreenDistance.Value);
                // Marcus: leaving the title and loading a save, the panel came up far away. During the load SteamVR
                // shows its own loading screen and the head pose can be stale; place it again once tracking is back.
                _relinkPanelAt = Time.unscaledTime + RelinkDelay;
            }
        }

        /// <summary>
        /// Qualquer outra câmera de cena que continue renderizando é MONO (stereoTargetEye None),
        /// e com XR ativo uma câmera mono só chega ao olho esquerdo. Apaga todas elas e acumula
        /// suas máscaras para que a VRCamera estéreo desenhe o mesmo conteúdo nos dois olhos.
        /// </summary>
        [HideFromIl2Cpp]
        private void BlankOtherGameCameras(Camera target)
        {
            int mask = 0;
            var cams = Camera.allCameras;
            if (cams == null) return;

            foreach (var c in cams)
            {
                if (c == null || c == target) continue;

                var hijacker = c.GetComponent<CameraHijacker>();
                if (hijacker != null)
                {
                    // Já apagada por nós numa passagem anterior (Destination null = apagada, sem destino)
                    if (hijacker.Destination == null) mask |= hijacker.LastCullingMask;
                    continue;
                }

                if (!IsValidGameCamera(c)) continue;

                PluginLog.Info($"[AmanatsuVR] Extra mono camera cleared: '{c.name}' depth={c.depth} mask=0x{c.cullingMask:X8} clear={c.clearFlags}");
                mask |= c.cullingMask;
                CameraHijacker.Hijack(c, null, false, false);
            }

            ExtraGameCullingMask = mask;
        }

        /// <summary>
        /// O GPU Resident Drawer (BatchRendererGroup) do Unity 6 não suporta XR multipass:
        /// tudo que ele gerencia é submetido só na primeira passada e some no olho direito.
        /// Sobra exatamente o que ele NÃO gerencia — no caso, uma árvore solta.
        /// </summary>
        [HideFromIl2Cpp]
        private static void DisableGpuResidentDrawer()
        {
            try
            {
                var asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                    ?.TryCast<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>();
                if (asset == null)
                {
                    asset = QualitySettings.renderPipeline
                        ?.TryCast<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>();
                }
                if (asset == null)
                {
                    PluginLog.Warning("[AmanatsuVR][GRD] UniversalRenderPipelineAsset not found.");
                    return;
                }

                PluginLog.Info($"[AmanatsuVR][GRD] asset='{asset.name}' mode={asset.gpuResidentDrawerMode}"
                    + $" occlusionInCameras={asset.gpuResidentDrawerEnableOcclusionCullingInCameras}");

                asset.gpuResidentDrawerEnableOcclusionCullingInCameras = false;

                // No olho direito o LineRenderer do laser desenha e 1388 MeshRenderers não.
                // A diferença entre eles é o SRP Batcher: geometria dinâmica de LineRenderer
                // não passa por ele, MeshRenderer passa. Desligar custa desempenho e é o teste
                // direto dessa hipótese.
                if (asset.useSRPBatcher)
                {
                    asset.useSRPBatcher = false;
                    PluginLog.Info("[AmanatsuVR][GRD] SRP Batcher DISABLED (right eye test).");
                }
                if (asset.gpuResidentDrawerMode != UnityEngine.Rendering.GPUResidentDrawerMode.Disabled)
                {
                    asset.gpuResidentDrawerMode = UnityEngine.Rendering.GPUResidentDrawerMode.Disabled;
                    PluginLog.Info("[AmanatsuVR][GRD] GPU Resident Drawer DISABLED for XR multipass.");
                }
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning($"[AmanatsuVR][GRD] failed to adjust: {ex.Message}");
            }
        }

        /// <summary>
        /// Espelha cada olho na janela e fotografa. Todo screenshot até agora mostrou o mesmo
        /// olho, então nunca vimos de fato o que o olho quebrado desenha.
        /// </summary>
        [HideFromIl2Cpp]
        private IEnumerator CaptureBothEyesRoutine(string tag)
        {
            string dir = System.IO.Path.Combine(Application.dataPath, "..", "AmanatsuVR_diag");
            System.IO.Directory.CreateDirectory(dir);

            foreach (var mode in new[] { Unity.XR.OpenVR.OpenVRSettings.MirrorViewModes.Left,
                                         Unity.XR.OpenVR.OpenVRSettings.MirrorViewModes.Right })
            {
                bool ok = true;
                try { Unity.XR.OpenVR.OpenVRSettings.SetMirrorViewMode((ushort)mode); }
                catch (System.Exception ex)
                {
                    ok = false;
                    PluginLog.Warning($"[AmanatsuVR][EYE] could not mirror {mode}: {ex.Message}");
                }
                if (!ok) yield break;

                yield return new WaitForSeconds(0.5f);
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, $"EYE_{tag}_{mode}.png"));
                yield return new WaitForSeconds(0.5f);
            }

            Unity.XR.OpenVR.OpenVRSettings.SetMirrorViewMode(
                (ushort)Unity.XR.OpenVR.OpenVRSettings.MirrorViewModes.Left);
            PluginLog.Info($"[AmanatsuVR][EYE] left/right pair saved for '{tag}'.");
        }

        /// <summary>
        /// O modo estéreo pedido ao OpenVRSettings não pega neste build (player compilado sem XR).
        /// O layout de textura do display subsystem é o interruptor de verdade:
        /// Texture2DArray = single pass instanced, SeparateTexture2Ds = multipass.
        /// </summary>
        /// <summary>
        /// No olho direito chegam o skybox e os transparentes (painel, laser), e falta toda a
        /// geometria OPACA. Isso não é culling — os PNGs por olho provaram que a visão direita
        /// desenha o mapa inteiro. É a passada opaca da segunda renderização que se perde, e o
        /// skybox ocupa o lugar dela. Mexe no que decide os alvos de cor/profundidade da URP.
        /// </summary>
        [HideFromIl2Cpp]
        private static void TuneRendererForStereo()
        {
            try
            {
                var asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                    ?.TryCast<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>();
                if (asset == null) return;

                var renderer = asset.GetRenderer(0);
                PluginLog.Info($"[AmanatsuVR][RENDER] renderer='{(renderer != null ? renderer.GetType().Name : "NULL")}'"
                    + $" | asset.supportsCameraDepthTexture={asset.supportsCameraDepthTexture}"
                    + $" supportsCameraOpaqueTexture={asset.supportsCameraOpaqueTexture}"
                    + $" msaa={asset.msaaSampleCount} hdr={asset.supportsHDR}"
                    + $" renderScale={asset.renderScale}");

                // Sem textura intermediária a URP desenha direto na textura do olho, e a
                // segunda passada é a que erra o alvo. Forçar intermediária + blit dá a cada
                // passada um alvo próprio, que é o que o probe mono fazia quando deu certo.
                var uni = renderer?.TryCast<UnityEngine.Rendering.Universal.UniversalRenderer>();
                if (uni != null)
                {
                    PluginLog.Info($"[AmanatsuVR][RENDER] renderingModeActual={uni.renderingModeActual}"
                        + $" depthPriming={uni.depthPrimingMode}");

                    uni.depthPrimingMode = UnityEngine.Rendering.Universal.DepthPrimingMode.Disabled;
                    PluginLog.Info("[AmanatsuVR][RENDER] depthPriming=Disabled");
                }

                if (renderer != null)
                {
                    renderer.useRenderPassEnabled = false;
                    PluginLog.Info("[AmanatsuVR][RENDER] useRenderPassEnabled=false (Native RenderPass off).");
                }

                // Forward+ NÃO suporta XR multipass — é limitação documentada da URP. Ele monta
                // a lista de luzes agrupada por câmera e só a primeira passada a recebe; a
                // geometria opaca depende dela, o skybox e os transparentes não. É exatamente o
                // que sobra no olho direito. O caminho Forward clássico não tem essa restrição.
                var datas = asset.m_RendererDataList;
                if (datas != null)
                {
                    for (int i = 0; i < datas.Length; i++)
                    {
                        var urd = datas[i]?.TryCast<UnityEngine.Rendering.Universal.UniversalRendererData>();
                        if (urd == null) continue;

                        PluginLog.Info($"[AmanatsuVR][RENDER] rendererData[{i}]='{urd.name}' renderingMode={urd.renderingMode}");
                        if (urd.renderingMode != UnityEngine.Rendering.Universal.RenderingMode.Forward)
                        {
                            urd.renderingMode = UnityEngine.Rendering.Universal.RenderingMode.Forward;
                            urd.SetDirty();
                            PluginLog.Info($"[AmanatsuVR][RENDER] rendererData[{i}] -> Forward (Forward+ does not do multipass).");
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning($"[AmanatsuVR][RENDER] failed: {ex.Message}");
            }
        }

        /// <summary>
        /// O RenderDoc provou onde o olho direito se perde: a passada opaca dele emite 3 draws
        /// (os mesmos 3 primeiros do olho esquerdo) contra 50+309 do esquerdo, e zero
        /// instanciados. Nao e depth nem blend — a geometria nunca e submetida, ou seja, o
        /// conjunto de culling da segunda passada chega vazio. A URP, em XR, nao usa o culling
        /// da camera: usa o que o XRDisplaySubsystem devolve por cullingPassIndex. Entao e esse
        /// o numero que precisa ser lido.
        /// </summary>
        [HideFromIl2Cpp]
        private static void LogXrCullingParams(Camera cam)
        {
            try
            {
                var list = new Il2CppSystem.Collections.Generic.List<UnityEngine.XR.XRDisplaySubsystem>();
                SubsystemManager.GetSubsystems(list);
                foreach (var display in list)
                {
                    if (display == null || !display.running) continue;

                    int passes = display.GetRenderPassCount();
                    PluginLog.Info($"[AmanatsuVR][XRCULL] passes={passes} layout={display.textureLayout}");

                    for (int i = 0; i < passes; i++)
                    {
                        display.GetRenderPass(i, out var pass);
                        PluginLog.Info($"[AmanatsuVR][XRCULL] pass {i}: cullingPassIndex={pass.cullingPassIndex}"
                            + $" renderParams={pass.GetRenderParameterCount()}");
                    }

                    // Le explicitamente os indices 0 e 1 mesmo que so exista um pass: se o
                    // indice 1 devolver mascara zero ou planos degenerados, achamos a causa.
                    for (int ci = 0; ci < 2; ci++)
                    {
                        display.GetCullingParameters(cam, ci, out var cp);

                        PluginLog.Info($"[AmanatsuVR][XRCULL] culling {ci}: mask=0x{cp.cullingMask:X8}"
                            + $" planes={cp.cullingPlaneCount} origin={cp.origin} ortho={cp.isOrthographic}"
                            + $" shadow={cp.shadowDistance}");
                        PluginLog.Info($"[AmanatsuVR][XRCULL] culling {ci}: matrix={DescribeProjection(cp.cullingMatrix)}");
                        for (int p = 0; p < cp.cullingPlaneCount && p < 6; p++)
                        {
                            var pl = cp.GetCullingPlane(p);
                            PluginLog.Info($"[AmanatsuVR][XRCULL]   plane {p}: n={pl.normal} d={pl.distance:F3}");
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning($"[AmanatsuVR][XRCULL] failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        [HideFromIl2Cpp]
        private static void ApplyTextureLayout()
        {
            try
            {
                var list = new Il2CppSystem.Collections.Generic.List<UnityEngine.XR.XRDisplaySubsystem>();
                SubsystemManager.GetSubsystems(list);
                if (list.Count == 0)
                {
                    PluginLog.Warning("[AmanatsuVR][XR] no XRDisplaySubsystem found.");
                    return;
                }

                // O plugin OpenVR nativo está travado em MultiPass (volumeDepth=1): pedimos
                // SinglePassInstanced, o loader leu SinglePassInstanced, e o runtime continuou
                // MultiPass. Só que a URP reescrevia o layout para Texture2DArray todo frame, e
                // aí a segunda passada gravava numa fatia inexistente — o olho direito. Este
                // sinalizador é o que faz a URP parar de exigir array.
                if (!PluginConfig.UseSinglePassInstanced.Value)
                {
                    try
                    {
                        UnityEngine.Experimental.Rendering.XRSystem.singlePassAllowed = false;
                        PluginLog.Info($"[AmanatsuVR][XR] XRSystem.singlePassAllowed={UnityEngine.Experimental.Rendering.XRSystem.singlePassAllowed}");
                    }
                    catch (System.Exception ex)
                    {
                        PluginLog.Warning($"[AmanatsuVR][XR] singlePassAllowed failed: {ex.Message}");
                    }
                }

                var wanted = PluginConfig.UseSinglePassInstanced.Value
                    ? UnityEngine.XR.XRDisplaySubsystem.TextureLayout.Texture2DArray
                    : UnityEngine.XR.XRDisplaySubsystem.TextureLayout.SeparateTexture2Ds;

                foreach (var display in list)
                {
                    if (display == null) continue;
                    PluginLog.Info($"[AmanatsuVR][XR] display running={display.running}"
                        + $" layout={display.textureLayout} supported={display.supportedTextureLayouts}");

                    if (display.textureLayout != wanted)
                    {
                        display.textureLayout = wanted;
                        PluginLog.Info($"[AmanatsuVR][XR] textureLayout changed to {display.textureLayout}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning($"[AmanatsuVR][XR] failed to adjust textureLayout: {ex.Message}");
            }
        }

        /// <summary>
        /// Rosa = shader/material quebrado. Já varremos todo Renderer da cena (nada) e todo
        /// canvas WorldSpace (nada). Sobra qualquer Graphic com shader fora do comum:
        /// SoftMask, Unmask, CompositeCanvas — este jogo usa os três.
        /// </summary>
        [HideFromIl2Cpp]
        private void DumpUnusualGraphics()
        {
            int shown = 0;
            PluginLog.Info("[AmanatsuVR][GFX] Graphics with unusual shader:");
            foreach (var g in FindObjectsOfType<UnityEngine.UI.Graphic>(true))
            {
                if (g == null || !g.enabled || !g.gameObject.activeInHierarchy) continue;
                if (g.transform.root == transform.root) continue;

                var mat = g.materialForRendering;
                string shader = (mat != null && mat.shader != null) ? mat.shader.name : "SEM MATERIAL";
                if (shader == "UI/Default" || shader.StartsWith("TextMeshPro")) continue;

                if (++shown > 40) { PluginLog.Info("[AmanatsuVR][GFX]   ... (truncated at 40)"); break; }

                var canvas = g.canvas;
                PluginLog.Info($"[AmanatsuVR][GFX]   '{GetHierarchyPath(g.transform)}' type={g.GetIl2CppType().Name}"
                    + $" layer={g.gameObject.layer} color={g.color} material='{(mat != null ? mat.name : "-")}' shader='{shader}'"
                    + $" canvas={(canvas != null ? canvas.renderMode.ToString() : "-")} pos={g.transform.position}");
            }
        }

        /// <summary>Planos do frustum a partir de proj*view, normalizados. Ordem: esq, dir, baixo, cima, perto, longe.</summary>
        [HideFromIl2Cpp]
        private static Vector4[] ExtractFrustumPlanes(Matrix4x4 m)
        {
            var p = new Vector4[6];
            for (int i = 0; i < 3; i++)
            {
                // linha 3 +/- linha i
                p[i * 2] = new Vector4(m.m30 + m[i, 0], m.m31 + m[i, 1], m.m32 + m[i, 2], m.m33 + m[i, 3]);
                p[i * 2 + 1] = new Vector4(m.m30 - m[i, 0], m.m31 - m[i, 1], m.m32 - m[i, 2], m.m33 - m[i, 3]);
            }
            for (int i = 0; i < 6; i++)
            {
                float len = Mathf.Sqrt(p[i].x * p[i].x + p[i].y * p[i].y + p[i].z * p[i].z);
                if (len > 1e-6f) p[i] /= len;
            }
            return p;
        }

        /// <summary>AABB dentro do frustum? Testa o vértice positivo de cada plano.</summary>
        [HideFromIl2Cpp]
        private static bool AabbInFrustum(Vector4[] planes, Bounds b)
        {
            Vector3 c = b.center, e = b.extents;
            foreach (var pl in planes)
            {
                // distância do centro mais o raio projetado na normal
                float dist = pl.x * c.x + pl.y * c.y + pl.z * c.z + pl.w;
                float radius = Mathf.Abs(pl.x) * e.x + Mathf.Abs(pl.y) * e.y + Mathf.Abs(pl.z) * e.z;
                if (dist + radius < 0f) return false;
            }
            return true;
        }

        /// <summary>
        /// Renderiza a visão de CADA olho com uma câmera mono descartável, usando as matrizes
        /// de view/projeção estéreo da câmera VR, e grava PNG. Isto separa duas hipóteses que
        /// nenhuma medição anterior separou:
        ///   - PNG do olho direito com o mapa completo => culling e render estão corretos,
        ///     o defeito está na passada/submissão XR (fix = submeter os olhos na mão).
        ///   - PNG do olho direito vazio => o culling do olho direito é que descarta tudo.
        /// </summary>
        private void CaptureEyeViewsToPng(string tag)
        {
            var cam = MainVRCamera != null ? MainVRCamera.Normal : null;
            if (cam == null) { PluginLog.Warning("[AmanatsuVR][EYE] no VR camera."); return; }

            string dir = System.IO.Path.Combine(Application.dataPath, "..", "AmanatsuVR_diag");
            System.IO.Directory.CreateDirectory(dir);

            var probeGo = new GameObject("AmanatsuVR_EyeProbe");
            try
            {
                var probe = probeGo.AddComponent<Camera>();
                probe.CopyFrom(cam);
                probe.enabled = false;
                probe.stereoTargetEye = StereoTargetEyeMask.None;
                probe.useOcclusionCulling = false;
                probe.cullingMask = cam.cullingMask;
                probe.clearFlags = cam.clearFlags;
                probe.backgroundColor = cam.backgroundColor;

                var addData = probeGo.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                if (addData != null)
                {
                    addData.allowXRRendering = false;
                    addData.renderPostProcessing = false;
                    addData.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.None;
                    addData.renderType = UnityEngine.Rendering.Universal.CameraRenderType.Base;
                }

                var rt = new RenderTexture(1024, 1024, 24, RenderTextureFormat.ARGB32);
                var renderers = FindObjectsOfType<Renderer>(false);

                for (int i = 0; i < 2; i++)
                {
                    var eye = i == 0 ? Camera.StereoscopicEye.Left : Camera.StereoscopicEye.Right;
                    string nome = i == 0 ? "esquerdo" : "direito";

                    var view = cam.GetStereoViewMatrix(eye);
                    var proj = cam.GetStereoProjectionMatrix(eye);

                    // Contagem puramente matemática: quantos renderizadores caem no frustum
                    // deste olho. Se os dois números baterem, o culling não é o culpado.
                    // GeometryUtility.CalculateFrustumPlanes foi removido no strip deste build,
                    // então os planos saem direto da matriz (Gribb/Hartmann).
                    // A contagem é acessório: se algo nela falhar, o PNG ainda tem que sair.
                    int dentro = -1;
                    try
                    {
                        dentro = 0;
                        var planes = ExtractFrustumPlanes(proj * view);
                        foreach (var r in renderers)
                        {
                            if (r == null || !r.enabled) continue;
                            if ((cam.cullingMask & (1 << r.gameObject.layer)) == 0) continue;
                            if (AabbInFrustum(planes, r.bounds)) dentro++;
                        }
                    }
                    catch (System.Exception ex) { PluginLog.Warning($"[AmanatsuVR][EYE] count failed: {ex.Message}"); }

                    probe.worldToCameraMatrix = view;
                    probe.projectionMatrix = proj;
                    probe.targetTexture = rt;
                    probe.Render();

                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                    tex.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;

                    var encoded = ImageConversion.EncodeToPNG(tex);
                    var bytes = new byte[encoded.Length];
                    for (int k = 0; k < encoded.Length; k++) bytes[k] = encoded[k];
                    string path = System.IO.Path.Combine(dir, $"PROBE_{nome}_{tag}.png");
                    System.IO.File.WriteAllBytes(path, bytes);
                    Destroy(tex);

                    PluginLog.Info($"[AmanatsuVR][EYE] {nome}: {dentro}/{renderers.Length} renderers in frustum, PNG='{path}'");
                }

                // Teoria do Marcus: a cabeça pode estar DENTRO de um objeto grande que oclui o
                // resto. [PERTO] mede distância ao centro e não pegaria um objeto imenso cujo
                // centro está longe; isto testa a contenção do ponto de vista pelos bounds.
                Vector3 cabeca = cam.transform.position;
                int dentroDeAlgo = 0;
                foreach (var r in renderers)
                {
                    if (r == null || !r.enabled) continue;
                    if (!r.bounds.Contains(cabeca)) continue;
                    dentroDeAlgo++;
                    var b = r.bounds;
                    PluginLog.Info($"[AmanatsuVR][INSIDE] '{GetHierarchyPath(r.transform)}' layer={r.gameObject.layer}"
                        + $" type={r.GetType().Name} size={b.size} center={b.center}"
                        + $" shader='{(r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "-")}'");
                }
                PluginLog.Info($"[AmanatsuVR][INSIDE] head at {cabeca}: inside the bounds of {dentroDeAlgo} renderers.");

                probe.targetTexture = null;
                rt.Release();
            }
            catch (System.Exception ex)
            {
                PluginLog.Error($"[AmanatsuVR][EYE] failed: {ex}");
            }
            finally
            {
                Destroy(probeGo);
            }
        }

        /// <summary>
        /// Salva a RenderTexture da captura de UI em PNG (tecla F9). Se o quadrado rosa estiver
        /// no PNG ele nasce na captura de uGUI; se não estiver, é objeto do mundo 3D.
        /// </summary>
        [HideFromIl2Cpp]
        private void SaveUICaptureToPng(string tag)
        {
            try
            {
                var rt = UIScreen != null && UIScreen.Panels != null && UIScreen.Panels.Length > 0
                    ? UIScreen.Panels[0].Texture : null;
                if (rt == null) { PluginLog.Warning("[AmanatsuVR][PNG] no UI RenderTexture."); return; }

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;

                var encoded = ImageConversion.EncodeToPNG(tex);
                var bytes = new byte[encoded.Length];
                for (int i = 0; i < encoded.Length; i++) bytes[i] = encoded[i];

                string dir = System.IO.Path.Combine(Application.dataPath, "..", "AmanatsuVR_diag");
                System.IO.Directory.CreateDirectory(dir);

                string path = System.IO.Path.Combine(dir, $"UI_{tag}.png");
                System.IO.File.WriteAllBytes(path, bytes);
                Destroy(tex);
                PluginLog.Info($"[AmanatsuVR][PNG] uGUI capture saved to '{path}'");

                // A janela do desktop espelha um dos olhos: é a visão real, com o objeto rosa.
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, $"OLHO_{tag}.png"));
            }
            catch (System.Exception ex)
            {
                PluginLog.Error($"[AmanatsuVR][PNG] failed: {ex}");
            }
        }

        /// <summary>
        /// O objeto rosa é um Graphic de canvas WorldSpace (CanvasRenderer não é Renderer,
        /// por isso nunca apareceu no dump de renderizadores). Lista todos eles.
        /// </summary>
        [HideFromIl2Cpp]
        private void DumpWorldSpaceCanvases()
        {
            int shown = 0;
            PluginLog.Info("[AmanatsuVR][CANVAS] canvases WorldSpace:");
            foreach (var canvas in FindObjectsOfType<Canvas>(true))
            {
                if (canvas == null || canvas.renderMode != RenderMode.WorldSpace) continue;
                if (canvas.transform.root == transform.root) continue;

                PluginLog.Info($"[AmanatsuVR][CANVAS]   '{GetHierarchyPath(canvas.transform)}' layer={canvas.gameObject.layer}"
                    + $" active={canvas.gameObject.activeInHierarchy} pos={canvas.transform.position}"
                    + $" scale={canvas.transform.lossyScale} cam={(canvas.worldCamera != null ? canvas.worldCamera.name : "-")}");

                foreach (var g in canvas.gameObject.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                {
                    if (g == null || !g.enabled) continue;
                    if (++shown > 30) { PluginLog.Info("[AmanatsuVR][CANVAS]   ... (truncated at 30)"); return; }

                    var mat = g.materialForRendering;
                    PluginLog.Info($"[AmanatsuVR][CANVAS]     '{g.name}' type={g.GetIl2CppType().Name} layer={g.gameObject.layer}"
                        + $" color={g.color} material='{(mat != null ? mat.name : "NULL")}'"
                        + $" shader='{(mat != null && mat.shader != null ? mat.shader.name : "-")}'");
                }
            }
        }

        /// <summary>
        /// O objeto rosa aparece em UM olho só, ou seja é desenhado por algo mono.
        /// Lista TODAS as câmeras (inclusive as que IsValidGameCamera rejeita) e tudo que
        /// está a menos de 3 m da cabeça, que é onde o objeto reclamado fica.
        /// </summary>
        [HideFromIl2Cpp]
        private void DumpSceneDiagnostics()
        {
            var desc = UnityEngine.XR.XRSettings.eyeTextureDesc;
            PluginLog.Info($"[AmanatsuVR][XR] mode={UnityEngine.XR.XRSettings.stereoRenderingMode}"
                + $" eyeTexture={desc.width}x{desc.height} volumeDepth={desc.volumeDepth}"
                + " (volumeDepth=2 => single pass instanced for real)");

            // Se o cenário for entidades (BRG), quase não existem MeshRenderer de GameObject
            // na cena. Se existirem às centenas, a culpa do olho direito NÃO é do Entities
            // Graphics e a rota do single pass é beco sem saída.
            int renderers = 0, meshRenderers = 0, skinned = 0;
            foreach (var r in FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled) continue;
                renderers++;
                if (r.TryCast<MeshRenderer>() != null) meshRenderers++;
                else if (r.TryCast<SkinnedMeshRenderer>() != null) skinned++;
            }
            PluginLog.Info($"[AmanatsuVR][GO] active GameObject renderers: total={renderers}"
                + $" mesh={meshRenderers} skinned={skinned} (few => scenery is Entities/BRG)");

            // O laser (LineRenderer) sobrevive nos dois olhos e todo MeshRenderer some num deles.
            // Se a teoria for culling, a diferença tem que estar em bounds/isVisible.
            var cam = MainVRCamera != null ? MainVRCamera.Normal : null;
            if (cam != null)
            {
                bool matrizExplicita = cam.cullingMatrix != (cam.projectionMatrix * cam.worldToCameraMatrix);
                PluginLog.Info($"[AmanatsuVR][CULL] cullingMatrix differs from proj*view? {matrizExplicita}"
                    + $" | occlusionCulling={cam.useOcclusionCulling} layerCullSpherical={cam.layerCullSpherical}");

                // O laranja a 2 m aparece nos dois olhos e o roxo a 6 m só num: isso é corte por
                // distância. Decodifica near/far de cada projeção estéreo para achar o olho que
                // está com o far plane curto.
                PluginLog.Info($"[AmanatsuVR][PROJ] camera near={cam.nearClipPlane:F3} far={cam.farClipPlane:F1}"
                    + $" | mono {DescribeProjection(cam.projectionMatrix)}"
                    + $" | left {DescribeProjection(cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left))}"
                    + $" | right {DescribeProjection(cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right))}");
            }
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                PluginLog.Info($"[AmanatsuVR][CULL]   '{r.gameObject.name}' type={r.GetIl2CppType().Name}"
                    + $" active={r.enabled} visible={r.isVisible} bounds={r.bounds.center}/{r.bounds.size}");
            }

            var cams = Camera.allCameras;
            PluginLog.Info($"[AmanatsuVR][CAMS] {(cams == null ? 0 : cams.Length)} active cameras:");
            if (cams != null)
            {
                foreach (var c in cams)
                {
                    if (c == null) continue;
                    PluginLog.Info($"[AmanatsuVR][CAMS]   '{GetHierarchyPath(c.transform)}' tag={c.tag} depth={c.depth}"
                        + $" eye={c.stereoTargetEye} mask=0x{c.cullingMask:X8} clear={c.clearFlags}"
                        + $" rt={(c.targetTexture != null ? c.targetTexture.name : "-")}"
                        + $" hijacker={(c.GetComponent<CameraHijacker>() != null)} valid={IsValidGameCamera(c)}");

                    // Se as duas posições de olho não estiverem a ~6,5 cm uma da outra e em volta
                    // da posição da câmera, a matriz de view por olho está quebrada.
                    if (c.stereoEnabled)
                    {
                        Vector3 le = c.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse.GetColumn(3);
                        Vector3 re = c.GetStereoViewMatrix(Camera.StereoscopicEye.Right).inverse.GetColumn(3);
                        PluginLog.Info($"[AmanatsuVR][CAMS]     transform={c.transform.position} eyeL={le} eyeR={re}"
                            + $" separation={Vector3.Distance(le, re):F4}m fov={c.fieldOfView:F1} near={c.nearClipPlane:F3}"
                            + $" scale={c.transform.lossyScale} localScale={c.transform.localScale}");
                    }
                }
            }

            Vector3 head = (MainVRCamera != null && MainVRCamera.VR != null && MainVRCamera.VR.head != null)
                ? MainVRCamera.VR.head.position
                : transform.position;

            // INCLUI a nossa própria hierarquia: na Title a VRCamera só enxerga a layer 31,
            // então o objeto rosa só pode ser nosso (painel, laser ou retículo).
            int shown = 0;
            PluginLog.Info($"[AmanatsuVR][NEAR] renderers within 5 m of the head {head}:");
            foreach (var r in FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled) continue;
                if (Vector3.Distance(r.bounds.center, head) > 5.0f) continue;
                if (++shown > 40) { PluginLog.Info("[AmanatsuVR][NEAR]   ... (truncated at 40)"); break; }

                var mat = r.sharedMaterial;
                PluginLog.Info($"[AmanatsuVR][NEAR]   '{GetHierarchyPath(r.transform)}' layer={r.gameObject.layer}"
                    + $" type={r.GetIl2CppType().Name} size={r.bounds.size} dist={Vector3.Distance(r.bounds.center, head):F2}"
                    + $" material='{(mat != null ? mat.name : "NULL")}' shader='{(mat != null && mat.shader != null ? mat.shader.name : "-")}'");
            }
        }

        /// <summary>
        /// Objetos com shader de erro do Unity aparecem como blocos rosa/magenta flutuando na cena.
        /// Varre uma vez por cena, registra o caminho do objeto e desliga o renderizador.
        /// </summary>
        [HideFromIl2Cpp]
        private void DisableErrorShaderRenderers()
        {
            foreach (var r in FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled || r.transform.root == transform.root) continue;

                var mat = r.sharedMaterial;
                if (mat == null)
                {
                    PluginLog.Warning($"[AmanatsuVR] Renderer without material (renders pink): '{GetHierarchyPath(r.transform)}'");
                    continue;
                }
                if (mat.shader == null || !mat.shader.name.Contains("InternalError")) continue;

                PluginLog.Warning($"[AmanatsuVR] Renderer with error shader DISABLED: '{GetHierarchyPath(r.transform)}' (material='{mat.name}')");
                r.enabled = false;
            }
        }

        /// <summary>
        /// Extrai near/far/fov de uma matriz de projeção perspectiva:
        /// m22 = -(f+n)/(f-n) e m23 = -2fn/(f-n)  =>  n = m23/(m22-1), f = m23/(m22+1).
        /// </summary>
        [HideFromIl2Cpp]
        private static string DescribeProjection(Matrix4x4 p)
        {
            float m22 = p.m22, m23 = p.m23;
            float near = m23 / (m22 - 1f);
            float far = m23 / (m22 + 1f);
            float fovY = 2f * Mathf.Atan(1f / p.m11) * Mathf.Rad2Deg;
            return $"near={near:F3} far={far:F1} fovY={fovY:F1}";
        }

        [HideFromIl2Cpp]
        private static string GetHierarchyPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        public void Recenter()
        {
            if (MainVRCamera != null)
            {
                VRCamera.UpdateViewport(MainVRCamera);
                if (UIScreen != null)
                {
                    UIScreen.LinkToFront(MainVRCamera, PluginConfig.UIScreenDistance.Value);
                }
            }
        }

        private Camera FindTargetCamera()
        {
            // Camera.allCameras so devolve camera HABILITADA - e quem desabilita a camera do jogo
            // somos nos, no DisableHijackedCamera. Somado a rejeicao de quem tem CameraHijacker,
            // depois do primeiro hijack nenhuma camera do jogo voltava a ser elegivel: ao morrer
            // a camera alvo (fim da massagem) isto aqui devolvia null para sempre, o rig
            // congelava no angulo da massagem e trocar de ponto no mapa nao mudava nada.
            // Por isso a varredura inclui inativas e as marcas do mod nao desclassificam.
            var cams = FindObjectsOfType<Camera>(true);
            if (cams == null) return null;

            // Quem manda na tela e a profundidade, nao a ordem em que a Unity devolve o array:
            // com duas cameras tagueadas MainCamera vivas ao mesmo tempo (a do oleo e a do mapa)
            // sair na primeira que tivesse a tag era sorteio. A tag so desempata.

            Camera melhor = null;
            float melhorDepth = float.MinValue;
            bool melhorTagueada = false;

            foreach (var c in cams)
            {
                if (!IsValidGameCamera(c, true)) continue;

                bool tagueada = c.CompareTag("MainCamera");
                if (melhor == null || c.depth > melhorDepth
                    || (c.depth == melhorDepth && tagueada && !melhorTagueada))
                {
                    melhor = c;
                    melhorDepth = c.depth;
                    melhorTagueada = tagueada;
                }
            }

            return melhor;
        }

        private float _ultimoLogCam = -10f;
        private string _ultimaListaCam = "";

        /// <summary>
        /// Quem esta concorrendo pela tela, com profundidade e tag. Duas cameras vivas com a tag
        /// MainCamera foi o que travou a visao no angulo da massagem; se sobrar alguma que eu nao
        /// esperava, e aqui que aparece. Temporario, sai quando a massagem fechar.
        /// </summary>
        [HideFromIl2Cpp]
        private void LogCandidatasDeCamera()
        {
            if (Time.unscaledTime - _ultimoLogCam < 5f) return;
            _ultimoLogCam = Time.unscaledTime;

            try
            {
                var cams = FindObjectsOfType<Camera>(true);
                if (cams == null) return;

                var sb = new System.Text.StringBuilder();
                foreach (var c in cams)
                {
                    if (c == null) continue;
                    sb.Append($" '{c.name}'(d={c.depth:F0} tag={(c.CompareTag("MainCamera") ? "M" : "-")}"
                        + $" on={(c.enabled ? "1" : "0")} ok={(IsValidGameCamera(c, true) ? "1" : "0")})");
                }

                string lista = sb.ToString();
                if (lista == _ultimaListaCam) return;
                _ultimaListaCam = lista;

                PluginLog.Info($"[AmanatsuVR][CAM] target='{(LastHijackedCamera != null ? LastHijackedCamera.name : "-")}'"
                    + $" blocked='{(VRControllerLaser.CameraDaMassagemMorta != null ? VRControllerLaser.CameraDaMassagemMorta.name : "-")}'"
                    + $" |{lista}");
            }
            catch { }
        }

        private bool IsValidGameCamera(Camera c) => IsValidGameCamera(c, false);

        /// <param name="aceitarNossasMarcas">
        /// Aceita camera que so e inelegivel por causa do MOD: o CameraHijacker que nos mesmos
        /// penduramos e o enabled=false que o DisableHijackedCamera aplica todo frame. Quem
        /// escolhe o alvo precisa disto; quem decide o que APAGAR nao, senao apagariamos de novo
        /// o que ja esta apagado.
        /// </param>
        private bool IsValidGameCamera(Camera c, bool aceitarNossasMarcas)
        {
            if (c == null || !c.gameObject.activeInHierarchy) return false;

            // Bloqueia qualquer câmera ligada à raiz do mod VR
            if (c.transform.root == transform.root) return false;

            // Bloqueia por nomes internos do mod VR
            string name = c.gameObject.name;
            if (name.Contains("VR") || name.Contains("UGUI") || name.Contains("UIScreen") || name.Contains("Capture")) return false;

            // Bloqueia câmeras que têm componentes do mod ou do SteamVR
            if (c.GetComponent<SteamVR_Camera>() != null || c.GetComponent<SteamVR_TrackedObject>() != null) return false;

            bool nossa = c.GetComponent<CameraHijacker>() != null;
            if (nossa && !aceitarNossasMarcas) return false;
            if (!c.enabled && !nossa) return false;

            // A camera do oleo sobrevive a massagem com a tag MainCamera, e por isso continuava
            // ganhando de 'Main Camera' do mapa depois que a cena acabou.
            if (VRControllerLaser.CameraDaMassagemMorta != null && c == VRControllerLaser.CameraDaMassagemMorta) return false;

            // Bloqueia câmeras com targetTexture (off-screen capture)
            if (c.targetTexture != null) return false;

            // Bloqueia câmeras com cullingMask vazio ou apenas UI. Com o hijack rodando a
            // cullingMask real fica guardada no CameraHijacker: durante o render ele zera a da
            // camera e devolve no fim, e ler a zerada aqui reprovaria a propria camera alvo.
            int mascara = c.cullingMask;
            if (nossa)
            {
                var h = c.GetComponent<CameraHijacker>();
                if (h != null && h.LastCullingMask != 0) mascara = h.LastCullingMask;
            }
            int uiMask = (1 << 5) | (1 << 15) | (1 << 31);
            if (mascara == 0 || (mascara & ~uiMask) == 0) return false;

            return true;
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9)) SaveUICaptureToPng($"F9_{Time.frameCount}");
            if (Input.GetKeyDown(KeyCode.F10)) CaptureEyeViewsToPng($"F10_{Time.frameCount}");

            FrameWatch.Update();
            AlternarPainel();
            MoveWithStick();
            RelinkPanelAfterSceneChange();

            MotionTranslator?.Update();
            CharCreation?.Update();
            HScene?.Update();
            ControllerLaser?.Update();
            WaterGunHandler?.Update();
            PauseWatch.Update();

            // Verifica periodicamente (a cada 0.5s) se a câmera principal da cena mudou
            if (Time.unscaledTime - LastCameraCheckTime > 0.5f)
            {
                LastCameraCheckTime = Time.unscaledTime;
                LogCandidatasDeCamera();

                var currentCam = FindTargetCamera();
                bool morto = MainVRCamera != null && MainVRCamera.HijackMorto;
                if (currentCam != null && (currentCam != LastHijackedCamera || morto))
                {
                    if (morto) PluginLog.Info($"[AmanatsuVR] Camera binding dead; rebinding to '{currentCam.name}'.");
                    UpdateCamera(false);
                }
                else if (!morto)
                {
                    // Cenas aditivas (map000) trazem câmeras novas sem trocar a câmera principal
                    BlankOtherGameCameras(LastHijackedCamera);
                }

                // F10 exige foco da janela do desktop, que não existe com o headset na cabeça.
                // Dispara a sonda sozinha quando o mapa de fato terminou de popular.
                if (!EyeProbeDone && FindObjectsOfType<Renderer>(false).Length > 500)
                {
                    EyeProbeDone = true;
                    CaptureEyeViewsToPng(SceneManager.GetActiveScene().name);
                    if (MainVRCamera?.Normal != null) LogXrCullingParams(MainVRCamera.Normal);
                    RenderDocApi.Capture();
                }
            }

            // Otimizações de iluminação/sombras executadas a cada 2 segundos (não em todo frame)
            if (Time.unscaledTime - LastOptimizationTime > 2.0f)
            {
                LastOptimizationTime = Time.unscaledTime;
                OptimizeVRExperience();
            }
        }

        /// <summary>
        /// Painel visivel manda o cursor; painel escondido manda a mao. Com a pistola d'agua e
        /// a mesma chave: escondido atira, visivel deixa clicar no botao de sair - que era o
        /// que nao tinha jeito de alcancar.
        /// </summary>
        public static bool PainelVisivel { get; private set; } = true;

        private bool _gripSegurado = false;

        [HideFromIl2Cpp]
        public void MostrarPainel(bool visivel)
        {
            if (PainelVisivel == visivel) return;
            PainelVisivel = visivel;
            var painel = UIScreen != null ? UIScreen.MainPanelObject : null;
            if (painel != null) painel.SetActive(visivel);
            PluginLog.Info($"[AmanatsuVR][PANEL] visible={visivel}");
        }

        /// <summary>
        /// O grip de qualquer mao alterna os dois modos. O menu do jogo fica sempre aberto no
        /// mapa, entao sem isto nao ha como mirar na garota: o painel cobre o campo de visao e
        /// a coordenada que o jogo recebe e a do furo no painel.
        /// </summary>
        private void AlternarPainel()
        {
            // Na H o grip sozinho e modificador (girar/subir a camera); o painel e grip + gatilho.
            if (VRHScene.Ativo) { _gripSegurado = true; return; }

            bool pressionado = SteamVRInput.ReadGrab(true) || SteamVRInput.ReadGrab(false);

            // Grip + stick now turns/rises the view, so the panel toggles on RELEASE of a grip that did not move it.
            if (pressionado && !_gripSegurado) _gripMoved = false;
            if (!pressionado && _gripSegurado && !_gripMoved)
            {
                bool show = !PainelVisivel;
                MostrarPainel(show);
                // Marcus: now that the view moves on the map/massage, the panel opens in front of the CURRENT head, as in H.
                if (show) UIScreen?.LinkToHead(MainVRCamera, PluginConfig.UIScreenDistance.Value);
            }
            _gripSegurado = pressionado;
        }

        private bool _gripMoved;
        private float _relinkPanelAt = -1f;
        private const float RelinkDelay = 1f;
        private string _lastScene;

        private void RelinkPanelAfterSceneChange()
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (scene != _lastScene) { _lastScene = scene; _relinkPanelAt = Time.unscaledTime + RelinkDelay; }
            if (_relinkPanelAt < 0f || Time.unscaledTime < _relinkPanelAt) return;
            _relinkPanelAt = -1f;
            if (VRHScene.Ativo || UIScreen == null || MainVRCamera == null || MainVRCamera.VR == null || MainVRCamera.VR.head == null) return;
            UIScreen.LinkToHead(MainVRCamera, PluginConfig.UIScreenDistance.Value);
            var sc = UIScreen.MainPanelObject;
            if (sc != null)
                PluginLog.Info($"[AmanatsuVR][PANEL] repositioned after scene change '{scene}': distance from head {Vector3.Distance(sc.transform.position, MainVRCamera.VR.head.position):F2}"
                    + $" (local head {MainVRCamera.VR.head.localPosition}, rig scale {MainVRCamera.VR.origin.localScale.x:F2})");
        }

        /// <summary>
        /// Outside H and character creation (map, massage): stick moves relative to the head, grip + stick X turns
        /// and Y rises - same as the H free camera. It is an offset on top of the game camera (VRCamera.UserOffset).
        /// </summary>
        private void MoveWithStick()
        {
            if (VRHScene.Ativo || VRCharCreation.Ativo || MainVRCamera == null || MainVRCamera.VR == null || MainVRCamera.VR.head == null) return;
            float dt = Time.unscaledDeltaTime;
            // perceived meters: the rig scale converts to world units
            float speed = PluginConfig.MoveSpeed.Value * MainVRCamera.VR.origin.localScale.x;
            foreach (bool left in new[] { true, false })
            {
                // laser over a scrollable list: this stick is scrolling it
                if (VRControllerLaser.StickNaUI && left == (VRControllerLaser.ActiveRole == Valve.VR.ETrackedControllerRole.LeftHand)) continue;
                var s = SteamVRInput.ReadStick(left);
                if (s.magnitude < 0.15f) continue;
                if (SteamVRInput.ReadGrab(left))
                {
                    _gripMoved = true;
                    MainVRCamera.UserYaw += s.x * PluginConfig.TurnSpeed.Value * dt;
                    MainVRCamera.UserOffset += Vector3.up * (s.y * speed * dt);
                }
                else
                {
                    var yaw = Quaternion.Euler(0f, MainVRCamera.VR.head.rotation.eulerAngles.y, 0f);
                    MainVRCamera.UserOffset += yaw * new Vector3(s.x, 0f, s.y) * (speed * dt);
                }
            }
        }

        void LateUpdate()
        {
            MotionTranslator?.LateUpdate();
            float turnYaw = MotionTranslator != null ? MotionTranslator.SmoothTurnYawOffset : 0f;
            HScene?.LateUpdate();

            if (MainVRCamera != null)
            {
                MainVRCamera.UpdateOriginPose(turnYaw);
            }

            WaterGunHandler?.LateUpdate();
        }

        private void OptimizeVRExperience()
        {
            if (PluginConfig.DisableLightShadows.Value)
            {
                foreach (var light in FindObjectsOfType<Light>())
                {
                    if (light != null && light.shadows != LightShadows.None)
                    {
                        light.shadows = LightShadows.None;
                    }
                }
            }

            if (PluginConfig.DisableParticleSystems.Value)
            {
                foreach (var ps in FindObjectsOfType<ParticleSystem>())
                {
                    if (ps != null && ps.isPlaying)
                    {
                        ps.Stop();
                    }
                }
            }
        }
    }
}
