namespace MainCore.Medical
{
    /// <summary>
    /// Часть тела, в которую нанесено ранение.
    /// </summary>
    public enum BodyPart
    {
        Head,
        Torso,
        LeftArm,
        RightArm,
        LeftLeg,
        RightLeg,
    }

    /// <summary>
    /// Тип ранения.
    /// </summary>
    public enum InjuryType
    {
        /// <summary>Лёгкое ранение. Лечится одним использованием аптечки.</summary>
        LightWound,

        /// <summary>Капиллярное кровотечение.</summary>
        CapillaryBleeding,

        /// <summary>Венозное кровотечение.</summary>
        VenousBleeding,

        /// <summary>Артериальное кровотечение.</summary>
        ArterialBleeding,

        /// <summary>Ушиб (падение, удар).</summary>
        Contusion,

        /// <summary>Перелом.</summary>
        Fracture,

        /// <summary>Ожог.</summary>
        Burn,

        /// <summary>Внутреннее кровотечение.</summary>
        InternalBleeding,
    }

    public static class BodyPartExtensions
    {
        public static bool IsLeg(this BodyPart part) => part == BodyPart.LeftLeg || part == BodyPart.RightLeg;

        public static bool IsArm(this BodyPart part) => part == BodyPart.LeftArm || part == BodyPart.RightArm;

        public static bool IsLimb(this BodyPart part) => part.IsLeg() || part.IsArm();
    }
}
