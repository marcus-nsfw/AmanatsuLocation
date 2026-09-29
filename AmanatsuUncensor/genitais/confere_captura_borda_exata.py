import sys, numpy as np, UnityPy
sys.path.insert(0, r"C:\Users\Marcus\Documents\My Games\Amanatsu Rokeeshon\UserData\tools\AmanatsuUncensor\genitais")
import converte_kk_fem as c
bp="C:/Users/Marcus/AppData/Local/Temp/claude/C--Users-Marcus-Desktop-romdump/3a804c90-f0a2-4566-a3a7-cf447def28be/scratchpad/bp/x/mods/BetterPenetration"
env=UnityPy.load("C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak")
am_mnpb=c.carrega(c.malhas(env,"mnpb")[0]); (kk,ko),km=c.doador(bp)
for nome in sys.argv[1:]:
    am=c.carrega(c.malhas(env,nome)[0])
    novo,info=c.monta(am,am_mnpb,kk,ko,km,c.uv_do_mamilo(env)); V=novo["V"]; I=novo["I"]; n0=len(am["V"])
    def bordas(V,I):
        cn=c.solda(V); ar=c.conta_arestas(I,cn)
        return {tuple(sorted((tuple(np.round(V[a],4)),tuple(np.round(V[b],4))))) for (a,b),k in ar.items() if k==1}
    b0=bordas(am["V"],am["I"]); b1=bordas(V,I)
    sumiram=b0-b1; surgiram=b1-b0
    print(f"{nome}: bordas originais que SUMIRAM {len(sumiram)} | bordas NOVAS {len(surgiram)}")
    for e in list(sumiram)[:4]: print("    sumiu", e)
    for e in list(surgiram)[:4]: print("    surgiu", e)
