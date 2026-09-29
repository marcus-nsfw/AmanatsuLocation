import sys, numpy as np, UnityPy, collections
sys.path.insert(0, r"C:\Users\Marcus\Documents\My Games\Amanatsu Rokeeshon\UserData\tools\AmanatsuUncensor\genitais")
import converte_kk_fem as c
bp="C:/Users/Marcus/AppData/Local/Temp/claude/C--Users-Marcus-Desktop-romdump/3a804c90-f0a2-4566-a3a7-cf447def28be/scratchpad/bp/x/mods/BetterPenetration"
body="C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak"
env=UnityPy.load(body); am_mnpb=c.carrega(c.malhas(env,"mnpb")[0]); (kk,ko),km=c.doador(bp)
for nome in sys.argv[1:]:
    am=c.carrega(c.malhas(env,nome)[0])
    novo,info=c.monta(am,am_mnpb,kk,ko,km,(np.zeros(2),np.ones(2)))
    V=novo["V"]; I=novo["I"]; canon=c.solda(V); ar=c.conta_arestas(I,canon)
    antes=c.conta_arestas(am["I"],c.solda(am["V"]))
    bordas_orig=sum(1 for v in antes.values() if v==1)
    bordas_nova=sum(1 for v in ar.values() if v==1)
    print(nome, info, "| bordas antes",bordas_orig,"depois",bordas_nova,"| arestas >2 tri: antes",sum(1 for v in antes.values() if v>2),"depois",sum(1 for v in ar.values() if v>2))
    if len(sys.argv)==2: c.desenha(am,novo,nome)
