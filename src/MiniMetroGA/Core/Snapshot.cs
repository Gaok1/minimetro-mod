using System;
using System.Collections.Generic;
using MiniMetroGA.Ga;
using UnityEngine;

namespace MiniMetroGA.Core
{
    /// <summary>
    /// Fotografia imutavel do estado do jogo, em dados puros.
    /// O AG roda numa thread separada e NUNCA pode tocar em objetos do Unity/jogo,
    /// entao tudo que ele precisa tem que estar aqui como float/int/array.
    /// As referencias a Station/Line ficam so para o applier, que roda na main thread.
    /// </summary>
    public class Snapshot
    {
        public int N;                       // numero de estacoes ativas
        public Vector2[] Pos;               // posicao (espaco local da CityLayer)
        public int[] ShapeOf;               // slot de forma por estacao
        public int ShapeCount;              // quantas formas distintas existem
        public StationType[] ShapeType;     // slot -> StationType
        public int[] PeepCount;             // passageiros esperando agora
        public int[] Capacity;              // capacidade da estacao
        public bool[] IsInterchange;
        public float[] Centrality;          // multiplicador de spawn por posicao na cidade
        public float[] Service;             // multiplicador de spawn por qualidade de atendimento

        /// <summary>
        /// Demanda real: DemandRate[origem][slotDeForma] = passageiros por SEGUNDO que
        /// a estacao gera com destino aquela forma.
        ///
        /// Isto substitui o par SpawnWeight x ShapeProb, que modelava o destino como
        /// uma distribuicao global proporcional a quantas estacoes de cada forma
        /// existem. O jogo nao faz isso: cada forma de estacao tem a sua propria
        /// agenda origem->destino (ver SpawnModel). Um circulo manda gente para
        /// quadrado e triangulo; um losango so alimenta quadrado.
        ///
        /// Estar em passageiros/segundo (e nao numa unidade arbitraria) e o que torna
        /// a comparacao com a capacidade dos trens honesta - ver Evaluator.
        /// </summary>
        public float[][] DemandRate;

        /// <summary>Demanda total por estacao, em passageiros/dia. So para a UI.</summary>
        public float[] SpawnPerDay;

        /// <summary>
        /// Soma de DemandRate por estacao, em passageiros/segundo. E o que as
        /// heuristicas usam para saber onde vale a pena por trem e vagao.
        /// </summary>
        public float[] StationDemand;

        /// <summary>Soma de DemandRate, em passageiros/segundo. So para a UI.</summary>
        public float TotalDemandRate;

        /// <summary>true = a tabela de spawn foi lida do jogo; false = replica interna.</summary>
        public bool SpawnTableFromGame;

        public int MaxLines;                // limite de linhas da cidade

        /// <summary>
        /// Linhas que a rede pode ter: as vivas (reaproveitadas) + as livres no
        /// estoque. O calculo antigo, max(livres, total concedido), mandou 7
        /// linhas para o AG em Londres com 4 (problems/val_9.bin): o AG desenhava
        /// linha que o jogo recusava.
        /// </summary>
        public int LinesAvailable;

        /// <summary>
        /// Quantas linhas da para OPERAR, nao so desenhar.
        ///
        /// Ao criar uma linha o jogo chama AssetDatabase.ConsumeAsset para lhe dar
        /// o primeiro trem - ou seja, o trem "de brinde" sai do mesmo estoque. Uma
        /// linha sem trem nao transporta ninguem, entao o teto real de linhas e
        /// min(linhas, locomotivas). Sem isso o AG desenha uma linha a mais e ela
        /// nasce parada.
        /// </summary>
        public int LineBudget;
        public int Locomotives;             // locomotivas disponiveis para alocar
        public int Carriages;               // vagoes disponiveis para alocar
        public int Crossings;               // tuneis
        public int Bridges;                 // pontes
        public int MaxLocosPerLine;

        /// <summary>
        /// Limite de locomotivas por linha, indexado pela linha. Cidades UGC definem
        /// isso linha a linha (City.GetMaxLocomotivesPerLine), entao um limite global
        /// so estaria certo por acaso.
        /// </summary>
        public int[] MaxLocosAt;

        /// <summary>Redea do espaco de busca, copiada da GaConfig (ver GaConfig.MaxCarsPerLoco).</summary>
        public int MaxCarsPerLoco = 2;

        /// <summary>
        /// Velocidade e capacidade REAIS, lidas de City.Definition.TrainDefinition
        /// em vez de chutadas. Um railcar (locomotiva ou vagao) carrega
        /// RailcarCapacity passageiros, entao a capacidade de uma linha e
        /// (locomotivas + vagoes) * RailcarCapacity.
        /// </summary>
        public float TrainSpeed = 55f;
        public int RailcarCapacity = 6;
        public bool ReadTrainDefFromGame;

        /// <summary>Segundos de tempo real que dura um dia no jogo (Clock.DayLength).</summary>
        public float DayLength = 20f;

        // Inventario cru, para a UI mostrar a verdade.
        // "Total" e tudo que o jogo ja concedeu; "Avail" e o que esta na mao agora
        // (o resto esta preso nas linhas atuais). Como aplicar apaga as linhas,
        // o orcamento util e o Total - mas o jogador precisa ver os dois numeros,
        // senao "0 vagoes aplicados" parece bug quando na verdade nao ha vagao.
        public int LinesTotal, LinesAvail;
        public int LocosTotal, LocosAvail;
        public int CarsTotal, CarsAvail;
        public int CrossingsTotal, CrossingsAvail;
        public int InterchangesTotal, InterchangesAvail;

        public List<int[]> CurrentRoutes = new List<int[]>();  // rotas atuais (indices de estacao)
        public List<bool> CurrentLoop = new List<bool>();
        public List<int> CurrentLocos = new List<int>();   // trens ativos por linha atual
        public List<int> CurrentCars = new List<int>();    // vagoes por linha atual
        public List<int> CurrentRev = new List<int>();     // trens contra a ordem da rota (loop)
        public List<int> CurrentOnboard = new List<int>(); // passageiros a bordo agora
        public List<Line> CurrentLineRefs = new List<Line>(); // so main thread

        /// <summary>CityDefinition.InterchangeCapacity.</summary>
        public int InterchangeCapacity = 18;

        // referencias vivas - somente main thread
        public Station[] StationRefs;
        public City CityRef;
        public Game GameRef;

        public string CityName = "?";
        public int Score;
        public int Week;

        /// <summary>Limite de locomotivas da linha <paramref name="line"/>, com folga se o indice sair do range.</summary>
        public int MaxLocosFor(int line)
        {
            if (MaxLocosAt == null || MaxLocosAt.Length == 0) return MaxLocosPerLine;
            if (line < 0 || line >= MaxLocosAt.Length) return MaxLocosPerLine;
            return MaxLocosAt[line];
        }

        public static Snapshot Capture(Game game, GaConfig cfg)
        {
            if (game == null) return null;
            var city = game.City;
            if (city == null) return null;

            var stations = GameHook.GetAllStations(city);
            if (stations.Count < 2) return null;

            var s = new Snapshot();
            s.GameRef = game;
            s.CityRef = city;
            s.N = stations.Count;
            s.StationRefs = stations.ToArray();
            s.Pos = new Vector2[s.N];
            s.ShapeOf = new int[s.N];
            s.SpawnPerDay = new float[s.N];
            s.PeepCount = new int[s.N];
            s.Capacity = new int[s.N];
            s.IsInterchange = new bool[s.N];
            s.Centrality = new float[s.N];
            s.Service = new float[s.N];

            var shapeSlots = new List<StationType>();
            var shapeTally = new List<int>();

            for (int i = 0; i < s.N; i++)
            {
                var st = stations[i];
                s.Pos[i] = st.Position;
                s.PeepCount[i] = SafeInt(() => st.PeepCount);
                s.Capacity[i] = Mathf.Max(1, SafeInt(() => st.PeepCapacity, 6));
                s.IsInterchange[i] = SafeBool(() => st.IsInterchange);
                s.Centrality[i] = Mathf.Max(0.01f, SafeFloat(() => st.Centrality, 1f));
                s.Service[i] = Mathf.Max(0.01f, SafeFloat(() => st.Service, 1f));

                var t = st.Type;
                int slot = shapeSlots.IndexOf(t);
                if (slot < 0) { shapeSlots.Add(t); shapeTally.Add(0); slot = shapeSlots.Count - 1; }
                shapeTally[slot]++;
                s.ShapeOf[i] = slot;
            }

            s.ShapeCount = shapeSlots.Count;
            s.ShapeType = shapeSlots.ToArray();

            ReadTrainDefinition(s, city);
            BuildDemand(s, city, game, cfg);

            // Orcamento
            s.MaxLines = Mathf.Max(1, SafeInt(() => city.MaxLineCount, 3));
            s.MaxLocosPerLine = Mathf.Max(1, SafeInt(() => game.MaxLocomotivesPerRoute, 4));
            s.MaxLocosAt = new int[Mathf.Max(1, s.MaxLines)];
            for (int i = 0; i < s.MaxLocosAt.Length; i++)
            {
                int li = i;
                s.MaxLocosAt[i] = Mathf.Max(1, SafeInt(
                    () => city.GetMaxLocomotivesPerLine(li), s.MaxLocosPerLine));
            }

            var db = game.AssetDatabase;
            int availLines = SafeInt(() => db.GetAvailableAssets(AssetType.Line));
            int totalLines = SafeInt(() => db.GetTotalAssets(AssetType.Line));
            int liveLines = GameHook.GetLiveLines(city).Count;
            s.LinesAvailable = Mathf.Clamp(liveLines + availLines, 1, s.MaxLines);
            s.InterchangeCapacity = Mathf.Max(1, SafeInt(() => city.Definition.InterchangeCapacity, 18));

            s.Locomotives = SafeInt(() => db.GetTotalAssets(AssetType.Locomotive))
                          + SafeInt(() => db.GetTotalAssets(AssetType.Shinkansen))
                          + SafeInt(() => db.GetTotalAssets(AssetType.Tram));
            s.Carriages = SafeInt(() => db.GetTotalAssets(AssetType.Carriage));
            s.Crossings = SafeInt(() => db.GetTotalAssets(AssetType.Crossing));
            s.Bridges = SafeInt(() => db.GetTotalAssets(AssetType.Bridge));

            s.LinesTotal = totalLines;
            s.LinesAvail = availLines;
            s.LocosTotal = s.Locomotives;
            s.LocosAvail = SafeInt(() => db.GetAvailableAssets(AssetType.Locomotive))
                         + SafeInt(() => db.GetAvailableAssets(AssetType.Shinkansen))
                         + SafeInt(() => db.GetAvailableAssets(AssetType.Tram));
            s.CarsTotal = s.Carriages;
            s.CarsAvail = SafeInt(() => db.GetAvailableAssets(AssetType.Carriage));
            s.CrossingsTotal = s.Crossings;
            s.CrossingsAvail = SafeInt(() => db.GetAvailableAssets(AssetType.Crossing));
            s.InterchangesTotal = SafeInt(() => db.GetTotalAssets(AssetType.Interchange));
            s.InterchangesAvail = SafeInt(() => db.GetAvailableAssets(AssetType.Interchange));

            s.LineBudget = Mathf.Clamp(Mathf.Min(s.LinesAvailable, s.Locomotives), 1, s.MaxLines);
            s.MaxCarsPerLoco = Mathf.Max(1, cfg.MaxCarsPerLoco);

            // Rotas atuais, para semear a populacao inicial
            var idx = new Dictionary<Station, int>(s.N);
            for (int i = 0; i < s.N; i++) idx[stations[i]] = i;
            foreach (var line in GameHook.GetLiveLines(city))
            {
                var route = ExtractRoute(line, idx);
                if (route != null && route.Length >= 2)
                {
                    s.CurrentRoutes.Add(route);
                    s.CurrentLoop.Add(SafeBool(() => line.IsLooping));
                    s.CurrentLocos.Add(SafeInt(() => line.ActiveTrainCount, 1));
                    s.CurrentCars.Add(SafeInt(() => line.CarriageCount, 0));
                    s.CurrentRev.Add(SafeInt(() => BackwardTrains(line), 0));
                    s.CurrentOnboard.Add(SafeInt(() => Onboard(line), 0));
                    s.CurrentLineRefs.Add(line);
                }
            }

            s.Score = SafeInt(() => game.Score);
            s.Week = SafeInt(() => game.Week);
            try { s.CityName = city.Definition != null ? city.Definition.Id.ToString() : "?"; }
            catch (Exception) { s.CityName = "?"; }

            return s;
        }

        /// <summary>
        /// Monta a matriz de demanda origem -> forma de destino, em passageiros/segundo.
        ///
        /// Segue o caminho do proprio jogo (Station.PeepSpawnScale + StationSchedule.
        /// CreateSpawns): a agenda da forma da estacao diz quantos passageiros/dia ela
        /// manda para cada destino; isso e multiplicado pela escala da cidade, pela
        /// centralidade da estacao, pelo servico dela e pela tensao do relogio.
        ///
        /// Um destino cuja forma nao existe na cidade nao entra: o jogo descarta esses
        /// passageiros antes de solta-los na plataforma.
        /// </summary>
        private static void BuildDemand(Snapshot s, City city, Game game, GaConfig cfg)
        {
            SpawnModel.EnsureLoaded();
            s.SpawnTableFromGame = SpawnModel.FromGame;

            s.DemandRate = new float[s.N][];
            for (int i = 0; i < s.N; i++) s.DemandRate[i] = new float[s.ShapeCount];

            var today = SafeDay(() => city.Clock.DayType, DayType.ANY);
            int day = SafeInt(() => city.Clock.Day);
            float tension = SafeFloat(() => city.Clock.Tension(Constants.PEEP_SPAWN_TENSION_SCALE), 1f);
            float cityScale = SafeFloat(() => city.PeepSpawnScale, 1f);
            bool serviceMatters = SafeBool(() => city.DoesServiceAffectPeepSpawns);
            s.DayLength = Mathf.Max(1f, SafeFloat(() => city.Clock.DayLength, 20f));

            float total = 0f;
            for (int i = 0; i < s.N; i++)
            {
                // Station.PeepSpawnScale
                float scale = cityScale * s.Centrality[i];
                if (serviceMatters) scale *= s.Service[i];

                // StationSchedule.CreateSpawns: a primeira semana entra suavizada
                if (day < 7 && scale > 1f)
                {
                    float k = Mathf.Sin(day / 7f);
                    scale = scale * k + (1f - k);
                }
                scale *= Constants.PEEP_SPAWN_SCALE;

                // Fila atual como sinal de urgencia: nao esta no jogo, e um vies nosso
                // para o AG socorrer quem ja esta lotando agora.
                float crowd = 1f + cfg.CrowdWeight * s.PeepCount[i];

                var origin = s.ShapeType[s.ShapeOf[i]];
                float perDay = 0f;
                for (int t = 0; t < s.ShapeCount; t++)
                {
                    if (t == s.ShapeOf[i]) continue;
                    float d = SpawnModel.PerDay(origin, s.ShapeType[t], today) * tension * scale;
                    perDay += d;
                    s.DemandRate[i][t] = d / s.DayLength * crowd * cfg.DemandScale;
                    total += s.DemandRate[i][t];
                }
                s.SpawnPerDay[i] = perDay;
            }
            s.TotalDemandRate = total;

            // Rede de seguranca: se a tabela nao cobrir nenhuma das formas presentes
            // (cidade so de GEM, tipo novo num update, reflexao falhando junto com um
            // fallback desatualizado), a demanda zeraria e o fitness perderia o sentido
            // - toda rede pontuaria igual. Nesse caso voltamos ao modelo antigo, que e
            // grosseiro mas nao e degenerado.
            if (total <= 1e-6f)
            {
                Log.Warn("Demanda calculada deu zero; usando modelo uniforme de reserva.");
                UniformDemandFallback(s, cfg);
            }

            s.StationDemand = new float[s.N];
            for (int i = 0; i < s.N; i++)
            {
                float sum = 0f;
                for (int t = 0; t < s.ShapeCount; t++) sum += s.DemandRate[i][t];
                s.StationDemand[i] = sum;
            }
        }

        /// <summary>Modelo antigo: toda forma e destino plausivel, peso pela raridade.</summary>
        private static void UniformDemandFallback(Snapshot s, GaConfig cfg)
        {
            float share = 1f / Mathf.Max(1, s.ShapeCount);
            float total = 0f;
            for (int i = 0; i < s.N; i++)
            {
                float crowd = 1f + cfg.CrowdWeight * s.PeepCount[i];
                float baseRate = crowd * cfg.DemandScale / Mathf.Max(1f, s.DayLength);
                for (int t = 0; t < s.ShapeCount; t++)
                {
                    if (t == s.ShapeOf[i]) continue;
                    s.DemandRate[i][t] = baseRate * share;
                    total += s.DemandRate[i][t];
                }
                s.SpawnPerDay[i] = crowd;
            }
            s.TotalDemandRate = total;
        }

        /// <summary>Velocidade e capacidade reais do material rodante desta cidade.</summary>
        private static void ReadTrainDefinition(Snapshot s, City city)
        {
            try
            {
                var def = city.Definition != null ? city.Definition.TrainDefinition : null;
                if (def == null) return;

                var loco = def.GetLocomotiveDefinition(AssetType.Locomotive);
                if (loco != null && loco.Speed > 0f)
                {
                    s.TrainSpeed = loco.Speed;
                    s.ReadTrainDefFromGame = true;
                }
                if (def.Capacity > 0) s.RailcarCapacity = def.Capacity;
            }
            catch (Exception e)
            {
                Log.Warn("Nao consegui ler TrainDefinition: " + e.Message);
            }
        }

        /// <summary>Le a sequencia de estacoes de uma linha percorrendo os Links.</summary>
        /// <summary>
        /// Trens andando contra a ordem dos links (a ordem de ExtractRoute). So
        /// importa em loop: em linha aberta todo trem vai e volta.
        /// </summary>
        private static int BackwardTrains(Line line)
        {
            int n = 0;
            for (int k = 0; k < line.TrainCount; k++)
            {
                var t = line.GetTrain(k);
                if (t == null) continue;
                var dl = t.Link;
                if (!dl.IsNull && dl.Direction == LineDirection.BACKWARDS) n++;
            }
            return n;
        }

        /// <summary>Passageiros a bordo dos trens da linha.</summary>
        public static int Onboard(Line line)
        {
            int n = 0;
            for (int k = 0; k < line.TrainCount; k++)
            {
                var t = line.GetTrain(k);
                if (t != null) n += t.PeepCount;
            }
            return n;
        }

        /// <summary>
        /// Sequencia de estacoes da linha, seguindo as conexoes dos links a partir
        /// do primeiro (Line.FirstActiveLink). A lista de links da Line NAO esta
        /// na ordem do trajeto depois de uma edicao no meio (o link novo entra no
        /// fim, o velho fica pendente ate o trem sair dele); le-la na ordem dava
        /// 0-2-1-3 para uma linha 0-2-3-1.
        /// </summary>
        internal static int[] ExtractRoute(Line line, Dictionary<Station, int> idx)
        {
            var walked = WalkRoute(line, idx);
            return walked ?? ExtractRouteByList(line, idx);
        }

        private static int[] WalkRoute(Line line, Dictionary<Station, int> idx)
        {
            try
            {
                var first = line.FirstActiveLink;
                if (first == null) return null;
                var seq = new List<int>();
                var seen = new HashSet<int>();
                var lk = first;
                for (int guard = 0; lk != null && guard < 512; guard++)
                {
                    var a = lk.Start != null ? lk.Start.Station : null;
                    var b = lk.End != null ? lk.End.Station : null;
                    if (a == null || b == null) break;
                    int ia, ib;
                    if (idx.TryGetValue(a, out ia) && seen.Add(ia)) seq.Add(ia);
                    if (idx.TryGetValue(b, out ib) && seen.Add(ib)) seq.Add(ib);
                    var next = lk.NextLink;
                    if (next == null || next == first) break;
                    lk = next;
                }
                return seq.Count >= 2 ? seq.ToArray() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int[] ExtractRouteByList(Line line, Dictionary<Station, int> idx)
        {
            try
            {
                var seq = new List<int>();
                for (int i = 0; i < line.Count; i++)
                {
                    var link = line[i];
                    if (link == null) continue;
                    var a = link.Start != null ? link.Start.Station : null;
                    var b = link.End != null ? link.End.Station : null;
                    if (a != null && idx.ContainsKey(a))
                    {
                        int ia = idx[a];
                        if (seq.Count == 0 || seq[seq.Count - 1] != ia) seq.Add(ia);
                    }
                    if (b != null && idx.ContainsKey(b))
                    {
                        int ib = idx[b];
                        if (seq.Count == 0 || seq[seq.Count - 1] != ib) seq.Add(ib);
                    }
                }
                // remove duplicatas mantendo a ordem (loop fecha sozinho no modelo)
                var seen = new HashSet<int>();
                var clean = new List<int>(seq.Count);
                foreach (int v in seq) if (seen.Add(v)) clean.Add(v);
                return clean.Count >= 2 ? clean.ToArray() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int SafeInt(Func<int> f, int fallback = 0)
        {
            try { return f(); } catch (Exception) { return fallback; }
        }

        private static bool SafeBool(Func<bool> f, bool fallback = false)
        {
            try { return f(); } catch (Exception) { return fallback; }
        }

        private static float SafeFloat(Func<float> f, float fallback = 0f)
        {
            try { return f(); } catch (Exception) { return fallback; }
        }

        private static DayType SafeDay(Func<DayType> f, DayType fallback)
        {
            try { return f(); } catch (Exception) { return fallback; }
        }
    }
}
