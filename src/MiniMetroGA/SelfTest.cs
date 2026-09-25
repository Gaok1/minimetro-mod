using System;
using System.Collections.Generic;
using MiniMetroGA.Core;
using MiniMetroGA.Ga;
using UnityEngine;

namespace MiniMetroGA
{
    /// <summary>
    /// Teste de ponta a ponta sem mao humana. So existe se a variavel de ambiente
    /// MINIMETROGA_SELFTEST tiver o id de uma cidade (ex.: "london"):
    ///
    ///   1. abre a cidade no modo classico;
    ///   2. joga sozinho, como o modo automatico do painel: a cada
    ///      MINIMETROGA_SELFTEST_EVERY segundos (default 6) pausa, otimiza, mexe
    ///      na rede (Applier, so no que mudou) e despausa, em velocidade 2x, ate
    ///      MINIMETROGA_SELFTEST_SECONDS (default 180) ou o game over. Com
    ///      MINIMETROGA_SELFTEST_PAUSE=0 o jogo segue rodando enquanto calcula;
    ///   3. segunda-feira: o UpgradeAdvisor escolhe o upgrade pela recomendacao
    ///      do nucleo e poe o interchange onde ele mandar;
    ///   4. loga o placar e, com MINIMETROGA_SELFTEST_QUIT=1, fecha o jogo.
    ///
    /// Com MINIMETROGA_SELFTEST_START_WEEK=N a partida pula para a semana N antes
    /// da primeira rodada (Core/WeekJump): estacoes, upgrades e demanda daquela
    /// semana, rede vazia.
    ///
    /// Com MINIMETROGA_SELFTEST_MODE=validate, de tempos em tempos (a cada
    /// MINIMETROGA_VALIDATE_GAP s reais, default 20) aplica a melhor rede e a
    /// CONGELA por MINIMETROGA_VALIDATE_WINDOW s de jogo (default 30), medindo no
    /// jogo o que o modelo previu (ValidationWindow -> validation.jsonl).
    ///
    /// Tudo vai para o MiniMetroGA.log com o prefixo "SELFTEST".
    /// </summary>
    public sealed class SelfTest
    {
        private enum Phase { WaitMenu, WaitGame, Jump, FirstSolve, Play, Solving, Done }

        private readonly string _city;
        private readonly float _duration;
        private readonly float _every;
        private readonly bool _quit;
        private Phase _phase = Phase.WaitMenu;
        private float _t0, _phaseAt, _nextSolve;
        private readonly EngineHost _host = new EngineHost();
        private readonly bool _pauseWhileSolving;
        private bool _pausedByUs;

        /// <summary>Quem escolhe o upgrade (do ModBehaviour, dividido com o painel).</summary>
        public UpgradeAdvisor Advisor;
        private Snapshot _snap;
        private int _rounds, _applied;
        private long _nativeMsTotal;
        private int _maxStations;
        private int _skipped;
        private int _passLost;
        private int _settleWaits;
        private bool _waitingSettle;
        private readonly int _startWeek;
        private WeekJump _jump;
        private readonly bool _validate;
        private readonly float _valWindow, _valGap;
        private readonly int _valFromDay;
        private float _nextWindow;
        private ValidationWindow _window;
        private int _windows;

        /// <summary>O self-test esta dirigindo a partida (o painel nao pode aplicar rede).</summary>
        public bool OwnsGame => _phase != Phase.Done && _phase != Phase.WaitMenu;

        public static SelfTest TryCreate()
        {
            string city = Env("MINIMETROGA_SELFTEST");
            if (string.IsNullOrEmpty(city)) return null;
            return new SelfTest(city);
        }

        private SelfTest(string city)
        {
            _city = city;
            _duration = ParseF(Env("MINIMETROGA_SELFTEST_SECONDS"), 180f);
            _every = ParseF(Env("MINIMETROGA_SELFTEST_EVERY"), 6f);
            _quit = Env("MINIMETROGA_SELFTEST_QUIT") == "1";
            _validate = Env("MINIMETROGA_SELFTEST_MODE") == "validate";
            _valWindow = ParseF(Env("MINIMETROGA_VALIDATE_WINDOW"), 30f);
            _valGap = ParseF(Env("MINIMETROGA_VALIDATE_GAP"), 20f);
            _valFromDay = (int)ParseF(Env("MINIMETROGA_VALIDATE_FROM_DAY"), 0f);
            _pauseWhileSolving = Env("MINIMETROGA_SELFTEST_PAUSE") != "0";
            _startWeek = (int)ParseF(Env("MINIMETROGA_SELFTEST_START_WEEK"), 0f);
            Log.Info("SELFTEST ativo: cidade=" + city + " duracao=" + _duration + "s intervalo=" + _every + "s peso de mexer=" + Cfg().ChangeWeight
                     + (_startWeek > 0 ? " largada na semana " + _startWeek : ""));
        }

        public static GaConfig Cfg()
        {
            // MINIMETROGA_SELFTEST_CHANGE_WEIGHT: peso do custo de mexer na rede,
            // para comparar partidas (1 = o default do painel)
            return new GaConfig
            {
                PopulationSize = 200, Generations = 5000, TimeBudgetMs = 1500, Seed = 4242, DumpProblem = true,
                ChangeWeight = ParseF(Env("MINIMETROGA_SELFTEST_CHANGE_WEIGHT"), 1f),
            };
        }

        public void Tick()
        {
            float now = Time.realtimeSinceStartup;
            var game = GameHook.Current;
            switch (_phase)
            {
                case Phase.WaitMenu:
                    if (now < 6f || Main.Instance == null) return;
                    if (game == null)
                    {
                        var def = CityDatabase.Instance[_city];
                        if (def == null) { Fail("cidade desconhecida: " + _city); return; }
                        Log.Info("SELFTEST abrindo " + _city + " (classico)");
                        Main.Instance.StartGame(GameMode.CLASSIC, null, def);
                    }
                    Enter(Phase.WaitGame);
                    return;

                case Phase.WaitGame:
                    if (game == null || game.City == null) { if (now - _phaseAt > 30f) Fail("a partida nao abriu"); return; }
                    if (game.City.Clock.Time < 1.5f) return;
                    if (_startWeek > 0 && _jump == null)
                    {
                        _jump = WeekJump.Start(game, _startWeek);
                        Enter(Phase.Jump);
                        return;
                    }
                    StartFirstSolve(game);
                    return;

                case Phase.Jump:
                    if (game == null) { Finish("a partida sumiu"); return; }
                    // o jogo abre as estacoes e agenda os premios das semanas
                    // puladas nos proximos pulsos; o recomendador escolhe cada um
                    if (Advisor != null && Advisor.Tick(game, Cfg(), true)) return;
                    if (now - _phaseAt < 2f || SafePendingAssets(game) > 0) return;
                    _jump.Finish(game);
                    StartFirstSolve(game);
                    return;

                case Phase.FirstSolve:
                    if (_host.IsRunning) return;
                    if (_host.Status == GaState.Failed) { Fail("nativo falhou: " + _host.Error); return; }
                    Log.Info(string.Format("SELFTEST nativo: {0} geracoes, {1} avaliacoes em {2} ms ({3:N1} us/aval de CPU, {4} do cache), melhor {5:N2}, rede atual {6}",
                        _host.Generation, _host.EvaluationsDone, _host.ElapsedMs, _host.MicrosPerEval,
                        _host.CacheHits, _host.BestFitness, Fmt(_host.CurrentNetworkFitness)));
                    ApplyBest(game);
                    try { game.ScheduledTimeScale = TimeScale.Double; } catch (Exception) { }
                    _t0 = now;
                    _nextSolve = now + _every;
                    _nextWindow = now + _valGap;
                    Enter(Phase.Play);
                    return;

                case Phase.Play:
                    if (game == null) { Finish("a partida sumiu"); return; }
                    if (SafeOver(game))
                    {
                        if (_window != null) _window.Abort(game, "game over");
                        Finish("game over");
                        return;
                    }
                    if (Advisor != null && Advisor.Tick(game, Cfg(), true)) return;
                    if (now - _t0 > _duration)
                    {
                        if (_window != null) _window.Abort(game, "tempo esgotado");
                        Finish("tempo esgotado");
                        return;
                    }
                    if (_window != null)
                    {
                        // rede congelada: so mede
                        _window.Tick(game);
                        if (!_window.Done) return;
                        _window = null;
                        _nextWindow = now + _valGap;
                        _nextSolve = now;
                    }
                    if (now < _nextSolve) return;
                    if (Advisor != null && Advisor.Busy) return;
                    if (Applier.IsSettling(game))
                    {
                        if (!_waitingSettle) { _waitingSettle = true; _settleWaits++; }
                        return;
                    }
                    _waitingSettle = false;
                    _snap = Snapshot.Capture(game, Cfg());
                    if (_snap == null) { _nextSolve = now + _every; return; }
                    if (_snap.N > _maxStations) _maxStations = _snap.N;
                    if (_pauseWhileSolving && !SafePaused(game))
                    {
                        // o jogador pode pausar e pensar; o modo automatico do painel faz igual
                        game.IsPaused = true;
                        _pausedByUs = true;
                    }
                    _host.Start(_snap, Cfg());
                    Enter(Phase.Solving);
                    return;

                case Phase.Solving:
                    if (_host.IsRunning) return;
                    _rounds++;
                    _nativeMsTotal += _host.ElapsedMs;
                    if (_host.Status == GaState.Done && game != null && !SafeOver(game))
                    {
                        string why;
                        if (_validate && now >= _nextWindow && SafeDay(game) >= _valFromDay)
                        {
                            // janela de validacao: aplica e congela
                            var best = _host.BestClone();
                            ApplyBest(game);
                            _window = ValidationWindow.Begin(++_windows, game, _host.SnapshotRef, best, _host, _valWindow);
                            if (_window == null) Log.Warn("VALIDACAO: sem previsao do modelo, janela pulada");
                            else Log.Info("VALIDACAO janela " + _windows + " aberta por " + _valWindow + " s de jogo");
                        }
                        else if (_host.WorthApplying(_host.BestClone(), Cfg().AutoApplyMinGain, out why)) ApplyBest(game);
                        else { _skipped++; Log.Info("SELFTEST mantida: " + why); }
                    }
                    if (_pausedByUs && game != null)
                    {
                        // Game.IsPaused conta as locomotivas de toda linha: se o
                        // jogo ficou num estado quebrado, a rodada segue mesmo assim
                        try { game.IsPaused = false; }
                        catch (Exception e) { Log.Warn("SELFTEST: despausar estourou: " + e.Message); }
                        _pausedByUs = false;
                    }
                    Log.Info(string.Format("SELFTEST rodada {0}: semana {1}, {2} estacoes, placar {3}, {4} ms, melhor {5:N1} (rede atual {6}){7}",
                        _rounds, SafeWeek(game), _snap.N, SafeScore(game), _host.ElapsedMs, _host.BestFitness,
                        Fmt(_host.CurrentNetworkFitness), _host.Status == GaState.Failed ? " ERRO " + _host.Error : ""));
                    _nextSolve = now + _every;
                    Enter(Phase.Play);
                    return;
            }
        }

        private void StartFirstSolve(Game game)
        {
            _snap = Snapshot.Capture(game, Cfg());
            if (_snap == null) { Fail("snapshot nulo"); return; }
            Log.Info(string.Format("SELFTEST snapshot: {0} estacoes ativas, {1} futuras",
                _snap.N, GameHook.GetFutureStations(game.City).Count));
            _host.Start(_snap, Cfg());
            if (!_host.IsNative) { Fail("nucleo nativo nao iniciou: " + _host.NativeNote); return; }
            Enter(Phase.FirstSolve);
        }

        private void ApplyBest(Game game)
        {
            var best = _host.BestClone();
            if (best == null || game == null) return;
            var res = Applier.Apply(game, _host.SnapshotRef, best);
            if (res.Ok) _applied++;
            // Mothball nao some com ninguem: diferenca aqui = bug no Applier
            if (res.PassengersAfter < res.PassengersBefore) _passLost += res.PassengersBefore - res.PassengersAfter;
            Log.Info("SELFTEST aplicado: " + res.Message + " | " + best.Describe(_host.SnapshotRef).Replace("\n", " ; "));
        }

        private void Finish(string why)
        {
            var game = GameHook.Current;
            if (_pausedByUs && game != null) { try { game.IsPaused = false; } catch (Exception) { } }
            Log.Info(string.Format("SELFTEST FIM ({0}): placar {1}, semana {2}, {3} rodadas, {4} aplicadas, {5} mantidas, {6} upgrades, {7} interchanges, ate {8} estacoes, {9:N0} ms medios por rodada, {10} janelas de validacao, {11} passageiros sumidos ao aplicar, {12} rodadas esperaram a rede assentar",
                why, SafeScore(game), SafeWeek(game), _rounds, _applied, _skipped,
                Advisor != null ? Advisor.Picks : 0, Advisor != null ? Advisor.InterchangesPlaced : 0, _maxStations,
                _rounds > 0 ? _nativeMsTotal / (double)_rounds : 0.0, _windows, _passLost, _settleWaits));
            Enter(Phase.Done);
            if (_quit) Application.Quit();
        }

        private void Fail(string why)
        {
            Log.Error("SELFTEST FALHOU: " + why);
            Enter(Phase.Done);
            if (_quit) Application.Quit();
        }

        private void Enter(Phase p)
        {
            _phase = p;
            _phaseAt = Time.realtimeSinceStartup;
        }

        private static string Fmt(double v)
        {
            return double.IsNaN(v) || v > double.MaxValue * 0.5 ? "-" : v.ToString("N1");
        }

        private static bool SafeOver(Game g) { try { return g.IsOver; } catch (Exception) { return false; } }
        private static bool SafePaused(Game g) { try { return g.IsPaused; } catch (Exception) { return false; } }
        private static int SafePendingAssets(Game g) { try { return g.PendingAssetCount; } catch (Exception) { return 0; } }
        private static int SafeScore(Game g) { try { return g != null ? g.Score : -1; } catch (Exception) { return -1; } }
        private static int SafeWeek(Game g) { try { return g != null ? g.City.Clock.Week : -1; } catch (Exception) { return -1; } }
        private static int SafeDay(Game g) { try { return g != null ? g.City.Clock.Day : -1; } catch (Exception) { return -1; } }

        private static string Env(string k)
        {
            try { return Environment.GetEnvironmentVariable(k); } catch (Exception) { return null; }
        }

        private static float ParseF(string s, float def)
        {
            float v;
            return !string.IsNullOrEmpty(s) && float.TryParse(s, out v) ? v : def;
        }
    }
}
