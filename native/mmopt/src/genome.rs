//! Um individuo = uma rede inteira. Mesmo encoding do `Ga/Genome.cs`: rotas como
//! sequencias de indices de estacao ATIVA, e por rota loop/locomotivas/vagoes.

use crate::ctx::Ctx;
use crate::rng::Rng;

#[derive(Clone, Debug, Default, PartialEq, Eq, Hash)]
pub struct Route {
    pub stops: Vec<u16>,
    pub looped: bool,
    pub locos: u16,
    pub cars: u16,
    /// So em loop: quantos trens rodam no sentido CONTRARIO a ordem de `stops`
    /// (o resto roda na ordem). O jogo, sozinho, alterna (Line.AddTrain): o
    /// Applier poe cada trem no sentido pedido aqui.
    pub rev: u16,
}

impl Route {
    /// Rota nova com a divisao de sentidos que o jogo faria sozinho.
    pub fn new(stops: Vec<u16>, looped: bool, locos: u16, cars: u16) -> Route {
        Route { stops, looped, locos, cars, rev: if looped { locos / 2 } else { 0 } }
    }
}

/// Decomposicao do custo, na mesma ordem e unidade que `EvalBreakdown` do C#.
#[derive(Clone, Copy, Debug, Default)]
pub struct Breakdown {
    pub travel: f64,
    pub unreachable: f64,
    pub congestion: f64,
    pub station_load: f64,
    pub track: f64,
    pub crossing: f64,
    pub unserved: f64,
    pub urgency: f64,
    /// custo de sair da rede atual para esta (ver change.rs)
    pub change: f64,
    pub unreachable_pairs: u32,
    pub unserved_stations: u32,
    pub crossings_used: u32,
    pub track_length: f32,
    pub worst_line_util: f32,
    pub avg_travel_time: f32,
    pub line_crossings: u32,
    /// linhas atuais desmontadas / trens tirados de linha que fica
    pub rebuilt_lines: u32,
    pub moved_trains: u32,
}

impl Breakdown {
    pub fn total(&self) -> f64 {
        self.travel
            + self.unreachable
            + self.congestion
            + self.station_load
            + self.track
            + self.crossing
            + self.unserved
            + self.urgency
            + self.change
    }
}

#[derive(Clone, Debug, Default)]
pub struct Genome {
    pub routes: Vec<Route>,
    pub fitness: f64,
    pub bd: Breakdown,
}

impl Genome {
    pub fn new() -> Genome {
        Genome { routes: Vec::new(), fitness: f64::MAX, bd: Breakdown::default() }
    }

    pub fn invalidate(&mut self) {
        self.fitness = f64::MAX;
    }

    pub fn is_evaluated(&self) -> bool {
        self.fitness < f64::MAX
    }

    /// Chave para o cache de avaliacao.
    pub fn key(&self) -> u64 {
        // FNV-1a sobre o conteudo; colisao so custa uma avaliacao reaproveitada errada
        let mut h: u64 = 0xcbf2_9ce4_8422_2325;
        let mut eat = |v: u64| {
            h ^= v;
            h = h.wrapping_mul(0x0100_0000_01b3);
        };
        for r in &self.routes {
            eat(0xFFFF_0000 | r.looped as u64);
            eat(((r.locos as u64) << 16) | r.cars as u64 | ((if r.looped { r.rev as u64 } else { 0 }) << 32));
            for &s in &r.stops {
                eat(s as u64);
            }
        }
        h
    }

    /// Mesmas regras do Genome.Repair do C#: sem estacao repetida na rota, sem
    /// estacao inativa, tamanho entre min e max, nao mais linhas que o orcamento,
    /// loop so com 3+, frota dentro do estoque.
    pub fn repair(&mut self, ctx: &Ctx, rng: &mut Rng) {
        let na = ctx.na() as u16;
        let cfg = &ctx.cfg;
        let mut seen = vec![false; ctx.na()];
        for r in self.routes.iter_mut() {
            for s in seen.iter_mut() {
                *s = false;
            }
            r.stops.retain(|&s| {
                if s >= na || seen[s as usize] {
                    false
                } else {
                    seen[s as usize] = true;
                    true
                }
            });
            if r.stops.len() > cfg.max_stations_per_line {
                r.stops.truncate(cfg.max_stations_per_line);
            }
        }
        self.fix_water(ctx, rng);
        let min_len = cfg.min_stations_per_line.max(2);
        self.routes.retain(|r| r.stops.len() >= min_len);
        self.routes.truncate(ctx.max_lines);

        if self.routes.is_empty() {
            let a = rng.below(ctx.na()) as u16;
            let mut b = rng.below(ctx.na()) as u16;
            if b == a {
                b = (a + 1) % na;
            }
            self.routes.push(Route::new(vec![a, b], false, 1, 0));
        }

        for r in self.routes.iter_mut() {
            if !cfg.allow_loops || r.stops.len() < 3 {
                r.looped = false;
            }
        }
        self.normalize_fleet(ctx);
        for r in self.routes.iter_mut() {
            r.rev = if r.looped { r.rev.min(r.locos) } else { 0 };
        }
    }

    /// Links que gastam travessia (tunel/ponte), contando so rotas operaveis.
    pub fn water_links(&self, ctx: &Ctx) -> usize {
        let mut n = 0;
        for r in &self.routes {
            let m = r.stops.len();
            if m < 2 {
                continue;
            }
            let links = if r.looped && m >= 3 { m } else { m - 1 };
            for k in 0..links {
                if ctx.d.w(r.stops[k] as usize, r.stops[(k + 1) % m] as usize) {
                    n += 1;
                }
            }
        }
        n
    }

    /// O jogo nao deixa desenhar link sobre a agua sem tunel/ponte no estoque
    /// (Link.ReserveCrossing): rede que passa do orcamento nao existe. Corta
    /// links de agua sorteados ate caber: loop abre naquele link, ponta de linha
    /// perde a estacao, e trecho do meio parte a linha em duas (ou fica o
    /// pedaco maior, se nao ha linha sobrando).
    pub fn fix_water(&mut self, ctx: &Ctx, rng: &mut Rng) {
        if ctx.d.hull.is_empty() {
            return;
        }
        let budget = ctx.p.budget.crossings as usize;
        let mut guard = 0;
        while self.water_links(ctx) > budget && guard < 64 {
            guard += 1;
            let mut cands: Vec<(usize, usize)> = Vec::new();
            for (ri, r) in self.routes.iter().enumerate() {
                let m = r.stops.len();
                if m < 2 {
                    continue;
                }
                let links = if r.looped && m >= 3 { m } else { m - 1 };
                for k in 0..links {
                    if ctx.d.w(r.stops[k] as usize, r.stops[(k + 1) % m] as usize) {
                        cands.push((ri, k));
                    }
                }
            }
            if cands.is_empty() {
                break;
            }
            let (ri, k) = cands[rng.below(cands.len())];
            self.cut_link(ctx, ri, k);
        }
    }

    fn cut_link(&mut self, ctx: &Ctx, ri: usize, k: usize) {
        let can_split = self.routes.len() < ctx.max_lines;
        let r = &mut self.routes[ri];
        let m = r.stops.len();
        if r.looped && m >= 3 {
            // o link k liga stops[k] a stops[k+1]: a linha aberta comeca depois dele
            r.stops.rotate_left((k + 1) % m);
            r.looped = false;
            r.rev = 0;
            return;
        }
        if k == 0 {
            r.stops.remove(0);
            return;
        }
        if k + 2 >= m {
            r.stops.pop();
            return;
        }
        let tail = r.stops.split_off(k + 1);
        if can_split {
            // o trem da linha nova sai desta, se ela tem mais de um
            if r.locos > 1 {
                r.locos -= 1;
            }
            self.routes.push(Route::new(tail, false, 1, 0));
        } else if tail.len() > r.stops.len() {
            r.stops = tail;
        }
    }

    /// Cada linha precisa de >= 1 locomotiva (nenhuma e de graca: a linha nova
    /// consome uma do estoque). Estourou o orcamento? Corta de quem tem menos
    /// demanda por assento.
    pub fn normalize_fleet(&mut self, ctx: &Ctx) {
        let budget = ctx.loco_budget();
        self.routes.truncate(budget);
        let n = self.routes.len();
        for (i, r) in self.routes.iter_mut().enumerate() {
            r.locos = r.locos.clamp(1, ctx.max_locos_for(i) as u16);
        }
        let mut used: usize = self.routes.iter().map(|r| r.locos as usize).sum();
        while used > budget {
            match self.least_deserving(ctx, false) {
                Some(v) => {
                    self.routes[v].locos -= 1;
                    used -= 1;
                }
                None => break,
            }
        }
        let car_budget = ctx.car_budget();
        let per = ctx.cfg.max_cars_per_loco as u16;
        for r in self.routes.iter_mut() {
            r.cars = r.cars.min(r.locos * per);
        }
        let mut used: usize = self.routes.iter().map(|r| r.cars as usize).sum();
        while used > car_budget {
            match self.least_deserving(ctx, true) {
                Some(v) => {
                    self.routes[v].cars -= 1;
                    used -= 1;
                }
                None => break,
            }
        }
        debug_assert!(self.routes.len() == n);
    }

    pub fn route_demand(&self, ctx: &Ctx, i: usize) -> f32 {
        self.routes[i].stops.iter().map(|&s| ctx.station_weight[s as usize]).sum()
    }

    pub fn seats(&self, ctx: &Ctx, i: usize) -> f32 {
        let r = &self.routes[i];
        ((r.locos + r.cars) as f32 * ctx.p.params.railcar_capacity.max(1) as f32).max(1.0)
    }

    fn least_deserving(&self, ctx: &Ctx, cars: bool) -> Option<usize> {
        let mut best = None;
        let mut best_score = f32::MAX;
        for i in 0..self.routes.len() {
            let r = &self.routes[i];
            if cars {
                if r.cars == 0 {
                    continue;
                }
            } else if r.locos <= 1 {
                continue;
            }
            let score = self.route_demand(ctx, i) / self.seats(ctx, i);
            if score < best_score {
                best_score = score;
                best = Some(i);
            }
        }
        best
    }

    pub fn used_locos(&self) -> usize {
        self.routes.iter().map(|r| r.locos as usize).sum()
    }
    pub fn used_cars(&self) -> usize {
        self.routes.iter().map(|r| r.cars as usize).sum()
    }

    /// Formato de troca com o C#:
    /// [rotas, (loop, locos, vagoes, trens no sentido contrario, len, paradas...)*]
    pub fn encode(&self, out: &mut Vec<i32>) {
        out.clear();
        out.push(self.routes.len() as i32);
        for r in &self.routes {
            out.push(r.looped as i32);
            out.push(r.locos as i32);
            out.push(r.cars as i32);
            out.push(r.rev as i32);
            out.push(r.stops.len() as i32);
            out.extend(r.stops.iter().map(|&s| s as i32));
        }
    }
}
