using Exiled.API.Features;
using Exiled.API.Features.Attributes;
using Exiled.API.Features.Items;
using Exiled.API.Features.Spawn;
using Exiled.CustomItems.API.Features;
using Exiled.Events.EventArgs.Player;
using MainCore.Drone;
using MEC;

namespace MainCore.CustomItems
{
    /// <summary>
    /// Пульт управления FPV-дроном - рация. Выдаётся командой <c>ci give 3</c>.
    /// </summary>
    /// <remarks>
    /// Порядок работы: рация в руках - перед игроком появляется схематик дрона и
    /// следует за взглядом; нажатие на рации ставит дрон и пересаживает игрока в него.
    ///
    /// Установка и возврат управления обрабатываются в одном месте -
    /// <c>EventHandlers.OnTogglingRadio</c>. Разносить их по двум подписчикам нельзя:
    /// установка сама делает игрока пилотом, и второй обработчик в том же событии
    /// увидел бы уже летящего пилота и мгновенно вернул бы управление назад.
    /// </remarks>
    [CustomItem(ItemType.Radio)]
    public sealed class DroneController : CustomItem
    {
        /// <summary>Идентификатор пульта: им пользуется команда <c>ci give 3</c>.</summary>
        public const uint ControllerId = 3;

        public override uint Id { get; set; } = ControllerId;

        public override string Name { get; set; } = "FPV Drone";

        public override string Description { get; set; } = "Drone remote. Hold it to place the drone, press the radio key to fly.";

        public override ItemType Type { get; set; } = ItemType.Radio;

        public override float Weight { get; set; } = 1f;

        // The base declares this as nullable; matching the signature avoids CS8765.
        // The remote is only handed out by commands, so no spawn locations are defined.
        public override SpawnProperties? SpawnProperties { get; set; } = new SpawnProperties();

        /// <summary>Хинты кастомных предметов отключены по всему плагину.</summary>
        protected override void ShowPickedUpMessage(Player player)
        {
        }

        /// <inheritdoc cref="ShowPickedUpMessage"/>
        protected override void ShowSelectedMessage(Player player)
        {
        }

        protected override void OnAcquired(Player player, Item item, bool displayMessage)
        {
            // displayMessage is forced off: no pickup hint for a custom item.
            base.OnAcquired(player, item, false);
        }

        protected override void SubscribeEvents()
        {
            Exiled.Events.Handlers.Player.ChangingItem += OnChangingItem;
            base.SubscribeEvents();
        }

        protected override void UnsubscribeEvents()
        {
            Exiled.Events.Handlers.Player.ChangingItem -= OnChangingItem;
            base.UnsubscribeEvents();
        }

        /// <summary>
        /// Показывает или убирает превью дрона при смене предмета в руках.
        /// </summary>
        /// <remarks>
        /// Событие приходит и когда рацию берут, и когда убирают, поэтому обе ветки
        /// обрабатываются здесь: иначе схематик остался бы висеть в воздухе после
        /// переключения на другой предмет.
        ///
        /// ChangingItem иногда приходит с промежуточным null/нераспознанным предметом
        /// (кадр между слотами), и наивная ветка else немедленно уничтожала бы только
        /// что показанное превью. Поэтому отмену откладываем и проверяем РЕАЛЬНО
        /// выбранный предмет, а не промежуточный <c>ev.Item</c>.
        /// </remarks>
        private void OnChangingItem(ChangingItemEventArgs ev)
        {
            Player player = ev.Player;
            if (player is null || DroneManager.IsPiloting(player))
                return;

            if (ev.Item is not null && Check(ev.Item))
            {
                DroneManager.ShowPreview(player);
                return;
            }

            // Событие иногда приходит с промежуточным null/неактуальным предметом.
            Timing.CallDelayed(0.15f, () =>
            {
                Item current = player.CurrentItem;

                // Рация всё ещё реально находится в руках - превью не трогаем.
                if (current is not null && Check(current))
                    return;

                DroneManager.CancelPreview(player);
            });
        }
    }
}