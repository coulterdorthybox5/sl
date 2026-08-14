using System.Collections.Generic;

namespace MainCore.Medical
{
    /// <summary>
    /// Статическое описание ранения: сколько урона наносит, чем лечится, какие эффекты выдаёт.
    /// </summary>
    public sealed class InjuryDefinition
    {
        public InjuryDefinition(
            InjuryType type,
            string displayName,
            float damagePerSecond,
            int treatmentSteps,
            IReadOnlyList<InjuryEffect> effects,
            float clotAfterDamage = 0f,
            float clottedDamagePerSecond = 0f,
            float autoHealSeconds = 0f,
            bool isBleeding = false,
            float healRateMultiplier = 1f)
        {
            Type = type;
            DisplayName = displayName;
            DamagePerSecond = damagePerSecond;
            TreatmentSteps = treatmentSteps < 1 ? 1 : treatmentSteps;
            Effects = effects;
            ClotAfterDamage = clotAfterDamage;
            ClottedDamagePerSecond = clottedDamagePerSecond;
            AutoHealSeconds = autoHealSeconds;
            IsBleeding = isBleeding;
            HealRateMultiplier = healRateMultiplier < 0f ? 0f : healRateMultiplier;
        }

        public InjuryType Type { get; }

        /// <summary>Название ранения для сообщений медику.</summary>
        public string DisplayName { get; }

        /// <summary>Базовый урон в секунду.</summary>
        public float DamagePerSecond { get; }

        /// <summary>Сколько использований аптечки нужно для полного лечения.</summary>
        public int TreatmentSteps { get; }

        /// <summary>Эффекты ранения.</summary>
        public IReadOnlyList<InjuryEffect> Effects { get; }

        /// <summary>
        /// После нанесения этого количества урона кровь свёртывается и урон падает
        /// до <see cref="ClottedDamagePerSecond"/>. 0 - свёртывания нет.
        /// </summary>
        public float ClotAfterDamage { get; }

        /// <summary>Урон в секунду после свёртывания крови.</summary>
        public float ClottedDamagePerSecond { get; }

        /// <summary>Через сколько секунд ранение проходит само. 0 - не проходит.</summary>
        public float AutoHealSeconds { get; }

        /// <summary>
        /// Это кровотечение. Пока такое ранение активно, организм не восстанавливает ХП.
        /// </summary>
        public bool IsBleeding { get; }

        /// <summary>
        /// Насколько это ранение замедляет восстановление ХП.
        /// 1.0 - не мешает, 0.3 - организм восстанавливается втрое медленнее,
        /// 0 - пока ранение не залечено, ХП не восстанавливаются вообще.
        /// Множители всех активных ранений перемножаются.
        /// </summary>
        public float HealRateMultiplier { get; }

        public bool CanClot => ClotAfterDamage > 0f;

        public bool CanAutoHeal => AutoHealSeconds > 0f;
    }

    /// <summary>
    /// Реестр всех ранений войны. Значения подобраны так, чтобы поведение было близко к реальному:
    /// кровь свёртывается, лёгкие ранения проходят сами, тяжёлые требуют нескольких перевязок.
    /// </summary>
    public static class InjuryRegistry
    {
        private static readonly Dictionary<InjuryType, InjuryDefinition> Definitions = new();

        static InjuryRegistry()
        {
            // Лёгкое ранение: -1 хп/сек, после 30 хп потерь кровь свёртывается (-1 хп раз в 5 сек),
            // лечится одним использованием аптечки, само проходит через 5 минут.
            Add(new InjuryDefinition(
                InjuryType.LightWound,
                "Лёгкое ранение",
                damagePerSecond: 1f,
                treatmentSteps: 1,
                effects: new InjuryEffect[0],
                clotAfterDamage: 30f,
                clottedDamagePerSecond: 0.2f,
                autoHealSeconds: 300f,
                isBleeding: true,
                healRateMultiplier: 0.8f));

            // Капиллярное кровотечение: Blindness 15, -1 хп/сек, после 50 хп потерь -1 хп раз в 5 сек.
            Add(new InjuryDefinition(
                InjuryType.CapillaryBleeding,
                "Капиллярное кровотечение",
                damagePerSecond: 1f,
                treatmentSteps: 1,
                effects: new[]
                {
                    new InjuryEffect("Blinded", 15, fallbackEffectName: "Blindness"),
                },
                clotAfterDamage: 50f,
                clottedDamagePerSecond: 0.2f,
                isBleeding: true,
                healRateMultiplier: 0.6f));

            // Венозное кровотечение (руки, ноги): Blindness 15, -2 хп/сек, само останавливается через 5 минут.
            Add(new InjuryDefinition(
                InjuryType.VenousBleeding,
                "Венозное кровотечение",
                damagePerSecond: 2f,
                treatmentSteps: 2,
                effects: new[]
                {
                    new InjuryEffect("Blinded", 15, fallbackEffectName: "Blindness"),
                },
                clotAfterDamage: 60f,
                clottedDamagePerSecond: 0.4f,
                autoHealSeconds: 300f,
                isBleeding: true,
                healRateMultiplier: 0.35f));

            // Артериальное кровотечение: -3 хп/сек, само не останавливается, нужно 3 перевязки.
            Add(new InjuryDefinition(
                InjuryType.ArterialBleeding,
                "Артериальное кровотечение",
                damagePerSecond: 3f,
                treatmentSteps: 3,
                effects: new[]
                {
                    new InjuryEffect("Blinded", 25, fallbackEffectName: "Blindness"),
                    new InjuryEffect("Slowness", 20, fallbackEffectName: "Disabled"),
                },
                isBleeding: true,
                healRateMultiplier: 0f));

            // Ушиб (падение с ~4 метров, удар): Slowness 15 + Concussed на 10 секунд, проходит через 5 минут.
            Add(new InjuryDefinition(
                InjuryType.Contusion,
                "Ушиб",
                damagePerSecond: 0f,
                treatmentSteps: 1,
                effects: new[]
                {
                    new InjuryEffect("Slowness", 15, fallbackEffectName: "Disabled"),
                    new InjuryEffect("Concussed", 1, duration: 10f),
                },
                autoHealSeconds: 300f,
                healRateMultiplier: 0.9f));

            // Перелом: сильно замедляет, сам не проходит, нужно 2 перевязки (шина).
            Add(new InjuryDefinition(
                InjuryType.Fracture,
                "Перелом",
                damagePerSecond: 0.2f,
                treatmentSteps: 2,
                effects: new[]
                {
                    new InjuryEffect("Slowness", 40, fallbackEffectName: "Disabled"),
                    new InjuryEffect("Concussed", 1, duration: 10f),
                },
                healRateMultiplier: 0.3f));

            // Ожог: медленно снимает хп, проходит через 10 минут, нужно 2 перевязки.
            Add(new InjuryDefinition(
                InjuryType.Burn,
                "Ожог",
                damagePerSecond: 0.5f,
                treatmentSteps: 2,
                effects: new[]
                {
                    new InjuryEffect("Burned", 1),
                    new InjuryEffect("Blinded", 10, fallbackEffectName: "Blindness"),
                },
                autoHealSeconds: 600f,
                healRateMultiplier: 0.4f));

            // Внутреннее кровотечение: не видно снаружи, требует 3 перевязок.
            Add(new InjuryDefinition(
                InjuryType.InternalBleeding,
                "Внутреннее кровотечение",
                damagePerSecond: 1.5f,
                treatmentSteps: 3,
                effects: new[]
                {
                    new InjuryEffect("Blinded", 20, fallbackEffectName: "Blindness"),
                    new InjuryEffect("Exhausted", 1),
                },
                isBleeding: true,
                healRateMultiplier: 0f));
        }

        public static IEnumerable<InjuryDefinition> All => Definitions.Values;

        public static InjuryDefinition Get(InjuryType type) => Definitions[type];

        private static void Add(InjuryDefinition definition) => Definitions[definition.Type] = definition;
    }
}
