using System;
using CommandSystem;
using Exiled.API.Features;
using Exiled.Permissions.Extensions;
using MainCore.Medical.Visuals;

namespace MainCore.Commands
{
    /// <summary>
    /// Диагностика визуала ранений: <c>medvisual [ник]</c>.
    /// Показывает конфиг, каталог перевязок и состояние костей игрока.
    /// </summary>
    /// <remarks>
    /// Ответы только ASCII: RemoteAdmin выводит не-ASCII символы как '?'.
    /// </remarks>
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    public sealed class VisualDebugCommand : ICommand
    {
        public string Command => "medvisual";

        public string[] Aliases => new[] { "medvis", "woundcheck" };

        public string Description =>
            "Wound visual diagnostics. Usage: medvisual [nick] | medvisual test <nick> [dressing] " +
            "| medvisual bone <nick> <bodypart> [dressing]";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission("maincore.wound"))
            {
                response = "Not enough permissions (maincore.wound required).";
                return false;
            }

            string[] args = arguments.ToArray();

            // medvisual test <ник> - статичный спавн перед игроком, без костей.
            // Проверяет примитивы и сеть в отрыве от привязки к скелету.
            if (args.Length >= 1 && args[0].Equals("test", StringComparison.OrdinalIgnoreCase))
            {
                string name = args.Length >= 3 ? args[2] : "Med_Bandage_Head_Clean";
                Player? subject = args.Length >= 2 ? Player.Get(args[1]) : Player.Get(sender);

                if (subject is null)
                {
                    response = "Player not found. Usage: medvisual test <nick> [dressing name]";
                    return false;
                }

                response = VisualSelfTest.Run(subject, name);

                // Дублируем в лог: ответ RemoteAdmin нигде не сохраняется, и разбирать
                // потом «что показал тест» было невозможно.
                VisualDebug.Step("medvisual test:\n" + response);
                return true;
            }

            // medvisual bone <ник> <часть> - бинт прямо на кости, как настоящий визуал,
            // но без ранений и аптечек. Отвечает, работает ли привязка к скелету.
            if (args.Length >= 1 && args[0].Equals("bone", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 3)
                {
                    response = "Usage: medvisual bone <nick> <bodypart> [dressing]\n" +
                               $"Body parts: {string.Join(", ", Enum.GetNames(typeof(MainCore.Medical.BodyPart)))}";
                    return false;
                }

                Player? subject = Player.Get(args[1]);
                if (subject is null)
                {
                    response = $"Player '{args[1]}' not found.";
                    return false;
                }

                if (!Enum.TryParse(args[2], true, out MainCore.Medical.BodyPart part))
                {
                    response = $"Unknown body part '{args[2]}'.\n" +
                               $"Body parts: {string.Join(", ", Enum.GetNames(typeof(MainCore.Medical.BodyPart)))}";
                    return false;
                }

                // Имя перевязки по умолчанию подбираем под слот, иначе для руки
                // подставился бы Head-бинт и результат сбивал бы с толку.
                string dressing = args.Length >= 4
                    ? args[3]
                    : $"Med_Bandage_{WoundVisualCatalog.GetSlot(part)}_Clean";

                response = VisualSelfTest.RunOnBone(subject, part, dressing);
                VisualDebug.Step("medvisual bone:\n" + response);
                return true;
            }

            Player? player = args.Length >= 1 ? Player.Get(args[0]) : Player.Get(sender);

            if (args.Length >= 1 && player is null)
            {
                response = $"Player '{args[0]}' not found.";
                return false;
            }

            response = VisualDebug.BuildReport(player);
            VisualDebug.Step("medvisual report:\n" + response);
            return true;
        }
    }
}
