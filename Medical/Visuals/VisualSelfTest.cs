using System;
using System.Text;
using AdminToys;
using Exiled.API.Features;
using Exiled.API.Features.Toys;
using MEC;
using Mirror;
using UnityEngine;

namespace MainCore.Medical.Visuals
{
    /// <summary>
    /// Пробные спавны перевязок для диагностики. Разделяют две независимые причины
    /// «ничего не видно»: сами примитивы и привязка к костям.
    /// </summary>
    /// <remarks>
    /// Сообщения только ASCII: консоль сервера не выводит не-ASCII символы.
    /// </remarks>
    public static class VisualSelfTest
    {
        /// <summary>Через сколько секунд тестовый объект исчезает сам.</summary>
        private const float LifetimeSeconds = 30f;

        /// <summary>
        /// Спавнит перевязку в метре перед игроком, без всякой привязки к костям.
        /// Если её видно - примитивы и сеть работают, и дело в костях.
        /// </summary>
        public static string Run(Player player, string dressingName)
        {
            StringBuilder builder = new();
            builder.AppendLine($"=== Dressing test '{dressingName}' (static, no bone) ===");
            builder.AppendLine($"Map Editor: {MapEditorBridge.Status}");

            try
            {
                // Метр перед игроком на уровне груди - хорошо видно.
                Vector3 origin = player.Position + (player.GameObject.transform.forward * 1f);

                // Основной путь - Map Editor: он и в бою используется, поэтому
                // тест должен проверять именно его.
                if (MapEditorBridge.IsAvailable)
                {
                    Component? schematic = MapEditorBridge.SpawnSchematic(
                        dressingName, origin, Quaternion.identity, out string error);

                    if (schematic != null)
                    {
                        builder.AppendLine("RESULT: spawned via Map Editor.");
                        builder.AppendLine($"  world position: {origin}");
                        builder.AppendLine($"  player position: {player.Position}");
                        builder.AppendLine($"  It stays for {LifetimeSeconds:0}s, then vanishes.");

                        GameObject root = schematic.gameObject;
                        Timing.CallDelayed(LifetimeSeconds, () => DestroySchematic(root));
                        return builder.ToString();
                    }

                    builder.AppendLine($"  Map Editor failed: {error}");
                    builder.AppendLine("  Trying built-in primitives instead.");
                }

                WoundBlock[] blocks = WoundBlockCatalog.Get(dressingName);

                if (blocks.Length == 0)
                {
                    builder.AppendLine("NOT IN CATALOG either.");
                    builder.AppendLine($"Available dressings: {WoundBlockCatalog.Count}.");
                    builder.AppendLine("Naming: Med_<Kind>_<Slot>_<State>, e.g. Med_Bandage_Head_Clean.");
                    return builder.ToString();
                }

                PrimitiveObjectToy[] toys = SpawnBlocks(blocks, origin, Quaternion.identity);

                builder.AppendLine($"RESULT: spawned {toys.Length} primitives (fallback path).");
                builder.AppendLine($"  world position: {origin}");
                builder.AppendLine($"  player position: {player.Position}");
                builder.AppendLine($"  It stays for {LifetimeSeconds:0}s, then vanishes.");
                builder.AppendLine("  If you do NOT see it, the problem is primitives/network, not bones.");

                DespawnAfterLifetime(toys);
                return builder.ToString();
            }
            catch (Exception exception)
            {
                builder.AppendLine($"EXCEPTION: {exception.GetType().Name}: {exception.Message}");
                builder.AppendLine(exception.StackTrace);
                return builder.ToString();
            }
        }

        /// <summary>
        /// Спавнит бинт прямо на кости игрока и следит за ней - как настоящий визуал,
        /// но без ранений и аптечек. Отвечает на вопрос «привязка к костям работает?».
        /// </summary>
        public static string RunOnBone(Player player, BodyPart bodyPart, string dressingName)
        {
            StringBuilder builder = new();
            builder.AppendLine($"=== Bone test '{dressingName}' on {bodyPart} ===");
            builder.AppendLine($"Player: {player.Nickname}, role {player.Role.Type}, alive {player.IsAlive}");
            builder.AppendLine($"Map Editor: {MapEditorBridge.Status}");

            if (!BoneResolver.TryGetBone(player.ReferenceHub, bodyPart, out Transform bone, out string reason))
            {
                builder.AppendLine($"BONE NOT FOUND ({bodyPart}): {reason}");
                builder.AppendLine("Without a skeleton there is nothing to attach to (SCP roles have none).");
                return builder.ToString();
            }

            builder.AppendLine($"Bone: '{bone.name}' at world {bone.position}");

            try
            {
                // Тот же путь, что у настоящего визуала: следящий компонент без родителя.
                BoneFollower follower = player.GameObject.AddComponent<BoneFollower>();
                follower.Bone = bone;
                follower.LocalOffset = OffsetFor(bodyPart);

                // Основной путь - Map Editor.
                if (MapEditorBridge.IsAvailable)
                {
                    Component? schematic = MapEditorBridge.SpawnSchematic(
                        dressingName, follower.AnchorPosition, bone.rotation, out string error);

                    if (schematic != null)
                    {
                        follower.Schematic = schematic.transform;
                        follower.Apply();

                        builder.AppendLine("RESULT: spawned via Map Editor and attached to the bone.");
                        builder.AppendLine($"  anchor world position: {follower.AnchorPosition}");
                        builder.AppendLine($"  schematic position:    {schematic.transform.position}");
                        builder.AppendLine($"  player position:       {player.Position}");
                        builder.AppendLine($"  It follows the bone for {LifetimeSeconds:0}s, then vanishes.");
                        builder.AppendLine("  Anchor must be close to the player. If it is near (0,0,0),");
                        builder.AppendLine("  the bone lookup or the offset is wrong, not the network.");

                        GameObject root = schematic.gameObject;
                        Timing.CallDelayed(LifetimeSeconds, () =>
                        {
                            if (follower != null)
                            {
                                follower.Detach();
                                UnityEngine.Object.Destroy(follower);
                            }

                            DestroySchematic(root);
                        });

                        return builder.ToString();
                    }

                    builder.AppendLine($"  Map Editor failed: {error}");
                    builder.AppendLine("  Trying built-in primitives instead.");
                }

                WoundBlock[] blocks = WoundBlockCatalog.Get(dressingName);
                if (blocks.Length == 0)
                {
                    builder.AppendLine($"NOT IN CATALOG either: '{dressingName}'.");
                    UnityEngine.Object.Destroy(follower);
                    return builder.ToString();
                }

                PrimitiveObjectToy[] toys = new PrimitiveObjectToy[blocks.Length];

                for (int i = 0; i < blocks.Length; i++)
                {
                    WoundBlock block = blocks[i];

                    Primitive primitive = Primitive.Create(
                        UnityEngine.PrimitiveType.Cube,
                        PrimitiveFlags.Visible,
                        Vector3.zero,
                        Vector3.zero,
                        block.Scale,
                        false,
                        block.Color);

                    PrimitiveObjectToy toy = primitive.Base;
                    toy.NetworkMovementSmoothing = 60;
                    toy.NetworkIsStatic = false;

                    follower.Add(toy.transform, block.Position, Quaternion.Euler(block.EulerAngles), block.Scale);
                    toys[i] = toy;
                }

                // Ставим на место до спавна, чтобы клиент сразу получил верные координаты.
                follower.Apply();

                for (int i = 0; i < toys.Length; i++)
                    NetworkServer.Spawn(toys[i].gameObject);

                builder.AppendLine($"RESULT: spawned {toys.Length} blocks (fallback path).");
                builder.AppendLine($"  anchor world position: {follower.AnchorPosition}");
                builder.AppendLine($"  first block position:  {toys[0].transform.position}");
                builder.AppendLine($"  player position:       {player.Position}");
                builder.AppendLine($"  It follows the bone for {LifetimeSeconds:0}s, then vanishes.");
                builder.AppendLine("  Anchor must be close to the player. If it is near (0,0,0), the");
                builder.AppendLine("  bone lookup or the offset is wrong, not the network.");

                Timing.CallDelayed(LifetimeSeconds, () =>
                {
                    if (follower != null)
                    {
                        follower.Detach();
                        UnityEngine.Object.Destroy(follower);
                    }

                    Despawn(toys);
                });

                return builder.ToString();
            }
            catch (Exception exception)
            {
                builder.AppendLine($"EXCEPTION: {exception.GetType().Name}: {exception.Message}");
                builder.AppendLine(exception.StackTrace);
                return builder.ToString();
            }
        }

        /// <summary>Смещение к середине сегмента - то же, что у настоящего визуала.</summary>
        private static Vector3 OffsetFor(BodyPart bodyPart) => bodyPart switch
        {
            BodyPart.Head => new Vector3(0f, 0.10f, 0f),
            BodyPart.Torso => new Vector3(0f, 0.15f, 0f),
            BodyPart.LeftArm or BodyPart.RightArm => new Vector3(0f, 0.12f, 0f),
            BodyPart.LeftLeg or BodyPart.RightLeg => new Vector3(0f, 0.18f, 0f),
            _ => Vector3.zero,
        };

        private static PrimitiveObjectToy[] SpawnBlocks(WoundBlock[] blocks, Vector3 origin, Quaternion rotation)
        {
            PrimitiveObjectToy[] toys = new PrimitiveObjectToy[blocks.Length];

            for (int i = 0; i < blocks.Length; i++)
            {
                WoundBlock block = blocks[i];

                // Позиция задаётся сразу, поэтому spawn: true безопасен.
                Primitive primitive = Primitive.Create(
                    UnityEngine.PrimitiveType.Cube,
                    PrimitiveFlags.Visible,
                    origin + (rotation * block.Position),
                    (rotation * Quaternion.Euler(block.EulerAngles)).eulerAngles,
                    block.Scale,
                    true,
                    block.Color);

                toys[i] = primitive.Base;
            }

            return toys;
        }

        private static void DespawnAfterLifetime(PrimitiveObjectToy[] toys) =>
            Timing.CallDelayed(LifetimeSeconds, () => Despawn(toys));

        /// <summary>Убирает схематик Map Editor вместе со всеми его блоками.</summary>
        private static void DestroySchematic(GameObject root)
        {
            if (root == null)
                return;

            try
            {
                NetworkServer.Destroy(root);
            }
            catch (Exception exception)
            {
                VisualDebug.Step($"Test schematic not destroyed over network ({exception.Message}); " +
                                 "removing locally.");

                try
                {
                    UnityEngine.Object.Destroy(root);
                }
                catch (Exception inner)
                {
                    VisualDebug.Step($"Test schematic not destroyed: {inner.Message}");
                }
            }
        }

        private static void Despawn(PrimitiveObjectToy[] toys)
        {
            for (int i = 0; i < toys.Length; i++)
            {
                try
                {
                    if (toys[i] != null)
                        NetworkServer.Destroy(toys[i].gameObject);
                }
                catch (Exception exception)
                {
                    VisualDebug.Step($"Test block not destroyed: {exception.Message}");
                }
            }
        }
    }
}
