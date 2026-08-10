using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace ACunicombat.Mechs
{
	[DisallowMultipleComponent]
	public sealed class MechAssemblyMenu : MonoBehaviour
	{
		private const float HeaderHeight = 84f;

		private const float FooterHeight = 64f;

		private const float ContentTop = 102f;

		private const float ContentBottom = 78f;

		private const float OuterMargin = 32f;

		private const float PanelGap = 14f;

		private const float PartMetricRowHeight = 46f;

		private const float PartMetricRowStride = 48f;

		private const float PartMetricTrackHeight = 8f;

		private const float AggregateMetricRowHeight = 38f;

		private const float AggregateMetricRowStride = 40f;

		private const float KilowattsPerMegawatt = 1000f;

		private static readonly Color ComparisonImprovementColor = new Color(0.36f, 0.7f, 1f, 1f);

		private static readonly Color ComparisonRegressionColor = new Color(1f, 0.33f, 0.3f, 1f);

		private static readonly Color ComparisonBaselineColor = new Color(0.44f, 0.56f, 0.65f, 1f);

		private static readonly MechPartSlot[] Slots = new MechPartSlot[13]
		{
			MechPartSlot.LeftHandWeapon,
			MechPartSlot.RightHandWeapon,
			MechPartSlot.LeftBackWeapon,
			MechPartSlot.RightBackWeapon,
			MechPartSlot.Head,
			MechPartSlot.Core,
			MechPartSlot.Arms,
			MechPartSlot.Legs,
			MechPartSlot.Booster,
			MechPartSlot.FireControlSystem,
			MechPartSlot.Generator,
			MechPartSlot.Radar,
			MechPartSlot.Extension
		};

		private static readonly MechPartSlot[] WeaponSlots = new MechPartSlot[4]
		{
			MechPartSlot.LeftHandWeapon,
			MechPartSlot.RightHandWeapon,
			MechPartSlot.LeftBackWeapon,
			MechPartSlot.RightBackWeapon
		};

		private static readonly MechPartSlot[] FrameSlots = new MechPartSlot[4]
		{
			MechPartSlot.Head,
			MechPartSlot.Core,
			MechPartSlot.Arms,
			MechPartSlot.Legs
		};

		private static readonly MechPartSlot[] InternalSlots = new MechPartSlot[5]
		{
			MechPartSlot.Booster,
			MechPartSlot.FireControlSystem,
			MechPartSlot.Generator,
			MechPartSlot.Radar,
			MechPartSlot.Extension
		};

		private readonly Dictionary<MechPartSlot, string> selection = new Dictionary<MechPartSlot, string>();

		private MechAssembler assembler;

		private MechPartCatalog catalog;

		private MechTestWeaponSystem weaponSystem;

		private MechPartSlot activeSlot = MechPartSlot.LeftHandWeapon;

		private string pendingPartId;

		private Vector2 slotScroll;

		private Vector2 partScroll;

		private Vector2 statsScroll;

		private Vector2 selectedStatsScroll;

		private string search = string.Empty;

		private bool visible;

		private bool selectingPart;

		private bool editingAppearance;

		private int activeColorRegion;

		private MechAppearanceSettings appearance;

		private float previousTimeScale = 1f;

		private bool ownsMenuPause;

		private bool stylesReady;

		private GUIStyle titleStyle;

		private GUIStyle headingStyle;

		private GUIStyle labelStyle;

		private GUIStyle mutedStyle;

		private GUIStyle breadcrumbStyle;

		private GUIStyle idStyle;

		private GUIStyle placeholderStyle;

		private GUIStyle valueStyle;

		private GUIStyle testedStyle;

		private GUIStyle untestedStyle;

		private GUIStyle warningStyle;

		private GUIStyle buttonStyle;

		private GUIStyle selectedButtonStyle;

		private GUIStyle equippedButtonStyle;

		private GUIStyle partButtonStyle;

		private GUIStyle selectedPartButtonStyle;

		private GUIStyle equippedPartButtonStyle;

		private GUIStyle primaryButtonStyle;

		private GUIStyle segmentButtonStyle;

		private GUIStyle selectedSegmentButtonStyle;

		private GUIStyle sectionStyle;

		private GUIStyle searchFieldStyle;

		private GUIStyle sliderStyle;

		private GUIStyle sliderThumbStyle;

		private GUIStyle noteStyle;

		private GUIStyle overlineStyle;

		private GUIStyle darkHeadingStyle;

		private GUIStyle darkLabelStyle;

		private GUIStyle darkCenteredStyle;

		private GUIStyle comparisonPositiveStyle;

		private GUIStyle comparisonNegativeStyle;

		private GUIStyle comparisonBaselineStyle;

		private GUIStyle comparisonArrowStyle;

		private GUIStyle aggregateLabelStyle;

		private GUIStyle aggregateValueStyle;

		private Texture2D panelTexture;

		private Texture2D headerTexture;

		private Texture2D footerTexture;

		private Texture2D sectionTexture;

		private Texture2D sectionHeaderTexture;

		private Texture2D buttonTexture;

		private Texture2D buttonHoverTexture;

		private Texture2D buttonActiveTexture;

		private Texture2D selectedTexture;

		private Texture2D equippedTexture;

		private Texture2D primaryTexture;

		private Texture2D primaryHoverTexture;

		private Texture2D lineTexture;

		private Texture2D sliderTrackTexture;

		private Texture2D sliderThumbTexture;

		private Texture2D scanlineTexture;

		private Texture2D frameTexture;

		private Texture2D rowTexture;

		private Texture2D alternateRowTexture;

		private Texture2D metricFillTexture;

		private Font uiFont;

		private bool ownsUiFont;

		private MechAggregatedStats cachedStats;

		private MechAggregatedStats cachedEquippedStats;

		private MechValidationResult cachedValidation;

		private MechLoadoutDefinition previewLoadout;

		private bool summaryDirty = true;

		private MechHangarPreviewRenderer previewRenderer;

		private bool previewDirty = true;

		private CursorLockMode previousCursorLockMode;

		private bool previousCursorVisible;

		public static bool IsAnyMenuVisible { get; private set; }

		private void Awake()
		{
			assembler = GetComponent<MechAssembler>();
			weaponSystem = GetComponent<MechTestWeaponSystem>();
			catalog = MechLoadoutSelectionStore.LoadCatalog();
			previewRenderer = GetComponent<MechHangarPreviewRenderer>();
			if (previewRenderer == null)
			{
				previewRenderer = base.gameObject.AddComponent<MechHangarPreviewRenderer>();
			}
		}

		private void Start()
		{
			if (assembler == null || catalog == null)
			{
				base.enabled = false;
				return;
			}
			CopySelection(MechLoadoutSelectionStore.ReadSelection(assembler.Loadout));
			appearance = MechAppearanceSelectionStore.Load();
			previewRenderer.SetAppearance(appearance);
			bool skipMenu = MechLoadoutSelectionStore.ConsumeSkipNextOpen();
			if (skipMenu)
			{
				RestoreGameplayState(restoreStalePause: true);
			}
			SetVisible(!skipMenu);
		}

		private void OnEnable()
		{
			if (visible)
			{
				IsAnyMenuVisible = true;
				AcquireMenuPause();
			}
		}

		private void OnDisable()
		{
			RestoreGameplayState(restoreStalePause: false);
			IsAnyMenuVisible = false;
		}

		private void OnDestroy()
		{
			RestoreGameplayState(restoreStalePause: false);
			IsAnyMenuVisible = false;
			DestroyTexture(panelTexture);
			DestroyTexture(headerTexture);
			DestroyTexture(footerTexture);
			DestroyTexture(sectionTexture);
			DestroyTexture(sectionHeaderTexture);
			DestroyTexture(buttonTexture);
			DestroyTexture(buttonHoverTexture);
			DestroyTexture(buttonActiveTexture);
			DestroyTexture(selectedTexture);
			DestroyTexture(equippedTexture);
			DestroyTexture(primaryTexture);
			DestroyTexture(primaryHoverTexture);
			DestroyTexture(lineTexture);
			DestroyTexture(sliderTrackTexture);
			DestroyTexture(sliderThumbTexture);
			DestroyTexture(scanlineTexture);
			DestroyTexture(frameTexture);
			DestroyTexture(rowTexture);
			DestroyTexture(alternateRowTexture);
			DestroyTexture(metricFillTexture);
			if (ownsUiFont && uiFont != null)
			{
				UnityEngine.Object.Destroy(uiFont);
			}
			if (previewLoadout != null)
			{
				UnityEngine.Object.Destroy(previewLoadout);
			}
		}

		private void Update()
		{
			Keyboard keyboard = Keyboard.current;
			if (keyboard == null)
			{
				return;
			}
			if (keyboard.f2Key.wasPressedThisFrame)
			{
				SetVisible(!visible);
			}
			else if (visible && keyboard.escapeKey.wasPressedThisFrame)
			{
				if (selectingPart)
				{
					ClosePartSelection();
				}
				else if (editingAppearance)
				{
					editingAppearance = false;
				}
				else
				{
					SetVisible(value: false);
				}
			}
		}

		private void SetVisible(bool value)
		{
			IsAnyMenuVisible = value;
			previewRenderer?.SetRenderingEnabled(value);
			if (visible != value)
			{
				visible = value;
				if (visible)
				{
					AcquireMenuPause();
					summaryDirty = true;
					previewDirty = true;
				}
				else
				{
					RestoreGameplayState(restoreStalePause: false);
				}
			}
		}

		private void AcquireMenuPause()
		{
			if (!ownsMenuPause)
			{
				// A script reload can preserve Time.timeScale while recreating this component.
				// Never adopt that stale zero as the gameplay time scale to restore later.
				previousTimeScale = Time.timeScale > Mathf.Epsilon ? Time.timeScale : 1f;
				previousCursorLockMode = Cursor.lockState;
				previousCursorVisible = Cursor.visible;
				ownsMenuPause = true;
			}

			Time.timeScale = 0f;
			Cursor.lockState = CursorLockMode.None;
			Cursor.visible = true;
		}

		private void RestoreGameplayState(bool restoreStalePause)
		{
			if (ownsMenuPause)
			{
				Time.timeScale = previousTimeScale > Mathf.Epsilon ? previousTimeScale : 1f;
				Cursor.lockState = previousCursorLockMode;
				Cursor.visible = previousCursorVisible;
				ownsMenuPause = false;
				return;
			}

			if (restoreStalePause && Time.timeScale <= Mathf.Epsilon)
			{
				Debug.LogWarning("[MechAssemblyMenu] Cleared a stale menu pause before entering combat.", this);
				Time.timeScale = 1f;
			}
		}

		private void OnGUI()
		{
			if (visible)
			{
				EnsureStyles();
				int depth = GUI.depth;
				GUI.depth = -10000;
				GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), panelTexture, ScaleMode.StretchToFill);
				GUI.DrawTextureWithTexCoords(new Rect(0f, 0f, Screen.width, Screen.height), scanlineTexture, new Rect(0f, 0f, (float)Screen.width / 4f, (float)Screen.height / 8f));
				GUI.DrawTexture(new Rect(0f, 0f, Screen.width, 84f), headerTexture, ScaleMode.StretchToFill);
				GUI.DrawTexture(new Rect(0f, (float)Screen.height - 64f, Screen.width, 64f), footerTexture, ScaleMode.StretchToFill);
				GUI.DrawTexture(new Rect(0f, 83f, Screen.width, 1f), lineTexture);
				GUI.DrawTexture(new Rect(0f, (float)Screen.height - 64f, Screen.width, 1f), lineTexture);
				DrawHeader();
				if (selectingPart)
				{
					DrawPartSelection();
				}
				else if (editingAppearance)
				{
					DrawAppearanceEditor();
				}
				else
				{
					DrawAssemblyOverview();
				}
				DrawFooter();
				GUI.depth = depth;
			}
		}

		private void DrawHeader()
		{
			GUI.DrawTexture(new Rect(32f, 0f, 258f, 84f), sectionHeaderTexture);
			GUI.DrawTexture(new Rect(32f, 0f, 1f, 84f), lineTexture);
			GUI.DrawTexture(new Rect(289f, 0f, 1f, 84f), frameTexture);
			GUI.Label(new Rect(48f, 12f, 244f, 42f), "机体组装", titleStyle);
			GUI.Label(new Rect(49f, 48f, 244f, 22f), "ASSEMBLY / CONFIGURATION", overlineStyle);
			if (!selectingPart)
			{
				if (GUI.Button(new Rect(304f, 22f, 112f, 42f), "装配", editingAppearance ? segmentButtonStyle : selectedSegmentButtonStyle))
				{
					editingAppearance = false;
					previewDirty = true;
				}
				if (GUI.Button(new Rect(424f, 22f, 112f, 42f), "涂装", editingAppearance ? selectedSegmentButtonStyle : segmentButtonStyle))
				{
					editingAppearance = true;
					selectingPart = false;
					previewDirty = true;
				}
			}
			GUI.Label(new Rect((float)Screen.width - 590f, 20f, 542f, 44f), selectingPart ? ("装配  /  " + SlotName(activeSlot) + "  /  部件选择") : (editingAppearance ? "机库  /  涂装设置" : "机库  /  装配总览"), breadcrumbStyle);
		}

		private void DrawAppearanceEditor()
		{
			if (appearance == null)
			{
				appearance = new MechAppearanceSettings();
			}
			float contentHeight = (float)Screen.height - 102f - 78f;
			Rect left = new Rect(32f, 102f, Mathf.Clamp((float)Screen.width * 0.235f, 350f, 490f), contentHeight);
			float rightWidth = Mathf.Clamp((float)Screen.width * 0.25f, 380f, 520f);
			Rect right = new Rect((float)Screen.width - rightWidth - 32f, 102f, rightWidth, contentHeight);
			Rect preview = new Rect(left.xMax + 14f, 102f, right.x - left.xMax - 28f, contentHeight);
			DrawSectionFrame(left, "涂装颜色");
			DrawSectionFrame(right, "材质与旧化");
			DrawSectionFrame(preview, "整机预览");
			DrawColorEditor(new Rect(left.x + 18f, left.y + 60f, left.width - 36f, left.height - 78f));
			DrawMaterialEditor(new Rect(right.x + 18f, right.y + 60f, right.width - 36f, right.height - 78f));
			DrawPreview(new Rect(preview.x + 2f, preview.y + 51f, preview.width - 4f, preview.height - 53f), selectedPartOnly: false);
		}

		private void DrawColorEditor(Rect rect)
		{
			string[] regionNames = new string[5] { "主色", "副色", "支撑结构", "可选区域", "设备区域" };
			float y = rect.y;
			for (int i = 0; i < regionNames.Length; i++)
			{
				Rect row = new Rect(rect.x, y, rect.width, 48f);
				if (GUI.Button(row, regionNames[i], (i == activeColorRegion) ? selectedButtonStyle : buttonStyle))
				{
					activeColorRegion = i;
				}
				DrawColorSwatch(new Rect(row.xMax - 58f, row.y + 9f, 42f, 30f), appearance.GetRegionColor(i));
				y += 56f;
			}
			y += 14f;
			GUI.Label(new Rect(rect.x, y, rect.width, 30f), regionNames[activeColorRegion], headingStyle);
			y += 38f;
			Color.RGBToHSV(appearance.GetRegionColor(activeColorRegion), out float hue, out float saturation, out float value);
			float a = hue;
			float oldSaturation = saturation;
			float oldValue = value;
			hue = DrawSliderRow(rect, ref y, "色相", hue, 0f, 1f, $"{hue * 360f:0}°");
			saturation = DrawSliderRow(rect, ref y, "饱和度", saturation, 0f, 1f, $"{saturation * 100f:0}%");
			value = DrawSliderRow(rect, ref y, "明度", value, 0f, 1f, $"{value * 100f:0}%");
			if (!Mathf.Approximately(a, hue) || !Mathf.Approximately(oldSaturation, saturation) || !Mathf.Approximately(oldValue, value))
			{
				appearance.SetRegionColor(activeColorRegion, Color.HSVToRGB(hue, saturation, value));
				ApplyAppearancePreview();
			}
			y += 14f;
			GUI.Label(new Rect(rect.x, y, rect.width, 28f), "快速色板", labelStyle);
			y += 34f;
			Color[] palette = new Color[8]
			{
				new Color(0.12f, 0.16f, 0.2f),
				new Color(0.24f, 0.28f, 0.32f),
				new Color(0.65f, 0.68f, 0.7f),
				new Color(0.78f, 0.76f, 0.68f),
				new Color(0.1f, 0.32f, 0.62f),
				new Color(0.48f, 0.1f, 0.08f),
				new Color(0.18f, 0.42f, 0.22f),
				new Color(0.72f, 0.48f, 0.08f)
			};
			float swatchWidth = (rect.width - 24f) / 4f;
			for (int j = 0; j < palette.Length; j++)
			{
				Rect swatchRect = new Rect(rect.x + (float)(j % 4) * (swatchWidth + 8f), y + (float)(j / 4) * 46f, swatchWidth, 36f);
				if (GUI.Button(swatchRect, GUIContent.none, buttonStyle))
				{
					appearance.SetRegionColor(activeColorRegion, palette[j]);
					ApplyAppearancePreview();
				}
				DrawColorSwatch(new Rect(swatchRect.x + 4f, swatchRect.y + 4f, swatchRect.width - 8f, swatchRect.height - 8f), palette[j]);
			}
		}

		private void DrawMaterialEditor(Rect rect)
		{
			float y = rect.y;
			GUI.Label(new Rect(rect.x, y, rect.width, 30f), "材质组合", headingStyle);
			y += 40f;
			GUI.Label(new Rect(rect.x, y, rect.width, 28f), $"组合 {appearance.lusterLevel * 6 + appearance.reflectivenessLevel + 1:00}  /  36", mutedStyle);
			y += 40f;
			GUI.Label(new Rect(rect.x, y, rect.width, 28f), "反射", labelStyle);
			y += 32f;
			int reflectivenessLevel = appearance.reflectivenessLevel;
			appearance.reflectivenessLevel = DrawSixStepSelector(new Rect(rect.x, y, rect.width, 38f), appearance.reflectivenessLevel);
			y += 58f;
			GUI.Label(new Rect(rect.x, y, rect.width, 28f), "光泽度", labelStyle);
			y += 32f;
			int previousLuster = appearance.lusterLevel;
			appearance.lusterLevel = DrawSixStepSelector(new Rect(rect.x, y, rect.width, 38f), appearance.lusterLevel);
			if (reflectivenessLevel != appearance.reflectivenessLevel || previousLuster != appearance.lusterLevel)
			{
				ApplyAppearancePreview();
			}
			y += 64f;
			GUI.DrawTexture(new Rect(rect.x, y, rect.width, 1f), lineTexture);
			y += 20f;
			GUI.Label(new Rect(rect.x, y, rect.width, 30f), "旧化", headingStyle);
			y += 42f;
			float weatheringAmount = appearance.weatheringAmount;
			appearance.weatheringAmount = DrawSliderRow(rect, ref y, "磨损度", appearance.weatheringAmount, 0f, 1f, $"{appearance.weatheringAmount * 100f:0}%");
			GUI.Label(new Rect(rect.x, y + 4f, rect.width, 54f), "旧化独立于颜色和材质。它改变局部掉漆、裸露表面与高光响应，不会覆盖五个涂装区域。", noteStyle);
			if (!Mathf.Approximately(weatheringAmount, appearance.weatheringAmount))
			{
				ApplyAppearancePreview();
			}
		}

		private int DrawSixStepSelector(Rect rect, int selected)
		{
			float width = (rect.width - 35f) / 6f;
			for (int i = 0; i < 6; i++)
			{
				if (GUI.Button(new Rect(rect.x + (float)i * (width + 7f), rect.y, width, rect.height), (i + 1).ToString(), (i == selected) ? selectedSegmentButtonStyle : segmentButtonStyle))
				{
					selected = i;
				}
			}
			return selected;
		}

		private float DrawSliderRow(Rect area, ref float y, string name, float value, float minimum, float maximum, string text)
		{
			GUI.Label(new Rect(area.x, y, 96f, 28f), name, labelStyle);
			value = GUI.HorizontalSlider(new Rect(area.x + 100f, y + 6f, area.width - 184f, 22f), value, minimum, maximum, sliderStyle, sliderThumbStyle);
			GUI.Label(new Rect(area.xMax - 76f, y, 76f, 28f), text, valueStyle);
			y += 40f;
			return value;
		}

		private static void DrawColorSwatch(Rect rect, Color color)
		{
			Color color2 = GUI.color;
			GUI.color = color;
			GUI.DrawTexture(rect, Texture2D.whiteTexture);
			GUI.color = color2;
		}

		private void ApplyAppearancePreview()
		{
			appearance.Normalize();
			previewRenderer?.SetAppearance(appearance);
		}

		private void DrawAssemblyOverview()
		{
			float contentHeight = (float)Screen.height - 102f - 78f;
			Rect left = new Rect(32f, 102f, Mathf.Clamp((float)Screen.width * 0.235f, 390f, 500f), contentHeight);
			float rightWidth = Mathf.Clamp((float)Screen.width * 0.265f, 430f, 560f);
			Rect right = new Rect((float)Screen.width - rightWidth - 32f, 102f, rightWidth, contentHeight);
			Rect preview = new Rect(left.xMax + 14f, 102f, right.x - left.xMax - 28f, contentHeight);
			DrawSectionFrame(left, "当前装配");
			DrawSectionFrame(right, "机体总数值");
			DrawSectionFrame(preview, "整机预览");
			Rect content = new Rect(left.x + 16f, left.y + 58f, left.width - 32f, left.height - 74f);
			Rect view = new Rect(0f, 0f, content.width - 18f, (float)Slots.Length * 62f + 108f);
			slotScroll = GUI.BeginScrollView(content, slotScroll, view);
			float y = 0f;
			for (int i = 0; i < Slots.Length; i++)
			{
				if (i == 0 || i == 4 || i == 9)
				{
					GUI.Label(new Rect(4f, y, view.width - 8f, 28f), i switch
					{
						4 => "框架", 
						0 => "武器", 
						_ => "内部部件", 
					}, headingStyle);
					y += 34f;
				}
				DrawSlotButton(new Rect(4f, y, view.width - 8f, 54f), Slots[i]);
				y += 62f;
			}
			GUI.EndScrollView();
			DrawPreview(new Rect(preview.x + 2f, preview.y + 51f, preview.width - 4f, preview.height - 53f), selectedPartOnly: false);
			DrawAggregateStats(new Rect(right.x + 18f, right.y + 58f, right.width - 36f, right.height - 76f));
		}

		private void DrawPartSelection()
		{
			float contentHeight = (float)Screen.height - 102f - 78f;
			Rect left = new Rect(32f, 102f, Mathf.Clamp((float)Screen.width * 0.21f, 350f, 460f), contentHeight);
			float rightWidth = Mathf.Clamp((float)Screen.width * 0.27f, 430f, 580f);
			Rect right = new Rect((float)Screen.width - rightWidth - 32f, 102f, rightWidth, contentHeight);
			Rect middle = new Rect(left.xMax + 14f, 102f, right.x - left.xMax - 28f, contentHeight);
			DrawSectionFrame(left, SlotName(activeSlot));
			DrawSectionFrame(middle, "部件性能");
			DrawSectionFrame(right, "装配后总数值");
			DrawSlotNavigation(new Rect(left.x + 14f, left.y + 58f, left.width - 28f, 38f));
			Rect searchRect = new Rect(left.x + 14f, left.y + 104f, left.width - 28f, 40f);
			search = GUI.TextField(searchRect, search, searchFieldStyle);
			if (string.IsNullOrEmpty(search))
			{
				GUI.Label(new Rect(searchRect.x + 12f, searchRect.y, searchRect.width - 24f, searchRect.height), "搜索名称或部件编号", placeholderStyle);
			}
			DrawPartList(new Rect(left.x + 12f, left.y + 154f, left.width - 24f, left.height - 168f));
			Rect identity = new Rect(middle.x + 20f, middle.y + 60f, middle.width - 40f, 78f);
			DrawSelectedPartIdentity(identity);
			float maximumPreviewHeight = Mathf.Max(180f, middle.height - 330f);
			float previewHeight = Mathf.Clamp(middle.height * 0.38f, 180f, Mathf.Min(455f, maximumPreviewHeight));
			Rect preview = new Rect(middle.x + 18f, identity.yMax + 4f, middle.width - 36f, previewHeight);
			DrawPreview(preview, selectedPartOnly: true);
			Rect stats = new Rect(middle.x + 18f, preview.yMax + 14f, middle.width - 36f, middle.yMax - preview.yMax - 30f);
			DrawSelectedPartStats(stats);
			DrawAggregateStats(new Rect(right.x + 18f, right.y + 58f, right.width - 36f, right.height - 76f));
		}

		private void DrawSlotButton(Rect rect, MechPartSlot slot)
		{
			selection.TryGetValue(slot, out var partId);
			MechPartCatalogEntry entry = catalog.Find(partId);
			if (GUI.Button(rect, GUIContent.none, buttonStyle))
			{
				OpenPartSelection(slot);
			}
			GUI.DrawTexture(new Rect(rect.x + 1f, rect.y + 1f, 54f, rect.height - 2f), sectionHeaderTexture);
			GUI.Label(new Rect(rect.x + 3f, rect.y, 50f, rect.height), SlotShortName(slot), overlineStyle);
			GUI.Label(new Rect(rect.x + 68f, rect.y + 5f, rect.width - 178f, 22f), SlotName(slot), labelStyle);
			GUI.Label(new Rect(rect.x + 68f, rect.y + 27f, rect.width - 178f, 22f), (entry == null) ? "未安装" : entry.DisplayName, (entry == null) ? idStyle : headingStyle);
			if (entry != null)
			{
				DrawStatusTag(new Rect(rect.xMax - 98f, rect.y + 16f, 84f, 22f), entry, onSelectedRow: false);
			}
		}

		private void DrawSlotNavigation(Rect rect)
		{
			IReadOnlyList<MechPartSlot> peers = GetSlotGroup(activeSlot);
			float width = (rect.width - 5f * (float)(peers.Count - 1)) / (float)peers.Count;
			for (int i = 0; i < peers.Count; i++)
			{
				MechPartSlot slot = peers[i];
				GUIStyle style = ((slot == activeSlot) ? selectedSegmentButtonStyle : segmentButtonStyle);
				if (GUI.Button(new Rect(rect.x + (float)i * (width + 5f), rect.y, width, rect.height), SlotShortName(slot), style) && slot != activeSlot)
				{
					OpenPartSelection(slot);
				}
			}
		}

		private void DrawPartList(Rect rect)
		{
			List<MechPartCatalogEntry> entries = (from mechPartCatalogEntry in catalog.GetEntries(activeSlot)
				where string.IsNullOrWhiteSpace(search) || mechPartCatalogEntry.DisplayName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || mechPartCatalogEntry.PartId.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
				orderby mechPartCatalogEntry.IsTested descending
				select mechPartCatalogEntry).ThenBy<MechPartCatalogEntry, string>((MechPartCatalogEntry mechPartCatalogEntry) => mechPartCatalogEntry.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
			Rect view = new Rect(0f, 0f, rect.width - 18f, Mathf.Max(rect.height, (float)entries.Count * 88f));
			partScroll = GUI.BeginScrollView(rect, partScroll, view);
			selection.TryGetValue(activeSlot, out var equippedId);
			for (int i = 0; i < entries.Count; i++)
			{
				MechPartCatalogEntry entry = entries[i];
				Rect row = new Rect(2f, (float)i * 88f, view.width - 4f, 80f);
				bool isSelected = string.Equals(entry.PartId, pendingPartId, StringComparison.OrdinalIgnoreCase);
				bool isEquipped = string.Equals(entry.PartId, equippedId, StringComparison.OrdinalIgnoreCase);
				GUIStyle style = (isSelected ? selectedPartButtonStyle : (isEquipped ? equippedPartButtonStyle : partButtonStyle));
				if (GUI.Button(row, GUIContent.none, style))
				{
					pendingPartId = entry.PartId;
					selectedStatsScroll = Vector2.zero;
					summaryDirty = true;
					previewDirty = true;
				}
				GUIStyle rowHeading = (isSelected ? darkHeadingStyle : headingStyle);
				GUIStyle rowLabel = (isSelected ? darkLabelStyle : idStyle);
				GUI.DrawTexture(new Rect(row.x + 1f, row.y + 1f, 66f, row.height - 2f), isSelected ? selectedTexture : sectionHeaderTexture);
				GUI.Label(new Rect(row.x + 5f, row.y + 9f, 58f, 30f), $"{i + 1:00}", isSelected ? darkCenteredStyle : overlineStyle);
				GUI.Label(new Rect(row.x + 80f, row.y + 13f, row.width - 92f, 26f), entry.DisplayName, rowHeading);
				GUI.Label(new Rect(row.x + 80f, row.y + 43f, row.width - 188f, 22f), entry.PartId, rowLabel);
				if (isEquipped)
				{
					GUI.Label(new Rect(row.x + 5f, row.y + 46f, 58f, 20f), "E", isSelected ? darkCenteredStyle : testedStyle);
				}
				DrawStatusTag(new Rect(row.xMax - 100f, row.y + 43f, 88f, 24f), entry, isSelected);
			}
			GUI.EndScrollView();
		}

		private void DrawSelectedPartIdentity(Rect rect)
		{
			MechPartCatalogEntry entry = catalog.Find(pendingPartId);
			MechPartDefinition part = entry?.LoadPart();
			if (entry == null || part == null)
			{
				GUI.Label(rect, "该部件资源不可用。", warningStyle);
				return;
			}
			GUI.Label(new Rect(rect.x, rect.y, rect.width - 118f, 30f), entry.DisplayName, titleStyle);
			GUI.Label(new Rect(rect.x, rect.y + 34f, rect.width - 118f, 24f), entry.PartId, idStyle);
			string manufacturer = (string.IsNullOrWhiteSpace(part.Manufacturer) ? "AC6 / PARTS ARCHIVE" : part.Manufacturer);
			GUI.Label(new Rect(rect.x, rect.y + 56f, rect.width - 118f, 20f), manufacturer, idStyle);
			DrawStatusTag(new Rect(rect.xMax - 104f, rect.y + 8f, 96f, 26f), entry, onSelectedRow: false);
		}

		private void DrawSelectedPartStats(Rect rect)
		{
			MechPartCatalogEntry entry = catalog.Find(pendingPartId);
			MechPartDefinition part = entry?.LoadPart();
			if (entry == null || part == null)
			{
				GUI.Label(rect, "该部件资源不可用。", warningStyle);
				return;
			}
			selection.TryGetValue(activeSlot, out var equippedPartId);
			MechPartCatalogEntry equippedEntry = catalog.Find(equippedPartId);
			MechPartDefinition equippedPart = equippedEntry?.LoadPart();
			bool hasImportedInternalData = part.InternalPerformance != null && part.InternalPerformance.HasData;
			bool hasCoreNozzles = part.Slot == MechPartSlot.Core && part.CorePerformance != null && part.CorePerformance.HasData;
			bool hasBoosterData = part.Slot == MechPartSlot.Booster && part.BoosterPerformance != null && part.BoosterPerformance.HasData;
			bool hasGeneratorData = part.Slot == MechPartSlot.Generator && part.GeneratorPerformance != null && part.GeneratorPerformance.HasData;
			bool hasRadarData = part.Slot == MechPartSlot.Radar && part.RadarPerformance != null && part.RadarPerformance.HasData;
			bool hasFireControlData = part.Slot == MechPartSlot.FireControlSystem && part.FireControlPerformance != null && part.FireControlPerformance.HasData;
			float viewHeight = entry.IsWeapon
				? (part.HasTag("point-defense") ? 2100f : 1500f)
				: (hasImportedInternalData ? 1180f : ((hasCoreNozzles || hasBoosterData || hasGeneratorData || hasRadarData || hasFireControlData) ? 2100f : 1760f));
			Rect view = new Rect(0f, 0f, rect.width - 18f, viewHeight);
			selectedStatsScroll = GUI.BeginScrollView(rect, selectedStatsScroll, view);
			float y = 0f;
			if (equippedPart != null)
			{
				DrawComparisonColumnHeader(view, ref y);
			}
			if (!string.IsNullOrWhiteSpace(entry.StatusNote) || !entry.IsTested)
			{
				GUI.Label(new Rect(0f, y, view.width - 8f, 42f), string.IsNullOrWhiteSpace(entry.StatusNote) ? "尚未完成战斗功能验证。" : entry.StatusNote, entry.IsTested ? noteStyle : warningStyle);
				y += 50f;
			}
			if (entry.IsWeapon)
			{
				DrawWeaponPerformanceStats(view, ref y, entry, part, equippedEntry, equippedPart);
			}
			else if (hasBoosterData)
			{
				DrawBoosterPartStats(view, ref y, part, equippedPart);
			}
			else if (hasGeneratorData)
			{
				DrawGeneratorPartStats(view, ref y, part, equippedPart);
			}
			else if (hasRadarData)
			{
				DrawRadarPartStats(view, ref y, part, equippedPart);
			}
			else if (hasFireControlData)
			{
				DrawFireControlPartStats(view, ref y, part, equippedPart);
			}
			else if (hasImportedInternalData)
			{
				DrawImportedInternalPartStats(view, ref y, part, equippedPart);
			}
			else if (part.Slot == MechPartSlot.Head)
			{
				DrawHeadPartStats(view, ref y, part, equippedPart);
			}
			else if (part.Slot == MechPartSlot.Arms)
			{
				DrawArmPartStats(view, ref y, part, equippedPart);
			}
			else if (part.Slot == MechPartSlot.Core)
			{
				DrawCorePartStats(view, ref y, part, equippedPart);
			}
			else if (part.Slot == MechPartSlot.Legs)
			{
				DrawLegPartStats(view, ref y, part, equippedPart);
			}
			else
			{
				DrawGeneralPartStats(view, ref y, part.Stats, equippedPart?.Stats);
			}
			GUI.EndScrollView();
		}

		private void DrawRadarPartStats(Rect view, ref float y, MechPartDefinition part, MechPartDefinition equippedPart)
		{
			MechPartStats stats = part.Stats;
			MechPartStats equippedStats = equippedPart?.Stats;
			MechRadarPerformance radar = part.RadarPerformance;
			MechRadarPerformance equippedRadar = equippedPart?.RadarPerformance;
			bool compare = equippedRadar != null && equippedRadar.HasData;
			DrawPartMetricGroup(view, ref y, "结构与负载");
			DrawPartComparisonMetric(view, ref y, "质量", stats.massKg, equippedStats?.massKg, 5000f, "0", " kg", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "待机功耗", ToKilowatts(stats.idlePowerDrawMw), ToKilowatts(equippedStats?.idlePowerDrawMw), 5000f, "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "工作功耗", ToKilowatts(stats.peakPowerDrawMw), ToKilowatts(equippedStats?.peakPowerDrawMw), 10000f, "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "待机产热", ToKilowatts(stats.idleHeatMw), ToKilowatts(equippedStats?.idleHeatMw), 5000f, "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "工作产热", ToKilowatts(stats.activeHeatMw), ToKilowatts(equippedStats?.activeHeatMw), 10000f, "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "热容量", stats.heatCapacityMj, equippedStats?.heatCapacityMj, 100f, "0.0", " MJ", higherIsBetter: true);
			DrawPartMetricGroup(view, ref y, "搜索与跟踪");
			DrawPartComparisonMetric(view, ref y, "参考探测距离", radar.referenceDetectionRangeKm, compare ? equippedRadar.referenceDetectionRangeKm : null, 100f, "0.0", " km", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "稳定跟踪距离", radar.stableTrackRangeKm, compare ? equippedRadar.stableTrackRangeKm : null, 100f, "0.0", " km", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "水平扫描范围", radar.searchAzimuthDeg, compare ? equippedRadar.searchAzimuthDeg : null, 180f, "0", "°", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "垂直扫描范围", radar.searchElevationDeg, compare ? equippedRadar.searchElevationDeg : null, 120f, "0", "°", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "航迹容量", radar.trackCapacity, compare ? equippedRadar.trackCapacity : null, 32f, "0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "STT 通道", radar.sttChannelCapacity, compare ? equippedRadar.sttChannelCapacity : null, 8f, "0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "最大跟踪角速度", radar.maximumTrackingRateDegPerSecond, compare ? equippedRadar.maximumTrackingRateDegPerSecond : null, 360f, "0", " °/s", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "锁定维持能力", radar.LockMaintenanceRating, compare ? equippedRadar.LockMaintenanceRating : null, 100f, "0.0", string.Empty, higherIsBetter: true);
			DrawPartMetricGroup(view, ref y, "测量精度");
			DrawPartComparisonMetric(view, ref y, "距离误差", radar.rangeAccuracyM, compare ? equippedRadar.rangeAccuracyM : null, 50f, "0.0", " m", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "角度误差", radar.angularAccuracyMrad, compare ? equippedRadar.angularAccuracyMrad : null, 60f, "0.00", " mrad", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "径向速度误差", radar.radialVelocityAccuracyMps, compare ? equippedRadar.radialVelocityAccuracyMps : null, 10f, "0.00", " m/s", higherIsBetter: false);
		}

		private void DrawFireControlPartStats(Rect view, ref float y, MechPartDefinition part, MechPartDefinition equippedPart)
		{
			MechPartStats stats = part.Stats;
			MechPartStats equippedStats = equippedPart?.Stats;
			MechFireControlPerformance fcs = part.FireControlPerformance;
			MechFireControlPerformance equippedFcs = equippedPart?.FireControlPerformance;
			bool compare = equippedFcs != null && equippedFcs.HasData;
			DrawPartMetricGroup(view, ref y, "结构与负载");
			DrawPartComparisonMetric(view, ref y, "质量", stats.massKg, equippedStats?.massKg, 5000f, "0", " kg", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "待机功耗", ToKilowatts(stats.idlePowerDrawMw), ToKilowatts(equippedStats?.idlePowerDrawMw), 5000f, "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "工作功耗", ToKilowatts(stats.peakPowerDrawMw), ToKilowatts(equippedStats?.peakPowerDrawMw), 10000f, "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "待机产热", ToKilowatts(stats.idleHeatMw), ToKilowatts(equippedStats?.idleHeatMw), 5000f, "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "工作产热", ToKilowatts(stats.activeHeatMw), ToKilowatts(equippedStats?.activeHeatMw), 10000f, "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "热容量", stats.heatCapacityMj, equippedStats?.heatCapacityMj, 100f, "0.0", " MJ", higherIsBetter: true);
			DrawPartMetricGroup(view, ref y, "解算性能");
			DrawPartComparisonMetric(view, ref y, "基础解算误差", fcs.baseSolutionErrorMrad, compare ? equippedFcs.baseSolutionErrorMrad : null, 1f, "0.000", " mrad", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "最大预测时间", fcs.maximumPredictionTimeS, compare ? equippedFcs.maximumPredictionTimeS : null, 12f, "0.0", " s", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "解算刷新率", fcs.solutionUpdateRateHz, compare ? equippedFcs.solutionUpdateRateHz : null, 120f, "0.0", " Hz", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "解算延迟", fcs.solutionLatencyMs, compare ? equippedFcs.solutionLatencyMs : null, 200f, "0.0", " ms", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "航迹重捕时间", fcs.trackReacquisitionTimeS, compare ? equippedFcs.trackReacquisitionTimeS : null, 3f, "0.00", " s", higherIsBetter: false);
			DrawPartMetricGroup(view, ref y, "交战容量");
			DrawPartComparisonMetric(view, ref y, "并行解算容量", fcs.simultaneousSolutionCapacity, compare ? equippedFcs.simultaneousSolutionCapacity : null, 32f, "0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "武器控制通道", fcs.weaponControlChannels, compare ? equippedFcs.weaponControlChannels : null, 16f, "0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "制导通道", fcs.guidanceChannelCapacity, compare ? equippedFcs.guidanceChannelCapacity : null, 16f, "0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "多目标交战容量", fcs.multiTargetEngagementCapacity, compare ? equippedFcs.multiTargetEngagementCapacity : null, 16f, "0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "点防御性能", fcs.pointDefenseRating, compare ? equippedFcs.pointDefenseRating : null, 100f, "0", string.Empty, higherIsBetter: true);
		}

		private void DrawImportedInternalPartStats(Rect view, ref float y, MechPartDefinition part, MechPartDefinition equippedPart)
		{
			MechInternalPartPerformance performance = part.InternalPerformance;
			MechInternalPartPerformance equippedPerformance = equippedPart?.InternalPerformance;
			MechInternalPartPerformance comparable = ((equippedPerformance != null && equippedPerformance.kind == performance.kind) ? equippedPerformance : null);
			DrawPartMetricGroup(view, ref y, "原始资源来源");
			DrawPartTextRow(view, ref y, "参数表行 ID", performance.sourceParamId);
			DrawPartTextRow(view, ref y, "菜单图编号", $"WP_A_{performance.sourceIconId}");
			if (performance.kind == MechInternalPartPerformanceKind.FireControlSystem)
			{
				DrawPartMetricGroup(view, ref y, "原版火控参数");
				DrawPartComparisonMetric(view, ref y, "EN 负荷", performance.sourceEnergyLoad, comparable?.sourceEnergyLoad, 520f, "0", string.Empty, higherIsBetter: false);
				DrawPartComparisonMetric(view, ref y, "导弹锁定修正", performance.missileLockCorrection, comparable?.missileLockCorrection, 1.7f, "0.00", string.Empty, higherIsBetter: true);
				DrawPartComparisonMetric(view, ref y, "多重锁定修正", performance.multiLockCorrection, comparable?.multiLockCorrection, 1.7f, "0.00", string.Empty, higherIsBetter: true);
				DrawPartComparisonMetric(view, ref y, "近距离辅助", performance.closeRangeAssist, comparable?.closeRangeAssist, 1f, "0.00", string.Empty, higherIsBetter: true);
				DrawPartComparisonMetric(view, ref y, "中距离辅助", performance.mediumRangeAssist, comparable?.mediumRangeAssist, 1f, "0.00", string.Empty, higherIsBetter: true);
				DrawPartComparisonMetric(view, ref y, "远距离辅助", performance.longRangeAssist, comparable?.longRangeAssist, 1f, "0.00", string.Empty, higherIsBetter: true);
			}
			else if (performance.kind == MechInternalPartPerformanceKind.Generator)
			{
				DrawPartMetricGroup(view, ref y, "原版发电机参数");
				DrawPartComparisonMetric(view, ref y, "EN 容量", performance.energyCapacity, comparable?.energyCapacity, 4600f, "0", string.Empty, higherIsBetter: true);
				DrawPartComparisonMetric(view, ref y, "EN 输出", performance.energyOutput, comparable?.energyOutput, 4600f, "0", string.Empty, higherIsBetter: true);
				DrawPartComparisonMetric(view, ref y, "EN 补充延迟（原始）", performance.energyRecharge, comparable?.energyRecharge, 4.5f, "0.00", string.Empty, higherIsBetter: false);
				DrawPartComparisonMetric(view, ref y, "供给复原延迟（原始）", performance.supplyRecovery, comparable?.supplyRecovery, 3.5f, "0.00", string.Empty, higherIsBetter: false);
				DrawPartComparisonMetric(view, ref y, "复原时 EN 供给", performance.postRecoveryEnergySupply, comparable?.postRecoveryEnergySupply, 3300f, "0", string.Empty, higherIsBetter: true);
				DrawPartComparisonMetric(view, ref y, "EN 武器适性", performance.energyWeaponSpecialization, comparable?.energyWeaponSpecialization, 160f, "0", string.Empty, higherIsBetter: true);
				DrawPartTextRow(view, ref y, "推进焰色参数 ID", performance.boosterEmitterColorId.ToString());
			}
			DrawPartMetricGroup(view, ref y, "安装信息");
			DrawPartComparisonMetric(view, ref y, "质量", part.Stats.massKg, equippedPart?.Stats.massKg, 12000f, "0", " kg", higherIsBetter: false);
			DrawPartTextRow(view, ref y, "仿真状态", "尚未映射到本项目数值体系");
		}

		private void DrawWeaponPerformanceStats(Rect view, ref float y, MechPartCatalogEntry entry, MechPartDefinition part, MechPartCatalogEntry equippedEntry, MechPartDefinition equippedPart)
		{
			MechWeaponPerformanceSnapshot weapon = default(MechWeaponPerformanceSnapshot);
			bool configured = weaponSystem != null && weaponSystem.TryGetWeaponPerformance(part, out weapon);
			MechWeaponPerformanceSnapshot equippedWeapon = default(MechWeaponPerformanceSnapshot);
			bool equippedConfigured = equippedPart != null && weaponSystem != null && weaponSystem.TryGetWeaponPerformance(equippedPart, out equippedWeapon);
			string unavailable = "未录入";
			DrawPartMetricGroup(view, ref y, "武器性能");
			DrawPartTextRow(view, ref y, "武器类别", configured ? weapon.WeaponType : unavailable, equippedConfigured ? equippedWeapon.WeaponType : null);
			if (configured)
			{
				DrawPartMetric(view, ref y, "攻击力", weapon.DirectHitDamage, 500f, $"{weapon.DirectHitDamage:0}", Compared(equippedConfigured, equippedWeapon.DirectHitDamage), equippedConfigured ? $"{equippedWeapon.DirectHitDamage:0}" : null);
				if (weapon.HasEffectiveRange)
				{
					DrawPartMetric(view, ref y, "有效射程", weapon.EffectiveRangeMeters, 50000f, FormatDistance(weapon.EffectiveRangeMeters), Compared(equippedConfigured && equippedWeapon.HasEffectiveRange, equippedWeapon.EffectiveRangeMeters), (equippedConfigured && equippedWeapon.HasEffectiveRange) ? FormatDistance(equippedWeapon.EffectiveRangeMeters) : null);
				}
				else
				{
					DrawPartTextRow(view, ref y, "有效射程", "未标定", (equippedConfigured && equippedWeapon.HasEffectiveRange) ? FormatDistance(equippedWeapon.EffectiveRangeMeters) : null);
				}
				DrawPartMetric(view, ref y, "最大射程", weapon.MaximumRangeMeters, 50000f, FormatDistance(weapon.MaximumRangeMeters), Compared(equippedConfigured, equippedWeapon.MaximumRangeMeters), equippedConfigured ? FormatDistance(equippedWeapon.MaximumRangeMeters) : null);
			}
			else
			{
				DrawPartTextRow(view, ref y, "攻击力", unavailable);
				DrawPartTextRow(view, ref y, "有效射程", unavailable);
				DrawPartTextRow(view, ref y, "最大射程", unavailable);
			}
			DrawPartMetricGroup(view, ref y, "弹道与精度");
			if (configured)
			{
				DrawPartMetric(view, ref y, weapon.IsGuidedMissile ? "出筒速度" : "弹丸初速", weapon.MuzzleVelocityMetersPerSecond, 3000f, $"{weapon.MuzzleVelocityMetersPerSecond:0} m/s", Compared(equippedConfigured, equippedWeapon.MuzzleVelocityMetersPerSecond), equippedConfigured ? $"{equippedWeapon.MuzzleVelocityMetersPerSecond:0} m/s" : null);
				if (weapon.HasFireRate)
				{
					DrawPartMetric(view, ref y, "射速", weapon.FireRateRoundsPerMinute, 1200f, $"{weapon.FireRateRoundsPerMinute:0.#} rpm", Compared(equippedConfigured && equippedWeapon.HasFireRate, equippedWeapon.FireRateRoundsPerMinute), (equippedConfigured && equippedWeapon.HasFireRate) ? $"{equippedWeapon.FireRateRoundsPerMinute:0.#} rpm" : null);
				}
				else
				{
					DrawPartTextRow(view, ref y, "射速", "由发射动作周期决定");
				}
				if (weapon.IsGuidedMissile)
				{
					DrawPartMetric(view, ref y, "最大飞行时间", weapon.ProjectileLifetimeSeconds, 300f, $"{weapon.ProjectileLifetimeSeconds:0.#} s", Compared(equippedConfigured && equippedWeapon.IsGuidedMissile, equippedWeapon.ProjectileLifetimeSeconds), (equippedConfigured && equippedWeapon.IsGuidedMissile) ? $"{equippedWeapon.ProjectileLifetimeSeconds:0.#} s" : null);
					DrawPartMetric(view, ref y, "导引头距离", weapon.SeekerRangeMeters, 50000f, FormatDistance(weapon.SeekerRangeMeters), Compared(equippedConfigured && equippedWeapon.IsGuidedMissile, equippedWeapon.SeekerRangeMeters), (equippedConfigured && equippedWeapon.IsGuidedMissile) ? FormatDistance(equippedWeapon.SeekerRangeMeters) : null);
					DrawPartMetric(view, ref y, "最大角速度", weapon.MaximumAngularRateDegreesPerSecond, 600f, $"{weapon.MaximumAngularRateDegreesPerSecond:0} °/s", Compared(equippedConfigured && equippedWeapon.IsGuidedMissile, equippedWeapon.MaximumAngularRateDegreesPerSecond), (equippedConfigured && equippedWeapon.IsGuidedMissile) ? $"{equippedWeapon.MaximumAngularRateDegreesPerSecond:0} °/s" : null);
				}
				else
				{
					string accuracyLabel = (weapon.IsAutomatic ? "基础精度" : "精度");
					DrawPartMetric(view, ref y, accuracyLabel, weapon.BaseDispersionMilliradians, 1f, $"{weapon.BaseDispersionMilliradians:0.00} mrad", Compared(equippedConfigured && !equippedWeapon.IsGuidedMissile && equippedWeapon.HasAccuracy, equippedWeapon.BaseDispersionMilliradians), (equippedConfigured && !equippedWeapon.IsGuidedMissile && equippedWeapon.HasAccuracy) ? $"{equippedWeapon.BaseDispersionMilliradians:0.00} mrad" : null, higherIsBetter: false);
					if (weapon.IsAutomatic && weapon.HasSustainedAccuracy)
					{
						DrawPartMetric(view, ref y, "持续射击精度", weapon.SustainedDispersionMilliradians, 1f, $"{weapon.SustainedDispersionMilliradians:0.00} mrad", Compared(equippedConfigured && equippedWeapon.HasSustainedAccuracy, equippedWeapon.SustainedDispersionMilliradians), (equippedConfigured && equippedWeapon.HasSustainedAccuracy) ? $"{equippedWeapon.SustainedDispersionMilliradians:0.00} mrad" : null, higherIsBetter: false);
					}
					DrawPartTextRow(view, ref y, "火控误差", "随航迹质量动态变化");
				}
			}
			else
			{
				DrawPartTextRow(view, ref y, "弹丸初速", unavailable);
				DrawPartTextRow(view, ref y, "射速", unavailable);
				DrawPartTextRow(view, ref y, "基础精度", unavailable);
				DrawPartTextRow(view, ref y, "持续射击精度", unavailable);
				DrawPartTextRow(view, ref y, "火控误差", unavailable);
			}
			DrawPartMetricGroup(view, ref y, "弹药与操作");
			if (configured)
			{
				DrawPartTextRow(view, ref y, "单发后坐冲量", weapon.HasRecoilImpulse ? $"{weapon.RecoilImpulseNewtonSeconds:0.#} N·s" : (weapon.IsGuidedMissile ? "不适用" : "未标定"));
				DrawPartMetric(view, ref y, "弹匣容量", weapon.MagazineCapacity, 64f, $"{weapon.MagazineCapacity} 发", Compared(equippedConfigured, equippedWeapon.MagazineCapacity), equippedConfigured ? $"{equippedWeapon.MagazineCapacity} 发" : null);
				float totalAmmoScale = weapon.IsPointDefense ? 1000f : 500f;
				DrawPartMetric(view, ref y, "总弹药量", weapon.TotalAmmunition, totalAmmoScale, $"{weapon.TotalAmmunition} 发", Compared(equippedConfigured, equippedWeapon.TotalAmmunition), equippedConfigured ? $"{equippedWeapon.TotalAmmunition} 发" : null);
				DrawPartMetric(view, ref y, "装填时间", weapon.ReloadTimeSeconds, 10f, (weapon.ReloadTimeSeconds > 0f) ? $"{weapon.ReloadTimeSeconds:0.00} s" : "即时换架", Compared(equippedConfigured, equippedWeapon.ReloadTimeSeconds), (!equippedConfigured) ? null : ((equippedWeapon.ReloadTimeSeconds > 0f) ? $"{equippedWeapon.ReloadTimeSeconds:0.00} s" : "即时换架"), higherIsBetter: false);
			}
			else
			{
				DrawPartTextRow(view, ref y, "单发后坐冲量", "未标定");
				DrawPartTextRow(view, ref y, "弹匣容量", unavailable);
				DrawPartTextRow(view, ref y, "总弹药量", unavailable);
				DrawPartTextRow(view, ref y, "装填时间", unavailable);
			}
			if (configured && weapon.IsPointDefense)
			{
				DrawPartMetricGroup(view, ref y, "点防御火控");
				DrawPartTextRow(view, ref y, "工作模式", "自动拦截");
				DrawPartTextRow(view, ref y, "目标门限", (weapon.RequiresWeaponsQualityTrack && weapon.IncomingTargetsOnly) ? "武器级来袭导弹航迹" : "融合导弹航迹");
				DrawPartMetric(view, ref y, "最大拦截距离", weapon.PointDefenseMaximumRangeMeters, 10000f, FormatDistance(weapon.PointDefenseMaximumRangeMeters), Compared(equippedConfigured && equippedWeapon.IsPointDefense, equippedWeapon.PointDefenseMaximumRangeMeters), (equippedConfigured && equippedWeapon.IsPointDefense) ? FormatDistance(equippedWeapon.PointDefenseMaximumRangeMeters) : null);
				DrawPartMetric(view, ref y, "方位覆盖", weapon.PointDefenseFieldOfFireDegrees, 360f, $"{weapon.PointDefenseFieldOfFireDegrees:0}°", Compared(equippedConfigured && equippedWeapon.IsPointDefense, equippedWeapon.PointDefenseFieldOfFireDegrees), (equippedConfigured && equippedWeapon.IsPointDefense) ? $"{equippedWeapon.PointDefenseFieldOfFireDegrees:0}°" : null);
				DrawPartMetric(view, ref y, "跟踪转速", weapon.PointDefenseTraverseRateDegreesPerSecond, 360f, $"{weapon.PointDefenseTraverseRateDegreesPerSecond:0} °/s", Compared(equippedConfigured && equippedWeapon.IsPointDefense, equippedWeapon.PointDefenseTraverseRateDegreesPerSecond), (equippedConfigured && equippedWeapon.IsPointDefense) ? $"{equippedWeapon.PointDefenseTraverseRateDegreesPerSecond:0} °/s" : null);
				DrawPartTextRow(view, ref y, "开火门限", $"单发命中率 ≥ {weapon.PointDefenseMinimumHitProbability * 100f:0.#}% / TCPA ≤ {weapon.PointDefenseMaximumThreatTimeSeconds:0.#} s");
				DrawPartTextRow(view, ref y, "提前量 / 保持", $"{weapon.PointDefenseMaximumLeadSeconds:0.0} s / {weapon.PointDefenseLockHoldSeconds:0.00} s");
			}
			if (configured && weapon.UsesHeat)
			{
				DrawPartMetricGroup(view, ref y, "武器热管理");
				DrawPartTextRow(view, ref y, "单发热量", $"{weapon.HeatPerShotMj:0.00} MJ");
				DrawPartTextRow(view, ref y, "热容 / 过热", $"{weapon.HeatCapacityMj:0.0} / {weapon.OverheatThresholdMj:0.0} MJ");
				DrawPartTextRow(view, ref y, "冷却 / 恢复", $"{ToKilowatts(weapon.CoolingMw):0.#} kW / {weapon.ResumeThresholdMj:0.0} MJ");
				DrawPartTextRow(view, ref y, "转管启动", $"{weapon.SpinUpSeconds:0.00} s");
			}
			if (configured && weapon.HasProximityFuse)
			{
				DrawPartMetricGroup(view, ref y, "近炸与战斗部");
				DrawPartTextRow(view, ref y, "近炸模式", weapon.ProximityFuseMode);
				DrawPartMetric(view, ref y, "近炸触发半径", weapon.ProximityFuseTriggerRadiusMeters, 25f, $"{weapon.ProximityFuseTriggerRadiusMeters:0.0} m", Compared(equippedConfigured && equippedWeapon.HasProximityFuse, equippedWeapon.ProximityFuseTriggerRadiusMeters), (equippedConfigured && equippedWeapon.HasProximityFuse) ? $"{equippedWeapon.ProximityFuseTriggerRadiusMeters:0.0} m" : null);
				DrawPartMetric(view, ref y, "爆炸伤害范围", weapon.WarheadDamageRadiusMeters, 30f, $"{weapon.WarheadDamageRadiusMeters:0.0} m", Compared(equippedConfigured && equippedWeapon.HasProximityFuse, equippedWeapon.WarheadDamageRadiusMeters), (equippedConfigured && equippedWeapon.HasProximityFuse) ? $"{equippedWeapon.WarheadDamageRadiusMeters:0.0} m" : null);
				DrawPartMetric(view, ref y, "引信解保时间", weapon.ProximityFuseArmSeconds, 1f, $"{weapon.ProximityFuseArmSeconds:0.00} s", Compared(equippedConfigured && equippedWeapon.HasProximityFuse, equippedWeapon.ProximityFuseArmSeconds), (equippedConfigured && equippedWeapon.HasProximityFuse) ? $"{equippedWeapon.ProximityFuseArmSeconds:0.00} s" : null, higherIsBetter: false);
				DrawPartMetric(view, ref y, "引信安全距离", weapon.ProximityFuseSafeDistanceMeters, 100f, $"{weapon.ProximityFuseSafeDistanceMeters:0} m", Compared(equippedConfigured && equippedWeapon.HasProximityFuse, equippedWeapon.ProximityFuseSafeDistanceMeters), (equippedConfigured && equippedWeapon.HasProximityFuse) ? $"{equippedWeapon.ProximityFuseSafeDistanceMeters:0} m" : null, higherIsBetter: false);
			}
			else if (!configured)
			{
				DrawPartMetricGroup(view, ref y, "弹药特性");
				DrawPartTextRow(view, ref y, "近炸能力", unavailable);
			}
			MechPartStats stats = part.Stats;
			MechPartStats equippedStats = equippedPart?.Stats;
			DrawPartMetricGroup(view, ref y, "安装代价");
			DrawPartComparisonMetric(view, ref y, "质量", stats.massKg, equippedStats?.massKg, 16000f, "0", " kg", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "峰值功耗", ToKilowatts(stats.peakPowerDrawMw), ToKilowatts(equippedStats?.peakPowerDrawMw), ToKilowatts(30f), "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "发热功率", ToKilowatts(stats.heatGenerationMw), ToKilowatts(equippedStats?.heatGenerationMw), ToKilowatts(30f), "0.#", " kW", higherIsBetter: false);
			static float? Compared(bool available, float value)
			{
				if (!available)
				{
					return null;
				}
				return value;
			}
		}

		private void DrawHeadPartStats(Rect view, ref float y, MechPartDefinition part, MechPartDefinition equippedPart)
		{
			MechPartStats stats = part.Stats;
			MechPartStats equippedStats = equippedPart?.Stats;
			MechHeadSensorPerformance sensor = part.HeadSensorPerformance ?? new MechHeadSensorPerformance();
			MechHeadSensorPerformance equippedSensor = equippedPart?.HeadSensorPerformance;
			bool hasEquippedSensor = equippedSensor != null && equippedSensor.HasData;

			DrawPartMetricGroup(view, ref y, "结构");
			DrawPartComparisonMetric(view, ref y, "质量", stats.massKg, equippedStats?.massKg, 5000f, "0", " kg", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "装甲耐久", stats.armorPoints, equippedStats?.armorPoints, 1000f, "0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "结构耐久", stats.structurePoints, equippedStats?.structurePoints, 1000f, "0", string.Empty, higherIsBetter: true);

			DrawPartMetricGroup(view, ref y, "能源与热量");
			DrawPartComparisonMetric(view, ref y, "待机功耗", ToKilowatts(stats.idlePowerDrawMw), ToKilowatts(equippedStats?.idlePowerDrawMw), ToKilowatts(10f), "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "工作功耗", ToKilowatts(stats.peakPowerDrawMw), ToKilowatts(equippedStats?.peakPowerDrawMw), ToKilowatts(10f), "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "待机产热", ToKilowatts(stats.idleHeatMw), ToKilowatts(equippedStats?.idleHeatMw), ToKilowatts(5f), "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "工作产热", ToKilowatts(stats.activeHeatMw), ToKilowatts(equippedStats?.activeHeatMw), ToKilowatts(5f), "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "热容量", stats.heatCapacityMj, equippedStats?.heatCapacityMj, 100f, "0.0", " MJ", higherIsBetter: true);

			DrawPartMetricGroup(view, ref y, "光电与红外");
			DrawPartComparisonMetric(view, ref y, "光电指向能力", sensor.pointingCapability, hasEquippedSensor ? equippedSensor.pointingCapability : null, 100f, "0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "广角搜索能力", sensor.searchCapability, hasEquippedSensor ? equippedSensor.searchCapability : null, 100f, "0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "窄角跟踪能力", sensor.trackingCapability, hasEquippedSensor ? equippedSensor.trackingCapability : null, 100f, "0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "目标识别能力", sensor.identificationCapability, hasEquippedSensor ? equippedSensor.identificationCapability : null, 100f, "0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "最大放大倍率", sensor.maximumMagnification, hasEquippedSensor ? equippedSensor.maximumMagnification : null, 40f, "0.0", "x", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "热目标参考探测距离", sensor.referenceHotRangeKm, hasEquippedSensor ? equippedSensor.referenceHotRangeKm : null, 100f, "0.0", " km", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "冷目标参考探测距离", sensor.referenceColdRangeKm, hasEquippedSensor ? equippedSensor.referenceColdRangeKm : null, 100f, "0.0", " km", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "光电航迹容量", sensor.trackCapacity, hasEquippedSensor ? equippedSensor.trackCapacity : null, 64f, "0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "被动距离估计误差", sensor.referenceRangeErrorM, hasEquippedSensor ? equippedSensor.referenceRangeErrorM : null, 1000f, "0", " m", higherIsBetter: false);
			DrawPartTextRow(view, ref y, "光电工作波段", FormatElectroOpticalBands(sensor.supportedBands), hasEquippedSensor ? FormatElectroOpticalBands(equippedSensor.supportedBands) : null);

			DrawPartMetricGroup(view, ref y, "激光测距");
			DrawPartComparisonMetric(view, ref y, "有效距离", sensor.laserRangefinderRangeKm, hasEquippedSensor ? equippedSensor.laserRangefinderRangeKm : null, 50f, "0.0", " km", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "测距误差", sensor.laserRangefinderErrorM, hasEquippedSensor ? equippedSensor.laserRangefinderErrorM : null, 10f, "0.00", " m", higherIsBetter: false);
		}

		private void DrawBoosterPartStats(
			Rect view,
			ref float y,
			MechPartDefinition part,
			MechPartDefinition equippedPart)
		{
			MechPartStats stats = part.Stats;
			MechPartStats equippedStats = equippedPart?.Stats;
			MechBoosterPerformance booster = part.BoosterPerformance ?? new MechBoosterPerformance();
			MechBoosterPerformance equippedBooster = equippedPart?.BoosterPerformance;
			bool hasEquipped = equippedBooster != null && equippedBooster.HasData;

			DrawPartMetricGroup(view, ref y, "结构");
			DrawPartComparisonMetric(view, ref y, "质量", stats.massKg, equippedStats?.massKg, 7000f, "0", " kg", higherIsBetter: false);
			DrawPartMetricGroup(view, ref y, "能源与热量");
			DrawPartComparisonMetric(view, ref y, "待机功耗", ToKilowatts(stats.idlePowerDrawMw), ToKilowatts(equippedStats?.idlePowerDrawMw), 1000f, "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "常规工作功耗", ToKilowatts(stats.peakPowerDrawMw), ToKilowatts(equippedStats?.peakPowerDrawMw), 40000f, "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "AB 工作功耗", booster.assaultBoostPowerDrawKw, hasEquipped ? equippedBooster.assaultBoostPowerDrawKw : null, 70000f, "0", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "QB 工作功耗", booster.quickBoostPowerDrawKw, hasEquipped ? equippedBooster.quickBoostPowerDrawKw : null, 250000f, "0", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "待机产热", ToKilowatts(stats.idleHeatMw), ToKilowatts(equippedStats?.idleHeatMw), 1000f, "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "工作产热", ToKilowatts(stats.activeHeatMw), ToKilowatts(equippedStats?.activeHeatMw), 15000f, "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "热容量", stats.heatCapacityMj, equippedStats?.heatCapacityMj, 160f, "0.0", " MJ", higherIsBetter: true);

			DrawPartMetricGroup(view, ref y, "推进性能");
			DrawPartComparisonMetric(view, ref y, "主向推力", booster.mainThrustKn, hasEquipped ? equippedBooster.mainThrustKn : null, 1200f, "0", " kN", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "AB 附加推力", booster.assaultBoostThrustKn, hasEquipped ? equippedBooster.assaultBoostThrustKn : null, 6000f, "0", " kN", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "QB 推力", booster.quickBoostThrustKn, hasEquipped ? equippedBooster.quickBoostThrustKn : null, 24000f, "0", " kN", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "QB 持续时间", booster.quickBoostDurationS, hasEquipped ? equippedBooster.quickBoostDurationS : null, 0.6f, "0.00", " s", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "QB 再启动间隔", booster.quickBoostRecoveryS, hasEquipped ? equippedBooster.quickBoostRecoveryS : null, 1f, "0.00", " s", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "水平矢量范围", booster.horizontalVectorLimitDeg, hasEquipped ? equippedBooster.horizontalVectorLimitDeg : null, 60f, "0.0", "°", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "垂直矢量范围", booster.verticalVectorLimitDeg, hasEquipped ? equippedBooster.verticalVectorLimitDeg : null, 60f, "0.0", "°", higherIsBetter: true);
		}

		private void DrawGeneratorPartStats(
			Rect view,
			ref float y,
			MechPartDefinition part,
			MechPartDefinition equippedPart)
		{
			MechPartStats stats = part.Stats;
			MechPartStats equippedStats = equippedPart?.Stats;
			MechGeneratorPerformance generator = part.GeneratorPerformance ?? new MechGeneratorPerformance();
			MechGeneratorPerformance equippedGenerator = equippedPart?.GeneratorPerformance;
			bool hasEquipped = equippedGenerator != null && equippedGenerator.HasData;

			DrawPartMetricGroup(view, ref y, "结构");
			DrawPartComparisonMetric(view, ref y, "质量", stats.massKg, equippedStats?.massKg, 12000f, "0", " kg", higherIsBetter: false);
			DrawPartMetricGroup(view, ref y, "发电与储能");
			DrawPartComparisonMetric(view, ref y, "发电功率", ToKilowatts(stats.powerOutputMw), ToKilowatts(equippedStats?.powerOutputMw), 120000f, "0.#", " kW", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "储能容量", generator.energyStorageCapacityMj, hasEquipped ? equippedGenerator.energyStorageCapacityMj : null, 600f, "0.0", " MJ", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "回充功率", generator.energyRechargePowerKw, hasEquipped ? equippedGenerator.energyRechargePowerKw : null, 120000f, "0", " kW", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "回充延迟", generator.energyRechargeDelayS, hasEquipped ? equippedGenerator.energyRechargeDelayS : null, 3f, "0.00", " s", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "耗尽恢复阈值", generator.depletedRestartFraction * 100f, hasEquipped ? equippedGenerator.depletedRestartFraction * 100f : null, 100f, "0", "%", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "启动时间", generator.startupTimeS, hasEquipped ? equippedGenerator.startupTimeS : null, 15f, "0.0", " s", higherIsBetter: false);
			DrawPartMetricGroup(view, ref y, "热量");
			DrawPartComparisonMetric(view, ref y, "待机产热", ToKilowatts(stats.idleHeatMw), ToKilowatts(equippedStats?.idleHeatMw), 10000f, "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "工作产热", ToKilowatts(stats.activeHeatMw), ToKilowatts(equippedStats?.activeHeatMw), 30000f, "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "热容量", stats.heatCapacityMj, equippedStats?.heatCapacityMj, 300f, "0.0", " MJ", higherIsBetter: true);
		}

		private static string FormatElectroOpticalBands(MechElectroOpticalBand bands)
		{
			if (bands == MechElectroOpticalBand.None)
			{
				return "未配置";
			}
			List<string> labels = new List<string>();
			if ((bands & MechElectroOpticalBand.Visible) != 0)
			{
				labels.Add("可见光");
			}
			if ((bands & MechElectroOpticalBand.Mwir) != 0)
			{
				labels.Add("中波红外");
			}
			if ((bands & MechElectroOpticalBand.Lwir) != 0)
			{
				labels.Add("长波红外");
			}
			return string.Join(" / ", labels);
		}

		private void DrawArmPartStats(Rect view, ref float y, MechPartDefinition part, MechPartDefinition equippedPart)
		{
			MechPartStats stats = part.Stats;
			MechPartStats equippedStats = equippedPart?.Stats;
			MechArmPerformance arms = part.ArmPerformance ?? new MechArmPerformance();
			MechArmPerformance equippedArms = equippedPart?.ArmPerformance;
			bool hasEquippedArms = equippedArms != null && equippedArms.HasData;

			DrawPartMetricGroup(view, ref y, "结构");
			DrawPartComparisonMetric(view, ref y, "质量", stats.massKg, equippedStats?.massKg, 14000f, "0", " kg", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "装甲耐久", stats.armorPoints, equippedStats?.armorPoints, 1800f, "0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "结构耐久", stats.structurePoints, equippedStats?.structurePoints, 1600f, "0", string.Empty, higherIsBetter: true);

			DrawPartMetricGroup(view, ref y, "能源与热量");
			DrawPartComparisonMetric(view, ref y, "待机功耗", ToKilowatts(stats.idlePowerDrawMw), ToKilowatts(equippedStats?.idlePowerDrawMw), ToKilowatts(10f), "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "工作功耗", ToKilowatts(stats.peakPowerDrawMw), ToKilowatts(equippedStats?.peakPowerDrawMw), ToKilowatts(10f), "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "待机产热", ToKilowatts(stats.idleHeatMw), ToKilowatts(equippedStats?.idleHeatMw), ToKilowatts(5f), "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "工作产热", ToKilowatts(stats.activeHeatMw), ToKilowatts(equippedStats?.activeHeatMw), ToKilowatts(5f), "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "热容量", stats.heatCapacityMj, equippedStats?.heatCapacityMj, 150f, "0.0", " MJ", higherIsBetter: true);

			DrawPartMetricGroup(view, ref y, "武器操控");
			DrawPartComparisonMetric(view, ref y, "单臂武器承载", arms.weaponLoadLimitKg, hasEquippedArms ? equippedArms.weaponLoadLimitKg : null, 12000f, "0", " kg", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "后坐冲量上限", arms.recoilImpulseLimitNs, hasEquippedArms ? equippedArms.recoilImpulseLimitNs : null, 40000f, "0", " N·s", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "后坐恢复能力", arms.recoilRecoveryRateNsPerSecond, hasEquippedArms ? equippedArms.recoilRecoveryRateNsPerSecond : null, 150000f, "0", " N·s/s", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "瞄准角速度", arms.aimTraverseRateDegPerSecond, hasEquippedArms ? equippedArms.aimTraverseRateDegPerSecond : null, 180f, "0.0", " °/s", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "瞄准角加速度", arms.aimAngularAccelerationDegPerSecondSquared, hasEquippedArms ? equippedArms.aimAngularAccelerationDegPerSecondSquared : null, 800f, "0", " °/s²", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "武器指向误差", arms.weaponPointingErrorMrad, hasEquippedArms ? equippedArms.weaponPointingErrorMrad : null, 1f, "0.00", " mrad", higherIsBetter: false);

			DrawPartMetricGroup(view, ref y, "内置喷口");
			DrawPartComparisonMetric(view, ref y, "喷口数量", arms.InstalledNozzleCount, hasEquippedArms ? equippedArms.InstalledNozzleCount : null, 16f, "0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "横向推力贡献", arms.LateralThrustContributionKn, hasEquippedArms ? equippedArms.LateralThrustContributionKn : null, 200f, "0.0", " kN", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "RCS 推力贡献", arms.RcsThrustContributionKn, hasEquippedArms ? equippedArms.RcsThrustContributionKn : null, 800f, "0.0", " kN", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "喷口峰值功耗", SumArmNozzleValue(arms, heat: false), hasEquippedArms ? SumArmNozzleValue(equippedArms, heat: false) : null, 3000f, "0", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "喷口峰值产热", SumArmNozzleValue(arms, heat: true), hasEquippedArms ? SumArmNozzleValue(equippedArms, heat: true) : null, 1500f, "0", " kW", higherIsBetter: false);
		}

		private static float SumArmNozzleValue(MechArmPerformance arms, bool heat)
		{
			if (arms?.thrusterNozzles == null)
			{
				return 0f;
			}
			var total = 0f;
			for (var i = 0; i < arms.thrusterNozzles.Length; i++)
			{
				var nozzle = arms.thrusterNozzles[i];
				if (nozzle != null)
				{
					total += heat ? nozzle.maximumHeatKw : nozzle.maximumPowerKw;
				}
			}
			return total * 2f;
		}

		private void DrawCorePartStats(Rect view, ref float y, MechPartDefinition part, MechPartDefinition equippedPart)
		{
			MechCorePerformance core = part.CorePerformance ?? new MechCorePerformance();
			MechCorePerformance equippedCore = equippedPart?.CorePerformance;
			bool hasEquippedCore = equippedCore != null && equippedCore.HasData;

			MechPartStats effectiveStats = BuildCoreDisplayStats(part.Stats, core);
			MechPartStats equippedEffectiveStats = equippedPart == null
				? null
				: BuildCoreDisplayStats(equippedPart.Stats, equippedCore);
			DrawGeneralPartStats(view, ref y, effectiveStats, equippedEffectiveStats);

			DrawPartMetricGroup(view, ref y, "姿态控制");
			DrawPartComparisonMetric(view, ref y, "动量轮转向速度", core.momentumWheelTurnRateDegPerSecond, hasEquippedCore ? equippedCore.momentumWheelTurnRateDegPerSecond : null, 60f, "0.0", " °/s", higherIsBetter: true);
			DrawPartMetricGroup(view, ref y, "核心喷口");
			DrawPartComparisonMetric(view, ref y, "向前排气喷口", core.ForwardExhaustNozzleCount, hasEquippedCore ? equippedCore.ForwardExhaustNozzleCount : null, 12f, "0", " 个", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "反向推力贡献", core.ReverseThrustContributionKn, hasEquippedCore ? equippedCore.ReverseThrustContributionKn : null, 600f, "0.0", " kN", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "反向喷口功耗", core.ReversePowerKw, hasEquippedCore ? equippedCore.ReversePowerKw : null, 3000f, "0", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "向上排气喷口", core.UpwardExhaustNozzleCount, hasEquippedCore ? equippedCore.UpwardExhaustNozzleCount : null, 12f, "0", " 个", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "下降推力贡献", core.DownwardThrustContributionKn, hasEquippedCore ? equippedCore.DownwardThrustContributionKn : null, 900f, "0.0", " kN", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "下降喷口功耗", core.DownwardPowerKw, hasEquippedCore ? equippedCore.DownwardPowerKw : null, 3000f, "0", " kW", higherIsBetter: false);
		}

		private static MechPartStats BuildCoreDisplayStats(MechPartStats source, MechCorePerformance core)
		{
			var display = new MechPartStats();
			display.CopyFrom(source);
			if (core != null)
			{
				display.reverseThrustKn += core.ReverseThrustContributionKn;
				display.downwardThrustKn += core.DownwardThrustContributionKn;
			}
			return display;
		}

		private void DrawLegPartStats(Rect view, ref float y, MechPartDefinition part, MechPartDefinition equippedPart)
		{
			MechLegPerformance legs = part.LegPerformance ?? new MechLegPerformance();
			MechLegPerformance equippedLegs = equippedPart?.LegPerformance;
			bool hasEquippedLegs = equippedLegs != null && equippedLegs.HasData;

			MechPartStats effectiveStats = BuildLegDisplayStats(part.Stats, legs);
			MechPartStats equippedEffectiveStats = equippedPart == null
				? null
				: BuildLegDisplayStats(equippedPart.Stats, equippedLegs);
			DrawGeneralPartStats(view, ref y, effectiveStats, equippedEffectiveStats);

			DrawPartMetricGroup(view, ref y, "腿部喷口");
			DrawPartComparisonMetric(view, ref y, "主喷口数量", legs.PrimaryNozzleCount, hasEquippedLegs ? equippedLegs.PrimaryNozzleCount : null, 12f, "0", " 个", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "主向矢量角", legs.MaximumMainGimbalDeg, hasEquippedLegs ? equippedLegs.MaximumMainGimbalDeg : null, 30f, "0.0", "°", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "反向矢量角", legs.MaximumReverseGimbalDeg, hasEquippedLegs ? equippedLegs.MaximumReverseGimbalDeg : null, 30f, "0.0", "°", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "横向矢量角", legs.MaximumLateralGimbalDeg, hasEquippedLegs ? equippedLegs.MaximumLateralGimbalDeg : null, 10f, "0.0", "°", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "横向喷口数量", legs.LateralNozzleCount, hasEquippedLegs ? equippedLegs.LateralNozzleCount : null, 8f, "0", " 个", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "RCS 喷口数量", legs.RcsNozzleCount, hasEquippedLegs ? equippedLegs.RcsNozzleCount : null, 12f, "0", " 个", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "喷口峰值功耗", legs.MaximumPowerKw, hasEquippedLegs ? equippedLegs.MaximumPowerKw : null, 3000f, "0", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "喷口峰值产热", legs.MaximumHeatKw, hasEquippedLegs ? equippedLegs.MaximumHeatKw : null, 1500f, "0", " kW", higherIsBetter: false);
		}

		private static MechPartStats BuildLegDisplayStats(MechPartStats source, MechLegPerformance legs)
		{
			var display = new MechPartStats();
			display.CopyFrom(source);
			if (legs != null)
			{
				display.mainThrustKn += legs.MainThrustContributionKn;
				display.lateralThrustKn += legs.LateralThrustContributionKn;
				display.reverseThrustKn += legs.ReverseThrustContributionKn;
				display.upwardThrustKn += legs.UpwardThrustContributionKn;
				display.rcsThrustKn += legs.RcsThrustContributionKn;
				display.supportedMassKg += legs.maximumSupportedMassKg;
			}
			return display;
		}

		private void DrawGeneralPartStats(Rect view, ref float y, MechPartStats stats, MechPartStats equippedStats)
		{
			DrawPartMetricGroup(view, ref y, "结构");
			DrawPartComparisonMetric(view, ref y, "质量", stats.massKg, equippedStats?.massKg, 16000f, "0", " kg", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "装甲", stats.armorPoints, equippedStats?.armorPoints, 3000f, "0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "结构强度", stats.structurePoints, equippedStats?.structurePoints, 2600f, "0", string.Empty, higherIsBetter: true);
			DrawPartMetricGroup(view, ref y, "能源与热量");
			DrawPartComparisonMetric(view, ref y, "发电功率", ToKilowatts(stats.powerOutputMw), ToKilowatts(equippedStats?.powerOutputMw), ToKilowatts(100f), "0.#", " kW", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "待机功耗", ToKilowatts(stats.idlePowerDrawMw), ToKilowatts(equippedStats?.idlePowerDrawMw), ToKilowatts(30f), "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "峰值功耗", ToKilowatts(stats.peakPowerDrawMw), ToKilowatts(equippedStats?.peakPowerDrawMw), ToKilowatts(30f), "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "产热", ToKilowatts(stats.heatGenerationMw), ToKilowatts(equippedStats?.heatGenerationMw), ToKilowatts(30f), "0.#", " kW", higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "散热", ToKilowatts(stats.heatDissipationMw), ToKilowatts(equippedStats?.heatDissipationMw), ToKilowatts(30f), "0.#", " kW", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "热容量", stats.heatCapacityMj, equippedStats?.heatCapacityMj, 120f, "0.0", " MJ", higherIsBetter: true);
			DrawPartMetricGroup(view, ref y, "推进");
			DrawPartComparisonMetric(view, ref y, "主向推力", stats.mainThrustKn, equippedStats?.mainThrustKn, 1000f, "0", " kN", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "横向推力", stats.lateralThrustKn, equippedStats?.lateralThrustKn, 600f, "0", " kN", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "反向推力", stats.reverseThrustKn, equippedStats?.reverseThrustKn, 600f, "0", " kN", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "上升推力", stats.upwardThrustKn, equippedStats?.upwardThrustKn, 1800f, "0", " kN", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "下降推力", stats.downwardThrustKn, equippedStats?.downwardThrustKn, 900f, "0", " kN", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "RCS 推力", stats.rcsThrustKn, equippedStats?.rcsThrustKn, 1000f, "0", " kN", higherIsBetter: true);
			if (stats.supportedMassKg > 0.001f || (equippedStats?.supportedMassKg ?? 0f) > 0.001f)
			{
				DrawPartComparisonMetric(view, ref y, "整机承载上限", stats.supportedMassKg, equippedStats?.supportedMassKg, 100000f, "0", " kg", higherIsBetter: true);
			}
			DrawPartMetricGroup(view, ref y, "传感器与火控");
			DrawPartComparisonMetric(view, ref y, "雷达距离", stats.radarRangeKm, equippedStats?.radarRangeKm, 100f, "0.0", " km", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "雷达孔径", stats.radarAperture, equippedStats?.radarAperture, 10f, "0.00", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "雷达刷新率", stats.radarRefreshHz, equippedStats?.radarRefreshHz, 20f, "0.0", " Hz", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "传感器精度", stats.sensorPrecision, equippedStats?.sensorPrecision, 100f, "0.0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "航迹容量", stats.trackCapacity, equippedStats?.trackCapacity, 64f, "0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "火控质量", stats.fireControlQuality, equippedStats?.fireControlQuality, 100f, "0.0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "制导通道", stats.guidanceChannels, equippedStats?.guidanceChannels, 16f, "0", string.Empty, higherIsBetter: true);
			DrawPartMetricGroup(view, ref y, "武器适配与特征");
			DrawPartComparisonMetric(view, ref y, "后坐力控制", stats.weaponRecoilControl, equippedStats?.weaponRecoilControl, 100f, "0.0", string.Empty, higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "挂点承载", stats.hardpointCapacityKg, equippedStats?.hardpointCapacityKg, 30000f, "0", " kg", higherIsBetter: true);
			DrawPartComparisonMetric(view, ref y, "雷达截面积", stats.radarCrossSection, equippedStats?.radarCrossSection, 100f, "0.0", string.Empty, higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "热特征", stats.thermalSignature, equippedStats?.thermalSignature, 100f, "0.0", string.Empty, higherIsBetter: false);
			DrawPartComparisonMetric(view, ref y, "电磁特征", stats.electromagneticSignature, equippedStats?.electromagneticSignature, 100f, "0.0", string.Empty, higherIsBetter: false);
		}

		private void DrawAggregateStats(Rect rect)
		{
			RefreshSummary();
			MechAggregatedStats current = cachedStats ?? new MechAggregatedStats();
			MechAggregatedStats equipped = cachedEquippedStats ?? current;
			statsScroll = GUI.BeginScrollView(rect, statsScroll, new Rect(0f, 0f, rect.width - 18f, 1650f));
			Rect row = new Rect(0f, 0f, rect.width - 22f, AggregateMetricRowHeight);
			float y = 0f;
			if (selectingPart)
			{
				DrawAggregateComparisonHeader(row, ref y);
			}
			DrawGroup(ref y, row, "结构");
			DrawAggregateValueRow(row, ref y, "总质量", current.totalMassKg, equipped.totalMassKg, "0", " kg", higherIsBetter: false, 100000f);
			DrawAggregateValueRow(row, ref y, "装甲", current.armorPoints, equipped.armorPoints, "0", string.Empty, higherIsBetter: true, 10000f);
			DrawAggregateValueRow(row, ref y, "结构强度", current.structurePoints, equipped.structurePoints, "0", string.Empty, higherIsBetter: true, 8000f);
			DrawGroup(ref y, row, "能源与热量");
			DrawAggregateValueRow(row, ref y, "发电功率", ToKilowatts(current.powerOutputMw), ToKilowatts(equipped.powerOutputMw), "0.#", " kW", higherIsBetter: true, ToKilowatts(150f));
			DrawAggregateValueRow(row, ref y, "储能容量", current.energyStorageCapacityMj, equipped.energyStorageCapacityMj, "0.0", " MJ", higherIsBetter: true, 600f);
			DrawAggregateValueRow(row, ref y, "回充功率", current.energyRechargePowerKw, equipped.energyRechargePowerKw, "0", " kW", higherIsBetter: true, 120000f);
			DrawAggregateValueRow(row, ref y, "峰值余量", ToKilowatts(current.peakPowerSurplusMw), ToKilowatts(equipped.peakPowerSurplusMw), "0.#", " kW", higherIsBetter: true, ToKilowatts(100f), ToKilowatts(-50f));
			DrawAggregateValueRow(row, ref y, "散热余量", ToKilowatts(current.heatSurplusMw), ToKilowatts(equipped.heatSurplusMw), "0.#", " kW", higherIsBetter: true, ToKilowatts(100f), ToKilowatts(-50f));
			DrawGroup(ref y, row, "推进");
			DrawAggregateValueRow(row, ref y, "主向加速度", current.mainAccelerationMps2, equipped.mainAccelerationMps2, "0.00", " m/s²", higherIsBetter: true, 30f);
			DrawAggregateValueRow(row, ref y, "横向加速度", current.lateralAccelerationMps2, equipped.lateralAccelerationMps2, "0.00", " m/s²", higherIsBetter: true, 30f);
			DrawAggregateValueRow(row, ref y, "反向加速度", current.reverseAccelerationMps2, equipped.reverseAccelerationMps2, "0.00", " m/s²", higherIsBetter: true, 30f);
			DrawAggregateValueRow(row, ref y, "上向加速度", current.upwardAccelerationMps2, equipped.upwardAccelerationMps2, "0.00", " m/s²", higherIsBetter: true, 40f);
			DrawAggregateValueRow(row, ref y, "下向加速度", current.downwardAccelerationMps2, equipped.downwardAccelerationMps2, "0.00", " m/s²", higherIsBetter: true, 30f);
			DrawAggregateValueRow(row, ref y, "RCS 推力", current.rcsThrustKn, equipped.rcsThrustKn, "0", " kN", higherIsBetter: true, 1500f);
			DrawAggregateValueRow(row, ref y, "RCS 转向速度", current.rcsTurnRateDegreesPerSecond, equipped.rcsTurnRateDegreesPerSecond, "0.0", " °/s", higherIsBetter: true, 300f);
			DrawGroup(ref y, row, "雷达");
			DrawAggregateValueRow(row, ref y, "参考探测距离", current.radarPerformance.referenceDetectionRangeKm, equipped.radarPerformance.referenceDetectionRangeKm, "0.0", " km", higherIsBetter: true, 100f);
			DrawAggregateValueRow(row, ref y, "稳定跟踪距离", current.radarPerformance.stableTrackRangeKm, equipped.radarPerformance.stableTrackRangeKm, "0.0", " km", higherIsBetter: true, 100f);
			DrawAggregateValueRow(row, ref y, "航迹容量", current.radarPerformance.trackCapacity, equipped.radarPerformance.trackCapacity, "0", string.Empty, higherIsBetter: true, 32f);
			DrawAggregateValueRow(row, ref y, "锁定维持能力", current.radarPerformance.LockMaintenanceRating, equipped.radarPerformance.LockMaintenanceRating, "0.0", string.Empty, higherIsBetter: true, 100f);
			DrawGroup(ref y, row, "火控");
			DrawAggregateValueRow(row, ref y, "基础解算误差", current.fireControlPerformance.baseSolutionErrorMrad, equipped.fireControlPerformance.baseSolutionErrorMrad, "0.000", " mrad", higherIsBetter: false, 1f);
			DrawAggregateValueRow(row, ref y, "最大预测时间", current.fireControlPerformance.maximumPredictionTimeS, equipped.fireControlPerformance.maximumPredictionTimeS, "0.0", " s", higherIsBetter: true, 12f);
			DrawAggregateValueRow(row, ref y, "武器控制通道", current.fireControlPerformance.weaponControlChannels, equipped.fireControlPerformance.weaponControlChannels, "0", string.Empty, higherIsBetter: true, 16f);
			DrawAggregateValueRow(row, ref y, "制导通道", current.fireControlPerformance.guidanceChannelCapacity, equipped.fireControlPerformance.guidanceChannelCapacity, "0", string.Empty, higherIsBetter: true, 16f);
			y += 12f;
			GUI.Label(new Rect(0f, y, row.width, 54f), (cachedValidation == null || cachedValidation.IsValid) ? "装配检查：通过" : $"装配检查：{cachedValidation.ErrorCount} 个错误，{cachedValidation.WarningCount} 个警告", (cachedValidation == null || cachedValidation.IsValid) ? testedStyle : warningStyle);
			y += 64f;
			DrawCapacityBar(row, ref y, "承载负载", current.totalMassKg, current.supportedMassKg, "kg");
			DrawCapacityBar(row, ref y, "峰值能源负载", ToKilowatts(current.peakPowerDrawMw), ToKilowatts(current.powerOutputMw), "kW");
			DrawCapacityBar(row, ref y, "散热负载", ToKilowatts(current.heatGenerationMw), ToKilowatts(current.heatDissipationMw), "kW");
			DrawFireControlErrorChart(row, ref y, current, equipped);
			GUI.EndScrollView();
		}

		private void DrawFooter()
		{
			float y = (float)Screen.height - 64f + 10f;
			bool drawingPartSelection = selectingPart;
			if (drawingPartSelection)
			{
				if (GUI.Button(new Rect(32f, y, 190f, 42f), "返回装配总览", buttonStyle))
				{
					ClosePartSelection();
				}
				selection.TryGetValue(activeSlot, out var equippedId);
				MechPartDefinition pendingPart = catalog.Find(pendingPartId)?.LoadPart();
				bool referenceOnly = pendingPart != null && pendingPart.HasTag("reference-only");
				bool num = !string.IsNullOrWhiteSpace(pendingPartId) && pendingPart != null && !referenceOnly;
				bool alreadyEquipped = string.Equals(pendingPartId, equippedId, StringComparison.OrdinalIgnoreCase);
				GUI.enabled = num && !alreadyEquipped;
				if (GUI.Button(new Rect((float)Screen.width - 32f - 230f, y, 230f, 42f), referenceOnly ? "尚未接入仿真" : (alreadyEquipped ? "已装备" : "装备"), primaryButtonStyle))
				{
					selection[activeSlot] = pendingPartId;
					ClosePartSelection();
				}
				GUI.enabled = true;
			}
			else if (GUI.Button(new Rect(32f, y, 190f, 42f), "恢复测试机体", buttonStyle))
			{
				MechLoadoutSelectionStore.ResetToDefault();
				MechAppearanceSelectionStore.ResetToDefault();
				ReloadScene();
			}
			RefreshSummary();
			if (!drawingPartSelection)
			{
				GUI.enabled = cachedValidation == null || cachedValidation.IsValid;
				if (GUI.Button(new Rect((float)Screen.width - 32f - 230f, y, 230f, 42f), "应用并进入测试", primaryButtonStyle))
				{
					MechLoadoutSelectionStore.SaveSelection(selection);
					MechAppearanceSelectionStore.Save(appearance);
					ReloadScene();
				}
				GUI.enabled = true;
			}
			GUI.Label(new Rect((float)Screen.width * 0.5f - 220f, y + 8f, 440f, 26f), drawingPartSelection ? ("候选配置 / " + SlotName(activeSlot)) : "LOCAL TEST LOADOUT", overlineStyle);
		}

		private void RefreshSummary()
		{
			if (summaryDirty)
			{
				summaryDirty = false;
				if (previewLoadout != null)
				{
					UnityEngine.Object.Destroy(previewLoadout);
				}
				Dictionary<MechPartSlot, string> effectiveSelection = new Dictionary<MechPartSlot, string>(selection);
				if (selectingPart && !string.IsNullOrWhiteSpace(pendingPartId))
				{
					effectiveSelection[activeSlot] = pendingPartId;
				}
				previewLoadout = CreateRuntimeLoadout(effectiveSelection, "hangar_preview", "机库预览");
				cachedStats = MechStatsCalculator.Calculate(previewLoadout);
				cachedValidation = MechLoadoutValidator.Validate(previewLoadout, new MechValidationOptions());
				MechLoadoutDefinition equippedLoadout = CreateRuntimeLoadout(selection, "hangar_equipped", "当前装备");
				cachedEquippedStats = MechStatsCalculator.Calculate(equippedLoadout);
				UnityEngine.Object.Destroy(equippedLoadout);
				previewDirty = true;
			}
		}

		private MechLoadoutDefinition CreateRuntimeLoadout(IReadOnlyDictionary<MechPartSlot, string> source, string loadoutId, string displayName)
		{
			List<MechPartAssignment> assignments = new List<MechPartAssignment>();
			foreach (KeyValuePair<MechPartSlot, string> pair in source)
			{
				MechPartDefinition part = catalog.Find(pair.Value)?.LoadPart();
				if (part != null)
				{
					assignments.Add(new MechPartAssignment
					{
						slot = pair.Key,
						part = part,
						enabled = true
					});
				}
			}
			MechLoadoutDefinition mechLoadoutDefinition = ScriptableObject.CreateInstance<MechLoadoutDefinition>();
			mechLoadoutDefinition.hideFlags = HideFlags.DontSave;
			mechLoadoutDefinition.ConfigureRuntime(loadoutId, displayName, assignments);
			return mechLoadoutDefinition;
		}

		private void CopySelection(Dictionary<MechPartSlot, string> source)
		{
			selection.Clear();
			pendingPartId = null;
			foreach (KeyValuePair<MechPartSlot, string> pair in source)
			{
				selection[pair.Key] = pair.Value;
			}
			summaryDirty = true;
			previewDirty = true;
		}

		private void ReloadScene()
		{
			RestoreGameplayState(restoreStalePause: true);
			visible = false;
			IsAnyMenuVisible = false;
			Scene scene = SceneManager.GetActiveScene();
			SceneManager.LoadScene((scene.buildIndex >= 0) ? scene.buildIndex : 0);
		}

		private void OpenPartSelection(MechPartSlot slot)
		{
			activeSlot = slot;
			selectingPart = true;
			editingAppearance = false;
			selection.TryGetValue(slot, out pendingPartId);
			search = string.Empty;
			partScroll = Vector2.zero;
			selectedStatsScroll = Vector2.zero;
			summaryDirty = true;
			previewDirty = true;
		}

		private void ClosePartSelection()
		{
			selectingPart = false;
			pendingPartId = null;
			search = string.Empty;
			summaryDirty = true;
			previewDirty = true;
		}

		private void DrawSectionFrame(Rect rect, string title)
		{
			GUI.DrawTexture(rect, sectionTexture);
			GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 50f), sectionHeaderTexture);
			GUI.Label(new Rect(rect.x + 18f, rect.y + 8f, rect.width - 36f, 34f), title, headingStyle);
			GUI.DrawTexture(new Rect(rect.x, rect.y + 49f, rect.width, 1f), lineTexture);
			DrawOutline(rect);
		}

		private void DrawPreview(Rect rect, bool selectedPartOnly)
		{
			RefreshSummary();
			if (previewRenderer == null)
			{
				return;
			}
			if (previewDirty)
			{
				previewRenderer.SetAppearance(appearance);
				if (selectedPartOnly)
				{
					previewRenderer.ShowPart(catalog.Find(pendingPartId)?.LoadPart());
				}
				else
				{
					previewRenderer.ShowLoadout(previewLoadout);
				}
				previewDirty = false;
			}
			previewRenderer.Draw(rect);
			DrawOutline(rect);
			GUI.Label(new Rect(rect.x + 14f, rect.y + 12f, rect.width - 28f, 28f), selectedPartOnly ? (SlotShortName(activeSlot) + " / VISUAL PREVIEW") : "ASSEMBLED UNIT / VISUAL PREVIEW", overlineStyle);
		}

		private void DrawGroup(ref float y, Rect row, string title)
		{
			y += 8f;
			GUI.Label(new Rect(row.x, y, row.width, 30f), title, headingStyle);
			GUI.DrawTexture(new Rect(row.x, y + 29f, row.width, 1f), frameTexture);
			y += 36f;
		}

		private void DrawPartMetricGroup(Rect area, ref float y, string title)
		{
			y += 10f;
			GUI.Label(new Rect(area.x + 4f, y, area.width - 4f, 30f), title, headingStyle);
			GUI.DrawTexture(new Rect(area.x, y + 29f, area.width - 4f, 1f), frameTexture);
			y += 38f;
		}

		private void DrawPartTextRow(Rect area, ref float y, string label, string value, string equippedValue = null)
		{
			Rect row = new Rect(area.x, y, area.width - 4f, PartMetricRowHeight);
			GUI.DrawTexture(row, ((Mathf.FloorToInt(y / PartMetricRowStride) & 1) == 0) ? rowTexture : alternateRowTexture);
			if (selectingPart && equippedValue != null)
			{
				GetComparisonRowRects(row, out var labelRect, out var track, out var equippedRect, out var arrowRect, out var candidateRect);
				bool changed = !string.Equals(value, equippedValue, StringComparison.Ordinal);
				if (changed)
				{
					DrawColorSwatch(new Rect(row.x, row.y, 3f, row.height), ComparisonImprovementColor);
				}
				GUI.Label(labelRect, label, labelStyle);
				GUI.DrawTexture(track, sliderTrackTexture);
				if (changed)
				{
					DrawColorSwatch(new Rect(track.x, track.y, track.width, track.height), new Color(ComparisonImprovementColor.r, ComparisonImprovementColor.g, ComparisonImprovementColor.b, 0.72f));
				}
				GUI.Label(equippedRect, equippedValue, comparisonBaselineStyle);
				DrawComparisonFlowArrow(equippedRect, candidateRect);
				DrawComparisonArrow(arrowRect, string.Empty, changed, better: true);
				GUI.Label(candidateRect, value, changed ? comparisonPositiveStyle : valueStyle);
			}
			else
			{
				GUI.Label(new Rect(row.x + 12f, row.y, row.width * 0.46f - 12f, row.height), label, labelStyle);
				GUI.Label(new Rect(row.x + row.width * 0.46f, row.y, row.width * 0.54f - 10f, row.height), value, valueStyle);
			}
			y += PartMetricRowStride;
		}

		private void DrawPartMetric(Rect area, ref float y, string label, float value, float displayMaximum, string valueText, float? equippedValue = null, string equippedValueText = null, bool higherIsBetter = true, float displayMinimum = 0f)
		{
			Rect row = new Rect(area.x, y, area.width - 4f, PartMetricRowHeight);
			GUI.DrawTexture(row, ((Mathf.FloorToInt(y / PartMetricRowStride) & 1) == 0) ? rowTexture : alternateRowTexture);
			if (selectingPart && equippedValue.HasValue)
			{
				GetComparisonRowRects(row, out var labelRect, out var track, out var equippedRect, out var arrowRect, out var candidateRect);
				float baseline = equippedValue.Value;
				float delta = value - baseline;
				bool changed = HasMeaningfulDifference(value, baseline);
				bool better = (higherIsBetter ? (delta > 0f) : (delta < 0f));
				if (changed)
				{
					DrawColorSwatch(new Rect(row.x, row.y, 3f, row.height), better ? ComparisonImprovementColor : ComparisonRegressionColor);
				}
				GUI.Label(labelRect, label, labelStyle);
				DrawComparisonTrack(track, value, baseline, displayMinimum, displayMaximum, changed, better);
				GUI.Label(equippedRect, equippedValueText ?? baseline.ToString("0.##"), comparisonBaselineStyle);
				DrawComparisonFlowArrow(equippedRect, candidateRect);
				DrawComparisonArrow(arrowRect, (!changed) ? string.Empty : ((delta > 0f) ? "▲" : "▼"), changed, better);
				GUI.Label(candidateRect, valueText, (!changed) ? valueStyle : (better ? comparisonPositiveStyle : comparisonNegativeStyle));
			}
			else
			{
				float labelWidth = row.width * 0.21f;
				float valueWidth = Mathf.Clamp(row.width * 0.19f, 110f, 210f);
				float availableTrackWidth = Mathf.Max(72f, row.width - labelWidth - valueWidth - 24f);
				Rect track2 = new Rect(row.x + labelWidth, row.y + row.height * 0.5f - PartMetricTrackHeight * 0.5f, availableTrackWidth * 0.7f, PartMetricTrackHeight);
				GUI.Label(new Rect(row.x + 12f, row.y, labelWidth - 16f, row.height), label, labelStyle);
				GUI.DrawTexture(track2, sliderTrackTexture);
				Rect fill = track2;
				fill.width *= Mathf.Clamp01(Mathf.InverseLerp(displayMinimum, displayMaximum, value));
				if (fill.width > 0.5f)
				{
					GUI.DrawTexture(fill, metricFillTexture);
				}
				GUI.DrawTexture(new Rect(track2.x, track2.y - 3f, 2f, 14f), metricFillTexture);
				GUI.DrawTexture(new Rect(track2.xMax - 1f, track2.y - 3f, 2f, 14f), frameTexture);
				GUI.Label(new Rect(row.xMax - valueWidth, row.y, valueWidth - 8f, row.height), valueText, valueStyle);
			}
			y += PartMetricRowStride;
		}

		private void DrawPartComparisonMetric(Rect area, ref float y, string label, float value, float? equippedValue, float displayMaximum, string format, string suffix, bool higherIsBetter, float displayMinimum = 0f)
		{
			DrawPartMetric(area, ref y, label, value, displayMaximum, value.ToString(format) + suffix, equippedValue, equippedValue.HasValue ? (equippedValue.Value.ToString(format) + suffix) : null, higherIsBetter, displayMinimum);
		}

		private void DrawComparisonColumnHeader(Rect area, ref float y)
		{
			Rect rect = new Rect(area.x, y, area.width - 4f, 28f);
			GUI.DrawTexture(rect, sectionHeaderTexture);
			GetComparisonRowRects(rect, out var labelRect, out var trackRect, out var equippedRect, out var arrowRect, out var candidateRect);
			GUI.Label(labelRect, "性能比较", overlineStyle);
			GUI.Label(trackRect, "性能比例", overlineStyle);
			GUI.Label(equippedRect, "当前", comparisonBaselineStyle);
			DrawComparisonFlowArrow(equippedRect, candidateRect);
			GUI.Label(candidateRect, "候选", valueStyle);
			y += 36f;
		}

		private static void GetComparisonRowRects(Rect row, out Rect labelRect, out Rect trackRect, out Rect equippedRect, out Rect arrowRect, out Rect candidateRect)
		{
			if (row.width < 700f)
			{
				float compactLabelWidth = Mathf.Clamp(row.width * 0.3f, 112f, 150f);
				float compactTrackWidth = Mathf.Clamp(row.width * 0.18f, 56f, 84f);
				float compactEquippedWidth = Mathf.Clamp(row.width * 0.18f, 68f, 92f);
				float compactArrowWidth = 22f;
				labelRect = new Rect(row.x + 12f, row.y, compactLabelWidth - 16f, row.height);
				float compactTrackStart = row.x + compactLabelWidth + 4f;
				trackRect = new Rect(compactTrackStart, row.y + row.height * 0.5f - PartMetricTrackHeight * 0.5f, compactTrackWidth * 0.7f, PartMetricTrackHeight);
				equippedRect = new Rect(compactTrackStart + compactTrackWidth + 6f, row.y, compactEquippedWidth, row.height);
				candidateRect = new Rect(equippedRect.xMax + 20f, row.y, Mathf.Max(56f, row.xMax - equippedRect.xMax - compactArrowWidth - 32f), row.height);
				arrowRect = new Rect(candidateRect.xMax + 2f, row.y, compactArrowWidth, row.height);
				return;
			}

			float labelWidth = Mathf.Clamp(row.width * 0.19f, 132f, 210f);
			float equippedWidth = Mathf.Clamp(row.width * 0.14f, 92f, 156f);
			float candidateWidth = Mathf.Clamp(row.width * 0.18f, 118f, 210f);
			float arrowWidth = 30f;
			float rightPadding = 8f;
			float trackStart = row.x + labelWidth + 8f;
			float trackEnd = row.xMax - rightPadding - arrowWidth - candidateWidth - equippedWidth - 42f;
			labelRect = new Rect(row.x + 12f, row.y, labelWidth - 16f, row.height);
			float availableTrackWidth = Mathf.Max(96f, trackEnd - trackStart);
			trackRect = new Rect(trackStart, row.y + row.height * 0.5f - PartMetricTrackHeight * 0.5f, availableTrackWidth * 0.7f, PartMetricTrackHeight);
			equippedRect = new Rect(trackStart + availableTrackWidth + 14f, row.y, equippedWidth, row.height);
			candidateRect = new Rect(equippedRect.xMax + 28f, row.y, candidateWidth, row.height);
			arrowRect = new Rect(candidateRect.xMax + 2f, row.y, arrowWidth, row.height);
		}

		private void DrawComparisonFlowArrow(Rect equippedRect, Rect candidateRect)
		{
			Rect flowRect = new Rect(
				equippedRect.xMax,
				equippedRect.y,
				Mathf.Max(12f, candidateRect.x - equippedRect.xMax),
				equippedRect.height);
			GUI.Label(flowRect, ">", comparisonArrowStyle);
		}

		private void DrawComparisonTrack(Rect track, float candidate, float equipped, float displayMinimum, float displayMaximum, bool changed, bool better)
		{
			if (displayMaximum <= displayMinimum)
			{
				displayMaximum = displayMinimum + 1f;
			}
			GUI.DrawTexture(track, sliderTrackTexture);
			float equippedRatio = Mathf.InverseLerp(displayMinimum, displayMaximum, equipped);
			float candidateRatio = Mathf.InverseLerp(displayMinimum, displayMaximum, candidate);
			float a = Mathf.InverseLerp(displayMinimum, displayMaximum, 0f);
			float baselineStart = Mathf.Min(a, equippedRatio);
			float baselineEnd = Mathf.Max(a, equippedRatio);
			if (baselineEnd - baselineStart > 0.001f)
			{
				DrawColorSwatch(new Rect(track.x + baselineStart * track.width, track.y, (baselineEnd - baselineStart) * track.width, track.height), new Color(ComparisonBaselineColor.r, ComparisonBaselineColor.g, ComparisonBaselineColor.b, 0.72f));
			}
			if (changed)
			{
				float differenceStart = Mathf.Min(equippedRatio, candidateRatio);
				float differenceWidth = Mathf.Max(2f, Mathf.Abs(candidateRatio - equippedRatio) * track.width);
				DrawColorSwatch(new Rect(Mathf.Clamp(track.x + differenceStart * track.width, track.x, Mathf.Max(track.x, track.xMax - differenceWidth)), track.y, differenceWidth, track.height), better ? ComparisonImprovementColor : ComparisonRegressionColor);
			}
			DrawColorSwatch(new Rect(Mathf.Lerp(track.x + 1f, track.xMax - 1f, equippedRatio) - 1f, track.y - 4f, 2f, 16f), ComparisonBaselineColor);
			DrawColorSwatch(new Rect(Mathf.Lerp(track.x + 1.5f, track.xMax - 1.5f, candidateRatio) - 1.5f, track.y - 5f, 3f, 18f), (!changed) ? new Color(0.755f, 0.85f, 0.9f, 1f) : (better ? ComparisonImprovementColor : ComparisonRegressionColor));
		}

		private void DrawComparisonArrow(Rect rect, string symbol, bool changed, bool better)
		{
			Color contentColor = GUI.contentColor;
			GUI.contentColor = ((!changed) ? ComparisonBaselineColor : (better ? ComparisonImprovementColor : ComparisonRegressionColor));
			GUI.Label(rect, symbol, comparisonArrowStyle);
			GUI.contentColor = contentColor;
		}

		private static bool HasMeaningfulDifference(float first, float second)
		{
			float scale = Mathf.Max(1f, Mathf.Abs(first), Mathf.Abs(second));
			return Mathf.Abs(first - second) > Mathf.Max(0.0001f, scale * 1E-05f);
		}

		private static float ToKilowatts(float megawatts)
		{
			return megawatts * KilowattsPerMegawatt;
		}

		private static float? ToKilowatts(float? megawatts)
		{
			return megawatts.HasValue ? ToKilowatts(megawatts.Value) : null;
		}

		private static string FormatDistance(float meters)
		{
			if (!(meters >= 1000f))
			{
				return $"{meters:0} m";
			}
			return $"{meters / 1000f:0.0} km";
		}

		private void DrawAggregateValueRow(Rect area, ref float y, string label, float candidate, float equipped, string format, string suffix, bool higherIsBetter, float displayMaximum, float displayMinimum = 0f)
		{
			Rect row = new Rect(area.x, y, area.width, AggregateMetricRowHeight);
			GUI.DrawTexture(row, ((Mathf.FloorToInt(y / AggregateMetricRowStride) & 1) == 0) ? rowTexture : alternateRowTexture);
			if (selectingPart)
			{
				GetAggregateComparisonRects(row, out var labelRect, out var equippedRect, out var flowRect, out var candidateRect, out var arrowRect);
				float delta = candidate - equipped;
				bool changed = HasMeaningfulDifference(candidate, equipped);
				bool better = (higherIsBetter ? (delta > 0f) : (delta < 0f));
				if (changed)
				{
					DrawColorSwatch(new Rect(row.x, row.y, 3f, row.height), better ? ComparisonImprovementColor : ComparisonRegressionColor);
				}
				GUI.Label(labelRect, label, aggregateLabelStyle);
				GUI.Label(equippedRect, equipped.ToString(format), comparisonBaselineStyle);
				GUI.Label(flowRect, ">", comparisonArrowStyle);
				DrawComparisonArrow(arrowRect, (!changed) ? string.Empty : ((delta > 0f) ? "▲" : "▼"), changed, better);
				GUI.Label(candidateRect, candidate.ToString(format) + suffix, (!changed) ? aggregateValueStyle : (better ? comparisonPositiveStyle : comparisonNegativeStyle));
			}
			else
			{
				GUI.Label(new Rect(row.x + 12f, row.y, row.width * 0.54f - 12f, row.height), label, aggregateLabelStyle);
				GUI.Label(new Rect(row.x + row.width * 0.54f, row.y, row.width * 0.46f - 8f, row.height), candidate.ToString(format) + suffix, aggregateValueStyle);
			}
			y += AggregateMetricRowStride;
		}

		private void DrawAggregateComparisonHeader(Rect area, ref float y)
		{
			Rect row = new Rect(area.x, y, area.width, 30f);
			GUI.DrawTexture(row, sectionHeaderTexture);
			GetAggregateComparisonRects(row, out var labelRect, out var equippedRect, out var flowRect, out var candidateRect, out var arrowRect);
			GUI.Label(labelRect, "性能比较", overlineStyle);
			GUI.Label(equippedRect, "当前", comparisonBaselineStyle);
			GUI.Label(flowRect, ">", comparisonArrowStyle);
			GUI.Label(candidateRect, "候选", aggregateValueStyle);
			y += 36f;
		}

		private static void GetAggregateComparisonRects(
			Rect row,
			out Rect labelRect,
			out Rect equippedRect,
			out Rect flowRect,
			out Rect candidateRect,
			out Rect arrowRect)
		{
			float labelWidth = row.width * 0.4f;
			float equippedWidth = row.width * 0.23f;
			float flowWidth = 20f;
			float arrowWidth = 24f;
			labelRect = new Rect(row.x + 12f, row.y, labelWidth - 16f, row.height);
			equippedRect = new Rect(row.x + labelWidth, row.y, equippedWidth, row.height);
			flowRect = new Rect(equippedRect.xMax, row.y, flowWidth, row.height);
			candidateRect = new Rect(flowRect.xMax, row.y, Mathf.Max(64f, row.xMax - flowRect.xMax - arrowWidth - 6f), row.height);
			arrowRect = new Rect(candidateRect.xMax, row.y, arrowWidth, row.height);
		}

		private void DrawCapacityBar(Rect area, ref float y, string label, float current, float capacity, string unit)
		{
			float ratio = ((capacity > 0.001f) ? (current / capacity) : 0f);
			GUI.Label(new Rect(area.x, y, area.width * 0.56f, 24f), label, labelStyle);
			GUI.Label(new Rect(area.x + area.width * 0.56f, y, area.width * 0.44f, 24f), (capacity > 0.001f) ? $"{current:0.#} / {capacity:0.#} {unit}" : "--", (ratio > 1f) ? comparisonNegativeStyle : valueStyle);
			y += 27f;
			Rect track = new Rect(area.x, y, area.width, 7f);
			GUI.DrawTexture(track, sliderTrackTexture);
			Rect fill = track;
			fill.width *= Mathf.Clamp01(ratio);
			if (fill.width > 0.5f)
			{
				if (ratio > 1f)
				{
					Color color = GUI.color;
					GUI.color = new Color(0.92f, 0.52f, 0.24f, 1f);
					GUI.DrawTexture(fill, Texture2D.whiteTexture);
					GUI.color = color;
				}
				else
				{
					GUI.DrawTexture(fill, metricFillTexture);
				}
			}
			GUI.DrawTexture(new Rect(track.x + track.width * 0.8f, track.y - 3f, 1f, 13f), frameTexture);
			y += 25f;
		}

		private void DrawFireControlErrorChart(
			Rect area,
			ref float y,
			MechAggregatedStats candidate,
			MechAggregatedStats equipped)
		{
			y += 10f;
			GUI.Label(new Rect(area.x, y, area.width, 28f), "标称火控预测误差", headingStyle);
			y += 31f;

			var candidateProfile = MechFireControlPerformanceModel.FromStats(candidate);
			var equippedProfile = MechFireControlPerformanceModel.FromStats(equipped);
			float maximumRangeMeters = Mathf.Max(
				1000f,
				candidateProfile.RadarMaximumRangeMeters,
				equippedProfile.RadarMaximumRangeMeters);
			float maximumErrorMeters = Mathf.Max(
				MechFireControlPerformanceModel.EstimatePredictionErrorMeters(
					candidateProfile,
					candidateProfile.RadarMaximumRangeMeters,
					MechFireControlTrackMode.TrackWhileScan),
				MechFireControlPerformanceModel.EstimatePredictionErrorMeters(
					equippedProfile,
					equippedProfile.RadarMaximumRangeMeters,
					MechFireControlTrackMode.TrackWhileScan));
			maximumErrorMeters = NiceGraphMaximum(maximumErrorMeters * 1.08f);

			Rect chart = new Rect(area.x, y, area.width, 238f);
			DrawColorSwatch(chart, new Color(0.025f, 0.055f, 0.075f, 0.92f));
			DrawOutline(chart);
			GUI.Label(
				new Rect(chart.x + 10f, chart.y + 5f, chart.width - 20f, 20f),
				"1σ / 1秒预测",
				overlineStyle);

			Color candidateColor = new Color(0.36f, 0.7f, 1f, 1f);
			Color equippedColor = new Color(0.82f, 0.88f, 0.92f, 0.88f);
			float legendY = chart.y + 26f;
			if (selectingPart)
			{
				DrawColorSwatch(new Rect(chart.x + 12f, legendY + 7f, 18f, 3f), equippedColor);
				GUI.Label(new Rect(chart.x + 35f, legendY, 92f, 20f), "当前装备", mutedStyle);
				DrawColorSwatch(new Rect(chart.x + 124f, legendY + 7f, 18f, 3f), candidateColor);
				GUI.Label(new Rect(chart.x + 147f, legendY, 72f, 20f), "候选", mutedStyle);
			}
			else
			{
				DrawColorSwatch(new Rect(chart.x + 12f, legendY + 7f, 18f, 3f), candidateColor);
				GUI.Label(new Rect(chart.x + 35f, legendY, 92f, 20f), "当前机体", mutedStyle);
			}
			GUI.Label(
				new Rect(chart.xMax - 176f, legendY, 166f, 20f),
				"实线 STT  ·  虚线 TWS",
				mutedStyle);

			Rect plot = new Rect(chart.x + 48f, chart.y + 58f, chart.width - 64f, 142f);
			DrawFireControlGraphGrid(plot, maximumRangeMeters, maximumErrorMeters);
			if (selectingPart)
			{
				DrawFireControlCurve(
					plot,
					equippedProfile,
					MechFireControlTrackMode.SingleTargetTrack,
					maximumRangeMeters,
					maximumErrorMeters,
					equippedColor,
					dashed: false);
				DrawFireControlCurve(
					plot,
					equippedProfile,
					MechFireControlTrackMode.TrackWhileScan,
					maximumRangeMeters,
					maximumErrorMeters,
					new Color(equippedColor.r, equippedColor.g, equippedColor.b, 0.58f),
					dashed: true);
			}
			DrawFireControlCurve(
				plot,
				candidateProfile,
				MechFireControlTrackMode.SingleTargetTrack,
				maximumRangeMeters,
				maximumErrorMeters,
				candidateColor,
				dashed: false);
			DrawFireControlCurve(
				plot,
				candidateProfile,
				MechFireControlTrackMode.TrackWhileScan,
				maximumRangeMeters,
				maximumErrorMeters,
				new Color(candidateColor.r, candidateColor.g, candidateColor.b, 0.62f),
				dashed: true);

			y += chart.height + 16f;
		}

		private void DrawFireControlGraphGrid(
			Rect plot,
			float maximumRangeMeters,
			float maximumErrorMeters)
		{
			Color gridColor = new Color(0.26f, 0.42f, 0.5f, 0.34f);
			for (int i = 0; i <= 4; i++)
			{
				float ratio = i / 4f;
				float x = Mathf.Lerp(plot.x, plot.xMax, ratio);
				float y = Mathf.Lerp(plot.yMax, plot.y, ratio);
				DrawColorSwatch(new Rect(x, plot.y, 1f, plot.height), gridColor);
				DrawColorSwatch(new Rect(plot.x, y, plot.width, 1f), gridColor);
			}

			GUI.Label(new Rect(plot.x - 46f, plot.y - 8f, 42f, 20f), $"{maximumErrorMeters:0.#} m", mutedStyle);
			GUI.Label(new Rect(plot.x - 46f, plot.center.y - 9f, 42f, 20f), $"{maximumErrorMeters * 0.5f:0.#}", mutedStyle);
			GUI.Label(new Rect(plot.x - 46f, plot.yMax - 12f, 42f, 20f), "0", mutedStyle);
			GUI.Label(new Rect(plot.x - 8f, plot.yMax + 2f, 46f, 20f), "0", mutedStyle);
			GUI.Label(
				new Rect(plot.center.x - 32f, plot.yMax + 2f, 64f, 20f),
				$"{maximumRangeMeters * 0.0005f:0.#} km",
				mutedStyle);
			GUI.Label(
				new Rect(plot.xMax - 68f, plot.yMax + 2f, 68f, 20f),
				$"{maximumRangeMeters * 0.001f:0.#} km",
				mutedStyle);
		}

		private static void DrawFireControlCurve(
			Rect plot,
			in MechFireControlPerformanceProfile profile,
			MechFireControlTrackMode mode,
			float maximumRangeMeters,
			float maximumErrorMeters,
			Color color,
			bool dashed)
		{
			const int segmentCount = 28;
			float curveMaximumRangeMeters = Mathf.Min(
				maximumRangeMeters,
				profile.RadarMaximumRangeMeters);
			float initialErrorMeters = MechFireControlPerformanceModel.EstimatePredictionErrorMeters(
				profile,
				0f,
				mode);
			Vector2 previous = new Vector2(
				plot.x,
				Mathf.Lerp(
					plot.yMax,
					plot.y,
					Mathf.Clamp01(initialErrorMeters / maximumErrorMeters)));
			for (int i = 1; i <= segmentCount; i++)
			{
				float curveRatio = i / (float)segmentCount;
				float rangeMeters = curveMaximumRangeMeters * curveRatio;
				float plotRatio = rangeMeters / maximumRangeMeters;
				float errorMeters = MechFireControlPerformanceModel.EstimatePredictionErrorMeters(
					profile,
					rangeMeters,
					mode);
				Vector2 current = new Vector2(
					Mathf.Lerp(plot.x, plot.xMax, plotRatio),
					Mathf.Lerp(plot.yMax, plot.y, Mathf.Clamp01(errorMeters / maximumErrorMeters)));
				if (!dashed || (i & 1) == 0)
				{
					DrawGraphLine(previous, current, color, dashed ? 1.5f : 2.5f);
				}
				previous = current;
			}
		}

		private static void DrawGraphLine(Vector2 start, Vector2 end, Color color, float width)
		{
			Vector2 delta = end - start;
			float length = delta.magnitude;
			if (length <= 0.01f)
			{
				return;
			}

			Matrix4x4 previousMatrix = GUI.matrix;
			Color previousColor = GUI.color;
			GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, start);
			GUI.color = color;
			GUI.DrawTexture(new Rect(start.x, start.y - width * 0.5f, length, width), Texture2D.whiteTexture);
			GUI.color = previousColor;
			GUI.matrix = previousMatrix;
		}

		private static float NiceGraphMaximum(float value)
		{
			value = Mathf.Max(1f, value);
			float magnitude = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(value)));
			float normalized = value / magnitude;
			float rounded = normalized <= 1f ? 1f : normalized <= 2f ? 2f : normalized <= 5f ? 5f : 10f;
			return rounded * magnitude;
		}

		private void DrawStatusTag(Rect rect, MechPartCatalogEntry entry, bool onSelectedRow)
		{
			bool verified = entry.IsTested && !entry.NeedsReview;
			Color color = GUI.color;
			GUI.color = (onSelectedRow ? new Color(0.11f, 0.18f, 0.24f, 0.92f) : (verified ? new Color(0.2f, 0.36f, 0.4f, 0.78f) : new Color(0.38f, 0.28f, 0.16f, 0.82f)));
			GUI.DrawTexture(rect, Texture2D.whiteTexture);
			GUI.color = color;
			GUI.Label(rect, StatusLabel(entry), verified ? testedStyle : untestedStyle);
			DrawOutline(rect);
		}

		private void DrawOutline(Rect rect)
		{
			GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 1f), frameTexture);
			GUI.DrawTexture(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), frameTexture);
			GUI.DrawTexture(new Rect(rect.x, rect.y, 1f, rect.height), frameTexture);
			GUI.DrawTexture(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), frameTexture);
		}

		private static IReadOnlyList<MechPartSlot> GetSlotGroup(MechPartSlot slot)
		{
			if (WeaponSlots.Contains(slot))
			{
				return WeaponSlots;
			}
			if (FrameSlots.Contains(slot))
			{
				return FrameSlots;
			}
			return InternalSlots;
		}

		private static string SlotShortName(MechPartSlot slot)
		{
			return slot switch
			{
				MechPartSlot.LeftHandWeapon => "L 手", 
				MechPartSlot.RightHandWeapon => "R 手", 
				MechPartSlot.LeftBackWeapon => "L 肩", 
				MechPartSlot.RightBackWeapon => "R 肩", 
				MechPartSlot.Head => "头部", 
				MechPartSlot.Core => "核心", 
				MechPartSlot.Arms => "手臂", 
				MechPartSlot.Legs => "腿部", 
				MechPartSlot.Booster => "推进", 
				MechPartSlot.FireControlSystem => "火控", 
				MechPartSlot.Generator => "能源", 
				MechPartSlot.Radar => "雷达", 
				MechPartSlot.Extension => "扩展", 
				_ => slot.ToString(), 
			};
		}

		private static string SlotName(MechPartSlot slot)
		{
			return slot switch
			{
				MechPartSlot.Head => "头部", 
				MechPartSlot.Core => "核心", 
				MechPartSlot.Arms => "手臂", 
				MechPartSlot.Legs => "腿部", 
				MechPartSlot.Booster => "推进器", 
				MechPartSlot.Generator => "发电机", 
				MechPartSlot.FireControlSystem => "火控系统", 
				MechPartSlot.Radar => "雷达", 
				MechPartSlot.Extension => "扩展系统", 
				MechPartSlot.LeftHandWeapon => "左手武器", 
				MechPartSlot.RightHandWeapon => "右手武器", 
				MechPartSlot.LeftBackWeapon => "左肩武器", 
				MechPartSlot.RightBackWeapon => "右肩武器", 
				_ => slot.ToString(), 
			};
		}

		private static string StatusLabel(MechPartCatalogEntry entry)
		{
			if (entry == null)
			{
				return string.Empty;
			}
			if (!entry.NeedsReview)
			{
				if (!entry.IsTested)
				{
					return "未测试";
				}
				return "已测试";
			}
			return "待复核";
		}

		private void EnsureStyles()
		{
			float fontScale;
			if (!stylesReady)
			{
				stylesReady = true;
				uiFont = CreateUiFont();
				panelTexture = MakeTexture(new Color(0.018f, 0.035f, 0.052f, 1f));
				headerTexture = MakeTexture(new Color(0.05f, 0.082f, 0.118f, 0.98f));
				footerTexture = MakeTexture(new Color(0.028f, 0.052f, 0.078f, 1f));
				sectionTexture = MakeTexture(new Color(0.05f, 0.082f, 0.12f, 0.96f));
				sectionHeaderTexture = MakeTexture(new Color(0.08f, 0.125f, 0.178f, 0.98f));
				buttonTexture = MakeTexture(new Color(0.11f, 0.165f, 0.225f, 0.98f));
				buttonHoverTexture = MakeTexture(new Color(0.165f, 0.235f, 0.31f, 1f));
				buttonActiveTexture = MakeTexture(new Color(0.265f, 0.375f, 0.465f, 1f));
				selectedTexture = MakeTexture(new Color(0.675f, 0.77f, 0.83f, 1f));
				equippedTexture = MakeTexture(new Color(0.125f, 0.215f, 0.285f, 1f));
				primaryTexture = MakeTexture(new Color(0.675f, 0.77f, 0.83f, 1f));
				primaryHoverTexture = MakeTexture(new Color(0.79f, 0.855f, 0.895f, 1f));
				lineTexture = MakeTexture(new Color(0.45f, 0.65f, 0.755f, 0.88f));
				sliderTrackTexture = MakeTexture(new Color(0.035f, 0.075f, 0.11f, 1f));
				sliderThumbTexture = MakeTexture(new Color(0.72f, 0.835f, 0.89f, 1f));
				frameTexture = MakeTexture(new Color(0.215f, 0.35f, 0.445f, 0.82f));
				rowTexture = MakeTexture(new Color(0.09f, 0.145f, 0.205f, 0.72f));
				alternateRowTexture = MakeTexture(new Color(0.065f, 0.11f, 0.165f, 0.72f));
				metricFillTexture = MakeTexture(new Color(0.755f, 0.85f, 0.9f, 1f));
				scanlineTexture = MakeScanlineTexture();
				fontScale = Mathf.Clamp((float)Screen.height / 1080f, 0.9f, 1.2f);
				Color text = new Color(0.885f, 0.925f, 0.95f);
				Color secondaryText = new Color(0.69f, 0.77f, 0.825f);
				Color mutedText = new Color(0.445f, 0.555f, 0.63f);
				Color darkText = new Color(0.035f, 0.075f, 0.105f);
				titleStyle = NewStyle(Scaled(25), text, TextAnchor.MiddleLeft);
				headingStyle = NewStyle(Scaled(18), new Color(0.8f, 0.875f, 0.915f), TextAnchor.MiddleLeft);
				labelStyle = NewStyle(Scaled(16), secondaryText, TextAnchor.MiddleLeft);
				mutedStyle = NewStyle(Scaled(13), mutedText, TextAnchor.MiddleCenter);
				breadcrumbStyle = NewStyle(Scaled(13), mutedText, TextAnchor.MiddleRight);
				idStyle = NewStyle(Scaled(13), mutedText, TextAnchor.MiddleLeft);
				placeholderStyle = NewStyle(Scaled(13), new Color(0.35f, 0.46f, 0.535f), TextAnchor.MiddleLeft);
				valueStyle = NewStyle(Scaled(16), text, TextAnchor.MiddleRight);
				testedStyle = NewStyle(Scaled(12), new Color(0.57f, 0.835f, 0.78f), TextAnchor.MiddleCenter);
				untestedStyle = NewStyle(Scaled(12), new Color(0.925f, 0.675f, 0.33f), TextAnchor.MiddleCenter);
				warningStyle = NewStyle(Scaled(13), new Color(0.95f, 0.575f, 0.3f), TextAnchor.UpperLeft);
				warningStyle.wordWrap = true;
				noteStyle = NewStyle(Scaled(13), mutedText, TextAnchor.UpperLeft);
				noteStyle.wordWrap = true;
				overlineStyle = NewStyle(Scaled(10), new Color(0.445f, 0.62f, 0.71f), TextAnchor.MiddleCenter);
				darkHeadingStyle = NewStyle(Scaled(17), darkText, TextAnchor.MiddleLeft);
				darkLabelStyle = NewStyle(Scaled(13), new Color(0.075f, 0.135f, 0.175f), TextAnchor.MiddleLeft);
				darkCenteredStyle = NewStyle(Scaled(13), new Color(0.075f, 0.135f, 0.175f), TextAnchor.MiddleCenter);
				comparisonPositiveStyle = NewStyle(Scaled(16), ComparisonImprovementColor, TextAnchor.MiddleRight);
				comparisonPositiveStyle.fontStyle = FontStyle.Bold;
				comparisonNegativeStyle = NewStyle(Scaled(16), ComparisonRegressionColor, TextAnchor.MiddleRight);
				comparisonNegativeStyle.fontStyle = FontStyle.Bold;
				comparisonBaselineStyle = NewStyle(Scaled(16), text, TextAnchor.MiddleRight);
				comparisonBaselineStyle.fontStyle = FontStyle.Bold;
				comparisonArrowStyle = NewStyle(Scaled(15), ComparisonBaselineColor, TextAnchor.MiddleCenter);
				aggregateLabelStyle = NewStyle(Scaled(16), secondaryText, TextAnchor.MiddleLeft);
				aggregateLabelStyle.fontStyle = FontStyle.Bold;
				aggregateValueStyle = NewStyle(Scaled(16), text, TextAnchor.MiddleRight);
				aggregateValueStyle.fontStyle = FontStyle.Bold;
				sectionStyle = new GUIStyle(GUI.skin.box);
				sectionStyle.normal.background = sectionTexture;
				sectionStyle.padding = new RectOffset(0, 0, 0, 0);
				sectionStyle.margin = new RectOffset(0, 0, 0, 0);
				buttonStyle = new GUIStyle(GUI.skin.button)
				{
					font = uiFont,
					fontSize = Scaled(14),
					alignment = TextAnchor.MiddleLeft,
					padding = new RectOffset(14, 12, 5, 5),
					wordWrap = false
				};
				ConfigureButtonStyle(buttonStyle, buttonTexture, buttonHoverTexture, buttonActiveTexture, text);
				selectedButtonStyle = new GUIStyle(buttonStyle);
				ConfigureButtonStyle(selectedButtonStyle, selectedTexture, primaryHoverTexture, buttonActiveTexture, darkText);
				equippedButtonStyle = new GUIStyle(buttonStyle);
				ConfigureButtonStyle(equippedButtonStyle, equippedTexture, buttonHoverTexture, buttonActiveTexture, text);
				partButtonStyle = new GUIStyle(buttonStyle)
				{
					padding = new RectOffset(14, 112, 5, 5)
				};
				selectedPartButtonStyle = new GUIStyle(selectedButtonStyle)
				{
					padding = new RectOffset(14, 112, 5, 5)
				};
				equippedPartButtonStyle = new GUIStyle(equippedButtonStyle)
				{
					padding = new RectOffset(14, 112, 5, 5)
				};
				primaryButtonStyle = new GUIStyle(buttonStyle)
				{
					alignment = TextAnchor.MiddleCenter,
					fontSize = Scaled(16),
					padding = new RectOffset(12, 12, 4, 4)
				};
				ConfigureButtonStyle(primaryButtonStyle, primaryTexture, primaryHoverTexture, buttonActiveTexture, darkText);
				segmentButtonStyle = new GUIStyle(buttonStyle)
				{
					alignment = TextAnchor.MiddleCenter,
					padding = new RectOffset(4, 4, 2, 2)
				};
				selectedSegmentButtonStyle = new GUIStyle(segmentButtonStyle);
				ConfigureButtonStyle(selectedSegmentButtonStyle, selectedTexture, primaryHoverTexture, buttonActiveTexture, darkText);
				searchFieldStyle = new GUIStyle(GUI.skin.textField)
				{
					font = uiFont,
					fontSize = Scaled(14),
					alignment = TextAnchor.MiddleLeft,
					padding = new RectOffset(12, 12, 6, 6)
				};
				searchFieldStyle.normal.background = sectionHeaderTexture;
				searchFieldStyle.hover.background = buttonTexture;
				searchFieldStyle.focused.background = buttonHoverTexture;
				searchFieldStyle.normal.textColor = text;
				searchFieldStyle.hover.textColor = text;
				searchFieldStyle.focused.textColor = text;
				sliderStyle = new GUIStyle
				{
					fixedHeight = 6f,
					alignment = TextAnchor.MiddleCenter
				};
				sliderStyle.normal.background = sliderTrackTexture;
				sliderThumbStyle = new GUIStyle
				{
					fixedWidth = 14f,
					fixedHeight = 22f
				};
				sliderThumbStyle.normal.background = sliderThumbTexture;
				sliderThumbStyle.hover.background = primaryHoverTexture;
				sliderThumbStyle.active.background = primaryTexture;
			}
			int Scaled(int value)
			{
				return Mathf.RoundToInt((float)value * fontScale);
			}
		}

		private GUIStyle NewStyle(int size, Color color, TextAnchor anchor)
		{
			return new GUIStyle(GUI.skin.label)
			{
				font = uiFont,
				fontSize = size,
				alignment = anchor,
				normal = 
				{
					textColor = color
				}
			};
		}

		private static void ConfigureButtonStyle(GUIStyle style, Texture2D normal, Texture2D hover, Texture2D active, Color text)
		{
			style.normal.background = normal;
			style.hover.background = hover;
			style.active.background = active;
			style.focused.background = hover;
			style.normal.textColor = text;
			style.hover.textColor = text;
			style.active.textColor = text;
			style.focused.textColor = text;
		}

		private Font CreateUiFont()
		{
			string[] preferred = new string[5] { "Microsoft YaHei UI", "Microsoft YaHei", "Source Han Sans SC", "Noto Sans CJK SC", "DengXian" };
			HashSet<string> installed = new HashSet<string>(Font.GetOSInstalledFontNames(), StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < preferred.Length; i++)
			{
				if (installed.Contains(preferred[i]))
				{
					Font font = Font.CreateDynamicFontFromOSFont(preferred[i], 20);
					if (font != null)
					{
						font.hideFlags = HideFlags.HideAndDontSave;
						ownsUiFont = true;
						return font;
					}
				}
			}
			ownsUiFont = false;
			return GUI.skin.font;
		}

		private static Texture2D MakeTexture(Color color)
		{
			Texture2D texture2D = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false);
			texture2D.hideFlags = HideFlags.HideAndDontSave;
			texture2D.SetPixel(0, 0, color);
			texture2D.Apply();
			return texture2D;
		}

		private static Texture2D MakeScanlineTexture()
		{
			Texture2D texture = new Texture2D(4, 8, TextureFormat.RGBA32, mipChain: false)
			{
				hideFlags = HideFlags.HideAndDontSave,
				wrapMode = TextureWrapMode.Repeat,
				filterMode = FilterMode.Point
			};
			Color[] pixels = new Color[32];
			for (int y = 0; y < 8; y++)
			{
				for (int x = 0; x < 4; x++)
				{
					pixels[y * 4 + x] = new Color(0.55f, 0.72f, 0.82f, y switch
					{
						4 => 0.018f, 
						0 => 0.045f, 
						_ => 0f, 
					});
				}
			}
			texture.SetPixels(pixels);
			texture.Apply();
			return texture;
		}

		private static void DestroyTexture(Texture2D texture)
		{
			if (texture != null)
			{
				UnityEngine.Object.Destroy(texture);
			}
		}
	}
}
