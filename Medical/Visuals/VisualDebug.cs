using System;
using System.Text;
using Exiled.API.Features;

namespace MainCore.Medical.Visuals
{
    /// <summary>
    /// Диагностика визуала ранений. Пишет в консоль сервера каждый шаг спавна,
    /// чтобы по логу было видно, на чём именно всё оборвалось.
    /// </summary>
    /// <remarks>
    /// Все сообщения только на английском и только ASCII: консоль сервера выводит
    /// не-ASCII символы как '?', и лог становится нечитаемым.
    /// </remarks>
    public static class VisualDebug
    {
        private static bool Enabled => MainCorePlugin.Instance?.Config?.WoundVisualDebug ?? true;

        /// <summary>Обычный шаг: виден только при включённой отладке визуала.</summary>
        public static void Step(string message)
        {
            if (Enabled)
                Log.Info($"[Medical:Visual] {message}");
        }

        /// <summary>Проблема, из-за которой визуал не появится. Пишется всегда.</summary>
        public static void Problem(string message) => Log.Warn($"[Medical:Visual] {message}");

        /// <summary>
        /// Ошибка. Пишется всегда: обработчик команд игры гасит исключения молча,
        /// поэтому без своего лога сбой выглядел бы как полное отсутствие реакции.
        /// </summary>
        public static void Failure(string message) => Log.Error($"[Medical:Visual] {message}");

        /// <summary>
        /// Полный отчёт о состоянии системы визуала. Используется командой диагностики.
        /// </summary>
        public static string BuildReport(Player? player)
        {
            Config config = MainCorePlugin.Instance.Config;

            StringBuilder builder = new();
            builder.AppendLine("=== Wound visual diagnostics ===");
            builder.AppendLine($"ShowWoundVisuals: {config.ShowWoundVisuals}");
            builder.AppendLine($"UseMapEditorWoundVisuals: {config.UseMapEditorWoundVisuals}");
            builder.AppendLine($"WoundVisualDebug: {config.WoundVisualDebug}");
            builder.AppendLine($"WoundVisualDeepDebug: {config.WoundVisualDeepDebug}");
            builder.AppendLine($"WoundVisualDebugInterval: {config.WoundVisualDebugInterval}s");
            builder.AppendLine($"MaxWoundVisualsPerPlayer: {config.MaxWoundVisualsPerPlayer}");
            builder.AppendLine($"WoundVisualSpawnDelay: {config.WoundVisualSpawnDelay}s");
            builder.AppendLine($"Active visuals: {WoundVisualManager.ActiveCount}");

            // Основной путь спавна - ProjectMER; если его нет, работает запасной
            // каталог примитивов, вкомпилированный в плагин.
            builder.AppendLine($"Map Editor (ProjectMER): {MapEditorBridge.Status}");
            builder.AppendLine($"Fallback dressings in catalog: {WoundBlockCatalog.Count} " +
                               $"(max {WoundBlockCatalog.MaxBlocksPerDressing} blocks each)");

            if (player is null)
                return builder.ToString();

            builder.AppendLine($"--- Player {player.Nickname} ---");
            builder.AppendLine($"Alive: {player.IsAlive}, role: {player.Role.Type}");

            // Проверяем каждую кость: без скелета визуал не появится.
            foreach (BodyPart part in Enum.GetValues(typeof(BodyPart)))
            {
                bool found = BoneResolver.TryGetBone(player.ReferenceHub, part,
                    out UnityEngine.Transform bone, out string reason);

                builder.AppendLine($"  {part,-10} {(found ? $"OK ({bone.name})" : $"NOT FOUND - {reason}")}");
            }

            if (MedicalManager.TryGetState(player, out PlayerMedicalState? state) && state is not null)
            {
                builder.AppendLine($"Injuries: {state.Injuries.Count}");

                for (int i = 0; i < state.Injuries.Count; i++)
                {
                    Injury injury = state.Injuries[i];
                    string? name = WoundVisualCatalog.GetSchematicName(injury);

                    if (name is null)
                    {
                        builder.AppendLine($"  {injury.Describe()} -> no visual (by design)");
                        continue;
                    }

                    int blocks = WoundBlockCatalog.Get(name).Length;
                    builder.AppendLine($"  {injury.Describe()} -> {name} " +
                                       $"[{(blocks > 0 ? $"{blocks} blocks" : "NOT IN CATALOG")}]");
                }
            }
            else
            {
                builder.AppendLine("No injuries.");
            }

            return builder.ToString();
        }
    }
}
