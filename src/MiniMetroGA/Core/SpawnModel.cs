using System;
using System.Collections.Generic;
using System.Reflection;

namespace MiniMetroGA.Core
{
    /// <summary>
    /// A tabela de geracao de passageiros do Mini Metro.
    ///
    /// Descoberta central: o destino de um passageiro NAO e sorteado entre as formas
    /// presentes na cidade. Cada tipo de estacao tem a SUA propria agenda
    /// (StationDatabase.Load), e a agenda diz quantos passageiros por dia aquela forma
    /// manda para cada outra forma, em que hora e em que tipo de dia. Ou seja, a
    /// demanda e uma matriz origem->destino, nao uma distribuicao global.
    ///
    /// Exemplo do proprio jogo: CIRCLE manda 1.0/dia para SQUARE em dia util e
    /// 1.0/dia para TRIANGLE em qualquer dia; TRIANGLE devolve 1.0/dia para CIRCLE;
    /// EGG despeja 2.0/dia em CIRCLE, mas so no fim de semana. Um modelo que usa
    /// "probabilidade proporcional a quantas estacoes daquela forma existem" erra
    /// justamente nos casos que decidem a partida.
    ///
    /// Preferimos LER a tabela do jogo por reflexao - assim ela continua certa se o
    /// jogo mudar. A replica hardcoded abaixo e so o plano B.
    /// </summary>
    public static class SpawnModel
    {
        public struct Entry
        {
            public StationType Dest;
            public float PerDay;
            public int Hour;
            public DayType Day;
        }

        /// <summary>true = tabela lida do StationDatabase do jogo; false = replica local.</summary>
        public static bool FromGame { get; private set; }

        private static Dictionary<StationType, List<Entry>> _table;
        private static bool _loaded;

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                _table = ReadFromGame();
                FromGame = _table != null && _table.Count > 0;
            }
            catch (Exception e)
            {
                Log.Warn("SpawnModel: reflexao no StationDatabase falhou (" + e.Message + ").");
                _table = null;
            }
            if (_table == null || _table.Count == 0)
            {
                _table = BuiltIn();
                FromGame = false;
            }
            Log.Info("SpawnModel: tabela de spawn " + (FromGame ? "lida do jogo" : "interna (fallback)")
                     + ", " + _table.Count + " formas de origem.");
        }

        /// <summary>
        /// Passageiros por dia que uma estacao da forma <paramref name="origin"/> gera
        /// com destino a forma <paramref name="dest"/>, no tipo de dia informado.
        /// Ainda falta multiplicar pela escala da estacao (centralidade x servico) e
        /// pela tensao do relogio - isso e feito no Snapshot.
        /// </summary>
        public static float PerDay(StationType origin, StationType dest, DayType today)
        {
            EnsureLoaded();
            List<Entry> entries;
            if (!_table.TryGetValue(origin, out entries)) return 0f;
            float sum = 0f;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Dest != dest) continue;
                // mesma checagem do StationSchedule.CreateSpawns
                if ((e.Day & today) == 0) continue;
                sum += e.PerDay;
            }
            return sum;
        }

        /// <summary>Total diario que a forma gera, somando todos os destinos.</summary>
        public static float PerDayTotal(StationType origin, DayType today)
        {
            EnsureLoaded();
            List<Entry> entries;
            if (!_table.TryGetValue(origin, out entries)) return 0f;
            float sum = 0f;
            for (int i = 0; i < entries.Count; i++)
                if ((entries[i].Day & today) != 0) sum += entries[i].PerDay;
            return sum;
        }

        /// <summary>
        /// A tabela inteira, achatada: (origem, entrada). O nucleo nativo recebe
        /// isto e faz a conta por cenario (dia util x fim de semana) do lado de la.
        /// </summary>
        public static List<KeyValuePair<StationType, Entry>> AllEntries()
        {
            EnsureLoaded();
            var list = new List<KeyValuePair<StationType, Entry>>(64);
            foreach (var kv in _table)
                for (int i = 0; i < kv.Value.Count; i++)
                    list.Add(new KeyValuePair<StationType, Entry>(kv.Key, kv.Value[i]));
            return list;
        }

        /// <summary>Origens que a tabela conhece - para a UI conferir cobertura.</summary>
        public static bool Knows(StationType origin)
        {
            EnsureLoaded();
            return _table.ContainsKey(origin);
        }

        // ------------------------------------------------------------------
        // Leitura por reflexao
        // ------------------------------------------------------------------
        private static Dictionary<StationType, List<Entry>> ReadFromGame()
        {
            var db = StationDatabase.Instance;
            if (db == null) return null;

            var schedulesField = typeof(StationDatabase).GetField(
                "schedules", BindingFlags.NonPublic | BindingFlags.Instance);
            if (schedulesField == null) return null;

            var schedules = schedulesField.GetValue(db) as System.Collections.IDictionary;
            if (schedules == null) return null;

            var typesField = typeof(StationSchedule).GetField(
                "peepSpawnTypes", BindingFlags.NonPublic | BindingFlags.Instance);
            if (typesField == null) return null;

            var result = new Dictionary<StationType, List<Entry>>();
            foreach (System.Collections.DictionaryEntry kv in schedules)
            {
                var origin = (StationType)kv.Key;
                var list = typesField.GetValue(kv.Value) as List<PeepSpawnType>;
                if (list == null) continue;

                var entries = new List<Entry>(list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    entries.Add(new Entry
                    {
                        Dest = t.Type,
                        PerDay = t.NumSpawns,
                        Hour = t.Hour,
                        Day = t.Day
                    });
                }
                result[origin] = entries;
            }
            return result;
        }

        // ------------------------------------------------------------------
        // Replica da StationDatabase.Load()
        // ------------------------------------------------------------------
        private static void Add(List<Entry> list, int hour, DayType day, StationType dest, float perDay)
        {
            list.Add(new Entry { Dest = dest, PerDay = perDay, Hour = hour, Day = day });
        }

        /// <summary>
        /// Copia fiel de StationDatabase.Load(), incluindo os deslizes do original:
        /// a entrada de GEM da agenda do SQUARE cai na lista do CIRCLE, e a agenda do
        /// GEM nasce vazia enquanto duas entradas dela vao parar no WEDGE. Replicamos
        /// o comportamento, nao a intencao - o fitness precisa modelar o jogo que
        /// esta rodando. Consequencia pratica: estacao GEM nao gera passageiro.
        /// </summary>
        private static Dictionary<StationType, List<Entry>> BuiltIn()
        {
            var t = new Dictionary<StationType, List<Entry>>();

            var circle = new List<Entry>();
            Add(circle, 7, DayType.WEEKDAY, StationType.SQUARE, 1f);
            Add(circle, 16, DayType.ANY, StationType.TRIANGLE, 1f);
            Add(circle, 11, DayType.WEEKEND, StationType.TRIANGLE, 0.75f);
            Add(circle, 10, DayType.ANY, StationType.CROSS, 0.25f);
            Add(circle, 14, DayType.WEEKEND, StationType.WEDGE, 0.5f);
            Add(circle, 9, DayType.WEEKEND, StationType.STAR, 0.25f);
            Add(circle, 11, DayType.WEEKEND, StationType.DIAMOND, 0.25f);
            Add(circle, 9, DayType.WEEKEND, StationType.GEM, 0.2f);
            Add(circle, 13, DayType.WEEKEND, StationType.GEM, 0.2f);
            Add(circle, 15, DayType.WEEKEND, StationType.EGG, 0.4f);
            Add(circle, 11, DayType.WEEKDAY, StationType.GEM, 0.1f);   // deslize do original
            t[StationType.CIRCLE] = circle;

            var triangle = new List<Entry>();
            Add(triangle, 18, DayType.ANY, StationType.CIRCLE, 1f);
            Add(triangle, 15, DayType.WEEKEND, StationType.CIRCLE, 0.75f);
            Add(triangle, 15, DayType.WEEKEND, StationType.STAR, 0.25f);
            Add(triangle, 9, DayType.WEEKEND, StationType.PENTAGON, 0.25f);
            t[StationType.TRIANGLE] = triangle;

            var square = new List<Entry>();
            Add(square, 17, DayType.WEEKDAY, StationType.CIRCLE, 1f);
            Add(square, 12, DayType.WEEKDAY, StationType.TRIANGLE, 0.5f);
            Add(square, 10, DayType.WEEKDAY, StationType.DIAMOND, 0.25f);
            Add(square, 13, DayType.WEEKDAY, StationType.PENTAGON, 0.25f);
            t[StationType.SQUARE] = square;

            var pentagon = new List<Entry>();
            Add(pentagon, 9, DayType.WEEKDAY, StationType.TRIANGLE, 0.25f);
            Add(pentagon, 14, DayType.WEEKDAY, StationType.SQUARE, 0.25f);
            t[StationType.PENTAGON] = pentagon;

            var diamond = new List<Entry>();
            Add(diamond, 10, DayType.WEEKDAY, StationType.SQUARE, 0.5f);
            t[StationType.DIAMOND] = diamond;

            var star = new List<Entry>();
            Add(star, 12, DayType.WEEKDAY, StationType.TRIANGLE, 0.5f);
            Add(star, 15, DayType.WEEKEND, StationType.CIRCLE, 0.5f);
            t[StationType.STAR] = star;

            var cross = new List<Entry>();
            Add(cross, 4, DayType.ANY, StationType.CIRCLE, 0.25f);
            Add(cross, 16, DayType.ANY, StationType.CIRCLE, 0.25f);
            t[StationType.CROSS] = cross;

            var wedge = new List<Entry>();
            Add(wedge, 8, DayType.ANY, StationType.CIRCLE, 0.25f);
            Add(wedge, 16, DayType.ANY, StationType.CIRCLE, 0.25f);
            Add(wedge, 13, DayType.WEEKEND, StationType.CIRCLE, 0.5f);
            Add(wedge, 14, DayType.WEEKDAY, StationType.SQUARE, 0.1f);  // deslize do original
            Add(wedge, 20, DayType.WEEKEND, StationType.CIRCLE, 0.4f);  // deslize do original
            t[StationType.WEDGE] = wedge;

            t[StationType.GEM] = new List<Entry>();                     // vazia no original

            var egg = new List<Entry>();
            Add(egg, 21, DayType.WEEKEND, StationType.CIRCLE, 2f);
            t[StationType.EGG] = egg;

            return t;
        }
    }
}
