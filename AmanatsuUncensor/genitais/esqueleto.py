"""Pose com o esqueleto REAL do bundle (hierarquia + TRS local), skin = mundo_bone @ bindpose."""
import sys, numpy as np, UnityPy
from scipy.spatial.transform import Rotation as R
BODY="C:/Users/Marcus/Documents/My Games/Amanatsu Rokeeshon/lib/chara/body/body_00.unity3d.bak"
env=UnityPy.load(BODY)
def trs(t):
    p=t.m_LocalPosition; q=t.m_LocalRotation; s=t.m_LocalScale
    M=np.eye(4); M[:3,:3]=R.from_quat([q.x,q.y,q.z,q.w]).as_matrix()@np.diag([s.x,s.y,s.z]); M[:3,3]=[p.x,p.y,p.z]; return M
def carrega_esqueleto(raiz_nome="p_cf_al_body_00"):
    nos={}   # path_id -> (nome, pai_pid, local)
    for o in env.objects:
        if o.type.name=="Transform":
            t=o.read(); nos[o.path_id]=(t.m_GameObject.read().m_Name, t.m_Father.path_id if t.m_Father else None, trs(t))
    # so os da arvore da raiz pedida
    def raiz_de(pid):
        while nos[pid][1] in nos: pid=nos[pid][1]
        return nos[pid][0]
    return {pid:v for pid,v in nos.items() if raiz_de(pid)==raiz_nome}
def mundos(nos, extra=None):
    """extra: dict pid -> matriz aplicada no mundo desse no (e herdada pelos filhos)"""
    W={}
    def w(pid):
        if pid in W: return W[pid]
        nome,pai,L=nos[pid]
        M=(w(pai) if pai in nos else np.eye(4))@L
        W[pid]=M; return M
    for pid in nos: w(pid)
    if extra:
        # aplica giros em ordem de profundidade (pai antes do filho) propagando pros descendentes
        filhos={}
        for pid,(n,pai,L) in nos.items(): filhos.setdefault(pai,[]).append(pid)
        def desc(pid):
            out=[pid]
            for f in filhos.get(pid,[]): out+=desc(f)
            return out
        for pid,G in extra.items():
            for d in desc(pid): W[d]=G@W[d]
    return W
def giro_em(W, pid, graus_xyz, fator=1.0):
    p=W[pid][:3,3]; G=np.eye(4); G[:3,:3]=R.from_euler("xyz",np.array(graus_xyz)*fator,degrees=True).as_matrix()
    T1=np.eye(4); T1[:3,3]=p; T0=np.eye(4); T0[:3,3]=-p; return T1@G@T0
def matrizes_skin(nomes_bones, bindposes, pose_graus=(-60,0,35)):
    nos=carrega_esqueleto(); por_nome={}
    for pid,(n,pai,L) in nos.items(): por_nome.setdefault(n,pid)
    W0=mundos(nos)
    extra={}
    for lado,sinal in (("L",1),("R",-1)):
        fx,fy,fz=pose_graus
        extra[por_nome[f"cf_j_thigh00_{lado}"]]=giro_em(W0,por_nome[f"cf_j_thigh00_{lado}"],(fx,fy,fz*sinal))
        for aj in (f"cf_d_hipleg1_{lado}",f"cf_d_hipleg2_{lado}",f"cf_d_siri_{lado}"):
            if aj in por_nome: extra[por_nome[aj]]=giro_em(W0,por_nome[f"cf_j_thigh00_{lado}"],(fx,fy,fz*sinal),0.5)
    W1=mundos(nos,extra)
    M0=np.array([W0[por_nome[n]]@B for n,B in zip(nomes_bones,bindposes)])
    M1=np.array([W1[por_nome[n]]@B for n,B in zip(nomes_bones,bindposes)])
    return M0,M1
def skin(V,BI,BW,Ms):
    Vh=np.c_[V,np.ones(len(V))]; S=np.zeros((len(V),3))
    for k in range(4): S+=BW[:,k:k+1]*np.einsum('nij,nj->ni',Ms[BI[:,k]],Vh)[:,:3]
    return S
