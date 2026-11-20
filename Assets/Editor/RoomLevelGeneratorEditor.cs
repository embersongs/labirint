using UnityEditor;
using UnityEngine;

namespace LevelGeneration.EditorTools
{
    /// <summary>Инспектор генератора: параметры, статус и три стилизованные кнопки.</summary>
    [CustomEditor(typeof(RoomLevelGenerator))]
    public sealed class RoomLevelGeneratorEditor : Editor
    {
        private SerializedProperty _startPrefab;
        private SerializedProperty _turnPrefab;
        private SerializedProperty _corridorPrefab;
        private SerializedProperty _crossroadsPrefab;
        private SerializedProperty _forkPrefab;

        private SerializedProperty _roomCount;
        private SerializedProperty _roomScale;
        private SerializedProperty _autoFit;
        private SerializedProperty _corridorWeight;

        private SerializedProperty _extraRooms;

        private SerializedProperty _startDoors;
        private SerializedProperty _turnDoors;
        private SerializedProperty _corridorDoors;
        private SerializedProperty _crossroadsDoors;
        private SerializedProperty _forkDoors;

        private RoomLevelGenerator _generator;

        private GUIStyle _titleStyle;
        private GUIStyle _generateStyle;
        private GUIStyle _fixStyle;
        private GUIStyle _clearStyle;

        private bool _doorsFoldout;
        private string _message;
        private MessageType _messageType = MessageType.Info;

        private void OnEnable()
        {
            _generator = (RoomLevelGenerator)target;

            _startPrefab = serializedObject.FindProperty("startPrefab");
            _turnPrefab = serializedObject.FindProperty("turnPrefab");
            _corridorPrefab = serializedObject.FindProperty("corridorPrefab");
            _crossroadsPrefab = serializedObject.FindProperty("crossroadsPrefab");
            _forkPrefab = serializedObject.FindProperty("forkPrefab");

            _roomCount = serializedObject.FindProperty("roomCount");
            _roomScale = serializedObject.FindProperty("roomScale");
            _autoFit = serializedObject.FindProperty("autoFitPrefabScale");
            _corridorWeight = serializedObject.FindProperty("corridorWeight");

            _extraRooms = serializedObject.FindProperty("extraRooms");

            _startDoors = serializedObject.FindProperty("startDoors");
            _turnDoors = serializedObject.FindProperty("turnDoors");
            _corridorDoors = serializedObject.FindProperty("corridorDoors");
            _crossroadsDoors = serializedObject.FindProperty("crossroadsDoors");
            _forkDoors = serializedObject.FindProperty("forkDoors");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EnsureStyles();

            EditorGUILayout.LabelField("Генератор уровня из комнат", _titleStyle);
            EditorGUILayout.Space(4);

            EditorGUILayout.LabelField("Префабы", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_startPrefab, new GUIContent("1. Старт = финиш (дверь спереди)"));
            EditorGUILayout.PropertyField(_turnPrefab, new GUIContent("2. Поворот (угол)"));
            EditorGUILayout.PropertyField(_corridorPrefab, new GUIContent("3. Коридор"));
            EditorGUILayout.PropertyField(_crossroadsPrefab, new GUIContent("4. Перекрёсток"));
            EditorGUILayout.PropertyField(_forkPrefab, new GUIContent("5. Развилка (4 двери)"));

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Параметры", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_roomCount, new GUIContent("Комнат в уровне"));

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(_roomScale, new GUIContent("Scale комнаты"));
            bool scaleChanged = EditorGUI.EndChangeCheck();

            using (new EditorGUI.DisabledScope(true))
            {
                float cellSize = _roomScale.floatValue * RoomLevelGenerator.BasePlaneSize;
                EditorGUILayout.FloatField("Размер клетки (ед.)", cellSize);
            }

            if (scaleChanged && _generator.HasGeneratedLevel && !_generator.IsCurrentLevelFixed)
            {
                _message = "Масштаб изменён — нажмите «Сгенерировать уровень», чтобы пересобрать.";
                _messageType = MessageType.Info;
            }

            EditorGUILayout.PropertyField(_autoFit, new GUIContent("Подгонять масштаб префабов"));
            EditorGUILayout.PropertyField(_extraRooms, new GUIContent("Дополнительные комнаты"), true);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Веса комнат", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_corridorWeight, new GUIContent("Вес коридора (и поворота)"));
            EditorGUILayout.HelpBox(
                "Чем больше вес, тем чаще выпадают прямые коридоры и повороты и тем реже — " +
                "перекрёстки и развилки.",
                MessageType.None);

            EditorGUILayout.Space(4);
            _doorsFoldout = EditorGUILayout.Foldout(_doorsFoldout, "Проёмы комнат (тонкая настройка)", true);
            if (_doorsFoldout)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_startDoors, new GUIContent("Старт / финиш"));
                EditorGUILayout.PropertyField(_turnDoors, new GUIContent("Поворот"));
                EditorGUILayout.PropertyField(_corridorDoors, new GUIContent("Коридор"));
                EditorGUILayout.PropertyField(_crossroadsDoors, new GUIContent("Перекрёсток"));
                EditorGUILayout.PropertyField(_forkDoors, new GUIContent("Развилка"));
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(8);
            DrawStatus();

            bool valid = _generator.ValidateConfiguration(out string error);
            if (!valid)
                EditorGUILayout.HelpBox(error, MessageType.Warning);

            if (!string.IsNullOrEmpty(_message))
                EditorGUILayout.HelpBox(_message, _messageType);

            EditorGUILayout.Space(6);

            using (new EditorGUI.DisabledScope(!valid))
            {
                if (GUILayout.Button("Сгенерировать уровень", _generateStyle))
                    RunGenerate();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!_generator.HasGeneratedLevel || _generator.IsCurrentLevelFixed))
                {
                    if (GUILayout.Button("Зафиксировать", _fixStyle))
                        RunFix();
                }

                using (new EditorGUI.DisabledScope(!_generator.HasGeneratedLevel))
                {
                    if (GUILayout.Button("Очистить", _clearStyle))
                        RunClear();
                }
            }
        }

        // ------------------------------------------------------------------ действия

        private void RunGenerate()
        {
            int count = _generator.GenerateLevel();

            if (count > 0)
            {
                _message = $"Готово: {count} комнат.";
                _messageType = MessageType.Info;
            }
            else
            {
                _message = "Генерация не удалась. Проверьте префабы и набор проёмов.";
                _messageType = MessageType.Error;
            }

            AfterAction();
        }

        private void RunFix()
        {
            if (_generator.FixLevel())
            {
                _message = "Копия уровня зафиксирована. Рабочий экземпляр скрыт — его можно очистить.";
                _messageType = MessageType.Info;
            }
            else
            {
                _message = "Фиксировать нечего: сначала сгенерируйте уровень.";
                _messageType = MessageType.Warning;
            }

            AfterAction();
        }

        private void RunClear()
        {
            _generator.ClearGeneratedLevel();

            _message = "Сгенерированный уровень удалён. Зафиксированные копии не тронуты.";
            _messageType = MessageType.Info;

            AfterAction();
        }

        private void AfterAction()
        {
            serializedObject.Update();
            SceneView.RepaintAll();
            Repaint();
        }

        // ------------------------------------------------------------------ отрисовка

        private void DrawStatus()
        {
            string status;

            if (!_generator.HasGeneratedLevel)
            {
                status = "Сгенерированный уровень отсутствует.";
            }
            else if (_generator.IsCurrentLevelFixed)
            {
                status = $"Рабочий экземпляр зафиксирован и скрыт ({_generator.GeneratedRoomCount} комнат).";
            }
            else
            {
                float cellSize = _roomScale.floatValue * RoomLevelGenerator.BasePlaneSize;
                status = $"В сцене: {_generator.GeneratedRoomCount} комнат, клетка {cellSize:0.#} ед.";
            }

            if (_generator.FixedLevelCount > 0)
                status += $"\nЗафиксированных копий: {_generator.FixedLevelCount}";

            EditorGUILayout.HelpBox(status, MessageType.None);
        }

        private void EnsureStyles()
        {
            if (_generateStyle != null) return;

            _titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };

            _generateStyle = CreateButtonStyle(
                new Color(0.18f, 0.55f, 0.29f),
                new Color(0.42f, 0.78f, 0.48f));

            _fixStyle = CreateButtonStyle(
                new Color(0.16f, 0.38f, 0.62f),
                new Color(0.38f, 0.62f, 0.88f));

            _clearStyle = CreateButtonStyle(
                new Color(0.56f, 0.20f, 0.20f),
                new Color(0.86f, 0.42f, 0.42f));
        }

        private static GUIStyle CreateButtonStyle(Color fill, Color border)
        {
            var style = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                fixedHeight = 36,
                border = new RectOffset(12, 12, 12, 12),
                padding = new RectOffset(14, 14, 8, 8),
                margin = new RectOffset(0, 0, 4, 4),
            };

            style.normal.background = CreateRoundedTexture(fill, border);
            style.hover.background = CreateRoundedTexture(Lighten(fill, 0.1f), border);
            style.active.background = CreateRoundedTexture(Lighten(fill, -0.1f), border);

            style.normal.textColor = Color.white;
            style.hover.textColor = Color.white;
            style.active.textColor = new Color(0.92f, 0.92f, 0.92f);

            return style;
        }

        private static Color Lighten(Color color, float amount)
        {
            return new Color(
                Mathf.Clamp01(color.r + amount),
                Mathf.Clamp01(color.g + amount),
                Mathf.Clamp01(color.b + amount),
                color.a);
        }

        private static Texture2D CreateRoundedTexture(Color fill, Color border)
        {
            const int size = 64;
            const float radius = 10f;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            var pixels = new Color[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = RoundedRectDistance(x + 0.5f, y + 0.5f, 1f, 1f, size - 1f, size - 1f, radius);

                    Color color;
                    if (distance <= -1.5f) color = fill;
                    else if (distance <= 0f) color = border;
                    else if (distance <= 1f) color = new Color(border.r, border.g, border.b, border.a * (1f - distance));
                    else color = Color.clear;

                    pixels[y * size + x] = color;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private static float RoundedRectDistance(float x, float y,
            float left, float bottom, float right, float top, float radius)
        {
            float halfWidth = (right - left) * 0.5f - radius;
            float halfHeight = (top - bottom) * 0.5f - radius;
            float centerX = (left + right) * 0.5f;
            float centerY = (bottom + top) * 0.5f;

            float dx = Mathf.Abs(x - centerX) - halfWidth;
            float dy = Mathf.Abs(y - centerY) - halfHeight;

            float outsideX = Mathf.Max(dx, 0f);
            float outsideY = Mathf.Max(dy, 0f);

            return Mathf.Sqrt(outsideX * outsideX + outsideY * outsideY)
                   + Mathf.Min(Mathf.Max(dx, dy), 0f)
                   - radius;
        }
    }
}
