//! Qual upgrade pegar na segunda-feira (NewAssetScreen).
//!
//! O jogo oferece 2 opcoes (linha, vagao, travessia, interchange...) e o
//! jogador fica com uma pelo resto da partida. Aqui cada opcao vira uma
//! variante do problema com o recurso a mais, e o AG otimiza cada variante com
//! o mesmo orcamento de tempo e a mesma semente, partindo da melhor rede SEM o
//! upgrade (a base). Como a base continua valendo em toda variante, a
//! comparacao mede o que o recurso rende, nao a sorte do AG. O horizonte que
//! o C# manda aqui e mais longo que o do dia a dia: o recurso fica para sempre.
//!
//! Interchange precisa de lugar: a estacao escolhida e a que mais melhora a
//! base com capacidade de interchange e embarque num pulso so.

use std::sync::atomic::Ordering;

use crate::config::Config;
use crate::ctx::Ctx;
use crate::eval::Evaluator;
use crate::ga::{self, Shared};
use crate::genome::Genome;
use crate::problem::Problem;

// AssetType do jogo
pub const LINE: i32 = 1;
pub const LOCOMOTIVE: i32 = 2;
pub const SHINKANSEN: i32 = 3;
pub const TRAM: i32 = 4;
pub const CARRIAGE: i32 = 6;
pub const CROSSING: i32 = 7;
pub const INTERCHANGE: i32 = 8;
pub const BRIDGE: i32 = 9;
/// Controle: roda o AG de novo sem recurso a mais. Mede quanto da melhora de
/// uma opcao e so o tempo extra de busca.
pub const CONTROL: i32 = -1;

#[derive(Clone, Debug)]
pub struct Choice {
    pub kind: i32,
    pub count: i32,
    pub fitness: f64,
    /// estacao que recebe o interchange (-1 = nao se aplica)
    pub station: i32,
    pub genome: Option<Genome>,
}

/// O problema com o recurso a mais. Devolve false se a opcao nao muda nada
/// que o modelo enxergue.
pub fn apply(p: &mut Problem, kind: i32, count: i32, station: i32) -> bool {
    let c = count.max(1) as u16;
    let b = &mut p.budget;
    match kind {
        LINE => {
            b.line_budget = (b.line_budget + c).min(b.max_lines.max(1));
            true
        }
        LOCOMOTIVE | SHINKANSEN | TRAM => {
            b.locos += c;
            true
        }
        CARRIAGE => {
            b.cars += c;
            true
        }
        CROSSING | BRIDGE => {
            b.crossings += c;
            true
        }
        INTERCHANGE => {
            if station < 0 || station as usize >= p.na {
                return false;
            }
            let cap = p.params.interchange_capacity.max(1);
            let s = &mut p.stations[station as usize];
            s.interchange = true;
            s.capacity = s.capacity.max(cap);
            true
        }
        CONTROL => true,
        _ => false,
    }
}

/// Estacao ativa que, virando interchange, mais melhora a rede `g`.
pub fn best_interchange_station(problem: &Problem, cfg: &Config, g: &Genome) -> (i32, f64) {
    let mut best = (-1i32, f64::MAX);
    for s in 0..problem.na {
        if problem.stations[s].interchange {
            continue;
        }
        let mut p = problem.clone();
        apply(&mut p, INTERCHANGE, 1, s as i32);
        let ctx = Ctx::new(p, cfg.clone());
        let mut ev = Evaluator::new(&ctx);
        let mut gg = g.clone();
        gg.invalidate();
        let f = ev.evaluate(&ctx, &mut gg);
        if f < best.1 {
            best = (s as i32, f);
        }
    }
    best
}

fn absorb(parent: &Shared, child: &Shared) {
    parent.evals.fetch_add(child.evals.load(Ordering::Relaxed), Ordering::Relaxed);
    parent.cache_hits.fetch_add(child.cache_hits.load(Ordering::Relaxed), Ordering::Relaxed);
    parent.eval_ns.fetch_add(child.eval_ns.load(Ordering::Relaxed), Ordering::Relaxed);
    parent.threads.store(child.threads.load(Ordering::Relaxed), Ordering::Relaxed);
}

/// Avalia a base (indice 0, kind 0) e cada opcao. Progresso em `shared`
/// (geracao = opcoes ja avaliadas).
pub fn run(problem: &Problem, cfg: &Config, opts: &[(i32, i32)], shared: &Shared) -> Vec<Choice> {
    let t0 = std::time::Instant::now();
    shared.total_generations.store(opts.len() as u32 + 1, Ordering::Relaxed);
    let base_ctx = Ctx::new(problem.clone(), cfg.clone());
    let child = Shared::new();
    let base = ga::evolve(&base_ctx, &child, &[], &shared.cancel);
    absorb(shared, &child);
    let Some(base) = base else { return Vec::new() };
    let mut out = vec![Choice { kind: 0, count: 0, fitness: base.fitness, station: -1, genome: Some(base.clone()) }];
    shared.generation.store(1, Ordering::Relaxed);

    for (i, &(kind, count)) in opts.iter().enumerate() {
        if shared.cancel.load(Ordering::Relaxed) {
            break;
        }
        let mut p = problem.clone();
        let station = if kind == INTERCHANGE { best_interchange_station(problem, cfg, &base).0 } else { -1 };
        if !apply(&mut p, kind, count, station) {
            out.push(Choice { kind, count, fitness: base.fitness, station, genome: None });
            continue;
        }
        let ctx = Ctx::new(p, cfg.clone());
        let child = Shared::new();
        let best = ga::evolve(&ctx, &child, &[base.clone()], &shared.cancel);
        absorb(shared, &child);
        let fitness = best.as_ref().map_or(base.fitness, |g| g.fitness);
        out.push(Choice { kind, count, fitness, station, genome: best });
        shared.generation.store(i as u32 + 2, Ordering::Relaxed);
        shared.elapsed_ms.store(t0.elapsed().as_millis() as u64, Ordering::Relaxed);
    }
    shared.elapsed_ms.store(t0.elapsed().as_millis() as u64, Ordering::Relaxed);
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::problem::{synthetic, SynthSpec};

    #[test]
    fn more_resources_never_hurt() {
        let p = synthetic(&SynthSpec { active: 16, future: 2, week: 3, seed: 3, river: true, with_network: true });
        let mut cfg = Config { population: 24, generations: 30, threads: 2, seed: 9, local_search_every: 0, ..Config::default() };
        cfg.sanitize();
        let shared = Shared::new();
        let res = run(&p, &cfg, &[(CARRIAGE, 1), (LINE, 1), (INTERCHANGE, 1)], &shared);
        assert_eq!(res.len(), 4);
        let base = res[0].fitness;
        for c in &res[1..] {
            // a base continua valendo na variante: o AG nunca devolve pior
            assert!(c.fitness <= base + 1e-6, "{:?} {} > {}", c.kind, c.fitness, base);
        }
        assert!(res[3].station >= 0);
    }
}
