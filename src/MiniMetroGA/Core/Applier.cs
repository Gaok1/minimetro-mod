using System;
using System.Reflection;
using System.Collections.Generic;
using System.Text;
using MiniMetroGA.Ga;
using UnityEngine;

namespace MiniMetroGA.Core
{
    /// <summary>
    /// Leva o melhor genoma para dentro do jogo mexendo so no que mudou, do jeito
    /// que um jogador mexeria:
    ///
    ///   - linha IGUAL a uma atual (a menos de sentido e rotacao do loop): fica;
    ///   - linha que so GANHA estacoes: estica a ponta pelo terminal e encaixa no
    ///     meio arrastando o trecho (os mesmos gestos do dedo), com os trens
    ///     rodando e os passageiros a bordo;
    ///   - o resto: a linha atual e APAGADA como o jogador apaga (Line.Mothball:
    ///     os trens terminam o trecho e desembarcam todo mundo na proxima
    ///     estacao) e a nova e desenhada;
    ///   - frota: trem que sobra numa linha vai para onde falta (o mesmo "arrastar
    ///     o trem" do jogo: desembarca antes de mudar de linha); o que faltar e o
    ///     estoque ainda nao tem fica pendente e entra quando liberar (Tick).
    ///
    /// Antes isto apagava a rede inteira com Line.Remove, que some com quem esta
    /// a bordo (Train.Release nao desembarca ninguem): cada reconstrucao jogava
    /// fora dezenas de passageiros que iam virar ponto. As regras de casamento
    /// sao as mesmas de native/mmopt/src/change.rs, que cobra no fitness o custo
    /// de cada tipo de mexida.
    ///
    /// Tudo pelo LineBuilder e pelo ApplyAsset: o jogo aplica as regras dele
    /// (tunel, limite de trens, plataforma) e recusa o que for ilegal. Depois de
    /// cada edicao a rota e relida; se nao bateu, a linha e refeita.
    ///
    /// Roda SEMPRE na main thread.
    /// </summary>
    public static class Applier
    {
        public struct Result
        {
            public int LinesKept, LinesEdited, LinesBuilt, LinesRemoved, LinesFailed;
            public int StationsInserted;
            public int LocomotivesPlaced, LocomotivesWanted, TrainsMoved, TrainsPending;
            public int CarriagesPlaced, CarriagesWanted, CarsMoved;
            public int PassengersBefore, PassengersAfter;
            public int CrossingsModel, CrossingsGame;
            public int LinesWithoutTrain;
            public string Message;
            public bool Ok;
        }

        private struct Cur
        {
            public Line Line;
            public int[] Stops;
            public bool Loop;
        }

        /// <summary>Linha que ainda espera trem ou vagao do estoque.</summary>
        private sealed class Pending
        {
            public Line Line;
            public List<int> Route;
            public bool Loop;
            public int Locos, Cars, Rev;
        }

        private static readonly List<Pending> _pending = new List<Pending>();
        private static Snapshot _pendingSnap;
        private static float _pendingUntil, _pendingNext, _pendingSince;

        /// <summary>Frota que a ultima rede pediu para cada linha (doadoras do Tick).</summary>
        private static readonly List<Pending> _want = new List<Pending>();

        /// <summary>
        /// Rotas que o jogo recusou ha pouco (chave = rota, valor = quando). O AG
        /// nao sabe da recusa e pede a mesma rota na rodada seguinte; apagar e
        /// redesenhar de novo so manda trem para o deposito e volta com a mesma
        /// linha (Londres, semana 7: "queria 2-8-1-0-15-19-27, ficou 2-8-1-0-19-27"
        /// em rodadas seguidas, com 0 travessias livres).
        /// </summary>
        private static readonly Dictionary<string, float> _refused = new Dictionary<string, float>();
        private const float RefusedForSec = 60f;

        /// <summary>
        /// Por quanto tempo (s reais) uma pendencia segura a proxima otimizacao.
        /// Normalmente a frota entra em 1 a 5 s; passado isso, reotimizar sai mais
        /// barato do que deixar estacao nova sem linha.
        /// </summary>
        private const float SettleCapSec = 10f;

        public static bool HasPending => _pending.Count > 0;

        /// <summary>
        /// A ultima aplicacao ainda esta assentando: linha apagada com trem
        /// rodando (Line.Mothball so devolve o trem ao estoque quando ele termina
        /// o trecho e desembarca todo mundo) ou linha esperando trem/vagao do
        /// estoque. Otimizar agora le uma rede de passagem, com linha sem trem e
        /// frota que nao esta nem no estoque nem em linha viva; o AG "conserta"
        /// isso apagando mais linhas, e cada rodada deixa mais trem parado. Foi
        /// o que derrubou Londres na semana 4 (2026-09-25).
        /// </summary>
        public static bool IsSettling(Game game)
        {
            if (_pending.Count > 0 && Time.realtimeSinceStartup - _pendingSince < SettleCapSec) return true;
            return MothballedStillRunning(game);
        }

        /// <summary>Linha apagada cujo trem ainda nao voltou ao estoque.</summary>
        private static bool MothballedStillRunning(Game game)
        {
            City city;
            try { city = game != null ? game.City : null; } catch (Exception) { return false; }
            if (city == null) return false;
            try
            {
                for (int i = 0; i < city.LineCount; i++)
                {
                    var l = city.GetLine(i);
                    if (l != null && l.IsMothballed && l.TrainCount > 0) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        public static Result Apply(Game game, Snapshot snap, Genome genome)
        {
            var res = new Result();
            if (game == null || snap == null || genome == null)
            {
                res.Message = "Nada para aplicar.";
                return res;
            }

            var city = game.City;
            if (city == null) { res.Message = "Cidade indisponivel."; return res; }

            bool wasPaused = game.IsPaused;
            game.IsPaused = true;
            bool tipSuppressed = SetAmbiguousTipRequired(false);
            _pending.Clear();
            _want.Clear();

            try
            {
                CancelPendingEdit(game);
                res.PassengersBefore = CountPassengers(city);
                int n = genome.RouteCount;

                var idx = new Dictionary<Station, int>(snap.N);
                for (int i = 0; i < snap.N; i++) if (snap.StationRefs[i] != null) idx[snap.StationRefs[i]] = i;

                // ---- rede atual, como o jogo esta AGORA ----
                var cur = new List<Cur>();
                foreach (var line in GameHook.GetLiveLines(city))
                {
                    ScrubOrphans(line);
                    var stops = Snapshot.ExtractRoute(line, idx);
                    if (stops == null) { MothballLine(line); res.LinesRemoved++; continue; }
                    cur.Add(new Cur { Line = line, Stops = stops, Loop = SafeLoop(line) && stops.Length >= 3 });
                }

                // ---- casamento (igual ao change.rs) ----
                var matchOf = new int[n];
                var insOf = new int[n];
                for (int r = 0; r < n; r++) matchOf[r] = -1;
                var order = new List<int>(cur.Count);
                for (int i = 0; i < cur.Count; i++) order.Add(i);
                order.Sort((a, b) =>
                {
                    int c = cur[b].Stops.Length.CompareTo(cur[a].Stops.Length);
                    return c != 0 ? c : a.CompareTo(b);
                });
                var curMatched = new bool[cur.Count];
                foreach (int ci in order)
                {
                    int best = -1, bestIns = int.MaxValue;
                    for (int r = 0; r < n; r++)
                    {
                        if (matchOf[r] >= 0) continue;
                        bool gLoop = genome.Loops[r] && genome.Routes[r].Count >= 3;
                        int ins; bool same;
                        if (!Relation(cur[ci].Stops, cur[ci].Loop, genome.Routes[r], gLoop, out ins, out same)) continue;
                        if (ins < bestIns) { best = r; bestIns = ins; }
                    }
                    if (best < 0) continue;
                    matchOf[best] = ci;
                    insOf[best] = bestIns;
                    curMatched[ci] = true;
                }

                // ---- 1. apaga o que nao fica (libera linha e travessia antes de desenhar) ----
                for (int ci = 0; ci < cur.Count; ci++)
                {
                    if (curMatched[ci]) continue;
                    MothballLine(cur[ci].Line);
                    res.LinesRemoved++;
                }

                // ---- 2. linhas que ficam ou so ganham estacoes ----
                // Uma edicao recusada volta para o fim da fila antes de a linha
                // ser apagada: o estoque de travessias e da rede inteira, e a
                // edicao que precisa de uma pode vir antes da que a libera (o
                // total final cabe, a ordem e que nao).
                var lineOf = new Line[n];
                var retry = new List<int>();
                for (int pass = 0; pass < 2; pass++)
                {
                    var todo = retry;
                    if (pass == 0)
                    {
                        todo = new List<int>();
                        for (int r = 0; r < n; r++) if (matchOf[r] >= 0) todo.Add(r);
                    }
                    retry = new List<int>();
                    foreach (int r in todo)
                    {
                        var c = cur[matchOf[r]];
                        if (pass == 0 && insOf[r] == 0) { lineOf[r] = c.Line; res.LinesKept++; continue; }
                        int[] now;
                        string why = EditAndCheck(game, snap, idx, c.Line, genome.Routes[r], genome.Loops[r] && genome.Routes[r].Count >= 3, out now);
                        if (why == null)
                        {
                            lineOf[r] = c.Line;
                            res.LinesEdited++;
                            res.StationsInserted += insOf[r];
                            continue;
                        }
                        string msg = string.Format("Applier: edicao da linha {0} nao bateu ({1}; {2} travessia(s) livre(s)). queria {3}, ficou {4}",
                            r + 1, why, FreeCrossings(game), Join(genome.Routes[r]), now != null ? Join(now) : "-");
                        if (pass == 0)
                        {
                            Log.Info(msg + "; tenta de novo depois das outras");
                            retry.Add(r);
                            continue;
                        }
                        if (RecentlyRefused(genome.Routes[r], genome.Loops[r]))
                        {
                            Log.Info(msg + "; o jogo ja recusou esta rota, a linha fica como esta");
                            lineOf[r] = c.Line;
                            res.LinesKept++;
                            continue;
                        }
                        Log.Warn(msg + "; refazendo");
                        MarkRefused(genome.Routes[r], genome.Loops[r]);
                        MothballLine(c.Line);
                        res.LinesRemoved++;
                    }
                }

                // ---- 3. linhas novas ----
                for (int r = 0; r < n; r++)
                {
                    if (lineOf[r] != null) continue;
                    var route = genome.Routes[r];
                    if (route.Count < 2) continue;
                    Line built;
                    try { built = BuildLine(game, snap, route, genome.Loops[r]); }
                    catch (Exception e)
                    {
                        // Uma linha que o jogo recusou no meio do gesto nao pode
                        // derrubar as outras: cancela o gesto pendente e segue.
                        Log.Warn("Linha " + (r + 1) + " falhou dentro do LineBuilder: " + e);
                        CancelPendingEdit(game);
                        built = null;
                    }
                    if (built == null) { res.LinesFailed++; continue; }
                    lineOf[r] = built;
                    res.LinesBuilt++;
                    var got = Snapshot.ExtractRoute(built, idx);
                    int ins3; bool same3;
                    bool gLoop3 = genome.Loops[r] && route.Count >= 3;
                    if (got == null || !Relation(got, SafeLoop(built) && got.Length >= 3, route, gLoop3, out ins3, out same3) || ins3 != 0)
                    {
                        Log.Warn(string.Format("Applier: linha {0} desenhada diferente ({1} travessia(s) livre(s)). queria {2}, ficou {3}",
                            r + 1, FreeCrossings(game), Join(route), got != null ? Join(got) : "-"));
                        MarkRefused(route, genome.Loops[r]);
                    }
                }

                // ---- 4. frota ----
                AdjustFleet(game, snap, genome, lineOf, ref res);
                for (int r = 0; r < n; r++)
                    if (lineOf[r] != null)
                        _want.Add(new Pending { Line = lineOf[r], Locos = genome.Locos[r], Cars = genome.Cars[r] });

                for (int r = 0; r < n; r++)
                {
                    if (lineOf[r] != null && TrainCountOf(lineOf[r]) <= 0 && !IsPending(lineOf[r]))
                    {
                        res.LinesWithoutTrain++;
                        Log.Warn("Linha " + (r + 1) + " ficou sem trem: estoque de locomotivas acabou.");
                    }
                }

                // ---- conferencia: travessias do modelo x do jogo ----
                res.CrossingsModel = genome.Breakdown.CrossingsUsed;
                try
                {
                    var db = game.AssetDatabase;
                    res.CrossingsGame = db.GetTotalAssets(AssetType.Crossing) - db.GetAvailableAssets(AssetType.Crossing);
                }
                catch (Exception) { res.CrossingsGame = -1; }
                if (res.CrossingsGame >= 0 && res.CrossingsGame != res.CrossingsModel)
                    Log.Warn(string.Format("Applier: travessias no jogo {0}, no modelo {1} (a deteccao de agua divergiu)",
                        res.CrossingsGame, res.CrossingsModel));

                res.PassengersAfter = CountPassengers(city);
                res.Ok = res.LinesFailed == 0 && res.LinesWithoutTrain == 0;
                res.Message = Describe(res, snap);
            }
            catch (Exception e)
            {
                res.Message = "Erro ao aplicar: " + e.Message;
                Log.Error("Applier: " + e);
            }
            finally
            {
                if (tipSuppressed) SetAmbiguousTipRequired(true);
                game.IsPaused = wasPaused;
            }

            if (_pending.Count > 0)
            {
                _pendingSnap = snap;
                _pendingSince = Time.realtimeSinceStartup;
                _pendingUntil = _pendingSince + 60f;
                _pendingNext = 0f;
            }
            return res;
        }

        /// <summary>
        /// Poe o que ficou pendente (trem e vagao que so liberam quando o trem
        /// apagado chega na estacao). Chamar todo frame; barato quando nao ha nada.
        /// </summary>
        public static void Tick(Game game)
        {
            if (_pending.Count == 0 || game == null) return;
            float now = Time.realtimeSinceStartup;
            if (now < _pendingNext) return;
            _pendingNext = now + 0.5f;
            if (now > _pendingUntil)
            {
                Log.Warn("Applier: " + _pending.Count + " linha(s) ficaram sem a frota pedida (estoque nao liberou).");
                _pending.Clear();
                return;
            }
            try
            {
                // Quando mais nada vai voltar ao estoque, o que falta sai de linha
                // com sobra: a locomotiva que volta do deposito vai para a primeira
                // linha que o jogo marcou esperando (nao para a que a rede quer), e
                // vagao que nao coube numa linha ainda sem trem fica onde estava.
                // Sem isso a pendencia travava ate expirar (Londres, semana 7).
                bool stockFinal = !MothballedStillRunning(game);
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    var p = _pending[i];
                    if (p.Line == null || p.Line.IsMothballed || p.Line.Index < 0) { _pending.RemoveAt(i); continue; }
                    int moved = 0;
                    if (p.Loop && p.Route.Count >= 3)
                    {
                        PlaceLoopTrains(game, _pendingSnap, p.Line, p.Route, p.Locos, p.Rev,
                            stockFinal ? Surplus(p.Line, p.Locos - TrainCountOf(p.Line)) : null, ref moved);
                    }
                    else
                    {
                        while (TrainCountOf(p.Line) < p.Locos && TryApplyAsset(p.Line, PreferredLocomotive(game))) { }
                        if (stockFinal)
                            foreach (var t in Surplus(p.Line, p.Locos - TrainCountOf(p.Line)))
                                if (MoveTrain(t, p.Line, null, LineDirection.FORWARDS)) moved++;
                    }
                    while (CarsOf(p.Line) < p.Cars && TryApplyAsset(p.Line, AssetType.Carriage)) { }
                    if (stockFinal && TrainCountOf(p.Line) > 0)
                        foreach (var car in SurplusCars(p.Line, p.Cars - CarsOf(p.Line)))
                            if (MoveCar(car, p.Line)) moved++;
                    if (moved > 0)
                        Log.Info("Applier: " + moved + " trem(ns)/vagao(oes) tirados de linha com sobra para a linha " + SafeIndex(p.Line) + ".");
                    if (TrainCountOf(p.Line) >= p.Locos && CarsOf(p.Line) >= p.Cars) _pending.RemoveAt(i);
                }
                if (_pending.Count == 0)
                    Log.Info(string.Format("Applier: frota pendente posta {0:N1} s (reais) depois da aplicacao.", now - _pendingSince));
            }
            catch (Exception e)
            {
                Log.Warn("Applier.Tick: " + e.Message);
                _pending.Clear();
            }
        }

        public static void ClearPending() => _pending.Clear();

        private static string RouteKey(List<int> route, bool loop)
        {
            return (loop ? "o" : "-") + Join(route);
        }

        private static void MarkRefused(List<int> route, bool loop)
        {
            _refused[RouteKey(route, loop)] = Time.realtimeSinceStartup;
        }

        private static bool RecentlyRefused(List<int> route, bool loop)
        {
            float at;
            return _refused.TryGetValue(RouteKey(route, loop), out at) && Time.realtimeSinceStartup - at < RefusedForSec;
        }

        /// <summary>Ate k trens de linhas que tem mais do que a ultima rede pediu.</summary>
        private static List<Train> Surplus(Line except, int k)
        {
            var list = new List<Train>();
            foreach (var w in _want)
            {
                if (list.Count >= k) break;
                if (w.Line == except || w.Line == null || w.Line.IsMothballed) continue;
                int extra = TrainCountOf(w.Line) - w.Locos;
                if (extra > 0) list.AddRange(PickTrains(w.Line, Math.Min(extra, k - list.Count)));
            }
            return list;
        }

        /// <summary>Ate k vagoes de linhas que tem mais do que a ultima rede pediu.</summary>
        private static List<Railcar> SurplusCars(Line except, int k)
        {
            var list = new List<Railcar>();
            foreach (var w in _want)
            {
                if (list.Count >= k) break;
                if (w.Line == except || w.Line == null || w.Line.IsMothballed) continue;
                int extra = CarsOf(w.Line) - w.Cars;
                if (extra > 0) list.AddRange(PickCars(w.Line, Math.Min(extra, k - list.Count)));
            }
            return list;
        }

        private static bool IsPending(Line line)
        {
            foreach (var p in _pending) if (p.Line == line) return true;
            return false;
        }

        // ------------------------------------------------------------------
        // casamento
        // ------------------------------------------------------------------

        /// <summary>
        /// A rota g sai da atual c sem apagar? Mesma regra de change.rs: c e
        /// subsequencia de g (aberta: na ordem ou ao contrario; loop: ciclica nos
        /// dois sentidos) e as duas sao do mesmo tipo.
        /// </summary>
        internal static bool Relation(int[] c, bool cLoop, List<int> g, bool gLoop, out int ins, out bool same)
        {
            ins = 0; same = true;
            if (c.Length < 2 || g.Count < c.Length || cLoop != gLoop) return false;
            ins = g.Count - c.Length;
            int m = g.Count;
            if (!cLoop)
            {
                if (IsSubseq(c, g, 0, 1, false)) { same = true; return true; }
                if (IsSubseq(c, g, m - 1, -1, false)) { same = false; return true; }
                return false;
            }
            int start = Find(g, c[0]);
            if (start < 0) return false;
            if (IsSubseq(c, g, start, 1, true)) { same = true; return true; }
            if (IsSubseq(c, g, start, -1, true)) { same = false; return true; }
            return false;
        }

        private static bool IsSubseq(int[] c, List<int> g, int start, int step, bool cyclic)
        {
            int m = g.Count, i = 0;
            for (int k = 0; k < m; k++)
            {
                int j = start + step * k;
                if (cyclic) j = ((j % m) + m) % m;
                else if (j < 0 || j >= m) break;
                if (i < c.Length && c[i] == g[j]) i++;
            }
            return i == c.Length;
        }

        /// <summary>A rota do genoma lida no sentido da rota atual (loop: comecando nela).</summary>
        private static List<int> Aligned(int[] c, List<int> g, bool loop, bool same)
        {
            int m = g.Count;
            var a = new List<int>(m);
            if (!loop)
            {
                for (int k = 0; k < m; k++) a.Add(same ? g[k] : g[m - 1 - k]);
                return a;
            }
            int start = Find(g, c[0]);
            for (int k = 0; k < m; k++) a.Add(g[(((start + (same ? k : -k)) % m) + m) % m]);
            return a;
        }

        // ------------------------------------------------------------------
        // edicao por gestos
        // ------------------------------------------------------------------

        /// <summary>
        /// Leva a linha ate a rota g por gestos, partindo de como ela esta AGORA
        /// no jogo (numa segunda tentativa ela pode ter ficado pela metade), e
        /// confere o resultado. Devolve null se bateu, ou o motivo.
        /// </summary>
        private static string EditAndCheck(Game game, Snapshot snap, Dictionary<Station, int> idx, Line line, List<int> g, bool gLoop, out int[] now)
        {
            now = Snapshot.ExtractRoute(line, idx);
            if (now == null) return "rota atual ilegivel";
            int ins; bool same;
            if (!Relation(now, SafeLoop(line) && now.Length >= 3, g, gLoop, out ins, out same)) return "rota atual nao cabe na nova";
            if (ins == 0) return null;
            string why = EditLine(game, snap, line, now, g, gLoop, same);
            now = Snapshot.ExtractRoute(line, idx);
            if (why != null) return why;
            if (now == null) return "rota ilegivel depois da edicao";
            if (!Relation(now, SafeLoop(line) && now.Length >= 3, g, gLoop, out ins, out same) || ins != 0) return "rota diferente";
            return null;
        }

        private static int FreeCrossings(Game game)
        {
            try { return game.AssetDatabase.GetAvailableAssets(AssetType.Crossing); }
            catch (Exception) { return -1; }
        }

        /// <summary>
        /// Encaixa na linha as estacoes que a rota nova tem a mais. Devolve null
        /// se todos os gestos rodaram, ou o motivo da falha.
        /// </summary>
        private static string EditLine(Game game, Snapshot snap, Line line, int[] c, List<int> target, bool loop, bool same)
        {
            var lb = game.LineBuilder;
            if (lb == null) return "sem LineBuilder";
            var g = Aligned(c, target, loop, same);
            int m = g.Count;
            // onde cada parada atual cai na rota nova (menor encaixe, em ordem)
            var pos = new int[c.Length];
            int j = 0;
            for (int k = 0; k < c.Length; k++)
            {
                while (j < m && g[j] != c[k]) j++;
                if (j >= m) return "rota atual nao cabe na nova";
                pos[k] = j++;
            }

            // trechos do meio (e o de fechamento do loop)
            int gaps = loop ? c.Length : c.Length - 1;
            for (int k = 0; k < gaps; k++)
            {
                int a = pos[k];
                int b = k + 1 < c.Length ? pos[k + 1] : m; // loop: ate o fim, que volta em c[0]
                if (b - a <= 1) continue;
                var gap = Slice(g, a + 1, b - a - 1);
                var sa = snap.StationRefs[c[k]];
                var sb = snap.StationRefs[c[(k + 1) % c.Length]];
                bool startIsA;
                var lk = FindLink(line, sa, sb, out startIsA);
                if (lk == null) return "trecho " + c[k] + "-" + c[(k + 1) % c.Length] + " nao achado";
                if (!startIsA) Reverse(gap);
                var mid = (sa.Position + sb.Position) * 0.5f;
                lb.HandleLinkTouchBegan(lk, mid);
                if (!lb.IsBuilding) return "o jogo nao deixou pegar o trecho " + c[k] + "-" + c[(k + 1) % c.Length];
                TouchStations(game, lb, snap, line, gap, startIsA ? sa : sb, startIsA ? sb : sa);
                if (lb.IsBuilding) lb.HandleTouchEnded();
                RefreshTracks(line);
            }

            if (!loop)
            {
                // pontas: esticam a partir do terminal
                if (pos[c.Length - 1] < m - 1)
                {
                    var tail = Slice(g, pos[c.Length - 1] + 1, m - 1 - pos[c.Length - 1]);
                    string why = Extend(game, lb, snap, line, snap.StationRefs[c[c.Length - 1]], tail);
                    if (why != null) return why;
                }
                if (pos[0] > 0)
                {
                    var head = Slice(g, 0, pos[0]);
                    Reverse(head);
                    string why = Extend(game, lb, snap, line, snap.StationRefs[c[0]], head);
                    if (why != null) return why;
                }
            }
            return null;
        }

        private static string Extend(Game game, LineBuilder lb, Snapshot snap, Line line, Station end, List<int> stations)
        {
            Terminator term = null;
            for (int i = 0; i < 2 && term == null; i++)
            {
                Terminator t;
                try { t = line.GetTerminator(i); } catch (Exception) { t = null; }
                if (t == null) continue;
                var stop = t.AnchoredStop;
                if (stop != null && stop.Station == end) term = t;
            }
            if (term == null) return "terminal nao achado na estacao da ponta";
            lb.HandleTerminatorTouchBegan(term, end.Position);
            if (!lb.IsBuilding) return "o jogo nao deixou pegar o terminal";
            TouchStations(game, lb, snap, line, stations, end, null);
            if (lb.IsBuilding) lb.HandleTouchEnded();
            RefreshTracks(line);
            return null;
        }

        /// <summary>
        /// Passa o dedo pelas estacoes, em ordem, a partir de `from`. Com `other`
        /// (arrasto de trecho) o jogo tem duas pontas soltas, uma saindo de cada
        /// estacao do trecho, e prende a estacao tocada s na ponta j que maximiza
        /// |S_j - s|^2 - |S_j - ultimo toque|^2 = 2 (S_j - s).v - |v|^2, com
        /// v = ultimo toque - s (LineBuilder.HandleStationTouchOver). Com o dedo
        /// em cima da estacao (v = 0) quem decidia era o arredondamento, e duas
        /// estacoes no mesmo trecho entravam na ordem trocada. Aqui o ultimo
        /// toque fica um pouco para o lado de S_0 - S_1: a estacao sempre prende
        /// na ponta que vem da anterior, qualquer que seja a geometria.
        /// </summary>
        private static void TouchStations(Game game, LineBuilder lb, Snapshot snap, Line line, List<int> stations, Station from, Station other)
        {
            Vector2 prev = from != null ? from.Position : Vector2.zero;
            foreach (int s in stations)
            {
                var st = snap.StationRefs[s];
                if (st == null) continue;
                var touch = st.Position;
                if (other != null)
                {
                    var d = prev - other.Position;
                    float len = d.magnitude;
                    if (len > 1e-3f) touch += d * (Mathf.Max(1f, 0.05f * len) / len);
                }
                lb.HandleTouchMove(ToGlobal(game, touch));
                RefreshTracks(line);
                lb.HandleStationTouchOver(st);
                if (!lb.IsBuilding) break;
                prev = st.Position;
            }
            RefreshTracks(line);
        }

        private static Link FindLink(Line line, Station a, Station b, out bool startIsA)
        {
            startIsA = true;
            for (int i = 0; i < line.Count; i++)
            {
                var lk = line[i];
                if (lk == null || lk.State != LinkState.ACTIVE || lk.IsPendingMothball) continue;
                var s = lk.Start != null ? lk.Start.Station : null;
                var e = lk.End != null ? lk.End.Station : null;
                if (s == a && e == b) { startIsA = true; return lk; }
                if (s == b && e == a) { startIsA = false; return lk; }
            }
            return null;
        }

        // ------------------------------------------------------------------
        // frota
        // ------------------------------------------------------------------

        private static void AdjustFleet(Game game, Snapshot snap, Genome genome, Line[] lineOf, ref Result res)
        {
            int n = genome.RouteCount;
            var type = PreferredLocomotive(game);

            // vagoes primeiro: um trem movido leva os vagoes junto, entao e
            // melhor ja ter tirado os que sobram
            var spareCars = new List<Railcar>();
            for (int r = 0; r < n; r++)
            {
                var line = lineOf[r];
                if (line == null) continue;
                int extra = CarsOf(line) - genome.Cars[r];
                if (extra > 0) spareCars.AddRange(PickCars(line, extra));
            }
            for (int r = 0; r < n; r++)
            {
                var line = lineOf[r];
                if (line == null) continue;
                int missing = genome.Cars[r] - CarsOf(line);
                res.CarriagesWanted += Math.Max(0, missing);
                for (int i = 0; i < missing; i++)
                {
                    if (TryApplyAsset(line, AssetType.Carriage)) { res.CarriagesPlaced++; continue; }
                    if (spareCars.Count == 0) break;
                    var car = spareCars[spareCars.Count - 1];
                    spareCars.RemoveAt(spareCars.Count - 1);
                    if (MoveCar(car, line)) { res.CarriagesPlaced++; res.CarsMoved++; }
                }
            }

            // trens que sobram (os mais vazios e sem vagao primeiro)
            var spare = new List<Train>();
            for (int r = 0; r < n; r++)
            {
                var line = lineOf[r];
                if (line == null) continue;
                int extra = TrainCountOf(line) - genome.Locos[r];
                if (extra > 0) spare.AddRange(PickTrains(line, extra));
            }

            for (int r = 0; r < n; r++)
            {
                var line = lineOf[r];
                if (line == null) continue;
                int want = genome.Locos[r];
                int missing = Math.Max(0, want - TrainCountOf(line));
                res.LocomotivesWanted += missing;
                bool loop = genome.Loops[r] && genome.Routes[r].Count >= 3;
                if (loop)
                {
                    int before = TrainCountOf(line);
                    int moved = 0;
                    PlaceLoopTrains(game, snap, line, genome.Routes[r], want, genome.RevOf(r), spare, ref moved);
                    res.TrainsMoved += moved;
                    res.LocomotivesPlaced += Math.Max(0, TrainCountOf(line) - before);
                }
                else
                {
                    for (int i = 0; i < missing; i++)
                    {
                        if (TryApplyAsset(line, type)) { res.LocomotivesPlaced++; continue; }
                        if (spare.Count == 0) break;
                        var t = spare[0];
                        spare.RemoveAt(0);
                        if (MoveTrain(t, line, null, LineDirection.FORWARDS)) { res.LocomotivesPlaced++; res.TrainsMoved++; }
                    }
                }
                if (TrainCountOf(line) < want || CarsOf(line) < genome.Cars[r])
                {
                    _pending.Add(new Pending
                    {
                        Line = line, Route = genome.Routes[r], Loop = loop,
                        Locos = want, Cars = genome.Cars[r], Rev = genome.RevOf(r),
                    });
                    res.TrainsPending += Math.Max(0, want - TrainCountOf(line));
                }
            }
        }

        private static List<Train> PickTrains(Line line, int k)
        {
            var list = new List<Train>();
            for (int i = 0; i < line.TrainCount; i++)
            {
                var t = line.GetTrain(i);
                if (t == null || t.Locomotive == null || t.Locomotive.State == RailcarState.MOTHBALLED) continue;
                list.Add(t);
            }
            list.Sort((a, b) =>
            {
                int c = (a.RailcarCount).CompareTo(b.RailcarCount);
                return c != 0 ? c : a.PeepCount.CompareTo(b.PeepCount);
            });
            if (list.Count > k) list.RemoveRange(k, list.Count - k);
            return list;
        }

        private static List<Railcar> PickCars(Line line, int k)
        {
            var list = new List<Railcar>();
            for (int i = 0; i < line.TrainCount && list.Count < k; i++)
            {
                var t = line.GetTrain(i);
                if (t == null) continue;
                for (int j = t.RailcarCount - 1; j >= 1 && list.Count < k; j--)
                {
                    var rc = t.GetRailcar(j);
                    if (rc != null && rc.State == RailcarState.ACTIVE) list.Add(rc);
                }
            }
            return list;
        }

        /// <summary>
        /// O "arrastar o trem para outra linha" do jogo (AssetBuilder): o trem
        /// antigo fica marcado, termina o trecho, desembarca todo mundo e so entao
        /// o novo (ja na linha de destino) comeca a andar.
        /// </summary>
        private static bool MoveTrain(Train t, Line target, TrackPosition? pos, LineDirection dir)
        {
            var loco = t.Locomotive;
            if (loco == null) return false;
            var type = t.Definition.Type;
            try
            {
                loco.State = RailcarState.MOTHBALLED;
                if (SafeApply(target, type, pos, dir, loco)) return true;
            }
            catch (Exception e) { Log.Warn("Applier: mover trem: " + e.Message); }
            try { loco.State = RailcarState.ACTIVE; } catch (Exception) { }
            return false;
        }

        private static bool MoveCar(Railcar car, Line target)
        {
            try
            {
                car.State = RailcarState.MOTHBALLED;
                if (SafeApply(target, AssetType.Carriage, null, LineDirection.FORWARDS, car)) return true;
            }
            catch (Exception e) { Log.Warn("Applier: mover vagao: " + e.Message); }
            try { car.State = RailcarState.ACTIVE; } catch (Exception) { }
            return false;
        }

        private static int CountPassengers(City city)
        {
            int total = 0;
            try
            {
                foreach (var s in GameHook.GetAllStations(city)) total += s.PeepCount;
                for (int i = 0; i < city.LineCount; i++)
                {
                    var l = city.GetLine(i);
                    if (l != null) total += l.PeepCount;
                }
            }
            catch (Exception) { }
            return total;
        }

        /// <summary>
        /// Mensagem que diz o que aconteceu E por que faltou. "0 vagoes" sozinho
        /// parece defeito do mod; "0 vagoes (nenhum no inventario)" e informacao.
        /// </summary>
        private static string Describe(Result res, Snapshot snap)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("{0} mantida(s), {1} editada(s) (+{2} estacoes), {3} desenhada(s), {4} apagada(s)",
                res.LinesKept, res.LinesEdited, res.StationsInserted, res.LinesBuilt, res.LinesRemoved);
            if (res.LinesFailed > 0) sb.AppendFormat(", {0} recusada(s) pelo jogo", res.LinesFailed);
            sb.AppendFormat(" | trens +{0}/{1}", res.LocomotivesPlaced, res.LocomotivesWanted);
            if (res.TrainsMoved > 0) sb.AppendFormat(" ({0} movidos)", res.TrainsMoved);
            if (res.TrainsPending > 0) sb.AppendFormat(" ({0} esperando o estoque)", res.TrainsPending);
            sb.AppendFormat(" | vagoes +{0}/{1}", res.CarriagesPlaced, res.CarriagesWanted);
            if (res.CarsMoved > 0) sb.AppendFormat(" ({0} movidos)", res.CarsMoved);
            sb.AppendFormat(" | travessias {0} (modelo {1})", res.CrossingsGame, res.CrossingsModel);
            if (res.PassengersAfter != res.PassengersBefore)
                sb.AppendFormat(" | passageiros {0} -> {1}", res.PassengersBefore, res.PassengersAfter);

            if (res.LinesWithoutTrain > 0)
                sb.AppendFormat(" | ATENCAO: {0} linha(s) SEM TREM", res.LinesWithoutTrain);
            else if (res.CarriagesWanted == 0 && snap.CarsTotal == 0)
                sb.Append(" (a cidade ainda nao deu nenhum vagao)");
            return sb.ToString();
        }

        private static AssetType PreferredLocomotive(Game game)
        {
            try
            {
                var t = game.AssetDatabase.AvailableLocomotiveAsset;
                if (t == AssetType.Locomotive || t == AssetType.Shinkansen || t == AssetType.Tram) return t;
            }
            catch (Exception) { }
            return AssetType.Locomotive;
        }

        private static int TrainCountOf(Line line)
        {
            try { return line.ActiveTrainCount; } catch (Exception) { return 0; }
        }

        private static int CarsOf(Line line)
        {
            try { return line.CarriageCount; } catch (Exception) { return 0; }
        }

        private static bool SafeLoop(Line line)
        {
            try { return line.IsLooping; } catch (Exception) { return false; }
        }

        private static bool TryApplyAsset(Line line, AssetType type)
        {
            return SafeApply(line, type, null, LineDirection.FORWARDS, null);
        }

        private static int _applyErrors;

        /// <summary>
        /// Line.ApplyAsset sem deixar estrago. Line.AddTrain poe o trem na lista
        /// da linha ANTES de Train.Start criar a locomotiva; se o Start estoura
        /// no meio, sobra um trem oco, e Line.ActiveTrainCount (que o jogo chama
        /// em Game.IsPaused, no placar de conquistas...) passa a estourar em todo
        /// frame (Londres, 2026-09-25, logo depois de encaixar uma estacao no meio
        /// de um trecho). Aqui a falha vai para o log e o trem oco sai da linha.
        /// </summary>
        private static bool SafeApply(Line line, AssetType type, TrackPosition? pos, LineDirection dir, Railcar existing)
        {
            try { return line.ApplyAsset(type, pos, dir, existing, true, false, false); }
            catch (Exception e)
            {
                if (_applyErrors++ < 5)
                    Log.Warn("Applier: ApplyAsset(" + type + (existing != null ? ", movido" : "") + ") na linha "
                             + SafeIndex(line) + " estourou: " + e);
                ScrubOrphans(line);
                return false;
            }
        }

        private static FieldInfo _lineTrains;

        /// <summary>Tira da linha os trens sem locomotiva (ver SafeApply).</summary>
        private static void ScrubOrphans(Line line)
        {
            try
            {
                if (_lineTrains == null)
                    _lineTrains = typeof(Line).GetField("trains", BindingFlags.NonPublic | BindingFlags.Instance);
                var list = _lineTrains != null ? _lineTrains.GetValue(line) as List<Train> : null;
                if (list == null) return;
                int removed = 0;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var t = list[i];
                    if (t == null || t.Locomotive != null) continue;
                    // Line.RemoveTrain le a locomotiva: tira direto da lista
                    try { if (!t.Link.IsNull) t.Link.Link.RemoveTrain(t); } catch (Exception) { }
                    list.RemoveAt(i);
                    removed++;
                }
                if (removed > 0)
                    Log.Warn("Applier: " + removed + " trem(ns) sem locomotiva tirado(s) da linha " + SafeIndex(line) + ".");
            }
            catch (Exception e) { Log.Warn("Applier: limpeza de trem sem locomotiva: " + e.Message); }
        }

        private static int SafeIndex(Line line)
        {
            try { return line.Index; } catch (Exception) { return -1; }
        }

        /// <summary>
        /// Loop: o jogo alterna o sentido de cada trem novo (Line.AddTrain). Aqui
        /// cada trem vai no sentido que o genoma pediu: `want - rev` na ordem da
        /// rota e `rev` contra. Trem sobrando num sentido que falta no outro e
        /// virado; os que faltam entram com posicao e sentido explicitos,
        /// espalhados pelo loop - do estoque ou, se ele acabou, movidos de
        /// `spare` (trens que sobram em outra linha).
        /// </summary>
        private static void PlaceLoopTrains(Game game, Snapshot snap, Line line, List<int> route, int want, int rev, List<Train> spare, ref int moved)
        {
            try
            {
                var links = new List<Link>();
                for (int i = 0; i < line.Count; i++)
                {
                    var lk = line[i];
                    if (lk != null && lk.State == LinkState.ACTIVE && lk.Start != null && lk.End != null
                        && lk.Start.Station != null && lk.End.Station != null)
                        links.Add(lk);
                }
                if (links.Count == 0) return;

                // FORWARDS do jogo anda na ordem da rota do genoma?
                bool fwdIsGenome = true;
                int m = route.Count;
                for (int k = 0; k < m; k++)
                {
                    if (snap.StationRefs[route[k]] != links[0].Start.Station) continue;
                    fwdIsGenome = snap.StationRefs[route[(k + 1) % m]] == links[0].End.Station;
                    break;
                }

                int[] target = { Math.Max(0, want - rev), Math.Max(0, rev) };
                int[] have = new int[2];
                var trains = new List<Train>();
                for (int k = 0; k < line.TrainCount; k++)
                {
                    var t = line.GetTrain(k);
                    if (t == null || t.Link.IsNull || t.Locomotive == null || t.Locomotive.State == RailcarState.MOTHBALLED) continue;
                    trains.Add(t);
                    have[GenomeDir(t.Link.Direction, fwdIsGenome)]++;
                }
                // trem sobrando num sentido que falta no outro: vira
                foreach (var t in trains)
                {
                    int d = GenomeDir(t.Link.Direction, fwdIsGenome);
                    if (have[d] > target[d] && have[1 - d] < target[1 - d])
                    {
                        t.HandleLinkReversed();
                        have[d]--;
                        have[1 - d]++;
                        line.City.DirtyConnectivity();
                    }
                }

                var type = PreferredLocomotive(game);
                int total = have[0] + have[1];
                for (int d = 0; d < 2; d++)
                {
                    while (have[d] < target[d])
                    {
                        // espalha: o i-esimo trem entra no link i*L/T
                        var lk = links[(total * links.Count / Math.Max(1, want)) % links.Count];
                        bool gameFwd = (d == 0) == fwdIsGenome;
                        var pos = gameFwd
                            ? new TrackPosition(lk.FirstTrack, 0f)
                            : new TrackPosition(lk.LastTrack, lk.LastTrack.Length);
                        var dir = gameFwd ? LineDirection.FORWARDS : LineDirection.BACKWARDS;
                        bool ok = SafeApply(line, type, pos, dir, null);
                        if (!ok && spare != null && spare.Count > 0)
                        {
                            var t = spare[0];
                            spare.RemoveAt(0);
                            ok = MoveTrain(t, line, pos, dir);
                            if (ok) moved++;
                        }
                        if (!ok) return;
                        have[d]++;
                        total++;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("Applier: sentido dos trens do loop: " + e.Message);
            }
        }

        private static int GenomeDir(LineDirection dir, bool fwdIsGenome)
        {
            bool fwd = dir == LineDirection.FORWARDS;
            return fwd == fwdIsGenome ? 0 : 1;
        }

        private static void CancelPendingEdit(Game game)
        {
            try
            {
                var lb = game.LineBuilder;
                if (lb != null && lb.IsBuilding) lb.HandleTouchCanceled();
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Apaga a linha como o jogador apaga: os trens terminam o trecho,
        /// desembarcam todo mundo na proxima estacao e somem. Libera a linha e as
        /// travessias na hora. (Line.Remove sumiria com quem esta a bordo.)
        /// </summary>
        private static void MothballLine(Line line)
        {
            try { line.Mothball(); }
            catch (Exception e) { Log.Warn("Falha ao apagar linha: " + e.Message); }
        }

        /// <summary>
        /// Constroi uma linha simulando o gesto de arrastar por cima das estacoes.
        /// Devolve a Line criada, ou null se o jogo recusou a jogada.
        /// </summary>
        private static Line BuildLine(Game game, Snapshot snap, List<int> route, bool loop)
        {
            var lb = game.LineBuilder;
            if (lb == null) return null;
            if (lb.IsBuilding) lb.HandleTouchCanceled();

            var first = snap.StationRefs[route[0]];
            if (first == null) return null;

            lb.HandleStationTouchBegan(first, ToGlobal(game, first.Position));
            if (!lb.IsBuilding)
            {
                if (Log.Verbose)
                    Log.Warn("LineBuilder recusou iniciar em " + route[0] + " (sem linha livre?)");
                return null;
            }

            var line = lb.Line;

            for (int k = 1; k < route.Count; k++)
            {
                var st = snap.StationRefs[route[k]];
                if (st == null) continue;
                lb.HandleTouchMove(ToGlobal(game, st.Position));
                RefreshTracks(line);
                lb.HandleStationTouchOver(st);
                if (!lb.IsBuilding) break; // o jogo fechou a linha sozinho
            }

            // Fechar o loop (e soltar o gesto) roda o TipSystem, que percorre os
            // trilhos de cada link. Montando a linha inteira num frame, os links
            // ainda nao geraram trilho (isso e lazy, no Link.Update) e o jogo
            // estourava NullReference em Station.GetAmbiguousSections.
            RefreshTracks(line);
            if (loop && lb.IsBuilding && route.Count >= 3)
            {
                lb.HandleTouchMove(ToGlobal(game, first.Position));
                lb.HandleStationTouchOver(first);
                RefreshTracks(line);
            }

            if (lb.IsBuilding) lb.HandleTouchEnded();

            return line != null && !line.IsMothballed && line.Index >= 0 ? line : null;
        }

        // List.GetRange/Reverse/IndexOf nao estao todos no mscorlib stripado do
        // jogo (GetRange estourou MissingMethodException): feitos a mao.
        private static List<int> Slice(List<int> g, int from, int count)
        {
            var r = new List<int>(Math.Max(0, count));
            for (int i = 0; i < count; i++) r.Add(g[from + i]);
            return r;
        }

        private static void Reverse(List<int> v)
        {
            for (int i = 0, j = v.Count - 1; i < j; i++, j--) { int t = v[i]; v[i] = v[j]; v[j] = t; }
        }

        private static int Find(List<int> g, int x)
        {
            for (int i = 0; i < g.Count; i++) if (g[i] == x) return i;
            return -1;
        }

        private static string Join(IEnumerable<int> v)
        {
            var sb = new StringBuilder();
            foreach (int x in v) { if (sb.Length > 0) sb.Append('-'); sb.Append(x); }
            return sb.ToString();
        }

        private static FieldInfo _profileData;
        private static MethodInfo _isTipRequired, _setTipRequired;

        /// <summary>
        /// A dica "estacao ambigua" (AmbiguousStationTip) roda em Line.Start e
        /// percorre os trilhos de todos os links da linha. Construindo a linha num
        /// frame so, o link recem-criado ainda nao tem trilho e o jogo estoura
        /// NullReference em Station.GetAmbiguousSections - e a linha fica pela
        /// metade. Enquanto o perfil ainda "precisa" dessa dica (perfil novo), a
        /// desligamos so durante o Apply, direto no ProfileData em memoria (sem
        /// Profile.Save), e religamos no fim. Devolve true se desligou.
        /// </summary>
        private static bool SetAmbiguousTipRequired(bool required)
        {
            try
            {
                if (_profileData == null)
                {
                    _profileData = typeof(Profile).GetField("profileData", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_profileData == null) return false;
                    var t = _profileData.FieldType;
                    _isTipRequired = t.GetMethod("IsTipRequired", new[] { typeof(Type) });
                    _setTipRequired = t.GetMethod("SetTipRequired", new[] { typeof(Type), typeof(bool) });
                }
                if (_isTipRequired == null || _setTipRequired == null) return false;
                var pd = _profileData.GetValue(Profile.Instance);
                if (pd == null) return false;
                var tip = AmbiguousStationTip.Type;
                bool now = (bool)_isTipRequired.Invoke(pd, new object[] { tip });
                if (now == required) return false;
                _setTipRequired.Invoke(pd, new object[] { tip, required });
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("Applier: nao consegui mexer na dica de estacao ambigua: " + e.Message);
                return false;
            }
        }

        /// <summary>Gera os trilhos pendentes de todos os links da linha.</summary>
        private static void RefreshTracks(Line line)
        {
            if (line == null) return;
            try
            {
                for (int i = 0; i < line.Count; i++)
                {
                    var link = line[i];
                    if (link != null) link.Update();
                }
                // Link.Update so regenera a lista de trilhos; quem liga
                // NextTrack/PreviousTrack e soma o comprimento e o GenerateGeo do
                // LateUpdate (depois da solda, que olha os vizinhos). Sem isso, um
                // trem posto no mesmo frame estoura em TrackPosition.LinkDistance,
                // que anda de FirstTrack por NextTrack (Londres, 2026-09-25).
                for (int i = 0; i < line.Count; i++)
                {
                    var link = line[i];
                    if (link != null) link.LateUpdate();
                }
            }
            catch (Exception e) { Log.Warn("RefreshTracks: " + e.Message); }
        }

        /// <summary>
        /// Station.Position esta no espaco local da CityLayer, mas o LineBuilder
        /// espera coordenadas globais (ele mesmo converte de volta).
        /// </summary>
        private static Vector2 ToGlobal(Game game, Vector2 localPos)
        {
            try { return game.City.CityLayer.LocalToGlobal(localPos); }
            catch (Exception) { return localPos; }
        }
    }
}
