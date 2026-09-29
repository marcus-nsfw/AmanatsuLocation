import sys; sys.path.insert(0,".")
sys.path.insert(0, r"C:\Users\Marcus\Documents\My Games\Amanatsu Rokeeshon\UserData\tools\AmanatsuUncensor\genitais")
import numpy as np, esqueleto as E, converte_kk_fem as c, collections
env=E.env
bp="C:/Users/Marcus/AppData/Local/Temp/claude/C--Users-Marcus-Desktop-romdump/3a804c90-f0a2-4566-a3a7-cf447def28be/scratchpad/bp/x/mods/BetterPenetration"
am_mnpb=c.carrega(c.malhas(env,"mnpb")[0]); (kk,ko),km=c.doador(bp)
nome=sys.argv[1]; pose=tuple(float(x) for x in sys.argv[2].split(",")) if len(sys.argv)>2 else (-60,0,35)
for o in env.objects:
    if o.type.name=="SkinnedMeshRenderer":
        r=o.read()
        if r.m_Mesh and r.m_Mesh.read().m_Name==nome: break
m=r.m_Mesh.read(); am=c.carrega(m)
nomes=[b.read().m_GameObject.read().m_Name for b in r.m_Bones]
bind=[np.array([[getattr(b,f"e{i}{j}") for j in range(4)] for i in range(4)]) for b in m.m_BindPose]
M0,M1=E.matrizes_skin(nomes,bind,pose)
novo,info=c.monta(am,am_mnpb,kk,ko,km,c.uv_do_mamilo(env)); V=novo["V"]; I=novo["I"]; n0=len(am["V"])
def dobras(V,I,BI,BW,sel):
    canon=c.solda(V)
    S0=E.skin(V,BI,BW,M0); S1=E.skin(V,BI,BW,M1)
    def nrm(S): f=np.cross(S[I[:,1]]-S[I[:,0]],S[I[:,2]]-S[I[:,0]]); return f/(np.linalg.norm(f,axis=1,keepdims=True)+1e-12)
    n0_,n1_=nrm(S0),nrm(S1)
    ar=collections.defaultdict(list)
    for k,t in enumerate(canon[I]):
        for a,b in ((t[0],t[1]),(t[1],t[2]),(t[2],t[0])): ar[(min(a,b),max(a,b))].append(k)
    ruins=[]
    for e,ks in ar.items():
        if len(ks)==2 and (sel[ks[0]] or sel[ks[1]]):
            a,b=ks
            if n0_[a]@n0_[b]>-0.5 and n1_[a]@n1_[b]<-0.5: ruins.append((a,b))
    return ruins
Cc=am["V"][am["I"]].mean(1); reg=(abs(Cc[:,0])<0.6)&(Cc[:,1]>8.8)&(Cc[:,1]<9.6)&(abs(Cc[:,2])<0.7)
ro=dobras(am["V"],am["I"],am["BI"],am["BW"],reg)
novos=(I>=n0).any(1)
rn=dobras(V,I,novo["BI"],novo["BW"],novos)
print(f"{nome} pose {pose}: pares vizinhos que DOBRAM (>120 graus) so na pose - pele original (regiao) {len(ro)} | com triangulo novo {len(rn)}")
nf=len(I)-info["peca_t"]-info["ponte"]
gr=lambda k: "pele" if k<nf else ("peca" if k<nf+info["peca_t"] else "leque")
for a,b in rn:
    Sa=V[I[a]]; Sb=V[I[b]]
    na=np.cross(Sa[1]-Sa[0],Sa[2]-Sa[0]); nb_=np.cross(Sb[1]-Sb[0],Sb[2]-Sb[0])
    ang0=np.degrees(np.arccos(np.clip(na@nb_/(np.linalg.norm(na)*np.linalg.norm(nb_)+1e-12),-1,1)))
    print("    ", V[I[a]].mean(0).round(3).tolist(), gr(a), gr(b), "quebra parado", round(float(ang0)), "areas", round(float(np.linalg.norm(na)/2),5), round(float(np.linalg.norm(nb_)/2),5))
