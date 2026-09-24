using System.Collections.Generic;
using MiniMetroGA.Ga;
using UnityEngine;

namespace MiniMetroGA.UI
{
    /// <summary>
    /// Overlay sempre visivel no canto inferior esquerdo. Existe por dois motivos:
    /// lembrar quais teclas o mod usa (e piscar a tecla quando ela e apertada, para
    /// o jogador ver que o mod recebeu o comando) e mostrar o AG progredindo mesmo
    /// com o painel fechado.
    ///
    /// Desenha tudo em coordenadas absolutas de tela, sem GUILayout: overlay nao
    /// deve competir por espaco de layout com nada.
    /// </summary>
    public class Hud
    {
        public bool Visible = true;

        // preenchidos pelo ModBehaviour/GaWindow a cada frame
        public bool PanelOpen;
        public bool GamePaused;
        public bool HasGame;
        public GaState State = GaState.Idle;
        public int Generation, TotalGenerations;
        public double BestFitness = double.MaxValue;

        private struct Chip
        {
            public string Key;
            public string Label;
            public float Flash;
        }

        private readonly Chip[] _chips =
        {
            new Chip { Key = "F8",  Label = "painel" },
            new Chip { Key = "F9",  Label = "pausar" },
            new Chip { Key = "F10", Label = "otimizar" },
            new Chip { Key = "F11", Label = "aplicar" },
        };

        private struct Toast
        {
            public string Text;
            public Color Color;
            public float Born;
        }

        private readonly List<Toast> _toasts = new List<Toast>(8);
        private const float ToastLife = 5f;

        // ==================================================================
        public void FlashKey(int index)
        {
            if (index < 0 || index >= _chips.Length) return;
            _chips[index].Flash = 1f;
        }

        public void Say(string text, Color color)
        {
            _toasts.Add(new Toast { Text = text, Color = color, Born = Time.unscaledTime });
            if (_toasts.Count > 5) _toasts.RemoveAt(0);
        }

        public void Say(string text) { Say(text, Theme.Text); }

        public void Tick()
        {
            float dt = Time.unscaledDeltaTime;
            for (int i = 0; i < _chips.Length; i++)
                if (_chips[i].Flash > 0f) _chips[i].Flash = Mathf.Max(0f, _chips[i].Flash - dt * 2.2f);

            for (int i = _toasts.Count - 1; i >= 0; i--)
                if (Time.unscaledTime - _toasts[i].Born > ToastLife) _toasts.RemoveAt(i);
        }

        // ==================================================================
        private const float ChipW = 92f, ChipH = 26f, Gap = 5f;
        private const float MarginX = 14f, MarginY = 14f;

        public void Draw()
        {
            if (!Visible) return;
            Theme.Ensure();

            float totalW = _chips.Length * ChipW + (_chips.Length - 1) * Gap;
            float baseY = Screen.height - MarginY - ChipH;

            DrawChips(MarginX, baseY, totalW);

            float y = baseY - Gap - 22f;
            if (DrawStatusStrip(MarginX, y, totalW)) y -= 22f + Gap;

            DrawToasts(MarginX, y);
        }

        private void DrawChips(float x, float y, float totalW)
        {
            // fundo unico atras da fileira, para o texto nao sumir sobre o mapa claro
            Theme.Fill(new Rect(x - 5f, y - 5f, totalW + 10f, ChipH + 10f), new Color(0f, 0f, 0f, 0.45f));

            for (int i = 0; i < _chips.Length; i++)
            {
                var r = new Rect(x + i * (ChipW + Gap), y, ChipW, ChipH);
                var c = _chips[i];

                bool on = (i == 0 && PanelOpen) || (i == 1 && GamePaused);
                Color bg = on ? Theme.Accent2 : Theme.Card;
                if (c.Flash > 0f) bg = Color.Lerp(bg, Theme.Accent, c.Flash);

                Theme.Fill(r, bg, Theme.Soft);
                Theme.Frame(r, c.Flash > 0f ? Theme.Accent : new Color(1f, 1f, 1f, 0.10f));

                // a tecla em destaque, a acao em cinza ao lado
                var keyRect = new Rect(r.x + 6f, r.y, 30f, r.height);
                var labRect = new Rect(r.x + 36f, r.y, r.width - 40f, r.height);

                var ks = Theme.Chip;
                var prev = ks.normal.textColor;
                ks.normal.textColor = c.Flash > 0f || on ? Color.white : Theme.Accent;
                GUI.Label(keyRect, c.Key, ks);
                ks.normal.textColor = prev;

                GUI.Label(labRect, c.Label, on ? Theme.Label : Theme.Dim);
            }
        }

        /// <summary>Faixa com o estado do AG. Devolve false quando nao ha nada a dizer.</summary>
        private bool DrawStatusStrip(float x, float y, float totalW)
        {
            if (!HasGame)
            {
                Theme.Fill(new Rect(x - 5f, y - 3f, totalW + 10f, 22f), new Color(0f, 0f, 0f, 0.45f));
                GUI.Label(new Rect(x, y, totalW, 20f), "sem partida ativa - entre numa cidade", Theme.Dim);
                return true;
            }

            if (State != GaState.Running && State != GaState.Done) return false;

            Theme.Fill(new Rect(x - 5f, y - 3f, totalW + 10f, 22f), new Color(0f, 0f, 0f, 0.45f));

            float t = TotalGenerations > 0 ? Mathf.Clamp01(Generation / (float)TotalGenerations) : 0f;
            var barRect = new Rect(x, y + 15f, totalW, 3f);
            Widgets.Bar(barRect, t, State == GaState.Running ? Theme.Accent : Theme.Accent2);

            string fit = BestFitness == double.MaxValue ? "-" : string.Format("{0:N0}", BestFitness);
            string txt = State == GaState.Running
                ? string.Format("AG rodando  ger {0}/{1}  melhor {2}", Generation, TotalGenerations, fit)
                : string.Format("AG pronto  melhor {0}  - F11 aplica", fit);

            GUI.Label(new Rect(x, y - 2f, totalW, 18f), txt, Theme.Mono);
            return true;
        }

        private void DrawToasts(float x, float y)
        {
            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                var t = _toasts[i];
                float age = Time.unscaledTime - t.Born;
                float alpha = Mathf.Clamp01((ToastLife - age) / 1.2f);
                if (alpha <= 0f) continue;

                var r = new Rect(x, y, 420f, 20f);
                Theme.Fill(new Rect(r.x - 5f, r.y - 2f, r.width + 10f, r.height + 2f),
                    new Color(0f, 0f, 0f, 0.45f * alpha));
                Theme.Fill(new Rect(r.x - 5f, r.y - 2f, 3f, r.height + 2f),
                    new Color(t.Color.r, t.Color.g, t.Color.b, alpha));

                var st = Theme.Mono;
                var prev = st.normal.textColor;
                st.normal.textColor = new Color(t.Color.r, t.Color.g, t.Color.b, alpha);
                GUI.Label(r, t.Text, st);
                st.normal.textColor = prev;

                y -= 22f;
            }
        }
    }
}
