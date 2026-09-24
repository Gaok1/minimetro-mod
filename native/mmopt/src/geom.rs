//! Geometria do trilho. Ver vault/Dossie §8.
//!
//! O jogo liga duas estacoes com um trecho reto e um diagonal (octilinear). Um
//! link inclinado tem duas formas: diagonal primeiro ou reta primeiro. Entre elas
//! o LinkBuilder prefere a que nao cruza agua e depois a que cruza menos trilhos,
//! entao "precisa de tunel" = as duas formas cruzam a costa.

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct V2 {
    pub x: f32,
    pub y: f32,
}

impl V2 {
    pub const fn new(x: f32, y: f32) -> V2 {
        V2 { x, y }
    }
    #[inline]
    pub fn sub(self, o: V2) -> V2 {
        V2::new(self.x - o.x, self.y - o.y)
    }
    #[inline]
    pub fn add(self, o: V2) -> V2 {
        V2::new(self.x + o.x, self.y + o.y)
    }
    #[inline]
    pub fn len(self) -> f32 {
        (self.x * self.x + self.y * self.y).sqrt()
    }
}

#[derive(Clone, Copy, Debug)]
pub struct Seg {
    pub a: V2,
    pub b: V2,
}

impl Seg {
    pub fn new(a: V2, b: V2) -> Seg {
        Seg { a, b }
    }
}

const SQRT2: f32 = std::f32::consts::SQRT_2;

/// Comprimento do trilho octilinear entre dois pontos:
/// |dx| + |dy| - (2 - sqrt2) * min(|dx|, |dy|). Ate +8,2% sobre a euclidiana.
#[inline]
pub fn octilinear_len(p: V2, q: V2) -> f32 {
    let dx = (q.x - p.x).abs();
    let dy = (q.y - p.y).abs();
    let m = dx.min(dy);
    dx + dy - (2.0 - SQRT2) * m
}

/// As duas formas possiveis do link p->q, cada uma com ate 2 segmentos.
/// [0] = diagonal primeiro, [1] = reta primeiro. Links alinhados (reto ou 45
/// graus) tem uma forma so, repetida.
pub fn footprints(p: V2, q: V2) -> [[Seg; 2]; 2] {
    let dx = q.x - p.x;
    let dy = q.y - p.y;
    let m = dx.abs().min(dy.abs());
    let diag = V2::new(m * dx.signum(), m * dy.signum());
    let c1 = p.add(diag); // fim da diagonal saindo de p
    let c2 = q.sub(diag); // comeco da diagonal chegando em q
    [
        [Seg::new(p, c1), Seg::new(c1, q)],
        [Seg::new(p, c2), Seg::new(c2, q)],
    ]
}

#[inline]
fn cross(o: V2, a: V2, b: V2) -> f32 {
    (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x)
}

/// Interseccao propria de segmentos (toque em ponta nao conta). Segmento
/// degenerado nunca cruza.
#[inline]
pub fn segs_cross(s: &Seg, t: &Seg) -> bool {
    let d1 = cross(t.a, t.b, s.a);
    let d2 = cross(t.a, t.b, s.b);
    let d3 = cross(s.a, s.b, t.a);
    let d4 = cross(s.a, s.b, t.b);
    ((d1 > 0.0 && d2 < 0.0) || (d1 < 0.0 && d2 > 0.0))
        && ((d3 > 0.0 && d4 < 0.0) || (d3 < 0.0 && d4 > 0.0))
}

/// Costa da agua: a ObstacleHull do jogo e uma lista plana de segmentos. Aqui
/// eles vao para uma grade uniforme, para o teste de travessia nao ser O(costa).
pub struct Hull {
    segs: Vec<Seg>,
    min: V2,
    cell: f32,
    nx: usize,
    ny: usize,
    cells: Vec<Vec<u32>>,
}

impl Hull {
    pub fn new(segs: Vec<Seg>) -> Hull {
        if segs.is_empty() {
            return Hull { segs, min: V2::default(), cell: 1.0, nx: 0, ny: 0, cells: Vec::new() };
        }
        let mut lo = V2::new(f32::MAX, f32::MAX);
        let mut hi = V2::new(f32::MIN, f32::MIN);
        for s in &segs {
            for p in [s.a, s.b] {
                lo.x = lo.x.min(p.x);
                lo.y = lo.y.min(p.y);
                hi.x = hi.x.max(p.x);
                hi.y = hi.y.max(p.y);
            }
        }
        let cell = 64.0f32;
        let nx = (((hi.x - lo.x) / cell) as usize + 1).clamp(1, 512);
        let ny = (((hi.y - lo.y) / cell) as usize + 1).clamp(1, 512);
        let mut cells = vec![Vec::new(); nx * ny];
        for (i, s) in segs.iter().enumerate() {
            let (x0, y0, x1, y1) = Self::range(lo, cell, nx, ny, s);
            for cy in y0..=y1 {
                for cx in x0..=x1 {
                    cells[cy * nx + cx].push(i as u32);
                }
            }
        }
        Hull { segs, min: lo, cell, nx, ny, cells }
    }

    fn range(lo: V2, cell: f32, nx: usize, ny: usize, s: &Seg) -> (usize, usize, usize, usize) {
        let f = |v: f32, o: f32, n: usize| (((v - o) / cell).floor().max(0.0) as usize).min(n - 1);
        let x0 = f(s.a.x.min(s.b.x), lo.x, nx);
        let x1 = f(s.a.x.max(s.b.x), lo.x, nx);
        let y0 = f(s.a.y.min(s.b.y), lo.y, ny);
        let y1 = f(s.a.y.max(s.b.y), lo.y, ny);
        (x0, y0, x1, y1)
    }

    pub fn is_empty(&self) -> bool {
        self.segs.is_empty()
    }

    pub fn crosses(&self, s: &Seg) -> bool {
        if self.segs.is_empty() {
            return false;
        }
        let (x0, y0, x1, y1) = Self::range(self.min, self.cell, self.nx, self.ny, s);
        for cy in y0..=y1 {
            for cx in x0..=x1 {
                for &i in &self.cells[cy * self.nx + cx] {
                    if segs_cross(s, &self.segs[i as usize]) {
                        return true;
                    }
                }
            }
        }
        false
    }

    /// O link p->q gasta travessia se TODA forma octilinear cruza a costa.
    pub fn link_needs_crossing(&self, p: V2, q: V2) -> bool {
        if self.segs.is_empty() {
            return false;
        }
        footprints(p, q)
            .iter()
            .all(|fp| fp.iter().any(|s| self.crosses(s)))
    }
}

/// Custo extra, em segundos, de passar por um cruzamento de trilhos: dentro de
/// +-30 unidades da interseccao a velocidade maxima cai para 40%, com frenagem
/// antes e aceleracao depois (Train.UpdatePosition). ~1,33 s no trem padrao.
pub fn line_crossing_delay(v: f32, acc: f32, dec: f32) -> f32 {
    if v <= 0.0 || acc <= 0.0 || dec <= 0.0 {
        return 0.0;
    }
    let slow = 0.4 * v;
    let window = 60.0;
    let t_dec = (v - slow) / dec;
    let d_dec = (v * v - slow * slow) / (2.0 * dec);
    let t_acc = (v - slow) / acc;
    let d_acc = (v * v - slow * slow) / (2.0 * acc);
    let t_slow = window / slow;
    let total_t = t_dec + t_slow + t_acc;
    let total_d = d_dec + window + d_acc;
    (total_t - total_d / v).max(0.0)
}

/// Tempo perdido por parar numa estacao (frear ate 0 e acelerar de volta), em
/// relacao a passar direto: v/(2a) + v/(2d). ~1,77 s no trem padrao.
pub fn stop_loss(v: f32, acc: f32, dec: f32) -> f32 {
    if v <= 0.0 || acc <= 0.0 || dec <= 0.0 {
        return 0.0;
    }
    v / (2.0 * acc) + v / (2.0 * dec)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn octilinear_matches_axis_and_diagonal() {
        let o = V2::new(0.0, 0.0);
        assert!((octilinear_len(o, V2::new(100.0, 0.0)) - 100.0).abs() < 1e-3);
        assert!((octilinear_len(o, V2::new(100.0, 100.0)) - 141.421).abs() < 1e-2);
        // pior caso ~22,5 graus: +8,2%
        let q = V2::new(100.0, 41.421);
        let r = octilinear_len(o, q) / q.len();
        assert!(r > 1.08 && r < 1.085, "{r}");
    }

    #[test]
    fn crossing_delay_default_train() {
        let d = line_crossing_delay(130.0, 55.0, 110.0);
        assert!((d - 1.33).abs() < 0.02, "{d}");
        let s = stop_loss(130.0, 55.0, 110.0);
        assert!((s - 1.772).abs() < 0.01, "{s}");
    }

    #[test]
    fn hull_needs_both_footprints() {
        // costa curta em x=50, de y=-10 a y=10. Link (0,0)->(100,30): a forma
        // "reta primeiro" passa em y=0 e cruza; a "diagonal primeiro" passa em
        // y=30 e desvia. O jogo escolhe a que desvia -> sem tunel.
        let hull = Hull::new(vec![Seg::new(V2::new(50.0, -10.0), V2::new(50.0, 10.0))]);
        let fp = footprints(V2::new(0.0, 0.0), V2::new(100.0, 30.0));
        assert!(!fp[0].iter().any(|s| hull.crosses(s)));
        assert!(fp[1].iter().any(|s| hull.crosses(s)));
        assert!(!hull.link_needs_crossing(V2::new(0.0, 0.0), V2::new(100.0, 30.0)));
        // costa longa: toda forma cruza
        let wall = Hull::new(vec![Seg::new(V2::new(50.0, -500.0), V2::new(50.0, 500.0))]);
        assert!(wall.link_needs_crossing(V2::new(0.0, 0.0), V2::new(100.0, 30.0)));
    }
}
