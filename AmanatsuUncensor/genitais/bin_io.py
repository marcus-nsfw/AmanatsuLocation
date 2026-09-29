import struct, numpy as np
def le(p, uv0=False):
    d=open(p,"rb").read(); o=4; n,=struct.unpack_from("<i",d,o); o+=4; ch={}
    for k in "VNTC":
        w,=struct.unpack_from("<i",d,o); o+=4
        if w: ch[k]=np.frombuffer(d,"<f4",n*w,o).reshape(n,w).astype(float); o+=n*w*4
    uvs=[]
    for k in range(4):
        w,=struct.unpack_from("<i",d,o); o+=4
        uvs.append(np.frombuffer(d,"<f4",n*w,o).reshape(n,w).astype(float) if w else None); o+=n*w*4
    ni,=struct.unpack_from("<i",d,o); o+=4; I=np.frombuffer(d,"<i4",ni,o).reshape(-1,3); o+=ni*4
    BI=np.frombuffer(d,"<i4",n*4,o).reshape(n,4); o+=n*16; BW=np.frombuffer(d,"<f4",n*4,o).reshape(n,4).astype(float)
    return (ch["V"],I,BI,BW,uvs[0]) if uv0 else (ch["V"],I,BI,BW)
