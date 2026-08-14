using System.Collections.Generic;
using Exiled.API.Features;
using Exiled.API.Features.Items;
using Exiled.API.Features.Pickups;
using MEC;

namespace MainCore.Medical
{
    /// <summary>
    /// Управляет медицинскими предметами: количеством использований и лечением ранений.
    /// Аптечка не исчезает после одного применения - у неё есть заряды (по умолчанию 15),
    /// а одно применение снимает одну ступень самого тяжёлого ранения.
    /// ХП мгновенно не выдаются: они ставятся в очередь и восстанавливаются постепенно.
    /// </summary>
    public static class MedicalItemManager
    {
        /// <summary>
        /// Предохранитель: максимальное время применения предмета. Если игра не пришлёт
        /// событие завершения (предмет отменён, игрок сменил слот), подавление снимется само.
        /// Значение с запасом перекрывает самую долгую анимацию в игре (аптечка ~4.5 сек).
        /// </summary>
        private const float MaxUseSeconds = 10f;

        /// <summary>
        /// Сколько секунд после завершения применения ванильное лечение ещё считается учтённым.
        /// Игра начисляет свой хил не мгновенно, а через несколько кадров после события.
        /// </summary>
        private const float PostUseSuppressSeconds = 1.5f;

        /// <summary>Осталось использований у предмета. Ключ - серийный номер предмета.</summary>
        private static readonly Dictionary<ushort, int> Charges = new();

        /// <summary>
        /// Игроки, у которых ванильное лечение сейчас подавляется, потому что
        /// предмет уже отработан по конфигу. Значение - номер сессии применения:
        /// отложенная очистка сработает только для своей сессии и не снимет
        /// подавление, поставленное более поздним применением.
        /// </summary>
        private static readonly Dictionary<ReferenceHub, int> SuppressedVanillaHeal = new();

        /// <summary>Счётчик сессий применения предметов.</summary>
        private static int useGeneration;

        private static Config Config => MainCorePlugin.Instance.Config;

        public static void Clear()
        {
            Charges.Clear();
            SuppressedVanillaHeal.Clear();
        }

        /// <summary>Является ли предмет медицинским (описан в конфиге).</summary>
        public static bool IsMedicalItem(Item? item) =>
            item is not null && Config.ItemCharges.ContainsKey(item.Type);

        /// <summary>
        /// Сколько использований осталось у предмета.
        /// Данные хранятся только для начатых предметов, поэтому обычная проверка
        /// ничего не записывает в словарь и он не разрастается за раунд.
        /// </summary>
        public static int GetCharges(Item item) =>
            Charges.TryGetValue(item.Serial, out int charges) ? charges : GetMaxCharges(item.Type);

        /// <summary>
        /// Подавляется ли сейчас ванильное лечение этого игрока.
        /// Нужно, чтобы ХП от аптечки не начислились дважды.
        /// </summary>
        public static bool IsVanillaHealSuppressed(Player player) =>
            player is not null && SuppressedVanillaHeal.ContainsKey(player.ReferenceHub);

        /// <summary>
        /// Начало применения предмета. Ванильный хил помечается как учтённый на всё
        /// время анимации, чтобы игра не выдала свои ХП поверх нашего расчёта.
        /// </summary>
        public static void OnUseStarted(Player player) => SuppressVanillaHeal(player, MaxUseSeconds);

        /// <summary>Применение отменено (смена предмета, смерть) - подавление больше не нужно.</summary>
        public static void OnUseCancelled(Player player)
        {
            if (player is not null)
                SuppressedVanillaHeal.Remove(player.ReferenceHub);
        }

        public static void Forget(Player player)
        {
            if (player is not null)
                SuppressedVanillaHeal.Remove(player.ReferenceHub);
        }

        /// <summary>
        /// Обрабатывает завершённое применение медицинского предмета:
        /// расходует заряд, ставит ХП в очередь на постепенное восстановление
        /// и снимает часть ранений.
        /// </summary>
        public static void HandleUsed(Player player, Item item)
        {
            ItemType itemType = item.Type;

            int chargesLeft = GetCharges(item) - 1;

            // Предмет израсходован игрой, его серийный номер больше не актуален.
            Charges.Remove(item.Serial);

            // Ванильный хил уже отменён - выдаём своё, но медленно.
            // Продлеваем подавление: игра начисляет ХП чуть позже этого события.
            SuppressVanillaHeal(player, PostUseSuppressSeconds);

            if (Config.ItemHealPerUse.TryGetValue(itemType, out float heal) && heal > 0f)
                MedicalManager.QueueHeal(player, heal);

            // Искусственные ХП работают как обезболивающее - действуют сразу.
            if (Config.ItemAhpPerUse.TryGetValue(itemType, out float ahp) && ahp > 0f)
                player.ArtificialHealth += ahp;

            // Одно применение снимает часть ранения.
            int steps = Config.ItemTreatmentSteps.TryGetValue(itemType, out int value) ? value : 1;
            MedicalManager.Treat(player, steps);

            // Если заряды остались - возвращаем предмет игроку.
            if (chargesLeft > 0)
                ReturnItem(player, itemType, chargesLeft);
        }

        /// <summary>Убирает данные о предмете (например, при уничтожении).</summary>
        public static void ForgetItem(ushort serial) => Charges.Remove(serial);

        private static int GetMaxCharges(ItemType itemType) =>
            Config.ItemCharges.TryGetValue(itemType, out int max) && max > 0 ? max : 1;

        /// <summary>
        /// Помечает игрока, чтобы ванильное лечение не начислилось поверх нашего значения.
        /// Подавление снимается по таймеру, но только если за это время не началось
        /// новое применение предмета.
        /// </summary>
        private static void SuppressVanillaHeal(Player player, float seconds)
        {
            if (player is null)
                return;

            ReferenceHub hub = player.ReferenceHub;
            int generation = ++useGeneration;

            SuppressedVanillaHeal[hub] = generation;

            Timing.CallDelayed(seconds, () =>
            {
                // Более позднее применение перезаписало сессию - снимать подавление нельзя.
                if (SuppressedVanillaHeal.TryGetValue(hub, out int current) && current == generation)
                    SuppressedVanillaHeal.Remove(hub);
            });
        }

        /// <summary>
        /// Возвращает предмет в инвентарь с сохранением оставшихся зарядов.
        /// Серийный номер меняется, поэтому заряды переносятся на новый предмет.
        /// </summary>
        private static void ReturnItem(Player player, ItemType itemType, int chargesLeft)
        {
            Timing.CallDelayed(0.15f, () =>
            {
                if (player is null || !player.IsAlive)
                    return;

                Item? restored = player.AddItem(itemType);

                if (restored is null)
                {
                    // Инвентарь полон - выбрасываем предмет под ноги.
                    Pickup pickup = Pickup.CreateAndSpawn(itemType, player.Position, player.Rotation, player);
                    Charges[pickup.Serial] = chargesLeft;
                    return;
                }

                Charges[restored.Serial] = chargesLeft;
            });
        }
    }
}
