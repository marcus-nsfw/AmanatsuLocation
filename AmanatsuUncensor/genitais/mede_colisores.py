"""Mede a grossura real (espaco da malha) das falanges do masculino e do penis, pra dimensionar os
colisores de capsula. Raio = mediana da distancia dos vertices dominados pelo osso ate o eixo osso->filho."""
import sys, numpy as np, UnityPy
import converte_kk_fem as c
from bin_io import le

def ossos_smr(env, nome_malha):
    for o in env.objects:
        if o.type.name != "SkinnedMeshRenderer":
            continue
        r = o.read()
        if r.m_Mesh and r.m_Mesh.read().m_Name == nome_malha:
            m = r.m_Mesh.read()
            nomes = [b.read().m_GameObject.read().m_Name for b in r.m_Bones]
            pos = [np.linalg.inv(np.array([[getattr(B, f"e{a}{k}") for k in range(4)] for a in range(4)]))[:3, 3] for B in m.m_BindPose]
            return m, nomes, np.array(pos)

def raio(V, BI, BW, i, a, b):
    dom = (BI[:, 0] == i) & (BW[:, 0] > 0.6)
    P = V[dom]; d = (b - a) / np.linalg.norm(b - a)
    t = (P - a) @ d
    r = np.linalg.norm((P - a) - t[:, None] * d, axis=1)
    return np.median(r), dom.sum(), np.linalg.norm(b - a)

env = UnityPy.load(sys.argv[1])
m, nomes, pos = ossos_smr(env, "o_body_base_cm")
am = c.carrega(m)
for dedo in ("index", "middle", "ring"):
    for k in ("02", "03"):
        i = nomes.index(f"cf_s_{dedo}{k}_R")
        prox = f"cf_s_{dedo}{int(k) + 1:02d}_R" if k == "02" else None
        b = pos[nomes.index(prox)] if prox else pos[i] + (pos[i] - pos[nomes.index(f"cf_s_{dedo}02_R")])
        r, n, L = raio(am["V"], am["BI"], am["BW"], i, pos[i], b)
        print(f"{dedo}{k}_R: raio {r:.4f}  comprimento {L:.4f}  ({n} vertices)")
