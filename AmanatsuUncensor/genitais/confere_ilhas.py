import sys, numpy as np, UnityPy, collections
sys.path.insert(0, r"C:\Users\Marcus\Documents\My Games\Amanatsu Rokeeshon\UserData\tools\AmanatsuUncensor\genitais")
import converte_kk_fem as c
bp="C:/Users/Marcus/AppData/Local/Temp/claude/C--Users-Marcus-Desktop-romdump/3a804c90-f0a2-4566-a3a7-cf447def28be/scratchpad/bp/x/mods/BetterPenetration"
env=UnityPy.load("C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak")
am_mnpb=c.carrega(c.malhas(env,"mnpb")[0]); (kk,ko),km=c.doador(bp)
am=c.carrega(c.malhas(env,sys.argv[1] if len(sys.argv)>1 else "o_lower_type01")[0])
novo,info=c.monta(am,am_mnpb,kk,ko,km,c.uv_do_mamilo(env)); V=novo["V"]; I=novo["I"]; n0=len(am["V"])
canon=c.solda(V)
T=I[(I>=n0).any(1)]   # tudo que e novo (peca + pele subdividida + leques)
g=collections.defaultdict(set)
for t in canon[T]:
    for a in t:
        for b in t: g[a].add(b)
seen=set(); comps=[]
for s in g:
    if s in seen: continue
    st=[s]; comp=set()
    while st:
        x=st.pop()
        if x in comp: continue
        comp.add(x); st+=list(g[x])
    seen|=comp; comps.append(comp)
comps.sort(key=len,reverse=True)
print("componentes do que e novo:",[len(x) for x in comps][:12])
for comp in comps[1:12]:
    P=V[list(comp)]; toca_pele=any(v<n0 for v in comp)
    print(f"  ilha {len(comp)} vertices, toca pele? {toca_pele}, centro {P.mean(0).round(3)}, tamanho {(P.max(0)-P.min(0)).round(3)}")
