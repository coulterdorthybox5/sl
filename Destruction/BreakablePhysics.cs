using AdminToys;
using UnityEngine;

namespace MainCore.Destruction
{
    /// <summary>
    /// Приводит примитивы разрушаемого объекта в состояние, пригодное для серверной физики.
    ///
    /// Причина существования этого класса: PrimitiveObjectToy включает/выключает свой Collider
    /// по флагу PrimitiveFlags.Collidable (см. PrimitiveObjectToy.SetFlags). Если у объекта
    /// флага Collidable нет, коллайдер выключен, и добавленный при разрушении Rigidbody
    /// ничего под собой не видит - объект просто проваливается сквозь пол карты.
    /// Поэтому перед включением физики примитивы принудительно делаются collidable.
    /// </summary>
    internal static class BreakablePhysics
    {
        /// <summary>Толщина коллайдера для плоских примитивов (Plane/Quad), у них нет объёма.</summary>
        private const float FlatColliderThickness = 0.05f;

        /// <summary>
        /// Включает коллайдеры всех примитивов объекта. Если <paramref name="forceNetworkFlag"/>
        /// истинно, к сетевым флагам добавляется Collidable, чтобы состояние на клиенте
        /// совпадало с серверным (иначе клиент считает объект проходимым).
        /// </summary>
        public static void EnsureCollidable(GameObject root, bool forceNetworkFlag)
        {
            if (root == null)
                return;

            PrimitiveObjectToy[] toys = root.GetComponentsInChildren<PrimitiveObjectToy>(true);
            for (int i = 0; i < toys.Length; i++)
            {
                PrimitiveObjectToy toy = toys[i];
                if (toy == null)
                    continue;

                if (forceNetworkFlag)
                {
                    PrimitiveFlags flags = toy.NetworkPrimitiveFlags;
                    if ((flags & PrimitiveFlags.Collidable) == 0)
                    {
                        PrimitiveFlags updated = flags | PrimitiveFlags.Collidable;

                        // Сначала обновляем кэш оптимизатора: иначе он вернёт старые флаги
                        // (без Collidable) на следующем тике и коллайдер снова выключится.
                        PrimitiveCuller.OverrideOriginalFlags(toy, updated);
                        toy.NetworkPrimitiveFlags = updated;
                    }
                }

                EnsureCollider(toy);
            }

            // Флаг применяется хуком синквара, а он может сработать позже кадра, в котором
            // включается Rigidbody. Поэтому коллайдеры включаются ещё и напрямую.
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider col = colliders[i];
                if (col == null || col.isTrigger)
                    continue;

                col.enabled = true;
            }
        }

        /// <summary>Возвращает половину высоты объекта - нужна для ручной обработки столкновений.</summary>
        public static float GetHalfHeight(GameObject root)
        {
            if (root == null)
                return 0.5f;

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            float best = 0f;
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider col = colliders[i];
                if (col == null || col.isTrigger)
                    continue;

                float extent = col.bounds.extents.y;
                if (extent > best)
                    best = extent;
            }

            if (best <= 0f)
            {
                Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer r = renderers[i];
                    if (r == null)
                        continue;

                    float extent = r.bounds.extents.y;
                    if (extent > best)
                        best = extent;
                }
            }

            return Mathf.Clamp(best, 0.05f, 10f);
        }

        private static void EnsureCollider(PrimitiveObjectToy toy)
        {
            Collider existing = toy.GetComponent<Collider>();
            if (existing == null)
                existing = toy.GetComponentInChildren<Collider>(true);

            if (existing != null)
            {
                existing.enabled = true;
                existing.isTrigger = false;
                return;
            }

            // Коллайдера нет вообще (примитив собран без него) - создаём по форме меша.
            // Размеры даются в локальных единицах: масштаб объекта их сам домножит.
            switch (toy.NetworkPrimitiveType)
            {
                case PrimitiveType.Sphere:
                {
                    SphereCollider sphere = toy.gameObject.AddComponent<SphereCollider>();
                    sphere.radius = 0.5f;
                    break;
                }

                case PrimitiveType.Capsule:
                {
                    CapsuleCollider capsule = toy.gameObject.AddComponent<CapsuleCollider>();
                    capsule.radius = 0.5f;
                    capsule.height = 2f;
                    capsule.direction = 1;
                    break;
                }

                case PrimitiveType.Cylinder:
                {
                    CapsuleCollider cylinder = toy.gameObject.AddComponent<CapsuleCollider>();
                    cylinder.radius = 0.5f;
                    cylinder.height = 2f;
                    cylinder.direction = 1;
                    break;
                }

                case PrimitiveType.Plane:
                {
                    BoxCollider plane = toy.gameObject.AddComponent<BoxCollider>();
                    plane.size = new Vector3(10f, FlatColliderThickness, 10f);
                    break;
                }

                case PrimitiveType.Quad:
                {
                    BoxCollider quad = toy.gameObject.AddComponent<BoxCollider>();
                    quad.size = new Vector3(1f, 1f, FlatColliderThickness);
                    break;
                }

                default:
                {
                    BoxCollider box = toy.gameObject.AddComponent<BoxCollider>();
                    box.size = Vector3.one;
                    break;
                }
            }
        }
    }
}
