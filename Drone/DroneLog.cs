using Exiled.API.Features;

namespace MainCore.Drone
{
    /// <summary>
    /// Логирование системы дрона.
    /// </summary>
    /// <remarks>
    /// У каждой записи есть префикс <c>[Drone]</c> и имя шага, чтобы по логу сразу
    /// было видно, где именно оборвалась цепочка: превью, спавн, вход, полёт, удар,
    /// выход. Обычные шаги пишутся только при включённом <c>DroneDebug</c>, а
    /// предупреждения и ошибки - всегда: молча ломаться система не должна.
    ///
    /// Лог сервера выводит не-ASCII как '?', поэтому текст только английский.
    /// </remarks>
    internal static class DroneLog
    {
        private static bool Enabled => MainCorePlugin.Instance?.Config?.DroneDebug ?? false;

        /// <summary>Штатный шаг работы. Пишется только при включённой отладке.</summary>
        internal static void Step(string step, string message)
        {
            if (Enabled)
                Log.Info($"[Drone] {step}: {message}");
        }

        /// <summary>Шаг, привязанный к игроку - в лог попадает и его ник.</summary>
        internal static void Step(string step, Player? player, string message)
        {
            if (Enabled)
                Log.Info($"[Drone] {step}: {Describe(player)} {message}");
        }

        /// <summary>Ожидаемая, но нештатная ситуация. Пишется всегда.</summary>
        internal static void Warn(string step, string message)
            => Log.Warn($"[Drone] {step}: {message}");

        /// <summary>Ожидаемая, но нештатная ситуация у игрока. Пишется всегда.</summary>
        internal static void Warn(string step, Player? player, string message)
            => Log.Warn($"[Drone] {step}: {Describe(player)} {message}");

        /// <summary>Сбой. Пишется всегда.</summary>
        internal static void Error(string step, string message)
            => Log.Error($"[Drone] {step}: {message}");

        private static string Describe(Player? player)
            => player is null ? "<null player>" : $"{player.Nickname} ({player.UserId})";
    }
}