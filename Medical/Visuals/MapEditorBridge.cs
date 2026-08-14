using System;
using System.Reflection;
using UnityEngine;

namespace MainCore.Medical.Visuals
{
    /// <summary>
    /// Мост к ProjectMER (Map Editor) через рефлексию: спавнит схематики,
    /// не имея на сборку ProjectMER ни одной статической ссылки.
    /// </summary>
    /// <remarks>
    /// Почему именно рефлексия, а не обычный вызов.
    ///
    /// EXILED грузит плагины как <c>Assembly.Load(byte[])</c>, поэтому у сборки
    /// MainCore нет <c>Location</c>, и среда не может разрешить по имени сборку
    /// ProjectMER: её загружает другой загрузчик (LabAPI) из своей папки.
    /// Ссылка на внешний тип разрешается при JIT-компиляции метода, то есть метод
    /// со упоминанием <c>ObjectSpawner</c> падал с <c>FileNotFoundException</c>
    /// ещё до выполнения первой строки - и ни одна строка лога не появлялась.
    ///
    /// Рефлексия обходит это полностью: тип ищется среди уже загруженных сборок
    /// в том же процессе (<c>AppDomain.CurrentDomain.GetAssemblies()</c>), никакой
    /// загрузки по имени не происходит. При этом сам ProjectMER остаётся владельцем
    /// сетевой синхронизации схематика - именно то, что нужно.
    ///
    /// Возвращаемый <c>SchematicObject</c> наследует <c>MonoBehaviour</c>, поэтому
    /// снаружи с ним можно работать как с <see cref="Component"/>: этого достаточно,
    /// чтобы получить <c>transform</c> и двигать схематик за костью.
    /// </remarks>
    public static class MapEditorBridge
    {
        private const string SpawnerTypeName = "ProjectMER.Features.ObjectSpawner";

        private const string AssemblyName = "ProjectMER";

        /// <summary>Кеш найденного метода: рефлексия дорогая, а спавн частый.</summary>
        private static MethodInfo? trySpawnSchematic;

        /// <summary>Поиск уже выполнялся - повторно сборку не ищем.</summary>
        private static bool resolved;

        /// <summary>Почему мост недоступен (для диагностики).</summary>
        private static string unavailableReason = "not probed yet";

        /// <summary>Доступен ли ProjectMER в этом процессе.</summary>
        public static bool IsAvailable
        {
            get
            {
                Resolve();
                return trySpawnSchematic is not null;
            }
        }

        /// <summary>Текст о состоянии моста для отчётов диагностики.</summary>
        public static string Status
        {
            get
            {
                Resolve();
                return trySpawnSchematic is not null
                    ? "available"
                    : $"unavailable ({unavailableReason})";
            }
        }

        /// <summary>Сбрасывает кеш. Нужен, если ProjectMER догрузился позже.</summary>
        public static void ResetCache()
        {
            resolved = false;
            trySpawnSchematic = null;
            unavailableReason = "not probed yet";
        }

        /// <summary>
        /// Спавнит схематик и возвращает его как <see cref="Component"/>.
        /// </summary>
        /// <returns><c>null</c>, если ProjectMER недоступен или схематик не найден.</returns>
        public static Component? SpawnSchematic(string name, Vector3 position, Quaternion rotation, out string error)
        {
            Resolve();

            if (trySpawnSchematic is null)
            {
                error = unavailableReason;
                return null;
            }

            // out-параметр через рефлексию: значение возвращается в массиве аргументов.
            object?[] args = { name, position, rotation, null };

            try
            {
                object? result = trySpawnSchematic.Invoke(null, args);

                if (result is not bool spawned || !spawned)
                {
                    error = $"ProjectMER refused to spawn '{name}' (schematic missing or invalid JSON)";
                    return null;
                }

                if (args[3] is not Component schematic)
                {
                    error = $"ProjectMER returned true for '{name}' but the object is null";
                    return null;
                }

                error = string.Empty;
                return schematic;
            }
            catch (TargetInvocationException exception)
            {
                // Разворачиваем: рефлексия оборачивает исходное исключение.
                error = $"{exception.InnerException?.GetType().Name}: {exception.InnerException?.Message}";
                return null;
            }
            catch (Exception exception)
            {
                error = $"{exception.GetType().Name}: {exception.Message}";
                return null;
            }
        }

        /// <summary>
        /// Ищет в загруженных сборках ProjectMER и нужную перегрузку
        /// <c>TrySpawnSchematic(string, Vector3, Quaternion, out SchematicObject)</c>.
        /// </summary>
        private static void Resolve()
        {
            if (resolved)
                return;

            resolved = true;

            try
            {
                Assembly? merAssembly = null;

                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(assembly.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        merAssembly = assembly;
                        break;
                    }
                }

                if (merAssembly is null)
                {
                    unavailableReason = "ProjectMER assembly is not loaded in this process";
                    return;
                }

                Type? spawner = merAssembly.GetType(SpawnerTypeName, false);
                if (spawner is null)
                {
                    unavailableReason = $"type {SpawnerTypeName} not found in ProjectMER";
                    return;
                }

                // Ищем перегрузку по форме параметров: имена типов сравнивать нельзя,
                // потому что SchematicObject нам недоступен на этапе компиляции.
                foreach (MethodInfo method in spawner.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != "TrySpawnSchematic")
                        continue;

                    ParameterInfo[] parameters = method.GetParameters();

                    if (parameters.Length != 4)
                        continue;

                    if (parameters[0].ParameterType != typeof(string))
                        continue;

                    if (parameters[1].ParameterType != typeof(Vector3))
                        continue;

                    // Есть две 4-параметровые перегрузки: с Quaternion и с Vector3
                    // (эйлеровы углы). Берём Quaternion - он не страдает от
                    // неоднозначности порядка поворотов.
                    if (parameters[2].ParameterType != typeof(Quaternion))
                        continue;

                    if (!parameters[3].IsOut)
                        continue;

                    trySpawnSchematic = method;
                    return;
                }

                unavailableReason = "TrySpawnSchematic(string, Vector3, Quaternion, out _) not found";
            }
            catch (Exception exception)
            {
                unavailableReason = $"{exception.GetType().Name}: {exception.Message}";
            }
        }
    }
}
