using System;
using System.Collections.Generic;
using System.Text;
using MiniMetroGA.Core;
using MiniMetroGA.Ga;
using UnityEngine;

namespace MiniMetroGA.UI
{
    public class GaWindow : IDisposable
    {
        private const int WindowId = 0x6A17;

        public bool Visible;
        public Hud Hud;

        private Rect _rect = new Rect(24, 24, 580, 700);
        private int _tab;
        private static readonly string[] TabNames = { "Controle", "Parametros", "Fitness", "Rede", "Log" };

        private readonly GaConfig _cfg = new GaConfig();
        private readonly EngineHost _engine = new EngineHost();

        /// <summary>Parametros atuais do painel (o UpgradeAdvisor usa os mesmos).</summary>
        public GaConfig Config => _cfg;

        /// <summary>Recomendacao de upgrade (criado pelo ModBehaviour).</summary>
        public UpgradeAdvisor Advisor;

        private Texture2D _chartTex;
        private Texture2D _netTex;
        private float _lastRedraw;

        // uma posicao de rolagem por aba, senao trocar de aba embaralha o scroll
        private readonly float[] _scroll = new float[5];
        private readonly float[] _contentH = new float[5];
        private Rect _viewport;

        private string _status = "Entre numa cidade e aperte F10.";
        private Color _statusColor;
        private bool _logScale = true;

        private bool _lockedByUs;
        private bool _resizing;
        private Vector2 _resizeGrab;

        // modo automatico
        private bool _auto;

        /// <summary>Self-test no controle: o modo automatico nao aplica nada.</summary>
        public bool AutoLocked { get; set; }
        private float _autoIntervalSec = 45f;
        private float _autoTimer;
        private bool _autoPauseWhileSolving = true;
        private bool _autoWaitingToApply;

        // inventario vivo (nao depende de ter rodado o AG)
        private GameHook.Inventory _inv;
        private float _invAt;

        // se o desenho estourar, mostramos o erro em vez de repetir o estouro
        private string _drawError;

        public GaWindow()
        {
            _statusColor = Theme.TextDim;
            // chute inicial coerente com o layout; o primeiro Repaint corrige
            _viewport = new Rect(0f, 132f, _rect.width - 24f, _rect.height - 190f);
        }

        // ==================================================================
        public void Tick()
        {
            Widgets.ReleaseIfMouseUp();
            var game = GameHook.Current;

            if (game != null && Time.unscaledTime - _invAt > 0.25f)
            {
                _inv = GameHook.ReadInventory(game);
                _invAt = Time.unscaledTime;
            }

            // enquanto o mouse esta em cima do painel, trava a entrada do jogo,
            // senao um clique na UI vira um traco de linha no mapa
            if (game != null)
            {
                bool over = Visible && _rect.Contains(GuiMousePos());
                if (over && !_lockedByUs) { SafeSetLocked(game, true); _lockedByUs = true; }
                else if (!over && _lockedByUs) { SafeSetLocked(game, false); _lockedByUs = false; }
            }

            if (_engine.Status == GaState.Done && _autoWaitingToApply && !AutoLocked)
            {
                _autoWaitingToApply = false;
                string why;
                var cand = _engine.BestClone();
                var cfgUsed = _engine.ConfigRef ?? _cfg;
                if (_engine.WorthApplying(cand, cfgUsed.AutoApplyMinGain, out why)) ApplyBest();
                else Status("Automatico: rede mantida (" + why + ").", Theme.TextDim);
                if (_autoPauseWhileSolving && game != null) game.IsPaused = false;
            }

            if (_auto && !AutoLocked && game != null && !_engine.IsRunning && !_autoWaitingToApply)
            {
                _autoTimer += Time.unscaledDeltaTime;
                // espera a ultima aplicacao assentar (ver Applier.IsSettling)
                if (_autoTimer >= _autoIntervalSec && !Applier.IsSettling(game))
                {
                    _autoTimer = 0f;
                    if (_autoPauseWhileSolving) game.IsPaused = true;
                    StartSolve();
                    _autoWaitingToApply = _engine.IsRunning;
                }
            }

            if (Hud != null)
            {
                Hud.PanelOpen = Visible;
                Hud.HasGame = game != null;
                Hud.GamePaused = game != null && SafeIsPaused(game);
                Hud.State = _engine.Status;
                Hud.Generation = _engine.Generation;
                Hud.TotalGenerations = _engine.TotalGenerations;
                Hud.BestFitness = _engine.BestFitness;
            }
        }

        private static bool SafeIsPaused(Game g)
        {
            try { return g.IsPaused; } catch (Exception) { return false; }
        }

        private static Vector2 GuiMousePos()
        {
            var m = Input.mousePosition;
            return new Vector2(m.x, Screen.height - m.y);
        }

        private static void SafeSetLocked(Game game, bool v)
        {
            try { game.IsLocked = v; } catch (Exception) { }
        }

        private void Status(string msg, Color color)
        {
            _status = msg;
            _statusColor = color;
            if (Hud != null) Hud.Say(msg, color);
        }

        // ==================================================================
        public void TogglePause()
        {
            var game = GameHook.Current;
            if (game == null) { Status("Nenhuma partida ativa.", Theme.Warn); return; }
            game.IsPaused = !game.IsPaused;
            Status(game.IsPaused ? "Jogo pausado." : "Jogo retomado.", Theme.Accent2);
        }

        public void StartSolve()
        {
            var game = GameHook.Current;
            if (game == null) { Status("Nenhuma partida ativa.", Theme.Warn); return; }

            Snapshot snap;
            try { snap = Snapshot.Capture(game, _cfg); }
            catch (Exception e)
            {
                Log.Error("Snapshot.Capture: " + e);
                Status("Falha ao ler o estado do jogo (ver log).", Theme.Danger);
                return;
            }

            if (snap == null) { Status("Precisa de pelo menos 2 estacoes.", Theme.Warn); return; }

            _engine.Start(snap, _cfg.Clone());
            Status(string.Format("AG rodando ({4}): {0} estacoes, ate {1} linhas operaveis, {2} trens, {3} vagoes.",
                snap.N, snap.LineBudget, snap.Locomotives, snap.Carriages, _engine.EngineName), Theme.Accent);
        }

        public void ApplyBest()
        {
            if (AutoLocked) { Status("Self-test rodando: aplicar esta desligado.", Theme.Warn); return; }
            var game = GameHook.Current;
            var best = _engine.BestClone();
            var snap = _engine.SnapshotRef;
            if (game == null || best == null || snap == null)
            {
                Status("Nada para aplicar - rode o AG (F10) antes.", Theme.Warn);
                return;
            }

            if (!ReferenceEquals(snap.GameRef, game))
            {
                Status("A solucao e de outra partida. Rode o AG de novo.", Theme.Warn);
                return;
            }

            var res = Applier.Apply(game, snap, best);
            Status(res.Message, res.Ok ? Theme.Accent : Theme.Danger);
            Log.Info("Aplicado: " + res.Message);
        }

        // ==================================================================
        public void Draw()
        {
            if (!Visible) return;
            Theme.Ensure();

            var old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0f, 0f, 0f, 0f); // a moldura e desenhada por nos
            _rect = GUI.Window(WindowId, _rect, DrawWindow, GUIContent.none);
            GUI.backgroundColor = old;

            // mantem o painel dentro da tela
            _rect.x = Mathf.Clamp(_rect.x, -_rect.width + 80f, Screen.width - 80f);
            _rect.y = Mathf.Clamp(_rect.y, 0f, Screen.height - 40f);
        }

        private void DrawWindow(int id)
        {
            var full = new Rect(0f, 0f, _rect.width, _rect.height);
            Theme.Fill(full, Theme.Bg);
            Theme.Fill(new Rect(0f, 0f, _rect.width, 3f), Theme.Accent);
            Theme.Frame(full, new Color(1f, 1f, 1f, 0.12f));

            if (_drawError != null) { DrawErrorState(); return; }

            try
            {
                GUILayout.BeginArea(new Rect(12f, 8f, _rect.width - 24f, _rect.height - 16f));
                DrawBody();
                GUILayout.EndArea();
            }
            catch (Exception e)
            {
                // Um estouro aqui deixa os grupos de layout desbalanceados e o
                // Unity passa a cuspir "pushing more GUIClips" todo frame. Melhor
                // congelar num estado de erro legivel e registrar uma vez so.
                _drawError = e.ToString();
                Log.Error("GaWindow.Draw: " + e);
            }

            DrawResizeGrip();
            GUI.DragWindow(new Rect(0f, 0f, _rect.width - 22f, 30f));
        }

        private void DrawBody()
        {
            var game = GameHook.Current;

            // --- cabecalho ---
            GUILayout.BeginHorizontal();
            GUILayout.Label("OTIMIZADOR GENETICO", Theme.Title);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("X", Theme.Btn, GUILayout.Width(26), GUILayout.Height(20)))
                Visible = false;
            GUILayout.EndHorizontal();

            GUILayout.Label(game == null ? "sem partida ativa" : Header(game), Theme.Dim);
            GUILayout.Space(4);

            _tab = Widgets.Tabs(_tab, TabNames);
            GUILayout.Space(6);

            // --- conteudo rolavel ---
            // GUILayoutUtility.GetRect devolve Rect(0,0,0,0) durante o evento
            // Layout - e o Layout e justamente o passe que dimensiona os controles.
            // Se abrissemos a area de rolagem com esse rect, tudo la dentro seria
            // medido com largura negativa e o painel apareceria vazio. Entao
            // guardamos o rect bom do ultimo Repaint e usamos ele em todos os passes.
            var measured = GUILayoutUtility.GetRect(10f, 10f,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (Event.current.type == EventType.Repaint && measured.width > 1f)
                _viewport = measured;

            // O conteudo e desenhado em TODOS os passes, sempre com a mesma
            // estrutura. Pular o bloco em alguns eventos desbalancearia os grupos
            // de layout ("pushing more GUIClips than you are popping").
            Widgets.BeginScroll(_viewport, ref _scroll[_tab], _contentH[_tab]);
            switch (_tab)
            {
                case 0: DrawControl(game); break;
                case 1: DrawParams(); break;
                case 2: DrawFitnessTab(); break;
                case 3: DrawNetworkTab(); break;
                default: DrawLogTab(); break;
            }
            _contentH[_tab] = Widgets.EndScroll(_contentH[_tab]);

            // --- barra de status ---
            GUILayout.Space(4);
            var bar = GUILayoutUtility.GetRect(10f, 22f, GUILayout.ExpandWidth(true));
            Theme.Fill(bar, Theme.CardAlt);
            Theme.Fill(new Rect(bar.x, bar.y, 3f, bar.height), _statusColor);
            var st = Theme.Mono;
            var prev = st.normal.textColor;
            st.normal.textColor = _statusColor;
            GUI.Label(new Rect(bar.x + 8f, bar.y, bar.width - 12f, bar.height), _status, st);
            st.normal.textColor = prev;
        }

        private void DrawErrorState()
        {
            GUILayout.BeginArea(new Rect(14f, 14f, _rect.width - 28f, _rect.height - 28f));
            GUILayout.Label("A UI estourou", Theme.Title);
            GUILayout.Space(4);
            GUILayout.Label(Trunc(_drawError, 900), Theme.Dim);
            GUILayout.Space(8);
            if (Widgets.Button("Tentar de novo", Theme.Accent2)) _drawError = null;
            GUILayout.EndArea();
            GUI.DragWindow(new Rect(0f, 0f, _rect.width, 30f));
        }

        private void DrawResizeGrip()
        {
            var grip = new Rect(_rect.width - 18f, _rect.height - 18f, 16f, 16f);
            var e = Event.current;

            if (e.type == EventType.MouseDown && e.button == 0 && grip.Contains(e.mousePosition))
            {
                _resizing = true;
                _resizeGrab = new Vector2(_rect.width - e.mousePosition.x, _rect.height - e.mousePosition.y);
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _resizing)
            {
                _rect.width = Mathf.Clamp(e.mousePosition.x + _resizeGrab.x, 420f, Screen.width - 40f);
                _rect.height = Mathf.Clamp(e.mousePosition.y + _resizeGrab.y, 320f, Screen.height - 40f);
                e.Use();
            }
            else if (e.type == EventType.MouseUp && _resizing)
            {
                _resizing = false;
                e.Use();
            }

            if (e.type == EventType.Repaint)
                for (int i = 0; i < 3; i++)
                    Theme.Fill(new Rect(grip.x + 4f + i * 4f, grip.yMax - 4f - i * 4f, 3f, 3f),
                        new Color(1f, 1f, 1f, 0.30f));
        }

        private static string Header(Game game)
        {
            try
            {
                return string.Format("{0}  -  semana {1}  -  score {2}  -  {3}",
                    game.Mode, game.Week, game.Score, game.IsPaused ? "PAUSADO" : "rodando");
            }
            catch (Exception) { return "partida ativa"; }
        }

        // ------------------------------------------------------------------
        private void DrawControl(Game game)
        {
            Widgets.BeginCard("Fluxo");
            GUILayout.BeginHorizontal();
            GUI.enabled = game != null;
            bool paused = game != null && SafeIsPaused(game);
            if (Widgets.Button(paused ? "Retomar  F9" : "Pausar  F9", paused ? Theme.Warn : Theme.Card, 30f))
                TogglePause();

            GUI.enabled = game != null && !_engine.IsRunning;
            if (Widgets.Button("Otimizar  F10", Theme.Accent, 30f)) StartSolve();

            GUI.enabled = _engine.IsRunning;
            if (Widgets.Button("Parar", Theme.Danger, 30f)) { _engine.Stop(); Status("AG interrompido.", Theme.Warn); }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUI.enabled = game != null && _engine.HasBest;
            if (Widgets.Button("Aplicar a melhor solucao  F11", Theme.Accent2, 34f)) ApplyBest();
            GUI.enabled = true;
            GUILayout.Label("aplicar mexe so no que mudou: linha igual fica, estacao nova e encaixada com os trens "
                            + "rodando, e linha que muda de verdade e apagada como o jogador apaga (ninguem some)", Theme.Dim);
            Widgets.EndCard();

            // ---- upgrade da semana ----
            Widgets.BeginCard("Upgrade da semana");
            _cfg.AutoPickUpgrade = Widgets.Toggle(_cfg.AutoPickUpgrade, "escolher sozinho o recomendado (e por o interchange)");
            GUILayout.Label(Advisor != null && Advisor.Status != null ? Advisor.Status
                : "na segunda-feira o nucleo otimiza a rede com cada oferta e recomenda a melhor", Theme.Dim);
            Widgets.EndCard();

            // ---- inventario ----
            Widgets.BeginCard("Inventario da cidade");
            if (game == null)
            {
                GUILayout.Label("entre numa cidade para ver", Theme.Dim);
            }
            else
            {
                GUILayout.Label("disponivel agora / total ja concedido", Theme.Dim);
                GUILayout.Space(2);
                InvRow("Linhas", _inv.LinesAvail, _inv.LinesTotal);
                InvRow("Trens", _inv.LocosAvail, _inv.LocosTotal);
                InvRow("Vagoes", _inv.CarsAvail, _inv.CarsTotal);
                InvRow("Tuneis / pontes", _inv.CrossingsAvail, _inv.CrossingsTotal);
                InvRow("Baldeacoes", _inv.InterchangesAvail, _inv.InterchangesTotal);
                GUILayout.Space(2);
                Widgets.Field("Estacoes no mapa", _inv.Stations.ToString());
                Widgets.Field("Linhas construidas", _inv.Lines.ToString());

                // O teto real de linhas: cada linha precisa de um trem, e o trem que
                // vem junto com a linha nova sai deste mesmo estoque.
                int operable = Mathf.Min(_inv.LinesTotal, _inv.LocosTotal);
                Widgets.Field("Linhas operaveis", operable.ToString(),
                    operable < _inv.LinesTotal ? Theme.Warn : Theme.Accent);
                if (operable < _inv.LinesTotal)
                    GUILayout.Label("ha mais linhas que trens - o AG limita a rede a " + operable +
                                    " linha(s) para nao deixar nenhuma parada", Theme.Dim);

                if (_inv.CarsTotal == 0)
                    GUILayout.Label("esta cidade ainda nao ofereceu nenhum vagao - por isso o AG nao aloca vagao nenhum", Theme.Dim);
            }
            Widgets.EndCard();

            DrawDemandCard();

            // ---- progresso ----
            Widgets.BeginCard("Progresso");
            float prog = _engine.Progress;

            var barRect = GUILayoutUtility.GetRect(100f, 20f, GUILayout.ExpandWidth(true));
            var runCfg = _engine.ConfigRef;
            string budget = runCfg != null && runCfg.TimeBudgetMs > 0
                ? string.Format("{0} geracoes, {1:N1}/{2:N1} s", _engine.Generation, _engine.ElapsedMs / 1000f, runCfg.TimeBudgetMs / 1000f)
                : string.Format("{0} / {1}", _engine.Generation, _engine.TotalGenerations);
            Widgets.Bar(barRect, prog, StateColor(_engine.Status), budget + "   " + StateName(_engine.Status));

            GUILayout.Space(3);
            Widgets.Field("Melhor fitness", _engine.BestFitness == double.MaxValue ? "-" : string.Format("{0:N1}", _engine.BestFitness), Theme.Accent);
            Widgets.Field("Media da populacao", string.Format("{0:N1}", _engine.AvgFitness));
            Widgets.Field("Estagnado ha", _engine.StagnantFor + " geracoes");
            Widgets.Field("Avaliacoes", string.Format("{0:N0} em {1:N0} ms", _engine.EvaluationsDone, _engine.ElapsedMs));
            Widgets.Field("Motor", _engine.EngineName, _engine.IsNative ? Theme.Accent : Theme.TextDim);
            if (_engine.IsNative)
            {
                var bb = _engine.BestClone();
                if (bb != null)
                    Widgets.Field("Mexida na rede atual", string.Format("{0} linha(s) refeita(s), {1} trem(ns) movido(s)",
                        bb.Breakdown.RebuiltLines, bb.Breakdown.MovedTrains));
                Widgets.Field("Custo por avaliacao", string.Format("{0:N0} us  ({1:N0} do cache)",
                    _engine.MicrosPerEval, _engine.CacheHits));
                double cur = _engine.CurrentNetworkFitness;
                if (!double.IsNaN(cur) && cur < double.MaxValue * 0.5 && _engine.BestFitness < double.MaxValue * 0.5)
                {
                    Widgets.Field("Rede atual (mesmo modelo)", string.Format("{0:N1}", cur));
                    double gain = cur > 0 ? 1.0 - _engine.BestFitness / cur : 0.0;
                    Widgets.Field("Ganho sobre a rede atual", string.Format("{0:P0}", gain),
                        gain > 0 ? Theme.Accent : Theme.Warn);
                }
            }
            else if (!string.IsNullOrEmpty(_engine.NativeNote))
            {
                GUILayout.Label("o otimizador nao rodou: " + Trunc(_engine.NativeNote, 160), Theme.Dim);
            }
            if (_engine.Status == GaState.Failed)
                GUILayout.Label("ERRO: " + Trunc(_engine.Error, 400), Theme.Dim);
            Widgets.EndCard();

            // ---- convergencia ----
            Widgets.BeginCard("Convergencia");
            RefreshTextures(null, null);
            if (_chartTex != null)
            {
                var r = GUILayoutUtility.GetRect(10f, ChartH, GUILayout.ExpandWidth(true));
                Theme.Blit(r, _chartTex, Color.white);
                Theme.Frame(r, new Color(1f, 1f, 1f, 0.10f));
            }
            GUILayout.BeginHorizontal();
            _logScale = Widgets.Toggle(_logScale, "escala logaritmica");
            GUILayout.EndHorizontal();
            GUILayout.Label("verde = melhor individuo    laranja = media da populacao", Theme.Dim);
            Widgets.EndCard();

            // ---- automatico ----
            Widgets.BeginCard("Modo automatico");
            _auto = Widgets.Toggle(_auto, "reotimizar e aplicar sozinho");
            _autoPauseWhileSolving = Widgets.Toggle(_autoPauseWhileSolving, "pausar o jogo enquanto calcula");
            _autoIntervalSec = Widgets.SliderRow("Intervalo", _autoIntervalSec, 10f, 300f, "{0:N0} s", Theme.Accent2);
            if (_auto)
                Widgets.Field("Proxima rodada", string.Format("{0:N0} s", Mathf.Max(0f, _autoIntervalSec - _autoTimer)), Theme.Warn);
            Widgets.EndCard();

            Widgets.BeginCard("Overlay");
            if (Hud != null) Hud.Visible = Widgets.Toggle(Hud.Visible, "mostrar as teclas no canto da tela");
            Widgets.EndCard();
        }

        private void InvRow(string label, int avail, int total)
        {
            float t = total > 0 ? avail / (float)total : 0f;
            var color = total == 0 ? Theme.Track : (avail > 0 ? Theme.Accent : Theme.Warn);
            Widgets.BarRow(label, t, color, avail + " / " + total);
        }

        private static Color StateColor(GaState s)
        {
            switch (s)
            {
                case GaState.Running: return Theme.Accent;
                case GaState.Done: return Theme.Accent2;
                case GaState.Failed: return Theme.Danger;
                case GaState.Cancelled: return Theme.Warn;
                default: return Theme.Track;
            }
        }

        private static string StateName(GaState s)
        {
            switch (s)
            {
                case GaState.Running: return "rodando";
                case GaState.Done: return "concluido";
                case GaState.Failed: return "falhou";
                case GaState.Cancelled: return "cancelado";
                default: return "parado";
            }
        }

        // ------------------------------------------------------------------
        private void DrawParams()
        {
            Widgets.BeginCard("Algoritmo genetico");
            _cfg.PopulationSize = Widgets.SliderRowInt("Populacao", _cfg.PopulationSize, 20, 1500, Theme.Accent);
            _cfg.Generations = Widgets.SliderRowInt("Geracoes (maximo)", _cfg.Generations, 20, 20000, Theme.Accent);
            _cfg.TimeBudgetMs = Widgets.SliderRowInt("Tempo de busca (ms, 0 = sem limite)", _cfg.TimeBudgetMs, 0, 20000, Theme.Accent);
            _cfg.Elitism = Widgets.SliderRowInt("Elitismo", _cfg.Elitism, 0, 20, Theme.Accent);
            _cfg.TournamentSize = Widgets.SliderRowInt("Torneio (k)", _cfg.TournamentSize, 2, 12, Theme.Accent);
            _cfg.CrossoverRate = Widgets.SliderRow("Taxa de crossover", _cfg.CrossoverRate, 0f, 1f, "{0:P0}", Theme.Accent);
            _cfg.MutationRate = Widgets.SliderRow("Taxa de mutacao", _cfg.MutationRate, 0f, 1f, "{0:P0}", Theme.Accent);
            _cfg.MutationsPerGenome = Widgets.SliderRowInt("Mutacoes por filho", _cfg.MutationsPerGenome, 1, 8, Theme.Accent);
            _cfg.StagnationRestart = Widgets.SliderRowInt("Reinjetar apos estagnar", _cfg.StagnationRestart, 0, 400, Theme.Accent);
            _cfg.SeedFromCurrentNetwork = Widgets.Toggle(_cfg.SeedFromCurrentNetwork, "semear com a rede atual do jogador");
            Widgets.EndCard();

            Widgets.BeginCard("Espaco de busca");
            _cfg.MaxLinesOverride = Widgets.SliderRowInt("Max de linhas (0 = auto)", _cfg.MaxLinesOverride, 0, 8, Theme.Accent2);
            _cfg.MinStationsPerLine = Widgets.SliderRowInt("Min estacoes por linha", _cfg.MinStationsPerLine, 2, 8, Theme.Accent2);
            _cfg.MaxStationsPerLine = Widgets.SliderRowInt("Max estacoes por linha", _cfg.MaxStationsPerLine, 3, 40, Theme.Accent2);
            _cfg.AllowLoops = Widgets.Toggle(_cfg.AllowLoops, "permitir linhas circulares");
            Widgets.EndCard();

            Widgets.BeginCard("Modelo de demanda e simulacao");
            GUILayout.Label("a demanda vem da tabela de spawn do proprio jogo; 1.00 = o que o jogo faz", Theme.Dim);
            GUILayout.Space(2);
            _cfg.DemandScale = Widgets.SliderRow("Escala de demanda", _cfg.DemandScale, 0.1f, 4f, "{0:N2}x", Theme.Warn);
            _cfg.CrowdWeight = Widgets.SliderRow("Peso das filas atuais", _cfg.CrowdWeight, 0f, 1f, "{0:N2}", Theme.Warn);
            GUILayout.Space(4);
            _cfg.CapacityFeedback = Widgets.Toggle(_cfg.CapacityFeedback, "lotacao vira espera (2o passe)");
            GUILayout.Label("desligar dobra a velocidade do AG, mas os vagoes deixam de ter efeito no tempo de viagem", Theme.Dim);
            _cfg.MaxCrowdingMultiplier = Widgets.SliderRow("Teto da espera por lotacao", _cfg.MaxCrowdingMultiplier, 1f, 10f, "{0:N1}x", Theme.Warn);
            _cfg.MaxCarsPerLoco = Widgets.SliderRowInt("Max vagoes por trem", _cfg.MaxCarsPerLoco, 1, 6, Theme.Warn);
            GUILayout.Space(4);
            GUILayout.Label("velocidade do trem: 0 = usar a real da cidade", Theme.Dim);
            _cfg.TrainSpeedOverride = Widgets.SliderRow("Velocidade (override)", _cfg.TrainSpeedOverride, 0f, 200f, "{0:N0} u/s", Theme.Warn);
            Widgets.EndCard();

            Widgets.BeginCard("Nucleo nativo (Rust)");
            bool loaded = NativeEngine.TryLoad();
            Widgets.Field("Biblioteca", loaded ? "carregada" : "indisponivel", loaded ? Theme.Accent : Theme.Warn);
            if (!loaded && !string.IsNullOrEmpty(NativeEngine.LoadError))
                GUILayout.Label(Trunc(NativeEngine.LoadError, 200), Theme.Dim);
            GUILayout.Label("o nucleo modela as regras do jogo (parada, baldeacao, cruzamentos, estacoes futuras, "
                            + "sentido dos trens de loop, tuneis/pontes)", Theme.Dim);
            _cfg.HorizonDays = Widgets.SliderRow("Horizonte", _cfg.HorizonDays, 0.25f, 7f, "{0:N2} dias", Theme.Accent2);
            _cfg.UrgencyWeight = Widgets.SliderRow("Urgencia (lotando agora)", _cfg.UrgencyWeight, 0f, 400f, "{0:N0}", Theme.Accent2);
            _cfg.LocalSearchEvery = Widgets.SliderRowInt("Busca local a cada", _cfg.LocalSearchEvery, 0, 200, Theme.Accent2);
            _cfg.NativeThreads = Widgets.SliderRowInt("Threads (0 = todas)", _cfg.NativeThreads, 0, 32, Theme.Accent2);
            _cfg.PlanFutureStations = Widgets.Toggle(_cfg.PlanFutureStations, "planejar para as estacoes que vao nascer");
            _cfg.DumpProblem = Widgets.Toggle(_cfg.DumpProblem, "salvar o problema em problems/last.bin (para o mmopt-bench)");
            Widgets.EndCard();

            Widgets.BeginCard("Pesos do fitness (menor = melhor)");
            _cfg.UnreachablePenalty = Widgets.SliderRow("Destino inalcancavel", _cfg.UnreachablePenalty, 0f, 5000f, "{0:N0}", Theme.Danger);
            _cfg.UnservedStationPenalty = Widgets.SliderRow("Estacao fora da rede", _cfg.UnservedStationPenalty, 0f, 3000f, "{0:N0}", Theme.Danger);
            _cfg.CongestionWeight = Widgets.SliderRow("Lotacao de linha", _cfg.CongestionWeight, 0f, 3000f, "{0:N0}", Theme.Danger);
            _cfg.StationLoadWeight = Widgets.SliderRow("Lotacao de estacao", _cfg.StationLoadWeight, 0f, 3000f, "{0:N0}", Theme.Danger);
            _cfg.TrackLengthWeight = Widgets.SliderRow("Custo de trilho", _cfg.TrackLengthWeight, 0f, 0.2f, "{0:N4}", Theme.Danger);
            _cfg.ChangeWeight = Widgets.SliderRow("Custo de mexer na rede atual", _cfg.ChangeWeight, 0f, 5f, "{0:N2}x", Theme.Danger);
            GUILayout.Label("rede que passa tunel/ponte a mais do que o estoque tem e descartada (o jogo nao deixa desenhar)", Theme.Dim);
            Widgets.EndCard();

            Widgets.BeginCard("Upgrade da semana");
            _cfg.UpgradeBudgetMs = Widgets.SliderRowInt("Busca por oferta (ms)", _cfg.UpgradeBudgetMs, 300, 10000, Theme.Accent2);
            _cfg.UpgradeHorizonDays = Widgets.SliderRow("Horizonte da recomendacao", _cfg.UpgradeHorizonDays, 1f, 14f, "{0:N1} dias", Theme.Accent2);
            Widgets.EndCard();

            Widgets.BeginCard(null);
            GUILayout.BeginHorizontal();
            if (Widgets.Button("Padroes", Theme.Card, 26f)) CopyDefaults();
            if (Widgets.Button("Rapido", Theme.Card, 26f)) Preset(100, 5000, 800);
            if (Widgets.Button("Caprichado", Theme.Card, 26f)) Preset(400, 20000, 10000);
            GUILayout.EndHorizontal();
            Widgets.EndCard();
        }

        private void Preset(int pop, int gens, int ms)
        {
            _cfg.PopulationSize = pop;
            _cfg.Generations = gens;
            _cfg.TimeBudgetMs = ms;
            Status(string.Format("Preset: populacao {0}, ate {1} geracoes ou {2:N1} s.", pop, gens, ms / 1000f), Theme.Accent2);
        }

        private void CopyDefaults()
        {
            var d = new GaConfig();
            foreach (var f in typeof(GaConfig).GetFields())
                f.SetValue(_cfg, f.GetValue(d));
            Status("Parametros restaurados.", Theme.Accent2);
        }

        // ------------------------------------------------------------------
        private void DrawFitnessTab()
        {
            var best = _engine.BestClone();
            if (best == null)
            {
                Widgets.BeginCard("Fitness");
                GUILayout.Label("Rode o AG (F10) para ver a decomposicao do custo.", Theme.Dim);
                Widgets.EndCard();
                return;
            }
            var b = best.Breakdown;

            Widgets.BeginCard("De onde vem o custo do melhor individuo");
            Row("Tempo de viagem", b.TravelCost, best.Fitness);
            Row("Destinos inalcancaveis", b.UnreachableCost, best.Fitness);
            Row("Lotacao de linha", b.CongestionCost, best.Fitness);
            Row("Lotacao de estacao", b.StationLoadCost, best.Fitness);
            Row("Comprimento de trilho", b.TrackCost, best.Fitness);
            Row("Tuneis acima do estoque (inviavel)", b.CrossingCost, best.Fitness);
            Row("Estacoes fora da rede", b.UnservedCost, best.Fitness);
            Row("Urgencia (lotando agora)", b.UrgencyCost, best.Fitness);
            Row("Mexer na rede atual", b.ChangeCost, best.Fitness);
            GUILayout.Space(4);
            Widgets.Field("TOTAL", string.Format("{0:N1}", best.Fitness), Theme.Accent);
            Widgets.EndCard();

            Widgets.BeginCard("Indicadores");
            Widgets.Field("Tempo medio de viagem", string.Format("{0:N1} s", b.AvgTravelTime));
            Widgets.Field("Pares sem rota", b.UnreachablePairs.ToString(), b.UnreachablePairs > 0 ? Theme.Danger : Theme.Accent);
            Widgets.Field("Estacoes desatendidas", b.UnservedStations.ToString(), b.UnservedStations > 0 ? Theme.Warn : Theme.Accent);
            Widgets.Field("Travessias usadas", b.CrossingsUsed.ToString());
            Widgets.Field("Trilho total", string.Format("{0:N0} u", b.TrackLength));
            Widgets.Field("Cruzamentos de trilho", b.LineCrossings.ToString());
            Widgets.Field("Linhas refeitas / trens movidos", b.RebuiltLines + " / " + b.MovedTrains);
            Widgets.Field("Pior lotacao de linha", string.Format("{0:P0}", b.WorstLineUtil),
                b.WorstLineUtil > 1f ? Theme.Danger : Theme.Accent);
            Widgets.EndCard();
        }

        private void Row(string label, double value, double total)
        {
            float frac = total > 0 ? (float)(value / total) : 0f;
            Widgets.BarRow(label, frac, Theme.Warn, string.Format("{0:N0}", value));
        }

        // ------------------------------------------------------------------
        private void DrawNetworkTab()
        {
            var best = _engine.BestClone();
            var snap = _engine.SnapshotRef;

            Widgets.BeginCard("Melhor rede encontrada");
            RefreshTextures(best, snap);
            if (_netTex != null)
            {
                var r = GUILayoutUtility.GetRect(10f, NetH, GUILayout.ExpandWidth(true));
                Theme.Blit(r, _netTex, Color.white, ScaleMode.ScaleToFit);
                Theme.Frame(r, new Color(1f, 1f, 1f, 0.10f));
            }
            GUILayout.Label("pontos vermelhos = estacoes fora de qualquer linha", Theme.Dim);
            Widgets.EndCard();

            Widgets.BeginCard("Rotas propostas");
            if (best == null || snap == null) GUILayout.Label("Rode o AG (F10) primeiro.", Theme.Dim);
            else
            {
                for (int r = 0; r < best.RouteCount; r++)
                {
                    var col = ToColor(Plot.LinePalette[r % Plot.LinePalette.Length]);
                    GUILayout.BeginHorizontal();
                    var swatch = GUILayoutUtility.GetRect(10f, 14f, GUILayout.Width(10f));
                    Theme.Fill(new Rect(swatch.x, swatch.y + 2f, 10f, 10f), col);
                    GUILayout.Label(string.Format("L{0}{1}  -  {2} trem(ns), {3} vagao(oes), {4} paradas",
                        r + 1, best.Loops[r] ? " circular" : "", best.Locos[r], best.Cars[r], best.Routes[r].Count),
                        Theme.Mono);
                    GUILayout.EndHorizontal();
                    GUILayout.Label("   " + RouteText(best, snap, r), Theme.Dim);
                    GUILayout.Space(3);
                }
            }
            Widgets.EndCard();
        }

        private static Color ToColor(Color32 c)
        {
            return new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
        }

        /// <summary>
        /// Mostra a demanda que o AG realmente usou: taxa de spawn total, de onde veio
        /// a tabela, e as estacoes que mais geram passageiro. Se o numero aqui nao
        /// bater com o que se ve no mapa, o problema esta no modelo, nao no AG.
        /// </summary>
        private void DrawDemandCard()
        {
            var snap = _engine.SnapshotRef;
            if (snap == null || snap.StationDemand == null) return;

            Widgets.BeginCard("Demanda (do ultimo snapshot)");

            Widgets.Field("Tabela de spawn",
                snap.SpawnTableFromGame ? "lida do jogo" : "replica interna",
                snap.SpawnTableFromGame ? Theme.Accent : Theme.Warn);
            Widgets.Field("Geracao total",
                (snap.TotalDemandRate * snap.DayLength).ToString("N1") + " pass/dia");
            Widgets.Field("Velocidade do trem",
                snap.TrainSpeed.ToString("N0") + " u/s",
                snap.ReadTrainDefFromGame ? Theme.Accent : Theme.Warn);
            Widgets.Field("Capacidade por vagao", snap.RailcarCapacity.ToString());

            if (!snap.ReadTrainDefFromGame)
                GUILayout.Label("nao consegui ler o TrainDefinition da cidade - velocidade e um chute", Theme.Dim);

            // top 5 geradoras
            int show = Mathf.Min(5, snap.N);
            var order = new int[snap.N];
            for (int i = 0; i < snap.N; i++) order[i] = i;
            for (int a = 0; a < show; a++)
            {
                int best = a;
                for (int b = a + 1; b < snap.N; b++)
                    if (snap.StationDemand[order[b]] > snap.StationDemand[order[best]]) best = b;
                int tmp = order[a]; order[a] = order[best]; order[best] = tmp;
            }

            GUILayout.Space(4);
            GUILayout.Label("maiores geradoras (pass/dia)", Theme.Dim);
            float top = snap.N > 0 ? snap.StationDemand[order[0]] : 0f;
            for (int a = 0; a < show; a++)
            {
                int st = order[a];
                float perDay = snap.StationDemand[st] * snap.DayLength;
                Widgets.BarRow(
                    "#" + st + " " + snap.ShapeType[snap.ShapeOf[st]],
                    top > 0f ? snap.StationDemand[st] / top : 0f,
                    Theme.Accent2,
                    perDay.ToString("N1"));
            }

            Widgets.EndCard();
        }

        private static string RouteText(Genome g, Snapshot snap, int r)
        {
            var sb = new StringBuilder();
            var route = g.Routes[r];
            for (int k = 0; k < route.Count; k++)
            {
                if (k > 0) sb.Append(" > ");
                int si = route[k];
                if (si >= 0 && si < snap.ShapeOf.Length)
                    sb.Append(ShapeName(snap.ShapeType[snap.ShapeOf[si]])).Append("#").Append(si);
            }
            return sb.ToString();
        }

        private static string ShapeName(StationType t)
        {
            switch (t)
            {
                case StationType.CIRCLE: return "circ";
                case StationType.TRIANGLE: return "tri";
                case StationType.SQUARE: return "quad";
                case StationType.CROSS: return "cruz";
                case StationType.DIAMOND: return "diam";
                case StationType.EGG: return "ovo";
                case StationType.GEM: return "gema";
                case StationType.PENTAGON: return "pent";
                case StationType.STAR: return "estr";
                case StationType.WEDGE: return "cunha";
                default: return t.ToString().ToLowerInvariant();
            }
        }

        // ------------------------------------------------------------------
        private void DrawLogTab()
        {
            Widgets.BeginCard("Atalhos");
            Widgets.Field("F8", "abre/fecha este painel");
            Widgets.Field("F9", "pausa/retoma o jogo");
            Widgets.Field("F10", "roda o algoritmo genetico");
            Widgets.Field("F11", "aplica a melhor solucao");
            Widgets.EndCard();

            Widgets.BeginCard("Log do mod");
            var lines = Log.Recent();
            int from = Mathf.Max(0, lines.Length - 60);
            for (int i = lines.Length - 1; i >= from; i--)
            {
                var l = lines[i];
                GUILayout.Label(l, l.Contains("[ERROR]") ? Theme.Dim : Theme.Mono);
            }
            if (lines.Length == 0) GUILayout.Label("(vazio)", Theme.Dim);
            Widgets.EndCard();
        }

        // ------------------------------------------------------------------
        private const int ChartW = 512, ChartH = 150;
        private const int NetW = 512, NetH = 300;

        private void RefreshTextures(Genome best, Snapshot snap)
        {
            if (Time.unscaledTime - _lastRedraw < 0.20f && _chartTex != null) return;
            _lastRedraw = Time.unscaledTime;

            if (_chartTex == null) _chartTex = Plot.NewTexture(ChartW, ChartH);
            if (_netTex == null) _netTex = Plot.NewTexture(NetW, NetH);

            _engine.CopyHistory(_histBest, _histAvg);
            Plot.DrawConvergence(_chartTex, _histBest, _histAvg, _logScale);

            var g = best ?? _engine.BestClone();
            var s = snap ?? _engine.SnapshotRef;
            if (s != null) Plot.DrawNetwork(_netTex, s, g);
        }

        private readonly List<float> _histBest = new List<float>(512);
        private readonly List<float> _histAvg = new List<float>(512);

        private static string Trunc(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        public void Dispose()
        {
            _engine.Dispose();
            var game = GameHook.Current;
            if (_lockedByUs && game != null) SafeSetLocked(game, false);
            if (_chartTex != null) UnityEngine.Object.Destroy(_chartTex);
            if (_netTex != null) UnityEngine.Object.Destroy(_netTex);
        }
    }
}
