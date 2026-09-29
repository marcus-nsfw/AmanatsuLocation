"""
Vulva 3D pro corpo feminino do Amanatsu, portada do corpo "SAC Innie" do BetterPenetration (KKS).

Diferente do masculino, nao da pra so trocar uma peca: a virilha do Amanatsu e pele fechada (o mnpb
e so um decalque por cima, onde o jogo pinta o mosaico). Entao, pra cada malha de corpo que cobre a
virilha (o_lower_type01..05, o_onepi_type01..04), offline:
  1. corta os triangulos que o decalque mnpb cobre (o buraco)
  2. recorta do corpo KK a regiao sob o o_mnpb dele (vulva + canal)
  3. alinha: centro do o_mnpb do KK -> centro do mnpb do Amanatsu, escala pela largura dos dois
  4. costura a borda do buraco na borda da peca com uma faixa de triangulos (ordenadas por angulo)
  5. atributos dos vertices novos (pesos, cores, UV1..3) copiados do vertice do Amanatsu mais proximo;
     UV0: pele da borda do buraco por fora, mamilo (tom rosado) onde o KK pesa nos bones cf_J_Vagina_*
Saida: <malha>.bin (malha inteira) nesta pasta; o plugin troca o sharedMesh reaproveitando os
bindposes da original (os indices de bone sao os da propria malha original).

Uso: py -3.13 converte_kk_fem.py <pasta dos .zipmod do BP KKS> <body_00.unity3d original> [--png]
"""
import os, sys, io, struct, zipfile
import numpy as np
import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler

AQUI = os.path.dirname(os.path.abspath(__file__))
RAIO = 0.15  # alcance (unidades do Amanatsu) do encaixe da borda e da mistura de normal/UV com a pele
ARESTA_RIGIDA = 1.2   # L1 de peso entre os cantos acima do qual a aresta do buraco nao recebe ponto (MEDIDO na pose real F9)
RAIO_APLAINA = 0.12   # aplaina a peca perto da borda (MEDIDO na pose real F9: tira o vinco de ~113 graus)
ELEVA_PECA = 0.05    # relevo que sobe o interior da peca (MEDIDO: parte de fora ia de -5 mm pro nivel da pele)
SUAVIZA_RELEVO = int(os.environ.get("SUAVIZA_RELEVO", "20"))   # passos Taubin no deslocamento do relevo (MEDIDO: 20; 60+ piora dobras na pose)
TRANSFERE_RELEVO = 1 # 1 = assenta a peca na pele pelo relevo sobre a virilha lisa do KK (escolhido medindo)
RAIO_RELEVO = 0.08
# rosado (mascara do mamilo no UV2): profundidade abaixo da pele onde comeca e onde fica cheio (unid. da malha,
# 0.01 = 1 mm). "0" desliga.
ROSADO = None if os.environ.get("ROSADO", "0.005,0.025") == "0" else tuple(float(x) for x in os.environ.get("ROSADO", "0.005,0.025").split(","))
ANUS = os.environ.get("ANUS", "1") == "1"                 # segunda passada: anus no mnpa
# "cavity" (padrao): cavidade procedural, estilo anime (nenhum doador do KK tem canal; relevo/enxerto viravam so mancha)
# "relevo" (perfil do KK na malha propria) ou "enxerto" (peca do KK)
ANUS_MODO = os.environ.get("ANUS_MODO", "cavity")
R_ANUS = float(os.environ.get("R_ANUS", "0.012"))         # raio do recorte no KK (MEDIDO: 0.02 ja pegava 86 v da vulva, o anus do KK e colado nela)
ESCALAS_ANUS = [float(x) for x in os.environ.get("ESCALAS_ANUS", "1.05,1.1,1.15,1.2,1.25,1.3,1.4,1.5").split(",")]   # buraco do anus: tenta cada uma
MARGEM_RELEVO_ANUS = float(os.environ.get("MARGEM_RELEVO_ANUS", "0.05"))
PROF_ANUS = -0.025   # alvo do centro do anus (MEDIDO: no KK o centro fica 3.6 mm abaixo do corpo liso -> ~-0.029 aqui; as boas dao -0.023)
JANELA_ANUS =float(os.environ.get("JANELA_ANUS", "0.06"))  # altura da janela do buraco no anus (as nadegas ficam acima)
OSSOS_VULVA = os.environ.get("OSSOS_VULVA", "1") == "1"   # grava os cf_J_Vagina_* (colisao no plugin)
# MEDIDO (simulacao do empurrao dos dedos na pose da captura F9): sem suavizar, 33-37 dobras; 40 passos com
# transicao 3x mais larga, 3 dobras em qualquer raio/limite testado (parado: 1).
SUAVIZA_PESO_VULVA = int(os.environ.get("SUAVIZA_PESO_VULVA", "40"))   # passos de media nos pesos dos ossos da vulva
FADE_VULVA = float(os.environ.get("FADE_VULVA", "3"))   # alcance da transicao pro peso da pele, x RAIO
DENTE_GRAUS =float(os.environ.get("DENTE_GRAUS", "0"))   # canto do buraco com pele mais aguda que isso e cortado
# buraco = contorno da peca x ESCALA_BURACO. MEDIDO: com 0.9 o encaixe comprimia a borda pra dentro e
# dobrava triangulos inclinados; esticar pra fora (>1) nao dobra.
ESCALA_BURACO = float(os.environ.get("ESCALA_BURACO", "1.05"))
DOADOR = os.environ.get("DOADOR_KK", "[KKS][Body][BP] SAC Innie.zipmod")
VARIANTES = [f"o_lower_type0{i}" for i in range(1, 6)] + [f"o_onepi_type0{i}" for i in range(1, 5)]


def carrega(m):
    h = MeshHandler(m)
    h.process()
    n = h.m_VertexCount
    nbind = len(m.m_BindPose)
    arr = lambda x, k: np.array(x, dtype=np.float64).reshape(n, k) if x else None
    C = arr(h.m_Colors, 4)
    # MEDIDO: cor do vertice e UNorm8 no bundle e o MeshHandler devolve 0..255; o Unity espera 0..1.
    # Sem isso o shader de pele (que usa a cor do vertice) estoura/infla a malha.
    if C is not None and C.max() > 1.0:
        C = C / 255.0
    return dict(
        V=arr(h.m_Vertices, 3), N=arr(h.m_Normals, 3), T=arr(h.m_Tangents, 4), C=C,
        UV=[arr(getattr(h, f"m_UV{i}"), 2) for i in range(4)],
        BI=np.array(h.m_BoneIndices, dtype=np.int64).reshape(n, 4) if h.m_BoneIndices else None,
        BW=np.array(h.m_BoneWeights, dtype=np.float64).reshape(n, 4) if h.m_BoneWeights else None,
        I=np.array(h.m_IndexBuffer, dtype=np.int64).reshape(-1, 3),
        nbind=nbind,
    )


def malhas(env, nome):
    out = []
    for o in env.objects:
        if o.type.name == "Mesh":
            m = o.read()
            if m.m_Name == nome:
                out.append(m)
    return out


def doador(pasta_bp):
    z = zipfile.ZipFile(os.path.join(pasta_bp, DOADOR))
    env = UnityPy.load(io.BytesIO(z.read(next(n for n in z.namelist() if n.endswith(".unity3d")))))
    corpo = mnpb = liso = mnpa_kk = None
    for o in env.objects:
        if o.type.name != "SkinnedMeshRenderer":
            continue
        r = o.read()
        if not r.m_Mesh:
            continue
        m = r.m_Mesh.read()
        ossos = [b.read().m_GameObject.read().m_Name for b in r.m_Bones]
        if m.m_Name == "o_body_a" and any(b.startswith("cf_J_Vagina") for b in ossos):
            corpo = (carrega(m), ossos)
            # ossos da vulva: matriz do osso e do pivo dele (pai) no espaco da malha, no repouso
            corpo[0]["ossos_vag"] = []
            for i, b in enumerate(r.m_Bones):
                if not ossos[i].startswith("cf_J_Vagina"):
                    continue
                Bp = m.m_BindPose[i]
                W = np.linalg.inv(np.array([[getattr(Bp, f"e{a}{c}") for c in range(4)] for a in range(4)]))
                t = b.read(); p, q = t.m_LocalPosition, t.m_LocalRotation
                x, y, z, w = q.x, q.y, q.z, q.w
                L = np.eye(4); L[:3, 3] = (p.x, p.y, p.z)
                L[:3, :3] = [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                             [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                             [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]
                corpo[0]["ossos_vag"].append((ossos[i], i, W @ np.linalg.inv(L), W))
        elif m.m_Name == "o_body_a":
            c = carrega(m)   # corpo KK padrao, virilha LISA (mesmo espaco do Innie): base do relevo
            if liso is None or len(c["V"]) > len(liso["V"]):
                liso = c
        elif m.m_Name == "o_mnpb" and mnpb is None and "cf_j_kokan" in ossos:
            mnpb = carrega(m)
        elif m.m_Name == "o_mnpa" and "cf_j_ana" in ossos:
            h = MeshHandler(m); h.process()   # (carrega falha: pesos do decalque vem em outro formato)
            mnpa_kk = np.array(h.m_Vertices, dtype=np.float64).reshape(-1, 3)
    corpo[0]["liso"] = liso
    corpo[0]["mnpa"] = mnpa_kk
    return corpo, mnpb


def solda(V, tol=1e-4):
    """indice canonico por posicao (o Amanatsu duplica vertices nas costuras de UV)."""
    chave = np.round(V / tol).astype(np.int64)
    _, canon, inv = np.unique(chave, axis=0, return_index=True, return_inverse=True)
    return canon[inv.ravel()]


def conta_arestas(I, canon):
    from collections import Counter
    ar = Counter()
    for t in canon[I]:
        for a, b in ((t[0], t[1]), (t[1], t[2]), (t[2], t[0])):
            ar[(min(a, b), max(a, b))] += 1
    return ar


def laco_borda(I, canon, so_estas=None):
    """maior laco de borda (arestas usadas 1x) com vertices soldados.
    so_estas: restringe a um conjunto de arestas (ex.: so as que o corte abriu)."""
    from collections import defaultdict
    ar = conta_arestas(I, canon)
    viz = defaultdict(list)
    for (a, b), c in ar.items():
        if c == 1 and (so_estas is None or (a, b) in so_estas):
            viz[a].append(b); viz[b].append(a)
    lacos, vistos = [], set()
    for ini in viz:
        if ini in vistos:
            continue
        laco, ant, cur = [ini], None, ini
        vistos.add(ini)
        while True:
            prox = [v for v in viz[cur] if v != ant and v not in vistos]
            if not prox:
                break
            ant, cur = cur, prox[0]
            vistos.add(cur); laco.append(cur)
        lacos.append(laco)
    return max(lacos, key=len)


def arco(L, V):
    P = V[list(L) + [L[0]]]
    s = np.r_[0, np.cumsum(np.linalg.norm(np.diff(P, axis=0), axis=1))]
    return s / s[-1]


def alinha_lacos(A, B, V):
    """mesma orientacao (area no plano xz) e B comecando no vertice mais perto de A[0]."""
    A, B = list(A), list(B)
    area = lambda L: np.sum(V[L][:, 0] * np.roll(V[L][:, 2], -1) - np.roll(V[L][:, 0], -1) * V[L][:, 2])
    if np.sign(area(A)) != np.sign(area(B)):
        B = B[::-1]
    k = int(np.argmin(np.linalg.norm(V[B] - V[A[0]], axis=1)))
    return A, B[k:] + B[:k]


def encaixa(A, B, V, n0, KI, kcanon):
    """Leva a borda da peca (B) exatamente pra borda do buraco (A), pareando por comprimento de arco,
    e deforma o interior de forma HARMONICA (laplaciano = 0 com a borda presa). Sem o encaixe a borda
    do KK ficava mais funda que a do buraco e a costura virava paredes verticais em volta da vulva;
    espalhar por 1/d^2 + remendos locais dobrava triangulos perto da borda."""
    from scipy.sparse import coo_matrix
    from scipy.sparse.linalg import spsolve
    seg, frac = pareia(A, B, V)
    An = np.array(A)
    alvo = V[An[seg]] * (1 - frac)[:, None] + V[An[(seg + 1) % len(An)]] * frac[:, None]

    # laplaciano uniforme no grafo soldado da peca (indices canonicos, locais a peca)
    nk = len(kcanon)
    ar = set()
    for t in kcanon[KI]:
        for a, b in ((t[0], t[1]), (t[1], t[2]), (t[2], t[0])):
            ar.add((min(a, b), max(a, b)))
    ar = np.array(sorted(ar))
    L = coo_matrix((np.ones(2 * len(ar)), (np.r_[ar[:, 0], ar[:, 1]], np.r_[ar[:, 1], ar[:, 0]])), shape=(nk, nk)).tocsr()
    grau = np.asarray(L.sum(1)).ravel()
    L = (coo_matrix((grau, (np.arange(nk), np.arange(nk))), shape=(nk, nk)) - L).tocsr()
    presos = np.array([b - n0 for b in B])
    livres = np.setdiff1d(np.unique(kcanon[KI]), presos)
    desloc_b = alvo - V[B]
    # epsilon na diagonal: pedaco da peca que nao encosta na borda (casca solta) fica parado em vez de singular
    Lii = L[livres][:, livres] + 1e-6 * coo_matrix((np.ones(len(livres)), (np.arange(len(livres)), np.arange(len(livres))))).tocsr()
    Lib = L[livres][:, presos]
    desloc_i = np.stack([spsolve(Lii.tocsc(), -Lib @ desloc_b[:, k]) for k in range(3)], axis=1)
    d = np.zeros((nk, 3))
    d[presos] = desloc_b
    d[livres] = desloc_i
    V[n0:] = V[n0:] + d[kcanon]
    return seg, frac


def pareia(A, B, V):
    """Para cada vertice da borda da peca (B), o ponto do buraco onde ele encosta: (segmento de A,
    fracao no segmento). Pareia por trechos entre 4 ancoras presentes nas duas bordas (frente, tras,
    esquerda, direita no plano xz) e, dentro de cada trecho, por comprimento de arco da borda do KK
    suavizada. (Um arco global a partir de um ponto so escorregava onde uma borda e mais serrilhada que
    a outra - girava a peca, uma metade esticava mais que a outra.)"""
    A, B = list(A), list(B)
    Bs = V[B].copy()
    for _ in range(15):   # a borda recortada por triangulos e serrilhada
        Bs = 0.5 * Bs + 0.25 * (np.roll(Bs, 1, 0) + np.roll(Bs, -1, 0))
    PA = V[A]
    def ancoras(P):
        return [int(np.argmin(P[:, 0])), int(np.argmin(P[:, 2])), int(np.argmax(P[:, 0])), int(np.argmax(P[:, 2]))]
    ka, kb = ancoras(PA), ancoras(Bs)
    # ordena as ancoras pela posicao no laco de A (mesma orientacao dos dois lacos)
    ordem = np.argsort(ka)
    ka = [ka[i] for i in ordem]; kb = [kb[i] for i in ordem]
    na, nb = len(A), len(B)
    def arco_trecho(P, i0, i1, n):
        idx = [(i0 + k) % n for k in range(((i1 - i0) % n) + 1)]
        s = np.r_[0, np.cumsum(np.linalg.norm(np.diff(P[idx], axis=0), axis=1))]
        return idx, s / max(s[-1], 1e-12)
    seg = np.zeros(nb, dtype=np.int64)
    frac = np.zeros(nb)
    for j in range(4):
        ia, sa = arco_trecho(PA, ka[j], ka[(j + 1) % 4], na)
        ib, sb = arco_trecho(Bs, kb[j], kb[(j + 1) % 4], nb)
        for b, t in zip(ib, sb):
            k = min(int(np.searchsorted(sa, t, side="right")) - 1, len(ia) - 2)
            k = max(k, 0)
            seg[b] = ia[k]
            frac[b] = np.clip((t - sa[k]) / max(sa[k + 1] - sa[k], 1e-12), 0, 1)
    return seg, frac


def costura(A, B, V):
    """Triangula a faixa entre dois lacos fechados, seguindo a ORDEM de cada laco por comprimento de
    arco (lacos ja alinhados por alinha_lacos). Cada aresta dos dois lacos entra em exatamente um
    triangulo - fecha sem fresta. (Por angulo em volta do centro falhava na frente, onde a pele sobe
    e o laco nao e 'estrelado': sobravam frestas e arestas repetidas.)"""
    ta, tb = arco(A, V), arco(B, V)
    na, nb = len(A), len(B)
    tris, i, j = [], 0, 0
    while i < na or j < nb:
        a0, b0 = A[i % na], B[j % nb]
        if j >= nb or (i < na and ta[i + 1] <= tb[j + 1]):
            tris.append((a0, A[(i + 1) % na], b0)); i += 1
        else:
            tris.append((a0, B[(j + 1) % nb], b0)); j += 1
    return tris


def projeta(P, V, T):
    """Para cada ponto de P: (triangulo de T mais proximo, coordenadas baricentricas do ponto mais
    proximo nele). Usado pra herdar UV/cor da pele que foi removida."""
    A, B, C = V[T[:, 0]], V[T[:, 1]], V[T[:, 2]]
    AB, AC = B - A, C - A
    n = np.cross(AB, AC)
    nn = np.einsum("ij,ij->i", n, n) + 1e-20
    tri = np.zeros(len(P), dtype=np.int64)
    bar = np.zeros((len(P), 3))
    def no_segmento(p, X, Y):
        d = Y - X
        t = np.clip(np.einsum("ij,ij->i", p - X, d) / (np.einsum("ij,ij->i", d, d) + 1e-20), 0, 1)
        return X + d * t[:, None], t
    pp_shape = A.shape
    for i, p in enumerate(P):
        # projecao no plano de cada triangulo; vale so se cair dentro dele
        AP = p - A
        w2 = np.einsum("ij,ij->i", np.cross(AB, AP), n) / nn
        w1 = np.einsum("ij,ij->i", np.cross(AP, AC), n) / nn
        w0 = 1 - w1 - w2
        dentro = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
        W = np.stack([w0, w1, w2], 1)
        Q = A * w0[:, None] + B * w1[:, None] + C * w2[:, None]
        dist = np.where(dentro, np.linalg.norm(Q - p, axis=1), np.inf)
        # fora: ponto mais proximo nas 3 arestas
        pp = np.broadcast_to(p, pp_shape)
        for (X, Y, ia, ib) in ((A, B, 0, 1), (B, C, 1, 2), (C, A, 2, 0)):
            q, t = no_segmento(pp, X, Y)
            dq = np.linalg.norm(q - p, axis=1)
            troca = dq < dist
            Wn = np.zeros_like(W); Wn[:, ia] = 1 - t; Wn[:, ib] = t
            W = np.where(troca[:, None], Wn, W)
            dist = np.where(troca, dq, dist)
        k = int(np.argmin(dist))
        tri[i], bar[i] = k, W[k]
    return tri, bar


def area_dobrada(P, KI, ref):
    """Area de cada triangulo da peca que esta DOBRADO (normal contra a orientacao original do KK), 0 se nao.
    As travas comparam antes/depois: MEDIDO - olhar so dobra NOVA deixava passar uma dobra de area 2e-6
    que o relevo aumentava 400x (o_onepi_type04)."""
    f_ = np.cross(P[KI[:, 1]] - P[KI[:, 0]], P[KI[:, 2]] - P[KI[:, 0]])
    a = np.linalg.norm(f_, axis=1) / 2
    return np.where(np.einsum("ij,ij->i", f_, ref) < 0, a, 0.0)


def maior_pedaco(KI, kcanon):
    """So o maior pedaco conectado da peca. MEDIDO: o recorte + limpeza de pincas deixava 2 triangulos
    soltos (x=+-0.2) sem ligacao com nada - pesos pela media da borda, ficavam parados no ar quando as
    pernas abriam."""
    from collections import defaultdict
    viz = defaultdict(set)
    for t in kcanon[KI]:
        for a in t:
            viz[a].update(t)
    visto, maior = set(), set()
    for s in viz:
        if s in visto:
            continue
        pilha, comp = [s], set()
        while pilha:
            x = pilha.pop()
            if x not in comp:
                comp.add(x); pilha += list(viz[x])
        visto |= comp
        if len(comp) > len(maior):
            maior = comp
    return KI[np.isin(kcanon[KI], list(maior)).all(1)]


def sem_orelhas(KI, kcanon):
    """Tira "orelhas" da borda da peca: triangulo com 2+ arestas na borda. MEDIDO (pose REAL capturada
    no jogo com F9): o vertice do meio da orelha era soldado num canto do buraco, o triangulo ficava
    com os 3 vertices na borda - uma linha parada, com pesos diferentes - e na pose abria como uma aba
    na frente da virilha, dos dois lados. Sem a orelha, a borda passa pela aresta interna dela."""
    for _ in range(30):
        ar = conta_arestas(KI, kcanon)
        ct = kcanon[KI]
        n_b = sum(np.array([ar[(min(a, b), max(a, b))] == 1 for a, b in zip(ct[:, i], ct[:, (i + 1) % 3])])
                  for i in range(3))
        orelhas = n_b >= 2
        if not orelhas.any():
            break
        KI = KI[~orelhas]
    return KI


def _buraco_e_encaixe(am, V, KV, KI, kcanon, externa, borda_kk, n0, cortar=frozenset(), janela=0.3, escala=None):
    """Abre o buraco no formato da peca e encaixa a borda da peca nele. Devolve
    (corta, fica, borda_am, borda_kk, Vtot, seg, frac) - lacos ja alinhados entre si."""
    from matplotlib.path import Path as Poligono
    from collections import Counter, defaultdict
    # buraco no FORMATO DA PECA: triangulos do Amanatsu cujo centro cai dentro do contorno da peca
    # visto de baixo (plano xz), um pouco encolhido. (Cortar pelo decalque mnpb abria um buraco bem
    # maior que a peca na frente, e esticar a peca ate ele gerava triangulos longos e dobras.)
    contorno = KV[externa][:, [0, 2]]
    cc = contorno.mean(0)
    C = V[am["I"]].mean(1)
    dentro = Poligono((contorno - cc) * (escala or ESCALA_BURACO) + cc).contains_points(C[:, [0, 2]])
    # janela de altura: no anus (fundo do sulco entre as nadegas) as paredes das nadegas caem dentro do contorno
    # visto de baixo; so a janela estreita separa o fundo delas
    corta = dentro & (C[:, 1] > KV[:, 1].min() - janela) & (C[:, 1] < KV[:, 1].max() + janela)
    canon = solda(V)
    if cortar:   # triangulos de pele sob um fechamento do avesso (ver monta)
        ct_ = canon[am["I"]]
        corta |= np.array([any(tuple(sorted((int(t[i]), int(t[(i + 1) % 3])))) in cortar for i in range(3)) for t in ct_])
    # o recorte cru deixa "pincas" (triangulos cortados que se tocam so por um vertice): a borda vira
    # um 8 e o laco para no meio. Corta tambem os triangulos em volta de cada pinca ate virar um disco.
    antes = conta_arestas(am["I"], canon)
    for _ in range(30):
        grau = Counter()
        abertas = set()
        for e, c in conta_arestas(am["I"][~corta], canon).items():
            if c == 1 and antes.get(e) == 2:
                grau.update(e); abertas.add(e)
        pincas = [v for v, g in grau.items() if g > 2]
        # "orelhas": triangulo de pele com 2+ arestas na borda do buraco (espetado pra dentro) - a
        # subdivisao so enxerga uma aresta e o leque do canto recriava o mesmo triangulo por cima
        ct = canon[am["I"]]
        n_ab = sum(np.array([(min(a, b), max(a, b)) in abertas for a, b in zip(ct[:, i], ct[:, (i + 1) % 3])])
                   for i in range(3))
        orelhas = (~corta) & (n_ab >= 2)
        # "dentes": canto do buraco onde a pele que sobrou forma angulo agudo (ponta de pele entrando no
        # buraco, cada triangulo com 1 aresta so). MEDIDO: nenhum leque consegue fechar ali sem ficar do
        # avesso (canto 3533 do o_onepi_type01 = triangulos escuros vistos no jogo). Corta a ponta.
        ang = defaultdict(float)
        for t in np.where(~corta)[0]:
            for i in range(3):
                v0 = ct[t, i]
                if v0 in grau:
                    p, q, r = V[am["I"][t, i]], V[am["I"][t, (i + 1) % 3]], V[am["I"][t, (i + 2) % 3]]
                    u_, w_ = q - p, r - p
                    ang[v0] += np.arccos(np.clip(u_ @ w_ / (np.linalg.norm(u_) * np.linalg.norm(w_) + 1e-20), -1, 1))
        dentes = [v for v, a in ang.items() if np.degrees(a) < DENTE_GRAUS]
        if os.environ.get("DEBUG_DENTE") and _ == 0:
            print("   angulos de pele nos cantos do buraco (menores):", sorted(round(float(np.degrees(a))) for a in ang.values())[:8])
        if not pincas and not orelhas.any() and not dentes:
            break
        if pincas:
            corta |= np.isin(ct, pincas).any(1)
        if dentes:
            corta |= (~corta) & np.isin(ct, dentes).any(1)
        corta |= orelhas
    fica = am["I"][~corta]
    # borda do buraco = arestas que eram internas (2 triangulos) e viraram borda com o corte.
    # (pegar o maior laco do anel em volta do buraco dava a borda EXTERNA do anel: a costura ia por
    # cima da pele que ficou e gerava as lascas vistas no jogo)
    abertas = {e for e, c in conta_arestas(fica, canon).items() if c == 1 and antes.get(e) == 2}
    borda_am = laco_borda(fica, canon, abertas)

    Vtot = np.vstack([V, KV])
    borda_am, borda_kk = alinha_lacos(borda_am, borda_kk, Vtot)
    seg, frac = encaixa(borda_am, borda_kk, Vtot, n0, KI, kcanon)
    return corta, fica, borda_am, borda_kk, Vtot, (seg, frac)


def monta(am, am_mnpb, kk, kk_ossos, kk_mnpb, uv_mamilo, forca=frozenset(), cortar=frozenset(), regiao=None):
    V = am["V"]
    lo, hi = am_mnpb["V"].min(0), am_mnpb["V"].max(0)

    # 1. peca do KK sob o o_mnpb dele (sem as faixas de coxa que descem)
    klo, khi = kk_mnpb["V"].min(0), kk_mnpb["V"].max(0)
    KC = kk["V"][kk["I"]].mean(1)
    # MEDIDO: sem teto de altura a faixa estreita em x/z pegava tambem o pescoco do KK (y 0.27..0.38),
    # que virava lasca presa no quadril. Limita a vulva + profundidade do canal.
    regiao = regiao or {}
    sel = ((abs(KC[:, 0] - (klo[0] + khi[0]) / 2) <= (khi[0] - klo[0]) / 2) & (KC[:, 2] >= klo[2]) & (KC[:, 2] <= khi[2])
           & (KC[:, 1] >= klo[1] - 0.004) & (KC[:, 1] <= khi[1] + regiao.get("teto", 0.08)))
    # mesma limpeza de pincas do buraco, agora tirando triangulos da peca
    kcan = solda(kk["V"])
    from collections import Counter
    for _ in range(20):
        grau = Counter()
        for e, c in conta_arestas(kk["I"][sel], kcan).items():
            if c == 1:
                grau.update(e)
        pincas = [v for v, g in grau.items() if g > 2]
        if not pincas:
            break
        sel &= ~np.isin(kcan[kk["I"]], pincas).any(1)
    KI = kk["I"][sel]
    usados = np.unique(KI)
    remap = -np.ones(len(kk["V"]), dtype=np.int64); remap[usados] = np.arange(len(usados))
    KI = remap[KI]

    # 3. alinhamento pelos dois mnpb (mesmos eixos nos dois jogos)
    s = regiao.get("s") or min((hi[0] - lo[0]) / (khi[0] - klo[0]), (hi[2] - lo[2]) / (khi[2] - klo[2]))
    c_am, c_kk = (lo + hi) / 2, (klo + khi) / 2
    KV = (kk["V"][usados] - c_kk) * s + c_am
    KN = kk["N"][usados]

    # TRANSFERENCIA DE RELEVO: altura de cada ponto sobre a virilha LISA do proprio KK (corpo padrao, mesmo
    # espaco), reaplicada sobre a pele do Amanatsu ao longo da normal dela. MEDIDO: os grandes labios do
    # Innie ficam RENTES a virilha lisa do KK (mediana 0 mm); encaixar a peca inteira pela borda nao
    # preservava isso e eles ficavam 2-8 mm abaixo da pele do Amanatsu - a "cavidade".
    if os.environ.get("TRANSFERE_RELEVO", str(TRANSFERE_RELEVO)) == "1" and kk.get("liso") is not None:
        L = kk["liso"]
        Pk = kk["V"][usados]
        Cl = L["V"][L["I"]].mean(1)
        lo_k, hi_k = Pk.min(0) - 0.05, Pk.max(0) + 0.05
        base_k = L["I"][((Cl >= lo_k) & (Cl <= hi_k)).all(1)]
        tk, bk = projeta(Pk, L["V"], base_k)
        Qk = np.einsum("ij,ijk->ik", bk, L["V"][base_k[tk]])
        Nk = np.einsum("ij,ijk->ik", bk, L["N"][base_k[tk]]); Nk /= np.linalg.norm(Nk, axis=1, keepdims=True)
        h = np.einsum("ij,ij->i", Pk - Qk, Nk) * s                 # altura, ja na escala do Amanatsu
        q_am = (Qk - c_kk) * s + c_am                              # ponto-base levado pro Amanatsu...
        Ca = V[am["I"]].mean(1)
        m_rel = regiao.get("margem_relevo", 0.3)   # anus: estreita (no sulco, a parede da nadega fica mais perto que o fundo)
        lo_a, hi_a = q_am.min(0) - m_rel, q_am.max(0) + m_rel
        base_a = am["I"][((Ca >= lo_a) & (Ca <= hi_a)).all(1)]
        ta, ba = projeta(q_am, V, base_a)                          # ...e assentado na pele dele
        Qa = np.einsum("ij,ijk->ik", ba, V[base_a[ta]])
        Na = np.einsum("ij,ijk->ik", ba, am["N"][base_a[ta]]); Na /= np.linalg.norm(Na, axis=1, keepdims=True)
        # MEDIDO: aplicar Qa + Na*h direto deixava a superficie ondulada (aspereza mediana 0.025 -> 0.039):
        # a projecao no triangulo mais proximo salta entre vizinhos. So o DESLOCAMENTO (varia devagar) e
        # aplicado, suavizado sobre a malha da peca; a forma redonda do KK fica intacta.
        D = Qa + Na * h[:, None] - KV
        kc = solda(KV)
        viz = [[] for _ in range(len(KV))]
        for a, b, d in kc[KI]:
            viz[a] += [b, d]; viz[b] += [a, d]; viz[d] += [a, b]
        tem = np.array([len(x) > 0 for x in viz])
        # Taubin (lambda/mu alternados): media simples encolhia o proprio deslocamento e a cavidade voltava
        for passo in range(SUAVIZA_RELEVO):
            Dc = D[kc]
            M = np.where(tem[:, None], np.array([Dc[x].mean(0) if x else Dc[i] for i, x in enumerate(viz)]), Dc)
            D = (Dc + (0.5 if passo % 2 == 0 else -0.53) * (M - Dc))[kc]
        KV = KV + D
        if os.environ.get("DEBUG_RELEVO") and regiao.get("s"):
            print(f"   relevo: h(KK, x s) min {h.min():+.4f} mediana {np.median(h):+.4f} | desloc medio {np.linalg.norm(D, axis=1).mean():.4f} "
                  f"| Na medio {Na.mean(0).round(2)} | distancia q_am->pele mediana {np.median(np.linalg.norm(q_am - Qa, axis=1)):.4f}")

    n0 = len(V)
    kcanon = solda(KV)
    KV_antes = KV.copy()
    from collections import defaultdict
    # Rodadas: encaixa; se algum triangulo da BORDA da peca dobrar (trechos onde a superficie do KK
    # se curva por cima da propria borda recortada - nenhum espalhamento resolve), descasca esses
    # triangulos, recalcula a borda e repete.
    RODADAS = 8   # MEDIDO: com a remocao de orelhas, 4 rodadas nao convergiam (19 dobras de borda); 8 = 16
    for rodada in range(RODADAS):
        KI = maior_pedaco(KI, kcanon)   # a cada rodada: o recorte e o descascar podem deixar ilhas
        if os.environ.get("SEM_ORELHAS", "1") == "1":
            KI = sem_orelhas(KI, kcanon)
        externa = laco_borda(KI, kcanon)
        # a limpeza de pincas deixa furinhos de 1 triangulo na peca: tapa (laco de 3 arestas fora da borda externa)
        viz = defaultdict(set)
        for (a, b), c in conta_arestas(KI, kcanon).items():
            if c == 1 and not (a in externa and b in externa):
                viz[a].add(b); viz[b].add(a)
        tampas = {tuple(sorted((a, b, c))) for a in viz for b in viz[a] for c in viz[b] if c != a and a in viz[c]}
        if tampas:
            dirs = {(t[i], t[(i + 1) % 3]) for t in kcanon[KI] for i in range(3)}
            extra = []
            for a, b, c in tampas:
                extra.append((a, c, b) if (a, b) in dirs else (a, b, c))   # sentido oposto ao vizinho
            KI = np.vstack([KI, np.array(extra, dtype=np.int64)])
        borda_kk = [n0 + i for i in externa]
        KV = KV_antes.copy()
        corta, fica, borda_am, borda_kk, Vtot, idx = _buraco_e_encaixe(am, V, KV, KI, kcanon, externa, borda_kk, n0, cortar, regiao.get("janela", 0.3), regiao.get("escala_buraco"))
        externa = [b - n0 for b in borda_kk]   # MESMA ordem (alinhada) em que seg/frac foram calculados
        KV = Vtot[n0:]
        fixos = set(externa)
        ref = np.cross(KV_antes[KI[:, 1]] - KV_antes[KI[:, 0]], KV_antes[KI[:, 2]] - KV_antes[KI[:, 0]])
        f = np.cross(KV[KI[:, 1]] - KV[KI[:, 0]], KV[KI[:, 2]] - KV[KI[:, 0]])
        # triangulo que colapsa (2 vertices de borda soldados no mesmo vertice do buraco) nao e dobra: some
        ruins = (np.einsum("ij,ij->i", f, ref) < 0) & (np.linalg.norm(f, axis=1) > 1e-7)
        na_borda_t = np.array([any(v in fixos for v in t) for t in kcanon[KI]])
        if not (ruins & na_borda_t).any() or rodada == RODADAS - 1:   # ultima rodada sempre termina encaixada
            break
        KI = KI[~(ruins & na_borda_t)]
        for _ in range(20):   # descascar pode criar pinca: limpa de novo
            grau = Counter()
            for e, c in conta_arestas(KI, kcanon).items():
                if c == 1:
                    grau.update(e)
            pincas = [v for v, g in grau.items() if g > 2]
            if not pincas:
                break
            KI = KI[~np.isin(kcanon[KI], pincas).any(1)]
    dobras = int(ruins.sum())
    area_dobra = float(np.linalg.norm(f[ruins], axis=1).max() / 2) if ruins.any() else 0.0
    area_media = float(np.median(np.linalg.norm(f, axis=1)) / 2)
    if ruins.any() and os.environ.get("DEBUG_DOBRA"):
        pos_laco = {v: i for i, v in enumerate(externa)}
        for t in kcanon[KI[ruins]]:
            print("   dobra: vertices na borda =", sum(v in fixos for v in t), " area", round(float(np.linalg.norm(np.cross(KV[t[1]] - KV[t[0]], KV[t[2]] - KV[t[0]])) / 2), 5),
                  " posicoes no laco:", [pos_laco.get(v) for v in t], "de", len(externa))

    # aplaina a peca PERTO DA BORDA (MEDIDO na pose real capturada: tira o vinco de ~113 graus entre
    # pele e peca nas laterais, que dobrava com as coxas comprimidas). Puxa cada
    # vertice pro ponto da pele original embaixo dele, forca 1 na borda caindo a 0 no raio.
    R_AP = regiao.get("aplaina", float(os.environ.get("RAIO_APLAINA", str(RAIO_APLAINA))))
    if R_AP > 0:
        d_b = np.linalg.norm(KV[:, None, :] - Vtot[borda_kk][None, :, :], axis=2).min(1)
        w_ap = np.clip(1 - d_b / R_AP, 0, 1) ** 2
        Cc_ = V[am["I"]].mean(1)
        lo_a, hi_a = KV.min(0) - 0.3, KV.max(0) + 0.3
        pele_perto = am["I"][((Cc_ >= lo_a) & (Cc_ <= hi_a)).all(1)]
        mexe = np.where(w_ap > 0)[0]
        tri_a, bar_a = projeta(KV[mexe], V, pele_perto)
        Q = np.full_like(KV, np.nan)
        Q[mexe] = np.einsum("ij,ijk->ik", bar_a, V[pele_perto[tri_a]])
        antes_ap = KV.copy()
        ja = area_dobrada(antes_ap, KI, ref)
        # TRAVA: onde aplainar criar dobra nova (MEDIDO: 1 variante, o_onepi_type04, ganhava uma dobra
        # parada de area 0.0011), reduz o aplainamento dos vertices dela pela metade e refaz
        for tentativa in range(24):
            KV = antes_ap.copy()
            KV[mexe] = antes_ap[mexe] + (Q[mexe] - antes_ap[mexe]) * w_ap[mexe, None]
            KV = KV[kcanon]
            novos_d = area_dobrada(KV, KI, ref) > np.maximum(2 * ja, 1e-6)   # nova OU que cresceu
            if not novos_d.any():
                break
            # nas ultimas tentativas zera de vez (MEDIDO: com so 12 metades, o_onepi_type04 ainda saia
            # com uma dobra parada de area 0.0008)
            w_ap[np.unique(kcanon[KI[novos_d]])] *= 0.5 if tentativa < 16 else 0.0   # no REPRESENTANTE
            w_ap = w_ap[kcanon]
        Vtot[n0:] = KV
    # RELEVO: sobe o interior da peca ao longo da normal da pele (0 na borda, ELEVA_PECA a partir de
    # RAIO_RELEVO pra dentro). MEDIDO: a parte de fora da vulva ficava ~5 mm DENTRO do corpo em relacao a
    # pele original (o corpo "Innie" do KK e rebaixado); com a borda presa na pele o miolo virava uma
    # cavidade. (Transladar a peca inteira nao adianta: o encaixe harmonico devolve o deslocamento.)
    ELEVA = regiao.get("eleva", float(os.environ.get("ELEVA_PECA", str(ELEVA_PECA))))
    if ELEVA:
        R_REL = float(os.environ.get("RAIO_RELEVO", str(RAIO_RELEVO)))
        perto_m = np.linalg.norm(V - c_am, axis=1) < 0.25
        n_pele = am["N"][perto_m].mean(0)
        n_pele /= np.linalg.norm(n_pele)
        d_b = np.linalg.norm(KV[:, None, :] - Vtot[borda_kk][None, :, :], axis=2).min(1)
        tt = np.clip(d_b / R_REL, 0, 1)
        amp = ELEVA * tt * tt * (3 - 2 * tt)
        base_rel = KV.copy()
        ja_r = area_dobrada(base_rel, KI, ref)
        # TRAVA (MEDIDO: sem ela o relevo criava ~22 dobras paradas de area ate 0.0036 onde a rampa
        # cruza as paredes ingremes): onde surgir dobra nova, metade da amplitude naqueles vertices
        for tentativa in range(24):
            KV = (base_rel + n_pele * amp[:, None])[kcanon]
            novos_r = area_dobrada(KV, KI, ref) > np.maximum(2 * ja_r, 1e-6)   # nova OU que cresceu
            if not novos_r.any():
                break
            # nas ultimas tentativas zera de vez (garante que o relevo nunca deixa dobra nova)
            amp[np.unique(kcanon[KI[novos_r]])] *= 0.5 if tentativa < 16 else 0.0   # no REPRESENTANTE (o [kcanon] usa o dele)
            amp = amp[kcanon]
        Vtot[n0:] = KV

    # (MEDIDO na pose real: suavizar a geometria perto da borda com laplaciano PIORA - 6 -> 10 dobras;
    # os vincos fechados sao do proprio modelo da vulva, suavizar so troca de lugar.)

    # dobras na geometria FINAL (depois do aplainamento), contra a orientacao original do KK
    f_fin = np.cross(KV[KI[:, 1]] - KV[KI[:, 0]], KV[KI[:, 2]] - KV[KI[:, 0]])
    ruins_fin = (np.einsum("ij,ij->i", f_fin, ref) < 0) & (np.linalg.norm(f_fin, axis=1) > 1e-7)
    dobras_fin = int(ruins_fin.sum())
    area_fin = float(np.linalg.norm(f_fin[ruins_fin], axis=1).max() / 2) if ruins_fin.any() else 0.0

    # normais da peca refeitas depois da deformacao (media das faces, com costuras de UV soldadas)
    fn = np.cross(KV[KI[:, 1]] - KV[KI[:, 0]], KV[KI[:, 2]] - KV[KI[:, 0]])
    acc = np.zeros_like(KV)
    for k in range(3):
        np.add.at(acc, kcanon[KI[:, k]], fn)
    KN = acc[kcanon]
    KN /= np.linalg.norm(KN, axis=1, keepdims=True) + 1e-12
    # orientacao da peca do KK: confere pela normal original do KK (as recalculadas seguem o enrolamento)
    t0 = KI[0]
    nn = np.cross(KV_antes[t0[1]] - KV_antes[t0[0]], KV_antes[t0[2]] - KV_antes[t0[0]])
    if nn @ kk["N"][usados][t0].mean(0) < 0:
        KI = KI[:, [0, 2, 1]]
        KN = -KN

    # 4b. SOLDA: sem faixa de costura. Cada vertice da borda da peca vira o proprio vertice do buraco
    # em que foi pareado (mesma posicao, peso, normal e UV da pele), entao nao existe fresta pra abrir
    # na pose. (A faixa ligava pontos no meio dos segmentos da pele; como o skinning nao e linear ao
    # longo do segmento, ela abria filetes brancos.)
    # A borda da peca fica NA borda do buraco (pontos sobre os segmentos). Esses pontos viram vertices
    # compartilhados: os triangulos de pele encostados no buraco sao subdivididos em leque pra incluir
    # os pontos, e a peca usa os mesmos indices. Ponto muito perto de um canto do buraco solda no canto.
    seg, frac = idx
    A = np.array(borda_am)
    na = len(A)
    canon = solda(V)
    mapa = np.arange(n0, n0 + len(KV))
    ponto_de = {}   # canonico da borda da peca -> indice global do vertice compartilhado
    # solda no canto pela DISTANCIA (por fracao, num segmento curto o ponto ficava em cima do canto sem
    # soldar - dois vertices no mesmo lugar, arestas com 3 triangulos)
    # Alem disso: em cada canto, o ponto da borda MAIS PROXIMO solda nele se estiver a menos de 30% do
    # segmento (so 1 por canto - varios no mesmo canto colapsavam e abriam buraco). MEDIDO: sem isso
    # 15 de 103 triangulos de pele subdividida tinham angulo < 2 graus (pele original: 0) - lascas que
    # estragam sombreamento/contorno e viram do avesso com a proporcao de cada personagem.
    PERTO = 0.002
    FRACAO_CANTO = float(os.environ.get("FRACAO_CANTO", "0.3"))
    fracs = frac.copy()
    compr = np.linalg.norm(Vtot[A[(np.arange(na) + 1) % na]] - Vtot[A], axis=1)   # segmento k: A[k]->A[k+1]
    candidatos = defaultdict(list)   # canto k -> (dist, j, fracao_nova)
    for j, (c, sgm) in enumerate(zip(externa, seg)):
        p = Vtot[n0 + c]
        a_k, b_k = int(sgm), int((sgm + 1) % na)
        da, db = np.linalg.norm(p - Vtot[A[a_k]]), np.linalg.norm(p - Vtot[A[b_k]])
        lim_a = max(PERTO, FRACAO_CANTO * min(compr[a_k], compr[(a_k - 1) % na]))
        lim_b = max(PERTO, FRACAO_CANTO * min(compr[a_k], compr[b_k]))
        if da < lim_a:
            candidatos[a_k].append((da, j, 0.0))
        if db < lim_b:
            candidatos[b_k].append((db, j, 1.0))
    soldado = {}   # j -> (canto, fracao)
    for k, lst in candidatos.items():
        for dist, j, fr in sorted(lst):
            if j not in soldado:   # 1 ponto por canto, 1 canto por ponto
                soldado[j] = (k, fr, dist)
                break
    # Aresta RIGIDA do buraco: cantos com pesos muito diferentes (coxa x virilha). Ponto inserido no meio
    # dela sai da linha na pose (skinning linear) e dobra a pele subdividida - a pele original nunca tem
    # vertice ali. Nelas, todo ponto da borda solda no canto mais proximo. MEDIDO na pose REAL (F9).
    LIM_RIGIDA = float(os.environ.get("ARESTA_RIGIDA", str(ARESTA_RIGIDA)))
    nb_ = int(am["BI"].max()) + 1
    def peso_denso(v):
        d_ = np.zeros(nb_); np.add.at(d_, am["BI"][v], am["BW"][v]); return d_
    rigida = np.array([np.abs(peso_denso(A[k]) - peso_denso(A[(k + 1) % na])).sum() > LIM_RIGIDA for k in range(na)])
    for j, (c, sgm) in enumerate(zip(externa, seg)):
        if j in soldado:
            k, fr, _ = soldado[j]
            ponto_de[c] = A[k]; fracs[j] = fr
        elif rigida[int(sgm)] or c in forca:   # forca: pontos cujo leque de canto saiu do avesso (abaixo)
            if frac[j] < 0.5:
                ponto_de[c] = A[int(sgm)]; fracs[j] = 0.0
            else:
                ponto_de[c] = A[(int(sgm) + 1) % na]; fracs[j] = 1.0
        else:
            ponto_de[c] = n0 + c
    frac = fracs
    for i, c in enumerate(kcanon):
        if c in ponto_de:
            mapa[i] = ponto_de[c]
    KIg = mapa[KI]
    KIg = KIg[(KIg[:, 0] != KIg[:, 1]) & (KIg[:, 1] != KIg[:, 2]) & (KIg[:, 0] != KIg[:, 2])]
    # soldar os duplicados de costura de UV da borda pode tornar dois triangulos do KK identicos (mesmo
    # SENTIDO). Pares costas-com-costas (membrana de 2 faces do KK) ficam - tirar um abria borda.
    rot = np.array([np.roll(t, -int(np.argmin(t))) for t in KIg])
    _, unicos = np.unique(rot, axis=0, return_index=True)
    KIg = KIg[np.sort(unicos)]

    # pontos inseridos em cada aresta do buraco, em ordem ao longo dela
    na_aresta = defaultdict(list)
    for c, sgm, fr in zip(externa, seg, frac):
        if ponto_de[c] == n0 + c:
            na_aresta[int(sgm)].append((fr, n0 + c))
    # subdivide o triangulo de pele de cada aresta do buraco que recebeu pontos
    fica = [list(t) for t in fica]
    novos = []
    if os.environ.get("DEBUG_ARESTA"):
        for sgm, pts in na_aresta.items():
            a, b = A[sgm], A[(sgm + 1) % na]
            print("   seg", sgm, "a", a, Vtot[a].round(3), "b", b, Vtot[b].round(3), "pts", [(round(f, 3), g, Vtot[g].round(3).tolist()) for f, g in sorted(pts)])
    for sgm, pts in na_aresta.items():
        a, b = A[sgm], A[(sgm + 1) % na]
        pts = [g for _, g in sorted(pts)]
        for k, t in enumerate(fica):
            if t is None:
                continue
            ct = canon[t]
            # triangulo degenerado da malha original (2 vertices na mesma posicao, na costura de UV do
            # meio do maio) nao e dono da aresta - subdividir ele criava aresta com 3 triangulos
            if len(set(ct)) < 3:
                continue
            if a in ct and b in ct:
                ia, ib = list(ct).index(a), list(ct).index(b)
                io = 3 - ia - ib
                va, vb, vo = t[ia], t[ib], t[io]
                cadeia = [va] + pts + [vb]
                if (ib - ia) % 3 != 1:          # no triangulo a aresta vai b->a: percorre ao contrario
                    cadeia = cadeia[::-1]
                novos += [(cadeia[m], cadeia[m + 1], vo) for m in range(len(cadeia) - 1)]
                fica[k] = None
                break
    fica = np.array([t for t in fica if t is not None] + novos, dtype=np.int64)

    # cantos do buraco entre pontos consecutivos da borda que caem em arestas diferentes: fecha o
    # pedacinho (ponto, cantos..., proximo ponto) em leque
    leque = []
    ordem = list(zip(externa, seg, frac))
    for j in range(len(ordem)):
        c1, s1, f1 = ordem[j]
        c2, s2, f2 = ordem[(j + 1) % len(ordem)]
        u, v = ponto_de[c1], ponto_de[c2]
        # cantos de A estritamente entre as posicoes continuas (segmento + fracao) dos dois pontos
        p1, p2 = s1 + f1, s2 + f2
        if p2 < p1 - 1e-9:
            p2 += na
        cantos = [A[k % na] for k in range(int(np.floor(p1)) + 1, int(np.ceil(p2)))]
        cantos = [x for x in cantos if x != u and x != v]
        poli = [u] + cantos + [v]
        # MEDIDO: o buraco faz zigue-zague (canto concavo, ex. 593 -> 3533 -> 3536); o leque saindo
        # sempre de u sobrepunha triangulos e um ficava do avesso (triangulo escuro no jogo). Escolhe o
        # vertice do poligono de onde todos os triangulos apontam pro mesmo lado (normal de Newell).
        P = Vtot[poli]
        nw = np.cross(P, np.roll(P, -1, 0)).sum(0)
        melhor = None
        for r in range(len(poli)):
            q = poli[r:] + poli[:r]
            tris = [(q[0], q[m], q[m + 1]) for m in range(1, len(q) - 1)]
            pior = min((np.cross(Vtot[b] - Vtot[a], Vtot[c] - Vtot[a]) @ nw for a, b, c in tris), default=0)
            if melhor is None or pior > melhor[0]:
                melhor = (pior, tris)
        leque += melhor[1]
    leque = [t for t in leque if len(set(t)) == 3]
    if leque:
        leque = np.array(leque, dtype=np.int64)
        # sentido: a aresta de borda (u->v da peca) ja existe na peca num sentido; o leque usa ao contrario
        # PROPAGA: triangulo do leque que so encosta em outro triangulo do leque precisa do vizinho ja
        # orientado. MEDIDO: decidir so pelos vizinhos de pele/peca deixava 3 arestas com o mesmo sentido
        # em 2 triangulos (face do avesso = buraco escuro com backface culling).
        dirs = {(t[i], t[(i + 1) % 3]) for t in KIg for i in range(3)} | {(t[i], t[(i + 1) % 3]) for t in fica for i in range(3)}
        pend, ok = [np.array(t) for t in leque], []
        while pend:
            resto = []
            for t in pend:
                arestas = [(t[i], t[(i + 1) % 3]) for i in range(3)]
                if any(e in dirs for e in arestas):
                    t = t[[0, 2, 1]]
                elif not any((b, a) in dirs for a, b in arestas):
                    resto.append(t); continue
                ok.append(t)
                dirs |= {(t[i], t[(i + 1) % 3]) for i in range(3)}
            if len(resto) == len(pend):   # isolados: pela normal da pele em volta
                for t in resto:
                    nn = np.cross(Vtot[t[1]] - Vtot[t[0]], Vtot[t[2]] - Vtot[t[0]])
                    ok.append(t if nn @ am["N"][t[t < n0]].mean(0) >= 0 else t[[0, 2, 1]])
                break
            pend = resto
        leque = np.array(ok)
    else:
        leque = np.zeros((0, 3), dtype=np.int64)

    # Triangulo de leque contra a pele em volta = canto do buraco onde a pele e convexa (angulo < 180):
    # com pontos nas 2 arestas do canto, o triangulo entre eles cobre pele em vez de buraco e fica do
    # avesso (triangulo escuro no jogo, MEDIDO na captura F9). Solda esses pontos no canto e remonta.
    # (Cortar mais pele nesses cantos NAO resolve: MEDIDO, o buraco cresce e a pose piora.)
    vir_t = [t for t in leque
             if np.cross(Vtot[t[1]] - Vtot[t[0]], Vtot[t[2]] - Vtot[t[0]]) @ am["N"][t[t < n0]].mean(0) < 0]
    virados = {int(x) - n0 for t in vir_t for x in t if x >= n0}
    # Leque feito SO de cantos (os 2 pontos da borda ja soldados em cantos) e do avesso: nao ha ponto pra
    # soldar - o buraco tem um "dente" de pele que o fechamento cobre. MEDIDO (captura F9): 3 triangulos
    # grandes (area ~1e-3) do avesso ja em repouso = os triangulos vistos de fora. Corta os triangulos de
    # pele que encostam nele (so esses; cortar dentes pelo angulo no buraco todo piorava a pose).
    arestas = {tuple(sorted((int(canon[a]), int(canon[b])))) for t in vir_t if not (t >= n0).any()
               for a, b in ((t[0], t[1]), (t[1], t[2]), (t[2], t[0]))}
    if virados - forca and len(forca) < 40:
        return monta(am, am_mnpb, kk, kk_ossos, kk_mnpb, uv_mamilo, forca | virados, cortar, regiao)
    # cortar muda o buraco e pode criar problema novo (MEDIDO: o_onepi_type04 foi de 3 pra 12 virados):
    # so fica com o corte se ele melhorar (decidido no fim, comparando as duas malhas prontas)
    # MEDIDO NO JOGO: cortar resolveu o triangulo mas SERRILHOU o contorno (o buraco avanca na dobra da coxa;
    # aresta mais longa da costura 0.226 -> 0.301). Padrao agora: ACHATAR o dente (abaixo). CORTA_DENTE=1 volta.
    tenta_corte = os.environ.get("CORTA_DENTE", "0") == "1" and bool(arestas - cortar) and len(cortar) < 60
    if os.environ.get("CORTA_DENTE", "0") != "1":
        # Achata o dente: no leque do avesso feito so de cantos, a aresta que tambem e da PECA fica; o canto
        # oposto (a ponta do dente) vai pro ponto mais proximo dela - o triangulo vira lasca de area 0 e o
        # buraco mantem o formato. Move so esse vertice da pele (e os duplicados de costura de UV dele).
        # MEDIDO: nem todo leque do avesso encosta na aresta da peca (poligono com varios cantos; o do avesso
        # e um interno). Generico: o vertice mais perto da aresta oposta (a ponta do dente) vai pra cima dela;
        # repete, porque mover um canto pode virar um vizinho.
        vira = lambda t: np.cross(Vtot[t[1]] - Vtot[t[0]], Vtot[t[2]] - Vtot[t[0]]) @ am["N"][t[t < n0]].mean(0) < 0
        if os.environ.get("TIRA_ABA", "1") == "1":
            # MEDIDO: o leque do avesso feito so de cantos esta DEITADO sobre a pele do dente (invertido, a
            # normal dele bate 1.0 com os triangulos de pele vizinhos e os vertices opostos ficam do mesmo lado
            # da aresta = aba dupla). A pele de baixo ja cobre a regiao: remove a aba. (O shader de pele desenha
            # as 2 faces: a aba aparecia escura/clara por cima da pele e tambem por dentro do corpo.) Contorno e
            # pele ficam identicos - cortar o dente serrilhou o contorno no jogo; achatar virava um vizinho.
            leque = np.array([t for t in leque if (t >= n0).any() or not vira(t)], dtype=np.int64).reshape(-1, 3)
        for _ in range(0 if os.environ.get("TIRA_ABA", "1") == "1" else 6):
            alvo = [t for t in leque if not (t >= n0).any() and vira(t)]
            if not alvo:
                break
            for t in alvo:
                melhor = None
                for i in range(3):
                    x, y, z = t[i], t[(i + 1) % 3], t[(i + 2) % 3]
                    s_ = Vtot[y] - Vtot[x]
                    q_ = Vtot[x] + np.clip((Vtot[z] - Vtot[x]) @ s_ / (s_ @ s_), 0.05, 0.95) * s_
                    dd = np.linalg.norm(q_ - Vtot[z])
                    if melhor is None or dd < melhor[0]:
                        melhor = (dd, z, q_)
                Vtot[np.where(canon == canon[melhor[1]])[0]] = melhor[2]

    # 5. atributos por interpolacao SUAVE. (Copiar do vertice mais proximo deixava pesos em degraus:
    # na pose, vizinhos da peca seguiam bones diferentes, a peca rasgava em facetas e a borda
    # descolava da pele - os "dentes" vistos no jogo.)
    #  - borda: mistura dos 2 cantos do segmento do buraco onde encostou (mesma fracao do encaixe);
    #    como o ponto agora e vertice da propria pele subdividida, pele e peca deformam juntas
    #  - interior: media dos valores da borda, ponderada por 1/d^2
    a0, a1 = A[seg], A[(seg + 1) % na]
    na_borda = lambda X: X[a0] * (1 - frac)[:, None] + X[a1] * frac[:, None]
    borda_pos = Vtot[[n0 + c for c in externa]]
    d = np.linalg.norm(KV[:, None, :] - borda_pos[None, :, :], axis=2)
    Wd = 1.0 / (d ** 2 + 1e-8)
    Wd /= Wd.sum(1, keepdims=True)
    suave = lambda X: Wd @ na_borda(X)

    # PROJECAO na pele ORIGINAL (a removida + a em volta): cada vertice herda o que a pele tinha no
    # ponto mais proximo (pesos, UVs e cor). Na borda da peca o ponto cai sobre a aresta do buraco,
    # que e a mesma dos 2 lados - da a mesma mistura dos 2 cantos que os vertices compartilhados usam.
    # MEDIDO (pose com o esqueleto real): projetando SO na pele removida, vertice interno perto da borda
    # passava um pouco alem da area removida e grudava no canto do buraco (peso 100% da coxa) - com as
    # pernas abertas ele seguia a coxa e fazia a "ponta" na junta da virilha.
    lo_p, hi_p = KV.min(0) - 0.3, KV.max(0) + 0.3
    Cc = V[am["I"]].mean(1)
    removidos = am["I"][((Cc >= lo_p) & (Cc <= hi_p)).all(1)]
    tri, bar = projeta(KV, V, removidos)
    Tp = removidos[tri]
    projetado = lambda X: np.einsum("ij,ijk->ik", bar, X[Tp])

    # pesos: MEDIDO - a media da borda por 1/d^2 diferia do peso da pele original no mesmo ponto
    # (mediana 0.24, max 0.87 em L1), pior nas laterais da frente: num rig de verdade esses pontos
    # seguiam bones diferentes da pele vizinha e ficavam triangulos soltos no ar com as pernas abertas.
    nb = int(am["BI"].max()) + 1
    dens = np.zeros((len(V), nb))
    np.add.at(dens, (np.repeat(np.arange(len(V)), 4), am["BI"].ravel()), am["BW"].ravel())
    Wk = projetado(dens)
    # (MEDIDO: alisar os pesos dentro da peca NAO reduz as dobras da pose - 11 com 0, 5, 15 ou 40
    # iteracoes - e piora o descolamento de 0.005 pra 0.07. Os pesos projetados ficam como estao.)
    # OSSOS DA VULVA (cf_J_Vagina_* do BetterPenetration), pra colisao em tempo de jogo. Viram filhos do
    # osso que carrega a peca (MEDIDO: cf_s_kokanskin, 98% do peso) e recebem a fatia desse peso que o KK
    # da a eles no mesmo vertice, indo a 0 na borda (a costura continua presa na pele). Parados, movem
    # igual ao pai: a malha fica identica a sem ossos em qualquer pose.
    ossos_vag = []
    if OSSOS_VULVA and kk.get("ossos_vag") and regiao.get("ossos", True):
        vag = [i for _, i, _, _ in kk["ossos_vag"]]
        kkd = np.zeros((len(usados), len(kk_ossos)))
        np.add.at(kkd, (np.repeat(np.arange(len(usados)), 4), kk["BI"][usados].ravel()), kk["BW"][usados].ravel())
        pai = int(np.argmax(Wk.sum(0)))
        fv = kkd[:, vag]
        # MEDIDO (simulacao na pose da captura): com os pesos do KK copiados vertice a vertice, vizinhos
        # seguem ossos diferentes com forca muito diferente e qualquer empurrao rasga em vincos. Media sobre
        # a malha da peca antes de aplicar.
        if SUAVIZA_PESO_VULVA:
            viz = defaultdict(set)
            for a_, b_, c_ in kcanon[KI]:
                viz[a_] |= {b_, c_}; viz[b_] |= {a_, c_}; viz[c_] |= {a_, b_}
            lista = [np.array(sorted(viz[kcanon[i]]), dtype=np.int64) for i in range(len(KV))]
            for _ in range(SUAVIZA_PESO_VULVA):
                fc = fv[kcanon]
                fv = np.array([fc[l].mean(0) if len(l) else fc[i] for i, l in enumerate(lista)])[kcanon]
        fv = fv * (1 - np.clip(1 - d.min(1) / (RAIO * FADE_VULVA), 0, 1) ** 2)[:, None]
        Wk = np.hstack([Wk, np.zeros((len(Wk), am["nbind"] - Wk.shape[1])), Wk[:, [pai]] * fv])
        Wk[:, pai] *= 1 - fv.sum(1)
        # limite de 4 ossos: o osso da vulva mais fraco devolve o peso ao pai (parado e o mesmo osso).
        # MEDIDO: cortar e renormalizar deslocava 11 vertices ate 0.015 na pose real.
        nv = Wk.shape[1] - len(vag)
        for _ in range(len(vag)):
            mais = ((Wk > 0).sum(1) > 4) | (((Wk > 0).sum(1) == 4) & (Wk[:, pai] == 0) & (Wk[:, nv:] > 0).any(1) & ((Wk[:, :nv] > 0).sum(1) == 4))
            if not mais.any():
                break
            Wv = np.where(Wk[:, nv:] > 0, Wk[:, nv:], np.inf)
            j = nv + np.argmin(Wv, 1)
            r = np.where(mais & np.isfinite(Wv.min(1)))[0]
            Wk[r, pai] += Wk[r, j[r]]; Wk[r, j[r]] = 0
        para_am = lambda X: np.r_[np.c_[X[:3, :3] / np.linalg.norm(X[:3, :3], axis=0), (X[:3, 3] - c_kk) * s + c_am], [[0, 0, 0, 1]]]
        # ponytail: pivos na posicao do KK escalada; nao seguem o deslocamento do relevo (so pivo de colisao)
        ossos_vag = [(nome, pai, para_am(Wp), para_am(Wb)) for nome, _, Wp, Wb in kk["ossos_vag"]]
    BIk = np.argsort(-Wk, axis=1)[:, :4]
    BWk = np.take_along_axis(Wk, BIk, axis=1)
    perda = 1 - BWk.sum(1) / Wk.sum(1)   # peso que nao cabe em 4 ossos por vertice
    BWk /= BWk.sum(1, keepdims=True)

    # UV0: por fora, a propria textura da virilha interpolada da borda (continua com a pele em volta -
    # UV da areola na borda deixava filete de outra cor quando a costura abria na pose). O rosado da
    # areola so entra no fundo (peso nos bones de vagina > 0.6), com a transicao escondida na dobra.
    vag = np.array([i for i, b in enumerate(kk_ossos) if b.startswith("cf_J_Vagina")])
    peso_vag = np.array([sum(w for bi, w in zip(kk["BI"][u], kk["BW"][u]) if bi in vag) for u in usados])
    t = np.clip((peso_vag - 0.6) / 0.3, 0, 1)[:, None]
    t = t * t * (3 - 2 * t)
    uv_pele, uv_mam = uv_mamilo
    # UVs e cor tambem PROJETADOS. MEDIDO: interpolar por 1/d^2 misturava as 2 ilhas de UV (esquerda/
    # direita) do UV2/UV3 - a textura (pelos de cada personagem) saia espalhada em zigue-zague.
    # (o "rosado" antigo trocava o UV0 pelo da ponta do seio na textura PRINCIPAL - que e so pele: nao fazia nada
    # alem de puxar outra regiao da textura. O rosa agora vem da mascara do mamilo no UV2, abaixo.)
    uv0 = projetado(am["UV"][0]) if ROSADO else projetado(am["UV"][0]) * (1 - t) + uv_mam * t

    # normal: na borda igual a da pele (interpolada), misturando com a da peca ate RAIO pra dentro
    q = np.clip(1 - d.min(1) / RAIO, 0, 1)[:, None] ** 2
    KN = KN * (1 - q) + suave(am["N"]) * q
    KN /= np.linalg.norm(KN, axis=1, keepdims=True) + 1e-12

    novo = {}
    novo["V"] = Vtot
    novo["N"] = np.vstack([am["N"], KN])
    if am["T"] is not None:
        Tk = suave(am["T"])
        Tk[:, :3] /= np.linalg.norm(Tk[:, :3], axis=1, keepdims=True) + 1e-12
        Tk[:, 3] = np.sign(Tk[:, 3]) + (Tk[:, 3] == 0)
        novo["T"] = np.vstack([am["T"], Tk])
    else:
        novo["T"] = None
    novo["C"] = np.vstack([am["C"], projetado(am["C"])]) if am["C"] is not None else None
    novo["UV"] = [np.vstack([am["UV"][0], uv0])] + [np.vstack([u, projetado(u)]) if u is not None else None for u in am["UV"][1:]]
    if ROSADO and am["UV"][2] is not None:
        # TOM ROSADO pela mascara do MAMILO. MEDIDO: o rosa do mamilo NAO esta na textura principal (a montagem
        # em tempo de jogo so tem pintura/bronzeado); vem da mascara cf_t_nip_0X aplicada pelo shader no UV2
        # (canal 2: em volta do bico ele mapeia a mascara com centro (0.5,0.5) no bico; a virilha inteira e (0,0),
        # transparente). A mascara: fora de r~0.2 transparente, faixa azul = areola (cor da areola da personagem),
        # verde no centro = bico. Vertices da peca ABAIXO da pele (fenda, pequenos labios) recebem UV2 no raio da
        # areola; o resto fica em (0, 0.5) - transparente, e a linha ate o (0,0) da pele nao cruza a mascara.
        Qs = projetado(V); Ns = projetado(am["N"]); Ns /= np.linalg.norm(Ns, axis=1, keepdims=True) + 1e-12
        prof = -np.einsum("ij,ij->i", Vtot[n0:] - Qs, Ns)            # + = abaixo da pele original
        tr = np.clip((prof - ROSADO[0]) / (ROSADO[1] - ROSADO[0]), 0, 1); tr = tr * tr * (3 - 2 * tr)
        r = 0.22 - 0.14 * tr                                          # raio na mascara: 0.22 (borda) -> 0.08 (areola)
        # fora do rosado: o MESMO UV2 da pele em volta (projetado). MEDIDO no jogo: com (0, 0.5) fixo, a faixa da
        # costura (pele (0,0) -> peca (0,0.5)) vazou um contorno rosado numa 2a personagem (aureola/ajustes dela).
        uv2 = np.where((tr > 0)[:, None], np.c_[0.5 - r, np.full(len(r), 0.5)], projetado(am["UV"][2]))
        novo["UV"][2] = np.vstack([am["UV"][2], uv2])
        info_rosado = (int((tr > 0.5).sum()), len(tr))
    else:
        info_rosado = (0, 0)
    novo["BI"] = np.vstack([am["BI"], BIk])
    novo["BW"] = np.vstack([am["BW"], BWk])
    novo["I"] = np.vstack([fica, KIg, leque])
    novo["ossos"] = ossos_vag
    info = dict(cortados=int(corta.sum()), peca_v=len(KV), peca_t=len(KIg), ponte=len(leque),
                borda_am=len(borda_am), borda_kk=len(borda_kk), escala=round(float(s), 2), dobras=dobras,
                area_dobra_max=round(area_dobra, 6), area_tri_mediana=round(area_media, 6),
                dobras_final=dobras_fin, area_dobra_final=round(area_fin, 6),
                ossos=len(ossos_vag), perda_4ossos_max=round(float(perda.max()), 3), rosado=info_rosado)
    info["area_virada"] = round(area_virada(novo["V"], novo["I"], am["I"]), 5)
    if tenta_corte:
        outro, oi = monta(am, am_mnpb, kk, kk_ossos, kk_mnpb, uv_mamilo, forca, cortar | arestas, regiao)
        if (oi["area_virada"], oi["dobras_final"]) < (info["area_virada"], info["dobras_final"]):
            return outro, oi
    return novo, info


def area_virada(V, F, I_orig):
    """Area total dos triangulos que NAO existiam na malha original e apontam contra os vizinhos em repouso
    (de fora viram buraco). Ignora os minusculos do fundo do canal (< 5e-5). Mesmo criterio do confere_virados."""
    from collections import defaultdict
    orig = {tuple(sorted(t)) for t in I_orig.tolist()}
    f = np.cross(V[F[:, 1]] - V[F[:, 0]], V[F[:, 2]] - V[F[:, 0]]); A = np.linalg.norm(f, axis=1) / 2
    N = f / (2 * A[:, None] + 1e-12)
    k = solda(V); viz = defaultdict(list)
    for t, tri in enumerate(k[F]):
        for v in tri: viz[v].append(t)
    total = 0.0
    for t in range(len(F)):
        if A[t] <= 5e-5 or tuple(sorted(F[t].tolist())) in orig:
            continue
        ts = set(x for v in k[F[t]] for x in viz[v]) - {t}
        m = sum(N[x] * A[x] for x in ts)
        if N[t] @ m < 0:
            total += A[t]
    return total


def grava(caminho, m):
    n = len(m["V"])
    with open(caminho, "wb") as f:
        f.write(b"AMBM")
        f.write(struct.pack("<i", n))
        canais = [("V", 3), ("N", 3), ("T", 4), ("C", 4)]
        for k, w in canais:
            tem = m[k] is not None
            f.write(struct.pack("<i", w if tem else 0))
            if tem:
                f.write(np.asarray(m[k], "<f4").tobytes())
        for u in m["UV"]:
            f.write(struct.pack("<i", 2 if u is not None else 0))
            if u is not None:
                f.write(np.asarray(u, "<f4").tobytes())
        f.write(struct.pack("<i", m["I"].size))
        f.write(np.asarray(m["I"], "<i4").tobytes())
        f.write(np.asarray(m["BI"], "<i4").tobytes())
        f.write(np.asarray(m["BW"], "<f4").tobytes())
        # ossos novos (opcional; o plugin le se houver): indice = n de bindposes da original + ordem aqui
        ossos = m.get("ossos") or []
        f.write(struct.pack("<i", len(ossos)))
        for nome, pai, Wp, Wb in ossos:
            b = nome.encode()
            f.write(struct.pack("<i", len(b)) + b + struct.pack("<i", pai))
            f.write(np.asarray(Wp, "<f4").tobytes() + np.asarray(Wb, "<f4").tobytes())   # linha a linha


def uv_do_mamilo(env, raio_pele=0.5):
    """(UV de pele logo fora da areola, UV do centro da areola) no o_upper_type01 - o par que da o
    degrade pele->rosado da propria textura. Mamilo = vertice mais saliente do seio."""
    am = carrega(malhas(env, "o_upper_type01")[0])
    V = am["V"]
    peito = np.where((V[:, 1] > 11.0) & (V[:, 0] > 0.3))[0]
    m = peito[np.argmax(V[peito, 2])]
    d = np.linalg.norm(V[peito] - V[m], axis=1)
    p = peito[np.argmin(abs(d - raio_pele))]
    return am["UV"][0][p], am["UV"][0][m]


def anus(novo, am, env, kk, kk_ossos, uv_m):
    """SEGUNDA PASSADA: o anus do doador na regiao do decalque mnpa, sobre a malha que ja tem a vulva.
    MEDIDO: no KK ha uma depressao de ~9 mm em volta do cf_s_ana (sem canal); a pele do Amanatsu ali e lisa
    e quase sem vertices (2 num raio de 0.04). O decalque do KK e minusculo (1 cm): recorta um raio R_ANUS em
    volta do centro dele, na escala do corpo (8.18). Sem ossos de colisao."""
    if not ANUS or kk.get("mnpa") is None:
        return novo, None
    from UnityPy.helpers.MeshHelper import MeshHandler
    decal = []
    for m in malhas(env, "mnpa"):
        h = MeshHandler(m); h.process(); decal.append(np.array(h.m_Vertices, dtype=np.float64).reshape(-1, 3))
    ca = min(decal, key=len).mean(0)          # o mnpa pequeno (40 v) e o do anus; o de 149 v e maior e difuso
    if ANUS_MODO == "cavity":
        return anus_cavity(novo, am, ca)
    if ANUS_MODO == "relevo":
        return anus_relevo(novo, am, kk, ca)
    ck = kk["mnpa"].mean(0)
    meia = np.array([R_ANUS, 0.012, R_ANUS])
    kbox = {"V": np.array([ck - meia, ck + meia])}
    abox = {"V": np.array([ca - meia * 8.18, ca + meia * 8.18])}
    am2 = dict(novo); am2["nbind"] = am["nbind"] + len(novo.get("ossos") or [])
    # MEDIDO: cada variante tem outra triangulacao no anus; com o buraco 1.05x o contorno, 4 variantes descascavam
    # a peca (76 de 102 tri, 6 dobras) e com 1.2 uma delas ficou perfeita mas outra quebrava no pareamento.
    # Tenta as escalas e fica com a melhor: menos area do avesso, menos dobras, peca mais inteira.
    melhor = None
    for esc in ESCALAS_ANUS:
        try:
            r_ = anus_monta(am2, abox, kk, kk_ossos, kbox, uv_m, esc)
        except Exception as ex:
            print("   anus: escala", esc, "falhou:", type(ex).__name__)
            continue
        # MEDIDO: escolher so pela area do avesso pegou uma saliencia de +4 mm (o_onepi_type01). Nota: defeitos
        # primeiro (dobras + avesso), depois o quanto o centro chega na profundidade do KK, depois peca inteira.
        prof = profundidade_centro(r_[0], am2, ca)
        # MEDIDO: em 3 variantes o encaixe (borda presa na pele) deixava o centro raso (-0.006..0 contra -0.023).
        # Completa o que falta com relevo NEGATIVO (0 na borda, cheio no centro, com as travas de dobra).
        eleva = 0.0
        for _ in range(3):
            falta = PROF_ANUS - prof
            if falta > -0.004:
                break
            eleva += falta
            try:
                r2_ = anus_monta(am2, abox, kk, kk_ossos, kbox, uv_m, esc, eleva)
            except Exception:
                break
            p2 = profundidade_centro(r2_[0], am2, ca)
            if p2 >= prof:
                break
            r_, prof = r2_, p2
        defeito = r_[1]["dobras_final"] + (r_[1]["area_virada"] > 2e-4) * 5
        nota = (defeito, round(abs(prof - PROF_ANUS), 3), -r_[1]["peca_t"])
        if melhor is None or nota < melhor[0]:
            melhor = (nota, r_, esc, prof)
    if melhor is None:
        return novo, None
    novo2, info = melhor[1]; info["escala_buraco"] = melhor[2]; info["prof_centro"] = round(melhor[3], 4)
    novo2["ossos"] = novo.get("ossos") or []
    return novo2, info


def anus_relevo(novo, am, kk, ca):
    """Modo 'relevo': a malha do Amanatsu JA tem topologia de anus (anel + leque em volta do centro), so que plana.
    Aplica nela o PERFIL de profundidade MEDIDO no anus do doador (altura sobre o corpo liso do KK, por raio),
    na escala do corpo, ao longo da normal da pele. Sem costura nem triangulo novo: igual em todas as variantes.
    (O enxerto da peca do KK precisava de buraco 1.2-1.5x maior que ela - pele de triangulos grandes ali - e
    saia raso ou saliente em 3 das 9 variantes.)"""
    L = kk["liso"]; ck = kk["mnpa"].mean(0); Vk = kk["V"]
    selk = np.where((np.linalg.norm(Vk[:, [0, 2]] - ck[[0, 2]], axis=1) < 0.015) & (Vk[:, 1] < ck[1] + 0.02) & (Vk[:, 1] > ck[1] - 0.02))[0]
    Cl = L["V"][L["I"]].mean(1)
    base = L["I"][(np.linalg.norm(Cl[:, [0, 2]] - ck[[0, 2]], axis=1) < 0.05) & (np.abs(Cl[:, 1] - ck[1]) < 0.05)]
    tri, bar = projeta(Vk[selk], L["V"], base)
    Q = np.einsum("ij,ijk->ik", bar, L["V"][base[tri]]); N = np.einsum("ij,ijk->ik", bar, L["N"][base[tri]])
    N /= np.linalg.norm(N, axis=1, keepdims=True)
    hk = np.einsum("ij,ij->i", Vk[selk] - Q, N) * 8.18
    rk = np.linalg.norm(Vk[selk][:, [0, 2]] - ck[[0, 2]], axis=1) * 8.18
    # perfil radial: mediana por faixa, e 0 a partir de onde o KK ja esta rente
    faixas = np.linspace(0, rk.max(), 12)
    prof_r = np.array([np.median(hk[(rk >= a) & (rk < b)]) if ((rk >= a) & (rk < b)).any() else 0.0 for a, b in zip(faixas[:-1], faixas[1:])])
    meio = (faixas[:-1] + faixas[1:]) / 2
    prof_r = np.minimum(prof_r, 0.0)
    # MEDIDO: a ultima faixa (r 0.115) trazia -0.012 da curvatura da nadega do KK - viraria degrau na borda.
    # O anus termina onde o perfil chega a 0 pela primeira vez.
    zero = np.where(prof_r > -1e-4)[0]
    if len(zero): prof_r[zero[0]:] = 0.0
    V = novo["V"].copy(); n0 = len(am["V"])
    k = solda(V)
    r = np.linalg.norm(V[:, [0, 2]] - ca[[0, 2]], axis=1)
    alvo = np.where((np.arange(len(V)) < n0) & (r < meio[-1]) & (np.abs(V[:, 1] - ca[1]) < 0.06))[0]
    d = np.interp(r[alvo], meio, prof_r, left=prof_r[0], right=0.0)
    Nn = novo["N"][alvo] / (np.linalg.norm(novo["N"][alvo], axis=1, keepdims=True) + 1e-12)
    V[alvo] += Nn * d[:, None]
    novo2 = dict(novo); novo2["V"] = V
    # normais da regiao mexida: recalculadas da forma nova (media das faces por area, nos duplicados de costura de UV)
    mexidos = set(k[alvo].tolist())
    F = novo["I"]; toca = np.isin(k[F], list(mexidos)).any(1)
    f = np.cross(V[F[toca, 1]] - V[F[toca, 0]], V[F[toca, 2]] - V[F[toca, 0]])
    acc = np.zeros((len(V), 3))
    for i in range(3): np.add.at(acc, k[F[toca, i]], f)
    recalc = np.array(sorted(set(k[F[toca]].ravel().tolist()) & mexidos))
    Nnovo = novo["N"].copy()
    todos = np.where(np.isin(k, recalc))[0]
    Nnovo[todos] = acc[k[todos]] / (np.linalg.norm(acc[k[todos]], axis=1, keepdims=True) + 1e-12)
    novo2["N"] = Nnovo
    # rosado (mascara do mamilo no UV2), mesmo criterio da vulva: pela profundidade
    if ROSADO and novo["UV"][2] is not None:
        tr = np.clip((-d - ROSADO[0]) / (ROSADO[1] - ROSADO[0]), 0, 1); tr = tr * tr * (3 - 2 * tr)
        uv = [u.copy() if u is not None else None for u in novo["UV"]]
        tem = tr > 0
        uv[2][alvo[tem]] = np.c_[0.5 - (0.22 - 0.14 * tr[tem]), np.full(tem.sum(), 0.5)]
        novo2["UV"] = uv
    info = dict(modo="relevo", vertices=len(alvo), prof_min=round(float(d.min()), 4),
                perfil={round(float(a), 3): round(float(b), 4) for a, b in zip(meio, prof_r)})
    return novo2, info


# Perfil da cavidade: (fracao do raio, altura na normal da pele, peso das pregas, "redondeza").
# MEDIDO nas 9 variantes: o anus e um leque de 4 triangulos (losango 0.066 x 0.032) em volta de um vertice, tudo
# 100% no cf_s_hip_ana. O raio de cada nivel = u * (R(theta) misturado com o circulo CAVITY_ROUND pela redondeza):
# a borda segue o losango, o interior fica redondo. Abaixo da abertura, altura = -profundidade do canal.
# Marcus: em repouso FECHADO (pregas convergindo num ponto); abre pelos ossos do anel quando algo entra.
CAVITY_PROFILE = [
    # Marcus: o losango do leque aparecia porque as pregas iam ate a borda dele. Agora tudo cabe num CIRCULO
    # (0.6 x 0.045 = 0.027, dentro do raio inscrito do losango, 0.029): do circulo pra fora a pele fica igual.
    (0.60, 0.0, 0.0, 1.0),
    (0.47, -0.0015, 0.5, 1.0),
    (0.35, -0.003, 1.0, 1.0),
    (0.23, -0.005, 0.9, 1.0),
    (0.12, -0.010, 0.3, 1.0),   # abertura (CAVITY_OPEN_LEVEL): ~0.005 de raio, praticamente fechada
    (0.10, -0.03, 0.0, 1.0),
    (0.09, -0.07, 0.0, 1.0),
    (0.08, -0.12, 0.0, 1.0),
    (0.07, -0.20, 0.0, 1.0),
    (0.05, -0.30, 0.0, 1.0),
    (0.03, -0.36, 0.0, 1.0),
]
CAVITY_OPEN_LEVEL = 4
CAVITY_BONES = 8                 # ossos em anel na abertura (cf_J_Anus_00..), empurrados pelas capsulas no plugin
# Marcus: "abriu pouco" (0.8 do circulo = 4 mm de diametro, empurrao no limite no F9). A pele em volta agora
# estica junto: cantos do losango e o anel de pele seguinte ganham peso nos ossos do anel (tirado do cf_s_hip_ana).
CAVITY_OPEN_RADIUS = float(os.environ.get("CAVITY_OPEN_RADIUS", "0.04"))   # raio da abertura no maximo
# ate onde a pele acompanha (peso cai linear ate 0). MEDIDO (sim): com 0.14 a pele de fora (0.063+) tem pares de
# vertices colados em que um tem 4 ossos (sem slot) e o outro nao -> 10 triangulos grandes virados. 0.06 = so o leque.
# Abertura 0.04: sobram ~17 paredes de prega viradas (area 0.00016 de 0.32 - a prega se desfazendo); 0.045+ vira 110+.
CAVITY_SPREAD = float(os.environ.get("CAVITY_SPREAD", "0.06"))
CAVITY_CANAL_FULL, CAVITY_CANAL_ZERO = 0.07, 0.20   # canal abre inteiro ate essa fundura e deixa de abrir nesta
CAVITY_APEX = -0.40              # fundo do canal
CAVITY_ROUND = 0.045             # raio do circulo que substitui o losango no interior
CAVITY_FOLDS = int(os.environ.get("CAVITY_FOLDS", "10"))          # pregas radiais
CAVITY_FOLD_DEPTH = float(os.environ.get("CAVITY_FOLD_DEPTH", "0.005"))
CAVITY_SUBDIV = 12               # divisoes de cada aresta do leque (4 x 12 = 48 em volta)
CAVITY_PINK = (0.015, 0.06)      # rosado so dentro do canal (Marcus: com 0..0.015 as pregas rasas pintavam um losango)


def anus_cavity(novo, am, ca):
    """Modo 'cavity': troca o leque do anus por beirada + pregas radiais + canal fechado no fundo, gerados aqui.
    Os vertices de borda do leque ficam; os novos pontos na aresta do leque ficam SOBRE ela (a pele e 100%
    cf_s_hip_ana, entao continuam colineares na pose - sem fresta). Atributos: interpolados no triangulo
    original do leque; pesos = os do centro; UV2 rosado pela profundidade, como na vulva."""
    V = novo["V"]; n0 = len(am["V"]); k = solda(V)
    orig = np.arange(len(V)) < n0
    i0 = np.where(orig)[0][np.argmin(np.linalg.norm(V[orig] - ca, axis=1))]
    fan_mask = (k[novo["I"]] == k[i0]).any(1)
    fan = novo["I"][fan_mask]
    if len(fan) < 3:
        return novo, None
    P0 = V[i0]; nrm = novo["N"][i0] / np.linalg.norm(novo["N"][i0])
    e1 = np.array([1.0, 0, 0]) - nrm * nrm[0]; e1 /= np.linalg.norm(e1); e2 = np.cross(nrm, e1)
    ang = lambda p: np.arctan2((p - P0) @ e2, (p - P0) @ e1)
    S = CAVITY_SUBDIV
    out = {key: [] for key in ("V", "src", "bar", "h", "share", "th")}
    keyed = {}

    def vert(key, pos, tri, bar, h, share=0.0, th=0.0):
        if key not in keyed:
            keyed[key] = len(V) + len(out["V"])
            out["V"].append(pos); out["src"].append(tri); out["bar"].append(bar); out["h"].append(h)
            out["share"].append(share); out["th"].append(th)
        return keyed[key]
    r_open = CAVITY_PROFILE[CAVITY_OPEN_LEVEL][0] * CAVITY_ROUND
    spread = lambda r: float(np.clip((CAVITY_SPREAD - r) / (CAVITY_SPREAD - r_open), 0, 1))
    flat = lambda p: np.linalg.norm((p - P0) - ((p - P0) @ nrm) * nrm)
    bone_of = lambda th: int(round((th % (2 * np.pi)) / (2 * np.pi) * CAVITY_BONES)) % CAVITY_BONES

    tris = []
    edge_u = {}
    for t in fan:
        j = int(np.where(k[t] == k[i0])[0][0]); c, a, b = t[j], t[(j + 1) % 3], t[(j + 2) % 3]
        ta, tb = ang(V[a]), ang(V[b])
        dt = (tb - ta + np.pi) % (2 * np.pi) - np.pi
        ring_prev = []
        for s in range(S + 1):
            # ponto da aresta a-b no raio de angulo theta (interseccao da reta do centro com a aresta)
            th = ta + dt * s / S
            d = np.cos(th) * e1 + np.sin(th) * e2
            A2, B2 = np.array([(V[a] - P0) @ e1, (V[a] - P0) @ e2]), np.array([(V[b] - P0) @ e1, (V[b] - P0) @ e2])
            M = np.c_[[d @ e1, d @ e2], A2 - B2]
            lam, u = np.linalg.solve(M, A2)
            u = float(np.clip(u, 0, 1)) if 0 < s < S else float(s == S)
            if s == 0: ring_prev.append(a)
            elif s == S: ring_prev.append(b)
            else: ring_prev.append(vert(("e", tuple(sorted((a, b))), round(u if a < b else 1 - u, 6), -1),
                                        V[a] * (1 - u) + V[b] * u, (c, a, b), (0.0, 1 - u, u), 0.0))
        rings = [ring_prev]
        for lvl, (fr, hgt, fold, rnd) in enumerate(CAVITY_PROFILE):
            ring = []
            for s in range(S + 1):
                th = ta + dt * s / S; d = np.cos(th) * e1 + np.sin(th) * e2
                A2, B2 = np.array([(V[a] - P0) @ e1, (V[a] - P0) @ e2]), np.array([(V[b] - P0) @ e1, (V[b] - P0) @ e2])
                lam, u = np.linalg.solve(np.c_[[d @ e1, d @ e2], A2 - B2], A2)
                R = abs(lam)
                r = fr * ((1 - rnd) * R + rnd * CAVITY_ROUND)
                g = (0.5 + 0.5 * np.cos(CAVITY_FOLDS * th)) ** 3
                h = hgt - fold * CAVITY_FOLD_DEPTH * g
                pos = P0 + d * r + nrm * h
                rho = min(r / R, 1.0); u = float(np.clip(u, 0, 1))
                # vertices de juncao entre triangulos do leque (s=0/S) sao compartilhados pelo indice real da borda
                key = ("l", lvl, a, "a") if s == 0 else ("l", lvl, b, "a") if s == S else ("l", lvl, c, a, b, s)
                # parte do peso nos ossos do anel: 1 na abertura e no comeco do canal; entre a abertura e a borda
                # cai linear no raio (a borda fica presa, o que esta no meio acompanha sem dobrar)
                if lvl < CAVITY_OPEN_LEVEL:
                    share = -1.0 - (R - r) / (R - r_open)   # marcador: resolvido depois, com o peso da aresta
                else:
                    share = float(np.clip((CAVITY_CANAL_ZERO + hgt) / (CAVITY_CANAL_ZERO - CAVITY_CANAL_FULL), 0, 1))
                ring.append(vert(key, pos, (c, a, b), (1 - rho, rho * (1 - u), rho * u), h, share, th))
                edge_u[keyed[key]] = u
            rings.append(ring)
        apex = vert(("apex", c), P0 + nrm * CAVITY_APEX, (c, a, b), (1.0, 0.0, 0.0), CAVITY_APEX)
        for O, In in zip(rings[:-1], rings[1:]):
            for s in range(S):
                tris += [(In[s], O[s], O[s + 1]), (In[s], O[s + 1], In[s + 1])]
        for s in range(S):
            tris.append((apex, rings[-1][s], rings[-1][s + 1]))

    nn = len(out["V"]); src = np.array(out["src"]); bar = np.array(out["bar"]); hh = np.array(out["h"])
    interp = lambda X: np.einsum("ij,ijk->ik", bar, X[src]) if X is not None else None
    novo2 = dict(novo)
    novo2["V"] = np.r_[V, np.array(out["V"])]
    novo2["I"] = np.r_[novo["I"][~fan_mask], np.array(tris, dtype=np.int64)]
    for key in ("T", "C"):
        novo2[key] = np.r_[novo[key], interp(novo[key])] if novo[key] is not None else None
    uv = []
    for i, U in enumerate(novo["UV"]):
        uv.append(np.r_[U, interp(U)] if U is not None else None)
    # ossos do anel: pivo no limite de abertura daquela direcao (o plugin usa a distancia pivo->osso como
    # empurrao maximo), eixo z do pivo = normal da pele (o plugin tira o empurrao nesse eixo: so abre pros lados)
    hip = int(novo["BI"][i0][np.argmax(novo["BW"][i0])])
    ossos = list(novo.get("ossos") or [])
    base_idx = am["nbind"] + len(ossos)
    rot = np.c_[e1, e2, nrm]
    h_open = CAVITY_PROFILE[CAVITY_OPEN_LEVEL][1]
    for j in range(CAVITY_BONES):
        phi = 2 * np.pi * j / CAVITY_BONES; d = np.cos(phi) * e1 + np.sin(phi) * e2
        Wp = np.eye(4); Wp[:3, :3] = rot; Wp[:3, 3] = P0 + d * CAVITY_OPEN_RADIUS + nrm * h_open
        Wb = np.eye(4); Wb[:3, :3] = rot; Wb[:3, 3] = P0 + d * r_open + nrm * h_open
        ossos.append((f"cf_J_Anus_{j:02d}", hip, Wp, Wb))
    novo2["ossos"] = ossos
    share = np.array(out["share"]); th = np.array(out["th"]) % (2 * np.pi)
    x = th / (2 * np.pi) * CAVITY_BONES; j0 = np.floor(x).astype(int) % CAVITY_BONES; fr = x - np.floor(x)
    BIn = np.zeros((nn, 4), np.int64); BWn = np.zeros((nn, 4))
    # Pele original em volta: parte do peso do cf_s_hip_ana vai pro osso do anel mais proximo (em repouso os ossos
    # do anel andam igual ao pai, entao a pose nao muda). Os cantos do leque caem nos angulos 0/90/180/270 = ossos
    # 0/2/4/6: 1 osso cada, e os pontos na aresta do leque (interpolados) ficam exatamente sobre a aresta mesmo
    # com o anel aberto (osso filho so translada -> skinning linear nos pesos).
    BIo = novo["BI"].copy(); BWo = novo["BW"].copy()
    corner = {}
    fan_ring = set(k[fan].ravel().tolist()) - {k[i0]}
    skipped = moved = 0
    for i in np.where(orig)[0]:
        if k[i] == k[i0]: continue
        rel = V[i] - P0
        if abs(rel @ nrm) > 0.08: continue
        r = flat(V[i]); f = spread(r)
        if f <= 0:
            if k[i] in fan_ring: corner[i] = (bone_of(ang(V[i])), 0.0)
            continue
        slot_h = np.where((BIo[i] == hip) & (BWo[i] > 0))[0]
        free = np.where(BWo[i] == 0)[0]
        if len(slot_h) == 0: continue
        if len(free) == 0: skipped += 1; continue
        sh = min(f, BWo[i][slot_h[0]])
        jb = bone_of(ang(V[i]))
        BWo[i][slot_h[0]] -= sh; BIo[i][free[0]] = base_idx + jb; BWo[i][free[0]] = sh
        moved += 1
        if k[i] in fan_ring: corner[i] = (jb, sh)
    # niveis entre a abertura e a aresta: peso = o da aresta naquela direcao + o que falta ate 1, linear no raio.
    # MEDIDO (sim): com o peso so pelo raio, o circulo das pregas (0.027) andava mais que a aresta (0.029) e 60
    # triangulos da faixa entre eles viravam.
    # MEDIDO (sim, 2a rodada): mesmo assim 48 viravam - a aresta mistura ossos a 90 graus (os dos cantos) e a
    # soma encurta; o circulo usava os vizinhos a 45 e andava ~40% mais. Agora o nivel = a MESMA mistura da aresta
    # + o extra radial nos ossos do angulo dele: so se afasta da aresta. (cantos 0/2 + vizinhos = 3 ossos + quadril)
    for m_ in range(nn):
        w = {}
        if share[m_] <= -1.0:
            t_ = float(np.clip(-1.0 - share[m_], 0, 1)); tri = src[m_]; u = edge_u[len(V) + m_]
            (ja, sa), (jb, sb) = corner[tri[1]], corner[tri[2]]
            # deslocamento = (1-t) x o da aresta + t x o da abertura (t = 0 na aresta, 1 na abertura): linear no raio
            w[ja] = w.get(ja, 0) + (1 - t_) * (1 - u) * sa; w[jb] = w.get(jb, 0) + (1 - t_) * u * sb
            extra = t_
        else:
            extra = share[m_]
        j1 = (j0[m_] + 1) % CAVITY_BONES
        w[j0[m_]] = w.get(j0[m_], 0) + extra * (1 - fr[m_]); w[j1] = w.get(j1, 0) + extra * fr[m_]
        w = {j: x for j, x in w.items() if x > 1e-7}
        assert len(w) <= 3, w
        BIn[m_, 0] = hip; BWn[m_, 0] = 1 - sum(w.values())
        for q, (j, x) in enumerate(w.items()):
            BIn[m_, q + 1] = base_idx + j; BWn[m_, q + 1] = x
    # pontos na aresta do leque: pesos = interpolacao dos cantos
    for m_ in range(nn):
        tri, b_ = src[m_], bar[m_]
        if b_[0] != 0.0: continue
        a_, bb = tri[1], tri[2]; u = b_[2]
        (ja, sa), (jb, sb) = corner[a_], corner[bb]
        BIn[m_] = [hip, base_idx + ja, base_idx + jb, 0]; BWn[m_] = [1 - (1 - u) * sa - u * sb, (1 - u) * sa, u * sb, 0]
    novo2["BI"] = np.r_[BIo, BIn]
    novo2["BW"] = np.r_[BWo, BWn]
    # rosado (mascara do mamilo no UV2) pela profundidade: as pregas ganham um traco de cor, o canal fica cheio
    if ROSADO and uv[2] is not None:
        tr = np.clip((-hh - CAVITY_PINK[0]) / (CAVITY_PINK[1] - CAVITY_PINK[0]), 0, 1); tr = tr * tr * (3 - 2 * tr)
        tem = tr > 0
        uv[2][len(V):][tem] = np.c_[0.5 - (0.22 - 0.14 * tr[tem]), np.full(tem.sum(), 0.5)]
    novo2["UV"] = uv
    # normais dos vertices novos pela forma (media por area, soldando as copias de costura de UV)
    Vn = novo2["V"]; kn = solda(Vn); F = np.array(tris)
    f = np.cross(Vn[F[:, 1]] - Vn[F[:, 0]], Vn[F[:, 2]] - Vn[F[:, 0]])
    acc = np.zeros((len(Vn), 3))
    for i in range(3): np.add.at(acc, kn[F[:, i]], f)
    Nn = acc[kn[len(V):]]; Nn /= np.linalg.norm(Nn, axis=1, keepdims=True) + 1e-12
    # Marcus: o losango aparecia no jogo. A pele em volta usa a normal SUAVIZADA do sulco; a do leque chapado e
    # outra, e a luz mudava de uma vez na borda. Agora: normal suavizada original (interpolada no leque) + so o
    # desvio que a forma nova cria em relacao a face chapada do leque. Na borda o desvio e 0 = pele original.
    Fo = np.cross(V[src[:, 1]] - V[src[:, 0]], V[src[:, 2]] - V[src[:, 0]]); Fo /= np.linalg.norm(Fo, axis=1, keepdims=True)
    Ni = interp(novo["N"]); Ni /= np.linalg.norm(Ni, axis=1, keepdims=True)
    Nn = Ni + (Nn - Fo); Nn /= np.linalg.norm(Nn, axis=1, keepdims=True) + 1e-12
    novo2["N"] = np.r_[novo["N"], Nn]
    info = dict(modo="cavity", fan=len(fan), vertices=nn, triangulos=len(tris), ossos=CAVITY_BONES,
                abertura=round(r_open, 4), fundo=CAVITY_APEX, pele_esticada=moved, pele_sem_slot=skipped)
    return novo2, info


def profundidade_centro(novo, am, ca, raio=0.04):
    """Mediana da altura (ao longo da normal da pele) dos vertices novos a menos de 'raio' do centro do anus,
    em relacao a pele de 'am' (a malha antes desta passada). Negativo = afundado."""
    V = novo["V"]; n0 = len(am["V"])
    nv = np.where((np.arange(len(V)) >= n0) & (np.linalg.norm(V[:, [0, 2]] - ca[[0, 2]], axis=1) < raio))[0]
    if len(nv) == 0:
        return 0.0
    # MEDIDO: projetar no ponto de pele mais proximo enganava no sulco (a parede da nadega ficava mais perto que
    # o fundo). Agora: raio pela normal media da pele no anus, altura ate a pele original que ele atravessa.
    Cc = am["V"][am["I"]].mean(1)
    reg = am["I"][(np.linalg.norm(Cc[:, [0, 2]] - ca[[0, 2]], axis=1) < 0.3) & (np.abs(Cc[:, 1] - ca[1]) < 0.15)]
    A, B, C = am["V"][reg[:, 0]], am["V"][reg[:, 1]], am["V"][reg[:, 2]]
    nrm = am["N"][reg].mean((0, 1)); nrm /= np.linalg.norm(nrm)
    alt = []
    for p in V[nv]:
        # Moller-Trumbore contra todos os triangulos da regiao, reta p + t*nrm (t de qualquer sinal)
        e1, e2 = B - A, C - A; pv = np.cross(nrm, e2); det = np.einsum("ij,ij->i", e1, pv)
        ok = np.abs(det) > 1e-12; inv = np.where(ok, 1 / np.where(ok, det, 1), 0)
        tv = p - A; u = np.einsum("ij,ij->i", tv, pv) * inv
        qv = np.cross(tv, e1); v = (qv @ nrm) * inv; t = np.einsum("ij,ij->i", e2, qv) * inv
        acerta = ok & (u >= 0) & (v >= 0) & (u + v <= 1)
        if acerta.any():
            alt.append(-t[acerta][np.argmin(np.abs(t[acerta]))])   # p fica em p0 - t*nrm: altura = -t
    return float(np.median(alt)) if alt else 0.0


def anus_monta(am2, abox, kk, kk_ossos, kbox, uv_m, esc, eleva=0.0):
    return monta(am2, abox, kk, kk_ossos, kbox, uv_m, regiao={"teto": 0.01, "s": 8.18, "ossos": False, "janela": JANELA_ANUS,
                                     "escala_buraco": esc, "margem_relevo": MARGEM_RELEVO_ANUS,
                                     # MEDIDO: o relevo (feito pra tirar a vulva da cavidade) virava o anus em SALIENCIA de
                                     # +3 mm, e o aplainamento (raio 0.12) pega a peca inteira (raio ~0.1): os dois desligados
                                     "eleva": eleva, "aplaina": 0.0})


def main():
    pasta_bp, body = sys.argv[1], sys.argv[2]
    png = "--png" in sys.argv
    env = UnityPy.load(body)
    am_mnpb = carrega(malhas(env, "mnpb")[0])
    (kk, kk_ossos), kk_mnpb = doador(pasta_bp)
    uv_m = uv_do_mamilo(env)
    print("uv mamilo", uv_m)
    for nome in VARIANTES:
        ms = malhas(env, nome)
        if not ms:
            print(nome, "nao existe"); continue
        am = carrega(ms[0])
        novo, info = monta(am, am_mnpb, kk, kk_ossos, kk_mnpb, uv_m)
        novo, info_a = anus(novo, am, env, kk, kk_ossos, uv_m)
        grava(os.path.join(AQUI, nome + ".bin"), novo)
        print(nome, info)
        if info_a: print(nome, "anus", info_a)
        if png:
            desenha(am, novo, nome)


def desenha(am, novo, nome):
    import matplotlib; matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    V, I = novo["V"], novo["I"]
    C = V[I].mean(1)
    perto = (abs(C[:, 0]) < 0.6) & (C[:, 1] < 9.8) & (C[:, 1] > 8.6) & (C[:, 2] > -1.0) & (C[:, 2] < 0.8)
    n0 = len(am["V"])
    fig, ax = plt.subplots(1, 2, figsize=(14, 7))
    for k, (a, b) in enumerate([(0, 2), (2, 1)]):
        for t in I[perto]:
            cor = "r" if (t >= n0).all() else ("g" if (t >= n0).any() else "0.6")
            p = V[np.r_[t, t[0]]]
            ax[k].plot(p[:, a], p[:, b], color=cor, lw=0.25)
        ax[k].set_aspect("equal")
    ax[0].set_title(f"{nome} de baixo (vermelho=peca KK, verde=costura)")
    ax[1].set_title("lateral z-y")
    fig.savefig(os.path.join(AQUI, f"_{nome}.png"), dpi=90)
    plt.close(fig)


if __name__ == "__main__":
    main()
