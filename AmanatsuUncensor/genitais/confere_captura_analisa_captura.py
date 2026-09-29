import sys, struct, numpy as np, collections
sys.path.insert(0, r"C:\Users\Marcus\Documents\My Games\Amanatsu Rokeeshon\UserData\tools\AmanatsuUncensor\genitais")
import converte_kk_fem as c
from scipy.spatial import cKDTree
CAP=sys.argv[1]; BIN=sys.argv[2]
# captura: objetos
objs={}; cur=None
for l in open(CAP):
    if l.startswith("o "): cur=l.split()[1]; objs[cur]={"v":[],"f":[]}
    elif l.startswith("v "): objs[cur]["v"].append(list(map(float,l.split()[1:4])))
    elif l.startswith("f "): objs[cur]["f"].append([int(x)-1 for x in l.split()[1:4]])
off=0
for k,o in objs.items():
    o["v"]=np.array(o["v"]); o["f"]=np.array(o["f"])-off; off+=len(o["v"])
nome=[k for k in objs if k.endswith("_kk")][0]; S=objs[nome]["v"]; F=objs[nome]["f"]
# nosso .bin (repouso) na mesma ordem
d=open(BIN,"rb").read(); o=4; n,=struct.unpack_from("<i",d,o); o+=4
w,=struct.unpack_from("<i",d,o); o+=4; V=np.frombuffer(d,"<f4",n*3,o).reshape(n,3).astype(float)
print(nome, "vertices captura", len(S), "bin", n, "tris", len(F))
# alinha repouso->captura (so pra ter uma ideia da escala/rotacao): usa vertices longe da virilha
n0=6350-1078 if "onepi" in nome else None
canon=c.solda(V)
def nrm(P): f=np.cross(P[F[:,1]]-P[F[:,0]],P[F[:,2]]-P[F[:,0]]); return f/(np.linalg.norm(f,axis=1,keepdims=True)+1e-12)
N0,N1=nrm(V),nrm(S)
ar=collections.defaultdict(list)
for k,t in enumerate(canon[F]):
    for a,b in ((t[0],t[1]),(t[1],t[2]),(t[2],t[0])): ar[(min(a,b),max(a,b))].append(k)
# 1) dobras: quebra > 120 graus na captura e nao em repouso
dob=[(a,b) for e,ks in ar.items() if len(ks)==2 for a,b in [ks] if N0[a]@N0[b]>-0.5 and N1[a]@N1[b]<-0.5]
# numero de vertices originais: triangulos cujo algum vertice e "novo" (indice >= n_original)
import UnityPy
env=UnityPy.load("C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak")
norig=c.carrega(c.malhas(env,nome.replace("_kk",""))[0])["V"].shape[0]
novo=(F>=norig).any(1)
print(f"1) dobras na pose real: total {len(dob)} | com triangulo novo {sum(1 for a,b in dob if novo[a] or novo[b])} | so pele original {sum(1 for a,b in dob if not novo[a] and not novo[b])}")
for a,b in dob[:12]: print("     ", V[F[a]].mean(0).round(3).tolist(), "novo" if novo[a] else "pele", "novo" if novo[b] else "pele")
# 2) frestas: vertices coincidentes em repouso separados na captura
g=collections.defaultdict(list)
for v in np.unique(F): g[canon[v]].append(v)
fr=sorted(((max(np.linalg.norm(S[x]-S[y]) for x in vs for y in vs),vs) for vs in g.values() if len(vs)>1),key=lambda z:-z[0])
print(f"2) maior fresta entre vertices coincidentes: {fr[0][0]:.5f}", "novos?", [v>=norig for v in fr[0][1]])
# 3) costura com o_body_armleg na captura
L=objs["o_body_armleg"]["v"]
ar2=c.conta_arestas(F,canon); bv=np.unique([v for e,k in ar2.items() if k==1 for v in e])
dd,jj=cKDTree(L).query(S[bv])
print(f"3) borda do corpo x armleg na captura: distancia min dos vertices de borda ao armleg mediana {np.median(dd):.4f}, max {dd.max():.4f}")
np.save("cap_S.npy",S); np.save("cap_F.npy",F); np.save("cap_V.npy",V)
