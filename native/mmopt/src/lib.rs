//! mmopt: nucleo nativo do otimizador do MiniMetroGA.
//!
//! Carregado dentro do processo do jogo via P/Invoke (`Ga/NativeEngine.cs`), do
//! mesmo jeito que o proprio jogo chama `libminimetrox`. Sem rede e sem IPC: o
//! mod serializa o problema num blob, o nucleo roda o AG em threads proprias e
//! o C# consulta o progresso todo frame.
//!
//! Regras do ABI:
//!   - so tipos C (ponteiros crus, i32, f64); nada de string gerenciada;
//!   - toda entrada passa por `guard` (catch_unwind): panico vira erro, nunca
//!     derruba o jogo;
//!   - handles sao inteiros; 0 = invalido.

pub mod change;
pub mod config;
pub mod ctx;
pub mod eval;
pub mod ga;
pub mod genome;
pub mod geom;
pub mod ops;
pub mod problem;
pub mod rng;
pub mod upgrade;

use std::collections::HashMap;
use std::sync::atomic::Ordering;
use std::sync::{Arc, Mutex, OnceLock};
use std::thread::JoinHandle;

use config::Config;
use ctx::Ctx;
use ga::Shared;
use genome::{Breakdown, Genome, Route};
use problem::Problem;

/// Versao do ABI. O C# recusa o nucleo se nao bater.
pub const ABI_VERSION: i32 = 3;

struct Handle {
    problem: Problem,
    ctx: Option<Arc<Ctx>>,
    shared: Arc<Shared>,
    thread: Option<JoinHandle<()>>,
    last_error: String,
}

fn registry() -> &'static Mutex<HashMap<i32, Handle>> {
    static R: OnceLock<Mutex<HashMap<i32, Handle>>> = OnceLock::new();
    R.get_or_init(|| Mutex::new(HashMap::new()))
}

fn next_id() -> i32 {
    static N: std::sync::atomic::AtomicI32 = std::sync::atomic::AtomicI32::new(1);
    N.fetch_add(1, Ordering::Relaxed)
}

fn guard<T>(fallback: T, f: impl FnOnce() -> T) -> T {
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(f)).unwrap_or(fallback)
}

unsafe fn write_str(s: &str, out: *mut u8, cap: i32) -> i32 {
    if out.is_null() || cap <= 0 {
        return s.len() as i32;
    }
    let n = s.len().min(cap as usize - 1);
    std::ptr::copy_nonoverlapping(s.as_ptr(), out, n);
    *out.add(n) = 0;
    n as i32
}

unsafe fn slice<'a, T>(p: *const T, n: i32) -> &'a [T] {
    if p.is_null() || n <= 0 {
        &[]
    } else {
        std::slice::from_raw_parts(p, n as usize)
    }
}

#[no_mangle]
pub extern "C" fn mm_version() -> i32 {
    ABI_VERSION
}

/// Cria um handle a partir do blob do problema. Devolve 0 e escreve o erro em
/// `err` se o blob nao parsear.
///
/// # Safety
/// `blob` aponta para `len` bytes validos; `err` para `err_cap` bytes graváveis.
#[no_mangle]
pub unsafe extern "C" fn mm_create(blob: *const u8, len: i32, err: *mut u8, err_cap: i32) -> i32 {
    guard(0, || {
        let bytes = slice(blob, len);
        match Problem::parse(bytes) {
            Ok(problem) => {
                let id = next_id();
                registry().lock().unwrap().insert(
                    id,
                    Handle { problem, ctx: None, shared: Arc::new(Shared::new()), thread: None, last_error: String::new() },
                );
                id
            }
            Err(e) => {
                write_str(&e.0, err, err_cap);
                0
            }
        }
    })
}

/// Inicia (ou reinicia) o AG com o vetor de configuracao. 1 = ok.
///
/// # Safety
/// `cfg` aponta para `n` f64 validos.
#[no_mangle]
pub unsafe extern "C" fn mm_start(h: i32, cfg: *const f64, n: i32) -> i32 {
    guard(0, || {
        stop_inner(h);
        let cfg = Config::from_vector(slice(cfg, n));
        let mut reg = registry().lock().unwrap();
        let Some(hd) = reg.get_mut(&h) else { return 0 };
        let ctx = Arc::new(Ctx::new(hd.problem.clone(), cfg));
        let shared = Arc::new(Shared::new());
        shared.state.store(ga::ST_RUNNING, Ordering::SeqCst);
        let (c2, s2) = (ctx.clone(), shared.clone());
        let th = std::thread::Builder::new()
            .name("mmopt-ga".into())
            .spawn(move || ga::run(c2, s2));
        match th {
            Ok(t) => {
                hd.ctx = Some(ctx);
                hd.shared = shared;
                hd.thread = Some(t);
                1
            }
            Err(e) => {
                hd.last_error = format!("nao consegui criar a thread: {e}");
                0
            }
        }
    })
}

/// Recomendacao de upgrade (ver upgrade.rs), assincrona como o AG: `opts` tem
/// `nopt` pares (AssetType, quantidade). O progresso sai no mm_status e o
/// resultado no mm_upgrade_result. 1 = ok.
///
/// # Safety
/// `cfg` aponta para `n` f64 e `opts` para `2*nopt` i32 validos.
#[no_mangle]
pub unsafe extern "C" fn mm_upgrade_start(h: i32, cfg: *const f64, n: i32, opts: *const i32, nopt: i32) -> i32 {
    guard(0, || {
        stop_inner(h);
        let cfg = Config::from_vector(slice(cfg, n));
        let raw = slice(opts, nopt.max(0) * 2);
        let opts: Vec<(i32, i32)> = raw.chunks(2).map(|c| (c[0], c[1])).collect();
        let mut reg = registry().lock().unwrap();
        let Some(hd) = reg.get_mut(&h) else { return 0 };
        let problem = hd.problem.clone();
        let shared = Arc::new(Shared::new());
        shared.state.store(ga::ST_RUNNING, Ordering::SeqCst);
        let s2 = shared.clone();
        let th = std::thread::Builder::new().name("mmopt-upgrade".into()).spawn(move || {
            let r = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| upgrade::run(&problem, &cfg, &opts, &s2)));
            match r {
                Ok(res) => {
                    let mut inner = s2.inner.lock().unwrap();
                    inner.upgrade = res.iter().map(|c| [c.kind as f64, c.count as f64, c.fitness, c.station as f64]).collect();
                    if let Some(c) = res.iter().skip(1).min_by(|a, b| a.fitness.total_cmp(&b.fitness)) {
                        if let Some(g) = &c.genome {
                            inner.best_fitness = g.fitness;
                            inner.best = Some(g.clone());
                        }
                    }
                    drop(inner);
                    let st = if s2.cancel.load(Ordering::Relaxed) { ga::ST_CANCELLED } else { ga::ST_DONE };
                    s2.state.store(st, Ordering::SeqCst);
                }
                Err(_) => {
                    s2.inner.lock().unwrap().error = "panico na recomendacao de upgrade".into();
                    s2.state.store(ga::ST_FAILED, Ordering::SeqCst);
                }
            }
        });
        match th {
            Ok(t) => {
                hd.ctx = None;
                hd.shared = shared;
                hd.thread = Some(t);
                1
            }
            Err(e) => {
                hd.last_error = format!("nao consegui criar a thread: {e}");
                0
            }
        }
    })
}

/// [linhas, (tipo, quantidade, fitness, estacao)*]; a linha 0 e a base (sem
/// upgrade, tipo 0). Devolve quantos f64 escreveu; negativo = buffer pequeno.
///
/// # Safety
/// `out` aponta para `cap` f64 graváveis.
#[no_mangle]
pub unsafe extern "C" fn mm_upgrade_result(h: i32, out: *mut f64, cap: i32) -> i32 {
    guard(0, || {
        let reg = registry().lock().unwrap();
        let Some(hd) = reg.get(&h) else { return 0 };
        let inner = hd.shared.inner.lock().unwrap();
        let mut v = vec![inner.upgrade.len() as f64];
        for row in &inner.upgrade {
            v.extend_from_slice(row);
        }
        if out.is_null() || (cap as usize) < v.len() {
            return -(v.len() as i32);
        }
        std::ptr::copy_nonoverlapping(v.as_ptr(), out, v.len());
        v.len() as i32
    })
}

fn stop_inner(h: i32) {
    let th = {
        let mut reg = registry().lock().unwrap();
        let Some(hd) = reg.get_mut(&h) else { return };
        hd.shared.cancel.store(true, Ordering::SeqCst);
        hd.thread.take()
    };
    if let Some(t) = th {
        let _ = t.join();
    }
}

/// Pede para parar e espera a thread terminar (a geracao corrente acaba antes).
#[no_mangle]
pub extern "C" fn mm_stop(h: i32) {
    guard((), || stop_inner(h))
}

#[no_mangle]
pub extern "C" fn mm_destroy(h: i32) {
    guard((), || {
        stop_inner(h);
        registry().lock().unwrap().remove(&h);
    })
}

/// [estado, geracao, total, melhor, media, pior, estagnado, ms, avaliacoes,
///  cache, threads, us_por_avaliacao, fitness_da_rede_atual]
///
/// # Safety
/// `out` aponta para `n` f64 graváveis.
#[no_mangle]
pub unsafe extern "C" fn mm_status(h: i32, out: *mut f64, n: i32) -> i32 {
    guard(0, || {
        if out.is_null() || n <= 0 {
            return 0;
        }
        let reg = registry().lock().unwrap();
        let Some(hd) = reg.get(&h) else { return 0 };
        let s = &hd.shared;
        let inner = s.inner.lock().unwrap();
        let evals = s.evals.load(Ordering::Relaxed);
        let us = if evals > 0 { s.eval_ns.load(Ordering::Relaxed) as f64 / evals as f64 / 1000.0 } else { 0.0 };
        let vals = [
            s.state.load(Ordering::SeqCst) as f64,
            s.generation.load(Ordering::Relaxed) as f64,
            s.total_generations.load(Ordering::Relaxed) as f64,
            inner.best_fitness,
            inner.avg,
            inner.worst,
            inner.stagnant as f64,
            s.elapsed_ms.load(Ordering::Relaxed) as f64,
            evals as f64,
            s.cache_hits.load(Ordering::Relaxed) as f64,
            s.threads.load(Ordering::Relaxed) as f64,
            us,
            inner.initial_fitness,
        ];
        let k = vals.len().min(n as usize);
        std::ptr::copy_nonoverlapping(vals.as_ptr(), out, k);
        k as i32
    })
}

/// [viagem, sem rota, lotacao de trem, lotacao de estacao, trilho, travessia,
///  estacao fora, urgencia, pares sem rota, estacoes fora, travessias usadas,
///  comprimento, pior trecho, viagem media, cruzamentos, FITNESS, mudanca,
///  linhas desmontadas, trens tirados]
pub const BREAKDOWN_LEN: usize = 19;

fn breakdown_vec(bd: &Breakdown, fitness: f64) -> [f64; BREAKDOWN_LEN] {
    [
        bd.travel,
        bd.unreachable,
        bd.congestion,
        bd.station_load,
        bd.track,
        bd.crossing,
        bd.unserved,
        bd.urgency,
        bd.unreachable_pairs as f64,
        bd.unserved_stations as f64,
        bd.crossings_used as f64,
        bd.track_length as f64,
        bd.worst_line_util as f64,
        bd.avg_travel_time as f64,
        bd.line_crossings as f64,
        fitness,
        bd.change,
        bd.rebuilt_lines as f64,
        bd.moved_trains as f64,
    ]
}

/// Copia o melhor genoma (formato de `Genome::encode`) e o breakdown dele.
/// Devolve o tamanho do genoma; negativo = buffer pequeno (valor = -necessario);
/// 0 = ainda nao ha melhor.
///
/// # Safety
/// `out` aponta para `cap` i32 e `bd` para `bd_n` f64 graváveis.
#[no_mangle]
pub unsafe extern "C" fn mm_best(h: i32, out: *mut i32, cap: i32, bd: *mut f64, bd_n: i32) -> i32 {
    guard(0, || {
        let reg = registry().lock().unwrap();
        let Some(hd) = reg.get(&h) else { return 0 };
        let inner = hd.shared.inner.lock().unwrap();
        let Some(best) = inner.best.as_ref() else { return 0 };
        let mut enc = Vec::new();
        best.encode(&mut enc);
        if (cap as usize) < enc.len() || out.is_null() {
            return -(enc.len() as i32);
        }
        std::ptr::copy_nonoverlapping(enc.as_ptr(), out, enc.len());
        if !bd.is_null() && bd_n > 0 {
            let v = breakdown_vec(&best.bd, best.fitness);
            std::ptr::copy_nonoverlapping(v.as_ptr(), bd, v.len().min(bd_n as usize));
        }
        enc.len() as i32
    })
}

/// Copia as ultimas `cap` entradas do historico. Devolve quantas copiou.
///
/// # Safety
/// `best` e `avg` apontam para `cap` f32 graváveis cada.
#[no_mangle]
pub unsafe extern "C" fn mm_history(h: i32, best: *mut f32, avg: *mut f32, cap: i32) -> i32 {
    guard(0, || {
        if best.is_null() || avg.is_null() || cap <= 0 {
            return 0;
        }
        let reg = registry().lock().unwrap();
        let Some(hd) = reg.get(&h) else { return 0 };
        let inner = hd.shared.inner.lock().unwrap();
        let n = inner.hist_best.len().min(cap as usize);
        let from = inner.hist_best.len() - n;
        std::ptr::copy_nonoverlapping(inner.hist_best[from..].as_ptr(), best, n);
        std::ptr::copy_nonoverlapping(inner.hist_avg[from..].as_ptr(), avg, n);
        n as i32
    })
}

/// Mensagem do ultimo erro (panico no AG ou falha ao iniciar).
///
/// # Safety
/// `out` aponta para `cap` bytes graváveis.
#[no_mangle]
pub unsafe extern "C" fn mm_error(h: i32, out: *mut u8, cap: i32) -> i32 {
    guard(0, || {
        let reg = registry().lock().unwrap();
        let Some(hd) = reg.get(&h) else { return write_str("handle invalido", out, cap) };
        let inner = hd.shared.inner.lock().unwrap();
        let msg = if !inner.error.is_empty() { inner.error.clone() } else { hd.last_error.clone() };
        write_str(&msg, out, cap)
    })
}

/// Avalia um genoma arbitrario (ex.: a rede atual) com a configuracao dada.
/// Devolve o fitness (ou NaN se falhar) e preenche o breakdown.
///
/// # Safety
/// `genome` aponta para `len` i32; `cfg` para `n` f64; `bd` para `bd_n` f64.
#[no_mangle]
pub unsafe extern "C" fn mm_evaluate(
    h: i32,
    genome: *const i32,
    len: i32,
    cfg: *const f64,
    n: i32,
    bd: *mut f64,
    bd_n: i32,
) -> f64 {
    guard(f64::NAN, || {
        let problem = {
            let reg = registry().lock().unwrap();
            let Some(hd) = reg.get(&h) else { return f64::NAN };
            hd.problem.clone()
        };
        let ctx = Ctx::new(problem, Config::from_vector(slice(cfg, n)));
        let Some(mut g) = decode(slice(genome, len), ctx.na()) else { return f64::NAN };
        let mut ev = eval::Evaluator::new(&ctx);
        let f = ev.evaluate(&ctx, &mut g);
        if !bd.is_null() && bd_n > 0 {
            let v = breakdown_vec(&g.bd, f);
            std::ptr::copy_nonoverlapping(v.as_ptr(), bd, v.len().min(bd_n as usize));
        }
        f
    })
}

pub const EXPLAIN_HEADER: usize = 8;
pub const EXPLAIN_LINE: usize = 18;
pub const EXPLAIN_STATION: usize = 18;

/// Achata o `Explain` num vetor de f64 (o C# le por indice):
///   cabecalho [versao, linhas, estacoes, entregue/s, demanda/s, viagem media,
///              janela s, fitness]
///   por linha [rota, paradas, loop, trens, lugares, trens sentido 0, sentido 1,
///              trecho s, ciclo 0, ciclo 1, headway 0, headway 1 (-1 = sem trem),
///              pior trecho 0, pior trecho 1, a bordo/trem, paradas por passada,
///              comprimento de uma passada, cruzamentos de trilho]
///   por estacao ativa [fila regime, dp, P(>cap), excesso, embarca/s, nasce/s,
///              sem rota/s, cresce por lotacao/s, lugares livres/s, fila atual
///              que escoa, fila atual presa, capacidade, fila media na janela,
///              fracao acima da cap na janela, cresce total/s, fila media e
///              fracao acima da cap nos ultimos 2/3 da janela, 0]
pub fn explain_vec(ex: &eval::Explain, fitness: f64) -> Vec<f64> {
    let fin = |v: f32| if v.is_finite() { v as f64 } else { -1.0 };
    let mut v = Vec::with_capacity(EXPLAIN_HEADER + ex.lines.len() * EXPLAIN_LINE + ex.stations.len() * EXPLAIN_STATION);
    v.extend_from_slice(&[
        1.0,
        ex.lines.len() as f64,
        ex.stations.len() as f64,
        ex.delivered_rate as f64,
        ex.demand_rate as f64,
        ex.avg_travel_time as f64,
        ex.window as f64,
        fitness,
    ]);
    for l in &ex.lines {
        v.extend_from_slice(&[
            l.route as f64,
            l.stops as f64,
            l.looped as u8 as f64,
            l.trains as f64,
            l.seats as f64,
            l.n_dir[0] as f64,
            l.n_dir[1] as f64,
            l.run as f64,
            l.cycle[0] as f64,
            l.cycle[1] as f64,
            fin(l.headway[0]),
            fin(l.headway[1]),
            l.max_util[0] as f64,
            l.max_util[1] as f64,
            l.mean_onboard as f64,
            l.stops_made as f64,
            l.length as f64,
            l.crossings as f64,
        ]);
    }
    for st in &ex.stations {
        v.extend_from_slice(&[
            st.mean_queue as f64,
            st.sd_queue as f64,
            st.p_over as f64,
            st.excess as f64,
            st.board_rate as f64,
            st.spawn_rate as f64,
            st.stuck_rate as f64,
            st.growth_cap as f64,
            st.spare as f64,
            st.backlog_drain as f64,
            st.backlog_stay as f64,
            st.capacity as f64,
            st.window_queue as f64,
            st.window_p_over as f64,
            st.growth as f64,
            st.late_queue as f64,
            st.late_p_over as f64,
            0.0,
        ]);
    }
    v
}

/// Detalhe do modelo para um genoma (ver `explain_vec`), com a configuracao
/// dada. Serve para comparar previsto x medido no jogo. Devolve quantos f64
/// escreveu; negativo = buffer pequeno (valor = -necessario); 0 = falha.
///
/// # Safety
/// `genome` aponta para `len` i32; `cfg` para `n` f64; `out` para `cap` f64.
#[no_mangle]
pub unsafe extern "C" fn mm_explain(
    h: i32,
    genome: *const i32,
    len: i32,
    cfg: *const f64,
    n: i32,
    out: *mut f64,
    cap: i32,
) -> i32 {
    guard(0, || {
        let problem = {
            let reg = registry().lock().unwrap();
            let Some(hd) = reg.get(&h) else { return 0 };
            hd.problem.clone()
        };
        let ctx = Ctx::new(problem, Config::from_vector(slice(cfg, n)));
        let Some(mut g) = decode(slice(genome, len), ctx.na()) else { return 0 };
        let mut ev = eval::Evaluator::new(&ctx);
        ev.explain = Some(eval::Explain::default());
        let f = ev.evaluate(&ctx, &mut g);
        let Some(ex) = ev.explain.take() else { return 0 };
        let v = explain_vec(&ex, f);
        if out.is_null() || (cap as usize) < v.len() {
            return -(v.len() as i32);
        }
        std::ptr::copy_nonoverlapping(v.as_ptr(), out, v.len());
        v.len() as i32
    })
}

/// Inverso de `Genome::encode`. Descarta estacoes fora do intervalo ativo.
pub fn decode(v: &[i32], na: usize) -> Option<Genome> {
    let mut g = Genome::new();
    let mut p = 0usize;
    let routes = *v.first()? as usize;
    p += 1;
    for _ in 0..routes {
        let looped = *v.get(p)? != 0;
        let locos = (*v.get(p + 1)?).max(0) as u16;
        let cars = (*v.get(p + 2)?).max(0) as u16;
        let rev = (*v.get(p + 3)?).max(0) as u16;
        let len = (*v.get(p + 4)?).max(0) as usize;
        p += 5;
        let stops: Vec<u16> = v.get(p..p + len)?.iter().filter(|&&s| s >= 0 && (s as usize) < na).map(|&s| s as u16).collect();
        p += len;
        let rev = if looped { rev.min(locos) } else { 0 };
        g.routes.push(Route { stops, looped, locos, cars, rev });
    }
    Some(g)
}
