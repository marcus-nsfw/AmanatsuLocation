import sys, numpy as np, UnityPy
sys.path.insert(0, r"C:\Users\Marcus\Documents\My Games\Amanatsu Rokeeshon\UserData\tools\AmanatsuUncensor\genitais")
import converte_kk_fem as c
bp="C:/Users/Marcus/AppData/Local/Temp/claude/C--Users-Marcus-Desktop-romdump/3a804c90-f0a2-4566-a3a7-cf447def28be/scratchpad/bp/x/mods/BetterPenetration"
env=UnityPy.load("C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak")
am_mnpb=c.carrega(c.malhas(env,"mnpb")[0]); (kk,ko),km=c.doador(bp)
am=c.carrega(c.malhas(env,sys.argv[1])[0])
novo,info=c.monta(am,am_mnpb,kk,ko,km,c.uv_do_mamilo(env)); V=novo["V"]; I=novo["I"]; n0=len(am["V"])
nb=int(novo["BI"].max())+1
def dens(BI,BW):
    D=np.zeros((len(BI),nb)); np.add.at(D,(np.repeat(np.arange(len(BI)),4),BI.ravel()),BW.ravel()); return D
Dn=dens(novo["BI"],novo["BW"]); Da=dens(am["BI"],am["BW"])
novos=np.unique(I[(I>=n0).any(1)]); novos=novos[novos>=n0]
# peso da pele original no ponto mais proximo (toda a pele original, antes do corte)
tri,bar=c.projeta(V[novos],am["V"],am["I"])
Dp=np.einsum("ij,ijk->ik",bar,Da[am["I"][tri]])
dif=np.abs(Dn[novos]-Dp).sum(1)
k=np.argsort(-dif)
print(f"{sys.argv[1]}: diferenca de peso vs pele original no mesmo ponto (L1, 0..2): mediana {np.median(dif):.2f} p95 {np.percentile(dif,95):.2f} max {dif.max():.2f}")
for i in k[:8]: print("   ", V[novos[i]].round(3).tolist(), "dif", dif[i].round(2))
