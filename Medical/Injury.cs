using System;

namespace MainCore.Medical
{
    /// <summary>
    /// Активное ранение конкретного игрока.
    /// </summary>
    public sealed class Injury
    {
        public Injury(InjuryType type, BodyPart bodyPart)
        {
            Type = type;
            BodyPart = bodyPart;
            Definition = InjuryRegistry.Get(type);
        }

        public InjuryType Type { get; }

        public BodyPart BodyPart { get; }

        public InjuryDefinition Definition { get; }

        /// <summary>Сколько ступеней лечения уже выполнено.</summary>
        public int TreatedSteps { get; private set; }

        /// <summary>Сколько урона ранение уже нанесло (для свёртывания крови).</summary>
        public float DamageDealt { get; private set; }

        /// <summary>Сколько секунд ранение существует (для автолечения).</summary>
        public float Age { get; private set; }

        /// <summary>
        /// На ранение наложена перевязка. До этого визуала нет: голая рана
        /// снаружи не видна, бинт появляется только после применения аптечки.
        /// </summary>
        public bool IsDressed { get; private set; }

        /// <summary>Сколько секунд назад наложена перевязка (бинт со временем грязнеет).</summary>
        public float DressedAge { get; private set; }

        /// <summary>
        /// Ранение уже зажило, но перевязку рано снимать: бинт остаётся на модели,
        /// пока не истечёт <see cref="Config.DressingLingerSeconds"/>.
        /// </summary>
        /// <remarks>
        /// Без этого визуал бинта не появлялся бы вообще. Большинство ранений
        /// требует одну ступень лечения, а аптечка даёт ровно одну - ранение
        /// становилось IsHealed в тот же момент и удалялось из списка, так что
        /// показывать перевязку было уже не на чем.
        /// </remarks>
        public bool KeepForDressing =>
            IsDressed && DressedAge < MainCorePlugin.Instance.Config.DressingLingerSeconds;

        /// <summary>Накопленный дробный урон, чтобы наносить его целыми числами.</summary>
        public float DamageBuffer { get; private set; }

        /// <summary>Ранение полностью вылечено.</summary>
        public bool IsHealed => TreatedSteps >= Definition.TreatmentSteps;

        /// <summary>Кровь свернулась - урон резко упал.</summary>
        public bool IsClotted => Definition.CanClot && DamageDealt >= Definition.ClotAfterDamage;

        /// <summary>Ранение всё ещё активно кровоточит (мешает восстановлению ХП).</summary>
        public bool IsActivelyBleeding => Definition.IsBleeding && !IsHealed && CurrentDamagePerSecond > 0f;

        /// <summary>
        /// Доля ранения, которая осталась необработанной.
        /// Например, при 3 ступенях: 1.0 -> 0.66 -> 0.33 -> 0.
        /// </summary>
        public float RemainingFraction
        {
            get
            {
                int remaining = Definition.TreatmentSteps - TreatedSteps;
                if (remaining <= 0)
                    return 0f;

                return (float)remaining / Definition.TreatmentSteps;
            }
        }

        /// <summary>Текущий урон в секунду с учётом свёртывания крови и лечения.</summary>
        public float CurrentDamagePerSecond
        {
            get
            {
                float baseDamage = IsClotted ? Definition.ClottedDamagePerSecond : Definition.DamagePerSecond;
                return baseDamage * RemainingFraction;
            }
        }

        /// <summary>
        /// Насколько это ранение сейчас замедляет восстановление ХП.
        /// Учитывает степень лечения и свёртывание крови: перевязанная рана
        /// мешает заживлению меньше, чем свежая.
        /// </summary>
        public float CurrentHealRateMultiplier
        {
            get
            {
                if (IsHealed)
                    return 1f;

                float baseMultiplier = Definition.HealRateMultiplier;

                // Свернувшаяся кровь мешает вдвое меньше - организм уже начал восстанавливаться.
                if (IsClotted)
                    baseMultiplier += (1f - baseMultiplier) * 0.5f;

                if (baseMultiplier >= 1f)
                    return 1f;

                // Каждая перевязка приближает множитель к единице.
                // При 3 ступенях и множителе 0: 0 -> 0.33 -> 0.66 -> 1.
                return 1f - ((1f - baseMultiplier) * RemainingFraction);
            }
        }

        /// <summary>Текущая интенсивность эффекта с учётом лечения.</summary>
        public byte GetEffectIntensity(InjuryEffect effect, byte maxIntensity)
        {
            float scaled = effect.Intensity * RemainingFraction;
            int value = (int)Math.Round(scaled, MidpointRounding.AwayFromZero);

            if (value < 1 && scaled > 0f)
                value = 1;

            if (value > maxIntensity)
                value = maxIntensity;

            return (byte)Math.Max(0, value);
        }

        /// <summary>Обрабатывает одну ступень лечения.</summary>
        public void Treat(int steps = 1)
        {
            TreatedSteps = Math.Min(Definition.TreatmentSteps, TreatedSteps + Math.Max(1, steps));
        }

        /// <summary>
        /// Накладывает свежую перевязку. Вызывается при лечении: именно с этого
        /// момента появляется визуал, и заново начинается отсчёт свежести бинта.
        /// </summary>
        public void Dress()
        {
            IsDressed = true;
            DressedAge = 0f;
        }

        /// <summary>Мгновенно вылечивает ранение полностью.</summary>
        public void TreatFully() => TreatedSteps = Definition.TreatmentSteps;

        /// <summary>
        /// Повторное попадание в то же место: ранение открывается заново.
        /// Прогресс лечения, свёртывание и время жизни сбрасываются.
        /// </summary>
        public void Reopen()
        {
            TreatedSteps = 0;
            DamageDealt = 0f;
            DamageBuffer = 0f;
            Age = 0f;

            // Новое попадание срывает бинт: визуал снимается до следующего лечения.
            IsDressed = false;
            DressedAge = 0f;
        }

        /// <summary>Продвигает время жизни ранения и старение перевязки.</summary>
        public void Advance(float deltaSeconds)
        {
            Age += deltaSeconds;

            if (IsDressed)
                DressedAge += deltaSeconds;
        }

        /// <summary>
        /// Накапливает урон за прошедшее время и возвращает целое количество ХП,
        /// которое нужно снять с игрока сейчас.
        /// </summary>
        public int ConsumeDamage(float deltaSeconds)
        {
            float dps = CurrentDamagePerSecond;
            if (dps <= 0f)
                return 0;

            DamageBuffer += dps * deltaSeconds;

            if (DamageBuffer < 1f)
                return 0;

            int damage = (int)Math.Floor(DamageBuffer);
            DamageBuffer -= damage;
            DamageDealt += damage;
            return damage;
        }

        /// <summary>Пора ли ранению зажить самостоятельно.</summary>
        public bool ShouldAutoHeal => Definition.CanAutoHeal && Age >= Definition.AutoHealSeconds;

        public string Describe()
        {
            string part = BodyPart switch
            {
                BodyPart.Head => "голова",
                BodyPart.Torso => "корпус",
                BodyPart.LeftArm => "левая рука",
                BodyPart.RightArm => "правая рука",
                BodyPart.LeftLeg => "левая нога",
                BodyPart.RightLeg => "правая нога",
                _ => "тело",
            };

            return $"{Definition.DisplayName} ({part})";
        }
    }
}
