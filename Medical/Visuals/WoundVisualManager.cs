using System;
using System.Collections.Generic;
using AdminToys;
using Exiled.API.Features;
using Exiled.API.Features.Toys;
using Mirror;
using UnityEngine;

namespace MainCore.Medical.Visuals
{
    /// <summary>
    /// Показывает ранения игрока перевязками, закреплёнными на костях скелета.
    /// Один слот (часть тела) - максимум один визуал: показывается самое тяжёлое ранение.
    /// </summary>
    /// <remarks>
    /// Способ спавна выбирается автоматически:
    ///
    /// 1. <b>ProjectMER (Map Editor)</b> через <see cref="MapEditorBridge"/> - основной путь.
    ///    Сетевой синхронизацией схематика занимается сам ProjectMER, который это делает
    ///    надёжно. Вызов идёт по рефлексии, потому что статическая ссылка на ProjectMER
    ///    невозможна: EXILED грузит плагины как <c>Assembly.Load(byte[])</c>, у сборки нет
    ///    Location, и среда не находит ProjectMER по имени (её грузит LabAPI). Метод
    ///    с упоминанием типа ProjectMER падал бы с FileNotFoundException прямо
    ///    при JIT-компиляции, ещё до первой своей строки.
    ///
    /// 2. <b>Свои примитивы</b> (<see cref="PrimitiveObjectToy"/>) - запас на случай,
    ///    если ProjectMER на сервере не стоит. Геометрия та же, вкомпилирована в плагин.
    /// </remarks>
    public static class WoundVisualManager
    {
        /// <summary>
        /// Сглаживание движения на клиенте. Перевязка двигается каждый кадр,
        /// без сглаживания клиент дёргал бы её между сетевыми апдейтами.
        /// </summary>
        private const byte MovementSmoothing = 60;

        /// <summary>Активные визуалы: игрок -> часть тела -> перевязка.</summary>
        private static readonly Dictionary<ReferenceHub, Dictionary<BodyPart, ActiveVisual>> Visuals = new();

        /// <summary>Буферы обхода, чтобы не создавать списки на каждое обновление.</summary>
        private static readonly List<BodyPart> SlotBuffer = new();

        private static readonly List<ReferenceHub> HubBuffer = new();

        private static Config Config => MainCorePlugin.Instance.Config;

        /// <summary>Сколько визуалов сейчас в мире (для диагностики и лимитов).</summary>
        public static int ActiveCount { get; private set; }

        /// <summary>Данные одной закреплённой перевязки.</summary>
        private sealed class ActiveVisual
        {
            public string DressingName = string.Empty;

            /// <summary>Схематик ProjectMER, если перевязка создана через Map Editor.</summary>
            public Component? Schematic;

            /// <summary>Блоки перевязки в режиме своих примитивов.</summary>
            public readonly List<PrimitiveObjectToy> Toys = new();

            /// <summary>Компонент, который держит перевязку на кости.</summary>
            public BoneFollower? Follower;

            /// <summary>Есть ли ещё живые объекты у этого визуала.</summary>
            public bool HasObjects => (Schematic != null) || Toys.Count > 0;
        }

        /// <summary>Убирает все визуалы (конец раунда, выключение плагина).</summary>
        public static void Clear()
        {
            HubBuffer.Clear();
            foreach (ReferenceHub hub in Visuals.Keys)
                HubBuffer.Add(hub);

            for (int i = 0; i < HubBuffer.Count; i++)
                RemoveAll(HubBuffer[i]);

            HubBuffer.Clear();
            Visuals.Clear();
            ActiveCount = 0;
        }

        /// <summary>Убирает все визуалы игрока (смерть, респавн, выход).</summary>
        public static void RemoveAll(Player player)
        {
            if (player is not null)
                RemoveAll(player.ReferenceHub);
        }

        private static void RemoveAll(ReferenceHub hub)
        {
            if (!Visuals.TryGetValue(hub, out Dictionary<BodyPart, ActiveVisual>? slots))
                return;

            foreach (ActiveVisual visual in slots.Values)
                Destroy(visual);

            slots.Clear();
            Visuals.Remove(hub);
            RecountActive();
        }

        /// <summary>
        /// Приводит визуалы игрока в соответствие его ранениям.
        /// Вызывается при получении ранения, лечении и из тика, когда меняется
        /// состояние перевязки (кровь свернулась).
        /// </summary>
        /// <remarks>
        /// Метод никогда не бросает исключение наружу: его вызывают из тика системы
        /// и из команды RemoteAdmin, а обработчик команд игры молча гасит ошибки -
        /// админ увидел бы "command failed" без причины. Поэтому все ошибки
        /// логируются здесь и всегда.
        /// </remarks>
        public static void Sync(Player player, PlayerMedicalState state)
        {
            try
            {
                SyncCore(player, state);
            }
            catch (Exception exception)
            {
                VisualDebug.Failure($"Sync failed for {player?.Nickname ?? "?"}: {exception}");
            }
        }

        private static void SyncCore(Player player, PlayerMedicalState state)
        {
            if (player is null)
                return;

            if (!Config.ShowWoundVisuals)
            {
                VisualDebug.Step("Sync skipped: ShowWoundVisuals is false in config.");
                return;
            }

            if (!player.IsAlive)
            {
                VisualDebug.Step($"Sync skipped: {player.Nickname} is dead.");
                return;
            }

            ReferenceHub hub = player.ReferenceHub;

            // Определяем, какая перевязка должна быть в каждом слоте.
            Dictionary<BodyPart, Injury> desired = SelectVisibleInjuries(state);

            VisualDebug.Step($"Sync {player.Nickname}: injuries {state.Injuries.Count}, " +
                             $"visuals wanted {desired.Count}.");

            if (!Visuals.TryGetValue(hub, out Dictionary<BodyPart, ActiveVisual>? slots))
            {
                if (desired.Count == 0)
                    return;

                slots = new Dictionary<BodyPart, ActiveVisual>();
                Visuals[hub] = slots;
            }

            // Снимаем визуалы со слотов, где ранений больше нет.
            SlotBuffer.Clear();
            foreach (BodyPart slot in slots.Keys)
            {
                if (!desired.ContainsKey(slot))
                    SlotBuffer.Add(slot);
            }

            for (int i = 0; i < SlotBuffer.Count; i++)
            {
                BodyPart slot = SlotBuffer[i];
                VisualDebug.Step($"  removing visual from slot {slot} (injury healed).");
                Destroy(slots[slot]);
                slots.Remove(slot);
            }

            SlotBuffer.Clear();

            // Создаём или обновляем визуалы в оставшихся слотах.
            foreach (KeyValuePair<BodyPart, Injury> pair in desired)
            {
                string? name = WoundVisualCatalog.GetSchematicName(pair.Value);
                if (name is null)
                    continue;

                if (slots.TryGetValue(pair.Key, out ActiveVisual? existing))
                {
                    // Перевязка уже нужного вида - ничего не делаем.
                    if (existing.DressingName == name && existing.HasObjects)
                        continue;

                    VisualDebug.Step($"  replacing visual on {pair.Key}: {existing.DressingName} -> {name}.");
                    Destroy(existing);
                    slots.Remove(pair.Key);
                }

                ActiveVisual? spawned = Spawn(player, pair.Value, name);
                if (spawned is not null)
                    slots[pair.Key] = spawned;
            }

            if (slots.Count == 0)
                Visuals.Remove(hub);

            RecountActive();
        }

        /// <summary>
        /// Выбирает по одному самому тяжёлому видимому ранению на часть тела.
        /// Ограничение по числу слотов защищает сеть: 40 игроков с 6 перевязками
        /// каждый - это сотни сетевых объектов.
        /// </summary>
        private static Dictionary<BodyPart, Injury> SelectVisibleInjuries(PlayerMedicalState state)
        {
            Dictionary<BodyPart, Injury> best = new();

            for (int i = 0; i < state.Injuries.Count; i++)
            {
                Injury injury = state.Injuries[i];

                // Визуал - это перевязка, а не сама рана. Пока рану не обработали,
                // снаружи ничего не видно: бинт появляется только после аптечки.
                // Заживление бинт не снимает: он висит, пока не истечёт
                // DressingLingerSeconds, иначе перевязку не увидеть вообще -
                // большинство ранений закрывается одной ступенью, которую даёт аптечка.
                if (!injury.IsDressed)
                    continue;

                if (injury.IsHealed && !injury.KeepForDressing)
                    continue;

                if (WoundVisualCatalog.GetKind(injury.Type, injury.BodyPart) == DressingKind.None)
                    continue;

                if (!best.TryGetValue(injury.BodyPart, out Injury? current)
                    || PlayerMedicalState.Severity(injury.Type) > PlayerMedicalState.Severity(current.Type))
                {
                    best[injury.BodyPart] = injury;
                }
            }

            // Если ранений больше лимита, оставляем самые тяжёлые.
            int limit = Math.Max(1, Config.MaxWoundVisualsPerPlayer);
            if (best.Count <= limit)
                return best;

            List<KeyValuePair<BodyPart, Injury>> ordered = new(best);
            ordered.Sort((a, b) => PlayerMedicalState.Severity(b.Value.Type)
                .CompareTo(PlayerMedicalState.Severity(a.Value.Type)));

            Dictionary<BodyPart, Injury> trimmed = new();
            for (int i = 0; i < limit; i++)
                trimmed[ordered[i].Key] = ordered[i].Value;

            VisualDebug.Step($"  limit {limit}: showing {trimmed.Count} of {best.Count} injuries.");
            return trimmed;
        }

        /// <summary>Создаёт перевязку и вешает её на кость.</summary>
        private static ActiveVisual? Spawn(Player player, Injury injury, string dressingName)
        {
            // Шаг 1: кость. Без скелета крепить некуда.
            if (!BoneResolver.TryGetBone(player.ReferenceHub, injury.BodyPart, out Transform bone, out string boneReason))
            {
                VisualDebug.Problem($"{player.Nickname}: bone {injury.BodyPart} not found - {boneReason}. " +
                                    "Visual not created.");
                return null;
            }

            Vector3 offset = WoundVisualCatalog.GetOffset(injury);

            // Шаг 2: компонент, который держит перевязку на кости. Живёт на объекте
            // игрока, чтобы автоматически уничтожиться вместе с ним.
            BoneFollower follower = player.GameObject.AddComponent<BoneFollower>();
            follower.Bone = bone;
            follower.LocalOffset = offset;

            ActiveVisual visual = new()
            {
                DressingName = dressingName,
                Follower = follower,
            };

            try
            {
                // Независимые примитивы - основной production path. В отличие от MER
                // hierarchy, каждый блок имеет собственную мировую сетевую позицию.
                if (SpawnViaPrimitives(player, injury, dressingName, follower, visual, bone))
                    return visual;

                // MER остаётся только явным аварийным backend, если каталога нет.
                if (Config.UseMapEditorWoundVisuals
                    && SpawnViaMapEditor(player, injury, dressingName, follower, visual, bone))
                {
                    return visual;
                }

                Destroy(visual);
                return null;
            }
            catch (Exception exception)
            {
                VisualDebug.Failure($"Failed to build '{dressingName}': {exception}");
                Destroy(visual);
                return null;
            }
        }

        /// <summary>Спавн схематика через ProjectMER (Map Editor).</summary>
        private static bool SpawnViaMapEditor(Player player, Injury injury, string dressingName,
            BoneFollower follower, ActiveVisual visual, Transform bone)
        {
            if (!Config.UseMapEditorWoundVisuals)
                return false;

            if (!MapEditorBridge.IsAvailable)
            {
                VisualDebug.Step($"  Map Editor {MapEditorBridge.Status}; using built-in primitives.");
                return false;
            }

            // Спавним сразу на кости, чтобы схематик не мигнул в нуле карты.
            Vector3 position = follower.AnchorPosition;
            Quaternion rotation = bone.rotation;

            Component? schematic = MapEditorBridge.SpawnSchematic(dressingName, position, rotation, out string error);

            if (schematic == null)
            {
                VisualDebug.Problem($"Map Editor could not spawn '{dressingName}': {error}. " +
                                    "Falling back to built-in primitives.");
                return false;
            }

            visual.Schematic = schematic;
            follower.TrackSchematic(schematic.transform, dressingName);
            follower.Apply();

            VisualDebug.Step($"  '{dressingName}' spawned via Map Editor on bone '{bone.name}' " +
                             $"({injury.BodyPart}) at {position}, player at {player.Position}.");
            return true;
        }

        /// <summary>Запасной спавн из вкомпилированного каталога примитивов.</summary>
        private static bool SpawnViaPrimitives(Player player, Injury injury, string dressingName,
            BoneFollower follower, ActiveVisual visual, Transform bone)
        {
            WoundBlock[] blocks = WoundBlockCatalog.Get(dressingName);
            if (blocks.Length == 0)
            {
                VisualDebug.Problem($"Dressing '{dressingName}' is not in the catalog either. " +
                                    "Regenerate WoundBlockCatalog.cs via tools/gen_block_catalog.py.");
                return false;
            }

            for (int i = 0; i < blocks.Length; i++)
            {
                WoundBlock block = blocks[i];

                // spawn: false - объект уйдёт в сеть только после того, как
                // окажется на своём месте: иначе клиент на кадр увидит его в нуле.
                Primitive primitive = Primitive.Create(
                    UnityEngine.PrimitiveType.Cube,
                    PrimitiveFlags.Visible,
                    Vector3.zero,
                    Vector3.zero,
                    block.Scale,
                    false,
                    block.Color);

                PrimitiveObjectToy toy = primitive.Base;

                toy.NetworkMovementSmoothing = MovementSmoothing;

                // IsStatic отключил бы LateUpdate, а с ним и синхронизацию позиции.
                toy.NetworkIsStatic = false;

                follower.Add(toy.transform, block.Position, Quaternion.Euler(block.EulerAngles), block.Scale);
                visual.Toys.Add(toy);
            }

            // Ставим блоки на место до спавна, чтобы клиент получил верные координаты
            // с первого кадра.
            follower.Apply();

            for (int i = 0; i < visual.Toys.Count; i++)
                NetworkServer.Spawn(visual.Toys[i].gameObject);

            VisualDebug.Step($"  '{dressingName}' created from primitives: {visual.Toys.Count} blocks " +
                             $"on bone '{bone.name}' ({injury.BodyPart}) at {follower.AnchorPosition}, " +
                             $"player at {player.Position}.");
            return true;
        }

        /// <summary>Уничтожает визуал и его сетевые объекты.</summary>
        private static void Destroy(ActiveVisual visual)
        {
            // Сначала отвязываем от кости: объекты могут жить ещё кадр,
            // а скелет к тому времени уже уничтожен (смена роли).
            if (visual.Follower != null)
            {
                visual.Follower.Detach();
                UnityEngine.Object.Destroy(visual.Follower);
                visual.Follower = null;
            }

            // Схематик удаляет сам ProjectMER: у SchematicObject есть свой Destroy(),
            // но вызывать его по рефлексии не нужно - уничтожение GameObject снимает
            // и все вложенные сетевые объекты.
            if (visual.Schematic != null)
            {
                try
                {
                    NetworkServer.Destroy(visual.Schematic.gameObject);
                }
                catch (Exception exception)
                {
                    // Схематик мог быть не сетевым объектом сам по себе - тогда
                    // достаточно обычного Destroy.
                    VisualDebug.Step($"Schematic not destroyed over network ({exception.Message}); " +
                                     "removing locally.");

                    try
                    {
                        UnityEngine.Object.Destroy(visual.Schematic.gameObject);
                    }
                    catch (Exception inner)
                    {
                        VisualDebug.Step($"Schematic not destroyed: {inner.Message}");
                    }
                }

                visual.Schematic = null;
            }

            for (int i = 0; i < visual.Toys.Count; i++)
            {
                PrimitiveObjectToy toy = visual.Toys[i];

                try
                {
                    if (toy != null)
                        NetworkServer.Destroy(toy.gameObject);
                }
                catch (Exception exception)
                {
                    VisualDebug.Step($"Dressing block not destroyed: {exception.Message}");
                }
            }

            visual.Toys.Clear();
        }

        private static void RecountActive()
        {
            int total = 0;

            foreach (Dictionary<BodyPart, ActiveVisual> slots in Visuals.Values)
                total += slots.Count;

            ActiveCount = total;
        }
    }
}
