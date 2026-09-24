using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MiniMetroGA.Core;
using MiniMetroGA.Ga;

namespace MiniMetroGA
{
    /// <summary>
    /// Janela de validacao: com a rede congelada, mede no jogo o que o modelo
    /// nativo preve (mm_explain) e grava os dois lado a lado em
    /// MiniMetroGA/validation.jsonl. O jogo e o gabarito; ver
    /// vault/Validacao do modelo.
    ///
    /// Mede, entre o inicio e o fim da janela (tempo de jogo):
    ///   - por linha: ciclo de cada trem (intervalo entre passagens pelo mesmo
    ///     link no mesmo sentido), passageiros a bordo (media por trem), fracao
    ///     do tempo com trem lotado, e quantos trens rodam em cada sentido;
    ///   - por estacao ativa no inicio: fila media, fracao do tempo acima da
    ///     capacidade, pico;
    ///   - no total: passageiros entregues (placar) e nascidos (placar + fila +
    ///     a bordo, contando so as estacoes que ja existiam).
    /// </summary>
    public sealed class ValidationWindow
    {
        private const int HeaderLen = 8, LineLen = 18, StationLen = 18;

        private sealed class LineRec
        {
            public int Route;
            public Line Line;
            public Link Link0;
            /// <summary>Soma de Link.Length do jogo (uma passada), para calibrar o octilinear.</summary>
            public float GameLength;
            /// <summary>LineDirection.FORWARDS anda na ordem do genoma?</summary>
            public bool ForwardIsGenome;
            public double OnboardInt, FullInt, TrainTime, OnboardLate, TrainTimeLate;
            public readonly Dictionary<int, float>[] LastEntry = { new Dictionary<int, float>(), new Dictionary<int, float>() };
            public readonly List<float>[] Cycles = { new List<float>(), new List<float>() };
            public readonly Dictionary<int, int> TrainDir = new Dictionary<int, int>();
            public readonly Dictionary<int, Link> PrevLink = new Dictionary<int, Link>();
            public readonly Dictionary<int, int> PrevDir = new Dictionary<int, int>();
        }

        private readonly int _index;
        private readonly string _city;
        private readonly double[] _pred;
        private readonly Genome _genome;
        private readonly Station[] _stations;
        private readonly HashSet<Station> _startSet;
        private readonly float _t0, _window;
        private readonly int _day0;
        private float _last;
        private readonly int _score0, _wait0, _onboard0, _peeps0;
        /// <summary>Placar e conteudo do sistema a 1/3 da janela (depois do transiente de reconstruir as linhas).</summary>
        private int _scoreLate = -1, _waitLate, _onboardLate;
        private float _tLate;
        private readonly double[] _qInt, _overT, _qIntLate, _overLate;
        private double _lateT;
        private readonly int[] _qMax, _cap;
        private readonly List<LineRec> _lines = new List<LineRec>();
        private readonly string _blobPath;

        public bool Done { get; private set; }
        /// <summary>A rede foi trocada por fora durante a janela; nada foi gravado.</summary>
        public bool Invalid { get; private set; }
        public float Window => _window;

        private ValidationWindow(int index, Game game, Snapshot snap, Genome genome, double[] pred, float window, string blobPath)
        {
            _index = index;
            _city = snap.CityName;
            _pred = pred;
            _genome = genome;
            _window = window;
            _blobPath = blobPath;
            var city = game.City;
            _t0 = _last = city.Clock.Time;
            _day0 = city.Clock.Day;
            _stations = snap.StationRefs;
            _startSet = new HashSet<Station>(_stations);
            int n = _stations.Length;
            _qInt = new double[n];
            _overT = new double[n];
            _qIntLate = new double[n];
            _overLate = new double[n];
            _qMax = new int[n];
            _cap = new int[n];
            for (int i = 0; i < n; i++) _cap[i] = SafeCap(_stations[i]);

            _score0 = game.Score;
            _wait0 = WaitingAt(_stations);
            _onboard0 = Onboard(city);
            _peeps0 = SafePeeps(city);

            var idx = new Dictionary<Station, int>(n);
            for (int i = 0; i < n; i++) idx[_stations[i]] = i;
            foreach (var line in GameHook.GetLiveLines(city))
            {
                var seq = Snapshot.ExtractRoute(line, idx);
                if (seq == null) continue;
                int r = MatchRoute(seq);
                if (r < 0) continue;
                Link link0 = null;
                for (int i = 0; i < line.Count && link0 == null; i++)
                {
                    var lk = line[i];
                    if (lk != null && lk.Start != null && lk.End != null && lk.Start.Station != null && lk.End.Station != null)
                        link0 = lk;
                }
                if (link0 == null) continue;
                float glen = 0f;
                for (int i = 0; i < line.Count; i++)
                {
                    var lk = line[i];
                    if (lk != null && lk.Start != null && lk.End != null && lk.Start.Station != null && lk.End.Station != null)
                    {
                        try { glen += lk.Length; } catch (Exception) { }
                    }
                }
                _lines.Add(new LineRec
                {
                    Route = r,
                    Line = line,
                    Link0 = link0,
                    GameLength = glen,
                    ForwardIsGenome = SameDirection(genome.Routes[r], idx, link0),
                });
            }
        }

        /// <summary>Abre a janela logo depois de aplicar a rede. Null se nao houver previsao.</summary>
        public static ValidationWindow Begin(int index, Game game, Snapshot snap, Genome genome, EngineHost host, float window)
        {
            if (game == null || snap == null || genome == null || host == null) return null;
            // mesma configuracao do AG, mas: horizonte = janela e sem encaixe de
            // estacao futura (no jogo a rede fica congelada, ninguem as liga)
            var cfg = (host.ConfigRef ?? new GaConfig()).Clone();
            cfg.HorizonDays = Math.Max(0.25f, window / 20f);
            cfg.PlanFutureStations = false;
            double[] pred = host.Explain(genome, cfg);
            if (pred == null || pred.Length < HeaderLen) return null;
            string blob = CopyBlob(index);
            return new ValidationWindow(index, game, snap, genome, pred, window, blob);
        }

        public void Tick(Game game)
        {
            if (Done || game == null) return;
            var city = game.City;
            float now = city.Clock.Time;
            float dt = now - _last;
            if (dt <= 0f) return; // pausado (tela de upgrade)
            _last = now;

            bool late = now - _t0 >= _window / 3f;
            if (late) _lateT += dt;
            for (int i = 0; i < _stations.Length; i++)
            {
                int q = SafeCount(_stations[i]);
                _qInt[i] += q * dt;
                if (q > _cap[i]) _overT[i] += dt;
                if (q > _qMax[i]) _qMax[i] = q;
                if (late)
                {
                    _qIntLate[i] += q * dt;
                    if (q > _cap[i]) _overLate[i] += dt;
                }
            }

            // alguem mexeu na rede (F11, jogador): a janela nao mede mais o que
            // o modelo previu
            if (NetworkChanged(city))
            {
                Invalid = true;
                Abort(game, "a rede mudou durante a janela");
                return;
            }

            foreach (var rec in _lines)
            {
                int tc;
                try { tc = rec.Line.TrainCount; } catch (Exception) { continue; }
                for (int k = 0; k < tc; k++)
                {
                    Train t;
                    try { t = rec.Line.GetTrain(k); } catch (Exception) { continue; }
                    if (t == null) continue;
                    DirectedLink dl;
                    try { dl = t.Link; } catch (Exception) { continue; }
                    if (dl.IsNull) continue;
                    int pc = t.PeepCount;
                    rec.OnboardInt += pc * dt;
                    rec.TrainTime += dt;
                    if (late)
                    {
                        rec.OnboardLate += pc * dt;
                        rec.TrainTimeLate += dt;
                    }
                    if (t.FreePeepCapacity <= 0) rec.FullInt += dt;

                    int gameDir = dl.Direction == LineDirection.FORWARDS ? 0 : 1;
                    int dir = rec.ForwardIsGenome ? gameDir : 1 - gameDir;
                    int id = t.Id;
                    Link prev;
                    int prevDir;
                    rec.PrevLink.TryGetValue(id, out prev);
                    if (!rec.PrevDir.TryGetValue(id, out prevDir)) prevDir = -1;
                    if (dl.Link == rec.Link0 && (prev != rec.Link0 || prevDir != dir))
                    {
                        float last;
                        if (rec.LastEntry[dir].TryGetValue(id, out last)) rec.Cycles[dir].Add(now - last);
                        rec.LastEntry[dir][id] = now;
                    }
                    rec.PrevLink[id] = dl.Link;
                    rec.PrevDir[id] = dir;
                    if (!rec.TrainDir.ContainsKey(id)) rec.TrainDir[id] = dir;
                }
            }

            if (_scoreLate < 0 && now - _t0 >= _window / 3f)
            {
                _scoreLate = game.Score;
                _waitLate = WaitingAt(_stations);
                _onboardLate = Onboard(city);
                _tLate = now;
            }

            if (now - _t0 >= _window) Finish(game);
        }

        /// <summary>Fecha antes da hora (game over, rede trocada). Grava o que tiver.</summary>
        public void Abort(Game game, string why)
        {
            if (Done) return;
            Log.Info("VALIDACAO janela " + _index + " interrompida (" + why + ")");
            if (!Invalid && game != null && game.City != null && game.City.Clock.Time - _t0 > 5f) Finish(game);
            Done = true;
        }

        private void Finish(Game game)
        {
            Done = true;
            var city = game.City;
            float w = Math.Max(1e-3f, city.Clock.Time - _t0);
            int score1 = game.Score;
            int wait1 = WaitingAt(_stations);
            int onboard1 = Onboard(city);
            int delivered = score1 - _score0;
            int spawned = delivered + (wait1 - _wait0) + (onboard1 - _onboard0);
            // contador do proprio jogo (City.NewPeepCount); zera no fim da semana
            int peeps1 = SafePeeps(city);
            int counted = peeps1 >= _peeps0 ? peeps1 - _peeps0 : -1;
            int newWait = WaitingNew(city);

            int nl = (int)_pred[1], ns = (int)_pred[2];
            double predDelivered = _pred[3], predDemand = _pred[4], predTravel = _pred[5];
            // fila atual que o modelo preve escoar dentro da janela
            double drained = 0;
            for (int i = 0; i < ns && i < _stations.Length; i++)
            {
                int o = HeaderLen + nl * LineLen + i * StationLen;
                drained += Math.Min(_pred[o + 9], _pred[o + 8] * w);
            }

            var sb = new StringBuilder(4096);
            sb.Append("{\"window\":").Append(_index)
              .Append(",\"city\":\"").Append(_city).Append('"')
              .Append(",\"t0\":").Append(Num(_t0))
              .Append(",\"day0\":").Append(_day0)
              .Append(",\"seconds\":").Append(Num(w))
              .Append(",\"blob\":\"").Append(_blobPath != null ? Path.GetFileName(_blobPath) : "").Append('"')
              .Append(",\"genome\":[");
            var enc = NativeEngine.Encode(_genome);
            for (int i = 0; i < enc.Length; i++) { if (i > 0) sb.Append(','); sb.Append(enc[i]); }
            sb.Append("],\"fitness\":").Append(Num(_pred[7]))
              .Append(",\"pred\":{\"spawned\":").Append(Num(predDemand * w))
              .Append(",\"delivered\":").Append(Num(predDelivered * Math.Max(0.0, w - predTravel) + drained))
              .Append(",\"avg_travel\":").Append(Num(_pred[5])).Append('}')
              // nascidos = entregues + fila + a bordo (so estacoes do inicio). O
              // contador City.NewPeepCount zera no meio do dia (estatistica do
              // jogo): fica so como informacao
              .Append(",\"meas\":{\"spawned\":").Append(spawned)
              .Append(",\"spawned_derived\":").Append(spawned)
              .Append(",\"spawned_counter\":").Append(counted)
              .Append(",\"delivered\":").Append(delivered)
              .Append(",\"new_station_wait\":").Append(newWait);
            if (_scoreLate >= 0)
            {
                // regime: so os ultimos 2/3 da janela
                float wl = city.Clock.Time - _tLate;
                int dLate = score1 - _scoreLate;
                sb.Append(",\"late_seconds\":").Append(Num(wl))
                  .Append(",\"late_delivered\":").Append(dLate)
                  .Append(",\"late_spawned\":").Append(dLate + (wait1 - _waitLate) + (onboard1 - _onboardLate));
            }
            sb.Append('}');
            sb.Append(",\"pred_rates\":{\"delivered\":").Append(Num(predDelivered))
              .Append(",\"demand\":").Append(Num(predDemand)).Append('}');

            sb.Append(",\"lines\":[");
            bool first = true;
            foreach (var rec in _lines)
            {
                int li = FindPredLine(rec.Route, nl);
                if (li < 0) continue;
                int o = HeaderLen + li * LineLen;
                int[] dirCount = new int[2];
                foreach (var kv in rec.TrainDir) dirCount[kv.Value]++;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"route\":").Append(rec.Route)
                  .Append(",\"stops\":").Append(Num(_pred[o + 1]))
                  .Append(",\"loop\":").Append(_pred[o + 2] > 0.5 ? "true" : "false")
                  .Append(",\"trains\":").Append(Num(_pred[o + 3]))
                  .Append(",\"pred_ndir\":[").Append(Num(_pred[o + 5])).Append(',').Append(Num(_pred[o + 6])).Append(']')
                  .Append(",\"meas_ndir\":[").Append(dirCount[0]).Append(',').Append(dirCount[1]).Append(']')
                  .Append(",\"pred_run\":").Append(Num(_pred[o + 7]))
                  .Append(",\"pred_cycle\":[").Append(Num(_pred[o + 8])).Append(',').Append(Num(_pred[o + 9])).Append(']')
                  .Append(",\"meas_cycle\":[").Append(Num(Mean(rec.Cycles[0]))).Append(',').Append(Num(Mean(rec.Cycles[1]))).Append(']')
                  .Append(",\"meas_cycle_n\":[").Append(rec.Cycles[0].Count).Append(',').Append(rec.Cycles[1].Count).Append(']')
                  .Append(",\"pred_util\":[").Append(Num(_pred[o + 12])).Append(',').Append(Num(_pred[o + 13])).Append(']')
                  .Append(",\"pred_onboard\":").Append(Num(_pred[o + 14]))
                  .Append(",\"meas_onboard\":").Append(Num(rec.TrainTime > 0 ? rec.OnboardInt / rec.TrainTime : double.NaN))
                  .Append(",\"meas_onboard_late\":").Append(Num(rec.TrainTimeLate > 0 ? rec.OnboardLate / rec.TrainTimeLate : double.NaN))
                  .Append(",\"meas_full_frac\":").Append(Num(rec.TrainTime > 0 ? rec.FullInt / rec.TrainTime : double.NaN))
                  .Append(",\"pred_stops_made\":").Append(Num(_pred[o + 15]))
                  .Append(",\"pred_length\":").Append(Num(_pred[o + 16]))
                  .Append(",\"game_length\":").Append(Num(rec.GameLength))
                  .Append(",\"pred_crossings\":").Append(Num(_pred[o + 17]))
                  .Append('}');
            }
            sb.Append(']');

            sb.Append(",\"stations\":[");
            for (int i = 0; i < _stations.Length && i < ns; i++)
            {
                int o = HeaderLen + nl * LineLen + i * StationLen;
                if (i > 0) sb.Append(',');
                sb.Append("{\"i\":").Append(i)
                  .Append(",\"cap\":").Append(_cap[i])
                  .Append(",\"pred_q\":").Append(Num(_pred[o + 12]))
                  .Append(",\"pred_over\":").Append(Num(_pred[o + 13]))
                  .Append(",\"pred_regime\":").Append(Num(_pred[o + 0]))
                  .Append(",\"pred_growth\":").Append(Num(_pred[o + 14]))
                  .Append(",\"pred_backlog\":").Append(Num(_pred[o + 9] + _pred[o + 10]))
                  .Append(",\"pred_spawn\":").Append(Num(_pred[o + 5]))
                  .Append(",\"meas_q\":").Append(Num(_qInt[i] / w))
                  .Append(",\"meas_over\":").Append(Num(_overT[i] / w))
                  .Append(",\"meas_max\":").Append(_qMax[i])
                  .Append(",\"pred_q_late\":").Append(Num(_pred[o + 15]))
                  .Append(",\"pred_over_late\":").Append(Num(_pred[o + 16]))
                  .Append(",\"meas_q_late\":").Append(Num(_lateT > 0 ? _qIntLate[i] / _lateT : double.NaN))
                  .Append(",\"meas_over_late\":").Append(Num(_lateT > 0 ? _overLate[i] / _lateT : double.NaN))
                  .Append('}');
            }
            sb.Append("]}");

            Append(sb.ToString());
            Log.Info(string.Format("VALIDACAO janela {0}: {1:N0} s, nascidos {2} (modelo {3:N1}), entregues {4} (modelo {5:N1}), {6} linhas medidas",
                _index, w, spawned, predDemand * w, delivered,
                predDelivered * Math.Max(0.0, w - predTravel) + drained, _lines.Count));
        }

        // ------------------------------------------------------------------

        private bool NetworkChanged(City city)
        {
            var live = GameHook.GetLiveLines(city);
            foreach (var rec in _lines)
            {
                if (!live.Contains(rec.Line)) return true;
                try
                {
                    bool found = false;
                    for (int i = 0; i < rec.Line.Count && !found; i++) found = rec.Line[i] == rec.Link0;
                    if (!found) return true;
                }
                catch (Exception) { return true; }
            }
            return false;
        }

        private int MatchRoute(int[] seq)
        {
            var set = new HashSet<int>(seq);
            for (int r = 0; r < _genome.RouteCount; r++)
            {
                var route = _genome.Routes[r];
                if (route.Count != set.Count) continue;
                bool all = true;
                foreach (int s in route) if (!set.Contains(s)) { all = false; break; }
                if (all) return r;
            }
            return -1;
        }

        /// <summary>O link0 do jogo vai de route[k] para route[k+1] (ordem do genoma)?</summary>
        private static bool SameDirection(List<int> route, Dictionary<Station, int> idx, Link link0)
        {
            int a, b;
            if (!idx.TryGetValue(link0.Start.Station, out a) || !idx.TryGetValue(link0.End.Station, out b)) return true;
            int m = route.Count;
            for (int k = 0; k < m; k++)
            {
                if (route[k] != a) continue;
                return route[(k + 1) % m] == b;
            }
            return true;
        }

        private int FindPredLine(int route, int nl)
        {
            for (int l = 0; l < nl; l++)
                if ((int)_pred[HeaderLen + l * LineLen] == route) return l;
            return -1;
        }

        private static int WaitingAt(Station[] stations)
        {
            int total = 0;
            foreach (var s in stations) total += SafeCount(s);
            return total;
        }

        private int WaitingNew(City city)
        {
            int total = 0;
            foreach (var s in GameHook.GetAllStations(city))
                if (!_startSet.Contains(s)) total += SafeCount(s);
            return total;
        }

        /// <summary>
        /// Passageiros a bordo em TODAS as linhas, inclusive as que estao sendo
        /// removidas: Line.Remove() so aposenta a linha, e os trens dela seguem
        /// ate largar os passageiros numa estacao (que entrariam na conta da
        /// fila como se tivessem nascido).
        /// </summary>
        private static int Onboard(City city)
        {
            int total = 0;
            for (int i = 0; i < city.LineCount; i++)
            {
                Line line;
                try { line = city.GetLine(i); } catch (Exception) { continue; }
                if (line == null) continue;
                try
                {
                    for (int k = 0; k < line.TrainCount; k++)
                    {
                        var t = line.GetTrain(k);
                        if (t != null) total += t.PeepCount;
                    }
                }
                catch (Exception) { }
            }
            return total;
        }

        private static int SafePeeps(City city)
        {
            try { return city.NewPeepCount; } catch (Exception) { return 0; }
        }

        private static int SafeCount(Station s)
        {
            try { return s != null ? s.PeepCount : 0; } catch (Exception) { return 0; }
        }

        private static int SafeCap(Station s)
        {
            try { return s.PeepCapacity; } catch (Exception) { return 6; }
        }

        private static double Mean(List<float> v)
        {
            if (v.Count == 0) return double.NaN;
            double s = 0;
            foreach (var x in v) s += x;
            return s / v.Count;
        }

        /// <summary>Numero em JSON sem depender da cultura do sistema (virgula decimal).</summary>
        private static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            long scaled = (long)Math.Round(Math.Abs(v) * 10000.0);
            long ip = scaled / 10000, fp = scaled % 10000;
            var sb = new StringBuilder(24);
            if (v < 0 && scaled != 0) sb.Append('-');
            sb.Append(ip);
            if (fp != 0)
            {
                sb.Append('.');
                string f = fp.ToString();
                for (int i = f.Length; i < 4; i++) sb.Append('0');
                sb.Append(f.TrimEnd('0'));
            }
            return sb.ToString();
        }

        private static string Dir()
        {
            string bin = Path.GetDirectoryName(typeof(ValidationWindow).Assembly.Location);
            return Path.GetDirectoryName(bin);
        }

        private static void Append(string line)
        {
            try
            {
                string path = Path.Combine(Dir(), "validation.jsonl");
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                using (var f = new FileStream(path, FileMode.Append, FileAccess.Write))
                    f.Write(bytes, 0, bytes.Length);
            }
            catch (Exception e)
            {
                Log.Warn("VALIDACAO: nao gravou validation.jsonl: " + e.Message);
            }
        }

        /// <summary>Guarda o problema desta janela (problems/last.bin -> val_N.bin) para o mmopt-bench.</summary>
        private static string CopyBlob(int index)
        {
            try
            {
                string dir = Path.Combine(Dir(), "problems");
                string src = Path.Combine(dir, "last.bin");
                if (!File.Exists(src)) return null;
                string dst = Path.Combine(dir, "val_" + index + ".bin");
                using (var i = new FileStream(src, FileMode.Open, FileAccess.Read))
                using (var o = new FileStream(dst, FileMode.Create, FileAccess.Write))
                {
                    var buf = new byte[65536];
                    int n;
                    while ((n = i.Read(buf, 0, buf.Length)) > 0) o.Write(buf, 0, n);
                }
                return dst;
            }
            catch (Exception e)
            {
                Log.Warn("VALIDACAO: nao copiou o problema: " + e.Message);
                return null;
            }
        }
    }
}
