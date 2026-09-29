import sys, numpy as np, UnityPy, collections
sys.path.insert(0, r"C:\Users\Marcus\Documents\My Games\Amanatsu Rokeeshon\UserData\tools\AmanatsuUncensor\genitais")
import converte_kk_fem as c
bp="C:/Users/Marcus/AppData/Local/Temp/claude/C--Users-Marcus-Desktop-romdump/3a804c90-f0a2-4566-a3a7-cf447def28be/scratchpad/bp/x/mods/BetterPenetration"
env=UnityPy.load("C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak")
am_mnpb=c.carrega(c.malhas(env,"mnpb")[0]); (kk,ko),km=c.doador(bp)
def viol(I,V):
    canon=c.solda(V); d=collections.Counter()
    for t in canon[I]:
        for k in range(3): d[(t[k],t[(k+1)%3])]+=1
    return sum(1 for (a,b),n in d.items() if a<b and (d.get((a,b),0)>=1 and d.get((b,a),0)==0 and False) ) , [ (a,b) for (a,b),n in d.items() if n>1 ]
for nome in sys.argv[1:]:
    am=c.carrega(c.malhas(env,nome)[0])
    novo,info=c.monta(am,am_mnpb,kk,ko,km,(np.zeros(2),np.ones(2))); V=novo["V"]; I=novo["I"]; n0=len(am["V"])
    nf=len(I)-info["peca_t"]-info["ponte"]
    grupo=np.array(["pele"]*nf+["peca"]*info["peca_t"]+["leque"]*info["ponte"])
    canon=c.solda(V); d=collections.defaultdict(list)
    for k,t in enumerate(canon[I]):
        for j in range(3): d[(t[j],t[(j+1)%3])].append(k)
    ruins=[(e,ks) for e,ks in d.items() if len(ks)>1]   # mesma aresta no MESMO sentido em 2 triangulos = enrolamento inconsistente
    _,ks0=viol(am["I"],am["V"])
    print(f"{nome}: arestas com sentido repetido - original {len(ks0)}, novo {len(ruins)}; grupos envolvidos:",collections.Counter(tuple(sorted(set(grupo[k] for k in ks))) for e,ks in ruins))
