using System;
using System.Collections.Generic;
using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.API.Features.DamageHandlers;
using Exiled.Events.EventArgs.Player;
using PlayerStatsSystem;

namespace MainCore.Medical
{
    /// <summary>
    /// Определяет, какое ранение игрок получает от конкретного урона.
    /// </summary>
    public static class InjuryResolver
    {
        private static readonly Random Random = new();

        /// <summary>
        /// Названия типов урона от огнестрельного оружия.
        /// Сравнение идёт по строке, чтобы список оружия в игре можно было
        /// расширять без изменения кода.
        /// </summary>
        private static readonly HashSet<string> FirearmDamageTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Firearm",
            "Com15",
            "Com18",
            "Com45",
            "Fsp9",
            "Crossvec",
            "E11Sr",
            "Logicer",
            "Revolver",
            "Shotgun",
            "AK",
            "A7",
            "Frmg0",
            "ParticleDisruptor",
            "GunSCP127",
        };

        /// <summary>Названия типов урона от взрывов и осколков.</summary>
        private static readonly HashSet<string> ExplosiveDamageTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Explosion",
            "Scp018",
        };

        /// <summary>Названия типов урона от падения.</summary>
        private static readonly HashSet<string> FallDamageTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Falldown",
        };

        /// <summary>Названия типов урона, которые всегда дают ожог.</summary>
        private static readonly HashSet<string> BurnDamageTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "MicroHid",
            "Tesla",
        };

        /// <summary>
        /// Игнор-лист из конфига, приведённый к регистронезависимому набору.
        /// Пересобирается при перезагрузке конфига.
        /// </summary>
        private static HashSet<string> ignoredDamageTypes = new(StringComparer.OrdinalIgnoreCase);

        private static Config Config => MainCorePlugin.Instance.Config;

        /// <summary>
        /// Пересобирает кеш игнорируемых типов урона из конфига.
        /// Вызывается при включении плагина и в начале раунда.
        /// </summary>
        public static void ReloadConfigCache()
        {
            ignoredDamageTypes = new HashSet<string>(
                Config.IgnoredDamageTypes ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Разбирает событие получения урона и наносит соответствующие ранения.
        /// </summary>
        public static void Resolve(HurtingEventArgs ev)
        {
            Player player = ev.Player;
            if (player is null || !player.IsAlive)
                return;

            string damageName = ev.DamageHandler.Type.ToString();
            if (ignoredDamageTypes.Contains(damageName))
                return;

            float damage = ev.Amount;
            if (damage <= 0f)
                return;

            if (FallDamageTypes.Contains(damageName))
            {
                ResolveFall(player, damage);
                return;
            }

            if (ExplosiveDamageTypes.Contains(damageName))
            {
                ResolveExplosion(player, damage);
                return;
            }

            if (BurnDamageTypes.Contains(damageName))
            {
                ResolveBurn(player, damage, GetBodyPart(ev));
                return;
            }

            if (FirearmDamageTypes.Contains(damageName))
            {
                ResolveGunshot(player, damage, GetBodyPart(ev));
                return;
            }

            ResolveGeneric(player, damage, GetBodyPart(ev));
        }

        /// <summary>Падение: ушиб, с большой высоты - перелом ноги.</summary>
        private static void ResolveFall(Player player, float damage)
        {
            if (damage >= Config.FractureFallDamage)
            {
                MedicalManager.Inflict(player, InjuryType.Fracture, RandomLeg());
                MedicalManager.Inflict(player, InjuryType.Contusion, BodyPart.Torso);
                return;
            }

            if (damage >= Config.ContusionFallDamage)
                MedicalManager.Inflict(player, InjuryType.Contusion, RandomLeg());
        }

        /// <summary>Взрыв: осколочные кровотечения плюс возможный ожог.</summary>
        private static void ResolveExplosion(Player player, float damage)
        {
            InjuryType bleeding = Roll(Config.ExplosionVenousChance)
                ? InjuryType.VenousBleeding
                : InjuryType.CapillaryBleeding;

            MedicalManager.Inflict(player, bleeding, RandomLimb());

            if (Roll(Config.BurnChance))
                MedicalManager.Inflict(player, InjuryType.Burn, RandomBodyPart());

            if (damage >= Config.HeavyDamage)
                MedicalManager.Inflict(player, InjuryType.Contusion, BodyPart.Torso);
        }

        /// <summary>Термическое поражение.</summary>
        private static void ResolveBurn(Player player, float damage, BodyPart bodyPart)
        {
            MedicalManager.Inflict(player, InjuryType.Burn, bodyPart);

            if (damage >= Config.HeavyDamage)
                MedicalManager.Inflict(player, InjuryType.CapillaryBleeding, bodyPart);
        }

        /// <summary>Огнестрельное ранение.</summary>
        private static void ResolveGunshot(Player player, float damage, BodyPart bodyPart)
        {
            if (damage >= Config.HeavyDamage)
            {
                InjuryType type = PickWeighted(
                    (InjuryType.ArterialBleeding, Config.HeavyArterialChance),
                    (InjuryType.VenousBleeding, Config.HeavyVenousChance),
                    (InjuryType.CapillaryBleeding, Config.HeavyCapillaryChance));

                MedicalManager.Inflict(player, type, bodyPart);

                if (bodyPart == BodyPart.Torso && Roll(Config.InternalBleedingChance))
                    MedicalManager.Inflict(player, InjuryType.InternalBleeding, BodyPart.Torso);

                return;
            }

            if (damage >= Config.MediumDamage)
            {
                InjuryType type = PickWeighted(
                    (InjuryType.VenousBleeding, Config.MediumVenousChance),
                    (InjuryType.CapillaryBleeding, Config.MediumCapillaryChance),
                    (InjuryType.LightWound, Config.MediumLightChance));

                MedicalManager.Inflict(player, type, bodyPart);
                return;
            }

            // Слабое попадание может вообще не оставить ранения (царапина).
            InjuryType? weak = PickWeightedOrNothing(
                Config.WeakNothingChance,
                (InjuryType.CapillaryBleeding, Config.WeakCapillaryChance),
                (InjuryType.LightWound, Config.WeakLightChance));

            if (weak.HasValue)
                MedicalManager.Inflict(player, weak.Value, bodyPart);
        }

        /// <summary>Прочий урон (удары в ближнем бою, SCP и т.д.).</summary>
        private static void ResolveGeneric(Player player, float damage, BodyPart bodyPart)
        {
            if (damage >= Config.HeavyDamage)
            {
                MedicalManager.Inflict(player, InjuryType.VenousBleeding, bodyPart);
                MedicalManager.Inflict(player, InjuryType.Contusion, bodyPart);
                return;
            }

            if (damage >= Config.MediumDamage)
            {
                MedicalManager.Inflict(player, InjuryType.CapillaryBleeding, bodyPart);
                MedicalManager.Inflict(player, InjuryType.Contusion, bodyPart);
                return;
            }

            MedicalManager.Inflict(player, InjuryType.Contusion, bodyPart);
        }

        /// <summary>
        /// Определяет часть тела по хитбоксу. Игра различает только голову, корпус
        /// и конечности, поэтому конкретная конечность выбирается случайно.
        /// </summary>
        private static BodyPart GetBodyPart(HurtingEventArgs ev)
        {
            if (!TryGetHitbox(ev, out HitboxType hitbox))
                return RandomBodyPart();

            return hitbox switch
            {
                HitboxType.Headshot => BodyPart.Head,
                HitboxType.Body => BodyPart.Torso,
                HitboxType.Limb => RandomLimb(),
                _ => RandomBodyPart(),
            };
        }

        /// <summary>
        /// Хитбокс лежит в публичном поле <c>StandardDamageHandler.Hitbox</c>
        /// и заполняется игрой для любого попадания по хитбоксу игрока.
        /// </summary>
        private static bool TryGetHitbox(HurtingEventArgs ev, out HitboxType hitbox)
        {
            hitbox = default;

            if (ev.DamageHandler?.Base is StandardDamageHandler standard)
            {
                hitbox = standard.Hitbox;
                return true;
            }

            return false;
        }

        private static BodyPart RandomBodyPart()
        {
            // Корпус и конечности получают попадания чаще головы.
            int roll = Random.Next(100);

            if (roll < 10)
                return BodyPart.Head;

            if (roll < 50)
                return BodyPart.Torso;

            return RandomLimb();
        }

        private static BodyPart RandomLimb() => Random.Next(2) == 0 ? RandomArm() : RandomLeg();

        private static BodyPart RandomArm() => Random.Next(2) == 0 ? BodyPart.LeftArm : BodyPart.RightArm;

        private static BodyPart RandomLeg() => Random.Next(2) == 0 ? BodyPart.LeftLeg : BodyPart.RightLeg;

        private static bool Roll(int chance) => chance > 0 && Random.Next(100) < chance;

        private static InjuryType PickWeighted(params (InjuryType Type, int Weight)[] options)
        {
            int total = 0;
            foreach ((InjuryType _, int weight) in options)
                total += Math.Max(0, weight);

            if (total <= 0)
                return options[options.Length - 1].Type;

            int roll = Random.Next(total);

            foreach ((InjuryType type, int weight) in options)
            {
                roll -= Math.Max(0, weight);
                if (roll < 0)
                    return type;
            }

            return options[options.Length - 1].Type;
        }

        /// <summary>
        /// Как <see cref="PickWeighted"/>, но с дополнительным весом на исход "без ранения".
        /// </summary>
        private static InjuryType? PickWeightedOrNothing(int nothingWeight, params (InjuryType Type, int Weight)[] options)
        {
            int nothing = Math.Max(0, nothingWeight);

            int total = nothing;
            foreach ((InjuryType _, int weight) in options)
                total += Math.Max(0, weight);

            if (total <= 0)
                return null;

            int roll = Random.Next(total);

            foreach ((InjuryType type, int weight) in options)
            {
                roll -= Math.Max(0, weight);
                if (roll < 0)
                    return type;
            }

            return null;
        }
    }
}
