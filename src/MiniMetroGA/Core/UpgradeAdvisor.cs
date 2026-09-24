using System;
using System.Collections.Generic;
using System.Reflection;
using MiniMetroGA.Ga;
using UnityEngine;

namespace MiniMetroGA.Core
{
    /// <summary>
    /// Upgrade da semana. Toda segunda o jogo para na NewAssetScreen: primeiro o
    /// grupo das locomotivas (quase sempre uma opcao so), depois 2 ofertas
    /// sorteadas (linha, vagao, travessia, interchange...). Aqui:
    ///
    ///   1. le as ofertas do painel (NewAssetPanel.buttons, por reflexao);
    ///   2. pede ao nucleo nativo a recomendacao (native/mmopt/src/upgrade.rs):
    ///      a rede otimizada com cada opcao, com o mesmo orcamento de busca;
    ///   3. mostra a recomendacao e, se AutoPick, escolhe sozinho;
    ///   4. interchange no estoque (recem-escolhido ou sobrando) vai para a
    ///      estacao que o nucleo apontou, quando a tela fecha.
    ///
    /// O jogo fica parado na tela enquanto isso roda, entao ha tempo de sobra.
    /// Tem o proprio EngineHost: nao mexe na otimizacao do painel.
    /// </summary>
    public sealed class UpgradeAdvisor : IDisposable
    {
        private readonly EngineHost _host = new EngineHost();
        private object _panel;              // painel cujas ofertas ja foram pedidas
        private List<NewAssetButton> _buttons;
        private float _clickAt, _placeAt;
        private bool _asked;
        private int _interchangeStation = -1;
        private Snapshot _snap;

        /// <summary>Ultima recomendacao, para a UI e o HUD.</summary>
        public string Status { get; private set; }
        public bool Busy => _host.IsRunning;
        public int Picks { get; private set; }
        public int InterchangesPlaced { get; private set; }

        /// <summary>
        /// Chamar todo frame. Devolve true enquanto a tela de upgrade esta aberta
        /// (quem dirige a partida deve esperar).
        /// </summary>
        public bool Tick(Game game, GaConfig cfg, bool autoPick)
        {
            if (game == null) return false;
            GameScreen scr;
            try { scr = game.Screen; } catch (Exception) { return false; }
            float now = Time.realtimeSinceStartup;

            if (scr != GameScreen.NewAsset)
            {
                _panel = null;
                if (autoPick && now > _placeAt) PlaceInterchanges(game, cfg);
                return false;
            }

            var screen = ScreenOf(game);
            var panel = screen != null ? PanelOf(screen) : null;
            var buttons = panel != null ? ButtonsOf(panel) : null;
            if (buttons == null || buttons.Count == 0) return true;

            if (!ReferenceEquals(panel, _panel))
            {
                _panel = panel;
                _buttons = buttons;
                _asked = false;
                _clickAt = now + 0.8f; // deixa a animacao do painel entrar
                if (buttons.Count >= 2 && !OnlyLocomotives(buttons))
                {
                    StartRecommendation(game, cfg, buttons);
                    _asked = true;
                }
                else
                {
                    Status = "Upgrade: " + Offers(buttons) + " (sem escolha a fazer)";
                }
            }

            if (_asked && _host.IsRunning) return true;
            if (!autoPick || now < _clickAt) return true;

            NewAssetButton pick = _asked ? Recommended(buttons) : buttons[0];
            if (pick == null) pick = FallbackPick(game, buttons);
            try
            {
                Log.Info("Upgrade escolhido: " + pick.Type + " x" + pick.Count + " de [" + Offers(buttons) + "]");
                screen.OnAsset(pick);
                Picks++;
                if (pick.Type == AssetType.Interchange) _placeAt = now + 1.0f;
            }
            catch (Exception e) { Log.Warn("Upgrade: clique falhou: " + e.Message); }
            _clickAt = now + 0.8f;
            _panel = null; // o jogo monta outro painel (ou fecha a tela)
            return true;
        }

        private void StartRecommendation(Game game, GaConfig baseCfg, List<NewAssetButton> buttons)
        {
            var cfg = baseCfg.Clone();
            cfg.TimeBudgetMs = Math.Max(300, baseCfg.UpgradeBudgetMs);
            cfg.Generations = 100000;
            cfg.HorizonDays = Math.Max(baseCfg.HorizonDays, baseCfg.UpgradeHorizonDays);
            cfg.DumpProblem = false;
            if (cfg.Seed == 0) cfg.Seed = 4242; // mesma semente em toda opcao
            _snap = Snapshot.Capture(game, cfg);
            if (_snap == null) { Status = "Upgrade: sem snapshot"; return; }
            var opts = new List<KeyValuePair<AssetType, int>>();
            foreach (var b in buttons) opts.Add(new KeyValuePair<AssetType, int>(b.Type, b.Count));
            _host.StartUpgrade(_snap, cfg, opts);
            Status = "Upgrade: avaliando " + Offers(buttons) + "...";
        }

        /// <summary>
        /// A opcao cuja rede otimizada sai mais barata. Empate (menos de 1% do
        /// custo da base): vagao, linha, interchange, travessia, nessa ordem.
        /// </summary>
        private NewAssetButton Recommended(List<NewAssetButton> buttons)
        {
            var res = _host.UpgradeResult();
            if (res == null || res.Count < 2)
            {
                Status = "Upgrade: o nucleo nao respondeu (" + (_host.Error ?? "?") + "), escolha por regra";
                return null;
            }
            double baseF = res[0].Fitness;
            int best = -1;
            for (int i = 1; i < res.Count && i - 1 < buttons.Count; i++)
            {
                if (best < 0) { best = i; continue; }
                double d = res[i].Fitness - res[best].Fitness;
                bool tie = Math.Abs(d) < 0.01 * Math.Max(1.0, baseF);
                if ((!tie && d < 0) || (tie && Priority(res[i].Type) > Priority(res[best].Type))) best = i;
            }
            if (best < 0) return null;
            var parts = new List<string>();
            for (int i = 1; i < res.Count; i++)
                parts.Add(string.Format("{0} {1:+0.0;-0.0}%", res[i].Type, 100.0 * (res[i].Fitness / baseF - 1.0)));
            _interchangeStation = res[best].Type == AssetType.Interchange ? res[best].Station : _interchangeStation;
            Status = "Upgrade recomendado: " + res[best].Type + " (" + string.Join(", ", parts.ToArray()) + ")"
                     + (res[best].Type == AssetType.Interchange && res[best].Station >= 0 ? ", na estacao #" + res[best].Station : "");
            Log.Info(Status);
            return buttons[best - 1];
        }

        private static int Priority(AssetType t)
        {
            switch (t)
            {
                case AssetType.Carriage: return 4;
                case AssetType.Line: return 3;
                case AssetType.Interchange: return 2;
                case AssetType.Crossing:
                case AssetType.Bridge: return 1;
                default: return 0;
            }
        }

        /// <summary>Sem resposta do nucleo: a regra antiga do self-test.</summary>
        private static NewAssetButton FallbackPick(Game game, List<NewAssetButton> buttons)
        {
            NewAssetButton pick = buttons[0];
            int bestRank = -1;
            foreach (var b in buttons)
            {
                int rank = Priority(b.Type);
                if (rank > bestRank) { bestRank = rank; pick = b; }
            }
            return pick;
        }

        /// <summary>
        /// Interchange no estoque vai para a estacao que o nucleo apontou (ou,
        /// sem apontamento, para a que ele apontar agora).
        /// </summary>
        private void PlaceInterchanges(Game game, GaConfig cfg)
        {
            int avail;
            try { avail = game.AssetDatabase.GetAvailableAssets(AssetType.Interchange); }
            catch (Exception) { return; }
            if (avail <= 0) { _interchangeStation = -1; return; }
            float now = Time.realtimeSinceStartup;

            if (_interchangeStation < 0 || _snap == null)
            {
                if (_host.IsRunning) return;
                var res = _host.IsUpgradeRun ? _host.UpgradeResult() : null;
                if (res != null && res.Count >= 2 && res[1].Type == AssetType.Interchange && _snap != null)
                {
                    _interchangeStation = res[1].Station;
                }
                else
                {
                    // pergunta so pelo lugar
                    var c = cfg.Clone();
                    c.TimeBudgetMs = 300;
                    c.Generations = 100000;
                    c.DumpProblem = false;
                    _snap = Snapshot.Capture(game, c);
                    if (_snap == null) { _placeAt = now + 5f; return; }
                    _host.StartUpgrade(_snap, c, new List<KeyValuePair<AssetType, int>> { new KeyValuePair<AssetType, int>(AssetType.Interchange, 1) });
                    return;
                }
            }
            if (_interchangeStation < 0 || _interchangeStation >= _snap.N) { _placeAt = now + 5f; _interchangeStation = -1; return; }
            var st = _snap.StationRefs[_interchangeStation];
            try
            {
                if (st != null && !st.IsInterchange && st.ApplyAsset(AssetType.Interchange, false))
                {
                    InterchangesPlaced++;
                    Log.Info("Interchange posto na estacao #" + _interchangeStation);
                    Status = "Interchange posto na estacao #" + _interchangeStation;
                }
            }
            catch (Exception e) { Log.Warn("Interchange: " + e.Message); }
            _interchangeStation = -1;
            _placeAt = now + 2f;
        }

        private static bool OnlyLocomotives(List<NewAssetButton> buttons)
        {
            foreach (var b in buttons)
                if (b.Type != AssetType.Locomotive && b.Type != AssetType.Shinkansen && b.Type != AssetType.Tram) return false;
            return true;
        }

        private static string Offers(List<NewAssetButton> buttons)
        {
            var names = new List<string>();
            foreach (var b in buttons) names.Add(b.Type + "x" + b.Count);
            return string.Join(", ", names.ToArray());
        }

        // ------------------------------------------------------------------
        // reflexao: Game.screens -> NewAssetScreen.newAssetPanel -> buttons
        // ------------------------------------------------------------------
        private static FieldInfo _screensField, _panelField, _buttonsField;

        private static NewAssetScreen ScreenOf(Game game)
        {
            try
            {
                if (_screensField == null)
                    _screensField = typeof(Game).GetField("screens", BindingFlags.NonPublic | BindingFlags.Instance);
                var screens = _screensField.GetValue(game) as global::UI.Screen[];
                return screens != null ? screens[(int)GameScreen.NewAsset] as NewAssetScreen : null;
            }
            catch (Exception) { return null; }
        }

        private static object PanelOf(NewAssetScreen screen)
        {
            try
            {
                if (_panelField == null)
                    _panelField = typeof(NewAssetScreen).GetField("newAssetPanel", BindingFlags.NonPublic | BindingFlags.Instance);
                return _panelField.GetValue(screen);
            }
            catch (Exception) { return null; }
        }

        private static List<NewAssetButton> ButtonsOf(object panel)
        {
            try
            {
                if (_buttonsField == null)
                    _buttonsField = panel.GetType().GetField("buttons", BindingFlags.NonPublic | BindingFlags.Instance);
                return _buttonsField.GetValue(panel) as List<NewAssetButton>;
            }
            catch (Exception) { return null; }
        }

        public void Dispose() => _host.Dispose();
    }
}
