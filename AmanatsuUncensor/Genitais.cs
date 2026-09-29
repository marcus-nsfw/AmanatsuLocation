using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Character;
using UnityEngine;

namespace Amanatsu.Uncensor
{
    /// <summary>
    /// Troca os genitais de jogo censurado (capsula lisa o_dankon, base o_dan_f) pelas malhas do
    /// BetterPenetration (KKS), geradas offline por genitais/converte_kk.py.
    ///
    /// Mesma tecnica do UncensorSelector: trocar o sharedMesh e religar os bones por nome. Os bundles
    /// do KKS sao Unity 2019.4 e nao carregam no Unity 6, por isso a malha vem num .bin ja alinhada
    /// ao espaco da malha original do Amanatsu - aqui so montamos a Mesh e reaproveitamos os
    /// bindposes da original (mesmos bones, mesma ordem).
    /// </summary>
    internal static class Genitais
    {
        private sealed class Dados
        {
            public Vector3[] V, N;
            public int[] I;
            public Vector2 Uv;
            public string[] Ossos;
            public int[] BI;     // 4 por vertice, indice em Ossos
            public float[] BW;   // 4 por vertice
        }

        /// <summary>Malha inteira ja pronta (corpo feminino com a vulva costurada) - indices de bone da original.</summary>
        private sealed class Completa
        {
            public Vector3[] V, N;
            public Vector4[] T;
            public Color[] C;
            public Vector2[][] UV = new Vector2[4][];
            public int[] I, BI;
            public float[] BW;
            // ossos da vulva (cf_J_Vagina_*): indice na malha = bindposes da original + ordem aqui
            public string[] Ossos = new string[0];
            public int[] Pai;                  // indice do osso da original onde o pivo pendura
            public Matrix4x4[] Pivo, Osso;     // no espaco da malha, no repouso
        }

        private static readonly Dictionary<string, Dados> _dados = new Dictionary<string, Dados>();
        private static readonly Dictionary<string, Completa> _completas = new Dictionary<string, Completa>();
        // variantes do corpo feminino que cobrem a virilha (troca de roupa alterna entre elas)
        private static readonly string[] _corposFemininos =
        {
            "o_lower_type01", "o_lower_type02", "o_lower_type03", "o_lower_type04", "o_lower_type05",
            "o_onepi_type01", "o_onepi_type02", "o_onepi_type03", "o_onepi_type04",
        };
        // malha pronta por malha original (cada prefab tem sua ordem de bones/bindposes)
        private static readonly Dictionary<IntPtr, Mesh> _prontas = new Dictionary<IntPtr, Mesh>();
        private static readonly Dictionary<IntPtr, int> _proximaVarredura = new Dictionary<IntPtr, int>();
        private static readonly HashSet<IntPtr> _relatados = new HashSet<IntPtr>();

        internal static void Carrega()
        {
            var pasta = Path.Combine(Paths.PluginPath, "AmanatsuUncensor", "genitais");
            foreach (var nome in new[] { "o_dankon", "o_dan_f" })
            {
                var arq = Path.Combine(pasta, nome + ".bin");
                if (!File.Exists(arq))
                {
                    UncensorPlugin.Logger.LogWarning($"[GEN] {arq} not found; {nome} stays original.");
                    continue;
                }
                try { _dados[nome] = Le(arq); }
                catch (Exception ex) { UncensorPlugin.Logger.LogWarning($"[GEN] error reading {arq}: {ex.Message}"); }
            }
            foreach (var nome in _corposFemininos)
            {
                var arq = Path.Combine(pasta, nome + ".bin");
                if (!File.Exists(arq)) continue;
                try { _completas[nome] = LeCompleta(arq); }
                catch (Exception ex) { UncensorPlugin.Logger.LogWarning($"[GEN] error reading {arq}: {ex.Message}"); }
            }
            UncensorPlugin.Logger.Info($"[GEN] {_dados.Count} male parts and {_completas.Count} female bodies loaded.");
        }

        private static Completa LeCompleta(string arq)
        {
            using var br = new BinaryReader(File.OpenRead(arq));
            if (new string(br.ReadChars(4)) != "AMBM") throw new InvalidDataException("assinatura invalida");
            int n = br.ReadInt32();
            var c = new Completa();
            float[] Le(int largura)
            {
                if (largura == 0) return null;
                var a = new float[n * largura];
                for (int i = 0; i < a.Length; i++) a[i] = br.ReadSingle();
                return a;
            }
            var v = Le(br.ReadInt32()); c.V = new Vector3[n];
            for (int i = 0; i < n; i++) c.V[i] = new Vector3(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);
            var nn = Le(br.ReadInt32());
            if (nn != null) { c.N = new Vector3[n]; for (int i = 0; i < n; i++) c.N[i] = new Vector3(nn[i * 3], nn[i * 3 + 1], nn[i * 3 + 2]); }
            var t = Le(br.ReadInt32());
            if (t != null) { c.T = new Vector4[n]; for (int i = 0; i < n; i++) c.T[i] = new Vector4(t[i * 4], t[i * 4 + 1], t[i * 4 + 2], t[i * 4 + 3]); }
            var cc = Le(br.ReadInt32());
            if (cc != null) { c.C = new Color[n]; for (int i = 0; i < n; i++) c.C[i] = new Color(cc[i * 4], cc[i * 4 + 1], cc[i * 4 + 2], cc[i * 4 + 3]); }
            for (int k = 0; k < 4; k++)
            {
                var u = Le(br.ReadInt32());
                if (u == null) continue;
                c.UV[k] = new Vector2[n];
                for (int i = 0; i < n; i++) c.UV[k][i] = new Vector2(u[i * 2], u[i * 2 + 1]);
            }
            c.I = new int[br.ReadInt32()];
            for (int i = 0; i < c.I.Length; i++) c.I[i] = br.ReadInt32();
            c.BI = new int[n * 4];
            for (int i = 0; i < c.BI.Length; i++) c.BI[i] = br.ReadInt32();
            c.BW = new float[n * 4];
            for (int i = 0; i < c.BW.Length; i++) c.BW[i] = br.ReadSingle();
            if (br.BaseStream.Position < br.BaseStream.Length)
            {
                int k = br.ReadInt32();
                c.Ossos = new string[k]; c.Pai = new int[k]; c.Pivo = new Matrix4x4[k]; c.Osso = new Matrix4x4[k];
                Matrix4x4 Mat()
                {
                    var m = new Matrix4x4();
                    for (int r = 0; r < 4; r++) for (int col = 0; col < 4; col++) m[r, col] = br.ReadSingle();
                    return m;
                }
                for (int i = 0; i < k; i++)
                {
                    c.Ossos[i] = System.Text.Encoding.UTF8.GetString(br.ReadBytes(br.ReadInt32()));
                    c.Pai[i] = br.ReadInt32();
                    c.Pivo[i] = Mat();
                    c.Osso[i] = Mat();
                }
            }
            return c;
        }

        private static Mesh MontaCompleta(Completa c, Mesh orig)
        {
            int n = c.V.Length;
            var pesos = new BoneWeight[n];
            for (int i = 0; i < n; i++)
            {
                int b = i * 4;
                pesos[i] = new BoneWeight
                {
                    boneIndex0 = c.BI[b], weight0 = c.BW[b],
                    boneIndex1 = c.BI[b + 1], weight1 = c.BW[b + 1],
                    boneIndex2 = c.BI[b + 2], weight2 = c.BW[b + 2],
                    boneIndex3 = c.BI[b + 3], weight3 = c.BW[b + 3],
                };
            }
            // criada em codigo e as vezes fora de qualquer renderer (calcinha vestida): sem isto o
            // UnloadUnusedAssets da troca de cena/roupa a destroi e o corpo fica SEM MALHA (medido)
            var m = new Mesh { name = orig.name + "_kk", hideFlags = HideFlags.DontUnloadUnusedAsset };
            m.vertices = c.V;
            if (c.N != null) m.normals = c.N;
            if (c.T != null) m.tangents = c.T;
            if (c.C != null) m.colors = c.C;
            if (c.UV[0] != null) m.uv = c.UV[0];
            if (c.UV[1] != null) m.uv2 = c.UV[1];
            if (c.UV[2] != null) m.uv3 = c.UV[2];
            if (c.UV[3] != null) m.uv4 = c.UV[3];
            m.triangles = c.I;
            m.boneWeights = pesos;
            var bp = new List<Matrix4x4>(orig.bindposes);
            foreach (var o in c.Osso) bp.Add(o.inverse);
            m.bindposes = bp.ToArray();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// Cria (ou reaproveita) os ossos da vulva deste renderer: pivo filho do osso pai da original, osso
        /// filho do pivo, nas posicoes de repouso do .bin. Local = bindpose do pai x matriz no espaco da malha,
        /// entao parados eles se movem igual ao pai e a malha fica identica a sem ossos.
        /// </summary>
        private static bool LigaOssos(SkinnedMeshRenderer smr, Completa c, Matrix4x4[] bindOrig)
        {
            var bones = smr.bones;
            int n0 = bindOrig.Length;
            if (bones.Length < n0) return false;
            var lista = new List<Transform>();
            for (int i = 0; i < n0; i++) lista.Add(bones[i]);
            for (int i = 0; i < c.Ossos.Length; i++)
            {
                var pai = bones[c.Pai[i]];
                if (pai == null) return false;
                var nomePivo = c.Ossos[i].StartsWith("cf_J_Vagina_") ? c.Ossos[i].Replace("cf_J_Vagina_", "cf_J_Vagina_Pivot_") : c.Ossos[i] + "_Pivot";
                var pivo = pai.Find(nomePivo);
                if (pivo == null)
                {
                    pivo = new GameObject(nomePivo).transform;
                    pivo.SetParent(pai, false);
                    AplicaLocal(pivo, bindOrig[c.Pai[i]] * c.Pivo[i]);
                }
                var osso = pivo.Find(c.Ossos[i]);
                if (osso == null)
                {
                    osso = new GameObject(c.Ossos[i]).transform;
                    osso.SetParent(pivo, false);
                    AplicaLocal(osso, c.Pivo[i].inverse * c.Osso[i]);
                }
                lista.Add(osso);
                LigaFisica(pivo, osso, c.Ossos[i]);
            }
            smr.bones = lista.ToArray();
            return true;
        }

        // ---- Collision (o "push" do BetterPenetration: osso da vulva sai do caminho dos dedos/penis, com limite) ----
        // Primeira versao usou DynamicBone (os parametros do doador). MEDIDO na captura F9: 56 dobras - o braco
        // pivo->osso e longo (~3 unid.), a trava de rigidez do DB deixa o osso ir 1.2x isso e a animacao do jogo
        // enfia os dedos fundo na virilha. Este calculo e o MESMO validado offline (sim_empurra.py) na pose real:
        // raio do KK x 1.235 x 8.18, limite 0.15, pesos suavizados -> 3 dobras (parado: 1).
        private const float EscalaKK = 8.18f, CurvaFim = 1.235f, EmpurraoMax = 0.15f, Taxa = 20f;
        private static float RaioKK(string osso) =>
            osso.EndsWith("_F") ? 0.0125f : osso.EndsWith("_B") ? 0.015f :
            osso.EndsWith(".001") || osso.EndsWith(".002") ? 0.02f : osso.EndsWith(".003") ? 0.0175f : 0.015f;

        // Capsulas: falanges 02->03 e 03->ponta (index/middle/ring, as 2 maos) e o penis (dan101 -> dan109).
        // MEDIDO nas malhas do Amanatsu: falange 0.035-0.08 de raio (o do BP convertido, 0.045, fica no meio);
        // penis 0.21. Unidade: espaco da malha (bindposes sem escala -> escala do osso = malha->mundo).
        private static readonly string[] _dedos = { "index", "middle", "ring" };
        private const float RaioDedo = 0.045f, RaioPenis = 0.21f;

        // Vis: se definido, a capsula so atua com esse renderer visivel. MEDIDO (log do F9, heroina parada): o
        // esqueleto FEMININO tambem tem cf_j_dan101/109 na virilha - a capsula de penis dela empurrava a propria
        // vulva no limite o tempo todo (a "forma redonda" perdida em repouso).
        private sealed class Capsula { public Transform A, B; public bool Estende, AnusOnly; public float Raio; public Renderer Vis; }
        // Anal: ring bones of the anus cavity (cf_J_Anus_*). The pivot sits at the opening limit for that direction
        // (|Repouso| = max push) with its z axis on the skin normal; the push only opens sideways.
        private sealed class OssoVulva { public Transform Pivo, Osso; public Vector3 Repouso, Atual; public float Raio; public bool Anal; }
        private const float AnusBoneRadius = 0.01f;
        private static readonly List<Capsula> _capsulas = new List<Capsula>();
        private static readonly List<OssoVulva> _ossosVulva = new List<OssoVulva>();
        private static readonly HashSet<int> _comColisor = new HashSet<int>();   // InstanceID da raiz do esqueleto
        private static int _ultimoFrameFisica = -1;

        // ---- Profundidade do penis ----
        // MEDIDO (captura F9, penetracao): os ossos do penis nao mudam de escala; a ANIMACAO do jogo puxa o
        // cf_j_dan109_00 pra 24% da distancia de repouso ate o dan101 (o penis original era uma capsula curta e
        // isso evita atravessar o corpo feminino). Devolvemos uma fracao configuravel do que ela tirou.
        internal static BepInEx.Configuration.ConfigEntry<float> ExtraDepth;
        // Na boca o encurtamento da animacao e o mesmo, mas o encolhimento aparece muito mais (Marcus): a boca
        // aceita devolver mais comprimento. Boca = a ponta esta mais perto de uma cabeca (que nao a do dono)
        // do que de qualquer osso da vulva.
        internal static BepInEx.Configuration.ConfigEntry<float> MouthDepth;
        internal static BepInEx.Configuration.ConfigEntry<bool> Collision;
        private sealed class Penis { public Transform Ponta, CabecaDono, Raiz; public SkinnedMeshRenderer Malha; public float Repouso, Escrito = float.NaN, Base, MouthPendingSince = -1f; public bool NaBoca;
            public bool Aimed, WasAnal; public Quaternion RotWritten, RotBase; }
        private static readonly List<Penis> _penis = new List<Penis>();
        private static readonly List<Transform> _cabecas = new List<Transform>();
        // Anal entrances (center + inward canal axis), from the ring bones' rest positions; refreshed 1x per frame.
        private static readonly List<(Transform key, Vector3 center, Vector3 inward)> _anusEntrances = new List<(Transform, Vector3, Vector3)>();

        private static void RefreshAnusEntrances()
        {
            _anusEntrances.Clear();
            var acc = new Dictionary<Transform, (Vector3 pos, Vector3 ax, int n)>();
            foreach (var o in _ossosVulva)
            {
                if (!o.Anal || o.Pivo == null) continue;
                acc.TryGetValue(o.Pivo.parent, out var a);
                acc[o.Pivo.parent] = (a.pos + o.Pivo.TransformPoint(o.Repouso), a.ax - o.Pivo.forward, a.n + 1);
            }
            foreach (var kv in acc) _anusEntrances.Add((kv.Key, kv.Value.pos / kv.Value.n, kv.Value.ax.normalized));
        }

        internal static BepInEx.Configuration.ConfigEntry<float> MaxDepth;
        internal static BepInEx.Configuration.ConfigEntry<float> AnalDepth;

        /// <summary>Centro da vulva em repouso: media dos pivos dos ossos dela (o osso em si e empurrado).</summary>
        private static Vector3? CentroVulva()
        {
            Vector3 soma = Vector3.zero; int n = 0;
            foreach (var o in _ossosVulva)
                if (o.Pivo != null && !o.Anal) { soma += o.Pivo.position; n++; }
            return n > 0 ? soma / n : (Vector3?)null;
        }


        /// <summary>Distancias da ponta ate a cabeca (que nao a do dono) e ate a vulva, em comprimentos do penis.</summary>
        private static (float cabeca, float vulva) Distancias(Penis p)
        {
            // Where the ANIMATION put the tip, not where we stretched it: measuring our own stretch made the
            // lick pose ("floor: licking") flip in/out of the mouth every frame - stretched tip past the face =
            // "out", shrunk back = "in".
            var lp = p.Ponta.localPosition;
            if (!float.IsNaN(p.Escrito) && Mathf.Abs(lp.z - p.Escrito) < 1e-6f) lp.z = p.Base;
            Vector3 pt = p.Ponta.parent != null ? p.Ponta.parent.TransformPoint(lp) : p.Ponta.position;
            float comp = p.Repouso * (p.Raiz != null ? p.Raiz.lossyScale.z : 1f);
            float cabeca = float.MaxValue, vulva = float.MaxValue;
            foreach (var c in _cabecas)
                if (c != null && c != p.CabecaDono) cabeca = Mathf.Min(cabeca, (c.position - pt).magnitude);
            foreach (var o in _ossosVulva)
                if (o.Osso != null) vulva = Mathf.Min(vulva, (o.Osso.position - pt).magnitude);
            return (cabeca / comp, vulva / comp);
        }

        /// <summary>
        /// Boca = a ponta esta perto de uma cabeca. Antes era "mais perto da cabeca que da vulva", e num oral de
        /// joelhos a vulva dela fica mais perto da ponta que o centro da cabeca - nunca reconheceu a boca.
        /// </summary>
        private const float MouthEnter = 0.6f, MouthExit = 0.8f, MouthDwell = 0.25f;

        /// <summary>
        /// Hysteresis (enter under 0.6 lengths, leave over 0.8) plus a short dwell: a licking tip grazes the face
        /// right at the threshold, and each crossing reset the length.
        /// </summary>
        private static bool NaBoca(Penis p)
        {
            var (cabeca, _) = Distancias(p);
            bool wanted = p.NaBoca ? cabeca < MouthExit : cabeca < MouthEnter;
            if (wanted == p.NaBoca) { p.MouthPendingSince = -1f; return p.NaBoca; }
            if (p.MouthPendingSince < 0f) p.MouthPendingSince = Time.time;
            return Time.time - p.MouthPendingSince >= MouthDwell ? wanted : p.NaBoca;
        }

        /// <summary>Depois do LateUpdate do LookAtPenis: no oral ele reposiciona a genital, entao o alongamento vem de novo.</summary>
        internal static void ReaplicaAlongamento() { if (!OtherUncensor) AlongaPenis(); }


        // F9 also traces the penis frame by frame for a few seconds: a single capture showed it inside, the
        // escape only happens during the motion.
        private static float _traceUntil = -1f;
        private const float TraceSeconds = 4f;

        private static void Trace(Penis p, string mode, Vector3 vl, float zAnim, float alvo)
        {
            if (Time.unscaledTime > _traceUntil) return;
            float r = p.Repouso;
            UncensorPlugin.Logger.Info($"[GEN][TRACE] f{Time.frameCount} {mode} side {new Vector2(vl.x, vl.y).magnitude / r:F2}"
                + $" entrance {vl.z / r:F2} anim {zAnim / r:F2} target {alvo / r:F2} tip {p.Ponta.localPosition.z / r:F2}"
                + $" aim {(p.Aimed ? Quaternion.Angle(p.RotBase, p.RotWritten) : 0f):F0}deg (penis lengths)");
        }

        private static void AlongaPenis()
        {
            float fVulva = ExtraDepth != null ? Mathf.Clamp01(ExtraDepth.Value) : 0f;
            float fBoca = MouthDepth != null ? Mathf.Clamp01(MouthDepth.Value) : fVulva;
            _penis.RemoveAll(p => p.Ponta == null);
            _cabecas.RemoveAll(c => c == null);
            Vector3? centroVulva = CentroVulva();
            float dMax = MaxDepth != null ? Mathf.Max(0f, MaxDepth.Value) : 0.35f;
            foreach (var p in _penis)
            {
                if (!Visivel(p)) continue;
                // MEASURED (F9, teleport): with the aim applied, the next frame measured the entrance on OUR rotation.
                // Undo it first (if the game did not rewrite it) so every check sees the game's pose.
                if (p.Aimed)
                {
                    if (Quaternion.Angle(p.Raiz.localRotation, p.RotWritten) < 0.01f) p.Raiz.localRotation = p.RotBase;
                    p.Aimed = false;
                }
                bool boca = NaBoca(p);
                if (boca != p.NaBoca)
                {
                    // Saiu da boca: a animacao nao reescreve a ponta todo quadro, entao o alongamento da boca
                    // ficaria. Devolve o que a animacao tinha posto antes de aplicar a regra de fora.
                    if (!boca && !float.IsNaN(p.Escrito))
                    {
                        var lr = p.Ponta.localPosition;
                        if (Mathf.Abs(lr.z - p.Escrito) < 1e-6f) { lr.z = p.Base; p.Ponta.localPosition = lr; }
                        p.Escrito = float.NaN;
                    }
                    p.NaBoca = boca;
                    UncensorPlugin.Logger.Info($"[GEN] penis {(boca ? $"in mouth: length x{(MouthLength != null ? MouthLength.Value : 1.35f):F2}" : "outside mouth")}");
                }
                // Vagina: comprimento inteiro ate a ponta passar dMax (fracao do comprimento) da entrada;
                // so dai o encurtamento da animacao aparece (Marcus: "a profundidade maxima primeiro, so
                // entao amassar"). A fracao fixa (ExtraDepth) fica para quando nao ha vulva.
                // So com a vulva de fato no eixo, a frente da base: se a deteccao de boca falhar, a vulva longe
                // daria "comprimento inteiro" e o penis atravessaria a cabeca dela.
                // Entrada = vulva ou anus, a que estiver mais perto do eixo.
                Vector3 vl = Vector3.zero;
                bool noEixo = false, anal = false;
                int entrance = -1;
                float bestSide = float.MaxValue;
                // MEASURED (F9): in vaginal the anus sat 0.517 off the axis, right at the 0.475 limit - the entrance
                // flipped vulva/anus and the aim jumped. Anus only when really on the axis, with hysteresis.
                float anusLimit = (p.WasAnal ? 0.18f : 0.12f) * p.Repouso;
                void Try(Vector3 w, bool isAnus)
                {
                    var l = p.Raiz.InverseTransformPoint(w);
                    float side = new Vector2(l.x, l.y).magnitude;
                    if (isAnus && side >= anusLimit) return;
                    if (side < 0.25f * p.Repouso && l.z > -0.1f * p.Repouso && l.z < 1.2f * p.Repouso && side < bestSide)
                    { bestSide = side; vl = l; noEixo = true; anal = isAnus; entrance = -1; }
                }
                if (p.Raiz != null)
                {
                    if (centroVulva.HasValue) Try(centroVulva.Value, false);
                    for (int i = 0; i < _anusEntrances.Count; i++)
                        if (_anusEntrances[i].key != null) { Try(_anusEntrances[i].center, true); if (anal && entrance < 0) entrance = i; }
                }
                p.WasAnal = !boca && noEixo && anal;
                if (!boca && noEixo)
                {
                    var lpv = p.Ponta.localPosition;
                    float zAnim = !float.IsNaN(p.Escrito) && Mathf.Abs(lpv.z - p.Escrito) < 1e-6f ? p.Base : lpv.z;
                    p.Base = zAnim;
                    float entrada = vl.z;
                    // MEASURED (F9, anal): the game aims the penis 61-81 deg off the canal, almost straight up her
                    // body; with the vaginal depth (0.35) the tip came out of her skin. Anal has its own, shorter depth.
                    float depth = anal ? (AnalDepth != null ? Mathf.Max(0f, AnalDepth.Value) : 0.15f) : dMax;
                    float alvo = Mathf.Clamp(entrada + depth * p.Repouso, zAnim, p.Repouso);
                    if (anal && entrance >= 0)
                    {
                        // MEASURED (F9 render): in anal the game lays the penis along the buttock crease, 85 deg off the
                        // canal - in the censored game it shrinks and the tip barely reaches the anus. Aim it from its
                        // base at a point inside the canal and give it the length to get there.
                        p.RotBase = p.Raiz.localRotation;
                        float scale = Mathf.Abs(p.Raiz.lossyScale.z);
                        var e = _anusEntrances[entrance];
                        var dir = e.center + e.inward * (depth * p.Repouso * scale) - p.Raiz.position;
                        p.Raiz.rotation = Quaternion.FromToRotation(p.Raiz.forward, dir.normalized) * p.Raiz.rotation;
                        p.RotWritten = p.Raiz.localRotation; p.Aimed = true;
                        alvo = Mathf.Clamp(dir.magnitude / scale, zAnim, p.Repouso);
                    }
                    Trace(p, anal ? "anus" : "vulva", vl, zAnim, alvo);
                    if (alvo <= zAnim) { p.Escrito = float.NaN; continue; }
                    lpv.z = alvo;
                    p.Ponta.localPosition = lpv;
                    p.Escrito = alvo;
                    continue;
                }
                float f = boca ? fBoca : fVulva;
                var lp = p.Ponta.localPosition;
                // se a animacao nao reescreveu neste frame, o valor ainda e o nosso: parte da base anterior
                float z = !float.IsNaN(p.Escrito) && Mathf.Abs(lp.z - p.Escrito) < 1e-6f ? p.Base : lp.z;
                p.Base = z;
                // off the axis: show where the vulva was, to see which limit dropped it
                if (Time.unscaledTime <= _traceUntil && p.Raiz != null)
                    Trace(p, boca ? "boca" : "fora", centroVulva.HasValue ? p.Raiz.InverseTransformPoint(centroVulva.Value) : Vector3.zero, z, z);
                if (boca)
                {
                    // MEDIDO (F9 no oral): o penis nao e encurtado; a animacao da cabeca foi feita para o penis
                    // original e no meio do movimento a ponta ainda aparecia nos labios. Na boca ele fica mais longo
                    // que o repouso, ate a ponta sumir dentro dela. Os pesos sao lineares entre dan101 e dan109, entao
                    // afastar a ponta alonga a malha sem afinar.
                    float longo = p.Repouso * Mathf.Max(1f, MouthLength != null ? MouthLength.Value : 1.35f);
                    float zb = Mathf.Max(z + f * Mathf.Max(0f, p.Repouso - z), longo);
                    if (zb <= z) { p.Escrito = float.NaN; continue; }
                    lp.z = zb;
                    p.Ponta.localPosition = lp;
                    p.Escrito = zb;
                    continue;
                }
                if (z >= p.Repouso || f <= 0f) { p.Escrito = float.NaN; continue; }
                lp.z = z + f * (p.Repouso - z);
                p.Ponta.localPosition = lp;
                p.Escrito = lp.z;
            }
        }

        internal static BepInEx.Configuration.ConfigEntry<float> MouthLength;

        private static void LigaFisica(Transform pivo, Transform osso, string nome)
        {
            foreach (var o in _ossosVulva) if (o.Osso == osso) return;
            bool anal = nome.StartsWith("cf_J_Anus_");
            _ossosVulva.Add(new OssoVulva { Pivo = pivo, Osso = osso, Repouso = osso.localPosition, Anal = anal,
                Raio = anal ? AnusBoneRadius : RaioKK(nome) * CurvaFim * EscalaKK });
        }

        /// <summary>1x por frame: cada osso da vulva vai (suave) pra fora das capsulas, limitado a EmpurraoMax.</summary>
        private static void AtualizaFisica()
        {
            if (_ultimoFrameFisica == Time.frameCount) return;
            _ultimoFrameFisica = Time.frameCount;
            RefreshAnusEntrances();
            AlongaPenis();   // antes do empurrao: a capsula do penis usa a ponta ja alongada
            if (_ossosVulva.Count == 0) return;
            if (Collision != null && !Collision.Value)
            {
                foreach (var o in _ossosVulva)
                    if (o.Osso != null) { o.Osso.localPosition = o.Repouso; o.Atual = Vector3.zero; }
                return;
            }
            _ossosVulva.RemoveAll(o => o.Osso == null || o.Pivo == null);
            _capsulas.RemoveAll(c => c.A == null || c.B == null);
            float k = 1f - Mathf.Exp(-Taxa * Time.deltaTime);
            // anus entrance center per skeleton = mean of its ring bones' rest positions
            _anusCenters.Clear();
            foreach (var o in _ossosVulva)
                if (o.Anal)
                {
                    var key = o.Pivo.parent;
                    _anusCenters.TryGetValue(key, out var acc);
                    _anusCenters[key] = new Vector4(acc.x, acc.y, acc.z, acc.w) + (Vector4)o.Pivo.TransformPoint(o.Repouso) + new Vector4(0, 0, 0, 1);
                }
            foreach (var o in _ossosVulva)
            {
                float esc = Mathf.Abs(o.Pivo.lossyScale.x);
                if (o.Anal) { AnusBone(o, esc, k); continue; }
                var p0 = o.Pivo.TransformPoint(o.Repouso);
                var d = Vector3.zero;
                foreach (var c in _capsulas)
                {
                    if (c.AnusOnly || (c.Vis != null && !(c.Vis.enabled && c.Vis.gameObject.activeInHierarchy))) continue;
                    Vector3 a = c.A.position, b = c.B.position;
                    if (c.Estende) { var ab = b - a; a = b; b = b + ab; }
                    var p = p0 + d; var s = b - a;
                    float t = Mathf.Clamp01(Vector3.Dot(p - a, s) / Mathf.Max(s.sqrMagnitude, 1e-12f));
                    var v = p - (a + t * s); float dist = v.magnitude;
                    float lim = o.Raio * esc + c.Raio * Mathf.Abs(c.A.lossyScale.x);
                    if (dist < lim && dist > 1e-6f) d += v / dist * (lim - dist);
                }
                d = Vector3.ClampMagnitude(d, EmpurraoMax * esc);
                o.Atual = Vector3.Lerp(o.Atual, d, k);
                o.Osso.position = p0 + o.Atual;
            }
        }

        // MEASURED (F9, finger in the anus): the finger surface was 1-2 mm from the entrance center, but its capsule
        // axis stayed ~0.05 from the ring bones and the lateral push gave 0.0004 - no visible opening. The ring now
        // opens by how close any capsule gets to the ENTRANCE CENTER: from AnusReach outside the capsule surface
        // (closed) to touching it (fully open, up to the limit stored as the pivot distance).
        // MEASURED (F9 diagnostic): the game animation puts middle+ring fingers one on each side of the entrance,
        // capsule axes 0.082-0.086 from the center (radius 0.044) - with 0.03 it stayed closed (-0.011).
        // Max opening grew to 0.035 (x0.92): 0.075 opens it fully with the fingers of that pose (0.078 away).
        private const float AnusReach = 0.075f;
        private static readonly Dictionary<Transform, Vector4> _anusCenters = new Dictionary<Transform, Vector4>();

        private static void AnusBone(OssoVulva o, float esc, float k)
        {
            var p0 = o.Pivo.TransformPoint(o.Repouso);
            var acc = _anusCenters[o.Pivo.parent];
            var center = (Vector3)acc / acc.w;
            var ax = o.Pivo.forward;   // outward skin normal
            float open = 0f;
            foreach (var c in _capsulas)
            {
                if (c.Vis != null && !(c.Vis.enabled && c.Vis.gameObject.activeInHierarchy)) continue;
                Vector3 a = c.A.position, b = c.B.position;
                if (c.Estende) { var ab = b - a; a = b; b = b + ab; }
                // MEASURED (F9, vaginal): the penis tip deep in the vagina passed 0.25 from the anus center and opened
                // it. Only what crosses the entrance plane counts (where it crosses), or a tip right outside it.
                float ha = Vector3.Dot(a - center, ax), hb = Vector3.Dot(b - center, ax);
                float r = c.Raio * Mathf.Abs(c.A.lossyScale.x);
                float dist;
                if ((ha > 0f) != (hb > 0f)) dist = (a + (b - a) * (ha / (ha - hb)) - center).magnitude;
                else if (ha > 0f && Mathf.Min(ha, hb) < r)
                {
                    var q = ha < hb ? a : b;   // outside, closest end approaching the entrance
                    dist = (q - Vector3.Dot(q - center, ax) * ax - center).magnitude;
                }
                else continue;
                open = Mathf.Max(open, r + AnusReach * esc - dist);
            }
            var radial = p0 - center; radial -= Vector3.Dot(radial, ax) * ax;
            var d = radial.normalized * Mathf.Clamp(open, 0f, o.Repouso.magnitude * esc);
            o.Atual = Vector3.Lerp(o.Atual, d, k);
            o.Osso.position = p0 + o.Atual;
        }

        /// <summary>O esqueleto dela tambem tem penis (oculto): so conta o que esta de fato na tela.</summary>
        private static bool Visivel(Penis p) =>
            p.Malha == null || (p.Malha.enabled && p.Malha.gameObject.activeInHierarchy);

        private static bool Contem(Transform raiz, string nome)
        {
            foreach (var t in raiz.GetComponentsInChildren<Transform>(true)) if (t.name == nome) return true;
            return false;
        }

        /// <summary>Cria as capsulas nos dedos e no penis deste personagem (1x).</summary>
        private static void CriaColisores(Human h, SkinnedMeshRenderer[] smrs)
        {
            // MEDIDO (log): a chave antiga, o ponteiro do Human, falhou - o homem da cena H reaproveitou o Human de
            // um personagem criado antes do mapa, nunca foi registrado, e todo ajuste de penis agia no outro (parado
            // em y~3, longe da cena). A chave agora e o esqueleto: InstanceID da raiz, unico na sessao.
            // MEDIDO (log): o esqueleto NAO fica sob o p_*_body. Sobe a partir de um osso do corpo ate o
            // primeiro ancestral que contem os dedos - a raiz do esqueleto deste personagem.
            Transform raiz = null;
            foreach (var r in smrs)
            {
                var bs = r.bones;
                if (bs == null || bs.Length == 0 || bs[0] == null) continue;
                int nivel = 0;
                for (var a = bs[0]; a != null && raiz == null && nivel++ < 8; a = a.parent)
                    foreach (var x in a.GetComponentsInChildren<Transform>(true))
                        if (x.name == "cf_j_index02_R") { raiz = a; break; }
                if (raiz != null) break;
            }
            if (raiz == null) return;   // tenta de novo na proxima varredura
            // MEDIDO (log): no homem o primeiro ancestral com os dedos era cf_j_spine03, e o penis fica sob
            // cf_j_hips - ele era registrado sem penis (12 capsulas), e o unico penis registrado era o oculto do
            // esqueleto dela. Sobe ate a raiz conter tambem o penis (se este esqueleto tiver um).
            if (!Contem(raiz, "cf_j_dan101_00"))
            {
                var a = raiz.parent;
                for (int sobe = 0; sobe < 5 && a != null; sobe++, a = a.parent)
                    if (Contem(a, "cf_j_dan101_00")) { raiz = a; break; }
            }
            if (!_comColisor.Add(raiz.GetInstanceID())) return;
            var porNome = new Dictionary<string, Transform>();
            foreach (var t in raiz.GetComponentsInChildren<Transform>(true))
                if (!porNome.ContainsKey(t.name)) porNome[t.name] = t;
            porNome.TryGetValue("cf_j_head", out var cabeca);
            if (cabeca != null && !_cabecas.Contains(cabeca)) _cabecas.Add(cabeca);
            int n = 0;
            foreach (var mao in new[] { "L", "R" })
                foreach (var dedo in _dedos)
                    if (porNome.TryGetValue($"cf_j_{dedo}02_{mao}", out var f2) && porNome.TryGetValue($"cf_j_{dedo}03_{mao}", out var f3))
                    {
                        _capsulas.Add(new Capsula { A = f2, B = f3, Raio = RaioDedo });
                        _capsulas.Add(new Capsula { A = f2, B = f3, Estende = true, Raio = RaioDedo });   // 03 -> ponta
                        n += 2;
                        // MEASURED (F9, finger fully inside the anus): the entrance sat on phalanx 01, which had no
                        // capsule. Anus only, so the validated vulva push does not change.
                        if (porNome.TryGetValue($"cf_j_{dedo}01_{mao}", out var f1))
                        { _capsulas.Add(new Capsula { A = f1, B = f2, Raio = RaioDedo, AnusOnly = true }); n++; }
                    }
            SkinnedMeshRenderer rDan = null;
            foreach (var r in smrs)
                if (r.sharedMesh != null && r.sharedMesh.name.StartsWith("o_dankon")) { rDan = r; break; }
            if (rDan != null && porNome.TryGetValue("cf_j_dan101_00", out var dan) && porNome.TryGetValue("cf_j_dan109_00", out var ponta))
            {
                _capsulas.Add(new Capsula { A = dan, B = ponta, Raio = RaioPenis, Vis = rDan });
                n++;
                // comprimento de repouso dan101 -> dan109 pelos bindposes da malha (a pose atual pode ja estar encurtada)
                float repouso = 0f;
                foreach (var r in smrs)
                {
                    var m = r.sharedMesh;
                    if (m == null || !m.name.StartsWith("o_dankon")) continue;
                    var bs = r.bones; var bp = m.bindposes;
                    int i1 = -1, i9 = -1;
                    for (int i = 0; i < bs.Length && i < bp.Length; i++)
                    {
                        if (bs[i] == dan) i1 = i;
                        if (bs[i] == ponta) i9 = i;
                    }
                    if (i1 >= 0 && i9 >= 0)
                        repouso = ((Vector3)bp[i9].inverse.GetColumn(3) - (Vector3)bp[i1].inverse.GetColumn(3)).magnitude;
                }
                if (repouso > 0f) _penis.Add(new Penis { Ponta = ponta, Repouso = repouso, CabecaDono = cabeca, Raiz = dan, Malha = rDan });
                UncensorPlugin.Logger.Info($"[GEN] penis: rest length dan101->dan109 {repouso:F3}");
            }
            UncensorPlugin.Logger.Info($"[GEN] {raiz.name}: {n} capsules (fingers/penis); {_ossosVulva.Count} active vulva bones.");
        }

        private static void AplicaLocal(Transform t, Matrix4x4 m)
        {
            t.localPosition = m.GetColumn(3);
            t.localRotation = m.rotation;
            t.localScale = m.lossyScale;
        }

        private static Dados Le(string arq)
        {
            using var br = new BinaryReader(File.OpenRead(arq));
            if (new string(br.ReadChars(4)) != "AMGN") throw new InvalidDataException("assinatura invalida");
            var d = new Dados();
            int n = br.ReadInt32();
            d.V = new Vector3[n];
            for (int i = 0; i < n; i++) d.V[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            d.N = new Vector3[n];
            for (int i = 0; i < n; i++) d.N[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            d.I = new int[br.ReadInt32()];
            for (int i = 0; i < d.I.Length; i++) d.I[i] = br.ReadInt32();
            d.Uv = new Vector2(br.ReadSingle(), br.ReadSingle());
            d.Ossos = new string[br.ReadInt32()];
            for (int i = 0; i < d.Ossos.Length; i++) d.Ossos[i] = System.Text.Encoding.UTF8.GetString(br.ReadBytes(br.ReadInt32()));
            d.BI = new int[n * 4];
            d.BW = new float[n * 4];
            for (int i = 0; i < n; i++)
            {
                for (int k = 0; k < 4; k++) d.BI[i * 4 + k] = br.ReadInt32();
                for (int k = 0; k < 4; k++) d.BW[i * 4 + k] = br.ReadSingle();
            }
            return d;
        }

        private static int _ultimoFrameCaptura = -1;
        private static int _capturas;

        /// <summary>
        /// F9: grava a malha DEFORMADA de verdade (BakeMesh) do corpo de cada personagem em OBJ, no espaco
        /// do mundo, pra analisar offline a geometria exata de um defeito que so aparece numa pose do jogo
        /// (a pose simulada offline e so aproximada). Tambem registra quantos bones por vertice o jogo usa.
        /// </summary>
        /// <summary>
        /// MEDIDO (F9, oral): a malha do penis encurta (1,35 -> 1,09 e ate 0,46) sem a ponta (dan109) ser puxada em z
        /// - o que os ajustes de profundidade corrigem. Entao o jogo encurta por outro osso da cadeia. Despeja cada
        /// cf_j_dan*: pai, posicao e escala locais, e distancia real ate a raiz do penis.
        /// </summary>
        private static void LogCadeiaPenis()
        {
            foreach (var p in _penis)
            {
                if (p.Raiz == null || p.Ponta == null) continue;
                var raiz = p.Raiz.parent != null ? p.Raiz.parent : p.Raiz;
                UncensorPlugin.Logger.Info($"[GEN][CHAIN] root '{raiz.name}' world scale {raiz.lossyScale} | rest dan101->109 {p.Repouso:F3}"
                    + $" | real {Vector3.Distance(p.Raiz.position, p.Ponta.position) / Mathf.Max(1e-6f, p.Raiz.lossyScale.z):F3}");
                foreach (var t in raiz.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("cf_j_dan") && !t.name.StartsWith("cf_s_dan")) continue;
                    UncensorPlugin.Logger.Info($"[GEN][CHAIN]   {t.name} parent={(t.parent != null ? t.parent.name : "-")}"
                        + $" pos={t.localPosition.ToString("F3")} rot={t.localEulerAngles.ToString("F0")} scale={t.localScale.ToString("F3")}"
                        + $" dist-root={Vector3.Distance(raiz.position, t.position) / Mathf.Max(1e-6f, raiz.lossyScale.z):F3}");
                }

                // MEDIDO: os tres ossos acima em repouso e a malha com menos da metade do comprimento. Entao a
                // malha tambem e deformada por outros ossos: lista todos os dela, com o peso total de vertices.
                var m = p.Malha != null ? p.Malha.sharedMesh : null;
                if (m == null) continue;
                var ossos = p.Malha.bones;
                var peso = new float[ossos.Length];
                foreach (var bw in m.boneWeights)
                {
                    if (bw.boneIndex0 < peso.Length) peso[bw.boneIndex0] += bw.weight0;
                    if (bw.boneIndex1 < peso.Length) peso[bw.boneIndex1] += bw.weight1;
                    if (bw.boneIndex2 < peso.Length) peso[bw.boneIndex2] += bw.weight2;
                    if (bw.boneIndex3 < peso.Length) peso[bw.boneIndex3] += bw.weight3;
                }
                UncensorPlugin.Logger.Info($"[GEN][MESH] '{m.name}': {ossos.Length} bones, {m.vertexCount} vertices, rootBone={(p.Malha.rootBone != null ? p.Malha.rootBone.name : "-")}");
                for (int i = 0; i < ossos.Length; i++)
                {
                    if (peso[i] < 0.5f) continue;
                    var t = ossos[i];
                    if (t == null) { UncensorPlugin.Logger.Info($"[GEN][MESH]   [{i}] NULL weight {peso[i]:F0}"); continue; }
                    UncensorPlugin.Logger.Info($"[GEN][MESH]   [{i}] {t.name} weight {peso[i]:F0} parent={(t.parent != null ? t.parent.name : "-")}"
                        + $" pos={t.localPosition.ToString("F3")} scale={t.localScale.ToString("F3")} world={t.position.ToString("F2")}");
                }
            }
        }

        private static void Captura(Human h)
        {
            if (_ultimoFrameCaptura != Time.frameCount)
            {
                _ultimoFrameCaptura = Time.frameCount;
                _capturas++;
                UncensorPlugin.Logger.Info($"[GEN] capture {_capturas}: QualitySettings.skinWeights={QualitySettings.skinWeights}");
                LogCadeiaPenis();
            }
            var o = h.GetRefObject(Table.RefObjKey.ObjBody);
            if (o == null) return;
            var t = o.transform;
            while (t.parent != null && t.name != "cf_o_root") t = t.parent;

            var pasta = Path.Combine(Paths.BepInExRootPath, "genital_dump");
            Directory.CreateDirectory(pasta);
            var arq = Path.Combine(pasta, $"captura{_capturas}_{t.parent?.name}_{h.Pointer.ToInt64():X}.obj");
            using var w = new StreamWriter(arq);
            int base_ = 1;
            foreach (var smr in t.gameObject.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (!smr.enabled || smr.sharedMesh == null) continue;
                var nome = smr.sharedMesh.name;
                if (!(nome.StartsWith("o_lower") || nome.StartsWith("o_onepi") || nome.StartsWith("o_upper")
                      || nome.StartsWith("o_body") || nome.StartsWith("o_dan") || nome == "mnpa" || nome == "mnpb"))
                    continue;
                var m = new Mesh();
                smr.BakeMesh(m, true);
                var mundo = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                w.WriteLine($"o {nome} quality={smr.quality} verts={m.vertexCount}");
                UncensorPlugin.Logger.Info($"[GEN]   {nome}: quality={smr.quality}, {m.vertexCount} vertices");
                var vs = m.vertices;
                for (int i = 0; i < vs.Length; i++)
                {
                    var p = mundo.MultiplyPoint3x4(vs[i]);
                    w.WriteLine(FormattableString.Invariant($"v {p.x} {p.y} {p.z}"));
                }
                var tris = m.triangles;
                for (int i = 0; i < tris.Length; i += 3)
                    w.WriteLine($"f {tris[i] + base_} {tris[i + 1] + base_} {tris[i + 2] + base_}");
                base_ += vs.Length;
                UnityEngine.Object.Destroy(m);
            }
            UncensorPlugin.Logger.Info($"[GEN] capture saved: {arq}");
            // estado da fisica da vulva neste frame (diagnostico: deslocamento sem contato)
            foreach (var ov in _ossosVulva)
            {
                if (ov.Osso == null || ov.Pivo == null) continue;
                float perto = float.MaxValue;
                var p0 = ov.Pivo.TransformPoint(ov.Repouso);
                foreach (var cp in _capsulas)
                    if (cp.A != null) perto = Mathf.Min(perto, (cp.A.position - p0).magnitude);
                UncensorPlugin.Logger.Info($"[GEN]   {ov.Osso.name}: local-rest {(ov.Osso.localPosition - ov.Repouso).magnitude:F4}, " +
                    $"push {ov.Atual.magnitude:F4}, nearest capsule {perto:F3}, pivot scale {ov.Pivo.lossyScale.x:F3}, " +
                    $"parent {ov.Pivo.parent.name} local scale {ov.Pivo.parent.localScale.x:F3}");
            }
            // anus: entrance center, closest capsule segments to it and the opening they give
            foreach (var kv in _anusCenters)
            {
                var center = (Vector3)kv.Value / kv.Value.w;
                var lines = new List<(float, string)>();
                foreach (var c in _capsulas)
                {
                    if (c.A == null || c.B == null) continue;
                    Vector3 a = c.A.position, b = c.B.position;
                    if (c.Estende) { var ab = b - a; a = b; b = b + ab; }
                    var s = b - a;
                    float tt = Mathf.Clamp01(Vector3.Dot(center - a, s) / Mathf.Max(s.sqrMagnitude, 1e-12f));
                    float dist = (center - (a + tt * s)).magnitude, r = c.Raio * Mathf.Abs(c.A.lossyScale.x);
                    bool vis = c.Vis == null || (c.Vis.enabled && c.Vis.gameObject.activeInHierarchy);
                    lines.Add((dist, $"{c.A.name}->{c.B.name}{(c.Estende ? "+" : "")} dist {dist:F3} t {tt:F2} raio {r:F3} abre {r + AnusReach * Mathf.Abs(kv.Key.lossyScale.x) - dist:F3}{(vis ? "" : " (oculta)")}"));
                }
                lines.Sort((x, y) => x.Item1.CompareTo(y.Item1));
                UncensorPlugin.Logger.Info($"[GEN] anus {kv.Key.name} center {center}:");
                for (int i = 0; i < Mathf.Min(5, lines.Count); i++) UncensorPlugin.Logger.Info($"[GEN]   {lines[i].Item2}");
            }
        }

        // Another uncensor owns the genitals: ours (meshes, bone rebinding, vulva/anus bones, penis stretch and aim,
        // collision) stays off so the two do not fight over the same SkinnedMeshRenderers and bones. Mosaic removal
        // and Freemode are not affected. Checked lazily: during our Load() the plugins after us are not loaded yet.
        private static bool? _otherUncensor;
        private static readonly System.Text.RegularExpressions.Regex OtherUncensorName = new System.Text.RegularExpressions.Regex(
            @"(^|[^a-z])al[_.\- ]?uncensor|uncensorselector", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        internal static bool OtherUncensor => _otherUncensor ??= DetectOtherUncensor();

        private static bool DetectOtherUncensor()
        {
            var seen = new List<string>();
            try
            {
                foreach (var kv in BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Plugins)
                {
                    var info = kv.Value;
                    if (info?.Metadata == null || info.Metadata.GUID == UncensorPlugin.PluginGuid) continue;
                    string id = $"{info.Metadata.GUID} | {info.Metadata.Name} | {System.IO.Path.GetFileName(info.Location ?? "")}";
                    seen.Add(id);
                    if (!OtherUncensorName.IsMatch(id)) continue;
                    UncensorPlugin.Logger.LogWarning($"[GEN] other uncensor detected ({id}): Amanatsu 3D genitals, bones and collision turned off.");
                    return true;
                }
            }
            catch (Exception ex) { UncensorPlugin.Logger.LogWarning($"[GEN] other-uncensor detection failed: {ex.Message}"); }
            UncensorPlugin.Logger.Info($"[GEN] no other uncensor; plugins: {string.Join("; ", seen)}");
            return false;
        }

        /// <summary>Chamado por frame pelo LateUpdate do Human; varre no maximo 1x por segundo por personagem.</summary>
        internal static void Aplica(Human h)
        {
            if (_dados.Count == 0 && _completas.Count == 0) return;
            if (OtherUncensor) return;
            try { if (Input.GetKeyDown(KeyCode.F9)) { Captura(h); _traceUntil = Time.unscaledTime + TraceSeconds; } }
            catch (Exception ex) { UncensorPlugin.Logger.LogWarning($"[GEN] F9 capture failed: {ex.Message}"); }
            try { AtualizaFisica(); }
            catch (Exception ex) { UncensorPlugin.Logger.LogWarning($"[GEN] physics failed: {ex.Message}"); }
            try { VulvaCoberta(h); }
            catch (Exception ex) { UncensorPlugin.Logger.LogWarning($"[GEN] panties failed: {ex.Message}"); }
            var chave = h.Pointer;
            if (_proximaVarredura.TryGetValue(chave, out var quando) && Time.frameCount < quando) return;
            _proximaVarredura[chave] = Time.frameCount + 60;

            // MEDIDO (log): ObjBody e o GameObject "o_body", so com a malha do corpo. Os genitais ficam em
            // ramos irmaos (cf_o_root/n_body*/n_dankon) - varre a partir do cf_o_root.
            var o = h.GetRefObject(Table.RefObjKey.ObjBody);
            if (o == null) return;
            var t = o.transform;
            while (t.parent != null && t.name != "cf_o_root") t = t.parent;
            var corpo = t.gameObject;
            var smrs = corpo.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            // chave: nome do objeto (a malha pode ser trocada por nos ou pelo jogo)
            var malhasCorpo = new Dictionary<string, SkinnedMeshRenderer>();
            foreach (var r in smrs) malhasCorpo[r.gameObject.name] = r;
            _malhasCorpo[chave] = malhasCorpo;
            // colisores de dedos/penis deste personagem (o esqueleto fica no p_*_body, pai do cf_o_root)
            if (_completas.Count > 0)
            {
                try { CriaColisores(h, smrs); }
                catch (Exception ex) { UncensorPlugin.Logger.LogWarning($"[GEN] colliders failed: {ex.Message}"); }
            }

            Material pele = null;
            foreach (var r in smrs)
            {
                var mat = r.sharedMaterial;
                if (mat != null && mat.shader != null && mat.shader.name == "AL/skin_body") { pele = mat; break; }
            }

            if (_relatados.Add(chave))
            {
                var nomes = new List<string>();
                foreach (var r in smrs) if (r.sharedMesh != null) nomes.Add(r.sharedMesh.name);
                UncensorPlugin.Logger.Info($"[GEN] {corpo.name}: skin={(pele != null ? pele.name : "none")}, meshes=[{string.Join(", ", nomes)}]");
            }

            foreach (var smr in smrs)
            {
                var orig = smr.sharedMesh;
                if (orig == null) continue;

                // decalque do mosaico: some quando a vulva 3D esta no corpo (senao tampa o buraco)
                // mnpa (anus) tambem: o decalque chapado tampava a cavidade
                if ((orig.name == "mnpb" || orig.name == "mnpa") && _completas.Count > 0) { smr.enabled = false; continue; }

                if (_completas.TryGetValue(orig.name, out var comp))
                {
                    if (_trocas.ContainsKey(smr.GetInstanceID())) continue;   // coberta pela calcinha: VulvaCoberta cuida
                    try
                    {
                        if (!_prontas.TryGetValue(orig.Pointer, out var novaC) || novaC == null)
                            _prontas[orig.Pointer] = novaC = MontaCompleta(comp, orig);
                        var ossosOrig = smr.bones;
                        if (comp.Ossos.Length > 0 && !LigaOssos(smr, comp, orig.bindposes))
                        {
                            UncensorPlugin.Logger.LogWarning($"[GEN] {orig.name}: vulva bones did not link; keeping original.");
                            continue;
                        }
                        _trocas[smr.GetInstanceID()] = new Troca
                        {
                            Smr = smr, Dono = h.Pointer, Orig = orig, Nova = novaC, OssosOrig = ossosOrig, OssosNova = smr.bones,
                        };
                        smr.bones = ossosOrig;
                        UncensorPlugin.Logger.Info($"[GEN] {orig.name} ready (3D vulva, {comp.V.Length} vertices, {comp.Ossos.Length} bones).");
                    }
                    catch (Exception ex) { UncensorPlugin.Logger.LogWarning($"[GEN] failed swapping {orig.name}: {ex.Message}"); }
                    continue;
                }

                if (!_dados.TryGetValue(orig.name, out var d)) continue;   // ja trocada ou nao e genital
                try
                {
                    if (!_prontas.TryGetValue(orig.Pointer, out var nova) || nova == null)
                    {
                        nova = Monta(d, orig, smr);
                        if (nova == null) continue;
                        _prontas[orig.Pointer] = nova;
                    }
                    smr.sharedMesh = nova;
                    if (pele != null) smr.sharedMaterial = pele;
                    UncensorPlugin.Logger.Info($"[GEN] {orig.name} swapped ({d.V.Length} vertices).");
                }
                catch (Exception ex) { UncensorPlugin.Logger.LogWarning($"[GEN] failed swapping {orig.name}: {ex.Message}"); }
            }

            TiraRoupaDeBaixo(h, smrs);
        }

        // Corpo feminino com as duas malhas: a original (virilha lisa) e a com vulva 3D, cada uma com seus bones
        private sealed class Troca
        {
            public SkinnedMeshRenderer Smr; public IntPtr Dono;
            public Mesh Orig, Nova; public Transform[] OssosOrig, OssosNova;
        }
        private static readonly Dictionary<int, Troca> _trocas = new Dictionary<int, Troca>();
        private static readonly List<int> _mortas = new List<int>();

        /// <summary>
        /// Com a calcinha (Shorts) vestida por inteiro a vulva nao aparece: volta a malha original. Semi-removida,
        /// tirada ou sem calcinha, entra a vulva 3D. A calca (Bot) nao conta: pode ser uma saia. O estado vem de
        /// HumanCloth.GetClothesStateType (le _clothesState[kind]: 0 vestida, 1 semi, 2 nua). Por quadro, so troca
        /// a malha quando muda.
        /// </summary>
        private static void VulvaCoberta(Human h)
        {
            if (_trocas.Count == 0) return;
            bool coberta = false;
            var roupa = h.Cloth;
            if (roupa != null && roupa.IsExist(HumanCloth.Define.ClothesKind.Shorts))
                coberta = roupa.GetClothesStateType(HumanCloth.Define.ClothesKind.Shorts) == HumanCloth.Define.ClothesState.Clothing;
            _mortas.Clear();
            foreach (var par in _trocas)
            {
                var t = par.Value;
                if (t.Dono != h.Pointer) continue;
                if (t.Smr == null) { _mortas.Add(par.Key); continue; }
                if (t.Nova == null)
                {
                    // malha destruida (versao sem DontUnloadUnusedAsset): volta a original e a varredura remonta
                    t.Smr.sharedMesh = t.Orig; t.Smr.bones = t.OssosOrig;
                    _mortas.Add(par.Key);
                    continue;
                }
                var quer = coberta ? t.Orig : t.Nova;
                if (t.Smr.sharedMesh == quer) continue;
                // bones antes da malha na ida, depois na volta: a contagem sempre bate com os bindposes
                if (coberta) { t.Smr.sharedMesh = quer; t.Smr.bones = t.OssosOrig; }
                else { t.Smr.bones = t.OssosNova; t.Smr.sharedMesh = quer; }
                UncensorPlugin.Logger.Info($"[GEN] {t.Orig.name}: panties {(coberta ? "on, vulva hidden" : "off, vulva visible")}.");
            }
            foreach (var k in _mortas) _trocas.Remove(k);
            if (!coberta) MostraQuadril(h);
        }

        // Por personagem: malhas do corpo por nome (o_upper_type0N, o_lower_type0N, ...)
        private static readonly Dictionary<IntPtr, Dictionary<string, SkinnedMeshRenderer>> _malhasCorpo =
            new Dictionary<IntPtr, Dictionary<string, SkinnedMeshRenderer>>();
        private static readonly Dictionary<IntPtr, string> _estadoQuadril = new Dictionary<IntPtr, string>();

        private static bool Aparece(SkinnedMeshRenderer r) => r != null && r.sharedMesh != null && r.enabled && r.gameObject.activeInHierarchy;

        /// <summary>
        /// MEDIDO (capturas F9): nua o jogo mostra o_onepi_type01 (corpo inteiro). Com o "pano enrolado" mostra so
        /// o o_upper_type01 e nenhuma metade de baixo (corte anti-atravessamento: a roupa deveria tapar), e o
        /// quadril e a vulva somem por baixo dela. Sem calcinha vestida, liga a metade de baixo do mesmo tipo da de
        /// cima (o Generater do jogo troca Upper e Lower pelo mesmo indice). Liga pelo objeto e pelo renderer: nao
        /// se sabe qual dos dois o jogo desligou, e ambos ficam como ele deixaria numa troca de roupa.
        /// </summary>
        private static void MostraQuadril(Human h)
        {
            if (!_malhasCorpo.TryGetValue(h.Pointer, out var malhas)) return;
            SkinnedMeshRenderer baixo = null;
            string cima = null;
            bool temBaixo = false;
            foreach (var par in malhas)
            {
                if (!Aparece(par.Value)) continue;
                if (par.Key.StartsWith("o_upper_type")) cima = par.Key;
                else if (par.Key.StartsWith("o_lower_type") || par.Key.StartsWith("o_onepi_type")) temBaixo = true;
            }
            string estado = cima == null ? "sem metade de cima" : temBaixo ? "quadril visivel" : "quadril escondido";
            if (!_estadoQuadril.TryGetValue(h.Pointer, out var antes) || antes != estado)
            {
                _estadoQuadril[h.Pointer] = estado;
                var partes = new List<string>();
                foreach (var par in malhas)
                    if (par.Key.StartsWith("o_") && !par.Key.StartsWith("o_nail"))
                        partes.Add($"{par.Key}[{(par.Value.sharedMesh != null ? par.Value.sharedMesh.name : "SEM MALHA")}](obj={par.Value.gameObject.activeSelf}/{par.Value.gameObject.activeInHierarchy} " +
                                   $"rend={par.Value.enabled} pai={par.Value.transform.parent?.name})");
                UncensorPlugin.Logger.Info($"[GEN] body: {estado} | {string.Join(", ", partes)}");
            }
            if (cima == null || temBaixo) return;
            if (!malhas.TryGetValue(cima.Replace("upper", "lower"), out baixo)
                && !malhas.TryGetValue("o_lower_type01", out baixo)) return;
            for (var o = baixo.transform; o != null && !baixo.gameObject.activeInHierarchy; o = o.parent) o.gameObject.SetActive(true);
            baixo.enabled = true;
            UncensorPlugin.Logger.Info($"[GEN] hip hidden by clothing without panties: {baixo.name} re-enabled.");
        }

        private static readonly Dictionary<int, bool> _penisVisivelAntes = new Dictionary<int, bool>();

        /// <summary>
        /// O penis 3D atravessava calca e cueca: o jogo o mostra sem tirar a roupa de baixo (o original era uma
        /// capsula curta, escondida por ela). Quando o penis passa a aparecer, calca (Bot) e cueca (Shorts) vao para
        /// Naked pela mesma API da interface de roupa. So na borda (ficou visivel): se o jogador vestir de novo pela
        /// interface, nao brigamos com ele.
        /// </summary>
        private static void TiraRoupaDeBaixo(Human h, SkinnedMeshRenderer[] smrs)
        {
            SkinnedMeshRenderer dan = null;
            foreach (var r in smrs)
                if (r.sharedMesh != null && r.sharedMesh.name.StartsWith("o_dankon")) { dan = r; break; }
            if (dan == null) return;

            bool visivel = dan.enabled && dan.gameObject.activeInHierarchy;
            int chave = dan.GetInstanceID();
            _penisVisivelAntes.TryGetValue(chave, out bool antes);
            _penisVisivelAntes[chave] = visivel;
            if (!visivel || antes) return;

            try
            {
                var roupa = h.Cloth;
                if (roupa == null) return;
                roupa.SetClothesState(HumanCloth.Define.ClothesKind.Bot, HumanCloth.Define.ClothesState.Naked);
                roupa.SetClothesState(HumanCloth.Define.ClothesKind.Shorts, HumanCloth.Define.ClothesState.Naked);
                UncensorPlugin.Logger.Info("[GEN] penis visible: pants and underwear removed.");
            }
            catch (Exception ex) { UncensorPlugin.Logger.LogWarning($"[GEN] removing bottom clothing failed: {ex.Message}"); }
        }

        private static Mesh Monta(Dados d, Mesh orig, SkinnedMeshRenderer smr)
        {
            // indice de cada osso do .bin dentro dos bones do renderer (bindposes da original seguem essa ordem)
            var bones = smr.bones;
            var mapa = new int[d.Ossos.Length];
            for (int i = 0; i < d.Ossos.Length; i++)
            {
                mapa[i] = -1;
                for (int j = 0; j < bones.Length; j++)
                    if (bones[j] != null && bones[j].name == d.Ossos[i]) { mapa[i] = j; break; }
                if (mapa[i] < 0)
                {
                    UncensorPlugin.Logger.LogWarning($"[GEN] {orig.name}: bone {d.Ossos[i]} does not exist in this body; keeping original.");
                    return null;
                }
            }

            int n = d.V.Length;
            var uv = new Vector2[n];
            var pesos = new BoneWeight[n];
            for (int i = 0; i < n; i++)
            {
                uv[i] = d.Uv;
                int b = i * 4;
                pesos[i] = new BoneWeight
                {
                    boneIndex0 = mapa[d.BI[b]], weight0 = d.BW[b],
                    boneIndex1 = mapa[d.BI[b + 1]], weight1 = d.BW[b + 1],
                    boneIndex2 = mapa[d.BI[b + 2]], weight2 = d.BW[b + 2],
                    boneIndex3 = mapa[d.BI[b + 3]], weight3 = d.BW[b + 3],
                };
            }

            // criada em codigo e as vezes fora de qualquer renderer (calcinha vestida): sem isto o
            // UnloadUnusedAssets da troca de cena/roupa a destroi e o corpo fica SEM MALHA (medido)
            var m = new Mesh { name = orig.name + "_kk", hideFlags = HideFlags.DontUnloadUnusedAsset };
            m.vertices = d.V;
            m.normals = d.N;
            m.uv = uv;
            m.triangles = d.I;
            m.boneWeights = pesos;
            m.bindposes = orig.bindposes;
            m.RecalculateBounds();
            m.RecalculateTangents();
            return m;
        }
    }
}
