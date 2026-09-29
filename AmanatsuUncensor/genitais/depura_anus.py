"""Depuracao do recorte do anus no KK: tamanho, bordas (lacos) e se pega a vulva. Uso: depura_anus.py <pasta BP> [raio] [teto]"""
import sys, numpy as np
from collections import Counter
import converte_kk_fem as c
(kk, ko), km = c.doador(sys.argv[1])
R = float(sys.argv[2]) if len(sys.argv) > 2 else 0.02
teto = float(sys.argv[3]) if len(sys.argv) > 3 else 0.01
ck = kk["mnpa"].mean(0); print("centro do mnpa do KK", ck.round(4))
KC = kk["V"][kk["I"]].mean(1)
sel = (np.linalg.norm(KC[:, [0, 2]] - ck[[0, 2]], axis=1) < R) & (KC[:, 1] > ck[1] - 0.016) & (KC[:, 1] < ck[1] + 0.012 + teto)
kcan = c.solda(kk["V"])
ar = c.conta_arestas(kk["I"][sel], kcan)
borda = [e for e, n in ar.items() if n == 1]
grau = Counter(v for e in borda for v in e)
print(f"raio {R} teto {teto}: {sel.sum()} triangulos, {len(borda)} arestas de borda, vertices com grau>2: {sum(1 for g in grau.values() if g > 2)}")
lacos = []
resto = set(borda)
viz = {}
for a, b in borda: viz.setdefault(a, []).append(b); viz.setdefault(b, []).append(a)
vistos = set()
for v0 in viz:
    if v0 in vistos: continue
    pilha = [v0]; comp = 0
    while pilha:
        v = pilha.pop()
        if v in vistos: continue
        vistos.add(v); comp += 1; pilha += viz[v]
    lacos.append(comp)
print("lacos de borda (vertices):", sorted(lacos, reverse=True)[:6])
vag = [i for i, b in enumerate(ko) if b.startswith("cf_J_Vagina")]
pv = np.isin(kk["BI"][kk["I"][sel]], vag).any(2).any(1) if False else None
u = np.unique(kk["I"][sel]); wv = np.array([sum(w for bi, w in zip(kk["BI"][x], kk["BW"][x]) if bi in vag) for x in u])
print("vertices do recorte com peso de osso da vulva > 0.1:", int((wv > 0.1).sum()), "de", len(u))
print("y dos vertices do recorte: min", kk["V"][u, 1].min().round(4), "max", kk["V"][u, 1].max().round(4))
