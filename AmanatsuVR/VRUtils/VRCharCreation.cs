using UnityEngine;
using UnityEngine.UI;
using AmanatsuVR.Config;
using AmanatsuVR.Logging;

namespace AmanatsuVR.VRUtils
{
    /// <summary>
    /// No modo desktop o plugin inteiro desiste antes de tudo, entao nao havia como medir o caso
    /// de controle que mais importa: a MESMA tela de criacao sem VR. Este patch e o unico pedaco
    /// que roda nos dois modos, e so na CustomScene, que e onde o HumanCustom existe.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(CharacterCreation.HumanCustom), "Update")]
    internal static class HumanCustomUpdatePatch
    {
        private static void Postfix() => VRCharCreation.MedeSkinDaCena();
    }

    /// <summary>
    /// A tela de criacao de personagem e o unico lugar do jogo onde a composicao "painel plano
    /// na frente + mundo 3D atras" nao fecha. O fundo da tela nao e cenario: e um RawImage
    /// (Cvs_BackGround/rawBackGround), e o modelo 3D e desenhado ENTRE esse fundo e o resto da
    /// UI. Isso so alinha se a projecao da tela e a da camera forem a mesma; em estereo nao sao,
    /// e o que fecha para um olho esta errado para o outro.
    ///
    /// Entao o modelo nao e composto em 3D: uma camera propria o desenha, sozinho e com fundo
    /// transparente, numa RenderTexture do tamanho da tela, e essa textura entra na UI como um
    /// RawImage de tela cheia logo acima do fundo - dentro do canvas do jogo, nao colado por
    /// cima do painel. Assim a ordem de desenho e a do proprio jogo: na frente do fundo, atras
    /// de todo botao e de qualquer caixa de confirmacao. E como a camera copia pose e fov da
    /// camera do jogo, o modelo cai exatamente onde o jogo o punha.
    ///
    /// Com o grip escondendo o painel, nada disso e usado e o modelo real volta em estereo.
    /// </summary>
    public class VRCharCreation
    {
        /// <summary>Estamos na tela de criacao de personagem.</summary>
        public static bool Ativo { get; private set; } = false;

        private CharacterCreation.HumanCustom _custom;
        private float _ultimaBusca = -10f;

        private Camera _previa;
        // Dois alvos alternados. Com um so, a mesma textura era escrita pela camera de previa e
        // amostrada pela UGUICapture no MESMO frame, e a UGUICapture por sua vez e amostrada pelo
        // olho - dois niveis de encadeamento num quadro. O RenderGraph do Unity 6 reaproveita
        // memoria de recurso nesse caso e o resultado indefinido aparece como magenta; foi assim
        // que o quadrado rosa voltou. Escrevendo em um e mostrando o outro, nada e lido enquanto
        // esta sendo escrito. Custa um frame de atraso, invisivel.
        private RenderTexture[] _rts = new RenderTexture[2];
        private int _atual = 0;
        private RawImage _imagem;
        private int _mascaraModelo = 0;
        private bool _jaDiagnosticou = false;
        private float _tempoEntrada = 0f;
        private bool _jaRemontou = false;
        private bool _jaConferiu = false;

        public void Update()
        {
            MedeSkinDaCena();

            if (Time.unscaledTime - _ultimaBusca > 0.5f)
            {
                _ultimaBusca = Time.unscaledTime;
                try { _custom = Object.FindObjectOfType<CharacterCreation.HumanCustom>(); }
                catch { _custom = null; }

                if (_custom != null) { AtualizaMascaraDoModelo(); GaranteLuzVisivel(); }
            }

            bool ativoAgora = _custom != null && PluginConfig.CharCreationPreview.Value;
            if (ativoAgora != Ativo)
            {
                Ativo = ativoAgora;
                PluginLog.Info($"[AmanatsuVR][CUSTOM] character creation {(Ativo ? "ENTERED" : "left")}.");
                if (!Ativo) { _jaDiagnosticou = false; Teardown(); return; }
            }

            if (!Ativo) return;

            RotateWithStick();

            // A mascara so fica pronta quando o modelo existe, e ele nao existe no frame em que
            // a cena carrega - foi por isso que o primeiro diagnostico saiu com ObjTop nulo.
            if (_mascaraModelo == 0) return;

            if (!_jaDiagnosticou) { _jaDiagnosticou = true; _tempoEntrada = Time.unscaledTime; Diagnostico(); }

            // Quem remonta e o LateUpdate do jogo, depois das marcacoes - conferir no mesmo frame
            // mediria o estado anterior.
            if (!_jaRemontou && Time.unscaledTime - _tempoEntrada > 1f)
            {
                _jaRemontou = true;
                RefazTexturasDaPele();
            }
            else if (_jaRemontou && !_jaConferiu && Time.unscaledTime - _tempoEntrada > 2.5f)
            {
                _jaConferiu = true;
                SalvaPele("PELE_DEPOIS");
            }

            // Painel escondido = o jogador quer ver o modelo de verdade, em estereo.
            bool usarPrevia = VRController.PainelVisivel;

            // Quem tira o modelo da vista estereo e o CameraHijacker, no Synchronize: ele
            // reescreve a cullingMask da nossa camera todo frame a partir da do jogo, entao
            // mexer nela aqui nao duraria um quadro.
            VRController.MascaraOculta = usarPrevia ? _mascaraModelo : 0;

            GarantePrevia();
            if (_previa != null)
            {
                _previa.enabled = usarPrevia;
                var camJogo = _custom.MainCamera;
                if (usarPrevia && camJogo != null)
                {
                    _previa.transform.position = camJogo.transform.position;
                    _previa.transform.rotation = camJogo.transform.rotation;
                    _previa.fieldOfView = camJogo.fieldOfView;
                    _previa.nearClipPlane = camJogo.nearClipPlane;
                    _previa.farClipPlane = camJogo.farClipPlane;
                    // The whole projection, not just fov: aspect and lens shift come with it, so the model lands
                    // where the game draws it even if our texture size and the game camera's aspect disagree.
                    _previa.projectionMatrix = camJogo.projectionMatrix;
                    _previa.cullingMask = _mascaraModelo;
                }
            }
            if (_imagem != null)
            {
                _imagem.enabled = usarPrevia;
                if (usarPrevia && _previa != null)
                {
                    _atual ^= 1;
                    _previa.targetTexture = _rts[_atual];
                    _imagem.texture = _rts[_atual ^ 1];
                }
            }
        }

        private const float StickDeadzone = 0.2f, StickDegreesPerSecond = 90f, MaxPitch = 80f;

        /// <summary>
        /// Stick orbits the creation camera (X = yaw, Y = pitch) through the game's own CameraController, the
        /// same rotation its mouse drag writes. The laser hand's stick is skipped while it scrolls a list.
        /// </summary>
        private void RotateWithStick()
        {
            var camCtrl = _custom.CamCtrl;
            if (camCtrl == null) return;

            Vector2 stick = Vector2.zero;
            foreach (bool left in new[] { true, false })
            {
                if (VRControllerLaser.StickNaUI && left == (VRControllerLaser.ActiveRole == Valve.VR.ETrackedControllerRole.LeftHand)) continue;
                var s = SteamVRInput.ReadStick(left);
                if (s.sqrMagnitude > stick.sqrMagnitude) stick = s;
            }
            if (stick.magnitude < StickDeadzone) return;

            float step = StickDegreesPerSecond * Time.unscaledDeltaTime;
            var rot = camCtrl.CameraRot;
            float pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, rot.x) - stick.y * step, -MaxPitch, MaxPitch);
            camCtrl.CameraRot = new Vector3(pitch, rot.y + stick.x * step, rot.z);
        }

        /// <summary>
        /// As layers do modelo saidas dos renderizadores dele. Ler Human.LowLayer/HighLayer
        /// seria adivinhar se sao estaticas ou de instancia; o que esta na cena nao mente.
        /// </summary>
        private void AtualizaMascaraDoModelo()
        {
            try
            {
                var human = _custom.Human;
                var topo = human != null ? human.ObjTop : null;
                if (topo == null) return;

                int mascara = 0;
                foreach (var r in topo.GetComponentsInChildren<Renderer>(true))
                {
                    if (r != null) mascara |= 1 << r.gameObject.layer;
                }
                if (mascara != 0) _mascaraModelo = mascara;
            }
            catch { }
        }

        /// <summary>O canvas do fundo: e nele que o modelo tem que entrar para ficar na frente
        /// do fundo e atras de tudo mais. Se ele sumir, nao ha onde por a imagem.</summary>
        private Canvas AchaCanvasDeFundo()
        {
            foreach (var c in Object.FindObjectsOfType<Canvas>())
            {
                if (c != null && c.name.Contains("BackGround")) return c;
            }
            return null;
        }

        private void GarantePrevia()
        {
            for (int i = 0; i < 2; ++i)
            {
                // Created on entry with the window size of that moment; the window can still resize after
                // it (the model sat off its desktop position until leaving and entering again).
                if (_rts[i] != null && (_rts[i].width != Screen.width || _rts[i].height != Screen.height))
                {
                    PluginLog.Info($"[AmanatsuVR][CUSTOM] screen changed {_rts[i].width}x{_rts[i].height} -> {Screen.width}x{Screen.height}; texture recreated.");
                    if (_previa != null && _previa.targetTexture == _rts[i]) _previa.targetTexture = null;
                    _rts[i].Release();
                    Object.Destroy(_rts[i]);
                    _rts[i] = null;
                }
                if (_rts[i] == null)
                {
                    _rts[i] = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32)
                    { name = $"AmanatsuVR_CharPreviewRT{i}" };
                }
            }

            if (_previa == null)
            {
                var go = new GameObject("AmanatsuVR_CharPreviewCamera");
                _previa = go.AddComponent<Camera>();
                _previa.targetTexture = _rts[_atual];
                _previa.clearFlags = CameraClearFlags.Color;
                // Transparente: o fundo da tela ja esta desenhado embaixo, e um preto opaco aqui
                // viraria justamente o quadrado preto em volta do modelo.
                _previa.backgroundColor = new Color(0f, 0f, 0f, 0f);
                _previa.stereoTargetEye = StereoTargetEyeMask.None;
                _previa.useOcclusionCulling = false;
                // Antes da UGUICapture (que esta em -1000): a RenderTexture precisa estar pronta
                // quando o canvas que a mostra for capturado, senao e leitura-no-mesmo-frame.
                _previa.depth = -1100f;

                var add = go.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                if (add != null)
                {
                    add.allowXRRendering = false;
                    add.renderPostProcessing = false;
                    add.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.None;
                    add.renderType = UnityEngine.Rendering.Universal.CameraRenderType.Base;
                    add.renderShadows = false;
                }
            }

            if (_imagem == null)
            {
                var canvas = AchaCanvasDeFundo();
                if (canvas == null) return;

                var go = new GameObject("AmanatsuVR_CharPreview");
                go.layer = canvas.gameObject.layer;
                go.transform.SetParent(canvas.transform, false);
                go.transform.SetAsLastSibling();

                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;

                _imagem = go.AddComponent<RawImage>();
                _imagem.texture = _rts[_atual ^ 1];
                // Tela cheia e clicavel engoliria todos os botoes - e o arrasto que gira o modelo.
                _imagem.raycastTarget = false;

                PluginLog.Info($"[AmanatsuVR][CUSTOM] model image inserted in '{canvas.name}'"
                    + $" (order={canvas.sortingOrder}, layer={go.layer}).");
            }
        }

        private void Teardown()
        {
            VRController.MascaraOculta = 0;
            if (_imagem != null) { Object.Destroy(_imagem.gameObject); _imagem = null; }
            if (_previa != null) { Object.Destroy(_previa.gameObject); _previa = null; }
            for (int i = 0; i < 2; ++i)
            {
                if (_rts[i] != null) { _rts[i].Release(); _rts[i] = null; }
            }
            _mascaraModelo = 0;
        }

        /// <summary>
        /// O objeto que monta a textura da pele, quando existe.
        /// </summary>
        private Character.CustomTextureControl CtrlPele()
        {
            var human = _custom != null ? _custom.Human : null;
            return human != null && human.Face != null ? human.Face._customTexCtrlFace : null;
        }

        private void SalvaPele(string nome)
        {
            var ctrl = CtrlPele();
            if (ctrl != null) SalvaPng(ctrl._createTex, nome);
        }

        /// <summary>
        /// A pele nao e uma textura: e uma montagem por partes que a tela de criacao refaz a cada
        /// edicao. Por isso chamar CreateFaceTexture na mao nao adiantava - ela e so a etapa do
        /// blit, e sem as marcacoes de cor, parametro e layout o resultado sai com as camadas que
        /// ja estavam la e o resto branco, que foi exatamente o que o PNG mostrou (boca desenhada,
        /// pele em branco).
        ///
        /// AddUpdateCM...FlagsFull marca tudo e deixa o LateUpdate do proprio jogo remontar, que e
        /// o mesmo caminho de quando o jogador troca um item na UI.
        /// </summary>
        private void RefazTexturasDaPele()
        {
            try
            {
                var human = _custom.Human;
                if (human == null) return;

                if (human.Face != null) human.Face.AddUpdateCMFaceFlagsFull();
                if (human.Body != null) human.Body.AddUpdateCMBodyFlagsFull();
                PluginLog.Info("[AmanatsuVR][CUSTOM] full rebuild flagged.");
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning($"[AmanatsuVR][CUSTOM] rebuild failed: {ex.Message}");
            }
        }

        private static string _cenaMedida = null;

        /// <summary>
        /// O material estar no default nao diz de onde vem o default. Duas causas possiveis e
        /// opostas: o personagem entrou no editor ja em branco (os dados nunca foram preenchidos),
        /// ou os dados estao certos e alguem nao os aplicou. Os dados sao a resposta - e o
        /// DefaultData, que e o molde que o jogo usa para "personagem padrao", diz se o molde em si
        /// veio vazio. O codigo de erro do ultimo load diz se algum arquivo falhou ao ser lido.
        /// </summary>
        private static void MedeDados()
        {
            var hc = Object.FindObjectOfType<CharacterCreation.HumanCustom>();
            if (hc == null) return;

            Dump("vivo", hc.HumanData);
            Dump("padrao", hc.DefaultData);
        }

        private static void Dump(string rotulo, Character.HumanData d)
        {
            if (d == null) { PluginLog.Info($"[AmanatsuVR][DATA] {rotulo}: NULL"); return; }

            var c = d.Custom;
            if (c == null || c.Body == null || c.Face == null)
            {
                PluginLog.Info($"[AmanatsuVR][DATA] {rotulo}: Custom/Body/Face null.");
                return;
            }

            PluginLog.Info($"[AmanatsuVR][DATA] {rotulo}: file='{d.CharaFileName}' error={d._lastLoadErrorCode}"
                + $" version={d.NowVersion}");
            PluginLog.Info($"[AmanatsuVR][DATA]   body.skinId={c.Body.skinId}"
                + $" skinMain={c.Body.skinMainColor} skinShadow={c.Body.skinShadowColor}"
                + $" nip={c.Body.nipColor}");
            PluginLog.Info($"[AmanatsuVR][DATA]   face.headId={c.Face.headId} skinId={c.Face.skinId}"
                + $" eyebrow={c.Face.eyebrowColor} nose={c.Face.noseColor} detail={c.Face.detailColor}");
        }

        /// <summary>
        /// A luz nao era: movida para dentro da mascara, o modelo continuou branco. E o print
        /// mostra que o fundo esta normal e so o modelo sai branco - pele E cabelo, isto e, todos
        /// os shaders AL/*, com as linhas escuras no lugar. Cor branca com desenho certo em toda a
        /// familia de shaders aponta para os parametros de cor do material, que eu nunca li: o
        /// diagnostico so listava as TEXTURAS dos materiais dos renderizadores; as cores eu so li
        /// no material de montagem.
        ///
        /// E nunca medi o caso que funciona. Esta funcao nao depende da tela de criacao: acha
        /// qualquer pele AL na cena, seja a garota do mapa 3D ou a da criacao, e despeja as cores.
        /// Com o mesmo numero nos dois lugares a diferenca aparece sozinha.
        /// </summary>
        internal static void MedeSkinDaCena()
        {
            try
            {
                // Sem o sufixo, a medida do desktop sobrescreveria a do VR com o mesmo nome.
                string cena = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
                    + (Plugin.IsVRModeActive ? "_VR" : "_desktop");
                if (cena == _cenaMedida) return;

                Renderer alvo = null;
                foreach (var r in Object.FindObjectsOfType<Renderer>())
                {
                    var m = r != null ? r.sharedMaterial : null;
                    if (m != null && m.shader != null && m.shader.name == "AL/skin_head") { alvo = r; break; }
                }
                // Sem pele na cena ainda: tentar de novo no proximo frame, nao marcar como medida.
                if (alvo == null) return;

                _cenaMedida = cena;
                var mat = alvo.sharedMaterial;
                PluginLog.Info($"[AmanatsuVR][SKIN] scene='{cena}' renderer='{alvo.gameObject.name}'"
                    + $" material='{mat.name}' keywords={string.Join(",", mat.shaderKeywords)}");

                var sh = mat.shader;
                for (int i = 0; i < sh.GetPropertyCount(); ++i)
                {
                    string prop = sh.GetPropertyName(i);
                    var tipo = sh.GetPropertyType(i);
                    string valor;
                    if (tipo == UnityEngine.Rendering.ShaderPropertyType.Color) valor = mat.GetColor(prop).ToString();
                    else if (tipo == UnityEngine.Rendering.ShaderPropertyType.Float
                          || tipo == UnityEngine.Rendering.ShaderPropertyType.Range) valor = mat.GetFloat(prop).ToString("F3");
                    else if (tipo == UnityEngine.Rendering.ShaderPropertyType.Vector) valor = mat.GetVector(prop).ToString();
                    else continue;
                    PluginLog.Info($"[AmanatsuVR][SKIN]   {prop} = {valor}");
                }

                // A montagem da pele desta cena, em imagem: e o unico jeito de saber se a branca
                // da criacao e a mesma branca que o mapa 3D usa e pinta depois.
                var pele = mat.GetTexture("_Create_jm_main_skin_head");
                if (pele != null) SalvaPng(pele.TryCast<RenderTexture>(), $"MONTAGEM_{cena}");

                MedeDados();
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning($"[AmanatsuVR][SKIN] measuring skin failed: {ex.Message}");
            }
        }

        /// <summary>
        /// O [LUZ] mostrou o que faltava: a unica luz da cena e um Directional filho da MainCamera,
        /// na layer 0, com cullingMask=0x80 (so a layer do personagem). A layer 0 nao entra nem na
        /// mascara da camera de previa (que so tem as layers do modelo) nem na da camera VR - e o
        /// Unity culla luz pela mascara da camera: uma luz cuja layer esta fora dela nao ilumina
        /// nada naquele render. Toon sem luz nenhuma = branco chapado com olhos pretos.
        ///
        /// Mover a luz para uma layer do modelo nao muda o que ela ilumina - isso e a cullingMask
        /// dela, que fica intacta - so a faz existir para as nossas cameras. Luz nao tem renderer,
        /// entao nada novo passa a ser desenhado.
        /// </summary>
        private void GaranteLuzVisivel()
        {
            if (_mascaraModelo == 0) return;

            int layerModelo = 0;
            while (layerModelo < 32 && (_mascaraModelo & (1 << layerModelo)) == 0) ++layerModelo;
            if (layerModelo >= 32) return;

            try
            {
                foreach (var l in Object.FindObjectsOfType<Light>(true))
                {
                    if (l == null) continue;
                    if ((_mascaraModelo & (1 << l.gameObject.layer)) != 0) continue;

                    PluginLog.Info($"[AmanatsuVR][LIGHT] '{l.name}' moved from layer"
                        + $" {l.gameObject.layer} to {layerModelo} (outside the cameras' mask).");
                    l.gameObject.layer = layerModelo;
                }
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning($"[AmanatsuVR][LIGHT] moving light failed: {ex.Message}");
            }
        }

        /// <summary>
        /// O mesmo AL/skin_head funciona nos mapas 3D, com a mesma camera VR e as mesmas features
        /// de render desligadas - so falha aqui. Entao nao e o shader nem o pipeline: e alguma
        /// coisa desta cena. Pele chapada e olhos pretos num shader toon e o que se ve quando ele
        /// nao recebe luz nenhuma, e luz o Unity culla pela mascara da camera: uma luz numa layer
        /// fora da mascara simplesmente nao ilumina.
        ///
        /// E a UGUICapture move TODO objeto sob um canvas para a layer 15 - que e exatamente a
        /// layer que o Synchronize tira da camera VR. Se o refletor desta tela estiver nessa
        /// hierarquia, ele foi para a 15 junto e sumiu do calculo de luz.
        /// </summary>
        private void MedeLuzes()
        {
            try
            {
                var cam = _custom.MainCamera;
                int mascaraCam = cam != null ? cam.cullingMask : 0;
                // A mascara que a camera VR recebe todo frame, montada como no Synchronize.
                int mascaraVR = ((mascaraCam | VRController.ExtraGameCullingMask)
                    & ~((1 << 15) | VRController.MascaraOculta)) | (1 << 31);

                PluginLog.Info($"[AmanatsuVR][LIGHT] gameMask=0x{mascaraCam:X8} vrMask=0x{mascaraVR:X8}"
                    + $" ambient={RenderSettings.ambientLight} mode={RenderSettings.ambientMode}");

                foreach (var l in Object.FindObjectsOfType<Light>(true))
                {
                    if (l == null) continue;
                    int bit = 1 << l.gameObject.layer;
                    PluginLog.Info($"[AmanatsuVR][LIGHT]   '{l.name}' layer={l.gameObject.layer}"
                        + $" type={l.type} intensity={l.intensity:F2} enabled={l.enabled}"
                        + $" active={l.gameObject.activeInHierarchy}"
                        + $" cullingMask=0x{l.cullingMask:X8}"
                        + $" inVR={((mascaraVR & bit) != 0)}"
                        + $" parent='{(l.transform.parent != null ? l.transform.parent.name : "-")}'");
                }
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning($"[AmanatsuVR][LIGHT] measuring lights failed: {ex.Message}");
            }
        }

        /// <summary>
        /// O streaming ja estava desligado e a textura base esta com o mipmap 0 carregado, entao
        /// nao e carregamento. Restam duas explicacoes para uma montagem que desenha a boca e nao
        /// desenha a pele: a base do rosto e branca de verdade, ou a montagem soma as camadas com
        /// os parametros zerados. Uma imagem e a lista de parametros respondem as duas.
        /// </summary>
        private void MedeEntradaDaMontagem()
        {
            try
            {
                var ctrl = CtrlPele();
                if (ctrl == null) return;

                SalvaTextura(ctrl._texMain, "BASE");

                var mat = ctrl._matCreate;
                if (mat == null || mat.shader == null) return;

                var sh = mat.shader;
                for (int i = 0; i < sh.GetPropertyCount(); ++i)
                {
                    string prop = sh.GetPropertyName(i);
                    var tipo = sh.GetPropertyType(i);
                    string valor;
                    if (tipo == UnityEngine.Rendering.ShaderPropertyType.Color) valor = mat.GetColor(prop).ToString();
                    else if (tipo == UnityEngine.Rendering.ShaderPropertyType.Float
                          || tipo == UnityEngine.Rendering.ShaderPropertyType.Range) valor = mat.GetFloat(prop).ToString("F3");
                    else if (tipo == UnityEngine.Rendering.ShaderPropertyType.Vector) valor = mat.GetVector(prop).ToString();
                    else continue;

                    PluginLog.Info($"[AmanatsuVR][CUSTOM]   create.{prop} = {valor}");
                }
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning($"[AmanatsuVR][CUSTOM] measuring input failed: {ex.Message}");
            }
        }

        /// <summary>Salva uma textura qualquer passando por uma RT temporaria: as Texture2D do
        /// jogo nao sao legiveis pela CPU e so um blit as torna observaveis.</summary>
        private void SalvaTextura(Texture tex, string nome)
        {
            if (tex == null) { PluginLog.Warning($"[AmanatsuVR][CUSTOM] {nome}: null texture."); return; }

            var tmp = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, tmp);
            SalvaPng(tmp, nome);
            RenderTexture.ReleaseTemporary(tmp);
        }

        private static void SalvaPng(RenderTexture rt, string nome)
        {
            if (rt == null) { PluginLog.Warning($"[AmanatsuVR][CUSTOM] {nome}: no RenderTexture."); return; }

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
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, $"CUSTOM_{nome}.png"), bytes);

            var c = tex.GetPixel(rt.width / 2, rt.height / 2);
            PluginLog.Info($"[AmanatsuVR][CUSTOM] {nome} saved ({rt.width}x{rt.height}) center={c}");
            Object.Destroy(tex);
        }

        /// <summary>
        /// O modelo aparece branco com os olhos pretos, o que e textura faltando, nao
        /// iluminacao. Nao ha como saber daqui se o shader nao suporta o nosso caminho de
        /// render ou se a textura combinada nunca foi gerada - entao mede as duas coisas em vez
        /// de eu chutar uma.
        /// </summary>
        private void Diagnostico()
        {
            try
            {
                var cam = _custom.MainCamera;
                PluginLog.Info($"[AmanatsuVR][CUSTOM] camera='{(cam != null ? cam.name : "-")}'"
                    + $" fov={(cam != null ? cam.fieldOfView : 0f):F1} pos={(cam != null ? cam.transform.position : Vector3.zero)}"
                    + $" modelMask=0x{_mascaraModelo:X8} panelVisible={VRController.PainelVisivel}");

                MedeLuzes();

                var topo = _custom.Human.ObjTop;
                int n = 0;
                foreach (var r in topo.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled || !r.isVisible) continue;
                    // So a pele e o cabelo interessam: sao eles que saem brancos.
                    string nome = r.gameObject.name;
                    if (!nome.Contains("face") && !nome.Contains("body") && !nome.Contains("hair")) continue;
                    if (++n > 4) break;

                    var mat = r.sharedMaterial;
                    if (mat == null || mat.shader == null) continue;

                    PluginLog.Info($"[AmanatsuVR][CUSTOM]   '{nome}' material='{mat.name}'"
                        + $" shader='{mat.shader.name}' supported={mat.shader.isSupported}"
                        + $" passes={mat.passCount}");

                    // As texturas destes shaders nao se chamam _MainTex; perguntar pelo nome
                    // errado devolve "nenhuma" para todo mundo e nao mede nada.
                    var sh = mat.shader;
                    for (int i = 0; i < sh.GetPropertyCount(); ++i)
                    {
                        if (sh.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                        string prop = sh.GetPropertyName(i);
                        var t = mat.GetTexture(prop);
                        PluginLog.Info($"[AmanatsuVR][CUSTOM]     {prop} = "
                            + (t != null ? $"{t.name} {t.width}x{t.height} ({t.GetIl2CppType().Name})" : "NULL"));
                    }
                }
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning($"[AmanatsuVR][CUSTOM] diagnostic failed: {ex.Message}");
            }
        }
    }
}
