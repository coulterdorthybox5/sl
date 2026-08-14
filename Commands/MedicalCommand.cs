using System;
using CommandSystem;
using Exiled.API.Features;
using Exiled.Permissions.Extensions;
using MainCore.Medical;

namespace MainCore.Commands
{
    /// <summary>
    /// Серверная команда осмотра игрока: <c>medical &lt;ник или id&gt;</c>.
    /// Нужна администрации и медикам для диагностики без гадания по эффектам.
    /// </summary>
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    public sealed class MedicalCommand : ICommand
    {
        public string Command => "medical";

        public string[] Aliases => new[] { "med" };

        public string Description => "Показывает медицинское состояние игрока. Использование: medical <ник или id>";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission("maincore.medical"))
            {
                response = "Недостаточно прав (требуется maincore.medical).";
                return false;
            }

            if (arguments.Count < 1)
            {
                response = "Укажите игрока: medical <ник или id>";
                return false;
            }

            string query = string.Join(" ", arguments);
            Player? target = Player.Get(query);

            if (target is null)
            {
                response = $"Игрок '{query}' не найден.";
                return false;
            }

            response = MedicalReport.Build(target, self: false);
            return true;
        }
    }
}
