using System.Collections.Generic;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.Thirdperson;
using UnityEngine;

namespace MainCore.Medical.Visuals
{
    /// <summary>
    /// Находит Transform кости скелета игрока для крепления визуала ранения.
    /// Работает только с человеческими моделями: медицинская система на SCP не действует.
    /// </summary>
    /// <remarks>
    /// Строки причин уходят в лог сервера через <see cref="VisualDebug"/>, поэтому
    /// они только на английском и только ASCII: консоль выводит не-ASCII как '?'.
    /// </remarks>
    public static class BoneResolver
    {
        /// <summary>
        /// Соответствие частей тела костям Unity-скелета.
        /// Ранение на конечности крепится к нижней части (предплечье, голень):
        /// там перевязка выглядит естественнее и меньше проваливается в модель.
        /// </summary>
        private static readonly Dictionary<BodyPart, HumanBodyBones> Bones = new()
        {
            [BodyPart.Head] = HumanBodyBones.Head,
            [BodyPart.Torso] = HumanBodyBones.Spine,
            [BodyPart.LeftArm] = HumanBodyBones.LeftLowerArm,
            [BodyPart.RightArm] = HumanBodyBones.RightLowerArm,
            [BodyPart.LeftLeg] = HumanBodyBones.LeftLowerLeg,
            [BodyPart.RightLeg] = HumanBodyBones.RightLowerLeg,
        };

        /// <summary>
        /// Пытается получить кость игрока для указанной части тела.
        /// </summary>
        public static bool TryGetBone(ReferenceHub hub, BodyPart bodyPart, out Transform bone) =>
            TryGetBone(hub, bodyPart, out bone, out _);

        /// <summary>
        /// Пытается получить кость и объясняет причину неудачи.
        /// Причина нужна для диагностики: без неё непонятно, почему визуал не появился.
        /// </summary>
        public static bool TryGetBone(ReferenceHub hub, BodyPart bodyPart, out Transform bone, out string reason)
        {
            bone = null!;

            if (hub is null)
            {
                reason = "ReferenceHub == null";
                return false;
            }

            if (!Bones.TryGetValue(bodyPart, out HumanBodyBones boneType))
            {
                reason = $"no bone mapped for body part {bodyPart}";
                return false;
            }

            // Скелет есть только у ролей с моделью от первого лица.
            if (hub.roleManager.CurrentRole is not IFpcRole fpcRole)
            {
                reason = $"role {hub.roleManager.CurrentRole?.RoleTypeId} has no skeleton (not IFpcRole)";
                return false;
            }

            FirstPersonMovementModule? module = fpcRole.FpcModule;
            if (module is null)
            {
                reason = "FpcModule == null";
                return false;
            }

            // Анимированная модель - признак того, что скелет уже собран.
            if (module.CharacterModelInstance is not AnimatedCharacterModel)
            {
                string actual = module.CharacterModelInstance is null
                    ? "null"
                    : module.CharacterModelInstance.GetType().Name;

                reason = $"character model is not animated ({actual}) - no skeleton";
                return false;
            }

            // Animator ищем в детях: свойство модели недоступно снаружи сборки игры.
            Animator? animator = hub.gameObject.GetComponentInChildren<Animator>(true);
            if (animator is null)
            {
                reason = "Animator not found in player hierarchy";
                return false;
            }

            if (!animator.isHuman)
            {
                string avatar = animator.avatar is null ? "null" : animator.avatar.name;
                reason = $"Animator is not humanoid (avatar: {avatar})";
                return false;
            }

            Transform? found = animator.GetBoneTransform(boneType);
            if (found is null)
            {
                reason = $"bone {boneType} is missing from the avatar";
                return false;
            }

            bone = found;
            reason = found.name;
            return true;
        }
    }
}
