//! Gerador pseudoaleatorio proprio (xoshiro256++). Sem dependencia externa para
//! o crate compilar igual em Linux x64 e Windows x86, e para a semente do C#
//! reproduzir exatamente a mesma execucao.

#[derive(Clone)]
pub struct Rng {
    s: [u64; 4],
}

impl Rng {
    pub fn new(seed: u64) -> Self {
        // SplitMix64 espalha a semente pelos 256 bits de estado
        let mut z = seed.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut s = [0u64; 4];
        for v in s.iter_mut() {
            z = z.wrapping_add(0x9E37_79B9_7F4A_7C15);
            let mut x = z;
            x = (x ^ (x >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
            x = (x ^ (x >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
            *v = x ^ (x >> 31);
        }
        Rng { s }
    }

    #[inline]
    pub fn next_u64(&mut self) -> u64 {
        let r = self.s[0]
            .wrapping_add(self.s[3])
            .rotate_left(23)
            .wrapping_add(self.s[0]);
        let t = self.s[1] << 17;
        self.s[2] ^= self.s[0];
        self.s[3] ^= self.s[1];
        self.s[1] ^= self.s[2];
        self.s[0] ^= self.s[3];
        self.s[2] ^= t;
        self.s[3] = self.s[3].rotate_left(45);
        r
    }

    /// Inteiro uniforme em [0, n). n = 0 devolve 0.
    #[inline]
    pub fn below(&mut self, n: usize) -> usize {
        if n <= 1 {
            return 0;
        }
        ((self.next_u64() >> 32) * n as u64 >> 32) as usize
    }

    /// Uniforme em [0, 1).
    #[inline]
    pub fn f64(&mut self) -> f64 {
        (self.next_u64() >> 11) as f64 * (1.0 / (1u64 << 53) as f64)
    }

    #[inline]
    pub fn chance(&mut self, p: f64) -> bool {
        self.f64() < p
    }

    /// Filho independente, para dar um gerador proprio a cada thread.
    pub fn fork(&mut self) -> Rng {
        Rng::new(self.next_u64())
    }
}
