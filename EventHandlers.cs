using System.Collections.Generic;
using Exiled.API.Enums;
using Exiled.Events.EventArgs.Map;
using Exiled.Events.EventArgs.Player;
using Exiled.Events.EventArgs.Server;
using MainCore.CustomItems;
using MainCore.Destruction;
using MainCore.Drone;
using MainCore.Medical;
using UnityEngine;

// using MainCore.Medical.Visuals; // ВРЕМЕННО: система видимых ранений отключена

namespace MainCore
{
    /// <summary>
    /// Подписки на события сервера. Логики здесь минимум - только маршрутизация в системы.
    /// </summary>
    public sealed class EventHandlers
    {
        /// <summary>
        /// Момент последнего действия кнопки дрона по игроку. Стадии переключает
        /// только <see cref="OnChangingRadioPreset"/>; дедупликация - страховка от
        /// двойного события за один клик при лагах.
        /// </summary>
        private static readonly Dictionary<string, float> radioCooldown = new Dictionary<string, float>();

        /// <summary>Минимальный интервал между реакциями на кнопку рации, секунды.</summary>
        private const float RadioCooldownSeconds = 0.5f;

        public void OnWaitingForPlayers()
        {
            MedicalManager.Start();
            MedicalItemManager.Clear();
            // WoundVisualManager.Clear(); // ВРЕМЕННО: визуал ранений отключён
            DestructionManager.Start();
            PrimitiveCuller.Start();
            DroneManager.Start();

            // Серийные номера предметов выдаются заново каждый раунд: старые
            // записи об РПГ могли бы совпасть с новым предметом и он оказался бы
            // "уже заряжен" без патрона.
            RpgLauncher.Clear();
        }

        public void OnRestartingRound()
        {
            MedicalManager.Stop();
            MedicalItemManager.Clear();
            // WoundVisualManager.Clear(); // ВРЕМЕННО: визуал ранений отключён
            DestructionManager.Stop();
            PrimitiveCuller.Stop();
            DroneManager.Stop();
            RpgLauncher.Clear();
        }

        // ---------------------------------------------------------------- FPV drone

        /// <summary>
        /// Прыжок разгоняет дрон. Сам прыжок при этом отменяется: пилот в воздухе,
        /// прыгать ему нечем, а клавиша занята под тягу.
        /// </summary>
        public void OnJumping(JumpingEventArgs ev)
        {
            if (ev.Player is null || !DroneManager.IsPiloting(ev.Player))
                return;

            ev.IsAllowed = false;
            DroneManager.Accelerate(ev.Player);
        }

        /// <summary>
        /// Alt (в SL это переключение noclip) тормозит дрон.
        /// </summary>
        /// <remarks>
        /// Пилот летает именно за счёт noclip, поэтому переключение обязательно
        /// отменяется: иначе первая же попытка сбросить скорость выбросила бы дрон
        /// из полёта и уронила игрока на землю.
        /// </remarks>
        public void OnTogglingNoClip(TogglingNoClipEventArgs ev)
        {
            if (ev.Player is null || !DroneManager.IsPiloting(ev.Player))
                return;

            ev.IsAllowed = false;
            DroneManager.Decelerate(ev.Player);
        }

        /// <summary>
        /// Бросок гранаты в полёте превращается в сброс из-под дрона.
        /// </summary>
        /// <remarks>
        /// Ванильный бросок всегда отменяется: граната ушла бы от камеры пилота,
        /// уменьшенного до 0.1, в непредсказуемую сторону. Вместо этого граната
        /// создаётся под дроном и получает его скорость.
        /// </remarks>
        public void OnThrowingRequest(ThrowingRequestEventArgs ev)
        {
            if (ev.Player is null || !DroneManager.IsPiloting(ev.Player))
                return;

            // ThrowingRequestEventArgs не имеет IsAllowed: бросок отменяется
            // подменой типа запроса - тем же приёмом, что и у снаряда РПГ.
            bool isFirstPhase = ev.RequestType == ThrowRequest.BeginThrow;
            ev.RequestType = ThrowRequest.CancelThrow;

            // Одно нажатие даёт несколько запросов; сбрасываем только на первом,
            // иначе за одно нажатие ушли бы две гранаты.
            if (!isFirstPhase)
                return;

            if (DroneManager.DropGrenade(ev.Player) && ev.Item is not null)
                ev.Player.RemoveItem(ev.Item);
        }

        /// <summary>
        /// ЛКМ по рации приходит как TogglingRadio - именно оно переключает стадии
        /// дрона: Preview -> Placed -> Piloting -> Placed. Стадию двигаем только здесь,
        /// а смену волны (<see cref="OnChangingRadioPreset"/>) лишь глушим, чтобы не
        /// проскочить две стадии за один клик.
        /// </summary>
        public void OnTogglingRadio(TogglingRadioEventArgs ev)
        {
            if (ev.Player is null)
                return;

            // Именно ЛКМ переключает стадии дрона.
            if (HandleRadioKey(ev.Player))
                ev.IsAllowed = false;
        }

        /// <summary>
        /// Смена волны/дальности рации. Стадию дрона здесь НЕ трогаем (это делает
        /// <see cref="OnTogglingRadio"/> по ЛКМ), только блокируем ванильную смену
        /// пресета у рации, связанной с дроном.
        /// </summary>
        public void OnChangingRadioPreset(ChangingRadioPresetEventArgs ev)
        {
            if (ev.Player is null)
                return;

            if (DroneManager.IsPiloting(ev.Player) ||
                DroneManager.HasPlaced(ev.Player) ||
                DroneManager.HasPreview(ev.Player))
            {
                ev.IsAllowed = false;
            }
        }

        /// <summary>
        /// Действие кнопки по стадиям дрона:
        /// Preview -> Placed (поставить), Placed -> Piloting (войти),
        /// Piloting -> Placed (выйти). Возвращает <c>true</c>, если событие отменить.
        /// </summary>
        private static bool HandleRadioKey(Exiled.API.Features.Player player)
        {
            string id = player.UserId ?? player.Nickname ?? string.Empty;
            float now = Time.realtimeSinceStartup;
            if (radioCooldown.TryGetValue(id, out float last) && now - last < RadioCooldownSeconds)
                return true; // дубль события за один клик - гасим, но отменяем

            if (DroneManager.IsPiloting(player))
            {
                radioCooldown[id] = now;
                DroneManager.ReturnControl(player, "radio");
                return true;
            }

            if (DroneManager.HasPlaced(player))
            {
                radioCooldown[id] = now;
                DroneManager.Deploy(player);
                return true;
            }

            if (DroneManager.HasPreview(player))
            {
                radioCooldown[id] = now;
                DroneManager.Place(player);
                return true;
            }

            return false;
        }

        /// <summary>Выстрел игрока: возможно, попал по дрону (у схематика нет коллайдеров).</summary>
        public void OnShot(ShotEventArgs ev)
        {
            if (ev.Player is null)
                return;

            DroneManager.HandleShot(ev.Player, 15f);
        }

        /// <summary>Урон превращается в ранения.</summary>
        public void OnHurting(HurtingEventArgs ev)
        {
            if (!ev.IsAllowed)
                return;

            InjuryResolver.Resolve(ev);
            DestructionManager.OnHurting(ev);

            // Пилота выбивает из дрона только реальной атакой по телу. Кровотечение,
            // яд и прочий периодический урон игнорируем: иначе раненый пилот вылетал бы
            // из дрона каждую секунду от тика собственной медсистемы.
            if (ev.Player is not null && DroneManager.IsPiloting(ev.Player) && IsRealAttack(ev.DamageHandler.Type))
                DroneManager.ReturnControl(ev.Player, "pilot hurt");
        }

        /// <summary>Реальная ли это атака по телу (а не кровотечение/яд/окружение).</summary>
        private static bool IsRealAttack(DamageType type)
        {
            switch (type)
            {
                case DamageType.Bleeding:
                case DamageType.Poison:
                case DamageType.Asphyxiation:
                case DamageType.Decontamination:
                case DamageType.Scp207:
                case DamageType.Hypothermia:
                case DamageType.CardiacArrest:
                case DamageType.Custom:
                case DamageType.Unknown:
                    return false;
                default:
                    return true;
            }
        }

        public void OnChangedIntoGrenade(ChangedIntoGrenadeEventArgs ev)
        {
            DestructionManager.OnChangedIntoGrenade(ev);
        }

        /// <summary>
        /// Любое лечение перехватывается: мгновенных ХП в системе нет,
        /// всё уходит в очередь постепенного восстановления.
        /// </summary>
        public void OnHealing(HealingEventArgs ev)
        {
            if (ev.Player is null)
                return;

            bool isAllowed = ev.IsAllowed;
            float amount = ev.Amount;

            MedicalManager.OnHealing(ev.Player, ref isAllowed, ref amount);

            ev.IsAllowed = isAllowed;
            ev.Amount = amount;
        }

        public void OnDied(DiedEventArgs ev)
        {
            if (ev.Player is null)
                return;

            MedicalManager.ResetPlayer(ev.Player);

            // Смерть прерывает применение предмета - подавление ванильного хила больше не нужно.
            MedicalItemManager.OnUseCancelled(ev.Player);

            // Мёртвый пилот не должен остаться уменьшенным и в noclip после респавна.
            DroneManager.ReturnControl(ev.Player, "pilot died");
        }

        /// <summary>
        /// Смена роли создаёт новую модель игрока. Старые визуалы висели на костях
        /// прежнего скелета, поэтому их нужно снять, а состояние обнулить.
        /// </summary>
        public void OnSpawned(SpawnedEventArgs ev)
        {
            if (ev.Player is null)
                return;

            MedicalManager.ResetPlayer(ev.Player);
            // ВРЕМЕННО: визуал ранений отключён
            // MedicalManager.ResyncVisualsAfterSpawn(ev.Player);
        }

        public void OnLeft(LeftEventArgs ev)
        {
            if (ev.Player is null)
                return;

            MedicalManager.RemovePlayer(ev.Player);
            MedicalItemManager.Forget(ev.Player);

            // Игрока больше нет: держать его дрон и даммика не за кем.
            DroneManager.Remove(ev.Player, "player left");
        }

        /// <summary>
        /// Смена роли: визуал снимаем сразу. Скелет новой модели создаётся не мгновенно,
        /// поэтому повторный показ произойдёт при следующем ранении или тике.
        /// </summary>
        public void OnChangingRole(ChangingRoleEventArgs ev)
        {
            // Смена роли (в т.ч. RA без смерти) обязана вернуть пилоту управление:
            // иначе он останется в noclip и невидимым в новой роли.
            if (ev.Player is not null)
                DroneManager.ReturnControl(ev.Player, "role changed");

            // ВРЕМЕННО: визуал ранений отключён
            // if (ev.Player is not null)
            //     WoundVisualManager.RemoveAll(ev.Player);
        }

        /// <summary>Начало применения медицинского предмета: гасим ванильный хил.</summary>
        public void OnUsingItem(UsingItemEventArgs ev)
        {
            if (!ev.IsAllowed || ev.Player is null)
                return;

            if (!MedicalItemManager.IsMedicalItem(ev.Item))
                return;

            MedicalItemManager.OnUseStarted(ev.Player);
        }

        /// <summary>
        /// Применение прервано (смена слота, отмена). Снимаем подавление, иначе игрок
        /// не смог бы получить лечение из других источников до истечения таймера.
        /// </summary>
        public void OnCancellingItemUse(CancellingItemUseEventArgs ev)
        {
            if (ev.Player is not null)
                MedicalItemManager.OnUseCancelled(ev.Player);
        }

        /// <summary>Применение аптечки: расход заряда и лечение ранений.</summary>
        public void OnUsedItem(UsedItemEventArgs ev)
        {
            if (ev.Player is null || ev.Item is null)
                return;

            if (!MedicalItemManager.IsMedicalItem(ev.Item))
                return;

            MedicalItemManager.HandleUsed(ev.Player, ev.Item);
        }

        /// <summary>
        /// Предмет уничтожен как объект инвентаря - его заряды больше не нужны.
        /// Без этого словарь зарядов рос бы до конца раунда.
        /// </summary>
        public void OnItemRemoved(ItemRemovedEventArgs ev)
        {
            // Если предмет превратился в пикап, заряды остаются привязаны к серийному
            // номеру пикапа - он совпадает с номером предмета, поэтому чистить нечего.
            if (ev.Pickup is not null || ev.Item is null)
                return;

            MedicalItemManager.ForgetItem(ev.Item.Serial);
        }
    }
}