using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.EventSystems;
using Valve.VR;
using HarmonyLib;
using AmanatsuVR.Logging;
using AmanatsuVR.Config;

namespace AmanatsuVR.VRUtils
{
    /// <summary>
    /// Mira a laser e cursor virtual dos controles VR com suporte a ambas as mãos (Dual-Hand).
    /// Pressionar o gatilho em qualquer uma das mãos ativa imediatamente o laser naquela mão.
    /// Interage diretamente com o uGUI EventSystem do Unity (hover, click, drag)
    /// e redireciona os patches de Input de mouse para compatibilidade total.
    /// </summary>
    public class VRControllerLaser
    {
        public static bool IsPointingAtUI { get; private set; } = false;

        /// <summary>
        /// Verdadeiro enquanto o laser esta ativo, apontando para a UI ou atravessando ela.
        /// IsPointingAtUI nao serve para isso: com ele o mouse virtual so valia dentro do
        /// painel, e fora dele o jogo recebia a posicao parada do mouse fisico.
        /// </summary>
        public static bool IsVirtualMouseValid { get; private set; } = false;

        public static Vector3 VirtualMousePosition { get; private set; } = Vector3.zero;
        public static bool IsTriggerHeld { get; private set; } = false;
        public static bool IsTriggerDown { get; private set; } = false;
        public static bool IsTriggerUp { get; private set; } = false;
        public static ETrackedControllerRole ActiveRole { get; private set; } = ETrackedControllerRole.RightHand;

        private VRCamera MainVRCamera;
        private UIScreen TargetUIScreen;

        /// <summary>
        /// A ancora de mao ja rastreada por este laser. A pistola d'agua mantinha uma copia
        /// propria, com um fallback de pose que falhava calado e deixava a arma parada na
        /// origem do rig. Uma ancora so, que sabidamente funciona, resolve os dois.
        /// </summary>
        public Transform GetAnchor(bool isLeft)
        {
            var obj = isLeft ? LeftAnchor : RightAnchor;
            if (obj == null) obj = RightAnchor ?? LeftAnchor;
            return obj != null ? obj.transform : null;
        }

        // Âncoras independentes vinculadas ao origin do SteamVR (garantem poses reais para ambas as mãos)
        private GameObject LeftAnchor;
        private SteamVR_TrackedObject LeftTrackedObject;
        private GameObject RightAnchor;
        private SteamVR_TrackedObject RightTrackedObject;

        private LineRenderer LaserLine;
        private GameObject ReticleObject;

        private bool _prevLeftTrigger = false;
        private bool _prevRightTrigger = false;
        private bool _prevActiveTrigger = false;
        private float _lastHapticTime = 0f;

        // Rastreamento de interação com uGUI
        private GameObject _lastHoverObject = null;
        private GameObject _pressedObject = null;
        private Vector2 _lastScreenPos = Vector2.zero;

        private static Mesh _cachedReticleMesh;
        private static Mesh GetOrCreateReticleMesh()
        {
            if (_cachedReticleMesh != null) return _cachedReticleMesh;

            int segments = 24;
            float radius = 0.012f; // 1.2 cm
            var vertices = new Vector3[segments + 1];
            var uvs = new Vector2[segments + 1];
            var triangles = new int[segments * 3];

            vertices[0] = Vector3.zero;
            uvs[0] = new Vector2(0.5f, 0.5f);

            for (int i = 0; i < segments; i++)
            {
                float angle = (i / (float)segments) * Mathf.PI * 2f;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);

                vertices[i + 1] = new Vector3(cos * radius, sin * radius, 0f);
                uvs[i + 1] = new Vector2(cos * 0.5f + 0.5f, sin * 0.5f + 0.5f);

                triangles[i * 3 + 0] = 0;
                triangles[i * 3 + 1] = i + 1;
                triangles[i * 3 + 2] = (i + 1) % segments + 1;
            }

            var mesh = new Mesh
            {
                name = "Procedural_Reticle_Disc",
                vertices = vertices,
                uv = uvs,
                triangles = triangles
            };
            mesh.RecalculateNormals();
            _cachedReticleMesh = mesh;
            return _cachedReticleMesh;
        }

        public void Setup(VRCamera vrCamera, UIScreen uiScreen)
        {
            MainVRCamera = vrCamera;
            TargetUIScreen = uiScreen;
            PluginLog.Info("[AmanatsuVR] VRControllerLaser inicializado com suporte a Dual-Hand.");
        }

        private void EnsureLaserObjects()
        {
            if (MainVRCamera == null || MainVRCamera.VR == null || MainVRCamera.VR.origin == null) return;

            Transform vrOrigin = MainVRCamera.VR.origin;

            // Âncoras físicas dedicadas e contínuas para mão esquerda e direita
            if (LeftAnchor == null)
            {
                LeftAnchor = new GameObject("AmanatsuVR_Laser_Anchor_Left");
                LeftAnchor.transform.parent = vrOrigin;
                LeftTrackedObject = LeftAnchor.AddComponent<SteamVR_TrackedObject>();
            }

            if (RightAnchor == null)
            {
                RightAnchor = new GameObject("AmanatsuVR_Laser_Anchor_Right");
                RightAnchor.transform.parent = vrOrigin;
                RightTrackedObject = RightAnchor.AddComponent<SteamVR_TrackedObject>();
            }

            // Linha do Laser (parentada à âncora ativa)
            if (LaserLine == null)
            {
                var lineObj = new GameObject("AmanatsuVR_Laser_Line");
                lineObj.transform.parent = (ActiveRole == ETrackedControllerRole.LeftHand && LeftAnchor != null)
                    ? LeftAnchor.transform
                    : RightAnchor.transform;
                lineObj.transform.localPosition = Vector3.zero;
                lineObj.transform.localRotation = Quaternion.identity;
                lineObj.layer = VRController.UI_SCREEN_LAYER;

                LaserLine = lineObj.AddComponent<LineRenderer>();
                LaserLine.useWorldSpace = true;
                LaserLine.positionCount = 2;
                LaserLine.startWidth = 0.005f;
                LaserLine.endWidth = 0.002f;

                var laserMat = CustomAssetManager.CreateLaserMaterial(new Color(0.1f, 0.8f, 1.0f, 0.75f));
                LaserLine.material = laserMat;
                LaserLine.enabled = false;
            }

            // Retículo / Cursor na tela (Procedural - NUNCA cria GameObject primitivo padrão)
            if (ReticleObject == null)
            {
                ReticleObject = new GameObject("AmanatsuVR_Laser_Reticle");
                ReticleObject.SetActive(false);
                ReticleObject.transform.parent = vrOrigin;
                ReticleObject.layer = VRController.UI_SCREEN_LAYER;

                var mf = ReticleObject.AddComponent<MeshFilter>();
                mf.mesh = GetOrCreateReticleMesh();

                var mr = ReticleObject.AddComponent<MeshRenderer>();
                var reticleMat = CustomAssetManager.CreateLaserMaterial(new Color(0.1f, 1.0f, 0.55f, 0.98f));
                mr.material = reticleMat;
            }
        }

        public void Update()
        {
            if (!Plugin.IsVRModeActive)
            {
                DisableLaser();
                return;
            }

            // A arma so tem prioridade com o painel escondido. Com o painel visivel quem manda e
            // o ponteiro - senao nao havia como alcancar o botao de sair do modo pistola, que e
            // justamente o que faltava para o grip servir de troca entre os dois modos.
            if (VRWaterGunHandler.IsWaterGunSceneActive && !VRController.PainelVisivel)
            {
                DisableLaser();
                return;
            }

            EnsureLaserObjects();

            if ((LeftAnchor == null && RightAnchor == null) || (LeftTrackedObject == null && RightTrackedObject == null))
            {
                DisableLaser();
                return;
            }

            // Lê dispositivos conectados para ambas as mãos com busca completa
            uint leftDev = OpenVR.k_unTrackedDeviceIndexInvalid;
            uint rightDev = OpenVR.k_unTrackedDeviceIndexInvalid;

            if (SteamVR.active && OpenVR.System != null)
            {
                leftDev = OpenVR.System.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
                rightDev = OpenVR.System.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand);

                // Varredura para garantir que ambos os controles sejam detectados mesmo se o SteamVR não tiver atribuído papéis explícitos
                var controllers = new System.Collections.Generic.List<uint>();
                for (uint i = 1; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
                {
                    if (OpenVR.System.GetTrackedDeviceClass(i) == ETrackedDeviceClass.Controller &&
                        OpenVR.System.IsTrackedDeviceConnected(i))
                    {
                        controllers.Add(i);
                    }
                }

                if (controllers.Count >= 2)
                {
                    if (rightDev == OpenVR.k_unTrackedDeviceIndexInvalid && leftDev == OpenVR.k_unTrackedDeviceIndexInvalid)
                    {
                        rightDev = controllers[0];
                        leftDev = controllers[1];
                    }
                    else if (rightDev == OpenVR.k_unTrackedDeviceIndexInvalid)
                    {
                        rightDev = (controllers[0] != leftDev) ? controllers[0] : controllers[1];
                    }
                    else if (leftDev == OpenVR.k_unTrackedDeviceIndexInvalid)
                    {
                        leftDev = (controllers[0] != rightDev) ? controllers[0] : controllers[1];
                    }
                }
                else if (controllers.Count == 1)
                {
                    if (rightDev == OpenVR.k_unTrackedDeviceIndexInvalid && leftDev == OpenVR.k_unTrackedDeviceIndexInvalid)
                    {
                        rightDev = controllers[0];
                        leftDev = controllers[0];
                    }
                    else if (rightDev == OpenVR.k_unTrackedDeviceIndexInvalid) rightDev = controllers[0];
                    else if (leftDev == OpenVR.k_unTrackedDeviceIndexInvalid) leftDev = controllers[0];
                }
            }

            // Atualiza o índice dos tracked objects para que ambas as mãos sigam as poses físicas contínuas
            if (LeftTrackedObject != null && leftDev != OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                LeftTrackedObject.index = (SteamVR_TrackedObject.EIndex)leftDev;
            }
            if (RightTrackedObject != null && rightDev != OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                RightTrackedObject.index = (SteamVR_TrackedObject.EIndex)rightDev;
            }

            // Leitura abrangente de gatilho em ambas as mãos (XR, JoyCodes, OpenVR)
            bool leftTrigger = ReadTriggerCombined(true, leftDev);
            bool rightTrigger = ReadTriggerCombined(false, rightDev);

            LogInputDiagnostics(leftDev, rightDev, leftTrigger, rightTrigger);

            bool leftTriggerDown = leftTrigger && !_prevLeftTrigger;
            bool rightTriggerDown = rightTrigger && !_prevRightTrigger;

            _prevLeftTrigger = leftTrigger;
            _prevRightTrigger = rightTrigger;

            // Gatilho com o jogo sem foco: o clique iria para o Windows. Retoma o foco e este
            // toque serve so para isso (a injecao de clique ja exige foco).
            if ((leftTriggerDown || rightTriggerDown) && !Application.isFocused)
                EntradaFisica.FocaJanela("gatilho com o jogo sem foco");

            // Alternância de mão ativa (Dual-Hand): apertar gatilho na outra mão transfere o laser imediatamente
            if (leftTriggerDown && ActiveRole != ETrackedControllerRole.LeftHand)
            {
                ActiveRole = ETrackedControllerRole.LeftHand;
                _prevActiveTrigger = false; // Permite que o mesmo toque registre o clique na nova mão
                TriggerHaptic(leftDev, 800);
                PluginLog.Info($"[AmanatsuVR] Laser alternado para mão ESQUERDA via gatilho! (devIndex={leftDev})");
            }
            else if (rightTriggerDown && ActiveRole != ETrackedControllerRole.RightHand)
            {
                ActiveRole = ETrackedControllerRole.RightHand;
                _prevActiveTrigger = false;
                TriggerHaptic(rightDev, 800);
                PluginLog.Info($"[AmanatsuVR] Laser alternado para mão DIREITA via gatilho! (devIndex={rightDev})");
            }

            // Fallback se a mão ativa não estiver conectada
            if (ActiveRole == ETrackedControllerRole.RightHand && rightDev == OpenVR.k_unTrackedDeviceIndexInvalid && leftDev != OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                ActiveRole = ETrackedControllerRole.LeftHand;
            }
            else if (ActiveRole == ETrackedControllerRole.LeftHand && leftDev == OpenVR.k_unTrackedDeviceIndexInvalid && rightDev != OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                ActiveRole = ETrackedControllerRole.RightHand;
            }

            uint devIndex = (ActiveRole == ETrackedControllerRole.LeftHand) ? leftDev : rightDev;
            bool triggerPressed = (ActiveRole == ETrackedControllerRole.LeftHand) ? leftTrigger : rightTrigger;

            IsTriggerDown = triggerPressed && !_prevActiveTrigger;
            IsTriggerUp = !triggerPressed && _prevActiveTrigger;
            if (IsTriggerDown) _cliqueConsumido = false;
            IsTriggerHeld = triggerPressed;
            _prevActiveTrigger = triggerPressed;

            // Define a âncora ativa para o laser
            GameObject activeAnchor = (ActiveRole == ETrackedControllerRole.LeftHand) ? LeftAnchor : RightAnchor;
            if (activeAnchor == null) activeAnchor = RightAnchor ?? LeftAnchor;

            if (activeAnchor == null)
            {
                DisableLaser();
                return;
            }

            // Se o SteamVR não estiver atualizando a pose das âncoras, sincroniza via UnityEngine.XR
            if (LeftTrackedObject == null || LeftTrackedObject.index == SteamVR_TrackedObject.EIndex.None || leftDev == OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                UpdateAnchorPoseFromXR(LeftAnchor, UnityEngine.XR.XRNode.LeftHand);
            }
            if (RightTrackedObject == null || RightTrackedObject.index == SteamVR_TrackedObject.EIndex.None || rightDev == OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                UpdateAnchorPoseFromXR(RightAnchor, UnityEngine.XR.XRNode.RightHand);
            }

            if (LaserLine != null && activeAnchor != null && LaserLine.transform.parent != activeAnchor.transform)
            {
                LaserLine.transform.parent = activeAnchor.transform;
                LaserLine.transform.localPosition = Vector3.zero;
                LaserLine.transform.localRotation = Quaternion.identity;
            }

            // Na H com o painel escondido os controles sao os atalhos do VRHScene: nada de
            // cursor nem clique fisico, que o jogo leria como toque na cena. So a mira do tiro
            // livre aparece, com a linha e o retículo onde o VRHScene diz que ela bate.
            if (VRHScene.Ativo && !VRController.PainelVisivel)
            {
                DisableLaser();
                if (VRHScene.MiraOrigem.HasValue) DesenhaMira(VRHScene.MiraOrigem.Value, VRHScene.MiraFim);
                return;
            }

            // Origem e direção do raio do laser a partir da mão do jogador
            Vector3 rayOrigin = activeAnchor != null ? activeAnchor.transform.position : Vector3.zero;
            Vector3 rayDir = activeAnchor != null ? activeAnchor.transform.forward : Vector3.forward;
            rayDir = LaserStabilization(rayDir, activeAnchor);
            StickNaUI = false;   // ProcessUGUIEvents liga de novo se o laser estiver sobre algo rolavel
            PointerOverInteractiveUI = false;

            // Localiza o painel da UI virtual (UIScreen)
            GameObject targetPanel = TargetUIScreen != null ? TargetUIScreen.MainPanelObject : null;
            bool hitUI = false;
            bool sobreElementoClicavel = false;

            if (targetPanel != null && targetPanel.activeInHierarchy)
            {
                Transform panelTrans = targetPanel.transform;
                Vector3 localOrigin = panelTrans.InverseTransformPoint(rayOrigin);
                Vector3 localDir = panelTrans.InverseTransformDirection(rayDir);

                // Interseção com o plano local Z = 0
                if (Mathf.Abs(localDir.z) > 0.0001f)
                {
                    float t = -localOrigin.z / localDir.z;
                    if (t > 0f)
                    {
                        Vector3 localHit = localOrigin + localDir * t;
                        if (Mathf.Abs(localHit.x) <= 0.5f && Mathf.Abs(localHit.y) <= 0.5f)
                        {
                            hitUI = true;

                            // Coordenadas virtuais da tela em pixels [0..Screen.width, 0..Screen.height]
                            float screenX = (localHit.x + 0.5f) * UnityEngine.Screen.width;
                            float screenY = (localHit.y + 0.5f) * UnityEngine.Screen.height;
                            VirtualMousePosition = new Vector3(screenX, screenY, 0f);
                            Vector2 currentScreenPos = new Vector2(screenX, screenY);

                            // Ponto de impacto em coordenadas mundiais
                            Vector3 worldHit = panelTrans.TransformPoint(localHit);

                            // Vetor em direção à cabeça do jogador para garantir que o cursor
                            // fique exatamente 2cm à frente do painel da UI para ambos os olhos
                            Vector3 dirToHead = (MainVRCamera != null && MainVRCamera.VR != null && MainVRCamera.VR.head != null)
                                ? (MainVRCamera.VR.head.position - worldHit).normalized
                                : -panelTrans.forward;
                            Vector3 reticleWorldPos = worldHit + dirToHead * 0.02f;

                            if (LaserLine != null)
                            {
                                LaserLine.enabled = true;
                                LaserLine.SetPosition(0, rayOrigin);
                                LaserLine.SetPosition(1, reticleWorldPos);
                            }

                            if (ReticleObject != null)
                            {
                                ReticleObject.SetActive(true);
                                ReticleObject.transform.position = reticleWorldPos;
                                ReticleObject.transform.rotation = Quaternion.LookRotation(-dirToHead);
                            }

                            // Processamento DIRETO de eventos de uGUI via EventSystem
                            sobreElementoClicavel = ProcessUGUIEvents(currentScreenPos, devIndex);
                        }
                    }
                }
            }

            IsPointingAtUI = hitUI;

            if (!hitUI)
            {
                // Se saiu da tela, limpa o estado de hover anterior
                ClearHoverState();
            }

            // Passagem atraves da UI 2D: se nao ha elemento clicavel sob o ponteiro, o mouse
            // virtual deixa de ser o furo no painel e passa a ser a direcao fisica do controle,
            // projetada na camera do jogo. Assim o raycast que o proprio jogo faz
            // (camera.ScreenPointToRay(Input.mousePosition)) acerta a garota para onde a mao
            // aponta - tanto o clique no mapa quanto o modo de massagem, que usam o mesmo
            // caminho. Sem isto o painel cobre so uma fatia do campo de visao e a coordenada
            // entregue ao jogo nao tem relacao com o que a mao mira.
            //
            // Menos na criacao de personagem: ali a tela inteira e UI e a area do modelo nao
            // tem elemento clicavel, entao era justamente ela que caia neste caminho. O raycast
            // nao acerta nada, o alvo vira um ponto fixo a 10 m e o cursor sai passeando com a
            // direcao da mao - com o botao pressionado isso e um arrasto, e e por isso que o
            // painel parecia se deslocar sozinho e que girar o modelo nunca funcionou.
            bool miraNoMundo = !sobreElementoClicavel && !VRCharCreation.Ativo;

            IsVirtualMouseValid = true;
            if (miraNoMundo) MiraAtravesDaUI(rayOrigin, rayDir);
            else if (VRCharCreation.Ativo && !hitUI) IsVirtualMouseValid = false;

            if (!hitUI)
            {
                if (LaserLine != null)
                {
                    LaserLine.enabled = true;
                    LaserLine.SetPosition(0, rayOrigin);
                    LaserLine.SetPosition(1, rayOrigin + rayDir * _alcanceLaser);
                }
                if (ReticleObject != null)
                {
                    ReticleObject.SetActive(false);
                }
            }

            if (IsVirtualMouseValid) EspelhaCursorFisico();
            InjetaCliqueFisico();
        }

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, System.IntPtr dwExtraInfo);

        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        private POINT _ultimoPontoFisico;
        private bool _temPontoFisico = false;

        private bool _cliqueConsumido = false;
        private bool _botaoFisicoPressionado = false;

        /// <summary>
        /// O mesmo motivo do EspelhaCursorFisico, um passo adiante: o ADV nao avanca pelo uGUI.
        /// MsgWindow02 recebe pointerDown e pointerClick nossos e nada acontece, porque a janela
        /// de mensagem le o botao do mouse direto - e o il2cpp inlinou esse getter igual ao
        /// mousePosition. Cursor no lugar certo sem clique de verdade nao avanca conversa.
        ///
        /// So injetamos quando nenhum Button/Toggle ja consumiu o clique; senao o botao
        /// dispararia duas vezes, uma pelo onClick.Invoke e outra pelo clique fisico.
        /// </summary>
        private void InjetaCliqueFisico()
        {
            try
            {
                if (IsTriggerDown && !_botaoFisicoPressionado && !_cliqueConsumido
                    && IsVirtualMouseValid && Application.isFocused)
                {
                    _botaoFisicoPressionado = true;
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, System.IntPtr.Zero);
                }
                else if (_botaoFisicoPressionado && IsTriggerUp)
                {
                    _botaoFisicoPressionado = false;
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, System.IntPtr.Zero);
                }
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        private static extern System.IntPtr GetActiveWindow();

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(System.IntPtr hWnd, ref POINT p);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        /// <summary>
        /// Poe o cursor do Windows onde o laser esta mirando.
        ///
        /// O patch Harmony em Input.mousePosition nao chega no jogo: o il2cpp faz inline de
        /// getters triviais na compilacao AOT, entao o call site do proprio jogo nunca passa
        /// pelo patch e continua lendo o mouse fisico parado. Medido: com enable=True,
        /// updateProc subindo e o nosso raio partindo da MESMA camera acertando o
        /// OutlineCollider, CurrentCol ficava nulo em todas as amostras.
        ///
        /// Mexer no cursor de verdade dispensa o patch. Ele fica valendo para quem nao foi
        /// inlinado, e os dois passam a concordar.
        /// </summary>
        private void EspelhaCursorFisico()
        {
            Vector3 vm = VirtualMousePosition;

            // Fora da janela o SetCursorPos grudaria o cursor na borda, o que daria ao jogo uma
            // coordenada errada em vez de nenhuma. Melhor deixar onde estava.
            if (vm.x < 0f || vm.y < 0f || vm.x > Screen.width || vm.y > Screen.height) return;

            // Sem foco o cursor andaria pelo Windows (e o que estiver embaixo dele recebe o clique
            // do usuario). Com foco a janela certa e a do jogo, nao a ativa: sem foco a ativa e
            // zero, e a coordenada de cliente virava coordenada de tela.
            if (!Application.isFocused) return;

            try
            {
                // Unity conta a partir de baixo, o Windows a partir de cima.
                var p = new POINT { X = (int)vm.x, Y = (int)(Screen.height - vm.y) };
                System.IntPtr hwnd = EntradaFisica.WindowHandle;
                if (hwnd == System.IntPtr.Zero) hwnd = GetActiveWindow();
                if (hwnd == System.IntPtr.Zero || !ClientToScreen(hwnd, ref p)) return;

                // Posicao nao basta para o modo mao da massagem. AL.Oil.MouseTouch tem
                // GetMouseMove/_mouseSpeedX/_mouseVector: esfregar e movimento, nao clique - por
                // isso clicar no corpo nao fazia nada enquanto o modo gota funcionava. E o delta
                // do Unity vem do raw input, que o SetCursorPos nao alimenta; o mouse_event sim.
                // Emitimos o movimento relativo primeiro e so entao encaixamos a posicao exata,
                // para que a aceleracao de ponteiro do Windows nao desvie a mira.
                if (_temPontoFisico)
                {
                    int dx = p.X - _ultimoPontoFisico.X;
                    int dy = p.Y - _ultimoPontoFisico.Y;
                    if (dx != 0 || dy != 0) mouse_event(MOUSEEVENTF_MOVE, dx, dy, 0, System.IntPtr.Zero);
                }
                _ultimoPontoFisico = p;
                _temPontoFisico = true;

                SetCursorPos(p.X, p.Y);
            }
            catch { }
        }

        private float _alcanceLaser = 1.5f;
        private float _ultimoLogMira = -10f;
        private AL.OutlinableManager _selecao;
        private float _ultimaBuscaSelecao = -10f;

        /// <summary>
        /// O gerenciador de selecao do jogo. Ele e quem dispara
        /// MainCamera.ScreenPointToRay(Input.mousePosition) contra os OutlineCollider das
        /// garotas, entao a camera, a mascara e a distancia dele sao os unicos valores
        /// corretos para a nossa projecao. Adivinhar qualquer um dos tres faz o ponteiro errar.
        /// </summary>
        private AL.OutlinableManager Selecao
        {
            get
            {
                if (_selecao == null && Time.unscaledTime - _ultimaBuscaSelecao > 0.5f)
                {
                    _ultimaBuscaSelecao = Time.unscaledTime;
                    try { _selecao = UnityEngine.Object.FindObjectOfType<AL.OutlinableManager>(); }
                    catch { }
                }
                return _selecao;
            }
        }

        private AL.Oil.MouseTouch _massagem;
        private float _ultimaBuscaMassagem = -10f;

        /// <summary>A camera do oleo enquanto a massagem esta de fato rodando; nula fora dela.</summary>
        public static Camera CameraDaMassagem { get; private set; }

        /// <summary>
        /// A camera que ERA do oleo depois que a massagem acabou. Ela continua viva e marcada
        /// como MainCamera, entao o FindTargetCamera a escolhia de novo e o jogador ficava preso
        /// no angulo da massagem. Fica na lista negra ate o proximo oleo comecar.
        /// </summary>
        public static Camera CameraDaMassagemMorta { get; private set; }

        /// <summary>
        /// Quem dispara o raio no modo massagem. Tem camera e mascara proprias, diferentes das
        /// do mapa; e a mesma licao do OutlinableManager, noutra cena.
        /// </summary>
        private AL.Oil.MouseTouch Massagem
        {
            get
            {
                if (Time.unscaledTime - _ultimaBuscaMassagem > 0.5f)
                {
                    _ultimaBuscaMassagem = Time.unscaledTime;
                    try { _massagem = UnityEngine.Object.FindObjectOfType<AL.Oil.MouseTouch>(); }
                    catch { _massagem = null; }

                    // O objeto do oleo sobrevive ao fim da massagem, e com ele a camera dele.
                    // Medido: o vinculo ficou em 'MainCamera' (a do oleo) e nunca voltou para
                    // 'Main Camera' (a do mapa) - dai a camera travada no angulo da massagem.
                    //
                    // A tentativa anterior condicionava o uso a isActiveAndEnabled && IsInit e
                    // isso derrubou o oleo DURANTE a massagem, ou seja algum desses campos e
                    // falso mesmo com a cena rodando. Enquanto nao sei qual, o criterio volta a
                    // ser so 'existe': some o MouseTouch, a camera dele vai para a lista negra.
                    if (_massagem != null)
                    {
                        CameraDaMassagem = _massagem.Camera;
                        CameraDaMassagemMorta = null;
                    }
                    else if (CameraDaMassagem != null)
                    {
                        CameraDaMassagemMorta = CameraDaMassagem;
                        CameraDaMassagem = null;
                        PluginLog.Info($"[AmanatsuVR][OLEO] massagem encerrada; camera '{CameraDaMassagemMorta.name}' na lista negra.");
                    }
                }
                return _massagem;
            }
        }

        /// <summary>
        /// Converte a direcao fisica do controle na coordenada de tela que a camera do jogo
        /// entende. Mira no ponto que o raio realmente encosta: projetar o ponto de impacto
        /// elimina a paralaxe entre a mao e o olho, que e o que fazia o ponteiro errar a
        /// garota. WorldToScreenPoint e a inversa exata de ScreenPointToRay na MESMA camera,
        /// entao o acerto e exato desde que o jogo use a camera que nos sequestramos.
        /// </summary>
        private void MiraAtravesDaUI(Vector3 rayOrigin, Vector3 rayDir)
        {
            var sel = Selecao;
            var oleo = Massagem;

            // A camera do jogo, nao a nossa. Se o jogo dispara o raio de uma camera e nos
            // projetamos por outra, WorldToScreenPoint deixa de ser a inversa de
            // ScreenPointToRay e o ponteiro erra por construcao. No modo massagem quem atira o
            // raio e AL.Oil.MouseTouch, com camera e mascara proprias - usar as do mapa ali era
            // o mesmo erro de antes, e e por isso que a contabilizacao caia na parte errada do
            // corpo.
            Camera cam = (oleo != null && oleo.Camera != null) ? oleo.Camera
                : ((sel != null && sel.MainCamera != null) ? sel.MainCamera
                : ((MainVRCamera != null && MainVRCamera.TargetCamera != null)
                    ? MainVRCamera.TargetCamera
                    : (MainVRCamera != null ? MainVRCamera.Normal : null)));

            if (cam == null)
            {
                IsVirtualMouseValid = false;
                return;
            }

            // A mascara e a distancia do proprio jogo. Com a nossa mascara generica o raio da
            // mao parava no cenario antes da garota, ou nao encostava em nada (medido: 'sem
            // colisor' em quase todas as amostras de [MIRA]) e caia no ponto fixo de 10 m,
            // onde a paralaxe mao/olho joga a coordenada para longe dela.
            int mascara = (oleo != null) ? oleo._layerMaskHit.value
                : ((sel != null) ? sel._raycastLayerMask.value
                : ~((1 << VRController.UI_SCREEN_LAYER) | (1 << 5)));
            float alcance = (oleo != null) ? 10f
                : ((sel != null && sel._maxDistance > 0f) ? sel._maxDistance : 50f);

            Vector3 alvo;
            // Triggers entram: os OutlineCollider de selecao sao volumes invisiveis. Eles sao
            // justamente o que queremos acertar, e a mascara do jogo ja exclui o resto.
            bool acertou = Physics.Raycast(rayOrigin, rayDir, out RaycastHit hit, alcance, mascara,
                QueryTriggerInteraction.Collide);

            if (acertou)
            {
                alvo = hit.point;
                _alcanceLaser = hit.distance;
            }
            else
            {
                alvo = rayOrigin + rayDir * 10f;
                _alcanceLaser = 1.5f;
            }

            Vector3 sp = cam.WorldToScreenPoint(alvo);
            if (sp.z <= 0f)
            {
                // Alvo atras da camera: nao ha coordenada de tela valida.
                IsVirtualMouseValid = false;
                return;
            }

            VirtualMousePosition = new Vector3(sp.x, sp.y, 0f);

            if (Time.unscaledTime - _ultimoLogMira > 5f)
            {
                _ultimoLogMira = Time.unscaledTime;
                PluginLog.Info($"[AmanatsuVR][MIRA] camera='{cam.name}' alvo='{(acertou ? hit.collider.name : "sem colisor")}'"
                    + $" dist={_alcanceLaser:F2} tela={VirtualMousePosition}");
                LogSelecao(sel, cam, mascara, alcance);
            }
        }

        /// <summary>
        /// Refaz o raycast do jogo com a coordenada que acabamos de entregar. Se esta linha nao
        /// nomear um OutlineCollider, a coordenada esta errada; se nomear e mesmo assim o clique
        /// nao seleciona, o problema esta na entrega do clique e nao na mira.
        /// </summary>
        private void LogSelecao(AL.OutlinableManager sel, Camera cam, int mascara, float alcance)
        {
            if (sel == null)
            {
                PluginLog.Info("[AmanatsuVR][ALVO] OutlinableManager ausente nesta cena");
                return;
            }

            try
            {
                string atual = (sel.CurrentCol != null) ? sel.CurrentCol.name : "nenhum";
                string batida = "nada";

                Ray raio = cam.ScreenPointToRay(VirtualMousePosition);
                if (Physics.Raycast(raio, out RaycastHit h, alcance, mascara, QueryTriggerInteraction.Collide))
                {
                    var oc = h.collider.GetComponentInParent<AL.OutlineCollider>();
                    batida = $"{h.collider.name} outline={(oc != null ? "SIM" : "nao")} d={h.distance:F1}";
                }

                // Input.mousePosition passa pelo nosso patch. Se aqui sair diferente de
                // VirtualMousePosition, o patch nao esta valendo e o jogo le outra coisa.
                Vector3 lido = Input.mousePosition;

                PluginLog.Info($"[AmanatsuVR][ALVO] enable={AL.OutlinableManager.Enable} atual='{atual}' mascara=0x{mascara:X}"
                    + $" alcance={alcance:F1} raioDaCamera->{batida}"
                    + $" | updateProc={VR_OutlinableManager_UpdateProc_Patch.Chamadas} mouseLido={lido}"
                    + $" cursorWin={(GetCursorPos(out POINT cp) ? $"{cp.X},{cp.Y}" : "?")}"
                    + $" tela={Screen.width}x{Screen.height} pixel={cam.pixelWidth}x{cam.pixelHeight}"
                    + $" sobreUI={(EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())}");

                // Com o cursor travado o SetCursorPos nao vale de nada: o Windows devolve o
                // cursor para o centro e o jogo passa a ler delta, nao posicao. Se o ADV nao
                // avanca so no modo massagem, e o primeiro lugar para olhar.
                var oleo = Massagem;
                if (oleo != null)
                {
                    PluginLog.Info($"[AmanatsuVR][OLEO] camera='{(oleo.Camera != null ? oleo.Camera.name : "-")}'"
                        + $" mascaraHit=0x{oleo._layerMaskHit.value:X} mascaraChara=0x{oleo._layerMaskChara.value:X}"
                        + $" modo={oleo.NowMode} cameraMode={oleo.CameraMode} semCtrl={oleo.NoCtrlCondition}"
                        + $" ativo={oleo.isActiveAndEnabled} init={oleo.IsInit}"
                        + $" camAtiva={(oleo.Camera != null ? oleo.Camera.isActiveAndEnabled.ToString() : "-")}"
                        + $" travaCursor={(oleo.CameraController != null ? oleo.CameraController.IsCursorLock.ToString() : "?")}"
                        + $" lockState={Cursor.lockState} visivel={Cursor.visible}");
                }
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning($"[AmanatsuVR][ALVO] falhou: {ex.Message}");
            }
        }

        /// <summary>
        /// Devolve true se havia um elemento de uGUI clicavel sob o ponteiro. So nesse caso a
        /// coordenada do painel deve prevalecer sobre a mira no mundo.
        /// </summary>
        private bool ProcessUGUIEvents(Vector2 screenPos, uint devIndex)
        {
            if (EventSystem.current == null) return false;

            var pointerData = new PointerEventData(EventSystem.current)
            {
                position = screenPos,
                delta = screenPos - _lastScreenPos,
                eligibleForClick = true,
                button = PointerEventData.InputButton.Left
            };
            _lastScreenPos = screenPos;

            var raycastResults = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            EventSystem.current.RaycastAll(pointerData, raycastResults);

            GameObject currentHover = null;
            RaycastResult firstRaycast = default;

            // Prioriza elementos interativos (botões, toggles, campos de texto, manipuladores de clique)
            // para evitar que imagens decorativas de 'Background' bloqueiem o clique
            GameObject clickableTarget = null;
            RaycastResult clickableRaycast = default;

            for (int i = 0; i < raycastResults.Count; i++)
            {
                var r = raycastResults[i];
                if (r.gameObject == null) continue;

                if (currentHover == null)
                {
                    firstRaycast = r;
                    currentHover = r.gameObject;
                }

                var b = r.gameObject.GetComponentInParent<UnityEngine.UI.Button>();
                var t = r.gameObject.GetComponentInParent<UnityEngine.UI.Toggle>();
                var inp = r.gameObject.GetComponentInParent<UnityEngine.UI.InputField>();
                var h = ExecuteEvents.GetEventHandler<IPointerClickHandler>(r.gameObject);

                if ((b != null && b.interactable) || (t != null && t.interactable) || inp != null || h != null)
                {
                    clickableTarget = r.gameObject;
                    clickableRaycast = r;
                    break;
                }
            }

            // Se encontrou um elemento clicável sob o laser, utiliza-o preferencialmente
            GameObject activeTarget = clickableTarget ?? currentHover;
            RaycastResult activeRaycast = clickableTarget != null ? clickableRaycast : firstRaycast;

            // 1. Atualiza Hover (PointerEnter / PointerExit)
            if (activeTarget != _lastHoverObject)
            {
                if (_lastHoverObject != null)
                {
                    try { ExecuteEvents.ExecuteHierarchy(_lastHoverObject, pointerData, ExecuteEvents.pointerExitHandler); } catch { }
                }
                if (activeTarget != null)
                {
                    try { ExecuteEvents.ExecuteHierarchy(activeTarget, pointerData, ExecuteEvents.pointerEnterHandler); } catch { }
                    TriggerHaptic(devIndex, 350);
                }
                _lastHoverObject = activeTarget;
            }

            // 1b. Analogico cima/baixo rola o que estiver sob o laser, subindo pelos pais (o botao de uma
            // lista acha o ScrollRect dela). Mesmo evento que a roda do mouse gera no input module: um degrau
            // (scrollDelta 1) por vez, entao vale a sensibilidade que o jogo configurou em cada lista.
            var rolavel = activeTarget != null ? ExecuteEvents.GetEventHandler<IScrollHandler>(activeTarget) : null;
            StickNaUI = rolavel != null;
            if (rolavel != null)
            {
                float y = SteamVRInput.ReadStick(ActiveRole == ETrackedControllerRole.LeftHand).y;
                if (Mathf.Abs(y) < 0.3f) _degrausRolagem = 1f;   // ao inclinar, o primeiro degrau sai na hora
                else
                {
                    // unscaled: dialogos pausam o jogo (timeScale 0) e ainda assim a lista tem que rolar
                    _degrausRolagem += Time.unscaledDeltaTime * DegrausPorSegundo * Mathf.Abs(y);
                    for (; _degrausRolagem >= 1f; _degrausRolagem -= 1f)
                    {
                        pointerData.scrollDelta = new Vector2(0f, Mathf.Sign(y));
                        try { ExecuteEvents.ExecuteHierarchy(rolavel, pointerData, ExecuteEvents.scrollHandler); } catch { }
                    }
                }
            }

            // 2. Clique Pressionado (PointerDown + disparo imediato no Button/Toggle/Submit)
            if (IsTriggerDown)
            {
                _pressedObject = activeTarget;
                pointerData.pressPosition = screenPos;

                PluginLog.Info($"[AmanatsuVR] Trigger DOWN acionado! Alvo: '{activeTarget?.name}', pos: {screenPos}");

                if (activeTarget != null)
                {
                    // So atribuir com alvo real. Um RaycastResult 'default' tem o GameObject
                    // nulo e o setter do Il2Cpp joga NullReferenceException; como isto roda
                    // dentro do trampolim, a excecao abortava o Update inteiro do VRController
                    // a cada frame em que o gatilho caia fora da UI - o travamento.
                    pointerData.pointerPressRaycast = activeRaycast;
                    pointerData.pointerCurrentRaycast = activeRaycast;

                    try
                    {
                        ExecuteEvents.ExecuteHierarchy(activeTarget, pointerData, ExecuteEvents.pointerDownHandler);
                    }
                    catch { }

                    // ACIONAMENTO DIRETO NO TRIGGER DOWN
                    try
                    {
                        var btn = activeTarget.GetComponentInParent<UnityEngine.UI.Button>();
                        if (btn != null && btn.interactable)
                        {
                            PluginLog.Info($"[AmanatsuVR] Botão acionado com sucesso (onClick.Invoke): '{btn.name}'");
                            btn.onClick.Invoke();
                            _cliqueConsumido = true;
                            TriggerHaptic(devIndex, 1000);
                        }
                        else
                        {
                            var toggle = activeTarget.GetComponentInParent<UnityEngine.UI.Toggle>();
                            if (toggle != null && toggle.interactable)
                            {
                                PluginLog.Info($"[AmanatsuVR] Toggle alternado: '{toggle.name}' -> {!toggle.isOn}");
                                toggle.isOn = !toggle.isOn;
                                _cliqueConsumido = true;
                                TriggerHaptic(devIndex, 1000);
                            }
                            else
                            {
                                ExecuteEvents.ExecuteHierarchy(activeTarget, pointerData, ExecuteEvents.submitHandler);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Warning($"[AmanatsuVR] Erro ao processar clique no Down: {ex.Message}");
                    }
                }
                TriggerHaptic(devIndex, 600);
            }

            // 3. Clique Solto (PointerUp + PointerClick + Submit)
            if (IsTriggerUp)
            {
                var target = _pressedObject ?? activeTarget;
                if (target != null)
                {
                    if (activeTarget != null)
                    {
                        pointerData.pointerPressRaycast = activeRaycast;
                        pointerData.pointerCurrentRaycast = activeRaycast;
                    }
                    try
                    {
                        ExecuteEvents.ExecuteHierarchy(target, pointerData, ExecuteEvents.pointerUpHandler);
                    }
                    catch { }

                    // Nao repetir o clique que o Down ja executou. Button implementa
                    // IPointerClickHandler e o submit tambem chama onClick, entao o caminho
                    // antigo (Invoke no Down + pointerClick + submit no Up) disparava o handler
                    // TRES vezes por clique. Num botao que abre ou fecha dialogo isso empilhava
                    // pausa e despausa fora de ordem: medido escala=0,00 desde o primeiro
                    // ConfirmDialog e nunca mais de volta a 1 - o jogo parado no preto, com a
                    // UI ainda respondendo.
                    if (!_cliqueConsumido)
                    {
                        try
                        {
                            ExecuteEvents.ExecuteHierarchy(target, pointerData, ExecuteEvents.pointerClickHandler);
                        }
                        catch { }
                    }
                }
                _pressedObject = null;
            }

            PointerOverInteractiveUI = clickableTarget != null || rolavel != null
                || (currentHover != null && ExecuteEvents.GetEventHandler<IDragHandler>(currentHover) != null);
            if (IsTriggerDown && VRCharCreation.Ativo)
                PluginLog.Info($"[AmanatsuVR][CRIACAO] clique em '{currentHover?.name}': " +
                               (PointerOverInteractiveUI ? "UI, camera bloqueada" : "area livre, camera liberada"));
            return clickableTarget != null;
        }

        /// <summary>
        /// Laser sobre uma lista rolavel: o analogico da mao do laser e da UI. O VRHScene ignora esse analogico
        /// (senao rolar a lista andava com a camera ou iniciava o ato em primeira pessoa).
        /// </summary>
        public static bool StickNaUI { get; private set; }

        /// <summary>
        /// Laser over something the player can use (button, field, slider, list, window with a click handler).
        /// Character creation locks its camera when EventSystem.IsPointerOverGameObject() is true at mouse down;
        /// in VR the whole panel hits some graphic, so this is the answer we give instead.
        /// </summary>
        public static bool PointerOverInteractiveUI { get; private set; }
        private const float DegrausPorSegundo = 10f;   // com o analogico no fim do curso
        private float _degrausRolagem = 1f;

        // Estabilizador: filtro exponencial com constante adaptativa. Devagar (tremor, ajuste fino em cima de um
        // botao) a direcao segue com peso; rapido (trocar de alvo) segue quase na hora. Como o filtro sempre
        // converge para a mao, nao ha deriva nem recentralizacao a fazer. No aperto do gatilho a mao gira um
        // pouco junto com o dedo: a direcao fica parada por um instante para o clique cair onde se mirou.
        private Vector3 _dirSuave;
        private GameObject _ancoraSuave;
        private int _quadroSuave;
        private float _travaClique;

        private Vector3 LaserStabilization(Vector3 bruta, GameObject ancora)
        {
            float peso = PluginConfig.LaserStabilization.Value;
            bool recomeca = peso <= 0f || ancora != _ancoraSuave || Time.frameCount - _quadroSuave > 1;
            _ancoraSuave = ancora;
            _quadroSuave = Time.frameCount;
            if (recomeca) { _travaClique = 0f; return _dirSuave = bruta; }

            float dt = Time.unscaledDeltaTime;
            if (IsTriggerDown) _travaClique = 0.12f * peso;
            if (_travaClique > 0f) { _travaClique -= dt; return _dirSuave; }

            float grausPorSegundo = Vector3.Angle(_dirSuave, bruta) / Mathf.Max(dt, 1e-4f);
            float k = Mathf.Lerp(6f / peso, 40f, Mathf.InverseLerp(10f, 90f, grausPorSegundo));
            return _dirSuave = Vector3.Slerp(_dirSuave, bruta, 1f - Mathf.Exp(-k * dt));
        }

        private void ClearHoverState()
        {
            if (_lastHoverObject != null && EventSystem.current != null)
            {
                var pointerData = new PointerEventData(EventSystem.current)
                {
                    position = _lastScreenPos
                };
                try { ExecuteEvents.ExecuteHierarchy(_lastHoverObject, pointerData, ExecuteEvents.pointerExitHandler); } catch { }
                _lastHoverObject = null;
            }

            if (_pressedObject != null && IsTriggerUp && EventSystem.current != null)
            {
                var pointerData = new PointerEventData(EventSystem.current)
                {
                    position = _lastScreenPos
                };
                try { ExecuteEvents.ExecuteHierarchy(_pressedObject, pointerData, ExecuteEvents.pointerUpHandler); } catch { }
                _pressedObject = null;
            }
        }

        private float _lastInputLogTime = -10f;

        /// <summary>
        /// Nenhum 'Trigger DOWN' apareceu no log da sessão anterior: os três caminhos de leitura
        /// falham silenciosamente (todos engolem exceções). Isto diz qual deles está morto.
        /// </summary>
        private void LogInputDiagnostics(uint leftDev, uint rightDev, bool leftTrigger, bool rightTrigger)
        {
            if (Time.unscaledTime - _lastInputLogTime < 2f) return;
            _lastInputLogTime = Time.unscaledTime;

            PluginLog.Info(
                // timeScale e frame juntos separam 'jogo pausado' de 'jogo travado'. A tela preta
                // que nao saia deixou TODO o nosso log mudo de uma vez - porque todo limitador
                // usava Time.time, que nao anda com timeScale=0. Agora o log sobrevive a pausa e
                // diz qual dos dois e.
                $"[AmanatsuVR][INPUT] escala={Time.timeScale:F2} frame={Time.frameCount}"
                + $" dev L={leftDev} R={rightDev} | combinado L={leftTrigger} R={rightTrigger}"
                + $" | acao L={SteamVRInput.ReadTrigger(true)} R={SteamVRInput.ReadTrigger(false)}"
                + $" | xr L={ReadXRDeviceTrigger(true)} R={ReadXRDeviceTrigger(false)}"
                + $" | joy L={ReadLegacyJoystickTrigger(true)} R={ReadLegacyJoystickTrigger(false)}"
                + $" | openvr L={ReadOpenVRTrigger(leftDev)} R={ReadOpenVRTrigger(rightDev)}"
                + $" | apontandoUI={IsPointingAtUI} foco={Application.isFocused}"
                + $" | {SteamVRInput.Describe(true)} | {SteamVRInput.Describe(false)}");
        }

        private static bool ReadXRDeviceTrigger(bool isLeft)
        {
            try
            {
                var node = isLeft ? UnityEngine.XR.XRNode.LeftHand : UnityEngine.XR.XRNode.RightHand;
                var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
                if (dev.isValid)
                {
                    ulong id = dev.deviceId;

                    // 1. Gatilho analógico (Trigger axis)
                    if (UnityEngine.XR.InputDevices.TryGetFeatureValue_float(id, "Trigger", out float t1) && t1 > 0.15f) return true;
                    if (UnityEngine.XR.InputDevices.TryGetFeatureValue_float(id, "trigger", out float t2) && t2 > 0.15f) return true;

                    // 2. Botão do Gatilho (Trigger button / click)
                    if (UnityEngine.XR.InputDevices.TryGetFeatureValue_bool(id, "TriggerButton", out bool tb1) && tb1) return true;
                    if (UnityEngine.XR.InputDevices.TryGetFeatureValue_bool(id, "triggerButton", out bool tb2) && tb2) return true;

                    // 3. Botão Principal (A no direito, X no esquerdo)
                    if (UnityEngine.XR.InputDevices.TryGetFeatureValue_bool(id, "PrimaryButton", out bool pb1) && pb1) return true;
                    if (UnityEngine.XR.InputDevices.TryGetFeatureValue_bool(id, "primaryButton", out bool pb2) && pb2) return true;

                    // 4. Botão Secundário (B no direito, Y no esquerdo)
                    if (UnityEngine.XR.InputDevices.TryGetFeatureValue_bool(id, "SecondaryButton", out bool sb1) && sb1) return true;
                    if (UnityEngine.XR.InputDevices.TryGetFeatureValue_bool(id, "secondaryButton", out bool sb2) && sb2) return true;

                    // 5. Analógico / Botão do Grip
                    if (UnityEngine.XR.InputDevices.TryGetFeatureValue_float(id, "Grip", out float g1) && g1 > 0.20f) return true;
                    if (UnityEngine.XR.InputDevices.TryGetFeatureValue_float(id, "grip", out float g2) && g2 > 0.20f) return true;
                    if (UnityEngine.XR.InputDevices.TryGetFeatureValue_bool(id, "GripButton", out bool gb1) && gb1) return true;
                    if (UnityEngine.XR.InputDevices.TryGetFeatureValue_bool(id, "gripButton", out bool gb2) && gb2) return true;

                    // 6. Clique do Thumbstick / Trackpad
                    if (UnityEngine.XR.InputDevices.TryGetFeatureValue_bool(id, "Primary2DAxisClick", out bool ac1) && ac1) return true;
                    if (UnityEngine.XR.InputDevices.TryGetFeatureValue_bool(id, "primary2DAxisClick", out bool ac2) && ac2) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool ReadLegacyJoystickTrigger(bool isLeft)
        {
            try
            {
                if (isLeft)
                {
                    if (Input.GetKey(KeyCode.JoystickButton14) || // Left Trigger
                        Input.GetKey(KeyCode.JoystickButton2)  || // Left X button
                        Input.GetKey(KeyCode.JoystickButton3)  || // Left Y button
                        Input.GetKey(KeyCode.JoystickButton4)  || // Left Grip
                        Input.GetKey(KeyCode.JoystickButton8))   // Left Stick Click
                    {
                        return true;
                    }
                }
                else
                {
                    if (Input.GetKey(KeyCode.JoystickButton15) || // Right Trigger
                        Input.GetKey(KeyCode.JoystickButton0)  || // Right A button
                        Input.GetKey(KeyCode.JoystickButton1)  || // Right B button
                        Input.GetKey(KeyCode.JoystickButton5)  || // Right Grip
                        Input.GetKey(KeyCode.JoystickButton9))   // Right Stick Click
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool ReadOpenVRTrigger(uint devIndex)
        {
            if (devIndex == OpenVR.k_unTrackedDeviceIndexInvalid || OpenVR.System == null) return false;

            try
            {
                VRControllerState_t state = default;
                if (OpenVR.System.GetControllerState(devIndex, ref state, (uint)Marshal.SizeOf<VRControllerState_t>()))
                {
                    ulong mask = (1UL << (int)EVRButtonId.k_EButton_SteamVR_Trigger)
                               | (1UL << (int)EVRButtonId.k_EButton_Grip)
                               | (1UL << (int)EVRButtonId.k_EButton_A)
                               | (1UL << (int)EVRButtonId.k_EButton_ApplicationMenu)
                               | (1UL << (int)EVRButtonId.k_EButton_SteamVR_Touchpad);

                    if ((state.ulButtonPressed & mask) != 0) return true;

                    if (state.rAxis1.x > 0.15f || state.rAxis2.x > 0.15f || state.rAxis3.x > 0.15f || state.rAxis4.x > 0.15f)
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static bool ReadTriggerCombined(bool isLeft, uint devIndex)
        {
            // SteamVR Input v2 primeiro: e o unico caminho vivo neste jogo.
            // Os outros tres ficam como rede de seguranca para runtimes sem manifesto.
            return SteamVRInput.ReadTrigger(isLeft)
                || ReadXRDeviceTrigger(isLeft)
                || ReadLegacyJoystickTrigger(isLeft)
                || ReadOpenVRTrigger(devIndex);
        }

        private static void UpdateAnchorPoseFromXR(GameObject anchor, UnityEngine.XR.XRNode node)
        {
            if (anchor == null) return;
            try
            {
                var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
                if (dev.isValid)
                {
                    if (UnityEngine.XR.InputDevices.TryGetFeatureValue_Vector3f(dev.deviceId, "devicePosition", out Vector3 pos))
                    {
                        anchor.transform.localPosition = pos;
                    }
                    if (UnityEngine.XR.InputDevices.TryGetFeatureValue_Quaternionf(dev.deviceId, "deviceRotation", out Quaternion rot))
                    {
                        anchor.transform.localRotation = rot;
                    }
                }
            }
            catch { }
        }

        private void TriggerHaptic(uint devIndex, ushort durationMicroseconds)
        {
            if (Time.unscaledTime - _lastHapticTime < 0.05f) return;
            _lastHapticTime = Time.unscaledTime;

            if (devIndex != OpenVR.k_unTrackedDeviceIndexInvalid && OpenVR.System != null)
            {
                try { OpenVR.System.TriggerHapticPulse(devIndex, 0, durationMicroseconds); } catch { }
            }

            SteamVRInput.Haptic(ActiveRole == ETrackedControllerRole.LeftHand,
                Mathf.Max(durationMicroseconds / 1_000_000f, 0.05f), 0.75f);

            try
            {
                var node = (ActiveRole == ETrackedControllerRole.LeftHand) ? UnityEngine.XR.XRNode.LeftHand : UnityEngine.XR.XRNode.RightHand;
                var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
                if (dev.isValid)
                {
                    float durationSec = durationMicroseconds / 1_000_000f;
                    if (durationSec < 0.02f) durationSec = 0.05f;
                    dev.SendHapticImpulse(0, 0.75f, durationSec);
                }
            }
            catch { }
        }

        private void DesenhaMira(Vector3 origem, Vector3 fim)
        {
            if (LaserLine != null)
            {
                LaserLine.enabled = true;
                LaserLine.SetPosition(0, origem);
                LaserLine.SetPosition(1, fim);
            }
            if (ReticleObject != null && MainVRCamera?.VR?.head != null)
            {
                Vector3 paraCabeca = (MainVRCamera.VR.head.position - fim).normalized;
                ReticleObject.SetActive(true);
                ReticleObject.transform.position = fim + paraCabeca * 0.01f;
                ReticleObject.transform.rotation = Quaternion.LookRotation(-paraCabeca);
            }
        }

        private void DisableLaser()
        {
            ClearHoverState();

            IsPointingAtUI = false;
            StickNaUI = false;
            IsVirtualMouseValid = false;
            IsTriggerHeld = false;
            IsTriggerDown = false;
            IsTriggerUp = false;

            // Sem zerar o ponto, o primeiro movimento apos religar o laser viraria um delta
            // gigante de um canto da tela ao outro.
            _temPontoFisico = false;

            // Sem isto um botao fisico injetado ficaria preso apertado se o laser sumisse
            // no meio do clique - o desktop inteiro passaria a arrastar.
            if (_botaoFisicoPressionado)
            {
                _botaoFisicoPressionado = false;
                try { mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, System.IntPtr.Zero); } catch { }
            }

            if (LaserLine != null) LaserLine.enabled = false;
            if (ReticleObject != null) ReticleObject.SetActive(false);
        }
    }

    /// <summary>
    /// Patches Harmony unificados para redirecionar coordenadas e cliques de mouse no Unity
    /// a partir do Laser Pointer dos controles VR ou da pistolinha d'água.
    /// </summary>
    /// <summary>
    /// Conta as chamadas de UpdateProc, o laco que faz a selecao da garota no mapa. Medido:
    /// a coordenada que entregamos acerta o OutlineCollider dela ([ALVO] raioDaCamera->Capsule
    /// outline=SIM) e mesmo assim CurrentCol fica nulo. Ou este laco nao roda, ou roda lendo
    /// um mousePosition diferente do nosso. O contador separa os dois casos.
    /// </summary>
    [HarmonyPatch(typeof(AL.OutlinableManager), nameof(AL.OutlinableManager.UpdateProc))]
    public static class VR_OutlinableManager_UpdateProc_Patch
    {
        public static int Chamadas = 0;

        public static void Prefix()
        {
            Chamadas++;
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.mousePosition), MethodType.Getter)]
    public static class VR_Input_mousePosition_Patch
    {
        public static void Postfix(ref Vector3 __result)
        {
            try
            {
                if (Plugin.IsVRModeActive && VRControllerLaser.IsVirtualMouseValid)
                {
                    __result = VRControllerLaser.VirtualMousePosition;
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// A pistola d'agua nao dispara pelo mouse: WaterGunSceneParam tem um ShootKey, e o jogo
    /// le Input.GetKey nele. Os patches de GetMouseButton nunca chegavam nesse caminho, e por
    /// isso havia vibracao (nossa) mas nao saia agua (do jogo).
    /// </summary>
    [HarmonyPatch(typeof(Input), nameof(Input.GetKey), typeof(KeyCode))]
    public static class VR_Input_GetKey_Patch
    {
        public static void Postfix(KeyCode key, ref bool __result)
        {
            try
            {
                if (Plugin.IsVRModeActive
                    && VRWaterGunHandler.IsWaterGunSceneActive
                    && VRWaterGunHandler.IsTriggerHeld
                    && key != KeyCode.None
                    && key == VRWaterGunHandler.TeclaDeTiro)
                {
                    __result = true;
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// GetKey sozinho nao basta: quem le a tecla pode ler a borda, nao o estado. Medido:
    /// ShootKey=Space, ShootMouseKey=2 - e os patches de mouse so respondiam ao botao 0.
    /// </summary>
    [HarmonyPatch(typeof(Input), nameof(Input.GetKeyDown), typeof(KeyCode))]
    public static class VR_Input_GetKeyDown_Patch
    {
        public static void Postfix(KeyCode key, ref bool __result)
        {
            try
            {
                if (Plugin.IsVRModeActive
                    && VRWaterGunHandler.IsWaterGunSceneActive
                    && VRWaterGunHandler.IsTriggerHeld && !VRWaterGunHandler.LastTriggerHeld
                    && key != KeyCode.None
                    && key == VRWaterGunHandler.TeclaDeTiro)
                {
                    __result = true;
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.GetKeyUp), typeof(KeyCode))]
    public static class VR_Input_GetKeyUp_Patch
    {
        public static void Postfix(KeyCode key, ref bool __result)
        {
            try
            {
                if (Plugin.IsVRModeActive
                    && VRWaterGunHandler.IsWaterGunSceneActive
                    && !VRWaterGunHandler.IsTriggerHeld && VRWaterGunHandler.LastTriggerHeld
                    && key != KeyCode.None
                    && key == VRWaterGunHandler.TeclaDeTiro)
                {
                    __result = true;
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.anyKeyDown), MethodType.Getter)]
    public static class VR_Input_anyKeyDown_Patch
    {
        public static void Postfix(ref bool __result)
        {
            try
            {
                if (Plugin.IsVRModeActive && (VRControllerLaser.IsTriggerDown || VRWaterGunHandler.IsTriggerHeld))
                {
                    __result = true;
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.anyKey), MethodType.Getter)]
    public static class VR_Input_anyKey_Patch
    {
        public static void Postfix(ref bool __result)
        {
            try
            {
                if (Plugin.IsVRModeActive && (VRControllerLaser.IsTriggerHeld || VRWaterGunHandler.IsTriggerHeld))
                {
                    __result = true;
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButton))]
    public static class VR_Input_GetMouseButton_Patch
    {
        public static void Postfix(int button, ref bool __result)
        {
            try
            {
                if (!Plugin.IsVRModeActive) return;

                if (button == 0 && VRControllerLaser.IsTriggerHeld)
                {
                    __result = true;
                }
                else if (VRWaterGunHandler.IsWaterGunSceneActive && VRWaterGunHandler.IsTriggerHeld
                    && (button == 0 || button == VRWaterGunHandler.BotaoDeTiro))
                {
                    __result = true;
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButtonDown))]
    public static class VR_Input_GetMouseButtonDown_Patch
    {
        public static void Postfix(int button, ref bool __result)
        {
            try
            {
                if (!Plugin.IsVRModeActive) return;

                if (button == 0 && VRControllerLaser.IsTriggerDown)
                {
                    __result = true;
                }
                else if (VRWaterGunHandler.IsWaterGunSceneActive
                    && VRWaterGunHandler.IsTriggerHeld && !VRWaterGunHandler.LastTriggerHeld
                    && (button == 0 || button == VRWaterGunHandler.BotaoDeTiro))
                {
                    __result = true;
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButtonUp))]
    public static class VR_Input_GetMouseButtonUp_Patch
    {
        public static void Postfix(int button, ref bool __result)
        {
            try
            {
                if (!Plugin.IsVRModeActive) return;

                if (button == 0 && VRControllerLaser.IsTriggerUp)
                {
                    __result = true;
                }
                else if (VRWaterGunHandler.IsWaterGunSceneActive
                    && !VRWaterGunHandler.IsTriggerHeld && VRWaterGunHandler.LastTriggerHeld
                    && (button == 0 || button == VRWaterGunHandler.BotaoDeTiro))
                {
                    __result = true;
                }
            }
            catch { }
        }
    }
}

namespace AmanatsuVR.VRUtils
{
    /// <summary>
    /// Character creation (HumanCustom.&lt;&gt;c.&lt;Start&gt;b__260_6, read in IDA): on mouse down it jumps to
    /// IsPointerOverGameObject(int) (the no-arg overload is inlined into that jump); if it is true
    /// EventSystem.IsPointerOverGameObject() the camera gets NoCtrlCondition = always-true until mouse up.
    /// In VR the physical cursor always lands on some UI graphic, so the model never rotated. While creating
    /// in VR, "over UI" means the laser is over something interactive.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(UnityEngine.EventSystems.EventSystem), nameof(UnityEngine.EventSystems.EventSystem.IsPointerOverGameObject), typeof(int))]
    public static class VR_EventSystem_IsPointerOverGameObject_Patch
    {
        public static void Postfix(ref bool __result)
        {
            if (!Plugin.IsVRModeActive) return;
            if (VRCharCreation.Ativo && VRControllerLaser.IsPointingAtUI)
                __result = VRControllerLaser.PointerOverInteractiveUI;
            // HScene.WheelCalc drops the wheel while the pointer is over UI. With the panel hidden the physical
            // cursor stays wherever it was (often on the H menus), so start/auto did nothing.
            else if (VRHScene.Ativo)
                __result = VRControllerLaser.IsPointingAtUI && VRControllerLaser.PointerOverInteractiveUI;
        }
    }
}
