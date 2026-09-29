import sys, numpy as np, UnityPy
sys.path.insert(0, r"C:\Users\Marcus\Documents\My Games\Amanatsu Rokeeshon\UserData\tools\AmanatsuUncensor\genitais")
import converte_kk_fem as c
from scipy.spatial.transform import Rotation as R
bp="C:/Users/Marcus/AppData/Local/Temp/claude/C--Users-Marcus-Desktop-romdump/3a804c90-f0a2-4566-a3a7-cf447def28be/scratchpad/bp/x/mods/BetterPenetration"
env=UnityPy.load("C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak")
am_mnpb=c.carrega(c.malhas(env,"mnpb")[0]); (kk,ko),km=c.doador(bp)
smr={}
for o in env.objects:
    if o.type.name=="SkinnedMeshRenderer":
        r=o.read()
        if r.m_Mesh: smr.setdefault(r.m_Mesh.read().m_Name,[b.read().m_GameObject.read().m_Name for b in r.m_Bones])
def matrizes(nomes, bind, flex, abre):
    Ms=[]
    pos={n:np.linalg.inv(B)[:3,3] for n,B in zip(nomes,bind)}
    for n,B in zip(nomes,bind):
        lado = 1 if n.endswith("_L") else (-1 if n.endswith("_R") else 0)
        perna = any(k in n for k in ("thigh","leg0","knee","foot","toe"))
        meio = any(k in n for k in ("hipleg","siri_L","siri_R"))
        f = 1.0 if perna else (0.5 if meio else 0.0)
        if lado==0 or f==0: Ms.append(np.eye(4)); continue
        Rm=np.eye(4); Rm[:3,:3]=R.from_euler("xz",[-flex*f, lado*abre*f],degrees=True).as_matrix()
        piv=pos.get("cf_s_thigh01_L" if lado>0 else "cf_s_thigh01_R")
        T1=np.eye(4); T1[:3,3]=piv; T0=np.eye(4); T0[:3,3]=-piv; Ms.append(T1@Rm@T0)
    return np.array(Ms)
def mede(V,I,BI,BW,Ms):
    Vh=np.c_[V,np.ones(len(V))]; S=np.zeros((len(V),3))
    for k in range(4): S+=BW[:,k:k+1]*np.einsum('nij,nj->ni',Ms[BI[:,k]],Vh)[:,:3]
    e0=np.linalg.norm(V[I[:,[0,1,2]]]-V[I[:,[1,2,0]]],axis=2); e1=np.linalg.norm(S[I[:,[0,1,2]]]-S[I[:,[1,2,0]]],axis=2)
    ok=e0>1e-3
    f0=np.cross(V[I[:,1]]-V[I[:,0]],V[I[:,2]]-V[I[:,0]]); f1=np.cross(S[I[:,1]]-S[I[:,0]],S[I[:,2]]-S[I[:,0]])
    big=np.linalg.norm(f0,axis=1)>1e-5
    return np.max(np.where(ok,e1/np.maximum(e0,1e-6),1)), int(((np.einsum('ij,ij->i',f0,f1)<0)&big).sum()), S
for nome in sys.argv[1:]:
    m=c.malhas(env,nome)[0]; am=c.carrega(m); bind=[np.array([[getattr(b,f"e{i}{j}") for j in range(4)] for i in range(4)]) for b in m.m_BindPose]
    novo,info=c.monta(am,am_mnpb,kk,ko,km,(np.zeros(2),np.ones(2))); V=novo["V"]; I=novo["I"]; n0=len(am["V"])
    Ms=matrizes(smr[nome],bind,60,35)
    C=am["V"][am["I"]].mean(1); reg=am["I"][(abs(C[:,0])<0.45)&(C[:,1]>8.9)&(C[:,1]<9.5)&(abs(C[:,2])<0.6)]
    ec,fc,_=mede(am["V"],reg,am["BI"],am["BW"],Ms)
    peca=I[(I>=n0).all(1)]; ponte=I[(I>=n0).any(1)&~(I>=n0).all(1)]
    if len(ponte)==0: ponte=I[:1]
    ep,fp,S=mede(V,peca,novo["BI"],novo["BW"],Ms)
    ab=np.linalg.norm(S[ponte[:,[0,1,2]]]-S[ponte[:,[1,2,0]]],axis=2).max(); ab0=np.linalg.norm(V[ponte[:,[0,1,2]]]-V[ponte[:,[1,2,0]]],axis=2).max()
    print(f"{nome}: CONTROLE pele esticamento x{ec:.2f} viradas {fc} | PECA x{ep:.2f} viradas {fp} | costura maior aresta {ab0:.3f}->{ab:.3f}")
