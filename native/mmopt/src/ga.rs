//! Motor do AG. Roda numa thread propria criada pelo FFI e publica progresso
//! num `Shared` que o C# consulta todo frame.
//!
//! Igual ao GaEngine do C# (torneio, elitismo, crossover por rota, mutacoes de
//! dominio, reinicio por estagnacao), mais:
//!   - avaliacao paralela com threads com escopo, um Evaluator por thread;
//!   - cache de avaliacao por hash do genoma (elites e clones nao reavaliam);
//!   - busca local (memetico) no melhor individuo a cada N geracoes.

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, AtomicU8, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Instant;

use crate::ctx::Ctx;
use crate::eval::Evaluator;
use crate::genome::{Breakdown, Genome};
use crate::ops;
use crate::rng::Rng;

pub const ST_IDLE: u8 = 0;
pub const ST_RUNNING: u8 = 1;
pub const ST_DONE: u8 = 2;
pub const ST_CANCELLED: u8 = 3;
pub const ST_FAILED: u8 = 4;

const HISTORY_CAP: usize = 4000;
const CACHE_CAP: usize = 250_000;

#[derive(Default)]
pub struct Inner {
    pub best: Option<Genome>,
    pub best_fitness: f64,
    pub avg: f64,
    pub worst: f64,
    pub stagnant: u32,
    pub hist_best: Vec<f32>,
    pub hist_avg: Vec<f32>,
    pub error: String,
    pub initial_fitness: f64,
    /// resultado da recomendacao de upgrade: (tipo, quantidade, fitness, estacao)
    pub upgrade: Vec<[f64; 4]>,
}

pub struct Shared {
    pub state: AtomicU8,
    pub cancel: AtomicBool,
    pub generation: AtomicU32,
    pub total_generations: AtomicU32,
    pub evals: AtomicU64,
    pub cache_hits: AtomicU64,
    pub eval_ns: AtomicU64,
    pub elapsed_ms: AtomicU64,
    pub threads: AtomicU32,
    pub inner: Mutex<Inner>,
}

impl Shared {
    pub fn new() -> Shared {
        Shared {
            state: AtomicU8::new(ST_IDLE),
            cancel: AtomicBool::new(false),
            generation: AtomicU32::new(0),
            total_generations: AtomicU32::new(0),
            evals: AtomicU64::new(0),
            cache_hits: AtomicU64::new(0),
            eval_ns: AtomicU64::new(0),
            elapsed_ms: AtomicU64::new(0),
            threads: AtomicU32::new(0),
            inner: Mutex::new(Inner { best_fitness: f64::MAX, initial_fitness: f64::MAX, ..Default::default() }),
        }
    }
}

impl Default for Shared {
    fn default() -> Self {
        Shared::new()
    }
}

struct Pool {
    evals: Vec<Evaluator>,
    cache: HashMap<u64, (f64, Breakdown)>,
}

impl Pool {
    /// Avalia quem nao tem fitness. Cache primeiro; o resto em paralelo.
    fn evaluate(&mut self, ctx: &Ctx, pop: &mut [Genome], shared: &Shared) {
        let mut todo: Vec<usize> = Vec::new();
        for (i, g) in pop.iter_mut().enumerate() {
            if g.is_evaluated() {
                continue;
            }
            if let Some(&(f, bd)) = self.cache.get(&g.key()) {
                g.fitness = f;
                g.bd = bd;
                shared.cache_hits.fetch_add(1, Ordering::Relaxed);
            } else {
                todo.push(i);
            }
        }
        if todo.is_empty() {
            return;
        }
        let t0 = Instant::now();
        let workers = self.evals.len().min(todo.len()).max(1);
        if workers == 1 {
            let ev = &mut self.evals[0];
            for &i in &todo {
                ev.evaluate(ctx, &mut pop[i]);
            }
        } else {
            // cada worker pega um fatiamento intercalado (carga equilibrada)
            let mut buckets: Vec<Vec<Genome>> = vec![Vec::new(); workers];
            for (k, &i) in todo.iter().enumerate() {
                buckets[k % workers].push(std::mem::take(&mut pop[i]));
            }
            std::thread::scope(|s| {
                for (b, ev) in buckets.iter_mut().zip(self.evals.iter_mut()) {
                    s.spawn(move || {
                        for g in b.iter_mut() {
                            ev.evaluate(ctx, g);
                        }
                    });
                }
            });
            let mut its: Vec<std::vec::IntoIter<Genome>> = buckets.into_iter().map(|b| b.into_iter()).collect();
            for (k, &i) in todo.iter().enumerate() {
                pop[i] = its[k % workers].next().expect("bucket");
            }
        }
        let ns = t0.elapsed().as_nanos() as u64;
        shared.evals.fetch_add(todo.len() as u64, Ordering::Relaxed);
        shared.eval_ns.fetch_add(ns * workers as u64, Ordering::Relaxed);
        if self.cache.len() > CACHE_CAP {
            self.cache.clear();
        }
        for &i in &todo {
            self.cache.insert(pop[i].key(), (pop[i].fitness, pop[i].bd));
        }
    }
}

pub fn run(ctx: Arc<Ctx>, shared: Arc<Shared>) {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        evolve(&ctx, &shared, &[], &shared.cancel);
    }));
    match result {
        Ok(()) => {
            let st = if shared.cancel.load(Ordering::Relaxed) { ST_CANCELLED } else { ST_DONE };
            shared.state.store(st, Ordering::SeqCst);
        }
        Err(e) => {
            let msg = if let Some(s) = e.downcast_ref::<&str>() {
                s.to_string()
            } else if let Some(s) = e.downcast_ref::<String>() {
                s.clone()
            } else {
                "panico no nucleo nativo".to_string()
            };
            if let Ok(mut inner) = shared.inner.lock() {
                inner.error = msg;
            }
            shared.state.store(ST_FAILED, Ordering::SeqCst);
        }
    }
}

/// Roda o AG e publica o progresso em `shared`; devolve o melhor. `seeds` entram
/// na populacao inicial (reparados); `cancel` interrompe entre geracoes.
pub fn evolve(ctx: &Ctx, shared: &Shared, seeds: &[Genome], cancel: &AtomicBool) -> Option<Genome> {
    let cfg = &ctx.cfg;
    let t0 = Instant::now();
    let seed = if cfg.seed != 0 {
        cfg.seed
    } else {
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_nanos() as u64)
            .unwrap_or(1)
    };
    let mut rng = Rng::new(seed);
    let threads = cfg.thread_count();
    shared.threads.store(threads as u32, Ordering::Relaxed);
    shared.total_generations.store(cfg.generations as u32, Ordering::Relaxed);
    let mut pool = Pool { evals: (0..threads).map(|_| Evaluator::new(ctx)).collect(), cache: HashMap::new() };

    // populacao inicial
    let mut pop: Vec<Genome> = Vec::with_capacity(cfg.population);
    for s in seeds.iter().take(cfg.population / 4) {
        let mut g = s.clone();
        g.repair(ctx, &mut rng);
        g.invalidate();
        pop.push(g);
    }
    if cfg.seed_from_current && !ctx.p.current.is_empty() {
        // a rede atual com as estacoes novas encaixadas onde custa menos: o
        // que o jogador faria sem pensar muito
        if let Some(mut g) = ops::current_network(ctx) {
            ops::connect_all_orphans(ctx, &mut g);
            ops::fill_spare_fleet(ctx, &mut g);
            g.repair(ctx, &mut rng);
            pop.push(g);
        }
        let seeds = (cfg.population / 10).max(1);
        for i in 0..seeds {
            let mut g = if i == 0 {
                ops::current_network(ctx).unwrap_or_else(|| ops::from_current(ctx, &mut rng))
            } else {
                ops::from_current(ctx, &mut rng)
            };
            if i > 1 {
                for _ in 0..3 {
                    ops::mutate(ctx, &mut g, &mut rng);
                }
            }
            g.repair(ctx, &mut rng);
            pop.push(g);
        }
    }
    while pop.len() < cfg.population {
        let mut g = ops::random_constructive(ctx, &mut rng);
        g.repair(ctx, &mut rng);
        pop.push(g);
    }

    // fitness da rede atual, para a UI mostrar o ganho
    if let Some(mut cur) = ops::current_network(ctx) {
        cur.repair(ctx, &mut rng);
        let f = pool.evals[0].evaluate(ctx, &mut cur);
        shared.inner.lock().unwrap().initial_fitness = f;
    }

    pool.evaluate(ctx, &mut pop, shared);
    sort(&mut pop);
    publish(0, &pop, shared, t0);

    let elite = cfg.elitism.min(cfg.population - 1);
    let mut next: Vec<Genome> = Vec::with_capacity(cfg.population);
    let mut seen: std::collections::HashSet<u64> = std::collections::HashSet::with_capacity(cfg.population * 2);
    for gen in 1..=cfg.generations {
        if cancel.load(Ordering::Relaxed) {
            break;
        }
        if cfg.time_budget_ms > 0.0 && t0.elapsed().as_secs_f64() * 1000.0 >= cfg.time_budget_ms {
            break;
        }
        next.clear();
        next.extend(pop.iter().take(elite).cloned());
        seen.clear();
        for g in next.iter() {
            seen.insert(g.key());
        }
        while next.len() < cfg.population {
            let a = tournament(&pop, cfg.tournament, &mut rng);
            let mut child = if rng.chance(cfg.crossover_rate) {
                let b = tournament(&pop, cfg.tournament, &mut rng);
                ops::crossover(&pop[a], &pop[b], &mut rng)
            } else {
                pop[a].clone()
            };
            if rng.chance(cfg.mutation_rate) {
                let k = 1 + rng.below(cfg.mutations_per_genome.max(1));
                for _ in 0..k {
                    ops::mutate(ctx, &mut child, &mut rng);
                }
            }
            child.repair(ctx, &mut rng);
            // Filho repetido (ja avaliado, ou gemeo de outro desta geracao) nao
            // ensina nada: com a populacao convergida isso era ~90% dos filhos.
            // Muta de novo algumas vezes antes de desistir.
            let mut tries = 0;
            while tries < 4 {
                let k = child.key();
                if !pool.cache.contains_key(&k) && !seen.contains(&k) {
                    break;
                }
                ops::mutate(ctx, &mut child, &mut rng);
                child.repair(ctx, &mut rng);
                tries += 1;
            }
            seen.insert(child.key());
            child.invalidate();
            next.push(child);
        }
        pool.evaluate(ctx, &mut next, shared);
        std::mem::swap(&mut pop, &mut next);
        sort(&mut pop);

        if cfg.local_search_every > 0 && gen % cfg.local_search_every == 0 {
            local_search(ctx, &mut pool, &mut pop, &mut rng, shared);
        }

        let stagnant = publish(gen, &pop, shared, t0);

        if cfg.stagnation_restart > 0 && stagnant as usize >= cfg.stagnation_restart {
            let keep = (cfg.population / 5).max(1);
            for g in pop.iter_mut().skip(keep) {
                let mut ng = ops::random_constructive(ctx, &mut rng);
                ng.repair(ctx, &mut rng);
                *g = ng;
            }
            pool.evaluate(ctx, &mut pop, shared);
            sort(&mut pop);
            shared.inner.lock().unwrap().stagnant = 0;
        }
    }
    shared.elapsed_ms.store(t0.elapsed().as_millis() as u64, Ordering::Relaxed);
    let best = shared.inner.lock().unwrap().best.clone();
    best
}

/// Memetico: vizinhanca aleatoria do melhor (1-2 mutacoes), avaliada em lote
/// paralelo; aceita a melhor melhora e repete enquanto houver ganho.
fn local_search(ctx: &Ctx, pool: &mut Pool, pop: &mut [Genome], rng: &mut Rng, shared: &Shared) {
    let batch = (ctx.cfg.population / 2).clamp(16, 256);
    let mut best = pop[0].clone();
    for _round in 0..4 {
        let mut cand: Vec<Genome> = (0..batch)
            .map(|_| {
                let mut g = best.clone();
                let k = 1 + rng.below(2);
                for _ in 0..k {
                    ops::mutate(ctx, &mut g, rng);
                }
                g.repair(ctx, rng);
                g.invalidate();
                g
            })
            .collect();
        pool.evaluate(ctx, &mut cand, shared);
        let Some(top) = cand.into_iter().min_by(|a, b| a.fitness.total_cmp(&b.fitness)) else { break };
        if top.fitness < best.fitness - 1e-9 {
            best = top;
        } else {
            break;
        }
    }
    if best.fitness < pop[0].fitness {
        let last = pop.len() - 1;
        pop[last] = best;
        sort(pop);
    }
}

fn sort(pop: &mut [Genome]) {
    pop.sort_by(|a, b| a.fitness.total_cmp(&b.fitness));
}

fn tournament(pop: &[Genome], k: usize, rng: &mut Rng) -> usize {
    let mut best = rng.below(pop.len());
    for _ in 1..k.max(2) {
        let c = rng.below(pop.len());
        if pop[c].fitness < pop[best].fitness {
            best = c;
        }
    }
    best
}

/// Publica a geracao; devolve ha quantas geracoes o melhor nao melhora.
fn publish(gen: usize, pop: &[Genome], shared: &Shared, t0: Instant) -> u32 {
    let best = pop[0].fitness;
    let worst = pop[pop.len() - 1].fitness;
    let valid: Vec<f64> = pop.iter().map(|g| g.fitness).filter(|f| f.is_finite() && *f < f64::MAX / 8.0).collect();
    let avg = if valid.is_empty() { 0.0 } else { valid.iter().sum::<f64>() / valid.len() as f64 };
    let mut inner = shared.inner.lock().unwrap();
    let improved = best < inner.best_fitness - 1e-6;
    inner.stagnant = if improved || inner.best.is_none() { 0 } else { inner.stagnant + 1 };
    if improved || inner.best.is_none() {
        inner.best = Some(pop[0].clone());
        inner.best_fitness = best;
    }
    inner.avg = avg;
    inner.worst = worst;
    inner.hist_best.push(best as f32);
    inner.hist_avg.push(avg as f32);
    if inner.hist_best.len() > HISTORY_CAP {
        inner.hist_best.remove(0);
        inner.hist_avg.remove(0);
    }
    shared.generation.store(gen as u32, Ordering::Relaxed);
    shared.elapsed_ms.store(t0.elapsed().as_millis() as u64, Ordering::Relaxed);
    inner.stagnant
}
