using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using MiniMetroGA.Core;
using UnityEngine;

namespace MiniMetroGA.Ga
{
    /// <summary>
    /// Ponte para o nucleo nativo em Rust (native/mmopt). O AG inteiro roda la,
    /// em threads proprias; aqui so serializamos o problema, disparamos, e
    /// consultamos o progresso todo frame.
    ///
    /// Carregado dentro do processo por P/Invoke - o mesmo mecanismo que o jogo
    /// usa para a libminimetrox - entao nao ha rede nem copia por geracao. Tudo que
    /// cruza a fronteira e ponteiro cru para array pinado (GCHandle), como no
    /// NativeExtensions do jogo: nada de marshalling de string ou de array, que
    /// dependeria de partes do mscorlib que o stripping pode ter levado.
    ///
    /// Se a biblioteca nao existir (Windows sem o build nativo, por exemplo), o
    /// mod avisa e nao otimiza: o AG em C# antigo foi removido.
    /// </summary>
    public sealed class NativeEngine : IDisposable
    {
        private const string Lib = "mmopt";
        public const int AbiVersion = 3;

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mm_version();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mm_create(IntPtr blob, int len, IntPtr err, int errCap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mm_start(int h, IntPtr cfg, int n);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void mm_stop(int h);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void mm_destroy(int h);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mm_status(int h, IntPtr outp, int n);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mm_best(int h, IntPtr outp, int cap, IntPtr bd, int bdN);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mm_history(int h, IntPtr best, IntPtr avg, int cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mm_error(int h, IntPtr outp, int cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mm_explain(int h, IntPtr genome, int len, IntPtr cfg, int n, IntPtr outp, int cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mm_upgrade_start(int h, IntPtr cfg, int n, IntPtr opts, int nopt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mm_upgrade_result(int h, IntPtr outp, int cap);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr dlopen(IntPtr path, int flags);
        [DllImport("kernel32", CallingConvention = CallingConvention.StdCall)] private static extern IntPtr LoadLibraryW(IntPtr path);

        // ------------------------------------------------------------------
        // carregamento
        // ------------------------------------------------------------------
        private static bool _probed;
        private static bool _available;
        public static string LoadError { get; private set; }
        public static string LibraryPath { get; private set; }

        /// <summary>Carrega a biblioteca uma vez e confere a versao do ABI.</summary>
        public static bool TryLoad()
        {
            if (_probed) return _available;
            _probed = true;
            try
            {
                Preload();
                int v = mm_version();
                if (v != AbiVersion)
                {
                    LoadError = "ABI " + v + " no nucleo, o mod espera " + AbiVersion + " (recompile os dois)";
                    Log.Warn("NativeEngine: " + LoadError);
                    return _available = false;
                }
                Log.Info("NativeEngine: nucleo Rust carregado (" + (LibraryPath ?? Lib) + ").");
                return _available = true;
            }
            catch (Exception e)
            {
                LoadError = e.GetType().Name + ": " + e.Message;
                Log.Warn("NativeEngine: nucleo nativo indisponivel, usando o AG em C#. " + LoadError);
                return _available = false;
            }
        }

        /// <summary>
        /// O Mono procura "mmopt" no caminho de busca do sistema, nao ao lado da DLL
        /// do mod. Carregamos pelo caminho completo antes: no Linux a biblioteca tem
        /// SONAME libmmopt.so e o dlopen do Mono acha a ja carregada; no Windows o
        /// LoadLibrary casa pelo nome do modulo.
        /// </summary>
        private static void Preload()
        {
            string bin = Path.GetDirectoryName(typeof(NativeEngine).Assembly.Location);
            bool windows = Application.platform == RuntimePlatform.WindowsPlayer
                        || Application.platform == RuntimePlatform.WindowsEditor;
            string path = Path.Combine(bin, windows ? "mmopt.dll" : "libmmopt.so");
            if (!File.Exists(path))
                throw new FileNotFoundException("biblioteca nativa nao encontrada em " + path);
            LibraryPath = path;

            if (windows)
            {
                var chars = new char[path.Length + 1];
                for (int i = 0; i < path.Length; i++) chars[i] = path[i];
                var h = GCHandle.Alloc(chars, GCHandleType.Pinned);
                try
                {
                    if (LoadLibraryW(h.AddrOfPinnedObject()) == IntPtr.Zero)
                        throw new Exception("LoadLibraryW falhou para " + path);
                }
                finally { h.Free(); }
            }
            else
            {
                var bytes = Utf8Z(path);
                var h = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try
                {
                    const int RTLD_NOW = 2, RTLD_GLOBAL = 0x100;
                    if (dlopen(h.AddrOfPinnedObject(), RTLD_NOW | RTLD_GLOBAL) == IntPtr.Zero)
                        throw new Exception("dlopen falhou para " + path);
                }
                finally { h.Free(); }
            }
        }

        private static byte[] Utf8Z(string s)
        {
            var list = new List<byte>(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                int c = s[i];
                if (c < 0x80) list.Add((byte)c);
                else if (c < 0x800) { list.Add((byte)(0xC0 | (c >> 6))); list.Add((byte)(0x80 | (c & 0x3F))); }
                else { list.Add((byte)(0xE0 | (c >> 12))); list.Add((byte)(0x80 | ((c >> 6) & 0x3F))); list.Add((byte)(0x80 | (c & 0x3F))); }
            }
            list.Add(0);
            return list.ToArray();
        }

        // ------------------------------------------------------------------
        // estado
        // ------------------------------------------------------------------
        private int _h;
        private Snapshot _snap;
        private GaConfig _cfg;
        private readonly double[] _status = new double[13];
        private int _statusFrame = -1;
        private int[] _genomeBuf = new int[1024];
        private readonly double[] _bdBuf = new double[19];
        private Genome _bestCache;
        private double _bestCacheFitness = double.NaN;
        private float[] _histBest = new float[4000];
        private float[] _histAvg = new float[4000];

        public Snapshot SnapshotRef => _snap;
        public GaConfig ConfigRef => _cfg;
        public string ProblemDumpPath { get; private set; }
        public int BlobBytes { get; private set; }

        public void Start(Snapshot snap, GaConfig cfg)
        {
            Create(snap, cfg);
            var vec = ConfigVector(cfg);
            var hc = GCHandle.Alloc(vec, GCHandleType.Pinned);
            int ok;
            try { ok = mm_start(_h, hc.AddrOfPinnedObject(), vec.Length); }
            finally { hc.Free(); }
            if (ok != 1) throw new Exception("mm_start falhou: " + LastError());
        }

        /// <summary>
        /// Recomendacao de upgrade (native/mmopt/src/upgrade.rs): a base e cada
        /// opcao (AssetType, quantidade) otimizadas com o mesmo orcamento.
        /// </summary>
        public void StartUpgrade(Snapshot snap, GaConfig cfg, List<KeyValuePair<AssetType, int>> options)
        {
            Create(snap, cfg);
            var vec = ConfigVector(cfg);
            var opts = new int[options.Count * 2];
            for (int i = 0; i < options.Count; i++)
            {
                opts[2 * i] = (int)options[i].Key;
                opts[2 * i + 1] = options[i].Value;
            }
            var hc = GCHandle.Alloc(vec, GCHandleType.Pinned);
            var ho = GCHandle.Alloc(opts, GCHandleType.Pinned);
            int ok;
            try { ok = mm_upgrade_start(_h, hc.AddrOfPinnedObject(), vec.Length, ho.AddrOfPinnedObject(), options.Count); }
            finally { hc.Free(); ho.Free(); }
            if (ok != 1) throw new Exception("mm_upgrade_start falhou: " + LastError());
        }

        public struct UpgradeChoice
        {
            public AssetType Type;      // None = base, sem upgrade
            public int Count;
            public double Fitness;
            public int Station;         // interchange: onde por (-1 = nao se aplica)
        }

        /// <summary>Base primeiro, depois as opcoes na ordem pedida. Null se nao ha resultado.</summary>
        public List<UpgradeChoice> UpgradeResult()
        {
            if (_h == 0) return null;
            var buf = new double[64];
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var hb = GCHandle.Alloc(buf, GCHandleType.Pinned);
                int n;
                try { n = mm_upgrade_result(_h, hb.AddrOfPinnedObject(), buf.Length); }
                finally { hb.Free(); }
                if (n < 0) { buf = new double[-n + 8]; continue; }
                if (n < 1) return null;
                int rows = (int)buf[0];
                var list = new List<UpgradeChoice>(rows);
                for (int i = 0; i < rows && 1 + 4 * i + 3 < n; i++)
                {
                    list.Add(new UpgradeChoice
                    {
                        Type = (AssetType)(int)buf[1 + 4 * i],
                        Count = (int)buf[2 + 4 * i],
                        Fitness = buf[3 + 4 * i],
                        Station = (int)buf[4 + 4 * i],
                    });
                }
                return list.Count > 0 ? list : null;
            }
            return null;
        }

        private void Create(Snapshot snap, GaConfig cfg)
        {
            Dispose();
            if (!TryLoad()) throw new Exception(LoadError ?? "nucleo nativo indisponivel");

            _snap = snap;
            _cfg = cfg;
            _bestCache = null;
            _bestCacheFitness = double.NaN;
            _statusFrame = -1;

            byte[] blob = ProblemExport.Build(snap);
            BlobBytes = blob.Length;
            ProblemDumpPath = cfg.DumpProblem ? ProblemExport.Dump(blob) : null;

            var err = new byte[512];
            var hb = GCHandle.Alloc(blob, GCHandleType.Pinned);
            var he = GCHandle.Alloc(err, GCHandleType.Pinned);
            try
            {
                _h = mm_create(hb.AddrOfPinnedObject(), blob.Length, he.AddrOfPinnedObject(), err.Length);
            }
            finally { hb.Free(); he.Free(); }
            if (_h == 0) throw new Exception("o nucleo recusou o problema: " + Ascii(err));
        }

        /// <summary>
        /// Vetor de configuracao na ordem de native/mmopt/src/config.rs
        /// (Config::from_vector). Indice novo vai sempre no fim.
        /// </summary>
        public static double[] ConfigVector(GaConfig c)
        {
            return new double[]
            {
                c.PopulationSize, c.Generations, c.Elitism, c.TournamentSize,
                c.CrossoverRate, c.MutationRate, c.MutationsPerGenome,
                c.Seed, c.SeedFromCurrentNetwork ? 1 : 0, c.StagnationRestart,
                c.MaxLinesOverride, c.MinStationsPerLine, c.MaxStationsPerLine, c.AllowLoops ? 1 : 0,
                c.DemandScale, c.CrowdWeight,
                c.TrainSpeedOverride, c.CapacityFeedback ? 1 : 0, c.MaxCrowdingMultiplier, c.MaxCarsPerLoco,
                c.UnreachablePenalty, c.CongestionWeight, c.StationLoadWeight,
                c.TrackLengthWeight, c.CrossingOverBudget, c.UnservedStationPenalty,
                c.HorizonDays, c.UrgencyWeight, c.NativeThreads, c.LocalSearchEvery,
                c.PlanFutureStations ? 1 : 0,
                // custos do A* do jogo (PATH_ROUTE_CHANGE_COST, PATH_STATION_LOADING_COST)
                5.0, 3.5,
                c.PersistDays,
                c.TimeBudgetMs,
                c.ChangeWeight,
            };
        }

        /// <summary>Formato de Genome::encode do nucleo: [rotas, (loop, locos, vagoes, contra, n, paradas...)*].</summary>
        public static int[] Encode(Genome g)
        {
            var v = new List<int>(64);
            v.Add(g.RouteCount);
            for (int r = 0; r < g.RouteCount; r++)
            {
                v.Add(g.Loops[r] ? 1 : 0);
                v.Add(g.Locos[r]);
                v.Add(g.Cars[r]);
                v.Add(g.RevOf(r));
                v.Add(g.Routes[r].Count);
                v.AddRange(g.Routes[r]);
            }
            return v.ToArray();
        }

        /// <summary>
        /// O que o modelo preve para esta rede no problema desta rodada (ver
        /// explain_vec em native/mmopt/src/lib.rs para o layout). Null se falhar.
        /// </summary>
        public double[] Explain(Genome g, GaConfig cfg)
        {
            if (_h == 0 || g == null) return null;
            var gen = Encode(g);
            var vec = ConfigVector(cfg);
            var buf = new double[4096];
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var hg = GCHandle.Alloc(gen, GCHandleType.Pinned);
                var hc = GCHandle.Alloc(vec, GCHandleType.Pinned);
                var ho = GCHandle.Alloc(buf, GCHandleType.Pinned);
                int n;
                try { n = mm_explain(_h, hg.AddrOfPinnedObject(), gen.Length, hc.AddrOfPinnedObject(), vec.Length, ho.AddrOfPinnedObject(), buf.Length); }
                finally { hg.Free(); hc.Free(); ho.Free(); }
                if (n > 0)
                {
                    var res = new double[n];
                    Array.Copy(buf, res, n);
                    return res;
                }
                if (n == 0) return null;
                buf = new double[-n + 16];
            }
            return null;
        }

        private void Poll()
        {
            if (_h == 0) return;
            int f = Time.frameCount;
            if (f == _statusFrame) return;
            _statusFrame = f;
            var h = GCHandle.Alloc(_status, GCHandleType.Pinned);
            try { mm_status(_h, h.AddrOfPinnedObject(), _status.Length); }
            finally { h.Free(); }
        }

        public GaState Status
        {
            get
            {
                if (_h == 0) return GaState.Idle;
                Poll();
                switch ((int)_status[0])
                {
                    case 1: return GaState.Running;
                    case 2: return GaState.Done;
                    case 3: return GaState.Cancelled;
                    case 4: return GaState.Failed;
                    default: return GaState.Idle;
                }
            }
        }

        public int Generation { get { Poll(); return (int)_status[1]; } }
        public int TotalGenerations { get { Poll(); return (int)_status[2]; } }
        public double BestFitness { get { Poll(); return _h == 0 ? double.MaxValue : _status[3]; } }
        public double AvgFitness { get { Poll(); return _status[4]; } }
        public double WorstFitness { get { Poll(); return _status[5]; } }
        public int StagnantFor { get { Poll(); return (int)_status[6]; } }
        public long ElapsedMs { get { Poll(); return (long)_status[7]; } }
        public int EvaluationsDone { get { Poll(); return (int)_status[8]; } }
        public long CacheHits { get { Poll(); return (long)_status[9]; } }
        public int Threads { get { Poll(); return (int)_status[10]; } }
        public double MicrosPerEval { get { Poll(); return _status[11]; } }
        public double CurrentNetworkFitness { get { Poll(); return _status[12]; } }

        public string Error => Status == GaState.Failed ? LastError() : null;

        private string LastError()
        {
            if (_h == 0) return LoadError;
            var buf = new byte[1024];
            var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { mm_error(_h, h.AddrOfPinnedObject(), buf.Length); }
            finally { h.Free(); }
            return Ascii(buf);
        }

        public bool HasBest => _h != 0 && BestFitness < double.MaxValue * 0.5;

        /// <summary>
        /// Melhor genoma decodificado para o formato do C# (o Applier e a UI usam
        /// esse). Cacheado pelo fitness: a UI chama isto varias vezes por frame.
        /// </summary>
        public Genome BestClone()
        {
            if (!HasBest) return null;
            double f = BestFitness;
            if (_bestCache != null && f == _bestCacheFitness) return _bestCache.Clone();

            int n;
            while (true)
            {
                var hg = GCHandle.Alloc(_genomeBuf, GCHandleType.Pinned);
                var hb = GCHandle.Alloc(_bdBuf, GCHandleType.Pinned);
                try { n = mm_best(_h, hg.AddrOfPinnedObject(), _genomeBuf.Length, hb.AddrOfPinnedObject(), _bdBuf.Length); }
                finally { hg.Free(); hb.Free(); }
                if (n >= 0) break;
                _genomeBuf = new int[-n + 64];
            }
            if (n == 0) return null;

            var g = Decode(_genomeBuf, n, _snap != null ? _snap.N : int.MaxValue);
            g.Fitness = _bdBuf[15];
            g.Breakdown = BreakdownFrom(_bdBuf);
            _bestCache = g;
            _bestCacheFitness = f;
            return g.Clone();
        }

        public static Genome Decode(int[] v, int n, int stations)
        {
            var g = new Genome();
            int p = 0;
            int routes = v[p++];
            for (int r = 0; r < routes && p + 5 <= n; r++)
            {
                bool loop = v[p++] != 0;
                int locos = v[p++];
                int cars = v[p++];
                int rev = v[p++];
                int len = v[p++];
                var route = new List<int>(len);
                for (int k = 0; k < len && p < n; k++)
                {
                    int s = v[p++];
                    if (s >= 0 && s < stations) route.Add(s);
                }
                g.Routes.Add(route);
                g.Loops.Add(loop);
                g.Locos.Add(locos);
                g.Cars.Add(cars);
                g.Rev.Add(loop ? rev : 0);
            }
            return g;
        }

        public static EvalBreakdown BreakdownFrom(double[] b)
        {
            return new EvalBreakdown
            {
                TravelCost = b[0],
                UnreachableCost = b[1],
                CongestionCost = b[2],
                StationLoadCost = b[3],
                TrackCost = b[4],
                CrossingCost = b[5],
                UnservedCost = b[6],
                UrgencyCost = b[7],
                UnreachablePairs = (int)b[8],
                UnservedStations = (int)b[9],
                CrossingsUsed = (int)b[10],
                TrackLength = (float)b[11],
                WorstLineUtil = (float)b[12],
                AvgTravelTime = (float)b[13],
                LineCrossings = (int)b[14],
                ChangeCost = b.Length > 16 ? b[16] : 0.0,
                RebuiltLines = b.Length > 17 ? (int)b[17] : 0,
                MovedTrains = b.Length > 18 ? (int)b[18] : 0,
            };
        }

        public void CopyHistory(List<float> best, List<float> avg)
        {
            best.Clear();
            avg.Clear();
            if (_h == 0) return;
            var hb = GCHandle.Alloc(_histBest, GCHandleType.Pinned);
            var ha = GCHandle.Alloc(_histAvg, GCHandleType.Pinned);
            int n;
            try { n = mm_history(_h, hb.AddrOfPinnedObject(), ha.AddrOfPinnedObject(), _histBest.Length); }
            finally { hb.Free(); ha.Free(); }
            for (int i = 0; i < n; i++) { best.Add(_histBest[i]); avg.Add(_histAvg[i]); }
        }

        public void Stop()
        {
            if (_h != 0) mm_stop(_h);
            _statusFrame = -1;
        }

        public void Dispose()
        {
            if (_h != 0)
            {
                try { mm_destroy(_h); } catch (Exception) { }
                _h = 0;
            }
        }

        private static string Ascii(byte[] b)
        {
            var sb = new System.Text.StringBuilder(b.Length);
            for (int i = 0; i < b.Length && b[i] != 0; i++) sb.Append(b[i] < 128 ? (char)b[i] : '?');
            return sb.ToString();
        }
    }
}
