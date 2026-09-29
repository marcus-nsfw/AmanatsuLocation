"""Compara o ANUS dos corpos de uncensor de varios zipmods do KK/KKS: por corpo (o_body_a com pele), vertices em
volta do anus e quanto a geometria entra no corpo (canal). Centro do anus = media dos vertices dominados por
cf_s_ana/cf_j_ana (ou cf_J_Ana*). Uso: compara_anus.py arq.zipmod [arq.zipmod ...]"""
import sys, io, zipfile, numpy as np, UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler

def malha(m):
    h = MeshHandler(m); h.process(); n = h.m_VertexCount
    V = np.array(h.m_Vertices, dtype=float).reshape(n, 3)
    BI = np.array(h.m_BoneIndices, dtype=int).reshape(n, -1)[:, :4] if h.m_BoneIndices else None
    BW = np.array(h.m_BoneWeights, dtype=float).reshape(n, -1)[:, :4] if h.m_BoneWeights else None
    I = np.array(h.m_IndexBuffer, dtype=int).reshape(-1, 3)
    return V, BI, BW, I

for arq in sys.argv[1:]:
    z = zipfile.ZipFile(arq)
    print("=====", arq.split("\\")[-1])
    for nb in [n for n in z.namelist() if n.endswith(".unity3d")]:
        env = UnityPy.load(io.BytesIO(z.read(nb)))
        for o in env.objects:
            if o.type.name != "SkinnedMeshRenderer": continue
            r = o.read()
            if not r.m_Mesh: continue
            m = r.m_Mesh.read()
            if not m.m_Name.startswith("o_body"): continue
            try:
                ossos = [b.read().m_GameObject.read().m_Name for b in r.m_Bones]
                V, BI, BW, I = malha(m)
            except Exception as ex:
                print(f"  {nb.split('/')[-1]} {m.m_Name}: erro {type(ex).__name__}"); continue
            ana = [i for i, b in enumerate(ossos) if b.lower() in ("cf_s_ana", "cf_j_ana") or b.startswith("cf_J_Ana")]
            if BI is None or not ana:
                print(f"  {nb.split('/')[-1]} {m.m_Name}: {len(V)} v, sem osso de anus"); continue
            w = np.where(np.isin(BI, ana), BW, 0).sum(1)
            if (w > 0.3).sum() == 0:
                print(f"  {nb.split('/')[-1]} {m.m_Name}: {len(V)} v, osso de anus sem peso"); continue
            dom = V[w > 0.3]; cen = np.median(dom, 0)
            rxz = np.linalg.norm(V[:, [0, 2]] - cen[[0, 2]], axis=1)
            baixo = V[:, 1] < cen[1] + 0.15
            anel = baixo & (np.abs(rxz - 0.015) < 0.003)
            ysup = np.median(V[anel, 1]) if anel.any() else cen[1]
            miolo = baixo & (rxz < 0.006)
            fundo = (V[miolo, 1].max() - ysup) if miolo.any() else 0.0
            n12 = int((baixo & (rxz < 0.012)).sum())
            extra = [b for b in ossos if "Ana" in b and b not in ("cf_s_ana", "cf_j_ana")]
            print(f"  {nb.split('/')[-1][:40]:40s} {m.m_Name}: {len(V):5d} v | anus: {n12:4d} v num raio de 1.2 cm, "
                  f"entra {fundo * 1000:5.1f} mm no corpo | ossos de anus extras: {extra[:4]}")
