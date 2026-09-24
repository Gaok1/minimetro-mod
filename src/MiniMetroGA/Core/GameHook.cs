using System;
using System.Collections.Generic;
using System.Reflection;

namespace MiniMetroGA.Core
{
    /// <summary>
    /// Acha a instancia viva de <see cref="Game"/> sem nenhum patching.
    ///
    /// O jogo nao expoe singleton para ela: Main.Instance guarda um IController
    /// num campo privado, e o GameController guarda o Game em outro campo
    /// privado. Como Harmony nao funciona com a BCL stripada deste build
    /// (ver Doorstop.Entrypoint), resolvemos os dois campos por reflexao pura e
    /// reconsultamos a cada frame. Sao dois GetValue por frame, custo irrelevante.
    /// </summary>
    public static class GameHook
    {
        public static Game Current { get; private set; }

        private static FieldInfo _mainControllerField;
        private static FieldInfo _controllerGameField;
        private static FieldInfo _cityStationsField;
        private static bool _warnedMissingField;

        /// <summary>Chamado todo frame pelo <see cref="ModBehaviour"/>.</summary>
        public static void Refresh()
        {
            try
            {
                var main = Main.Instance;
                if (main == null) { Current = null; return; }

                if (_mainControllerField == null)
                {
                    _mainControllerField = typeof(Main).GetField("controller",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_mainControllerField == null) { WarnOnce("Main.controller"); return; }
                }

                var controller = _mainControllerField.GetValue(main) as GameController;
                if (controller == null) { Current = null; return; }

                if (_controllerGameField == null)
                {
                    _controllerGameField = typeof(GameController).GetField("game",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_controllerGameField == null) { WarnOnce("GameController.game"); return; }
                }

                Current = _controllerGameField.GetValue(controller) as Game;
            }
            catch (Exception e)
            {
                Log.Error("GameHook.Refresh: " + e.Message);
                Current = null;
            }
        }

        private static void WarnOnce(string what)
        {
            if (_warnedMissingField) return;
            _warnedMissingField = true;
            Log.Error("Campo " + what + " nao encontrado - o jogo atualizou?");
        }

        /// <summary>
        /// City.stations e um Station[] privado (indexado por id, com buracos).
        /// Nao ha acessor publico que devolva todas de uma vez, so GetStationsOfType.
        /// </summary>
        public static List<Station> GetAllStations(City city)
        {
            var result = new List<Station>(64);
            if (city == null) return result;

            if (_cityStationsField == null)
            {
                _cityStationsField = typeof(City).GetField("stations",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (_cityStationsField == null)
                {
                    Log.Error("Campo City.stations nao encontrado - o jogo atualizou?");
                    return result;
                }
            }

            var arr = _cityStationsField.GetValue(city) as Station[];
            if (arr == null) return result;

            for (int i = 0; i < arr.Length; i++)
            {
                var s = arr[i];
                if (s == null) continue;
                if (!s.IsActive) continue;
                if (s.IsGhost) continue;
                if (s.IsDisabled) continue;
                result.Add(s);
            }
            return result;
        }

        /// <summary>
        /// Estacoes que ainda vao nascer. O CityPlanner sorteia a cidade inteira na
        /// largada (ScheduleStationSpawns) e guarda as futuras no mesmo City.stations,
        /// inativas, com ActiveTime marcado. Ordenadas pela hora em que ativam.
        /// </summary>
        public static List<Station> GetFutureStations(City city)
        {
            var result = new List<Station>(32);
            if (city == null || _cityStationsField == null) GetAllStations(city);
            if (city == null || _cityStationsField == null) return result;
            var arr = _cityStationsField.GetValue(city) as Station[];
            if (arr == null) return result;
            for (int i = 0; i < arr.Length; i++)
            {
                var s = arr[i];
                if (s == null || s.IsActive || s.IsGhost || s.IsDisabled) continue;
                result.Add(s);
            }
            result.Sort((a, b) => a.ActiveTime.CompareTo(b.ActiveTime));
            return result;
        }

        /// <summary>
        /// Inventario cru do jogo. Leitura barata (dois indices num int[]), entao a
        /// UI pode consultar direto em vez de depender do ultimo Snapshot - que so
        /// existe depois de rodar o AG.
        /// </summary>
        public struct Inventory
        {
            public int LinesAvail, LinesTotal;
            public int LocosAvail, LocosTotal;
            public int CarsAvail, CarsTotal;
            public int CrossingsAvail, CrossingsTotal;
            public int InterchangesAvail, InterchangesTotal;
            public int Stations, Lines;
        }

        public static Inventory ReadInventory(Game game)
        {
            var inv = new Inventory();
            if (game == null) return inv;
            try
            {
                var db = game.AssetDatabase;
                if (db != null)
                {
                    inv.LinesAvail = db.GetAvailableAssets(AssetType.Line);
                    inv.LinesTotal = db.GetTotalAssets(AssetType.Line);
                    inv.LocosAvail = db.GetAvailableAssets(AssetType.Locomotive)
                                   + db.GetAvailableAssets(AssetType.Shinkansen)
                                   + db.GetAvailableAssets(AssetType.Tram);
                    inv.LocosTotal = db.GetTotalAssets(AssetType.Locomotive)
                                   + db.GetTotalAssets(AssetType.Shinkansen)
                                   + db.GetTotalAssets(AssetType.Tram);
                    inv.CarsAvail = db.GetAvailableAssets(AssetType.Carriage);
                    inv.CarsTotal = db.GetTotalAssets(AssetType.Carriage);
                    inv.CrossingsAvail = db.GetAvailableAssets(AssetType.Crossing);
                    inv.CrossingsTotal = db.GetTotalAssets(AssetType.Crossing);
                    inv.InterchangesAvail = db.GetAvailableAssets(AssetType.Interchange);
                    inv.InterchangesTotal = db.GetTotalAssets(AssetType.Interchange);
                }

                var city = game.City;
                if (city != null)
                {
                    inv.Stations = GetAllStations(city).Count;
                    inv.Lines = GetLiveLines(city).Count;
                }
            }
            catch (Exception) { }
            return inv;
        }

        /// <summary>Linhas vivas (nao mothballed, com indice valido).</summary>
        public static List<Line> GetLiveLines(City city)
        {
            var result = new List<Line>(8);
            if (city == null) return result;
            for (int i = 0; i < city.LineCount; i++)
            {
                Line l;
                try { l = city.GetLine(i); }
                catch (Exception) { continue; }
                if (l == null) continue;
                if (l.IsMothballed) continue;
                if (l.Index < 0) continue;
                result.Add(l);
            }
            return result;
        }
    }
}
