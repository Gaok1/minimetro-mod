using UnityEngine;

namespace MiniMetroGA.UI
{
    /// <summary>
    /// Widgets escritos a mao porque os do IMGUI nao funcionam neste jogo.
    ///
    /// A auditoria de stripping (ver vault, "APIs ausentes") mostrou que a
    /// UnityEngine.IMGUIModule nao-stripada chama cinco membros que o jogo removeu
    /// do CoreModule. Tres deles vivem dentro de UnityEngine.SliderHandler:
    ///
    ///   RectOffset.Remove   -> ThumbRect(), no caminho de TODO slider
    ///   Rect.set_center     -> ThumbExtRect()
    ///   SystemClock         -> repeticao de clique na calha
    ///
    /// Ou seja: HorizontalSlider, VerticalSlider, Scrollbar e, por tabela,
    /// BeginScrollView (que usa scrollbar) estouram MissingMethodException.
    /// Os outros dois membros ausentes sao Vector4.one/op_Multiply, usados pela
    /// sobrecarga de GUI.DrawTexture com borda e raio, e
    /// TouchScreenKeyboard.isRequiredToForceOpen, usado por GUI.TextField.
    ///
    /// Nada aqui usa nenhum deles.
    /// </summary>
    public static class Widgets
    {
        // ==================================================================
        // Slider
        // ==================================================================
        /// <summary>
        /// Qual slider esta sendo arrastado, identificado pelo rotulo e nao por um
        /// id do IMGUI.
        ///
        /// GUIUtility.GetControlID numera os controles pela ordem em que aparecem.
        /// Como esta UI muda de conteudo entre frames (cartao de inventario que so
        /// existe com partida ativa, linha de "proxima rodada" que so aparece no
        /// modo automatico), a numeracao anda - e um arrasto comecado num slider
        /// termina escrevendo no vizinho. Chave estavel resolve na raiz.
        /// </summary>
        private static int _hotSlider;

        /// <summary>Solta qualquer arrasto quando o botao do mouse nao esta mais
        /// fisicamente pressionado. Protege contra o MouseUp que se perde quando a
        /// janela do jogo troca de foco.</summary>
        public static void ReleaseIfMouseUp()
        {
            if (_hotSlider != 0 && !Input.GetMouseButton(0)) _hotSlider = 0;
        }

        public static float Slider(Rect r, float value, float min, float max, Color fill, int key)
        {
            var e = Event.current;

            // area de agarre mais alta que a trilha desenhada, senao e dificil pegar
            var hit = new Rect(r.x - 2f, r.y - 4f, r.width + 4f, r.height + 8f);

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button == 0 && hit.Contains(e.mousePosition))
                    {
                        _hotSlider = key;
                        value = ValueAt(r, e.mousePosition.x, min, max);
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (_hotSlider == key)
                    {
                        value = ValueAt(r, e.mousePosition.x, min, max);
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (_hotSlider == key)
                    {
                        _hotSlider = 0;
                        e.Use();
                    }
                    break;
            }

            if (e.type == EventType.Repaint)
            {
                float t = Mathf.Approximately(max, min) ? 0f : Mathf.Clamp01((value - min) / (max - min));
                float cy = r.y + r.height * 0.5f;

                Theme.Fill(new Rect(r.x, cy - 2f, r.width, 4f), Theme.Track);
                Theme.Fill(new Rect(r.x, cy - 2f, r.width * t, 4f), fill);

                bool hot = _hotSlider == key;
                float knob = hot ? 13f : 11f;
                var knobRect = new Rect(r.x + r.width * t - knob * 0.5f, cy - knob * 0.5f, knob, knob);
                Theme.Fill(knobRect, hot ? Color.white : fill, Theme.Soft);
                Theme.Frame(knobRect, new Color(0f, 0f, 0f, 0.45f));
            }

            return Mathf.Clamp(value, Mathf.Min(min, max), Mathf.Max(min, max));
        }

        /// <summary>Chave estavel a partir do rotulo. Rotulos sao unicos nesta UI.</summary>
        private static int KeyOf(string label)
        {
            int h = 17;
            for (int i = 0; i < label.Length; i++) h = h * 31 + label[i];
            return h == 0 ? 1 : h;
        }

        private static float ValueAt(Rect r, float mouseX, float min, float max)
        {
            float t = r.width <= 0f ? 0f : Mathf.Clamp01((mouseX - r.x) / r.width);
            return min + (max - min) * t;
        }

        /// <summary>Linha completa: rotulo, slider e valor formatado.</summary>
        public static float SliderRow(string label, float value, float min, float max, string fmt, Color fill)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, Theme.Mono, GUILayout.Width(168));
            var r = GUILayoutUtility.GetRect(80, 18, GUILayout.ExpandWidth(true));
            float v = Slider(r, value, min, max, fill, KeyOf(label));
            GUILayout.Label(string.Format(fmt, v), Theme.Value, GUILayout.Width(76));
            GUILayout.EndHorizontal();
            return v;
        }

        public static int SliderRowInt(string label, int value, int min, int max, Color fill)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, Theme.Mono, GUILayout.Width(168));
            var r = GUILayoutUtility.GetRect(80, 18, GUILayout.ExpandWidth(true));
            int v = Mathf.RoundToInt(Slider(r, value, min, max, fill, KeyOf(label)));
            GUILayout.Label(v.ToString(), Theme.Value, GUILayout.Width(76));
            GUILayout.EndHorizontal();
            return v;
        }

        // ==================================================================
        // Barra de progresso / proporcao
        // ==================================================================
        public static void Bar(Rect r, float t, Color fill, string caption = null)
        {
            Theme.Fill(r, Theme.Track);
            Theme.Fill(new Rect(r.x, r.y, r.width * Mathf.Clamp01(t), r.height), fill);
            if (caption != null) GUI.Label(r, caption, Theme.Center);
        }

        public static void BarRow(string label, float t, Color fill, string right, float labelWidth = 168f)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, Theme.Mono, GUILayout.Width(labelWidth));
            var r = GUILayoutUtility.GetRect(60, 14, GUILayout.ExpandWidth(true));
            Bar(new Rect(r.x, r.y + 3f, r.width, 8f), t, fill);
            GUILayout.Label(right, Theme.Value, GUILayout.Width(76));
            GUILayout.EndHorizontal();
        }

        // ==================================================================
        // Botoes e abas
        // ==================================================================
        public static bool Button(string text, Color tint, float height = 28f, params GUILayoutOption[] opts)
        {
            var old = GUI.backgroundColor;
            GUI.backgroundColor = tint;
            var all = new GUILayoutOption[opts.Length + 1];
            all[0] = GUILayout.Height(height);
            for (int i = 0; i < opts.Length; i++) all[i + 1] = opts[i];
            bool hit = GUILayout.Button(text, Theme.Btn, all);
            GUI.backgroundColor = old;
            return hit;
        }

        /// <summary>Barra de abas desenhada na mao: a do IMGUI (Toolbar) funcionaria,
        /// mas nao da para estilizar sem mexer em RectOffset.</summary>
        public static int Tabs(int selected, string[] labels, float height = 26f)
        {
            var row = GUILayoutUtility.GetRect(100, height, GUILayout.ExpandWidth(true));
            float w = row.width / labels.Length;
            var e = Event.current;

            for (int i = 0; i < labels.Length; i++)
            {
                var cell = new Rect(row.x + i * w, row.y, w - 2f, row.height);
                bool active = i == selected;
                bool hover = cell.Contains(e.mousePosition);

                if (e.type == EventType.Repaint)
                {
                    Theme.Fill(cell, active ? Theme.Card : (hover ? Theme.CardAlt : new Color(0f, 0f, 0f, 0.25f)));
                    if (active)
                        Theme.Fill(new Rect(cell.x, cell.yMax - 2f, cell.width, 2f), Theme.Accent);
                    var st = Theme.Center;
                    var prev = st.normal.textColor;
                    st.normal.textColor = active ? Theme.Text : Theme.TextDim;
                    GUI.Label(cell, labels[i], st);
                    st.normal.textColor = prev;
                }
                else if (e.type == EventType.MouseDown && e.button == 0 && cell.Contains(e.mousePosition))
                {
                    selected = i;
                    e.Use();
                }
            }
            return selected;
        }

        // ==================================================================
        // Scroll sem scrollbar
        // ==================================================================
        // BeginScrollView usa GUI.Scroller -> SliderHandler -> RectOffset.Remove,
        // que nao existe aqui. Entao rolamos na mao: um grupo que corta o conteudo
        // e uma area deslocada por dentro. A altura do conteudo e medida no frame
        // anterior (GetLastRect no Repaint), o que basta para limitar a rolagem.
        public static void BeginScroll(Rect viewport, ref float scroll, float contentHeight)
        {
            var e = Event.current;
            float maxScroll = Mathf.Max(0f, contentHeight - viewport.height);

            if (e.type == EventType.ScrollWheel && viewport.Contains(e.mousePosition))
            {
                scroll = Mathf.Clamp(scroll + e.delta.y * 18f, 0f, maxScroll);
                e.Use();
            }
            scroll = Mathf.Clamp(scroll, 0f, maxScroll);

            // indicador de rolagem, so quando ha o que rolar
            if (e.type == EventType.Repaint && maxScroll > 1f)
            {
                float frac = viewport.height / contentHeight;
                float barH = Mathf.Max(24f, viewport.height * frac);
                float y = viewport.y + (viewport.height - barH) * (scroll / maxScroll);
                Theme.Fill(new Rect(viewport.xMax - 4f, viewport.y, 3f, viewport.height), new Color(1f, 1f, 1f, 0.06f));
                Theme.Fill(new Rect(viewport.xMax - 4f, y, 3f, barH), new Color(1f, 1f, 1f, 0.28f));
            }

            GUI.BeginGroup(viewport);
            GUILayout.BeginArea(new Rect(0f, -scroll, viewport.width - 8f, Mathf.Max(viewport.height, contentHeight) + 400f));
        }

        /// <summary>Fecha a area e devolve a altura usada pelo conteudo (para o
        /// proximo frame saber quanto da para rolar).</summary>
        public static float EndScroll(float previous)
        {
            float measured = previous;
            if (Event.current.type == EventType.Repaint)
            {
                var last = GUILayoutUtility.GetLastRect();
                if (last.yMax > 0f) measured = last.yMax + 8f;
            }
            GUILayout.EndArea();
            GUI.EndGroup();
            return measured;
        }

        // ==================================================================
        // Cartao
        // ==================================================================
        public static void BeginCard(string title)
        {
            var old = GUI.backgroundColor;
            GUI.backgroundColor = Theme.Card;
            GUILayout.BeginVertical(Theme.Card9);
            GUI.backgroundColor = old;
            if (title != null) GUILayout.Label(title, Theme.Header);
        }

        public static void EndCard()
        {
            GUILayout.EndVertical();
            GUILayout.Space(5);
        }

        /// <summary>Par rotulo/valor alinhado, o formato usado em toda a UI.</summary>
        public static void Field(string label, string value, Color? valueColor = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, Theme.Mono, GUILayout.Width(168));
            var st = Theme.Value;
            var prev = st.normal.textColor;
            if (valueColor.HasValue) st.normal.textColor = valueColor.Value;
            GUILayout.Label(value, st, GUILayout.ExpandWidth(true));
            st.normal.textColor = prev;
            GUILayout.EndHorizontal();
        }

        /// <summary>Checkbox desenhado a mao. O toggle do skin depende das texturas
        /// de check do skin padrao, que ficam sem gracas neste tema escuro.</summary>
        public static bool Toggle(bool value, string label)
        {
            var row = GUILayoutUtility.GetRect(100, 20, GUILayout.ExpandWidth(true));
            var e = Event.current;
            var box = new Rect(row.x + 1f, row.y + 3f, 14f, 14f);

            if (e.type == EventType.MouseDown && e.button == 0 && row.Contains(e.mousePosition))
            {
                value = !value;
                e.Use();
            }

            if (e.type == EventType.Repaint)
            {
                bool hover = row.Contains(e.mousePosition);
                Theme.Fill(box, value ? Theme.Accent : (hover ? Theme.Track : Theme.CardAlt));
                Theme.Frame(box, value ? Theme.Accent : Theme.Track);
                if (value)
                {
                    // "check" simplificado: dois tracos grossos
                    Theme.Fill(new Rect(box.x + 3f, box.y + 7f, 4f, 2f), Theme.Bg);
                    Theme.Fill(new Rect(box.x + 6f, box.y + 4f, 2f, 6f), Theme.Bg);
                }
                GUI.Label(new Rect(row.x + 20f, row.y, row.width - 20f, row.height), label,
                    value ? Theme.Label : Theme.Dim);
            }
            return value;
        }
    }
}
