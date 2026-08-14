using System;
using System.Collections.Generic;
using System.Linq;

namespace MainCore.Medical
{
    /// <summary>
    /// Медицинское состояние игрока: список активных ранений и запас ХП,
    /// которые организм ещё должен восстановить.
    /// </summary>
    public sealed class PlayerMedicalState
    {
        private readonly List<Injury> injuries = new();

        public IReadOnlyList<Injury> Injuries => injuries;

        public bool HasInjuries => injuries.Count > 0;

        /// <summary>
        /// Сколько ХП игроку осталось восстановить. Аптечка не даёт ХП сразу -
        /// она пополняет этот запас, а <see cref="MedicalManager"/> выдаёт его постепенно.
        /// </summary>
        public float PendingHeal { get; private set; }

        /// <summary>Накопленная дробная часть восстановления, чтобы не дёргать ХП мелкими кусками.</summary>
        public float HealBuffer { get; private set; }

        /// <summary>Есть ли ранение, которое активно кровоточит.</summary>
        public bool HasActiveBleeding
        {
            get
            {
                for (int i = 0; i < injuries.Count; i++)
                {
                    if (injuries[i].IsActivelyBleeding)
                        return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Итоговый множитель скорости заживления от всех активных ранений.
        /// Множители перемножаются: два тяжёлых ранения заживают медленнее одного.
        /// </summary>
        public float HealRateMultiplier
        {
            get
            {
                float multiplier = 1f;

                for (int i = 0; i < injuries.Count; i++)
                {
                    Injury injury = injuries[i];

                    if (injury.IsHealed)
                        continue;

                    multiplier *= injury.CurrentHealRateMultiplier;

                    if (multiplier <= 0f)
                        return 0f;
                }

                return multiplier;
            }
        }

        /// <summary>
        /// Множитель скорости заживления с учётом нижнего предела из конфига.
        /// Предел не даёт большому числу ранений полностью остановить восстановление,
        /// но ранения с множителем 0 (артериальное и внутреннее кровотечение)
        /// останавливают заживление полностью - их нужно лечить.
        /// </summary>
        public float GetEffectiveHealRateMultiplier(float minMultiplier)
        {
            float multiplier = HealRateMultiplier;

            // Ноль означает "заживление невозможно" и не поднимается до минимума.
            if (multiplier <= 0f)
                return 0f;

            return Math.Max(multiplier, Math.Min(Math.Max(minMultiplier, 0f), 1f));
        }

        /// <summary>Добавляет ХП в запас на постепенное восстановление.</summary>
        public void QueueHeal(float amount, float maxPending = 0f)
        {
            if (amount <= 0f)
                return;

            PendingHeal += amount;

            if (maxPending > 0f && PendingHeal > maxPending)
                PendingHeal = maxPending;
        }

        /// <summary>Сбрасывает запас восстановления (смерть, респавн).</summary>
        public void ClearHeal()
        {
            PendingHeal = 0f;
            HealBuffer = 0f;
        }

        /// <summary>
        /// Забирает из запаса порцию ХП за прошедшее время.
        /// Скорость подбирается так, чтобы весь запас израсходовался не дольше
        /// <paramref name="maxSeconds"/>, но не медленнее <paramref name="perSecond"/>,
        /// после чего умножается на <paramref name="rateMultiplier"/> от ранений.
        /// </summary>
        public float ConsumeHeal(float deltaSeconds, float perSecond, float maxSeconds, float rateMultiplier = 1f)
        {
            if (PendingHeal <= 0f || deltaSeconds <= 0f || rateMultiplier <= 0f)
                return 0f;

            float rate = Math.Max(0f, perSecond);

            // Если запас большой, ускоряемся, чтобы уложиться в максимальное время.
            if (maxSeconds > 0f)
                rate = Math.Max(rate, PendingHeal / maxSeconds);

            // Ранения замедляют заживление уже после подбора базовой скорости,
            // иначе ускорение по maxSeconds свело бы замедление на нет.
            rate *= rateMultiplier;

            if (rate <= 0f)
                return 0f;

            HealBuffer += Math.Min(PendingHeal, rate * deltaSeconds);

            // Выдаём ХП порциями от 1, иначе клиент не увидит изменения.
            if (HealBuffer < 1f)
                return 0f;

            float granted = Math.Min(PendingHeal, (float)Math.Floor(HealBuffer));
            HealBuffer -= granted;
            PendingHeal -= granted;

            if (PendingHeal < 0.01f)
            {
                PendingHeal = 0f;
                HealBuffer = 0f;
            }

            return granted;
        }

        /// <summary>
        /// Добавляет ранение. Если такое же ранение на этой же части тела уже есть,
        /// оно усугубляется (сброс лечения и свёртывания), а не дублируется.
        /// </summary>
        public Injury Add(InjuryType type, BodyPart bodyPart)
        {
            for (int i = 0; i < injuries.Count; i++)
            {
                Injury candidate = injuries[i];

                if (candidate.Type != type || candidate.BodyPart != bodyPart)
                    continue;

                // Повторное попадание в то же место - ранение открывается заново.
                candidate.Reopen();
                return candidate;
            }

            Injury injury = new(type, bodyPart);
            injuries.Add(injury);
            return injury;
        }

        public bool Has(InjuryType type) => injuries.Any(i => i.Type == type);

        public bool Has(InjuryType type, BodyPart bodyPart) => injuries.Any(i => i.Type == type && i.BodyPart == bodyPart);

        public void Remove(Injury injury) => injuries.Remove(injury);

        public void Clear() => injuries.Clear();

        /// <summary>
        /// Лечит ранения по приоритету тяжести. Возвращает список ранений,
        /// которые получили ступень лечения.
        /// </summary>
        /// <remarks>
        /// Перевязка накладывается здесь же, до удаления зажившего: иначе визуал
        /// бинта не появлялся бы никогда. Большинство ранений лечится одной
        /// ступенью, аптечка даёт ровно одну - ранение сразу становится IsHealed
        /// и вылетало из списка раньше, чем на него успевали наложить бинт.
        /// </remarks>
        public IReadOnlyList<Injury> TreatMostSevere(int steps)
        {
            List<Injury> treated = new();

            for (int i = 0; i < steps; i++)
            {
                Injury? target = FindMostSevere();

                if (target is null)
                    break;

                target.Treat();
                target.Dress();

                if (!treated.Contains(target))
                    treated.Add(target);
            }

            // Полностью залеченное ранение больше не кровоточит и не даёт эффектов,
            // но бинт на модели остаётся: его снимет DressingLingerSeconds в тике.
            injuries.RemoveAll(x => x.IsHealed && !x.KeepForDressing);

            return treated;
        }


        /// <summary>
        /// Находит самое тяжёлое незалеченное ранение одним проходом,
        /// без сортировки и промежуточных коллекций.
        /// </summary>
        private Injury? FindMostSevere()
        {
            Injury? best = null;
            int bestSeverity = int.MinValue;
            float bestDamage = float.MinValue;

            for (int i = 0; i < injuries.Count; i++)
            {
                Injury injury = injuries[i];

                if (injury.IsHealed)
                    continue;

                int severity = Severity(injury.Type);
                float damage = injury.CurrentDamagePerSecond;

                if (best is not null && (severity < bestSeverity || (severity == bestSeverity && damage <= bestDamage)))
                    continue;

                best = injury;
                bestSeverity = severity;
                bestDamage = damage;
            }

            return best;
        }

        /// <summary>Приоритет лечения: чем больше, тем опаснее ранение.</summary>
        public static int Severity(InjuryType type) => type switch
        {
            InjuryType.ArterialBleeding => 100,
            InjuryType.InternalBleeding => 90,
            InjuryType.VenousBleeding => 80,
            InjuryType.Fracture => 60,
            InjuryType.Burn => 50,
            InjuryType.CapillaryBleeding => 40,
            InjuryType.Contusion => 20,
            InjuryType.LightWound => 10,
            _ => 0,
        };

        /// <summary>Самое тяжёлое из активных ранений (для отладки и диагностики медиком).</summary>
        public Injury? MostSevere => FindMostSevere();

        /// <summary>Суммарный урон в секунду от всех ранений.</summary>
        public float TotalDamagePerSecond
        {
            get
            {
                float total = 0f;

                for (int i = 0; i < injuries.Count; i++)
                    total += injuries[i].CurrentDamagePerSecond;

                return total;
            }
        }

        public string DescribeAll()
        {
            if (!HasInjuries)
                return "Ранений нет.";

            return string.Join(", ", injuries
                .OrderByDescending(x => Severity(x.Type))
                .Select(x => x.Describe()));
        }
    }
}
