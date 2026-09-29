import sys, struct, numpy as np, collections
sys.path.insert(0, r"C:\Users\Marcus\Documents\My Games\Amanatsu Rokeeshon\UserData\tools\AmanatsuUncensor\genitais")
import converte_kk_fem as c
from pose_captura import posa
def le(p):
    d=open(p,"rb").read(); o=4; n,=struct.unpack_from("<i",d,o); o+=4; ch={}
    for k in "VNTC":
        w,=struct.unpack_from("<i",d,o); o+=4
        if w: ch[k]=np.frombuffer(d,"<f4",n*w,o).reshape(n,w).astype(float); o+=n*w*4
    for k in range(4):
        w,=struct.unpack_from("<i",d,o); o+=4
        if w: o+=n*w*4
    ni,=struct.unpack_from("<i",d,o); o+=4; I=np.frombuffer(d,"<i4",ni,o).reshape(-1,3); o+=ni*4
    BI=np.frombuffer(d,"<i4",n*4,o).reshape(n,4); o+=n*16; BW=np.frombuffer(d,"<f4",n*4,o).reshape(n,4).astype(float)
    return ch["V"],I,BI,BW
V,F,BI,BW=le(sys.argv[1]); n0=int(sys.argv[2]); M=np.load("cap_M.npy")
S=posa(V,BI,BW,M); canon=c.solda(V)
def nrm(P): f=np.cross(P[F[:,1]]-P[F[:,0]],P[F[:,2]]-P[F[:,0]]); return f/(np.linalg.norm(f,axis=1,keepdims=True)+1e-12)
N0,N1=nrm(V),nrm(S)
ar=collections.defaultdict(list)
for k,t in enumerate(canon[F]):
    for a,b in ((t[0],t[1]),(t[1],t[2]),(t[2],t[0])): ar[(min(a,b),max(a,b))].append(k)
novo=(F>=n0).any(1); dn=0; areas=[]
for e,ks in ar.items():
    if len(ks)==2:
        a,b=ks
        if (novo[a] or novo[b]) and N0[a]@N0[b]>-0.5 and N1[a]@N1[b]<-0.5:
            dn+=1; areas.append(max(np.linalg.norm(np.cross(S[F[k][1]]-S[F[k][0]],S[F[k][2]]-S[F[k][0]]))/2 for k in (a,b)))
# degenerados parados que abrem na pose
P0=V[F[novo]]; P1=S[F[novo]]
a0=np.linalg.norm(np.cross(P0[:,1]-P0[:,0],P0[:,2]-P0[:,0]),axis=1)/2; a1=np.linalg.norm(np.cross(P1[:,1]-P1[:,0],P1[:,2]-P1[:,0]),axis=1)/2
abas=((a1>10*a0+2e-4)).sum()
print(f"{sys.argv[1].split('/')[-1]}: pose REAL dobras com novo {dn}, area max {max([0]+areas):.4f} | abas (quase sem area parado, abrem 10x+ na pose) {abas}, maior crescimento {(a1-a0).max():.4f}")
