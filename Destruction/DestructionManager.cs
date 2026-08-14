using System;
using System.Collections.Generic;
using Exiled.API.Features;
using Exiled.Events.EventArgs.Map;
using Exiled.Events.EventArgs.Player;
using MEC;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MainCore.Destruction
{
    public static class DestructionManager
    {
        private const float GrenadeFuseSeconds = 3f;

        private static readonly Collider[] OverlapBuffer = new Collider[256];
        private static readonly HashSet<int> processed = new HashSet<int>();

        private static readonly HashSet<string> normNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> rigidNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> damagingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<GameObject> rootBuffer = new List<GameObject>();

        private static CoroutineHandle scanCoroutine;
        private static bool running;

        private static Config Config => MainCorePlugin.Instance.Config;

        public static void Start()
        {
            if (running)
                return;

            running = true;
            processed.Clear();

            RefreshNameCaches();

            if (scanCoroutine.IsRunning)
                Timing.KillCoroutines(scanCoroutine);

            scanCoroutine = Timing.RunCoroutine(ScanLoop(), "MainCore.Destruction.Scan");
        }

        public static void Stop()
        {
            running = false;
            if (scanCoroutine.IsRunning)
                Timing.KillCoroutines(scanCoroutine);
            processed.Clear();
        }

        private static void RefreshNameCaches()
        {
            normNames.Clear();
            rigidNames.Clear();
            damagingNames.Clear();

            if (Config.BreakableNames != null)
                for (int i = 0; i < Config.BreakableNames.Count; i++)
                    if (!string.IsNullOrEmpty(Config.BreakableNames[i]))
                        normNames.Add(Config.BreakableNames[i]);

            if (Config.BreakableRigidNames != null)
                for (int i = 0; i < Config.BreakableRigidNames.Count; i++)
                    if (!string.IsNullOrEmpty(Config.BreakableRigidNames[i]))
                        rigidNames.Add(Config.BreakableRigidNames[i]);

            if (Config.DamagingNames != null)
                for (int i = 0; i < Config.DamagingNames.Count; i++)
                    if (!string.IsNullOrEmpty(Config.DamagingNames[i]))
                        damagingNames.Add(Config.DamagingNames[i]);
        }

        public static void OnChangedIntoGrenade(ChangedIntoGrenadeEventArgs ev)
        {
            if (ev.Projectile == null || ev.Projectile.GameObject == null)
                return;

            GameObject projectile = ev.Projectile.GameObject;

            Timing.CallDelayed(GrenadeFuseSeconds, () =>
            {
                if (projectile == null)
                    return;

                Vector3 origin = projectile.transform.position;
                HandleExplosion(
                    origin,
                    Config.BreakableExplosionRadius,
                    Config.BreakableExplosionDamage,
                    Config.BreakableExplosionForce);
            });
        }

        public static void OnHurting(HurtingEventArgs ev)
        {
            if (!ev.IsAllowed || ev.Attacker == null || ev.Player == null)
                return;

            if (ev.Player.ReferenceHub == ev.Attacker.ReferenceHub)
                return;

            Vector3 origin = ev.Attacker.CameraTransform != null
                ? ev.Attacker.CameraTransform.position
                : ev.Attacker.Position;
            Vector3 direction = ev.Attacker.CameraTransform != null
                ? ev.Attacker.CameraTransform.forward
                : (ev.Player.Position - origin).normalized;

            if (Physics.Raycast(origin, direction, out RaycastHit hit, 100f))
                TryDamageBreakable(hit.collider, ev.Amount);
        }

        private static void TryDamageBreakable(Collider collider, float amount)
        {
            if (collider == null)
                return;

            BreakableNorm? norm = collider.GetComponentInParent<BreakableNorm>();
            if (norm != null)
            {
                norm.TakeDamage(amount);
                return;
            }

            BreakableRigid? rigid = collider.GetComponentInParent<BreakableRigid>();
            if (rigid != null)
                rigid.TakeDamage(amount);
        }

        /// <summary>
        /// Applies an explosion to every breakable in range. Public so custom items
        /// (the RPG rocket) can trigger a blast with their own radius, damage and force
        /// instead of the grenade defaults.
        /// </summary>
        public static void HandleExplosion(Vector3 origin, float radius, float damage, float force)
        {
            int count;
            try
            {
                count = Physics.OverlapSphereNonAlloc(origin, radius, OverlapBuffer);
            }
            catch (Exception exception)
            {
                Log.Error($"[Destruction] Explosion overlap failed: {exception.Message}");
                return;
            }

            HashSet<int> hit = new HashSet<int>();

            for (int i = 0; i < count; i++)
            {
                Collider col = OverlapBuffer[i];
                if (col == null)
                    continue;

                BreakableNorm? norm = col.GetComponentInParent<BreakableNorm>();
                if (norm != null)
                {
                    int id = norm.GetInstanceID();
                    if (hit.Add(id))
                        norm.Explode(origin, radius, damage, force);
                    continue;
                }

                BreakableRigid? rigid = col.GetComponentInParent<BreakableRigid>();
                if (rigid != null)
                {
                    int id = rigid.GetInstanceID();
                    if (hit.Add(id))
                        rigid.Explode(origin, radius, damage, force);
                }
            }
        }

        private static IEnumerator<float> ScanLoop()
        {
            while (running)
            {
                yield return Timing.WaitForSeconds(Mathf.Max(0.25f, Config.DestructionScanInterval));

                try
                {
                    ScanScene();
                }
                catch (Exception exception)
                {
                    Log.Error($"[Destruction] Scan failed: {exception}");
                }
            }
        }

        private static void ScanScene()
        {
            if (normNames.Count == 0 && rigidNames.Count == 0 && damagingNames.Count == 0)
                return;

            Scene active = SceneManager.GetActiveScene();
            if (!active.IsValid())
                return;

            rootBuffer.Clear();
            active.GetRootGameObjects(rootBuffer);

            for (int i = 0; i < rootBuffer.Count; i++)
            {
                GameObject root = rootBuffer[i];
                if (root == null)
                    continue;

                int id = root.GetInstanceID();
                if (processed.Contains(id))
                    continue;

                string name = root.name;
                if (string.IsNullOrEmpty(name))
                    continue;

                if (MatchesAny(name, rigidNames))
                {
                    AttachRigid(root);
                    processed.Add(id);
                }
                else if (MatchesAny(name, normNames))
                {
                    AttachNorm(root);
                    processed.Add(id);
                }
                else if (MatchesAny(name, damagingNames))
                {
                    AttachDamaging(root);
                    processed.Add(id);
                }
            }
        }

        private static bool MatchesAny(string name, HashSet<string> patterns)
        {
            if (patterns.Count == 0)
                return false;

            foreach (string p in patterns)
            {
                if (name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static void AttachNorm(GameObject go)
        {
            if (go.GetComponent<BreakableNorm>() != null)
                return;

            BreakableNorm bn = go.AddComponent<BreakableNorm>();
            bn.Configure(
                Config.BreakableHealth,
                Config.BreakableExplosionRadius,
                Config.BreakableExplosionDamage,
                Mathf.Max(1, Config.BreakableSplitCount),
                Mathf.Max(0.1f, Config.BreakableSplitLifetimeSeconds),
                Mathf.Max(0f, Config.BreakableSplitForce));

            // Примитив без флага Collidable держит свой коллайдер выключенным: по такому
            // объекту нельзя попасть выстрелом (урон ищется рейкастом) и он не держит
            // ни игроков, ни обломки. Разрушаемым объектам столкновения нужны всегда.
            if (Config.BreakableForceCollidable)
                BreakablePhysics.EnsureCollidable(go, true);
        }

        private static void AttachRigid(GameObject go)
        {
            if (go.GetComponent<BreakableRigid>() != null)
                return;

            BreakableRigid br = go.AddComponent<BreakableRigid>();
            br.Configure(
                Config.BreakableHealth,
                Config.BreakableExplosionRadius,
                Config.BreakableExplosionDamage,
                Config.BreakableExplosionForce,
                Mathf.Max(1, Config.BreakableSplitCount),
                Mathf.Max(0.1f, Config.BreakableSplitLifetimeSeconds),
                Mathf.Max(0f, Config.BreakableSplitForce),
                Mathf.Max(1f, Config.BreakableMaxFallDepth));

            // Столкновения включаются заранее, а не в момент разрушения: иначе первый же
            // кадр с Rigidbody прошёл бы без коллайдера и объект ушёл бы под карту.
            if (Config.BreakableForceCollidable)
                BreakablePhysics.EnsureCollidable(go, true);
        }

        private static void AttachDamaging(GameObject go)
        {
            if (go.GetComponent<Damaging>() != null)
                return;

            Damaging dg = go.AddComponent<Damaging>();
            dg.Configure(
                Config.DamagingDamagePerSecond,
                Config.DamagingTickInterval,
                Config.DamagingEffectName,
                Config.DamagingEffectSeconds,
                Config.DamagingEffectIntensity,
                Config.DamagingShowMessage,
                Config.DamagingBroadcast,
                Config.DamagingBroadcastSeconds);
        }
    }
}