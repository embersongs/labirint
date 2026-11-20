using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace LevelGeneration
{
    /// <summary>
    /// Генератор уровня из комнат-префабов (Unity 2021 / URP).
    ///
    /// Соглашения о сторонах комнаты (вид сверху, оси мира):
    ///   Front (+Z) — «спереди»,  Back (-Z) — «сзади/снизу»,
    ///   Left  (-X) — «слева»,    Right (+X) — «справа».
    ///
    /// Комнаты можно поворачивать: генератор сам подбирает поворот под стык дверей.
    /// Соединяются только реально совпавшие проёмы, комнаты не пересекаются.
    /// Стартовая комната используется и как финиш (одна дверь = тупик).
    /// Размер клетки = 10 * roomScale (стандартный Plane в Unity — 10x10 при scale = 1).
    /// </summary>
    [AddComponentMenu("Level Generation/Room Level Generator")]
    [DisallowMultipleComponent]
    public sealed class RoomLevelGenerator : MonoBehaviour
    {
        /// <summary>Сторона стандартного Plane при scale = 1.</summary>
        public const float BasePlaneSize = 10f;

        private const int MaxAttempts = 64;

        // ------------------------------------------------------------------ типы

        [Flags]
        public enum DoorSide
        {
            None = 0,
            Front = 1 << 0, // +Z
            Back = 1 << 1,  // -Z
            Left = 1 << 2,  // -X
            Right = 1 << 3, // +X
        }

        [Serializable]
        public sealed class ExtraRoom
        {
            [Tooltip("Подпись для инспектора")]
            public string label = "Развилка";

            [Tooltip("Префаб комнаты (например, перекрёсток с 3-4 дверями)")]
            public GameObject prefab;

            [Tooltip("Стороны префаба, на которых есть проём")]
            public DoorSide doors = DoorSide.Front | DoorSide.Back | DoorSide.Left | DoorSide.Right;

            [Min(0.1f)]
            public float weight = 1f;
        }

        private struct DoorSlot
        {
            public Vector2Int Target;   // клетка, куда ведёт проём
            public DoorSide Required;   // сторона новой комнаты, которая должна иметь проём
        }

        private struct Placement
        {
            public Vector2Int Cell;
            public GameObject Prefab;
            public int Rotation;        // в шагах по 90 градусов
            public DoorSide Doors;      // уже повёрнутые проёмы
            public bool IsClosure;      // тупик/финиш (комната с одной дверью)
        }

        private struct Candidate
        {
            public GameObject Prefab;
            public DoorSide Doors;
            public float Weight;
        }

        private struct Option
        {
            public Vector2Int Cell;
            public GameObject Prefab;
            public int Rotation;
            public DoorSide Doors;
            public DoorSide Open;
            public float Weight;
        }

        // ------------------------------------------------------------------ поля

        [Header("Префабы комнат")]
        [SerializeField, Tooltip("1. Старт — дверь спереди (+Z). Используется и как финиш")]
        private GameObject startPrefab;

        [SerializeField, Tooltip("2. Поворот (угол) — двери снизу (-Z) и справа (+X)")]
        private GameObject turnPrefab;

        [SerializeField, Tooltip("3. Коридор — двери снизу (-Z) и спереди (+Z)")]
        private GameObject corridorPrefab;

        [SerializeField, Tooltip("4. Перекрёсток — двери снизу (-Z), слева (-X) и справа (+X)")]
        private GameObject crossroadsPrefab;

        [SerializeField, Tooltip("5. Развилка — двери со всех сторон (+Z, -Z, -X, +X)")]
        private GameObject forkPrefab;

        [Header("Генерация")]
        [SerializeField, Min(2), Tooltip("Сколько комнат в уровне, включая старт и финиш")]
        private int roomCount = 10;

        [SerializeField, Min(0.1f), Tooltip("Масштаб комнаты. Plane 10x10 при scale 1 -> 30x30 при scale 3")]
        private float roomScale = 3f;

        [SerializeField, Tooltip("Подгонять масштаб префаба под размер клетки по габаритам пола")]
        private bool autoFitPrefabScale = true;

        [Header("Веса комнат")]
        [SerializeField, Min(0.1f),
         Tooltip("Вес коридора и поворота. Чем больше значение, тем чаще прямые коридоры и повороты " +
                 "и тем реже перекрёстки и развилки")]
        private float corridorWeight = 6f;

        [Header("Дополнительные комнаты")]
        [SerializeField, Tooltip("Необязательные префабы (тупики, залы). Дают дополнительные варианты комнат")]
        private List<ExtraRoom> extraRooms = new List<ExtraRoom>();

        [Header("Проёмы (стороны, где у префаба дверь)")]
        [SerializeField] private DoorSide startDoors = DoorSide.Front;
        [SerializeField] private DoorSide turnDoors = DoorSide.Back | DoorSide.Right;
        [SerializeField] private DoorSide corridorDoors = DoorSide.Back | DoorSide.Front;
        [SerializeField] private DoorSide crossroadsDoors = DoorSide.Back | DoorSide.Left | DoorSide.Right;
        [SerializeField] private DoorSide forkDoors =
            DoorSide.Front | DoorSide.Back | DoorSide.Left | DoorSide.Right;

        [SerializeField, HideInInspector] private GameObject generatedRoot;
        [SerializeField, HideInInspector] private bool generatedIsFixed;
        [SerializeField, HideInInspector] private int fixedLevelCount;

        // ------------------------------------------------------------------ свойства

        public int RoomCount => roomCount;
        public float RoomScale => roomScale;
        public float RoomSize => BasePlaneSize * Mathf.Max(0.05f, roomScale);
        public bool HasGeneratedLevel => generatedRoot != null;
        public bool IsCurrentLevelFixed => generatedIsFixed;
        public int FixedLevelCount => fixedLevelCount;
        public int GeneratedRoomCount => generatedRoot != null ? generatedRoot.transform.childCount : 0;

        private static readonly DoorSide[] AllSides =
        {
            DoorSide.Front, DoorSide.Back, DoorSide.Left, DoorSide.Right,
        };

        private static readonly Dictionary<GameObject, Vector2> FootprintCache =
            new Dictionary<GameObject, Vector2>();

        // ------------------------------------------------------------------ Unity

        private void OnValidate()
        {
            roomCount = Mathf.Max(2, roomCount);
            roomScale = Mathf.Max(0.1f, roomScale);
            corridorWeight = Mathf.Max(0.1f, corridorWeight);
        }

        // ------------------------------------------------------------------ публичное API

        /// <summary>Проверяет, можно ли запускать генерацию. Возвращает false и текст ошибки.</summary>
        public bool ValidateConfiguration(out string error)
        {
            if (startPrefab == null) { error = "Не назначен префаб стартовой комнаты (он же финиш)."; return false; }
            if (turnPrefab == null) { error = "Не назначен префаб комнаты «поворот»."; return false; }
            if (corridorPrefab == null) { error = "Не назначен префаб коридора."; return false; }
            if (crossroadsPrefab == null) { error = "Не назначен префаб перекрёстка."; return false; }
            if (forkPrefab == null) { error = "Не назначен префаб развилки."; return false; }
            if (forkDoors == DoorSide.None) { error = "У развилки не отмечено ни одного проёма."; return false; }
            if (startDoors == DoorSide.None) { error = "У стартовой комнаты не отмечено ни одного проёма."; return false; }

            error = null;
            return true;
        }

        [ContextMenu("Сгенерировать уровень")]
        public void GenerateLevelFromMenu() => GenerateLevel();

        /// <summary>Удаляет текущий сгенерированный уровень и строит новый. Возвращает число комнат.</summary>
        public int GenerateLevel()
        {
            if (!ValidateConfiguration(out string error))
            {
                Debug.LogError($"[RoomLevelGenerator] {error}", this);
                return 0;
            }

#if UNITY_EDITOR
            Undo.RegisterCompleteObjectUndo(this, "Generate Room Level");
#endif
            ClearGeneratedLevel();

            List<Placement> plan = BuildPlan();
            if (plan == null || plan.Count < 2)
            {
                Debug.LogError("[RoomLevelGenerator] Не удалось построить план уровня. " +
                               "Проверьте набор проёмов у префабов.", this);
                return 0;
            }

            generatedRoot = Spawn(plan);
            generatedIsFixed = false;

#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif

            int deadEnds = 0;
            foreach (Placement placement in plan)
            {
                if (placement.IsClosure) deadEnds++;
            }

            Debug.Log($"[RoomLevelGenerator] Готово: {plan.Count} комнат (тупиков, включая финиш: {deadEnds}), " +
                      $"клетка {RoomSize:0.#}x{RoomSize:0.#}.", this);
            return plan.Count;
        }

        [ContextMenu("Зафиксировать уровень")]
        public void FixLevelFromMenu() => FixLevel();

        /// <summary>Делает независимую копию сгенерированного уровня и скрывает рабочий экземпляр.</summary>
        public bool FixLevel()
        {
            if (generatedRoot == null || generatedIsFixed)
                return false;

#if UNITY_EDITOR
            Undo.RegisterCompleteObjectUndo(this, "Fix Room Level");
#endif

            Transform parent = generatedRoot.transform.parent;
            GameObject copy = Instantiate(generatedRoot, parent);
            copy.name = $"Fixed Level {fixedLevelCount + 1}";
            copy.transform.localPosition = generatedRoot.transform.localPosition;
            copy.transform.localRotation = generatedRoot.transform.localRotation;
            copy.transform.localScale = generatedRoot.transform.localScale;
            copy.SetActive(true);

            generatedRoot.SetActive(false);
            generatedIsFixed = true;
            fixedLevelCount++;

#if UNITY_EDITOR
            if (!Application.isPlaying)
                Undo.RegisterCreatedObjectUndo(copy, "Fix Room Level");
            EditorUtility.SetDirty(this);
#endif
            return true;
        }

        [ContextMenu("Очистить уровень")]
        public void ClearLevelFromMenu() => ClearGeneratedLevel();

        /// <summary>Удаляет текущий сгенерированный уровень. Зафиксированные копии остаются.</summary>
        public void ClearGeneratedLevel()
        {
#if UNITY_EDITOR
            Undo.RegisterCompleteObjectUndo(this, "Clear Room Level");
#endif
            if (generatedRoot != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    Undo.DestroyObjectImmediate(generatedRoot);
                else
                    Destroy(generatedRoot);
#else
                Destroy(generatedRoot);
#endif
            }

            generatedRoot = null;
            generatedIsFixed = false;

#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
        }

        // ------------------------------------------------------------------ построение плана

        private List<Placement> BuildPlan()
        {
            // Вес общий для коридора и поворота: больше значение — меньше развилок.
            float pathWeight = Mathf.Max(0.1f, corridorWeight);

            var regular = new List<Candidate>
            {
                new Candidate { Prefab = turnPrefab, Doors = turnDoors, Weight = pathWeight },
                new Candidate { Prefab = corridorPrefab, Doors = corridorDoors, Weight = pathWeight },
                new Candidate { Prefab = crossroadsPrefab, Doors = crossroadsDoors, Weight = 2f },
                new Candidate { Prefab = forkPrefab, Doors = forkDoors, Weight = 1.5f },
            };

            if (extraRooms != null)
            {
                foreach (ExtraRoom extra in extraRooms)
                {
                    if (extra == null || extra.prefab == null || extra.doors == DoorSide.None) continue;

                    regular.Add(new Candidate
                    {
                        Prefab = extra.prefab,
                        Doors = extra.doors,
                        Weight = Mathf.Max(0.1f, extra.weight),
                    });
                }
            }

            var start = new Candidate { Prefab = startPrefab, Doors = startDoors, Weight = 1f };
            // Финиш и тупики — та же комната с одной дверью, что и старт.
            Candidate deadEnd = start;

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                List<Placement> plan = TryBuildPlan(start, deadEnd, regular);
                if (plan != null) return plan;
            }

            return null;
        }

        private List<Placement> TryBuildPlan(Candidate start, Candidate deadEnd, List<Candidate> regular)
        {
            var plan = new List<Placement>(roomCount);
            var occupied = new Dictionary<Vector2Int, Placement>(roomCount);
            var frontier = new List<DoorSlot>();

            var origin = new Placement
            {
                Cell = Vector2Int.zero,
                Prefab = start.Prefab,
                Rotation = 0,
                Doors = start.Doors,
            };

            plan.Add(origin);
            occupied[origin.Cell] = origin;
            AddSlots(frontier, origin.Cell, origin.Doors);

            // budget — сколько комнат осталось поставить,
            // frontier.Count — сколько проёмов ещё нужно закрыть.
            // Разница need — сколько «лишних» комнат можно потратить на ветки.
            int budget = roomCount - 1;

            while (budget > 0)
            {
                int need = budget - frontier.Count;
                if (need < 0) return null;

                bool placed = need == 0
                    ? PlaceClosure(plan, occupied, frontier, deadEnd)
                    : PlaceRegular(plan, occupied, frontier, regular, need);

                if (!placed) return null;
                budget--;
            }

            return frontier.Count == 0 ? plan : null;
        }

        private bool PlaceRegular(List<Placement> plan, Dictionary<Vector2Int, Placement> occupied,
            List<DoorSlot> frontier, List<Candidate> regular, int need)
        {
            List<Option> options = CollectOptions(frontier, occupied, regular, need);
            if (options.Count == 0) return false;

            Apply(plan, occupied, frontier, PickWeighted(options), false);
            return true;
        }

        /// <summary>Закрывает один проём комнатой с одной дверью (финиш/тупик).</summary>
        private bool PlaceClosure(List<Placement> plan, Dictionary<Vector2Int, Placement> occupied,
            List<DoorSlot> frontier, Candidate deadEnd)
        {
            var options = new List<Option>();

            foreach (DoorSlot slot in frontier)
            {
                if (occupied.ContainsKey(slot.Target)) continue;

                for (int r = 0; r < 4; r++)
                {
                    DoorSide doors = RotateMask(deadEnd.Doors, r);
                    if (!Fits(doors, slot, occupied, out DoorSide open)) continue;
                    if (!AllPendingDoorsMatch(frontier, slot.Target, doors)) continue;

                    options.Add(new Option
                    {
                        Cell = slot.Target,
                        Prefab = deadEnd.Prefab,
                        Rotation = r,
                        Doors = doors,
                        Open = open,
                        Weight = 1f,
                    });
                }
            }

            if (options.Count == 0) return false;

            Apply(plan, occupied, frontier, options[Random.Range(0, options.Count)], true);
            return true;
        }

        private List<Option> CollectOptions(List<DoorSlot> frontier, Dictionary<Vector2Int, Placement> occupied,
            List<Candidate> candidates, int need)
        {
            var options = new List<Option>();

            foreach (DoorSlot slot in frontier)
            {
                if (occupied.ContainsKey(slot.Target)) continue;

                foreach (Candidate candidate in candidates)
                {
                    for (int r = 0; r < 4; r++)
                    {
                        DoorSide doors = RotateMask(candidate.Doors, r);
                        if (!Fits(doors, slot, occupied, out DoorSide open)) continue;
                        if (!AllPendingDoorsMatch(frontier, slot.Target, doors)) continue;
                        if (DoorCount(open) > need) continue; // ветка не влезет в бюджет

                        options.Add(new Option
                        {
                            Cell = slot.Target,
                            Prefab = candidate.Prefab,
                            Rotation = r,
                            Doors = doors,
                            Open = open,
                            Weight = candidate.Weight,
                        });
                    }
                }
            }

            return options;
        }

        /// <summary>Все прочие проёмы, смотрящие в эту клетку, тоже должны найти дверь в новой комнате.</summary>
        private static bool AllPendingDoorsMatch(List<DoorSlot> frontier, Vector2Int cell, DoorSide doors)
        {
            foreach (DoorSlot slot in frontier)
            {
                if (slot.Target != cell) continue;
                if ((doors & slot.Required) == 0) return false;
            }

            return true;
        }

        /// <summary>Сколько проёмов у комнаты (для расчёта бюджета веток).</summary>
        private static int DoorCount(DoorSide mask)
        {
            int count = 0;
            foreach (DoorSide side in AllSides)
            {
                if ((mask & side) != 0) count++;
            }

            return count;
        }

        /// <summary>Проверяет, что комната с такими проёмами стыкуется с соседями и не упирается в стены.</summary>
        private static bool Fits(DoorSide doors, DoorSlot slot,
            Dictionary<Vector2Int, Placement> occupied, out DoorSide open)
        {
            open = DoorSide.None;

            if ((doors & slot.Required) == 0) return false;

            DoorSide free = doors & ~slot.Required;
            DoorSide openDoors = DoorSide.None;

            foreach (DoorSide side in Sides(free))
            {
                Vector2Int target = slot.Target + Offset(side);

                if (occupied.TryGetValue(target, out Placement neighbor))
                {
                    // сосед уже стоит: проём должен совпасть, иначе это дверь в стену
                    if ((neighbor.Doors & Opposite(side)) == 0) return false;
                }
                else
                {
                    openDoors |= side;
                }
            }

            open = openDoors;
            return true;
        }

        private static void Apply(List<Placement> plan, Dictionary<Vector2Int, Placement> occupied,
            List<DoorSlot> frontier, Option option, bool isClosure)
        {
            // все проёмы, смотревшие в эту клетку, теперь соединены
            for (int i = frontier.Count - 1; i >= 0; i--)
            {
                if (frontier[i].Target == option.Cell) frontier.RemoveAt(i);
            }

            var placement = new Placement
            {
                Cell = option.Cell,
                Prefab = option.Prefab,
                Rotation = option.Rotation,
                Doors = option.Doors,
                IsClosure = isClosure,
            };

            occupied[placement.Cell] = placement;
            plan.Add(placement);
            AddSlots(frontier, placement.Cell, option.Open);
        }

        private static void AddSlots(List<DoorSlot> frontier, Vector2Int cell, DoorSide openDoors)
        {
            foreach (DoorSide side in Sides(openDoors))
            {
                frontier.Add(new DoorSlot
                {
                    Target = cell + Offset(side),
                    Required = Opposite(side),
                });
            }
        }

        private static Option PickWeighted(List<Option> options)
        {
            float total = 0f;
            foreach (Option option in options) total += Mathf.Max(0.01f, option.Weight);

            float roll = Random.value * total;
            foreach (Option option in options)
            {
                roll -= Mathf.Max(0.01f, option.Weight);
                if (roll <= 0f) return option;
            }

            return options[options.Count - 1];
        }

        // ------------------------------------------------------------------ спавн

        private GameObject Spawn(List<Placement> plan)
        {
            var root = new GameObject($"Generated Level [{plan.Count} rooms]");
            root.transform.SetParent(transform, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            int finishIndex = FindFinishIndex(plan);

            for (int i = 0; i < plan.Count; i++)
            {
                Placement placement = plan[i];
                GameObject instance = Instantiate(placement.Prefab, root.transform);

                string label;
                if (i == 0) label = "Start";
                else if (placement.IsClosure) label = i == finishIndex ? "Finish" : "DeadEnd";
                else label = placement.Prefab.name;

                instance.name = $"{label} ({placement.Cell.x},{placement.Cell.y})";

                Transform tr = instance.transform;
                tr.localPosition = new Vector3(placement.Cell.x * RoomSize, 0f, placement.Cell.y * RoomSize);
                tr.localRotation = Quaternion.Euler(0f, placement.Rotation * 90f, 0f);

                ApplyFootprintScale(instance, placement.Prefab);
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
                Undo.RegisterCreatedObjectUndo(root, "Generate Room Level");
#endif
            return root;
        }

        /// <summary>Самый удалённый от старта тупик помечаем финишем.</summary>
        private static int FindFinishIndex(List<Placement> plan)
        {
            var indexByCell = new Dictionary<Vector2Int, int>(plan.Count);
            for (int i = 0; i < plan.Count; i++)
                indexByCell[plan[i].Cell] = i;

            var distance = new Dictionary<Vector2Int, int>(plan.Count) { [plan[0].Cell] = 0 };
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(plan[0].Cell);

            while (queue.Count > 0)
            {
                Vector2Int cell = queue.Dequeue();
                Placement room = plan[indexByCell[cell]];

                foreach (DoorSide side in Sides(room.Doors))
                {
                    Vector2Int next = cell + Offset(side);
                    if (!indexByCell.TryGetValue(next, out int nextIndex)) continue;
                    if ((plan[nextIndex].Doors & Opposite(side)) == 0) continue;
                    if (distance.ContainsKey(next)) continue;

                    distance[next] = distance[cell] + 1;
                    queue.Enqueue(next);
                }
            }

            int best = -1;
            int bestDistance = -1;

            for (int i = 0; i < plan.Count; i++)
            {
                if (!plan[i].IsClosure) continue;
                if (!distance.TryGetValue(plan[i].Cell, out int d)) continue;

                if (d > bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }

            return best;
        }

        private void ApplyFootprintScale(GameObject instance, GameObject prefab)
        {
            if (!autoFitPrefabScale) return;

            Vector2 footprint = GetPrefabFootprint(prefab);
            if (footprint.x <= 0.001f || footprint.y <= 0.001f) return;

            float factor = RoomSize / Mathf.Max(footprint.x, footprint.y);
            if (Mathf.Abs(factor - 1f) < 0.001f) return;

            Vector3 scale = instance.transform.localScale;
            instance.transform.localScale = new Vector3(scale.x * factor, scale.y * factor, scale.z * factor);
        }

        /// <summary>Габариты префаба по XZ: сначала корневой меш (обычно пол), иначе общий bounds рендереров.</summary>
        private static Vector2 GetPrefabFootprint(GameObject prefab)
        {
            if (prefab == null) return Vector2.zero;
            if (FootprintCache.TryGetValue(prefab, out Vector2 cached)) return cached;

            Vector2 size = Vector2.zero;

            MeshFilter filter = prefab.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null)
            {
                Vector3 bounds = filter.sharedMesh.bounds.size;
                Vector3 scale = prefab.transform.localScale;
                size = new Vector2(Mathf.Abs(bounds.x * scale.x), Mathf.Abs(bounds.z * scale.z));
            }

            if (size.x <= 0.001f || size.y <= 0.001f)
            {
                Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length > 0)
                {
                    Bounds bounds = renderers[0].bounds;
                    for (int i = 1; i < renderers.Length; i++)
                        bounds.Encapsulate(renderers[i].bounds);

                    size = new Vector2(bounds.size.x, bounds.size.z);
                }
            }

            FootprintCache[prefab] = size;
            return size;
        }

        // ------------------------------------------------------------------ сетка и стороны

        private static Vector2Int Offset(DoorSide side) => side switch
        {
            DoorSide.Front => new Vector2Int(0, 1),
            DoorSide.Back => new Vector2Int(0, -1),
            DoorSide.Left => new Vector2Int(-1, 0),
            DoorSide.Right => new Vector2Int(1, 0),
            _ => Vector2Int.zero,
        };

        private static DoorSide Opposite(DoorSide side) => side switch
        {
            DoorSide.Front => DoorSide.Back,
            DoorSide.Back => DoorSide.Front,
            DoorSide.Left => DoorSide.Right,
            DoorSide.Right => DoorSide.Left,
            _ => DoorSide.None,
        };

        private static DoorSide RotateSide(DoorSide side, int steps)
        {
            steps = ((steps % 4) + 4) % 4;
            for (int i = 0; i < steps; i++)
            {
                side = side switch
                {
                    DoorSide.Front => DoorSide.Right,
                    DoorSide.Right => DoorSide.Back,
                    DoorSide.Back => DoorSide.Left,
                    DoorSide.Left => DoorSide.Front,
                    _ => DoorSide.None,
                };
            }

            return side;
        }

        private static DoorSide RotateMask(DoorSide mask, int steps)
        {
            if (steps % 4 == 0) return mask;

            DoorSide result = DoorSide.None;
            foreach (DoorSide side in Sides(mask))
            {
                result |= RotateSide(side, steps);
            }

            return result;
        }

        private static IEnumerable<DoorSide> Sides(DoorSide mask)
        {
            foreach (DoorSide side in AllSides)
            {
                if ((mask & side) != 0) yield return side;
            }
        }
    }
}
