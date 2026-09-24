using UnityEngine;

namespace MiniMetroGA.UI
{
    /// <summary>
    /// Paleta, texturas e estilos do mod.
    ///
    /// Regra de ouro deste arquivo: NAO construir RectOffset e NAO mexer em
    /// padding/border/margin. O stripping do jogo removeu o ctor de 4 argumentos
    /// e os setters de RectOffset, entao todo estilo aqui nasce como copia de um
    /// estilo do GUI.skin (que ja vem com espacamentos validos) e so troca cor,
    /// tamanho e alinhamento. Ver "Managed stripping - o problema central" no vault.
    /// </summary>
    public static class Theme
    {
        // ---- paleta ----
        public static readonly Color Bg = new Color(0.09f, 0.10f, 0.13f, 0.97f);
        public static readonly Color Card = new Color(0.16f, 0.17f, 0.21f, 0.95f);
        public static readonly Color CardAlt = new Color(0.13f, 0.14f, 0.18f, 0.95f);
        public static readonly Color Accent = new Color(0.35f, 0.86f, 0.55f);   // verde
        public static readonly Color Accent2 = new Color(0.36f, 0.68f, 0.96f);  // azul
        public static readonly Color Warn = new Color(0.96f, 0.68f, 0.26f);     // laranja
        public static readonly Color Danger = new Color(0.90f, 0.34f, 0.32f);   // vermelho
        public static readonly Color Text = new Color(0.90f, 0.92f, 0.95f);
        public static readonly Color TextDim = new Color(0.60f, 0.63f, 0.70f);
        public static readonly Color Track = new Color(0.24f, 0.26f, 0.32f);

        // ---- estilos ----
        public static GUIStyle Title, Header, Label, Dim, Mono, Value, Center, Chip, Card9, Btn;

        private static bool _ready;
        private static Texture2D _white, _soft;

        /// <summary>Textura 1x1 branca propria. Serve de base para tudo que e pintado na mao.</summary>
        public static Texture2D White
        {
            get { EnsureTextures(); return _white; }
        }

        /// <summary>Retangulo com cantos levemente escurecidos, para dar volume aos chips.</summary>
        public static Texture2D Soft
        {
            get { EnsureTextures(); return _soft; }
        }

        private static void EnsureTextures()
        {
            if (_white != null) return;

            _white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply(false);
            _white.hideFlags = HideFlags.HideAndDontSave;

            const int s = 16;
            _soft = new Texture2D(s, s, TextureFormat.RGBA32, false);
            var px = new Color32[s * s];
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    // vinheta suave: mais claro no topo, mais escuro na base
                    float t = 1f - (y / (float)(s - 1)) * 0.25f;
                    byte v = (byte)Mathf.Clamp(Mathf.RoundToInt(255f * t), 0, 255);
                    px[y * s + x] = new Color32(v, v, v, 255);
                }
            }
            _soft.SetPixels32(px);
            _soft.Apply(false);
            _soft.filterMode = FilterMode.Bilinear;
            _soft.hideFlags = HideFlags.HideAndDontSave;
        }

        public static void Ensure()
        {
            if (_ready) return;
            EnsureTextures();

            Title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 15 };
            Title.normal.textColor = Text;

            Header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 12 };
            Header.normal.textColor = Accent;

            Label = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = false };
            Label.normal.textColor = Text;

            Dim = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
            Dim.normal.textColor = TextDim;

            Mono = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = false };
            Mono.normal.textColor = Text;

            Value = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleRight };
            Value.normal.textColor = Accent2;

            Center = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleCenter };
            Center.normal.textColor = Text;

            Chip = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            Chip.normal.textColor = Text;

            Card9 = new GUIStyle(GUI.skin.box);
            Btn = new GUIStyle(GUI.skin.button) { fontSize = 12 };

            _ready = true;
        }

        // Bordas zeradas, criadas uma vez. Ver Blit() para o porque de existirem.
        private static readonly Vector4 NoBorder = new Vector4(0f, 0f, 0f, 0f);

        /// <summary>
        /// UNICO caminho de desenho de textura do mod.
        ///
        /// GUI.DrawTexture(rect, tex) parece inofensivo mas delega ate a sobrecarga
        /// que faz `Vector4.one * borderWidth`, e Vector4.one/op_Multiply foram
        /// removidos do CoreModule pelo stripping. A sobrecarga que recebe os dois
        /// Vector4 prontos e publica e nao passa por aquela linha, entao e por ela
        /// que tudo aqui desenha.
        /// </summary>
        public static void Blit(Rect r, Texture tex, Color c, ScaleMode mode = ScaleMode.StretchToFill)
        {
            GUI.DrawTexture(r, tex, mode, true, 0f, c, NoBorder, NoBorder);
        }

        public static void Fill(Rect r, Color c)
        {
            Blit(r, White, c);
        }

        public static void Fill(Rect r, Color c, Texture2D tex)
        {
            Blit(r, tex, c);
        }

        /// <summary>Moldura de 1px desenhada com quatro retangulos.</summary>
        public static void Frame(Rect r, Color c, float w = 1f)
        {
            Fill(new Rect(r.x, r.y, r.width, w), c);
            Fill(new Rect(r.x, r.yMax - w, r.width, w), c);
            Fill(new Rect(r.x, r.y, w, r.height), c);
            Fill(new Rect(r.xMax - w, r.y, w, r.height), c);
        }
    }
}
