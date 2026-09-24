using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace MiniMetroGA.Core
{
    /// <summary>
    /// Serializa o problema para o nucleo nativo (native/mmopt, src/problem.rs).
    /// Formato little-endian versionado; qualquer mudanca aqui tem que ir para o
    /// Problem::parse do Rust tambem.
    ///
    /// Alem do que o Snapshot ja tem, manda o que o dossie mostrou que importa e
    /// que o modelo C# ignorava: as estacoes FUTURAS (o jogo ja sorteou todas na
    /// largada), trocas de forma agendadas, a fila real de cada plataforma por
    /// destino, o timer de lotacao, a costa exata da agua (ObstacleHull), a
    /// cinematica do trem e o relogio/crescimento da demanda.
    ///
    /// Escrito a mao em vez de BinaryWriter: o mscorlib do jogo e stripado e nao
    /// da para garantir que todo overload de Write sobreviveu. Rode o StripAudit
    /// depois de mexer aqui.
    /// </summary>
    public static class ProblemExport
    {
        public const uint Magic = 0x41474D4D; // "MMGA"
        public const uint Version = 3;

        private static FieldInfo _hullLines;

        public static byte[] Build(Snapshot s)
        {
            var game = s.GameRef;
            var city = s.CityRef;
            var w = new Blob(16 * 1024);

            w.U32(Magic);
            w.U32(Version);
            w.Str(s.CityName ?? "?");

            // ---- parametros ----
            float speed = s.TrainSpeed, acc = 55f, dec = 110f;
            int railcarCap = s.RailcarCapacity;
            try
            {
                var def = city.Definition.TrainDefinition;
                var loco = def.GetLocomotiveDefinition(MainLocomotiveType(game));
                if (loco != null && loco.Speed > 0f)
                {
                    speed = loco.Speed;
                    acc = loco.Acceleration > 0f ? loco.Acceleration : acc;
                    dec = loco.Deceleration > 0f ? loco.Deceleration : dec;
                    if (loco.Capacity > 0) railcarCap = loco.Capacity;
                }
            }
            catch (Exception e) { Log.Warn("ProblemExport: trem: " + e.Message); }

            float pulse = Safe(() => city.PeepPulsePeriod, 0.5f) * (5f / 6f);
            float citySpawn = Safe(() => city.Definition.PassengerSpawnScale, 1f);
            float cityScaleNow = Safe(() => city.PeepSpawnScale, citySpawn);
            float planner = citySpawn > 0f ? cityScaleNow / citySpawn : 1f;

            var future = GameHook.GetFutureStations(city);
            // CityPlanner.lastStationSpawnTime e o instante da ultima estacao agendada,
            // que e a ultima das futuras; se nao ha futura, ja passou.
            float lastSpawn = future.Count > 0 ? Safe(() => future[future.Count - 1].ActiveTime, float.MaxValue) : 0f;

            w.F32(speed);
            w.F32(acc);
            w.F32(dec);
            w.U16(railcarCap);
            w.F32(pulse);
            w.U8(Safe(() => city.Definition.IsEmbarkingQuick, false) ? 1 : 0);
            w.U8((int)Safe(() => game.Mode, GameMode.CLASSIC));
            w.F32(s.DayLength);
            w.F32(Safe(() => city.Clock.Time, 0f));
            w.I32(Safe(() => city.Clock.Day, 0));
            w.I32(Safe(() => city.Clock.Week, 0));
            w.F32(planner);
            w.F32(citySpawn);
            w.F32(Safe(() => game.PassengerSpawnBoost, 0.05f));
            w.F32(lastSpawn);
            w.U8(Safe(() => game.DoesPeepSpawnScale, true) ? 1 : 0);
            w.U8(Safe(() => city.DoesServiceAffectPeepSpawns, false) ? 1 : 0);
            w.U16(s.InterchangeCapacity);

            // ---- orcamento ----
            // linhas cruas (vivas + livres); o nucleo limita pelos trens
            w.U16(s.MaxLines);
            w.U16(s.LinesAvailable);
            w.U16(s.Locomotives);
            w.U16(s.Carriages);
            w.U16(s.Crossings + s.Bridges);
            var maxAt = s.MaxLocosAt ?? new int[0];
            w.I32(maxAt.Length);
            for (int i = 0; i < maxAt.Length; i++) w.U16(maxAt[i]);

            // ---- estacoes: ativas (ordem do Snapshot) e depois futuras ----
            w.I32(s.N + future.Count);
            w.I32(s.N);
            for (int i = 0; i < s.N; i++) WriteStation(w, s.StationRefs[i], s.Capacity[i], s.Centrality[i], s.Service[i], true);
            for (int i = 0; i < future.Count; i++)
            {
                var st = future[i];
                WriteStation(w, st, Safe(() => st.PeepCapacity, 6), Safe(() => st.Centrality, 1f), 1f, false);
            }

            // ---- tabela de spawn ----
            var entries = SpawnModel.AllEntries();
            w.I32(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i].Value;
                w.U16((int)entries[i].Key);
                w.U16((int)e.Dest);
                w.I32(e.Hour);
                w.U8((int)e.Day & 0x7F);
                w.F32(e.PerDay);
            }

            // ---- costa ----
            var hull = ReadHull(city);
            w.I32(hull.Count / 4);
            for (int i = 0; i < hull.Count; i++) w.F32(hull[i]);

            // ---- rede atual ----
            w.I32(s.CurrentRoutes.Count);
            for (int r = 0; r < s.CurrentRoutes.Count; r++)
            {
                var route = s.CurrentRoutes[r];
                w.U8(r < s.CurrentLoop.Count && s.CurrentLoop[r] ? 1 : 0);
                w.U16(r < s.CurrentLocos.Count ? s.CurrentLocos[r] : 1);
                w.U16(r < s.CurrentCars.Count ? s.CurrentCars[r] : 0);
                w.U16(r < s.CurrentRev.Count ? s.CurrentRev[r] : 0);
                w.U16(r < s.CurrentOnboard.Count ? s.CurrentOnboard[r] : 0);
                w.I32(route.Length);
                for (int k = 0; k < route.Length; k++) w.U16(route[k]);
            }

            return w.ToArray();
        }

        private static void WriteStation(Blob w, Station st, int capacity, float centrality, float service, bool active)
        {
            var pos = Safe(() => st.Position, Vector2.zero);
            w.F32(pos.x);
            w.F32(pos.y);
            w.U16((int)Safe(() => st.Type, StationType.CIRCLE));
            w.U16(capacity);
            w.U8(Safe(() => st.IsInterchange, false) ? 1 : 0);
            w.F32(centrality);
            w.F32(service);
            w.F32(active ? 0f : Safe(() => st.ActiveTime, 0f));
            var sched = Safe(() => st.ScheduledType, StationType.NONE);
            w.U16((int)sched);
            w.F32(Safe(() => st.ScheduledTypeChangeTime, -1f));
            w.F32(Safe(() => st.ExpiryTimerCompletion, 0f));

            // fila por forma de destino (Peep.Type e o destino do passageiro)
            var types = new List<int>(4);
            var counts = new List<int>(4);
            int n = active ? Safe(() => st.PeepCount, 0) : 0;
            for (int i = 0; i < n; i++)
            {
                int t;
                try { t = (int)st.GetPeep(i).Type; }
                catch (Exception) { continue; }
                int k = types.IndexOf(t);
                if (k < 0) { types.Add(t); counts.Add(1); }
                else counts[k]++;
            }
            w.I32(types.Count);
            for (int i = 0; i < types.Count; i++)
            {
                w.U16(types[i]);
                w.U16(counts[i]);
            }
        }

        /// <summary>
        /// A costa que o jogo usa para decidir tunel: CityDefinition.ObstacleHull,
        /// uma lista plana de LineSegment (campo privado "lines"). Devolve x1,y1,x2,y2...
        /// </summary>
        private static List<float> ReadHull(City city)
        {
            var list = new List<float>(256);
            try
            {
                var hull = city.Definition.ObstacleHull;
                if (hull == null) return list;
                if (_hullLines == null)
                    _hullLines = typeof(ObstacleHull).GetField("lines", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_hullLines == null)
                {
                    Log.Warn("ProblemExport: ObstacleHull.lines nao encontrado - o jogo atualizou?");
                    return list;
                }
                var lines = _hullLines.GetValue(hull) as LineSegment[];
                if (lines == null) return list;
                for (int i = 0; i < lines.Length; i++)
                {
                    var a = lines[i].Start;
                    var b = lines[i].End;
                    list.Add(a.x); list.Add(a.y); list.Add(b.x); list.Add(b.y);
                }
            }
            catch (Exception e) { Log.Warn("ProblemExport: costa: " + e.Message); }
            return list;
        }

        /// <summary>O tipo de locomotiva que a cidade mais usa (Tram em Melbourne etc.).</summary>
        private static AssetType MainLocomotiveType(Game game)
        {
            var best = AssetType.Locomotive;
            int bestN = -1;
            var types = new[] { AssetType.Locomotive, AssetType.Tram, AssetType.Shinkansen };
            for (int i = 0; i < types.Length; i++)
            {
                var t = types[i];
                int n = Safe(() => game.AssetDatabase.GetTotalAssets(t), 0);
                if (n > bestN) { bestN = n; best = t; }
            }
            return best;
        }

        /// <summary>Grava o blob em MiniMetroGA/problems/last.bin, para o mmopt-bench.</summary>
        public static string Dump(byte[] blob)
        {
            try
            {
                string bin = Path.GetDirectoryName(typeof(ProblemExport).Assembly.Location);
                string dir = Path.Combine(Path.GetDirectoryName(bin), "problems");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "last.bin");
                using (var f = new FileStream(path, FileMode.Create, FileAccess.Write))
                    f.Write(blob, 0, blob.Length);
                return path;
            }
            catch (Exception e)
            {
                Log.Warn("ProblemExport.Dump: " + e.Message);
                return null;
            }
        }

        private static T Safe<T>(Func<T> f, T fallback)
        {
            try { return f(); } catch (Exception) { return fallback; }
        }

        /// <summary>
        /// float -> bits sem unsafe nem BitConverter. Codigo unsafe faz o compilador
        /// emitir SecurityPermission/UnverifiableCode, tipos que o mscorlib stripado
        /// do jogo nao tem (o StripAudit acusou). StructLayout/FieldOffset sao
        /// pseudo-atributos: viram flags no metadata, sem referencia de tipo.
        /// </summary>
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBits
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public float F;
            [System.Runtime.InteropServices.FieldOffset(0)] public int I;
        }

        /// <summary>Buffer de bytes little-endian sem depender de BinaryWriter.</summary>
        private sealed class Blob
        {
            private byte[] _b;
            private int _n;

            public Blob(int cap) { _b = new byte[cap]; }

            private void Room(int k)
            {
                if (_n + k <= _b.Length) return;
                int cap = _b.Length * 2;
                while (cap < _n + k) cap *= 2;
                var nb = new byte[cap];
                for (int i = 0; i < _n; i++) nb[i] = _b[i];
                _b = nb;
            }

            public void U8(int v) { Room(1); _b[_n++] = (byte)v; }

            public void U16(int v)
            {
                if (v < 0) v = 0;
                if (v > 0xFFFF) v = 0xFFFF;
                Room(2);
                _b[_n++] = (byte)v;
                _b[_n++] = (byte)(v >> 8);
            }

            public void I32(int v)
            {
                Room(4);
                _b[_n++] = (byte)v;
                _b[_n++] = (byte)(v >> 8);
                _b[_n++] = (byte)(v >> 16);
                _b[_n++] = (byte)(v >> 24);
            }

            public void U32(uint v) { I32((int)v); }

            public void F32(float f)
            {
                var u = new FloatBits();
                u.F = f;
                I32(u.I);
            }

            public void Str(string s)
            {
                // ASCII basta (ids de cidade); o resto vira '?'
                I32(s.Length);
                Room(s.Length);
                for (int i = 0; i < s.Length; i++) _b[_n++] = s[i] < 128 ? (byte)s[i] : (byte)'?';
            }

            public byte[] ToArray()
            {
                var r = new byte[_n];
                for (int i = 0; i < _n; i++) r[i] = _b[i];
                return r;
            }
        }
    }
}
