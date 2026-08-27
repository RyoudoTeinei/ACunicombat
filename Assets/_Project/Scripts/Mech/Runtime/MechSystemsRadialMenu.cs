using UnityEngine;
using UnityEngine.InputSystem;

namespace ACunicombat.Mechs
{
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class MechSystemsRadialMenu : MonoBehaviour
    {
        private enum SystemCategory : byte
        {
            None,
            Radar,
            ElectroOptical,
            Propulsion,
            FireControl,
            Weapons,
            Devices,
        }

        private static readonly string[] CategoryNames =
        {
            "雷达控制",
            "光电红外控制",
            "动力控制",
            "火控控制",
            "武器控制",
            "装置控制",
        };

        private static readonly SystemCategory[] Categories =
        {
            SystemCategory.Radar,
            SystemCategory.ElectroOptical,
            SystemCategory.Propulsion,
            SystemCategory.FireControl,
            SystemCategory.Weapons,
            SystemCategory.Devices,
        };

        private static readonly Color PanelColor =
            new Color(0.012f, 0.035f, 0.03f, 0.94f);
        private static readonly Color PanelHoverColor =
            new Color(0.025f, 0.105f, 0.075f, 0.97f);
        private static readonly Color Green =
            new Color(0.2f, 1f, 0.55f, 1f);
        private static readonly Color GreenMuted =
            new Color(0.12f, 0.58f, 0.36f, 1f);
        private static readonly Color Amber =
            new Color(1f, 0.62f, 0.16f, 1f);
        private static readonly Color TextMuted =
            new Color(0.55f, 0.72f, 0.65f, 1f);

        [SerializeField, Range(0.01f, 0.2f)]
        private float menuTimeScale = 0.05f;

        [SerializeField, Range(300f, 5000f)]
        private float slowTimeLowPassCutoffHz = 950f;

        [SerializeField, Range(0.1f, 1f)]
        private float slowTimeListenerVolume = 0.72f;

        private MechFlightMotionController motion;
        private MechRadarController radar;
        private MechTestWeaponSystem weapons;
        private MechThirdPersonCamera viewController;
        private bool visible;
        private SystemCategory activeCategory;
        private float openedAt;
        private bool ownsSlowTime;
        private float previousTimeScale = 1f;
        private float previousFixedDeltaTime = 0.02f;
        private CursorLockMode previousCursorLockMode;
        private bool previousCursorVisible;
        private float previousListenerVolume = 1f;
        private AudioLowPassFilter listenerLowPass;
        private bool ownsListenerLowPass;
        private bool previousLowPassEnabled;
        private float previousLowPassCutoff;
        private float previousLowPassResonance;
        private GUIStyle categoryStyle;
        private GUIStyle categorySubStyle;
        private GUIStyle titleStyle;
        private GUIStyle keyStyle;
        private GUIStyle statusStyle;
        private GUIStyle descriptionStyle;

        public static bool IsAnyMenuVisible { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            IsAnyMenuVisible = false;
        }

        private void Awake()
        {
            ResolveReferences();
        }

        private void OnDisable()
        {
            SetVisible(false);
        }

        private void OnDestroy()
        {
            SetVisible(false);
            if (ownsListenerLowPass && listenerLowPass != null)
            {
                Destroy(listenerLowPass);
            }
        }

        private void Update()
        {
            if (MechSolarSystemMap.IsAnyMapVisible || MechAssemblyMenu.IsAnyMenuVisible)
            {
                if (visible)
                {
                    SetVisible(false);
                }
                return;
            }

            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            if (!visible)
            {
                if (keyboard.digit1Key.wasPressedThisFrame)
                {
                    SetVisible(true);
                }
                return;
            }

            bool backRequested = keyboard.escapeKey.wasPressedThisFrame ||
                keyboard.backspaceKey.wasPressedThisFrame ||
                Mouse.current?.rightButton.wasPressedThisFrame == true;
            if (backRequested)
            {
                if (activeCategory == SystemCategory.None)
                {
                    SetVisible(false);
                }
                else
                {
                    activeCategory = SystemCategory.None;
                }
                return;
            }

            int shortcut = ReadNumericShortcut(keyboard);
            if (shortcut < 0)
            {
                return;
            }

            if (activeCategory == SystemCategory.None)
            {
                EnterCategory(shortcut);
            }
            else
            {
                ActivateControl(activeCategory, shortcut);
            }
        }

        private void SetVisible(bool value)
        {
            if (visible == value)
            {
                return;
            }

            visible = value;
            IsAnyMenuVisible = value;
            activeCategory = SystemCategory.None;
            if (visible)
            {
                ResolveReferences();
                openedAt = Time.unscaledTime;
                AcquireSlowTime();
            }
            else
            {
                RestoreGameplayState();
            }
        }

        private void ResolveReferences()
        {
            motion ??= GetComponent<MechFlightMotionController>();
            radar ??= GetComponent<MechRadarController>();
            weapons ??= GetComponent<MechTestWeaponSystem>();
            if (viewController == null)
            {
                var camera = Camera.main;
                viewController = camera != null
                    ? camera.GetComponent<MechThirdPersonCamera>()
                    : null;
            }
        }

        private void AcquireSlowTime()
        {
            if (!ownsSlowTime)
            {
                previousTimeScale = Time.timeScale > Mathf.Epsilon
                    ? Time.timeScale
                    : 1f;
                previousFixedDeltaTime = Mathf.Max(0.0001f, Time.fixedDeltaTime);
                previousCursorLockMode = Cursor.lockState;
                previousCursorVisible = Cursor.visible;
                previousListenerVolume = AudioListener.volume;
                ownsSlowTime = true;
            }

            float scale = Mathf.Clamp(menuTimeScale, 0.01f, 0.2f);
            Time.timeScale = scale;
            Time.fixedDeltaTime = Mathf.Max(0.0005f, previousFixedDeltaTime * scale);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            AudioListener.volume = Mathf.Clamp01(slowTimeListenerVolume);
            ConfigureSlowTimeAudioFilter();
        }

        private void ConfigureSlowTimeAudioFilter()
        {
            if (listenerLowPass == null)
            {
                var listener = FindAnyObjectByType<AudioListener>();
                if (listener == null)
                {
                    return;
                }

                listenerLowPass = listener.GetComponent<AudioLowPassFilter>();
                if (listenerLowPass == null)
                {
                    listenerLowPass = listener.gameObject.AddComponent<AudioLowPassFilter>();
                    ownsListenerLowPass = true;
                    previousLowPassEnabled = false;
                    previousLowPassCutoff = 22000f;
                    previousLowPassResonance = 1f;
                }
                else
                {
                    ownsListenerLowPass = false;
                    previousLowPassEnabled = listenerLowPass.enabled;
                    previousLowPassCutoff = listenerLowPass.cutoffFrequency;
                    previousLowPassResonance = listenerLowPass.lowpassResonanceQ;
                }
            }

            listenerLowPass.enabled = true;
            listenerLowPass.cutoffFrequency =
                Mathf.Clamp(slowTimeLowPassCutoffHz, 300f, 5000f);
            listenerLowPass.lowpassResonanceQ = 1.35f;
        }

        private void RestoreGameplayState()
        {
            if (!ownsSlowTime)
            {
                return;
            }

            Time.timeScale = previousTimeScale > Mathf.Epsilon
                ? previousTimeScale
                : 1f;
            Time.fixedDeltaTime = Mathf.Max(0.0001f, previousFixedDeltaTime);
            Cursor.lockState = previousCursorLockMode;
            Cursor.visible = previousCursorVisible;
            AudioListener.volume = previousListenerVolume;
            if (listenerLowPass != null)
            {
                if (ownsListenerLowPass)
                {
                    listenerLowPass.enabled = false;
                    Destroy(listenerLowPass);
                    listenerLowPass = null;
                    ownsListenerLowPass = false;
                }
                else
                {
                    listenerLowPass.enabled = previousLowPassEnabled;
                    listenerLowPass.cutoffFrequency = previousLowPassCutoff;
                    listenerLowPass.lowpassResonanceQ = previousLowPassResonance;
                }
            }
            ownsSlowTime = false;
        }

        private static int ReadNumericShortcut(Keyboard keyboard)
        {
            if (keyboard.digit1Key.wasPressedThisFrame) return 0;
            if (keyboard.digit2Key.wasPressedThisFrame) return 1;
            if (keyboard.digit3Key.wasPressedThisFrame) return 2;
            if (keyboard.digit4Key.wasPressedThisFrame) return 3;
            if (keyboard.digit5Key.wasPressedThisFrame) return 4;
            if (keyboard.digit6Key.wasPressedThisFrame) return 5;
            return -1;
        }

        private void EnterCategory(int index)
        {
            if ((uint)index >= Categories.Length)
            {
                return;
            }
            activeCategory = Categories[index];
        }

        private void ActivateControl(SystemCategory category, int index)
        {
            ResolveReferences();
            switch (category)
            {
                case SystemCategory.Radar when index == 0 && radar != null:
                    radar.SetRadarPowerEnabled(!radar.RadarPowerEnabled);
                    break;
                case SystemCategory.Radar when index == 1 && radar != null:
                    radar.SetRetainSttWhenOperationModeDisabled(
                        !radar.RetainSttWhenOperationModeDisabled);
                    break;
                case SystemCategory.ElectroOptical when index == 0 &&
                    viewController != null:
                    viewController.SetZoomTracksSttTarget(
                        !viewController.ZoomTracksSttTarget);
                    break;
                case SystemCategory.Propulsion when index == 0 && motion != null:
                    motion.SetVectorThrustEnabled(!motion.VectorThrustEnabled);
                    break;
                case SystemCategory.FireControl when index == 0 && weapons != null:
                    weapons.SetHandPointDefensePriorityEnabled(
                        !weapons.HandPointDefensePriorityEnabled);
                    break;
                case SystemCategory.FireControl when index == 1 && weapons != null:
                    weapons.SetHandPointDefenseTcpaLimitEnabled(
                        !weapons.HandPointDefenseTcpaLimitEnabled);
                    break;
                case SystemCategory.Devices when index == 0 && radar != null:
                    radar.CycleDataLinkPolicy();
                    break;
            }
        }

        private int GetControlCount(SystemCategory category) => category switch
        {
            SystemCategory.Radar => 2,
            SystemCategory.ElectroOptical => 1,
            SystemCategory.Propulsion => 1,
            SystemCategory.FireControl => 2,
            SystemCategory.Devices => 1,
            _ => 0,
        };

        private string GetControlName(SystemCategory category, int index) =>
            (category, index) switch
            {
                (SystemCategory.Radar, 0) => "主动雷达",
                (SystemCategory.Radar, 1) => "退出界面保留 STT",
                (SystemCategory.ElectroOptical, 0) => "缩放跟踪 STT 目标",
                (SystemCategory.Propulsion, 0) => "矢量推力",
                (SystemCategory.FireControl, 0) => "手部武器拦截优先",
                (SystemCategory.FireControl, 1) => "点防御 TCPA 门槛",
                (SystemCategory.Devices, 0) => "数据链策略",
                _ => "未定义控制",
            };

        private string GetControlDescription(SystemCategory category, int index) =>
            (category, index) switch
            {
                (SystemCategory.Radar, 0) => "控制主动雷达发射与波束驻留调度",
                (SystemCategory.Radar, 1) => "关闭 Tab 雷达操作界面后继续维持已有 STT",
                (SystemCategory.ElectroOptical, 0) => "按住 CapsLock 缩放时自动把视线保持在 STT 目标上",
                (SystemCategory.Propulsion, 0) => "允许主推进器在机械偏转范围内参与侧向与升降控制",
                (SystemCategory.FireControl, 0) => "开启时手部武器会在来袭导弹进入 2000 m 后优先拦截",
                (SystemCategory.FireControl, 1) => "开启后仅在来袭导弹 TCPA 不超过 3.5 秒时接管手部武器",
                (SystemCategory.Devices, 0) => "切换自动、态势优先、防空优先与无线电静默模式",
                _ => string.Empty,
            };

        private bool TryGetControlState(
            SystemCategory category,
            int index,
            out bool state)
        {
            ResolveReferences();
            switch (category)
            {
                case SystemCategory.Radar when index == 0 && radar != null:
                    state = radar.RadarPowerEnabled;
                    return true;
                case SystemCategory.Radar when index == 1 && radar != null:
                    state = radar.RetainSttWhenOperationModeDisabled;
                    return true;
                case SystemCategory.ElectroOptical when index == 0 &&
                    viewController != null:
                    state = viewController.ZoomTracksSttTarget;
                    return true;
                case SystemCategory.Propulsion when index == 0 && motion != null:
                    state = motion.VectorThrustEnabled;
                    return true;
                case SystemCategory.FireControl when index == 0 && weapons != null:
                    state = weapons.HandPointDefensePriorityEnabled;
                    return true;
                case SystemCategory.FireControl when index == 1 && weapons != null:
                    state = weapons.HandPointDefenseTcpaLimitEnabled;
                    return true;
                case SystemCategory.Devices when index == 0 && radar != null:
                    state = radar.DataLinkTransmitEnabled;
                    return true;
                default:
                    state = false;
                    return false;
            }
        }

        private void OnGUI()
        {
            if (!visible)
            {
                return;
            }

            ResolveReferences();
            EnsureStyles();
            int previousDepth = GUI.depth;
            GUI.depth = -1200;
            DrawRect(new Rect(0f, 0f, Screen.width, Screen.height),
                new Color(0f, 0.018f, 0.014f, 0.46f));
            if (activeCategory == SystemCategory.None)
            {
                DrawCategoryRing();
            }
            else
            {
                DrawSubmenu(activeCategory);
            }
            GUI.depth = previousDepth;
        }

        private void DrawCategoryRing()
        {
            float scale = Mathf.Clamp(Screen.height / 1080f, 0.78f, 1.28f);
            var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            float horizontalRadius = 255f * scale;
            float verticalStep = 96f * scale;
            float width = 188f * scale;
            float height = 62f * scale;
            var offsets = new[]
            {
                new Vector2(-horizontalRadius, -verticalStep),
                new Vector2(-horizontalRadius, 0f),
                new Vector2(-horizontalRadius, verticalStep),
                new Vector2(horizontalRadius, -verticalStep),
                new Vector2(horizontalRadius, 0f),
                new Vector2(horizontalRadius, verticalStep),
            };

            float reveal = Mathf.Clamp01((Time.unscaledTime - openedAt) / 0.16f);
            var centerRect = new Rect(
                center.x - 72f * scale,
                center.y - 42f * scale,
                144f * scale,
                84f * scale);
            DrawRect(centerRect, PanelColor);
            DrawFrame(centerRect, GreenMuted, 2f);
            GUI.Label(centerRect, "系统控制\nSYSTEMS", titleStyle);

            var mouse = Event.current.mousePosition;
            for (int i = 0; i < Categories.Length; i++)
            {
                var itemCenter = Vector2.Lerp(center, center + offsets[i], reveal);
                var rect = new Rect(
                    itemCenter.x - width * 0.5f,
                    itemCenter.y - height * 0.5f,
                    width,
                    height);
                bool hover = rect.Contains(mouse);
                DrawLine(center, itemCenter, GreenMuted * new Color(1f, 1f, 1f, 0.55f), 1f);
                DrawRect(rect, hover ? PanelHoverColor : PanelColor);
                DrawFrame(rect, hover ? Green : GreenMuted, hover ? 2.5f : 1.5f);
                GUI.Label(
                    new Rect(rect.x + 12f * scale, rect.y + 5f * scale,
                        28f * scale, rect.height - 10f * scale),
                    (i + 1).ToString(),
                    keyStyle);
                GUI.Label(
                    new Rect(rect.x + 42f * scale, rect.y,
                        rect.width - 50f * scale, rect.height),
                    CategoryNames[i],
                    categoryStyle);
                if (hover && Event.current.type == EventType.MouseDown &&
                    Event.current.button == 0)
                {
                    EnterCategory(i);
                    Event.current.Use();
                }
            }

            GUI.Label(
                new Rect(center.x - 260f * scale,
                    center.y + verticalStep + height + 34f * scale,
                    520f * scale,
                    30f * scale),
                "数字键 1-6 选择  /  ESC 或右键关闭  /  时间流速 5%",
                categorySubStyle);
        }

        private void DrawSubmenu(SystemCategory category)
        {
            float scale = Mathf.Clamp(Screen.height / 1080f, 0.78f, 1.28f);
            var panel = new Rect(
                Screen.width * 0.5f - 330f * scale,
                Screen.height * 0.5f - 210f * scale,
                660f * scale,
                420f * scale);
            DrawRect(panel, PanelColor);
            DrawFrame(panel, GreenMuted, 2f);
            int categoryIndex = System.Array.IndexOf(Categories, category);
            string categoryName = categoryIndex >= 0
                ? CategoryNames[categoryIndex]
                : "系统控制";
            GUI.Label(
                new Rect(panel.x + 28f * scale, panel.y + 18f * scale,
                    panel.width - 56f * scale, 48f * scale),
                categoryName,
                titleStyle);
            DrawRect(
                new Rect(panel.x + 28f * scale, panel.y + 70f * scale,
                    panel.width - 56f * scale, 2f),
                GreenMuted);

            int count = GetControlCount(category);
            if (count == 0)
            {
                GUI.Label(
                    new Rect(panel.x + 30f * scale, panel.center.y - 30f * scale,
                        panel.width - 60f * scale, 60f * scale),
                    "暂无已接入控制项",
                    categorySubStyle);
            }
            else
            {
                var mouse = Event.current.mousePosition;
                for (int i = 0; i < count; i++)
                {
                    var row = new Rect(
                        panel.x + 30f * scale,
                        panel.y + (98f + i * 112f) * scale,
                        panel.width - 60f * scale,
                        92f * scale);
                    bool hover = row.Contains(mouse);
                    DrawRect(row, hover ? PanelHoverColor :
                        new Color(0.016f, 0.058f, 0.046f, 0.96f));
                    DrawFrame(row, hover ? Green : GreenMuted, hover ? 2f : 1f);
                    GUI.Label(
                        new Rect(row.x + 12f * scale, row.y + 8f * scale,
                            30f * scale, 30f * scale),
                        (i + 1).ToString(),
                        keyStyle);
                    GUI.Label(
                        new Rect(row.x + 52f * scale, row.y + 5f * scale,
                            row.width - 190f * scale, 38f * scale),
                        GetControlName(category, i),
                        categoryStyle);
                    bool available = TryGetControlState(category, i, out bool state);
                    statusStyle.normal.textColor = !available
                        ? TextMuted
                        : state ? Green : Amber;
                    var statusText = !available
                        ? "不可用"
                        : category == SystemCategory.Devices && radar != null
                            ? radar.DataLinkPolicyDisplayName
                            : state ? "开启" : "关闭";
                    GUI.Label(
                        new Rect(row.xMax - 120f * scale, row.y + 5f * scale,
                            96f * scale, 38f * scale),
                        statusText,
                        statusStyle);
                    GUI.Label(
                        new Rect(row.x + 52f * scale, row.y + 45f * scale,
                            row.width - 76f * scale, 30f * scale),
                        GetControlDescription(category, i),
                        descriptionStyle);
                    if (hover && Event.current.type == EventType.MouseDown &&
                        Event.current.button == 0)
                    {
                        ActivateControl(category, i);
                        Event.current.Use();
                    }
                }
            }

            var backRect = new Rect(
                panel.x + 28f * scale,
                panel.yMax - 48f * scale,
                158f * scale,
                28f * scale);
            bool backHover = backRect.Contains(Event.current.mousePosition);
            DrawFrame(backRect, backHover ? Green : GreenMuted, 1.5f);
            GUI.Label(backRect, "ESC / 右键  返回", categorySubStyle);
            if (backHover && Event.current.type == EventType.MouseDown &&
                Event.current.button == 0)
            {
                activeCategory = SystemCategory.None;
                Event.current.Use();
            }
        }

        private void EnsureStyles()
        {
            int baseSize = Mathf.RoundToInt(
                18f * Mathf.Clamp(Screen.height / 1080f, 0.8f, 1.25f));
            categoryStyle ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Bold,
                wordWrap = false,
            };
            categoryStyle.fontSize = baseSize;
            categoryStyle.normal.textColor = Green;

            categorySubStyle ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
            };
            categorySubStyle.fontSize = Mathf.Max(12, baseSize - 5);
            categorySubStyle.normal.textColor = TextMuted;

            titleStyle ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                wordWrap = false,
            };
            titleStyle.fontSize = baseSize + 3;
            titleStyle.normal.textColor = Green;

            keyStyle ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                wordWrap = false,
            };
            keyStyle.fontSize = baseSize;
            keyStyle.normal.textColor = Green;

            statusStyle ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleRight,
                fontStyle = FontStyle.Bold,
                wordWrap = false,
            };
            statusStyle.fontSize = baseSize;

            descriptionStyle ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false,
            };
            descriptionStyle.fontSize = Mathf.Max(12, baseSize - 4);
            descriptionStyle.normal.textColor = TextMuted;
        }

        private static void DrawFrame(Rect rect, Color color, float thickness)
        {
            DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            DrawRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            DrawRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        private static void DrawRect(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private static void DrawLine(
            Vector2 from,
            Vector2 to,
            Color color,
            float thickness)
        {
            var delta = to - from;
            if (delta.sqrMagnitude <= 0.01f)
            {
                return;
            }

            Matrix4x4 previousMatrix = GUI.matrix;
            Color previousColor = GUI.color;
            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            GUIUtility.RotateAroundPivot(angle, from);
            GUI.color = color;
            GUI.DrawTexture(
                new Rect(from.x, from.y - thickness * 0.5f,
                    delta.magnitude, thickness),
                Texture2D.whiteTexture);
            GUI.matrix = previousMatrix;
            GUI.color = previousColor;
        }
    }
}
