"""
Porta as malhas de genital do Koikatsu Sunshine (BetterPenetration) pro Amanatsu Location.

Mesma ideia do UncensorSelector (troca o sharedMesh e religa bones por nome), mas os bundles do KKS
sao Unity 2019.4 e nao carregam no Unity 6 - entao a malha e extraida aqui e o plugin monta a Mesh.

Alinhamento (MEDIDO nos bindposes dos dois jogos):
  - os eixos dos bones batem (z ao longo do penis); o KK esta 10x menor (dan101->dan109: 0.19 vs 1.90)
  - cada vertice vai pro espaco local do bone ancora do KK (bindpose KK), escala pelo vao entre bones,
    e volta pro espaco da malha do Amanatsu pelo bindpose do mesmo bone no Amanatsu.
Pesos do penis: o Amanatsu so tem dan101 (base) e dan109 (ponta) com peso linear no eixo z - copiamos
o perfil. Testiculos: bones dan_f_top/L/R existem nos dois (Dynamic_* do BP caem no L/R).
UV: toda a malha aponta pra um ponto de pele limpa do corpo masculino do Amanatsu (coxa da frente),
entao o material de pele do proprio personagem pinta a cor certa sem depender das mascaras do shader.

Uso: py -3.13 converte_kk.py <pasta com os .zipmod do BetterPenetration KKS> <body_00.unity3d original>
Saida: o_dankon.bin e o_dan_f.bin nesta pasta.
"""
import os, sys, struct, zipfile, io
import numpy as np
import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler

AQUI = os.path.dirname(os.path.abspath(__file__))
PENIS = "[KKS][Penis][BP] SoS.zipmod"
BALLS = "[KKS][Balls][BP] Color Match.zipmod"


def env_do_zipmod(caminho):
    z = zipfile.ZipFile(caminho)
    nome = next(n for n in z.namelist() if n.endswith(".unity3d"))
    return UnityPy.load(io.BytesIO(z.read(nome)))


def smr(env, nome_malha):
    for o in env.objects:
        if o.type.name != "SkinnedMeshRenderer":
            continue
        r = o.read()
        if not r.m_Mesh:
            continue
        m = r.m_Mesh.read()
        if m.m_Name != nome_malha:
            continue
        ossos = [b.read().m_GameObject.read().m_Name for b in r.m_Bones]
        bind = [np.array([[getattr(b, f"e{i}{j}") for j in range(4)] for i in range(4)]) for b in m.m_BindPose]
        h = MeshHandler(m)
        h.process()
        return dict(
            ossos=ossos,
            bind=dict(zip(ossos, bind)),
            V=np.array(h.m_Vertices, dtype=np.float64).reshape(-1, 3),
            N=np.array(h.m_Normals, dtype=np.float64).reshape(-1, 3),
            I=np.array(h.m_IndexBuffer, dtype=np.int64).reshape(-1, 3),
            UV=np.array(h.m_UV0, dtype=np.float64).reshape(-1, 2) if h.m_UV0 else None,
            BI=h.m_BoneIndices, BW=h.m_BoneWeights,
        )
    raise SystemExit(f"{nome_malha} nao encontrado")


def nome_am(osso_kk):
    """cm_J_dan101_00 -> cf_j_dan101_00; Dynamic do BP -> bone fixo equivalente."""
    n = osso_kk.replace("cm_J_", "cf_j_").replace("_Dynamic", "")
    return n


def realinha(kk, am, ancora_kk, ancora_am, vao_kk, vao_am):
    """Leva os vertices do KK pro espaco da malha do Amanatsu passando pelo bone ancora."""
    escala = vao_am / vao_kk
    Bk = kk["bind"][ancora_kk]
    Ba_inv = np.linalg.inv(am["bind"][ancora_am])
    Vh = np.c_[kk["V"], np.ones(len(kk["V"]))]
    local = (Bk @ Vh.T).T[:, :3] * escala
    V = (Ba_inv @ np.c_[local, np.ones(len(local))].T).T[:, :3]
    R = Ba_inv[:3, :3] @ Bk[:3, :3]
    N = (R @ kk["N"].T).T
    N /= np.linalg.norm(N, axis=1, keepdims=True)
    return V, N


def uv_de_pele(body_env):
    """UV de um vertice da frente da coxa do corpo masculino - pele lisa, sem pelo/mamilo."""
    for o in body_env.objects:
        if o.type.name != "Mesh":
            continue
        m = o.read()
        if m.m_Name != "o_body_base_cm":
            continue
        h = MeshHandler(m)
        h.process()
        V = np.array(h.m_Vertices).reshape(-1, 3)
        UV = np.array(h.m_UV0).reshape(-1, 2)
        i = int(np.argmin(np.linalg.norm(V - np.array([0.6, 8.0, 1.0]), axis=1)))
        return tuple(UV[i])
    raise SystemExit("o_body_base_cm nao encontrado")


def grava(caminho, V, N, I, uv, ossos, pesos):
    """pesos: lista por vertice de [(indice_osso, peso)] (ate 4)."""
    with open(caminho, "wb") as f:
        f.write(b"AMGN")
        f.write(struct.pack("<i", len(V)))
        f.write(np.asarray(V, "<f4").tobytes())
        f.write(np.asarray(N, "<f4").tobytes())
        f.write(struct.pack("<i", I.size))
        f.write(np.asarray(I, "<i4").tobytes())
        f.write(struct.pack("<ff", *uv))
        f.write(struct.pack("<i", len(ossos)))
        for n in ossos:
            b = n.encode()
            f.write(struct.pack("<i", len(b)) + b)
        for p in pesos:
            p = sorted(p, key=lambda x: -x[1])[:4]
            tot = sum(w for _, w in p) or 1.0
            p = [(i, w / tot) for i, w in p] + [(0, 0.0)] * (4 - len(p))
            f.write(struct.pack("<4i4f", *[i for i, _ in p], *[w for _, w in p]))


def main():
    pasta_bp, body = sys.argv[1], sys.argv[2]
    body_env = UnityPy.load(body)
    uv = uv_de_pele(body_env)

    # --- penis ---
    kk = smr(env_do_zipmod(os.path.join(pasta_bp, PENIS)), "o_dankon")
    am = smr(body_env, "o_dankon")
    z = lambda b, n: np.linalg.inv(b[n])[2, 3]   # z do bone no espaco da malha
    vao_kk = abs(z(kk["bind"], "cm_J_dan109_00") - z(kk["bind"], "cm_J_dan101_00"))
    z101, z109 = z(am["bind"], "cf_j_dan101_00"), z(am["bind"], "cf_j_dan109_00")
    V, N = realinha(kk, am, "cm_J_dan101_00", "cf_j_dan101_00", vao_kk, abs(z109 - z101))
    ossos = ["cf_j_dan101_00", "cf_j_dan109_00"]
    t = np.clip((V[:, 2] - z101) / (z109 - z101), 0, 1)
    pesos = [[(0, 1 - ti), (1, ti)] for ti in t]
    grava(os.path.join(AQUI, "o_dankon.bin"), V, N, kk["I"], uv, ossos, pesos)
    print(f"o_dankon: {len(V)} vertices, z {V[:,2].min():.2f}..{V[:,2].max():.2f} (capsula original {am['V'][:,2].min():.2f}..{am['V'][:,2].max():.2f})")

    # --- testiculos ---
    kk = smr(env_do_zipmod(os.path.join(pasta_bp, BALLS)), "o_dan_f")
    am = smr(body_env, "o_dan_f")
    pos = lambda b, n: np.linalg.inv(b[n])[:3, 3]
    vao_kk = np.linalg.norm(pos(kk["bind"], "cm_J_dan_f_L") - pos(kk["bind"], "cm_J_dan_f_R"))
    vao_am = np.linalg.norm(pos(am["bind"], "cf_j_dan_f_L") - pos(am["bind"], "cf_j_dan_f_R"))
    V, N = realinha(kk, am, "cm_J_dan_f_top", "cf_j_dan_f_top", vao_kk, vao_am)
    ossos = sorted({nome_am(n) for n in kk["ossos"]})
    idx = {n: ossos.index(nome_am(n)) for n in kk["ossos"]}
    pesos = []
    for bi, bw in zip(kk["BI"], kk["BW"]):
        acc = {}
        for i, w in zip(bi, bw):
            if w > 0:
                j = idx[kk["ossos"][i]]
                acc[j] = acc.get(j, 0) + w
        pesos.append(list(acc.items()))
    grava(os.path.join(AQUI, "o_dan_f.bin"), V, N, kk["I"], uv, ossos, pesos)
    print(f"o_dan_f: {len(V)} vertices, ossos {ossos}, bbox {V.min(0).round(2)}..{V.max(0).round(2)} (original {am['V'].min(0).round(2)}..{am['V'].max(0).round(2)})")
    print(f"uv de pele: {uv}")


if __name__ == "__main__":
    main()
