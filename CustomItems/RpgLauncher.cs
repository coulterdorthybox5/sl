using System.Collections.Generic;
using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.API.Features.Attributes;
using Exiled.API.Features.Items;
using Exiled.API.Features.Pickups.Projectiles;
using Exiled.API.Features.Spawn;
using Exiled.CustomItems.API.Features;
using Exiled.Events.EventArgs.Player;
using UnityEngine;

namespace MainCore.CustomItems
{
    /// <summary>
    /// RPG-7: a single-shot rocket launcher.
    /// </summary>
    /// <remarks>
    /// Behaviour: the launcher is handed out with one rocket in the tube and holds at most
    /// one. The reload key consumes one <see cref="RpgAmmo"/> round from the inventory;
    /// ordinary rifle ammo can never load it. Firing spawns a rocket that flies straight at
    /// <see cref="Config.RpgRocketSpeed"/> and explodes on contact with any collider.
    /// </remarks>
    [CustomItem(ItemType.GunE11SR)]
    public sealed class RpgLauncher : CustomWeapon
    {
        /// <summary>Maximum rockets a launcher can hold. The RPG-7 is single-shot.</summary>
        public const byte MaxRockets = 1;

        /// <summary>Serials of every launcher currently in play, so ammo can find it.</summary>
        private static readonly HashSet<ushort> Launchers = new HashSet<ushort>();

        /// <summary>Serials of launchers that currently have a rocket in the tube.</summary>
        private static readonly HashSet<ushort> Loaded = new HashSet<ushort>();

        public override uint Id { get; set; } = 1;

        public override string Name { get; set; } = "RPG-7";

        public override string Description { get; set; } = "Single-shot rocket launcher. Press R to load an RPG round.";

        public override ItemType Type { get; set; } = ItemType.GunE11SR;

        public override float Weight { get; set; } = 4f;

        // The base declares this as nullable; matching the signature avoids CS8765.
        // The launcher is only handed out by commands, so no spawn locations are defined.
        public override SpawnProperties? SpawnProperties { get; set; } = new SpawnProperties();

        /// <summary>The rocket does the damage, not the hitscan bullet.</summary>
        public override float Damage { get; set; } = 0f;

        public override byte ClipSize { get; set; } = MaxRockets;

        private static Config Config => MainCorePlugin.Instance.Config;

        /// <summary>Whether the given serial belongs to an RPG-7.</summary>
        public static bool IsLauncher(ushort serial) => Launchers.Contains(serial);

        /// <summary>Whether the launcher with the given serial has a rocket loaded.</summary>
        public static bool IsLoaded(ushort serial) => Loaded.Contains(serial);

        /// <summary>
        /// Loads or unloads the launcher and mirrors the state in the weapon's magazine
        /// so the client HUD shows 1/1 or 0/1 instead of the E-11 default.
        /// </summary>
        public static void SetLoaded(Firearm firearm, bool loaded)
        {
            if (firearm is null)
                return;

            Launchers.Add(firearm.Serial);

            if (loaded)
                Loaded.Add(firearm.Serial);
            else
                Loaded.Remove(firearm.Serial);

            firearm.MaxMagazineAmmo = MaxRockets;
            firearm.MagazineAmmo = loaded ? MaxRockets : 0;
        }

        /// <summary>Clears tracking between rounds so serials are not leaked for a whole match.</summary>
        public static void Clear()
        {
            Launchers.Clear();
            Loaded.Clear();
        }

        /// <summary>
        /// Keeps a non-zero reserve of the launcher's own calibre in the ammo bag.
        /// </summary>
        /// <remarks>
        /// This is what makes the reload key work at all. The client refuses to send a
        /// reload request when the ammo bag holds nothing of the weapon's calibre, so with
        /// an empty 5.56 reserve the <c>ReloadingWeapon</c> event never fired and pressing R
        /// did literally nothing. Keeping at least one reserve round guarantees the request
        /// reaches the server, where the real rocket loading happens.
        /// The reserve is set to the number of RPG rounds carried, so the HUD counter
        /// doubles as a rocket count.
        /// </remarks>
        public static void SyncReserveAmmo(Player player, Firearm firearm)
        {
            if (player is null || firearm is null)
                return;

            AmmoType ammoType = firearm.AmmoType;
            if (ammoType == AmmoType.None)
                return;


            // At least one, otherwise the client would stop sending reload requests and the
            // player could never reload again.
            ushort desired = (ushort)Mathf.Max(1, CountRounds(player));

            if (player.GetAmmo(ammoType) != desired)
                player.SetAmmo(ammoType, desired);
        }

        /// <summary>
        /// Suppresses the "you picked up ..." hint. The base implementation shows a hint
        /// built from <see cref="Name"/> and <see cref="Description"/> on every pickup,
        /// which covers the screen and clashes with the launcher's own status hints.
        /// </summary>
        protected override void ShowPickedUpMessage(Player player)
        {
        }

        /// <summary>Suppresses the "you selected ..." hint for the same reason.</summary>
        protected override void ShowSelectedMessage(Player player)
        {
        }

        /// <summary>
        /// A freshly given launcher arrives with one rocket already in the tube.
        /// Launchers picked back up keep whatever state they had.
        /// </summary>
        protected override void OnAcquired(Player player, Item item, bool displayMessage)
        {
            // displayMessage is forced off: no pickup hint for a custom item.
            base.OnAcquired(player, item, false);

            if (item is not Firearm firearm)
                return;

            bool isNew = !Launchers.Contains(firearm.Serial);

            // A launcher that was never seen before is a fresh issue: start it loaded.
            SetLoaded(firearm, isNew || Loaded.Contains(firearm.Serial));
            SyncReserveAmmo(player, firearm);
        }

        /// <summary>
        /// The reload key loads a rocket by consuming one RPG round from the inventory.
        /// Vanilla reloading is always cancelled, otherwise the launcher could be topped up
        /// from ordinary rifle ammo.
        /// </summary>
        protected override void OnReloading(ReloadingWeaponEventArgs ev)
        {
            if (ev.Player is null || ev.Firearm is null)
                return;

            if (!Check(ev.Player.CurrentItem))
                return;

            // Vanilla reload never runs for an RPG: the magazine is driven manually.
            ev.IsAllowed = false;

            Player player = ev.Player;
            Firearm firearm = ev.Firearm;
            Launchers.Add(firearm.Serial);

            if (Loaded.Contains(firearm.Serial))
            {
                player.ShowHint("<b>RPG-7:</b> already loaded.", 1.5f);
                SyncReserveAmmo(player, firearm);
                return;
            }

            Item? round = FindRound(player);
            if (round is null)
            {
                player.ShowHint("<b>RPG-7:</b> no RPG rounds left.", 2f);
                SyncReserveAmmo(player, firearm);
                return;
            }

            SetLoaded(firearm, true);
            player.RemoveItem(round);
            player.ShowHint("<b>RPG-7:</b> loaded.", 1.5f);

            // Re-sync after the round was spent so the reserve counter matches the
            // remaining rockets and the next reload request is still sent.
            SyncReserveAmmo(player, firearm);
        }

        /// <summary>
        /// Replaces the hitscan shot with a rocket. The bullet itself is always
        /// suppressed so the launcher cannot be used as a rifle.
        /// </summary>
        protected override void OnShooting(ShootingEventArgs ev)
        {
            if (ev.Player is null || ev.Firearm is null)
                return;

            if (!Check(ev.Player.CurrentItem))
                return;

            // No hitscan damage from an RPG - the rocket carries the payload.
            ev.IsAllowed = false;

            Player player = ev.Player;
            Firearm firearm = ev.Firearm;
            Launchers.Add(firearm.Serial);

            if (!Loaded.Contains(firearm.Serial))
            {
                SetLoaded(firearm, false);
                SyncReserveAmmo(player, firearm);
                player.ShowHint("<b>RPG-7:</b> empty. Press R to load a round.", 1.5f);
                return;
            }

            SetLoaded(firearm, false);
            SyncReserveAmmo(player, firearm);
            LaunchRocket(player);
        }

        /// <summary>
        /// Refreshes the reserve counter on every launcher the player is carrying.
        /// Called when a round is gained or lost so the reload key keeps working.
        /// </summary>
        public static void SyncReserveAmmo(Player player)
        {
            if (player is null)
                return;

            foreach (Item item in player.Items)
            {
                if (item is Firearm firearm && Launchers.Contains(firearm.Serial))
                    SyncReserveAmmo(player, firearm);
            }
        }

        /// <summary>
        /// Counts the RPG rounds in the player's inventory.
        /// </summary>
        private static int CountRounds(Player player)

        {
            if (!CustomItem.TryGet(RpgAmmo.RoundId, out CustomItem? ammo) || ammo is null)
                return 0;

            int count = 0;
            foreach (Item item in player.Items)
            {
                if (item is not null && ammo.Check(item))
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Finds one RPG round in the player's inventory.
        /// </summary>
        /// <remarks>
        /// The round is matched through the custom item registry rather than by
        /// <see cref="ItemType"/>: an RPG round is a re-skinned HE grenade, so a plain type
        /// check would also match ordinary grenades and let a player load the launcher with
        /// regular explosives.
        /// </remarks>
        private static Item? FindRound(Player player)
        {
            if (!CustomItem.TryGet(RpgAmmo.RoundId, out CustomItem? ammo) || ammo is null)
                return null;

            foreach (Item item in player.Items)
            {
                if (item is not null && ammo.Check(item))
                    return item;
            }

            return null;
        }

        /// <summary>
        /// Spawns the rocket: a frag grenade projectile with gravity disabled, pushed
        /// forward at a constant speed and armed to explode on the first collider it
        /// touches (see <see cref="RpgRocketDetonator"/>).
        /// </summary>
        private void LaunchRocket(Player player)
        {
            Transform camera = player.CameraTransform;
            if (camera is null)
                return;

            Vector3 direction = camera.forward;

            Projectile? projectile = player.ThrowGrenade(ProjectileType.FragGrenade, false)?.Projectile;
            if (projectile is null || projectile.GameObject is null)
                return;

            // Spawn slightly ahead of the muzzle so the rocket does not start inside the
            // shooter's own collider and detonate immediately.
            projectile.Position = camera.position + direction * 1.2f;

            // The fuse doubles as the self-destruct timer for a rocket that never hits
            // anything, so a stray rocket cannot fly across the map forever.
            if (projectile is TimeGrenadeProjectile timed)
                timed.FuseTime = Config.RpgLifetimeSeconds;

            Rigidbody rb = projectile.GameObject.GetComponent<Rigidbody>();
            if (rb is not null)
            {
                // A rocket flies flat: no gravity, no tumbling, constant speed.
                rb.useGravity = false;
                rb.angularVelocity = Vector3.zero;
                rb.velocity = direction * Config.RpgRocketSpeed;
            }

            RpgRocketDetonator.Launch(
                projectile,
                player,
                direction * Config.RpgRocketSpeed,
                Config.RpgSafeDistance,
                Config.RpgLifetimeSeconds);
        }
    }
}
