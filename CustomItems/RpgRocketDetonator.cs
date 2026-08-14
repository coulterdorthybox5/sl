using System.Collections.Generic;
using Exiled.API.Features;
using Exiled.API.Features.Pickups.Projectiles;
using MainCore.Destruction;
using MEC;
using UnityEngine;

namespace MainCore.CustomItems
{
    /// <summary>
    /// Flight controller and contact fuse for an RPG rocket.
    /// </summary>
    /// <remarks>
    /// The rocket is driven by an MEC coroutine rather than a <see cref="MonoBehaviour"/>.
    /// A component added to a live network projectile is not guaranteed to receive Unity
    /// callbacks, and when it does not, the rocket simply keeps flying: the constant
    /// velocity is set once when the grenade is thrown, so nothing looks broken while the
    /// contact fuse silently never runs. A coroutine is owned by the plugin and always
    /// ticks.
    ///
    /// Contact is detected by sweeping the segment the rocket covered since the previous
    /// frame with a <see cref="Physics.SphereCast(Vector3, float, Vector3, out RaycastHit, float)"/>.
    /// A rocket at 40 m/s crosses far more than the thickness of a wall in one frame, so a
    /// plain position test would tunnel straight through it.
    /// </remarks>
    public static class RpgRocketDetonator
    {
        /// <summary>The rocket must travel at least this far before the fuse arms itself.</summary>
        private const float ArmingDistance = 0.75f;

        /// <summary>Radius of the sweep used as the contact fuse, in metres.</summary>
        private const float FuseRadius = 0.12f;

        /// <summary>Extra sweep length so a long frame cannot step over a thin wall.</summary>
        private const float SweepMargin = 0.3f;

        /// <summary>Layers that must never trigger the fuse (hitboxes, ragdolls, pickups).</summary>
        private static readonly HashSet<string> IgnoredLayers = new HashSet<string>
        {
            "Hitbox",
            "Ragdoll",
            "Pickup",
            "Ignore Raycast",
            "InvisibleCollider",
        };

        private static Config Config => MainCorePlugin.Instance.Config;

        /// <summary>
        /// Arms the rocket and starts driving it.
        /// </summary>
        /// <param name="projectile">The grenade projectile used as the rocket body.</param>
        /// <param name="shooter">Player who fired, used for the safety distance check.</param>
        /// <param name="velocity">Constant velocity, in metres per second.</param>
        /// <param name="safeDistance">Detonations closer than this to the shooter are cancelled.</param>
        /// <param name="lifetimeSeconds">Self-destruct timeout for a rocket that never hits anything.</param>
        public static void Launch(Projectile projectile, Player shooter, Vector3 velocity, float safeDistance, float lifetimeSeconds)
        {
            if (projectile is null || projectile.GameObject is null)
                return;

            Timing.RunCoroutine(Fly(projectile, shooter, velocity, safeDistance, lifetimeSeconds), "MainCore.Rpg.Rocket");
        }

        private static IEnumerator<float> Fly(Projectile projectile, Player shooter, Vector3 velocity, float safeDistance, float lifetimeSeconds)
        {
            GameObject rocket = projectile.GameObject;
            Rigidbody? body = rocket.GetComponent<Rigidbody>();

            Vector3 spawn = rocket.transform.position;
            Vector3 previous = spawn;
            Vector3 direction = velocity.normalized;
            float speed = velocity.magnitude;
            float selfDestructAt = Time.time + Mathf.Max(1f, lifetimeSeconds);

            while (true)
            {
                yield return Timing.WaitForOneFrame;

                // The projectile is destroyed by the server on detonation, on round end, or
                // when the pickup is cleaned up; the coroutine has to notice that.
                if (rocket == null || projectile.GameObject == null)
                    yield break;

                Vector3 position = rocket.transform.position;

                if (Time.time >= selfDestructAt)
                {
                    Detonate(projectile, shooter, position, safeDistance);
                    yield break;
                }

                // Physics drag and bounces would otherwise slow the rocket down or curve it.
                if (body != null)
                {
                    body.useGravity = false;
                    body.angularVelocity = Vector3.zero;
                    body.velocity = velocity;
                }

                // The rocket leaves the tube inside the shooter's own capsule; without an
                // arming distance every shot would explode in the shooter's face.
                if (Vector3.Distance(position, spawn) < ArmingDistance)
                {
                    previous = position;
                    continue;
                }

                Vector3 travelled = position - previous;
                float distance = travelled.magnitude;

                // A stationary rocket (first frame after the throw, or physics not applied
                // yet) still has to be swept forward, otherwise it could rest against a
                // wall without ever detonating.
                Vector3 sweep = distance > 0.001f ? travelled / distance : direction;
                float length = Mathf.Max(distance, speed * Time.deltaTime) + SweepMargin;

                if (Physics.SphereCast(previous, FuseRadius, sweep, out RaycastHit hit, length)
                    && IsValidTarget(hit.collider, rocket, shooter))
                {
                    // Detonate on the impact point so the blast is centred on the wall
                    // instead of behind it, where the rocket already tunnelled to.
                    Detonate(projectile, shooter, hit.point, safeDistance);
                    yield break;
                }

                previous = position;
            }
        }

        /// <summary>
        /// Filters out contacts that must not set the rocket off: the rocket's own
        /// colliders, the shooter's body right after firing, and non-solid helpers.
        /// </summary>
        private static bool IsValidTarget(Collider? collider, GameObject rocket, Player? shooter)
        {
            if (collider is null)
                return false;

            if (rocket != null && collider.transform.IsChildOf(rocket.transform))
                return false;

            if (shooter is not null && shooter.GameObject != null
                && collider.transform.IsChildOf(shooter.GameObject.transform))
                return false;

            string layer = LayerMask.LayerToName(collider.gameObject.layer);
            if (!string.IsNullOrEmpty(layer) && IgnoredLayers.Contains(layer))
                return false;

            return true;
        }

        /// <summary>
        /// Explodes the rocket. Detonating closer than the safety distance to the shooter
        /// is suppressed - the rocket is simply removed instead of killing its own user.
        /// </summary>
        private static void Detonate(Projectile projectile, Player? shooter, Vector3 point, float safeDistance)
        {
            if (projectile is null)
                return;

            if (shooter is not null && Vector3.Distance(point, shooter.Position) < safeDistance)
            {
                projectile.Destroy();
                return;
            }

            // Position is synced before the blast so clients see the explosion where the
            // rocket actually hit.
            projectile.Position = point;

            // The vanilla grenade blast is far too weak for a rocket: breakables are
            // damaged and thrown here with the RPG's own radius, damage and force, so a
            // direct hit tears the object apart instead of merely nudging it.
            DestructionManager.HandleExplosion(
                point,
                Mathf.Max(0.1f, Config.RpgExplosionRadius),
                Mathf.Max(0f, Config.RpgExplosionDamage),
                Mathf.Max(0f, Config.RpgExplosionForce));

            if (projectile is TimeGrenadeProjectile grenade)
            {
                // Zero fuse makes the server detonate the grenade on its next tick,
                // producing the normal explosion, damage and effects.
                grenade.FuseTime = 0f;
                grenade.Base.ServerFuseEnd();
            }
            else
            {
                projectile.Destroy();
            }
        }
    }
}