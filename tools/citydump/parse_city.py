# Decodifica os .bytes de cidade (formato de CityDefinition.FromStream) em JSON.
# Uso: python3 parse_city.py [pasta_dos_bytes] [saida.json]
# Os obstaculos visuais tem geometria cuja presenca depende do tema; o parse deles
# e heuristico. A costa exata que o jogo usa para decidir tunel e a ObstacleHull,
# que o mod le em runtime.
import struct, sys, json, glob, os
AT = ['None','Line','Locomotive','Shinkansen','Tram','Ferry','Carriage','Crossing','Interchange','Bridge','Count']
ST = {1:'CIRCLE',2:'TRIANGLE',4:'SQUARE',8:'CROSS',0x10:'DIAMOND',0x20:'EGG',0x40:'GEM',0x80:'PENTAGON',0x100:'STAR',0x200:'WEDGE',0:'NONE',7:'STANDARD',3:'REPLACEABLE',0x3F8:'SPECIAL'}
def st(v): return ST.get(v, hex(v))
class R:
    def __init__(s,b): s.b=b; s.p=0
    def i(s): v=struct.unpack_from('<i',s.b,s.p)[0]; s.p+=4; return v
    def f(s): v=struct.unpack_from('<f',s.b,s.p)[0]; s.p+=4; return v
    def bo(s): v=s.b[s.p]!=0; s.p+=1; return v
    def by(s): v=s.b[s.p]; s.p+=1; return v
    def s(s_):
        n=0; sh=0
        while True:
            c=s_.b[s_.p]; s_.p+=1; n|=(c&0x7f)<<sh; sh+=7
            if c<128: break
        v=s_.b[s_.p:s_.p+n].decode('utf-8','replace'); s_.p+=n; return v
def fpath(r):
    n=r.i(); loop=r.bo(); return {'loop':loop,'pts':[(r.f(),r.f()) for _ in range(n)]}
def fgeo(r):
    vt=r.i()
    if not (vt&1) or (vt & ~7): raise ValueError('bad geo %d'%vt)
    per=r.i(); ni=r.i(); r.p+=4*ni; nv=r.i(); r.p+=nv*(4*5); return nv
def parse(b, full=True):
    r=R(b); d={}
    r.s(); d['id']=r.s()
    if r.bo(): d['base']=r.s()
    d['cache']=r.s(); r.i(); r.i(); r.i()
    if r.bo(): d['lb']=r.s()
    if r.bo(): d['locale']=r.s()
    d['theme']=r.s(); d['order']=r.i(); d['disabled']=r.bo()
    for i in range(4):
        n=r.i()
        for _ in range(n): r.s()
    d['lineCount']=r.i(); d['crossingStyle']=['Bridge','Tunnel'][r.i()]
    d['origin']=(r.f(),r.f()); d['startArea']=[r.f() for _ in range(4)]
    d['zoom']=dict(start=r.f(),end=r.f(),early=r.f(),late=r.f(),delay=r.i(),duration=r.i())
    r.f(); r.f()
    d['initialUpgrades']=[AT[r.i()] for _ in range(r.i())]
    d['unlockable']=[]
    for l in range(2):
        lst=[]
        for _ in range(r.i()):
            lst.append(dict(type=AT[r.i()],max=r.i(),count=r.i(),weight=r.f(),week=r.i()))
        d['unlockable'].append(lst)
    sts=[]
    for _ in range(r.i()):
        tag=r.s(); t=r.i(); pos=None
        if r.bo(): pos=(r.f(),r.f())
        rot=r.f(); g=r.bo(); sts.append(dict(tag=tag,type=st(t),pos=pos,ghost=g))
    d['stations']=sts
    pts={}
    for _ in range(r.i()):
        k=r.s(); pts[k]=(r.f(),r.f())
    d['points']=pts
    d['trains']=r.s(); d['stationCapacity']=r.i(); d['interchangeCapacity']=r.i()
    d['stationSeparationScale']=r.f(); d['embarkingQuick']=r.bo(); d['passengerSpawnScale']=r.f()
    for _ in range(r.i()):
        r.s(); fpath(r); r.f(); r.f()
    sch=[]
    for _ in range(r.i()):
        nd=r.i(); rnd=r.f(); ty=[st(r.i()) for _ in range(r.i())]
        sch.append(dict(numDays=nd,randomness=rnd,types=ty))
    d['stationSpawnSchedules']=sch
    d['specialStationSpawns']=[st(r.i()) for _ in range(r.i())]
    d['stationSpawnCooldown']=r.i()
    areas=[]
    for _ in range(r.i()):
        a=dict(id=r.i(),bbox=[r.f() for _ in range(4)],density=r.f())
        a['spawns']=[dict(type=st(r.i()),weight=r.f(),max=r.i(),activeDay=r.i()) for _ in range(r.i())]
        nt=r.i(); r.p+=nt*24; a['triangles']=nt
        ng=r.i(); a['grid']=[(r.f(),r.f()) for _ in range(ng)]
        areas.append(a)
    d['areas']=areas
    obs=[]
    try:
        for _ in range(r.i()):
            o=dict(path=fpath(r),corner=r.f(),inverted=r.bo(),transitional=r.bo(),visual=r.bo(),decoration=r.bo(),alpha=r.i())
            r.bo(); r.p+=6; r.bo(); r.p+=6
            if o['visual']:
                fgeo(r)
                save=r.p
                try: fgeo(r)
                except Exception: r.p=save
            obs.append(o)
    except Exception as e:
        d['obstacleParseError']=str(e)
    d['obstacles']=obs
    return d
if __name__=='__main__':
    src = sys.argv[1] if len(sys.argv) > 1 else 'cities'
    dst = sys.argv[2] if len(sys.argv) > 2 else 'cities.json'
    out={}
    for fn in sorted(glob.glob(os.path.join(src, '*.bytes'))):
        n=os.path.basename(fn)[:-6]
        try: out[n]=parse(open(fn,'rb').read())
        except Exception as e: out[n]={'error':repr(e)}
    json.dump(out, open(dst,'w'), indent=1)
    for n,d in out.items():
        if 'error' in d: print(n,d['error']); continue
        print(f"{n:14s} lines={d['lineCount']} trains={d['trains']:8s} cap={d['stationCapacity']}/{d['interchangeCapacity']} spawn={d['passengerSpawnScale']:.2f} quick={d['embarkingQuick']} sep={d['stationSeparationScale']:.2f} cross={d['crossingStyle']} init={','.join(d['initialUpgrades'])} stations={len(d['stations'])} areas={len(d['areas'])} obs={len(d['obstacles'])} {d.get('obstacleParseError','')}")
