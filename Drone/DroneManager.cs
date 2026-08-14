using System;
using System.Collections.Generic;
using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.API.Features.Items;
using Exiled.API.Features.Pickups.Projectiles;
using MainCore.Destruction;
using MainCore.Medical.Visuals;
using MEC;
using PlayerRoles;
using UnityEngine;

namespace MainCore.Drone
{
    /// <summary>
    /// Вся логика FPV-дрона: превью, установка, полёт, столкновения и выход пилота.
    /// </summary>
    /// <remarks>
    /// Почему дрон не Rigidbody.
    ///
    /// Первая версия ракеты RPG уже показала, что физический объект с сетевой
    /// синхронизацией дёргается: клиент интерполирует позицию, а сервер её
    /// перезаписывает. Для камеры пилота это неприемлемо - картинка тряслась бы, как
    /// у брошенной гранаты. Поэтому позиция дрона хранится в
    /// <see cref="DroneSession.Position"/> и раз в кадр считается вручную:
    /// равномерное движение вперёд плюс падение, если тяги нет. Схематик и пилот
    /// просто ставятся в эту точку, никакой физики к ним не применяется.
    ///
    /// Столкновения ищутся сферическим лучом по пройденному за кадр отрезку. При
    /// скорости 25 м/с дрон проходит около 0.4 м за кадр - это больше толщины многих
    /// стен, поэтому проверка по одной точке пропускала бы удары.
    /// </remarks>
    public static class DroneManager
    {
        /// <summary>Радиус тела дрона для поиска столкновений, в метрах.</summary>
        private const float DroneRadius = 0.25f;

        /// <summary>На сколько дрон отводится от стены после несмертельного удара.</summary>
        private const float WallClearance = 0.1f;

        /// <summary>Слои, по которым дрон не разбивается: хитбоксы, трупы, пикапы.</summary>
        private static readonly HashSet<string> IgnoredLayers = new HashSet<string>
        {
            "Hitbox",
            "Ragdoll",
            "Pickup",
            "Ignore Raycast",
            "InvisibleCollider",
        };

        private static readonly Dictionary<Player, DroneSession> sessions = new Dictionary<Player, DroneSession>();

        private static CoroutineHandle flightCoroutine;
        private static bool running;

        private static Config Config => MainCorePlugin.Instance.Config;

        /// <summary>Управляет ли игрок дроном прямо сейчас.</summary>
        public static bool IsPiloting(Player player)
            => player is not null
               && sessions.TryGetValue(player, out DroneSession session)
               && session.Stage == DroneStage.Piloting;

        /// <summary>Показан ли игроку превью дрона, ещё не установленный.</summary>
        public static bool HasPreview(Player player)
            => player is not null
               && sessions.TryGetValue(player, out DroneSession session)
               && session.Stage == DroneStage.Preview;

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

            // Копия списка: выход из дрона меняет саму коллекцию.
            foreach (DroneSession session in new List<DroneSession>(sessions.Values))
                Release(session, "shutdown");

            sessions.Clear();
            DroneLog.Step("stop", "flight loop stopped, all drones released");
        }

        // ---------------------------------------------------------------- превью

        /// <summary>
        /// Показывает схематик дрона перед игроком. Вызывается при взятии контроллера
        /// в руках.
        /// </summary>
        /// <remarks>
        /// Точка установки ищется лучом вперёд: если игрок смотрит в стену, дрон
        /// ставится вплотную к ней, а не за ней. Затем позиция опускается на пол
        /// лучом вниз, чтобы дрон не висел в воздухе.
        ///
        /// Разрешается несколько превью: каждое создаёт отдельную сессию. Игрок
        /// может заспавнить несколько дронов и переключаться между ними.
        /// </remarks>
        public static void ShowPreview(Player player)
        {
            if (player is null || !player.IsAlive)
                return;

            // Уже есть дрон: превью или полёт не пересоздаём (иначе повис бы второй
            // схематик). Заброшенный дрон затираем, чтобы освободить место новому.
            if (sessions.TryGetValue(player, out DroneSession existing))
            {
                if (existing.Stage == DroneStage.Preview || existing.Stage == DroneStage.Piloting)
                    return;

                DestroyBody(existing);
                sessions.Remove(player);
            }

            Vector3 spot = ResolvePlacement(player);

            DroneSession session = new DroneSession(player)
            {
                Stage = DroneStage.Preview,
                Position = spot,
                Forward = Flatten(player.CameraTransform is null ? Vector3.forward : player.CameraTransform.forward),
            };

            session.Body = SpawnBody(spot, session.Forward, out string error);
            if (session.Body is null)
            {
                // Без схематика дрон бесполезен: летать было бы нечему, а игрок не
                // увидел бы, куда он ставит машину.
                DroneLog.Warn("preview", player, $"schematic '{Config.DroneSchematicName}' not spawned: {error}");
                player.ShowHint("<b>FPV Drone:</b> drone model is unavailable, ask an admin.", 3f);
                return;
            }

            sessions[player] = session;
            player.ShowHint("<b>FPV Drone:</b> press the radio key to deploy.", 2f);
            DroneLog.Step("preview", player, $"placed at {Format(spot)}");
        }

        /// <summary>Убирает превью, если игрок убрал контроллер, не установив дрон.</summary>
        public static void CancelPreview(Player player)
        {
            if (player is null || !sessions.TryGetValue(player, out DroneSession session))
                return;

            if (session.Stage != DroneStage.Preview)
                return;

            DestroyBody(session);
            sessions.Remove(player);
            DroneLog.Step("preview", player, "cancelled");
        }

        /// <summary>
        /// Ищет точку установки: 2 м вперёд, но не сквозь стену, и с посадкой на пол.
        /// </summary>
        private static Vector3 ResolvePlacement(Player player)
        {
            Transform? camera = player.CameraTransform;
            Vector3 origin = camera is not null ? camera.position : player.Position;
            Vector3 direction = Flatten(camera is not null ? camera.forward : Vector3.forward);

            float distance = Mathf.Max(0f, Config.DronePreviewDistance);
            Vector3 target = origin + direction * distance;

            // Сферический луч, а не обычный: дрон имеет объём, и точечный луч
            // позволил бы поставить его наполовину внутри стены.
            if (Physics.SphereCast(origin, DroneRadius, direction, out RaycastHit forwardHit, distance))
                target = origin + direction * Mathf.Max(0f, forwardHit.distance - WallClearance);

            // Посадка на пол: дрон должен лежать на земле, а не висеть.
            if (Physics.Raycast(target, Vector3.down, out RaycastHit floorHit, 10f))
                target = floorHit.point + Vector3.up * DroneRadius;

            return target;
        }

        // ---------------------------------------------------------------- вход в дрон

        /// <summary>
        /// Устанавливает дрон и пересаживает игрока в него. Вызывается по ЛКМ с
        /// контроллером в руках.
        /// </summary>
        /// <remarks>
        /// Разрешён вход только в превью (Preview). Заброшенные дроны (Abandoned)
        /// продолжают лететь сами и не могут быть перехвачены повторно - игрок
        /// должен создать новое превью, чтобы заспавнить новый дрон.
        /// </remarks>
        public static void Deploy(Player player)
        {
            if (player is null || !sessions.TryGetValue(player, out DroneSession session))
                return;

            if (session.Stage != DroneStage.Preview)
                return;

            session.Stage = DroneStage.Piloting;
            session.Speed = 0f;
            session.OwnerReturnPosition = player.Position;
            session.OwnerOriginalScale = player.Scale;
            session.OwnerNoclipPermitted = player.IsNoclipPermitted;
            session.OwnerNoclipEnabled = player.IsNoclipEnabled;

            // Даммик занимает место игрока: со стороны тело пилота остаётся на земле.
            session.Dummy = SpawnDummy(player);

            // Пилот уменьшается и переводится в noclip: карта не должна его толкать,
            // иначе позицию дрона перебивал бы обычный контроллер движения игрока.
            player.Scale = Vector3.one * Config.DronePilotScale;
            player.IsNoclipPermitted = true;
            player.IsNoclipEnabled = true;
            player.Position = CameraPoint(session);

            GiveDroneKit(player);

            player.ShowHint("<b>FPV Drone:</b> jump to speed up, alt to slow down. Radio key returns control.", 4f);
            DroneLog.Step("deploy", player, $"drone armed at {Format(session.Position)}");
        }

        /// <summary>
        /// Создаёт копию игрока на его прежнем месте.
        /// </summary>
        /// <remarks>
        /// Копируются роль, ник и здоровье: даммик должен выглядеть в точности как
        /// пилот, чтобы со стороны было видно оператора, сидящего за пультом.
        /// </remarks>
        private static Npc? SpawnDummy(Player player)
        {
            try
            {
                Npc dummy = Npc.Spawn(player.Nickname, player.Role.Type, false, player.Position);

                dummy.Position = player.Position;
                dummy.CustomInfo = player.CustomInfo;
                dummy.MaxHealth = player.MaxHealth;
                dummy.Health = player.Health;

                DroneLog.Step("dummy", player, $"spawned as {player.Role.Type}");
                return dummy;
            }
            catch (Exception exception)
            {
                // Даммик - это косметика. Без него дрон обязан работать дальше.
                DroneLog.Warn("dummy", player, $"spawn failed: {exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        /// <summary>Выдаёт пилоту гранаты и рацию для возврата управления.</summary>
        private static void GiveDroneKit(Player player)
        {
            for (int i = 0; i < Config.DroneGrenadeCount; i++)
                player.AddItem(ItemType.GrenadeHE);

            player.AddItem(ItemType.Radio);
            DroneLog.Step("kit", player, $"{Config.DroneGrenadeCount} grenades and a radio issued");
        }

        // ---------------------------------------------------------------- управление

        /// <summary>Прыжок разгоняет дрон на один шаг скорости.</summary>
        public static void Accelerate(Player player)
        {
            if (!sessions.TryGetValue(player, out DroneSession session) || session.Stage != DroneStage.Piloting)
                return;

            session.Speed = Mathf.Min(Config.DroneMaxSpeed, session.Speed + Config.DroneSpeedStep);

            // Разгон снимает дрон с опоры: пока скорость есть, он летит, а не лежит.
            session.Grounded = false;
            DroneLog.Step("speed", player, $"up to {session.Speed:0.#} m/s");
        }

        /// <summary>Alt тормозит дрон на один шаг скорости, не ниже нуля.</summary>
        public static void Decelerate(Player player)
        {
            if (!sessions.TryGetValue(player, out DroneSession session) || session.Stage != DroneStage.Piloting)
                return;

            session.Speed = Mathf.Max(0f, session.Speed - Config.DroneSpeedStep);
            DroneLog.Step("speed", player, $"down to {session.Speed:0.#} m/s");
        }

        /// <summary>
        /// Сбрасывает гранату из-под дрона, сохраняя его импульс.
        /// </summary>
        /// <returns><c>false</c>, если дрон лежит на земле - тогда бросок не разрешён.</returns>
        public static bool DropGrenade(Player player)
        {
            if (!sessions.TryGetValue(player, out DroneSession session) || session.Stage != DroneStage.Piloting)
                return false;

            // Лежащий дрон гранату не сбрасывает: она оказалась бы внутри пола.
            if (session.Speed <= 0f)
            {
                player.ShowHint("<b>FPV Drone:</b> too low to drop a grenade.", 1.5f);
                DroneLog.Step("grenade", player, "drop refused: drone is not flying");
                return false;
            }

            Vector3 point = session.Position + Vector3.down * Mathf.Max(0f, Config.DroneGrenadeDropOffset);

            try
            {
                ExplosiveGrenade grenade = (ExplosiveGrenade)Item.Create(ItemType.GrenadeHE, player);
                ExplosionGrenadeProjectile projectile = grenade.SpawnActive(point, player);

                // Момент дрона передаётся гранате: сброшенная на ходу, она летит
                // вперёд вместе с машиной, а не падает строго вниз.
                Rigidbody? body = projectile.GameObject?.GetComponent<Rigidbody>();
                if (body is not null)
                    body.velocity = session.Forward * session.Speed;

                DroneLog.Step("grenade", player, $"dropped at {Format(point)} with {session.Speed:0.#} m/s");
                return true;
            }
            catch (Exception exception)
            {
                DroneLog.Error("grenade", $"drop failed: {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        // ---------------------------------------------------------------- полёт

        private static IEnumerator<float> FlightLoop()
        {
            while (running)
            {
                yield return Timing.WaitForOneFrame;

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
                // Превью не летает: оно просто следует за взглядом игрока.
                if (session.Stage == DroneStage.Preview)
                {
                    UpdatePreview(session);
                    continue;
                }

                if (!Advance(session))
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

        private static void UpdatePreview(DroneSession session)
        {
            Player owner = session.Owner;
            if (owner is null || !owner.IsAlive)
                return;

            session.Position = ResolvePlacement(owner);
            session.Forward = Flatten(owner.CameraTransform is null ? session.Forward : owner.CameraTransform.forward);
            MoveBody(session);
        }

        /// <summary>
        /// Двигает дрон на один кадр.
        /// </summary>
        /// <returns><c>false</c>, если дрон прекратил существование.</returns>
        private static bool Advance(DroneSession session)
        {
            Player owner = session.Owner;
            bool piloted = session.Stage == DroneStage.Piloting;

            // Пилот вышел из игры или умер - дрон продолжает лететь сам, как и задумано.
            if (piloted && (owner is null || !owner.IsAlive))
            {
                DroneLog.Step("flight", owner, "pilot lost, drone keeps flying on its own");
                Release(session, "pilot lost");
                session.Stage = DroneStage.Abandoned;
                piloted = false;
            }

            if (piloted && owner!.CameraTransform is not null)
                session.Forward = owner.CameraTransform.forward;

            // Дрон лежит на опоре и тяги нет - он неподвижен. Никакой гравитации,
            // иначе он падал бы в пол, отталкивался и бесконечно дрожал вверх-вниз.
            // Просто держим пилота на месте.
            if (session.Grounded && session.Speed <= 0f)
            {
                Settle(session, piloted);
                return true;
            }

            Vector3 from = session.Position;

            // Тяга вперёд плюс падение: дрон без скорости снижается, пока не найдёт
            // опору. Как только он её касается, HandleImpact ставит Grounded, и ветка
            // выше замораживает его на месте.
            Vector3 motion = session.Forward.normalized * session.Speed;
            if (session.Speed <= 0f)
                motion += Vector3.down * Config.DroneFallSpeed;

            Vector3 delta = motion * Time.deltaTime;
            if (delta.sqrMagnitude <= 0.0000001f)
            {
                Settle(session, piloted);
                return true;
            }

            float distance = delta.magnitude;
            Vector3 direction = delta / distance;

            if (Physics.SphereCast(from, DroneRadius, direction, out RaycastHit hit, distance)
                && IsSolid(hit.collider, session))
            {
                return HandleImpact(session, hit, direction, piloted);
            }

            session.Position = from + delta;
            Settle(session, piloted);
            return true;
        }

        /// <summary>Переносит схематик и пилота в текущую точку дрона.</summary>
        private static void Settle(DroneSession session, bool piloted)
        {
            MoveBody(session);

            if (!piloted)
                return;

            Player owner = session.Owner;

            // Пилот прижат к дрону каждый кадр. Телепорт, а не физика: только так
            // камера остаётся ровной и не отстаёт от машины.
            owner.Position = CameraPoint(session);
        }

        private static Vector3 CameraPoint(DroneSession session)
            => session.Position + Vector3.up * Config.DroneCameraOffset;

        // ---------------------------------------------------------------- столкновения

        /// <summary>Косинус угла, выше которого поверхность считается полом/потолком.</summary>
        private const float FloorNormalThreshold = 0.5f;

        /// <summary>Минимальная скорость тяги, при которой удар в стену даёт отскок.</summary>
        private const float MinBounceSpeed = 0.2f;

        /// <summary>
        /// Обрабатывает удар. Пол и потолок только останавливают дрон (без отскока),
        /// стена на большой скорости разбивает его, стена на средней - отбрасывает.
        /// </summary>
        /// <returns><c>false</c>, если дрон уничтожен.</returns>
        /// <remarks>
        /// Раньше любой контакт вызывал отскок на 2 м. Из-за гравитации дрон постоянно
        /// падал на пол, отскакивал вверх, снова падал - камера дёргалась вверх-вниз.
        /// Теперь удар о пол/потолок просто прижимает дрон к поверхности снаружи
        /// (по нормали столкновения), поэтому он спокойно лежит на земле. Отскок
        /// остался только для вертикальных стен, как и задумано.
        ///
        /// Позиция всегда выносится наружу поверхности через <c>hit.normal</c>, так что
        /// дрон - и камера пилота вместе с ним - не проваливается под карту, даже если
        /// пилот смотрел резко вниз.
        /// </remarks>
        private static bool HandleImpact(DroneSession session, RaycastHit hit, Vector3 direction, bool piloted)
        {
            float speed = session.Speed;
            string target = hit.collider is null ? "<unknown>" : hit.collider.name;

            Vector3 normal = hit.normal;
            bool wallLike = Mathf.Abs(normal.y) <= FloorNormalThreshold;

            // Всегда выносим дрон на радиус наружу поверхности: это гарантирует, что
            // он не окажется внутри геометрии или под полом.
            Vector3 rest = hit.point + normal * (DroneRadius + WallClearance);

            // Настоящий краш - только лобовой удар в стену на большой скорости.
            // Падение на пол под действием гравитации крашем не считается.
            if (wallLike && speed >= Config.DroneCrashSpeed)
            {
                DroneLog.Step("impact", session.Owner, $"crash into '{target}' at {speed:0.#} m/s");
                Explode(session, hit.point, piloted);
                return false;
            }

            // Пол, потолок или мягкое касание: прижимаемся снаружи, гасим скорость и
            // помечаем дрон как лежащий. Пока стоит флаг Grounded, гравитация к нему
            // не применяется - это и убирает дрожание "упал - оттолкнулся - упал".
            if (!wallLike || speed < MinBounceSpeed)
            {
                session.Position = rest;
                session.Speed = 0f;

                // Полом считается только опора снизу (нормаль вверх). Потолок скорость
                // гасит, но лежать на нём нельзя - иначе дрон завис бы под потолком.
                session.Grounded = normal.y > FloorNormalThreshold;

                Settle(session, piloted);
                DroneLog.Step("impact", session.Owner, $"clamped to '{target}' (normal.y {normal.y:0.##}) at {speed:0.#} m/s");
                return true;
            }

            // Средний удар в стену: отбрасываем назад вдоль нормали (от стены),
            // гасим скорость. Дрон снова разгоняется прыжками.
            session.Position = rest + normal * Mathf.Max(0f, Config.DroneBounceDistance);
            session.Speed = 0f;
            Settle(session, piloted);

            DroneLog.Step("impact", session.Owner, $"bounced off '{target}' at {speed:0.#} m/s");
            return true;
        }

        /// <summary>
        /// Уничтожает дрон взрывом: обычная граната по игрокам плюс отдельный
        /// разрушающий импульс по разрушаемым объектам.
        /// </summary>
        private static void Explode(DroneSession session, Vector3 point, bool piloted)
        {
            Player owner = session.Owner;

            // Пилот покидает дрон до взрыва: иначе он остался бы уменьшенным в
            // noclip внутри огненного шара.
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

            // Ванильный взрыв гранаты почти не двигает разрушаемые объекты, поэтому
            // импульс по ним задаётся отдельно - как у ракеты RPG.
            DestructionManager.HandleExplosion(
                point,
                Mathf.Max(0.1f, Config.DroneExplosionRadius),
                Mathf.Max(0f, Config.DroneExplosionDamage),
                Mathf.Max(0f, Config.DroneExplosionForce));

            DestroyBody(session);
            DroneLog.Step("explode", owner, $"drone destroyed at {Format(point)}");
        }

        /// <summary>
        /// Считается ли попадание твёрдым. Отсекает сам дрон, пилота, его даммика и
        /// вспомогательные коллайдеры.
        /// </summary>
        private static bool IsSolid(Collider? collider, DroneSession session)
        {
            if (collider is null)
                return false;

            if (collider.isTrigger)
                return false;

            if (session.Body is not null && collider.transform.IsChildOf(session.Body.transform))
                return false;

            Player owner = session.Owner;
            if (owner?.GameObject is not null && collider.transform.IsChildOf(owner.GameObject.transform))
                return false;

            if (session.Dummy?.GameObject is not null && collider.transform.IsChildOf(session.Dummy.GameObject.transform))
                return false;

            string layer = LayerMask.LayerToName(collider.gameObject.layer);
            if (!string.IsNullOrEmpty(layer) && IgnoredLayers.Contains(layer))
                return false;

            return true;
        }

        // ---------------------------------------------------------------- выход

        /// <summary>
        /// Возвращает игроку управление собой. Вызывается рацией, при уроне, смерти и
        /// выключении плагина.
        /// </summary>
        /// <remarks>
        /// После выхода дрон переходит в состояние <see cref="DroneStage.Abandoned"/>
        /// и продолжает лететь сам. Игрок может снова взять превью и войти в него,
        /// используя <see cref="Deploy"/>, который проверяет, что сессия в состоянии
        /// Preview (не Piloting).
        /// </remarks>
        public static void ReturnControl(Player player, string reason)
        {
            if (player is null || !sessions.TryGetValue(player, out DroneSession session))
                return;

            if (session.Stage == DroneStage.Preview)
            {
                CancelPreview(player);
                return;
            }

            Release(session, reason);

            // Дрон продолжает жить: он летит с той же скоростью, что и до выхода.
            session.Stage = DroneStage.Abandoned;
        }

        /// <summary>
        /// Снимает с игрока всё, что связано с полётом: масштаб, noclip, позицию и
        /// даммика. Сам дрон не трогает.
        /// </summary>
        private static void Release(DroneSession session, string reason)
        {
            Player owner = session.Owner;

            if (session.Dummy is not null)
            {
                try
                {
                    session.Dummy.Destroy();
                }
                catch (Exception exception)
                {
                    DroneLog.Warn("release", owner, $"dummy destroy failed: {exception.Message}");
                }

                session.Dummy = null;
            }

            if (owner is null)
                return;

            owner.Scale = session.OwnerOriginalScale == Vector3.zero ? Vector3.one : session.OwnerOriginalScale;
            owner.IsNoclipEnabled = session.OwnerNoclipEnabled;
            owner.IsNoclipPermitted = session.OwnerNoclipPermitted;

            // Живого игрока возвращаем на его прежнее место: он всё это время
            // "стоял" там в виде даммика.
            if (owner.IsAlive && session.OwnerReturnPosition != Vector3.zero)
                owner.Position = session.OwnerReturnPosition;

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

        // ---------------------------------------------------------------- схематик

        private static Component? SpawnBody(Vector3 position, Vector3 forward, out string error)
        {
            Quaternion rotation = forward.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(forward, Vector3.up)
                : Quaternion.identity;

            Component? body = MapEditorBridge.SpawnSchematic(Config.DroneSchematicName, position, rotation, out error);

            // Схематик у дрона только визуальный. Его коллайдеры обязательно
            // выключаются: иначе лучи поиска места установки и столкновений
            // (SphereCast/Raycast) попадали бы в сам дрон, он "видел" бы себя и
            // бесконечно дёргался туда-сюда. Это и есть настоящая причина тряски.
            if (body is not null)
            {
                foreach (Collider collider in body.GetComponentsInChildren<Collider>(true))
                    collider.enabled = false;
            }

            return body;
        }

        private static void MoveBody(DroneSession session)
        {
            Component? body = session.Body;
            if (body == null)
                return;

            Transform t = body.transform;
            t.position = session.Position;

            if (session.Forward.sqrMagnitude > 0.0001f)
                t.rotation = Quaternion.LookRotation(session.Forward.normalized, Vector3.up);
        }

        private static void DestroyBody(DroneSession session)
        {
            Component? body = session.Body;
            session.Body = null;

            if (body == null)
                return;

            try
            {
                UnityEngine.Object.Destroy(body.gameObject);
            }
            catch (Exception exception)
            {
                DroneLog.Warn("body", $"destroy failed: {exception.GetType().Name}: {exception.Message}");
            }
        }

        // ---------------------------------------------------------------- прочее

        /// <summary>Убирает вертикальную составляющую: дрон ставится ровно, без наклона.</summary>
        private static Vector3 Flatten(Vector3 direction)
        {
            direction.y = 0f;
            return direction.sqrMagnitude < 0.0001f ? Vector3.forward : direction.normalized;
        }

        private static string Format(Vector3 v) => $"({v.x:0.##}, {v.y:0.##}, {v.z:0.##})";
    }
}