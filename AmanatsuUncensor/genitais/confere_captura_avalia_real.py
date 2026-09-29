"""Avalia a malha feminina gerada AGORA na pose REAL capturada (matrizes recuperadas em cap_M.npy)."""
import sys, numpy as np, collections, UnityPy
sys.path.insert(0, r"C:\Users\Marcus\Documents\My Games\Amanatsu Rokeeshon\UserData\tools\AmanatsuUncensor\genitais")
import converte_kk_fem as c
from pose_captura import posa
M=np.load("cap_M.npy")
bp="C:/Users/Marcus/AppData/Local/Temp/claude/C--Users-Marcus-Desktop-romdump/3a804c90-f0a2-4566-a3a7-cf447def28be/scratchpad/bp/x/mods/BetterPenetration"
env=UnityPy.load("C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak")
am_mnpb=c.carrega(c.malhas(env,"mnpb")[0]); (kk,ko),km=c.doador(bp); am=c.carrega(c.malhas(env,"o_onepi_type01")[0])
novo,info=c.monta(am,am_mnpb,kk,ko,km,c.uv_do_mamilo(env)); V=novo["V"]; F=novo["I"]; n0=len(am["V"])
S=posa(V,novo["BI"],novo["BW"],M)
canon=c.solda(V)
def nrm(P): f=np.cross(P[F[:,1]]-P[F[:,0]],P[F[:,2]]-P[F[:,0]]); return f/(np.linalg.norm(f,axis=1,keepdims=True)+1e-12)
N0,N1=nrm(V),nrm(S)
ar=collections.defaultdict(list)
for k,t in enumerate(canon[F]):
    for a,b in ((t[0],t[1]),(t[1],t[2]),(t[2],t[0])): ar[(min(a,b),max(a,b))].append(k)
novo_t=(F>=n0).any(1)
dob_novo=0; dob_pele=0; vinco=[]; areas_dob=[]
for e,ks in ar.items():
    if len(ks)==2:
        a,b=ks
        if novo_t[a] or novo_t[b]: vinco.append(np.degrees(np.arccos(np.clip(N0[a]@N0[b],-1,1))))
        if N0[a]@N0[b]>-0.5 and N1[a]@N1[b]<-0.5:
            if novo_t[a] or novo_t[b]:
                dob_novo+=1; areas_dob.append(max(np.linalg.norm(np.cross(S[F[k][1]]-S[F[k][0]],S[F[k][2]]-S[F[k][0]]))/2 for k in (a,b)))
            else: dob_pele+=1
vinco=np.array(vinco)
vv=vinco[vinco<170]
print(f"pose REAL: dobras com geometria nova {dob_novo} | so pele do jogo {dob_pele} | area posada max das dobras novas {max([0]+areas_dob):.4f}")
if "--lista" in sys.argv:
    nf=len(F)-info["peca_t"]-info["ponte"]
    gr=lambda k: "pele-sub" if k<nf else ("peca" if k<nf+info["peca_t"] else "leque")
    area=lambda P,k: np.linalg.norm(np.cross(P[F[k][1]]-P[F[k][0]],P[F[k][2]]-P[F[k][0]]))/2
    lst=[]
    for e,ks in ar.items():
        if len(ks)==2:
            a,b=ks
            if (novo_t[a] or novo_t[b]) and N0[a]@N0[b]>-0.5 and N1[a]@N1[b]<-0.5:
                lst.append((max(area(S,a),area(S,b)),a,b))
    for ar_,a,b in sorted(lst,reverse=True):
        print(f"   area posada {ar_:.5f}  {gr(a)} x {gr(b)}  repouso {V[F[a]].mean(0).round(3).tolist()}  quebra parado {np.degrees(np.arccos(np.clip(N0[a]@N0[b],-1,1))):.0f}")
