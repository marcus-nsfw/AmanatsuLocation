"""Vincos na regiao mudada pelo mod: angulo entre as normais de triangulos vizinhos (graus), separado em
contorno (pele original x triangulo mudado) e interior (os dois mudados). Toon destaca vinco como linha.
Uso: confere_vincos.py rotulo arq.bin [rotulo arq.bin ...]"""
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
    f = np.cross(V[F[:, 1]] - V[F[:, 0]], V[F[:, 2]] - V[F[:, 0]]); A = np.linalg.norm(f, axis=1) / 2; N = f / (2 * A[:, None] + 1e-12)
    mud = np.array([tuple(sorted(t)) not in orig for t in F.tolist()])
    ar = defaultdict(list)
    for t, tri in enumerate(Fk):
        for a, b in ((tri[0], tri[1]), (tri[1], tri[2]), (tri[2], tri[0])): ar[(min(a, b), max(a, b))].append(t)
    cont, inte = [], []
    for e, ts in ar.items():
        if len(ts) != 2 or not (mud[ts[0]] or mud[ts[1]]): continue
        ang = np.degrees(np.arccos(np.clip(N[ts[0]] @ N[ts[1]], -1, 1)))
        (cont if mud[ts[0]] != mud[ts[1]] else inte).append(ang)
    cont, inte = np.array(cont), np.array(inte)
    print(f"{rot:10s}: contorno {len(cont)} arestas mediana {np.median(cont):.0f} p90 {np.percentile(cont, 90):.0f} >40: {(cont > 40).sum()} | "
          f"interior {len(inte)} mediana {np.median(inte):.0f} p90 {np.percentile(inte, 90):.0f} >40: {(inte > 40).sum()}")
