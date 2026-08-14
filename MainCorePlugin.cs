using System;
using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.CustomItems.API.Features;
using MainCore.Medical;
// using MainCore.Medical.Visuals; // ВРЕМЕННО: система видимых ранений отключена

using MapHandlers = Exiled.Events.Handlers.Map;
using PlayerHandlers = Exiled.Events.Handlers.Player;
using ServerHandlers = Exiled.Events.Handlers.Server;

namespace MainCore
{
    /// <summary>
    /// War RP - основной плагин. На данном этапе реализована только медицинская система.
    /// </summary>
    public sealed class MainCorePlugin : Plugin<Config>
    {
        public static MainCorePlugin Instance { get; private set; } = null!;

        public override string Name => "MainCore";

        public override string Author => "tkanu";

        public override string Prefix => "maincore";

        public override Version Version { get; } = new(0, 1, 0);

        public override Version RequiredExiledVersion { get; } = new(9, 14, 2);

        public override PluginPriority Priority => PluginPriority.Default;

        private EventHandlers handlers = null!;

        public override void OnEnabled()
        {
            Instance = this;
            // MapEditorBridge.ResetCache(); // ВРЕМЕННО: визуал ранений отключён

            handlers = new EventHandlers();

            PlayerHandlers.Hurting += handlers.OnHurting;
            PlayerHandlers.Healing += handlers.OnHealing;
            PlayerHandlers.Died += handlers.OnDied;
            PlayerHandlers.Spawned += handlers.OnSpawned;
            PlayerHandlers.ChangingRole += handlers.OnChangingRole;
            PlayerHandlers.Left += handlers.OnLeft;

            PlayerHandlers.UsingItem += handlers.OnUsingItem;
            PlayerHandlers.CancellingItemUse += handlers.OnCancellingItemUse;
            PlayerHandlers.UsedItem += handlers.OnUsedItem;
            PlayerHandlers.ItemRemoved += handlers.OnItemRemoved;

            // Управление FPV-дроном: прыжок - тяга, alt (noclip) - торможение,
            // бросок гранаты - сброс из-под дрона, рация - возврат управления.
            PlayerHandlers.Jumping += handlers.OnJumping;
            PlayerHandlers.TogglingNoClip += handlers.OnTogglingNoClip;
            PlayerHandlers.ThrowingRequest += handlers.OnThrowingRequest;
            PlayerHandlers.TogglingRadio += handlers.OnTogglingRadio;
            PlayerHandlers.ChangingRadioPreset += handlers.OnChangingRadioPreset;

            ServerHandlers.WaitingForPlayers += handlers.OnWaitingForPlayers;
            ServerHandlers.RestartingRound += handlers.OnRestartingRound;

            MapHandlers.ChangedIntoGrenade += handlers.OnChangedIntoGrenade;

            CustomItem.RegisterItems();

            // Систему запускает WaitingForPlayers. Здесь достаточно проверить конфиг,
            // чтобы администратор увидел ошибки сразу при загрузке плагина.
            Config.Normalize();

            base.OnEnabled();
        }

        public override void OnDisabled()
        {
            PlayerHandlers.Hurting -= handlers.OnHurting;
            PlayerHandlers.Healing -= handlers.OnHealing;
            PlayerHandlers.Died -= handlers.OnDied;
            PlayerHandlers.Spawned -= handlers.OnSpawned;
            PlayerHandlers.ChangingRole -= handlers.OnChangingRole;
            PlayerHandlers.Left -= handlers.OnLeft;

            PlayerHandlers.UsingItem -= handlers.OnUsingItem;
            PlayerHandlers.CancellingItemUse -= handlers.OnCancellingItemUse;
            PlayerHandlers.UsedItem -= handlers.OnUsedItem;
            PlayerHandlers.ItemRemoved -= handlers.OnItemRemoved;

            PlayerHandlers.Jumping -= handlers.OnJumping;
            PlayerHandlers.TogglingNoClip -= handlers.OnTogglingNoClip;
            PlayerHandlers.ThrowingRequest -= handlers.OnThrowingRequest;
            PlayerHandlers.TogglingRadio -= handlers.OnTogglingRadio;
            PlayerHandlers.ChangingRadioPreset -= handlers.OnChangingRadioPreset;

            ServerHandlers.WaitingForPlayers -= handlers.OnWaitingForPlayers;
            ServerHandlers.RestartingRound -= handlers.OnRestartingRound;

            MapHandlers.ChangedIntoGrenade -= handlers.OnChangedIntoGrenade;

            CustomItem.UnregisterItems();

            MedicalManager.Stop();
            MedicalItemManager.Clear();
            Drone.DroneManager.Stop();
            // Medical.Visuals.WoundVisualManager.Clear(); // ВРЕМЕННО: визуал ранений отключён

            handlers = null!;

            Instance = null!;

            base.OnDisabled();
        }
    }
}
