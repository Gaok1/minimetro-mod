//! O problema que o mod manda: estado da partida + tudo que o jogo ja decidiu
//! sobre o futuro. Formato binario little-endian, versionado. O lado C# e
//! `Core/ProblemExport.cs`; qualquer mudanca aqui tem que ir para la tambem.
//!
//! Estacoes: primeiro as ATIVAS (indices 0..na, na mesma ordem do Snapshot do C#,
//! que e o indice usado pelo genoma), depois as FUTURAS (na..n), que o jogo ja
//! sorteou na largada (CityPlanner.ScheduleStationSpawns) e que so ativam em
//! `active_time`. O genoma nunca referencia estacao futura: o avaliador as
//! encaixa virtualmente para medir quao bem a rede vai absorve-las.

use crate::geom::{octilinear_len, Hull, Seg, V2};
use crate::rng::Rng;

pub const MAGIC: u32 = 0x4147_4D4D; // "MMGA"
pub const VERSION: u32 = 3;

/// DayType do jogo: bit 0 = segunda ... bit 6 = domingo.
pub const WEEKDAY: u8 = 0b001_1111;
pub const WEEKEND: u8 = 0b110_0000;

pub const CIRCLE: u16 = 1;
pub const TRIANGLE: u16 = 2;
pub const SQUARE: u16 = 4;

#[derive(Clone, Debug, Default)]
pub struct Station {
    pub pos: V2,
    /// StationType do jogo (bit unico: CIRCLE=1, TRIANGLE=2, SQUARE=4, ...)
    pub shape: u16,
    pub capacity: u16,
    pub interchange: bool,
    pub centrality: f32,
    pub service: f32,
    /// Clock.Time em que ativa. Ativas ja passaram disso.
    pub active_time: f32,
    /// Troca de forma agendada (Station.ScheduledType), 0 = nenhuma.
    pub sched_shape: u16,
    pub sched_time: f32,
    /// Station.ExpiryTimerCompletion: 0 = folgada, 1 = game over.
    pub timer: f32,
    /// Passageiros esperando agora, por forma de destino.
    pub waiting: Vec<(u16, u16)>,
}

#[derive(Clone, Copy, Debug)]
pub struct SpawnEntry {
    pub origin: u16,
    pub dest: u16,
    pub hour: i32,
    pub days: u8,
    pub per_day: f32,
}

#[derive(Clone, Debug)]
pub struct Params {
    pub speed: f32,
    pub acc: f32,
    pub dec: f32,
    pub railcar_capacity: u16,
    /// Segundos entre passageiros embarcando/desembarcando (PeepPulsePeriod * 5/6).
    pub pulse: f32,
    /// Cidade embarca tudo num pulso (Seul).
    pub quick_embark: bool,
    /// 0 classico, 1 zen, 2 extremo, 3 sandbox
    pub mode: u8,
    pub day_length: f32,
    /// Clock.Time agora.
    pub now: f32,
    /// Clock.Day (absoluto) e Clock.Week.
    pub day: i32,
    pub week: i32,
    /// CityPlanner.PeepSpawnScale agora (sem o fator da cidade).
    pub planner_scale: f32,
    /// CityDefinition.PassengerSpawnScale.
    pub city_spawn_scale: f32,
    /// Game.PassengerSpawnBoost (0,05 no classico).
    pub spawn_boost: f32,
    /// Instante do ultimo spawn de estacao agendado; depois dele a demanda cresce.
    pub last_station_spawn: f32,
    /// Game.DoesPeepSpawnScale
    pub spawn_grows: bool,
    /// Game.DoesServiceAffectPeepSpawns (so Zen/Sandbox)
    pub service_affects: bool,
    /// CityDefinition.InterchangeCapacity (capacidade da estacao com interchange).
    pub interchange_capacity: u16,
}

impl Default for Params {
    fn default() -> Self {
        Params {
            speed: 130.0,
            acc: 55.0,
            dec: 110.0,
            railcar_capacity: 6,
            pulse: 0.5 * 5.0 / 6.0,
            quick_embark: false,
            mode: 0,
            day_length: 20.0,
            now: 0.0,
            day: 0,
            week: 0,
            planner_scale: 1.0,
            city_spawn_scale: 1.0,
            spawn_boost: 0.05,
            last_station_spawn: f32::MAX,
            spawn_grows: true,
            service_affects: false,
            interchange_capacity: 18,
        }
    }
}

#[derive(Clone, Debug, Default)]
pub struct Budget {
    pub max_lines: u16,
    /// Linhas que o AG pode usar (min(linhas, trens)).
    pub line_budget: u16,
    pub locos: u16,
    pub cars: u16,
    pub crossings: u16,
    /// Limite de locomotivas por indice de linha.
    pub max_locos_at: Vec<u16>,
}

impl Budget {
    pub fn max_locos_for(&self, line: usize) -> u16 {
        self.max_locos_at.get(line).copied().unwrap_or_else(|| {
            self.max_locos_at.last().copied().unwrap_or(4)
        }).max(1)
    }
}

#[derive(Clone, Debug, Default)]
pub struct CurrentRoute {
    pub stops: Vec<u16>,
    pub looped: bool,
    pub locos: u16,
    pub cars: u16,
    /// trens rodando contra a ordem de `stops` (so importa em loop)
    pub rev: u16,
    /// passageiros a bordo dos trens da linha agora (o que desembarca se ela
    /// for desmontada)
    pub onboard: u16,
}

#[derive(Clone, Debug, Default)]
pub struct Problem {
    pub params: Params,
    pub budget: Budget,
    pub stations: Vec<Station>,
    /// Quantas estacoes do comeco de `stations` estao ativas.
    pub na: usize,
    pub spawn: Vec<SpawnEntry>,
    pub hull: Vec<Seg>,
    pub current: Vec<CurrentRoute>,
    pub city: String,
}

// ---------------------------------------------------------------------------
// leitura / escrita
// ---------------------------------------------------------------------------

pub struct Reader<'a> {
    b: &'a [u8],
    p: usize,
}

#[derive(Debug)]
pub struct ParseError(pub String);

impl std::fmt::Display for ParseError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

type R<T> = Result<T, ParseError>;

impl<'a> Reader<'a> {
    pub fn new(b: &'a [u8]) -> Self {
        Reader { b, p: 0 }
    }
    fn take(&mut self, n: usize) -> R<&'a [u8]> {
        if self.p + n > self.b.len() {
            return Err(ParseError(format!("blob truncado em {} (+{})", self.p, n)));
        }
        let s = &self.b[self.p..self.p + n];
        self.p += n;
        Ok(s)
    }
    pub fn u8(&mut self) -> R<u8> {
        Ok(self.take(1)?[0])
    }
    pub fn u16(&mut self) -> R<u16> {
        let s = self.take(2)?;
        Ok(u16::from_le_bytes([s[0], s[1]]))
    }
    pub fn i32(&mut self) -> R<i32> {
        let s = self.take(4)?;
        Ok(i32::from_le_bytes([s[0], s[1], s[2], s[3]]))
    }
    pub fn u32(&mut self) -> R<u32> {
        Ok(self.i32()? as u32)
    }
    pub fn f32(&mut self) -> R<f32> {
        let s = self.take(4)?;
        Ok(f32::from_le_bytes([s[0], s[1], s[2], s[3]]))
    }
    fn count(&mut self, max: usize, what: &str) -> R<usize> {
        let n = self.i32()?;
        if n < 0 || n as usize > max {
            return Err(ParseError(format!("contagem invalida de {what}: {n}")));
        }
        Ok(n as usize)
    }
    fn string(&mut self) -> R<String> {
        let n = self.count(4096, "string")?;
        Ok(String::from_utf8_lossy(self.take(n)?).into_owned())
    }
}

#[derive(Default)]
pub struct Writer {
    pub b: Vec<u8>,
}

impl Writer {
    fn u8(&mut self, v: u8) {
        self.b.push(v);
    }
    fn u16(&mut self, v: u16) {
        self.b.extend_from_slice(&v.to_le_bytes());
    }
    fn i32(&mut self, v: i32) {
        self.b.extend_from_slice(&v.to_le_bytes());
    }
    fn u32(&mut self, v: u32) {
        self.b.extend_from_slice(&v.to_le_bytes());
    }
    fn f32(&mut self, v: f32) {
        self.b.extend_from_slice(&v.to_le_bytes());
    }
    fn string(&mut self, s: &str) {
        self.i32(s.len() as i32);
        self.b.extend_from_slice(s.as_bytes());
    }
}

impl Problem {
    pub fn parse(bytes: &[u8]) -> R<Problem> {
        let mut r = Reader::new(bytes);
        if r.u32()? != MAGIC {
            return Err(ParseError("magic invalido (nao e um problema do MiniMetroGA)".into()));
        }
        let ver = r.u32()?;
        // v1 = sem o sentido dos trens de loop nas rotas atuais; v2 = sem a
        // capacidade de interchange e sem a lotacao atual das linhas
        if !(1..=VERSION).contains(&ver) {
            return Err(ParseError(format!("versao {ver} do blob; este nucleo le 1 a {VERSION}")));
        }
        let city = r.string()?;

        let mut params = Params {
            speed: r.f32()?,
            acc: r.f32()?,
            dec: r.f32()?,
            railcar_capacity: r.u16()?,
            pulse: r.f32()?,
            quick_embark: r.u8()? != 0,
            mode: r.u8()?,
            day_length: r.f32()?,
            now: r.f32()?,
            day: r.i32()?,
            week: r.i32()?,
            planner_scale: r.f32()?,
            city_spawn_scale: r.f32()?,
            spawn_boost: r.f32()?,
            last_station_spawn: r.f32()?,
            spawn_grows: r.u8()? != 0,
            service_affects: r.u8()? != 0,
            interchange_capacity: 18,
        };
        if ver >= 3 {
            params.interchange_capacity = r.u16()?;
        }

        let mut budget = Budget {
            max_lines: r.u16()?,
            line_budget: r.u16()?,
            locos: r.u16()?,
            cars: r.u16()?,
            crossings: r.u16()?,
            max_locos_at: Vec::new(),
        };
        let nm = r.count(64, "max_locos_at")?;
        for _ in 0..nm {
            budget.max_locos_at.push(r.u16()?);
        }

        let n = r.count(4096, "estacoes")?;
        let na = r.count(n, "estacoes ativas")?;
        let mut stations = Vec::with_capacity(n);
        for _ in 0..n {
            let mut s = Station {
                pos: V2::new(r.f32()?, r.f32()?),
                shape: r.u16()?,
                capacity: r.u16()?,
                interchange: r.u8()? != 0,
                centrality: r.f32()?,
                service: r.f32()?,
                active_time: r.f32()?,
                sched_shape: r.u16()?,
                sched_time: r.f32()?,
                timer: r.f32()?,
                waiting: Vec::new(),
            };
            let nw = r.count(64, "fila")?;
            for _ in 0..nw {
                s.waiting.push((r.u16()?, r.u16()?));
            }
            stations.push(s);
        }

        let ns = r.count(4096, "tabela de spawn")?;
        let mut spawn = Vec::with_capacity(ns);
        for _ in 0..ns {
            spawn.push(SpawnEntry {
                origin: r.u16()?,
                dest: r.u16()?,
                hour: r.i32()?,
                days: r.u8()?,
                per_day: r.f32()?,
            });
        }

        let nh = r.count(1 << 20, "costa")?;
        let mut hull = Vec::with_capacity(nh);
        for _ in 0..nh {
            hull.push(Seg::new(V2::new(r.f32()?, r.f32()?), V2::new(r.f32()?, r.f32()?)));
        }

        let nr = r.count(64, "rotas atuais")?;
        let mut current = Vec::with_capacity(nr);
        for _ in 0..nr {
            let looped = r.u8()? != 0;
            let locos = r.u16()?;
            let cars = r.u16()?;
            let rev = if ver >= 2 { r.u16()? } else { locos / 2 };
            let onboard = if ver >= 3 { r.u16()? } else { 0 };
            let len = r.count(4096, "paradas")?;
            let mut stops = Vec::with_capacity(len);
            for _ in 0..len {
                let s = r.u16()?;
                if (s as usize) < na {
                    stops.push(s);
                }
            }
            current.push(CurrentRoute { stops, looped, locos, cars, rev, onboard });
        }

        let p = Problem { params, budget, stations, na, spawn, hull, current, city };
        p.validate()?;
        Ok(p)
    }

    fn validate(&self) -> R<()> {
        if self.na < 2 {
            return Err(ParseError("precisa de pelo menos 2 estacoes ativas".into()));
        }
        if !(self.params.speed > 0.0) || !(self.params.day_length > 0.0) {
            return Err(ParseError("velocidade/dia invalidos".into()));
        }
        Ok(())
    }

    pub fn to_bytes(&self) -> Vec<u8> {
        let mut w = Writer::default();
        w.u32(MAGIC);
        w.u32(VERSION);
        w.string(&self.city);
        let p = &self.params;
        w.f32(p.speed);
        w.f32(p.acc);
        w.f32(p.dec);
        w.u16(p.railcar_capacity);
        w.f32(p.pulse);
        w.u8(p.quick_embark as u8);
        w.u8(p.mode);
        w.f32(p.day_length);
        w.f32(p.now);
        w.i32(p.day);
        w.i32(p.week);
        w.f32(p.planner_scale);
        w.f32(p.city_spawn_scale);
        w.f32(p.spawn_boost);
        w.f32(p.last_station_spawn);
        w.u8(p.spawn_grows as u8);
        w.u8(p.service_affects as u8);
        w.u16(p.interchange_capacity);
        let b = &self.budget;
        w.u16(b.max_lines);
        w.u16(b.line_budget);
        w.u16(b.locos);
        w.u16(b.cars);
        w.u16(b.crossings);
        w.i32(b.max_locos_at.len() as i32);
        for &m in &b.max_locos_at {
            w.u16(m);
        }
        w.i32(self.stations.len() as i32);
        w.i32(self.na as i32);
        for s in &self.stations {
            w.f32(s.pos.x);
            w.f32(s.pos.y);
            w.u16(s.shape);
            w.u16(s.capacity);
            w.u8(s.interchange as u8);
            w.f32(s.centrality);
            w.f32(s.service);
            w.f32(s.active_time);
            w.u16(s.sched_shape);
            w.f32(s.sched_time);
            w.f32(s.timer);
            w.i32(s.waiting.len() as i32);
            for &(t, c) in &s.waiting {
                w.u16(t);
                w.u16(c);
            }
        }
        w.i32(self.spawn.len() as i32);
        for e in &self.spawn {
            w.u16(e.origin);
            w.u16(e.dest);
            w.i32(e.hour);
            w.u8(e.days);
            w.f32(e.per_day);
        }
        w.i32(self.hull.len() as i32);
        for s in &self.hull {
            w.f32(s.a.x);
            w.f32(s.a.y);
            w.f32(s.b.x);
            w.f32(s.b.y);
        }
        w.i32(self.current.len() as i32);
        for c in &self.current {
            w.u8(c.looped as u8);
            w.u16(c.locos);
            w.u16(c.cars);
            w.u16(c.rev);
            w.u16(c.onboard);
            w.i32(c.stops.len() as i32);
            for &s in &c.stops {
                w.u16(s);
            }
        }
        w.b
    }
}

// ---------------------------------------------------------------------------
// tabela de spawn do jogo (StationDatabase.Load), com os dois deslizes do
// original. So para o gerador sintetico e para testes: em jogo a tabela vem
// lida por reflexao pelo mod.
// ---------------------------------------------------------------------------

pub fn builtin_spawn_table() -> Vec<SpawnEntry> {
    const ANY: u8 = 0x7F;
    let (c, t, s) = (CIRCLE, TRIANGLE, SQUARE);
    let (cross, diamond, egg, gem, pent, star, wedge) = (8u16, 0x10, 0x20, 0x40, 0x80, 0x100, 0x200);
    let raw: &[(u16, i32, u8, u16, f32)] = &[
        (c, 7, WEEKDAY, s, 1.0),
        (c, 16, ANY, t, 1.0),
        (c, 11, WEEKEND, t, 0.75),
        (c, 10, ANY, cross, 0.25),
        (c, 14, WEEKEND, wedge, 0.5),
        (c, 9, WEEKEND, star, 0.25),
        (c, 11, WEEKEND, diamond, 0.25),
        (c, 9, WEEKEND, gem, 0.2),
        (c, 13, WEEKEND, gem, 0.2),
        (c, 15, WEEKEND, egg, 0.4),
        (c, 11, WEEKDAY, gem, 0.1), // deslize: era do SQUARE
        (t, 18, ANY, c, 1.0),
        (t, 15, WEEKEND, c, 0.75),
        (t, 15, WEEKEND, star, 0.25),
        (t, 9, WEEKEND, pent, 0.25),
        (s, 17, WEEKDAY, c, 1.0),
        (s, 12, WEEKDAY, t, 0.5),
        (s, 10, WEEKDAY, diamond, 0.25),
        (s, 13, WEEKDAY, pent, 0.25),
        (pent, 9, WEEKDAY, t, 0.25),
        (pent, 14, WEEKDAY, s, 0.25),
        (diamond, 10, WEEKDAY, s, 0.5),
        (star, 12, WEEKDAY, t, 0.5),
        (star, 15, WEEKEND, c, 0.5),
        (cross, 4, ANY, c, 0.25),
        (cross, 16, ANY, c, 0.25),
        (wedge, 8, ANY, c, 0.25),
        (wedge, 16, ANY, c, 0.25),
        (wedge, 13, WEEKEND, c, 0.5),
        (wedge, 14, WEEKDAY, s, 0.1), // deslize: era do GEM
        (wedge, 20, WEEKEND, c, 0.4), // deslize: era do GEM
        (egg, 21, WEEKEND, c, 2.0),
    ];
    raw.iter()
        .map(|&(origin, hour, days, dest, per_day)| SpawnEntry { origin, dest, hour, days, per_day })
        .collect()
}

/// Centralidade do jogo (Station.Position): 1,1 ate 81 unidades do centro,
/// 0,5 alem de 729.
pub fn centrality(pos: V2, centre: V2) -> f32 {
    let v = pos.sub(centre).len().sqrt().clamp(9.0, 27.0);
    1.1 + (0.5 - 1.1) * ((v - 9.0) / 18.0)
}

// ---------------------------------------------------------------------------
// gerador sintetico, para o bench e para testes
// ---------------------------------------------------------------------------

pub struct SynthSpec {
    pub active: usize,
    pub future: usize,
    pub week: i32,
    pub seed: u64,
    pub river: bool,
    pub with_network: bool,
}

pub fn synthetic(spec: &SynthSpec) -> Problem {
    let mut rng = Rng::new(spec.seed);
    let total = spec.active + spec.future;
    let half_w = 300.0 + 30.0 * total as f32;
    let half_h = half_w * 0.62;
    let min_sep = 95.0f32;

    let mut pts: Vec<V2> = Vec::new();
    let mut guard = 0;
    while pts.len() < total && guard < 200_000 {
        guard += 1;
        // estacoes cedo nascem mais no centro, como a camera do jogo abrindo
        let grow = 0.35 + 0.65 * (pts.len() as f32 / total.max(1) as f32);
        let p = V2::new(
            (rng.f64() as f32 * 2.0 - 1.0) * half_w * grow,
            (rng.f64() as f32 * 2.0 - 1.0) * half_h * grow,
        );
        if pts.iter().all(|q| p.sub(*q).len() >= min_sep) {
            pts.push(p);
        }
    }

    let specials = [8u16, 0x10, 0x80, 0x100, 0x200];
    let mut stations = Vec::with_capacity(pts.len());
    let dt = 0.62 * 20.0; // uma estacao a cada ~0,6 dia, como o regime de Londres
    let now = (spec.week.max(0) as f32) * 140.0 + 30.0;
    for (i, &pos) in pts.iter().enumerate() {
        let shape = match i {
            0 => SQUARE,
            1 => CIRCLE,
            2 => TRIANGLE,
            _ => {
                let r = rng.f64();
                if r < 0.55 {
                    CIRCLE
                } else if r < 0.80 {
                    TRIANGLE
                } else if r < 0.90 {
                    SQUARE
                } else {
                    specials[rng.below(specials.len())]
                }
            }
        };
        let active_time = if i < spec.active {
            0.0
        } else {
            now + dt * (i - spec.active + 1) as f32
        };
        stations.push(Station {
            pos,
            shape,
            capacity: 6,
            interchange: false,
            centrality: 1.0,
            service: 1.0,
            active_time,
            ..Default::default()
        });
    }
    let centre = {
        let k = 3.min(stations.len()) as f32;
        let mut c = V2::default();
        for s in stations.iter().take(3) {
            c = c.add(s.pos);
        }
        V2::new(c.x / k, c.y / k)
    };
    for s in stations.iter_mut() {
        s.centrality = centrality(s.pos, centre);
    }
    // um pouco de fila e uma estacao apertando
    for s in stations.iter_mut().take(spec.active) {
        if rng.chance(0.4) {
            let n = 1 + rng.below(4) as u16;
            let dest = if s.shape == CIRCLE { TRIANGLE } else { CIRCLE };
            s.waiting.push((dest, n));
        }
    }
    if spec.active > 4 {
        let k = rng.below(spec.active);
        stations[k].waiting.push((SQUARE, 5));
        stations[k].timer = 0.3;
    }

    let mut hull = Vec::new();
    if spec.river {
        // rio atravessando a cidade na horizontal, meio sinuoso; duas margens
        let steps = 24;
        for bank in [-40.0f32, 40.0] {
            let mut prev: Option<V2> = None;
            for k in 0..=steps {
                let x = -half_w * 1.2 + 2.4 * half_w * k as f32 / steps as f32;
                let y = 60.0 * (x / 180.0).sin() + bank;
                let p = V2::new(x, y);
                if let Some(q) = prev {
                    hull.push(Seg::new(q, p));
                }
                prev = Some(p);
            }
        }
    }

    let w = spec.week.max(0) as u16;
    let max_lines = 7u16;
    let lines = (3 + w).min(max_lines);
    let locos = 3 + w;
    let budget = Budget {
        max_lines,
        line_budget: lines.min(locos),
        locos,
        cars: w / 2,
        crossings: 3 + w / 3,
        max_locos_at: vec![4; max_lines as usize],
    };

    let mut p = Problem {
        params: Params { now, day: (now / 20.0) as i32, week: spec.week, ..Default::default() },
        budget,
        stations,
        na: spec.active.min(pts.len()),
        spawn: builtin_spawn_table(),
        hull,
        current: Vec::new(),
        city: format!("synth-{}-{}", spec.active, spec.seed),
    };
    if spec.with_network {
        // rede "de jogador": uma linha ligando as estacoes na ordem de x
        let mut order: Vec<u16> = (0..p.na as u16).collect();
        order.sort_by(|&a, &b| p.stations[a as usize].pos.x.total_cmp(&p.stations[b as usize].pos.x));
        let k = (p.budget.line_budget as usize).max(1);
        let chunk = order.len().div_ceil(k).max(2);
        for c in order.chunks(chunk) {
            if c.len() >= 2 {
                p.current.push(CurrentRoute { stops: c.to_vec(), looped: false, locos: 1, cars: 0, rev: 0, onboard: 4 });
            }
        }
    }
    p
}

// ---------------------------------------------------------------------------
// derivados: tudo que nao muda entre avaliacoes
// ---------------------------------------------------------------------------

pub struct Derived {
    pub n: usize,
    pub na: usize,
    /// comprimento octilinear, n x n
    pub dist: Vec<f32>,
    /// link precisa de travessia (as duas formas cruzam a costa), n x n
    pub water: Vec<bool>,
    /// slot de forma por estacao (forma atual) e forma agendada (slot ou u8::MAX)
    pub shape_slot: Vec<u8>,
    pub sched_slot: Vec<u8>,
    /// StationType por slot
    pub slot_type: Vec<u16>,
    /// tabela de spawn por slot de origem: (slot destino, hora, mascara de dia, porDia)
    pub table: Vec<Vec<(u8, i32, u8, f32)>>,
    pub hull: Hull,
    pub crossing_delay: f32,
    pub stop_loss: f32,
}

impl Derived {
    pub fn new(p: &Problem) -> Derived {
        let n = p.stations.len();
        let hull = Hull::new(p.hull.clone());
        let mut dist = vec![0f32; n * n];
        let mut water = vec![false; n * n];
        for i in 0..n {
            for j in (i + 1)..n {
                let a = p.stations[i].pos;
                let b = p.stations[j].pos;
                let d = octilinear_len(a, b);
                dist[i * n + j] = d;
                dist[j * n + i] = d;
                let w = hull.link_needs_crossing(a, b);
                water[i * n + j] = w;
                water[j * n + i] = w;
            }
        }

        let mut slot_type: Vec<u16> = Vec::new();
        let slot_of = |t: u16, slot_type: &mut Vec<u16>| -> u8 {
            if let Some(k) = slot_type.iter().position(|&x| x == t) {
                k as u8
            } else {
                slot_type.push(t);
                (slot_type.len() - 1) as u8
            }
        };
        let mut shape_slot = Vec::with_capacity(n);
        let mut sched_slot = Vec::with_capacity(n);
        for s in &p.stations {
            shape_slot.push(slot_of(s.shape, &mut slot_type));
            sched_slot.push(if s.sched_shape != 0 && s.sched_shape != s.shape {
                slot_of(s.sched_shape, &mut slot_type)
            } else {
                u8::MAX
            });
        }
        // formas que so aparecem como destino de passageiro ja na fila
        for s in &p.stations {
            for &(t, _) in &s.waiting {
                slot_of(t, &mut slot_type);
            }
        }

        let mut table = vec![Vec::new(); slot_type.len()];
        for e in &p.spawn {
            let (Some(o), Some(d)) = (
                slot_type.iter().position(|&x| x == e.origin),
                slot_type.iter().position(|&x| x == e.dest),
            ) else {
                continue; // forma que nao existe nesta cidade: o jogo descarta
            };
            if o != d {
                table[o].push((d as u8, e.hour, e.days, e.per_day));
            }
        }

        let pr = &p.params;
        Derived {
            n,
            na: p.na,
            dist,
            water,
            shape_slot,
            sched_slot,
            slot_type,
            table,
            hull,
            crossing_delay: crate::geom::line_crossing_delay(pr.speed, pr.acc, pr.dec),
            stop_loss: crate::geom::stop_loss(pr.speed, pr.acc, pr.dec),
        }
    }

    #[inline]
    pub fn d(&self, a: usize, b: usize) -> f32 {
        self.dist[a * self.n + b]
    }

    #[inline]
    pub fn w(&self, a: usize, b: usize) -> bool {
        self.water[a * self.n + b]
    }

    pub fn shape_count(&self) -> usize {
        self.slot_type.len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roundtrip() {
        let p = synthetic(&SynthSpec { active: 20, future: 6, week: 3, seed: 7, river: true, with_network: true });
        let b = p.to_bytes();
        let q = Problem::parse(&b).expect("parse");
        assert_eq!(q.stations.len(), p.stations.len());
        assert_eq!(q.na, p.na);
        assert_eq!(q.spawn.len(), p.spawn.len());
        assert_eq!(q.hull.len(), p.hull.len());
        assert_eq!(q.current.len(), p.current.len());
        assert_eq!(q.to_bytes(), b);
    }

    #[test]
    fn centrality_matches_game() {
        let c = V2::default();
        assert!((centrality(V2::new(50.0, 0.0), c) - 1.1).abs() < 1e-6);
        assert!((centrality(V2::new(800.0, 0.0), c) - 0.5).abs() < 1e-6);
    }
}
