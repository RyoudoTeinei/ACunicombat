using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ACunicombat.Mechs
{
    public sealed class MechFrontEnd : MonoBehaviour
    {
        private MechBattlefieldMap editorMap;
        private GameObject hangarHost;
        private enum Page { Home, NewGame, LoadGame, Scenes, Editor, Equipment, Settings }
        private Page page, equipmentReturn;
        private MechScenarioDefinition[] scenarios;
        private MechScenarioDefinition scenario;
        private MechCampaignStore.Slot campaign;
        private MechCampaignStore.ScenarioEntry scenarioEntry;
        private MechScenarioStore.Document document;
        private MechPartCatalog catalog;
        private MechScenarioUnit selectedUnit, equipmentUnit;
        private string selectedKey, equipmentKey;
        private MechLoadoutDefinition draft;
        private string message = "", heightText = "0", scenarioNameDraft = "";
        private bool unitMenu, loading, confirmOverwrite, confirmDelete, hasContinue;
        private int pendingSlot;
        private Vector2 unitMenuPoint, unitListScroll, scenarioScroll;
        private int templateIndex;
        private Font font;
        private GUIStyle titleStyle, headingStyle, labelStyle, buttonStyle, primaryButtonStyle, smallStyle, textStyle;
        private Texture2D buttonNormal, buttonHover, buttonActive, primaryNormal, primaryHover;
        private float volume = 1;
        private int quality;
        private bool fullscreen;
        private const float Height = 900;
        private float Width => Screen.width * Height / Mathf.Max(1, Screen.height);
        private readonly Color textColor = new Color(.82f, .90f, .85f);
        private readonly Color mutedColor = new Color(.43f, .57f, .49f);
        private readonly Color accentColor = new Color(.35f, .86f, .61f);
        private readonly Color lineColor = new Color(.12f, .24f, .19f);
        private readonly Color panelColor = new Color(.025f, .052f, .042f, 1);

        private void Awake()
        {
            Time.timeScale = 1;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            scenarios = Resources.LoadAll<MechScenarioDefinition>("Scenarios");
            catalog = MechLoadoutSelectionStore.LoadCatalog();
            if (scenarios.Length > 0)
            {
                campaign = MechCampaignStore.LoadWorking(scenarios[0], out var warning);
                message = warning ?? "";
                ChooseEntry(MechCampaignStore.Current(campaign));
                ImportLegacyPlayerLoadout();
                hasContinue = MechCampaignStore.LoadLatest(out _) != null;
            }
            volume = PlayerPrefs.GetFloat("frontend.volume", 1);
            quality = PlayerPrefs.GetInt("frontend.quality", QualitySettings.GetQualityLevel());
            fullscreen = PlayerPrefs.GetInt("frontend.fullscreen", Screen.fullScreen ? 1 : 0) != 0;
            AudioListener.volume = volume;
            quality = Mathf.Clamp(quality, 0, QualitySettings.names.Length - 1);
            QualitySettings.SetQualityLevel(quality, true);
            if (!Application.isEditor) Screen.fullScreen = fullscreen;
        }

        private void ChooseEntry(MechCampaignStore.ScenarioEntry value)
        {
            if (editorMap != null) { Destroy(editorMap.gameObject); editorMap = null; }
            scenarioEntry = value;
            document = value?.document;
            scenario = document == null ? scenarios.FirstOrDefault() : scenarios.FirstOrDefault(item => item.scenarioId == document.scenarioId);
            campaign.selectedEntryId = value?.id;
            selectedUnit = null;
            selectedKey = null;
            templateIndex = 0;
            scenarioNameDraft = value?.name ?? "";
        }

        private void ImportLegacyPlayerLoadout()
        {
            if (campaign == null || !campaign.legacyPlayerLoadoutPending || scenario == null) return;
            var player = scenario.units.FirstOrDefault(unit => unit.player);
            var edit = document?.units.FirstOrDefault(unit => unit.player);
            if (player == null || edit == null) return;
            var loadout = MechLoadoutSelectionStore.ResolveSavedPlayerLoadout(player.loadout);
            if (loadout != null)
            {
                MechScenarioStore.StoreLoadout(edit, loadout);
                if (loadout != player.loadout) Destroy(loadout);
            }
            campaign.legacyPlayerLoadoutPending = false;
            SaveScenario();
        }

        private static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private void Styles()
        {
            if (titleStyle != null) return;
            font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Consolas" }, 24);
            labelStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 17, wordWrap = true };
            labelStyle.normal.textColor = textColor;
            smallStyle = new GUIStyle(labelStyle) { fontSize = 13 };
            smallStyle.normal.textColor = mutedColor;
            headingStyle = new GUIStyle(labelStyle) { fontSize = 23 };
            titleStyle = new GUIStyle(labelStyle) { fontSize = 36 };
            textStyle = new GUIStyle(GUI.skin.textField) { font = font, fontSize = 16, alignment = TextAnchor.MiddleLeft };
            buttonNormal = Solid(new Color(.035f, .075f, .058f));
            buttonHover = Solid(new Color(.08f, .18f, .125f));
            buttonActive = Solid(new Color(.16f, .36f, .23f));
            primaryNormal = Solid(new Color(.10f, .29f, .18f));
            primaryHover = Solid(new Color(.17f, .43f, .27f));
            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                font = font,
                fontSize = 17,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(12, 12, 4, 4)
            };
            buttonStyle.normal.background = buttonNormal;
            buttonStyle.hover.background = buttonHover;
            buttonStyle.active.background = buttonActive;
            buttonStyle.normal.textColor = textColor;
            buttonStyle.hover.textColor = Color.white;
            buttonStyle.active.textColor = Color.white;
            primaryButtonStyle = new GUIStyle(buttonStyle);
            primaryButtonStyle.normal.background = primaryNormal;
            primaryButtonStyle.hover.background = primaryHover;
            primaryButtonStyle.normal.textColor = Color.white;
        }

        private bool Button(Rect rect, string text, bool enabled = true, bool primary = false)
        {
            var previous = GUI.enabled;
            GUI.enabled = previous && enabled;
            var clicked = GUI.Button(rect, text, primary ? primaryButtonStyle : buttonStyle);
            GUI.enabled = previous;
            return clicked;
        }

        private void Fill(Rect rect, Color color)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private void Line(Vector2 a, Vector2 b, Color color, float thickness = 1)
        {
            if (Event.current.type != EventType.Repaint) return;
            var matrix = GUI.matrix;
            var tint = GUI.color;
            try
            {
                GUI.color = color;
                GUI.matrix = matrix * Matrix4x4.TRS(new Vector3(a.x, a.y, 0), Quaternion.Euler(0, 0, Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg), Vector3.one);
                GUI.DrawTexture(new Rect(0, -thickness * .5f, Vector2.Distance(a, b), thickness), Texture2D.whiteTexture);
            }
            finally { GUI.matrix = matrix; GUI.color = tint; }
        }

        private string PageTitle
        {
            get
            {
                switch (page)
                {
                    case Page.NewGame: return "开始新游戏";
                    case Page.LoadGame: return "加载游戏";
                    case Page.Scenes: return "场景库";
                    case Page.Settings: return "设置";
                    default: return "ACUNICOMBAT";
                }
            }
        }

        private void OnGUI()
        {
            Styles();
            if (page == Page.Equipment) return;
            if (page == Page.Editor) { DrawScenarioEditor(); return; }
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(Vector3.one * (Screen.height / Height));
            GUI.depth = -5000;
            Fill(new Rect(0, 0, Width, Height), new Color(.009f, .019f, .016f, 1));
            if (page == Page.Home) Fill(new Rect(0, 0, 570, Height), new Color(.012f, .029f, .023f, 1));
            GUI.Label(new Rect(54, 30, Width - 108, 54), PageTitle, titleStyle);
            GUI.Label(new Rect(56, 91, Width - 112, 24), "TACTICAL OPERATIONS TERMINAL  /  作战终端", smallStyle);
            Line(new Vector2(54, 125), new Vector2(Width - 54, 125), lineColor);
            if (loading)
            {
                GUI.Label(new Rect((Width - 440) / 2, Height / 2, 440, 50), "正在载入战场…", titleStyle);
                GUI.matrix = old;
                return;
            }
            switch (page)
            {
                case Page.Home: DrawHome(); break;
                case Page.NewGame: DrawSlots(true); break;
                case Page.LoadGame: DrawSlots(false); break;
                case Page.Scenes: DrawScenes(); break;
                case Page.Settings: DrawSettings(); break;
            }
            DrawDialogs();
            GUI.Label(new Rect(54, Height - 42, Width - 108, 30), message, smallStyle);
            GUI.matrix = old;
        }

        private void DrawHome()
        {
            string[] labels = { "继续游戏", "开始新游戏", "加载游戏", "场景", "机库", "设置", "退出" };
            for (var i = 0; i < labels.Length; i++)
            {
                if (!Button(new Rect(74, 178 + i * 68, 390, 54), labels[i], i != 0 || hasContinue, i == 0)) continue;
                message = "";
                if (i == 0) ContinueLatest();
                else if (i == 1) page = Page.NewGame;
                else if (i == 2) page = Page.LoadGame;
                else if (i == 3) page = Page.Scenes;
                else if (i == 4)
                {
                    var player = scenario?.units.FirstOrDefault(unit => unit.player);
                    if (player != null) OpenEquipment(player, Page.Home);
                    else message = "未找到玩家配装资料";
                }
                else if (i == 5) page = Page.Settings;
                else
                {
#if UNITY_EDITOR
                    UnityEditor.EditorApplication.isPlaying = false;
#else
                    Application.Quit();
#endif
                }
            }

            var x = Width - 566;
            Fill(new Rect(x, 178, 492, 458), panelColor);
            Line(new Vector2(x, 178), new Vector2(x + 492, 178), accentColor, 2);
            GUI.Label(new Rect(x + 28, 207, 430, 36), "当前作战配置", headingStyle);
            if (campaign != null && scenarioEntry != null && document != null)
            {
                GUI.Label(new Rect(x + 28, 274, 430, 36), scenarioEntry.name, labelStyle);
                GUI.Label(new Rect(x + 28, 323, 430, 28), "存档位  " + campaign.slot + " / " + MechCampaignStore.SlotCount, smallStyle);
                GUI.Label(new Rect(x + 28, 366, 430, 28), document.units.Count + " 个单位  ·  " + document.units.Select(unit => unit.team).Distinct().Count() + " 支势力", labelStyle);
                GUI.Label(new Rect(x + 28, 409, 430, 28), "场景文档  " + campaign.scenarios.Count, labelStyle);
                GUI.Label(new Rect(x + 28, 464, 430, 28), "自动保存  " + MechCampaignStore.DisplayTime(campaign.updatedUtc), smallStyle);
            }
            GUI.Label(new Rect(x + 28, 552, 430, 52), "继续游戏将按已保存的部署与配装重新开始任务。", smallStyle);
        }

        private void ContinueLatest()
        {
            var loaded = MechCampaignStore.LoadLatest(out var error);
            if (loaded == null) { message = error ?? "没有可继续的存档"; return; }
            campaign = loaded;
            ChooseEntry(MechCampaignStore.Current(campaign));
            StartBattle();
        }

        private void LoadGame(int slot)
        {
            if (!MechCampaignStore.TryLoad(slot, out var loaded, out var error)) { message = error ?? "存档位为空"; return; }
            campaign = loaded;
            ChooseEntry(MechCampaignStore.Current(campaign));
            StartBattle();
        }

        private void RequestNewGame(int slot)
        {
            if (MechCampaignStore.Exists(slot)) { pendingSlot = slot; confirmOverwrite = true; return; }
            campaign = MechCampaignStore.CreateNew(slot, scenarios[0]);
            ChooseEntry(MechCampaignStore.Current(campaign));
            StartBattle();
        }

        private void DrawSlots(bool newGame)
        {
            const float top = 158;
            const float gap = 20;
            var cardWidth = (Width - 128 - gap) / 2;
            var cardHeight = 176;
            for (var slot = 1; slot <= MechCampaignStore.SlotCount; slot++)
            {
                var column = (slot - 1) % 2;
                var row = (slot - 1) / 2;
                var rect = new Rect(54 + column * (cardWidth + gap), top + row * (cardHeight + gap), cardWidth, cardHeight);
                Fill(rect, panelColor);
                var loaded = MechCampaignStore.TryLoad(slot, out var data, out var error);
                var current = loaded ? MechCampaignStore.Current(data) : null;
                GUI.Label(new Rect(rect.x + 20, rect.y + 15, 150, 28), "存档位 " + slot, headingStyle);
                GUI.Label(new Rect(rect.x + 22, rect.y + 55, rect.width - 44, 28), loaded ? current.name : string.IsNullOrEmpty(error) ? "空存档位" : "存档损坏", labelStyle);
                GUI.Label(new Rect(rect.x + 22, rect.y + 91, rect.width - 44, 24), loaded ? current.document.units.Count + " 个单位  ·  " + MechCampaignStore.DisplayTime(data.updatedUtc) : error ?? "可建立新的作战配置", smallStyle);
                var text = newGame ? loaded ? "覆盖并开始" : "建立并开始" : "加载并部署";
                if (Button(new Rect(rect.x + 20, rect.y + 128, rect.width - 40, 34), text, newGame || loaded, newGame && !loaded))
                {
                    if (newGame) RequestNewGame(slot);
                    else LoadGame(slot);
                }
            }
            if (Button(new Rect(54, Height - 96, 150, 42), "返回")) page = Page.Home;
        }

        private void DrawScenes()
        {
            if (campaign == null) return;
            var left = new Rect(54, 150, 410, 654);
            var right = new Rect(486, 150, Width - 540, 654);
            Fill(left, panelColor);
            Fill(right, panelColor);
            GUI.Label(new Rect(74, 169, 350, 32), "场景文档  " + campaign.scenarios.Count, headingStyle);
            scenarioScroll = GUI.BeginScrollView(new Rect(68, 216, 382, 566), scenarioScroll, new Rect(0, 0, 358, Mathf.Max(566, campaign.scenarios.Count * 70)));
            for (var i = 0; i < campaign.scenarios.Count; i++)
            {
                var entry = campaign.scenarios[i];
                var selected = entry == scenarioEntry;
                var rect = new Rect(0, i * 70, 358, 62);
                if (selected) Fill(rect, new Color(.08f, .20f, .135f));
                if (Button(rect, entry.name, true, selected)) ChooseEntry(entry);
                GUI.Label(new Rect(16, i * 70 + 39, 320, 18), entry.document.units.Count + " 个单位" + (entry.isDefault ? "  ·  基础场景" : ""), smallStyle);
            }
            GUI.EndScrollView();

            var x = right.x + 28;
            var width = right.width - 56;
            GUI.Label(new Rect(x, 177, width, 34), "场景配置", headingStyle);
            GUI.Label(new Rect(x, 239, 110, 28), "场景名称", smallStyle);
            scenarioNameDraft = GUI.TextField(new Rect(x + 112, 231, width - 224, 38), scenarioNameDraft, textStyle);
            if (Button(new Rect(x + width - 100, 231, 100, 38), "重命名", scenarioEntry != null) && !string.IsNullOrWhiteSpace(scenarioNameDraft))
            {
                scenarioEntry.name = scenarioNameDraft.Trim();
                SaveScenario();
            }
            if (scenarioEntry != null && document != null)
            {
                GUI.Label(new Rect(x, 307, width, 32), scenario?.displayName ?? "未知战场", labelStyle);
                GUI.Label(new Rect(x, 354, width, 28), document.units.Count + " 个单位  ·  " + document.units.Select(unit => unit.team).Distinct().Count() + " 支势力", labelStyle);
                GUI.Label(new Rect(x, 401, width, 28), "编队  " + document.units.Where(unit => !string.IsNullOrEmpty(unit.formationId)).Select(unit => unit.team + unit.formationId).Distinct().Count(), labelStyle);
                GUI.Label(new Rect(x, 454, width, 50), "编辑出生位置、队伍、编队顺位和每台机体的配装。所有修改自动保存到当前存档位。", smallStyle);
                if (Button(new Rect(x, 548, width, 48), "编辑场景", true, true)) { page = Page.Editor; unitMenu = false; }
                if (Button(new Rect(x, 610, width, 48), "以此场景开始")) StartBattle();
            }
            if (Button(new Rect(54, Height - 96, 150, 42), "返回")) page = Page.Home;
            if (Button(new Rect(224, Height - 96, 170, 42), "新建场景"))
            {
                ChooseEntry(MechCampaignStore.AddScenario(campaign, scenario, "新建场景 " + campaign.scenarios.Count));
                SaveScenario();
            }
            if (Button(new Rect(410, Height - 96, 170, 42), "复制当前", scenarioEntry != null))
            {
                ChooseEntry(MechCampaignStore.AddScenario(campaign, scenario, scenarioEntry.name + " 副本", scenarioEntry));
                SaveScenario();
            }
            if (Button(new Rect(Width - 224, Height - 96, 170, 42), "删除场景", scenarioEntry != null && !scenarioEntry.isDefault)) confirmDelete = true;
        }

        private void DrawDialogs()
        {
            if (!confirmOverwrite && !confirmDelete) return;
            Fill(new Rect(0, 0, Width, Height), new Color(0, 0, 0, .72f));
            var rect = new Rect((Width - 520) / 2, 320, 520, 220);
            Fill(rect, new Color(.025f, .055f, .043f));
            Line(new Vector2(rect.x, rect.y), new Vector2(rect.xMax, rect.y), accentColor, 2);
            GUI.Label(new Rect(rect.x + 28, rect.y + 28, rect.width - 56, 40), confirmOverwrite ? "覆盖存档位 " + pendingSlot + "？" : "删除当前场景？", headingStyle);
            GUI.Label(new Rect(rect.x + 28, rect.y + 82, rect.width - 56, 44), confirmOverwrite ? "该槽现有场景与配装将被新游戏替换。" : "仅删除这份场景文档，不影响其他场景。", labelStyle);
            if (Button(new Rect(rect.x + 28, rect.y + 154, 210, 42), "取消")) { confirmOverwrite = false; confirmDelete = false; }
            if (Button(new Rect(rect.x + 282, rect.y + 154, 210, 42), "确认", true, true))
            {
                if (confirmOverwrite)
                {
                    confirmOverwrite = false;
                    campaign = MechCampaignStore.CreateNew(pendingSlot, scenarios[0]);
                    ChooseEntry(MechCampaignStore.Current(campaign));
                    StartBattle();
                }
                else
                {
                    confirmDelete = false;
                    MechCampaignStore.DeleteScenario(campaign, scenarioEntry.id);
                    ChooseEntry(MechCampaignStore.Current(campaign));
                    SaveScenario();
                }
            }
        }

        private void StartBattle()
        {
            if (loading || scenario == null || document == null) return;
            try { MechCampaignStore.Save(campaign); }
            catch (Exception exception) { message = "保存失败：" + exception.Message; return; }
            if (!MechScenarioStore.Begin(scenario, document, catalog, out var error)) { message = error; return; }
            var index = SceneUtility.GetBuildIndexByScenePath(scenario.scenePath);
            if (index < 0) { message = "场景尚未加入构建列表"; return; }
            Time.timeScale = 1;
            AudioListener.volume = volume;
            loading = true;
            SceneManager.LoadSceneAsync(index);
        }

        private Vector3 Origin => document.units.FirstOrDefault(unit => unit.player)?.position ?? Vector3.zero;
        private MechScenarioStore.UnitEdit Edit(string key) => MechScenarioStore.Unit(document, key);
        private MechScenarioStore.UnitEdit Edit(MechScenarioUnit unit) => document.units.FirstOrDefault(edit => edit.templateKey == unit.key);

        private void SaveScenario()
        {
            try
            {
                campaign.selectedEntryId = scenarioEntry.id;
                MechCampaignStore.Save(campaign);
                message = "已自动保存到存档位 " + campaign.slot;
            }
            catch (Exception exception) { message = "保存失败：" + exception.Message; }
        }

        private IEnumerable<MechBattlefieldMap.Contact> ScenarioContacts()
        {
            var team = document.units.First(unit => unit.player).team;
            foreach (var edit in document.units)
                yield return new MechBattlefieldMap.Contact
                {
                    Key = edit.key,
                    Name = edit.name,
                    Type = "MC",
                    Own = edit.player,
                    Position = edit.position,
                    Confidence = 1,
                    Source = "场景配置",
                    Affiliation = edit.team == team ? ACunicombat.Sensing.Contracts.TrackAffiliation.Friendly : ACunicombat.Sensing.Contracts.TrackAffiliation.Hostile
                };
        }

        private Rect EditorPanel => new Rect(Screen.width - 340, 20, 320, 620);
        private Rect EditorBottom => new Rect(20, Screen.height - 72, Screen.width - 40, 56);
        private Rect EditorNotice => new Rect(20, Screen.height - 112, Screen.width - 40, 32);

        private void SelectUnit(string key, bool context)
        {
            selectedKey = key;
            var edit = Edit(key);
            selectedUnit = edit != null ? MechScenarioStore.Template(scenario, edit.templateKey) : null;
            unitMenu = context && edit != null;
            if (edit != null) heightText = (edit.position.y / 1000).ToString("0.0");
            var mouse = UnityEngine.InputSystem.Mouse.current.position.ReadValue();
            unitMenuPoint = new Vector2(Mathf.Min(mouse.x, Screen.width - 160), Mathf.Min(Screen.height - mouse.y, Screen.height - 100));
        }

        private void EnsureEditorMap()
        {
            if (editorMap == null)
            {
                var host = new GameObject("Scenario Map");
                editorMap = host.AddComponent<MechBattlefieldMap>();
                editorMap.ConfigureScenarioEditor(Origin, ScenarioContacts, (key, position, done) =>
                {
                    var edit = Edit(key);
                    if (edit == null) return;
                    if (done) SaveScenario();
                    else edit.position = new Vector3(position.x, edit.position.y, position.z);
                }, (key, context) => SelectUnit(key, context), point => EditorPanel.Contains(point) || EditorBottom.Contains(point) || (!string.IsNullOrWhiteSpace(message) && EditorNotice.Contains(point)) || (unitMenu && new Rect(unitMenuPoint.x, unitMenuPoint.y, 160, 42).Contains(point)));
            }
            else if (!editorMap.Visible) editorMap.SetVisible(true);
        }

        private void DrawScenarioEditor()
        {
            EnsureEditorMap();
            var depth = GUI.depth;
            GUI.depth = -5000;
            GUI.Box(EditorPanel, "");
            var x = EditorPanel.x + 14;
            var templates = scenario.units.Where(unit => !unit.player).ToArray();
            templateIndex = Mathf.Clamp(templateIndex, 0, templates.Length - 1);
            GUI.Label(new Rect(x, 34, 250, 30), scenarioEntry.name, labelStyle);
            if (templates.Length > 0)
            {
                GUI.Label(new Rect(x, 72, 210, 30), templates[templateIndex].name, smallStyle);
                if (Button(new Rect(x + 214, 70, 28, 30), ">")) templateIndex = (templateIndex + 1) % templates.Length;
                if (Button(new Rect(x + 246, 70, 46, 30), "添加"))
                {
                    var edit = MechScenarioStore.AddUnit(document, templates[templateIndex]);
                    document.units.Add(edit);
                    SelectUnit(edit.key, false);
                    SaveScenario();
                }
            }
            unitListScroll = GUI.BeginScrollView(new Rect(x, 112, 292, 136), unitListScroll, new Rect(0, 0, 272, Mathf.Max(136, document.units.Count * 32)));
            for (var i = 0; i < document.units.Count; i++)
            {
                var edit = document.units[i];
                var formation = string.IsNullOrEmpty(edit.formationId) ? "独立" : edit.formationId + " #" + edit.formationIndex;
                if (Button(new Rect(0, i * 32, 272, 30), (edit.key == selectedKey ? "● " : "") + edit.name + "  " + formation)) SelectUnit(edit.key, false);
            }
            GUI.EndScrollView();
            var selected = Edit(selectedKey);
            if (selected != null)
            {
                GUI.Label(new Rect(x, 260, 250, 32), selected.name, labelStyle);
                var position = selected.position;
                GUI.Label(new Rect(x, 300, 250, 28), $"X {position.x / 1000:0.0} km / Z {position.z / 1000:0.0} km", smallStyle);
                GUI.Label(new Rect(x, 334, 120, 28), "高度（km）", smallStyle);
                heightText = GUI.TextField(new Rect(x + 120, 330, 122, 30), heightText, textStyle);
                if (Button(new Rect(x, 368, 240, 32), "设置高度") && float.TryParse(heightText, out var height) && float.IsFinite(height))
                {
                    selected.position = new Vector3(position.x, height * 1000, position.z);
                    SaveScenario();
                }
                GUI.Label(new Rect(x, 410, 70, 30), "队伍", smallStyle);
                if (Button(new Rect(x + 70, 408, 80, 30), selected.team == 1 ? "1 ✓" : "1") && selected.team != 1)
                {
                    selected.team = 1;
                    selected.formationId = "team-1";
                    MechScenarioStore.NormalizeFormations(document);
                    SaveScenario();
                }
                if (Button(new Rect(x + 158, 408, 80, 30), selected.team == 2 ? "2 ✓" : "2") && selected.team != 2)
                {
                    selected.team = 2;
                    selected.formationId = "team-2";
                    MechScenarioStore.NormalizeFormations(document);
                    SaveScenario();
                }
                var formation = string.IsNullOrEmpty(selected.formationId) ? "独立" : selected.formationId;
                GUI.Label(new Rect(x, 448, 120, 30), formation, smallStyle);
                if (Button(new Rect(x + 122, 446, 58, 30), "独立")) { MechScenarioStore.AssignFormation(document, selected.key, ""); SaveScenario(); }
                if (Button(new Rect(x + 186, 446, 58, 30), "新编队")) { MechScenarioStore.AssignFormation(document, selected.key, "编队-" + Guid.NewGuid().ToString("N").Substring(0, 6)); SaveScenario(); }
                if (Button(new Rect(x, 486, 116, 32), "切换编队")) CycleFormation(selected);
                if (Button(new Rect(x + 124, 486, 56, 32), "上移")) { MechScenarioStore.MoveUnit(document, selected.key, -1); SaveScenario(); }
                if (Button(new Rect(x + 188, 486, 56, 32), "下移")) { MechScenarioStore.MoveUnit(document, selected.key, 1); SaveScenario(); }
                if (Button(new Rect(x, 526, 116, 34), "配装")) OpenEquipment(selectedUnit, Page.Editor);
                if (Button(new Rect(x + 124, 526, 120, 34), "删除机体", !selected.player))
                {
                    if (MechScenarioStore.DeleteUnit(document, selected.key)) { selectedKey = null; selectedUnit = null; SaveScenario(); }
                }
            }
            if (unitMenu && selected != null && Button(new Rect(unitMenuPoint.x, unitMenuPoint.y, 160, 42), "配装"))
            {
                unitMenu = false;
                OpenEquipment(selectedUnit, Page.Editor);
            }
            GUI.Box(EditorBottom, "");
            if (!string.IsNullOrWhiteSpace(message))
            {
                var notice = EditorNotice;
                GUI.Box(notice, "");
                GUI.Label(new Rect(notice.x + 10, notice.y + 3, notice.width - 20, notice.height - 6), message, smallStyle);
            }
            if (Button(new Rect(30, Screen.height - 62, 130, 36), "返回场景")) { SaveScenario(); editorMap.SetVisible(false); page = Page.Scenes; }
            if (Button(new Rect(174, Screen.height - 62, 160, 36), "恢复默认场景"))
            {
                document = MechScenarioStore.Defaults(scenario);
                scenarioEntry.document = document;
                selectedKey = null;
                selectedUnit = null;
                SaveScenario();
            }
            GUI.Label(new Rect(350, Screen.height - 62, Screen.width - 550, 38), "左键拖动单位 · 右键配装 · 中键平移 · 滚轮缩放", smallStyle);
            if (Button(new Rect(Screen.width - 170, Screen.height - 62, 140, 36), "开始场景")) StartBattle();
            GUI.depth = depth;
        }

        private void CycleFormation(MechScenarioStore.UnitEdit selected)
        {
            var formations = document.units.Where(unit => unit.team == selected.team && !string.IsNullOrEmpty(unit.formationId)).Select(unit => unit.formationId).Distinct().OrderBy(value => value).ToArray();
            if (formations.Length == 0) { MechScenarioStore.AssignFormation(document, selected.key, "team-" + selected.team); SaveScenario(); return; }
            var current = Array.IndexOf(formations, selected.formationId);
            var next = formations[(current + 1 + formations.Length) % formations.Length];
            MechScenarioStore.AssignFormation(document, selected.key, next);
            SaveScenario();
        }

        private void OpenEquipment(MechScenarioUnit unit, Page returnPage)
        {
            try
            {
                if (editorMap != null) editorMap.SetVisible(false);
                equipmentUnit = unit;
                equipmentKey = selectedKey ?? unit.key;
                equipmentReturn = returnPage;
                if (draft != null) Destroy(draft);
                var edit = Edit(equipmentKey);
                draft = edit != null && edit.customLoadout ? MechScenarioStore.BuildLoadout(unit, edit, catalog) : Instantiate(unit.loadout);
                hangarHost = new GameObject("Scenario Hangar");
                hangarHost.SetActive(false);
                var assembler = hangarHost.AddComponent<MechAssembler>();
                assembler.Loadout = draft;
                var menu = hangarHost.AddComponent<MechAssemblyMenu>();
                hangarHost.SetActive(true);
                menu.OpenForEditing(draft, SaveEquipment, () => { page = equipmentReturn; Destroy(hangarHost); hangarHost = null; });
                page = Page.Equipment;
            }
            catch (Exception exception) { message = exception.Message; page = returnPage; }
        }

        private void SaveEquipment(MechLoadoutDefinition value)
        {
            MechScenarioStore.StoreLoadout(Edit(equipmentKey), value);
            SaveScenario();
            message = "配装已保存到存档位 " + campaign.slot;
        }

        private void DrawSettings()
        {
            var x = (Width - 520) / 2;
            GUI.Label(new Rect(x, 200, 520, 30), "主音量 " + Mathf.RoundToInt(volume * 100) + "%", labelStyle);
            volume = GUI.HorizontalSlider(new Rect(x, 244, 520, 25), volume, 0, 1);
            AudioListener.volume = volume;
            GUI.Label(new Rect(x, 300, 520, 30), "画质", labelStyle);
            quality = GUI.SelectionGrid(new Rect(x, 342, 520, 80), Mathf.Clamp(quality, 0, QualitySettings.names.Length - 1), QualitySettings.names, 3, buttonStyle);
            fullscreen = GUI.Toggle(new Rect(x, 460, 520, 30), fullscreen, "全屏");
            if (Button(new Rect(x, 550, 180, 44), "保存并返回"))
            {
                PlayerPrefs.SetFloat("frontend.volume", volume);
                PlayerPrefs.SetInt("frontend.quality", quality);
                PlayerPrefs.SetInt("frontend.fullscreen", fullscreen ? 1 : 0);
                PlayerPrefs.Save();
                QualitySettings.SetQualityLevel(quality, true);
                if (!Application.isEditor) Screen.fullScreen = fullscreen;
                page = Page.Home;
                message = "设置已保存";
            }
        }

        private void OnDestroy()
        {
            if (editorMap != null) Destroy(editorMap.gameObject);
            if (hangarHost != null) Destroy(hangarHost);
            if (draft != null) Destroy(draft);
            if (font != null) Destroy(font);
            if (buttonNormal != null) Destroy(buttonNormal);
            if (buttonHover != null) Destroy(buttonHover);
            if (buttonActive != null) Destroy(buttonActive);
            if (primaryNormal != null) Destroy(primaryNormal);
            if (primaryHover != null) Destroy(primaryHover);
        }
    }
}
