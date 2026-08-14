using System.Collections.Generic;
using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.API.Features.Items;
using UnityEngine;

namespace MainCore.Drone
{
    /// <summary>
    /// Стадия работы дрона у одного игрока.
    /// </summary>
    internal enum DroneStage
    {
        /// <summary>Схематик следует за взглядом, точка установки ещё не зафиксирована.</summary>
        Preview,

        /// <summary>Дрон поставлен на землю (свет белый), ждёт второго нажатия для входа.</summary>
        Placed,

        /// <summary>Игрок внутри дрона и управляет им.</summary>
        Piloting,

        /// <summary>Дрон брошен: пилот вышел. Летит/лежит сам до истечения таймера.</summary>
        Abandoned,
    }

    /// <summary>
    /// Всё состояние одного дрона: тело, свет, HP, а также снимок игрока, который
    /// нужно вернуть при выходе.
    /// </summary>
    /// <remarks>
    /// Управление построено по схеме «дрон следует за пилотом»: пилот летает нативным
    /// noclip (клиент сам двигает его по WASD и взгляду), а сервер лишь ведёт модель
    /// дрона за фактической позицией игрока. Поэтому здесь хранится
    /// <see cref="LastPilotPosition"/> - опорная точка для расчёта пройденного за тик
    /// пути и ограничения скорости.
    /// </remarks>
    internal sealed class DroneSession
    {
        internal Player Owner { get; }

        internal DroneStage Stage { get; set; } = DroneStage.Preview;

        /// <summary>Схематик дрона (визуал). <c>null</c>, если ProjectMER его не выдал.</summary>
        internal Component? Body { get; set; }

        /// <summary>
        /// Сетевые дети схематика, отвязанные от корня, с их смещением относительно
        /// центра дрона. Двигаются в мировых координатах каждый тик - иначе клиент
        /// удержал бы координаты спавна (см. BoneFollower / docs/Schematics.md).
        /// </summary>
        internal List<DroneBodyBlock> BodyBlocks { get; } = new List<DroneBodyBlock>();

        /// <summary>
        /// Источник света над дроном (красный/зелёный/белый индикатор).
        /// Хранится как <see cref="object"/>: тип-тулза Exiled Light не наследует
        /// Unity <see cref="Component"/>, поэтому в менеджере он приводится через
        /// <c>is ToyLight</c>.
        /// </summary>
        internal object? Light { get; set; }

        /// <summary>Позиция дрона. Ведётся отдельно от схематика.</summary>
        internal Vector3 Position { get; set; }

        /// <summary>Направление полёта (по взгляду пилота).</summary>
        internal Vector3 Forward { get; set; } = Vector3.forward;

        /// <summary>HP дрона. При нуле дрон взрывается.</summary>
        internal float Health { get; set; }

        /// <summary>Максимальная скорость дрона в м/с (регулируется прыжком/alt).</summary>
        internal float SpeedLimit { get; set; }

        /// <summary>Позиция пилота на прошлом тике: опора для расчёта смещения.</summary>
        internal Vector3 LastPilotPosition { get; set; }

        /// <summary>Даммик, изображающий тело пилота, пока он в дроне.</summary>
        internal Npc? Dummy { get; set; }

        // ------------------------------------------------------------ снимок игрока

        internal Vector3 OwnerReturnPosition { get; set; }

        internal Vector3 OwnerOriginalScale { get; set; } = Vector3.one;

        internal bool OwnerNoclipPermitted { get; set; }

        internal bool OwnerNoclipEnabled { get; set; }

        internal float OwnerHealth { get; set; }

        internal float OwnerMaxHealth { get; set; }

        internal string OwnerCustomInfo { get; set; } = string.Empty;

        /// <summary>Полный инвентарь пилота до входа: восстанавливается на выходе.</summary>
        internal List<ItemType> OwnerItems { get; } = new List<ItemType>();

        /// <summary>Резерв патронов пилота до входа (ключ - тип патрона как ItemType).</summary>
        internal Dictionary<ItemType, ushort> OwnerAmmo { get; } = new Dictionary<ItemType, ushort>();

        internal DroneSession(Player owner) => Owner = owner;
    }

    /// <summary>Один сетевой блок схематика дрона и его смещение от центра.</summary>
    internal struct DroneBodyBlock
    {
        internal Transform Transform;

        internal Vector3 Offset;

        internal Quaternion Rotation;

        internal Vector3 Scale;
    }
}
