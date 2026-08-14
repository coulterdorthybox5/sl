using System.Collections.Generic;
using AdminToys;
using Exiled.API.Features.Toys;
using Exiled.API.Enums;
using MEC;
using Mirror;
using UnityEngine;

namespace MainCore.Destruction
{
    public static class SplitHelper
    {
        /// <summary>Максимальная глубина падения обломка, после которой он удаляется.</summary>
        private const float FragmentMaxFallDepth = 20f;

        public static void SplitAndDestroy(GameObject root, int splitCount, float lifetime, float force, Vector3 impactOrigin)
        {
            if (root == null)
                return;

            if (splitCount <= 1)
            {
                DestroyNetworked(root);
                return;
            }

            int perAxis = Mathf.Max(2, Mathf.CeilToInt(Mathf.Pow(splitCount, 1f / 3f)));

            PrimitiveObjectToy[] cubes = root.GetComponentsInChildren<PrimitiveObjectToy>(true);

            List<PrimitiveObjectToy> spawnedFragments = new List<PrimitiveObjectToy>();

            for (int i = 0; i < cubes.Length; i++)
            {
                PrimitiveObjectToy toy = cubes[i];
                if (toy == null)
                    continue;

                if (toy.NetworkPrimitiveType != PrimitiveType.Cube)
                    continue;

                if (BelongsToOtherBreakable(toy.transform, root))
                    continue;

                SpawnFragmentsForCube(toy, perAxis, force, impactOrigin, spawnedFragments);
            }

            DestroyNetworked(root);

            if (spawnedFragments.Count == 0)
                return;

            List<PrimitiveObjectToy> toKill = new List<PrimitiveObjectToy>(spawnedFragments);
            Timing.CallDelayed(lifetime, () =>
            {
                for (int i = 0; i < toKill.Count; i++)
                {
                    PrimitiveObjectToy t = toKill[i];
                    if (t == null)
                        continue;

                    try
                    {
                        NetworkServer.Destroy(t.gameObject);
                    }
                    catch
                    {
                    }
                }
            });
        }

        private static void SpawnFragmentsForCube(
            PrimitiveObjectToy toy,
            int perAxis,
            float force,
            Vector3 impactOrigin,
            List<PrimitiveObjectToy> outFragments)
        {
            Transform t = toy.transform;
            Vector3 worldPos = t.position;
            Quaternion worldRot = t.rotation;
            Vector3 lossyScale = t.lossyScale;

            Vector3 fragmentScale = new Vector3(
                lossyScale.x / perAxis,
                lossyScale.y / perAxis,
                lossyScale.z / perAxis);

            Vector3 half = lossyScale * 0.5f;
            Vector3 step = new Vector3(fragmentScale.x, fragmentScale.y, fragmentScale.z);
            Vector3 startLocal = -half + step * 0.5f;

            Color color = toy.NetworkMaterialColor;

            for (int x = 0; x < perAxis; x++)
            {
                for (int y = 0; y < perAxis; y++)
                {
                    for (int z = 0; z < perAxis; z++)
                    {
                        Vector3 localOffset = new Vector3(
                            startLocal.x + step.x * x,
                            startLocal.y + step.y * y,
                            startLocal.z + step.z * z);

                        Vector3 fragWorld = worldPos + (worldRot * localOffset);

                        Primitive p = Primitive.Create(
                            UnityEngine.PrimitiveType.Cube,
                            PrimitiveFlags.Visible | PrimitiveFlags.Collidable,
                            fragWorld,
                            worldRot.eulerAngles,
                            fragmentScale,
                            false,
                            color);

                        PrimitiveObjectToy frag = p.Base;
                        frag.NetworkMovementSmoothing = 0;
                        frag.NetworkIsStatic = false;

                        // Обломок создаётся с флагом Collidable, но сам Collider включается
                        // только когда PrimitiveObjectToy применит флаги. До этого момента
                        // физика считала бы обломок пустым и он провалился бы сквозь пол.
                        BreakablePhysics.EnsureCollidable(frag.gameObject, false);

                        Rigidbody rb = frag.gameObject.AddComponent<Rigidbody>();
                        rb.mass = 1f;
                        rb.useGravity = true;
                        rb.isKinematic = false;
                        rb.interpolation = RigidbodyInterpolation.Interpolate;

                        // Мелкие обломки летят быстро, а они тоньше пола: без непрерывной
                        // проверки столкновений они бы проскакивали его между шагами физики.
                        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                        rb.AddExplosionForce(force, impactOrigin, 20f, 1f, ForceMode.Impulse);

                        FallGuard guard = frag.gameObject.AddComponent<FallGuard>();
                        guard.Init(rb, Mathf.Max(0.02f, fragmentScale.y * 0.5f), FragmentMaxFallDepth);

                        NetworkServer.Spawn(frag.gameObject);

                        outFragments.Add(frag);
                    }
                }
            }
        }

        private static void DestroyNetworked(GameObject go)
        {
            if (go == null)
                return;

            List<Transform> keepAlive = new List<Transform>();

            NetworkIdentity[] ids = go.GetComponentsInChildren<NetworkIdentity>(true);
            for (int i = ids.Length - 1; i >= 0; i--)
            {
                NetworkIdentity id = ids[i];
                if (id == null || id.gameObject == go)
                    continue;

                if (BelongsToOtherBreakable(id.transform, go))
                {
                    keepAlive.Add(id.transform);
                    continue;
                }

                try
                {
                    if (id.netId != 0)
                        NetworkServer.Destroy(id.gameObject);
                    else
                        UnityEngine.Object.Destroy(id.gameObject);
                }
                catch
                {
                }
            }

            for (int i = 0; i < keepAlive.Count; i++)
            {
                Transform t = keepAlive[i];
                if (t == null)
                    continue;
                t.SetParent(null, true);
            }

            NetworkIdentity self = go.GetComponent<NetworkIdentity>();
            try
            {
                if (self != null && self.netId != 0)
                    NetworkServer.Destroy(go);
                else
                    UnityEngine.Object.Destroy(go);
            }
            catch
            {
                UnityEngine.Object.Destroy(go);
            }
        }

        private static bool BelongsToOtherBreakable(Transform t, GameObject root)
        {
            Transform? cursor = t;
            while (cursor != null && cursor.gameObject != root)
            {
                if (cursor.GetComponent<BreakableNorm>() != null)
                    return true;
                if (cursor.GetComponent<BreakableRigid>() != null)
                    return true;
                if (HasNamedComponent(cursor, "IgnoreOptimizationFull"))
                    return true;

                cursor = cursor.parent;
            }
            return false;
        }

        private static bool HasNamedComponent(Transform t, string typeName)
        {
            Component[] components = t.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component c = components[i];
                if (c == null)
                    continue;

                if (string.Equals(c.GetType().Name, typeName, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
