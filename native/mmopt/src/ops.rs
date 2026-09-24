//! Operadores do AG: construcao inicial, mutacoes de dominio e crossover.
//! Port dos `Ga/GenomeBuilder.cs` e `Ga/Mutator.cs`, com duas melhorias:
//!   - quem escolhe estacao para inserir prefere as que nenhuma linha atende
//!     (estacao fora da rede e o custo mais caro do fitness);
//!   - operador novo `connect_orphan`: insercao mais barata global de uma
//!     estacao orfa, levando agua em conta.

use crate::ctx::Ctx;
use crate::genome::{Genome, Route};
use crate::rng::Rng;

const WATER_COST: f32 = 400.0;

#[inline]
fn link_cost(ctx: &Ctx, a: usize, b: usize) -> f32 {
    ctx.d.d(a, b) + if ctx.d.w(a, b) { WATER_COST } else { 0.0 }
}

// ---------------------------------------------------------------------------
// construcao
// ---------------------------------------------------------------------------

/// A rede que o jogador tem agora, com a frota real de cada linha. E contra ela
/// que o ganho do AG e medido (e o que o modo automatico usa para decidir se
/// vale reconstruir).
pub fn current_network(ctx: &Ctx) -> Option<Genome> {
    let mut g = Genome::new();
    for c in ctx.p.current.iter().take(ctx.max_lines) {
        if c.stops.len() >= 2 {
            let locos = c.locos.max(1);
            g.routes.push(Route { stops: c.stops.clone(), looped: c.looped, locos, cars: c.cars, rev: c.rev.min(locos) });
        }
    }
    if g.routes.is_empty() {
        None
    } else {
        Some(g)
    }
}

/// Liga toda estacao que nenhuma linha atende na insercao mais barata global,
/// da mais perto da rede para a mais longe. E a jogada padrao quando nasce
/// estacao: a rede atual intacta, mais o encaixe.
pub fn connect_all_orphans(ctx: &Ctx, g: &mut Genome) {
    let mut served = served_mask(ctx, g);
    loop {
        let mut best: Option<(f32, usize, usize, usize)> = None; // custo, estacao, rota, pos
        for st in (0..ctx.na()).filter(|&i| !served[i]) {
            for (ri, r) in g.routes.iter().enumerate() {
                if r.stops.len() >= ctx.cfg.max_stations_per_line {
                    continue;
                }
                let (pos, cost) = best_insert_pos(ctx, &r.stops, r.looped, st);
                if best.map_or(true, |b| cost < b.0) {
                    best = Some((cost, st, ri, pos));
                }
            }
        }
        let Some((_, st, ri, pos)) = best else { break };
        g.routes[ri].stops.insert(pos, st as u16);
        served[st] = true;
    }
}

/// Poe no trilho o que esta parado no estoque (trem ou vagao do upgrade da
/// semana), sem mexer no resto da frota: cada unidade vai para a linha com
/// mais demanda por lugar que ainda comporta.
pub fn fill_spare_fleet(ctx: &Ctx, g: &mut Genome) {
    for cars in [false, true] {
        loop {
            let (used, budget) = if cars {
                (g.used_cars(), ctx.car_budget())
            } else {
                (g.used_locos(), ctx.loco_budget())
            };
            if used >= budget {
                break;
            }
            let mut best = None;
            let mut bs = -1.0f32;
            for i in 0..g.routes.len() {
                if !fits(ctx, g, i, cars) {
                    continue;
                }
                let s = g.route_demand(ctx, i) / g.seats(ctx, i);
                if s > bs {
                    bs = s;
                    best = Some(i);
                }
            }
            match best {
                Some(i) if cars => g.routes[i].cars += 1,
                Some(i) => g.routes[i].locos += 1,
                None => break,
            }
        }
    }
}

/// Semente: a topologia atual com a frota redistribuida pela demanda.
pub fn from_current(ctx: &Ctx, rng: &mut Rng) -> Genome {
    let mut g = Genome::new();
    for c in ctx.p.current.iter().take(ctx.max_lines) {
        if c.stops.len() >= 2 {
            g.routes.push(Route::new(c.stops.clone(), c.looped, 1, 0));
        }
    }
    if g.routes.is_empty() {
        return random_constructive(ctx, rng);
    }
    distribute_fleet(ctx, &mut g, rng);
    g
}

/// k sementes espalhadas (k-means++ simplificado) -> cada estacao vai para a
/// semente mais proxima -> vizinho mais proximo dentro do cluster.
pub fn random_constructive(ctx: &Ctx, rng: &mut Rng) -> Genome {
    let na = ctx.na();
    let mut g = Genome::new();
    let mut k = 1 + rng.below(ctx.max_lines);
    if k > na / 2 {
        k = (na / 2).max(1);
    }
    let mut seeds: Vec<usize> = vec![rng.below(na)];
    while seeds.len() < k {
        let mut best = None;
        let mut best_d = -1.0f32;
        for _ in 0..8 {
            let c = rng.below(na);
            if seeds.contains(&c) {
                continue;
            }
            let near = seeds.iter().map(|&s| ctx.d.d(c, s)).fold(f32::MAX, f32::min);
            if near > best_d {
                best_d = near;
                best = Some(c);
            }
        }
        match best {
            Some(c) => seeds.push(c),
            None => break,
        }
    }
    let k = seeds.len();
    let mut clusters: Vec<Vec<usize>> = vec![Vec::new(); k];
    for s in 0..na {
        let mut bi = 0;
        let mut bd = f32::MAX;
        for (i, &sd) in seeds.iter().enumerate() {
            let d = link_cost(ctx, s, sd);
            if d < bd {
                bd = d;
                bi = i;
            }
        }
        clusters[bi].push(s);
    }
    // cluster de 1 estacao e inutil: doa para o vizinho
    for i in 0..k {
        if clusters[i].len() != 1 {
            continue;
        }
        let lone = clusters[i][0];
        let mut target = None;
        let mut bd = f32::MAX;
        for j in 0..k {
            if j == i || clusters[j].is_empty() {
                continue;
            }
            let d = ctx.d.d(lone, clusters[j][0]);
            if d < bd {
                bd = d;
                target = Some(j);
            }
        }
        if let Some(t) = target {
            clusters[t].push(lone);
            clusters[i].clear();
        }
    }
    for cl in clusters.iter().filter(|c| c.len() >= 2) {
        let mut tour = nearest_neighbour_tour(ctx, cl, rng);
        tour.truncate(ctx.cfg.max_stations_per_line);
        let looped = ctx.cfg.allow_loops && tour.len() >= 3 && rng.below(4) == 0;
        g.routes.push(Route::new(tour, looped, 1, 0));
    }
    if g.routes.is_empty() {
        let a = rng.below(na);
        let b = (a + 1 + rng.below(na.max(2) - 1)) % na;
        g.routes.push(Route::new(vec![a as u16, b as u16], false, 1, 0));
    }
    g.routes.truncate(ctx.max_lines);
    distribute_fleet(ctx, &mut g, rng);
    g
}

fn nearest_neighbour_tour(ctx: &Ctx, members: &[usize], rng: &mut Rng) -> Vec<u16> {
    let mut rem: Vec<usize> = members.to_vec();
    let mut cur = rem.swap_remove(rng.below(rem.len()));
    let mut tour = vec![cur as u16];
    while !rem.is_empty() {
        let mut bi = 0;
        let mut bd = f32::MAX;
        for (i, &c) in rem.iter().enumerate() {
            let d = link_cost(ctx, cur, c);
            if d < bd {
                bd = d;
                bi = i;
            }
        }
        cur = rem.swap_remove(bi);
        tour.push(cur as u16);
    }
    tour
}

/// 1 locomotiva por linha; a sobra vai para quem tem mais demanda por assento,
/// com 1 chance em 4 de sortear (para a populacao nao nascer clonada).
pub fn distribute_fleet(ctx: &Ctx, g: &mut Genome, rng: &mut Rng) {
    for r in g.routes.iter_mut() {
        r.locos = 1;
        r.cars = 0;
    }
    let spare = ctx.loco_budget().saturating_sub(g.routes.len());
    for _ in 0..spare {
        match neediest(ctx, g, rng, false) {
            Some(l) => g.routes[l].locos += 1,
            None => break,
        }
    }
    for _ in 0..ctx.car_budget() {
        match neediest(ctx, g, rng, true) {
            Some(l) => g.routes[l].cars += 1,
            None => break,
        }
    }
    // loop comeca com a divisao de sentidos que o jogo faria sozinho
    for r in g.routes.iter_mut() {
        r.rev = if r.looped { r.locos / 2 } else { 0 };
    }
}

fn fits(ctx: &Ctx, g: &Genome, i: usize, cars: bool) -> bool {
    let r = &g.routes[i];
    if cars {
        (r.cars as usize) < r.locos as usize * ctx.cfg.max_cars_per_loco
    } else {
        (r.locos as usize) < ctx.max_locos_for(i)
    }
}

fn neediest(ctx: &Ctx, g: &Genome, rng: &mut Rng, cars: bool) -> Option<usize> {
    let n = g.routes.len();
    if n == 0 {
        return None;
    }
    if rng.below(4) == 0 {
        let r = rng.below(n);
        if fits(ctx, g, r, cars) {
            return Some(r);
        }
    }
    let mut best = None;
    let mut bs = -1.0f32;
    for i in 0..n {
        if !fits(ctx, g, i, cars) {
            continue;
        }
        let s = g.route_demand(ctx, i) / g.seats(ctx, i);
        if s > bs {
            bs = s;
            best = Some(i);
        }
    }
    best
}

// ---------------------------------------------------------------------------
// mutacao
// ---------------------------------------------------------------------------

pub const OPERATORS: usize = 14;

pub fn mutate(ctx: &Ctx, g: &mut Genome, rng: &mut Rng) {
    if g.routes.is_empty() {
        return;
    }
    match rng.below(OPERATORS) {
        0 => insert_best_position(ctx, g, rng),
        1 => insert_random(ctx, g, rng),
        2 => remove_station(ctx, g, rng),
        3 => swap_within(g, rng),
        4 => two_opt(g, rng),
        5 => move_between(ctx, g, rng),
        6 => toggle_loop(ctx, g, rng),
        7 => shift_unit(ctx, g, rng, false),
        8 => shift_unit(ctx, g, rng, true),
        9 => add_or_drop_route(ctx, g, rng),
        10 => connect_orphan(ctx, g, rng),
        11 => link_components(ctx, g, rng),
        12 => add_spare_unit(ctx, g, rng),
        _ => flip_direction(g, rng),
    }
}

/// Trem ou vagao parado no estoque vai para uma linha (a mais apertada ou uma
/// sorteada). Sem isso so a redistribuicao inteira da frota os usava, e ela
/// tira trem de linha que esta rodando.
fn add_spare_unit(ctx: &Ctx, g: &mut Genome, rng: &mut Rng) {
    let cars = rng.below(2) == 0;
    let (used, budget) = if cars { (g.used_cars(), ctx.car_budget()) } else { (g.used_locos(), ctx.loco_budget()) };
    if used >= budget {
        return;
    }
    let target = if rng.below(2) == 0 { tightest(ctx, g, cars) } else { Some(rng.below(g.routes.len())) };
    if let Some(t) = target {
        if fits(ctx, g, t, cars) {
            if cars {
                g.routes[t].cars += 1;
            } else {
                g.routes[t].locos += 1;
            }
        }
    }
}

/// Loop: muda quantos trens rodam em cada sentido (o jogo alternaria sozinho;
/// o Applier poe cada trem no sentido escolhido). Com 1 trem, escolhe a mao.
fn flip_direction(g: &mut Genome, rng: &mut Rng) {
    let loops: Vec<usize> = (0..g.routes.len()).filter(|&i| g.routes[i].looped && g.routes[i].locos >= 1).collect();
    if loops.is_empty() {
        return;
    }
    let r = &mut g.routes[loops[rng.below(loops.len())]];
    let t = r.locos;
    if t == 1 {
        r.rev = 1 - r.rev.min(1);
    } else if rng.below(2) == 0 && r.rev < t {
        r.rev += 1;
    } else if r.rev > 0 {
        r.rev -= 1;
    } else {
        r.rev = 1;
    }
}

fn served_mask(ctx: &Ctx, g: &Genome) -> Vec<bool> {
    let mut m = vec![false; ctx.na()];
    for r in &g.routes {
        for &s in &r.stops {
            m[s as usize] = true;
        }
    }
    m
}

/// Estacao fora da rota; metade das vezes prefere uma que ninguem atende.
fn pick_station_not_in(ctx: &Ctx, g: &Genome, route: &[u16], rng: &mut Rng) -> Option<u16> {
    if rng.below(2) == 0 {
        let served = served_mask(ctx, g);
        let orphans: Vec<u16> = (0..ctx.na()).filter(|&i| !served[i]).map(|i| i as u16).collect();
        if !orphans.is_empty() {
            return Some(orphans[rng.below(orphans.len())]);
        }
    }
    for _ in 0..12 {
        let c = rng.below(ctx.na()) as u16;
        if !route.contains(&c) {
            return Some(c);
        }
    }
    None
}

fn best_insert_pos(ctx: &Ctx, stops: &[u16], looped: bool, st: usize) -> (usize, f32) {
    let m = stops.len();
    let mut best = (0usize, f32::MAX);
    if m == 0 {
        return (0, 0.0);
    }
    if !looped {
        let d0 = link_cost(ctx, st, stops[0] as usize);
        if d0 < best.1 {
            best = (0, d0);
        }
        let dz = link_cost(ctx, stops[m - 1] as usize, st);
        if dz < best.1 {
            best = (m, dz);
        }
    }
    let links = if looped { m } else { m - 1 };
    for k in 0..links {
        let a = stops[k] as usize;
        let b = stops[(k + 1) % m] as usize;
        let delta = link_cost(ctx, a, st) + link_cost(ctx, st, b) - link_cost(ctx, a, b);
        if delta < best.1 {
            best = (k + 1, delta);
        }
    }
    best
}

fn insert_best_position(ctx: &Ctx, g: &mut Genome, rng: &mut Rng) {
    let r = rng.below(g.routes.len());
    if g.routes[r].stops.len() >= ctx.cfg.max_stations_per_line {
        return;
    }
    let Some(st) = pick_station_not_in(ctx, g, &g.routes[r].stops, rng) else { return };
    if g.routes[r].stops.contains(&st) {
        return;
    }
    let route = &mut g.routes[r];
    let (pos, _) = best_insert_pos(ctx, &route.stops, route.looped, st as usize);
    route.stops.insert(pos, st);
}

fn insert_random(ctx: &Ctx, g: &mut Genome, rng: &mut Rng) {
    let r = rng.below(g.routes.len());
    if g.routes[r].stops.len() >= ctx.cfg.max_stations_per_line {
        return;
    }
    let Some(st) = pick_station_not_in(ctx, g, &g.routes[r].stops, rng) else { return };
    let route = &mut g.routes[r];
    if route.stops.contains(&st) {
        return;
    }
    let pos = rng.below(route.stops.len() + 1);
    route.stops.insert(pos, st);
}

fn remove_station(ctx: &Ctx, g: &mut Genome, rng: &mut Rng) {
    let r = rng.below(g.routes.len());
    let route = &mut g.routes[r];
    if route.stops.len() <= ctx.cfg.min_stations_per_line.max(2) {
        return;
    }
    let i = rng.below(route.stops.len());
    route.stops.remove(i);
}

fn swap_within(g: &mut Genome, rng: &mut Rng) {
    let r = rng.below(g.routes.len());
    let s = &mut g.routes[r].stops;
    if s.len() < 2 {
        return;
    }
    let (i, j) = (rng.below(s.len()), rng.below(s.len()));
    s.swap(i, j);
}

fn two_opt(g: &mut Genome, rng: &mut Rng) {
    let r = rng.below(g.routes.len());
    let s = &mut g.routes[r].stops;
    if s.len() < 4 {
        return;
    }
    let i = rng.below(s.len() - 1);
    let j = i + 1 + rng.below(s.len() - i - 1);
    s[i..=j].reverse();
}

fn move_between(ctx: &Ctx, g: &mut Genome, rng: &mut Rng) {
    let n = g.routes.len();
    if n < 2 {
        return;
    }
    let (from, to) = (rng.below(n), rng.below(n));
    if from == to
        || g.routes[from].stops.len() <= ctx.cfg.min_stations_per_line.max(2)
        || g.routes[to].stops.len() >= ctx.cfg.max_stations_per_line
    {
        return;
    }
    let idx = rng.below(g.routes[from].stops.len());
    let st = g.routes[from].stops[idx];
    if g.routes[to].stops.contains(&st) {
        return;
    }
    g.routes[from].stops.remove(idx);
    // metade das vezes na melhor posicao, metade ao acaso
    let pos = if rng.below(2) == 0 {
        best_insert_pos(ctx, &g.routes[to].stops, g.routes[to].looped, st as usize).0
    } else {
        rng.below(g.routes[to].stops.len() + 1)
    };
    g.routes[to].stops.insert(pos, st);
}

fn toggle_loop(ctx: &Ctx, g: &mut Genome, rng: &mut Rng) {
    if !ctx.cfg.allow_loops {
        return;
    }
    let r = rng.below(g.routes.len());
    if g.routes[r].stops.len() >= 3 {
        let rt = &mut g.routes[r];
        rt.looped = !rt.looped;
        rt.rev = if rt.looped { rt.locos / 2 } else { 0 };
    }
}

/// Tira um trem/vagao da linha mais folgada e poe na mais apertada. Metade das
/// vezes sorteia os dois lados, para nao virar heuristica gulosa.
fn shift_unit(ctx: &Ctx, g: &mut Genome, rng: &mut Rng, cars: bool) {
    let n = g.routes.len();
    if n < 2 {
        return;
    }
    let greedy = rng.below(2) == 0;
    let (from, to) = if greedy {
        (slackest(ctx, g, cars), tightest(ctx, g, cars))
    } else {
        (Some(rng.below(n)), Some(rng.below(n)))
    };
    let (Some(from), Some(to)) = (from, to) else { return };
    if from == to {
        return;
    }
    if cars {
        if g.routes[from].cars == 0 || !fits(ctx, g, to, true) {
            return;
        }
        g.routes[from].cars -= 1;
        g.routes[to].cars += 1;
    } else {
        if g.routes[from].locos <= 1 || !fits(ctx, g, to, false) {
            return;
        }
        g.routes[from].locos -= 1;
        g.routes[to].locos += 1;
    }
}

fn slackest(ctx: &Ctx, g: &Genome, cars: bool) -> Option<usize> {
    let mut best = None;
    let mut bs = f32::MAX;
    for i in 0..g.routes.len() {
        let r = &g.routes[i];
        if (cars && r.cars == 0) || (!cars && r.locos <= 1) {
            continue;
        }
        let s = g.route_demand(ctx, i) / g.seats(ctx, i);
        if s < bs {
            bs = s;
            best = Some(i);
        }
    }
    best
}

fn tightest(ctx: &Ctx, g: &Genome, cars: bool) -> Option<usize> {
    let mut best = None;
    let mut bs = -1.0f32;
    for i in 0..g.routes.len() {
        if !fits(ctx, g, i, cars) {
            continue;
        }
        let s = g.route_demand(ctx, i) / g.seats(ctx, i);
        if s > bs {
            bs = s;
            best = Some(i);
        }
    }
    best
}

/// Muda o numero de linhas: parte a maior em duas ou funde a menor numa vizinha.
fn add_or_drop_route(ctx: &Ctx, g: &mut Genome, rng: &mut Rng) {
    let split = rng.below(2) == 0;
    let n = g.routes.len();
    let min_len = ctx.cfg.min_stations_per_line.max(2);
    if split && n < ctx.max_lines {
        let biggest = (0..n).max_by_key(|&i| g.routes[i].stops.len()).unwrap();
        if g.routes[biggest].stops.len() < 2 * min_len {
            return;
        }
        let cut = g.routes[biggest].stops.len() / 2;
        let tail = g.routes[biggest].stops.split_off(cut);
        g.routes[biggest].looped = false;
        g.routes.push(Route::new(tail, false, 1, 0));
    } else if !split && n > 1 {
        let smallest = (0..n).min_by_key(|&i| g.routes[i].stops.len()).unwrap();
        let mut target = rng.below(n);
        if target == smallest {
            target = (smallest + 1) % n;
        }
        let moved = std::mem::take(&mut g.routes[smallest].stops);
        for st in moved {
            let t = &mut g.routes[target];
            if !t.stops.contains(&st) && t.stops.len() < ctx.cfg.max_stations_per_line {
                let (pos, _) = best_insert_pos(ctx, &t.stops, t.looped, st as usize);
                t.stops.insert(pos, st);
            }
        }
        let (lo, ca) = (g.routes[smallest].locos, g.routes[smallest].cars);
        let t = &mut g.routes[target];
        t.locos = (t.locos + lo).min(ctx.max_locos_for(target) as u16);
        t.cars += ca;
        g.routes.remove(smallest);
    }
}

/// Liga uma estacao que nenhuma linha atende, na insercao mais barata global.
fn connect_orphan(ctx: &Ctx, g: &mut Genome, rng: &mut Rng) {
    let served = served_mask(ctx, g);
    let orphans: Vec<usize> = (0..ctx.na()).filter(|&i| !served[i]).collect();
    if orphans.is_empty() {
        // sem orfa: usa como "realoca a estacao pior encaixada"
        return insert_best_position(ctx, g, rng);
    }
    let st = orphans[rng.below(orphans.len())];
    let mut best = (usize::MAX, 0usize, f32::MAX);
    for (ri, r) in g.routes.iter().enumerate() {
        if r.stops.len() >= ctx.cfg.max_stations_per_line {
            continue;
        }
        let (pos, cost) = best_insert_pos(ctx, &r.stops, r.looped, st);
        if cost < best.2 {
            best = (ri, pos, cost);
        }
    }
    if best.0 != usize::MAX {
        g.routes[best.0].stops.insert(best.1, st as u16);
    }
}

/// Rede partida em componentes (linhas que nao dividem estacao com o resto)
/// deixa passageiro preso para sempre. Pega o par de estacoes mais proximo entre
/// dois componentes e enfia uma na linha da outra, criando a baldeacao.
fn link_components(ctx: &Ctx, g: &mut Genome, rng: &mut Rng) {
    let n = g.routes.len();
    if n < 2 {
        return connect_orphan(ctx, g, rng);
    }
    // union-find sobre rotas: duas rotas se ligam se dividem estacao
    let mut parent: Vec<usize> = (0..n).collect();
    fn find(p: &mut [usize], mut x: usize) -> usize {
        while p[x] != x {
            p[x] = p[p[x]];
            x = p[x];
        }
        x
    }
    let mut owner: Vec<usize> = vec![usize::MAX; ctx.na()];
    for (ri, r) in g.routes.iter().enumerate() {
        for &s in &r.stops {
            let s = s as usize;
            if owner[s] == usize::MAX {
                owner[s] = ri;
            } else {
                let (a, b) = (find(&mut parent, owner[s]), find(&mut parent, ri));
                parent[a] = b;
            }
        }
    }
    let comps: Vec<usize> = (0..n).map(|i| find(&mut parent, i)).collect();
    let mut roots: Vec<usize> = comps.clone();
    roots.sort_unstable();
    roots.dedup();
    if roots.len() < 2 {
        return connect_orphan(ctx, g, rng);
    }
    // componente sorteado contra o resto
    let ca = roots[rng.below(roots.len())];
    let mut best = (f32::MAX, 0usize, 0usize, 0usize); // custo, rota destino, estacao, pos
    for (ra, r) in g.routes.iter().enumerate() {
        if comps[ra] != ca {
            continue;
        }
        for &s in &r.stops {
            for (rb, rr) in g.routes.iter().enumerate() {
                if comps[rb] == ca || rr.stops.len() >= ctx.cfg.max_stations_per_line {
                    continue;
                }
                let (pos, cost) = best_insert_pos(ctx, &rr.stops, rr.looped, s as usize);
                if cost < best.0 {
                    best = (cost, rb, s as usize, pos);
                }
            }
        }
    }
    if best.0 < f32::MAX && !g.routes[best.1].stops.contains(&(best.2 as u16)) {
        g.routes[best.1].stops.insert(best.3, best.2 as u16);
    }
}

/// Crossover em nivel de rota: cada slot de linha do filho vem do pai A ou do B.
pub fn crossover(a: &Genome, b: &Genome, rng: &mut Rng) -> Genome {
    let mut child = Genome::new();
    let n = a.routes.len().max(b.routes.len());
    for i in 0..n {
        let src = if i >= a.routes.len() {
            b
        } else if i >= b.routes.len() {
            a
        } else if rng.below(2) == 0 {
            a
        } else {
            b
        };
        if let Some(r) = src.routes.get(i) {
            child.routes.push(r.clone());
        }
    }
    child
}
