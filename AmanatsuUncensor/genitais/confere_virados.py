"""Triangulos que tocam vertice novo e apontam CONTRA os vizinhos em repouso (aparecem escuros/vazados no jogo:
o Unity nao desenha a face de tras). Ignora os minusculos (fundo do canal, invisiveis).
Uso: confere_virados.py rotulo arq.bin [rotulo arq.bin ...]"""
import sys, numpy as np, UnityPy
from collections import defaultdict
import converte_kk_fem as c
from bin_io import le
env = UnityPy.load("C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak")
cache = {}
for rot, p in zip(sys.argv[1::2], sys.argv[2::2]):
    nome = p.replace("\\", "/").split("/")[-1].replace(".bin", "")
    if nome not in cache:
        am = c.carrega(c.malhas(env, nome)[0])
        cache[nome] = {tuple(sorted(t)) for t in am["I"].tolist()}
    orig = cache[nome]
    V, F, BI, BW = le(p)
    # MEDIDO (captura F9): triangulo de leque feito SO de cantos do buraco (pontos da borda soldados nos cantos)
    # tem todos os vertices < n0 e escapava do filtro "toca vertice novo". Criterio: nao existia na original.
    mudou = np.array([tuple(sorted(t)) not in orig for t in F.tolist()])
    f = np.cross(V[F[:, 1]] - V[F[:, 0]], V[F[:, 2]] - V[F[:, 0]]); A = np.linalg.norm(f, axis=1) / 2; N = f / (2 * A[:, None] + 1e-12)
    k = c.solda(V); viz = defaultdict(list)
    for t, tri in enumerate(k[F]):
        for v in tri: viz[v].append(t)
    ruins = []
    for t in np.where(mudou & (A > 5e-5))[0]:
        ts = set(x for v in k[F[t]] for x in viz[v]) - {t}
        m = sum(N[x] * A[x] for x in ts)
        if N[t] @ m / (np.linalg.norm(m) + 1e-12) < 0:
            ruins.append((t, V[F[t]].mean(0).round(3).tolist(), round(float(A[t]), 5)))
    print(f"{rot:14s}: triangulos virados visiveis {len(ruins)}", ruins[:6])
