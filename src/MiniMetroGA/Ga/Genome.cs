using System;
using System.Collections.Generic;
using System.Text;
using MiniMetroGA.Core;

namespace MiniMetroGA.Ga
{
    /// <summary>Estado do otimizador (o nucleo nativo publica o mesmo codigo).</summary>
    public enum GaState { Idle, Running, Done, Cancelled, Failed }

    /// <summary>
    /// Um individuo = uma rede de metro inteira.
    ///
    /// Encoding: uma lista de rotas, cada rota e uma sequencia ordenada de indices
    /// de estacao (sem repeticao dentro da mesma rota). Uma estacao pode aparecer em
    /// varias rotas - e assim que baldeacoes surgem. Alem disso cada rota carrega
    /// quantas locomotivas e vagoes recebe, porque distribuir material rodante e
    /// metade do problema no Mini Metro.
    /// </summary>
    public class Genome
    {
        public List<List<int>> Routes = new List<List<int>>();
        public List<bool> Loops = new List<bool>();
        public List<int> Locos = new List<int>();
        public List<int> Cars = new List<int>();
        /// <summary>
        /// So em loop: quantos trens rodam contra a ordem da rota. Vazio = a
        /// divisao que o jogo faria sozinho.
        /// </summary>
        public List<int> Rev = new List<int>();

        public double Fitness = double.MaxValue;
        public EvalBreakdown Breakdown;

        public int RouteCount => Routes.Count;

        /// <summary>Trens do loop r rodando contra a ordem da rota (0 em linha aberta).</summary>
        public int RevOf(int r)
        {
            if (r >= Loops.Count || !Loops[r]) return 0;
            int t = r < Locos.Count ? Locos[r] : 1;
            return r < Rev.Count ? Clamp(Rev[r], 0, t) : t / 2;
        }

        public Genome Clone()
        {
            var g = new Genome
            {
                Routes = new List<List<int>>(Routes.Count),
                Loops = new List<bool>(Loops),
                Locos = new List<int>(Locos),
                Cars = new List<int>(Cars),
                Rev = new List<int>(Rev),
                Fitness = Fitness,
                Breakdown = Breakdown
            };
            for (int i = 0; i < Routes.Count; i++) g.Routes.Add(new List<int>(Routes[i]));
            return g;
        }

        /// <summary>
        /// A rede ja e esta? Mesmas rotas (em qualquer ordem de linha, lidas nos dois
        /// sentidos), mesmos loops e, se o snapshot souber, a mesma frota. Aplicar
        /// apaga e reconstroi tudo, entao reaplicar a mesma rede so reseta trem a toa.
        /// </summary>
        public bool SameNetwork(Snapshot s)
        {
            if (s == null || s.CurrentRoutes.Count != Routes.Count) return false;
            var used = new bool[s.CurrentRoutes.Count];
            for (int i = 0; i < Routes.Count; i++)
            {
                int match = -1;
                for (int j = 0; j < s.CurrentRoutes.Count && match < 0; j++)
                {
                    if (used[j]) continue;
                    bool loop = j < s.CurrentLoop.Count && s.CurrentLoop[j];
                    if (loop != Loops[i]) continue;
                    bool reversed;
                    if (!SameSequence(Routes[i], s.CurrentRoutes[j], loop, out reversed)) continue;
                    if (j < s.CurrentLocos.Count && s.CurrentLocos[j] != Locos[i]) continue;
                    if (j < s.CurrentCars.Count && s.CurrentCars[j] != Cars[i]) continue;
                    if (loop && j < s.CurrentRev.Count)
                    {
                        // sentido dos trens: a rota atual pode estar lida ao contrario
                        int cur = reversed ? Locos[i] - s.CurrentRev[j] : s.CurrentRev[j];
                        if (cur != RevOf(i)) continue;
                    }
                    match = j;
                }
                if (match < 0) return false;
                used[match] = true;
            }
            return true;
        }

        private static bool SameSequence(List<int> a, int[] b, bool loop, out bool reversed)
        {
            reversed = false;
            if (a.Count != b.Length) return false;
            int n = a.Count;
            // loop: rotacao e sentido nao importam; linha aberta: so o sentido
            int starts = loop ? n : 1;
            for (int off = 0; off < starts; off++)
            {
                bool fwd = true, rev = true;
                for (int k = 0; k < n && (fwd || rev); k++)
                {
                    int x = loop ? b[(off + k) % n] : b[k];
                    if (a[k] != x) fwd = false;
                    int y = loop ? b[((off - k) % n + n) % n] : b[n - 1 - k];
                    if (a[k] != y) rev = false;
                }
                if (fwd) return true;
                if (rev) { reversed = true; return true; }
            }
            return false;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        public string Describe(Snapshot snap)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Routes.Count; i++)
            {
                sb.Append("L").Append(i + 1).Append(Loops[i] ? " (loop) " : " ")
                  .Append("[").Append(Locos[i]).Append("T/").Append(Cars[i]).Append("V");
                if (Loops[i]) sb.Append(" ").Append(Locos[i] - RevOf(i)).Append(">").Append(RevOf(i)).Append("<");
                sb.Append("] ");
                for (int k = 0; k < Routes[i].Count; k++)
                {
                    if (k > 0) sb.Append("-");
                    sb.Append(Routes[i][k]);
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    /// <summary>Decomposicao do custo, para a UI mostrar de onde vem o fitness.</summary>
    public struct EvalBreakdown
    {
        public double TravelCost;
        public double UnreachableCost;
        public double CongestionCost;
        public double StationLoadCost;
        public double TrackCost;
        public double CrossingCost;
        public double UnservedCost;
        public double UrgencyCost;
        /// <summary>Custo de sair da rede atual para esta (linha desmontada, trem tirado).</summary>
        public double ChangeCost;

        public int UnreachablePairs;
        public int UnservedStations;
        public int CrossingsUsed;
        public float TrackLength;
        public float WorstLineUtil;
        public float AvgTravelTime;
        public int LineCrossings;
        public int RebuiltLines;
        public int MovedTrains;
    }
}
