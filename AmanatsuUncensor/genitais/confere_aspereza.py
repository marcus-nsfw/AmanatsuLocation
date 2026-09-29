"""Aspereza da parte nova: distancia de cada vertice novo a media dos vizinhos (umbrella), por ARESTA MEDIA.
Separa superficie redonda (baixa) de ondulada (alta). Uso: confere_aspereza.py rotulo arq.bin [rotulo arq.bin ...]"""
import sys, numpy as np, UnityPy
from collections import defaultdict
import converte_kk_fem as c
from bin_io import le
env = UnityPy.load("C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak")
cache = {}
for rot, p in zip(sys.argv[1::2], sys.argv[2::2]):
    nome = p.replace("\\", "/").split("/")[-1].replace(".bin", "")
    if nome not in cache:
        cache[nome] = len(c.carrega(c.malhas(env, nome)[0])["V"])
    n0 = cache[nome]
    V, F, BI, BW = le(p)
    k = c.solda(V); F = k[F]
    viz = defaultdict(set)
    for a, b, d in F:
        viz[a] |= {b, d}; viz[b] |= {a, d}; viz[d] |= {a, b}
    novos = [v for v in viz if v >= n0 and k[v] == v]
    ar = np.mean([np.linalg.norm(V[a] - V[b]) for a, b in F[:, :2]])
    r = np.array([np.linalg.norm(V[v] - V[list(viz[v])].mean(0)) for v in novos]) / ar
    print(f"{rot:12s}: aspereza (umbrella / aresta media) mediana {np.median(r):.3f} p90 {np.percentile(r, 90):.3f} p99 {np.percentile(r, 99):.3f}")
