using System;
using CommandSystem;
using Exiled.API.Features;
using MainCore.Medical;

namespace MainCore.Commands
{
    /// <summary>
    /// Клиентская команда: игрок осматривает себя и узнаёт свои ранения.
    /// Без неё игрок видит только эффекты и не понимает, что именно с ним не так.
    /// </summary>
    [CommandHandler(typeof(ClientCommandHandler))]
    public sealed class SelfCheckCommand : ICommand
    {
        public string Command => "selfcheck";

        public string[] Aliases => new[] { "осмотр", "state" };

        public string Description => "Показывает ваши текущие ранения и состояние организма.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            Player? player = Player.Get(sender);

            if (player is null)
            {
                response = "Команда доступна только в игре.";
                return false;
            }

            if (!player.IsAlive)
            {
                response = "Вы мертвы.";
                return false;
            }

            response = MedicalReport.Build(player, self: true);
            return true;
        }
    }
}
