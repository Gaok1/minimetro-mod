using System;
using System.Collections.Generic;
using MiniMetroGA.Core;

namespace MiniMetroGA.Ga
{
    /// <summary>
    /// A UI e o self-test falam so com isto. O otimizador e o nucleo nativo em
    /// Rust (native/mmopt); o AG em C# antigo foi removido, porque o modelo dele
    /// tinha ficado para tras (sem sentido de trem, sem fila como distribuicao,
    /// sem custo de mexer na rede). Sem a biblioteca, o mod avisa e nao otimiza.
    /// </summary>
    public sealed class EngineHost : IDisposable
    {
        private readonly NativeEngine _native = new NativeEngine();
        private bool _started;

        public bool IsNative => _started;

        public string EngineName
        {
            get
            {
                int t = _native.Threads;
                return t > 0 ? "Rust nativo, " + t + " threads" : "Rust nativo";
            }
        }

        /// <summary>Por que o nucleo nao rodou (null se rodou).</summary>
        public string NativeNote { get; private set; }

        /// <summary>O que esta rodando agora: AG normal ou recomendacao de upgrade.</summary>
        public bool IsUpgradeRun { get; private set; }

        public void Start(Snapshot snap, GaConfig cfg)
        {
            Stop();
            IsUpgradeRun = false;
            Launch(() => _native.Start(snap, cfg));
        }

        /// <summary>
        /// Recomenda o upgrade: roda o AG na base e em cada opcao (AssetType,
        /// quantidade). Resultado em <see cref="UpgradeResult"/> quando terminar.
        /// </summary>
        public void StartUpgrade(Snapshot snap, GaConfig cfg, List<KeyValuePair<AssetType, int>> options)
        {
            Stop();
            IsUpgradeRun = true;
            Launch(() => _native.StartUpgrade(snap, cfg, options));
        }

        private void Launch(Action start)
        {
            _started = false;
            NativeNote = null;
            try
            {
                start();
                _started = true;
                Log.Info(string.Format("AG nativo iniciado: blob {0} bytes{1}.", _native.BlobBytes,
                    _native.ProblemDumpPath != null ? ", problema salvo em " + _native.ProblemDumpPath : ""));
            }
            catch (Exception e)
            {
                NativeNote = e.Message;
                Log.Warn("Otimizador nativo nao iniciou: " + e.Message);
            }
        }

        public void Stop() => _native.Stop();

        public GaState Status => _started ? _native.Status : (NativeNote != null ? GaState.Failed : GaState.Idle);
        public bool IsRunning => Status == GaState.Running;
        public int Generation => _native.Generation;
        public int TotalGenerations => _native.TotalGenerations;

        /// <summary>
        /// Fracao da busca ja feita: geracoes ou orcamento de tempo, o que estiver
        /// mais perto do fim.
        /// </summary>
        public float Progress
        {
            get
            {
                if (Status == GaState.Done) return 1f;
                float byGen = TotalGenerations > 0 ? Generation / (float)TotalGenerations : 0f;
                var cfg = ConfigRef;
                float byTime = !IsUpgradeRun && cfg != null && cfg.TimeBudgetMs > 0 ? ElapsedMs / (float)cfg.TimeBudgetMs : 0f;
                return Math.Min(1f, Math.Max(byGen, byTime));
            }
        }
        public double BestFitness => _native.BestFitness;
        public double AvgFitness => _native.AvgFitness;
        public int StagnantFor => _native.StagnantFor;
        public long ElapsedMs => _native.ElapsedMs;
        public int EvaluationsDone => _native.EvaluationsDone;
        public string Error => _started ? _native.Error : NativeNote;
        public bool HasBest => _started && !IsUpgradeRun && _native.HasBest;
        public Snapshot SnapshotRef => _native.SnapshotRef;
        public GaConfig ConfigRef => _native.ConfigRef;
        public double MicrosPerEval => _native.MicrosPerEval;
        public long CacheHits => _native.CacheHits;

        /// <summary>Fitness da rede que o jogador tem agora, no mesmo modelo.</summary>
        public double CurrentNetworkFitness => _native.CurrentNetworkFitness;

        public Genome BestClone() => HasBest ? _native.BestClone() : null;

        /// <summary>Previsao do modelo para um genoma (ver NativeEngine.Explain).</summary>
        public double[] Explain(Genome g, GaConfig cfg) => _started ? _native.Explain(g, cfg) : null;

        /// <summary>Resultado da recomendacao: a base primeiro, depois cada opcao.</summary>
        public List<NativeEngine.UpgradeChoice> UpgradeResult() => _started ? _native.UpgradeResult() : null;

        /// <summary>
        /// Vale mexer na rede? Nao se o melhor ja e a rede atual, nem se o ganho
        /// sobre ela (no mesmo modelo, ja descontado o custo de mexer) for menor
        /// que o minimo.
        /// </summary>
        public bool WorthApplying(Genome best, float minGain, out string why)
        {
            var snap = SnapshotRef;
            if (best == null) { why = "sem solucao"; return false; }
            if (best.SameNetwork(snap)) { why = "a melhor rede ja e a atual"; return false; }
            double cur = CurrentNetworkFitness;
            if (!double.IsNaN(cur) && cur < double.MaxValue * 0.5 && cur > 0)
            {
                double gain = 1.0 - best.Fitness / cur;
                if (gain < minGain)
                {
                    why = string.Format("ganho de {0:P1} abaixo do minimo de {1:P0}", gain, minGain);
                    return false;
                }
            }
            why = null;
            return true;
        }

        public void CopyHistory(List<float> best, List<float> avg) => _native.CopyHistory(best, avg);

        public void Dispose()
        {
            Stop();
            _native.Dispose();
        }
    }
}
