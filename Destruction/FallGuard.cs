using UnityEngine;

namespace MainCore.Destruction
{
    /// <summary>
    /// Страховка от провала сквозь пол для объектов, которым при разрушении включили физику.
    ///
    /// Одних включённых коллайдеров мало: обломок разгоняется взрывом за один кадр,
    /// и обычный дискретный расчёт столкновений может "перепрыгнуть" тонкий пол
    /// (эффект туннелирования). Этот компонент каждый шаг физики стреляет луч вниз
    /// и, если объект оказался под поверхностью, возвращает его на неё.
    /// Если под объектом ничего нет (он вылетел за пределы карты) - объект удаляется,
    /// чтобы он не падал бесконечно и не тратил сеть.
    /// </summary>
    public sealed class FallGuard : MonoBehaviour
    {
        /// <summary>Запас над поверхностью, чтобы объект не оставался вклиненным в неё.</summary>
        private const float SurfaceSkin = 0.02f;

        /// <summary>Дальность луча вниз. Больше высоты любого этажа карты.</summary>
        private const float GroundProbeDistance = 80f;

        /// <summary>Насколько ниже точки появления объект считается провалившимся насквозь.</summary>
        public float MaxFallDepth = 30f;

        /// <summary>Сколько секунд гасить остаточные толчки после посадки.</summary>
        public float RestThreshold = 0.35f;

        private Rigidbody? body;
        private Collider[] ownColliders = new Collider[0];
        private float halfHeight = 0.5f;
        private float startY;
        private bool initialized;

        public void Init(Rigidbody rigidbody, float halfHeightOverride, float maxFallDepth)
        {
            body = rigidbody;
            halfHeight = Mathf.Max(0.02f, halfHeightOverride);
            MaxFallDepth = Mathf.Max(1f, maxFallDepth);

            ownColliders = GetComponentsInChildren<Collider>(true);
            startY = transform.position.y;
            initialized = true;
        }

        private void FixedUpdate()
        {
            if (!initialized || body == null)
                return;

            Vector3 position = transform.position;

            // Объект ушёл слишком глубоко - спасать нечего, физика его уже потеряла.
            if (position.y < startY - MaxFallDepth)
            {
                SplitHelper.SplitAndDestroy(gameObject, 1, 0f, 0f, position);
                initialized = false;
                return;
            }

            if (!TryGetGroundY(position, out float groundY))
                return;

            float bottom = position.y - halfHeight;
            if (bottom >= groundY - SurfaceSkin)
                return;

            // Провалился: поднимаем ровно на поверхность и гасим вертикальную скорость,
            // иначе на следующем шаге он снова уйдёт вниз с тем же импульсом.
            position.y = groundY + halfHeight + SurfaceSkin;
            transform.position = position;

            Vector3 velocity = body.velocity;
            if (velocity.y < 0f)
                velocity.y = 0f;

            // Горизонтальная скорость гасится, чтобы обломок не уезжал по полу вечно.
            velocity.x *= RestThreshold;
            velocity.z *= RestThreshold;

            body.velocity = velocity;
            body.angularVelocity *= RestThreshold;
        }

        /// <summary>Ищет ближайшую поверхность под объектом, игнорируя его собственные коллайдеры.</summary>
        private bool TryGetGroundY(Vector3 position, out float groundY)
        {
            groundY = 0f;

            // Луч начинается выше объекта: если он уже утонул в полу, старт из его центра
            // оказался бы под поверхностью, и попадания не было бы вовсе.
            Vector3 origin = position + Vector3.up * (halfHeight + 1f);

            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, GroundProbeDistance);
            if (hits.Length == 0)
                return false;

            bool found = false;
            float best = float.NegativeInfinity;

            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i].collider;
                if (col == null || col.isTrigger)
                    continue;

                if (IsOwnCollider(col))
                    continue;

                // Игроки и прочие персонажи не пол: вставать на них нельзя.
                if (col.GetComponentInParent<ReferenceHub>() != null)
                    continue;

                float y = hits[i].point.y;
                if (y > best)
                {
                    best = y;
                    found = true;
                }
            }

            if (!found)
                return false;

            groundY = best;
            return true;
        }

        private bool IsOwnCollider(Collider col)
        {
            for (int i = 0; i < ownColliders.Length; i++)
            {
                if (ownColliders[i] == col)
                    return true;
            }

            Transform t = col.transform;
            return t == transform || t.IsChildOf(transform);
        }
    }
}
