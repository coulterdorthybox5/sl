using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.API.Features.Attributes;
using Exiled.API.Features.Items;
using Exiled.API.Features.Spawn;
using Exiled.CustomItems.API.Features;
using Exiled.Events.EventArgs.Player;

namespace MainCore.CustomItems
{
    /// <summary>
    /// RPG round: the ammunition for <see cref="RpgLauncher"/>.
    /// </summary>
    /// <remarks>
    /// The normal way to spend a round is pressing the reload key with the RPG-7 in hand -
    /// see <see cref="RpgLauncher.OnReloading"/>, which pulls a round straight out of the
    /// inventory. Throwing the round is also handled here, but only as a safety net: the
    /// throw is cancelled so a live HE grenade never leaves the player's hand.
    /// </remarks>
    [CustomItem(ItemType.GrenadeHE)]
    public sealed class RpgAmmo : CustomItem
    {
        /// <summary>Custom item id of the round, used to recognise it in an inventory.</summary>
        public const uint RoundId = 2;

        public override uint Id { get; set; } = RoundId;


        public override string Name { get; set; } = "RPG Round";

        public override string Description { get; set; } = "Rocket for the RPG-7. Hold the launcher and press R to load it.";


        public override ItemType Type { get; set; } = ItemType.GrenadeHE;

        public override float Weight { get; set; } = 1f;

        // The base declares this as nullable; matching the signature avoids CS8765.
        // The round is only handed out by commands, so no spawn locations are defined.
        public override SpawnProperties? SpawnProperties { get; set; } = new SpawnProperties();


        /// <summary>
        /// Suppresses the "you picked up ..." hint. The base implementation shows a hint
        /// built from <see cref="Name"/> and <see cref="Description"/> on every pickup,
        /// which spams the screen while carrying several rounds.
        /// </summary>
        protected override void ShowPickedUpMessage(Player player)
        {
        }

        /// <summary>Suppresses the "you selected ..." hint for the same reason.</summary>
        protected override void ShowSelectedMessage(Player player)
        {
        }

        /// <summary>
        /// Receiving a round refreshes the launcher's reserve counter, so the reload key
        /// starts working immediately instead of only after the next shot.
        /// </summary>
        protected override void OnAcquired(Player player, Item item, bool displayMessage)
        {
            // displayMessage is forced off: no pickup hint for a custom item.
            base.OnAcquired(player, item, false);
            RpgLauncher.SyncReserveAmmo(player);
        }

        protected override void SubscribeEvents()
        {
            Exiled.Events.Handlers.Player.ThrowingRequest += OnThrowingRequest;
            base.SubscribeEvents();
        }


        protected override void UnsubscribeEvents()
        {
            Exiled.Events.Handlers.Player.ThrowingRequest -= OnThrowingRequest;
            base.UnsubscribeEvents();
        }

        /// <summary>
        /// Turns the throw input into a reload. The round never becomes a live grenade:
        /// the request is switched to <see cref="ThrowRequest.CancelThrow"/> so the server
        /// aborts the throw and the item stays in the player's hand.
        /// </summary>
        private void OnThrowingRequest(ThrowingRequestEventArgs ev)
        {
            if (ev.Player is null || ev.Item is null)
                return;

            if (!Check(ev.Item))
                return;

            // A single key press produces several requests: BeginThrow while the key is
            // held, then WeakThrow or FullForceThrow on release. Reload on BeginThrow
            // only, otherwise one press would consume two rounds.
            bool isFirstPhase = ev.RequestType == ThrowRequest.BeginThrow;

            // ThrowingRequestEventArgs has no IsAllowed in EXILED 9.x; a throw is
            // cancelled by rewriting the request type. Every phase must be cancelled -
            // otherwise the round leaves the hand as a live HE grenade and kills the
            // player who was trying to reload.
            ev.RequestType = ThrowRequest.CancelThrow;

            if (!isFirstPhase)
                return;

            TryLoadLauncher(ev.Player, ev.Item);
        }


        /// <summary>
        /// Loads one rocket into the first RPG-7 found in the player's inventory.
        /// The round is consumed only when the launcher actually had a free chamber.
        /// </summary>
        private static void TryLoadLauncher(Player player, Item round)
        {
            Firearm? launcher = FindLauncher(player);
            if (launcher is null)
            {

                return;
            }

            if (RpgLauncher.IsLoaded(launcher.Serial))
            {

                return;
            }

            RpgLauncher.SetLoaded(launcher, true);

            player.RemoveItem(round);

        }

        private static Firearm? FindLauncher(Player player)
        {
            foreach (Item item in player.Items)
            {
                if (item is Firearm firearm && RpgLauncher.IsLauncher(firearm.Serial))
                    return firearm;
            }

            return null;
        }
    }
}
