//! Quanto custa sair da rede atual para a do genoma.
//!
//! O Applier (`Core/Applier.cs`) mexe na rede como um jogador mexeria, e cada
//! tipo de mexida tem um preco diferente na partida:
//!   - linha IGUAL (mesmas paradas, a menos de sentido e de rotacao do loop):
//!     fica como esta, os trens nem percebem;
//!   - linha que so GANHA estacoes (a atual e subsequencia da nova, mesmo tipo
//!     de linha): o Applier estica a ponta pelo terminal e encaixa no meio
//!     arrastando o trecho, com os trens rodando. Custo quase zero;
//!   - qualquer outra mudanca: a linha e desmontada (`Line.Mothball`, como o
//!     jogador apagando: os trens terminam o trecho e DESEMBARCAM todo mundo na
//!     proxima estacao) e montada de novo. Quem estava a bordo volta para a
//!     fila, e a linha nova comeca com os trens num lugar so;
//!   - trem tirado de uma linha que fica: desembarca todo mundo na proxima
//!     estacao antes de ir para a outra.
//!
//! O custo vem em passageiro-segundo (quanta espera a mais a mexida gera) e
//! entra no fitness dividido pelo horizonte, na mesma unidade do custo de
//! viagem (passageiros em transito, em media). Sem isso o AG trocava a rede
//! inteira por 1% de ganho a cada rodada. As regras de casamento aqui e no
//! Applier tem que ser as mesmas.

use crate::ctx::Ctx;
use crate::genome::Route;
use crate::problem::CurrentRoute;

/// Linha desmontada e montada de novo: os trens recomecam de um ponto so e as
/// estacoes da ponta esperam ate um ciclo a mais.
pub const LINE_REBUILD: f64 = 300.0;
/// Cada passageiro desembarcado espera de novo (meio headway + reembarque).
pub const PER_DUMPED: f64 = 25.0;
/// Trem tirado de uma linha que fica (fora os passageiros dele).
pub const TRAIN_MOVED: f64 = 60.0;
/// Estacao encaixada numa linha existente (o trecho muda com os trens rodando).
pub const PER_INSERT: f64 = 20.0;
/// Trem de loop que muda de sentido.
pub const TRAIN_REVERSED: f64 = 10.0;

#[derive(Clone, Copy, Debug, Default)]
pub struct ChangeCost {
    /// passageiro-segundos
    pub pax_s: f64,
    pub rebuilt: u32,
    pub edited: u32,
    pub inserted: u32,
    pub moved_trains: u32,
}

/// Como a rota `g` sai da rota atual `c` sem desmontar: `Some((inseridas,
/// mesmo_sentido))` se `c` e subsequencia de `g` (linha aberta: na ordem ou ao
/// contrario; loop: ciclica, nos dois sentidos) e as duas sao do mesmo tipo.
/// `None` = so desmontando.
pub fn relation(c: &[u16], c_loop: bool, g: &[u16], g_loop: bool) -> Option<(usize, bool)> {
    if c.len() < 2 || g.len() < c.len() || c_loop != g_loop {
        return None;
    }
    let ins = g.len() - c.len();
    if !c_loop {
        if is_subseq(c, g.iter().copied()) {
            return Some((ins, true));
        }
        if is_subseq(c, g.iter().rev().copied()) {
            return Some((ins, false));
        }
        return None;
    }
    // loop: comeca onde a primeira parada da atual esta na nova e da uma volta
    let start = g.iter().position(|&s| s == c[0])?;
    let m = g.len();
    let fwd = (0..m).map(|k| g[(start + k) % m]);
    if is_subseq(c, fwd) {
        return Some((ins, true));
    }
    let bwd = (0..m).map(|k| g[(start + m - k) % m]);
    if is_subseq(c, bwd) {
        return Some((ins, false));
    }
    None
}

fn is_subseq(c: &[u16], g: impl Iterator<Item = u16>) -> bool {
    let mut i = 0;
    for s in g {
        if i < c.len() && c[i] == s {
            i += 1;
        }
    }
    i == c.len()
}

/// Casamento guloso atual -> genoma, igual ao do Applier: as linhas atuais,
/// da maior para a menor, pegam a rota do genoma ainda livre que as contem com
/// menos estacoes inseridas (igual primeiro). Devolve, por linha atual, o
/// indice da rota casada e o sentido.
pub fn match_routes(current: &[CurrentRoute], routes: &[Route], out: &mut Vec<Option<(usize, bool)>>) {
    out.clear();
    out.resize(current.len(), None);
    let mut order: Vec<usize> = (0..current.len()).collect();
    order.sort_by(|&a, &b| current[b].stops.len().cmp(&current[a].stops.len()).then(a.cmp(&b)));
    let mut taken = vec![false; routes.len()];
    for ci in order {
        let c = &current[ci];
        let mut best: Option<(usize, usize, bool)> = None;
        for (gi, g) in routes.iter().enumerate() {
            if taken[gi] || g.locos == 0 {
                continue;
            }
            if let Some((ins, same)) = relation(&c.stops, c.looped && c.stops.len() >= 3, &g.stops, g.looped && g.stops.len() >= 3) {
                if best.map_or(true, |b| ins < b.1) {
                    best = Some((gi, ins, same));
                }
            }
        }
        if let Some((gi, _, same)) = best {
            taken[gi] = true;
            out[ci] = Some((gi, same));
        }
    }
}

pub fn change_cost(ctx: &Ctx, routes: &[Route], scratch: &mut Vec<Option<(usize, bool)>>) -> ChangeCost {
    let cur = &ctx.p.current;
    let mut cc = ChangeCost::default();
    if cur.is_empty() {
        return cc;
    }
    match_routes(cur, routes, scratch);
    for (ci, c) in cur.iter().enumerate() {
        let onboard = c.onboard as f64;
        let Some((gi, same)) = scratch[ci] else {
            cc.rebuilt += 1;
            cc.pax_s += LINE_REBUILD + PER_DUMPED * onboard;
            continue;
        };
        let g = &routes[gi];
        let ins = g.stops.len() - c.stops.len();
        if ins > 0 {
            cc.edited += 1;
            cc.inserted += ins as u32;
            cc.pax_s += PER_INSERT * ins as f64;
        }
        // frota: trem que sai desembarca a parte dele de quem esta a bordo
        let locos = c.locos.max(1) as f64;
        let out = c.locos.saturating_sub(g.locos) as f64;
        if out > 0.0 {
            cc.moved_trains += out as u32;
            cc.pax_s += out * (TRAIN_MOVED + PER_DUMPED * onboard / locos);
        }
        if c.looped && g.looped {
            // trens no sentido da rota atual, antes e depois
            let fwd_now = c.locos.saturating_sub(c.rev) as i32;
            let g_fwd = g.locos.saturating_sub(g.rev.min(g.locos)) as i32;
            let fwd_new = if same { g_fwd } else { g.locos as i32 - g_fwd };
            let kept = c.locos.min(g.locos) as i32;
            let flips = (fwd_now.min(kept) - fwd_new).abs().min(kept);
            cc.pax_s += TRAIN_REVERSED * flips as f64;
        }
    }
    cc
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn open_line_extension_and_insertion() {
        let c = [1u16, 2, 3];
        assert_eq!(relation(&c, false, &[1, 2, 3], false), Some((0, true)));
        assert_eq!(relation(&c, false, &[3, 2, 1], false), Some((0, false)));
        assert_eq!(relation(&c, false, &[0, 1, 5, 2, 3, 4], false), Some((3, true)));
        assert_eq!(relation(&c, false, &[4, 3, 2, 1], false), Some((1, false)));
        // ordem trocada: so desmontando
        assert_eq!(relation(&c, false, &[1, 3, 2], false), None);
        // estacao tirada: so desmontando
        assert_eq!(relation(&c, false, &[1, 3], false), None);
        // virar loop: desmonta
        assert_eq!(relation(&c, false, &[1, 2, 3], true), None);
    }

    #[test]
    fn loop_rotation_and_direction() {
        let c = [1u16, 2, 3, 4];
        assert_eq!(relation(&c, true, &[3, 4, 1, 2], true), Some((0, true)));
        assert_eq!(relation(&c, true, &[4, 3, 2, 1], true), Some((0, false)));
        assert_eq!(relation(&c, true, &[2, 9, 3, 4, 1], true), Some((1, true)));
        assert_eq!(relation(&c, true, &[1, 3, 2, 4], true), None);
    }
}
