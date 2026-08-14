using System;
using System.Collections.Generic;
using Exiled.API.Enums;
using Exiled.API.Features;
// using MainCore.Medical.Visuals; // ВРЕМЕННО: система видимых ранений отключена
using MEC;

namespace MainCore.Medical
{
    /// <summary>
    /// Ядро медицинской системы: хранит ранения игроков, тикает урон, поддерживает эффекты
    /// и постепенно восстанавливает ХП. Худа нет - только эффекты, урон и визуал на модели.
    /// </summary>
    public static class MedicalManager
    {
        /// <summary>Интервал тика системы в секундах.</summary>
        public const float TickInterval = 1f;

        /// <summary>
        /// Состояния игроков. Ключ - <see cref="ReferenceHub"/>, а не <see cref="Player"/>:
        /// обёртка Player может быть пересоздана, а хаб живёт всю сессию игрока.
        /// </summary>
        private static readonly Dictionary<ReferenceHub, PlayerMedicalState> States = new();

        /// <summary>Буфер обхода игроков в тике - не создаём список каждую секунду.</summary>
        private static readonly List<ReferenceHub> TickBuffer = new();

        /// <summary>Буфер ранений, снятых за тик.</summary>
        private static readonly List<Injury> RemovedBuffer = new();

        private static CoroutineHandle tickCoroutine;

        /// <summary>
        /// Взведён, пока система сама восстанавливает ХП. Нужен, чтобы не отменить
        /// собственное лечение в обработчике <see cref="OnHealing"/>.
        /// </summary>
        private static bool applyingOwnHeal;

        private static Config Config => MainCorePlugin.Instance.Config;

        /// <summary>
        /// Запускает систему. Повторный вызов безопасен: старая корутина снимается,
        /// поэтому двойного тика при включении плагина и старте раунда не возникает.
        /// </summary>
        public static void Start()
        {
            States.Clear();

            Config.Normalize();
            InjuryResolver.ReloadConfigCache();

            if (tickCoroutine.IsRunning)
                Timing.KillCoroutines(tickCoroutine);

            tickCoroutine = Timing.RunCoroutine(TickLoop(), "MainCore.Medical.Tick");
        }

        public static void Stop()
        {
            if (tickCoroutine.IsRunning)
                Timing.KillCoroutines(tickCoroutine);

            States.Clear();
        }

        /// <summary>Возвращает состояние игрока, создавая его при необходимости.</summary>
        public static PlayerMedicalState GetState(Player player)
        {
            ReferenceHub hub = player.ReferenceHub;

            if (!States.TryGetValue(hub, out PlayerMedicalState? state) || state is null)
            {
                state = new PlayerMedicalState();
                States[hub] = state;
            }

            return state;
        }

        public static bool TryGetState(Player player, out PlayerMedicalState? state)
        {
            if (player is null)
            {
                state = null;
                return false;
            }

            return States.TryGetValue(player.ReferenceHub, out state);
        }

        /// <summary>Полностью очищает ранения и запас лечения игрока (смерть, респавн).</summary>
        public static void ResetPlayer(Player player)
        {
            if (player is null)
                return;

            // // Визуал снимаем всегда: модель могла быть пересоздана сменой роли.
            // WoundVisualManager.RemoveAll(player); // ВРЕМЕННО: визуал ранений отключён

            if (!States.TryGetValue(player.ReferenceHub, out PlayerMedicalState? state) || state is null)
                return;

            // Сначала снимаем эффекты, пока ранения ещё в состоянии: так проверка
            // «нужен ли эффект другому ранению» видит полную картину.
            DisableAllEffects(player, state);

            state.Clear();
            state.ClearHeal();
        }

        public static void RemovePlayer(Player player)
        {
            if (player is null)
                return;

            // WoundVisualManager.RemoveAll(player); // ВРЕМЕННО: визуал ранений отключён
            States.Remove(player.ReferenceHub);
        }

        /// <summary>
        /// Повторно показывает визуал после респавна.
        /// </summary>
        /// <remarks>
        /// В кадре события Spawned модель игрока ещё не собрана: у неё нет
        /// гуманоидного Animator, поэтому BoneResolver не нашёл бы ни одной кости.
        /// Ждём <see cref="Config.WoundVisualSpawnDelay"/> и синхронизируем заново.
        /// Ранения при респавне обнуляются, поэтому обычно работы нет - вызов важен
        /// для случаев, когда ранение выдано сразу после спавна (например командой).
        /// </remarks>
        public static void ResyncVisualsAfterSpawn(Player player)
        {
            if (player is null)
                return;

            float delay = Math.Max(0f, Config.WoundVisualSpawnDelay);

            Timing.CallDelayed(delay, () =>
            {
                try
                {
                    if (player is null || !player.IsConnected || !player.IsAlive)
                        return;

                    if (!TryGetState(player, out PlayerMedicalState? state) || state is null || !state.HasInjuries)
                        return;

                    // WoundVisualManager.Sync(player, state); // ВРЕМЕННО: визуал ранений отключён
                }
                catch (Exception exception)
                {
                    // Только ASCII: консоль сервера выводит не-ASCII как '?'.
                    Log.Error($"[Medical] Failed to show visuals after respawn: {exception}");
                }
            });
        }

        /// <summary>Наносит игроку ранение, выдаёт эффекты и показывает перевязку.</summary>
        public static Injury? Inflict(Player player, InjuryType type, BodyPart bodyPart)
        {
            if (player is null || !player.IsAlive)
                return null;

            PlayerMedicalState state = GetState(player);
            Injury injury = state.Add(type, bodyPart);

            ApplyEffects(player, injury);
            // WoundVisualManager.Sync(player, state); // ВРЕМЕННО: визуал ранений отключён

            // Лог сервера - только ASCII, поэтому здесь имена enum, а не Describe().
            if (Config.Debug)
                Log.Debug($"[Medical] {player.Nickname} got {type} on {bodyPart}");

            return injury;
        }

        /// <summary>
        /// Накладывает перевязку на конкретное ранение, не расходуя ступени лечения.
        /// Нужна отладочной команде: визуал появляется только на перевязанной ране,
        /// поэтому иначе его нельзя проверить без аптечки.
        /// </summary>
        public static void DressInjury(Player player, Injury injury)
        {
            if (player is null || injury is null)
                return;

            injury.Dress();

            // ВРЕМЕННО: визуал ранений отключён
            // if (TryGetState(player, out PlayerMedicalState? state) && state is not null)
            //     WoundVisualManager.Sync(player, state);
        }

        /// <summary>
        /// Обрабатывает лечение: снимает указанное количество ступеней с самых тяжёлых ранений.
        /// Возвращает true, если что-то было вылечено.
        /// </summary>
        public static bool Treat(Player player, int steps)
        {
            if (player is null
                || !States.TryGetValue(player.ReferenceHub, out PlayerMedicalState? state)
                || state is null
                || !state.HasInjuries)
            {
                return false;
            }

            // Запоминаем ранения до лечения: TreatMostSevere удаляет вылеченные,
            // а нам нужно снять с них эффекты.
            RemovedBuffer.Clear();
            for (int i = 0; i < state.Injuries.Count; i++)
                RemovedBuffer.Add(state.Injuries[i]);

            IReadOnlyList<Injury> treated = state.TreatMostSevere(steps);

            if (treated.Count == 0)
            {
                RemovedBuffer.Clear();
                return false;
            }

            // Полностью вылеченные ранения должны потерять свои эффекты.
            for (int i = 0; i < RemovedBuffer.Count; i++)
            {
                Injury injury = RemovedBuffer[i];

                if (injury.IsHealed)
                    ClearEffects(player, state, injury);
            }

            RemovedBuffer.Clear();

            // У недолеченных ранений интенсивность эффектов уменьшается.
            for (int i = 0; i < state.Injuries.Count; i++)
                ApplyEffects(player, state.Injuries[i], refreshOneShot: false);

            // ВРЕМЕННО: визуал ранений отключён
            // // Перевязку накладывает сам TreatMostSevere - до удаления заживших ранений,
            // // иначе бинт не успевал бы появиться. Здесь остаётся обновить визуал.
            // WoundVisualManager.Sync(player, state);

            return true;
        }

        /// <summary>
        /// Ставит ХП в очередь на постепенное восстановление.
        /// Мгновенно ХП не выдаются никогда - организм восстанавливается сам.
        /// </summary>
        public static void QueueHeal(Player player, float amount)
        {
            if (player is null || !player.IsAlive || amount <= 0f)
                return;

            GetState(player).QueueHeal(amount, Config.MaxPendingHeal);

            if (Config.Debug)
                Log.Debug($"[Medical] {player.Nickname}: queued {amount:0.#} HP for regeneration.");
        }

        /// <summary>
        /// Перехват любого лечения. Ванильные предметы лечат слишком много и слишком быстро,
        /// поэтому их лечение отменяется, а ХП уходят в очередь постепенного восстановления.
        /// </summary>
        public static void OnHealing(Player player, ref bool isAllowed, ref float amount)
        {
            // Собственное восстановление пропускаем без изменений.
            if (applyingOwnHeal || !Config.OverrideVanillaHealing || player is null)
                return;

            isAllowed = false;

            // Лечение от медицинского предмета уже посчитано по конфигу - его не дублируем.
            if (MedicalItemManager.IsVanillaHealSuppressed(player))
                return;

            // Прочие источники (админ-команды, конфеты) не пропадают, а работают медленно.
            QueueHeal(player, amount);
        }

        private static IEnumerator<float> TickLoop()
        {
            while (true)
            {
                yield return Timing.WaitForSeconds(TickInterval);

                try
                {
                    Tick();
                }
                catch (Exception exception)
                {
                    Log.Error($"[Medical] Tick failed: {exception}");
                }
            }
        }

        private static void Tick()
        {
            if (States.Count == 0)
                return;

            // Копируем ключи в переиспользуемый буфер: словарь меняется во время обхода.
            TickBuffer.Clear();
            foreach (ReferenceHub hub in States.Keys)
                TickBuffer.Add(hub);

            for (int i = 0; i < TickBuffer.Count; i++)
            {
                ReferenceHub hub = TickBuffer[i];

                if (!States.TryGetValue(hub, out PlayerMedicalState? state) || state is null)
                    continue;

                Player? player = Player.Get(hub);

                if (player is null || !player.IsConnected)
                {
                    States.Remove(hub);
                    continue;
                }

                if (!player.IsAlive)
                {
                    // WoundVisualManager.RemoveAll(player); // ВРЕМЕННО: визуал ранений отключён
                    state.Clear();
                    state.ClearHeal();
                    continue;
                }

                TickInjuries(player, state);
                TickHealing(player, state);
            }

            TickBuffer.Clear();
        }

        /// <summary>Продвигает ранения: урон, автозаживление и продление эффектов.</summary>
        private static void TickInjuries(Player player, PlayerMedicalState state)
        {
            float totalDamage = 0f;
            // bool visualsDirty = false; // ВРЕМЕННО: визуал ранений отключён

            RemovedBuffer.Clear();

            // Обход с конца: ранения могут удаляться по автозаживлению.
            for (int i = state.Injuries.Count - 1; i >= 0; i--)
            {
                Injury injury = state.Injuries[i];

                // ВРЕМЕННО: визуал ранений отключён
                // bool wasBleeding = injury.IsActivelyBleeding;
                // bool wasClotted = injury.IsClotted;
                //
                // // Вид перевязки зависит от её свежести, поэтому запоминаем состояние
                // // до тика: когда бинт пачкается, визуал нужно подменить.
                // DressingState wasDressingState = Visuals.WoundVisualCatalog.GetState(injury);

                injury.Advance(TickInterval);

                // Залеченное ранение остаётся в списке только ради бинта.
                // Время ношения вышло - убираем и ранение, и визуал.
                if (injury.IsHealed && !injury.KeepForDressing)
                {
                    state.Remove(injury);
                    RemovedBuffer.Add(injury);
                    // visualsDirty = true; // ВРЕМЕННО: визуал ранений отключён
                    continue;
                }

                if (injury.ShouldAutoHeal)
                {
                    injury.TreatFully();
                    state.Remove(injury);
                    RemovedBuffer.Add(injury);
                    // visualsDirty = true; // ВРЕМЕННО: визуал ранений отключён
                    continue;
                }

                totalDamage += injury.ConsumeDamage(TickInterval);

                // ВРЕМЕННО: визуал ранений отключён
                // // Бинт испачкался или кровь свернулась - визуал надо обновить.
                // if (wasBleeding != injury.IsActivelyBleeding
                //     || wasClotted != injury.IsClotted
                //     || wasDressingState != Visuals.WoundVisualCatalog.GetState(injury))
                // {
                //     visualsDirty = true;
                // }

                // Постоянные эффекты нужно продлевать, иначе они истекут.
                ApplyEffects(player, injury, refreshOneShot: false);
            }

            // Эффекты зажившего ранения снимаем после удаления из состояния,
            // чтобы проверка не считала его активным потребителем эффекта.
            for (int i = 0; i < RemovedBuffer.Count; i++)
                ClearEffects(player, state, RemovedBuffer[i]);

            RemovedBuffer.Clear();

            if (totalDamage > 0f)
                ApplyBleedingDamage(player, totalDamage);

            // ВРЕМЕННО: визуал ранений отключён
            // if (visualsDirty)
            //     WoundVisualManager.Sync(player, state);
        }

        /// <summary>Постепенное восстановление ХП из очереди лечения.</summary>
        private static void TickHealing(Player player, PlayerMedicalState state)
        {
            if (state.PendingHeal <= 0f)
                return;

            // Пока кровь не остановлена, организм не восстанавливается.
            if (Config.StopHealingWhileBleeding && state.HasActiveBleeding)
                return;

            if (player.Health >= player.MaxHealth)
                return;

            // Ранения замедляют заживление; при выключенной опции скорость базовая.
            float rateMultiplier = Config.UseInjuryHealRates
                ? state.GetEffectiveHealRateMultiplier(Config.MinHealRateMultiplier)
                : 1f;

            if (rateMultiplier <= 0f)
                return;

            float granted = state.ConsumeHeal(TickInterval, Config.HealPerSecond, Config.MaxHealSeconds, rateMultiplier);
            if (granted <= 0f)
                return;

            float target = Math.Min(player.MaxHealth, player.Health + granted);

            applyingOwnHeal = true;
            try
            {
                player.Health = target;
            }
            finally
            {
                applyingOwnHeal = false;
            }
        }

        private static void ApplyBleedingDamage(Player player, float damage)
        {
            // Кровопотеря не должна порождать новые ранения - тип урона Bleeding в игнор-листе.
            player.Hurt(damage, DamageType.Bleeding);
        }

        /// <summary>Выдаёт или продлевает эффекты ранения с учётом степени лечения.</summary>
        private static void ApplyEffects(Player player, Injury injury, bool refreshOneShot = true)
        {
            IReadOnlyList<InjuryEffect> effects = injury.Definition.Effects;

            for (int i = 0; i < effects.Count; i++)
            {
                InjuryEffect effect = effects[i];

                if (!effect.TryResolve(out EffectType effectType))
                {
                    if (Config.Debug)
                        Log.Debug($"[Medical] Effect '{effect.EffectName}' does not exist in this game version.");

                    continue;
                }

                byte intensity = injury.GetEffectIntensity(effect, Config.MaxEffectIntensity);

                if (intensity == 0)
                {
                    // Эффект может быть нужен другому ранению - снимаем только лишний.
                    if (!IsEffectUsedByOther(player, effectType, injury))
                        player.DisableEffect(effectType);

                    continue;
                }

                if (effect.IsOneShot)
                {
                    if (refreshOneShot)
                        player.EnableEffect(effectType, intensity, effect.Duration);

                    continue;
                }

                // Постоянный эффект: держим его чуть дольше тика, чтобы не мигал.
                player.EnableEffect(effectType, intensity, Config.EffectRefreshDuration);
            }
        }

        /// <summary>
        /// Снимает эффекты ранения, если ни одно другое активное ранение их не использует.
        /// Ранение должно быть уже удалено из состояния игрока.
        /// </summary>
        private static void ClearEffects(Player player, PlayerMedicalState state, Injury injury)
        {
            IReadOnlyList<InjuryEffect> effects = injury.Definition.Effects;

            for (int i = 0; i < effects.Count; i++)
            {
                if (!effects[i].TryResolve(out EffectType effectType))
                    continue;

                if (!IsEffectUsedByOther(state, effectType, injury))
                    player.DisableEffect(effectType);
            }
        }

        /// <summary>Снимает все эффекты всех ранений игрока (смерть, респавн).</summary>
        private static void DisableAllEffects(Player player, PlayerMedicalState state)
        {
            for (int i = 0; i < state.Injuries.Count; i++)
            {
                IReadOnlyList<InjuryEffect> effects = state.Injuries[i].Definition.Effects;

                for (int j = 0; j < effects.Count; j++)
                {
                    if (effects[j].TryResolve(out EffectType effectType))
                        player.DisableEffect(effectType);
                }
            }
        }

        private static bool IsEffectUsedByOther(Player player, EffectType effectType, Injury exclude) =>
            TryGetState(player, out PlayerMedicalState? state) && state is not null
            && IsEffectUsedByOther(state, effectType, exclude);

        /// <summary>
        /// Использует ли эффект какое-то другое незалеченное ранение игрока.
        /// Учитывается только ненулевая интенсивность: ранение, у которого эффект
        /// уже угас до нуля, держать его не должно.
        /// </summary>
        private static bool IsEffectUsedByOther(PlayerMedicalState state, EffectType effectType, Injury exclude)
        {
            for (int i = 0; i < state.Injuries.Count; i++)
            {
                Injury other = state.Injuries[i];

                if (other == exclude || other.IsHealed)
                    continue;

                IReadOnlyList<InjuryEffect> effects = other.Definition.Effects;

                for (int j = 0; j < effects.Count; j++)
                {
                    InjuryEffect effect = effects[j];

                    if (!effect.TryResolve(out EffectType otherType) || otherType != effectType)
                        continue;

                    if (other.GetEffectIntensity(effect, Config.MaxEffectIntensity) > 0)
                        return true;
                }
            }

            return false;
        }
    }
}
