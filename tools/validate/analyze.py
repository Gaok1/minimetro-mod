#!/usr/bin/env python3
"""Compara o que o modelo nativo preve com o que o jogo mediu.

Le o validation.jsonl que o self-test grava em modo validate
(MINIMETROGA_SELFTEST_MODE=validate, ver src/MiniMetroGA/Validation.cs) e
imprime, por grandeza, o erro do modelo:

    python3 tools/validate/analyze.py <jogo>/MiniMetroGA/validation.jsonl

Grandezas:
  - nascidos    : passageiros que surgiram na janela (tabela de spawn, tensao,
                  centralidade, escala do planner);
  - entregues   : placar ganho na janela;
  - ciclo       : tempo de uma volta completa de cada trem (cinematica, paradas,
                  embarque, cruzamentos);
  - a bordo     : passageiros por trem, media no tempo;
  - sentidos    : trens por sentido em loop (o jogo alterna);
  - fila        : fila media por estacao na janela;
  - acima da cap: fracao do tempo com fila > capacidade (o que dispara o timer).
"""
import json
import math
import sys


def pearson(xs, ys):
    n = len(xs)
    if n < 3:
        return float("nan")
    mx, my = sum(xs) / n, sum(ys) / n
    sx = math.sqrt(sum((x - mx) ** 2 for x in xs))
    sy = math.sqrt(sum((y - my) ** 2 for y in ys))
    if sx == 0 or sy == 0:
        return float("nan")
    return sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / (sx * sy)


def ranks(v):
    order = sorted(range(len(v)), key=lambda i: v[i])
    r = [0.0] * len(v)
    i = 0
    while i < len(order):
        j = i
        while j + 1 < len(order) and v[order[j + 1]] == v[order[i]]:
            j += 1
        for k in range(i, j + 1):
            r[order[k]] = (i + j) / 2.0
        i = j + 1
    return r


def spearman(xs, ys):
    return pearson(ranks(xs), ranks(ys))


def summary(name, pred, meas, unit=""):
    pairs = [(p, m) for p, m in zip(pred, meas) if p is not None and m is not None]
    if not pairs:
        print(f"  {name:<14} sem dados")
        return
    p = [a for a, _ in pairs]
    m = [b for _, b in pairs]
    sp, sm = sum(p), sum(m)
    mae = sum(abs(a - b) for a, b in pairs) / len(pairs)
    bias = (sp - sm) / len(pairs)
    ratio = sp / sm if sm else float("nan")
    print(
        f"  {name:<14} n={len(pairs):3d}  previsto/medido (soma) = {ratio:5.2f}  "
        f"erro medio abs = {mae:6.2f}{unit}  vies = {bias:+6.2f}{unit}  "
        f"pearson = {pearson(p, m):5.2f}  spearman = {spearman(p, m):5.2f}"
    )


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "validation.jsonl"
    wins = [json.loads(l) for l in open(path, encoding="utf-8") if l.strip()]
    if not wins:
        print("nenhuma janela")
        return
    print(f"{len(wins)} janelas de {wins[0]['city']}, {sum(w['seconds'] for w in wins):.0f} s de jogo medidos\n")

    # nascidos = entregues + variacao da fila + variacao a bordo (estacoes do
    # inicio da janela). O contador City.NewPeepCount zera no meio do dia.
    for w in wins:
        if "spawned_derived" in w["meas"]:
            w["meas"]["spawned"] = w["meas"]["spawned_derived"]

    print("Por janela:")
    print("  jan  dia  seg  nasc(prev/med)  entr(prev/med)  linhas")
    for w in wins:
        print(
            f"  {w['window']:3d}  {w['day0']:3d}  {w['seconds']:3.0f}  "
            f"{w['pred']['spawned']:6.1f}/{w['meas']['spawned']:4d}    "
            f"{w['pred']['delivered']:6.1f}/{w['meas']['delivered']:4d}    {len(w['lines'])}"
        )

    print("\nTotais da janela:")
    summary("nascidos", [w["pred"]["spawned"] for w in wins], [w["meas"]["spawned"] for w in wins], " pax")
    summary("entregues", [w["pred"]["delivered"] for w in wins], [w["meas"]["delivered"] for w in wins], " pax")
    late = [w for w in wins if "late_delivered" in w["meas"] and "pred_rates" in w]
    if late:
        # regime: ultimos 2/3 da janela, contra a vazao prevista em regime
        summary(
            "entregues/s*",
            [w["pred_rates"]["delivered"] for w in late],
            [w["meas"]["late_delivered"] / max(1e-6, w["meas"]["late_seconds"]) for w in late],
            " pax/s",
        )
        summary(
            "nascidos/s*",
            [w["pred_rates"]["demand"] for w in late],
            [w["meas"]["late_spawned"] / max(1e-6, w["meas"]["late_seconds"]) for w in late],
            " pax/s",
        )
        print("  (* ultimos 2/3 da janela, depois do transiente de reconstruir as linhas)")

    print("\nLinhas (so sentidos com volta completa medida):")
    pc, mc, po, mo = [], [], [], []
    nd_ok = nd_tot = 0
    loops = []
    for w in wins:
        for l in w["lines"]:
            for d in (0, 1):
                if l["meas_cycle_n"][d] > 0 and l["meas_cycle"][d] is not None and l["pred_cycle"][d] > 0:
                    pc.append(l["pred_cycle"][d])
                    mc.append(l["meas_cycle"][d])
            if l["meas_onboard"] is not None:
                po.append(l["pred_onboard"])
                mo.append(l["meas_onboard"])
            if l["loop"]:
                nd_tot += 1
                pn = [round(x) for x in l["pred_ndir"]]
                mn = l["meas_ndir"]
                if sum(mn) == l["trains"]:
                    loops.append((pn, mn))
                    # o genoma escolhe quantos trens vao em cada sentido e o
                    # Applier os poe assim: tem que bater exato
                    if pn == mn:
                        nd_ok += 1
    summary("ciclo", pc, mc, " s")
    pl, gl = [], []
    for w in wins:
        for l in w["lines"]:
            if l.get("game_length") and l.get("pred_length"):
                pl.append(l["pred_length"])
                gl.append(l["game_length"])
    if pl:
        summary("comprimento", pl, gl, " u")
    summary("a bordo/trem", po, mo, " pax")
    pl2 = [(l["pred_onboard"], l["meas_onboard_late"]) for w in wins for l in w["lines"] if l.get("meas_onboard_late") is not None]
    if pl2:
        summary("a bordo*", [a for a, _ in pl2], [b for _, b in pl2], " pax")
    if nd_tot:
        print(f"  sentidos em loop: {nd_ok}/{len(loops)} loops com a divisao de trens prevista ({nd_tot} loops no total)")
        for pn, mn in loops[:8]:
            print(f"      previsto {pn}  medido {mn}")

    print("\nEstacoes (ativas no inicio da janela):")
    pq, mq, pov, mov = [], [], [], []
    for w in wins:
        for s in w["stations"]:
            if s["pred_q"] is None or s["meas_q"] is None:
                continue
            pq.append(s["pred_q"])
            mq.append(s["meas_q"])
            pov.append(s["pred_over"])
            mov.append(s["meas_over"])
    summary("fila media", pq, mq, " pax")
    summary("frac > cap", pov, mov)
    lq = [(s["pred_q_late"], s["meas_q_late"], s["pred_over_late"], s["meas_over_late"])
          for w in wins for s in w["stations"]
          if s.get("pred_q_late") is not None and s.get("meas_q_late") is not None]
    if lq:
        summary("fila* regime", [a for a, _, _, _ in lq], [b for _, b, _, _ in lq], " pax")
        summary("frac>cap*", [c for _, _, c, _ in lq], [d for _, _, _, d in lq])
        print("  (* ultimos 2/3 da janela, depois do transiente de reconstruir as linhas)")
    # calibracao da fracao acima da capacidade
    bins = [(0, 0.02), (0.02, 0.1), (0.1, 0.3), (0.3, 0.6), (0.6, 1.01)]
    print("  calibracao de P(fila > cap):  faixa prevista -> media prevista / media medida (n)")
    for lo, hi in bins:
        sel = [(p, m) for p, m in zip(pov, mov) if lo <= p < hi]
        if sel:
            print(
                f"      [{lo:4.2f}, {hi:4.2f})  {sum(p for p, _ in sel) / len(sel):5.2f} / "
                f"{sum(m for _, m in sel) / len(sel):5.2f}   ({len(sel)})"
            )


if __name__ == "__main__":
    main()
