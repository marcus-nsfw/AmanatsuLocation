"""Serrilhado do CONTORNO da costura: a linha onde a pele original encontra triangulos mudados pelo mod.
Para cada vertice dessa linha, o angulo de virada entre os dois segmentos da linha (graus); zigue-zague =
viradas grandes alternadas. Uso: confere_contorno.py rotulo arq.bin [rotulo arq.bin ...]"""
import sys, numpy as np, UnityPy
from collections import defaultdict
import converte_kk_fem as c
from bin_io import le
env = UnityPy.load("C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak")
cache = {}
for rot, p in zip(sys.argv[1::2], sys.argv[2::2]):
    nome = p.replace("\\", "/").split("/")[-1].replace(".bin", "")
    if nome not in cache:
        cache[nome] = {tuple(sorted(t)) for t in c.carrega(c.malhas(env, nome)[0])["I"].tolist()}
    orig = cache[nome]
    V, F, BI, BW = le(p)
    k = c.solda(V); Fk = k[F]
    mud = np.array([tuple(sorted(t)) not in orig for t in F.tolist()])
    lado = defaultdict(set)   # aresta -> {mudou?} dos triangulos que a usam
    for t, tri in enumerate(Fk):
        for a, b in ((tri[0], tri[1]), (tri[1], tri[2]), (tri[2], tri[0])):
            lado[(min(a, b), max(a, b))].add(bool(mud[t]))
    linha = [e for e, s in lado.items() if s == {True, False}]
    viz = defaultdict(list)
    for a, b in linha: viz[a].append(b); viz[b].append(a)
    ang = []
    for v, ns in viz.items():
        if len(ns) != 2: continue
        u, w = V[ns[0]] - V[v], V[ns[1]] - V[v]
        ang.append(180 - np.degrees(np.arccos(np.clip(u @ w / (np.linalg.norm(u) * np.linalg.norm(w) + 1e-12), -1, 1))))
    ang = np.array(ang)
    L = sum(np.linalg.norm(V[a] - V[b]) for a, b in linha)
    print(f"{rot:10s}: contorno {len(linha)} arestas, comprimento {L:.2f} | virada mediana {np.median(ang):.0f} graus, p90 {np.percentile(ang, 90):.0f}, >60 graus: {(ang > 60).sum()}")
