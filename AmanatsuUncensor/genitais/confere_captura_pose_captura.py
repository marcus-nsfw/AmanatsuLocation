"""Recupera as matrizes de skin da pose REAL capturada no jogo (F9) por minimos quadrados:
S_v = sum_b w_vb * M_b @ [v,1]  (linear nas entradas de M_b). Usa so os vertices originais."""
import numpy as np, sys
from scipy.sparse import coo_matrix
from scipy.sparse.linalg import lsqr
def recupera(Vr, S, BI, BW, n_bones):
    n=len(Vr); Vh=np.c_[Vr,np.ones(n)]
    rows=[];cols=[];vals=[]
    for k in range(4):
        w=BW[:,k]; b=BI[:,k]; ok=w>0
        for c in range(3):          # linha de saida (x,y,z)
            for j in range(4):      # coluna de M
                rows.append(np.where(ok)[0]*3+c); cols.append(b[ok]*12+c*4+j); vals.append(w[ok]*Vh[ok,j])
    A=coo_matrix((np.concatenate(vals),(np.concatenate(rows),np.concatenate(cols))),shape=(3*n,12*n_bones)).tocsr()
    x=lsqr(A,S.ravel(),atol=1e-12,btol=1e-12,iter_lim=20000)[0]
    M=np.tile(np.eye(4),(n_bones,1,1)); M[:,:3,:]=x.reshape(n_bones,3,4)
    res=np.abs(A@x-S.ravel()).max()
    return M,res
def posa(V,BI,BW,M):
    Vh=np.c_[V,np.ones(len(V))]; S=np.zeros((len(V),3))
    for k in range(4): S+=BW[:,k:k+1]*np.einsum('nij,nj->ni',M[BI[:,k]],Vh)[:,:3]
    return S
if __name__=="__main__":
    sys.path.insert(0, r"C:\Users\Marcus\Documents\My Games\Amanatsu Rokeeshon\UserData\tools\AmanatsuUncensor\genitais")
    import converte_kk_fem as c, UnityPy
    S=np.load("cap_S.npy"); V=np.load("cap_V.npy"); norig=5272
    env=UnityPy.load("C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak")
    am=c.carrega(c.malhas(env,"o_onepi_type01")[0])
    nb=int(am["BI"].max())+1
    M,res=recupera(am["V"],S[:norig],am["BI"],am["BW"],nb)
    np.save("cap_M.npy",M)
    # valida: reposa os vertices NOVOS da captura com os pesos do nosso .bin e compara com o capturado
    bp="C:/Users/Marcus/AppData/Local/Temp/claude/C--Users-Marcus-Desktop-romdump/3a804c90-f0a2-4566-a3a7-cf447def28be/scratchpad/bp/x/mods/BetterPenetration"
    am_mnpb=c.carrega(c.malhas(env,"mnpb")[0]); (kk,ko),km=c.doador(bp)
    novo,info=c.monta(am,am_mnpb,kk,ko,km,c.uv_do_mamilo(env))
    Sn=posa(novo["V"],novo["BI"],novo["BW"],M)
    print(f"residuo nos originais {res:.2e} | erro reposando os vertices NOVOS (nao usados no ajuste): max {np.abs(Sn[norig:]-S[norig:]).max():.2e}")
