using System;

namespace MiniMetroGA.Core
{
    /// <summary>
    /// Largada avancada: a partida pula para a semana N pelo relogio do proprio
    /// jogo, para testar o otimizador no regime pesado sem esperar 10 minutos.
    ///
    ///   - Clock.Time vai para a meia-noite de segunda da semana N. As estacoes
    ///     cujo horario ja passou abrem no proximo Station.Update (o mesmo
    ///     City.ActivateStation de sempre), e a demanda sai com a tensao da
    ///     semana N (Clock.Tension le a semana do relogio);
    ///   - o spawn nao acumula os dias pulados: cada estacao so cria os
    ///     passageiros do dia corrente (Station.UpdatePeepSpawns), e caindo na
    ///     meia-noite nao sai nenhuma rajada;
    ///   - Game.ScheduleAudioEvents anda o relogio hora a hora e conta um premio
    ///     por segunda atravessada: a tela de upgrade abre com as N escolhas e um
    ///     botao "Locomotive xN" (Londres, semana 6: x6 e seis escolhas). Finish
    ///     confere o total e completa pelo mesmo City.UnlockUpgrade da tela se
    ///     faltar locomotiva (cidade com outro tipo de trem, por exemplo).
    ///
    /// A rede comeca vazia: o otimizador desenha tudo na primeira rodada.
    /// </summary>
    public sealed class WeekJump
    {
        private static readonly AssetType[] LocoTypes = { AssetType.Locomotive, AssetType.Shinkansen, AssetType.Tram };

        private readonly int _week;
        private readonly int[] _locosBefore = new int[LocoTypes.Length];

        public int Week => _week;

        private WeekJump(int week) { _week = week; }

        public static WeekJump Start(Game game, int week)
        {
            var jump = new WeekJump(week);
            for (int i = 0; i < LocoTypes.Length; i++) jump._locosBefore[i] = SafeTotal(game, LocoTypes[i]);
            var clock = game.City.Clock;
            clock.Time = week * 7 * clock.DayLength;
            Log.Info(string.Format("Largada avancada: relogio na semana {0} (dia {1}), tensao {2:N2}.",
                week, clock.Day, clock.Tension()));
            return jump;
        }

        /// <summary>
        /// Depois da tela de upgrade: garante uma locomotiva por semana pulada.
        /// Devolve quantas precisaram entrar alem das da tela.
        /// </summary>
        public int Finish(Game game)
        {
            int given = 0, best = 0;
            for (int i = 0; i < LocoTypes.Length; i++)
            {
                int now = SafeTotal(game, LocoTypes[i]);
                given += now - _locosBefore[i];
                // o tipo que a tela deu, ou o que a cidade ja usa
                if (now - _locosBefore[i] > 0 || (given == 0 && now > SafeTotal(game, LocoTypes[best]))) best = i;
            }
            int missing = Math.Max(0, _week - given);
            for (int k = 0; k < missing; k++) game.City.UnlockUpgrade(0, LocoTypes[best]);
            Log.Info(string.Format("Largada avancada pronta: {0} estacoes ativas, +{1} {2} alem das {3} da tela de upgrade.",
                GameHook.GetAllStations(game.City).Count, missing, LocoTypes[best], given));
            return missing;
        }

        private static int SafeTotal(Game game, AssetType t)
        {
            try { return game.AssetDatabase.GetTotalAssets(t); } catch (Exception) { return 0; }
        }
    }
}
