"""Profundidade da peca do anus (vertices da 2a passada) em relacao a pele original do Amanatsu, e o perfil
radial em volta do centro. Uso: confere_anus.py arq.bin [arq.bin ...]"""
import sys, numpy as np, UnityPy
import converte_kk_fem as c
from bin_io import le
from UnityPy.helpers.MeshHelper import MeshHandler
env = UnityPy.load("C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak")
dec = []
for m in c.malhas(env, "mnpa"):
    h = MeshHandler(m); h.process(); dec.append(np.array(h.m_Vertices, dtype=float).reshape(-1, 3))
ca = min(dec, key=len).mean(0)
for p in sys.argv[1:]:
    nome = p.replace("\\", "/").split("/")[-1].replace(".bin", "")
    am = c.carrega(c.malhas(env, nome)[0])
    V, F, BI, BW = le(p)
    perto = np.linalg.norm(V[:, [0, 2]] - ca[[0, 2]], axis=1) < 0.2
    novos = np.where(perto & (np.arange(len(V)) >= len(am["V"])))[0]
    Cc = am["V"][am["I"]].mean(1)
    reg = am["I"][(np.linalg.norm(Cc[:, [0, 2]] - ca[[0, 2]], axis=1) < 0.4) & (np.abs(Cc[:, 1] - ca[1]) < 0.15)]
    tri, bar = c.projeta(V[novos], am["V"], reg)
    Q = np.einsum("ij,ijk->ik", bar, am["V"][reg[tri]]); N = np.einsum("ij,ijk->ik", bar, am["N"][reg[tri]]); N /= np.linalg.norm(N, axis=1, keepdims=True)
    h = np.einsum("ij,ij->i", V[novos] - Q, N)
    r = np.linalg.norm(V[novos][:, [0, 2]] - ca[[0, 2]], axis=1)
    lin = " ".join(f"r<{a:.2f}:{np.median(h[(r >= a - 0.04) & (r < a)]) * 1000:+.1f}" for a in (0.04, 0.08, 0.12, 0.16) if ((r >= a - 0.04) & (r < a)).any())
    print(f"{nome}: {len(novos)} vertices do anus | altura vs pele (mm x10: 0.001 = 0.1 mm): min {h.min():+.4f} | por raio (x1000): {lin}")
