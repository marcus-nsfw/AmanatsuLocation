"""Quao FUNDA a parte de fora da peca fica em relacao a pele original (cavidade). Usa o .bin (qualquer versao)."""
import sys, numpy as np, UnityPy
sys.path.insert(0, r"C:\Users\Marcus\Documents\My Games\Amanatsu Rokeeshon\UserData\tools\AmanatsuUncensor\genitais")
import converte_kk_fem as c
from bin_io import le

def le_uv0(p):
    d=open(p,"rb").read(); o=4; n,=struct.unpack_from("<i",d,o); o+=4
    for k in "VNTC":
        w,=struct.unpack_from("<i",d,o); o+=4; o+=n*w*4
    w,=struct.unpack_from("<i",d,o); o+=4
    return np.frombuffer(d,"<f4",n*2,o).reshape(n,2)
env=UnityPy.load("C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak")
_,uvm=c.uv_do_mamilo(env)
for rot,p in zip(sys.argv[1::2],sys.argv[2::2]):
    nome=p.replace("\\","/").split("/")[-1].replace(".bin","")
    am=c.carrega(c.malhas(env,nome)[0]); n0=len(am["V"])
    V,F,BI,BW,U=le(p, uv0=True)
    usados=np.unique(F); novos=usados[usados>=n0]
    fora=novos[np.linalg.norm(U[novos]-uvm,axis=1)>0.03]   # parte de FORA (sem o fundo rosado)
    Cc=am["V"][am["I"]].mean(1); perto=am["I"][(abs(Cc[:,0])<0.7)&(Cc[:,1]>8.6)&(Cc[:,1]<9.8)&(abs(Cc[:,2])<0.9)]
    tri,bar=c.projeta(V[fora],am["V"],perto)
    Q=np.einsum("ij,ijk->ik",bar,am["V"][perto[tri]]); N=np.einsum("ij,ijk->ik",bar,am["N"][perto[tri]]); N/=np.linalg.norm(N,axis=1,keepdims=True)
    s=np.einsum("ij,ij->i",V[fora]-Q,N)
    print(f"{rot:28s}: parte de fora da peca vs pele (negativo = afundado): mediana {np.median(s):+.3f}  p10 {np.percentile(s,10):+.3f}  min {s.min():+.3f}  ({len(fora)} vertices)")
