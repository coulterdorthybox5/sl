using System;
using System.Collections.Generic;
using System.ComponentModel;
using Exiled.API.Features;
using Exiled.API.Interfaces;

// ---------------------------------------------------------------- разрушаемая карта

namespace MainCore
{
    /// <summary>
    /// Конфигурация плагина War RP (медицинская система).
    /// </summary>
    public sealed class Config : IConfig
    {
        /// <summary>Жёсткий предел интенсивности эффекта, заданный балансом системы.</summary>
        private const byte IntensityHardLimit = 50;

        public bool IsEnabled { get; set; } = true;

        public bool Debug { get; set; } = false;

        // ---------------------------------------------------------------- эффекты

        [Description("Максимально допустимая интенсивность любого эффекта от ранения (не более 50).")]
        public byte MaxEffectIntensity { get; set; } = 50;

        [Description("На сколько секунд выдаётся постоянный эффект при каждом тике (должно быть больше тика системы).")]
        public float EffectRefreshDuration { get; set; } = 2.5f;

        // ---------------------------------------------------------------- лечение

        [Description("Количество использований медицинских предметов. Ключ - тип предмета.")]
        public Dictionary<ItemType, int> ItemCharges { get; set; } = new Dictionary<ItemType, int>
        {
            [ItemType.Medkit] = 15,
            [ItemType.Painkillers] = 10,
            [ItemType.Adrenaline] = 10,
            [ItemType.SCP500] = 3,
        };

        [Description("Сколько ХП начисляет одно использование предмета. ХП не даются сразу - они восстанавливаются постепенно.")]
        public Dictionary<ItemType, float> ItemHealPerUse { get; set; } = new Dictionary<ItemType, float>
        {
            [ItemType.Medkit] = 12f,
            [ItemType.Painkillers] = 5f,
            [ItemType.Adrenaline] = 5f,
            [ItemType.SCP500] = 25f,
        };

        [Description("Сколько искусственных ХП (AHP) даёт одно использование предмета. Они начисляются сразу.")]
        public Dictionary<ItemType, float> ItemAhpPerUse { get; set; } = new Dictionary<ItemType, float>
        {
            [ItemType.Adrenaline] = 20f,
        };

        [Description("Сколько ступеней лечения снимает одно использование предмета.")]
        public Dictionary<ItemType, int> ItemTreatmentSteps { get; set; } = new Dictionary<ItemType, int>
        {
            [ItemType.Medkit] = 1,
            [ItemType.Painkillers] = 1,
            [ItemType.Adrenaline] = 1,
            [ItemType.SCP500] = 3,
        };

        // ---------------------------------------------------------------- постепенное восстановление ХП

        [Description("Базовая скорость восстановления ХП в секунду. 0.25 = 15 ХП в минуту.")]
        public float HealPerSecond { get; set; } = 0.25f;

        [Description("Максимальное время полного восстановления начисленных ХП в секундах. " +
                     "Если начислено много ХП, скорость поднимается так, чтобы уложиться в этот срок.")]
        public float MaxHealSeconds { get; set; } = 300f;

        [Description("Максимальный запас ХП в очереди восстановления. Больше этого накопить нельзя.")]
        public float MaxPendingHeal { get; set; } = 100f;

        [Description("Не восстанавливать ХП, пока у игрока есть незалеченное кровотечение. " +
                     "Если выключено, кровотечения просто сильно замедляют восстановление " +
                     "(множители заданы для каждого ранения в коде).")]
        public bool StopHealingWhileBleeding { get; set; } = true;

        [Description("Учитывать множители скорости лечения от ранений. " +
                     "Перелом, ожог и внутреннее кровотечение заживают медленнее лёгких ранений.")]
        public bool UseInjuryHealRates { get; set; } = true;

        [Description("Минимальная скорость восстановления в долях от базовой. " +
                     "Не даёт куче ранений полностью остановить заживление.")]
        public float MinHealRateMultiplier { get; set; } = 0.1f;

        [Description("Полностью отключать ванильное лечение медицинских предметов " +
                     "(мгновенные ХП аптечки, регенерация таблеток и SCP-500).")]
        public bool OverrideVanillaHealing { get; set; } = true;

        // ---------------------------------------------------------------- источники ранений

        [Description("Типы урона, которые не наносят ранений (сравнение по имени DamageType, регистр не важен).")]
        public List<string> IgnoredDamageTypes { get; set; } = new List<string>
        {
            "Bleeding",
            "Poison",
            "Asphyxiation",
            "Decontamination",
            "Warhead",
            "Recontainment",
            "PocketDimension",
            "FemurBreaker",
            "Crushed",
            "Hypothermia",
            "Scp207",
            "Scp1853",
            "CardiacArrest",
            "Strangled",
            "FriendlyFireDetector",
            "SeveredHands",
            "SeveredEyes",
            "Silent",
            "Custom",
            "Unknown",
        };

        [Description("Урон от падения, с которого игрок получает ушиб (примерно 4 метра).")]
        public float ContusionFallDamage { get; set; } = 5f;

        [Description("Урон от падения, с которого игрок получает перелом.")]
        public float FractureFallDamage { get; set; } = 25f;

        [Description("Урон, с которого попадание считается тяжёлым.")]
        public float HeavyDamage { get; set; } = 35f;

        [Description("Урон, с которого попадание считается средним.")]
        public float MediumDamage { get; set; } = 15f;

        [Description("Шансы (0-100) ранений при тяжёлом попадании: артериальное / венозное / капиллярное.")]
        public int HeavyArterialChance { get; set; } = 35;

        public int HeavyVenousChance { get; set; } = 40;

        public int HeavyCapillaryChance { get; set; } = 25;

        [Description("Шансы (0-100) ранений при среднем попадании: венозное / капиллярное / лёгкое.")]
        public int MediumVenousChance { get; set; } = 25;

        public int MediumCapillaryChance { get; set; } = 45;

        public int MediumLightChance { get; set; } = 30;

        [Description("Шансы (0-100) при слабом попадании: капиллярное / лёгкое / без ранения. Сумма должна быть 100.")]
        public int WeakCapillaryChance { get; set; } = 25;

        public int WeakLightChance { get; set; } = 55;

        public int WeakNothingChance { get; set; } = 20;

        [Description("Шанс (0-100) внутреннего кровотечения при попадании в корпус тяжёлым уроном.")]
        public int InternalBleedingChance { get; set; } = 30;

        [Description("Шанс (0-100) венозного кровотечения от взрыва (иначе капиллярное).")]
        public int ExplosionVenousChance { get; set; } = 40;

        [Description("Шанс (0-100) ожога от взрыва или микро-хида.")]
        public int BurnChance { get; set; } = 50;

        // ---------------------------------------------------------------- визуал ранений

        [Description("Show dressings on the player model. No external plugins or files are needed: " +
                     "dressings are built from built-in primitive admin toys compiled into MainCore.")]
        public bool ShowWoundVisuals { get; set; } = true;

        [Description("Use ProjectMER for wound dressings. Disabled by default because moving MER schematic " +
                     "hierarchies can report a successful spawn without producing a client-visible attachment. " +
                     "The built-in network primitives are used when false.")]
        public bool UseMapEditorWoundVisuals { get; set; } = false;

        [Description("Максимум перевязок на одном игроке. Каждая перевязка - это сетевые объекты, " +
                     "поэтому лимит защищает сервер от перегрузки при большом числе игроков.")]
        public int MaxWoundVisualsPerPlayer { get; set; } = 3;

        [Description("Задержка в секундах перед появлением перевязки после респавна. " +
                     "Нужна, чтобы модель игрока успела создать скелет.")]
        public float WoundVisualSpawnDelay { get; set; } = 1f;

        [Description("How long a freshly applied dressing stays clean, in seconds. After that it gets " +
                     "dirty, or bloody if the wound is still bleeding - that is the medic's cue to redress it.")]
        public float DressingCleanSeconds { get; set; } = 180f;

        [Description("How long a dressing stays on the body after the wound is fully healed, in seconds. " +
                     "Most wounds need a single treatment step and a medkit gives exactly one, so without " +
                     "this the bandage would vanish the instant it was applied.")]
        public float DressingLingerSeconds { get; set; } = 300f;

        [Description("Verbose wound-visual log in the server console: bone lookup, dressing spawn, " +
                     "attachment. Turn it on if dressings do not appear - the log shows which step failed. " +
                     "Warnings and errors are always logged regardless of this setting.")]
        public bool WoundVisualDebug { get; set; } = true;

        [Description("Perform real runtime audits of spawned wound visuals: inspect child objects, AdminToys, " +
                     "Mirror identities, transforms and following distance. More expensive than the normal debug log.")]
        public bool WoundVisualDeepDebug { get; set; } = false;

        [Description("Seconds between runtime audits while WoundVisualDeepDebug is enabled (minimum 0.25).")]
        public float WoundVisualDebugInterval { get; set; } = 5f;

        // ---------------------------------------------------------------- разрушаемая карта

        [Description("Names of objects (or ProjectMER schematic roots) that become BreakableNorm on spawn. " +
                     "Matched case-insensitive; a partial substring match on the GameObject name is enough.")]
        public List<string> BreakableNames { get; set; } = new List<string>
        {
            "Zabor",
        };

        [Description("Names of objects that become BreakableRigid on spawn (uses Rigidbody physics after break).")]
        public List<string> BreakableRigidNames { get; set; } = new List<string>();

        [Description("Names of objects that become Damaging on spawn (hurts player standing on/inside them).")]
        public List<string> DamagingNames { get; set; } = new List<string>();

        [Description("HP of a breakable object.")]
        public float BreakableHealth { get; set; } = 40f;

        [Description("Radius (m) in which a grenade explosion damages breakable objects.")]
        public float BreakableExplosionRadius { get; set; } = 6f;

        [Description("Damage dealt by a grenade explosion at zero distance.")]
        public float BreakableExplosionDamage { get; set; } = 40f;

        [Description("Force applied to BreakableRigid objects when they are broken by an explosion.")]
        public float BreakableExplosionForce { get; set; } = 500f;

        [Description("Split each cube of a breakable into N smaller cubes on break. 1 = disabled (default). " +
                     "Only applied to Cube primitives. Use small numbers like 8 or 27 to avoid heavy load.")]
        public int BreakableSplitCount { get; set; } = 1;

        [Description("Lifetime of the split fragments in seconds before they are removed from the world.")]
        public float BreakableSplitLifetimeSeconds { get; set; } = 5f;

        [Description("Explosion force applied to each fragment when a breakable is split on break.")]
        public float BreakableSplitForce { get; set; } = 250f;

        [Description("Force breakable primitives to be collidable. PrimitiveObjectToy keeps its collider " +
                     "disabled while the Collidable flag is missing, so a non-collidable breakable cannot be " +
                     "shot (hits are resolved by raycast) and its debris falls straight through the map floor. " +
                     "Keep this enabled unless a breakable is meant to be walked through.")]
        public bool BreakableForceCollidable { get; set; } = true;

        [Description("How far (m) below its starting height a broken piece may fall before it is removed. " +
                     "Protects against pieces that escaped the map geometry falling forever.")]
        public float BreakableMaxFallDepth { get; set; } = 30f;

        [Description("Damaging: base damage per second dealt to a player standing on the object.")]
        public float DamagingDamagePerSecond { get; set; } = 5f;

        [Description("Damaging: tick interval in seconds.")]
        public float DamagingTickInterval { get; set; } = 1f;

        [Description("Damaging: name of the effect applied to the player each tick (must match Exiled EffectType).")]
        public string DamagingEffectName { get; set; } = string.Empty;

        [Description("Damaging: duration of the effect in seconds.")]
        public float DamagingEffectSeconds { get; set; } = 3f;

        [Description("Damaging: intensity of the effect (1-50).")]
        public byte DamagingEffectIntensity { get; set; } = 1;

        [Description("Damaging: show broadcast message to the player.")]
        public bool DamagingShowMessage { get; set; } = false;

        [Description("Damaging: broadcast text.")]
        public string DamagingBroadcast { get; set; } = string.Empty;

        [Description("Damaging: broadcast duration in seconds.")]
        public ushort DamagingBroadcastSeconds { get; set; } = 3;

        [Description("How often (seconds) the destruction manager scans the scene for new objects to hook.")]
        public float DestructionScanInterval { get; set; } = 1f;

        // ---------------------------------------------------------------- custom items

        [Description("RPG: rocket speed in metres per second. The rocket flies in a straight " +
                     "line at this speed - gravity does not affect it.")]
        public float RpgRocketSpeed { get; set; } = 40f;

        [Description("RPG: safety distance in metres. If the rocket would detonate closer than " +
                     "this to the shooter, the explosion is cancelled and the rocket just " +
                     "disappears, so a point-blank shot cannot instantly kill its own user.")]
        public float RpgSafeDistance { get; set; } = 2f;

        [Description("RPG: rocket self-destruct timeout in seconds, used when the rocket never " +
                     "hits anything. Prevents rockets from flying across the map forever.")]
        public float RpgLifetimeSeconds { get; set; } = 10f;

        [Description("RPG: radius (m) in which a rocket impact damages breakable objects.")]
        public float RpgExplosionRadius { get; set; } = 8f;

        [Description("RPG: damage dealt to breakable objects at zero distance from the impact.")]
        public float RpgExplosionDamage { get; set; } = 200f;

        [Description("RPG: force applied to BreakableRigid objects and fragments broken by a rocket.")]
        public float RpgExplosionForce { get; set; } = 3400f;

        // ---------------------------------------------------------------- FPV drone

        [Description("Drone: name of the ProjectMER schematic used as the drone body.")]
        public string DroneSchematicName { get; set; } = "drone";

        [Description("Drone: if true, the plugin never uses the ProjectMER schematic for the drone body and " +
                     "always builds the simple primitive drone instead. This completely removes ProjectMER from " +
                     "the drone lifecycle (ProjectMER can despawn its own schematic ~1s after spawn, which " +
                     "manifested as the drone 'disappearing'). The primitive body is tagged to survive the " +
                     "PrimitiveCuller and is 100% owned by this plugin.")]
        public bool DroneForcePrimitiveBody { get; set; } = false;

        [Description("Drone: hit points. The drone explodes when shot or crashed down to zero.")]
        public float DroneHealth { get; set; } = 30f;

        [Description("Drone: indicator light intensity (green=can place, red=blocked, white=placed/flying).")]
        public float DroneLightIntensity { get; set; } = 5f;

        [Description("Drone: indicator light range in metres.")]
        public float DroneLightRange { get; set; } = 4f;

        [Description("Drone: how long the light stays solid white after placement, in seconds.")]
        public float DroneLightHoldSeconds { get; set; } = 1f;

        [Description("Drone: how long the light fades from full to zero after the hold, in seconds.")]
        public float DroneLightFadeSeconds { get; set; } = 3f;

        [Description("Drone: how far (m) in front of the player the placement preview appears.")]
        public float DronePreviewDistance { get; set; } = 2f;

        [Description("Drone: speed gained per jump press, in metres per second.")]
        public float DroneSpeedStep { get; set; } = 1f;

        [Description("Drone: maximum speed in metres per second. Kept close to the crash speed " +
                     "on purpose - a high ceiling only lets pilots kill themselves on the first wall.")]
        public float DroneMaxSpeed { get; set; } = 10f;

        [Description("Drone: impact speed (m/s) at or above which a collision destroys the drone. " +
                     "Slower impacts only bounce it back.")]
        public float DroneCrashSpeed { get; set; } = 6f;

        [Description("Drone: how far (m) the drone is thrown back by a non-lethal impact.")]
        public float DroneBounceDistance { get; set; } = 2f;

        [Description("Drone: explosion force applied to breakable objects when the drone crashes.")]
        public float DroneExplosionForce { get; set; } = 1500f;

        [Description("Drone: radius (m) in which a drone crash damages breakable objects.")]
        public float DroneExplosionRadius { get; set; } = 6f;

        [Description("Drone: damage dealt to breakable objects at zero distance from the crash.")]
        public float DroneExplosionDamage { get; set; } = 150f;

        [Description("Drone: fall speed (m/s) used while the drone has no forward thrust. " +
                     "The drone is not a rigidbody - gravity is applied by hand so the camera stays steady.")]
        public float DroneFallSpeed { get; set; } = 4f;

        [Description("Drone: how far (m) above the schematic the pilot's camera sits.")]
        public float DroneCameraOffset { get; set; } = 0.1f;

        [Description("Drone: pilot scale while flying. 1 = normal player size.")]
        public float DronePilotScale { get; set; } = 0.1f;

        [Description("Drone: how far (m) below the drone a dropped grenade appears.")]
        public float DroneGrenadeDropOffset { get; set; } = 0.5f;

        [Description("Drone: number of grenades handed to the pilot when the drone is entered.")]
        public int DroneGrenadeCount { get; set; } = 2;

        [Description("Drone: verbose log of every drone step (preview, spawn, speed change, impact, exit). " +
                     "Keep it on while testing - the log names the exact step that failed.")]
        public bool DroneDebug { get; set; } = false;




        /// <summary>
        /// Приводит конфиг к допустимым значениям и предупреждает администратора о правках.
        /// Вызывается при запуске системы, чтобы неверные значения не ломали баланс молча.
        /// </summary>
        public void Normalize()
        {
            if (MaxEffectIntensity < 1 || MaxEffectIntensity > IntensityHardLimit)
            {
                Warn($"MaxEffectIntensity must be within 1-{IntensityHardLimit}; value {MaxEffectIntensity} was corrected.");
                MaxEffectIntensity = Math.Min(Math.Max(MaxEffectIntensity, (byte)1), IntensityHardLimit);
            }

            // Постоянные эффекты продлеваются каждый тик: длительность должна перекрывать тик с запасом.
            float minRefresh = Medical.MedicalManager.TickInterval * 1.5f;
            if (EffectRefreshDuration < minRefresh)
            {
                Warn($"EffectRefreshDuration is too small ({EffectRefreshDuration}), effects would flicker. Set to {minRefresh}.");
                EffectRefreshDuration = minRefresh;
            }

            if (HealPerSecond < 0f)
            {
                Warn("HealPerSecond cannot be negative; set to 0.");
                HealPerSecond = 0f;
            }

            if (MaxHealSeconds < 0f)
            {
                Warn("MaxHealSeconds cannot be negative; set to 0.");
                MaxHealSeconds = 0f;
            }

            if (MaxPendingHeal < 0f)
            {
                Warn("MaxPendingHeal cannot be negative; set to 0 (no limit).");
                MaxPendingHeal = 0f;
            }

            if (MinHealRateMultiplier < 0f || MinHealRateMultiplier > 1f)
            {
                Warn($"MinHealRateMultiplier must be within 0-1; value {MinHealRateMultiplier} was corrected.");
                MinHealRateMultiplier = Math.Min(Math.Max(MinHealRateMultiplier, 0f), 1f);
            }

            // Части тела всего шесть, больше визуалов физически не нужно.
            if (MaxWoundVisualsPerPlayer < 1 || MaxWoundVisualsPerPlayer > 6)
            {
                Warn($"MaxWoundVisualsPerPlayer must be within 1-6; value {MaxWoundVisualsPerPlayer} was corrected.");
                MaxWoundVisualsPerPlayer = Math.Min(Math.Max(MaxWoundVisualsPerPlayer, 1), 6);
            }

            if (WoundVisualSpawnDelay < 0f)
            {
                Warn("WoundVisualSpawnDelay cannot be negative; set to 0.");
                WoundVisualSpawnDelay = 0f;
            }

            if (WoundVisualDebugInterval < 0.25f)
            {
                Warn($"WoundVisualDebugInterval is too small ({WoundVisualDebugInterval}); set to 0.25.");
                WoundVisualDebugInterval = 0.25f;
            }

            // Ноль допустим: бинт сразу считается несвежим. Отрицательное значение - нет.
            if (DressingCleanSeconds < 0f)
            {
                Warn("DressingCleanSeconds cannot be negative; set to 0.");
                DressingCleanSeconds = 0f;
            }

            // Ноль означает, что бинт снимается сразу после заживления - визуала не будет видно.
            if (DressingLingerSeconds < 0f)
            {
                Warn("DressingLingerSeconds cannot be negative; set to 0.");
                DressingLingerSeconds = 0f;
            }


            if (MediumDamage > HeavyDamage)
            {
                Warn($"MediumDamage ({MediumDamage}) is greater than HeavyDamage ({HeavyDamage}) - medium hits are unreachable.");
            }

            if (ContusionFallDamage > FractureFallDamage)
            {
                Warn($"ContusionFallDamage ({ContusionFallDamage}) is greater than FractureFallDamage ({FractureFallDamage}).");
            }

            if (BreakableMaxFallDepth < 1f)
            {
                Warn($"BreakableMaxFallDepth is too small ({BreakableMaxFallDepth}); set to 1.");
                BreakableMaxFallDepth = 1f;
            }

            // A rocket with zero or negative speed would hang in front of the muzzle and
            // detonate on the shooter once the self-destruct timer ran out.
            if (RpgRocketSpeed <= 0f)
            {
                Warn($"RpgRocketSpeed must be positive; value {RpgRocketSpeed} was set to 40.");
                RpgRocketSpeed = 40f;
            }

            if (RpgSafeDistance < 0f)
            {
                Warn("RpgSafeDistance cannot be negative; set to 0.");
                RpgSafeDistance = 0f;
            }

            // Below one second the rocket would self-destruct before reaching anything.
            if (RpgLifetimeSeconds < 1f)
            {
                Warn($"RpgLifetimeSeconds is too small ({RpgLifetimeSeconds}); set to 1.");
                RpgLifetimeSeconds = 1f;
            }

            // A zero radius would make the blast miss the very object it hit, because the
            // overlap sphere used to find breakables would be empty.
            if (RpgExplosionRadius <= 0f)
            {
                Warn($"RpgExplosionRadius must be positive; value {RpgExplosionRadius} was set to 8.");
                RpgExplosionRadius = 8f;
            }

            if (RpgExplosionDamage < 0f)
            {
                Warn("RpgExplosionDamage cannot be negative; set to 0.");
                RpgExplosionDamage = 0f;
            }

            if (RpgExplosionForce < 0f)
            {
                Warn("RpgExplosionForce cannot be negative; set to 0.");
                RpgExplosionForce = 0f;
            }

            // A drone that cannot gain speed would never leave the ground.
            if (DroneSpeedStep <= 0f)
            {
                Warn($"DroneSpeedStep must be positive; value {DroneSpeedStep} was set to 1.");
                DroneSpeedStep = 1f;
            }

            if (DroneMaxSpeed < DroneSpeedStep)
            {
                Warn($"DroneMaxSpeed ({DroneMaxSpeed}) is below one speed step; set to {DroneSpeedStep}.");
                DroneMaxSpeed = DroneSpeedStep;
            }

            if (DroneCrashSpeed < 0f)
            {
                Warn("DroneCrashSpeed cannot be negative; set to 0.");
                DroneCrashSpeed = 0f;
            }

            if (DroneBounceDistance < 0f)
            {
                Warn("DroneBounceDistance cannot be negative; set to 0.");
                DroneBounceDistance = 0f;
            }

            if (DroneExplosionRadius <= 0f)
            {
                Warn($"DroneExplosionRadius must be positive; value {DroneExplosionRadius} was set to 6.");
                DroneExplosionRadius = 6f;
            }

            if (DroneExplosionForce < 0f)
            {
                Warn("DroneExplosionForce cannot be negative; set to 0.");
                DroneExplosionForce = 0f;
            }

            if (DroneExplosionDamage < 0f)
            {
                Warn("DroneExplosionDamage cannot be negative; set to 0.");
                DroneExplosionDamage = 0f;
            }

            if (DroneFallSpeed < 0f)
            {
                Warn("DroneFallSpeed cannot be negative; set to 0.");
                DroneFallSpeed = 0f;
            }

            // A zero or negative scale would make the pilot invisible or inverted.
            if (DronePilotScale <= 0f)
            {
                Warn($"DronePilotScale must be positive; value {DronePilotScale} was set to 0.1.");
                DronePilotScale = 0.1f;
            }

            if (DronePreviewDistance < 0f)
            {
                Warn("DronePreviewDistance cannot be negative; set to 0.");
                DronePreviewDistance = 0f;
            }

            if (DroneGrenadeCount < 0)
            {
                Warn("DroneGrenadeCount cannot be negative; set to 0.");
                DroneGrenadeCount = 0;
            }

            // This used to be called FpvDrone while the feature was in development.
            // Existing EXILED YAML configs keep old values when a source default changes,
            // so migrate the old placeholder once instead of requiring every admin to
            // delete their config by hand.
            if (string.Equals(DroneSchematicName, "FpvDrone", StringComparison.OrdinalIgnoreCase))
            {
                Warn("DroneSchematicName 'FpvDrone' was renamed to 'drone'; value was migrated.");
                DroneSchematicName = "drone";
            }
            else if (string.IsNullOrWhiteSpace(DroneSchematicName))
            {
                Warn("DroneSchematicName is empty; set to drone.");
                DroneSchematicName = "drone";
            }


            HeavyArterialChance = ClampChance(nameof(HeavyArterialChance), HeavyArterialChance);
            HeavyVenousChance = ClampChance(nameof(HeavyVenousChance), HeavyVenousChance);
            HeavyCapillaryChance = ClampChance(nameof(HeavyCapillaryChance), HeavyCapillaryChance);
            MediumVenousChance = ClampChance(nameof(MediumVenousChance), MediumVenousChance);
            MediumCapillaryChance = ClampChance(nameof(MediumCapillaryChance), MediumCapillaryChance);
            MediumLightChance = ClampChance(nameof(MediumLightChance), MediumLightChance);
            WeakCapillaryChance = ClampChance(nameof(WeakCapillaryChance), WeakCapillaryChance);
            WeakLightChance = ClampChance(nameof(WeakLightChance), WeakLightChance);
            WeakNothingChance = ClampChance(nameof(WeakNothingChance), WeakNothingChance);
            InternalBleedingChance = ClampChance(nameof(InternalBleedingChance), InternalBleedingChance);
            ExplosionVenousChance = ClampChance(nameof(ExplosionVenousChance), ExplosionVenousChance);
            BurnChance = ClampChance(nameof(BurnChance), BurnChance);

            WarnIfZeroSum("heavy hits", HeavyArterialChance + HeavyVenousChance + HeavyCapillaryChance);
            WarnIfZeroSum("medium hits", MediumVenousChance + MediumCapillaryChance + MediumLightChance);
            WarnIfZeroSum("weak hits", WeakCapillaryChance + WeakLightChance + WeakNothingChance);

            ItemCharges ??= new Dictionary<ItemType, int>();
            ItemHealPerUse ??= new Dictionary<ItemType, float>();
            ItemAhpPerUse ??= new Dictionary<ItemType, float>();
            ItemTreatmentSteps ??= new Dictionary<ItemType, int>();
            IgnoredDamageTypes ??= new List<string>();

            // Предмет без зарядов исчезал бы после первого применения - это не задумано.
            foreach (ItemType key in new List<ItemType>(ItemCharges.Keys))
            {
                if (ItemCharges[key] >= 1)
                    continue;

                Warn($"ItemCharges[{key}] must be at least 1; value {ItemCharges[key]} was corrected to 1.");
                ItemCharges[key] = 1;
            }
        }

        private int ClampChance(string name, int value)
        {
            if (value >= 0 && value <= 100)
                return value;

            Warn($"{name} must be within 0-100; value {value} was corrected.");
            return Math.Min(Math.Max(value, 0), 100);
        }

        private void WarnIfZeroSum(string label, int sum)
        {
            if (sum <= 0)
                Warn($"All chances for {label} are zero - the last option in the list will always be used.");
        }

        // Лог сервера выводит не-ASCII как '?', поэтому предупреждения конфига
        // пишутся только английским ASCII.
        private static void Warn(string message) => Log.Warn($"[Medical] Config: {message}");
    }
}
