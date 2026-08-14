using System;
using CommandSystem;
using Exiled.API.Features;
using Exiled.Permissions.Extensions;
using MainCore.Medical;
// using MainCore.Medical.Visuals; // ВРЕМЕННО: система видимых ранений отключена

namespace MainCore.Commands
{
    /// <summary>
    /// Отладочная команда для проверки визуала ранений без стрельбы:
    /// <c>wound &lt;ник&gt; &lt;тип&gt; &lt;часть тела&gt;</c>, а также <c>wound clear &lt;ник&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Ответы только ASCII: RemoteAdmin и консоль сервера выводят не-ASCII
    /// символы как '?', и текст становится нечитаемым.
    /// </remarks>
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    public sealed class WoundCommand : ICommand
    {
        public string Command => "wound";

        public string[] Aliases => new[] { "injure" };

        public string Description =>
            "Inflict an injury to test wound visuals. Usage: wound <nick> <type> <bodypart> | wound clear <nick> | wound list";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission("maincore.wound"))
            {
                response = "Not enough permissions (maincore.wound required).";
                return false;
            }

            string[] args = arguments.ToArray();

            if (args.Length == 0)
            {
                response = BuildHelp();
                return false;
            }

            if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                response = BuildHelp();
                return true;
            }

            if (args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2)
                {
                    response = "Specify a player: wound clear <nick>";
                    return false;
                }

                Player? target = Player.Get(args[1]);
                if (target is null)
                {
                    response = $"Player '{args[1]}' not found.";
                    return false;
                }

                MedicalManager.ResetPlayer(target);
                response = $"Injuries of {target.Nickname} cleared.";
                return true;
            }

            if (args.Length < 3)
            {
                response = BuildHelp();
                return false;
            }

            Player? player = Player.Get(args[0]);
            if (player is null)
            {
                response = $"Player '{args[0]}' not found.";
                return false;
            }

            if (!player.IsAlive)
            {
                response = $"{player.Nickname} is dead.";
                return false;
            }

            if (!Enum.TryParse(args[1], true, out InjuryType type))
            {
                response = $"Unknown injury type '{args[1]}'.\n{BuildHelp()}";
                return false;
            }

            if (!Enum.TryParse(args[2], true, out BodyPart bodyPart))
            {
                response = $"Unknown body part '{args[2]}'.\n{BuildHelp()}";
                return false;
            }

            Injury? injury = MedicalManager.Inflict(player, type, bodyPart);
            if (injury is null)
            {
                response = "Failed to inflict the injury.";
                return false;
            }

            // "dressed" в конце - сразу наложить бинт, не тратя аптечку.
            // Без этого визуал не проверить: он появляется только после лечения.
            bool dressNow = args.Length >= 4 && args[3].Equals("dressed", StringComparison.OrdinalIgnoreCase);
            if (dressNow)
                MedicalManager.DressInjury(player, injury);

            // ВРЕМЕННО: визуал ранений отключён
            // string? dressing = WoundVisualCatalog.GetSchematicName(injury);
            //
            // // Сколько блоков реально нашлось в каталоге - главный признак того,
            // // появится ли перевязка вообще.
            // string visual = dressing is null
            //     ? "none (by design)"
            //     : $"{dressing} ({WoundBlockCatalog.Get(dressing).Length} blocks)";

            response = $"{player.Nickname}: {type} on {bodyPart}\n" +
                       $"Dressed: {injury.IsDressed}" +
                       (injury.IsDressed ? string.Empty : "  (no visual until treated - add 'dressed' to force it)") + "\n" +
                       $"Active visuals on server: disabled (visual system commented out)";
            return true;
        }

        private static string BuildHelp()
        {
            return "wound <nick> <type> <bodypart> [dressed]\n" +
                   $"Types: {string.Join(", ", Enum.GetNames(typeof(InjuryType)))}\n" +
                   $"Body parts: {string.Join(", ", Enum.GetNames(typeof(BodyPart)))}\n" +
                   "A dressing is only visible after the wound is treated (medkit).\n" +
                   "Add 'dressed' to apply a bandage immediately for testing.\n" +
                   "Also: wound clear <nick>";
        }
    }
}
