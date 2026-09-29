import sys; sys.path.insert(0,".")
sys.path.insert(0, r"C:\Users\Marcus\Documents\My Games\Amanatsu Rokeeshon\UserData\tools\AmanatsuUncensor\genitais")
import numpy as np, esqueleto as E, converte_kk_fem as c, collections
env=E.env
bp="C:/Users/Marcus/AppData/Local/Temp/claude/C--Users-Marcus-Desktop-romdump/3a804c90-f0a2-4566-a3a7-cf447def28be/scratchpad/bp/x/mods/BetterPenetration"
am_mnpb=c.carrega(c.malhas(env,"mnpb")[0]); (kk,ko),km=c.doador(bp)
pose=(-60,0,35)
for nome in sys.argv[1].split(","):
    for o in env.objects:
        if o.type.name=="SkinnedMeshRenderer":
            r=o.read()
            if r.m_Mesh and r.m_Mesh.read().m_Name==nome: break
    m=r.m_Mesh.read(); am=c.carrega(m)
    nomes=[b.read().m_GameObject.read().m_Name for b in r.m_Bones]
    bind=[np.array([[getattr(b,f"e{i}{j}") for j in range(4)] for i in range(4)]) for b in m.m_BindPose]
    M0,M1=E.matrizes_skin(nomes,bind,pose)
    novo,info=c.monta(am,am_mnpb,kk,ko,km,c.uv_do_mamilo(env)); V=novo["V"]; I=novo["I"]; n0=len(am["V"])
    def frestas(V,I,BI,BW):
        S=E.skin(V,BI,BW,M1)
        usados=np.unique(I); canon=c.solda(V)
        g=collections.defaultdict(list)
        for v in usados: g[canon[v]].append(v)
        out=[]
        for k,vs in g.items():
            if len(vs)>1:
                sp=max(np.linalg.norm(S[a]-S[b]) for a in vs for b in vs)
                out.append((sp,k,vs))
        return sorted(out,reverse=True)
    fo=frestas(am["V"],am["I"],am["BI"],am["BW"])
    fn=frestas(V,I,novo["BI"],novo["BW"])
    print(f"{nome}: maior fresta entre vertices coincidentes na pose - ORIGINAL {fo[0][0]:.4f} | NOVO {fn[0][0]:.4f}  (novas >0.002: {sum(1 for s,k,vs in fn if s>0.002)})")
    for s,k,vs in fn[:5]:
        print("    ", round(s,4), V[k].round(3).tolist(), ["KK" if v>=n0 else "AM" for v in vs], vs)
