#!/usr/bin/env python3
"""Refaz a previsao do modelo para janelas de validacao ja gravadas.

Cada janela guardou o problema (problems/val_N.bin) e o genoma aplicado. Com o
nucleo atual, recalcula o que o modelo preve e escreve um validation.jsonl novo
(medicoes do jogo intactas, previsoes novas), para o analyze.py. Assim da para
mexer no modelo e medir o efeito sem reabrir o jogo.

    python3 tools/validate/replay.py validation.jsonl <pasta dos val_N.bin> saida.jsonl
"""
import json
import os
import subprocess
import sys

H, L, S = 8, 18, 18
BENCH = os.path.join(os.path.dirname(__file__), "..", "..", "native", "mmopt", "target", "release", "mmopt-bench")


def main():
    src, blobs, out = sys.argv[1], sys.argv[2], sys.argv[3]
    res = []
    for line in open(src, encoding="utf-8"):
        if not line.strip():
            continue
        w = json.loads(line)
        blob = os.path.join(blobs, w["blob"] or f"val_{w['window']}.bin")
        genome = ",".join(str(x) for x in w["genome"])
        # genoma gravado antes do campo de sentido (5 por rota) ou depois (6)?
        txt = subprocess.run([BENCH, "explain", blob, genome, str(w["seconds"])], capture_output=True, text=True)
        if txt.returncode != 0:
            print("janela", w["window"], "falhou:", txt.stderr.strip()[:200])
            continue
        v = [float(x) for x in txt.stdout.strip().split(",")]
        nl, ns = int(v[1]), int(v[2])
        wsec = w["seconds"]
        drained = 0.0
        for i in range(min(ns, len(w["stations"]))):
            o = H + nl * L + i * S
            drained += min(v[o + 9], v[o + 8] * wsec)
        w["pred"]["spawned"] = v[4] * wsec
        w["pred"]["delivered"] = v[3] * max(0.0, wsec - v[5]) + drained
        w["pred"]["avg_travel"] = v[5]
        w["pred_rates"] = {"delivered": v[3], "demand": v[4]}
        for l in w["lines"]:
            for li in range(nl):
                o = H + li * L
                if int(v[o]) == l["route"]:
                    l["pred_cycle"] = [v[o + 8], v[o + 9]]
                    l["pred_util"] = [v[o + 12], v[o + 13]]
                    l["pred_onboard"] = v[o + 14]
                    l["pred_ndir"] = [v[o + 5], v[o + 6]]
                    l["pred_length"] = v[o + 16]
        for s in w["stations"]:
            o = H + nl * L + s["i"] * S
            s["pred_q"], s["pred_over"], s["pred_regime"] = v[o + 12], v[o + 13], v[o + 0]
            s["pred_growth"], s["pred_backlog"] = v[o + 14], v[o + 9] + v[o + 10]
            s["pred_q_late"], s["pred_over_late"] = v[o + 15], v[o + 16]
        res.append(w)
    with open(out, "w", encoding="utf-8") as f:
        for w in res:
            f.write(json.dumps(w) + "\n")
    print(f"{len(res)} janelas reprevistas -> {out}")


if __name__ == "__main__":
    main()
