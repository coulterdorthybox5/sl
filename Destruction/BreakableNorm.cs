using UnityEngine;

namespace MainCore.Destruction
{
    public sealed class BreakableNorm : MonoBehaviour
    {
        public float Health = 40f;
        public float ExplosionRadius = 6f;
        public float ExplosionDamage = 40f;

        public int SplitCount = 1;
        public float SplitLifetime = 5f;
        public float SplitForce = 250f;

        private bool broken;
        private Vector3 lastImpact;

        public void Configure(float health, float explosionRadius, float explosionDamage,
            int splitCount, float splitLifetime, float splitForce)
        {
            Health = health;
            ExplosionRadius = explosionRadius;
            ExplosionDamage = explosionDamage;
            SplitCount = splitCount;
            SplitLifetime = splitLifetime;
            SplitForce = splitForce;
        }

        public void TakeDamage(float amount)
        {
            if (broken)
                return;

            Health -= amount;
            if (Health <= 0f)
                Break();
        }

        public void Explode(Vector3 origin, float radius, float damage)
            => Explode(origin, radius, damage, SplitForce);

        /// <summary>
        /// Explosion damage with an explicit scatter force for the fragments.
        /// A rocket must throw the debris much harder than a grenade, so the force
        /// of the blast overrides the object's own <see cref="SplitForce"/>.
        /// </summary>
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
                Break(force);
        }

        public void Break() => Break(SplitForce);

        public void Break(float force)
        {
            if (broken)
                return;

            broken = true;

            Vector3 impact = lastImpact == Vector3.zero ? transform.position : lastImpact;
            SplitHelper.SplitAndDestroy(gameObject, SplitCount, SplitLifetime, force, impact);
        }

    }
}