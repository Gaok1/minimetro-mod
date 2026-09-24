//! Avaliador v3: modelo analitico da rede, calibrado pelas regras do jogo (ver
//! vault/Dossie). Um `Evaluator` por thread: ele guarda buffers mutaveis e nao
//! aloca nada por avaliacao depois de aquecido.
//!
//! Para cada epoca (conjunto de estacoes presentes):
//!   1. monta as linhas do genoma e encaixa virtualmente as estacoes futuras
//!      (insercao mais barata), porque o jogador vai liga-las quando nascem;
//!   2. conta cruzamentos de trilho por link (40% da velocidade, ~1,3 s cada);
//!   3. separa os SENTIDOS de cada linha. Linha aberta: todo trem passa nos dois
//!      sentidos, headway = ciclo/T. Loop: o jogo alterna o sentido de cada trem
//!      novo (Line.AddTrain), entao ha ceil(T/2) trens num sentido e floor(T/2)
//!      no outro; loop com 1 trem e mao unica;
//!   4. calcula o ciclo com a cinematica real (parada = ~1,8 s de frenagem e
//!      aceleracao + meio pulso de alinhamento), 1 pulso por passageiro (ou lote
//!      unico em interchange/cidade de embarque rapido) e a chance de o trem
//!      pular a estacao quando ninguem sobe nem desce;
//!   5. roteia cada origem com Dijkstra sobre o custo PERCEBIDO do A* do jogo
//!      (espera + trecho/velocidade + 3,5 por parada + 5 por baldeacao), mas
//!      mede o tempo REAL ao longo do caminho escolhido;
//!   6. lotacao de trem pelo TRECHO mais carregado de cada sentido:
//!      fluxo x headway / lugares por trem (e o trecho que estoura, nao a media);
//!   7. fila de cada plataforma como distribuicao: por sentido chegam
//!      A = lambda x headway passageiros entre dois trens (idade uniforme, mais
//!      ruido de Poisson). Pune o excesso esperado acima da capacidade
//!      E[(fila - cap)+], que e o que dispara o timer de game over.
//! Dois passes: o segundo usa os fluxos do primeiro para dwell e lotacao.

use crate::change;
use crate::ctx::Ctx;
use crate::genome::{Breakdown, Genome};
use crate::geom::{segs_cross, Seg};

const UNSERVED_WAIT: f32 = 60.0;

/// Link de agua acima do estoque de travessias: o jogo nao deixa desenhar, a
/// rede nao existe. O repair ja corta esses links; isto garante que nenhuma
/// rede inviavel ganhe de uma viavel.
pub const CROSSING_INFEASIBLE: f64 = 1.0e6;

#[derive(Clone, Default)]
struct LineBuf {
    stops: Vec<u16>,
    looped: bool,
    trains: f32,
    seats: f32,
    /// trens rodando em cada sentido (0 = ordem do genoma, 1 = contrario)
    n_dir: [f32; 2],
    /// indice (nos vetores ride_*) do no (sentido 0, posicao 0); o no
    /// (d, j) fica em ride0 + d*m + j
    ride0: usize,
    /// primeiro link desta linha; o link k liga stops[k] a stops[k+1 mod m]
    link0: usize,
    /// soma do tempo de trecho (sem paradas) de uma passada pela linha
    run: f32,
    cycle: [f32; 2],
    headway: [f32; 2],
    crowd: [f32; 2],
    route: usize,
}

impl LineBuf {
    #[inline]
    fn m(&self) -> usize {
        self.stops.len()
    }
    #[inline]
    fn node(&self, d: usize, j: usize) -> usize {
        self.ride0 + d * self.m() + j
    }
    #[inline]
    fn has_dir(&self, d: usize) -> bool {
        self.n_dir[d] > 0.0
    }
    /// o trem do sentido d sai de j para outra estacao
    #[inline]
    fn has_next(&self, d: usize, j: usize) -> bool {
        if self.looped {
            return true;
        }
        if d == 0 {
            j + 1 < self.m()
        } else {
            j > 0
        }
    }
    /// o trem do sentido d chega em j vindo de outra estacao
    #[inline]
    fn has_prev(&self, d: usize, j: usize) -> bool {
        if self.looped {
            return true;
        }
        if d == 0 {
            j > 0
        } else {
            j + 1 < self.m()
        }
    }
    #[inline]
    fn next(&self, d: usize, j: usize) -> usize {
        let m = self.m();
        if d == 0 {
            (j + 1) % m
        } else {
            (j + m - 1) % m
        }
    }
    /// link percorrido saindo de j no sentido d (indice global)
    #[inline]
    fn link_from(&self, d: usize, j: usize) -> usize {
        self.link0 + if d == 0 { j } else { self.next(1, j) }
    }
    /// Parada em que acontece o embarque em (d, j). Na ponta de linha aberta o
    /// trem chega por um sentido e inverte: quem sobe para o outro lado sobe
    /// nessa mesma parada.
    #[inline]
    fn board_visit(&self, d: usize, j: usize) -> usize {
        if !self.looped {
            let m = self.m();
            if d == 0 && j == 0 {
                return self.node(1, 0);
            }
            if d == 1 && j + 1 == m {
                return self.node(0, m - 1);
            }
        }
        self.node(d, j)
    }
    /// parada obrigatoria: ponta de linha aberta (o trem inverte ali)
    #[inline]
    fn terminal_visit(&self, d: usize, j: usize) -> bool {
        !self.looped && ((d == 0 && j + 1 == self.m()) || (d == 1 && j == 0))
    }
}

/// Detalhe do modelo para o cenario de hoje, para comparar com o jogo.
#[derive(Clone, Debug, Default)]
pub struct LineExplain {
    pub route: usize,
    pub stops: usize,
    pub looped: bool,
    pub trains: f32,
    pub seats: f32,
    pub n_dir: [f32; 2],
    /// tempo de trecho de uma passada, sem paradas
    pub run: f32,
    pub cycle: [f32; 2],
    pub headway: [f32; 2],
    /// pior trecho de cada sentido: fluxo x headway / lugares por trem
    pub max_util: [f32; 2],
    /// passageiros a bordo, media por trem (Little)
    pub mean_onboard: f32,
    /// paradas feitas por passada (esperado; ponta conta sempre)
    pub stops_made: f32,
    /// comprimento de uma passada (soma dos links, octilinear) e cruzamentos
    pub length: f32,
    pub crossings: u32,
}

#[derive(Clone, Debug, Default)]
pub struct StationExplain {
    /// fila em regime (media), sem a fila atual nem o acumulo
    pub mean_queue: f32,
    pub sd_queue: f32,
    /// fracao do tempo acima da capacidade e E[(fila - cap)+], media na janela
    /// de acumulo do fitness
    pub p_over: f32,
    pub excess: f32,
    /// passageiros/s que embarcam aqui (origem + baldeacao)
    pub board_rate: f32,
    /// passageiros/s que nascem aqui
    pub spawn_rate: f32,
    /// passageiros/s presos aqui sem rota
    pub stuck_rate: f32,
    /// passageiros/s que ficam na plataforma: trem cheio / sem rota / soma
    pub growth_cap: f32,
    pub growth_route: f32,
    pub growth: f32,
    /// lugares livres/s nos trens que embarcam aqui
    pub spare: f32,
    /// fila atual: que tem rota (escoa) / sem rota (fica) / soma
    pub backlog_drain: f32,
    pub backlog_stay: f32,
    pub backlog: f32,
    pub capacity: f32,
    /// na janela do horizonte (o que o jogo deve medir): fila media e fracao
    /// do tempo acima da capacidade
    pub window_queue: f32,
    pub window_p_over: f32,
    /// idem so nos ultimos 2/3 da janela (depois do transiente de aplicar a rede)
    pub late_queue: f32,
    pub late_p_over: f32,
}

#[derive(Clone, Copy, Debug, Default)]
struct Risk {
    excess: f64,
    p_over: f64,
    pen: f64,
}

#[derive(Clone, Debug, Default)]
pub struct Explain {
    pub lines: Vec<LineExplain>,
    /// so as estacoes ativas, no indice do genoma
    pub stations: Vec<StationExplain>,
    /// passageiros/s entregues (demanda com rota) e demanda total, hoje
    pub delivered_rate: f32,
    pub demand_rate: f32,
    pub avg_travel_time: f32,
    /// duracao da janela (horizonte), s
    pub window: f32,
}

pub struct Evaluator {
    lines: Vec<LineBuf>,
    // links (por linha, na ordem): extremidades, comprimento, cruzamentos, tempo
    link_a: Vec<u16>,
    link_b: Vec<u16>,
    link_len: Vec<f32>,
    link_x: Vec<u16>,
    link_run: Vec<f32>,
    // por no de "dentro do trem" (linha, sentido, posicao)
    ride_station: Vec<u16>,
    ride_line: Vec<u16>,
    ride_dir: Vec<u8>,
    ride_pos: Vec<u16>,
    stop_cost: Vec<f32>,
    board_rate: Vec<f32>,
    alight_rate: Vec<f32>,
    visit_rate: Vec<f32>,
    // nos de embarque por estacao (CSR), para a fila
    sr_head: Vec<u32>,
    sr_list: Vec<u32>,
    // trechos, por no de trem: proximo no no mesmo sentido (u32::MAX = fim da
    // linha), custo percebido e real de ir ate ele, e o link dirigido
    ride_next: Vec<u32>,
    ride_perc: Vec<f32>,
    ride_real: Vec<f32>,
    ride_dl: Vec<u32>,
    /// espera (meio headway x lotacao) por (linha, sentido)
    wait: Vec<f32>,
    // dijkstra so sobre estacoes: cada linha e percorrida direto
    dist: Vec<f32>,
    done: Vec<bool>,
    /// como se chegou na estacao: (no de embarque, no de desembarque, estacao de onde veio)
    prev: Vec<(u32, u32, u32)>,
    /// melhor custo ja visto dentro do trem, por no de trem (poda de dominancia)
    ride_best: Vec<f32>,
    heap: Vec<(u32, u32)>,
    // acumuladores por cenario
    dflow: Vec<f64>,
    /// utilizacao do pior trecho por (cenario, linha, sentido)
    util: Vec<f32>,
    boards: Vec<f64>,
    onboard: Vec<f64>,
    stuck: Vec<f64>,
    /// fila atual (so a epoca de hoje): que tem rota e escoa / que fica
    bl_drain: Vec<f64>,
    bl_stuck: Vec<f64>,
    travel: Vec<f64>,
    unreach: Vec<f64>,
    served: Vec<bool>,
    need: Vec<bool>,
    found: Vec<u32>,
    scen_w: Vec<f32>,
    match_buf: Vec<Option<(usize, bool)>>,
    /// se Some, registra (epoca, origem, slot) de cada par sem rota (diagnostico)
    pub trace: Option<Vec<(u32, u32, u32)>>,
    /// se Some, preenchido com o detalhe do cenario de hoje
    pub explain: Option<Explain>,
}

impl Evaluator {
    pub fn new(ctx: &Ctx) -> Evaluator {
        let n = ctx.n();
        Evaluator {
            lines: Vec::with_capacity(16),
            link_a: Vec::new(),
            link_b: Vec::new(),
            link_len: Vec::new(),
            link_x: Vec::new(),
            link_run: Vec::new(),
            ride_station: Vec::new(),
            ride_line: Vec::new(),
            ride_dir: Vec::new(),
            ride_pos: Vec::new(),
            stop_cost: Vec::new(),
            board_rate: Vec::new(),
            alight_rate: Vec::new(),
            visit_rate: Vec::new(),
            sr_head: Vec::new(),
            sr_list: Vec::new(),
            ride_next: Vec::new(),
            ride_perc: Vec::new(),
            ride_real: Vec::new(),
            ride_dl: Vec::new(),
            wait: Vec::new(),
            dist: Vec::new(),
            done: Vec::new(),
            prev: Vec::new(),
            ride_best: Vec::new(),
            heap: Vec::new(),
            dflow: Vec::new(),
            util: Vec::new(),
            boards: Vec::new(),
            onboard: Vec::new(),
            stuck: Vec::new(),
            bl_drain: Vec::new(),
            bl_stuck: Vec::new(),
            travel: Vec::new(),
            unreach: Vec::new(),
            served: vec![false; n],
            need: vec![false; ctx.shapes()],
            found: vec![u32::MAX; ctx.shapes()],
            scen_w: Vec::new(),
            match_buf: Vec::new(),
            trace: None,
            explain: None,
        }
    }

    pub fn evaluate(&mut self, ctx: &Ctx, g: &mut Genome) -> f64 {
        let mut bd = Breakdown::default();
        let n = ctx.n();
        let cfg = &ctx.cfg;
        let ns = ctx.scenarios.len();

        // ---- partes que so dependem do genoma real ----
        let mut track = 0f32;
        let mut water_used = 0u32;
        for s in self.served.iter_mut() {
            *s = false;
        }
        for r in &g.routes {
            if r.locos == 0 || r.stops.len() < 2 {
                continue;
            }
            let m = r.stops.len();
            let links = if r.looped && m >= 3 { m } else { m - 1 };
            for k in 0..links {
                let a = r.stops[k] as usize;
                let b = r.stops[(k + 1) % m] as usize;
                track += ctx.d.d(a, b);
                if ctx.d.w(a, b) {
                    water_used += 1;
                }
            }
            for &s in &r.stops {
                self.served[s as usize] = true;
            }
        }
        let unserved = (0..ctx.na()).filter(|&i| !self.served[i]).count() as u32;
        let over = water_used.saturating_sub(ctx.p.budget.crossings as u32);

        bd.track_length = track;
        bd.crossings_used = water_used;
        bd.unserved_stations = unserved;
        bd.track = (track * cfg.track_length_weight) as f64;
        bd.crossing = over as f64 * CROSSING_INFEASIBLE;
        bd.unserved = unserved as f64 * cfg.unserved_penalty as f64;
        let cc = change::change_cost(ctx, &g.routes, &mut self.match_buf);
        let horizon_s = (cfg.horizon_days * ctx.p.params.day_length).max(1.0) as f64;
        bd.change = cc.pax_s / horizon_s * cfg.change_weight as f64;
        bd.rebuilt_lines = cc.rebuilt;
        bd.moved_trains = cc.moved_trains;

        // ---- por epoca ----
        self.travel.clear();
        self.travel.resize(ns, 0.0);
        self.unreach.clear();
        self.unreach.resize(ns, 0.0);
        let mut demand_total = 0f64;
        let mut time_total = 0f64;
        let mut worst_util = 0f32;
        let mut congestion = 0f64;
        let mut station_load = 0f64;
        let mut unreachable_pairs = 0u32;
        let explain_epoch = ctx.scenarios[0].epoch;

        for e in 0..ctx.epochs.len() {
            let scen: Vec<usize> = (0..ns).filter(|&s| ctx.scenarios[s].epoch == e).collect();
            if scen.is_empty() {
                continue;
            }
            self.build_lines(ctx, g, e);
            self.count_line_crossings(ctx);
            if e == 0 {
                bd.line_crossings = self.link_x.iter().map(|&x| x as u32).sum::<u32>() / 2;
            }

            // pesos dos cenarios dentro da epoca, para dwell (media) e lotacao (pior)
            let wsum: f32 = scen.iter().map(|&s| ctx.scenarios[s].weight).sum::<f32>().max(1e-6);
            self.scen_w.clear();
            self.scen_w.extend(scen.iter().map(|&s| ctx.scenarios[s].weight / wsum));

            let passes = if cfg.capacity_feedback { 2 } else { 1 };
            let nl = self.lines.len();
            let ndl = self.link_a.len() * 2;
            let rides = self.ride_station.len();
            self.dflow.clear();
            self.dflow.resize(ns * ndl.max(1), 0.0);
            self.boards.clear();
            self.boards.resize(ns * rides.max(1), 0.0);
            self.onboard.clear();
            self.onboard.resize(ns * nl.max(1), 0.0);
            self.stuck.clear();
            self.stuck.resize(ns * n, 0.0);
            self.bl_drain.clear();
            self.bl_drain.resize(n, 0.0);
            self.bl_stuck.clear();
            self.bl_stuck.resize(n, 0.0);

            let mut last = (0f64, 0f64, 0u32);
            for pass in 0..passes {
                self.update_timing(ctx, pass == 0);
                self.prepare_rides(ctx);
                for v in self.dflow.iter_mut() {
                    *v = 0.0;
                }
                for v in self.boards.iter_mut() {
                    *v = 0.0;
                }
                for v in self.onboard.iter_mut() {
                    *v = 0.0;
                }
                for v in self.stuck.iter_mut() {
                    *v = 0.0;
                }
                for v in self.bl_drain.iter_mut() {
                    *v = 0.0;
                }
                for v in self.bl_stuck.iter_mut() {
                    *v = 0.0;
                }
                for &s in &scen {
                    self.travel[s] = 0.0;
                    self.unreach[s] = 0.0;
                }
                for v in self.board_rate.iter_mut() {
                    *v = 0.0;
                }
                for v in self.alight_rate.iter_mut() {
                    *v = 0.0;
                }
                last = self.route_all(ctx, e, &scen);
                self.compute_utils(ns, &scen);
                if pass + 1 < passes {
                    self.update_crowding(ctx, &scen);
                }
            }
            demand_total += last.0;
            time_total += last.1;
            unreachable_pairs += last.2;

            // penalidades por cenario desta epoca
            for &s in &scen {
                let w = ctx.scenarios[s].weight as f64;
                let mut c = 0f64;
                for l in 0..nl {
                    for d in 0..2 {
                        let u = self.util[(s * nl + l) * 2 + d];
                        if u > worst_util {
                            worst_util = u;
                        }
                        if u > 1.0 {
                            c += ((u - 1.0) * (u - 1.0)) as f64;
                        }
                    }
                }
                congestion += w * c;
                let mut sl = 0f64;
                for x in 0..n {
                    if !ctx.epochs[e].present[x] {
                        continue;
                    }
                    sl += self.station_risk(ctx, s, x).pen;
                }
                station_load += w * sl;
                bd.travel += w * self.travel[s];
                bd.unreachable += w * self.unreach[s] * cfg.unreachable_penalty as f64;
            }

            if e == explain_epoch {
                bd.urgency = self.urgency(ctx);
                if self.explain.is_some() {
                    let ex = self.build_explain(ctx, e);
                    self.explain = Some(ex);
                }
            }
        }

        bd.congestion = congestion * cfg.congestion_weight as f64;
        bd.station_load = station_load * cfg.station_load_weight as f64;
        bd.unreachable_pairs = unreachable_pairs;
        bd.worst_line_util = worst_util;
        bd.avg_travel_time = if demand_total > 0.0 { (time_total / demand_total) as f32 } else { 0.0 };

        let total = bd.total();
        g.fitness = if total.is_finite() { total } else { f64::MAX / 4.0 };
        g.bd = bd;
        g.fitness
    }

    // ------------------------------------------------------------------
    // linhas da epoca (genoma + encaixe virtual das futuras)
    // ------------------------------------------------------------------
    fn build_lines(&mut self, ctx: &Ctx, g: &Genome, e: usize) {
        let ep = &ctx.epochs[e];
        let n = ctx.n();
        let cap = ctx.p.params.railcar_capacity.max(1) as f32;
        let mut used = 0usize;
        for (ri, r) in g.routes.iter().enumerate() {
            if r.locos == 0 || r.stops.len() < 2 {
                continue;
            }
            if self.lines.len() <= used {
                self.lines.push(LineBuf::default());
            }
            let lb = &mut self.lines[used];
            lb.stops.clear();
            lb.stops.extend_from_slice(&r.stops);
            lb.looped = r.looped && r.stops.len() >= 3;
            lb.trains = r.locos as f32;
            lb.seats = (r.locos + r.cars) as f32 * cap;
            lb.n_dir = if lb.looped {
                let rev = r.rev.min(r.locos);
                [(r.locos - rev) as f32, rev as f32]
            } else {
                [lb.trains, lb.trains]
            };
            lb.crowd = [1.0, 1.0];
            lb.route = ri;
            used += 1;
        }
        self.lines.truncate(used);

        // estacoes futuras: insercao mais barata (o jogador liga quando nascem;
        // se nascem em cima do trilho o proprio jogo insere)
        for &f in &ep.future {
            let f = f as usize;
            let mut best = (f32::MAX, usize::MAX, 0usize);
            for (li, lb) in self.lines.iter().enumerate() {
                let m = lb.stops.len();
                if lb.stops.iter().any(|&s| s as usize == f) {
                    best = (0.0, usize::MAX, 0);
                    break;
                }
                let links = if lb.looped { m } else { m - 1 };
                for k in 0..links {
                    let a = lb.stops[k] as usize;
                    let b = lb.stops[(k + 1) % m] as usize;
                    let delta = ctx.d.d(a, f) + ctx.d.d(f, b) - ctx.d.d(a, b) + water_extra(ctx, a, f, b);
                    if delta < best.0 {
                        best = (delta, li, k + 1);
                    }
                }
                if !lb.looped {
                    let a = lb.stops[0] as usize;
                    let z = lb.stops[m - 1] as usize;
                    let da = ctx.d.d(a, f) + if ctx.d.w(a, f) { 400.0 } else { 0.0 };
                    let dz = ctx.d.d(z, f) + if ctx.d.w(z, f) { 400.0 } else { 0.0 };
                    if da < best.0 {
                        best = (da, li, 0);
                    }
                    if dz < best.0 {
                        best = (dz, li, m);
                    }
                }
            }
            if best.1 != usize::MAX {
                self.lines[best.1].stops.insert(best.2, f as u16);
            }
        }

        // nos "dentro do trem" (2 por parada: um por sentido) e links
        let mut ride = 0usize;
        self.ride_station.clear();
        self.ride_line.clear();
        self.ride_dir.clear();
        self.ride_pos.clear();
        self.link_a.clear();
        self.link_b.clear();
        self.link_len.clear();
        for (li, lb) in self.lines.iter_mut().enumerate() {
            lb.ride0 = ride;
            lb.link0 = self.link_a.len();
            let m = lb.stops.len();
            for d in 0..2u8 {
                for (j, &s) in lb.stops.iter().enumerate() {
                    self.ride_station.push(s);
                    self.ride_line.push(li as u16);
                    self.ride_dir.push(d);
                    self.ride_pos.push(j as u16);
                }
            }
            ride += 2 * m;
            let links = if lb.looped { m } else { m - 1 };
            for k in 0..links {
                let a = lb.stops[k];
                let b = lb.stops[(k + 1) % m];
                self.link_a.push(a);
                self.link_b.push(b);
                self.link_len.push(ctx.d.d(a as usize, b as usize));
            }
        }
        let rides = self.ride_station.len();
        self.stop_cost.clear();
        self.stop_cost.resize(rides, 0.0);
        self.board_rate.clear();
        self.board_rate.resize(rides, 0.0);
        self.alight_rate.clear();
        self.alight_rate.resize(rides, 0.0);
        self.visit_rate.clear();
        self.visit_rate.resize(rides, 0.0);
        self.link_run.clear();
        self.link_run.resize(self.link_a.len(), 0.0);

        // nos de embarque por estacao
        self.sr_head.clear();
        self.sr_head.resize(n + 1, 0);
        for &s in &self.ride_station {
            self.sr_head[s as usize + 1] += 1;
        }
        for i in 0..n {
            self.sr_head[i + 1] += self.sr_head[i];
        }
        self.sr_list.clear();
        self.sr_list.resize(rides, 0);
        let mut fill: Vec<u32> = self.sr_head[..n].to_vec();
        for (r, &s) in self.ride_station.iter().enumerate() {
            let k = fill[s as usize] as usize;
            fill[s as usize] += 1;
            self.sr_list[k] = r as u32;
        }
    }

    /// Cruzamentos de trilho: cordas que se cruzam sem dividir estacao. O jogo
    /// escolhe a forma octilinear que cruza menos, entao a corda reta e uma boa
    /// medida do cruzamento inevitavel.
    fn count_line_crossings(&mut self, ctx: &Ctx) {
        let e = self.link_a.len();
        self.link_x.clear();
        self.link_x.resize(e, 0);
        let pos = |s: u16| ctx.p.stations[s as usize].pos;
        for i in 0..e {
            let (a1, b1) = (self.link_a[i], self.link_b[i]);
            let s1 = Seg::new(pos(a1), pos(b1));
            for j in (i + 1)..e {
                let (a2, b2) = (self.link_a[j], self.link_b[j]);
                if a1 == a2 || a1 == b2 || b1 == a2 || b1 == b2 {
                    continue;
                }
                if segs_cross(&s1, &Seg::new(pos(a2), pos(b2))) {
                    self.link_x[i] += 1;
                    self.link_x[j] += 1;
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // ciclo, headway, custo de parada
    // ------------------------------------------------------------------
    fn update_timing(&mut self, ctx: &Ctx, first: bool) {
        let pr = &ctx.p.params;
        let v = ctx.speed;
        let pulse = pr.pulse.max(0.05);
        let xdelay = ctx.d.crossing_delay;
        let sloss = ctx.d.stop_loss;

        // passageiros por parada: quem desce ao chegar em (d, j) + quem sobe na
        // parada que atende o embarque em (d, j)
        if !first {
            for v in self.visit_rate.iter_mut() {
                *v = 0.0;
            }
            for li in 0..self.lines.len() {
                let lb = &self.lines[li];
                for d in 0..2 {
                    for j in 0..lb.m() {
                        let r = lb.node(d, j);
                        self.visit_rate[r] += self.alight_rate[r];
                        self.visit_rate[lb.board_visit(d, j)] += self.board_rate[r];
                    }
                }
            }
        }

        for li in 0..self.lines.len() {
            let (m, looped, link0, trains) = {
                let lb = &self.lines[li];
                (lb.m(), lb.looped, lb.link0, lb.trains.max(1.0))
            };
            let links = if looped { m } else { m - 1 };
            let mut run = 0f32;
            for k in 0..links {
                let t = self.link_len[link0 + k] / v + self.link_x[link0 + k] as f32 * xdelay;
                self.link_run[link0 + k] = t;
                run += t;
            }
            let mut stops = [0f32; 2];
            for d in 0..2 {
                let lb = &self.lines[li];
                if !lb.has_dir(d) {
                    continue;
                }
                // headway provisorio (passe 1) ou do passe anterior
                let h = if first {
                    let rough = if looped { run } else { 2.0 * run } + m as f32 * (sloss + pulse);
                    rough / if looped { lb.n_dir[d] } else { trains }
                } else {
                    lb.headway[d].max(0.1)
                };
                let quick_city = pr.quick_embark;
                for j in 0..m {
                    if !lb.has_prev(d, j) {
                        continue;
                    }
                    let r = lb.node(d, j);
                    let s = lb.stops[j] as usize;
                    let quick = quick_city || ctx.p.stations[s].interchange;
                    let terminal = lb.terminal_visit(d, j);
                    let per_visit = if first { 1.0 } else { self.visit_rate[r] * h };
                    let p_stop = if terminal { 1.0 } else { 1.0 - (-per_visit).exp() };
                    // parada: frenagem+aceleracao, espera o proximo pulso (meio,
                    // em media) e 1 pulso por passageiro; em interchange todo
                    // mundo desce e sobe no mesmo pulso
                    let dwell = if quick {
                        p_stop * pulse * 1.5
                    } else {
                        p_stop * pulse * 0.5 + pulse * per_visit
                    };
                    let c = p_stop * sloss + dwell;
                    self.stop_cost[r] = c;
                    stops[d] += c;
                }
            }
            let lb = &mut self.lines[li];
            lb.run = run;
            if looped {
                for d in 0..2 {
                    if lb.has_dir(d) {
                        lb.cycle[d] = run + stops[d];
                        lb.headway[d] = lb.cycle[d] / lb.n_dir[d];
                    } else {
                        lb.cycle[d] = 0.0;
                        lb.headway[d] = f32::INFINITY;
                    }
                }
            } else {
                let c = 2.0 * run + stops[0] + stops[1];
                lb.cycle = [c, c];
                lb.headway = [c / trains, c / trains];
            }
        }
    }

    /// Utilizacao do pior trecho do sentido d da linha l no cenario s:
    /// passageiros por trem = fluxo x headway; lugares por trem = lugares/T.
    fn max_util(&self, l: usize, d: usize, s: usize) -> f32 {
        let lb = &self.lines[l];
        if !lb.has_dir(d) {
            return 0.0;
        }
        let ndl = self.link_a.len() * 2;
        let links = if lb.looped { lb.m() } else { lb.m() - 1 };
        let mut worst = 0f64;
        for k in 0..links {
            let dl = (lb.link0 + k) * 2 + d;
            worst = worst.max(self.dflow[s * ndl + dl]);
        }
        let per_train = (lb.seats / lb.trains.max(1.0)).max(1.0);
        (worst * lb.headway[d] as f64 / per_train as f64) as f32
    }

    fn compute_utils(&mut self, ns: usize, scen: &[usize]) {
        let nl = self.lines.len();
        self.util.clear();
        self.util.resize(ns * nl * 2, 0.0);
        for &s in scen {
            for l in 0..nl {
                for d in 0..2 {
                    self.util[(s * nl + l) * 2 + d] = self.max_util(l, d, s);
                }
            }
        }
    }

    /// Trem cheio: quem esta na plataforma perde o trem e o A* do jogo passa a
    /// contar o ciclo seguinte. Entra como espera percebida maior, e o
    /// roteamento do segundo passe desvia de linha saturada quando da.
    fn update_crowding(&mut self, ctx: &Ctx, scen: &[usize]) {
        let max_c = ctx.cfg.max_crowding;
        let nl = self.lines.len();
        for l in 0..nl {
            for d in 0..2 {
                let mut worst = 1f32;
                for &s in scen {
                    worst = worst.max(self.util[(s * nl + l) * 2 + d]);
                }
                self.lines[l].crowd[d] = worst.min(max_c);
            }
        }
    }

    /// Fila da plataforma x no cenario s, como o jogo a produz:
    ///  - regime: em cada sentido que embarca aqui chegam lambda passageiros/s
    ///    e o trem leva a leva a cada headway: A = lambda x headway. A idade da
    ///    leva e uniforme (media A/2, variancia A^2/12) e a contagem e de
    ///    Poisson (+A/2);
    ///  - crescimento: quem nao tem rota fica ali (no classico ninguem
    ///    desiste), e com o trem cheio (utilizacao u > 1 no pior trecho do
    ///    sentido) sobra lambda x (1 - 1/u) na plataforma a cada segundo.
    /// Devolve (media e variancia da MEDIA de Poisson da fila, crescimento por
    /// falta de lugar, crescimento por falta de rota), crescimentos em
    /// passageiros/s. A fila e Poisson com essa media variando: a idade de cada
    /// leva e uniforme entre dois trens.
    fn queue_moments(&self, ctx: &Ctx, s: usize, x: usize) -> (f64, f64, f64, f64) {
        let rides = self.ride_station.len();
        let mut mu = 0f64;
        let mut var = 0f64;
        let mut growth = 0f64;
        let (a, b) = (self.sr_head[x] as usize, self.sr_head[x + 1] as usize);
        for &r in &self.sr_list[a..b] {
            let r = r as usize;
            let lam = self.boards[s * rides + r];
            if lam <= 0.0 {
                continue;
            }
            let l = self.ride_line[r] as usize;
            let lb = &self.lines[l];
            let d = self.ride_dir[r] as usize;
            let big_a = lam * lb.headway[d] as f64;
            mu += 0.5 * big_a;
            var += big_a * big_a / 12.0;
            // Sobra na plataforma quem nao cabe no trem que SAI daqui: o que
            // passa do limite no trecho seguinte, ate o tanto que embarca aqui
            // (quem ja estava a bordo tem o lugar garantido).
            let over = self.out_flow(s, r) - self.seat_rate(l, d);
            if over > 0.0 {
                growth += over.min(lam);
            }
        }
        (mu, var, growth, self.stuck[s * ctx.n() + x])
    }

    /// Lugares livres por segundo nos trens que embarcam em x: por sentido, os
    /// lugares que passam menos o fluxo no trecho que sai de x.
    fn spare(&self, s: usize, x: usize) -> f64 {
        let mut total = 0f64;
        let (a, b) = (self.sr_head[x] as usize, self.sr_head[x + 1] as usize);
        for &r in &self.sr_list[a..b] {
            let r = r as usize;
            let l = self.ride_line[r] as usize;
            let lb = &self.lines[l];
            let d = self.ride_dir[r] as usize;
            if !lb.has_dir(d) || !lb.has_next(d, self.ride_pos[r] as usize) {
                continue;
            }
            // lugares que sobram no trecho que sai daqui (nao no pior trecho
            // da linha: depois dele o trem ja esvaziou)
            total += (self.seat_rate(l, d) - self.out_flow(s, r)).max(0.0);
        }
        total
    }

    /// Lugares por segundo que passam num sentido da linha: lugares por trem /
    /// headway.
    #[inline]
    fn seat_rate(&self, l: usize, d: usize) -> f64 {
        let lb = &self.lines[l];
        if !lb.has_dir(d) {
            return 0.0;
        }
        (lb.seats / lb.trains.max(1.0)) as f64 / lb.headway[d] as f64
    }

    /// Passageiros/s no trecho que sai do no de trem r (no cenario s).
    #[inline]
    fn out_flow(&self, s: usize, r: usize) -> f64 {
        if self.ride_next[r] == u32::MAX {
            return 0.0;
        }
        let ndl = self.link_a.len() * 2;
        self.dflow[s * ndl + self.ride_dl[r] as usize]
    }

    /// Excesso esperado acima da capacidade, media no horizonte (3 pontos: 1/6,
    /// 1/2 e 5/6 do horizonte, com a fila crescendo linearmente). Acima da
    /// capacidade e o que dispara o timer de 45 + 2 s do jogo.
    fn station_risk(&self, ctx: &Ctx, s: usize, x: usize) -> Risk {
        let (m0, v0, g_cap, g_route) = self.queue_moments(ctx, s, x);
        let g = g_cap + g_route;
        // fila atual: a que tem rota escoa pela folga dos trens que embarcam
        // aqui (so no cenario de hoje); a sem rota fica para sempre
        let stay = self.bl_stuck[x];
        let (b0, spare) = if s == 0 && self.bl_drain[x] > 0.0 { (self.bl_drain[x], self.spare(s, x)) } else { (0.0, 0.0) };
        if m0 <= 0.0 && g <= 0.0 && stay <= 0.0 && b0 <= 0.0 {
            return Risk::default();
        }
        let cap = ctx.p.stations[x].capacity.max(1) as f64;
        let mut out = Risk::default();
        let far = |mean: f64, var: f64| mean + 6.0 * var.sqrt() + 1.0 < cap;
        if g <= 0.0 && b0 <= 0.0 {
            // regime estacionario: a fila nao depende do tempo
            if far(m0 + stay, v0 + m0) {
                return out;
            }
            let (ex, p) = queue_tail(stay, m0, v0, cap);
            let rho = ex / cap;
            out.pen = rho + rho * rho;
            out.excess = ex;
            out.p_over = p;
            return out;
        }
        let h_cap = persist_horizon(ctx);
        let h_route = 4.0 * h_cap;
        {
            // longe da capacidade ate no fim da janela: nada a calcular
            let grown = g_cap * 0.9 * h_cap + g_route * 0.9 * h_route;
            if far(m0 + grown + stay + b0, v0 + m0 + grown) {
                return out;
            }
        }
        // Quem fica na plataforma so sai quando a rede muda. Faltar lugar so
        // muda com o upgrade da semana (janela persist_days). Faltar rota o AG
        // poderia corrigir, mas escolheria a mesma rede de novo se nao visse o
        // acumulo: esse fica pelo resto da partida (4x a janela). Media em 5
        // pontos de cada janela.
        for f in [0.1, 0.3, 0.5, 0.7, 0.9] {
            let grown = g_cap * f * h_cap + g_route * f * h_route;
            let fixed = stay + (b0 - spare * f * h_cap).max(0.0);
            let (ex, p) = queue_tail(fixed, m0 + grown, v0, cap);
            let rho = ex / cap;
            out.pen += (rho + rho * rho) / 5.0;
            out.excess += ex / 5.0;
            out.p_over += p / 5.0;
        }
        out
    }

    // ------------------------------------------------------------------
    // trechos (o "grafo": cada linha em cada sentido e uma cadeia)
    // ------------------------------------------------------------------
    fn prepare_rides(&mut self, ctx: &Ctx) {
        let n = ctx.n();
        let rides = self.ride_station.len();
        let v = ctx.speed;
        let pstop = ctx.cfg.path_stop_cost;
        self.ride_next.clear();
        self.ride_next.resize(rides, u32::MAX);
        self.ride_perc.clear();
        self.ride_perc.resize(rides, 0.0);
        self.ride_real.clear();
        self.ride_real.resize(rides, 0.0);
        self.ride_dl.clear();
        self.ride_dl.resize(rides, 0);
        self.wait.clear();
        self.wait.resize(self.lines.len() * 2, f32::INFINITY);
        for li in 0..self.lines.len() {
            let lb = &self.lines[li];
            for d in 0..2 {
                if !lb.has_dir(d) {
                    continue;
                }
                self.wait[li * 2 + d] = 0.5 * lb.headway[d] * lb.crowd[d];
                for j in 0..lb.m() {
                    if !lb.has_next(d, j) {
                        continue;
                    }
                    let r = lb.node(d, j);
                    let r2 = lb.node(d, lb.next(d, j));
                    let k = lb.link_from(d, j);
                    self.ride_next[r] = r2 as u32;
                    // o A* do jogo estima trecho/velocidade + 3,5 por parada;
                    // o tempo real tem os cruzamentos e a parada seguinte de verdade
                    self.ride_perc[r] = self.link_len[k] / v + pstop;
                    self.ride_real[r] = self.link_run[k] + self.stop_cost[r2];
                    self.ride_dl[r] = (k * 2 + d) as u32;
                }
            }
        }
        self.dist.resize(n, 0.0);
        self.done.resize(n, false);
        self.prev.resize(n, (u32::MAX, u32::MAX, u32::MAX));
        self.ride_best.clear();
        self.ride_best.resize(rides, f32::MAX);
    }

    // ------------------------------------------------------------------
    // roteamento
    // ------------------------------------------------------------------
    /// Devolve (demanda roteada ponderada, tempo*demanda ponderado, pares sem rota).
    fn route_all(&mut self, ctx: &Ctx, e: usize, scen: &[usize]) -> (f64, f64, u32) {
        let n = ctx.n();
        let s_count = ctx.shapes();
        let ep = &ctx.epochs[e];
        let nl = self.lines.len();
        let ndl = self.link_a.len() * 2;
        let rides = self.ride_station.len();
        let mut demand_total = 0f64;
        let mut time_total = 0f64;
        let mut unreachable_pairs = 0u32;
        let sw = scen_weight_sum(ctx, scen);
        let today = e == ctx.scenarios[0].epoch;

        for u in 0..n {
            if !ep.present[u] {
                continue;
            }
            // formas pedidas por esta origem em algum cenario da epoca (e as da
            // fila atual, na epoca de hoje)
            let mut any = false;
            for t in 0..s_count {
                let mut need = false;
                for &s in scen {
                    if ctx.scenarios[s].rate[u * s_count + t] > 0.0 {
                        need = true;
                        break;
                    }
                }
                self.need[t] = need;
                self.found[t] = u32::MAX;
                any |= need;
            }
            let has_backlog = today && u < ctx.na() && !ctx.backlog[u].is_empty();
            if has_backlog {
                for &(t, _) in &ctx.backlog[u] {
                    any |= !self.need[t as usize];
                    self.need[t as usize] = true;
                }
            }
            if !any {
                continue;
            }

            if self.sr_head[u + 1] > self.sr_head[u] {
                self.dijkstra(u, ctx, ep);
            }
            if has_backlog {
                for &(t, c) in &ctx.backlog[u] {
                    if self.found[t as usize] == u32::MAX {
                        self.bl_stuck[u] += c as f64;
                    } else {
                        self.bl_drain[u] += c as f64;
                    }
                }
            }

            for t in 0..s_count {
                if !self.need[t] || scen.iter().all(|&s| ctx.scenarios[s].rate[u * s_count + t] <= 0.0) {
                    continue;
                }
                let dst = self.found[t];
                let wsum: f64 = scen
                    .iter()
                    .zip(self.scen_w.iter())
                    .map(|(&s, &w)| ctx.scenarios[s].rate[u * s_count + t] as f64 * w as f64)
                    .sum();
                if dst == u32::MAX {
                    // Sem rota o passageiro nao sai nunca da plataforma (no classico
                    // ninguem desiste): ele se acumula ali por todo o horizonte e
                    // ocupa capacidade. E isso que mata rede partida em pedacos.
                    for &s in scen {
                        let r = ctx.scenarios[s].rate[u * s_count + t] as f64;
                        self.unreach[s] += r;
                        self.stuck[s * n + u] += r;
                    }
                    unreachable_pairs += 1;
                    if let Some(tr) = self.trace.as_mut() {
                        tr.push((e as u32, u as u32, t as u32));
                    }
                    continue;
                }
                // anda o caminho de tras pra frente (trecho a trecho) somando o
                // tempo real e acumulando os fluxos
                let mut real_total = 0f32;
                let mut cur = dst as usize;
                let mut guard = 0;
                while cur != u && guard < 4096 {
                    guard += 1;
                    let (rb, ra, from) = self.prev[cur];
                    if rb == u32::MAX {
                        break;
                    }
                    let (rb, ra) = (rb as usize, ra as usize);
                    let l = self.ride_line[rb] as usize;
                    let d = self.ride_dir[rb] as usize;
                    let w = self.wait[l * 2 + d];
                    real_total += w;
                    self.board_rate[rb] += wsum as f32;
                    for &s in scen {
                        let r = ctx.scenarios[s].rate[u * s_count + t] as f64;
                        self.boards[s * rides + rb] += r;
                    }
                    let mut r = rb;
                    while r != ra {
                        let real = self.ride_real[r];
                        real_total += real;
                        let dl = self.ride_dl[r] as usize;
                        for &s in scen {
                            let q = ctx.scenarios[s].rate[u * s_count + t] as f64;
                            self.dflow[s * ndl + dl] += q;
                            self.onboard[s * nl + l] += q * real as f64;
                        }
                        r = self.ride_next[r] as usize;
                    }
                    self.alight_rate[ra] += wsum as f32;
                    cur = from as usize;
                }
                for &s in scen {
                    let r = ctx.scenarios[s].rate[u * s_count + t] as f64;
                    self.travel[s] += r * real_total as f64;
                }
                demand_total += wsum * sw;
                time_total += wsum * real_total as f64 * sw;
            }
        }
        (demand_total, time_total, unreachable_pairs)
    }

    /// Dijkstra sobre as estacoes. Ao fechar uma estacao, percorre direto cada
    /// linha/sentido que embarca nela e relaxa todas as estacoes seguintes com
    /// espera + trechos + baldeacao (o custo que o A* do jogo percebe). E o
    /// mesmo caminho do grafo (estacao, trem), sem por os nos de trem no heap.
    fn dijkstra(&mut self, src: usize, ctx: &Ctx, ep: &crate::ctx::Epoch) {
        let n = ctx.n();
        let tcost = ctx.cfg.transfer_cost;
        for i in 0..n {
            self.dist[i] = f32::MAX;
            self.done[i] = false;
            self.prev[i] = (u32::MAX, u32::MAX, u32::MAX);
        }
        for v in self.ride_best.iter_mut() {
            *v = f32::MAX;
        }
        let mut remaining = self.need.iter().filter(|&&b| b).count();
        self.heap.clear();
        self.dist[src] = 0.0;
        heap_push(&mut self.heap, 0.0, src as u32);
        while let Some((dk, node)) = heap_pop(&mut self.heap) {
            let x = node as usize;
            if self.done[x] {
                continue;
            }
            let dx = f32::from_bits(dk);
            if dx > self.dist[x] {
                continue;
            }
            self.done[x] = true;
            if x != src {
                let t = ep.slot[x] as usize;
                if self.need[t] && self.found[t] == u32::MAX {
                    self.found[t] = x as u32;
                    remaining -= 1;
                    if remaining == 0 {
                        break;
                    }
                }
            }
            let (a, b) = (self.sr_head[x] as usize, self.sr_head[x + 1] as usize);
            for k in a..b {
                let rb = self.sr_list[k] as usize;
                if self.ride_next[rb] == u32::MAX {
                    continue; // ponta de linha nesse sentido, ou sentido sem trem
                }
                let l = self.ride_line[rb] as usize;
                let d = self.ride_dir[rb] as usize;
                let mut acc = dx + self.wait[l * 2 + d];
                let mut r = rb;
                loop {
                    // alguem ja passou por este no do trem mais barato: tudo que
                    // vem depois na cadeia tambem sai mais barato por la
                    if acc >= self.ride_best[r] {
                        break;
                    }
                    self.ride_best[r] = acc;
                    acc += self.ride_perc[r];
                    let nx = self.ride_next[r];
                    if nx == u32::MAX {
                        break;
                    }
                    r = nx as usize;
                    let st = self.ride_station[r] as usize;
                    if st == x {
                        break; // loop deu a volta
                    }
                    let cand = acc + tcost;
                    if !self.done[st] && cand < self.dist[st] {
                        self.dist[st] = cand;
                        self.prev[st] = (rb as u32, r as u32, x as u32);
                        heap_push(&mut self.heap, cand, st as u32);
                    }
                }
            }
        }
    }

    /// Estacao com o timer de lotacao correndo (ou fila acima da capacidade agora)
    /// precisa de trem logo: pune a espera ate o proximo trem nela.
    fn urgency(&self, ctx: &Ctx) -> f64 {
        let w = ctx.cfg.urgency_weight as f64;
        if w <= 0.0 {
            return 0.0;
        }
        let mut total = 0f64;
        for x in 0..ctx.na() {
            let st = &ctx.p.stations[x];
            let queue: u32 = st.waiting.iter().map(|&(_, c)| c as u32).sum();
            let cap = st.capacity.max(1) as f32;
            let pressure = st.timer.clamp(0.0, 1.0) + ((queue as f32 - cap) / cap).max(0.0);
            if pressure <= 0.0 {
                continue;
            }
            let mut best = UNSERVED_WAIT;
            let (a, b) = (self.sr_head[x] as usize, self.sr_head[x + 1] as usize);
            for &r in &self.sr_list[a..b] {
                let r = r as usize;
                let lb = &self.lines[self.ride_line[r] as usize];
                let d = self.ride_dir[r] as usize;
                if lb.has_dir(d) && lb.has_next(d, self.ride_pos[r] as usize) {
                    best = best.min(0.5 * lb.headway[d]);
                }
            }
            total += w * pressure as f64 * best as f64;
        }
        total
    }

    /// Fila esperada em x no instante t (s a partir de agora), no cenario s.
    /// Fila em x no instante t: (parte fixa, media e variancia da media de
    /// Poisson do resto).
    fn queue_at(&self, ctx: &Ctx, s: usize, x: usize, t: f64) -> (f64, f64, f64) {
        let (m0, v0, g_cap, g_route) = self.queue_moments(ctx, s, x);
        let g = g_cap + g_route;
        let stay = self.bl_stuck[x];
        let drain = if self.bl_drain[x] > 0.0 { (self.bl_drain[x] - self.spare(s, x) * t).max(0.0) } else { 0.0 };
        (stay + drain, m0 + g * t, v0)
    }

    /// Detalhe do modelo na janela do horizonte (media ponderada dos cenarios
    /// da epoca de hoje), para comparar com o que o jogo mede.
    fn build_explain(&self, ctx: &Ctx, e: usize) -> Explain {
        let n = ctx.n();
        let s_count = ctx.shapes();
        let nl = self.lines.len();
        let scen: Vec<usize> = (0..ctx.scenarios.len()).filter(|&s| ctx.scenarios[s].epoch == e).collect();
        let wsum: f64 = scen.iter().map(|&s| ctx.scenarios[s].weight as f64).sum::<f64>().max(1e-9);
        let wt = |s: usize| ctx.scenarios[s].weight as f64 / wsum;
        // cenario em vigor numa fracao f da janela (cenarios estao em ordem)
        let scen_at = |f: f64| -> usize {
            let mut acc = 0f64;
            for (k, sc) in ctx.scenarios.iter().enumerate() {
                acc += sc.weight as f64;
                if f <= acc + 1e-9 {
                    return if sc.epoch == e { k } else { scen[0] };
                }
            }
            scen[0]
        };
        let window = (ctx.cfg.horizon_days * ctx.p.params.day_length) as f64;

        let mut ex = Explain::default();
        for l in 0..nl {
            let lb = &self.lines[l];
            let mut stops_made = 0f32;
            for d in 0..2 {
                if !lb.has_dir(d) {
                    continue;
                }
                for j in 0..lb.m() {
                    if !lb.has_prev(d, j) {
                        continue;
                    }
                    let r = lb.node(d, j);
                    let terminal = lb.terminal_visit(d, j);
                    let pv = self.visit_rate[r] * lb.headway[d];
                    stops_made += if terminal { 1.0 } else { 1.0 - (-pv).exp() };
                }
            }
            let mut util = [0f64; 2];
            let mut onb = 0f64;
            for &s in &scen {
                for (d, u) in util.iter_mut().enumerate() {
                    *u += wt(s) * self.util[(s * nl + l) * 2 + d] as f64;
                }
                onb += wt(s) * self.onboard[s * nl + l];
            }
            ex.lines.push(LineExplain {
                route: lb.route,
                stops: lb.m(),
                looped: lb.looped,
                trains: lb.trains,
                seats: lb.seats,
                n_dir: lb.n_dir,
                run: lb.run,
                cycle: lb.cycle,
                headway: lb.headway,
                max_util: [util[0] as f32, util[1] as f32],
                mean_onboard: (onb / lb.trains.max(1.0) as f64) as f32,
                stops_made,
                length: {
                    let links = if lb.looped { lb.m() } else { lb.m() - 1 };
                    (0..links).map(|k| self.link_len[lb.link0 + k]).sum()
                },
                crossings: {
                    let links = if lb.looped { lb.m() } else { lb.m() - 1 };
                    (0..links).map(|k| self.link_x[lb.link0 + k] as u32).sum()
                },
            });
        }

        let rides = self.ride_station.len();
        let mut demand = 0f64;
        let mut left = 0f64;
        let mut travel = 0f64;
        for x in 0..n {
            if !ctx.epochs[e].present[x] {
                continue;
            }
            let mut st = StationExplain::default();
            for &s in &scen {
                let w = wt(s);
                let (mu0, v0, g_cap, g_route) = self.queue_moments(ctx, s, x);
                let var0 = v0 + mu0;
                let row: f64 = ctx.scenarios[s].rate[x * s_count..(x + 1) * s_count].iter().map(|&r| r as f64).sum();
                let (a, b) = (self.sr_head[x] as usize, self.sr_head[x + 1] as usize);
                let board: f64 = self.sr_list[a..b].iter().map(|&r| self.boards[s * rides + r as usize]).sum();
                st.mean_queue += (w * mu0) as f32;
                st.sd_queue += (w * var0.sqrt()) as f32;
                st.growth_cap += (w * g_cap) as f32;
                st.growth_route += (w * g_route) as f32;
                st.spare += (w * self.spare(s, x)) as f32;
                st.board_rate += (w * board) as f32;
                st.spawn_rate += (w * row) as f32;
                st.stuck_rate += (w * self.stuck[s * n + x]) as f32;
                demand += w * row;
                left += w * (g_cap + g_route);
                let rk = self.station_risk(ctx, s, x);
                st.p_over += (w * rk.p_over) as f32;
                st.excess += (w * rk.excess) as f32;
            }
            st.growth = st.growth_cap + st.growth_route;
            st.backlog_drain = self.bl_drain[x] as f32;
            st.backlog_stay = self.bl_stuck[x] as f32;
            st.backlog = st.backlog_drain + st.backlog_stay;
            st.capacity = ctx.p.stations[x].capacity as f32;
            // a janela: media da fila e fracao do tempo acima da capacidade
            let cap = ctx.p.stations[x].capacity.max(1) as f64;
            const K: usize = 12;
            let mut late_n = 0;
            for k in 0..K {
                let f = (k as f64 + 0.5) / K as f64;
                let (fixed, m, v) = self.queue_at(ctx, scen_at(f), x, f * window);
                let mu = fixed + m;
                let (_, p) = queue_tail(fixed, m, v, cap);
                st.window_queue += (mu / K as f64) as f32;
                st.window_p_over += (p / K as f64) as f32;
                if f >= 1.0 / 3.0 {
                    st.late_queue += mu as f32;
                    st.late_p_over += p as f32;
                    late_n += 1;
                }
            }
            if late_n > 0 {
                st.late_queue /= late_n as f32;
                st.late_p_over /= late_n as f32;
            }
            if x < ctx.na() {
                ex.stations.push(st);
            }
        }
        for &s in &scen {
            travel += wt(s) * self.travel[s];
        }
        let delivered = demand - left;
        ex.delivered_rate = delivered as f32;
        ex.demand_rate = demand as f32;
        ex.avg_travel_time = if delivered > 0.0 { (travel / delivered) as f32 } else { 0.0 };
        ex.window = window as f32;
        ex
    }
}

/// Fila = parte fixa `fixed` + N, com N Poisson de media M, e M variando
/// (idade uniforme de cada leva entre dois trens) com media `m` e variancia
/// `v`. Devolve (E[(fila - cap)+], P(fila > cap)). M e integrado numa
/// uniforme com a mesma media e variancia (exato para um sentido so).
pub fn queue_tail(fixed: f64, m: f64, v: f64, cap: f64) -> (f64, f64) {
    let spread = 6.0 * (m + v).sqrt();
    if fixed + m + spread + 1.0 < cap {
        return (0.0, 0.0); // longe da capacidade: cauda desprezivel
    }
    let lo = m - (3.0 * v.max(0.0)).sqrt();
    if lo > 0.0 && fixed + lo - 6.0 * lo.sqrt() > cap + 1.0 {
        return (fixed + m - cap, 1.0); // acima da capacidade em qualquer caso
    }
    if m > 60.0 {
        // media grande: a normal ja e boa, e a Poisson estouraria exp(-m)
        return normal_excess(fixed + m, (v + m).sqrt(), cap + 0.5);
    }
    let half = (3.0 * v.max(0.0)).sqrt();
    if half < 1e-3 {
        return poisson_shift_tail(fixed, m, cap);
    }
    const K: usize = 4;
    let (mut ex, mut p) = (0.0, 0.0);
    for k in 0..K {
        let mk = (m - half + 2.0 * half * (k as f64 + 0.5) / K as f64).max(0.0);
        let (e1, p1) = poisson_shift_tail(fixed, mk, cap);
        ex += e1 / K as f64;
        p += p1 / K as f64;
    }
    (ex, p)
}

/// (E[(fixed + N - cap)+], P(fixed + N > cap)) para N ~ Poisson(mean).
fn poisson_shift_tail(fixed: f64, mean: f64, cap: f64) -> (f64, f64) {
    // "acima da capacidade" = pelo menos cap + 1 passageiros (fila inteira)
    let room = cap + 0.5 - fixed; // N precisa passar disto
    if room < 0.0 {
        return (fixed + mean - cap, 1.0);
    }
    let kmax = room.floor() as usize; // N <= kmax: nao passou
    let mut pmf = (-mean).exp();
    let mut below = 0.0; // P(N <= kmax)
    let mut short = 0.0; // E[(cap - fixed - N)+] nos N que nao passaram
    for n in 0..=kmax {
        below += pmf;
        let gap = cap - fixed - n as f64;
        if gap > 0.0 {
            short += gap * pmf;
        }
        pmf *= mean / (n + 1) as f64;
    }
    let ex = (fixed + mean - cap + short).max(0.0);
    (ex, (1.0 - below).clamp(0.0, 1.0))
}

/// E[(X - c)+] e P(X > c) para X normal(mu, sd).
pub fn normal_excess(mu: f64, sd: f64, c: f64) -> (f64, f64) {
    if sd < 1e-9 {
        return ((mu - c).max(0.0), if mu > c { 1.0 } else { 0.0 });
    }
    let z = (c - mu) / sd;
    let pdf = (-0.5 * z * z).exp() / (2.0 * std::f64::consts::PI).sqrt();
    let tail = 0.5 * erfc(z / std::f64::consts::SQRT_2);
    ((sd * (pdf - z * tail)).max(0.0), tail)
}

/// erfc com erro < 1,2e-7 (Numerical Recipes, erfcc).
fn erfc(x: f64) -> f64 {
    let z = x.abs();
    let t = 1.0 / (1.0 + 0.5 * z);
    let r = t
        * (-z * z - 1.265_512_23
            + t * (1.000_023_68
                + t * (0.374_091_96
                    + t * (0.096_784_18
                        + t * (-0.186_288_06
                            + t * (0.278_868_07
                                + t * (-1.135_203_98 + t * (1.488_515_87 + t * (-0.822_152_23 + t * 0.170_872_77)))))))))
            .exp();
    if x >= 0.0 {
        r
    } else {
        2.0 - r
    }
}

/// Janela em que a fila que cresce (sem rota, trem cheio) se acumula.
fn persist_horizon(ctx: &Ctx) -> f64 {
    (ctx.cfg.persist_days.max(ctx.cfg.horizon_days).max(0.5) * ctx.p.params.day_length) as f64
}

fn scen_weight_sum(ctx: &Ctx, scen: &[usize]) -> f64 {
    scen.iter().map(|&s| ctx.scenarios[s].weight as f64).sum()
}

/// Encaixar f entre a e b trocando um link seco por um molhado custa uma
/// travessia; pesa como 400 unidades de trilho a mais.
fn water_extra(ctx: &Ctx, a: usize, f: usize, b: usize) -> f32 {
    let before = ctx.d.w(a, b) as i32;
    let after = ctx.d.w(a, f) as i32 + ctx.d.w(f, b) as i32;
    (after - before).max(0) as f32 * 400.0
}

// heap binario minimo sobre (bits do f32 positivo, no)
#[inline]
fn heap_push(h: &mut Vec<(u32, u32)>, key: f32, node: u32) {
    h.push((key.to_bits(), node));
    let mut i = h.len() - 1;
    while i > 0 {
        let p = (i - 1) / 2;
        if h[p].0 <= h[i].0 {
            break;
        }
        h.swap(p, i);
        i = p;
    }
}

#[inline]
fn heap_pop(h: &mut Vec<(u32, u32)>) -> Option<(u32, u32)> {
    if h.is_empty() {
        return None;
    }
    let top = h[0];
    let last = h.pop().unwrap();
    if !h.is_empty() {
        h[0] = last;
        let len = h.len();
        let mut i = 0;
        loop {
            let l = 2 * i + 1;
            let r = l + 1;
            let mut m = i;
            if l < len && h[l].0 < h[m].0 {
                m = l;
            }
            if r < len && h[r].0 < h[m].0 {
                m = r;
            }
            if m == i {
                break;
            }
            h.swap(m, i);
            i = m;
        }
    }
    Some(top)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::Config;
    use crate::genome::Route;
    use crate::geom::V2;
    use crate::problem::{Problem, Station, CIRCLE, SQUARE, TRIANGLE};

    /// 6 estacoes num circulo, alternando formas; sem futuras, sem agua.
    fn ring() -> Ctx {
        let shapes = [CIRCLE, SQUARE, TRIANGLE, CIRCLE, SQUARE, TRIANGLE];
        let stations = (0..6)
            .map(|i| {
                let a = i as f32 * std::f32::consts::TAU / 6.0;
                Station {
                    pos: V2::new(400.0 * a.cos(), 400.0 * a.sin()),
                    shape: shapes[i],
                    capacity: 6,
                    centrality: 1.0,
                    service: 1.0,
                    ..Default::default()
                }
            })
            .collect();
        let mut p = Problem {
            stations,
            na: 6,
            spawn: crate::problem::builtin_spawn_table(),
            city: "ring".into(),
            ..Default::default()
        };
        p.params.now = 20.0 * 7.0 + 1.0;
        p.params.day = 7;
        p.params.week = 1;
        p.budget.max_lines = 3;
        p.budget.line_budget = 3;
        p.budget.locos = 6;
        p.budget.crossings = 0;
        p.budget.max_locos_at = vec![4; 3];
        Ctx::new(p, Config { horizon_days: 1.0, ..Config::default() })
    }

    fn eval_with(ctx: &Ctx, looped: bool, locos: u16) -> (f64, Explain) {
        let mut g = Genome::new();
        g.routes.push(Route::new((0..6).collect(), looped, locos, 0));
        let mut ev = Evaluator::new(ctx);
        ev.explain = Some(Explain::default());
        let f = ev.evaluate(ctx, &mut g);
        (f, ev.explain.take().unwrap())
    }

    #[test]
    fn loop_with_one_train_is_one_way() {
        let ctx = ring();
        let (_, one) = eval_with(&ctx, true, 1);
        let (_, two) = eval_with(&ctx, true, 2);
        assert_eq!(one.lines[0].n_dir, [1.0, 0.0]);
        assert_eq!(two.lines[0].n_dir, [1.0, 1.0]);
        assert!(one.lines[0].headway[1].is_infinite());
        // mao unica: quem ia para tras da a volta, entao a viagem media e maior
        assert!(one.avg_travel_time > two.avg_travel_time, "{} vs {}", one.avg_travel_time, two.avg_travel_time);
    }

    #[test]
    fn open_line_cycle_is_there_and_back() {
        let ctx = ring();
        let (_, open) = eval_with(&ctx, false, 1);
        let l = &open.lines[0];
        assert_eq!(l.n_dir, [1.0, 1.0]);
        assert!((l.cycle[0] - l.cycle[1]).abs() < 1e-3);
        // ida e volta: pelo menos 2x o trecho
        assert!(l.cycle[0] >= 2.0 * l.run);
        assert!((l.headway[0] - l.cycle[0]).abs() < 1e-3);
    }

    #[test]
    fn queue_tail_matches_poisson() {
        // sem variacao na media: Poisson puro. P(N > 6,5) com media 4:
        // 1 - P(N <= 6) = 0,11067
        let (_, p) = queue_tail(0.0, 4.0, 0.0, 6.0);
        assert!((p - 0.110_67).abs() < 1e-4, "{p}");
        // parte fixa acima da capacidade: sempre acima
        let (ex, p) = queue_tail(8.0, 0.0, 0.0, 6.0);
        assert!((p - 1.0).abs() < 1e-12);
        assert!((ex - 2.0).abs() < 1e-9);
        // nada na fila
        assert_eq!(queue_tail(0.0, 0.0, 0.0, 6.0), (0.0, 0.0));
        // mais variancia na media, mais cauda
        let (_, p1) = queue_tail(0.0, 4.0, 0.0, 6.0);
        let (_, p2) = queue_tail(0.0, 4.0, 4.0, 6.0);
        assert!(p2 > p1);
    }

    #[test]
    fn normal_excess_sane() {
        let (e, p) = normal_excess(6.0, 1e-12, 6.5);
        assert_eq!((e, p), (0.0, 0.0));
        let (e, p) = normal_excess(6.5, 2.0, 6.5);
        assert!((p - 0.5).abs() < 1e-6);
        assert!((e - 2.0 * 0.398_942_28).abs() < 1e-4, "{e}");
        assert!(erfc(-1.0) > 1.8 && erfc(1.0) < 0.16);
    }
}
