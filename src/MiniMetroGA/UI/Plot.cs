using System;
using System.Collections.Generic;
using MiniMetroGA.Core;
using MiniMetroGA.Ga;
using UnityEngine;

namespace MiniMetroGA.UI
{
    /// <summary>
    /// Desenho por pixel em Texture2D. IMGUI nao tem primitiva de linha, e puxar
    /// uma lib de grafico para dentro de um plugin de 60 KB seria exagero.
    /// Bresenham resolve e o custo e irrelevante nesse tamanho de textura.
    /// </summary>
    public static class Plot
    {
        public static readonly Color32 Bg = new Color32(24, 26, 32, 235);
        public static readonly Color32 Grid = new Color32(52, 56, 66, 255);
        public static readonly Color32 BestColor = new Color32(90, 220, 140, 255);
        public static readonly Color32 AvgColor = new Color32(240, 170, 70, 255);
        public static readonly Color32 StationColor = new Color32(225, 228, 235, 255);
        public static readonly Color32 DeadStation = new Color32(200, 70, 70, 255);

        /// <summary>Paleta parecida com a do jogo, para as linhas do preview.</summary>
        public static readonly Color32[] LinePalette =
        {
            new Color32(226, 62, 56, 255),    // vermelho
            new Color32(52, 121, 199, 255),   // azul
            new Color32(240, 178, 44, 255),   // amarelo
            new Color32(120, 190, 80, 255),   // verde
            new Color32(180, 92, 190, 255),   // roxo
            new Color32(70, 200, 205, 255),   // ciano
            new Color32(240, 130, 60, 255),   // laranja
            new Color32(150, 150, 150, 255),  // cinza
        };

        private static Color32[] _buf;
        private static int _bw, _bh;

        // um buffer por tamanho de textura: o grafico e o preview tem dimensoes
        // diferentes e realocar a cada refresh so gera lixo pro GC
        private static readonly Dictionary<long, Color32[]> _buffers = new Dictionary<long, Color32[]>();

        private static void Begin(Texture2D tex)
        {
            _bw = tex.width; _bh = tex.height;
            long key = ((long)_bw << 32) | (uint)_bh;
            if (!_buffers.TryGetValue(key, out _buf))
            {
                _buf = new Color32[_bw * _bh];
                _buffers[key] = _buf;
            }
            for (int i = 0; i < _buf.Length; i++) _buf[i] = Bg;
        }

        private static void End(Texture2D tex)
        {
            tex.SetPixels32(_buf);
            tex.Apply(false);
        }

        private static void Px(int x, int y, Color32 c)
        {
            if (x < 0 || y < 0 || x >= _bw || y >= _bh) return;
            _buf[y * _bw + x] = c;
        }

        private static void Dot(int x, int y, int r, Color32 c)
        {
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if (dx * dx + dy * dy <= r * r) Px(x + dx, y + dy, c);
        }

        private static void Seg(int x0, int y0, int x1, int y1, Color32 c, int thickness = 1)
        {
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            int guard = 0;
            while (guard++ < 20000)
            {
                if (thickness <= 1) Px(x0, y0, c);
                else Dot(x0, y0, thickness - 1, c);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        // ------------------------------------------------------------------
        // Grafico de convergencia
        // ------------------------------------------------------------------
        public static void DrawConvergence(Texture2D tex, List<float> best, List<float> avg, bool logScale)
        {
            Begin(tex);

            int n = best.Count;
            if (n < 2) { DrawGrid(4, 4); End(tex); return; }

            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                float b = Val(best[i], logScale);
                float a = i < avg.Count ? Val(avg[i], logScale) : b;
                if (b < lo) lo = b; if (b > hi) hi = b;
                if (a < lo) lo = a; if (a > hi) hi = a;
            }
            if (hi - lo < 1e-6f) { hi = lo + 1f; }

            DrawGrid(4, 4);

            int prevXa = 0, prevYa = 0, prevXb = 0, prevYb = 0;
            for (int i = 0; i < n; i++)
            {
                int x = Mathf.RoundToInt(i / (float)(n - 1) * (_bw - 1));
                int yb = MapY(Val(best[i], logScale), lo, hi);
                int ya = i < avg.Count ? MapY(Val(avg[i], logScale), lo, hi) : yb;
                if (i > 0)
                {
                    Seg(prevXa, prevYa, x, ya, AvgColor);
                    Seg(prevXb, prevYb, x, yb, BestColor, 2);
                }
                prevXa = x; prevYa = ya; prevXb = x; prevYb = yb;
            }

            End(tex);
        }

        private static float Val(float v, bool logScale)
        {
            if (!logScale) return v;
            return Mathf.Log10(Mathf.Max(1e-3f, v));
        }

        private static int MapY(float v, float lo, float hi)
        {
            float t = (v - lo) / (hi - lo);
            // textura tem y=0 embaixo; fitness menor = melhor = mais embaixo
            return Mathf.Clamp(Mathf.RoundToInt(t * (_bh - 5)) + 2, 0, _bh - 1);
        }

        private static void DrawGrid(int cols, int rows)
        {
            for (int c = 1; c < cols; c++)
            {
                int x = c * _bw / cols;
                for (int y = 0; y < _bh; y++) Px(x, y, Grid);
            }
            for (int r = 1; r < rows; r++)
            {
                int y = r * _bh / rows;
                for (int x = 0; x < _bw; x++) Px(x, y, Grid);
            }
        }

        // ------------------------------------------------------------------
        // Preview da rede
        // ------------------------------------------------------------------
        public static void DrawNetwork(Texture2D tex, Snapshot snap, Genome g)
        {
            Begin(tex);
            if (snap == null || snap.N == 0) { End(tex); return; }

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < snap.N; i++)
            {
                var p = snap.Pos[i];
                if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
            }
            float spanX = Mathf.Max(1f, maxX - minX);
            float spanY = Mathf.Max(1f, maxY - minY);
            float span = Mathf.Max(spanX, spanY);
            const int pad = 10;
            float scale = (Mathf.Min(_bw, _bh) - 2 * pad) / span;
            float offX = (_bw - spanX * scale) * 0.5f;
            float offY = (_bh - spanY * scale) * 0.5f;

            Func<int, Vector2Int> map = i =>
            {
                var p = snap.Pos[i];
                return new Vector2Int(
                    Mathf.RoundToInt((p.x - minX) * scale + offX),
                    Mathf.RoundToInt((p.y - minY) * scale + offY));
            };

            var served = new bool[snap.N];

            if (g != null)
            {
                for (int r = 0; r < g.RouteCount; r++)
                {
                    var route = g.Routes[r];
                    var col = LinePalette[r % LinePalette.Length];
                    for (int k = 0; k + 1 < route.Count; k++)
                    {
                        var a = map(route[k]);
                        var b = map(route[k + 1]);
                        Seg(a.x, a.y, b.x, b.y, col, 2);
                    }
                    if (g.Loops[r] && route.Count >= 3)
                    {
                        var a = map(route[route.Count - 1]);
                        var b = map(route[0]);
                        Seg(a.x, a.y, b.x, b.y, col, 2);
                    }
                    foreach (int st in route) served[st] = true;
                }
            }

            for (int i = 0; i < snap.N; i++)
            {
                var p = map(i);
                bool special = snap.ShapeOf[i] >= 3;
                Dot(p.x, p.y, special ? 3 : 2, served[i] ? StationColor : DeadStation);
            }

            End(tex);
        }

        public static Texture2D NewTexture(int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Clamp;
            return t;
        }
    }
}
