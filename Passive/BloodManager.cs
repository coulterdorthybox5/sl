using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.Events.EventArgs.Player;
using MainCore.Medical;
using PlayerStatsSystem;
using UnityEngine;

namespace MainCore.Passive
{
    /// <summary>
    /// Пассивная РП-система: симуляция потери крови. При получении урона игроком,
    /// у которого активно кровотечение (наша медсистема ИЛИ ванильный Bleeding),
    /// на ближайшей поверхности (пол или стена, в ~1 м от места попадания)
    /// спавнится ВАНИЛЬНАЯ кровь через <see cref="Map.PlaceBlood"/>.
    /// </summary>
    /// <remarks>
    /// Правила согласованы с ТЗ:
    /// - за каждые 5 ХП урона - 1 капля крови, максимум 3 за одно попадание;
    /// - направление зависит от хитбокса (правое плечо -> кровь правее и т.д.);
    /// - луч ищет поверхность в радиусе ~1 м: если игрок прижат плечом к стене -
    ///   кровь ложится на стену, иначе падает на пол;
    /// - глобальный лимит 500 капель на раунд, чтобы не грузить сеть.
    /// Никакого HUD/хинтов система не показывает.
    /// </remarks>
    public static class BloodManager
    {
        /// <summary>Глобальный лимит капель за раунд.</summary>
        private const int MaxTotalBlood = 500;

        /// <summary>ХП урона на одну каплю крови.</summary>
        private const float DamagePerBlood = 5f;

        /// <summary>Максимум капель за одно попадание.</summary>
        private const int MaxBloodPerHit = 3;

        /// <summary>Как далеко ищем поверхность (пол/стену) от места попадания, метры.</summary>
        private const float SurfaceSearchDistance = 1.1f;

        private static int spawnedBlood;

        public static void Subscribe()
            => Exiled.Events.Handlers.Player.Hurting += OnHurting;

        public static void Unsubscribe()
            => Exiled.Events.Handlers.Player.Hurting -= OnHurting;

        /// <summary>Сброс счётчика в начале раунда.</summary>
        public static void Reset() => spawnedBlood = 0;

        private static void OnHurting(HurtingEventArgs ev)
        {
            Player player = ev.Player;
            if (player is null || !ev.IsAllowed)
                return;

            if (!HasActiveBleeding(player))
                return;

            float damage = ev.Amount;
            if (damage < DamagePerBlood)
                return;

            int drops = Mathf.Clamp(Mathf.FloorToInt(damage / DamagePerBlood), 1, MaxBloodPerHit);

            // Смещение по хитбоксу: кровь идёт со стороны повреждённой части тела.
            Vector3 hitBias = GetHitboxBias(ev, player);
            Vector3 origin = player.Position + Vector3.up * 1.0f + hitBias;

            for (int i = 0; i < drops; i++)
            {
                if (spawnedBlood >= MaxTotalBlood)
                    return;

                // Небольшой рандом, чтобы капли летели "туда-сюда".
                Vector3 jitter = new Vector3(
                    Random.Range(-0.35f, 0.35f),
                    Random.Range(-0.25f, 0.15f),
                    Random.Range(-0.35f, 0.35f));

                if (TryPlaceBloodOnSurface(origin + jitter, hitBias))
                    spawnedBlood++;
            }
        }

        /// <summary>Есть ли у игрока активное кровотечение (наша медсистема или ванильный эффект).</summary>
        private static bool HasActiveBleeding(Player player)
        {
            // Ванильный bleeding.
            if (player.TryGetEffect(EffectType.Bleeding, out CustomPlayerEffects.StatusEffectBase vanilla) &&
                vanilla is not null && vanilla.IsEnabled)
                return true;

            // Наша медсистема (состояние кровотока).
            if (MedicalManager.TryGetState(player, out PlayerMedicalState? state) &&
                state is not null && state.HasActiveBleeding)
                return true;

            return false;
        }

        /// <summary>
        /// Возвращает горизонтальное смещение источника крови в сторону повреждённой
        /// части тела. Для рук/ног берём соответствующую сторону относительно взгляда.
        /// </summary>
        private static Vector3 GetHitboxBias(HurtingEventArgs ev, Player player)
        {
            if (ev.DamageHandler?.Base is not StandardDamageHandler standard)
                return Vector3.zero;

            Transform cam = player.CameraTransform;
            Vector3 right = cam is null ? Vector3.right : cam.right;
            right.y = 0f;
            right = right.sqrMagnitude > 0.0001f ? right.normalized : Vector3.right;

            // Хитбокс различает только голову/тело/конечность, поэтому для конечностей
            // случайно выбираем левую/правую сторону - создаёт эффект "кровь туда-сюда".
            return standard.Hitbox switch
            {
                HitboxType.Limb => right * (Random.value < 0.5f ? -0.45f : 0.45f),
                HitboxType.Body => right * Random.Range(-0.2f, 0.2f),
                _ => Vector3.zero,
            };
        }

        /// <summary>
        /// Ищет ближайшую поверхность (пол или стену) в радиусе ~1 м и ставит на неё
        /// ванильную кровь. Сначала пробует пол (вниз), затем стороны - так кровь
        /// ложится на стену, если игрок к ней прижат, иначе капает на пол.
        /// </summary>
        private static bool TryPlaceBloodOnSurface(Vector3 from, Vector3 sideHint)
        {
            // Порядок: пол -> подсказанная хитбоксом сторона -> остальные стороны.
            Vector3 side = sideHint.sqrMagnitude > 0.0001f ? sideHint.normalized : Vector3.right;

            Vector3[] dirs =
            {
                Vector3.down,
                side,
                -side,
                Vector3.forward,
                Vector3.back,
                Vector3.right,
                Vector3.left,
            };

            foreach (Vector3 dir in dirs)
            {
                if (Physics.Raycast(from, dir, out RaycastHit hit, SurfaceSearchDistance, ~0, QueryTriggerInteraction.Ignore))
                {
                    // Пропускаем хитбоксы/рэгдоллы/игроков - кровь только на геометрию.
                    if (hit.collider != null && hit.collider.GetComponentInParent<ReferenceHub>() != null)
                        continue;

                    // Ванильная кровь, видимая ВСЕМ игрокам. Расширение
                    // MirrorExtensions.PlaceBlood шлёт декаль только одному игроку,
                    // поэтому используем глобальный Map.PlaceBlood: он помечен
                    // устаревшим, но это единственный серверный (для всех) способ.
#pragma warning disable CS0618
                    Map.PlaceBlood(hit.point + hit.normal * 0.01f, hit.normal);
#pragma warning restore CS0618
                    return true;
                }
            }

            return false;
        }
    }
}
