//! Contexto imutavel de uma otimizacao: problema + derivados + a demanda do
//! horizonte, ja quebrada em cenarios e epocas.
//!
//! Cenario = um pedaco de um dia do horizonte (dia util ou fim de semana, com a
//! tensao e o crescimento de spawn daquele dia). Demanda e taxa em
//! passageiros/segundo por (estacao de origem, forma de destino).
//!
//! Epoca = um conjunto de estacoes presentes (ativas + futuras que ja nasceram) e
//! de formas (trocas agendadas ja aplicadas). O roteamento so depende da epoca,
//! entao cenarios da mesma epoca dividem as mesmas Dijkstras e so o acumulo de
//! fluxo e feito por cenario.

use crate::config::Config;
use crate::problem::{Derived, Problem};

pub const MAX_EPOCHS: usize = 4;

pub struct Scenario {
    /// fracao do horizonte (soma 1)
    pub weight: f32,
    pub epoch: usize,
    /// rate[u * S + slot], passageiros/segundo; u indexa todas as n estacoes
    pub rate: Vec<f32>,
    pub day_mask: u8,
}

pub struct Epoch {
    /// Clock.Time de referencia (meio do primeiro cenario da epoca)
    pub time: f32,
    pub present: Vec<bool>,
    /// forma (slot) de cada estacao nesta epoca
    pub slot: Vec<u8>,
    /// estacoes futuras presentes, em ordem de ativacao (para encaixe virtual)
    pub future: Vec<u16>,
    /// ha alguma estacao de cada slot presente
    pub slot_present: Vec<bool>,
}

pub struct Ctx {
    pub p: Problem,
    pub d: Derived,
    pub cfg: Config,
    pub scenarios: Vec<Scenario>,
    pub epochs: Vec<Epoch>,
    /// demanda somada por estacao ativa (cenario 0 + fila), para as heuristicas
    pub station_weight: Vec<f32>,
    /// fila atual por estacao: (slot de destino, passageiros). So ativas.
    pub backlog: Vec<Vec<(u8, f32)>>,
    pub max_lines: usize,
    pub speed: f32,
}

impl Ctx {
    pub fn new(p: Problem, cfg: Config) -> Ctx {
        let d = Derived::new(&p);
        let speed = if cfg.speed_override > 0.0 { cfg.speed_override } else { p.params.speed.max(1.0) };
        let max_lines = {
            let lb = p.budget.line_budget.max(1) as usize;
            let lb = lb.min(p.budget.locos.max(1) as usize);
            if cfg.max_lines_override > 0 { cfg.max_lines_override.min(lb) } else { lb }
        };
        let mut ctx = Ctx {
            p,
            d,
            cfg,
            scenarios: Vec::new(),
            epochs: Vec::new(),
            station_weight: Vec::new(),
            backlog: Vec::new(),
            max_lines,
            speed,
        };
        ctx.build_scenarios();
        ctx
    }

    pub fn n(&self) -> usize {
        self.d.n
    }
    pub fn na(&self) -> usize {
        self.d.na
    }
    pub fn shapes(&self) -> usize {
        self.d.shape_count()
    }

    fn build_scenarios(&mut self) {
        let pr = self.p.params.clone();
        let dl = pr.day_length;
        let n = self.d.n;
        let s_count = self.d.shape_count();
        let horizon = self.cfg.horizon_days.max(0.05) * dl;
        let t0 = pr.now;
        let t1 = t0 + horizon;

        // dias cobertos pelo horizonte
        let first_day = (t0 / dl).floor() as i32;
        let mut spans: Vec<(i32, f32, f32)> = Vec::new();
        let mut day = first_day;
        loop {
            let a = (day as f32 * dl).max(t0);
            let b = ((day + 1) as f32 * dl).min(t1);
            if b > a + 1e-3 {
                spans.push((day, a, b));
            }
            day += 1;
            if day as f32 * dl >= t1 || spans.len() > 32 {
                break;
            }
        }
        if spans.is_empty() {
            spans.push((first_day, t0, t0 + dl));
        }
        let total: f32 = spans.iter().map(|s| s.2 - s.1).sum();

        // epocas primeiro (precisa de &mut self), demanda depois
        let epochs: Vec<usize> = spans.iter().map(|s| self.epoch_for(0.5 * (s.1 + s.2))).collect();

        // o dia de hoje segundo o jogo, para a semana dos dias futuros
        let today = if pr.day > 0 { pr.day } else { first_day };
        let dow_today = today.rem_euclid(7);

        // Clock.Tension(0,08) do dia g
        let tension_of = |g: i32| -> f32 {
            let dow = g.rem_euclid(7);
            let week = pr.week + (dow_today + (g - today)).div_euclid(7);
            let mut t = week.clamp(0, 24) as f32 * 0.2;
            if dow < 5 {
                t += dow as f32 * 0.06;
            }
            1.0 + t * 0.08
        };
        // CityPlanner.PeepSpawnScale no instante t: cresce +boost a cada 6 s
        // depois da ultima estacao (planner_scale ja inclui o que cresceu ate agora)
        let growth = |t: f32| -> f32 {
            if pr.spawn_grows && t > pr.last_station_spawn {
                pr.spawn_boost * (t - pr.last_station_spawn) / 6.0
            } else {
                0.0
            }
        };
        let planner_at = |t: f32| (pr.planner_scale - growth(t0) + growth(t)).max(0.0);
        let hl = dl / 24.0;

        for (k, &(day, a, b)) in spans.iter().enumerate() {
            let dow = day.rem_euclid(7);
            let day_mask = 1u8 << dow;
            let epoch = epochs[k];
            let ep = &self.epochs[epoch];
            let mut rate = vec![0f32; n * s_count];
            for u in 0..n {
                if !ep.present[u] {
                    continue;
                }
                let st = &self.p.stations[u];
                let origin = ep.slot[u] as usize;
                let mut base = st.centrality.max(0.0) * pr.city_spawn_scale.max(0.0);
                if pr.service_affects {
                    base *= st.service.max(0.0);
                }
                let act = if u < self.d.na { f32::MIN } else { st.active_time };
                // StationSchedule.CreateSpawns: na virada do dia g a estacao sorteia
                // o dia inteiro; a entrada (hora h, x por dia) solta floor(x)
                // passageiros, mais 1 com chance frac(x), nas horas h, h+2, h+4...
                // (pode transbordar para o dia seguinte). Aqui: quantos caem
                // dentro do trecho [a, b), em valor esperado.
                for g in [day - 1, day] {
                    if g < 0 || act >= (g + 1) as f32 * dl {
                        continue;
                    }
                    let gmask = 1u8 << g.rem_euclid(7);
                    // estacao que nasce no meio do dia so sorteia as horas seguintes
                    let cut = if act > g as f32 * dl { (act - g as f32 * dl) / hl } else { -1.0 };
                    let t_gen = (g as f32 * dl).max(act);
                    let mut scale = planner_at(t_gen) * base;
                    if g < 7 && scale > 1.0 {
                        let k = (g as f32 / 7.0).sin();
                        scale = scale * k + (1.0 - k);
                    }
                    let x_mul = tension_of(g) * scale * self.cfg.demand_scale;
                    for &(dest, hour, days, per_day) in &self.d.table[origin] {
                        if days & gmask == 0 || !ep.slot_present[dest as usize] || (hour as f32) <= cut {
                            continue;
                        }
                        let x = per_day * x_mul;
                        let whole = x.floor();
                        let frac = x - whole;
                        let mut i = 0;
                        while (i as f32) <= whole {
                            let p = if (i as f32) < whole { 1.0 } else { frac };
                            let t = (g * 24 + hour + 2 * i) as f32 * hl;
                            if p > 0.0 && t >= a && t < b {
                                rate[u * s_count + dest as usize] += p / (b - a);
                            }
                            i += 1;
                        }
                    }
                }
            }
            self.scenarios.push(Scenario { weight: (b - a) / total, epoch, rate, day_mask });
        }

        // Fila atual: passageiros que ja estao na plataforma. NAO entram como
        // taxa (isso inflava a demanda de hoje em 2-3x): sao um lote que ja
        // esta ali e que o avaliador trata como fila inicial, escoando pela
        // folga dos trens ou ficando para sempre se nao tem rota.
        // crowd_weight 0,15 (o default antigo) = 1x.
        let backlog_k = self.cfg.crowd_weight / 0.15;
        let e0 = self.scenarios[0].epoch;
        let mut backlog = vec![Vec::new(); n];
        for (u, bl) in backlog.iter_mut().enumerate().take(self.d.na) {
            for &(t, c) in &self.p.stations[u].waiting {
                if let Some(slot) = self.d.slot_type.iter().position(|&x| x == t) {
                    let ep0 = &self.epochs[e0];
                    if slot != ep0.slot[u] as usize {
                        bl.push((slot as u8, c as f32 * backlog_k));
                    }
                }
            }
        }
        self.backlog = backlog;

        // peso de cada estacao para as heuristicas: demanda media + fila atual
        // diluida no horizonte
        let mut w = vec![0f32; self.d.na];
        for (u, wu) in w.iter_mut().enumerate() {
            let mut acc = 0.0;
            for sc in &self.scenarios {
                let row = &sc.rate[u * s_count..(u + 1) * s_count];
                acc += sc.weight * row.iter().sum::<f32>();
            }
            let q: f32 = self.backlog[u].iter().map(|b| b.1).sum();
            *wu = acc + q / horizon.max(1.0);
        }
        self.station_weight = w;
    }

    /// Epoca que vale no instante t. Cria se ainda nao existe (ate MAX_EPOCHS;
    /// depois disso reaproveita a ultima, que e a mais completa).
    fn epoch_for(&mut self, t: f32) -> usize {
        let p = &self.p;
        let n = self.d.n;
        let plan = self.cfg.plan_future;
        let mut present = vec![false; n];
        let mut future: Vec<(f32, u16)> = Vec::new();
        for (i, s) in p.stations.iter().enumerate() {
            if i < self.d.na {
                present[i] = true;
            } else if plan && s.active_time <= t {
                present[i] = true;
                future.push((s.active_time, i as u16));
            }
        }
        future.sort_by(|a, b| a.0.total_cmp(&b.0));
        let slot: Vec<u8> = (0..n)
            .map(|i| {
                let s = &p.stations[i];
                let ss = self.d.sched_slot[i];
                if ss != u8::MAX && s.sched_time <= t {
                    ss
                } else {
                    self.d.shape_slot[i]
                }
            })
            .collect();

        for (k, e) in self.epochs.iter().enumerate() {
            if e.present == present && e.slot == slot {
                return k;
            }
        }
        if self.epochs.len() >= MAX_EPOCHS {
            return self.epochs.len() - 1;
        }
        let mut slot_present = vec![false; self.d.shape_count()];
        for i in 0..n {
            if present[i] {
                slot_present[slot[i] as usize] = true;
            }
        }
        self.epochs.push(Epoch {
            time: t,
            present,
            slot,
            future: future.into_iter().map(|f| f.1).collect(),
            slot_present,
        });
        self.epochs.len() - 1
    }

    pub fn max_locos_for(&self, line: usize) -> usize {
        self.p.budget.max_locos_for(line) as usize
    }

    pub fn loco_budget(&self) -> usize {
        self.p.budget.locos.max(1) as usize
    }

    pub fn car_budget(&self) -> usize {
        self.p.budget.cars as usize
    }
}
