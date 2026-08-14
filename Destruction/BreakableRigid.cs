using MEC;
using UnityEngine;

namespace MainCore.Destruction
{
    public sealed class BreakableRigid : MonoBehaviour
    {
        public float Health = 40f;
        public float ExplosionRadius = 6f;
        public float ExplosionDamage = 40f;
        public float ExplosionForce = 500f;
        public float Mass = 10f;
        public float Drag = 0.05f;
        public float AngularDrag = 0.05f;
        public float LifetimeAfterBreak = 8f;

        public int SplitCount = 1;
        public float SplitLifetime = 5f;
        public float SplitForce = 250f;

        /// <summary>Максимальная глубина падения, после которой обломок удаляется.</summary>
        public float MaxFallDepth = 30f;

        private bool broken;
        private Vector3 lastImpact;

        public void Configure(float health, float explosionRadius, float explosionDamage, float explosionForce,
            int splitCount, float splitLifetime, float splitForce, float maxFallDepth)
        {
            Health = health;
            ExplosionRadius = explosionRadius;
            ExplosionDamage = explosionDamage;
            ExplosionForce = explosionForce;
            SplitCount = splitCount;
            SplitLifetime = splitLifetime;
            SplitForce = splitForce;
            MaxFallDepth = maxFallDepth;
        }

        public void TakeDamage(float amount)
        {
            if (broken)
                return;

            Health -= amount;
            if (Health <= 0f)
                Break(transform.position, ExplosionForce, ExplosionRadius);
        }

        public void Explode(Vector3 origin, float radius, float damage, float force)
        {
            if (broken)
                return;

            float distance = Vector3.Distance(origin, transform.position);
            if (distance > radius)
                return;

            float falloff = 1f - Mathf.Clamp01(distance / radius);
            lastImpact = origin;
            Health -= damage * falloff;

            if (Health <= 0f)
                Break(origin, force, radius);
        }

        public void Break(Vector3 impactOrigin, float force, float radius)
        {
            if (broken)
                return;

            broken = true;
            lastImpact = impactOrigin;

            if (SplitCount > 1)
            {
                // The blast force wins over the object's own split force: a rocket has to
                // scatter the debris far harder than a plain break does.
                SplitHelper.SplitAndDestroy(gameObject, SplitCount, SplitLifetime, Mathf.Max(SplitForce, force), impactOrigin);
                return;
            }

            // Примитивы без флага Collidable держат свой коллайдер выключенным, поэтому
            // физика не увидела бы под объектом пол и он ушёл бы под карту.
            // Включаем столкновения до того, как объектом займётся Rigidbody.
            BreakablePhysics.EnsureCollidable(gameObject, true);
            float halfHeight = BreakablePhysics.GetHalfHeight(gameObject);

            Rigidbody body = gameObject.AddComponent<Rigidbody>();
            body.mass = Mass;
            body.drag = Drag;
            body.angularDrag = AngularDrag;
            body.useGravity = true;
            body.isKinematic = false;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // Ограничение скорости: за один шаг физики объект не должен пролетать
            // больше собственной толщины, иначе он проскочит пол между шагами.
            body.maxDepenetrationVelocity = 3f;

            body.AddExplosionForce(force, impactOrigin, radius, 1f, ForceMode.Impulse);

            // Ремень безопасности на случай, если объект всё же проскочит пол.
            FallGuard guard = gameObject.AddComponent<FallGuard>();
            guard.Init(body, halfHeight, MaxFallDepth);

            Timing.CallDelayed(LifetimeAfterBreak, () =>
            {
                if (this == null || gameObject == null)
                    return;

                SplitHelper.SplitAndDestroy(gameObject, 1, 0f, 0f, transform.position);
            });
        }
    }
}