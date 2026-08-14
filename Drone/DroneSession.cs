using Exiled.API.Features;
using UnityEngine;

namespace MainCore.Drone
{
    /// <summary>
    /// Стадия работы дрона у одного игрока.
    /// </summary>
    internal enum DroneStage
    {
        /// <summary>Схематик показан перед игроком, но ещё не установлен.</summary>
        Preview,

        /// <summary>Игрок внутри дрона и управляет им.</summary>
        Piloting,

        /// <summary>Дрон летит сам: пилот вышел или получил урон.</summary>
        Abandoned,
    }

    /// <summary>
    /// Всё состояние одного дрона: тело, пилот, скорость и то, что нужно вернуть
    /// игроку при выходе.
    /// </summary>
    internal sealed class DroneSession
    {
        /// <summary>Игрок, которому принадлежит дрон.</summary>
        internal Player Owner { get; }

        internal DroneStage Stage { get; set; } = DroneStage.Preview;

        /// <summary>Схематик дрона. <c>null</c>, если ProjectMER его не выдал.</summary>
        internal Component? Body { get; set; }

        /// <summary>Позиция дрона. Ведётся отдельно от схематика: схематик - только визуал.</summary>
        internal Vector3 Position { get; set; }

        /// <summary>Направление полёта. Обновляется по камере пилота.</summary>
        internal Vector3 Forward { get; set; } = Vector3.forward;

        /// <summary>Текущая скорость в м/с. 0 - дрон падает или лежит.</summary>
        internal float Speed { get; set; }

        /// <summary>
        /// Дрон лежит на опоре и не двигается. Пока флаг стоит, гравитация не
        /// применяется - это убирает дрожание "упал на пол - оттолкнулся - упал".
        /// Сбрасывается, как только пилот снова разгоняет дрон прыжком.
        /// </summary>
        internal bool Grounded { get; set; }

        /// <summary>Даммик, изображающий тело пилота, пока он в дроне.</summary>
        internal Npc? Dummy { get; set; }

        /// <summary>Где стоял игрок до входа в дрон.</summary>
        internal Vector3 OwnerReturnPosition { get; set; }

        /// <summary>Исходный масштаб игрока: в дроне он уменьшается.</summary>
        internal Vector3 OwnerOriginalScale { get; set; } = Vector3.one;

        /// <summary>Был ли noclip разрешён игроку до входа в дрон.</summary>
        internal bool OwnerNoclipPermitted { get; set; }

        /// <summary>Был ли noclip включён у игрока до входа в дрон.</summary>
        internal bool OwnerNoclipEnabled { get; set; }

        internal DroneSession(Player owner) => Owner = owner;
    }
}