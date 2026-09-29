"""Triangulos cuja NORMAL DE VERTICE (a que o shader ilumina) aponta contra a face: saem escuros no jogo
mesmo com a geometria certa. Controle: os triangulos da pele original. Uso: confere_normais.py rotulo arq.bin ..."""
import sys, struct, numpy as np, UnityPy
import converte_kk_fem as c
env = UnityPy.load("C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak")
for rot, p in zip(sys.argv[1::2], sys.argv[2::2]):
    nome = p.replace("\\", "/").split("/")[-1].replace(".bin", "")
    n0 = len(c.carrega(c.malhas(env, nome)[0])["V"])
    d = open(p, "rb").read(); o = 4; n, = struct.unpack_from("<i", d, o); o += 4; ch = {}
    for k in "VNTC":
        w, = struct.unpack_from("<i", d, o); o += 4
        if w: ch[k] = np.frombuffer(d, "<f4", n * w, o).reshape(n, w).astype(float); o += n * w * 4
    for k in range(4):
        w, = struct.unpack_from("<i", d, o); o += 4; o += n * w * 4
    ni, = struct.unpack_from("<i", d, o); o += 4; F = np.frombuffer(d, "<i4", ni, o).reshape(-1, 3)
    V, N = ch["V"], ch["N"]
    f = np.cross(V[F[:, 1]] - V[F[:, 0]], V[F[:, 2]] - V[F[:, 0]]); A = np.linalg.norm(f, axis=1) / 2
    f /= 2 * A[:, None] + 1e-12
    dots = np.stack([np.einsum("ij,ij->i", f, N[F[:, i]]) for i in range(3)], 1)
    novo = (F >= n0).any(1)
    for rotulo, m in (("pele original", ~novo), ("com vertice novo", novo)):
        ruim = m & (dots.min(1) < 0) & (A > 5e-5)
        print(f"{rot:10s} {rotulo:17s}: {ruim.sum():4d} de {m.sum()} com normal de vertice contra a face",
              [(int(t), V[F[t]].mean(0).round(3).tolist(), round(float(dots[t].min()), 2)) for t in np.where(ruim)[0][:5]])
