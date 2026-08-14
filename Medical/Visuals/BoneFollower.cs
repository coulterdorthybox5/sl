using System.Collections.Generic;
using AdminToys;
using Mirror;
using UnityEngine;

namespace MainCore.Medical.Visuals
{
    /// <summary>
    /// Держит перевязку на кости игрока. Поддерживает два режима:
    /// схематик ProjectMER (один объект) и набор отдельных примитивов.
    /// </summary>
    /// <remarks>
    /// Родителя объектам не назначаем. <c>AdminToyBase.UpdatePositionServer()</c>
    /// публикует в SyncVar <b>локальные</b> координаты, а клиент узнаёт об иерархии
    /// только из <c>RpcChangeParent</c>, который уходит из
    /// <c>OnTransformParentChanged</c> и лишь при <c>netIdentity.isServer</c>.
    /// В момент спавна доставка этого RPC не гарантирована, и до его получения
    /// клиент трактует локальную позицию как мировую - перевязка улетает в нуль карты.
    /// Без родителя <c>localPosition == position</c>, и SyncVar сразу содержит
    /// верные мировые координаты. Плавность даёт <c>NetworkMovementSmoothing</c>.
    ///
    /// Почему <c>Update</c>, а не <c>LateUpdate</c>: записать позицию нужно до того,
    /// как <c>AdminToyBase</c> прочитает её в своём LateUpdate, а порядок вызова
    /// LateUpdate между компонентами Unity не гарантирует. Цена - отставание визуала
    /// от анимации на один кадр, что на глаз незаметно.
    /// </remarks>
    public sealed class BoneFollower : MonoBehaviour
    {
        /// <summary>Кость, за которой следует перевязка.</summary>
        public Transform? Bone;

        /// <summary>Смещение точки крепления относительно кости.</summary>
        public Vector3 LocalOffset = Vector3.zero;

        /// <summary>
        /// Корень схематика ProjectMER, если перевязка спавнилась через Map Editor.
        /// Двигаем его целиком - блоки внутри уже расставлены самим ProjectMER.
        /// </summary>
        public Transform? Schematic;

        private readonly List<Block> blocks = new();

        private float nextAuditTime;

        private string debugName = string.Empty;

        /// <summary>Один примитив и его положение относительно точки крепления.</summary>
        private struct Block
        {
            public Transform Transform;

            public Vector3 Position;

            public Quaternion Rotation;

            public Vector3 Scale;
        }

        /// <summary>Добавляет примитив под управление компонента (режим без ProjectMER).</summary>
        public void Add(Transform blockTransform, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            blocks.Add(new Block
            {
                Transform = blockTransform,
                Position = position,
                Rotation = rotation,
                Scale = scale,
            });
        }

        /// <summary>Сколько примитивов под управлением (0 для схематика).</summary>
        public int BlockCount => blocks.Count;

        public void TrackSchematic(Transform schematic, string name)
        {
            Schematic = schematic;
            debugName = name;
            blocks.Clear();

            AdminToyBase[] toys = schematic.GetComponentsInChildren<AdminToyBase>(true);

            for (int i = 0; i < toys.Length; i++)
            {
                Transform child = toys[i].transform;
                if (child == schematic)
                    continue;

                // Unparent so local == world. AdminToyBase publishes LOCAL coordinates
                // and the client only learns the hierarchy from RpcChangeParent, which
                // is unreliable right after spawn. Without a parent the SyncVar carries
                // correct world positions directly, exactly like the primitive path.
                Vector3 worldPosition = child.position;
                Quaternion worldRotation = child.rotation;
                Vector3 worldScale = child.lossyScale;

                if (child.parent != null)
                    child.SetParent(null, true);

                // Offsets relative to the schematic root, in world space.
                blocks.Add(new Block
                {
                    Transform = child,
                    Position = worldPosition - schematic.position,
                    Rotation = Quaternion.Inverse(schematic.rotation) * worldRotation,
                    Scale = worldScale,
                });

                toys[i].NetworkIsStatic = false;
                toys[i].NetworkMovementSmoothing = 60;
            }

            VisualDebug.Step($"  MER runtime capture '{name}': root='{schematic.name}', " +
                             $"children={schematic.GetComponentsInChildren<Transform>(true).Length}, " +
                             $"adminToys={toys.Length}, movableBlocks={blocks.Count}, " +
                             $"unparented={blocks.Count}.");
        }

        /// <summary>Мировая точка крепления перевязки (для диагностики).</summary>
        public Vector3 AnchorPosition =>
            Bone == null ? Vector3.zero : Bone.TransformPoint(LocalOffset);

        /// <summary>
        /// Ставит перевязку на кость. Вызывается каждый кадр, а также вручную
        /// сразу после спавна, чтобы клиент получил верные координаты с первого кадра.
        /// </summary>
        public void Apply()
        {
            if (Bone == null)
                return;

            Vector3 anchorPosition = Bone.TransformPoint(LocalOffset);
            Quaternion anchorRotation = Bone.rotation;

            // MER root is commonly only a server-side container. Move every networked
            // child in world space as well, otherwise clients keep the spawn coordinates.
            if (Schematic != null)
            {
                Schematic.SetPositionAndRotation(anchorPosition, anchorRotation);
            }

            for (int i = 0; i < blocks.Count; i++)
            {
                Block block = blocks[i];

                if (block.Transform == null)
                    continue;

                // Родителя нет, поэтому пишем мировые координаты напрямую.
                block.Transform.position = anchorPosition + (anchorRotation * block.Position);
                block.Transform.rotation = anchorRotation * block.Rotation;

                // Масштаб фиксированный: кость анимации имеет собственный масштаб,
                // и без этого перевязка «дышала» бы вместе с моделью.
                block.Transform.localScale = block.Scale;
            }
        }

        private void Update() => Apply();

        private void LateUpdate()
        {
            Config? config = MainCorePlugin.Instance?.Config;
            if (config == null || !config.WoundVisualDeepDebug || Time.unscaledTime < nextAuditTime)
                return;

            nextAuditTime = Time.unscaledTime + config.WoundVisualDebugInterval;
            Audit();
        }

        private void Audit()
        {
            int alive = 0;
            int networked = 0;
            int spawned = 0;
            float farthest = 0f;
            Vector3 anchor = AnchorPosition;

            for (int i = 0; i < blocks.Count; i++)
            {
                Transform current = blocks[i].Transform;
                if (current == null)
                    continue;

                alive++;
                farthest = Mathf.Max(farthest, Vector3.Distance(current.position, anchor));
                NetworkIdentity identity = current.GetComponent<NetworkIdentity>();
                if (identity == null)
                    continue;

                networked++;
                if (identity.netId != 0 && NetworkServer.spawned.ContainsKey(identity.netId))
                    spawned++;
            }

            string root = Schematic == null
                ? "none"
                : $"'{Schematic.name}' active={Schematic.gameObject.activeInHierarchy} pos={Schematic.position}";
            VisualDebug.Step($"  AUDIT '{debugName}': bone={(Bone == null ? "DEAD" : Bone.name)}, " +
                             $"anchor={anchor}, root={root}, blocks={alive}/{blocks.Count}, " +
                             $"networkIds={networked}, mirrorSpawned={spawned}, farthest={farthest:F3}m.");
        }

        /// <summary>
        /// Отвязывает перевязку от кости. Вызывается перед уничтожением, чтобы
        /// Update не обращался к уже удалённому скелету.
        /// </summary>
        public void Detach()
        {
            Bone = null;
            Schematic = null;
            blocks.Clear();
        }
    }
}
