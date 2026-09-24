# Acha as definicoes binarias de cidade (TextAsset .bytes) dentro do resources.assets.
# Uso: python3 find_cities.py "<jogo>/Mini Metro_Data/resources.assets" [saida]
import struct, sys
data = open(sys.argv[1],'rb').read()
names = "london tutorial addisababa auckland barcelona berlin boston budapest cairo chicago chongqing guangzhou hongkong istanbul lagos lisbon melbourne montreal mumbai nanjing nyc nyc1972 osaka paris paris1937 sanfrancisco santiago seoul shanghai singapore stockholm stpetersburg saopaulo tashkent tokyo warsaw washingtondc london1960 canberra".split()
import os
OUT = sys.argv[2] if len(sys.argv) > 2 else 'cities'
os.makedirs(OUT, exist_ok=True)
for n in names:
    key = struct.pack('<i', len(n)) + n.encode()
    pos = 0; found = 0
    while True:
        p = data.find(key, pos)
        if p < 0: break
        pos = p + 1
        q = p + 4 + ((len(n)+3)&~3)
        L = struct.unpack_from('<i', data, q)[0]
        if L <= 100 or L > 20_000_000: continue
        blob = data[q+4:q+4+L]
        # first: .NET string (varint len)
        l0 = blob[0]
        if l0 >= 128: continue
        s0 = blob[1:1+l0]
        l1 = blob[1+l0]
        s1 = blob[2+l0:2+l0+l1]
        if s1 != n.encode(): continue
        open(os.path.join(OUT, n + '.bytes'),'wb').write(blob); found += 1
        print(n, L, s0)
    if not found: print(n, 'NOT FOUND')
