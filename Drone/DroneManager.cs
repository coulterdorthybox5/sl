using System;
using System.Collections.Generic;
using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.API.Features.Items;
using Exiled.API.Features.Pickups.Projectiles;
using AdminToys;
using MainCore.Destruction;
using MainCore.Medical.Visuals;
using MEC;
using UnityEngine;
using ToyLight = Exiled.API.Features.Toys.Light;

namespace MainCore.Drone
{
    /// <summary>
    /// Вся логика FPV-дрона: превью, установка, полёт, столкновения, HP и выход пилота.
    /// </summary>
    /// <remarks>
    /// Ключевое решение - схема «дрон следует за пилотом».
    ///
    /// Пилота переводят в noclip, и он летает сам: клиент двигает его по WASD и взгляду,
    /// сервер в его позицию НЕ вмешивается. Раньше было наоборот - сервер каждый кадр
    /// телепортировал игрока в точку дрона, и это дралось с клиентским noclip'ом, давая
    /// постоянное дрожание «туда-сюда». Теперь сервер только ведёт модель дрона за
    /// фактической позицией игрока и ограничивает максимальную скорость, изредка мягко
    /// подтягивая пилота назад при превышении. Дрожания нет, WASD работает нативно.
    ///
    /// Невидимость - эффект <see cref="EffectType.Invisible"/>, а не масштаб: смена
    /// <c>Player.Scale</c> пересоздаёт модель игрока у всех клиентов и сбрасывает камеру.
    /// </remarks>
    public static class DroneManager
    {
        /// <summary>Радиус тела дрона для поиска столкновений, в метрах.</summary>
        private const float DroneRadius = 0.25f;

        /// <summary>Отступ от поверхности после столкновения.</summary>
        private const float WallClearance = 0.15f;

        /// <summary>Урон дрону за 1 м/с скорости удара (ТЗ: 3 HP за 1 м/с).</summary>
        private const float CrashDamagePerSpeed = 3f;

        /// <summary>Косинус, выше которого поверхность считается полом/потолком.</summary>
        private const float FloorNormalThreshold = 0.5f;

        /// <summary>Фиксированный шаг симуляции: скорость дрона не должна зависеть от FPS.</summary>
        private const float FixedStep = 0.05f;

        /// <summary>Слои, которые не считаются препятствием (хитбоксы, трупы, пикапы).</summary>
        private static readonly int SolidMask = BuildSolidMask();

        private static readonly RaycastHit[] HitBuffer = new RaycastHit[16];

        private static readonly Dictionary<Player, DroneSession> sessions = new Dictionary<Player, DroneSession>();

        private static CoroutineHandle flightCoroutine;
        private static bool running;

        private static Config Config => MainCorePlugin.Instance.Config;

        public static bool IsPiloting(Player player)
            => player is not null
               && sessions.TryGetValue(player, out DroneSession s)
               && s.Stage == DroneStage.Piloting;

        public static bool HasPreview(Player player)
            => player is not null
               && sessions.TryGetValue(player, out DroneSession s)
               && s.Stage == DroneStage.Preview;

        public static bool HasPlaced(Player player)
            => player is not null
               && sessions.TryGetValue(player, out DroneSession s)
               && s.Stage == DroneStage.Placed;

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

        /// <summary>Показывает превью дрона перед игроком, если у него ещё нет дрона.</summary>
        public static void ShowPreview(Player player)
        {
            if (player is null || !player.IsAlive)
                return;

            // Уже есть превью/поставленный/полёт - второй не создаём.
            if (sessions.TryGetValue(player, out DroneSession existing))
            {
                if (existing.Stage != DroneStage.Abandoned)
                    return;

                // Заброшенный дрон убираем, освобождая место под новый.
                DestroyBody(existing);
                sessions.Remove(player);
            }

            Vector3 spot = ResolvePlacement(player, out _);

            DroneSession session = new DroneSession(player)
            {
                Stage = DroneStage.Preview,
                Position = spot,
                Forward = Flatten(player.CameraTransform is null ? Vector3.forward : player.CameraTransform.forward),
                Health = Mathf.Max(1f, Config.DroneHealth),
            };

            session.Body = SpawnBody(spot, session.Forward, session, out string error);
            if (session.Body is null)
            {
                DroneLog.Warn("preview", player, $"schematic '{Config.DroneSchematicName}' not spawned: {error}");
                player.ShowHint("<b>FPV Drone:</b> drone model is unavailable, ask an admin.", 3f);
                return;
            }

            session.Light = SpawnLight(spot);
            sessions[player] = session;
            DroneLog.Step("preview", player, $"placed at {Format(spot)}");
        }

        /// <summary>Убирает превью, если игрок убрал контроллер, не поставив дрон.</summary>
        public static void CancelPreview(Player player)
        {
            if (player is null || !sessions.TryGetValue(player, out DroneSession session))
                return;

            // Убираем только не установленный дрон. Поставленный/летящий остаётся.
            if (session.Stage != DroneStage.Preview)
                return;

            DestroyBody(session);
            sessions.Remove(player);
            DroneLog.Step("preview", player, "cancelled");
        }

        /// <summary>
        /// Фиксирует превью на земле (Preview -> Placed). Свет становится белым.
        /// Возвращает <c>false</c>, если поставить нельзя (нет опоры/внутри стены).
        /// </summary>
        public static bool Place(Player player)
        {
            if (player is null || !sessions.TryGetValue(player, out DroneSession session))
                return false;

            if (session.Stage != DroneStage.Preview)
                return false;

            Vector3 spot = ResolvePlacement(player, out bool canPlace);
            if (!canPlace)
            {
                player.ShowHint("<b>FPV Drone:</b> no room to place here.", 1.5f);
                return false;
            }

            session.Position = spot;
            session.Stage = DroneStage.Placed;
            MoveBody(session);
            SetLight(session, Color.white, Config.DroneLightIntensity);
            player.ShowHint("<b>FPV Drone:</b> press again to take control.", 2f);
            DroneLog.Step("place", player, $"placed on ground at {Format(spot)}");
            return true;
        }

        // ---------------------------------------------------------------- вход

        /// <summary>Пересаживает игрока в дрон (Placed -> Piloting).</summary>
        public static void Deploy(Player player)
        {
            if (player is null || !sessions.TryGetValue(player, out DroneSession session))
                return;

            if (session.Stage != DroneStage.Placed)
                return;

            // Снимок игрока: вернём всё как было при выходе.
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

            // Даммик занимает место игрока: тело оператора остаётся на земле.
            session.Dummy = SpawnDummy(player);

            // Пилот телепортируется к дрону, летает noclip'ом, становится невидимым.
            // Масштаб НЕ трогаем: это пересоздавало бы модель и дёргало камеру.
            player.IsNoclipPermitted = true;
            player.IsNoclipEnabled = true;
            player.Position = CameraPoint(session);
            player.EnableEffect(EffectType.Invisible, 0f, false);

            session.LastPilotPosition = player.Position;
            session.SpeedLimit = 0f;
            session.Stage = DroneStage.Piloting;

            GiveDroneKit(player);
            SetLight(session, Color.white, Config.DroneLightIntensity);

            player.ShowHint("<b>FPV Drone:</b> fly with WASD. Jump/Alt change max speed. Radio to exit.", 4f);
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

        /// <summary>Выдаёт пилоту чистый набор: гранаты и рацию, очищая инвентарь.</summary>
        private static void GiveDroneKit(Player player)
        {
            player.ClearInventory();

            for (int i = 0; i < Config.DroneGrenadeCount; i++)
                player.AddItem(ItemType.GrenadeHE);

            player.AddItem(ItemType.Radio);
            DroneLog.Step("kit", player, $"{Config.DroneGrenadeCount} grenades and a radio issued");
        }

        // ---------------------------------------------------------------- управление

        /// <summary>Прыжок повышает предел скорости на один шаг.</summary>
        public static void Accelerate(Player player)
        {
            if (!sessions.TryGetValue(player, out DroneSession session) || session.Stage != DroneStage.Piloting)
                return;

            session.SpeedLimit = Mathf.Min(Config.DroneMaxSpeed, session.SpeedLimit + Config.DroneSpeedStep);
            DroneLog.Step("speed", player, $"limit up to {session.SpeedLimit:0.#} m/s");
        }

        /// <summary>Alt понижает предел скорости на один шаг, не ниже нуля.</summary>
        public static void Decelerate(Player player)
        {
            if (!sessions.TryGetValue(player, out DroneSession session) || session.Stage != DroneStage.Piloting)
                return;

            session.SpeedLimit = Mathf.Max(0f, session.SpeedLimit - Config.DroneSpeedStep);
            DroneLog.Step("speed", player, $"limit down to {session.SpeedLimit:0.#} m/s");
        }

        /// <summary>Сбрасывает гранату под дрон.</summary>
        public static bool DropGrenade(Player player)
        {
            if (!sessions.TryGetValue(player, out DroneSession session) || session.Stage != DroneStage.Piloting)
                return false;

            Vector3 point = session.Position + Vector3.down * Mathf.Max(0f, Config.DroneGrenadeDropOffset);

            // Если под дроном сразу пол - кладём чуть выше него, чтобы граната не утонула.
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

        /// <summary>
        /// Обрабатывает выстрел игрока: если луч проходит близко к дрону - наносит урон.
        /// Коллайдеры схематика выключены (иначе дрон «видит» себя), поэтому попадание
        /// определяется геометрически по лучу взгляда стрелка.
        /// </summary>
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
                if (session.Stage != DroneStage.Piloting && session.Stage != DroneStage.Abandoned && session.Stage != DroneStage.Placed)
                    continue;

                if (session.Owner == shooter)
                    continue;

                if (!RaySphere(origin, dir, session.Position, DroneRadius + 0.15f, out float hitDist))
                    continue;

                // Стена ближе дрона - пуля не долетела.
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
                    DroneStage.Placed => true,
                    DroneStage.Piloting => TickPiloting(session),
                    DroneStage.Abandoned => true,
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
            session.Forward = Flatten(owner.CameraTransform is null ? session.Forward : owner.CameraTransform.forward);
            MoveBody(session);
            SetLight(session, canPlace ? Color.green : Color.red, Config.DroneLightIntensity);
            return true;
        }

        /// <summary>
        /// Дрон следует за пилотом. Пилот летит noclip'ом сам; сервер вычисляет
        /// пройденный за тик отрезок, ограничивает его максимальной скоростью,
        /// проверяет столкновения и ведёт модель за игроком.
        /// </summary>
        /// <returns><c>false</c>, если дрон уничтожен.</returns>
        private static bool TickPiloting(DroneSession session)
        {
            Player owner = session.Owner;
            if (owner is null || !owner.IsAlive)
            {
                Release(session, "pilot lost");
                session.Stage = DroneStage.Abandoned;
                return true;
            }

            // Держим невидимость: эффект продлевается, пока пилот в дроне.
            owner.EnableEffect(EffectType.Invisible, 2f, false);

            Vector3 now = owner.Position;
            Vector3 delta = now - session.LastPilotPosition;
            float step = Mathf.Max(0.01f, session.SpeedLimit) * FixedStep;

            // Ограничитель максимальной скорости: если игрок ушёл дальше допустимого,
            // мягко подтягиваем его назад. Коррекция редкая и визуально незаметная.
            if (session.SpeedLimit <= 0f)
            {
                // Предел 0 - дрон стоит: держим пилота на месте дрона.
                owner.Position = CameraPoint(session);
                session.LastPilotPosition = owner.Position;
                MoveBody(session);
                return true;
            }

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

                    // После удара пилота ставим к (возможно скорректированной) позиции дрона.
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

        /// <summary>
        /// Свип по пройденному отрезку. Возвращает первое твёрдое препятствие,
        /// пропуская хитбоксы, трупы, пикапы, сам дрон и пилота.
        /// </summary>
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

        /// <summary>
        /// Обработка удара. 6+ м/с - взрыв. Иначе дрон теряет HP (3 за 1 м/с),
        /// отскакивает вдоль нормали и гасит часть скорости.
        /// </summary>
        /// <returns><c>false</c>, если дрон уничтожен.</returns>
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

        /// <summary>
        /// Возвращает игроку управление собой и переводит дрон в Placed (можно зайти
        /// снова). Вызывается рацией, при уроне, смерти, выходе игрока.
        /// </summary>
        public static void ReturnControl(Player player, string reason)
        {
            if (player is null || !sessions.TryGetValue(player, out DroneSession session))
                return;

            if (session.Stage == DroneStage.Preview)
            {
                CancelPreview(player);
                return;
            }

            if (session.Stage != DroneStage.Piloting)
                return;

            Release(session, reason);

            // Дрон остаётся стоять на месте выхода - в него можно зайти повторно.
            session.Stage = DroneStage.Placed;
            SetLight(session, Color.white, Config.DroneLightIntensity);
        }

        /// <summary>Снимает с игрока полётное состояние и возвращает снимок инвентаря.</summary>
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

                // Возвращаем инвентарь/патроны/HP/cinfo как было до входа.
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

        /// <summary>Полностью убирает дрон и запись о нём.</summary>
        public static void Remove(Player player, string reason)
        {
            if (player is null || !sessions.TryGetValue(player, out DroneSession session))
                return;

            Release(session, reason);
            DestroyBody(session);
            sessions.Remove(player);
            DroneLog.Step("remove", player, $"drone removed ({reason})");
        }

        // ---------------------------------------------------------------- схематик и свет

        private static Component? SpawnBody(Vector3 position, Vector3 forward, DroneSession session, out string error)
        {
            Quaternion rotation = forward.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(forward, Vector3.up)
                : Quaternion.identity;

            Component? body = MapEditorBridge.SpawnSchematic(Config.DroneSchematicName, position, rotation, out error);
            if (body == null)
                return null;

            // Коллайдеры схематика выключаем: дрон только визуальный. Иначе лучи поиска
            // места и столкновений попадали бы в сам дрон, и он бы дёргался/ловил себя.
            foreach (Collider collider in body.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;

            // Захватываем сетевых детей и отвязываем их от корня. AdminToyBase публикует
            // ЛОКАЛЬНЫЕ координаты, а иерархию клиент узнаёт из ненадёжного RpcChangeParent.
            // Без родителя local == world, и SyncVar сразу несёт верную мировую позицию -
            // тот же приём, что и в BoneFollower. Иначе модель у клиента застревала бы в
            // точке спавна, хотя на сервере она движется.
            session.BodyBlocks.Clear();
            Transform root = body.transform;
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

                session.BodyBlocks.Add(new DroneBodyBlock
                {
                    Transform = child,
                    Offset = worldPos - position,
                    Rotation = Quaternion.Inverse(rotation) * worldRot,
                    Scale = worldScale,
                });

                toy.NetworkIsStatic = false;
                toy.NetworkMovementSmoothing = 60;
            }

            return body;
        }

        private static ToyLight? SpawnLight(Vector3 position)
        {
            try
            {
                ToyLight light = ToyLight.Create(position + Vector3.up * 0.3f, null, null, true, Color.green);
                light.Intensity = Config.DroneLightIntensity;
                light.Range = Config.DroneLightRange;
                light.ShadowType = LightShadows.None;
                return light;
            }
            catch (Exception exception)
            {
                DroneLog.Warn("light", $"spawn failed: {exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        private static void SetLight(DroneSession session, Color color, float intensity)
        {
            if (session.Light is not ToyLight light)
                return;

            try
            {
                light.Color = color;
                light.Intensity = intensity;
                if (light.Base != null)
                    light.Base.transform.position = session.Position + Vector3.up * 0.3f;
            }
            catch
            {
                // Свет - косметика; сбой не должен ронять полёт.
            }
        }

        private static void MoveBody(DroneSession session)
        {
            Quaternion rotation = session.Forward.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(session.Forward.normalized, Vector3.up)
                : Quaternion.identity;

            Component? body = session.Body;
            if (body != null)
                body.transform.SetPositionAndRotation(session.Position, rotation);

            // Двигаем каждого отвязанного ребёнка в мировых координатах: только так
            // клиент увидит перемещение (см. комментарий в SpawnBody).
            List<DroneBodyBlock> blocks = session.BodyBlocks;
            for (int i = 0; i < blocks.Count; i++)
            {
                DroneBodyBlock block = blocks[i];
                if (block.Transform == null)
                    continue;

                block.Transform.position = session.Position + (rotation * block.Offset);
                block.Transform.rotation = rotation * block.Rotation;
                block.Transform.localScale = block.Scale;
            }

            if (session.Light is ToyLight light && light.Base != null)
                light.Base.transform.position = session.Position + Vector3.up * 0.3f;
        }

        private static void DestroyBody(DroneSession session)
        {
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

        /// <summary>Ищет точку установки лучом вперёд с маской слоёв и посадкой на пол.</summary>
        private static Vector3 ResolvePlacement(Player player, out bool canPlace)
        {
            Transform? camera = player.CameraTransform;
            Vector3 origin = camera is not null ? camera.position : player.Position;
            Vector3 direction = Flatten(camera is not null ? camera.forward : Vector3.forward);

            float distance = Mathf.Max(0f, Config.DronePreviewDistance);
            Vector3 target = origin + direction * distance;

            // Луч вперёд с маской: не попадаем в самого игрока (его хитбокс не в маске).
            if (Physics.SphereCast(origin, DroneRadius, direction, out RaycastHit forwardHit, distance, SolidMask, QueryTriggerInteraction.Ignore))
                target = origin + direction * Mathf.Max(0f, forwardHit.distance - WallClearance);

            // Посадка на пол.
            if (Physics.Raycast(target, Vector3.down, out RaycastHit floorHit, 10f, SolidMask, QueryTriggerInteraction.Ignore))
                target = floorHit.point + Vector3.up * DroneRadius;

            // Ставить можно, если точка не внутри геометрии.
            canPlace = !Physics.CheckSphere(target, DroneRadius * 0.9f, SolidMask, QueryTriggerInteraction.Ignore);
            return target;
        }

        private static Vector3 CameraPoint(DroneSession session)
            => session.Position + Vector3.up * Config.DroneCameraOffset;

        private static Vector3 Flatten(Vector3 direction)
        {
            direction.y = 0f;
            return direction.sqrMagnitude < 0.0001f ? Vector3.forward : direction.normalized;
        }

        /// <summary>Пересекает ли луч сферу; <paramref name="distance"/> - до точки входа.</summary>
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

        /// <summary>Маска слоёв столкновений: всё, кроме хитбоксов/рэгдоллов/пикапов.</summary>
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