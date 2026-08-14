using UnityEngine;

namespace MainCore.Medical.Visuals
{
    /// <summary>Вид перевязки. Определяет, какой схематик показать.</summary>
    public enum DressingKind
    {
        /// <summary>Визуала нет: ранение снаружи не видно.</summary>
        None,

        /// <summary>Обычный бинт.</summary>
        Bandage,

        /// <summary>Жгут (артериальное кровотечение на конечности).</summary>
        Tourniquet,

        /// <summary>Шина (перелом).</summary>
        Splint,

        /// <summary>Ожоговая повязка.</summary>
        Burn,
    }

    /// <summary>Состояние перевязки.</summary>
    public enum DressingState
    {
        /// <summary>Чистая: кровь остановлена, перевязка свежая.</summary>
        Clean,

        /// <summary>Грязная: кровь остановлена, но перевязка старая.</summary>
        Dirty,

        /// <summary>В крови: рана всё ещё кровоточит, нужна перевязка.</summary>
        Blood,
    }

    /// <summary>
    /// Сопоставляет ранение с именем схематика и параметрами крепления.
    /// Имена совпадают с папками в конфиге ProjectMER: Med_&lt;Вид&gt;_&lt;Слот&gt;_&lt;Состояние&gt;.
    /// </summary>
    public static class WoundVisualCatalog
    {
        /// <summary>Схематик появляется только после того, как ранение стало видимым.</summary>
        public static DressingKind GetKind(InjuryType type, BodyPart bodyPart) => type switch
        {
            InjuryType.LightWound => DressingKind.Bandage,
            InjuryType.CapillaryBleeding => DressingKind.Bandage,
            InjuryType.VenousBleeding => DressingKind.Bandage,

            // Жгут накладывают только на конечность: на шею или корпус - нельзя.
            InjuryType.ArterialBleeding => bodyPart.IsLimb()
                ? DressingKind.Tourniquet
                : DressingKind.Bandage,

            InjuryType.Fracture => DressingKind.Splint,
            InjuryType.Burn => DressingKind.Burn,

            // Ушиб и внутреннее кровотечение снаружи не видны - это осознанно:
            // без осмотра медиком диагноз не поставить.
            InjuryType.Contusion => DressingKind.None,
            InjuryType.InternalBleeding => DressingKind.None,

            _ => DressingKind.None,
        };

        /// <summary>
        /// Состояние перевязки по её свежести.
        /// </summary>
        /// <remarks>
        /// Бинт накладывают чистым, и какое-то время он таким и остаётся
        /// (<see cref="Config.DressingCleanSeconds"/>). Потом он пачкается: если рана
        /// всё ещё кровоточит - проступает кровь (Blood), если кровь уже свернулась -
        /// бинт просто грязный (Dirty). Кровь на бинте - главный сигнал медику,
        /// что перевязку надо менять.
        /// </remarks>
        public static DressingState GetState(Injury injury)
        {
            float cleanSeconds = MainCorePlugin.Instance.Config.DressingCleanSeconds;

            // Свежая перевязка выглядит чистой независимо от состояния раны.
            if (injury.DressedAge < cleanSeconds)
                return DressingState.Clean;

            // Бинт уже несвежий: кровь проступает, пока рана кровоточит.
            return injury.IsActivelyBleeding ? DressingState.Blood : DressingState.Dirty;
        }

        /// <summary>
        /// Слот схематика. Левая и правая конечности используют один и тот же
        /// схематик - он зеркалится поворотом кости, что вдвое сокращает число ассетов.
        /// </summary>
        public static string GetSlot(BodyPart bodyPart) => bodyPart switch
        {
            BodyPart.Head => "Head",
            BodyPart.Torso => "Torso",
            BodyPart.LeftArm or BodyPart.RightArm => "Arm",
            BodyPart.LeftLeg or BodyPart.RightLeg => "Leg",
            _ => "Torso",
        };

        /// <summary>Собирает имя схематика для ProjectMER.</summary>
        public static string? GetSchematicName(Injury injury)
        {
            DressingKind kind = GetKind(injury.Type, injury.BodyPart);
            if (kind == DressingKind.None)
                return null;

            return $"Med_{kind}_{GetSlot(injury.BodyPart)}_{GetState(injury)}";
        }

        /// <summary>
        /// Смещение перевязки вдоль кости. Кость находится в суставе, поэтому
        /// повязку нужно сдвинуть к середине сегмента, иначе она окажется на локте.
        /// Ось Y кости в скелете SCP:SL направлена вдоль сегмента.
        /// </summary>
        public static Vector3 GetOffset(Injury injury)
        {
            return injury.BodyPart switch
            {
                // Кость головы в основании черепа - поднимаем до уровня лба.
                BodyPart.Head => new Vector3(0f, 0.10f, 0f),

                // Spine в районе живота - поднимаем к груди.
                BodyPart.Torso => new Vector3(0f, 0.15f, 0f),

                // Предплечье: середина между локтем и кистью.
                BodyPart.LeftArm or BodyPart.RightArm => new Vector3(0f, 0.12f, 0f),

                // Голень: середина между коленом и стопой.
                BodyPart.LeftLeg or BodyPart.RightLeg => new Vector3(0f, 0.18f, 0f),

                _ => Vector3.zero,
            };
        }
    }
}
