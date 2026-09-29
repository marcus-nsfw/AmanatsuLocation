"""Para cada captura F9 do corpo o_onepi_type01: recupera a pose e mede, em cada versao, a AREA de triangulos
mudados pelo mod (nao existiam na original) que apontam contra os vizinhos NA POSE (de fora = buraco).
Uso: virada_poses.py rotulo arq.bin [rotulo arq.bin ...]"""
import sys, glob, collections, numpy as np, UnityPy
G = r"C:\Users\Marcus\Documents\My Games\Amanatsu Rokeeshon\UserData\tools\AmanatsuUncensor\genitais"
sys.path.insert(0, G)
import converte_kk_fem as c
from bin_io import le
from pose_captura import recupera, posa
env = UnityPy.load("C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak")
am = c.carrega(c.malhas(env, "o_onepi_type01")[0]); n0 = len(am["V"]); nb = am["nbind"]
orig = {tuple(sorted(t)) for t in am["I"].tolist()}
def le_corpo(p):
    V = []; on = False
    for l in open(p):
        if l.startswith("o "): on = l.split()[1] == "o_onepi_type01_kk"
        elif on and l.startswith("v "): V.append(list(map(float, l.split()[1:4])))
    return np.array(V)
poses = []
for p in sorted(glob.glob(r"C:\Users\Marcus\Documents\My Games\Amanatsu Rokeeshon\BepInEx\genital_dump\*_p_cf_*.obj")):
    S = le_corpo(p)
    if len(S) < n0: continue
    M, res = recupera(am["V"], S[:n0], am["BI"], am["BW"], nb)
    if res < 1e-2: poses.append((p.split("\\")[-1][:40], M))
versoes = [(r, le(p)) for r, p in zip(sys.argv[1::2], sys.argv[2::2])]
print(f"{len(poses)} poses capturadas")
for r, (V, F, BI, BW) in versoes:
    mud = np.array([tuple(sorted(t)) not in orig for t in F.tolist()])
    k = c.solda(V); viz = collections.defaultdict(list)
    for t, tri in enumerate(k[F]):
        for v in tri: viz[v].append(t)
    vizt = [list(set(x for v in k[F[t]] for x in viz[v]) - {t}) for t in range(len(F))]
    linha = []
    for nome, M in [("repouso", None)] + poses:
        if M is None: P = V
        else:
            Mx = np.concatenate([M, np.repeat(M[[0]], max(0, BI.max() + 1 - len(M)), 0)])
            P = posa(V, BI, BW, Mx)
        f = np.cross(P[F[:, 1]] - P[F[:, 0]], P[F[:, 2]] - P[F[:, 0]]); A = np.linalg.norm(f, axis=1) / 2; N = f / (2 * A[:, None] + 1e-12)
        tot = 0.0
        for t in np.where(mud & (A > 5e-5))[0]:
            m = (N[vizt[t]] * A[vizt[t], None]).sum(0)
            if N[t] @ m < 0: tot += A[t]
        linha.append(f"{tot:.4f}")
    print(f"{r:10s} area virada: " + "  ".join(linha) + "   (repouso, depois cada captura)")
