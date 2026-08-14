using System;
using Exiled.API.Enums;

namespace MainCore.Medical
{
    /// <summary>
    /// Описание эффекта, который выдаётся ранением.
    /// Имя эффекта хранится строкой и разрешается в <see cref="EffectType"/> в рантайме,
    /// чтобы система не ломалась при переименовании эффектов в игре.
    /// </summary>
    public sealed class InjuryEffect
    {
        /// <summary>
        /// Результат разбора имени эффекта. Считается один раз при первом обращении:
        /// эффекты проверяются каждый тик для каждого ранения, и парсить enum
        /// строкой на каждой проверке слишком дорого.
        /// </summary>
        private EffectType? resolved;

        private bool resolveAttempted;

        public InjuryEffect(string effectName, byte intensity, float duration = 0f, string? fallbackEffectName = null)
        {
            EffectName = effectName;
            FallbackEffectName = fallbackEffectName;
            Intensity = intensity;
            Duration = duration;
        }

        /// <summary>Основное имя эффекта (значение <see cref="EffectType"/>).</summary>
        public string EffectName { get; }

        /// <summary>Резервное имя эффекта, если основного нет в текущей версии игры.</summary>
        public string? FallbackEffectName { get; }

        /// <summary>Базовая интенсивность эффекта (до учёта лечения).</summary>
        public byte Intensity { get; }

        /// <summary>
        /// Длительность эффекта. 0 - эффект постоянный и продлевается каждый тик,
        /// значение больше 0 - эффект выдаётся один раз при получении ранения.
        /// </summary>
        public float Duration { get; }

        /// <summary>Выдаётся ли эффект только один раз (при получении ранения).</summary>
        public bool IsOneShot => Duration > 0f;

        /// <summary>
        /// Пытается получить <see cref="EffectType"/> для этого эффекта.
        /// </summary>
        public bool TryResolve(out EffectType effectType)
        {
            if (!resolveAttempted)
            {
                resolveAttempted = true;
                resolved = Resolve();
            }

            if (resolved.HasValue)
            {
                effectType = resolved.Value;
                return true;
            }

            effectType = default;
            return false;
        }

        private EffectType? Resolve()
        {
            if (Enum.TryParse(EffectName, true, out EffectType parsed))
                return parsed;

            if (!string.IsNullOrEmpty(FallbackEffectName) && Enum.TryParse(FallbackEffectName, true, out parsed))
                return parsed;

            return null;
        }
    }
}
