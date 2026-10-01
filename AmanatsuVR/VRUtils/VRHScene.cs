using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using AmanatsuVR.Logging;
using AmanatsuVR.Config;
using AL.H;

namespace AmanatsuVR.VRUtils
{
    /// <summary>
    /// A cena H nos controles: camera livre ou primeira pessoa (homem ou mulher), atalhos de
    /// iniciar / modo forte / finalizar, mira no tiro livre (ぶっかけ) e modo automatico.
    ///
    /// Tudo o que mexe no jogo passa pelo mesmo caminho da interface do jogo: a roda do mouse
    /// (entrada fisica) inicia o ato, e os botoes de forte, de finalizacao e de encerrar o tiro
    /// recebem um clique de verdade pelo EventSystem, que respeita interactable como o mouse.
    /// </summary>
    public class VRHScene
    {
        /// <summary>HScene viva: o grip sozinho deixa de alternar o painel.</summary>
        public static bool Ativo { get; private set; }

        /// <summary>
        /// Human do ator em primeira pessoa (Pointer), ou zero. O olhar do jogo (EyeNeck) desse
        /// ator fica desligado: senao, nas pausas com fala ele vira o pescoco para a camera do
        /// jogo e a nossa visao, presa ao pescoco, e arrastada junto.
        /// </summary>
        public static IntPtr HumanPrimeiraPessoa { get; private set; }

        /// <summary>Linha de mira do tiro livre para o laser desenhar. Nulo = sem mira.</summary>
        public static Vector3? MiraOrigem { get; private set; }
        public static Vector3 MiraFim { get; private set; }

        private enum Modo { Livre, PrimeiraPessoa }

        private VRCamera Cam;
        private VRController Controlador;
        private VRControllerLaser Laser;

        private Modo _modo = Modo.Livre;
        private bool _atorMulher = false;           // ultimo ator de primeira pessoa escolhido
        private Vector3 _livrePos;
        private float _livreYaw;
        private bool _livreIniciada = false;
        private bool _livreMovida = false;

        // Cabeca escondida: pescoco quase em escala zero (zero exato da matriz singular), e os
        // colisores da cabeca num substituto em tamanho real.
        private const float EscalaOculta = 1e-4f;
        private Transform _pescocoOculto;
        private Vector3 _escalaPescoco = Vector3.one;
        private Transform _cabecaFisica;
        private readonly List<(Transform t, Transform pai, Vector3 pos, Quaternion rot, Vector3 esc)> _colisores = new();

        // Entrada por mao
        private readonly Mao[] _maos = { new Mao(true), new Mao(false) };
        private bool _maoAtivaEsquerda = false;

        // Tiro livre
        private bool _emPut = false;
        private Modo _modoAntesDoPut;
        private bool _tiroAuto = false;
        private float _tiroAutoFim;
        private bool _tiroAutoAcabou;
        private Transform _alvoAuto;
        private bool _atirando = false;
        private KeyCode _teclaTiro = KeyCode.None;
        private bool _logouEmissor = false;

        // Automatico
        private bool _auto = false;
        private enum Fase { Iniciar, Ato, Finalizando, Trocando }
        private Fase _fase;
        private float _faseDesde;
        private float _tempoAto;
        private float _ultimaTentativa;

        private class Mao
        {
            public readonly bool Esquerda;
            public Mao(bool esquerda) { Esquerda = esquerda; }

            public Vector2 Stick;
            public bool Gatilho, Grip, Clique;
            public bool GatilhoAntes, GripAntes, CliqueAntes, AmbosAntes;
            public int Direcao = -1, DirecaoAntes = -1;   // 0 cima, 1 baixo, 2 esquerda, 3 direita
            public float SegurandoDesde = -1f;           // frente sem gatilho (primeira pessoa)
            public float ClickDesde = -1f;               // L3/R3: toque = camera, segurar = automatico
            public bool ClickConsumido;
            public bool SeguraConsumida, SeguraCancelada;
        }

        public void Setup(VRCamera cam, VRController controlador, VRControllerLaser laser)
        {
            Cam = cam;
            Controlador = controlador;
            Laser = laser;
        }

        // ---------------------------------------------------------------- ciclo

        public void Update()
        {
            HScene hs = null;
            try { hs = HScene.Instance; } catch { }
            // Vida da cena pela HScene, nao pelo controller: na troca de pose o controller some
            // por alguns quadros, e isso nao pode resetar a camera nem desligar o automatico.
            bool vivo = hs != null && Plugin.IsVRModeActive;

            if (vivo != Ativo)
            {
                Ativo = vivo;
                if (vivo) Entrou(hs); else Saiu();
            }
            if (!vivo) return;

            LeEntrada();
            TocaPulsos();

            var ctrl = SafeController(hs);
            if (ctrl == null)
            {
                if (_modo == Modo.Livre && !_emPut) MoveLivre();
                return;
            }

            bool put = false;
            try { put = ctrl.IsPutNow; } catch { }
            if (put != _emPut)
            {
                _emPut = put;
                if (put) EntrouPut(hs); else SaiuPut();
            }

            foreach (var m in _maos) ProcessaAtalhos(hs, ctrl, m);
            if (_emPut) AtualizaPut(hs);
            else if (_auto) AtualizaAuto(hs, ctrl);

            if (_modo == Modo.Livre && !_emPut) MoveLivre();
        }

        public void LateUpdate()
        {
            if (!Ativo || Cam == null) return;

            // Antes de tudo que le osso do homem (olhos em primeira pessoa, genital do jato).
            // Congelado so durante a mira; depois dela o jogo o mostra na animacao do parado pos-climax.
            SuspendeMiraDaGenital(_emPut && _congelaHomem);
            if (_emPut && _congelaHomem) { CongelaHomem(); AfastaHomem(); }

            // Primeira pessoa do homem com ele escondido pelo jogo (fim do tiro, ate o PourRestore): a
            // pose dele nesse estado nao e para ser vista e levava a camera para o alto dela. A camera
            // fica onde os olhos dele estavam por ultimo.
            bool homemEscondido = false;
            if (_modo == Modo.PrimeiraPessoa && !_atorMulher && !_emPut)
                try { var h = Ator(false); homemEscondido = h != null && !h.Active; } catch { }

            if (Cam != null) Cam.LockHeadToPose = _modo == Modo.PrimeiraPessoa;

            if (_modo == Modo.PrimeiraPessoa && homemEscondido)
            {
                // PoseForcada fica como estava.
            }
            else if (_modo == Modo.PrimeiraPessoa)
            {
                var pose = PoseDosOlhos();
                if (pose.HasValue)
                {
                    Vector3 p = pose.Value.position + AvancoAoAbaixar();
                    // Tiro com mira pelos olhos do homem: camera a frente dos olhos dele.
                    if (_emPut && _congelaHomem)
                        p += Quaternion.Euler(0f, pose.Value.rotation.eulerAngles.y, 0f) * Vector3.forward
                            * (PluginConfig.HAimCameraForward.Value * Escala(AtorDaPrimeiraPessoa()));
                    Cam.PoseForcada = (p, pose.Value.rotation);
                }
                else { MudaModo(Modo.Livre); }
            }
            else
            {
                // Ate o jogador mexer, a camera livre acompanha a do jogo: ela so vai para o
                // enquadramento da H depois de a cena carregar, e cada troca de pose a move.
                if (!_livreMovida) IniciaLivreNaCameraDoJogo(HScene.Instance);
                if (_livreIniciada) Cam.PoseForcada = (_livrePos, Quaternion.Euler(0f, _livreYaw, 0f));
            }

            if (_emPut) ApontaEmissor();
            else GuardaPoseHomem();
            AtualizaFade();
        }

        // ---------------------------------------------------------------- pose do homem no tiro livre

        // No tiro livre o jogo esconde o homem (HActor.Active = Human.VisibleAll) porque no modo
        // janela quem joga e a camera; a pose dele nesse estado nao foi feita para ser vista e
        // sai quebrada. Em VR ele fica em cena congelado na ultima pose antes do tiro.
        private Transform[] _ossosHomem;
        private Vector3[] _poseHomemPos;
        private Quaternion[] _poseHomemRot;
        private IntPtr _poseHomemDe;
        private bool _poseHomemValida;

        private void GuardaPoseHomem()
        {
            if (Time.frameCount % 3 != 0) return;
            var homem = Ator(false);
            if (homem == null) return;
            try
            {
                // So pose que o jogo esta mostrando: com ele escondido a pose ja pode ser a quebrada.
                if (!homem.Active) return;
                var ctrl = SafeController(HScene.Instance);
                if (ctrl == null || ctrl.IsPutNow) return;

                IntPtr chave = homem.Human.Pointer;
                if (chave != _poseHomemDe || _ossosHomem == null)
                {
                    _ossosHomem = homem.Human.Transform.GetComponentsInChildren<Transform>(true);
                    _poseHomemPos = new Vector3[_ossosHomem.Length];
                    _poseHomemRot = new Quaternion[_ossosHomem.Length];
                    _poseHomemDe = chave;
                }
                for (int i = 0; i < _ossosHomem.Length; i++)
                {
                    var t = _ossosHomem[i];
                    if (t == null) continue;
                    _poseHomemPos[i] = t.localPosition;
                    _poseHomemRot[i] = t.localRotation;
                }
                _poseHomemValida = true;

                var ponta = OssosPenis().ponta;
                _pontaAntesDoTiro = ponta != null ? ponta.position : (Vector3?)null;
            }
            catch { _poseHomemValida = false; }
        }

        // ---- fade: esconde os teleportes (homem recuando/voltando, camera saltando de lugar)

        private GameObject _fadeObj;
        private Material _fadeMat;
        private float _fadeDesde = -100f;
        private const float FadeSegura = 0.12f, FadeVolta = 0.5f;

        /// <summary>Escurece na hora e clareia em ~0,6 s. O salto acontece com a tela preta.</summary>
        private void Fade()
        {
            if (_fadeObj == null)
            {
                if (Cam?.Normal == null) return;
                try
                {
                    _fadeObj = new GameObject("AmanatsuVR_Fade");
                    _fadeObj.layer = VRController.UI_SCREEN_LAYER;
                    _fadeObj.transform.SetParent(Cam.Normal.transform, false);
                    // Colado aos olhos (o near e 0,05) e largo o bastante para cobrir tudo.
                    _fadeObj.transform.localPosition = new Vector3(0f, 0f, 0.06f);
                    _fadeObj.transform.localScale = new Vector3(1f, 1f, 1f);
                    _fadeObj.AddComponent<MeshFilter>().mesh = UIScreen.GetQuadMesh();
                    _fadeMat = CustomAssetManager.CreateLaserMaterial(Color.black);
                    _fadeMat.renderQueue = 5000; // por cima de tudo, inclusive do painel
                    var mr = _fadeObj.AddComponent<MeshRenderer>();
                    mr.material = _fadeMat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                }
                catch (Exception ex) { PluginLog.Warning($"[AmanatsuVR][H] fade not created: {ex.Message}"); return; }
            }
            _fadeDesde = Time.unscaledTime;
            _fadeObj.SetActive(true);
            AtualizaFade();
        }

        private void AtualizaFade()
        {
            if (_fadeObj == null || !_fadeObj.activeSelf) return;
            float t = Time.unscaledTime - _fadeDesde;
            float alfa = t < FadeSegura ? 1f : 1f - (t - FadeSegura) / FadeVolta;
            if (alfa <= 0f) { _fadeObj.SetActive(false); return; }
            var cor = new Color(0f, 0f, 0f, alfa);
            if (_fadeMat.HasProperty("_BaseColor")) _fadeMat.SetColor("_BaseColor", cor);
            if (_fadeMat.HasProperty("_Color")) _fadeMat.SetColor("_Color", cor);
        }

        private LookAtPenis _miraSuspensa;

        /// <summary>
        /// O LookAtPenis do jogo, no LateUpdate, aponta a genital para o alvo na parceira. Com o
        /// homem recuado ele continuava mirando nela e esticava a genital pela distancia toda.
        /// Enquanto o homem esta congelado, a pose guardada manda; depois o jogo volta a mirar.
        /// </summary>
        private void SuspendeMiraDaGenital(bool suspender)
        {
            try
            {
                if (suspender)
                {
                    if (_miraSuspensa != null) return;
                    var l = HScene.Instance?.LookAtDan;
                    if (l == null || !l.enabled) return;
                    l.enabled = false;
                    _miraSuspensa = l;
                    PluginLog.Info("[AmanatsuVR][H][PUT] genital auto-aim (LookAtPenis) suspended.");
                }
                else if (_miraSuspensa != null)
                {
                    _miraSuspensa.enabled = true;
                    _miraSuspensa = null;
                    PluginLog.Info("[AmanatsuVR][H][PUT] genital auto-aim handed back to the game.");
                }
            }
            catch { _miraSuspensa = null; }
        }

        private Vector3? _afastamento;

        /// <summary>
        /// Tiro com mira pelos olhos do homem: ele inteiro recua (~0,5 m) para longe dela, para
        /// caber o alvo na vista. A direcao e decidida uma vez na entrada; como o CongelaHomem
        /// devolve a raiz a pose guardada todo quadro, o deslocamento nunca acumula.
        /// </summary>
        private void AfastaHomem()
        {
            var homem = Ator(false);
            var raiz = homem?.Human?.Transform;
            if (raiz == null) return;
            if (!_afastamento.HasValue)
            {
                // Pelos quadris: as raizes dos dois costumam estar no mesmo ponto da cena, e quem
                // os separa e a animacao.
                Vector3 dir = Vector3.zero;
                var qh = Osso(homem, "cf_j_hips");
                var qm = Osso(Ator(true), "cf_j_hips");
                if (qh != null && qm != null) dir = qh.position - qm.position;
                dir.y = 0f;
                if (dir.sqrMagnitude < 1e-4f) { dir = -raiz.forward; dir.y = 0f; }
                _afastamento = dir.normalized * (PluginConfig.HAimManBackOff.Value * Escala(homem));
                PluginLog.Info($"[AmanatsuVR][H][PUT] man moved back {PluginConfig.HAimManBackOff.Value:F2} m"
                    + $" ({_afastamento.Value.magnitude:F2} units) to aim");
            }
            raiz.position += _afastamento.Value;
        }

        /// <summary>Reaplica a pose guardada por cima da animacao, todo quadro do tiro livre.</summary>
        private void CongelaHomem()
        {
            if (!_poseHomemValida || _ossosHomem == null) return;
            var homem = Ator(false);
            try
            {
                if (homem == null || homem.Human.Pointer != _poseHomemDe) return;
                for (int i = 0; i < _ossosHomem.Length; i++)
                {
                    var t = _ossosHomem[i];
                    if (t == null) continue;
                    t.localPosition = _poseHomemPos[i];
                    t.localRotation = _poseHomemRot[i];
                }
                // A pose guardada e a da penetracao, com a genital encurtada pela animacao (para nao
                // atravessar a parceira). Congelada assim, ela ficava "amassada" a mira inteira,
                // ja longe dela: durante a mira (homem recuado) o comprimento volta ao de repouso.
                // Depois do tiro ele volta para perto dela, e ai o encurtamento da pose e o certo -
                // em repouso a genital entrava no rosto dela.
                if (_emPut) foreach (var (t, pos) in GenitalEmRepouso(homem)) if (t != null) t.localPosition = pos;
            }
            catch { }
        }

        private List<(Transform t, Vector3 pos)> _genitalRepouso;
        private IntPtr _genitalRepousoDe;

        /// <summary>
        /// Posicao local de repouso de cada osso da genital, tirada dos bindposes da malha dela
        /// (o_dankon): local = bind[pai] * bind[osso]^-1. Mesmo metodo do Uncensor para medir o
        /// comprimento de repouso. Calculado uma vez por homem.
        /// </summary>
        private List<(Transform t, Vector3 pos)> GenitalEmRepouso(HActor homem)
        {
            IntPtr chave = homem.Human.Pointer;
            if (_genitalRepouso != null && chave == _genitalRepousoDe) return _genitalRepouso;
            _genitalRepousoDe = chave;
            _genitalRepouso = new List<(Transform, Vector3)>();
            try
            {
                foreach (var r in homem.Human.Transform.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var m = r.sharedMesh;
                    if (m == null || !m.name.StartsWith("o_dankon")) continue;
                    var ossos = r.bones; var bind = m.bindposes;
                    var indice = new Dictionary<IntPtr, int>();
                    for (int i = 0; i < ossos.Length && i < bind.Length; i++) if (ossos[i] != null) indice[ossos[i].Pointer] = i;
                    for (int i = 0; i < ossos.Length && i < bind.Length; i++)
                    {
                        var t = ossos[i];
                        if (t == null || !t.name.StartsWith("cf_j_dan") || t.parent == null) continue;
                        if (!indice.TryGetValue(t.parent.Pointer, out int ip)) continue;
                        Matrix4x4 local = bind[ip] * bind[i].inverse;
                        _genitalRepouso.Add((t, (Vector3)local.GetColumn(3)));
                    }
                    break;
                }
            }
            catch (Exception ex) { PluginLog.Warning($"[AmanatsuVR][H] genital rest: {ex.Message}"); }
            PluginLog.Info($"[AmanatsuVR][H] genital: {_genitalRepouso.Count} bones with rest length");
            return _genitalRepouso;
        }

        private static AL.H.Motion.Base.Controller SafeController(HScene hs)
        {
            try { return hs.Controller; } catch { return null; }
        }

        private void Entrou(HScene hs)
        {
            PluginLog.Info("[AmanatsuVR][H] H scene detected: free camera.");
            _modo = Modo.Livre;
            _livreIniciada = false;
            _livreMovida = false;
            _auto = false;
            _emPut = false;
            IniciaLivreNaCameraDoJogo(hs);
        }

        private void Saiu()
        {
            PluginLog.Info("[AmanatsuVR][H] H scene ended.");
            SoltaTiro();
            MostraCabeca();
            _ossosCabeca.Clear();
            _entreOlhos.Clear();
            _escalas.Clear();
            SuspendeMiraDaGenital(false);
            if (_fadeObj != null) _fadeObj.SetActive(false); // fora da H nada atualiza o fade
            MiraOrigem = null;
            _auto = false;
            _emPut = false;
            if (Cam != null) { Cam.PoseForcada = null; Cam.LockHeadToPose = false; }
            // O painel volta para a frente da pose de recentralizacao, como fora da H.
            Controlador?.UIScreen?.LinkToFront(Cam, PluginConfig.UIScreenDistance.Value);
        }

        // ---------------------------------------------------------------- entrada

        private void LeEntrada()
        {
            foreach (var m in _maos)
            {
                m.GatilhoAntes = m.Gatilho; m.GripAntes = m.Grip; m.CliqueAntes = m.Clique;
                m.DirecaoAntes = m.Direcao;

                m.Stick = SteamVRInput.ReadStick(m.Esquerda);
                // laser sobre lista rolavel: esse analogico esta rolando a lista
                if (VRControllerLaser.StickNaUI && m.Esquerda == (VRControllerLaser.ActiveRole == Valve.VR.ETrackedControllerRole.LeftHand))
                    m.Stick = Vector2.zero;
                m.Gatilho = SteamVRInput.ReadTrigger(m.Esquerda);
                m.Grip = SteamVRInput.ReadGrab(m.Esquerda);
                m.Clique = SteamVRInput.ReadStickClick(m.Esquerda);
                m.Direcao = DirecaoDe(m.Stick);

                if ((m.Gatilho && !m.GatilhoAntes) || (m.Grip && !m.GripAntes)) _maoAtivaEsquerda = m.Esquerda;
            }
        }

        private static int DirecaoDe(Vector2 s)
        {
            if (s.magnitude < 0.5f) return -1;
            if (Mathf.Abs(s.y) >= Mathf.Abs(s.x)) return s.y > 0 ? 0 : 1;
            return s.x < 0 ? 2 : 3;
        }

        private void ProcessaAtalhos(HScene hs, AL.H.Motion.Base.Controller ctrl, Mao m)
        {
            // Grip + gatilho: painel. Borda de subida do par, venha o botao que vier por ultimo.
            bool ambos = m.Gatilho && m.Grip;
            if (ambos && !m.AmbosAntes)
            {
                bool mostrar = !VRController.PainelVisivel;
                Controlador?.MostrarPainel(mostrar);
                if (mostrar) Controlador?.UIScreen?.LinkToHead(Cam, PluginConfig.UIScreenDistance.Value);
                Vibra(m);
            }
            m.AmbosAntes = ambos;

            // L3/R3 (Marcus): tap = free camera <-> first person; hold = automatic.
            if (m.Clique && !m.CliqueAntes) { m.ClickDesde = Time.unscaledTime; m.ClickConsumido = false; }
            if (m.Clique && !m.ClickConsumido && Time.unscaledTime - m.ClickDesde >= ClickHold)
            {
                m.ClickConsumido = true;
                _auto = !_auto;
                if (_auto) { _fase = Fase.Iniciar; _faseDesde = Time.unscaledTime; }
                PluginLog.Info($"[AmanatsuVR][H] auto={_auto}");
                AvisaAuto(_auto);
            }
            if (!m.Clique && m.CliqueAntes && !m.ClickConsumido)
            {
                MudaModo(_modo == Modo.Livre ? Modo.PrimeiraPessoa : Modo.Livre);
                Vibra(m, 0.15f);
            }

            if (_emPut) return; // no tiro livre o gatilho e o gatilho da arma

            // Gatilho + direcao (sem grip): dispara quando o par se forma, seja o gatilho ou a
            // direcao que chegou por ultimo.
            bool par = m.Gatilho && !m.Grip && m.Direcao >= 0;
            bool parAntes = m.GatilhoAntes && !m.GripAntes && m.DirecaoAntes == m.Direcao;
            if (par && !parAntes)
            {
                bool frente = m.Direcao == 0;
                if (frente && _modo == Modo.Livre) { DesligaAuto(); IniciaOuForte(hs, ctrl); Vibra(m); }
                else if (!frente) { DesligaAuto(); Finaliza(hs, m.Direcao == 2 ? 0 : m.Direcao == 1 ? 1 : 2); Vibra(m); }
            }

            // Segurar frente (primeira pessoa), sem gatilho: troca o ator. (Segurar tras saiu: a camera e no L3/R3.)
            bool frenteSolo = m.Direcao == 0 && _modo == Modo.PrimeiraPessoa;
            if (frenteSolo && !m.Grip)
            {
                if (m.SegurandoDesde < 0f)
                {
                    m.SegurandoDesde = Time.unscaledTime;
                    m.SeguraConsumida = false;
                    m.SeguraCancelada = m.Gatilho;
                }
                // O gatilho durante a contagem e o atalho de finalizacao: nao pode trocar a camera.
                if (m.Gatilho) m.SeguraCancelada = true;

                if (!m.SeguraConsumida && !m.SeguraCancelada
                    && Time.unscaledTime - m.SegurandoDesde >= PluginConfig.HHoldTime.Value)
                {
                    m.SeguraConsumida = true;
                    _atorMulher = !_atorMulher; _yawRef = float.NaN; MostraCabeca(); PluginLog.Info($"[AmanatsuVR][H] first person: {(_atorMulher ? "woman" : "man")}");
                    Vibra(m, 0.15f);
                }
            }
            else if (m.SegurandoDesde >= 0f)
            {
                // Soltou a frente antes do tempo, em primeira pessoa: e o toque curto de iniciar/forte.
                bool eraFrente = m.DirecaoAntes == 0 && _modo == Modo.PrimeiraPessoa;
                if (eraFrente && !m.SeguraConsumida && !m.SeguraCancelada && !m.Grip)
                {
                    DesligaAuto();
                    IniciaOuForte(hs, ctrl);
                    Vibra(m);
                }
                m.SegurandoDesde = -1f;
            }
        }

        private const float ClickHold = 0.6f;

        private static void Vibra(Mao m, float dur = 0.05f) => SteamVRInput.Haptic(m.Esquerda, dur, 0.6f);

        private void DesligaAuto()
        {
            if (!_auto) return;
            _auto = false;
            PluginLog.Info("[AmanatsuVR][H] auto turned off by manual shortcut.");
            AvisaAuto(false);
        }

        // Pulsos agendados (instante, duracao, forca) nas duas maos. Um Haptic longo so nao
        // se distingue dos toques de atalho; o padrao e o que diz liga/desliga.
        private readonly List<(float quando, float dur, float forca)> _pulsos = new();

        /// <summary>Ligou: tres toques fortes e rapidos. Desligou: um toque longo e fraco.</summary>
        private void AvisaAuto(bool ligou)
        {
            float t = Time.unscaledTime;
            _pulsos.Clear();
            if (ligou) for (int i = 0; i < 3; i++) _pulsos.Add((t + i * 0.18f, 0.08f, 1f));
            else _pulsos.Add((t, 0.6f, 0.35f));
        }

        private void TocaPulsos()
        {
            for (int i = _pulsos.Count - 1; i >= 0; i--)
            {
                if (Time.unscaledTime < _pulsos[i].quando) continue;
                SteamVRInput.Haptic(true, _pulsos[i].dur, _pulsos[i].forca);
                SteamVRInput.Haptic(false, _pulsos[i].dur, _pulsos[i].forca);
                _pulsos.RemoveAt(i);
            }
        }

        // ---------------------------------------------------------------- camera

        private void IniciaLivreNaCameraDoJogo(HScene hs)
        {
            Transform t = null;
            try { t = hs.MainCamera != null ? hs.MainCamera.transform : null; } catch { }
            if (t == null && Cam != null && Cam.TargetCamera != null) t = Cam.TargetCamera.transform;
            if (t == null) return;

            _livrePos = t.position;
            _livreYaw = t.eulerAngles.y;
            _livreIniciada = true;
        }

        private void MoveLivre()
        {
            if (!_livreIniciada || Cam == null || Cam.VR == null || Cam.VR.head == null) return;

            float dt = Time.unscaledDeltaTime;
            // metros percebidos: a escala do rig converte para unidades do mundo
            float speed = PluginConfig.MoveSpeed.Value * Cam.VR.origin.localScale.x;
            foreach (var m in _maos)
            {
                Vector2 s = m.Stick;
                if (s.magnitude < 0.15f || m.Gatilho) continue; // gatilho + analogico e atalho
                _livreMovida = true;

                if (m.Grip)
                {
                    _livreYaw += s.x * PluginConfig.TurnSpeed.Value * dt;
                    _livrePos.y += s.y * speed * dt;
                }
                else
                {
                    Quaternion yaw = Quaternion.Euler(0f, Cam.VR.head.rotation.eulerAngles.y, 0f);
                    _livrePos += yaw * new Vector3(s.x, 0f, s.y) * (speed * dt);
                }
            }
        }

        private void MudaModo(Modo novo)
        {
            if (novo == _modo) return;
            Cam?.ResetForcedHeadRef(); // o novo modo parte da altura real da cabeca agora
            _yawRef = float.NaN;

            if (novo == Modo.Livre)
            {
                // Sai da cabeca exatamente onde estava, para a troca nao dar um salto.
                var olhos = PoseDosOlhos();
                if (olhos.HasValue)
                {
                    _livrePos = olhos.Value.position;
                    _livreYaw = olhos.Value.rotation.eulerAngles.y;
                    _livreIniciada = true;
                    _livreMovida = true;
                }
                MostraCabeca();
            }
            _modo = novo;
            PluginLog.Info($"[AmanatsuVR][H] mode={novo}{(novo == Modo.PrimeiraPessoa ? (_atorMulher ? " (woman)" : " (man)") : "")}");
        }

        private HActor Ator(bool mulher)
        {
            try
            {
                var atores = HScene.Instance.Actors;
                if (atores == null) return null;
                for (int i = 0; i < atores.Length; i++)
                {
                    var a = atores[i];
                    if (a != null && a.Human != null && a.IsMan != mulher) return a;
                }
            }
            catch { }
            return null;
        }

        private HActor AtorDaPrimeiraPessoa() => Ator(_atorMulher) ?? Ator(!_atorMulher);

        private static Transform Osso(HActor a, string nome)
        {
            try
            {
                var raiz = a?.Human?.Transform;
                if (raiz == null) return null;
                foreach (var t in raiz.GetComponentsInChildren<Transform>(true))
                    if (string.Equals(t.name, nome, StringComparison.OrdinalIgnoreCase)) return t;
            }
            catch { }
            return null;
        }

        private readonly Dictionary<IntPtr, (Transform pescoco, Transform cabeca)> _ossosCabeca = new();

        private (Transform pescoco, Transform cabeca) OssosCabeca(HActor a)
        {
            var raiz = a?.Human?.Transform;
            if (raiz == null) return (null, null);
            if (_ossosCabeca.TryGetValue(raiz.Pointer, out var c) && c.pescoco != null) return c;
            c = (Osso(a, "cf_j_neck"), Osso(a, "cf_j_head"));
            _ossosCabeca[raiz.Pointer] = c;
            PluginLog.Info($"[AmanatsuVR][H] bones of '{a.Human.GameObject?.name}': neck={(c.pescoco != null)} head={(c.cabeca != null)}");
            return c;
        }

        /// <summary>
        /// Onde ficam os olhos do ator e para onde ele olha (sem rolagem; o resto e do HMD).
        /// Com o pescoco encolhido o osso da cabeca e puxado para o pescoco, entao a pose real
        /// sai da conta pescoco + offset local da cabeca, que o encolhimento nao altera.
        /// </summary>
        private (Vector3 position, Quaternion rotation)? PoseDosOlhos()
        {
            var ator = AtorDaPrimeiraPessoa();
            var (pescoco, cabeca) = OssosCabeca(ator);
            if (pescoco == null || cabeca == null) return null;
            Vector3? entreOlhos = EntreOlhos(ator, cabeca); // medido com a cabeca ainda no tamanho real
            EscondeCabeca(pescoco, cabeca); // antes: trocar de ator passa pelo MostraCabeca, que zera o ator
            try { HumanPrimeiraPessoa = ator.Human.Pointer; } catch { HumanPrimeiraPessoa = IntPtr.Zero; }

            float escala = pescoco.parent != null ? pescoco.parent.lossyScale.y : 1f;
            Vector3 posCabeca = pescoco.position
                + pescoco.rotation * (Vector3.Scale(cabeca.localPosition, _escalaPescoco) * escala);
            Quaternion rotCabeca = pescoco.rotation * cabeca.localRotation;
            if (_cabecaFisica != null) _cabecaFisica.SetPositionAndRotation(posCabeca, rotCabeca);

            Quaternion alvo = OrientacaoSemRolagem(rotCabeca);

            // Suaviza: a cabeca balanca a cada estocada, e isso direto no rig enjoa.
            float k = 1f - Mathf.Exp(-PluginConfig.HHeadSmoothing.Value * Time.unscaledDeltaTime);
            _rotOlhos = _rotOlhosValida ? Quaternion.Slerp(_rotOlhos, alvo, k) : alvo;
            _rotOlhosValida = true;

            // Ponto entre os dois olhos, no referencial da cabeca. Sem a medida (olhos nao
            // achados), cai no deslocamento fixo antigo. OlhosAcima/OlhosFrente somam por cima.
            Vector3 olhos;
            float esc = Escala(ator);
            Vector3 ajuste = new Vector3(0f, PluginConfig.HEyeOffsetUp.Value, PluginConfig.HEyeOffsetForward.Value) * esc;
            if (entreOlhos.HasValue)
                olhos = posCabeca + rotCabeca * (entreOlhos.Value + ajuste);
            else
            {
                Quaternion yaw = Quaternion.Euler(0f, _rotOlhos.eulerAngles.y, 0f);
                // 6 cm acima ficava na altura da boca (Marcus, em beijos); 10 cm e a linha dos olhos.
                olhos = posCabeca + Vector3.up * (0.10f * esc + ajuste.y) + yaw * Vector3.forward * (0.09f * esc + ajuste.z);
            }
            return (olhos, _rotOlhos);
        }

        private readonly Dictionary<IntPtr, float> _escalas = new();

        /// <summary>
        /// Quantas unidades do mundo vale um metro no corpo deste ator. Os personagens sao
        /// modelados em escala x10 (Human.MODEL_SCALE): tudo em metros (olhos, recuo, avanco)
        /// saia dez vezes menor - o recuo de 0,5 virava 5 cm. Medido pelo esqueleto: do quadril
        /// ao pescoco sao ~0,5 m num corpo real.
        /// </summary>
        private float Escala(HActor a)
        {
            IntPtr chave;
            try { chave = a.Human.Pointer; } catch { return 1f; }
            if (_escalas.TryGetValue(chave, out float e)) return e;

            e = 1f;
            var q = Osso(a, "cf_j_hips");
            var p = Osso(a, "cf_j_neck");
            if (q != null && p != null) e = Mathf.Clamp(Vector3.Distance(q.position, p.position) / 0.5f, 0.1f, 100f);
            _escalas[chave] = e;
            PluginLog.Info($"[AmanatsuVR][H] scale of '{a.Human.GameObject?.name}': 1 m = {e:F2} units");
            return e;
        }

        // Por Human: meio dos olhos em coordenadas da cabeca (rotacao da cabeca, escala do mundo).
        private readonly Dictionary<IntPtr, Vector3?> _entreOlhos = new();

        /// <summary>
        /// Centro dos dois globos oculares (HumanFace.rendEye) relativo ao osso da cabeca.
        /// Medido uma vez por ator e antes da primeira vez que a cabeca e escondida: com o
        /// pescoco em escala minima os bounds colapsam e a medida nao vale mais.
        /// </summary>
        private Vector3? EntreOlhos(HActor ator, Transform cabeca)
        {
            IntPtr chave;
            try { chave = ator.Human.Pointer; } catch { return null; }
            if (_entreOlhos.TryGetValue(chave, out var v)) return v;

            // Os ossos dos olhos que o proprio olhar do jogo gira (EyeNeck._eyeTrs). Os bounds de
            // HumanFace.rendEye nao servem: malha com skinning, saiu 5,5 m abaixo da cabeca.
            // O EyeNeck._eyeTrs sao os ALVOS do olhar (EyeTargetL/R, ~45 cm a frente), nao os
            // olhos. Os olhos sao ossos sob a cabeca com "eye" no nome e lado L/R: pega o par
            // mais perto do osso da cabeca, descartando alvos (target/look).
            v = null;
            string origem = "sem ossos de olho";
            try
            {
                Transform esq = null, dir = null;
                float de = float.MaxValue, dd = float.MaxValue;
                foreach (var t in cabeca.GetComponentsInChildren<Transform>(true))
                {
                    string n = t.name.ToLowerInvariant();
                    if (!n.Contains("eye") || n.Contains("target") || n.Contains("look")) continue;
                    float dist = (t.position - cabeca.position).magnitude;
                    if (dist > 0.3f * Escala(ator)) continue;
                    if (n.EndsWith("_l") || n.EndsWith("l")) { if (dist < de) { de = dist; esq = t; } }
                    else if (n.EndsWith("_r") || n.EndsWith("r")) { if (dist < dd) { dd = dist; dir = t; } }
                }
                if (esq != null && dir != null)
                {
                    Vector3 meio = (esq.position + dir.position) * 0.5f;
                    v = Quaternion.Inverse(cabeca.rotation) * (meio - cabeca.position);
                    origem = $"'{esq.name}'+'{dir.name}' = {v.Value:F3}";
                }
            }
            catch (Exception ex) { origem = ex.Message; }
            _entreOlhos[chave] = v;
            PluginLog.Info($"[AmanatsuVR][H] between the eyes of '{ator.Human.GameObject?.name}': {origem}"
                + $"{(v.HasValue ? "" : " -> uses fixed offset")}");
            return v;
        }

        /// <summary>
        /// Numa cabeca de verdade os olhos ficam a frente e acima do pivo do pescoco, entao
        /// abaixar a cabeca os leva para frente. Com o ponto de vista preso aos olhos do ator
        /// isso nao acontece e, olhando para baixo, aparece o corte do pescoco. Avanca na
        /// direcao do olhar em parabola: t^2, com t = quanto o HMD esta abaixado (0 reto, 1 a 75°).
        /// </summary>
        private Vector3 AvancoAoAbaixar()
        {
            float max = PluginConfig.HLookDownForward.Value;
            var head = Cam?.VR?.head;
            var origin = Cam?.VR?.origin;
            if (max <= 0f || head == null || origin == null) return Vector3.zero;

            // Inclinacao do HMD relativa ao rig (o rig ja inclina com a cabeca do ator).
            Vector3 frenteLocal = Quaternion.Inverse(origin.rotation) * head.forward;
            float abaixado = -Mathf.Asin(Mathf.Clamp(frenteLocal.y, -1f, 1f)) * Mathf.Rad2Deg;
            float t = Mathf.Clamp01(abaixado / 75f);
            if (t <= 0f) return Vector3.zero;

            // Direcao do olhar no plano do rig; olhando reto para baixo a frente some, e quem
            // aponta "para frente" e o topo do HMD.
            Vector3 cima = origin.up;
            Vector3 dir = Vector3.ProjectOnPlane(head.forward + head.up, cima);
            if (dir.sqrMagnitude < 1e-6f) return Vector3.zero;
            return dir.normalized * (max * Escala(AtorDaPrimeiraPessoa()) * t * t);
        }

        private Quaternion _rotOlhos = Quaternion.identity;
        private bool _rotOlhosValida = false;

        /// <summary>
        /// Para onde a cabeca olha, sem rolagem (horizonte sempre reto).
        ///
        /// So com yaw, deitada de costas (ou arqueando a cabeca no climax) a frente da cabeca
        /// aponta para cima: a projecao horizontal fica curta e vira para tras, e a visao caia
        /// no chao. Por isso a inclinacao (pitch) tambem vem da cabeca. Quando a frente esta
        /// quase vertical, o yaw sai do topo da cabeca, que e o que fica "para cima" na tela.
        /// </summary>
        // Heading (yaw) of the last frame that did not pass the vertical; NaN = none yet (new actor/mode).
        private float _yawRef = float.NaN;
        private float _frenteYAntes;
        private bool _yawTravado;

        private Quaternion OrientacaoSemRolagem(Quaternion rotCabeca)
        {
            Vector3 frente = rotCabeca * Vector3.forward;
            if (!PluginConfig.HFollowHeadTilt.Value)
            {
                frente.y = 0f;
                return Quaternion.LookRotation(frente.sqrMagnitude > 1e-6f ? frente.normalized : Vector3.forward, Vector3.up);
            }

            // Sem rolagem (yaw + pitch) e singular na vertical: quando a cabeca passa da vertical (arqueia
            // para tras), a direcao horizontal inverte e o yaw salta 180 graus. A correcao anterior
            // misturava com a rotacao inteira da cabeca perto da vertical - mas arqueada para tras ela tem
            // 180 graus de rolagem, e a mistura deixava a camera de ponta-cabeca (Marcus, F9 em outra pose).
            // Agora nunca ha rolagem: se o yaw inverter com a frente quase vertical, e a cabeca passando da
            // vertical - fica o yaw anterior com a mesma elevacao (espelho da passagem, continuo no topo),
            // ate ela voltar. Pitch limitado a 89: o "cima" da camera (cos pitch) nunca aponta para baixo.
            // MEASURED (F9, homem deitado de costas olhando para cima): a direcao horizontal vinha so da frente
            // projetada no chao, minuscula nessa pose - 3 graus da cabeca para tras a invertiam, e a trava acima
            // guardava a invertida. Olhando para cima/baixo, quem diz o "cima" da visao e o topo da cabeca;
            // a frente pesa (1 - |y|), para nao brigar com ele quando passa da vertical (orient_sim3: sem
            // inversao nos 7 casos, salto max 2,2 graus/quadro).
            Vector3 topo = rotCabeca * Vector3.up;
            Vector3 plano = (1f - Mathf.Abs(frente.y)) * new Vector3(frente.x, 0f, frente.z)
                - frente.y * new Vector3(topo.x, 0f, topo.z);
            float pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(frente.y, -1f, 1f)) * Mathf.Rad2Deg, -89f, 89f);
            float yaw = plano.sqrMagnitude > 1e-8f ? Mathf.Atan2(plano.x, plano.z) * Mathf.Rad2Deg : _yawRef;
            if (float.IsNaN(yaw)) yaw = 0f;
            // A trava so vale para a cabeca passando pela vertical (quadro anterior quase vertical, ou ja
            // travada). Troca de pose/estado (cansada, acelerar para finalizar) salta o yaw sem passar
            // pela vertical; travar ai prendia a camera na direcao velha enquanto a cabeca seguia inclinada.
            float yAntes = _frenteYAntes;
            _frenteYAntes = Mathf.Abs(frente.y);
            bool passando = _yawTravado || yAntes > 0.9f;
            if (passando && !float.IsNaN(_yawRef) && Mathf.Abs(frente.y) > 0.7f && Mathf.Abs(Mathf.DeltaAngle(_yawRef, yaw)) > 90f)
            { yaw = _yawRef; _yawTravado = true; }
            else { _yawRef = yaw; _yawTravado = false; }
            return Quaternion.Euler(pitch, yaw, 0f);
        }

        /// <summary>
        /// Esconde a cabeca encolhendo o pescoco: some do pescoco para cima, inclusive a parte
        /// do corpo, e nao se ve o lado de dentro (so desligar o desenho da cabeca deixava o
        /// buraco do pescoco a mostra). O encolhimento levaria junto os colisores do rosto, e o
        /// jato atravessaria a cabeca; por isso eles saem da cabeca e ficam num substituto,
        /// irmao do pescoco (continua dentro do Human, que e por onde o jogo sabe de quem e o
        /// colisor), movido todo quadro para a pose que a cabeca teria sem o encolhimento.
        /// </summary>
        private void EscondeCabeca(Transform pescoco, Transform cabeca)
        {
            if (_pescocoOculto != pescoco)
            {
                MostraCabeca();
                _pescocoOculto = pescoco;
                _escalaPescoco = pescoco.localScale; // a escala original volta intacta
                SeparaColisores(pescoco, cabeca);    // ainda sem encolher: pose e escala reais
            }
            // Todo quadro: se a animacao escrever escala no pescoco, a nossa vem depois. O que ela
            // escreveu e a escala real deste quadro (muda com a pose/estado); guardada, nao fica velha.
            Vector3 atual = pescoco.localScale;
            if (atual != Vector3.one * EscalaOculta) _escalaPescoco = atual;
            pescoco.localScale = Vector3.one * EscalaOculta;
        }

        private void SeparaColisores(Transform pescoco, Transform cabeca)
        {
            try
            {
                _cabecaFisica = new GameObject("AmanatsuVR_CabecaFisica").transform;
                _cabecaFisica.SetParent(pescoco.parent, false);
                _cabecaFisica.SetPositionAndRotation(cabeca.position, cabeca.rotation);
                _cabecaFisica.localScale = Vector3.Scale(cabeca.localScale, pescoco.localScale);

                foreach (var c in cabeca.GetComponentsInChildren<Collider>(true))
                {
                    var t = c.transform;
                    if (t == cabeca || _colisores.Exists(x => x.t == t)) continue;
                    _colisores.Add((t, t.parent, t.localPosition, t.localRotation, t.localScale));
                    t.SetParent(_cabecaFisica, true);
                }
                PluginLog.Info($"[AmanatsuVR][H] head hidden; {_colisores.Count} head colliders kept at real size");
            }
            catch (Exception ex) { PluginLog.Warning($"[AmanatsuVR][H] head colliders: {ex.Message}"); }
        }

        private void MostraCabeca()
        {
            if (_pescocoOculto != null) { try { _pescocoOculto.localScale = _escalaPescoco; } catch { } }
            foreach (var (t, pai, pos, rot, esc) in _colisores)
            {
                try
                {
                    if (t == null || pai == null) continue;
                    t.SetParent(pai, false);
                    t.localPosition = pos; t.localRotation = rot; t.localScale = esc;
                }
                catch { }
            }
            _colisores.Clear();
            if (_cabecaFisica != null) { try { UnityEngine.Object.Destroy(_cabecaFisica.gameObject); } catch { } }
            _cabecaFisica = null;
            _pescocoOculto = null;
            _escalaPescoco = Vector3.one;
            HumanPrimeiraPessoa = IntPtr.Zero;
            _rotOlhosValida = false; // outro ator ou saida: nao suavizar a partir da cabeca anterior
        }

        // ---------------------------------------------------------------- acoes do jogo

        private static bool EmRepouso(AL.H.Motion.Base.Controller ctrl)
        {
            string nome = NomeEstado(ctrl);
            if (nome == "Put" || nome == "SPut")
            {
                // Depois do PourEnd o estado continua Put (so o IsPutNow desliga): e o parado
                // pos-tiro, nao transicao. Tratar como transicao travava o "iniciar" ali.
                bool tiro = true;
                try { tiro = ctrl.IsPutNow; } catch { }
                return !tiro;
            }
            return nome == "" || nome.StartsWith("Idle") || nome.StartsWith("SIdle") || nome == "InsertIdle";
        }

        /// <summary>No vaivem (WLoop, WSPLoop, SSPLoop): so ai o ato esta rodando e o forte faz sentido.</summary>
        private static bool EmMovimento(AL.H.Motion.Base.Controller ctrl) => NomeEstado(ctrl).Contains("Loop");

        private static string NomeEstado(AL.H.Motion.Base.Controller ctrl)
        {
            try { return ctrl?.State?.GetIl2CppType().Name ?? ""; } catch { return ""; }
        }

        /// <summary>Parado: um degrau da roda (e o que o jogo le para comecar). Em movimento: o botao de forte.</summary>
        private void IniciaOuForte(HScene hs, AL.H.Motion.Base.Controller ctrl)
        {
            string estado = NomeEstado(ctrl);
            if (EmRepouso(ctrl))
            {
                // Na insercao sao dois degraus, como no mouse: Idle -> Insert -> InsertIdle -> loop.
                PluginLog.Info($"[AmanatsuVR][H] start: mouse wheel (state={estado} focus={Application.isFocused})");
                EntradaFisica.Roda(1);
                return;
            }
            // Transicao (Insert, orgasmo, Put...): nem roda nem forte, senao um segundo toque
            // durante a insercao ligava o forte antes de o vaivem comecar.
            if (!EmMovimento(ctrl)) { PluginLog.Info($"[AmanatsuVR][H] start/strong ignored during transition (state={estado})"); return; }

            bool atacado = true;
            try { atacado = ctrl.IsAttacked; } catch { }
            if (atacado) { PluginLog.Info($"[AmanatsuVR][H] strong already active (state={estado})"); return; }

            Selectable botao = null;
            try { botao = hs.Option._buttonAttack._selectable; } catch { }
            PluginLog.Info($"[AmanatsuVR][H] strong: button={(botao != null ? botao.name : "-")} state={estado}");
            Clica(botao != null ? botao.gameObject : null);
        }

        private List<(float x, GameObject go, int indice)> BotoesDeFinalizacao(HScene hs)
        {
            var lista = new List<(float, GameObject, int)>();
            // Primeira pessoa da mulher: nada de mira. O _finishes[3] (Shot) e o que abre o tiro
            // livre; sem ele os atalhos e o automatico usam o climax normal, com a animacao do jogo.
            bool semMira = _modo == Modo.PrimeiraPessoa && _atorMulher;
            try
            {
                var fins = hs.GaugeController._finishes;
                for (int i = 0; i < fins.Length; i++)
                {
                    if (semMira && i == IndiceShot) continue;
                    var b = fins[i]?._button;
                    if (b == null || !b.gameObject.activeInHierarchy || !b.interactable) continue;
                    lista.Add((b.transform.position.x, b.gameObject, i));
                }
            }
            catch (Exception ex) { PluginLog.Warning($"[AmanatsuVR][H] finish buttons: {ex.Message}"); }
            lista.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return lista;
        }

        /// <summary>GaugeController._finishes: 0 Male, 1 Female, 2 Double, 3 Shot (tiro livre com mira).</summary>
        private const int IndiceShot = 3;

        /// <summary>posicao 0/1/2 = 1o/2o/3o botao disponivel da esquerda para a direita.</summary>
        private bool Finaliza(HScene hs, int posicao)
        {
            var lista = BotoesDeFinalizacao(hs);
            PluginLog.Info($"[AmanatsuVR][H] finish position={posicao}: available=[{string.Join(",", lista.ConvertAll(b => $"{b.indice}:{b.go.name}"))}]");
            if (posicao < 0 || posicao >= lista.Count) return false;
            Clica(lista[posicao].go);
            return true;
        }

        private static void Clica(GameObject go)
        {
            if (go == null || EventSystem.current == null)
            {
                PluginLog.Warning($"[AmanatsuVR][H] click without target (go={(go != null)} eventSystem={(EventSystem.current != null)})");
                return;
            }
            try
            {
                var dados = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
                ExecuteEvents.Execute(go, dados, ExecuteEvents.pointerClickHandler);
            }
            catch (Exception ex) { PluginLog.Warning($"[AmanatsuVR][H] click on '{go.name}' failed: {ex.Message}"); }
        }

        // ---------------------------------------------------------------- tiro livre

        private void EntrouPut(HScene hs)
        {
            _modoAntesDoPut = _modo;
            MudaModo(Modo.PrimeiraPessoa);
            Controlador?.MostrarPainel(false);

            _tiroAuto = _atorMulher || _auto;
            _congelaHomem = !_atorMulher;
            _afastamento = null;
            Fade(); // a camera salta para a 1a pessoa e, na do homem, ele recua
            _tiroAutoAcabou = false;
            _alvoAuto = null;
            if (_tiroAuto)
            {
                var alvos = new[] { "cf_j_head", "cf_j_bust01_L", "cf_j_bust01_R", "cf_j_spine02", "cf_j_spine01", "cf_j_thigh00_L", "cf_j_thigh00_R" };
                var mulher = Ator(true);
                for (int n = 0; n < 5 && _alvoAuto == null; n++)
                    _alvoAuto = Osso(mulher, alvos[UnityEngine.Random.Range(0, alvos.Length)]);
                _tiroAutoFim = Time.unscaledTime + 1.0f + PluginConfig.HAutoShotTime.Value;
            }

            try { _teclaTiro = FluidFlowManager.Instance._key; } catch { _teclaTiro = KeyCode.None; }
            _logouEmissor = false;
            _putDesde = Time.unscaledTime;
            _logsPut = 0;
            LogCena("entrada");
            PluginLog.Info($"[AmanatsuVR][H] free shot: {(_tiroAuto ? $"auto on target '{(_alvoAuto != null ? _alvoAuto.name : "-")}'" : "aim by hand")} key={_teclaTiro}");
        }

        private void SaiuPut()
        {
            SoltaTiro();
            MiraOrigem = null;
            // A raiz do homem volta ao lugar: ela nao e animada, o recuo ficaria para sempre.
            if (_afastamento.HasValue)
            {
                try
                {
                    var raiz = Ator(false)?.Human?.Transform;
                    if (raiz != null && _ossosHomem != null && _ossosHomem.Length > 0 && _ossosHomem[0] == raiz)
                        raiz.localPosition = _poseHomemPos[0];
                    else if (raiz != null) raiz.position -= _afastamento.Value;
                }
                catch { }
                _afastamento = null;
            }
            // MEDIDO (disassembly): quem volta a mostrar o homem (VisibleAll = 1) e o PourRestore, nao
            // o PourEnd - depois do tiro o jogo o mantem escondido, na pose do estado de tiro, que nao e
            // para ser vista (o homem "dentro" dela). Se fomos nos que o mostramos, devolve o escondido;
            // o jogo o reexibe quando for a hora.
            if (_homemReexibido)
            {
                _homemReexibido = false;
                try { var h = Ator(false); if (h != null) h.Active = false; } catch { }
                PluginLog.Info("[AmanatsuVR][H][PUT] man handed back to the game's 'hidden' state until it shows him again.");
            }
            Fade(); // o homem volta do recuo e a camera ao modo anterior
            MudaModo(_modoAntesDoPut);
            // Depois do tiro a cena fica parada esperando o jogador (Marcus: "fica estranho ele parado a
            // distancia"): o painel abre sozinho a frente, para ficar claro que e hora de agir.
            if (!_auto)
            {
                Controlador?.MostrarPainel(true);
                Controlador?.UIScreen?.LinkToHead(Cam, PluginConfig.UIScreenDistance.Value);
            }
            PluginLog.Info("[AmanatsuVR][H] free shot ended.");
        }

        private float _putDesde;
        private int _logsPut;
        private bool _homemReexibido; // fomos nos que tornamos o homem visivel neste tiro

        /// <summary>
        /// No modo janela o jogador E a camera, entao o jogo esconde o homem no tiro livre
        /// (Active = Human.VisibleAll). Em VR ele volta a aparecer, mas so com uma pose guardada
        /// para congelar: a animacao dele nesse estado nao foi feita para ser vista.
        /// </summary>
        private void MantemAtoresVisiveis(HScene hs)
        {
            if (!_poseHomemValida || !_congelaHomem) return;
            try
            {
                var a = Ator(false);
                if (a == null || a.Active) return;
                a.Active = true;
                _homemReexibido = true;
                PluginLog.Info($"[AmanatsuVR][H][PUT] '{a.Human?.GameObject?.name}' hidden by the game during the free shot; visible and frozen in the last pose.");
            }
            catch { }
        }

        private void AtualizaPut(HScene hs)
        {
            MantemAtoresVisiveis(hs);
            if (_logsPut < 2 && Time.unscaledTime - _putDesde > (_logsPut == 0 ? 1f : 3f))
            {
                _logsPut++;
                LogCena($"{Time.unscaledTime - _putDesde:F0}s");
            }

            if (_tiroAuto)
            {
                MiraOrigem = null;
                // Um segundo para a camera assentar, atira, e encerra pelo botao do jogo.
                float agora = Time.unscaledTime;
                Atira(!_tiroAutoAcabou && agora > _tiroAutoFim - PluginConfig.HAutoShotTime.Value && agora < _tiroAutoFim);
                if (agora >= _tiroAutoFim) _tiroAutoAcabou = true;
                // Repete a cada 2 s: se o botao ainda nao estiver clicavel, o Put nunca fecharia.
                if (agora >= _tiroAutoFim + 0.5f)
                {
                    _tiroAutoFim = agora + 1.5f;
                    Button fim = null;
                    try { fim = hs.GaugeController._buttonEndPut; } catch { }
                    PluginLog.Info($"[AmanatsuVR][H] automatic shot: ending ({(fim != null ? fim.name : "no button")})");
                    Clica(fim != null ? fim.gameObject : null);
                }
                return;
            }

            // Mira pela mao ativa; com o painel aberto o gatilho e do ponteiro.
            var mao = Laser?.GetAnchor(_maoAtivaEsquerda);
            if (mao == null || VRController.PainelVisivel) { MiraOrigem = null; Atira(false); return; }

            MiraOrigem = mao.position;
            MiraFim = PontoMirado(mao.position, mao.forward);
            var m = _maos[_maoAtivaEsquerda ? 0 : 1];
            Atira(m.Gatilho && !m.Grip);
        }

        /// <summary>
        /// Onde o jato cairia: o primeiro colisor do Obi da mulher no raio (sao eles que o fluido
        /// acerta). Os outros colisores dela (volumes de toque, gatilhos de interacao) sao
        /// invisiveis e maiores que o corpo - paravam o ponteiro na frente dela. Alcance e ponto
        /// sem acerto em metros reais: em unidades (5 e 1,5) davam 50 e 15 cm nesta escala x10,
        /// e o ponteiro parava colado na mao.
        /// </summary>
        private Vector3 PontoMirado(Vector3 origem, Vector3 dir)
        {
            var atorMulher = Ator(true);
            var homem = Ator(false)?.Human?.Transform; // o proprio corpo (e a cabeca fisica) nao e alvo
            float esc = atorMulher != null ? Escala(atorMulher) : 1f;
            Vector3 semAcerto = origem + dir * (2f * esc);
            try
            {
                var hits = Physics.RaycastAll(origem, dir, 5f * esc, ~0, QueryTriggerInteraction.Collide);
                float melhor = float.MaxValue; Vector3 ponto = semAcerto;
                foreach (var h in hits)
                {
                    if (h.collider == null || h.distance >= melhor) continue;
                    if (homem != null && h.collider.transform.IsChildOf(homem)) continue;
                    if (h.collider.GetComponent<Obi.ObiCollider>() == null) continue;
                    melhor = h.distance; ponto = h.point;
                }
                return ponto;
            }
            catch { return semAcerto; }
        }

        /// <summary>
        /// No jogo o emissor e filho da camera (HScene/MainCamera/FF Solver/Siru Emitter): no
        /// modo janela o jato sai da tela, do ponto de vista de quem joga. Em VR a camera do jogo
        /// fica parada onde o jogo a pos (a uns 8 m da cena), entao o jato vinha do ceu. Aqui ele
        /// sai da ponta da genital do homem: a genital gira para o alvo (limitada), e o jato sai
        /// no eixo dela, que e para onde o conteudo voa. Roda no LateUpdate, depois da animacao.
        /// </summary>
        private void ApontaEmissor()
        {
            Transform em = null;
            try { em = FluidFlowManager.Instance._emitter?.transform; } catch { }
            if (em == null) return;

            Vector3? alvo = _tiroAuto ? (_alvoAuto != null ? _alvoAuto.position : (Vector3?)null)
                                      : (MiraOrigem.HasValue ? MiraFim : (Vector3?)null);

            var (raiz, ponta) = OssosPenis();

            if (!_logouEmissor)
            {
                _logouEmissor = true;
                string caminho = em.name;
                for (var p = em.parent; p != null; p = p.parent) caminho = p.name + "/" + caminho;
                PluginLog.Info($"[AmanatsuVR][H] emitter '{caminho}' pos={em.position} forward={em.forward} target={alvo}"
                    + $" genital={(raiz != null ? raiz.name : "-")}->{(ponta != null ? ponta.name : "-")}"
                    + $"{(ponta != null ? $" tip={ponta.position}" : "")}");
            }

            // A pose nao e tocada (girar a genital para a mira ficou estranho): o jato so sai da
            // ponta e vai reto ao alvo. Homem congelado (primeira pessoa dele): a ponta viva.
            // Senao o jogo o esconde e a pose dele no tiro nao vale; usa onde a ponta estava no
            // ultimo quadro antes do tiro.
            Vector3? origem = _congelaHomem && ponta != null ? ponta.position : _pontaAntesDoTiro;
            if (origem.HasValue) em.position = origem.Value;

            if (!alvo.HasValue) return;
            Vector3 d = alvo.Value - em.position;
            if (d.sqrMagnitude > 1e-6f) em.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
        }

        /// <summary>Primeira pessoa do homem no tiro: ele fica visivel e congelado. Na da mulher, o jogo segue.</summary>
        private bool _congelaHomem;
        private Vector3? _pontaAntesDoTiro;

        private (Transform raiz, Transform ponta) _penis;
        private IntPtr _penisDe;

        private (Transform raiz, Transform ponta) OssosPenis()
        {
            var homem = Ator(false);
            IntPtr chave = IntPtr.Zero;
            try { chave = homem?.Human?.Pointer ?? IntPtr.Zero; } catch { }
            if (chave == IntPtr.Zero) return (null, null);
            if (chave == _penisDe && _penis.raiz != null && _penis.ponta != null) return _penis;

            _penisDe = chave;
            _penis = (Osso(homem, "cf_j_dan100_00") ?? Osso(homem, "cf_j_dan101_00"), Osso(homem, "cf_j_dan109_00"));
            return _penis;
        }

        /// <summary>
        /// Diagnostico do tiro livre: quem esta visivel, onde fica a nossa visao e a camera do
        /// jogo. Marcus viu um personagem sumir e a visao ir para longe; isto diz qual e por que.
        /// </summary>
        private void LogCena(string quando)
        {
            try
            {
                var sb = new System.Text.StringBuilder($"[AmanatsuVR][H][PUT] {quando}:");
                var atores = HScene.Instance.Actors;
                int mascara = Cam?.Normal != null ? Cam.Normal.cullingMask : 0;
                for (int i = 0; atores != null && i < atores.Length; i++)
                {
                    var a = atores[i];
                    if (a?.Human == null) continue;
                    var go = a.Human.GameObject;
                    int ligados = 0, total = 0, foraDaMascara = 0;
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    {
                        total++;
                        if (r.enabled && r.gameObject.activeInHierarchy) ligados++;
                        if ((mascara & (1 << r.gameObject.layer)) == 0) foraDaMascara++;
                    }
                    bool ativo = false;
                    try { ativo = a.Active; } catch { }
                    sb.Append($" | {go.name}(man={a.IsMan} Active={ativo} go={go.activeInHierarchy} render={ligados}/{total} outsideMask={foraDaMascara})");
                }
                var head = Cam?.VR?.head;
                Transform camJogo = null;
                try { camJogo = HScene.Instance.MainCamera?.transform; } catch { }
                sb.Append($" | view={(head != null ? head.position.ToString("F2") : "-")} mode={_modo}{(_atorMulher ? "(woman)" : "(man)")}"
                    + $" gameCam={(camJogo != null ? camJogo.position.ToString("F2") : "-")}");
                PluginLog.Info(sb.ToString());
            }
            catch (Exception ex) { PluginLog.Warning($"[AmanatsuVR][H][PUT] log failed: {ex.Message}"); }
        }

        private void Atira(bool apertado)
        {
            if (apertado == _atirando) return;
            // Mesmo par que o jogo le: a tecla do FluidFlowManager e o botao do meio.
            if (EntradaFisica.Segura(_teclaTiro, 2, apertado)) _atirando = apertado;
        }

        private void SoltaTiro()
        {
            if (_atirando) { EntradaFisica.Segura(_teclaTiro, 2, false); _atirando = false; }
        }

        // ---------------------------------------------------------------- automatico

        private void AtualizaAuto(HScene hs, AL.H.Motion.Base.Controller ctrl)
        {
            float agora = Time.unscaledTime, naFase = agora - _faseDesde;
            bool repouso = EmRepouso(ctrl);

            switch (_fase)
            {
                case Fase.Iniciar:
                    if (EmMovimento(ctrl))
                    {
                        _tempoAto = UnityEngine.Random.Range(PluginConfig.HAutoTimeMin.Value, PluginConfig.HAutoTimeMax.Value);
                        MudaFase(Fase.Ato);
                        PluginLog.Info($"[AmanatsuVR][H][AUTO] act started, finishes in {_tempoAto:F0}s");
                    }
                    else if (agora - _ultimaTentativa > 2f)
                    {
                        _ultimaTentativa = agora;
                        IniciaOuForte(hs, ctrl);
                    }
                    break;

                case Fase.Ato:
                    // Parou sozinho (ex.: saiu do vaivem de volta a um Idle): empurra de novo.
                    if (repouso && agora - _ultimaTentativa > 2f)
                    {
                        _ultimaTentativa = agora;
                        IniciaOuForte(hs, ctrl);
                    }
                    // Os botoes so ficam clicaveis com a barra cheia; ate la tenta de novo.
                    else if (naFase >= _tempoAto && agora - _ultimaTentativa > 2f)
                    {
                        _ultimaTentativa = agora;
                        var lista = BotoesDeFinalizacao(hs);
                        if (lista.Count > 0)
                        {
                            int i = UnityEngine.Random.Range(0, lista.Count);
                            PluginLog.Info($"[AmanatsuVR][H][AUTO] finishing via '{lista[i].go.name}' (index {lista[i].indice})");
                            Clica(lista[i].go);
                            MudaFase(Fase.Finalizando);
                        }
                    }
                    break;

                case Fase.Finalizando:
                    // Fim do orgasmo: o controller volta a um Idle e o tiro livre, se houve, ja fechou.
                    // Estados de fim que nao sao Idle (Drink_A, Vomit_A...) nao podem travar o ciclo.
                    if (!_emPut && ((naFase > 3f && repouso) || naFase > 60f))
                    {
                        TrocaPose(hs);
                        MudaFase(Fase.Trocando);
                    }
                    break;

                case Fase.Trocando:
                    if (naFase > 3f) MudaFase(Fase.Iniciar);
                    break;
            }
        }

        private void MudaFase(Fase f) { _fase = f; _faseDesde = Time.unscaledTime; }

        /// <summary>
        /// Outra pose da mesma categoria. Com a lista de subtipo aberta nessa categoria o jogo
        /// troca pelo NowSelect (o mesmo que clicar nela); senao, pelo HScene.ChangePosture.
        /// </summary>
        private void TrocaPose(HScene hs)
        {
            try
            {
                // SelectedData e um ValueTuple<int,int>, e o interop o le errado (veio categoria
                // 1344653648). Os mesmos dois numeros saem do MotionPlayer por campo simples.
                var mp = hs.MotionPlayer;
                int cat = mp.Category, atual = mp.Param != null ? mp.Param.ID : -1;

                var ids = new List<int>();
                var tabela = hs.PostureMainSelecter.Table;
                if (tabela != null && tabela.TryGetValue(cat, out var sub) && sub != null)
                    foreach (var id in sub.Keys) if (id != atual) ids.Add(id);

                if (ids.Count == 0) { PluginLog.Info($"[AmanatsuVR][H][AUTO] category {cat} has no other pose; keeping {atual}"); return; }
                int novo = ids[UnityEngine.Random.Range(0, ids.Count)];

                var subSel = hs.PostureMainSelecter.SubSelecter;
                if (subSel != null && subSel._data != null && subSel._category == cat)
                {
                    int idx = subSel.FindIndex(novo);
                    if (idx >= 0)
                    {
                        PluginLog.Info($"[AmanatsuVR][H][AUTO] pose {atual} -> {novo} (list, index {idx})");
                        subSel.NowSelect = idx;
                        return;
                    }
                }

                PluginLog.Info($"[AmanatsuVR][H][AUTO] pose {atual} -> {novo} (ChangePosture)");
                hs.ChangePosture(cat, novo, hs.IsWeakness, false);
            }
            catch (Exception ex) { PluginLog.Warning($"[AmanatsuVR][H][AUTO] pose change failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// O olhar do jogo (pescoco/cabeca/olhos para a camera ou o parceiro) roda no LateProc,
    /// depois da animacao. No ator em primeira pessoa quem olha e o HMD: pula, e o pescoco
    /// fica so com a animacao.
    /// </summary>
    internal static class OlharPrimeiraPessoa
    {
        public static bool Pula(HMotionEyeNeckBase eyeNeck)
        {
            if (VRHScene.HumanPrimeiraPessoa == IntPtr.Zero) return false;
            try { return eyeNeck._human != null && eyeNeck._human.Pointer == VRHScene.HumanPrimeiraPessoa; }
            catch { return false; }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(HMotionEyeNeckFemale), nameof(HMotionEyeNeckFemale.LateProc))]
    internal static class HMotionEyeNeckFemale_LateProc_Patch
    {
        public static bool Prefix(HMotionEyeNeckFemale __instance) => !OlharPrimeiraPessoa.Pula(__instance);
    }

    [HarmonyLib.HarmonyPatch(typeof(HMotionEyeNeckMale), nameof(HMotionEyeNeckMale.LateProc))]
    internal static class HMotionEyeNeckMale_LateProc_Patch
    {
        public static bool Prefix(HMotionEyeNeckMale __instance) => !OlharPrimeiraPessoa.Pula(__instance);
    }
}
