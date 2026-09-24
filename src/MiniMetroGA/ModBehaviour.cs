using System;
using MiniMetroGA.Core;
using MiniMetroGA.UI;
using UnityEngine;

namespace MiniMetroGA
{
    /// <summary>
    /// O unico MonoBehaviour do mod. Existe porque precisamos de OnGUI, que so
    /// o Unity chama em componentes. Criado no primeiro sceneLoaded
    /// (ver <see cref="ModBootstrap"/>), quando a engine ja esta viva.
    /// </summary>
    public class ModBehaviour : MonoBehaviour
    {
        public static ModBehaviour Instance { get; private set; }

        public static KeyCode ToggleKey = KeyCode.F8;
        public static KeyCode PauseKey = KeyCode.F9;
        public static KeyCode SolveKey = KeyCode.F10;
        public static KeyCode ApplyKey = KeyCode.F11;

        private GaWindow _window;
        private Hud _hud;
        private SelfTest _selfTest;
        private UpgradeAdvisor _advisor;
        private string _advisorSaid;

        private void Awake()
        {
            Instance = this;
            _hud = new Hud();
            _window = new GaWindow { Visible = true, Hud = _hud };
            // Qualificado porque o proprio jogo tem uma classe Theme no namespace
            // global, e fora de MiniMetroGA.UI e ela que ganha a resolucao.
            _hud.Say("MiniMetroGA " + ModInfo.Version + " carregado", UI.Theme.Accent);
            Log.Info("UI pronta. " + ToggleKey + " abre/fecha o painel.");
            _advisor = new UpgradeAdvisor();
            _window.Advisor = _advisor;
            _selfTest = SelfTest.TryCreate();
            if (_selfTest != null) _selfTest.Advisor = _advisor;
        }

        private void Update()
        {
            try
            {
                GameHook.Refresh();

                // O indice do chip acompanha a ordem dos atalhos no Hud.
                if (Input.GetKeyDown(ToggleKey))
                {
                    _window.Visible = !_window.Visible;
                    _hud.FlashKey(0);
                }

                // Durante o self-test quem mexe na rede e ele: atalho ou modo
                // automatico do painel trocariam a rede no meio da medicao.
                bool locked = _selfTest != null && _selfTest.OwnsGame;
                if (locked && (Input.GetKeyDown(PauseKey) || Input.GetKeyDown(SolveKey) || Input.GetKeyDown(ApplyKey)))
                {
                    _hud.Say("self-test rodando: atalhos desligados", UI.Theme.Warn);
                }
                else if (GameHook.Current != null)
                {
                    if (Input.GetKeyDown(PauseKey)) { _hud.FlashKey(1); _window.TogglePause(); }
                    if (Input.GetKeyDown(SolveKey)) { _hud.FlashKey(2); _window.StartSolve(); }
                    if (Input.GetKeyDown(ApplyKey)) { _hud.FlashKey(3); _window.ApplyBest(); }
                }
                else if (Input.GetKeyDown(PauseKey) || Input.GetKeyDown(SolveKey) || Input.GetKeyDown(ApplyKey))
                {
                    _hud.Say("nenhuma partida ativa", UI.Theme.Warn);
                }

                _window.AutoLocked = locked;
                _window.Tick();
                _hud.Tick();
                if (_selfTest != null) _selfTest.Tick();

                // frota que ficou esperando o estoque (trem apagado ainda chegando)
                Applier.Tick(GameHook.Current);

                // upgrade da semana: com o self-test quem chama e ele; senao o
                // painel decide se so recomenda ou tambem escolhe
                if (!locked && GameHook.Current != null)
                    _advisor.Tick(GameHook.Current, _window.Config, _window.Config.AutoPickUpgrade);
                if (_advisor.Status != null && _advisor.Status != _advisorSaid)
                {
                    _advisorSaid = _advisor.Status;
                    _hud.Say(_advisorSaid, UI.Theme.Accent2);
                }
            }
            catch (Exception e)
            {
                Log.Error("Update: " + e);
            }
        }

        private void OnGUI()
        {
            try { _window.Draw(); }
            catch (Exception e) { Log.Error("OnGUI/window: " + e); }

            try { _hud.Draw(); }
            catch (Exception e) { Log.Error("OnGUI/hud: " + e); }
        }

        private void OnDestroy()
        {
            try { _window?.Dispose(); } catch (Exception) { }
            try { _advisor?.Dispose(); } catch (Exception) { }
        }
    }
}
