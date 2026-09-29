"""Discordancia entre a NORMAL gravada nos vertices novos e a normal da forma (media das faces em volta,
por area), em graus. O toon ilumina pela normal gravada: se ela nao segue a forma, aparecem linhas quebradas.
Uso: confere_normais_forma.py rotulo arq.bin [rotulo arq.bin ...]"""
import sys, struct, numpy as np, UnityPy
import converte_kk_fem as c
from bin_io import le
env = UnityPy.load("C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak")
for rot, p in zip(sys.argv[1::2], sys.argv[2::2]):
    nome = p.replace("\\", "/").split("/")[-1].replace(".bin", "")
    n0 = len(c.carrega(c.malhas(env, nome)[0])["V"])
    d = open(p, "rb").read(); n, = struct.unpack_from("<i", d, 4); o = 8
    w, = struct.unpack_from("<i", d, o); o += 4 + n * w * 4
    w, = struct.unpack_from("<i", d, o); o += 4; Nv = np.frombuffer(d, "<f4", n * 3, o).reshape(n, 3).astype(float)
    V, F, BI, BW = le(p)
    k = c.solda(V)
    fn = np.cross(V[F[:, 1]] - V[F[:, 0]], V[F[:, 2]] - V[F[:, 0]])
    acc = np.zeros((len(V), 3))
    for i in range(3): np.add.at(acc, k[F[:, i]], fn)
    Ng = acc[k]; Ng /= np.linalg.norm(Ng, axis=1, keepdims=True) + 1e-12
    Nv = Nv / (np.linalg.norm(Nv, axis=1, keepdims=True) + 1e-12)
    usados = np.unique(F); nv = usados[usados >= n0]
    ang = np.degrees(np.arccos(np.clip(np.einsum("ij,ij->i", Nv[nv], Ng[nv]), -1, 1)))
    print(f"{rot:10s}: normal gravada x forma nos vertices novos: mediana {np.median(ang):.1f} graus  p90 {np.percentile(ang, 90):.1f}  >20: {(ang > 20).sum()} de {len(nv)}")
    # cantos do buraco: vertices da pele ORIGINAL usados por triangulos que o mod mudou (o contorno da costura)
    orig = {tuple(sorted(t)) for t in c.carrega(c.malhas(env, nome)[0])["I"].tolist()}
    mud = np.array([tuple(sorted(t)) not in orig for t in F.tolist()])
    cantos = np.unique(F[mud]); cantos = cantos[cantos < n0]
    a2 = np.degrees(np.arccos(np.clip(np.einsum("ij,ij->i", Nv[cantos], Ng[cantos]), -1, 1)))
    print(f"{'':10s}  cantos do buraco (pele original na costura): mediana {np.median(a2):.1f} graus  p90 {np.percentile(a2, 90):.1f}  max {a2.max():.1f}  >20: {(a2 > 20).sum()} de {len(cantos)}")
