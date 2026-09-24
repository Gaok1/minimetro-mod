# Extrai os TextAssets JSON (trens, audio, temas) do resources.assets.
# Uso: python3 extract_text.py "<jogo>/Mini Metro_Data/resources.assets" <pasta_saida>
import struct, re, os, sys
data = open(sys.argv[1], 'rb').read()
out = sys.argv[2]
count = 0
# TextAsset: [int32 nameLen][name][pad4][int32 len][bytes]
for m in re.finditer(rb'[\{\[]', data):
    p = m.start()
    if p < 8: continue
    L = struct.unpack_from('<i', data, p-4)[0]
    if L < 20 or p + L > len(data): continue
    blob = data[p:p+L]
    t = blob.rstrip()
    if not t or t[-1:] not in (b'}', b']'): continue
    try: s = blob.decode('utf-8')
    except: continue
    # find name: walk back for len-prefixed ascii string ending before p-4 (aligned)
    name = None
    for nl in range(1, 120):
        q = p - 4 - ((nl + 3) & ~3) - 4
        if q < 0: break
        if struct.unpack_from('<i', data, q)[0] == nl:
            cand = data[q+4:q+4+nl]
            if all(32 <= c < 127 for c in cand):
                name = cand.decode(); break
    name = name or f'anon_{p}'
    fn = re.sub(r'[^A-Za-z0-9_.-]', '_', name)
    path = os.path.join(out, fn + '.json')
    i = 1
    while os.path.exists(path):
        path = os.path.join(out, f'{fn}__{i}.json'); i += 1
    open(path, 'w').write(s); count += 1
print(count)
