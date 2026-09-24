namespace MiniMetroGA.Ga
{
    /// <summary>
    /// Todos os knobs do AG e da funcao de fitness. Tudo isso aparece na UI.
    /// Copiado por valor antes de cada run, entao mexer nos sliders enquanto
    /// roda nao corrompe a execucao em andamento.
    /// </summary>
    public class GaConfig
    {
        // ---- Algoritmo Genetico ----
        public int PopulationSize = 200;
        public int Generations = 5000;

        /// <summary>
        /// Para o AG depois deste tempo (ms), mesmo sem chegar em Generations.
        /// 0 = so as geracoes mandam. O nucleo avalia ~10 mil redes/s por
        /// thread, entao 3 s ja sao dezenas de milhares.
        /// </summary>
        public int TimeBudgetMs = 3000;
        public int Elitism = 4;
        public int TournamentSize = 4;
        public float CrossoverRate = 0.75f;
        public float MutationRate = 0.35f;
        public int MutationsPerGenome = 2;
        public int Seed = 0;                 // 0 = aleatorio
        public bool SeedFromCurrentNetwork = true;
        public int StagnationRestart = 80;   // geracoes sem melhora antes de injetar diversidade

        // ---- Espaco de busca ----
        public int MaxLinesOverride = 0;     // 0 = usa o limite da cidade
        public int MinStationsPerLine = 2;
        public int MaxStationsPerLine = 20;
        public bool AllowLoops = true;

        // ---- Modelo de demanda ----
        // A demanda vem da tabela de spawn do proprio jogo (ver SpawnModel), entao
        // DemandScale e so um ajuste fino em cima do valor real: 1.0 = o que o jogo faz.
        public float DemandScale = 1.0f;
        public float CrowdWeight = 0.15f;    // quanto as filas atuais inflam a demanda da estacao

        // ---- Modelo de simulacao ----
        // 0 = usa a velocidade real da cidade (City.Definition.TrainDefinition).
        // So mexa nisto para testar hipoteses; o valor lido do jogo esta certo.
        public float TrainSpeedOverride = 0f;

        /// <summary>
        /// Segundo passe de roteamento em que a lotacao vira espera extra. E o que
        /// da sentido a um vagao: sem isso a capacidade nao afeta o tempo de ninguem
        /// e o AG distribui material rodante ao acaso. Custa ~2x o tempo de avaliacao.
        /// </summary>
        public bool CapacityFeedback = true;
        public float MaxCrowdingMultiplier = 4f;   // teto da espera extra por lotacao

        /// <summary>
        /// Quantos vagoes por locomotiva o AG pode propor. Nao e regra do jogo (o
        /// limite real e 64 por linha); e uma redea para o espaco de busca.
        /// </summary>
        public int MaxCarsPerLoco = 2;

        // ---- Pesos do fitness (tudo somado, menor = melhor) ----
        public float UnreachablePenalty = 900f;  // custo por par origem/forma sem rota
        public float CongestionWeight = 450f;    // penalidade por linha acima da capacidade
        public float StationLoadWeight = 260f;   // penalidade por estacao acima da capacidade
        public float TrackLengthWeight = 0.010f; // custo por unidade de trilho
        // Tunel/ponte acima do estoque nao e mais penalidade: a rede e inviavel
        // (o jogo nao deixa desenhar) e o nucleo a descarta. Fica pelo indice
        // do vetor de configuracao.
        public float CrossingOverBudget = 1500f;
        public float UnservedStationPenalty = 350f; // estacao que ficou fora de toda linha

        // ---- Nucleo nativo (Rust, native/mmopt) ----
        // A parada, a baldeacao e o embarque saem das regras do jogo (ver
        // vault/Dossie), nao de parametros.

        /// <summary>
        /// Quantos dias a frente o fitness enxerga: dia util x fim de semana, as
        /// estacoes que o jogo ja agendou e o crescimento da demanda.
        /// </summary>
        public float HorizonDays = 2f;

        /// <summary>Peso de "estacao lotando agora precisa de trem logo".</summary>
        public float UrgencyWeight = 40f;

        /// <summary>Encaixar as estacoes futuras na rede ao avaliar.</summary>
        public bool PlanFutureStations = true;

        /// <summary>Busca local no melhor individuo a cada N geracoes (0 = desliga).</summary>
        public int LocalSearchEvery = 25;

        /// <summary>Threads do nucleo nativo (0 = todas, ate 16).</summary>
        public int NativeThreads = 0;

        /// <summary>
        /// Modo automatico: so mexe na rede se o melhor for pelo menos isto
        /// melhor que a rede atual. O fitness ja cobra o custo de mexer
        /// (ChangeWeight), entao a margem aqui e so contra ruido.
        /// </summary>
        public float AutoApplyMinGain = 0.02f;

        /// <summary>
        /// Peso do custo de mexer na rede atual: linha desmontada (quem esta a
        /// bordo desembarca e os trens recomecam de um ponto so), trem tirado de
        /// linha. 0 = o AG troca a rede inteira por qualquer ganho. Ver
        /// native/mmopt/src/change.rs.
        /// </summary>
        public float ChangeWeight = 1f;

        // ---- Upgrade da semana ----
        /// <summary>Tempo de AG por opcao na recomendacao de upgrade (a base tambem).</summary>
        public int UpgradeBudgetMs = 2000;

        /// <summary>
        /// Horizonte da recomendacao de upgrade, em dias: o recurso fica para o
        /// resto da partida, entao olha mais longe que a otimizacao do dia a dia.
        /// </summary>
        public float UpgradeHorizonDays = 5f;

        /// <summary>Escolher sozinho o upgrade recomendado na tela de segunda-feira.</summary>
        public bool AutoPickUpgrade = false;

        /// <summary>
        /// Por quantos dias o fitness deixa acumular na plataforma quem nao cabe
        /// no trem (a frota so muda no upgrade da semana). Quem nao tem rota
        /// acumula 4x isso. Ver vault/Validacao do modelo.
        /// </summary>
        public float PersistDays = 7f;

        /// <summary>Grava o problema em MiniMetroGA/problems/last.bin a cada rodada.</summary>
        public bool DumpProblem = true;

        public GaConfig Clone()
        {
            return (GaConfig)MemberwiseClone();
        }
    }
}
