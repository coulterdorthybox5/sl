using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using AdminToys;
using Exiled.API.Features;
using MEC;
using UnityEngine;

namespace MainCore.Destruction
{
    public static class PrimitiveCuller
    {
        private const float RescanInterval = 3f;
        private const float TickInterval = 0.5f;
        private const int TickBatches = 4;

        private const float DefaultNearDistance = 60f;
        private const float DefaultFarDistance = 150f;

        /// <summary>
        /// Квады оптимизируются только на большой дистанции: из них собирают полы и
        /// стены, а исчезающий под ногами пол игрок замечает мгновенно.
        /// </summary>
        private const float QuadNearDistance = 200f;
        private const float QuadFarDistance = 260f;

        /// <summary>
        /// Объект крупнее этого размера (м) проверяется не только по центру, но и по
        /// углам: у пола 60x60 центр может быть в 30 м от игрока, стоящего на краю.
        /// </summary>
        private const float LargeObjectSize = 4f;

        /// <summary>Центр плюс четыре угла - максимум точек проверки на объект.</summary>
        private const int MaxSamplePoints = 5;

        private const float OcclusionSlack = 0.5f;
        private const int RaycastBufferSize = 16;

        private const string TagIgnoreFull = "[IgnoreOptFull]";

        private static readonly Regex CustomParamsRegex = new Regex(
            @"\[IgnoreOptCustom(?:\(([^)]*)\))?\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private struct Settings
        {
            public bool IgnoreFull;
            public float NearSqr;
            public float FarSqr;
        }

        private static readonly List<PrimitiveObjectToy> tracked = new List<PrimitiveObjectToy>();
        private static readonly Dictionary<PrimitiveObjectToy, PrimitiveFlags> originalFlags = new Dictionary<PrimitiveObjectToy, PrimitiveFlags>();
        private static readonly Dictionary<PrimitiveObjectToy, Settings> settingsCache = new Dictionary<PrimitiveObjectToy, Settings>();
        private static readonly HashSet<PrimitiveObjectToy> hidden = new HashSet<PrimitiveObjectToy>();
        private static readonly List<Vector3> cameraPositions = new List<Vector3>();
        private static readonly RaycastHit[] raycastBuffer = new RaycastHit[RaycastBufferSize];
        private static readonly Vector3[] sampleBuffer = new Vector3[MaxSamplePoints];

        private static CoroutineHandle scanCoroutine;
        private static CoroutineHandle tickCoroutine;
        private static bool running;
        private static int batchCursor;

        public static void Start()
        {
            if (running)
                return;

            running = true;
            batchCursor = 0;

            if (scanCoroutine.IsRunning)
                Timing.KillCoroutines(scanCoroutine);
            if (tickCoroutine.IsRunning)
                Timing.KillCoroutines(tickCoroutine);

            tracked.Clear();
            originalFlags.Clear();
            settingsCache.Clear();
            hidden.Clear();

            scanCoroutine = Timing.RunCoroutine(ScanLoop(), "MainCore.Culler.Scan");
            tickCoroutine = Timing.RunCoroutine(TickLoop(), "MainCore.Culler.Tick");
        }

        public static void Stop()
        {
            running = false;

            if (scanCoroutine.IsRunning)
                Timing.KillCoroutines(scanCoroutine);
            if (tickCoroutine.IsRunning)
                Timing.KillCoroutines(tickCoroutine);

            foreach (PrimitiveObjectToy toy in hidden)
            {
                if (toy == null)
                    continue;
                if (originalFlags.TryGetValue(toy, out PrimitiveFlags flags))
                    toy.NetworkPrimitiveFlags = flags;
            }

            tracked.Clear();
            originalFlags.Clear();
            settingsCache.Clear();
            hidden.Clear();
        }

        /// <summary>
        /// Меняет запомненные "исходные" флаги примитива.
        ///
        /// Оптимизатор возвращает объекту сохранённые флаги, когда тот снова становится видимым.
        /// Без этого вызова принудительно выставленный Collidable сбрасывался бы обратно
        /// на следующем тике, и разрушенный объект опять провалился бы под карту.
        /// </summary>
        public static void OverrideOriginalFlags(PrimitiveObjectToy toy, PrimitiveFlags flags)
        {
            if (toy == null)
                return;

            originalFlags[toy] = flags;

            // Объект был скрыт оптимизатором: снимаем метку, иначе тик решит, что он
            // уже спрятан, и не восстановит флаги при появлении в зоне видимости.
            hidden.Remove(toy);

            // Правило "квад без коллизии не оптимизируется" читает именно эти флаги,
            // поэтому кэш настроек нужно пересчитать сразу. Иначе до следующего
            // пересканирования (до 3 секунд) объект жил бы по устаревшему решению:
            // например, у квада включили коллизию, а он всё ещё не оптимизируется.
            settingsCache[toy] = ResolveSettings(toy);
        }


        private static IEnumerator<float> ScanLoop()
        {
            while (running)
            {
                try
                {
                    Rescan();
                }
                catch (Exception exception)
                {
                    Log.Error($"[Culler] Rescan failed: {exception}");
                }

                yield return Timing.WaitForSeconds(RescanInterval);
            }
        }

        private static IEnumerator<float> TickLoop()
        {
            while (running)
            {
                yield return Timing.WaitForSeconds(TickInterval);

                try
                {
                    Tick();
                }
                catch (Exception exception)
                {
                    Log.Error($"[Culler] Tick failed: {exception}");
                }
            }
        }

        private static void Rescan()
        {
            PrimitiveObjectToy[] found = UnityEngine.Object.FindObjectsOfType<PrimitiveObjectToy>();

            tracked.Clear();
            for (int i = 0; i < found.Length; i++)
            {
                PrimitiveObjectToy toy = found[i];
                if (toy == null)
                    continue;

                tracked.Add(toy);

                // Исходные флаги нужно запомнить до вызова ResolveSettings: проверка
                // коллизии у квада читает именно их, а не текущие (оптимизатор
                // снимает у скрытого объекта бит Visible).
                if (!originalFlags.ContainsKey(toy))
                    originalFlags[toy] = toy.NetworkPrimitiveFlags;

                settingsCache[toy] = ResolveSettings(toy);

            }

            List<PrimitiveObjectToy>? stale = null;
            foreach (PrimitiveObjectToy key in originalFlags.Keys)
            {
                if (key != null)
                    continue;

                stale ??= new List<PrimitiveObjectToy>();
                stale.Add(key!);
            }
            if (stale != null)
            {
                for (int i = 0; i < stale.Count; i++)
                {
                    originalFlags.Remove(stale[i]);
                    settingsCache.Remove(stale[i]);
                    hidden.Remove(stale[i]);
                }
            }
        }

        /// <summary>
        /// Квад без коллизии полностью исключён из оптимизации и всегда виден.
        /// </summary>
        /// <remarks>
        /// Такие квады - это плоский декор: вывески, плакаты, разметка, тонкие панели.
        /// Проверка видимости построена на луче от камеры к центру объекта, а у квада
        /// без коллайдера луч не может попасть в сам квад: он упирается в геометрию за
        /// ним, и объект ошибочно считался бы перекрытым. Плюс квад односторонний и
        /// бесконечно тонкий, поэтому выигрыша от его скрытия почти нет, а мигающий
        /// декор игрок замечает сразу. Отдельным следствием: если квад всё же был
        /// скрыт (например, коллизию сняли уже после скрытия), тик вернёт ему
        /// исходные флаги через ветку IgnoreFull.
        /// </remarks>
        private static bool IsUnoptimizedQuad(PrimitiveObjectToy toy)
        {
            if (toy.NetworkPrimitiveType != PrimitiveType.Quad)
                return false;

            // Оптимизатор гасит только бит Visible, поэтому исходные флаги - надёжный
            // источник сведений о коллизии у уже скрытого объекта.
            PrimitiveFlags flags = originalFlags.TryGetValue(toy, out PrimitiveFlags stored)
                ? stored
                : toy.NetworkPrimitiveFlags;

            return (flags & PrimitiveFlags.Collidable) == 0;
        }

        private static Settings ResolveSettings(PrimitiveObjectToy toy)
        {
            Settings s = new Settings
            {
                IgnoreFull = false,
                NearSqr = DefaultNearDistance * DefaultNearDistance,
                FarSqr = DefaultFarDistance * DefaultFarDistance,
            };

            // Приоритет выше любых тегов и компонентов: квад без коллизии не
            // оптимизируется ни при каких настройках.
            if (IsUnoptimizedQuad(toy))
            {
                s.IgnoreFull = true;
                return s;
            }

            // Коллизионный квад - это пол или стена. Его скрытие даёт мало выигрыша,
            // а провал под карту или дыра в стене видны сразу, поэтому дистанция
            // оптимизации поднята. Явные теги ниже всё ещё могут её переопределить.
            if (toy.NetworkPrimitiveType == PrimitiveType.Quad)
            {
                s.NearSqr = QuadNearDistance * QuadNearDistance;
                s.FarSqr = QuadFarDistance * QuadFarDistance;
            }

            Transform? t = toy.transform;

            while (t != null)
            {
                Component[] components = t.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    Component c = components[i];
                    if (c == null)
                        continue;

                    string typeName = c.GetType().Name;
                    if (string.Equals(typeName, "IgnoreOptimizationFull", StringComparison.OrdinalIgnoreCase))
                    {
                        s.IgnoreFull = true;
                        return s;
                    }

                    if (string.Equals(typeName, "IgnoreOptimizationCustom", StringComparison.OrdinalIgnoreCase))
                    {
                        float near = DefaultNearDistance;
                        float far = DefaultFarDistance;
                        ReadFloatField(c, "NearDistance", ref near);
                        ReadFloatField(c, "FarDistance", ref far);
                        s.NearSqr = near * near;
                        s.FarSqr = Mathf.Max(near + 1f, far) * Mathf.Max(near + 1f, far);
                        return s;
                    }
                }

                string name = t.name;
                if (!string.IsNullOrEmpty(name))
                {
                    if (name.IndexOf(TagIgnoreFull, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        s.IgnoreFull = true;
                        return s;
                    }

                    Match m = CustomParamsRegex.Match(name);
                    if (m.Success)
                    {
                        float near = DefaultNearDistance;
                        float far = DefaultFarDistance;
                        ParseCustomParams(m.Groups[1].Value, ref near, ref far);
                        s.NearSqr = near * near;
                        s.FarSqr = far * far;
                        return s;
                    }
                }
                t = t.parent;
            }

            return s;
        }

        private static void ReadFloatField(Component c, string fieldName, ref float target)
        {
            Type type = c.GetType();
            FieldInfo? field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field != null && field.FieldType == typeof(float))
            {
                object? val = field.GetValue(c);
                if (val is float f)
                    target = Mathf.Max(0f, f);
                return;
            }

            PropertyInfo? prop = type.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop != null && prop.PropertyType == typeof(float) && prop.CanRead)
            {
                object? val = prop.GetValue(c);
                if (val is float f)
                    target = Mathf.Max(0f, f);
            }
        }

        private static void ParseCustomParams(string raw, ref float near, ref float far)
        {
            if (string.IsNullOrEmpty(raw))
                return;

            string[] parts = raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                int eq = p.IndexOf('=');
                if (eq <= 0)
                    continue;

                string key = p.Substring(0, eq).Trim();
                string val = p.Substring(eq + 1).Trim();
                if (!float.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float parsed))
                    continue;

                if (key.Equals("near", StringComparison.OrdinalIgnoreCase))
                    near = Mathf.Max(0f, parsed);
                else if (key.Equals("far", StringComparison.OrdinalIgnoreCase))
                    far = Mathf.Max(1f, parsed);
            }
        }

        private static void Tick()
        {
            cameraPositions.Clear();
            foreach (Player player in Player.List)
            {
                if (player == null || !player.IsAlive)
                    continue;

                Transform? cam = player.CameraTransform;
                if (cam == null)
                    continue;

                cameraPositions.Add(cam.position);
            }

            if (cameraPositions.Count == 0)
                return;

            int total = tracked.Count;
            if (total == 0)
                return;

            int batchSize = Mathf.Max(1, (total + TickBatches - 1) / TickBatches);
            int start = batchCursor;
            int end = Mathf.Min(start + batchSize, total);
            batchCursor = end >= total ? 0 : end;

            for (int i = start; i < end; i++)
            {
                PrimitiveObjectToy toy = tracked[i];
                if (toy == null)
                    continue;

                PrimitiveFlags orig = originalFlags.TryGetValue(toy, out PrimitiveFlags storedOrig)
                    ? storedOrig
                    : toy.NetworkPrimitiveFlags;

                if ((orig & PrimitiveFlags.Visible) == 0)
                    continue;

                Settings s = settingsCache.TryGetValue(toy, out Settings storedSettings)
                    ? storedSettings
                    : ResolveSettings(toy);

                if (s.IgnoreFull)
                {
                    if (hidden.Contains(toy))
                    {
                        toy.NetworkPrimitiveFlags = orig;
                        hidden.Remove(toy);
                    }
                    continue;
                }

                int samples = FillSamplePoints(toy);
                bool visible = IsVisibleToAnyCamera(toy, samples, s);

                bool currentlyHidden = hidden.Contains(toy);
                if (visible && currentlyHidden)
                {
                    toy.NetworkPrimitiveFlags = orig;
                    hidden.Remove(toy);
                }
                else if (!visible && !currentlyHidden)
                {
                    toy.NetworkPrimitiveFlags = orig & ~PrimitiveFlags.Visible;
                    hidden.Add(toy);
                }
            }
        }

        /// <summary>
        /// Заполняет <see cref="sampleBuffer"/> точками проверки видимости и возвращает
        /// их количество.
        /// </summary>
        /// <remarks>
        /// Мелкий объект проверяется по центру - этого достаточно. У крупного (пол,
        /// стена, перекрытие) центр может оказаться в десятках метров от игрока,
        /// стоящего на его краю, и объект ошибочно уходил за дальнюю дистанцию: пол
        /// буквально пропадал под ногами. Поэтому к центру добавляются четыре угла.
        /// </remarks>
        private static int FillSamplePoints(PrimitiveObjectToy toy)
        {
            Transform t = toy.transform;
            Vector3 center = t.position;
            sampleBuffer[0] = center;

            Vector3 scale = t.lossyScale;

            // Локальный размер меша: квад - 1x1 в плоскости XY, плоскость - 10x10 в XZ,
            // остальные примитивы - единичный куб/сфера.
            PrimitiveType type = toy.NetworkPrimitiveType;
            bool quad = type == PrimitiveType.Quad;
            bool plane = type == PrimitiveType.Plane;
            float meshSize = plane ? 10f : 1f;

            // Углы берутся по двум осям, в которых лежит плоскость примитива:
            // у квада это локальные X/Y, у плоскости и объёмных примитивов - X/Z.
            Vector3 axisA = t.right;
            Vector3 axisB = quad ? t.up : t.forward;
            float halfA = Mathf.Abs(scale.x) * meshSize * 0.5f;
            float halfB = Mathf.Abs(quad ? scale.y : scale.z) * meshSize * 0.5f;

            if (halfA * 2f <= LargeObjectSize && halfB * 2f <= LargeObjectSize)
                return 1;

            Vector3 a = axisA * halfA;
            Vector3 b = axisB * halfB;

            sampleBuffer[1] = center + a + b;
            sampleBuffer[2] = center + a - b;
            sampleBuffer[3] = center - a + b;
            sampleBuffer[4] = center - a - b;
            return MaxSamplePoints;
        }

        private static bool IsVisibleToAnyCamera(PrimitiveObjectToy toy, int samples, Settings s)
        {
            bool anyInFarRange = false;

            for (int i = 0; i < cameraPositions.Count; i++)
            {
                for (int p = 0; p < samples; p++)
                {
                    float distSqr = (sampleBuffer[p] - cameraPositions[i]).sqrMagnitude;

                    if (distSqr <= s.NearSqr)
                        return true;

                    if (distSqr <= s.FarSqr)
                        anyInFarRange = true;
                }
            }

            if (!anyInFarRange)
                return false;

            for (int i = 0; i < cameraPositions.Count; i++)
            {
                Vector3 camPos = cameraPositions[i];

                for (int p = 0; p < samples; p++)
                {
                    Vector3 point = sampleBuffer[p];
                    if ((point - camPos).sqrMagnitude > s.FarSqr)
                        continue;

                    if (!IsOccluded(camPos, point, toy))
                        return true;
                }
            }

            return false;
        }

        private static bool IsOccluded(Vector3 from, Vector3 to, PrimitiveObjectToy toy)
        {
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance <= OcclusionSlack)
                return false;

            Vector3 dir = delta / distance;
            int count = Physics.RaycastNonAlloc(from, dir, raycastBuffer, distance - OcclusionSlack);

            Transform toyTransform = toy.transform;

            for (int i = 0; i < count; i++)
            {
                Collider col = raycastBuffer[i].collider;
                if (col == null)
                    continue;

                Transform ct = col.transform;
                if (ct == toyTransform || ct.IsChildOf(toyTransform))
                    continue;

                if (col.isTrigger)
                    continue;

                if (col.GetComponentInParent<ReferenceHub>() != null)
                    continue;

                return true;
            }

            return false;
        }
    }
}