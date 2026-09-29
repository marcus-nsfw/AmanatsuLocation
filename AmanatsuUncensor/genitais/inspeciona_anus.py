"""O que o corpo doador (SAC Innie) traz de anus: ossos cf_J_Ana*/siri, vertices que dependem deles e o decalque
mnpa (mosaico do anus) no KK e no Amanatsu. Uso: inspeciona_anus.py <pasta BP> <body_00.unity3d.bak>"""
import sys, io, zipfile, os, numpy as np, UnityPy
import converte_kk_fem as c
pasta = sys.argv[1]
z = zipfile.ZipFile(os.path.join(pasta, c.DOADOR))
env = UnityPy.load(io.BytesIO(z.read(next(n for n in z.namelist() if n.endswith(".unity3d")))))
(kk, ko), km = c.doador(pasta)
ana = [i for i, b in enumerate(ko) if any(k in b.lower() for k in ("ana", "siri", "hip"))]
print("ossos com ana/siri/hip:", [ko[i] for i in ana])
for i in ana:
    w = np.where(kk["BI"] == i, kk["BW"], 0).sum(1)
    if (w > 0.01).sum():
        cen = kk["V"][w > 0.5].mean(0).round(3) if (w > 0.5).any() else "-"
        print(f"  {ko[i]:28s} vertices {int((w > 0.01).sum()):5d} peso>0.5 {int((w > 0.5).sum()):5d} centro {cen}")
for o in env.objects:
    if o.type.name != "SkinnedMeshRenderer": continue
    r = o.read(); m = r.m_Mesh.read() if r.m_Mesh else None
    if m and "mnp" in m.m_Name:
        from UnityPy.helpers.MeshHelper import MeshHandler
        h = MeshHandler(m); h.process(); Vm = np.array(h.m_Vertices).reshape(-1, 3)
        print("KK", m.m_Name, "caixa", Vm.min(0).round(3), Vm.max(0).round(3), "vertices", len(Vm),
              "ossos", [b.read().m_GameObject.read().m_Name for b in r.m_Bones][:4])
from UnityPy.helpers.MeshHelper import MeshHandler
am_env = UnityPy.load(sys.argv[2])
for nome in ("mnpa", "mnpb"):
    for m in c.malhas(am_env, nome):
        h = MeshHandler(m); h.process(); V = np.array(h.m_Vertices).reshape(-1, 3)
        print("AM", nome, "caixa", V.min(0).round(3), V.max(0).round(3), "vertices", len(V))
# geometria do KK em volta do anus: ha canal (vertices subindo pra dentro do corpo)?
cen = np.array([0.0, -0.213, -0.052])
V = kk["V"]; w = np.zeros(len(V))
i = ko.index("cf_s_ana"); w = np.where(kk["BI"] == i, kk["BW"], 0).sum(1)
perto = np.linalg.norm(V[:, [0, 2]] - cen[[0, 2]], axis=1) < 0.012
print("KK em volta do anus (raio 0.012 em xz): y de", V[perto, 1].min().round(4), "a", V[perto, 1].max().round(4), "| vertices", perto.sum())
for r in (0.004, 0.008, 0.012, 0.02, 0.03):
    anel = np.abs(np.linalg.norm(V[:, [0, 2]] - cen[[0, 2]], axis=1) - r) < 0.002
    anel &= (V[:, 1] < -0.15)
    if anel.any(): print(f"   anel r={r}: y mediana {np.median(V[anel, 1]):.4f} max {V[anel, 1].max():.4f} ({anel.sum()} v)")
