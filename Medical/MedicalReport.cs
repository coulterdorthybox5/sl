using System.Text;
using Exiled.API.Features;

namespace MainCore.Medical
{
    /// <summary>
    /// Формирует текстовое описание состояния игрока для команд осмотра.
    /// Вынесено отдельно, чтобы игрок и медик видели один и тот же отчёт.
    /// </summary>
    public static class MedicalReport
    {
        /// <summary>
        /// Собирает отчёт о состоянии игрока.
        /// </summary>
        /// <param name="player">Осматриваемый игрок.</param>
        /// <param name="self">Осматривает ли игрок сам себя (меняются формулировки).</param>
        public static string Build(Player player, bool self)
        {
            StringBuilder builder = new();

            string subject = self ? "Ваше состояние" : $"Состояние {player.Nickname}";

            builder.AppendLine($"=== {subject} ===");
            builder.AppendLine($"Здоровье: {player.Health:0}/{player.MaxHealth:0}");

            if (!MedicalManager.TryGetState(player, out PlayerMedicalState? state) || state is null || !state.HasInjuries)
            {
                builder.Append("Ранений нет.");
                return builder.ToString();
            }

            builder.AppendLine($"Ранения: {state.DescribeAll()}");

            float damage = state.TotalDamagePerSecond;
            if (damage > 0f)
                builder.AppendLine($"Потеря крови: {damage:0.#} ХП/сек");

            if (state.PendingHeal > 0f)
                builder.AppendLine($"Осталось восстановить: {state.PendingHeal:0.#} ХП");

            builder.Append(DescribeHealing(state));

            return builder.ToString();
        }

        /// <summary>Объясняет, почему организм восстанавливается быстро, медленно или не восстанавливается.</summary>
        private static string DescribeHealing(PlayerMedicalState state)
        {
            Config config = MainCorePlugin.Instance.Config;

            if (config.StopHealingWhileBleeding && state.HasActiveBleeding)
                return "Заживление остановлено: кровотечение не перевязано.";

            if (!config.UseInjuryHealRates)
                return "Заживление идёт с базовой скоростью.";

            float multiplier = state.GetEffectiveHealRateMultiplier(config.MinHealRateMultiplier);

            if (multiplier <= 0f)
                return "Заживление невозможно: требуется помощь медика.";

            if (multiplier >= 0.99f)
                return "Заживление идёт нормально.";

            return $"Скорость заживления: {multiplier * 100f:0}% от обычной.";
        }
    }
}
