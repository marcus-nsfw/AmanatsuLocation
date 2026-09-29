import sys, os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, r"C:\Users\Marcus\Documents\My Games\Amanatsu Rokeeshon\UserData\tools\AmanatsuUncensor\genitais")
import numpy as np, esqueleto as E, converte_kk_fem as c
env=E.env
bp="C:/Users/Marcus/AppData/Local/Temp/claude/C--Users-Marcus-Desktop-romdump/3a804c90-f0a2-4566-a3a7-cf447def28be/scratchpad/bp/x/mods/BetterPenetration"
am_mnpb=c.carrega(c.malhas(env,"mnpb")[0]); (kk,ko),km=c.doador(bp)
pose=tuple(float(x) for x in sys.argv[2].split(",")) if len(sys.argv)>2 else (-60,0,35)
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
    S0=E.skin(V,novo["BI"],novo["BW"],M0); S1=E.skin(V,novo["BI"],novo["BW"],M1)
    P0=E.skin(am["V"],am["BI"],am["BW"],M0); P1=E.skin(am["V"],am["BI"],am["BW"],M1)
    novos=np.unique(I[(I>=n0).any(1)]); novos=novos[novos>=n0]
    Cc=am["V"][am["I"]].mean(1); perto=am["I"][(abs(Cc[:,0])<0.7)&(Cc[:,1]>8.6)&(Cc[:,1]<9.8)&(abs(Cc[:,2])<0.9)]
    tri,bar=c.projeta(V[novos],am["V"],perto)
    q0=np.einsum("ij,ijk->ik",bar,P0[perto[tri]]); q1=np.einsum("ij,ijk->ik",bar,P1[perto[tri]])
    mud=np.linalg.norm((S1[novos]-q1)-(S0[novos]-q0),axis=1)
    k=np.argsort(-mud)
    print(f"{nome} pose {pose}: vertice novo se afasta da pele embaixo dele: max {mud.max():.3f} p99 {np.percentile(mud,99):.3f} (>0.03: {(mud>0.03).sum()})")
    for i in k[:5]: print("    ", V[novos[i]].round(3).tolist(), "mud", mud[i].round(3))
