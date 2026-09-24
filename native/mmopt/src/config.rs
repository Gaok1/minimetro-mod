//! Parametros do AG e pesos do fitness. O C# manda um vetor de f64 nesta ordem
//! (`Ga/NativeEngine.cs`, `ConfigVector`). Indice novo vai sempre no fim, e o que
//! faltar no vetor fica com o default daqui, entao versoes diferentes dos dois
//! lados continuam conversando.

#[derive(Clone, Debug)]
pub struct Config {
    // AG
    pub population: usize,
    pub generations: usize,
    pub elitism: usize,
    pub tournament: usize,
    pub crossover_rate: f64,
    pub mutation_rate: f64,
    pub mutations_per_genome: usize,
    pub seed: u64,
    pub seed_from_current: bool,
    pub stagnation_restart: usize,
    // espaco de busca
    pub max_lines_override: usize,
    pub min_stations_per_line: usize,
    pub max_stations_per_line: usize,
    pub allow_loops: bool,
    // demanda
    pub demand_scale: f32,
    pub crowd_weight: f32,
    // simulacao
    pub speed_override: f32,
    pub capacity_feedback: bool,
    pub max_crowding: f32,
    pub max_cars_per_loco: usize,
    // pesos (menor = melhor)
    pub unreachable_penalty: f32,
    pub congestion_weight: f32,
    pub station_load_weight: f32,
    pub track_length_weight: f32,
    /// Ignorado desde que travessia acima do estoque virou inviavel
    /// (eval::CROSSING_INFEASIBLE); fica pelo indice do vetor.
    pub crossing_over_budget: f32,
    pub unserved_penalty: f32,
    // novos no nucleo nativo
    /// Quantos dias a frente o fitness olha (dia util x fim de semana, estacoes
    /// que vao nascer, crescimento da demanda). 1 = so hoje.
    pub horizon_days: f32,
    /// Peso da urgencia: estacao com o timer de lotacao correndo precisa de trem
    /// logo. Multiplica completude do timer x espera ate o proximo trem.
    pub urgency_weight: f32,
    /// 0 = todas as CPUs (ate 16).
    pub threads: usize,
    /// Busca local (memetico) no melhor individuo a cada tantas geracoes. 0 = nao.
    pub local_search_every: usize,
    /// Encaixar estacoes futuras virtualmente na rede ao avaliar.
    pub plan_future: bool,
    /// Custo (s de tempo percebido) de trocar de trem. O jogo usa 5.
    pub transfer_cost: f32,
    /// Custo de parada estimado pelo A* do jogo (PATH_STATION_LOADING_COST).
    pub path_stop_cost: f32,
    /// Dias em que passageiro sem rota ou sem lugar se acumula na plataforma
    /// (ate a rede ou a frota mudar). 7 = ate o proximo upgrade.
    pub persist_days: f32,
    /// Orcamento de tempo do AG em ms (0 = so o numero de geracoes manda).
    pub time_budget_ms: f64,
    /// Multiplica o custo de mexer na rede atual (desmontar linha, tirar trem;
    /// ver change.rs). 0 = o AG troca a rede inteira por qualquer ganho.
    pub change_weight: f32,
}

impl Default for Config {
    fn default() -> Self {
        Config {
            population: 120,
            generations: 400,
            elitism: 4,
            tournament: 4,
            crossover_rate: 0.75,
            mutation_rate: 0.35,
            mutations_per_genome: 2,
            seed: 0,
            seed_from_current: true,
            stagnation_restart: 80,
            max_lines_override: 0,
            min_stations_per_line: 2,
            max_stations_per_line: 20,
            allow_loops: true,
            demand_scale: 1.0,
            crowd_weight: 0.15,
            speed_override: 0.0,
            capacity_feedback: true,
            max_crowding: 4.0,
            max_cars_per_loco: 2,
            unreachable_penalty: 900.0,
            congestion_weight: 450.0,
            station_load_weight: 260.0,
            track_length_weight: 0.010,
            crossing_over_budget: 1500.0,
            unserved_penalty: 350.0,
            horizon_days: 2.0,
            urgency_weight: 40.0,
            threads: 0,
            local_search_every: 25,
            plan_future: true,
            transfer_cost: 5.0,
            path_stop_cost: 3.5,
            persist_days: 7.0,
            time_budget_ms: 0.0,
            change_weight: 1.0,
        }
    }
}

impl Config {
    pub fn from_vector(v: &[f64]) -> Config {
        let mut c = Config::default();
        let get = |i: usize| v.get(i).copied().filter(|x| x.is_finite());
        macro_rules! set {
            ($i:expr, $field:ident, usize) => {
                if let Some(x) = get($i) {
                    c.$field = x.max(0.0) as usize;
                }
            };
            ($i:expr, $field:ident, bool) => {
                if let Some(x) = get($i) {
                    c.$field = x != 0.0;
                }
            };
            ($i:expr, $field:ident, f32) => {
                if let Some(x) = get($i) {
                    c.$field = x as f32;
                }
            };
            ($i:expr, $field:ident, f64) => {
                if let Some(x) = get($i) {
                    c.$field = x;
                }
            };
        }
        set!(0, population, usize);
        set!(1, generations, usize);
        set!(2, elitism, usize);
        set!(3, tournament, usize);
        set!(4, crossover_rate, f64);
        set!(5, mutation_rate, f64);
        set!(6, mutations_per_genome, usize);
        if let Some(x) = get(7) {
            c.seed = x.max(0.0) as u64;
        }
        set!(8, seed_from_current, bool);
        set!(9, stagnation_restart, usize);
        set!(10, max_lines_override, usize);
        set!(11, min_stations_per_line, usize);
        set!(12, max_stations_per_line, usize);
        set!(13, allow_loops, bool);
        set!(14, demand_scale, f32);
        set!(15, crowd_weight, f32);
        set!(16, speed_override, f32);
        set!(17, capacity_feedback, bool);
        set!(18, max_crowding, f32);
        set!(19, max_cars_per_loco, usize);
        set!(20, unreachable_penalty, f32);
        set!(21, congestion_weight, f32);
        set!(22, station_load_weight, f32);
        set!(23, track_length_weight, f32);
        set!(24, crossing_over_budget, f32);
        set!(25, unserved_penalty, f32);
        set!(26, horizon_days, f32);
        set!(27, urgency_weight, f32);
        set!(28, threads, usize);
        set!(29, local_search_every, usize);
        set!(30, plan_future, bool);
        set!(31, transfer_cost, f32);
        set!(32, path_stop_cost, f32);
        set!(33, persist_days, f32);
        set!(34, time_budget_ms, f64);
        set!(35, change_weight, f32);
        c.sanitize();
        c
    }

    pub fn sanitize(&mut self) {
        self.population = self.population.clamp(4, 5000);
        self.generations = self.generations.clamp(1, 1_000_000);
        self.elitism = self.elitism.min(self.population - 1);
        self.tournament = self.tournament.max(2);
        self.min_stations_per_line = self.min_stations_per_line.max(2);
        self.max_stations_per_line = self.max_stations_per_line.max(self.min_stations_per_line);
        self.max_cars_per_loco = self.max_cars_per_loco.max(1);
        self.horizon_days = self.horizon_days.clamp(0.25, 14.0);
        self.max_crowding = self.max_crowding.max(1.0);
        self.demand_scale = self.demand_scale.max(0.0);
        self.persist_days = self.persist_days.clamp(0.5, 28.0);
        self.change_weight = self.change_weight.clamp(0.0, 100.0);
    }

    pub fn thread_count(&self) -> usize {
        if self.threads > 0 {
            return self.threads.min(64);
        }
        std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4).clamp(1, 16)
    }
}
