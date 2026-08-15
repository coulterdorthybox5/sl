using System;
using System.Collections.Generic;
using AdminToys;
using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.API.Features.Items;
using Exiled.API.Features.Pickups.Projectiles;
using MainCore.Destruction;
using MainCore.Medical.Visuals;
using MEC;
using Mirror;
using UnityEngine;
using ToyLight = Exiled.API.Features.Toys.Light;
using ToyPrimitive = Exiled.API.Features.Toys.Primitive;

namespace MainCore.Drone
{
    /// <summary>
    /// Вся логика FPV-дрона: превью, установка, полёт, столкновения, HP и выход пилота.
    /// </summary>
    /// <remarks>
    /// Ключевое решение - схема «дрон следует за пилотом». Пилот летает нативным noclip,
    /// клиент двигает его по WASD и взгляду, сервер в позицию не вмешивается - только
    /// ведёт модель дрона за игроком и ограничивает максимальную скорость. Это убирает
    /// борьбу за позицию (дрожание) и даёт бесплатное управление.
    ///
    /// Невидимость - эффект <see cref="EffectType.Invisible"/>, не масштаб.
    ///
    /// Модель: сначала пробуем схематик ProjectMER по имени <c>DroneSchematicName</c>.
    /// Если его нет или в нём ноль сетевых блоков - собираем простой дрон из примитивов
    /// прямо в плагине, чтобы предмет работал без внешних файлов.
    /// </remarks>
    public static class DroneManager
    {
        private const float DroneRadius = 0.25f;
        private const float WallClearance = 0.15f;
        private const float CrashDamagePerSpeed = 3f;
        private const float FixedStep = 0.05f;

        /// <summary>Минимальная вертикальная составляющая нормали, чтобы считать поверхность полом.</summary>
        private const float FloorNormalThreshold = 0.55f;

        /// <summary>Сколько свежей должна быть точка превью, чтобы Place её принял.</summary>
        private const float PlacementFreshness = 0.3f;

        private static readonly int SolidMask = BuildSolidMask();
        private static readonly RaycastHit[] HitBuffer = new RaycastHit[16];
        private static readonly Collider[] OverlapBuffer = new Collider[8];

        private static readonly Dictionary<Player, DroneSession> sessions = new Dictionary<Player, DroneSession>();

        private static CoroutineHandle flightCoroutine;
        private static bool running;

        private static Config Config => MainCorePlugin.Instance.Config;

        public static bool IsPiloting(Player player)
            => player is not null && sessions.TryGetValue(player, out DroneSession s) && s.Stage == DroneStage.Piloting;

        public static bool HasPreview(Player player)
            => player is not null && sessions.TryGetValue(player, out DroneSession s) && s.Stage == DroneStage.Preview;

        public static bool HasPlaced(Player player)
            => player is not null && sessions.TryGetValue(player, out DroneSession s) && s.Stage == DroneStage.Placed;

        public static void Start()
        {
            if (running)
                return;

            running = true;

            if (flightCoroutine.IsRunning)
                Timing.KillCoroutines(flightCoroutine);

            flightCoroutine = Timing.RunCoroutine(FlightLoop(), "MainCore.Drone.Flight");
            DroneLog.Step("start", "flight loop started");
        }

        public static void Stop()
        {
            running = false;

            if (flightCoroutine.IsRunning)
                Timing.KillCoroutines(flightCoroutine);

            foreach (DroneSession session in new List<DroneSession>(sessions.Values))
            {
                Release(session, "shutdown");
                DestroyBody(session);
            }

            sessions.Clear();
            DroneLog.Step("stop", "flight loop stopped, all drones removed");
        }

        // ---------------------------------------------------------------- превью

        public static void ShowPreview(Player player)
        {
            if (player is null || !player.IsAlive)
                return;

            if (sessions.TryGetValue(player, out DroneSession existing))
            {
                if (existing.Stage != DroneStage.Abandoned)
                    return;

                DestroyBody(existing);
                sessions.Remove(player);
            }

            Vector3 spot = ResolvePlacement(player, out bool canPlace);

            DroneSession session = new DroneSession(player)
            {
                Stage = DroneStage.Preview,
                Position = spot,
                CanPlace = canPlace,
                PlacementUpdatedAt = Time.realtimeSinceStartup,
                Forward = Flatten(player.CameraTransform is null ? Vector3.forward : player.CameraTransform.forward),
                Health = Mathf.Max(1f, Config.DroneHealth),
            };

            if (!SpawnBody(spot, session.Forward, session, out string error))
            {
                DroneLog.Warn("preview", player, $"drone body could not be created: {error}");

                return;
            }

            session.Light = SpawnLight(spot);
            sessions[player] = session;
            SetLight(session, canPlace ? Color.green : Color.red, Config.DroneLightIntensity);
            DroneLog.Step("preview", player, $"shown at {Format(spot)}, blocks={session.BodyBlocks.Count}");
        }

        public static void CancelPreview(Player player, string reason = "unspecified")
        {
            if (player is null || !sessions.TryGetValue(player, out DroneSession session))
                return;

            if (session.Stage != DroneStage.Preview)
                return;

            DestroyBody(session);
            sessions.Remove(player);
            DroneLog.Step("preview", player, $"cancelled ({reason})");
        }

        /// <summary>
        /// Фиксирует превью на земле (Preview -> Placed). Ставит именно ту точку, что
        /// игрок видел зелёной: позиция НЕ пересчитывается, иначе смещение камеры между
        /// кадром превью и кликом могло бы отклонить установку.
        /// </summary>
        public static bool Place(Player player)
        {
            if (player is null || !sessions.TryGetValue(player, out DroneSession session))
                return false;

            if (session.Stage != DroneStage.Preview)
                return false;

            bool fresh = Time.realtimeSinceStartup - session.PlacementUpdatedAt <= PlacementFreshness;
            if (!session.CanPlace || !fresh)
            {
                float age = Time.realtimeSinceStartup - session.PlacementUpdatedAt;

                DroneLog.Step(
                    "place",
                    player,
                    $"rejected: canPlace={session.CanPlace}, fresh={fresh}, age={age:0.000}s");


                return false;
            }

            session.Stage = DroneStage.Placed;
            MoveBody(session);
            StartLightSequence(session);

            DroneLog.Step("place", player, $"placed on ground at {Format(session.Position)}");
            return true;
        }

        // ---------------------------------------------------------------- вход

        public static void Deploy(Player player)
        {
            if (player is null || !sessions.TryGetValue(player, out DroneSession session))
                return;

            if (session.Stage != DroneStage.Placed)
                return;

            session.OwnerReturnPosition = player.Position;
            session.OwnerNoclipPermitted = player.IsNoclipPermitted;
            session.OwnerNoclipEnabled = player.IsNoclipEnabled;
            session.OwnerHealth = player.Health;
            session.OwnerMaxHealth = player.MaxHealth;
            session.OwnerCustomInfo = player.CustomInfo ?? string.Empty;

            session.OwnerItems.Clear();
            foreach (Item item in player.Items)
                session.OwnerItems.Add(item.Type);

            session.OwnerAmmo.Clear();
            foreach (KeyValuePair<ItemType, ushort> pair in player.Ammo)
                session.OwnerAmmo[pair.Key] = pair.Value;

            session.Dummy = SpawnDummy(player);

            player.IsNoclipPermitted = true;
            player.IsNoclipEnabled = true;
            player.Position = CameraPoint(session);
            player.EnableEffect(EffectType.Invisible, 0f, false);

            session.LastPilotPosition = player.Position;
            session.SpeedLimit = 0f;
            session.Stage = DroneStage.Piloting;

            GiveDroneKit(player);
            // Свет НЕ включаем заново: после установки он гаснет по своей корутине и
            // остаётся выключенным на всё время полёта - так задано в ТЗ.


            DroneLog.Step("deploy", player, $"drone armed at {Format(session.Position)}");
        }

        private static Npc? SpawnDummy(Player player)
        {
            try
            {
                Npc dummy = Npc.Spawn(player.Nickname, player.Role.Type, false, player.Position);
                dummy.Position = player.Position;
                dummy.CustomInfo = player.CustomInfo;
                dummy.MaxHealth = player.MaxHealth;
                dummy.Health = player.Health;
                return dummy;
            }
            catch (Exception exception)
            {
                DroneLog.Warn("dummy", player, $"spawn failed: {exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        private static void GiveDroneKit(Player player)
        {
            player.ClearInventory();

            for (int i = 0; i < Config.DroneGrenadeCount; i++)
                player.AddItem(ItemType.GrenadeHE);

            player.AddItem(ItemType.Radio);
            DroneLog.Step("kit", player, $"{Config.DroneGrenadeCount} grenades and a radio issued");
        }

        // ---------------------------------------------------------------- управление

        public static void Accelerate(Player player)
        {
            if (!sessions.TryGetValue(player, out DroneSession session) || session.Stage != DroneStage.Piloting)
                return;

            session.SpeedLimit = Mathf.Min(Config.DroneMaxSpeed, session.SpeedLimit + Config.DroneSpeedStep);
            DroneLog.Step("speed", player, $"limit up to {session.SpeedLimit:0.#} m/s");
        }

        public static void Decelerate(Player player)
        {
            if (!sessions.TryGetValue(player, out DroneSession session) || session.Stage != DroneStage.Piloting)
                return;

            session.SpeedLimit = Mathf.Max(0f, session.SpeedLimit - Config.DroneSpeedStep);
            DroneLog.Step("speed", player, $"limit down to {session.SpeedLimit:0.#} m/s");
        }

        public static bool DropGrenade(Player player)
        {
            if (!sessions.TryGetValue(player, out DroneSession session) || session.Stage != DroneStage.Piloting)
                return false;

            Vector3 point = session.Position + Vector3.down * Mathf.Max(0f, Config.DroneGrenadeDropOffset);

            if (Physics.Raycast(session.Position, Vector3.down, out RaycastHit floor, 3f, SolidMask, QueryTriggerInteraction.Ignore)
                && floor.distance < Config.DroneGrenadeDropOffset)
            {
                point = floor.point + Vector3.up * 0.1f;
            }

            try
            {
                ExplosiveGrenade grenade = (ExplosiveGrenade)Item.Create(ItemType.GrenadeHE, player);
                grenade.SpawnActive(point, player);
                DroneLog.Step("grenade", player, $"dropped at {Format(point)}");
                return true;
            }
            catch (Exception exception)
            {
                DroneLog.Error("grenade", $"drop failed: {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        // ---------------------------------------------------------------- выстрел по дрону

        public static bool HandleShot(Player shooter, float damage)
        {
            if (shooter is null || shooter.CameraTransform is null)
                return false;

            Vector3 origin = shooter.CameraTransform.position;
            Vector3 dir = shooter.CameraTransform.forward;

            float wallDistance = float.MaxValue;
            if (Physics.Raycast(origin, dir, out RaycastHit wall, 120f, SolidMask, QueryTriggerInteraction.Ignore))
                wallDistance = wall.distance;

            foreach (DroneSession session in sessions.Values)
            {
                if (session.Stage == DroneStage.Preview || session.Owner == shooter)
                    continue;

                if (!RaySphere(origin, dir, session.Position, DroneRadius + 0.15f, out float hitDist))
                    continue;

                if (hitDist > wallDistance)
                    continue;

                session.Health -= damage;
                shooter.ShowHitMarker(1f);
                DroneLog.Step("shot", shooter, $"drone hit for {damage:0.#}, hp {session.Health:0.#}");

                if (session.Health <= 0f)
                    Explode(session, session.Position, session.Stage == DroneStage.Piloting);

                return true;
            }

            return false;
        }

        // ---------------------------------------------------------------- полёт

        private static IEnumerator<float> FlightLoop()
        {
            while (running)
            {
                yield return Timing.WaitForSeconds(FixedStep);

                if (sessions.Count == 0)
                    continue;

                try
                {
                    Tick();
                }
                catch (Exception exception)
                {
                    DroneLog.Error("tick", exception.ToString());
                }
            }
        }

        private static void Tick()
        {
            List<DroneSession>? finished = null;

            foreach (DroneSession session in sessions.Values)
            {
                bool alive = session.Stage switch
                {
                    DroneStage.Preview => TickPreview(session),
                    DroneStage.Piloting => TickPiloting(session),
                    _ => true,
                };

                if (!alive)
                {
                    finished ??= new List<DroneSession>();
                    finished.Add(session);
                }
            }

            if (finished is null)
                return;

            for (int i = 0; i < finished.Count; i++)
                sessions.Remove(finished[i].Owner);
        }

        private static bool TickPreview(DroneSession session)
        {
            Player owner = session.Owner;
            if (owner is null || !owner.IsAlive)
                return false;

            Vector3 spot = ResolvePlacement(owner, out bool canPlace);
            session.Position = spot;
            session.CanPlace = canPlace;
            session.PlacementUpdatedAt = Time.realtimeSinceStartup;
            session.Forward = Flatten(owner.CameraTransform is null ? session.Forward : owner.CameraTransform.forward);
            MoveBody(session);
            SetLight(session, canPlace ? Color.green : Color.red, Config.DroneLightIntensity);

            // === Watchdog: if the schematic body disappeared (ProjectMER despawned it),
            // re-spawn it so the preview stays alive. This is option 2 the user requested.
            bool bodyLost = session.BodyBlocks.Count == 0 ||
                            session.BodyBlocks.TrueForAll(b => b.Transform == null || b.Transform.gameObject == null);

            if (bodyLost)
            {
                DroneLog.Step("watchdog", owner, "schematic body lost, attempting re-spawn");
                DestroyBody(session);

                if (SpawnBody(spot, session.Forward, session, out string err))
                {
                    session.Light = SpawnLight(spot);
                    SetLight(session, canPlace ? Color.green : Color.red, Config.DroneLightIntensity);
                    DroneLog.Step("watchdog", owner, "re-spawned successfully");
                }
                else
                {
                    DroneLog.Warn("watchdog", owner, $"re-spawn failed: {err}");
                    // fall back to primitive if schematic keeps failing
                    if (BuildPrimitiveDrone(spot, Quaternion.LookRotation(session.Forward, Vector3.up), session))
                    {
                        session.Light = SpawnLight(spot);
                        SetLight(session, canPlace ? Color.green : Color.red, Config.DroneLightIntensity);
                    }
                }
            }

            return true;
        }

        private static bool TickPiloting(DroneSession session)
        {
            Player owner = session.Owner;
            if (owner is null || !owner.IsAlive)
            {
                Release(session, "pilot lost");
                session.Stage = DroneStage.Abandoned;
                return true;
            }

            owner.EnableEffect(EffectType.Invisible, 2f, false);

            if (session.SpeedLimit <= 0f)
            {
                owner.Position = CameraPoint(session);
                session.LastPilotPosition = owner.Position;
                MoveBody(session);
                return true;
            }

            Vector3 now = owner.Position;
            Vector3 delta = now - session.LastPilotPosition;
            float step = session.SpeedLimit * FixedStep;

            if (delta.magnitude > step)
            {
                now = session.LastPilotPosition + delta.normalized * step;
                owner.Position = now;
                delta = now - session.LastPilotPosition;
            }

            if (delta.sqrMagnitude > 1e-6f)
            {
                session.Forward = Flatten(owner.CameraTransform is null ? session.Forward : owner.CameraTransform.forward);

                if (SweepHit(session.LastPilotPosition, delta, session, out RaycastHit hit))
                {
                    float speed = delta.magnitude / FixedStep;
                    if (!HandleImpact(session, hit, delta.normalized, speed))
                        return false;

                    owner.Position = CameraPoint(session);
                }
                else
                {
                    session.Position = now - Vector3.up * Config.DroneCameraOffset;
                }
            }

            session.LastPilotPosition = owner.Position;
            MoveBody(session);
            return true;
        }

        // ---------------------------------------------------------------- столкновения

        private static bool SweepHit(Vector3 from, Vector3 delta, DroneSession session, out RaycastHit result)
        {
            result = default;
            float distance = delta.magnitude;
            if (distance <= 1e-5f)
                return false;

            Vector3 dir = delta / distance;
            int count = Physics.SphereCastNonAlloc(from, DroneRadius, dir, HitBuffer, distance, SolidMask, QueryTriggerInteraction.Ignore);

            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                RaycastHit h = HitBuffer[i];
                if (!IsSolid(h.collider, session))
                    continue;

                if (h.distance < best)
                {
                    best = h.distance;
                    result = h;
                    found = true;
                }
            }

            return found;
        }

        private static bool HandleImpact(DroneSession session, RaycastHit hit, Vector3 direction, float speed)
        {
            string target = hit.collider is null ? "<unknown>" : hit.collider.name;
            Vector3 normal = hit.normal;
            Vector3 rest = hit.point + normal * (DroneRadius + WallClearance);

            if (speed >= Config.DroneCrashSpeed)
            {
                DroneLog.Step("impact", session.Owner, $"crash into '{target}' at {speed:0.#} m/s");
                Explode(session, hit.point, session.Stage == DroneStage.Piloting);
                return false;
            }

            session.Health -= speed * CrashDamagePerSpeed;
            session.Position = rest - Vector3.up * Config.DroneCameraOffset;
            DroneLog.Step("impact", session.Owner, $"bumped '{target}' at {speed:0.#} m/s, hp {session.Health:0.#}");

            if (session.Health <= 0f)
            {
                Explode(session, hit.point, session.Stage == DroneStage.Piloting);
                return false;
            }

            return true;
        }

        private static void Explode(DroneSession session, Vector3 point, bool piloted)
        {
            Player owner = session.Owner;

            if (piloted)
                Release(session, "crash");

            try
            {
                ExplosiveGrenade grenade = (ExplosiveGrenade)Item.Create(ItemType.GrenadeHE, owner);
                grenade.SpawnActive(point, owner);
            }
            catch (Exception exception)
            {
                DroneLog.Error("explode", $"grenade spawn failed: {exception.GetType().Name}: {exception.Message}");
            }

            DestructionManager.HandleExplosion(
                point,
                Mathf.Max(0.1f, Config.DroneExplosionRadius),
                Mathf.Max(0f, Config.DroneExplosionDamage),
                Mathf.Max(0f, Config.DroneExplosionForce));

            DestroyBody(session);
            sessions.Remove(owner);
            DroneLog.Step("explode", owner, $"drone destroyed at {Format(point)}");
        }

        private static bool IsSolid(Collider? collider, DroneSession session)
        {
            if (collider is null || collider.isTrigger)
                return false;

            if (session.Body != null && collider.transform.IsChildOf(session.Body.transform))
                return false;

            Player owner = session.Owner;
            if (owner?.GameObject != null && collider.transform.IsChildOf(owner.GameObject.transform))
                return false;

            if (session.Dummy?.GameObject != null && collider.transform.IsChildOf(session.Dummy.GameObject.transform))
                return false;

            return true;
        }

        // ---------------------------------------------------------------- выход

        public static void ReturnControl(Player player, string reason)
        {
            if (player is null || !sessions.TryGetValue(player, out DroneSession session))
                return;

            if (session.Stage == DroneStage.Preview)
            {
                CancelPreview(player, $"return control: {reason}");
                return;
            }

            if (session.Stage != DroneStage.Piloting)
                return;

            Release(session, reason);

            // Дрон остаётся стоять - в него можно зайти повторно. Свет не включаем заново
            // (белый горит только сразу после установки), чтобы не гонять сеть.
            session.Stage = DroneStage.Placed;
        }

        private static void Release(DroneSession session, string reason)
        {
            Player owner = session.Owner;

            if (session.Dummy is not null)
            {
                try { session.Dummy.Destroy(); }
                catch (Exception exception) { DroneLog.Warn("release", owner, $"dummy destroy failed: {exception.Message}"); }
                session.Dummy = null;
            }

            if (owner is null)
                return;

            owner.DisableEffect(EffectType.Invisible);
            owner.IsNoclipEnabled = session.OwnerNoclipEnabled;
            owner.IsNoclipPermitted = session.OwnerNoclipPermitted;

            if (owner.IsAlive)
            {
                if (session.OwnerReturnPosition != Vector3.zero)
                    owner.Position = session.OwnerReturnPosition;

                owner.ClearInventory();
                foreach (ItemType type in session.OwnerItems)
                    owner.AddItem(type);

                owner.AddAmmo(session.OwnerAmmo);

                if (session.OwnerMaxHealth > 0f)
                    owner.MaxHealth = session.OwnerMaxHealth;
                if (session.OwnerHealth > 0f)
                    owner.Health = session.OwnerHealth;

                owner.CustomInfo = session.OwnerCustomInfo;
            }

            DroneLog.Step("release", owner, $"control returned ({reason})");
        }

        public static void Remove(Player player, string reason)
        {
            if (player is null || !sessions.TryGetValue(player, out DroneSession session))
                return;

            Release(session, reason);
            DestroyBody(session);
            sessions.Remove(player);
            DroneLog.Step("remove", player, $"drone removed ({reason})");
        }

        // ---------------------------------------------------------------- тело дрона

        /// <summary>
        /// Создаёт тело дрона: сперва схематик ProjectMER, при неудаче - примитивный
        /// fallback. Заполняет <see cref="DroneSession.Body"/> и <see cref="DroneSession.BodyBlocks"/>.
        /// Возвращает <c>false</c>, если не удалось получить ни одного видимого блока.
        /// </summary>
        private static bool SpawnBody(Vector3 position, Vector3 forward, DroneSession session, out string error)
        {
            error = string.Empty;

            Quaternion rotation = forward.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(forward, Vector3.up)
                : Quaternion.identity;

            // === FORCED TEST MODE (user chose option 2) ===
            // Always attempt the schematic first, regardless of the YAML flag.
            // The watchdog in TickPreview will re-spawn it if ProjectMER despawns it.
            {
                Component? body = MapEditorBridge.SpawnSchematic(Config.DroneSchematicName, position, rotation, out string schematicError);
                if (body != null)
                {
                    CaptureBlocks(body, position, rotation, session);

                    if (session.BodyBlocks.Count > 0)
                    {
                        session.Body = body;
                        return true;
                    }

                    DroneLog.Warn("body", $"schematic '{Config.DroneSchematicName}' had zero AdminToy blocks, using primitive fallback.");
                    try { UnityEngine.Object.Destroy(body.gameObject); }
                    catch { /* empty container, harmless */ }
                }
                else
                {
                    DroneLog.Warn("body", $"schematic '{Config.DroneSchematicName}' not spawned ({schematicError}); using primitive fallback.");
                }
            }

            // Fallback / forced primitive
            if (BuildPrimitiveDrone(position, rotation, session))
                return true;

            error = "primitive fallback failed";
            return false;
        }

        // --- old schematic attempt removed by the forced-primitive edit above ---
        private static bool __SpawnBody_Original_Schematic_Path_Disabled() { return false; }

        private static bool SpawnBody_Original(Vector3 position, Vector3 forward, DroneSession session, out string error)
        {
            error = string.Empty;

            Quaternion rotation = forward.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(forward, Vector3.up)
                : Quaternion.identity;

            Component? body = MapEditorBridge.SpawnSchematic(Config.DroneSchematicName, position, rotation, out string schematicError);
            if (body != null)
            {
                CaptureBlocks(body, position, rotation, session);

                if (session.BodyBlocks.Count > 0)
                {
                    session.Body = body;
                    return true;
                }

                // Схематик есть, но видимых блоков нет - он бесполезен, убираем и падаем в fallback.
                DroneLog.Warn("body", $"schematic '{Config.DroneSchematicName}' had zero AdminToy blocks, using primitive fallback.");
                try { UnityEngine.Object.Destroy(body.gameObject); }
                catch { /* пустой контейнер, не критично */ }
            }
            else
            {
                DroneLog.Warn("body", $"schematic '{Config.DroneSchematicName}' not spawned ({schematicError}); using primitive fallback.");
            }

            // Fallback: собираем дрон из примитивов прямо в плагине.
            if (BuildPrimitiveDrone(position, rotation, session))
                return true;

            error = "primitive fallback failed";
            return false;
        }

        /// <summary>Отвязывает сетевых детей схематика и запоминает их смещение от центра.</summary>
        private static void CaptureBlocks(Component body, Vector3 position, Quaternion rotation, DroneSession session)
        {
            session.BodyBlocks.Clear();
            Transform root = body.transform;
            Quaternion inverse = Quaternion.Inverse(rotation);

            foreach (Collider collider in body.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;

            foreach (AdminToyBase toy in body.GetComponentsInChildren<AdminToyBase>(true))
            {
                Transform child = toy.transform;
                if (child == root)
                    continue;

                Vector3 worldPos = child.position;
                Quaternion worldRot = child.rotation;
                Vector3 worldScale = child.lossyScale;

                if (child.parent != null)
                    child.SetParent(null, true);

                // Дрон постоянно движется и стоит близко к полу/стенам, поэтому
                // PrimitiveCuller по лучу занятости счёл бы его блоки перекрытыми и
                // погасил бы Visible - дрон "пропадал" через 1-3 с. Метка выводит
                // блоки из оптимизации полностью (см. PrimitiveCuller.TagIgnoreFull).
                if (child.gameObject != null && child.name.IndexOf("[IgnoreOptFull]", StringComparison.OrdinalIgnoreCase) < 0)
                    child.name += " [IgnoreOptFull]";

                session.BodyBlocks.Add(new DroneBodyBlock
                {
                    Transform = child,
                    // Смещение хранится в ЛОКАЛЬНЫХ осях дрона: при движении оно снова
                    // поворачивается текущей ротацией. Без inverse получался двойной поворот.
                    Offset = inverse * (worldPos - position),
                    Rotation = inverse * worldRot,
                    Scale = worldScale,
                });

                toy.NetworkIsStatic = false;
                toy.NetworkMovementSmoothing = 60;
            }
        }

        /// <summary>Собирает простой дрон из примитивов (корпус + 4 луча) как fallback.</summary>
        private static bool BuildPrimitiveDrone(Vector3 position, Quaternion rotation, DroneSession session)
        {
            session.BodyBlocks.Clear();

            try
            {
                // Корпус и четыре «луча»: смещения в локальных осях дрона.
                AddPrimitiveBlock(PrimitiveType.Cube, new Vector3(0f, 0f, 0f), new Vector3(0.4f, 0.1f, 0.4f), Color.gray, session);
                AddPrimitiveBlock(PrimitiveType.Cylinder, new Vector3(0.25f, 0f, 0.25f), new Vector3(0.08f, 0.02f, 0.08f), Color.black, session);
                AddPrimitiveBlock(PrimitiveType.Cylinder, new Vector3(-0.25f, 0f, 0.25f), new Vector3(0.08f, 0.02f, 0.08f), Color.black, session);
                AddPrimitiveBlock(PrimitiveType.Cylinder, new Vector3(0.25f, 0f, -0.25f), new Vector3(0.08f, 0.02f, 0.08f), Color.black, session);
                AddPrimitiveBlock(PrimitiveType.Cylinder, new Vector3(-0.25f, 0f, -0.25f), new Vector3(0.08f, 0.02f, 0.08f), Color.black, session);

                session.Body = null; // у примитивного дрона нет единого корня - только блоки

                // Сразу ставим на место.
                MoveLocalBlocks(session, position, rotation);
                return session.BodyBlocks.Count > 0;
            }
            catch (Exception exception)
            {
                DroneLog.Error("body", $"primitive build failed: {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        private static void AddPrimitiveBlock(PrimitiveType type, Vector3 localOffset, Vector3 scale, Color color, DroneSession session)
        {
            // Без коллайдера (NonCollidable): дрон - только визуал, ловить себя лучами он не должен.
            ToyPrimitive primitive = ToyPrimitive.Create(type, PrimitiveFlags.Visible, null, null, scale, true, color);
            Transform primitiveTransform = primitive.Base.transform;

            // Выводим блок из PrimitiveCuller: иначе движущийся у пола дрон гасится
            // как "перекрытый" через 1-3 с (см. PrimitiveCuller.TagIgnoreFull).
            primitiveTransform.name += " [IgnoreOptFull]";

            session.BodyBlocks.Add(new DroneBodyBlock
            {
                Transform = primitiveTransform,
                Offset = localOffset,
                Rotation = Quaternion.identity,
                Scale = scale,
            });
        }

        private static ToyLight? SpawnLight(Vector3 position)
        {
            try
            {
                ToyLight light = ToyLight.Create(position + Vector3.up * 0.3f, null, null, false, Color.green);
                light.IsStatic = false;
                light.MovementSmoothing = 60;
                light.Intensity = Config.DroneLightIntensity;
                light.Range = Config.DroneLightRange;
                light.ShadowType = LightShadows.None;
                light.Spawn();
                return light;
            }
            catch (Exception exception)
            {
                DroneLog.Warn("light", $"spawn failed: {exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        /// <summary>Меняет цвет/интенсивность света только при реальном изменении (не 20 раз/с).</summary>
        private static void SetLight(DroneSession session, Color color, float intensity)
        {
            if (session.Light is not ToyLight light)
                return;

            try
            {
                if (session.LightColor != color)
                {
                    light.Color = color;
                    session.LightColor = color;
                }

                if (!Mathf.Approximately(session.LightIntensity, intensity))
                {
                    light.Intensity = intensity;
                    session.LightIntensity = intensity;
                }
            }
            catch
            {
                // Свет - косметика; сбой не должен ронять полёт.
            }
        }

        /// <summary>
        /// Свет после установки: белый на полную интенсивность <c>DroneLightHoldSeconds</c>,
        /// затем плавное затухание за <c>DroneLightFadeSeconds</c> до нуля.
        /// </summary>
        private static void StartLightSequence(DroneSession session)
        {
            if (session.LightFade.IsRunning)
                Timing.KillCoroutines(session.LightFade);

            session.LightFade = Timing.RunCoroutine(LightSequence(session));
        }

        private static IEnumerator<float> LightSequence(DroneSession session)
        {
            float full = Config.DroneLightIntensity;
            SetLight(session, Color.white, full);

            yield return Timing.WaitForSeconds(Mathf.Max(0f, Config.DroneLightHoldSeconds));

            float fade = Mathf.Max(0.01f, Config.DroneLightFadeSeconds);
            float elapsed = 0f;
            while (elapsed < fade)
            {
                if (session.Light is not ToyLight)
                    yield break;

                elapsed += FixedStep;
                SetLight(session, Color.white, Mathf.Lerp(full, 0f, elapsed / fade));
                yield return Timing.WaitForSeconds(FixedStep);
            }

            SetLight(session, Color.white, 0f);
        }

        private static void MoveBody(DroneSession session)
        {
            Quaternion rotation = session.Forward.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(session.Forward.normalized, Vector3.up)
                : Quaternion.identity;

            if (session.Body != null)
                session.Body.transform.SetPositionAndRotation(session.Position, rotation);

            MoveLocalBlocks(session, session.Position, rotation);

            if (session.Light is ToyLight light)
                light.Position = session.Position + Vector3.up * 0.3f;
        }

        private static void MoveLocalBlocks(DroneSession session, Vector3 position, Quaternion rotation)
        {
            List<DroneBodyBlock> blocks = session.BodyBlocks;
            for (int i = 0; i < blocks.Count; i++)
            {
                DroneBodyBlock block = blocks[i];
                if (block.Transform == null)
                    continue;

                block.Transform.position = position + (rotation * block.Offset);
                block.Transform.rotation = rotation * block.Rotation;
                block.Transform.localScale = block.Scale;
            }
        }

        private static void DestroyBody(DroneSession session)
        {
            if (session.LightFade.IsRunning)
                Timing.KillCoroutines(session.LightFade);

            // Отвязанные блоки - самостоятельные сетевые объекты: их нужно удалить явно,
            // иначе они останутся сиротами (уничтожение корня их не заберёт).
            foreach (DroneBodyBlock block in session.BodyBlocks)
            {
                if (block.Transform == null)
                    continue;

                try { NetworkServer.Destroy(block.Transform.gameObject); }
                catch { /* объект мог уже уйти */ }
            }

            session.BodyBlocks.Clear();

            Component? body = session.Body;
            session.Body = null;
            if (body != null)
            {
                try { UnityEngine.Object.Destroy(body.gameObject); }
                catch (Exception exception) { DroneLog.Warn("body", $"destroy failed: {exception.GetType().Name}: {exception.Message}"); }
            }

            if (session.Light is ToyLight light)
            {
                session.Light = null;
                try { light.Destroy(); }
                catch { /* косметика */ }
            }
        }

        // ---------------------------------------------------------------- геометрия

        /// <summary>
        /// Ищет точку установки лучом вперёд с маской слоёв и посадкой на пол.
        /// <paramref name="canPlace"/> = найден нормальный пол и в точке нет геометрии.
        /// </summary>
        private static Vector3 ResolvePlacement(Player player, out bool canPlace)
        {
            Transform? camera = player.CameraTransform;
            Vector3 origin = camera is not null ? camera.position : player.Position;
            Vector3 direction = Flatten(camera is not null ? camera.forward : Vector3.forward);

            float distance = Mathf.Max(0f, Config.DronePreviewDistance);
            Vector3 target = origin + direction * distance;

            if (Physics.SphereCast(origin, DroneRadius, direction, out RaycastHit forwardHit, distance, SolidMask, QueryTriggerInteraction.Ignore))
                target = origin + direction * Mathf.Max(0f, forwardHit.distance - WallClearance);

            canPlace = false;
            Collider? floorCollider = null;

            if (Physics.Raycast(target + Vector3.up * 0.5f, Vector3.down, out RaycastHit floorHit, 12f, SolidMask, QueryTriggerInteraction.Ignore))
            {
                floorCollider = floorHit.collider;

                // Пол должен быть достаточно горизонтальным, ставим по нормали с запасом.
                if (floorHit.normal.y >= FloorNormalThreshold)
                {
                    target = floorHit.point + floorHit.normal * (DroneRadius + 0.05f);
                    canPlace = !IsBlocked(target, floorCollider);
                }
            }

            return target;
        }

        /// <summary>Есть ли в точке геометрия, кроме самой опорной поверхности.</summary>
        private static bool IsBlocked(Vector3 point, Collider? ignore)
        {
            int count = Physics.OverlapSphereNonAlloc(point, DroneRadius * 0.9f, OverlapBuffer, SolidMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider c = OverlapBuffer[i];
                if (c == null || c == ignore)
                    continue;

                return true;
            }

            return false;
        }

        private static Vector3 CameraPoint(DroneSession session)
            => session.Position + Vector3.up * Config.DroneCameraOffset;

        private static Vector3 Flatten(Vector3 direction)
        {
            direction.y = 0f;
            return direction.sqrMagnitude < 0.0001f ? Vector3.forward : direction.normalized;
        }

        private static bool RaySphere(Vector3 origin, Vector3 dir, Vector3 center, float radius, out float distance)
        {
            distance = 0f;
            Vector3 oc = origin - center;
            float b = Vector3.Dot(oc, dir);
            float c = Vector3.Dot(oc, oc) - radius * radius;
            float disc = b * b - c;
            if (disc < 0f)
                return false;

            float t = -b - Mathf.Sqrt(disc);
            if (t < 0f)
                t = 0f;

            distance = t;
            return true;
        }

        private static int BuildSolidMask()
        {
            int mask = ~0;
            foreach (string layer in new[] { "Hitbox", "Ragdoll", "Pickup", "Ignore Raycast", "InvisibleCollider", "Player" })
            {
                int index = LayerMask.NameToLayer(layer);
                if (index >= 0)
                    mask &= ~(1 << index);
            }

            return mask;
        }

        private static string Format(Vector3 v) => $"({v.x:0.##}, {v.y:0.##}, {v.z:0.##})";
    }
}