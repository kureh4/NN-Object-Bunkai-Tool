#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace NekoNeko.Tools.ObjectBunkai
{
    public sealed class NNObjectBunkaiToolWindow : EditorWindow
    {
        private const string Version = "1.3.0";
        private const string BuildToken = "1.3.0-nn-object-bunkai-tool-rename-20261101";
        private const string ToolRootFolder = "Assets/nekoneko/NN - Object Bunkai Tool";
        private const string LegacyToolRootFolder = "Assets/nekoneko/object_bunkai";
        private const string DefaultOutputFolder = ToolRootFolder + "/Generated";
        private const string CombinedPrefabsFolder = DefaultOutputFolder + "/CombinedPrefabs";
        private const string GeneratedPreviewNamePrefix = "__NekoNekoGeneratedPreview__";

        private static readonly string[] SeparationModeLabels =
        {
            "連結メッシュ単位",
            "オブジェクト単位"
        };

        private static readonly string[] PageLabels =
        {
            "対象選択",
            "分解設定",
            "生成プレビュー"
        };

        [SerializeField] private GameObject prefabRoot;
        [SerializeField] private SeparationMode separationMode = SeparationMode.ConnectedParts;
        [SerializeField] private float linkedVertexTolerance = 0.0001f;
        [SerializeField] private List<string> selectedRendererIds = new List<string>();
        [SerializeField] private bool duplicateMaterials;
        [SerializeField] private bool duplicateMaterialsPerPart;
        [FormerlySerializedAs("disableOriginalRenderer")]
        [SerializeField] private bool hideOriginalObject = true;
        [SerializeField] private ToolPage activePage = ToolPage.TargetSelection;
        [SerializeField] private List<string> selectedGeneratedPrefabPaths = new List<string>();
        [SerializeField] private string activeGeneratedPrefabPath = string.Empty;
        [SerializeField] private string soloGeneratedPrefabPath = string.Empty;
        [SerializeField] private string combinedPrefabName = "CombinedPrefab";
        [SerializeField] private bool generatedPreviewVisible = true;
        [SerializeField] private bool generatedIntegrationMode;
        [SerializeField] private bool createModularAvatarMergeArmature = true;
        [SerializeField] private Vector2 mainScrollPosition;
        [SerializeField] private Vector2 rendererScrollPosition;
        [SerializeField] private Vector2 previewScrollPosition;

        [NonSerialized] private readonly List<RendererCandidate> rendererCandidates = new List<RendererCandidate>();
        [NonSerialized] private readonly List<PreviewPartEntry> previewParts = new List<PreviewPartEntry>();
        [NonSerialized] private readonly List<GeneratedPrefabEntry> generatedPrefabEntries = new List<GeneratedPrefabEntry>();
        [NonSerialized] private readonly Dictionary<string, bool> generatedGroupFoldouts = new Dictionary<string, bool>();
        [NonSerialized] private readonly Dictionary<string, Vector2> generatedGroupScrollPositions = new Dictionary<string, Vector2>();
        [NonSerialized] private PrefabScanSummary scanSummary = new PrefabScanSummary();
        [NonSerialized] private GameObject cachedPrefabRoot;
        [NonSerialized] private GameObject generatedPreviewObject;
        [NonSerialized] private readonly Dictionary<string, GameObject> generatedPreviewObjects = new Dictionary<string, GameObject>();
        [NonSerialized] private bool generatedSceneViewStateCaptured;
        [NonSerialized] private Vector3 generatedSceneViewSavedPivot;
        [NonSerialized] private Quaternion generatedSceneViewSavedRotation;
        [NonSerialized] private float generatedSceneViewSavedSize;
        [NonSerialized] private bool generatedSceneViewSavedOrthographic;

        [MenuItem("ねこねこツール/NN - Object Bunkai Tool", false, 1)]
        private static void OpenWindow()
        {
            NNObjectBunkaiToolWindow window = GetWindow<NNObjectBunkaiToolWindow>();
            window.titleContent = new GUIContent("NN - Object Bunkai Tool");
            window.minSize = new Vector2(560f, 580f);
            window.Show();
        }

        [MenuItem("GameObject/ねこねこツール/NN - Object Bunkai Toolを開く", false, 30)]
        private static void OpenFromGameObjectMenu()
        {
            NNObjectBunkaiToolWindow window = GetWindow<NNObjectBunkaiToolWindow>();
            window.titleContent = new GUIContent("NN - Object Bunkai Tool");
            window.prefabRoot = Selection.activeGameObject;
            window.RefreshRendererCandidates(false);
            window.Show();
        }

        [MenuItem("GameObject/ねこねこツール/NN - Object Bunkai Toolを開く", true)]
        private static bool ValidateOpenFromGameObjectMenu()
        {
            return Selection.activeGameObject != null;
        }

        private void OnEnable()
        {
            CleanupOrphanedGeneratedPreviewObjects();
            generatedPreviewObject = null;
            generatedPreviewObjects.Clear();
            activeGeneratedPrefabPath = string.Empty;
            soloGeneratedPrefabPath = string.Empty;

            if (prefabRoot == null && Selection.activeGameObject != null)
            {
                prefabRoot = Selection.activeGameObject;
            }

            MigrateLegacyInstallAndCleanup();
            RefreshRendererCandidates(true);
            RefreshGeneratedPreview();
        }

        private void OnDisable()
        {
            EndGeneratedPreviewDisplay(true, false);
        }

        private const int LegacyInstallCleanupMaxAttempts = 30;
        private static int legacyInstallCleanupAttempts;

        [InitializeOnLoadMethod]
        private static void ScheduleLegacyInstallMigration()
        {
            legacyInstallCleanupAttempts = 0;
            EditorApplication.update -= TryLegacyInstallMigration;
            EditorApplication.update += TryLegacyInstallMigration;
        }

        private static void TryLegacyInstallMigration()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            legacyInstallCleanupAttempts++;
            bool completed = MigrateLegacyInstallAndCleanup();
            if (completed || legacyInstallCleanupAttempts >= LegacyInstallCleanupMaxAttempts)
            {
                EditorApplication.update -= TryLegacyInstallMigration;
            }
        }

        private static bool MigrateLegacyInstallAndCleanup()
        {
            const string legacyReadmeFolder = "Assets/!nekoneko_ReadMe";
            string legacyReadmePath = LegacyToolRootFolder + "/README.txt";

            bool generatedMigrated = MigrateLegacyGeneratedAssets();
            bool readmeFolderRemoved = DeleteLegacyAssetCompletely(legacyReadmeFolder);
            bool readmeRemoved = DeleteLegacyAssetCompletely(legacyReadmePath);
            bool legacyToolRemoved = generatedMigrated && DeleteLegacyAssetCompletely(LegacyToolRootFolder);

            if (!generatedMigrated || !readmeFolderRemoved || !readmeRemoved || !legacyToolRemoved)
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                return false;
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            return true;
        }

        private static bool MigrateLegacyGeneratedAssets()
        {
            string legacyGeneratedFolder = LegacyToolRootFolder + "/Generated";
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
            {
                return false;
            }

            string legacyGeneratedFullPath = Path.Combine(
                projectRoot,
                legacyGeneratedFolder.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(legacyGeneratedFullPath) && !AssetDatabase.IsValidFolder(legacyGeneratedFolder))
            {
                return true;
            }

            EnsureFolderExists(DefaultOutputFolder);

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(legacyGeneratedFullPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[NN - Object Bunkai Tool] 旧Generatedの確認に失敗しました。再試行します: " + ex.Message);
                return false;
            }

            for (int i = 0; i < entries.Length; i++)
            {
                string entry = entries[i];
                if (entry.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string name = Path.GetFileName(entry);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                string sourceAssetPath = legacyGeneratedFolder + "/" + name;
                string destinationAssetPath = DefaultOutputFolder + "/" + name;
                string destinationFullPath = Path.Combine(
                    projectRoot,
                    destinationAssetPath.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(destinationFullPath) || Directory.Exists(destinationFullPath) ||
                    AssetDatabase.LoadMainAssetAtPath(destinationAssetPath) != null ||
                    AssetDatabase.IsValidFolder(destinationAssetPath))
                {
                    destinationAssetPath = AssetDatabase.GenerateUniqueAssetPath(destinationAssetPath);
                }

                string error = AssetDatabase.MoveAsset(sourceAssetPath, destinationAssetPath);
                if (!string.IsNullOrEmpty(error))
                {
                    Debug.LogWarning(
                        "[NN - Object Bunkai Tool] 旧Generatedのデータ移行に失敗しました。再試行します: " +
                        sourceAssetPath + " -> " + destinationAssetPath + "\n" + error);
                    return false;
                }
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            return true;
        }

        private static bool DeleteLegacyAssetCompletely(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
            {
                return false;
            }

            string normalizedRelativePath = assetPath.Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.Combine(projectRoot, normalizedRelativePath);
            bool exists = File.Exists(fullPath) || Directory.Exists(fullPath) || AssetDatabase.LoadMainAssetAtPath(assetPath) != null || AssetDatabase.IsValidFolder(assetPath);

            if (!exists)
            {
                FileUtil.DeleteFileOrDirectory(fullPath + ".meta");
                return true;
            }

            AssetDatabase.DeleteAsset(assetPath);

            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                FileUtil.DeleteFileOrDirectory(fullPath);
            }

            FileUtil.DeleteFileOrDirectory(fullPath + ".meta");

            bool stillExists = File.Exists(fullPath) || Directory.Exists(fullPath);
            if (stillExists)
            {
                Debug.LogWarning("[NN - Object Bunkai Tool] 旧ファイルを削除できませんでした。再試行します: " + assetPath);
                return false;
            }

            return true;
        }

        private void OnGUI()
        {
            mainScrollPosition = EditorGUILayout.BeginScrollView(mainScrollPosition);

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("NN - Object Bunkai Tool", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Version " + Version, EditorStyles.miniLabel);
            EditorGUILayout.Space(6f);

            ToolPage previousPage = activePage;
            activePage = (ToolPage)GUILayout.Toolbar((int)activePage, PageLabels, GUILayout.Height(26f));
            if (activePage != previousPage)
            {
                mainScrollPosition = Vector2.zero;
                if (activePage == ToolPage.GeneratedPreview)
                {
                    RefreshGeneratedPreview();
                }
            }

            EditorGUILayout.Space(10f);
            switch (activePage)
            {
                case ToolPage.TargetSelection:
                    DrawTargetSelectionPage();
                    break;
                case ToolPage.Settings:
                    DrawSettingsPage();
                    break;
                case ToolPage.GeneratedPreview:
                    DrawGeneratedPreviewPage();
                    break;
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.EndScrollView();

            DrawExecutionFooter();
        }

        private void DrawExecutionFooter()
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            bool canSeparate = CanSeparate(out _);
            using (new EditorGUI.DisabledScope(!canSeparate))
            {
                if (GUILayout.Button("選択したメッシュを分解", GUILayout.Height(42f)))
                {
                    SeparateSelectedRenderers();
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4f);
        }

        private void DrawTargetSelectionPage()
        {
            EditorGUI.BeginChangeCheck();
            GameObject newPrefabRoot = (GameObject)EditorGUILayout.ObjectField(
                "Prefab / アバタールート",
                prefabRoot,
                typeof(GameObject),
                true);
            if (EditorGUI.EndChangeCheck())
            {
                prefabRoot = newPrefabRoot;
                RefreshRendererCandidates(false);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Hierarchyの選択を取得"))
                {
                    prefabRoot = Selection.activeGameObject;
                    RefreshRendererCandidates(false);
                }

                using (new EditorGUI.DisabledScope(prefabRoot == null))
                {
                    if (GUILayout.Button("再検索", GUILayout.Width(90f)))
                    {
                        RefreshRendererCandidates(true);
                    }
                }
            }

            if (prefabRoot != cachedPrefabRoot)
            {
                RefreshRendererCandidates(false);
            }

            EditorGUILayout.Space(8f);
            DrawRendererSelection();
        }

        private void DrawSettingsPage()
        {
            EditorGUILayout.LabelField("分解方法", EditorStyles.boldLabel);
            int modeIndex = separationMode == SeparationMode.ConnectedParts ? 0 : 1;
            modeIndex = EditorGUILayout.Popup("処理単位", modeIndex, SeparationModeLabels);
            separationMode = modeIndex == 0
                ? SeparationMode.ConnectedParts
                : SeparationMode.ObjectUnit;

            if (separationMode == SeparationMode.ConnectedParts)
            {
                linkedVertexTolerance = Mathf.Max(
                    0.0000001f,
                    EditorGUILayout.FloatField("同一位置の判定距離", linkedVertexTolerance));
            }

            EditorGUILayout.Space(8f);
            duplicateMaterials = EditorGUILayout.ToggleLeft(
                "マテリアルを複製する（テクスチャ画像は共有）",
                duplicateMaterials);

            using (new EditorGUI.DisabledScope(!duplicateMaterials))
            {
                EditorGUI.indentLevel++;
                duplicateMaterialsPerPart = EditorGUILayout.ToggleLeft(
                    "パーツごとにマテリアルを作成する",
                    duplicateMaterialsPerPart);
                EditorGUI.indentLevel--;
            }

            if (!duplicateMaterials)
            {
                duplicateMaterialsPerPart = false;
            }

            hideOriginalObject = EditorGUILayout.ToggleLeft(
                "分解後に元オブジェクトを非表示にする",
                hideOriginalObject);

        }

        private void DrawGeneratedPreviewPage()
        {
            if (!string.IsNullOrEmpty(soloGeneratedPrefabPath) &&
                !SceneVisibilityManager.instance.IsCurrentStageIsolated())
            {
                soloGeneratedPrefabPath = string.Empty;
                RestoreGeneratedPreviewSceneViewState();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("生成プレビュー", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField(
                    "選択中: " + selectedGeneratedPrefabPaths.Count + "パーツ",
                    EditorStyles.miniBoldLabel,
                    GUILayout.Width(110f));

                GUIContent refreshContent = EditorGUIUtility.IconContent("Refresh");
                if (refreshContent == null || refreshContent.image == null)
                {
                    refreshContent = new GUIContent("↻", "生成プレビューを更新");
                }
                else
                {
                    refreshContent.tooltip = "生成プレビューを更新";
                }

                if (GUILayout.Button(
                        refreshContent,
                        GUI.skin.button,
                        GUILayout.Width(48f),
                        GUILayout.Height(32f)))
                {
                    RefreshGeneratedPreview();
                }
            }

            if (generatedIntegrationMode)
            {
                EditorGUILayout.LabelField(
                    "統合モード中：パーツをクリックすると複数選択できます。",
                    EditorStyles.miniLabel);
            }

            if (generatedPrefabEntries.Count == 0)
            {
                EditorGUILayout.HelpBox("生成済みPrefabがありません。", MessageType.Info);
                return;
            }

            int index = 0;
            while (index < generatedPrefabEntries.Count)
            {
                GeneratedPrefabEntry firstEntry = generatedPrefabEntries[index];
                string groupPath = firstEntry.GroupPath;
                int endIndex = index + 1;
                while (endIndex < generatedPrefabEntries.Count &&
                       string.Equals(
                           generatedPrefabEntries[endIndex].GroupPath,
                           groupPath,
                           StringComparison.Ordinal))
                {
                    endIndex++;
                }

                if (!generatedGroupFoldouts.TryGetValue(groupPath, out bool groupOpen))
                {
                    // 生成グループは初期状態では閉じておく。
                    groupOpen = false;
                }

                string groupLabel = GetGeneratedGroupDisplayName(firstEntry.GroupName) +
                                    "  (" + (endIndex - index) + "パーツ)";
                groupOpen = EditorGUILayout.Foldout(
                    groupOpen,
                    groupLabel,
                    true,
                    EditorStyles.foldout);
                generatedGroupFoldouts[groupPath] = groupOpen;

                if (groupOpen)
                {
                    DrawGeneratedPreviewGroup(index, endIndex, groupPath);
                }

                index = endIndex;
                EditorGUILayout.Space(6f);
            }

            DrawCombinedPrefabControls();
        }

        private void DrawGeneratedPreviewGroup(
            int startIndex,
            int endIndex,
            string groupPath)
        {
            const float rowHeight = 24f;
            const float visibleRowCount = 5f;
            const float listHeight = rowHeight * visibleRowCount + 10f;
            const float columnGap = 8f;
            const float actionMinWidth = 180f;
            const float horizontalBreakpoint = 520f;

            // currentViewWidth はドック/スクロール状態によって小さく返ることがあるため、
            // 実際の EditorWindow 幅を基準にする。
            float windowContentWidth = Mathf.Max(260f, position.width - 18f);
            bool useHorizontalLayout = windowContentWidth >= horizontalBreakpoint;

            if (useHorizontalLayout)
            {
                float actionWidth = Mathf.Clamp(windowContentWidth * 0.32f, actionMinWidth, 300f);
                float listWidth = Mathf.Max(280f, windowContentWidth - actionWidth - columnGap);

                using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandWidth(true)))
                {
                    DrawGeneratedPreviewPartList(
                        startIndex,
                        endIndex,
                        groupPath,
                        listHeight,
                        GUILayout.Width(listWidth));

                    GUILayout.Space(columnGap);

                    DrawGeneratedPreviewActionPanel(
                        groupPath,
                        listHeight,
                        GUILayout.Width(actionWidth));
                }
            }
            else
            {
                // 狭い時は無理に2列へ押し込まず、一覧を横幅いっぱいに使う。
                DrawGeneratedPreviewPartList(
                    startIndex,
                    endIndex,
                    groupPath,
                    listHeight,
                    GUILayout.ExpandWidth(true));

                GUILayout.Space(6f);

                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox, GUILayout.ExpandWidth(true)))
                {
                    DrawGeneratedPreviewActionButtons(groupPath, compactHorizontal: true);
                }
            }
        }

        private void DrawGeneratedPreviewPartList(
            int startIndex,
            int endIndex,
            string groupPath,
            float listHeight,
            params GUILayoutOption[] layoutOptions)
        {
            using (new EditorGUILayout.VerticalScope(layoutOptions))
            {
                if (!generatedGroupScrollPositions.TryGetValue(groupPath, out Vector2 groupScroll))
                {
                    groupScroll = Vector2.zero;
                }

                groupScroll = EditorGUILayout.BeginScrollView(
                    groupScroll,
                    GUILayout.Height(listHeight),
                    GUILayout.ExpandWidth(true));

                for (int i = startIndex; i < endIndex; i++)
                {
                    GeneratedPrefabEntry entry = generatedPrefabEntries[i];
                    string shortName = GetGeneratedPartDisplayName(entry);
                    bool isActive = string.Equals(
                        activeGeneratedPrefabPath,
                        entry.AssetPath,
                        StringComparison.Ordinal);
                    bool isSelected = selectedGeneratedPrefabPaths.Contains(entry.AssetPath);

                    Color previousBackground = GUI.backgroundColor;
                    if (isSelected)
                    {
                        GUI.backgroundColor = isActive
                            ? (EditorGUIUtility.isProSkin
                                ? new Color(0.22f, 0.52f, 0.95f, 1f)
                                : new Color(0.35f, 0.67f, 1f, 1f))
                            : (EditorGUIUtility.isProSkin
                                ? new Color(0.24f, 0.42f, 0.68f, 1f)
                                : new Color(0.55f, 0.75f, 0.95f, 1f));
                    }

                    bool clicked = GUILayout.Button(
                        shortName,
                        EditorStyles.miniButton,
                        GUILayout.Height(24f),
                        GUILayout.ExpandWidth(true));
                    GUI.backgroundColor = previousBackground;

                    if (clicked)
                    {
                        HandleGeneratedPreviewPartSelection(entry, generatedIntegrationMode);
                    }
                }

                EditorGUILayout.EndScrollView();
                generatedGroupScrollPositions[groupPath] = groupScroll;
            }
        }

        private void DrawGeneratedPreviewActionPanel(
            string groupPath,
            float listHeight,
            params GUILayoutOption[] layoutOptions)
        {
            using (new EditorGUILayout.VerticalScope(
                       EditorStyles.helpBox,
                       CombineLayoutOptions(layoutOptions, GUILayout.Height(listHeight))))
            {
                DrawGeneratedPreviewActionButtons(groupPath, compactHorizontal: false);
                GUILayout.FlexibleSpace();
            }
        }

        private static GUILayoutOption[] CombineLayoutOptions(
            GUILayoutOption[] first,
            GUILayoutOption last)
        {
            int firstLength = first == null ? 0 : first.Length;
            GUILayoutOption[] result = new GUILayoutOption[firstLength + 1];
            if (firstLength > 0)
            {
                Array.Copy(first, result, firstLength);
            }

            result[firstLength] = last;
            return result;
        }

        private void DrawGeneratedPreviewActionButtons(
            string groupPath,
            bool compactHorizontal)
        {
            GeneratedPrefabEntry activeEntry = FindGeneratedPrefabEntry(activeGeneratedPrefabPath);
            bool activeBelongsToGroup = activeEntry != null &&
                                        string.Equals(
                                            activeEntry.GroupPath,
                                            groupPath,
                                            StringComparison.Ordinal);
            GameObject previewObject = activeBelongsToGroup && generatedPreviewVisible
                ? GetGeneratedPreviewObject(activeEntry.AssetPath)
                : null;
            generatedPreviewObject = previewObject;

            if (compactHorizontal)
            {
                bool requestedPreviewVisible = GUILayout.Toggle(
                    generatedPreviewVisible,
                    "プレビュー表示",
                    GUI.skin.button,
                    GUILayout.Height(30f),
                    GUILayout.ExpandWidth(true));
                if (requestedPreviewVisible != generatedPreviewVisible)
                {
                    SetGeneratedPreviewVisible(requestedPreviewVisible);
                }

                bool requestedIntegrationMode = GUILayout.Toggle(
                    generatedIntegrationMode,
                    "統合モード",
                    GUI.skin.button,
                    GUILayout.Height(30f),
                    GUILayout.ExpandWidth(true));
                if (requestedIntegrationMode != generatedIntegrationMode)
                {
                    SetGeneratedIntegrationMode(requestedIntegrationMode);
                }

                if (generatedIntegrationMode &&
                    GUILayout.Button(
                        "全選択",
                        GUILayout.Height(30f),
                        GUILayout.ExpandWidth(true)))
                {
                    SelectAllGeneratedPartsInGroup(groupPath);
                }

                using (new EditorGUI.DisabledScope(!generatedPreviewVisible || !CanSoloGeneratedPreviewPart(previewObject)))
                {
                    bool partDisplayActive = activeBelongsToGroup &&
                                             IsGeneratedPreviewSoloActive(
                                                 previewObject,
                                                 activeEntry.AssetPath);
                    bool requestedPartDisplay = GUILayout.Toggle(
                        partDisplayActive,
                        "パーツ表示",
                        GUI.skin.button,
                        GUILayout.Height(30f),
                        GUILayout.ExpandWidth(true));

                    if (requestedPartDisplay != partDisplayActive)
                    {
                        if (requestedPartDisplay)
                        {
                            EnterGeneratedPreviewIsolation(previewObject, activeEntry.AssetPath);
                        }
                        else
                        {
                            ExitGeneratedPreviewIsolation(true);
                        }
                    }
                }

                return;
            }

            bool requestedPreview = GUILayout.Toggle(
                generatedPreviewVisible,
                "プレビュー表示",
                GUI.skin.button,
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true));
            if (requestedPreview != generatedPreviewVisible)
            {
                SetGeneratedPreviewVisible(requestedPreview);
            }

            GUILayout.Space(5f);

            bool requestedIntegration = GUILayout.Toggle(
                generatedIntegrationMode,
                "統合モード",
                GUI.skin.button,
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true));
            if (requestedIntegration != generatedIntegrationMode)
            {
                SetGeneratedIntegrationMode(requestedIntegration);
            }

            if (generatedIntegrationMode)
            {
                GUILayout.Space(5f);
                if (GUILayout.Button(
                        "全選択",
                        GUILayout.Height(30f),
                        GUILayout.ExpandWidth(true)))
                {
                    SelectAllGeneratedPartsInGroup(groupPath);
                }
            }

            GUILayout.Space(5f);

            using (new EditorGUI.DisabledScope(!generatedPreviewVisible || !CanSoloGeneratedPreviewPart(previewObject)))
            {
                bool partDisplayActive = activeBelongsToGroup &&
                                         IsGeneratedPreviewSoloActive(
                                             previewObject,
                                             activeEntry.AssetPath);
                bool requestedPartDisplay = GUILayout.Toggle(
                    partDisplayActive,
                    "パーツ表示",
                    GUI.skin.button,
                    GUILayout.Height(30f),
                    GUILayout.ExpandWidth(true));

                if (requestedPartDisplay != partDisplayActive)
                {
                    if (requestedPartDisplay)
                    {
                        EnterGeneratedPreviewIsolation(previewObject, activeEntry.AssetPath);
                    }
                    else
                    {
                        ExitGeneratedPreviewIsolation(true);
                    }
                }
            }
        }

        private void SelectAllGeneratedPartsInGroup(string groupPath)
        {
            if (!generatedIntegrationMode || string.IsNullOrEmpty(groupPath))
            {
                return;
            }

            if (SceneVisibilityManager.instance.IsCurrentStageIsolated())
            {
                ExitGeneratedPreviewIsolation(true);
            }

            string lastSelectedPath = string.Empty;
            for (int i = 0; i < generatedPrefabEntries.Count; i++)
            {
                GeneratedPrefabEntry entry = generatedPrefabEntries[i];
                if (entry == null ||
                    !string.Equals(entry.GroupPath, groupPath, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!selectedGeneratedPrefabPaths.Contains(entry.AssetPath))
                {
                    selectedGeneratedPrefabPaths.Add(entry.AssetPath);
                }

                lastSelectedPath = entry.AssetPath;
                if (generatedPreviewVisible)
                {
                    EnsureGeneratedPreviewObject(entry);
                }
            }

            if (!string.IsNullOrEmpty(lastSelectedPath))
            {
                activeGeneratedPrefabPath = lastSelectedPath;
                generatedPreviewObject = generatedPreviewVisible
                    ? GetGeneratedPreviewObject(lastSelectedPath)
                    : null;
            }

            SceneView.RepaintAll();
            Repaint();
        }

        private void HandleGeneratedPreviewPartSelection(
            GeneratedPrefabEntry entry,
            bool additiveSelection)
        {
            if (entry == null)
            {
                return;
            }

            bool currentlySelected = selectedGeneratedPrefabPaths.Contains(entry.AssetPath);
            bool sameActive = string.Equals(
                activeGeneratedPrefabPath,
                entry.AssetPath,
                StringComparison.Ordinal);

            if (additiveSelection)
            {
                if (SceneVisibilityManager.instance.IsCurrentStageIsolated())
                {
                    ExitGeneratedPreviewIsolation(true);
                }

                if (currentlySelected)
                {
                    selectedGeneratedPrefabPaths.Remove(entry.AssetPath);
                    RemoveGeneratedPreviewObject(entry.AssetPath);

                    if (sameActive)
                    {
                        activeGeneratedPrefabPath = selectedGeneratedPrefabPaths.Count > 0
                            ? selectedGeneratedPrefabPaths[selectedGeneratedPrefabPaths.Count - 1]
                            : string.Empty;
                        generatedPreviewObject = generatedPreviewVisible
                            ? GetGeneratedPreviewObject(activeGeneratedPrefabPath)
                            : null;
                    }

                    SceneView.RepaintAll();
                    Repaint();
                    return;
                }

                selectedGeneratedPrefabPaths.Add(entry.AssetPath);
                activeGeneratedPrefabPath = entry.AssetPath;
                generatedPreviewObject = generatedPreviewVisible
                    ? EnsureGeneratedPreviewObject(entry)
                    : null;
                if (generatedPreviewVisible && generatedPreviewObject == null)
                {
                    selectedGeneratedPrefabPaths.Remove(entry.AssetPath);
                    activeGeneratedPrefabPath = selectedGeneratedPrefabPaths.Count > 0
                        ? selectedGeneratedPrefabPaths[selectedGeneratedPrefabPaths.Count - 1]
                        : string.Empty;
                    generatedPreviewObject = GetGeneratedPreviewObject(activeGeneratedPrefabPath);
                }

                SceneView.RepaintAll();
                Repaint();
                return;
            }

            if (currentlySelected && sameActive && selectedGeneratedPrefabPaths.Count == 1)
            {
                if (SceneVisibilityManager.instance.IsCurrentStageIsolated())
                {
                    ExitGeneratedPreviewIsolation(true);
                }
                selectedGeneratedPrefabPaths.Clear();
                activeGeneratedPrefabPath = string.Empty;
                RemoveGeneratedPreviewObject(entry.AssetPath);
                generatedPreviewObject = null;
                SceneView.RepaintAll();
                Repaint();
                return;
            }

            if (SceneVisibilityManager.instance.IsCurrentStageIsolated())
            {
                ExitGeneratedPreviewIsolation(true);
            }

            List<string> previousPaths = new List<string>(selectedGeneratedPrefabPaths);
            selectedGeneratedPrefabPaths.Clear();
            selectedGeneratedPrefabPaths.Add(entry.AssetPath);
            activeGeneratedPrefabPath = entry.AssetPath;

            for (int i = 0; i < previousPaths.Count; i++)
            {
                if (!string.Equals(previousPaths[i], entry.AssetPath, StringComparison.Ordinal))
                {
                    RemoveGeneratedPreviewObject(previousPaths[i]);
                }
            }

            generatedPreviewObject = generatedPreviewVisible
                ? EnsureGeneratedPreviewObject(entry)
                : null;

            if (generatedPreviewVisible && generatedPreviewObject == null)
            {
                selectedGeneratedPrefabPaths.Clear();
                activeGeneratedPrefabPath = string.Empty;
            }

            SceneView.RepaintAll();
            Repaint();
        }

        private GameObject GetGeneratedPreviewObject(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return null;
            }

            if (!generatedPreviewObjects.TryGetValue(assetPath, out GameObject previewObject) ||
                previewObject == null)
            {
                generatedPreviewObjects.Remove(assetPath);
                return null;
            }

            return previewObject;
        }

        private GameObject EnsureGeneratedPreviewObject(GeneratedPrefabEntry entry)
        {
            if (entry == null)
            {
                return null;
            }

            GameObject existing = GetGeneratedPreviewObject(entry.AssetPath);
            if (existing != null)
            {
                return existing;
            }

            GameObject previewObject = CreateGeneratedPreviewObject(entry);
            if (previewObject != null)
            {
                generatedPreviewObjects[entry.AssetPath] = previewObject;
            }

            return previewObject;
        }

        private void RemoveGeneratedPreviewObject(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            if (generatedPreviewObjects.TryGetValue(assetPath, out GameObject previewObject))
            {
                generatedPreviewObjects.Remove(assetPath);
                if (previewObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(previewObject);
                }
            }
        }

        private void ShowAllSelectedGeneratedPreviewParts()
        {
            if (selectedGeneratedPrefabPaths.Count == 0)
            {
                return;
            }

            ExitGeneratedPreviewIsolation(false);

            for (int i = selectedGeneratedPrefabPaths.Count - 1; i >= 0; i--)
            {
                string assetPath = selectedGeneratedPrefabPaths[i];
                GeneratedPrefabEntry entry = FindGeneratedPrefabEntry(assetPath);
                if (entry == null)
                {
                    selectedGeneratedPrefabPaths.RemoveAt(i);
                    RemoveGeneratedPreviewObject(assetPath);
                    continue;
                }

                EnsureGeneratedPreviewObject(entry);
            }

            if (selectedGeneratedPrefabPaths.Count == 0)
            {
                activeGeneratedPrefabPath = string.Empty;
                generatedPreviewObject = null;
                Repaint();
                return;
            }

            if (string.IsNullOrEmpty(activeGeneratedPrefabPath) ||
                !selectedGeneratedPrefabPaths.Contains(activeGeneratedPrefabPath))
            {
                activeGeneratedPrefabPath = selectedGeneratedPrefabPaths[selectedGeneratedPrefabPaths.Count - 1];
            }

            generatedPreviewObject = GetGeneratedPreviewObject(activeGeneratedPrefabPath);
            FrameAllSelectedGeneratedPreviewParts();
            SceneView.RepaintAll();
            Repaint();
        }

        private void FrameAllSelectedGeneratedPreviewParts()
        {
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                return;
            }

            Bounds combinedBounds = default(Bounds);
            bool hasBounds = false;

            for (int i = 0; i < selectedGeneratedPrefabPaths.Count; i++)
            {
                GameObject previewObject = GetGeneratedPreviewObject(selectedGeneratedPrefabPaths[i]);
                if (previewObject == null ||
                    !TryGetAccuratePreviewBounds(previewObject, out Bounds partBounds))
                {
                    continue;
                }

                if (!hasBounds)
                {
                    combinedBounds = partBounds;
                    hasBounds = true;
                }
                else
                {
                    combinedBounds.Encapsulate(partBounds);
                }
            }

            if (!hasBounds)
            {
                return;
            }

            float longestSide = Mathf.Max(
                combinedBounds.size.x,
                Mathf.Max(combinedBounds.size.y, combinedBounds.size.z));
            float margin = Mathf.Max(longestSide * 0.06f, 0.0005f);
            combinedBounds.Expand(margin);
            sceneView.Frame(combinedBounds, false);
            sceneView.Repaint();
        }

        private void SetGeneratedPreviewVisible(bool visible)
        {
            if (generatedPreviewVisible == visible)
            {
                return;
            }

            if (!visible)
            {
                if (SceneVisibilityManager.instance.IsCurrentStageIsolated())
                {
                    ExitGeneratedPreviewIsolation(true);
                }

                foreach (KeyValuePair<string, GameObject> pair in generatedPreviewObjects)
                {
                    if (pair.Value != null)
                    {
                        UnityEngine.Object.DestroyImmediate(pair.Value);
                    }
                }
                generatedPreviewObjects.Clear();
                generatedPreviewObject = null;
                generatedPreviewVisible = false;
            }
            else
            {
                generatedPreviewVisible = true;
                for (int i = selectedGeneratedPrefabPaths.Count - 1; i >= 0; i--)
                {
                    GeneratedPrefabEntry entry = FindGeneratedPrefabEntry(selectedGeneratedPrefabPaths[i]);
                    if (entry == null)
                    {
                        selectedGeneratedPrefabPaths.RemoveAt(i);
                        continue;
                    }

                    EnsureGeneratedPreviewObject(entry);
                }

                generatedPreviewObject = GetGeneratedPreviewObject(activeGeneratedPrefabPath);
            }

            SceneView.RepaintAll();
            Repaint();
        }

        private void SetGeneratedIntegrationMode(bool enabled)
        {
            generatedIntegrationMode = enabled;
            if (enabled)
            {
                Repaint();
                return;
            }

            if (selectedGeneratedPrefabPaths.Count > 1)
            {
                string keepPath = !string.IsNullOrEmpty(activeGeneratedPrefabPath) &&
                                  selectedGeneratedPrefabPaths.Contains(activeGeneratedPrefabPath)
                    ? activeGeneratedPrefabPath
                    : selectedGeneratedPrefabPaths[selectedGeneratedPrefabPaths.Count - 1];

                List<string> removePaths = new List<string>();
                for (int i = 0; i < selectedGeneratedPrefabPaths.Count; i++)
                {
                    if (!string.Equals(selectedGeneratedPrefabPaths[i], keepPath, StringComparison.Ordinal))
                    {
                        removePaths.Add(selectedGeneratedPrefabPaths[i]);
                    }
                }

                for (int i = 0; i < removePaths.Count; i++)
                {
                    RemoveGeneratedPreviewObject(removePaths[i]);
                }

                selectedGeneratedPrefabPaths.Clear();
                selectedGeneratedPrefabPaths.Add(keepPath);
                activeGeneratedPrefabPath = keepPath;
                generatedPreviewObject = generatedPreviewVisible
                    ? GetGeneratedPreviewObject(keepPath)
                    : null;
            }

            Repaint();
        }

        private void CaptureGeneratedPreviewSceneViewState()
        {
            if (generatedSceneViewStateCaptured)
            {
                return;
            }

            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                return;
            }

            generatedSceneViewSavedPivot = sceneView.pivot;
            generatedSceneViewSavedRotation = sceneView.rotation;
            generatedSceneViewSavedSize = sceneView.size;
            generatedSceneViewSavedOrthographic = sceneView.orthographic;
            generatedSceneViewStateCaptured = true;
        }

        private void RestoreGeneratedPreviewSceneViewState()
        {
            if (!generatedSceneViewStateCaptured)
            {
                return;
            }

            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
            {
                sceneView.pivot = generatedSceneViewSavedPivot;
                sceneView.rotation = generatedSceneViewSavedRotation;
                sceneView.size = generatedSceneViewSavedSize;
                sceneView.orthographic = generatedSceneViewSavedOrthographic;
                sceneView.Repaint();
            }

            generatedSceneViewStateCaptured = false;
        }

        private GeneratedPrefabEntry FindGeneratedPrefabEntry(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return null;
            }

            for (int i = 0; i < generatedPrefabEntries.Count; i++)
            {
                if (string.Equals(
                        generatedPrefabEntries[i].AssetPath,
                        assetPath,
                        StringComparison.Ordinal))
                {
                    return generatedPrefabEntries[i];
                }
            }

            return null;
        }

        private static string GetGeneratedGroupDisplayName(string groupName)
        {
            if (string.IsNullOrEmpty(groupName))
            {
                return "Generated";
            }

            // Old generated data remains readable without renaming assets on disk.
            if (groupName.EndsWith("_Separated", StringComparison.OrdinalIgnoreCase))
            {
                return groupName.Substring(0, groupName.Length - "_Separated".Length) + "_nekoneko";
            }

            return groupName;
        }

        private static string GetGeneratedPartDisplayName(GeneratedPrefabEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.AssetName))
            {
                return "Part";
            }

            string groupName = entry.GroupName ?? string.Empty;
            string sourceName = groupName;
            if (sourceName.EndsWith("_nekoneko", StringComparison.OrdinalIgnoreCase))
            {
                sourceName = sourceName.Substring(0, sourceName.Length - "_nekoneko".Length);
            }
            else if (sourceName.EndsWith("_Separated", StringComparison.OrdinalIgnoreCase))
            {
                sourceName = sourceName.Substring(0, sourceName.Length - "_Separated".Length);
            }

            string prefix = sourceName + "_";
            if (!string.IsNullOrEmpty(sourceName) &&
                entry.AssetName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return entry.AssetName.Substring(prefix.Length);
            }

            return entry.AssetName;
        }

        private void RefreshGeneratedPreview()
        {
            generatedPrefabEntries.Clear();

            if (!AssetDatabase.IsValidFolder(DefaultOutputFolder))
            {
                selectedGeneratedPrefabPaths.Clear();
                EndGeneratedPreviewDisplay(true, false);
                Repaint();
                return;
            }

            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { DefaultOutputFolder });
            for (int i = 0; i < guids.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]).Replace('\\', '/');
                if (string.IsNullOrEmpty(assetPath))
                {
                    continue;
                }

                if (assetPath.StartsWith(CombinedPrefabsFolder + "/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string prefabDirectory = Path.GetDirectoryName(assetPath);
                if (string.IsNullOrEmpty(prefabDirectory))
                {
                    continue;
                }

                prefabDirectory = prefabDirectory.Replace('\\', '/');
                string groupPath = prefabDirectory;
                if (string.Equals(Path.GetFileName(prefabDirectory), "Prefabs", StringComparison.OrdinalIgnoreCase))
                {
                    string parent = Path.GetDirectoryName(prefabDirectory);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        groupPath = parent.Replace('\\', '/');
                    }
                }

                string groupName = Path.GetFileName(groupPath);
                if (string.IsNullOrEmpty(groupName))
                {
                    groupName = "Generated";
                }

                generatedPrefabEntries.Add(new GeneratedPrefabEntry(
                    assetPath,
                    groupPath,
                    groupName,
                    Path.GetFileNameWithoutExtension(assetPath)));
            }

            generatedPrefabEntries.Sort(delegate(GeneratedPrefabEntry left, GeneratedPrefabEntry right)
            {
                int groupCompare = string.Compare(
                    left.GroupPath,
                    right.GroupPath,
                    StringComparison.OrdinalIgnoreCase);
                if (groupCompare != 0)
                {
                    return groupCompare;
                }

                return string.Compare(left.AssetName, right.AssetName, StringComparison.OrdinalIgnoreCase);
            });

            HashSet<string> validPaths = new HashSet<string>();
            for (int i = 0; i < generatedPrefabEntries.Count; i++)
            {
                validPaths.Add(generatedPrefabEntries[i].AssetPath);
            }

            selectedGeneratedPrefabPaths.RemoveAll(delegate(string path)
            {
                return !validPaths.Contains(path);
            });

            List<string> invalidPreviewPaths = new List<string>();
            foreach (KeyValuePair<string, GameObject> pair in generatedPreviewObjects)
            {
                if (!validPaths.Contains(pair.Key))
                {
                    invalidPreviewPaths.Add(pair.Key);
                }
            }
            for (int i = 0; i < invalidPreviewPaths.Count; i++)
            {
                RemoveGeneratedPreviewObject(invalidPreviewPaths[i]);
            }

            if (!string.IsNullOrEmpty(activeGeneratedPrefabPath) &&
                !validPaths.Contains(activeGeneratedPrefabPath))
            {
                EndGeneratedPreviewDisplay(true, false);
            }

            if (!string.IsNullOrEmpty(soloGeneratedPrefabPath) &&
                !validPaths.Contains(soloGeneratedPrefabPath))
            {
                ExitGeneratedPreviewIsolation(false);
            }

            Repaint();
        }

        private void DrawCombinedPrefabControls()
        {
            if (!generatedIntegrationMode)
            {
                return;
            }

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("統合Prefab作成", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "選択中のパーツ: " + selectedGeneratedPrefabPaths.Count,
                    EditorStyles.miniBoldLabel);

                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField("作成するPrefab名", EditorStyles.miniBoldLabel);
                combinedPrefabName = EditorGUILayout.TextField(combinedPrefabName);
                EditorGUILayout.LabelField(
                    "↑ ここで統合後のPrefab名を変更できます。",
                    EditorStyles.miniLabel);

                Type modularAvatarType = FindModularAvatarMergeArmatureType();
                if (modularAvatarType != null)
                {
                    createModularAvatarMergeArmature = EditorGUILayout.ToggleLeft(
                        "Modular Avatar対応（MA Merge Armatureを追加）",
                        createModularAvatarMergeArmature);
                }
                else
                {
                    EditorGUILayout.LabelField(
                        "Modular Avatar: 未導入（通常Prefabを作成）",
                        EditorStyles.miniLabel);
                }

                if (selectedGeneratedPrefabPaths.Count < 2)
                {
                    EditorGUILayout.LabelField(
                        "統合するパーツを2つ以上選択してください。",
                        EditorStyles.miniLabel);
                }

                using (new EditorGUI.DisabledScope(selectedGeneratedPrefabPaths.Count < 2))
                {
                    if (GUILayout.Button("この名前で統合Prefabを作成", GUILayout.Height(34f)))
                    {
                        CreateCombinedPrefabFromSelection();
                    }
                }
            }
        }

        private void CreateCombinedPrefabFromSelection()
        {
            List<GeneratedPrefabEntry> entries = new List<GeneratedPrefabEntry>();
            for (int i = 0; i < selectedGeneratedPrefabPaths.Count; i++)
            {
                GeneratedPrefabEntry entry = FindGeneratedPrefabEntry(selectedGeneratedPrefabPaths[i]);
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }

            if (entries.Count < 2)
            {
                EditorUtility.DisplayDialog(
                    "NN - Object Bunkai Tool",
                    "統合するパーツを2つ以上選択してください。",
                    "OK");
                return;
            }

            string requestedName = string.IsNullOrWhiteSpace(combinedPrefabName)
                ? "CombinedPrefab"
                : combinedPrefabName.Trim();
            requestedName = SanitizeFileName(requestedName);

            EnsureFolderExists(DefaultOutputFolder);
            EnsureFolderExists(CombinedPrefabsFolder);

            GameObject combinedRoot = null;
            GameObject baseAsset = null;
            string savedPath = string.Empty;

            try
            {
                // SkinnedMeshRendererを含むPrefabを土台にすると、Armature・Bone・PhysBoneを
                // 1セットだけ保持したまま複数パーツを同じ骨へ再接続できます。
                for (int i = 0; i < entries.Count; i++)
                {
                    GameObject candidateAsset = AssetDatabase.LoadAssetAtPath<GameObject>(entries[i].AssetPath);
                    if (candidateAsset == null)
                    {
                        continue;
                    }

                    if (candidateAsset.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
                    {
                        baseAsset = candidateAsset;
                        break;
                    }
                }

                if (baseAsset != null)
                {
                    combinedRoot = UnityEngine.Object.Instantiate(baseAsset);
                    combinedRoot.hideFlags = HideFlags.HideAndDontSave;
                    combinedRoot.name = requestedName;
                    combinedRoot.SetActive(true);
                    combinedRoot.transform.SetParent(null, false);
                    RemoveRenderersForCombinedTemplate(combinedRoot);
                }
                else
                {
                    combinedRoot = new GameObject(requestedName);
                    combinedRoot.hideFlags = HideFlags.HideAndDontSave;
                }

                Dictionary<string, int> combinedPartGroupCounts = BuildCombinedPartGroupCounts(entries);

                for (int i = 0; i < entries.Count; i++)
                {
                    GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(entries[i].AssetPath);
                    if (prefabAsset == null)
                    {
                        throw new InvalidOperationException(
                            "生成Prefabを読み込めませんでした: " + entries[i].AssetPath);
                    }

                    GameObject donor = UnityEngine.Object.Instantiate(prefabAsset);
                    donor.hideFlags = HideFlags.HideAndDontSave;
                    donor.SetActive(true);
                    donor.transform.SetParent(null, false);

                    try
                    {
                        string partGroupName = GetCombinedPartGroupBaseName(entries[i].AssetName);
                        bool groupWithSameSource =
                            !string.IsNullOrEmpty(partGroupName) &&
                            combinedPartGroupCounts.TryGetValue(partGroupName, out int sameSourceCount) &&
                            sameSourceCount >= 2;

                        if (!AppendPrefabRenderersToCombinedRoot(
                                donor,
                                combinedRoot,
                                entries[i].AssetName,
                                groupWithSameSource ? partGroupName : string.Empty,
                                baseAsset != null,
                                out string appendError))
                        {
                            throw new InvalidOperationException(appendError);
                        }

                        // 個別Part Prefabごとに保持されているVRCPhysBone / Colliderも統合します。
                        // これを行わないと、土台に選ばれた1パーツ分のDynamicsしか残らず、
                        // 異なるPhysBoneを持つ複数パーツを統合した際に片方が消えてしまいます。
                        if (baseAsset == null || prefabAsset != baseAsset)
                        {
                            MergeVrcDynamicsFromDonor(donor, combinedRoot);
                        }
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(donor);
                    }
                }

                // 統合Prefabは、選択したRendererが実際に使用するBoneと、
                // その動作に必要なPhysBone / Colliderだけを残します。
                // Armatureルート自体と必要な親階層は維持するため、MA Merge Armatureで使用できます。
                PruneCombinedPrefabRig(combinedRoot);

                combinedRoot.hideFlags = HideFlags.None;
                ApplyHideFlagsRecursively(combinedRoot.transform, HideFlags.None);

                string modularAvatarStatus = string.Empty;
                Type modularAvatarType = FindModularAvatarMergeArmatureType();
                if (createModularAvatarMergeArmature && modularAvatarType != null)
                {
                    if (TryAddModularAvatarMergeArmature(combinedRoot, modularAvatarType, out string maError))
                    {
                        modularAvatarStatus = "\nModular Avatar: MA Merge Armatureを設定しました。";
                    }
                    else
                    {
                        modularAvatarStatus = "\nModular Avatar: 設定をスキップしました（" + maError + "）";
                    }
                }

                string targetPath = AssetDatabase.GenerateUniqueAssetPath(
                    CombinedPrefabsFolder + "/" + requestedName + ".prefab");
                PrefabUtility.SaveAsPrefabAsset(combinedRoot, targetPath, out bool success);
                if (!success)
                {
                    throw new IOException("統合Prefabを保存できませんでした。");
                }

                savedPath = targetPath;
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                GameObject savedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(savedPath);
                if (savedPrefab != null)
                {
                    Selection.activeObject = savedPrefab;
                    EditorGUIUtility.PingObject(savedPrefab);
                }

                // 統合完了後は作業状態をリセットする。
                if (SceneVisibilityManager.instance.IsCurrentStageIsolated())
                {
                    ExitGeneratedPreviewIsolation(false);
                }
                SetGeneratedPreviewVisible(false);
                generatedIntegrationMode = false;
                selectedGeneratedPrefabPaths.Clear();
                activeGeneratedPrefabPath = string.Empty;
                soloGeneratedPrefabPath = string.Empty;
                generatedPreviewObject = null;
                Repaint();

                EditorUtility.DisplayDialog(
                    "NN - Object Bunkai Tool",
                    "統合Prefabを作成しました。\n\n" + savedPath + modularAvatarStatus,
                    "OK");
            }
            catch (Exception ex)
            {
                string message = ex.Message;
                if (message.IndexOf("Armature", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    message = "Armatureが一致しません。\n選択したパーツのBone構造を対応付けできないため統合できません。";
                }

                EditorUtility.DisplayDialog(
                    "統合Prefabを作成できませんでした",
                    message,
                    "OK");
            }
            finally
            {
                if (combinedRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(combinedRoot);
                }
            }
        }

        private static Type FindModularAvatarMergeArmatureType()
        {
            foreach (Type type in TypeCache.GetTypesDerivedFrom<MonoBehaviour>())
            {
                if (type != null &&
                    string.Equals(
                        type.FullName,
                        "nadena.dev.modular_avatar.core.ModularAvatarMergeArmature",
                        StringComparison.Ordinal))
                {
                    return type;
                }
            }

            return null;
        }

        private static bool TryAddModularAvatarMergeArmature(
            GameObject combinedRoot,
            Type modularAvatarMergeArmatureType,
            out string error)
        {
            error = string.Empty;
            if (combinedRoot == null || modularAvatarMergeArmatureType == null)
            {
                error = "Modular Avatarが見つかりません";
                return false;
            }

            Transform armatureRoot = FindArmatureRootForModularAvatar(combinedRoot);
            if (armatureRoot == null)
            {
                error = "Armatureを特定できません";
                return false;
            }

            string mergeTargetPath = AnimationUtility.CalculateTransformPath(
                armatureRoot,
                combinedRoot.transform);
            if (string.IsNullOrEmpty(mergeTargetPath))
            {
                error = "Armatureの参照パスを作成できません";
                return false;
            }

            Component mergeArmature = armatureRoot.GetComponent(modularAvatarMergeArmatureType);
            if (mergeArmature == null)
            {
                mergeArmature = armatureRoot.gameObject.AddComponent(modularAvatarMergeArmatureType);
            }

            if (mergeArmature == null)
            {
                error = "MA Merge Armatureを追加できません";
                return false;
            }

            SerializedObject serializedObject = new SerializedObject(mergeArmature);
            SerializedProperty mergeTarget = serializedObject.FindProperty("mergeTarget");
            if (mergeTarget == null)
            {
                UnityEngine.Object.DestroyImmediate(mergeArmature);
                error = "MA Merge ArmatureのMerge Targetを設定できません";
                return false;
            }

            SerializedProperty referencePath = mergeTarget.FindPropertyRelative("referencePath");
            SerializedProperty targetObject = mergeTarget.FindPropertyRelative("targetObject");
            if (referencePath == null)
            {
                UnityEngine.Object.DestroyImmediate(mergeArmature);
                error = "MA Merge Armatureの参照パスを設定できません";
                return false;
            }

            // AvatarObjectReferenceはPrefab外のGameObjectを直接参照させず、
            // アバタールートからのパスだけを保存します。
            // 統合Prefabを元アバター直下へ配置した時、同じパスのArmatureへ解決されます。
            referencePath.stringValue = mergeTargetPath;
            if (targetObject != null)
            {
                targetObject.objectReferenceValue = null;
            }

            SerializedProperty prefix = serializedObject.FindProperty("prefix");
            SerializedProperty suffix = serializedObject.FindProperty("suffix");
            if (prefix != null) prefix.stringValue = string.Empty;
            if (suffix != null) suffix.stringValue = string.Empty;

            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(mergeArmature);
            return true;
        }

        private static Transform FindArmatureRootForModularAvatar(GameObject combinedRoot)
        {
            if (combinedRoot == null)
            {
                return null;
            }

            SkinnedMeshRenderer[] renderers =
                combinedRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers == null || renderers.Length == 0)
            {
                return null;
            }

            Transform seed = null;
            for (int i = 0; i < renderers.Length && seed == null; i++)
            {
                if (renderers[i] == null)
                {
                    continue;
                }

                seed = renderers[i].rootBone;
                if (seed == null && renderers[i].bones != null)
                {
                    for (int boneIndex = 0; boneIndex < renderers[i].bones.Length; boneIndex++)
                    {
                        if (renderers[i].bones[boneIndex] != null)
                        {
                            seed = renderers[i].bones[boneIndex];
                            break;
                        }
                    }
                }
            }

            if (seed == null || !seed.IsChildOf(combinedRoot.transform))
            {
                return null;
            }

            Transform namedCandidate = null;
            Transform cursor = seed;
            while (cursor != null && cursor != combinedRoot.transform)
            {
                string lower = cursor.name.ToLowerInvariant();
                if (lower.Contains("armature") ||
                    lower.Contains("amature") ||
                    lower.Contains("skeleton") ||
                    lower == "rig")
                {
                    namedCandidate = cursor;
                }
                cursor = cursor.parent;
            }

            Transform armatureRoot = namedCandidate;
            if (armatureRoot == null)
            {
                SkinnedMeshRenderer firstRenderer = renderers[0];
                if (firstRenderer != null &&
                    firstRenderer.rootBone != null &&
                    firstRenderer.rootBone.parent != null &&
                    firstRenderer.rootBone.parent != combinedRoot.transform)
                {
                    armatureRoot = firstRenderer.rootBone.parent;
                }
            }

            if (armatureRoot == null)
            {
                armatureRoot = seed;
                while (armatureRoot.parent != null &&
                       armatureRoot.parent != combinedRoot.transform)
                {
                    armatureRoot = armatureRoot.parent;
                }
            }

            if (armatureRoot == null || armatureRoot == combinedRoot.transform)
            {
                return null;
            }

            // 選択したSkinnedMeshRendererが別々のArmatureを参照している場合は、
            // MA Merge Armatureを誤設定しないように中止します。
            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                SkinnedMeshRenderer renderer = renderers[rendererIndex];
                if (renderer == null || renderer.bones == null)
                {
                    continue;
                }

                for (int boneIndex = 0; boneIndex < renderer.bones.Length; boneIndex++)
                {
                    Transform bone = renderer.bones[boneIndex];
                    if (bone != null && bone != armatureRoot && !bone.IsChildOf(armatureRoot))
                    {
                        return null;
                    }
                }
            }

            return armatureRoot;
        }

        private static void PruneCombinedPrefabRig(GameObject combinedRoot)
        {
            PrunePrefabRigInternal(combinedRoot, false, null, null);
        }

        private static void PruneStandalonePartPrefabRig(
            GameObject prefabRoot,
            SkinnedMeshRenderer focusedRenderer,
            HashSet<int> explicitUsedBoneIndices)
        {
            PrunePrefabRigInternal(
                prefabRoot,
                true,
                focusedRenderer,
                explicitUsedBoneIndices);
        }

        private static void PrunePrefabRigInternal(
            GameObject combinedRoot,
            bool pruneWholePrefab,
            SkinnedMeshRenderer focusedRenderer,
            HashSet<int> explicitUsedBoneIndices)
        {
            if (combinedRoot == null)
            {
                return;
            }

            Transform armatureRoot = FindArmatureRootForModularAvatar(combinedRoot);
            if (armatureRoot == null)
            {
                if (!pruneWholePrefab)
                {
                    // 統合PrefabではArmatureを特定できない状態で破壊的整理をしません。
                    return;
                }

                // 個別Part PrefabはRenderer/Bone参照を基準にホワイトリスト整理するため、
                // Armature名を特定できないアバターでもルートを境界として整理を続行します。
                armatureRoot = combinedRoot.transform;
            }

            HashSet<Transform> requiredTransforms = new HashSet<Transform>();
            HashSet<Transform> usedSkinBones = new HashSet<Transform>();
            HashSet<Component> keptPhysBones = new HashSet<Component>();
            HashSet<Component> keptPhysBoneColliders = new HashSet<Component>();

            AddTransformAndAncestors(requiredTransforms, armatureRoot, combinedRoot.transform);

            Renderer[] renderers = combinedRoot.GetComponentsInChildren<Renderer>(true);
            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                Renderer renderer = renderers[rendererIndex];
                if (renderer == null)
                {
                    continue;
                }

                AddTransformAndAncestors(
                    requiredTransforms,
                    renderer.transform,
                    combinedRoot.transform);

                SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
                if (skinned == null)
                {
                    continue;
                }

                if (skinned.rootBone != null)
                {
                    AddTransformAndAncestors(
                        requiredTransforms,
                        skinned.rootBone,
                        combinedRoot.transform);
                }

                Transform[] bones = skinned.bones ?? Array.Empty<Transform>();
                HashSet<int> usedBoneIndices =
                    skinned == focusedRenderer && explicitUsedBoneIndices != null
                        ? new HashSet<int>(explicitUsedBoneIndices)
                        : GetActuallyUsedBoneIndices(skinned.sharedMesh);

                // 個別Part Prefabでは、元Meshの「そのPartに含まれる頂点」から取得した
                // Bone番号を優先します。これによりPart Mesh側のBoneWeight取得に失敗して
                // 全Boneを残してしまうケースを防ぎます。
                // 明示情報がないRendererだけ従来どおり安全側へフォールバックします。
                if (usedBoneIndices.Count == 0 &&
                    !(skinned == focusedRenderer && explicitUsedBoneIndices != null))
                {
                    for (int boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                    {
                        Transform bone = bones[boneIndex];
                        if (bone == null)
                        {
                            continue;
                        }

                        usedSkinBones.Add(bone);
                        AddTransformAndAncestors(
                            requiredTransforms,
                            bone,
                            combinedRoot.transform);
                    }
                }
                else
                {
                    foreach (int boneIndex in usedBoneIndices)
                    {
                        if (boneIndex < 0 || boneIndex >= bones.Length)
                        {
                            continue;
                        }

                        Transform bone = bones[boneIndex];
                        if (bone == null)
                        {
                            continue;
                        }

                        usedSkinBones.Add(bone);
                        AddTransformAndAncestors(
                            requiredTransforms,
                            bone,
                            combinedRoot.transform);
                    }

                    // 未使用BoneをHierarchyから削除したあともbones配列にMissing参照を残さないよう、
                    // ウェイトから参照されないスロットは保持対象のRoot Boneへ向けておきます。
                    Transform fallbackBone = skinned.rootBone != null
                        ? skinned.rootBone
                        : armatureRoot;
                    bool remappedUnusedBone = false;
                    for (int boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                    {
                        if (usedBoneIndices.Contains(boneIndex) || bones[boneIndex] == null)
                        {
                            continue;
                        }

                        bones[boneIndex] = fallbackBone;
                        remappedUnusedBone = true;
                    }

                    if (remappedUnusedBone)
                    {
                        skinned.bones = bones;
                    }
                }
            }

            Component[] allComponents = combinedRoot.GetComponentsInChildren<Component>(true);
            List<Component> physBones = new List<Component>();
            List<Component> physBoneColliders = new List<Component>();

            for (int componentIndex = 0; componentIndex < allComponents.Length; componentIndex++)
            {
                Component component = allComponents[componentIndex];
                if (component == null)
                {
                    continue;
                }

                Type type = component.GetType();
                if (IsVrcPhysBoneType(type))
                {
                    physBones.Add(component);
                }
                else if (IsVrcPhysBoneColliderType(type))
                {
                    physBoneColliders.Add(component);
                }
            }

            // 選択されたMeshが実際に使用するBoneを動かすPhysBoneだけを残します。
            for (int physBoneIndex = 0; physBoneIndex < physBones.Count; physBoneIndex++)
            {
                Component physBone = physBones[physBoneIndex];
                Transform physBoneRoot = GetPhysBoneRootTransform(physBone);
                if (physBoneRoot == null)
                {
                    physBoneRoot = physBone.transform;
                }

                if (!IsInsideTransformRoot(physBoneRoot, combinedRoot.transform) ||
                    !DoesTransformSubtreeContainAny(physBoneRoot, usedSkinBones))
                {
                    continue;
                }

                keptPhysBones.Add(physBone);
                AddTransformAndAncestors(
                    requiredTransforms,
                    physBone.transform,
                    combinedRoot.transform);
                AddTransformAndAncestors(
                    requiredTransforms,
                    physBoneRoot,
                    combinedRoot.transform);
                // PhysBone Root以下を丸ごと保持すると、RootがArmature等の上位階層だった場合に
                // Body / Hair / Outfitなど無関係な枝までPrefabへ残ってしまいます。
                // 選択メッシュが実際に使うBoneへ到達する経路だけを保持します。
                AddPhysBonePathsForUsedBones(
                    requiredTransforms,
                    physBoneRoot,
                    usedSkinBones,
                    combinedRoot.transform);

                // PhysBoneのSerialized参照を全部保持するとignoreTransforms等の無関係な
                // Transformまでホワイトリストへ入るため、Collider参照だけを抽出します。
                CollectPhysBoneColliderReferences(
                    physBone,
                    keptPhysBoneColliders);
            }

            // 残すPhysBoneから参照されているColliderだけを残します。
            for (int colliderIndex = 0; colliderIndex < physBoneColliders.Count; colliderIndex++)
            {
                Component collider = physBoneColliders[colliderIndex];
                if (collider == null || !keptPhysBoneColliders.Contains(collider))
                {
                    continue;
                }

                AddTransformAndAncestors(
                    requiredTransforms,
                    collider.transform,
                    combinedRoot.transform);

                // AvatarDynamics配下などにColliderコンポーネントが置かれている場合でも、
                // Collider自身の追従先(rootTransform等)だけを保持します。
                Transform colliderRoot = GetPhysBoneRootTransform(collider);
                if (colliderRoot != null &&
                    IsInsideTransformRoot(colliderRoot, combinedRoot.transform))
                {
                    AddTransformAndAncestors(
                        requiredTransforms,
                        colliderRoot,
                        combinedRoot.transform);
                }
            }

            // コンポーネントを先に整理してからBone階層を削ります。
            for (int physBoneIndex = physBones.Count - 1; physBoneIndex >= 0; physBoneIndex--)
            {
                Component physBone = physBones[physBoneIndex];
                if (physBone != null && !keptPhysBones.Contains(physBone))
                {
                    UnityEngine.Object.DestroyImmediate(physBone);
                }
            }

            for (int colliderIndex = physBoneColliders.Count - 1; colliderIndex >= 0; colliderIndex--)
            {
                Component collider = physBoneColliders[colliderIndex];
                if (collider != null && !keptPhysBoneColliders.Contains(collider))
                {
                    UnityEngine.Object.DestroyImmediate(collider);
                }
            }

            // Armatureルートは必ず残します。
            // 統合PrefabではArmature内部だけを整理し、個別Part Prefabでは
            // 元アバターから複製されたBody/Hair/Outfit等の不要な兄弟階層や、
            // 不要なAvatarDynamics配下も含めてPrefab全体を整理します。
            if (pruneWholePrefab)
            {
                PruneUnusedTransformBranches(combinedRoot.transform, requiredTransforms);
            }
            else
            {
                PruneUnusedTransformBranches(armatureRoot, requiredTransforms);
            }
        }

        private static HashSet<int> GetActuallyUsedBoneIndices(Mesh mesh)
        {
            HashSet<int> result = new HashSet<int>();
            if (mesh == null)
            {
                return result;
            }

            try
            {
                if (mesh.HasVertexAttribute(VertexAttribute.BlendWeight) &&
                    mesh.HasVertexAttribute(VertexAttribute.BlendIndices))
                {
                    NativeArray<byte> bonesPerVertex = mesh.GetBonesPerVertex();
                    NativeArray<BoneWeight1> allWeights = mesh.GetAllBoneWeights();

                    if (bonesPerVertex.Length == mesh.vertexCount && allWeights.Length > 0)
                    {
                        for (int weightIndex = 0; weightIndex < allWeights.Length; weightIndex++)
                        {
                            BoneWeight1 weight = allWeights[weightIndex];
                            if (weight.weight > 0.000001f)
                            {
                                result.Add(weight.boneIndex);
                            }
                        }

                        if (result.Count > 0)
                        {
                            return result;
                        }
                    }
                }
            }
            catch
            {
                // Read/Write状態などで取得できない場合はlegacy APIへフォールバックします。
            }

            try
            {
                BoneWeight[] legacyWeights = mesh.boneWeights;
                if (legacyWeights == null)
                {
                    return result;
                }

                for (int i = 0; i < legacyWeights.Length; i++)
                {
                    BoneWeight weight = legacyWeights[i];
                    AddLegacyBoneIndex(result, weight.boneIndex0, weight.weight0);
                    AddLegacyBoneIndex(result, weight.boneIndex1, weight.weight1);
                    AddLegacyBoneIndex(result, weight.boneIndex2, weight.weight2);
                    AddLegacyBoneIndex(result, weight.boneIndex3, weight.weight3);
                }
            }
            catch
            {
                // 判定不能時は空で返し、呼び出し側ですべてのBoneを残します。
            }

            return result;
        }

        private static HashSet<int> GetUsedBoneIndicesForVertices(
            Mesh mesh,
            IEnumerable<int> sourceVertexIndices)
        {
            HashSet<int> result = new HashSet<int>();
            if (mesh == null || sourceVertexIndices == null)
            {
                return result;
            }

            HashSet<int> selectedVertices = new HashSet<int>();
            foreach (int vertexIndex in sourceVertexIndices)
            {
                if (vertexIndex >= 0 && vertexIndex < mesh.vertexCount)
                {
                    selectedVertices.Add(vertexIndex);
                }
            }

            if (selectedVertices.Count == 0)
            {
                return result;
            }

            try
            {
                NativeArray<byte> bonesPerVertex = mesh.GetBonesPerVertex();
                NativeArray<BoneWeight1> allWeights = mesh.GetAllBoneWeights();
                if (bonesPerVertex.Length == mesh.vertexCount && allWeights.Length > 0)
                {
                    int weightOffset = 0;
                    for (int vertexIndex = 0; vertexIndex < mesh.vertexCount; vertexIndex++)
                    {
                        int boneCount = bonesPerVertex[vertexIndex];
                        if (selectedVertices.Contains(vertexIndex))
                        {
                            for (int i = 0; i < boneCount; i++)
                            {
                                BoneWeight1 weight = allWeights[weightOffset + i];
                                if (weight.weight > 0.000001f)
                                {
                                    result.Add(weight.boneIndex);
                                }
                            }
                        }

                        weightOffset += boneCount;
                    }

                    if (result.Count > 0)
                    {
                        return result;
                    }
                }
            }
            catch
            {
                // legacy BoneWeightへフォールバックします。
            }

            try
            {
                BoneWeight[] legacyWeights = mesh.boneWeights;
                if (legacyWeights == null || legacyWeights.Length != mesh.vertexCount)
                {
                    return result;
                }

                foreach (int vertexIndex in selectedVertices)
                {
                    BoneWeight weight = legacyWeights[vertexIndex];
                    AddLegacyBoneIndex(result, weight.boneIndex0, weight.weight0);
                    AddLegacyBoneIndex(result, weight.boneIndex1, weight.weight1);
                    AddLegacyBoneIndex(result, weight.boneIndex2, weight.weight2);
                    AddLegacyBoneIndex(result, weight.boneIndex3, weight.weight3);
                }
            }
            catch
            {
                // BoneWeightが取得できない特殊MeshではrootBoneだけでPrefabを構成します。
            }

            return result;
        }

        private static void AddLegacyBoneIndex(
            HashSet<int> result,
            int boneIndex,
            float weight)
        {
            if (result != null && boneIndex >= 0 && weight > 0.000001f)
            {
                result.Add(boneIndex);
            }
        }

        private static Transform GetReferencedTransform(UnityEngine.Object referencedObject)
        {
            if (referencedObject == null)
            {
                return null;
            }

            Transform transform = referencedObject as Transform;
            if (transform != null)
            {
                return transform;
            }

            GameObject gameObject = referencedObject as GameObject;
            if (gameObject != null)
            {
                return gameObject.transform;
            }

            Component component = referencedObject as Component;
            return component != null ? component.transform : null;
        }

        private static Transform GetPhysBoneRootTransform(Component physBone)
        {
            if (physBone == null)
            {
                return null;
            }

            try
            {
                SerializedObject serializedObject = new SerializedObject(physBone);
                SerializedProperty rootProperty = serializedObject.FindProperty("rootTransform");
                if (rootProperty != null &&
                    rootProperty.propertyType == SerializedPropertyType.ObjectReference)
                {
                    return GetReferencedTransform(rootProperty.objectReferenceValue);
                }

                SerializedProperty iterator = serializedObject.GetIterator();
                while (iterator.Next(true))
                {
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference)
                    {
                        continue;
                    }

                    string propertyName = iterator.name ?? string.Empty;
                    if (!propertyName.Equals("rootTransform", StringComparison.OrdinalIgnoreCase) &&
                        !propertyName.EndsWith("RootTransform", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Transform referenced = GetReferencedTransform(iterator.objectReferenceValue);
                    if (referenced != null)
                    {
                        return referenced;
                    }
                }
            }
            catch
            {
                // SDK差異などでSerializedObjectを読めない場合はComponent自身をrootとして扱います。
            }

            return null;
        }

        private static void AddPhysBonePathsForUsedBones(
            HashSet<Transform> requiredTransforms,
            Transform physBoneRoot,
            HashSet<Transform> usedSkinBones,
            Transform prefabRoot)
        {
            if (requiredTransforms == null || physBoneRoot == null || prefabRoot == null)
            {
                return;
            }

            AddTransformAndAncestors(requiredTransforms, physBoneRoot, prefabRoot);

            if (usedSkinBones == null)
            {
                return;
            }

            foreach (Transform usedBone in usedSkinBones)
            {
                if (usedBone == null ||
                    (usedBone != physBoneRoot && !usedBone.IsChildOf(physBoneRoot)))
                {
                    continue;
                }

                // PhysBone Rootから実際にウェイトで使われるBoneまでの経路だけ残します。
                AddTransformAndAncestors(requiredTransforms, usedBone, physBoneRoot);
            }
        }

        private static void CollectPhysBoneColliderReferences(
            Component physBone,
            HashSet<Component> keptPhysBoneColliders)
        {
            if (physBone == null || keptPhysBoneColliders == null)
            {
                return;
            }

            try
            {
                SerializedObject serializedObject = new SerializedObject(physBone);
                SerializedProperty iterator = serializedObject.GetIterator();
                while (iterator.Next(true))
                {
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference)
                    {
                        continue;
                    }

                    Component referencedComponent = iterator.objectReferenceValue as Component;
                    if (referencedComponent != null &&
                        IsVrcPhysBoneColliderType(referencedComponent.GetType()))
                    {
                        keptPhysBoneColliders.Add(referencedComponent);
                    }
                }
            }
            catch
            {
                // SDK差異などで参照解析に失敗してもPrefab生成自体は止めません。
            }
        }

        private static void CollectRequiredReferencesFromComponent(
            Component component,
            Transform combinedRoot,
            HashSet<Transform> requiredTransforms,
            HashSet<Component> keptPhysBoneColliders)
        {
            if (component == null || combinedRoot == null)
            {
                return;
            }

            try
            {
                SerializedObject serializedObject = new SerializedObject(component);
                SerializedProperty iterator = serializedObject.GetIterator();
                while (iterator.Next(true))
                {
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference)
                    {
                        continue;
                    }

                    UnityEngine.Object referencedObject = iterator.objectReferenceValue;
                    if (referencedObject == null)
                    {
                        continue;
                    }

                    Component referencedComponent = referencedObject as Component;
                    if (referencedComponent != null &&
                        IsVrcPhysBoneColliderType(referencedComponent.GetType()))
                    {
                        keptPhysBoneColliders.Add(referencedComponent);
                    }

                    Transform referencedTransform = GetReferencedTransform(referencedObject);
                    if (referencedTransform == null ||
                        !IsInsideTransformRoot(referencedTransform, combinedRoot))
                    {
                        continue;
                    }

                    AddTransformAndAncestors(
                        requiredTransforms,
                        referencedTransform,
                        combinedRoot);
                }
            }
            catch
            {
                // 参照解析に失敗しても統合自体は止めません。
            }
        }

        private static bool DoesTransformSubtreeContainAny(
            Transform subtreeRoot,
            HashSet<Transform> candidates)
        {
            if (subtreeRoot == null || candidates == null || candidates.Count == 0)
            {
                return false;
            }

            foreach (Transform candidate in candidates)
            {
                if (candidate != null &&
                    (candidate == subtreeRoot || candidate.IsChildOf(subtreeRoot)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsInsideTransformRoot(Transform value, Transform root)
        {
            return value != null && root != null &&
                   (value == root || value.IsChildOf(root));
        }

        private static void AddTransformAndAncestors(
            HashSet<Transform> requiredTransforms,
            Transform value,
            Transform stopAt)
        {
            if (requiredTransforms == null || value == null)
            {
                return;
            }

            Transform current = value;
            while (current != null)
            {
                requiredTransforms.Add(current);
                if (current == stopAt)
                {
                    break;
                }

                current = current.parent;
            }
        }

        private static void AddTransformSubtree(
            HashSet<Transform> requiredTransforms,
            Transform root)
        {
            if (requiredTransforms == null || root == null)
            {
                return;
            }

            requiredTransforms.Add(root);
            for (int childIndex = 0; childIndex < root.childCount; childIndex++)
            {
                AddTransformSubtree(requiredTransforms, root.GetChild(childIndex));
            }
        }

        private static void PruneUnusedTransformBranches(
            Transform parent,
            HashSet<Transform> requiredTransforms)
        {
            if (parent == null || requiredTransforms == null)
            {
                return;
            }

            for (int childIndex = parent.childCount - 1; childIndex >= 0; childIndex--)
            {
                Transform child = parent.GetChild(childIndex);
                if (child == null)
                {
                    continue;
                }

                if (!requiredTransforms.Contains(child))
                {
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
                    continue;
                }

                PruneUnusedTransformBranches(child, requiredTransforms);
            }
        }

        private static void RemoveRenderersForCombinedTemplate(GameObject root)
        {
            if (root == null)
            {
                return;
            }

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);

            // 統合Prefabの土台にしたPart Prefabには、元アバター由来の
            // Body / Hair / Outfit等のRenderer GameObjectが残っている場合があります。
            // Rendererコンポーネントだけを削除すると空GameObjectがそのまま残るため、
            // Bone階層として本当に必要なもの以外はGameObjectごと除去します。
            HashSet<Transform> protectedRigTransforms = new HashSet<Transform>();
            for (int i = 0; i < renderers.Length; i++)
            {
                SkinnedMeshRenderer skinned = renderers[i] as SkinnedMeshRenderer;
                if (skinned == null)
                {
                    continue;
                }

                if (skinned.rootBone != null)
                {
                    AddTransformAndAncestors(
                        protectedRigTransforms,
                        skinned.rootBone,
                        root.transform);
                }

                Transform[] bones = skinned.bones ?? Array.Empty<Transform>();
                for (int boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                {
                    Transform bone = bones[boneIndex];
                    if (bone == null)
                    {
                        continue;
                    }

                    AddTransformAndAncestors(
                        protectedRigTransforms,
                        bone,
                        root.transform);
                }
            }

            List<GameObject> removeObjects = new List<GameObject>();
            for (int i = renderers.Length - 1; i >= 0; i--)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                Transform rendererTransform = renderer.transform;
                bool mustKeepTransform =
                    rendererTransform == root.transform ||
                    protectedRigTransforms.Contains(rendererTransform) ||
                    IsAncestorOfAny(rendererTransform, protectedRigTransforms);

                if (!mustKeepTransform)
                {
                    removeObjects.Add(renderer.gameObject);
                    continue;
                }

                // Boneそのもの、または必要Boneの親として使われているTransformは残し、
                // 表示用コンポーネントだけを外します。
                MeshRenderer meshRenderer = renderer as MeshRenderer;
                if (meshRenderer != null)
                {
                    MeshFilter filter = meshRenderer.GetComponent<MeshFilter>();
                    if (filter != null)
                    {
                        UnityEngine.Object.DestroyImmediate(filter);
                    }
                }

                UnityEngine.Object.DestroyImmediate(renderer);
            }

            // 深い階層から消すことで、親子ともRendererだった場合も安全に整理します。
            removeObjects.Sort((a, b) =>
            {
                int depthA = a != null ? GetTransformDepth(a.transform) : -1;
                int depthB = b != null ? GetTransformDepth(b.transform) : -1;
                return depthB.CompareTo(depthA);
            });

            for (int i = 0; i < removeObjects.Count; i++)
            {
                GameObject removeObject = removeObjects[i];
                if (removeObject != null && removeObject != root)
                {
                    UnityEngine.Object.DestroyImmediate(removeObject);
                }
            }
        }

        private static int GetTransformDepth(Transform transform)
        {
            int depth = 0;
            Transform current = transform;
            while (current != null)
            {
                depth++;
                current = current.parent;
            }
            return depth;
        }


        private static void MergeVrcDynamicsFromDonor(
            GameObject donorRoot,
            GameObject combinedRoot)
        {
            if (donorRoot == null || combinedRoot == null)
            {
                return;
            }

            Component[] donorComponents = donorRoot.GetComponentsInChildren<Component>(true);
            List<Component> donorColliders = new List<Component>();
            List<Component> donorPhysBones = new List<Component>();

            for (int i = 0; i < donorComponents.Length; i++)
            {
                Component component = donorComponents[i];
                if (component == null)
                {
                    continue;
                }

                Type type = component.GetType();
                if (IsVrcPhysBoneColliderType(type))
                {
                    donorColliders.Add(component);
                }
                else if (IsVrcPhysBoneType(type))
                {
                    donorPhysBones.Add(component);
                }
            }

            if (donorColliders.Count == 0 && donorPhysBones.Count == 0)
            {
                return;
            }

            SkinnedMeshRenderer donorRenderer =
                donorRoot.GetComponentInChildren<SkinnedMeshRenderer>(true);
            Transform donorArmature = donorRenderer != null
                ? FindArmatureRootForRenderer(donorRoot, donorRenderer)
                : null;
            Transform combinedArmature = donorArmature != null
                ? FindMatchingArmatureRootInHierarchy(combinedRoot, donorArmature)
                : null;

            Dictionary<Component, Component> remappedComponents =
                new Dictionary<Component, Component>();

            // PhysBoneがColliderを参照するため、Colliderを先に複製・対応付けします。
            for (int i = 0; i < donorColliders.Count; i++)
            {
                Component donorCollider = donorColliders[i];
                Component targetCollider = GetOrCreateMatchingDynamicsComponent(
                    donorCollider,
                    donorRoot.transform,
                    combinedRoot.transform,
                    donorArmature,
                    combinedArmature);
                if (targetCollider != null)
                {
                    remappedComponents[donorCollider] = targetCollider;
                }
            }

            for (int i = 0; i < donorPhysBones.Count; i++)
            {
                Component donorPhysBone = donorPhysBones[i];
                Component targetPhysBone = GetOrCreateMatchingDynamicsComponent(
                    donorPhysBone,
                    donorRoot.transform,
                    combinedRoot.transform,
                    donorArmature,
                    combinedArmature);
                if (targetPhysBone != null)
                {
                    remappedComponents[donorPhysBone] = targetPhysBone;
                }
            }

            // Componentを作り終えてからSerialized値をコピーし、
            // donor側Hierarchyへの参照を統合Prefab側へ張り直します。
            for (int i = 0; i < donorColliders.Count; i++)
            {
                Component source = donorColliders[i];
                if (source != null && remappedComponents.TryGetValue(source, out Component target))
                {
                    CopyDynamicsComponentWithRemappedReferences(
                        source,
                        target,
                        donorRoot.transform,
                        combinedRoot.transform,
                        donorArmature,
                        combinedArmature,
                        remappedComponents);
                }
            }

            for (int i = 0; i < donorPhysBones.Count; i++)
            {
                Component source = donorPhysBones[i];
                if (source != null && remappedComponents.TryGetValue(source, out Component target))
                {
                    CopyDynamicsComponentWithRemappedReferences(
                        source,
                        target,
                        donorRoot.transform,
                        combinedRoot.transform,
                        donorArmature,
                        combinedArmature,
                        remappedComponents);
                }
            }
        }

        private static Component GetOrCreateMatchingDynamicsComponent(
            Component donorComponent,
            Transform donorRoot,
            Transform combinedRoot,
            Transform donorArmature,
            Transform combinedArmature)
        {
            if (donorComponent == null || donorRoot == null || combinedRoot == null)
            {
                return null;
            }

            Transform targetHost = MapDonorTransformToCombined(
                donorComponent.transform,
                donorRoot,
                combinedRoot,
                donorArmature,
                combinedArmature);
            if (targetHost == null)
            {
                return null;
            }

            Type componentType = donorComponent.GetType();
            int sourceIndex = GetSameTypeComponentIndex(donorComponent);
            if (sourceIndex < 0)
            {
                sourceIndex = 0;
            }

            Component[] existing = targetHost.gameObject.GetComponents(componentType);
            if (sourceIndex < existing.Length)
            {
                return existing[sourceIndex];
            }

            Component created = null;
            while (existing.Length <= sourceIndex)
            {
                try
                {
                    created = targetHost.gameObject.AddComponent(componentType);
                }
                catch
                {
                    // DisallowMultipleComponent等で追加できない型の場合は既存の先頭を利用します。
                    return existing.Length > 0 ? existing[0] : null;
                }

                if (created == null)
                {
                    return existing.Length > 0 ? existing[0] : null;
                }

                existing = targetHost.gameObject.GetComponents(componentType);
            }

            return sourceIndex < existing.Length ? existing[sourceIndex] : created;
        }

        private static int GetSameTypeComponentIndex(Component component)
        {
            if (component == null)
            {
                return -1;
            }

            Component[] components = component.gameObject.GetComponents(component.GetType());
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] == component)
                {
                    return i;
                }
            }

            return -1;
        }

        private static Transform MapDonorTransformToCombined(
            Transform donorTransform,
            Transform donorRoot,
            Transform combinedRoot,
            Transform donorArmature,
            Transform combinedArmature)
        {
            if (donorTransform == null || donorRoot == null || combinedRoot == null)
            {
                return null;
            }

            if (donorTransform == donorRoot)
            {
                return combinedRoot;
            }

            if (donorArmature != null && combinedArmature != null &&
                (donorTransform == donorArmature || donorTransform.IsChildOf(donorArmature)))
            {
                return FindOrCreateMatchingBone(
                    donorArmature,
                    donorTransform,
                    combinedArmature);
            }

            Transform donorParent = donorTransform.parent;
            if (donorParent == null ||
                (donorParent != donorRoot && !donorParent.IsChildOf(donorRoot)))
            {
                return null;
            }

            Transform targetParent = MapDonorTransformToCombined(
                donorParent,
                donorRoot,
                combinedRoot,
                donorArmature,
                combinedArmature);
            if (targetParent == null)
            {
                return null;
            }

            Transform existing = FindDirectChildByName(targetParent, donorTransform.name);
            if (existing != null)
            {
                return existing;
            }

            GameObject created = new GameObject(donorTransform.name);
            Transform createdTransform = created.transform;
            createdTransform.SetParent(targetParent, false);
            createdTransform.localPosition = donorTransform.localPosition;
            createdTransform.localRotation = donorTransform.localRotation;
            createdTransform.localScale = donorTransform.localScale;
            CopyGameObjectSettings(donorTransform.gameObject, created);
            return createdTransform;
        }

        private static void CopyDynamicsComponentWithRemappedReferences(
            Component source,
            Component target,
            Transform donorRoot,
            Transform combinedRoot,
            Transform donorArmature,
            Transform combinedArmature,
            Dictionary<Component, Component> componentMap)
        {
            if (source == null || target == null)
            {
                return;
            }

            try
            {
                EditorUtility.CopySerialized(source, target);

                SerializedObject serializedObject = new SerializedObject(target);
                SerializedProperty iterator = serializedObject.GetIterator();
                bool enterChildren = true;
                while (iterator.Next(enterChildren))
                {
                    enterChildren = true;
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference)
                    {
                        continue;
                    }

                    UnityEngine.Object currentReference = iterator.objectReferenceValue;
                    if (currentReference == null)
                    {
                        continue;
                    }

                    UnityEngine.Object remapped = RemapDynamicsObjectReference(
                        currentReference,
                        donorRoot,
                        combinedRoot,
                        donorArmature,
                        combinedArmature,
                        componentMap);
                    if (remapped != currentReference)
                    {
                        iterator.objectReferenceValue = remapped;
                    }
                }

                serializedObject.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(target);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[NN - Object Bunkai Tool] VRC Dynamicsの統合中に一部設定をコピーできませんでした: " +
                    source.name + " / " + ex.Message);
            }
        }

        private static UnityEngine.Object RemapDynamicsObjectReference(
            UnityEngine.Object sourceReference,
            Transform donorRoot,
            Transform combinedRoot,
            Transform donorArmature,
            Transform combinedArmature,
            Dictionary<Component, Component> componentMap)
        {
            if (sourceReference == null)
            {
                return null;
            }

            Component sourceComponent = sourceReference as Component;
            if (sourceComponent != null)
            {
                if (componentMap != null &&
                    componentMap.TryGetValue(sourceComponent, out Component mappedComponent))
                {
                    return mappedComponent;
                }

                Transform componentTransform = sourceComponent.transform;
                if (componentTransform != null &&
                    (componentTransform == donorRoot || componentTransform.IsChildOf(donorRoot)))
                {
                    Transform targetTransform = MapDonorTransformToCombined(
                        componentTransform,
                        donorRoot,
                        combinedRoot,
                        donorArmature,
                        combinedArmature);
                    if (targetTransform == null)
                    {
                        return null;
                    }

                    if (sourceComponent is Transform)
                    {
                        return targetTransform;
                    }

                    int componentIndex = GetSameTypeComponentIndex(sourceComponent);
                    Component[] matching = targetTransform.gameObject.GetComponents(sourceComponent.GetType());
                    if (componentIndex >= 0 && componentIndex < matching.Length)
                    {
                        return matching[componentIndex];
                    }

                    return null;
                }

                // Project Asset等、donorの外にある参照はそのまま維持します。
                return sourceReference;
            }

            GameObject sourceGameObject = sourceReference as GameObject;
            if (sourceGameObject != null)
            {
                Transform sourceTransform = sourceGameObject.transform;
                if (sourceTransform == donorRoot || sourceTransform.IsChildOf(donorRoot))
                {
                    Transform targetTransform = MapDonorTransformToCombined(
                        sourceTransform,
                        donorRoot,
                        combinedRoot,
                        donorArmature,
                        combinedArmature);
                    return targetTransform != null ? targetTransform.gameObject : null;
                }

                return sourceReference;
            }

            return sourceReference;
        }

        private static Dictionary<string, int> BuildCombinedPartGroupCounts(
            List<GeneratedPrefabEntry> entries)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);

            if (entries == null)
            {
                return counts;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                GeneratedPrefabEntry entry = entries[i];
                if (entry == null)
                {
                    continue;
                }

                string groupName = GetCombinedPartGroupBaseName(entry.AssetName);
                if (string.IsNullOrEmpty(groupName))
                {
                    continue;
                }

                counts.TryGetValue(groupName, out int count);
                counts[groupName] = count + 1;
            }

            return counts;
        }

        private static string GetCombinedPartGroupBaseName(string partName)
        {
            if (string.IsNullOrEmpty(partName))
            {
                return string.Empty;
            }

            int markerIndex = partName.IndexOf(
                "_ObjectPart_",
                StringComparison.OrdinalIgnoreCase);
            if (markerIndex <= 0)
            {
                markerIndex = partName.IndexOf(
                    "_Part_",
                    StringComparison.OrdinalIgnoreCase);
            }

            return markerIndex > 0
                ? partName.Substring(0, markerIndex)
                : string.Empty;
        }

        private static Transform GetOrCreateCombinedPartGroup(
            GameObject combinedRoot,
            string groupName)
        {
            if (combinedRoot == null || string.IsNullOrEmpty(groupName))
            {
                return combinedRoot != null ? combinedRoot.transform : null;
            }

            Transform existing = FindDirectChildByName(combinedRoot.transform, groupName);
            if (existing != null)
            {
                return existing;
            }

            GameObject groupObject = new GameObject(groupName);
            groupObject.transform.SetParent(combinedRoot.transform, false);
            groupObject.transform.localPosition = Vector3.zero;
            groupObject.transform.localRotation = Quaternion.identity;
            groupObject.transform.localScale = Vector3.one;
            return groupObject.transform;
        }

        private static string GetCombinedPartChildName(
            string partName,
            string groupName,
            int rendererIndex)
        {
            string childName = string.IsNullOrEmpty(partName) ? "Part" : partName;

            if (!string.IsNullOrEmpty(groupName))
            {
                string prefix = groupName + "_";
                if (childName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    childName = childName.Substring(prefix.Length);
                }
            }

            if (rendererIndex > 0)
            {
                childName += "_" + rendererIndex.ToString("000");
            }

            return childName;
        }

        private static bool AppendPrefabRenderersToCombinedRoot(
            GameObject donorRoot,
            GameObject combinedRoot,
            string partName,
            string partGroupName,
            bool hasSharedArmatureTemplate,
            out string error)
        {
            error = string.Empty;
            if (donorRoot == null || combinedRoot == null)
            {
                error = "統合元のPrefabを取得できませんでした。";
                return false;
            }

            Renderer[] donorRenderers = donorRoot.GetComponentsInChildren<Renderer>(true);
            if (donorRenderers.Length == 0)
            {
                error = "Prefab内にRendererがありません: " + partName;
                return false;
            }

            int addedCount = 0;
            for (int rendererIndex = 0; rendererIndex < donorRenderers.Length; rendererIndex++)
            {
                Renderer donorRenderer = donorRenderers[rendererIndex];
                if (donorRenderer == null)
                {
                    continue;
                }

                SkinnedMeshRenderer donorSkinned = donorRenderer as SkinnedMeshRenderer;
                if (donorSkinned != null)
                {
                    if (!hasSharedArmatureTemplate)
                    {
                        error = "Armatureが一致しません。";
                        return false;
                    }

                    if (!AppendSkinnedRendererToCombined(
                            donorRoot,
                            donorSkinned,
                            combinedRoot,
                            partName,
                            partGroupName,
                            rendererIndex,
                            out error))
                    {
                        return false;
                    }

                    addedCount++;
                    continue;
                }

                MeshRenderer donorMeshRenderer = donorRenderer as MeshRenderer;
                if (donorMeshRenderer != null)
                {
                    AppendMeshRendererToCombined(
                        donorRoot,
                        donorMeshRenderer,
                        combinedRoot,
                        partName,
                        partGroupName,
                        rendererIndex);
                    addedCount++;
                }
            }

            if (addedCount == 0)
            {
                error = "統合できるRendererがありません: " + partName;
                return false;
            }

            return true;
        }

        private static bool AppendSkinnedRendererToCombined(
            GameObject donorRoot,
            SkinnedMeshRenderer donorRenderer,
            GameObject combinedRoot,
            string partName,
            string partGroupName,
            int rendererIndex,
            out string error)
        {
            error = string.Empty;

            // 個別Part Prefabはそれぞれ独立したArmatureコピーを持つため、
            // Transform参照そのものの一致ではなく「Armatureからの相対Boneパス」で統合します。
            Transform donorArmature = FindArmatureRootForRenderer(donorRoot, donorRenderer);
            // combinedRootは土台PrefabからRendererを外した直後なので、
            // Renderer依存のFindArmatureRootForModularAvatarではArmatureを見つけられません。
            // donor側Armatureの名前・階層を基準に、土台Hierarchyから直接探します。
            Transform combinedArmature = FindMatchingArmatureRootInHierarchy(combinedRoot, donorArmature);
            if (donorArmature == null || combinedArmature == null)
            {
                error = "Armatureが一致しません。";
                return false;
            }

            // Renderer本体はArmature外に置かれているアバターも多いため、
            // 親階層が一致しないことだけを理由に統合を失敗させません。
            Transform targetParent;
            if (!string.IsNullOrEmpty(partGroupName))
            {
                // 同じ「○○_Part_XXX」由来のパーツはMeshやMaterialを結合せず、
                // ○○という空の親の下へ整理するだけにします。
                targetParent = GetOrCreateCombinedPartGroup(combinedRoot, partGroupName);
            }
            else
            {
                targetParent = FindMatchingParentTransform(
                    donorRoot.transform,
                    donorRenderer.transform,
                    combinedRoot.transform);
                if (targetParent == null || targetParent == donorArmature)
                {
                    targetParent = combinedRoot.transform;
                }
            }

            GameObject partObject = new GameObject(
                GetCombinedPartChildName(partName, partGroupName, rendererIndex));
            partObject.transform.SetParent(targetParent, false);
            CopyLocalTransformForCombined(donorRoot.transform, donorRenderer.transform, partObject.transform);
            CopyGameObjectSettings(donorRenderer.gameObject, partObject);

            SkinnedMeshRenderer destination = partObject.AddComponent<SkinnedMeshRenderer>();
            destination.sharedMesh = donorRenderer.sharedMesh;
            destination.sharedMaterials = donorRenderer.sharedMaterials;
            CopyCommonRendererSettings(donorRenderer, destination);
            destination.quality = donorRenderer.quality;
            destination.updateWhenOffscreen = donorRenderer.updateWhenOffscreen;
            destination.skinnedMotionVectors = donorRenderer.skinnedMotionVectors;
            destination.localBounds = donorRenderer.localBounds;

            Transform[] donorBones = donorRenderer.bones ?? Array.Empty<Transform>();
            Transform[] remappedBones = new Transform[donorBones.Length];
            for (int i = 0; i < donorBones.Length; i++)
            {
                if (donorBones[i] == null)
                {
                    remappedBones[i] = null;
                    continue;
                }

                Transform remappedBone = FindOrCreateMatchingBone(
                    donorArmature,
                    donorBones[i],
                    combinedArmature);
                if (remappedBone == null)
                {
                    UnityEngine.Object.DestroyImmediate(partObject);
                    error = "Armatureが一致しません。";
                    return false;
                }

                remappedBones[i] = remappedBone;
            }

            destination.bones = remappedBones;

            if (donorRenderer.rootBone != null)
            {
                destination.rootBone = FindOrCreateMatchingBone(
                    donorArmature,
                    donorRenderer.rootBone,
                    combinedArmature);
                if (destination.rootBone == null)
                {
                    UnityEngine.Object.DestroyImmediate(partObject);
                    error = "Armatureが一致しません。";
                    return false;
                }
            }

            CopyBlendShapeWeights(donorRenderer, destination);
            return true;
        }

        private static Transform FindArmatureRootForRenderer(
            GameObject prefabRoot,
            SkinnedMeshRenderer renderer)
        {
            if (prefabRoot == null || renderer == null)
            {
                return null;
            }

            Transform seed = renderer.rootBone;
            if (seed == null)
            {
                Transform[] bones = renderer.bones ?? Array.Empty<Transform>();
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] != null)
                    {
                        seed = bones[i];
                        break;
                    }
                }
            }

            if (seed == null || !seed.IsChildOf(prefabRoot.transform))
            {
                return null;
            }

            Transform namedCandidate = null;
            Transform cursor = seed;
            while (cursor != null && cursor != prefabRoot.transform)
            {
                string lower = cursor.name.ToLowerInvariant();
                if (lower.Contains("armature") ||
                    lower.Contains("amature") ||
                    lower.Contains("skeleton") ||
                    lower == "rig")
                {
                    namedCandidate = cursor;
                }
                cursor = cursor.parent;
            }

            if (namedCandidate != null)
            {
                return namedCandidate;
            }

            Transform top = seed;
            while (top.parent != null && top.parent != prefabRoot.transform)
            {
                top = top.parent;
            }
            return top;
        }

        private static Transform FindMatchingArmatureRootInHierarchy(
            GameObject combinedRoot,
            Transform donorArmature)
        {
            if (combinedRoot == null || donorArmature == null)
            {
                return null;
            }

            Transform[] transforms = combinedRoot.GetComponentsInChildren<Transform>(true);
            Transform conventionCandidate = null;

            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (candidate == null || candidate == combinedRoot.transform)
                {
                    continue;
                }

                // 同じ個別Prefab群から作られたArmatureなら通常は名前が一致します。
                if (string.Equals(candidate.name, donorArmature.name, StringComparison.Ordinal))
                {
                    return candidate;
                }

                string lower = candidate.name.ToLowerInvariant();
                if (conventionCandidate == null &&
                    (lower.Contains("armature") ||
                     lower.Contains("amature") ||
                     lower.Contains("skeleton") ||
                     lower == "rig"))
                {
                    conventionCandidate = candidate;
                }
            }

            return conventionCandidate;
        }

        private static Transform FindOrCreateMatchingBone(
            Transform donorArmature,
            Transform donorBone,
            Transform combinedArmature)
        {
            if (donorArmature == null || donorBone == null || combinedArmature == null)
            {
                return null;
            }

            if (donorBone == donorArmature)
            {
                return combinedArmature;
            }

            if (!donorBone.IsChildOf(donorArmature))
            {
                return null;
            }

            string relativePath = AnimationUtility.CalculateTransformPath(
                donorBone,
                donorArmature);
            if (string.IsNullOrEmpty(relativePath))
            {
                return combinedArmature;
            }

            string[] segments = relativePath.Split('/');
            Transform donorCursor = donorArmature;
            Transform combinedCursor = combinedArmature;

            for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
            {
                string segment = segments[segmentIndex];
                Transform donorChild = FindDirectChildByName(donorCursor, segment);
                if (donorChild == null)
                {
                    return null;
                }

                Transform combinedChild = FindDirectChildByName(combinedCursor, segment);
                if (combinedChild == null)
                {
                    // 個別Prefabの軽量化によって土台Partに存在しないBoneがあっても、
                    // 選択した別Partから必要Boneだけ追加してArmatureを1セットにまとめます。
                    GameObject created = new GameObject(donorChild.name);
                    combinedChild = created.transform;
                    combinedChild.SetParent(combinedCursor, false);
                    combinedChild.localPosition = donorChild.localPosition;
                    combinedChild.localRotation = donorChild.localRotation;
                    combinedChild.localScale = donorChild.localScale;
                    CopyGameObjectSettings(donorChild.gameObject, created);
                }
                else if (!TransformsApproximatelyMatch(donorChild, combinedChild))
                {
                    // 同名BoneでもTransformが大きく違う場合は別骨格と判断します。
                    return null;
                }

                donorCursor = donorChild;
                combinedCursor = combinedChild;
            }

            return combinedCursor;
        }

        private static Transform FindDirectChildByName(Transform parent, string childName)
        {
            if (parent == null)
            {
                return null;
            }

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child != null && string.Equals(child.name, childName, StringComparison.Ordinal))
                {
                    return child;
                }
            }

            return null;
        }

        private static void AppendMeshRendererToCombined(
            GameObject donorRoot,
            MeshRenderer donorRenderer,
            GameObject combinedRoot,
            string partName,
            string partGroupName,
            int rendererIndex)
        {
            Transform targetParent;
            if (!string.IsNullOrEmpty(partGroupName))
            {
                targetParent = GetOrCreateCombinedPartGroup(combinedRoot, partGroupName);
            }
            else
            {
                targetParent = FindMatchingParentTransform(
                    donorRoot.transform,
                    donorRenderer.transform,
                    combinedRoot.transform);
                if (targetParent == null)
                {
                    targetParent = combinedRoot.transform;
                }
            }

            GameObject partObject = new GameObject(
                GetCombinedPartChildName(partName, partGroupName, rendererIndex));
            partObject.transform.SetParent(targetParent, false);
            CopyLocalTransformForCombined(donorRoot.transform, donorRenderer.transform, partObject.transform);
            CopyGameObjectSettings(donorRenderer.gameObject, partObject);

            MeshFilter donorFilter = donorRenderer.GetComponent<MeshFilter>();
            MeshFilter destinationFilter = partObject.AddComponent<MeshFilter>();
            destinationFilter.sharedMesh = donorFilter != null ? donorFilter.sharedMesh : null;

            MeshRenderer destinationRenderer = partObject.AddComponent<MeshRenderer>();
            destinationRenderer.sharedMaterials = donorRenderer.sharedMaterials;
            CopyMeshRendererSettings(donorRenderer, destinationRenderer);
        }

        private static Transform FindMatchingParentTransform(
            Transform donorRoot,
            Transform donorRendererTransform,
            Transform combinedRoot)
        {
            if (donorRoot == null || donorRendererTransform == null || combinedRoot == null)
            {
                return null;
            }

            if (donorRendererTransform == donorRoot || donorRendererTransform.parent == null)
            {
                return combinedRoot;
            }

            string parentPath = AnimationUtility.CalculateTransformPath(
                donorRendererTransform.parent,
                donorRoot);
            return FindTransformByRelativePath(combinedRoot, parentPath);
        }

        private static Transform FindTransformByRelativePath(Transform root, string path)
        {
            if (root == null)
            {
                return null;
            }

            return string.IsNullOrEmpty(path) ? root : root.Find(path);
        }

        private static bool TransformsApproximatelyMatch(Transform source, Transform target)
        {
            if (source == null || target == null)
            {
                return source == target;
            }

            const float positionTolerance = 0.0001f;
            const float scaleTolerance = 0.0001f;
            const float rotationTolerance = 0.05f;

            return Vector3.SqrMagnitude(source.localPosition - target.localPosition) <=
                       positionTolerance * positionTolerance &&
                   Vector3.SqrMagnitude(source.localScale - target.localScale) <=
                       scaleTolerance * scaleTolerance &&
                   Quaternion.Angle(source.localRotation, target.localRotation) <= rotationTolerance;
        }

        private static void CopyLocalTransformForCombined(
            Transform donorRoot,
            Transform donorTransform,
            Transform destination)
        {
            if (donorTransform == donorRoot)
            {
                destination.localPosition = Vector3.zero;
                destination.localRotation = Quaternion.identity;
                destination.localScale = Vector3.one;
                return;
            }

            destination.localPosition = donorTransform.localPosition;
            destination.localRotation = donorTransform.localRotation;
            destination.localScale = donorTransform.localScale;
        }

        private static void CopyGameObjectSettings(GameObject source, GameObject destination)
        {
            destination.layer = source.layer;
            destination.tag = source.CompareTag("EditorOnly") ? "Untagged" : source.tag;
            GameObjectUtility.SetStaticEditorFlags(
                destination,
                GameObjectUtility.GetStaticEditorFlags(source));
            destination.SetActive(source.activeSelf);
        }

        private static void ApplyHideFlagsRecursively(Transform root, HideFlags flags)
        {
            if (root == null)
            {
                return;
            }

            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null)
                {
                    transforms[i].gameObject.hideFlags = flags;
                }
            }
        }

        private void SetGeneratedPrefabSelected(string assetPath, bool selected)
        {
            if (selected)
            {
                if (!selectedGeneratedPrefabPaths.Contains(assetPath))
                {
                    selectedGeneratedPrefabPaths.Add(assetPath);
                }
            }
            else
            {
                selectedGeneratedPrefabPaths.Remove(assetPath);
            }
        }

        private static void SelectGeneratedPrefabAsset(string assetPath)
        {
            GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefabAsset == null)
            {
                return;
            }

            Selection.activeObject = prefabAsset;
            EditorGUIUtility.PingObject(prefabAsset);
        }

        private GameObject CreateGeneratedPreviewObject(GeneratedPrefabEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.AssetPath))
            {
                return null;
            }

            GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(entry.AssetPath);
            if (prefabAsset == null)
            {
                Debug.LogWarning("ねこねこツール: 生成Prefabを読み込めませんでした: " + entry.AssetPath);
                return null;
            }

            GameObject instance = UnityEngine.Object.Instantiate(prefabAsset);
            if (instance == null)
            {
                return null;
            }

            instance.name = GeneratedPreviewNamePrefix + entry.AssetName;
            instance.SetActive(true);
            ApplyGeneratedPreviewHideFlags(instance.transform);
            return instance;
        }

        private static void ApplyGeneratedPreviewHideFlags(Transform root)
        {
            if (root == null)
            {
                return;
            }

            HideFlags previewFlags = HideFlags.HideInHierarchy |
                                     HideFlags.DontSaveInEditor |
                                     HideFlags.DontSaveInBuild |
                                     HideFlags.NotEditable;

            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null)
                {
                    transforms[i].gameObject.hideFlags = previewFlags;
                }
            }
        }

        private static void CleanupOrphanedGeneratedPreviewObjects()
        {
            GameObject[] sceneObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < sceneObjects.Length; i++)
            {
                GameObject sceneObject = sceneObjects[i];
                if (sceneObject == null ||
                    EditorUtility.IsPersistent(sceneObject) ||
                    !sceneObject.scene.IsValid() ||
                    !sceneObject.name.StartsWith(GeneratedPreviewNamePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                UnityEngine.Object.DestroyImmediate(sceneObject);
            }
        }

        private bool CanSoloGeneratedPreviewPart(GameObject target)
        {
            return target != null;
        }

        private bool IsGeneratedPreviewSoloActive(GameObject target, string assetPath)
        {
            return target != null &&
                   !string.IsNullOrEmpty(assetPath) &&
                   string.Equals(
                       soloGeneratedPrefabPath,
                       assetPath,
                       StringComparison.Ordinal) &&
                   SceneVisibilityManager.instance.IsCurrentStageIsolated();
        }

        private void EnterGeneratedPreviewIsolation(GameObject target, string assetPath)
        {
            if (target == null || string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            CaptureGeneratedPreviewSceneViewState();

            if (SceneVisibilityManager.instance.IsCurrentStageIsolated())
            {
                SceneVisibilityManager.instance.ExitIsolation();
            }

            SceneVisibilityManager.instance.Isolate(target, true);
            soloGeneratedPrefabPath = assetPath;
            SelectAndFramePreviewPart(target);
            EditorApplication.RepaintHierarchyWindow();
            SceneView.RepaintAll();
            Repaint();
        }

        private void ExitGeneratedPreviewIsolation(bool restoreSceneView = false)
        {
            if (SceneVisibilityManager.instance.IsCurrentStageIsolated())
            {
                SceneVisibilityManager.instance.ExitIsolation();
            }

            soloGeneratedPrefabPath = string.Empty;

            if (restoreSceneView)
            {
                RestoreGeneratedPreviewSceneViewState();
            }

            EditorApplication.RepaintHierarchyWindow();
            SceneView.RepaintAll();
            Repaint();
        }

        private void EndGeneratedPreviewDisplay(bool clearSelection, bool repaint = true)
        {
            if (SceneVisibilityManager.instance.IsCurrentStageIsolated() &&
                !string.IsNullOrEmpty(soloGeneratedPrefabPath))
            {
                SceneVisibilityManager.instance.ExitIsolation();
            }

            soloGeneratedPrefabPath = string.Empty;
            RestoreGeneratedPreviewSceneViewState();

            foreach (KeyValuePair<string, GameObject> pair in generatedPreviewObjects)
            {
                if (pair.Value != null)
                {
                    UnityEngine.Object.DestroyImmediate(pair.Value);
                }
            }
            generatedPreviewObjects.Clear();
            generatedPreviewObject = null;

            if (clearSelection)
            {
                activeGeneratedPrefabPath = string.Empty;
                selectedGeneratedPrefabPaths.Clear();
            }

            if (repaint)
            {
                EditorApplication.RepaintHierarchyWindow();
                SceneView.RepaintAll();
                Repaint();
            }
        }

        private void SetAllPreviewPartsActive(bool active)
        {
            for (int i = 0; i < previewParts.Count; i++)
            {
                if (previewParts[i] != null && previewParts[i].PartObject != null)
                {
                    SetPreviewPartActive(previewParts[i].PartObject, active);
                }
            }

            SceneView.RepaintAll();
            Repaint();
        }

        private void SoloPreviewPart(GameObject target)
        {
            for (int i = 0; i < previewParts.Count; i++)
            {
                GameObject partObject = previewParts[i] != null
                    ? previewParts[i].PartObject
                    : null;
                if (partObject != null)
                {
                    SetPreviewPartActive(partObject, partObject == target);
                }
            }

            SelectAndFramePreviewPart(target);
            SceneView.RepaintAll();
            Repaint();
        }

        private static void SetPreviewPartActive(GameObject partObject, bool active)
        {
            if (partObject == null || partObject.activeSelf == active)
            {
                return;
            }

            Undo.RecordObject(partObject, active ? "分解パーツを表示" : "分解パーツを非表示");
            partObject.SetActive(active);
            EditorUtility.SetDirty(partObject);
        }

        private static void SelectAndFramePreviewPart(GameObject partObject)
        {
            if (partObject == null)
            {
                return;
            }

            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                return;
            }

            if (TryGetAccuratePreviewBounds(partObject, out Bounds bounds))
            {
                float longestSide = Mathf.Max(
                    bounds.size.x,
                    Mathf.Max(bounds.size.y, bounds.size.z));

                // A tiny margin keeps the mesh from touching the SceneView edges,
                // while still being much closer than Unity's generic FrameSelected.
                float margin = Mathf.Max(longestSide * 0.06f, 0.0005f);
                bounds.Expand(margin);
                sceneView.Frame(bounds, false);
            }
            else
            {
                sceneView.FrameSelected();
            }

            sceneView.Repaint();
        }

        private static bool TryGetAccuratePreviewBounds(GameObject partObject, out Bounds bounds)
        {
            bounds = default(Bounds);
            bool hasBounds = false;

            Renderer[] renderers = partObject.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                if (!TryGetRendererMeshBounds(renderer, out Bounds rendererBounds))
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = rendererBounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(rendererBounds);
                }
            }

            return hasBounds;
        }

        private static bool TryGetRendererMeshBounds(Renderer renderer, out Bounds worldBounds)
        {
            worldBounds = default(Bounds);

            SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null && skinned.sharedMesh != null)
            {
                Mesh bakedMesh = new Mesh();
                try
                {
                    skinned.BakeMesh(bakedMesh);
                    Bounds localBounds = bakedMesh.vertexCount > 0
                        ? bakedMesh.bounds
                        : skinned.sharedMesh.bounds;
                    worldBounds = TransformBounds(
                        skinned.transform.localToWorldMatrix,
                        localBounds);
                    return IsUsableBounds(worldBounds);
                }
                catch
                {
                    worldBounds = TransformBounds(
                        skinned.transform.localToWorldMatrix,
                        skinned.sharedMesh.bounds);
                    return IsUsableBounds(worldBounds);
                }
                finally
                {
                    DestroyImmediate(bakedMesh);
                }
            }

            MeshRenderer meshRenderer = renderer as MeshRenderer;
            if (meshRenderer != null)
            {
                MeshFilter meshFilter = meshRenderer.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    worldBounds = TransformBounds(
                        meshFilter.transform.localToWorldMatrix,
                        meshFilter.sharedMesh.bounds);
                    return IsUsableBounds(worldBounds);
                }
            }

            worldBounds = renderer.bounds;
            return IsUsableBounds(worldBounds);
        }

        private static Bounds TransformBounds(Matrix4x4 matrix, Bounds localBounds)
        {
            Vector3 worldCenter = matrix.MultiplyPoint3x4(localBounds.center);
            Vector3 extents = localBounds.extents;

            Vector3 axisX = matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f));
            Vector3 axisY = matrix.MultiplyVector(new Vector3(0f, extents.y, 0f));
            Vector3 axisZ = matrix.MultiplyVector(new Vector3(0f, 0f, extents.z));

            Vector3 worldExtents = AbsVector(axisX) + AbsVector(axisY) + AbsVector(axisZ);
            return new Bounds(worldCenter, worldExtents * 2f);
        }

        private static Vector3 AbsVector(Vector3 value)
        {
            return new Vector3(
                Mathf.Abs(value.x),
                Mathf.Abs(value.y),
                Mathf.Abs(value.z));
        }

        private static bool IsUsableBounds(Bounds bounds)
        {
            Vector3 size = bounds.size;
            return !float.IsNaN(size.x) && !float.IsInfinity(size.x) &&
                   !float.IsNaN(size.y) && !float.IsInfinity(size.y) &&
                   !float.IsNaN(size.z) && !float.IsInfinity(size.z) &&
                   size.sqrMagnitude > 0.000000000001f;
        }

        private void DrawPrefabStatus()
        {
            if (prefabRoot == null)
            {
                EditorGUILayout.HelpBox("PrefabまたはHierarchy上のルートオブジェクトを指定してください。", MessageType.Warning);
                return;
            }

            string sourceKind = EditorUtility.IsPersistent(prefabRoot)
                ? "Project内のPrefab（分解時にシーンへ複製します）"
                : "Hierarchy上のオブジェクト";

            string rootBones = scanSummary.RootBoneNames.Count > 0
                ? string.Join(", ", scanSummary.RootBoneNames.ToArray())
                : "なし";

            EditorGUILayout.HelpBox(
                "対象: " + prefabRoot.name +
                "\n種類: " + sourceKind +
                "\n検出メッシュ: " + rendererCandidates.Count +
                " / Animator: " + scanSummary.AnimatorCount +
                " / Armature候補: " + scanSummary.ArmatureCount +
                "\n参照ボーン: " + scanSummary.UniqueBoneCount +
                " / Root Bone: " + rootBones +
                "\nVRCPhysBone: " + scanSummary.PhysBoneCount +
                " / VRCPhysBoneCollider: " + scanSummary.PhysBoneColliderCount,
                MessageType.None);
        }

        private void DrawRendererSelection()
        {
            EditorGUILayout.LabelField("分解するメッシュ", EditorStyles.boldLabel);

            if (prefabRoot == null)
            {
                EditorGUILayout.HelpBox("Prefabまたはアバタールートを指定してください。", MessageType.Warning);
                return;
            }

            if (rendererCandidates.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "MeshFilter + MeshRenderer、またはSkinnedMeshRendererが見つかりませんでした。",
                    MessageType.Warning);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("すべて選択"))
                {
                    selectedRendererIds.Clear();
                    for (int i = 0; i < rendererCandidates.Count; i++)
                    {
                        if (rendererCandidates[i].CanSeparate)
                        {
                            selectedRendererIds.Add(rendererCandidates[i].Id);
                        }
                    }
                }

                if (GUILayout.Button("すべて解除"))
                {
                    selectedRendererIds.Clear();
                }
            }

            rendererScrollPosition = EditorGUILayout.BeginScrollView(
                rendererScrollPosition,
                GUILayout.MinHeight(130f),
                GUILayout.MaxHeight(230f));

            for (int i = 0; i < rendererCandidates.Count; i++)
            {
                RendererCandidate candidate = rendererCandidates[i];
                bool selected = selectedRendererIds.Contains(candidate.Id);

                using (new EditorGUI.DisabledScope(!candidate.CanSeparate))
                {
                    bool newSelected = GUILayout.Toggle(
                        selected,
                        candidate.DisplayLabel,
                        GUI.skin.button,
                        GUILayout.Height(22f));
                    if (newSelected != selected)
                    {
                        SetRendererSelected(candidate.Id, newSelected);
                    }
                }

                if (!candidate.CanSeparate)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.LabelField(candidate.Error, EditorStyles.miniLabel);
                    EditorGUI.indentLevel--;
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void RefreshRendererCandidates(bool preserveSelection)
        {
            HashSet<string> previousSelection = preserveSelection
                ? new HashSet<string>(selectedRendererIds)
                : new HashSet<string>();

            rendererCandidates.Clear();
            selectedRendererIds.Clear();
            scanSummary = new PrefabScanSummary();
            cachedPrefabRoot = prefabRoot;

            if (prefabRoot == null)
            {
                Repaint();
                return;
            }

            SkinnedMeshRenderer[] skinnedRenderers = prefabRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinnedRenderers.Length; i++)
            {
                SkinnedMeshRenderer renderer = skinnedRenderers[i];
                rendererCandidates.Add(CreateSkinnedCandidate(prefabRoot, renderer));

                if (renderer.rootBone != null && !scanSummary.RootBoneNames.Contains(renderer.rootBone.name))
                {
                    scanSummary.RootBoneNames.Add(renderer.rootBone.name);
                }

                if (renderer.bones != null)
                {
                    for (int boneIndex = 0; boneIndex < renderer.bones.Length; boneIndex++)
                    {
                        if (renderer.bones[boneIndex] != null)
                        {
                            scanSummary.UniqueBones.Add(renderer.bones[boneIndex]);
                        }
                    }
                }
            }

            MeshRenderer[] meshRenderers = prefabRoot.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < meshRenderers.Length; i++)
            {
                MeshRenderer renderer = meshRenderers[i];
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter != null)
                {
                    rendererCandidates.Add(CreateStaticCandidate(prefabRoot, renderer, filter));
                }
            }

            rendererCandidates.Sort(delegate(RendererCandidate left, RendererCandidate right)
            {
                return string.Compare(left.DisplayPath, right.DisplayPath, StringComparison.OrdinalIgnoreCase);
            });

            scanSummary.AnimatorCount = prefabRoot.GetComponentsInChildren<Animator>(true).Length;
            scanSummary.UniqueBoneCount = scanSummary.UniqueBones.Count;

            Transform[] allTransforms = prefabRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < allTransforms.Length; i++)
            {
                string lowerName = allTransforms[i].name.ToLowerInvariant();
                if (lowerName.Contains("armature") ||
                    lowerName.Contains("amature") ||
                    lowerName.Contains("skeleton") ||
                    lowerName == "rig")
                {
                    scanSummary.ArmatureCount++;
                }
            }

            Component[] components = prefabRoot.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                {
                    continue;
                }

                Type componentType = component.GetType();
                if (IsVrcPhysBoneType(componentType))
                {
                    scanSummary.PhysBoneCount++;
                }
                else if (IsVrcPhysBoneColliderType(componentType))
                {
                    scanSummary.PhysBoneColliderCount++;
                }
            }

            for (int i = 0; i < rendererCandidates.Count; i++)
            {
                RendererCandidate candidate = rendererCandidates[i];
                if (previousSelection.Contains(candidate.Id))
                {
                    selectedRendererIds.Add(candidate.Id);
                }
            }

            if (!preserveSelection && rendererCandidates.Count == 1 && rendererCandidates[0].CanSeparate)
            {
                selectedRendererIds.Add(rendererCandidates[0].Id);
            }

            Repaint();
        }

        private static RendererCandidate CreateSkinnedCandidate(
            GameObject root,
            SkinnedMeshRenderer renderer)
        {
            Mesh mesh = renderer.sharedMesh;
            string error = string.Empty;
            if (mesh == null)
            {
                error = "SkinnedMeshRendererにメッシュが設定されていません。";
            }

            return new RendererCandidate(
                BuildRendererId(root, renderer, SourceRendererType.Skinned),
                BuildDisplayPath(root.transform, renderer.transform),
                SourceRendererType.Skinned,
                renderer,
                mesh,
                error);
        }

        private static RendererCandidate CreateStaticCandidate(
            GameObject root,
            MeshRenderer renderer,
            MeshFilter filter)
        {
            Mesh mesh = filter.sharedMesh;
            string error = string.Empty;
            if (mesh == null)
            {
                error = "MeshFilterにメッシュが設定されていません。";
            }

            return new RendererCandidate(
                BuildRendererId(root, renderer, SourceRendererType.Static),
                BuildDisplayPath(root.transform, renderer.transform),
                SourceRendererType.Static,
                renderer,
                mesh,
                error);
        }

        private void SetRendererSelected(string id, bool selected)
        {
            if (selected)
            {
                if (!selectedRendererIds.Contains(id))
                {
                    selectedRendererIds.Add(id);
                }
            }
            else
            {
                selectedRendererIds.Remove(id);
            }
        }

        private bool CanSeparate(out string reason)
        {
            reason = string.Empty;

            if (prefabRoot == null)
            {
                reason = "Prefabまたはアバタールートを指定してください。";
                return false;
            }

            if (EditorUtility.IsPersistent(prefabRoot) && !PrefabUtility.IsPartOfPrefabAsset(prefabRoot))
            {
                reason = "Project内から指定する場合はPrefabまたはFBXモデルを選択してください。";
                return false;
            }

            int validSelectionCount = 0;
            for (int i = 0; i < rendererCandidates.Count; i++)
            {
                RendererCandidate candidate = rendererCandidates[i];
                if (selectedRendererIds.Contains(candidate.Id) && candidate.CanSeparate)
                {
                    validSelectionCount++;
                }
            }

            if (validSelectionCount == 0)
            {
                reason = "分解するメッシュを1つ以上選択してください。";
                return false;
            }


            return true;
        }

        private void SeparateSelectedRenderers()
        {
            RefreshRendererCandidates(true);
            if (!CanSeparate(out string reason))
            {
                EditorUtility.DisplayDialog("分解できません", reason, "OK");
                return;
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("NN - Object Bunkai Toolでオブジェクトを分解");

            EndGeneratedPreviewDisplay(true, false);

            GameObject workingRoot = prefabRoot;
            GameObject prefabTemplateRoot = null;
            bool instantiatedPrefab = false;
            List<string> warnings = new List<string>();
            List<GameObject> resultRoots = new List<GameObject>();
            int totalPartCount = 0;

            try
            {
                if (EditorUtility.IsPersistent(prefabRoot))
                {
                    UnityEngine.Object instantiated = PrefabUtility.InstantiatePrefab(prefabRoot);
                    workingRoot = instantiated as GameObject;
                    if (workingRoot == null)
                    {
                        throw new InvalidOperationException("Prefabをシーンへ複製できませんでした。");
                    }

                    instantiatedPrefab = true;
                    workingRoot.name = prefabRoot.name + "_ObjectBunkai";
                    workingRoot.hideFlags = HideFlags.HideInHierarchy |
                                            HideFlags.DontSaveInEditor |
                                            HideFlags.DontSaveInBuild;
                }

                // 個別Prefab用に、分解結果を追加する前の元階層を一時保存します。
                // SkinnedMeshRendererのPrefabにはこの階層からArmatureやPhysBoneを含め、
                // 単体でHierarchyへ配置しても表示できる状態にします。
                prefabTemplateRoot = UnityEngine.Object.Instantiate(workingRoot);
                prefabTemplateRoot.name = workingRoot.name + "_ObjectBunkaiPrefabTemplate";
                prefabTemplateRoot.hideFlags = HideFlags.HideAndDontSave;
                prefabTemplateRoot.SetActive(false);

                EnsureFolderExists(DefaultOutputFolder);

                List<string> idsToProcess = new List<string>(selectedRendererIds);
                for (int selectionIndex = 0; selectionIndex < idsToProcess.Count; selectionIndex++)
                {
                    string rendererId = idsToProcess[selectionIndex];
                    RendererCandidate candidate = ResolveRendererCandidate(workingRoot, rendererId);
                    if (candidate == null || !candidate.CanSeparate)
                    {
                        warnings.Add("選択したメッシュを取得できませんでした: " + rendererId);
                        continue;
                    }

                    float baseProgress = (float)selectionIndex / Math.Max(1, idsToProcess.Count);
                    EditorUtility.DisplayProgressBar(
                        "オブジェクト分解",
                        candidate.DisplayPath + " を解析しています…",
                        baseProgress);

                    Mesh sourceMesh = candidate.Mesh;
                    Renderer sourceRenderer = candidate.Renderer;
                    List<ConnectedPart> parts = separationMode == SeparationMode.ConnectedParts
                        ? MeshSeparatorUtility.FindConnectedParts(sourceMesh, linkedVertexTolerance)
                        : MeshSeparatorUtility.FindStrictConnectedParts(sourceMesh);

                    if (parts.Count == 0 || parts[0].UsedVertices.Count == 0)
                    {
                        warnings.Add(candidate.DisplayPath + ": 処理できる三角形がありませんでした。");
                        continue;
                    }

                    string resultFolder = CreateUniqueFolder(
                        DefaultOutputFolder,
                        sourceRenderer.gameObject.name + "_nekoneko");
                    string meshesFolder = CreateChildFolder(resultFolder, "Meshes");
                    string prefabsFolder = CreateChildFolder(resultFolder, "Prefabs");
                    string materialsFolder = duplicateMaterials
                        ? CreateChildFolder(resultFolder, "Materials")
                        : null;

                    Dictionary<Material, Material> duplicatedMaterialMap =
                        new Dictionary<Material, Material>();
                    GameObject resultRoot = CreateResultRoot(sourceRenderer.gameObject);
                    resultRoots.Add(resultRoot);
                    Material[] sourceMaterials = sourceRenderer.sharedMaterials;

                    for (int partIndex = 0; partIndex < parts.Count; partIndex++)
                    {
                        float selectionProgress = (float)partIndex / Math.Max(1, parts.Count);
                        float progress = ((float)selectionIndex + selectionProgress) /
                                         Math.Max(1, idsToProcess.Count);
                        EditorUtility.DisplayProgressBar(
                            "オブジェクト分解",
                            separationMode == SeparationMode.ConnectedParts
                                ? string.Format(
                                    "{0}: パーツを生成しています… {1}/{2}",
                                    candidate.DisplayPath,
                                    partIndex + 1,
                                    parts.Count)
                                : string.Format(
                                    "{0}: オブジェクト内のパーツを生成しています… {1}/{2}",
                                    candidate.DisplayPath,
                                    partIndex + 1,
                                    parts.Count),
                            progress);

                        string partName = separationMode == SeparationMode.ConnectedParts
                            ? sourceRenderer.gameObject.name +
                              "_Part_" + (partIndex + 1).ToString("000")
                            : sourceRenderer.gameObject.name +
                              "_ObjectPart_" + (partIndex + 1).ToString("000");
                        HashSet<int> partUsedBoneIndices =
                            candidate.RendererType == SourceRendererType.Skinned
                                ? GetUsedBoneIndicesForVertices(
                                    sourceMesh,
                                    parts[partIndex].UsedVertices)
                                : null;

                        Mesh partMesh = MeshSeparatorUtility.CreatePartMesh(
                            sourceMesh,
                            parts[partIndex],
                            partName);

                        string meshAssetPath = AssetDatabase.GenerateUniqueAssetPath(
                            meshesFolder + "/" + SanitizeFileName(partName) + ".asset");
                        AssetDatabase.CreateAsset(partMesh, meshAssetPath);

                        GameObject partObject = new GameObject(partName);
                        Undo.RegisterCreatedObjectUndo(partObject, "分解パーツを生成");
                        partObject.transform.SetParent(resultRoot.transform, false);
                        partObject.layer = sourceRenderer.gameObject.layer;
                        partObject.tag = GetOutputTag(sourceRenderer.gameObject);
                        GameObjectUtility.SetStaticEditorFlags(
                            partObject,
                            GameObjectUtility.GetStaticEditorFlags(sourceRenderer.gameObject));

                        Material[] partMaterials = BuildMaterialsForPart(
                            parts[partIndex],
                            sourceMaterials,
                            duplicateMaterials,
                            duplicateMaterialsPerPart,
                            partName,
                            materialsFolder,
                            duplicatedMaterialMap);

                        if (candidate.RendererType == SourceRendererType.Skinned)
                        {
                            SkinnedMeshRenderer partRenderer =
                                Undo.AddComponent<SkinnedMeshRenderer>(partObject);
                            partRenderer.sharedMesh = partMesh;
                            partRenderer.sharedMaterials = partMaterials;
                            CopySkinnedRendererSettings(
                                (SkinnedMeshRenderer)sourceRenderer,
                                partRenderer,
                                sourceMesh,
                                partMesh);
                        }
                        else
                        {
                            MeshFilter partFilter = Undo.AddComponent<MeshFilter>(partObject);
                            MeshRenderer partRenderer = Undo.AddComponent<MeshRenderer>(partObject);
                            partFilter.sharedMesh = partMesh;
                            partRenderer.sharedMaterials = partMaterials;
                            CopyMeshRendererSettings((MeshRenderer)sourceRenderer, partRenderer);
                        }

                        SavePartPrefab(
                            partObject,
                            prefabsFolder,
                            partName,
                            candidate.RendererType,
                            sourceRenderer,
                            workingRoot,
                            prefabTemplateRoot,
                            partUsedBoneIndices,
                            warnings,
                            candidate.DisplayPath);

                        totalPartCount++;
                    }

                    if (hideOriginalObject)
                    {
                        if (!TryHideOriginalObject(
                                workingRoot,
                                sourceRenderer,
                                out string hideWarning))
                        {
                            warnings.Add(candidate.DisplayPath + ": " + hideWarning);
                        }
                    }
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                previewParts.Clear();
                previewScrollPosition = Vector2.zero;
                RefreshGeneratedPreview();
                activePage = ToolPage.GeneratedPreview;
                mainScrollPosition = Vector2.zero;
                Repaint();

                string message = string.Format(
                    "処理が完了し、合計{0}個の結果を生成しました。\n\n" +
                    "メッシュとマテリアル設定済みPrefabの保存先:\n{1}",
                    totalPartCount,
                    DefaultOutputFolder);

                if (warnings.Count > 0)
                {
                    message += "\n\n注意:\n・" + string.Join("\n・", warnings.ToArray());
                }

                EditorUtility.DisplayDialog("分解完了", message, "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog(
                    "分解エラー",
                    "分解中にエラーが発生しました。Consoleを確認してください。\n\n" + exception.Message,
                    "OK");
            }
            finally
            {
                if (prefabTemplateRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(prefabTemplateRoot);
                }

                for (int i = 0; i < resultRoots.Count; i++)
                {
                    if (resultRoots[i] != null)
                    {
                        UnityEngine.Object.DestroyImmediate(resultRoots[i]);
                    }
                }

                if (instantiatedPrefab && workingRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(workingRoot);
                }

                Undo.CollapseUndoOperations(undoGroup);
                EditorUtility.ClearProgressBar();
            }
        }

        private static RendererCandidate ResolveRendererCandidate(GameObject root, string id)
        {
            if (root == null || string.IsNullOrEmpty(id))
            {
                return null;
            }

            string[] sections = id.Split('|');
            if (sections.Length != 3)
            {
                return null;
            }

            SourceRendererType rendererType;
            if (sections[0] == "S")
            {
                rendererType = SourceRendererType.Skinned;
            }
            else if (sections[0] == "M")
            {
                rendererType = SourceRendererType.Static;
            }
            else
            {
                return null;
            }

            if (!int.TryParse(sections[1], out int componentIndex))
            {
                return null;
            }

            Transform targetTransform = FindTransformByIndexPath(root.transform, sections[2]);
            if (targetTransform == null)
            {
                return null;
            }

            if (rendererType == SourceRendererType.Skinned)
            {
                SkinnedMeshRenderer[] renderers = targetTransform.GetComponents<SkinnedMeshRenderer>();
                if (componentIndex < 0 || componentIndex >= renderers.Length)
                {
                    return null;
                }

                return CreateSkinnedCandidate(root, renderers[componentIndex]);
            }

            MeshRenderer[] meshRenderers = targetTransform.GetComponents<MeshRenderer>();
            if (componentIndex < 0 || componentIndex >= meshRenderers.Length)
            {
                return null;
            }

            MeshFilter filter = targetTransform.GetComponent<MeshFilter>();
            if (filter == null)
            {
                return null;
            }

            return CreateStaticCandidate(root, meshRenderers[componentIndex], filter);
        }

        private static string BuildRendererId(
            GameObject root,
            Renderer renderer,
            SourceRendererType rendererType)
        {
            int componentIndex = 0;
            if (rendererType == SourceRendererType.Skinned)
            {
                SkinnedMeshRenderer[] components = renderer.GetComponents<SkinnedMeshRenderer>();
                for (int i = 0; i < components.Length; i++)
                {
                    if (components[i] == renderer)
                    {
                        componentIndex = i;
                        break;
                    }
                }
            }
            else
            {
                MeshRenderer[] components = renderer.GetComponents<MeshRenderer>();
                for (int i = 0; i < components.Length; i++)
                {
                    if (components[i] == renderer)
                    {
                        componentIndex = i;
                        break;
                    }
                }
            }

            string typePrefix = rendererType == SourceRendererType.Skinned ? "S" : "M";
            return typePrefix + "|" + componentIndex + "|" +
                   BuildTransformIndexPath(root.transform, renderer.transform);
        }

        private static string BuildTransformIndexPath(Transform root, Transform target)
        {
            if (root == target)
            {
                return string.Empty;
            }

            List<int> indices = new List<int>();
            Transform current = target;
            while (current != null && current != root)
            {
                indices.Add(current.GetSiblingIndex());
                current = current.parent;
            }

            if (current != root)
            {
                return string.Empty;
            }

            indices.Reverse();
            string[] sections = new string[indices.Count];
            for (int i = 0; i < indices.Count; i++)
            {
                sections[i] = indices[i].ToString();
            }

            return string.Join(".", sections);
        }

        private static Transform FindTransformByIndexPath(Transform root, string indexPath)
        {
            if (string.IsNullOrEmpty(indexPath))
            {
                return root;
            }

            string[] sections = indexPath.Split('.');
            Transform current = root;
            for (int i = 0; i < sections.Length; i++)
            {
                if (!int.TryParse(sections[i], out int childIndex) ||
                    childIndex < 0 || childIndex >= current.childCount)
                {
                    return null;
                }

                current = current.GetChild(childIndex);
            }

            return current;
        }

        private static string BuildDisplayPath(Transform root, Transform target)
        {
            if (root == target)
            {
                return root.name + "（ルート）";
            }

            List<string> names = new List<string>();
            Transform current = target;
            while (current != null && current != root)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return root.name + "/" + string.Join("/", names.ToArray());
        }

        private static bool TryHideOriginalObject(
            GameObject prefabRootObject,
            Renderer sourceRenderer,
            out string warning)
        {
            warning = string.Empty;
            GameObject sourceObject = sourceRenderer.gameObject;

            if (sourceObject == prefabRootObject)
            {
                warning = "ルートオブジェクトを非アクティブにするとArmature全体が停止するため、元オブジェクトは表示したままにしました。";
                return false;
            }

            SkinnedMeshRenderer skinnedRenderer = sourceRenderer as SkinnedMeshRenderer;
            if (skinnedRenderer != null && skinnedRenderer.bones != null)
            {
                for (int i = 0; i < skinnedRenderer.bones.Length; i++)
                {
                    Transform bone = skinnedRenderer.bones[i];
                    if (bone != null &&
                        (bone == sourceObject.transform || bone.IsChildOf(sourceObject.transform)))
                    {
                        warning = "元オブジェクトの子に使用中のボーンがあるため、ボーン追従を守るため非表示化を行いませんでした。";
                        return false;
                    }
                }
            }

            if (ContainsVrcDynamicsComponent(sourceObject))
            {
                warning = "元オブジェクトまたはその子にVRCPhysBone / Colliderがあるため、動作を守るため非表示化を行いませんでした。";
                return false;
            }

            if (IsReferencedByVrcDynamics(prefabRootObject, sourceObject.transform))
            {
                warning = "VRCPhysBone / Colliderから元オブジェクト配下が参照されているため、非表示化を行いませんでした。";
                return false;
            }

            Undo.RecordObject(sourceObject, "元オブジェクトを非表示");
            sourceObject.tag = "EditorOnly";
            sourceObject.SetActive(false);
            EditorUtility.SetDirty(sourceObject);
            return true;
        }

        private static bool ContainsVrcDynamicsComponent(GameObject sourceObject)
        {
            Component[] components = sourceObject.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] == null)
                {
                    continue;
                }

                Type type = components[i].GetType();
                if (IsVrcPhysBoneType(type) || IsVrcPhysBoneColliderType(type))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsReferencedByVrcDynamics(GameObject prefabRootObject, Transform sourceTransform)
        {
            Component[] components = prefabRootObject.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                {
                    continue;
                }

                Type type = component.GetType();
                if (!IsVrcPhysBoneType(type) && !IsVrcPhysBoneColliderType(type))
                {
                    continue;
                }

                try
                {
                    SerializedObject serializedObject = new SerializedObject(component);
                    SerializedProperty property = serializedObject.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference)
                        {
                            continue;
                        }

                        Transform referencedTransform = GetTransformFromObject(property.objectReferenceValue);
                        if (referencedTransform != null &&
                            (referencedTransform == sourceTransform ||
                             referencedTransform.IsChildOf(sourceTransform)))
                        {
                            return true;
                        }
                    }
                }
                catch
                {
                    // SDKのバージョン差などでSerializedObjectを読めない場合は、
                    // コンポーネント自体を変更しないため処理を続行します。
                }
            }

            return false;
        }

        private static Transform GetTransformFromObject(UnityEngine.Object value)
        {
            if (value is Transform transform)
            {
                return transform;
            }

            if (value is GameObject gameObject)
            {
                return gameObject.transform;
            }

            if (value is Component component)
            {
                return component.transform;
            }

            return null;
        }

        private static bool IsVrcPhysBoneType(Type type)
        {
            return type != null &&
                   (string.Equals(type.Name, "VRCPhysBone", StringComparison.Ordinal) ||
                    (!string.IsNullOrEmpty(type.FullName) &&
                     type.FullName.EndsWith(".VRCPhysBone", StringComparison.Ordinal)));
        }

        private static bool IsVrcPhysBoneColliderType(Type type)
        {
            return type != null &&
                   (string.Equals(type.Name, "VRCPhysBoneCollider", StringComparison.Ordinal) ||
                    (!string.IsNullOrEmpty(type.FullName) &&
                     type.FullName.EndsWith(".VRCPhysBoneCollider", StringComparison.Ordinal)));
        }

        private static GameObject CreateResultRoot(GameObject source)
        {
            GameObject root = new GameObject(source.name + "_nekoneko");
            root.hideFlags = HideFlags.HideInHierarchy |
                             HideFlags.DontSaveInEditor |
                             HideFlags.DontSaveInBuild;

            Transform sourceTransform = source.transform;
            Transform rootTransform = root.transform;
            rootTransform.SetParent(sourceTransform.parent, false);
            rootTransform.localPosition = sourceTransform.localPosition;
            rootTransform.localRotation = sourceTransform.localRotation;
            rootTransform.localScale = sourceTransform.localScale;

            root.layer = source.layer;
            root.tag = GetOutputTag(source);
            GameObjectUtility.SetStaticEditorFlags(root, GameObjectUtility.GetStaticEditorFlags(source));
            return root;
        }

        private static string GetOutputTag(GameObject source)
        {
            return source.CompareTag("EditorOnly") ? "Untagged" : source.tag;
        }

        private static void SavePartPrefab(
            GameObject partObject,
            string prefabsFolder,
            string partName,
            SourceRendererType rendererType,
            Renderer sourceRenderer,
            GameObject workingRoot,
            GameObject prefabTemplateRoot,
            HashSet<int> partUsedBoneIndices,
            List<string> warnings,
            string displayPath)
        {
            string prefabAssetPath = AssetDatabase.GenerateUniqueAssetPath(
                prefabsFolder + "/" + SanitizeFileName(partName) + ".prefab");

            GameObject prefabSource = null;
            bool success = false;

            try
            {
                if (rendererType == SourceRendererType.Skinned)
                {
                    prefabSource = CreateStandaloneSkinnedPrefabSource(
                        partObject,
                        partName,
                        sourceRenderer as SkinnedMeshRenderer,
                        workingRoot,
                        prefabTemplateRoot,
                        partUsedBoneIndices,
                        warnings,
                        displayPath);
                }
                else
                {
                    prefabSource = UnityEngine.Object.Instantiate(partObject);
                    prefabSource.name = partName;
                    prefabSource.transform.SetParent(null, false);
                    prefabSource.transform.localPosition = sourceRenderer.transform.localPosition;
                    prefabSource.transform.localRotation = sourceRenderer.transform.localRotation;
                    prefabSource.transform.localScale = sourceRenderer.transform.localScale;
                }

                if (prefabSource == null)
                {
                    warnings.Add(displayPath + ": Prefab生成用オブジェクトを作成できませんでした。");
                    return;
                }

                PrefabUtility.SaveAsPrefabAsset(prefabSource, prefabAssetPath, out success);
            }
            finally
            {
                if (prefabSource != null)
                {
                    UnityEngine.Object.DestroyImmediate(prefabSource);
                }
            }

            if (!success)
            {
                warnings.Add(displayPath + ": Prefabを保存できませんでした: " + prefabAssetPath);
            }
        }

        private static GameObject CreateStandaloneSkinnedPrefabSource(
            GameObject partObject,
            string partName,
            SkinnedMeshRenderer sourceRenderer,
            GameObject workingRoot,
            GameObject prefabTemplateRoot,
            HashSet<int> partUsedBoneIndices,
            List<string> warnings,
            string displayPath)
        {
            if (sourceRenderer == null || workingRoot == null || prefabTemplateRoot == null)
            {
                return null;
            }

            // 元Prefab全体の複製を土台にすることで、Armature、ボーン参照、
            // VRCPhysBone、VRCPhysBoneColliderの内部参照をUnityに再マッピングさせます。
            GameObject prefabSource = UnityEngine.Object.Instantiate(prefabTemplateRoot);
            prefabSource.hideFlags = HideFlags.None;
            prefabSource.name = partName;
            prefabSource.SetActive(true);
            prefabSource.transform.SetParent(null, false);

            string sourcePath = AnimationUtility.CalculateTransformPath(
                sourceRenderer.transform,
                workingRoot.transform);
            Transform targetTransform = string.IsNullOrEmpty(sourcePath)
                ? prefabSource.transform
                : prefabSource.transform.Find(sourcePath);

            if (targetTransform == null)
            {
                warnings.Add(displayPath + ": 個別Prefab内で元メッシュの階層を見つけられませんでした。");
                UnityEngine.Object.DestroyImmediate(prefabSource);
                return null;
            }

            int componentIndex = GetRendererComponentIndex(sourceRenderer);
            SkinnedMeshRenderer[] targetRenderers =
                targetTransform.GetComponents<SkinnedMeshRenderer>();
            if (componentIndex < 0 || componentIndex >= targetRenderers.Length)
            {
                warnings.Add(displayPath + ": 個別Prefab内でSkinnedMeshRendererを見つけられませんでした。");
                UnityEngine.Object.DestroyImmediate(prefabSource);
                return null;
            }

            SkinnedMeshRenderer prefabRenderer = targetRenderers[componentIndex];
            SkinnedMeshRenderer partRenderer = partObject.GetComponent<SkinnedMeshRenderer>();
            if (partRenderer == null)
            {
                warnings.Add(displayPath + ": 分解後のSkinnedMeshRendererを取得できませんでした。");
                UnityEngine.Object.DestroyImmediate(prefabSource);
                return null;
            }

            // 複製した元Rendererは、Instantiateによって複製Armatureへ正しく再接続済みです。
            // メッシュとマテリアルだけを分解パーツのものへ差し替えます。
            prefabRenderer.sharedMesh = partRenderer.sharedMesh;
            prefabRenderer.sharedMaterials = partRenderer.sharedMaterials;
            prefabRenderer.enabled = true;
            prefabRenderer.localBounds = partRenderer.localBounds;

            CopyBlendShapeWeights(partRenderer, prefabRenderer);

            // 対象パーツ以外の表示オブジェクトは、RendererだけでなくGameObjectごと除外します。
            // 以前はRendererコンポーネントだけを削除していたため、Body / Hair / Outfit等の
            // 空GameObjectが個別Part Prefabに大量に残っていました。
            RemoveUnrelatedRenderedObjects(prefabSource, prefabRenderer, partUsedBoneIndices);

            // 元の非表示状態に左右されず、個別Prefabを配置したときに表示できるようにします。
            Transform current = targetTransform;
            while (current != null)
            {
                current.gameObject.SetActive(true);
                if (current == prefabSource.transform)
                {
                    break;
                }
                current = current.parent;
            }

            // Avatar DescriptorやPipeline Managerなど、個別パーツPrefabに不要で
            // 重複エラーの原因になるコンポーネントを削除します。
            RemoveAvatarRootOnlyComponents(prefabSource, prefabRenderer);

            // 個別Part Prefabはホワイトリスト方式で整理します。
            // Part本体、実際に使用するBoneと親階層、関連するPhysBone、
            // そのPhysBoneが参照するColliderだけを残します。
            // PhysBone Root以下を丸ごと残さず、使用Boneへの経路だけを維持するため、
            // AvatarDynamicsやArmature配下の無関係なBody/Hair/Outfit等は削除されます。
            PruneStandalonePartPrefabRig(prefabSource, prefabRenderer, partUsedBoneIndices);
            return prefabSource;
        }

        private static void RemoveUnrelatedRenderedObjects(
            GameObject prefabRoot,
            SkinnedMeshRenderer keptRenderer,
            HashSet<int> explicitUsedBoneIndices)
        {
            if (prefabRoot == null || keptRenderer == null)
            {
                return;
            }

            HashSet<Transform> protectedTransforms = new HashSet<Transform>();
            AddTransformAndAncestors(
                protectedTransforms,
                keptRenderer.transform,
                prefabRoot.transform);

            if (keptRenderer.rootBone != null)
            {
                AddTransformAndAncestors(
                    protectedTransforms,
                    keptRenderer.rootBone,
                    prefabRoot.transform);
            }

            Transform[] bones = keptRenderer.bones ?? Array.Empty<Transform>();
            HashSet<int> usedBoneIndices = explicitUsedBoneIndices != null
                ? new HashSet<int>(explicitUsedBoneIndices)
                : GetActuallyUsedBoneIndices(keptRenderer.sharedMesh);
            if (usedBoneIndices.Count == 0 && explicitUsedBoneIndices == null)
            {
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] != null)
                    {
                        AddTransformAndAncestors(
                            protectedTransforms,
                            bones[i],
                            prefabRoot.transform);
                    }
                }
            }
            else
            {
                foreach (int boneIndex in usedBoneIndices)
                {
                    if (boneIndex >= 0 && boneIndex < bones.Length && bones[boneIndex] != null)
                    {
                        AddTransformAndAncestors(
                            protectedTransforms,
                            bones[boneIndex],
                            prefabRoot.transform);
                    }
                }
            }

            Renderer[] renderers = prefabRoot.GetComponentsInChildren<Renderer>(true);
            HashSet<GameObject> removeObjects = new HashSet<GameObject>();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer == keptRenderer)
                {
                    continue;
                }

                Transform rendererTransform = renderer.transform;
                if (rendererTransform == null ||
                    protectedTransforms.Contains(rendererTransform) ||
                    IsAncestorOfAny(rendererTransform, protectedTransforms))
                {
                    // Bone階層など、必要Transformの親として使われている場合は
                    // GameObject自体を消さずRendererだけ除去します。
                    MeshRenderer meshRenderer = renderer as MeshRenderer;
                    if (meshRenderer != null)
                    {
                        MeshFilter filter = meshRenderer.GetComponent<MeshFilter>();
                        if (filter != null)
                        {
                            UnityEngine.Object.DestroyImmediate(filter);
                        }
                    }
                    UnityEngine.Object.DestroyImmediate(renderer);
                    continue;
                }

                removeObjects.Add(renderer.gameObject);
            }

            foreach (GameObject removeObject in removeObjects)
            {
                if (removeObject != null && removeObject != prefabRoot)
                {
                    UnityEngine.Object.DestroyImmediate(removeObject);
                }
            }
        }

        private static bool IsAncestorOfAny(
            Transform possibleAncestor,
            HashSet<Transform> transforms)
        {
            if (possibleAncestor == null || transforms == null)
            {
                return false;
            }

            foreach (Transform value in transforms)
            {
                if (value != null && value != possibleAncestor && value.IsChildOf(possibleAncestor))
                {
                    return true;
                }
            }
            return false;
        }

        private static int GetRendererComponentIndex(SkinnedMeshRenderer sourceRenderer)
        {
            if (sourceRenderer == null)
            {
                return -1;
            }

            SkinnedMeshRenderer[] components =
                sourceRenderer.GetComponents<SkinnedMeshRenderer>();
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] == sourceRenderer)
                {
                    return i;
                }
            }

            return -1;
        }

        private static void CopyBlendShapeWeights(
            SkinnedMeshRenderer source,
            SkinnedMeshRenderer destination)
        {
            if (source == null || destination == null ||
                source.sharedMesh == null || destination.sharedMesh == null)
            {
                return;
            }

            Mesh sourceMesh = source.sharedMesh;
            Mesh destinationMesh = destination.sharedMesh;
            for (int destinationShapeIndex = 0;
                 destinationShapeIndex < destinationMesh.blendShapeCount;
                 destinationShapeIndex++)
            {
                string shapeName = destinationMesh.GetBlendShapeName(destinationShapeIndex);
                int sourceShapeIndex = sourceMesh.GetBlendShapeIndex(shapeName);
                if (sourceShapeIndex >= 0)
                {
                    destination.SetBlendShapeWeight(
                        destinationShapeIndex,
                        source.GetBlendShapeWeight(sourceShapeIndex));
                }
            }
        }

        private static void RemoveAvatarRootOnlyComponents(
            GameObject prefabSource,
            SkinnedMeshRenderer keptRenderer)
        {
            Component[] components = prefabSource.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || component is Transform || component == keptRenderer)
                {
                    continue;
                }

                Type type = component.GetType();
                string typeName = type.Name ?? string.Empty;
                string fullName = type.FullName ?? string.Empty;

                bool isAvatarDescriptor =
                    typeName == "VRCAvatarDescriptor" ||
                    fullName.EndsWith(".VRCAvatarDescriptor", StringComparison.Ordinal);
                bool isPipelineManager =
                    typeName == "PipelineManager" ||
                    fullName.EndsWith(".PipelineManager", StringComparison.Ordinal);

                if (isAvatarDescriptor || isPipelineManager)
                {
                    UnityEngine.Object.DestroyImmediate(component);
                }
            }
        }

        private static Material[] BuildMaterialsForPart(
            ConnectedPart part,
            Material[] sourceMaterials,
            bool shouldDuplicate,
            bool duplicatePerPart,
            string partName,
            string materialsFolder,
            Dictionary<Material, Material> sharedDuplicatedMaterialMap)
        {
            Material[] result = new Material[part.UsedSubmeshes.Count];
            Dictionary<Material, Material> partDuplicatedMaterialMap = duplicatePerPart
                ? new Dictionary<Material, Material>()
                : sharedDuplicatedMaterialMap;

            for (int i = 0; i < part.UsedSubmeshes.Count; i++)
            {
                int sourceSubmeshIndex = part.UsedSubmeshes[i];
                Material sourceMaterial = sourceSubmeshIndex < sourceMaterials.Length
                    ? sourceMaterials[sourceSubmeshIndex]
                    : null;

                if (!shouldDuplicate || sourceMaterial == null)
                {
                    result[i] = sourceMaterial;
                    continue;
                }

                if (!partDuplicatedMaterialMap.TryGetValue(sourceMaterial, out Material duplicatedMaterial))
                {
                    string duplicatedName = duplicatePerPart
                        ? sourceMaterial.name + "_" + partName
                        : sourceMaterial.name + "_Copy";

                    duplicatedMaterial = new Material(sourceMaterial)
                    {
                        name = duplicatedName
                    };

                    string materialAssetPath = AssetDatabase.GenerateUniqueAssetPath(
                        materialsFolder + "/" + SanitizeFileName(duplicatedName) + ".mat");
                    AssetDatabase.CreateAsset(duplicatedMaterial, materialAssetPath);
                    partDuplicatedMaterialMap.Add(sourceMaterial, duplicatedMaterial);
                }

                result[i] = duplicatedMaterial;
            }

            return result;
        }

        private static void CopyCommonRendererSettings(Renderer source, Renderer destination)
        {
            destination.shadowCastingMode = source.shadowCastingMode;
            destination.receiveShadows = source.receiveShadows;
            destination.lightProbeUsage = source.lightProbeUsage;
            destination.reflectionProbeUsage = source.reflectionProbeUsage;
            destination.probeAnchor = source.probeAnchor;
            destination.motionVectorGenerationMode = source.motionVectorGenerationMode;
            destination.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
            destination.renderingLayerMask = source.renderingLayerMask;
            destination.sortingLayerID = source.sortingLayerID;
            destination.sortingOrder = source.sortingOrder;
            destination.enabled = source.enabled;
        }

        private static void CopyMeshRendererSettings(MeshRenderer source, MeshRenderer destination)
        {
            CopyCommonRendererSettings(source, destination);
            destination.lightmapIndex = source.lightmapIndex;
            destination.realtimeLightmapIndex = source.realtimeLightmapIndex;
            destination.lightmapScaleOffset = source.lightmapScaleOffset;
            destination.realtimeLightmapScaleOffset = source.realtimeLightmapScaleOffset;
            destination.scaleInLightmap = source.scaleInLightmap;
            destination.receiveGI = source.receiveGI;
            destination.stitchLightmapSeams = source.stitchLightmapSeams;
        }

        private static void CopySkinnedRendererSettings(
            SkinnedMeshRenderer source,
            SkinnedMeshRenderer destination,
            Mesh sourceMesh,
            Mesh destinationMesh)
        {
            CopyCommonRendererSettings(source, destination);
            destination.bones = source.bones;
            destination.rootBone = source.rootBone;
            destination.quality = source.quality;
            destination.updateWhenOffscreen = source.updateWhenOffscreen;
            destination.skinnedMotionVectors = source.skinnedMotionVectors;
            destination.localBounds = source.localBounds;

            for (int destinationShapeIndex = 0;
                 destinationShapeIndex < destinationMesh.blendShapeCount;
                 destinationShapeIndex++)
            {
                string shapeName = destinationMesh.GetBlendShapeName(destinationShapeIndex);
                int sourceShapeIndex = sourceMesh.GetBlendShapeIndex(shapeName);
                if (sourceShapeIndex >= 0)
                {
                    destination.SetBlendShapeWeight(
                        destinationShapeIndex,
                        source.GetBlendShapeWeight(sourceShapeIndex));
                }
            }
        }

        private static void EnsureFolderExists(string assetFolder)
        {
            string normalized = assetFolder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(normalized))
            {
                return;
            }

            string[] segments = normalized.Split('/');
            if (segments.Length == 0 || segments[0] != "Assets")
            {
                throw new InvalidOperationException("保存先はAssetsフォルダー内である必要があります。");
            }

            string current = "Assets";
            for (int i = 1; i < segments.Length; i++)
            {
                string next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, segments[i]);
                }

                current = next;
            }
        }

        private static string CreateUniqueFolder(string parentFolder, string requestedName)
        {
            string safeName = SanitizeFileName(requestedName);
            string candidate = parentFolder.TrimEnd('/') + "/" + safeName;
            int suffix = 1;

            while (AssetDatabase.IsValidFolder(candidate))
            {
                candidate = parentFolder.TrimEnd('/') + "/" + safeName + "_" + suffix.ToString("000");
                suffix++;
            }

            string guid = AssetDatabase.CreateFolder(parentFolder.TrimEnd('/'), Path.GetFileName(candidate));
            string createdPath = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(createdPath))
            {
                throw new IOException("出力フォルダーを作成できませんでした。");
            }

            return createdPath;
        }

        private static string CreateChildFolder(string parentFolder, string folderName)
        {
            string childPath = parentFolder.TrimEnd('/') + "/" + folderName;
            if (!AssetDatabase.IsValidFolder(childPath))
            {
                AssetDatabase.CreateFolder(parentFolder, folderName);
            }

            return childPath;
        }

        private sealed class GeneratedPrefabEntry
        {
            public readonly string AssetPath;
            public readonly string GroupPath;
            public readonly string GroupName;
            public readonly string AssetName;

            public GeneratedPrefabEntry(
                string assetPath,
                string groupPath,
                string groupName,
                string assetName)
            {
                AssetPath = assetPath ?? string.Empty;
                GroupPath = groupPath ?? string.Empty;
                GroupName = groupName ?? string.Empty;
                AssetName = assetName ?? string.Empty;
            }
        }

        private sealed class PreviewPartEntry
        {
            public readonly GameObject PartObject;
            public readonly string SourcePath;

            public PreviewPartEntry(GameObject partObject, string sourcePath)
            {
                PartObject = partObject;
                SourcePath = sourcePath ?? string.Empty;
            }
        }

        private static string SanitizeFileName(string value)
        {
            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalidCharacter, '_');
            }

            return string.IsNullOrWhiteSpace(value) ? "NekonekoObject" : value;
        }
    }

    internal enum ToolPage
    {
        TargetSelection,
        Settings,
        GeneratedPreview
    }

    internal enum SeparationMode
    {
        ConnectedParts,
        ObjectUnit
    }

    internal enum SourceRendererType
    {
        None,
        Static,
        Skinned
    }

    internal sealed class RendererCandidate
    {
        public readonly string Id;
        public readonly string DisplayPath;
        public readonly SourceRendererType RendererType;
        public readonly Renderer Renderer;
        public readonly Mesh Mesh;
        public readonly string Error;

        public RendererCandidate(
            string id,
            string displayPath,
            SourceRendererType rendererType,
            Renderer renderer,
            Mesh mesh,
            string error)
        {
            Id = id;
            DisplayPath = displayPath;
            RendererType = rendererType;
            Renderer = renderer;
            Mesh = mesh;
            Error = error;
        }

        public bool CanSeparate
        {
            get { return Mesh != null && string.IsNullOrEmpty(Error); }
        }

        public string DisplayLabel
        {
            get { return DisplayPath; }
        }
    }

    internal sealed class PrefabScanSummary
    {
        public int AnimatorCount;
        public int ArmatureCount;
        public int UniqueBoneCount;
        public int PhysBoneCount;
        public int PhysBoneColliderCount;
        public readonly HashSet<Transform> UniqueBones = new HashSet<Transform>();
        public readonly List<string> RootBoneNames = new List<string>();
    }

    internal sealed class ConnectedPart
    {
        public readonly Dictionary<int, List<int>> TrianglesBySubmesh = new Dictionary<int, List<int>>();
        public readonly List<int> UsedSubmeshes = new List<int>();
        public readonly HashSet<int> UsedVertices = new HashSet<int>();
    }

    internal readonly struct PositionKey : IEquatable<PositionKey>
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;

        public PositionKey(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static PositionKey FromPosition(Vector3 position, float tolerance)
        {
            return new PositionKey(
                Mathf.FloorToInt(position.x / tolerance),
                Mathf.FloorToInt(position.y / tolerance),
                Mathf.FloorToInt(position.z / tolerance));
        }

        public bool Equals(PositionKey other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object obj)
        {
            return obj is PositionKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X;
                hash = (hash * 397) ^ Y;
                hash = (hash * 397) ^ Z;
                return hash;
            }
        }
    }

    internal static class MeshSeparatorUtility
    {
        public static ConnectedPart CreateWholeMeshPart(Mesh mesh)
        {
            ConnectedPart part = new ConnectedPart();

            for (int submeshIndex = 0; submeshIndex < mesh.subMeshCount; submeshIndex++)
            {
                int[] triangles = mesh.GetTriangles(submeshIndex);
                if (triangles == null || triangles.Length == 0)
                {
                    continue;
                }

                List<int> triangleList = new List<int>(triangles);
                part.TrianglesBySubmesh.Add(submeshIndex, triangleList);
                part.UsedSubmeshes.Add(submeshIndex);

                for (int triangleIndex = 0; triangleIndex < triangles.Length; triangleIndex++)
                {
                    part.UsedVertices.Add(triangles[triangleIndex]);
                }
            }

            return part;
        }

        public static List<ConnectedPart> FindStrictConnectedParts(Mesh mesh)
        {
            int vertexCount = mesh.vertexCount;
            DisjointSet disjointSet = new DisjointSet(vertexCount);
            List<int[]> trianglesPerSubmesh = new List<int[]>(mesh.subMeshCount);

            // オブジェクト単位では、Unity上の頂点インデックスをそのまま使って
            // 接続判定する。UVシームや法線境界で分割された頂点は結合しないため、
            // 単純なオブジェクトを細かく分けたい場合に向いている。
            for (int submeshIndex = 0; submeshIndex < mesh.subMeshCount; submeshIndex++)
            {
                int[] triangles = mesh.GetTriangles(submeshIndex);
                trianglesPerSubmesh.Add(triangles);

                for (int triangleIndex = 0; triangleIndex + 2 < triangles.Length; triangleIndex += 3)
                {
                    int a = triangles[triangleIndex];
                    int b = triangles[triangleIndex + 1];
                    int c = triangles[triangleIndex + 2];
                    disjointSet.Union(a, b);
                    disjointSet.Union(b, c);
                    disjointSet.Union(c, a);
                }
            }

            Dictionary<int, ConnectedPart> partMap = new Dictionary<int, ConnectedPart>();

            for (int submeshIndex = 0; submeshIndex < trianglesPerSubmesh.Count; submeshIndex++)
            {
                int[] triangles = trianglesPerSubmesh[submeshIndex];

                for (int triangleIndex = 0; triangleIndex + 2 < triangles.Length; triangleIndex += 3)
                {
                    int a = triangles[triangleIndex];
                    int b = triangles[triangleIndex + 1];
                    int c = triangles[triangleIndex + 2];
                    int root = disjointSet.Find(a);

                    if (!partMap.TryGetValue(root, out ConnectedPart part))
                    {
                        part = new ConnectedPart();
                        partMap.Add(root, part);
                    }

                    if (!part.TrianglesBySubmesh.TryGetValue(submeshIndex, out List<int> partTriangles))
                    {
                        partTriangles = new List<int>();
                        part.TrianglesBySubmesh.Add(submeshIndex, partTriangles);
                        part.UsedSubmeshes.Add(submeshIndex);
                    }

                    partTriangles.Add(a);
                    partTriangles.Add(b);
                    partTriangles.Add(c);
                    part.UsedVertices.Add(a);
                    part.UsedVertices.Add(b);
                    part.UsedVertices.Add(c);
                }
            }

            List<ConnectedPart> result = new List<ConnectedPart>(partMap.Values);
            result.Sort((left, right) => GetSmallestVertex(left).CompareTo(GetSmallestVertex(right)));
            return result;
        }

        public static List<ConnectedPart> FindConnectedParts(Mesh mesh, float positionTolerance)
        {
            int vertexCount = mesh.vertexCount;
            DisjointSet disjointSet = new DisjointSet(vertexCount);
            List<int[]> trianglesPerSubmesh = new List<int[]>(mesh.subMeshCount);

            // Unityのインポート時にUVシームや法線境界で複製された頂点を、
            // 判定上だけ同じ頂点として扱う。実際の頂点データは変更しない。
            Vector3[] vertices = mesh.vertices;
            float tolerance = Mathf.Max(positionTolerance, 0.0000001f);
            float sqrTolerance = tolerance * tolerance;
            Dictionary<PositionKey, List<int>> positionBuckets =
                new Dictionary<PositionKey, List<int>>(vertexCount);

            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                Vector3 position = vertices[vertexIndex];
                PositionKey key = PositionKey.FromPosition(position, tolerance);
                int representative = -1;

                // 丸め境界をまたぐ頂点も拾えるよう、周囲27セルを検索する。
                for (int x = -1; x <= 1 && representative < 0; x++)
                {
                    for (int y = -1; y <= 1 && representative < 0; y++)
                    {
                        for (int z = -1; z <= 1 && representative < 0; z++)
                        {
                            PositionKey neighbor = new PositionKey(key.X + x, key.Y + y, key.Z + z);
                            if (!positionBuckets.TryGetValue(neighbor, out List<int> candidates))
                            {
                                continue;
                            }

                            for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                            {
                                int candidate = candidates[candidateIndex];
                                if ((vertices[candidate] - position).sqrMagnitude <= sqrTolerance)
                                {
                                    representative = candidate;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (representative >= 0)
                {
                    disjointSet.Union(vertexIndex, representative);
                }

                if (!positionBuckets.TryGetValue(key, out List<int> bucket))
                {
                    bucket = new List<int>();
                    positionBuckets.Add(key, bucket);
                }
                bucket.Add(vertexIndex);
            }

            for (int submeshIndex = 0; submeshIndex < mesh.subMeshCount; submeshIndex++)
            {
                int[] triangles = mesh.GetTriangles(submeshIndex);
                trianglesPerSubmesh.Add(triangles);

                for (int triangleIndex = 0; triangleIndex + 2 < triangles.Length; triangleIndex += 3)
                {
                    int a = triangles[triangleIndex];
                    int b = triangles[triangleIndex + 1];
                    int c = triangles[triangleIndex + 2];
                    disjointSet.Union(a, b);
                    disjointSet.Union(b, c);
                    disjointSet.Union(c, a);
                }
            }

            Dictionary<int, ConnectedPart> partMap = new Dictionary<int, ConnectedPart>();

            for (int submeshIndex = 0; submeshIndex < trianglesPerSubmesh.Count; submeshIndex++)
            {
                int[] triangles = trianglesPerSubmesh[submeshIndex];

                for (int triangleIndex = 0; triangleIndex + 2 < triangles.Length; triangleIndex += 3)
                {
                    int a = triangles[triangleIndex];
                    int b = triangles[triangleIndex + 1];
                    int c = triangles[triangleIndex + 2];
                    int root = disjointSet.Find(a);

                    if (!partMap.TryGetValue(root, out ConnectedPart part))
                    {
                        part = new ConnectedPart();
                        partMap.Add(root, part);
                    }

                    if (!part.TrianglesBySubmesh.TryGetValue(submeshIndex, out List<int> partTriangles))
                    {
                        partTriangles = new List<int>();
                        part.TrianglesBySubmesh.Add(submeshIndex, partTriangles);
                        part.UsedSubmeshes.Add(submeshIndex);
                    }

                    partTriangles.Add(a);
                    partTriangles.Add(b);
                    partTriangles.Add(c);
                    part.UsedVertices.Add(a);
                    part.UsedVertices.Add(b);
                    part.UsedVertices.Add(c);
                }
            }

            List<ConnectedPart> result = new List<ConnectedPart>(partMap.Values);
            result.Sort((left, right) => GetSmallestVertex(left).CompareTo(GetSmallestVertex(right)));
            return result;
        }

        public static Mesh CreatePartMesh(Mesh source, ConnectedPart part, string meshName)
        {
            List<int> sourceVertexIndices = new List<int>(part.UsedVertices);
            sourceVertexIndices.Sort();

            Dictionary<int, int> vertexRemap = new Dictionary<int, int>(sourceVertexIndices.Count);
            for (int i = 0; i < sourceVertexIndices.Count; i++)
            {
                vertexRemap[sourceVertexIndices[i]] = i;
            }

            Mesh destination = new Mesh
            {
                name = meshName,
                indexFormat = sourceVertexIndices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };

            CopyVertexData(source, destination, sourceVertexIndices);

            destination.subMeshCount = part.UsedSubmeshes.Count;
            for (int destinationSubmesh = 0; destinationSubmesh < part.UsedSubmeshes.Count; destinationSubmesh++)
            {
                int sourceSubmesh = part.UsedSubmeshes[destinationSubmesh];
                List<int> sourceTriangles = part.TrianglesBySubmesh[sourceSubmesh];
                int[] remappedTriangles = new int[sourceTriangles.Count];

                for (int i = 0; i < sourceTriangles.Count; i++)
                {
                    remappedTriangles[i] = vertexRemap[sourceTriangles[i]];
                }

                destination.SetTriangles(remappedTriangles, destinationSubmesh, false);
            }

            CopyBlendShapes(source, destination, sourceVertexIndices);
            destination.RecalculateBounds();

            if (destination.normals == null || destination.normals.Length != destination.vertexCount)
            {
                destination.RecalculateNormals();
            }

            return destination;
        }

        private static void CopyVertexData(Mesh source, Mesh destination, List<int> indices)
        {
            Vector3[] sourceVertices = source.vertices;
            Vector3[] sourceNormals = source.normals;
            Vector4[] sourceTangents = source.tangents;
            Color32[] sourceColors = source.colors32;
            destination.vertices = Select(sourceVertices, indices);

            if (sourceNormals != null && sourceNormals.Length == source.vertexCount)
            {
                destination.normals = Select(sourceNormals, indices);
            }

            if (sourceTangents != null && sourceTangents.Length == source.vertexCount)
            {
                destination.tangents = Select(sourceTangents, indices);
            }

            if (sourceColors != null && sourceColors.Length == source.vertexCount)
            {
                destination.colors32 = Select(sourceColors, indices);
            }

            CopyBoneWeights(source, destination, indices);

            for (int channel = 0; channel < 8; channel++)
            {
                List<Vector4> sourceUvs = new List<Vector4>();
                source.GetUVs(channel, sourceUvs);

                if (sourceUvs.Count != source.vertexCount)
                {
                    continue;
                }

                List<Vector4> destinationUvs = new List<Vector4>(indices.Count);
                for (int i = 0; i < indices.Count; i++)
                {
                    destinationUvs.Add(sourceUvs[indices[i]]);
                }

                destination.SetUVs(channel, destinationUvs);
            }
        }

        private static void CopyBoneWeights(Mesh source, Mesh destination, List<int> sourceVertexIndices)
        {
            destination.bindposes = source.bindposes;

            if (!source.HasVertexAttribute(VertexAttribute.BlendWeight) ||
                !source.HasVertexAttribute(VertexAttribute.BlendIndices))
            {
                BoneWeight[] legacyWeights = source.boneWeights;
                if (legacyWeights != null && legacyWeights.Length == source.vertexCount)
                {
                    destination.boneWeights = Select(legacyWeights, sourceVertexIndices);
                }
                return;
            }

            NativeArray<byte> sourceBonesPerVertex = source.GetBonesPerVertex();
            if (sourceBonesPerVertex.Length != source.vertexCount)
            {
                BoneWeight[] legacyWeights = source.boneWeights;
                if (legacyWeights != null && legacyWeights.Length == source.vertexCount)
                {
                    destination.boneWeights = Select(legacyWeights, sourceVertexIndices);
                }

                return;
            }

            NativeArray<BoneWeight1> sourceBoneWeights = source.GetAllBoneWeights();
            int[] weightStartIndices = new int[source.vertexCount + 1];
            int runningWeightIndex = 0;
            for (int vertexIndex = 0; vertexIndex < source.vertexCount; vertexIndex++)
            {
                weightStartIndices[vertexIndex] = runningWeightIndex;
                runningWeightIndex += sourceBonesPerVertex[vertexIndex];
            }

            weightStartIndices[source.vertexCount] = runningWeightIndex;

            NativeArray<byte> destinationBonesPerVertex = new NativeArray<byte>(
                sourceVertexIndices.Count,
                Allocator.Temp);

            try
            {
                int destinationWeightCount = 0;
                for (int i = 0; i < sourceVertexIndices.Count; i++)
                {
                    byte boneCount = sourceBonesPerVertex[sourceVertexIndices[i]];
                    destinationBonesPerVertex[i] = boneCount;
                    destinationWeightCount += boneCount;
                }

                NativeArray<BoneWeight1> destinationBoneWeights = new NativeArray<BoneWeight1>(
                    destinationWeightCount,
                    Allocator.Temp);

                try
                {
                    int destinationWeightIndex = 0;
                    for (int i = 0; i < sourceVertexIndices.Count; i++)
                    {
                        int sourceVertexIndex = sourceVertexIndices[i];
                        int sourceWeightIndex = weightStartIndices[sourceVertexIndex];
                        int boneCount = sourceBonesPerVertex[sourceVertexIndex];

                        for (int weightIndex = 0; weightIndex < boneCount; weightIndex++)
                        {
                            destinationBoneWeights[destinationWeightIndex++] =
                                sourceBoneWeights[sourceWeightIndex + weightIndex];
                        }
                    }

                    destination.SetBoneWeights(destinationBonesPerVertex, destinationBoneWeights);
                }
                finally
                {
                    destinationBoneWeights.Dispose();
                }
            }
            finally
            {
                destinationBonesPerVertex.Dispose();
            }
        }

        private static void CopyBlendShapes(Mesh source, Mesh destination, List<int> sourceVertexIndices)
        {
            if (source.blendShapeCount == 0)
            {
                return;
            }

            Vector3[] deltaVertices = new Vector3[source.vertexCount];
            Vector3[] deltaNormals = new Vector3[source.vertexCount];
            Vector3[] deltaTangents = new Vector3[source.vertexCount];

            for (int shapeIndex = 0; shapeIndex < source.blendShapeCount; shapeIndex++)
            {
                string shapeName = source.GetBlendShapeName(shapeIndex);
                int frameCount = source.GetBlendShapeFrameCount(shapeIndex);

                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    Array.Clear(deltaVertices, 0, deltaVertices.Length);
                    Array.Clear(deltaNormals, 0, deltaNormals.Length);
                    Array.Clear(deltaTangents, 0, deltaTangents.Length);

                    source.GetBlendShapeFrameVertices(
                        shapeIndex,
                        frameIndex,
                        deltaVertices,
                        deltaNormals,
                        deltaTangents);

                    destination.AddBlendShapeFrame(
                        shapeName,
                        source.GetBlendShapeFrameWeight(shapeIndex, frameIndex),
                        Select(deltaVertices, sourceVertexIndices),
                        Select(deltaNormals, sourceVertexIndices),
                        Select(deltaTangents, sourceVertexIndices));
                }
            }
        }

        private static T[] Select<T>(T[] source, List<int> indices)
        {
            T[] result = new T[indices.Count];
            for (int i = 0; i < indices.Count; i++)
            {
                result[i] = source[indices[i]];
            }

            return result;
        }

        private static int GetSmallestVertex(ConnectedPart part)
        {
            int smallest = int.MaxValue;
            foreach (int vertex in part.UsedVertices)
            {
                if (vertex < smallest)
                {
                    smallest = vertex;
                }
            }

            return smallest;
        }
    }

    internal sealed class DisjointSet
    {
        private readonly int[] parent;
        private readonly byte[] rank;

        public DisjointSet(int size)
        {
            parent = new int[size];
            rank = new byte[size];

            for (int i = 0; i < size; i++)
            {
                parent[i] = i;
            }
        }

        public int Find(int value)
        {
            if (parent[value] != value)
            {
                parent[value] = Find(parent[value]);
            }

            return parent[value];
        }

        public void Union(int first, int second)
        {
            int firstRoot = Find(first);
            int secondRoot = Find(second);

            if (firstRoot == secondRoot)
            {
                return;
            }

            if (rank[firstRoot] < rank[secondRoot])
            {
                parent[firstRoot] = secondRoot;
            }
            else if (rank[firstRoot] > rank[secondRoot])
            {
                parent[secondRoot] = firstRoot;
            }
            else
            {
                parent[secondRoot] = firstRoot;
                rank[firstRoot]++;
            }
        }
    }

    [InitializeOnLoad]
    internal static class NekoNekoCombinedPrefabAutoSetup
    {
        private const string CombinedPrefabsFolder =
            "Assets/nekoneko/NN - Object Bunkai Tool/Generated/CombinedPrefabs/";
        private const string MergeArmatureTypeName =
            "nadena.dev.modular_avatar.core.ModularAvatarMergeArmature";
        private const string SetupOutfitTypeName =
            "nadena.dev.modular_avatar.core.editor.SetupOutfit";
        private const string LegacySetupOutfitTypeName =
            "nadena.dev.modular_avatar.core.editor.EasySetupOutfit";
        private const string SessionKeyPrefix =
            "NekoNeko.ObjectSeparator.AutoSetup.";

        private static bool scanQueued;
        private static bool processing;

        static NekoNekoCombinedPrefabAutoSetup()
        {
            EditorApplication.hierarchyChanged += QueueScan;
            EditorApplication.delayCall += QueueScan;
        }

        private static void QueueScan()
        {
            if (scanQueued || processing || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            scanQueued = true;
            EditorApplication.delayCall += ScanHierarchy;
        }

        private static void ScanHierarchy()
        {
            scanQueued = false;
            if (processing || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            Type mergeArmatureType = FindType(MergeArmatureTypeName);
            if (mergeArmatureType == null)
            {
                return;
            }

            GameObject[] objects = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < objects.Length; i++)
            {
                GameObject candidate = objects[i];
                if (candidate == null || EditorUtility.IsPersistent(candidate))
                {
                    continue;
                }

                if (!candidate.scene.IsValid() || !candidate.scene.isLoaded)
                {
                    continue;
                }

                GameObject nearestRoot = PrefabUtility.GetNearestPrefabInstanceRoot(candidate);
                if (nearestRoot != candidate)
                {
                    continue;
                }

                string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(candidate);
                if (string.IsNullOrEmpty(prefabPath) ||
                    !prefabPath.Replace('\\', '/').StartsWith(
                        CombinedPrefabsFolder,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Component mergeArmature = FindComponentInChildren(candidate, mergeArmatureType);
                if (mergeArmature == null)
                {
                    // MA対応を有効にして作った統合Prefabだけ自動セットアップします。
                    continue;
                }

                GameObject avatarRoot = FindAvatarRoot(candidate.transform.parent);
                if (avatarRoot == null)
                {
                    continue;
                }

                string sessionKey = SessionKeyPrefix +
                    candidate.GetInstanceID() + "@" + avatarRoot.GetInstanceID();
                if (SessionState.GetBool(sessionKey, false))
                {
                    continue;
                }

                SessionState.SetBool(sessionKey, true);
                GameObject outfit = candidate;
                GameObject avatar = avatarRoot;
                EditorApplication.delayCall += () => AutoSetup(outfit, avatar, sessionKey);
            }
        }

        private static void AutoSetup(GameObject outfitRoot, GameObject avatarRoot, string sessionKey)
        {
            if (outfitRoot == null || avatarRoot == null || processing)
            {
                return;
            }

            if (!outfitRoot.transform.IsChildOf(avatarRoot.transform))
            {
                SessionState.SetBool(sessionKey, false);
                return;
            }

            Type mergeArmatureType = FindType(MergeArmatureTypeName);
            if (mergeArmatureType == null ||
                FindComponentInChildren(outfitRoot, mergeArmatureType) == null)
            {
                return;
            }

            processing = true;
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("NN - Object Bunkai Tool: 統合Prefab自動セットアップ");

            try
            {
                // アバター直下へ置いた時の基準位置へ戻してからMAセットアップを行います。
                Undo.RecordObject(outfitRoot.transform, "統合Prefabの位置をアバターに合わせる");
                outfitRoot.transform.localPosition = Vector3.zero;
                outfitRoot.transform.localRotation = Quaternion.identity;
                outfitRoot.transform.localScale = Vector3.one;
                PrefabUtility.RecordPrefabInstancePropertyModifications(outfitRoot.transform);

                InvokeSetupOutfit(outfitRoot);

                // MAの「位置を元アバターに合わせてリセット」と同じ用途の自動調整。
                // デフォルトは 全体スケール=ON / A-Tポーズ=ON / 回転=OFF / 個別スケール=OFF。
                ApplyAutomaticFit(outfitRoot, avatarRoot, mergeArmatureType);

                EditorUtility.SetDirty(outfitRoot);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(outfitRoot.scene);
                SceneView.RepaintAll();
                Debug.Log(
                    "[NN - Object Bunkai Tool] 統合Prefab『" + outfitRoot.name +
                    "』をアバター『" + avatarRoot.name + "』へ自動セットアップしました。",
                    outfitRoot);
            }
            catch (TargetInvocationException ex)
            {
                Exception source = ex.InnerException ?? ex;
                Debug.LogWarning(
                    "[NN - Object Bunkai Tool] 統合Prefabの自動セットアップを完了できませんでした。\n" +
                    source.Message,
                    outfitRoot);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[NN - Object Bunkai Tool] 統合Prefabの自動セットアップを完了できませんでした。\n" +
                    ex.Message,
                    outfitRoot);
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
                processing = false;
            }
        }

        private static void InvokeSetupOutfit(GameObject outfitRoot)
        {
            Type setupType = FindType(SetupOutfitTypeName) ?? FindType(LegacySetupOutfitTypeName);
            if (setupType == null)
            {
                return;
            }

            MethodInfo setupUI = setupType.GetMethod(
                "SetupOutfitUI",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(GameObject) },
                null);
            if (setupUI != null)
            {
                setupUI.Invoke(null, new object[] { outfitRoot });
                return;
            }

            MethodInfo setupMenu = setupType.GetMethod(
                "SetupOutfit",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(MenuCommand) },
                null);
            if (setupMenu != null)
            {
                setupMenu.Invoke(null, new object[] { new MenuCommand(outfitRoot) });
            }
        }

        private static void ApplyAutomaticFit(
            GameObject outfitRoot,
            GameObject avatarRoot,
            Type mergeArmatureType)
        {
            Component[] mergers = FindComponentsInChildren(outfitRoot, mergeArmatureType);
            if (mergers == null || mergers.Length == 0)
            {
                return;
            }

            List<BonePair> pairs = CollectBonePairs(mergers);
            if (pairs.Count == 0)
            {
                return;
            }

            // 1) 衣装全体のスケールをアバターへ合わせる。
            float overallScale = CalculateOverallScale(avatarRoot, pairs);
            if (overallScale > 0f && !Mathf.Approximately(overallScale, 1f))
            {
                Undo.RecordObject(outfitRoot.transform, "衣装全体のスケールをアバターに合わせる");
                outfitRoot.transform.localScale *= overallScale;
                PrefabUtility.RecordPrefabInstancePropertyModifications(outfitRoot.transform);
            }

            // 2) Aポーズ/Tポーズ差を合わせる。MA側の実装が利用できる場合はそれを呼び出す。
            InvokeFixAPose(avatarRoot, mergers);

            // 3) 対応ボーンの位置をベースアバターへ合わせる。
            // 親から子の順で適用し、親移動による子のずれを後段で補正します。
            pairs.Sort((a, b) => GetDepth(a.Merge).CompareTo(GetDepth(b.Merge)));
            HashSet<Transform> adjusted = new HashSet<Transform>();
            for (int i = 0; i < pairs.Count; i++)
            {
                BonePair pair = pairs[i];
                if (pair.Base == null || pair.Merge == null || !adjusted.Add(pair.Merge))
                {
                    continue;
                }

                Undo.RecordObject(pair.Merge, "衣装ボーン位置をアバターに合わせる");
                pair.Merge.position = pair.Base.position;
                // 「回転も合わせる」「スケールも合わせる」はデフォルトOFFのため変更しません。
                PrefabUtility.RecordPrefabInstancePropertyModifications(pair.Merge);
            }
        }

        private static List<BonePair> CollectBonePairs(Component[] mergers)
        {
            List<BonePair> pairs = new List<BonePair>();
            HashSet<Transform> seenMerge = new HashSet<Transform>();

            for (int i = 0; i < mergers.Length; i++)
            {
                Component merger = mergers[i];
                if (merger == null)
                {
                    continue;
                }

                GameObject mergeTargetObject = GetMergeTargetObject(merger);
                if (mergeTargetObject != null && seenMerge.Add(merger.transform))
                {
                    pairs.Add(new BonePair(mergeTargetObject.transform, merger.transform));
                }

                MethodInfo mappingMethod = merger.GetType().GetMethod(
                    "GetBonesMapping",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mappingMethod == null)
                {
                    continue;
                }

                object mapping = mappingMethod.Invoke(merger, null);
                IEnumerable enumerable = mapping as IEnumerable;
                if (enumerable == null)
                {
                    continue;
                }

                foreach (object entry in enumerable)
                {
                    if (!TryReadTuple(entry, out Transform baseBone, out Transform mergeBone) ||
                        baseBone == null || mergeBone == null || !seenMerge.Add(mergeBone))
                    {
                        continue;
                    }

                    pairs.Add(new BonePair(baseBone, mergeBone));
                }
            }

            return pairs;
        }

        private static bool TryReadTuple(object tuple, out Transform baseBone, out Transform mergeBone)
        {
            baseBone = null;
            mergeBone = null;
            if (tuple == null)
            {
                return false;
            }

            Type type = tuple.GetType();
            FieldInfo item1Field = type.GetField("Item1");
            FieldInfo item2Field = type.GetField("Item2");
            PropertyInfo item1Property = type.GetProperty("Item1");
            PropertyInfo item2Property = type.GetProperty("Item2");

            object item1 = item1Field != null
                ? item1Field.GetValue(tuple)
                : item1Property != null ? item1Property.GetValue(tuple, null) : null;
            object item2 = item2Field != null
                ? item2Field.GetValue(tuple)
                : item2Property != null ? item2Property.GetValue(tuple, null) : null;

            baseBone = item1 as Transform;
            mergeBone = item2 as Transform;
            return baseBone != null && mergeBone != null;
        }

        private static GameObject GetMergeTargetObject(Component merger)
        {
            PropertyInfo property = merger.GetType().GetProperty(
                "mergeTargetObject",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                return property.GetValue(merger, null) as GameObject;
            }

            return null;
        }

        private static float CalculateOverallScale(GameObject avatarRoot, List<BonePair> pairs)
        {
            Animator animator = avatarRoot != null ? avatarRoot.GetComponent<Animator>() : null;
            if (animator != null && animator.isHuman)
            {
                Dictionary<Transform, Transform> mapping = new Dictionary<Transform, Transform>();
                for (int i = 0; i < pairs.Count; i++)
                {
                    if (pairs[i].Base != null && pairs[i].Merge != null && !mapping.ContainsKey(pairs[i].Base))
                    {
                        mapping.Add(pairs[i].Base, pairs[i].Merge);
                    }
                }

                List<float> armRatios = new List<float>();
                AddArmScaleRatio(
                    animator,
                    HumanBodyBones.LeftUpperArm,
                    HumanBodyBones.LeftLowerArm,
                    HumanBodyBones.LeftHand,
                    mapping,
                    armRatios);
                AddArmScaleRatio(
                    animator,
                    HumanBodyBones.RightUpperArm,
                    HumanBodyBones.RightLowerArm,
                    HumanBodyBones.RightHand,
                    mapping,
                    armRatios);

                if (armRatios.Count > 0)
                {
                    float total = 0f;
                    for (int i = 0; i < armRatios.Count; i++) total += armRatios[i];
                    return Mathf.Clamp(total / armRatios.Count, 0.001f, 1000f);
                }
            }

            // Humanoid情報が取れない場合は、対応する親子ボーン長の比率の中央値を使います。
            Dictionary<Transform, Transform> pairMap = new Dictionary<Transform, Transform>();
            for (int i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].Base != null && pairs[i].Merge != null && !pairMap.ContainsKey(pairs[i].Base))
                {
                    pairMap.Add(pairs[i].Base, pairs[i].Merge);
                }
            }

            List<float> ratios = new List<float>();
            for (int i = 0; i < pairs.Count; i++)
            {
                Transform baseBone = pairs[i].Base;
                Transform mergeBone = pairs[i].Merge;
                if (baseBone == null || mergeBone == null || baseBone.parent == null)
                {
                    continue;
                }

                if (!pairMap.TryGetValue(baseBone.parent, out Transform mergeParent) || mergeParent == null)
                {
                    continue;
                }

                float baseLength = Vector3.Distance(baseBone.position, baseBone.parent.position);
                float mergeLength = Vector3.Distance(mergeBone.position, mergeParent.position);
                if (baseLength > 0.00001f && mergeLength > 0.00001f)
                {
                    float ratio = baseLength / mergeLength;
                    if (ratio >= 0.001f && ratio <= 1000f)
                    {
                        ratios.Add(ratio);
                    }
                }
            }

            if (ratios.Count == 0)
            {
                return 1f;
            }

            ratios.Sort();
            int middle = ratios.Count / 2;
            return ratios.Count % 2 == 0
                ? (ratios[middle - 1] + ratios[middle]) * 0.5f
                : ratios[middle];
        }

        private static void AddArmScaleRatio(
            Animator animator,
            HumanBodyBones upperBone,
            HumanBodyBones lowerBone,
            HumanBodyBones handBone,
            Dictionary<Transform, Transform> mapping,
            List<float> ratios)
        {
            Transform baseUpper = animator.GetBoneTransform(upperBone);
            Transform baseLower = animator.GetBoneTransform(lowerBone);
            Transform baseHand = animator.GetBoneTransform(handBone);
            if (baseUpper == null || baseLower == null || baseHand == null)
            {
                return;
            }

            if (!mapping.TryGetValue(baseUpper, out Transform mergeUpper) ||
                !mapping.TryGetValue(baseLower, out Transform mergeLower) ||
                !mapping.TryGetValue(baseHand, out Transform mergeHand) ||
                mergeUpper == null || mergeLower == null || mergeHand == null)
            {
                return;
            }

            float baseLength = Vector3.Distance(baseUpper.position, baseLower.position) +
                               Vector3.Distance(baseLower.position, baseHand.position);
            float mergeLength = Vector3.Distance(mergeUpper.position, mergeLower.position) +
                                Vector3.Distance(mergeLower.position, mergeHand.position);
            if (baseLength > 0.00001f && mergeLength > 0.00001f)
            {
                ratios.Add(baseLength / mergeLength);
            }
        }

        private static void InvokeFixAPose(GameObject avatarRoot, Component[] mergers)
        {
            Type setupType = FindType(SetupOutfitTypeName) ?? FindType(LegacySetupOutfitTypeName);
            if (setupType == null)
            {
                return;
            }

            MethodInfo fixAPose = setupType.GetMethod(
                "FixAPose",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(GameObject), typeof(Transform), typeof(bool) },
                null);
            if (fixAPose == null)
            {
                return;
            }

            for (int i = 0; i < mergers.Length; i++)
            {
                if (mergers[i] != null)
                {
                    fixAPose.Invoke(null, new object[] { avatarRoot, mergers[i].transform, false });
                }
            }
        }

        private static int GetDepth(Transform transform)
        {
            int depth = 0;
            Transform current = transform;
            while (current != null)
            {
                depth++;
                current = current.parent;
            }

            return depth;
        }

        private static Component FindComponentInChildren(GameObject root, Type componentType)
        {
            if (root == null || componentType == null) return null;
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Component component = transforms[i].GetComponent(componentType);
                if (component != null) return component;
            }

            return null;
        }

        private static Component[] FindComponentsInChildren(GameObject root, Type componentType)
        {
            List<Component> results = new List<Component>();
            if (root == null || componentType == null) return results.ToArray();
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Component component = transforms[i].GetComponent(componentType);
                if (component != null) results.Add(component);
            }

            return results.ToArray();
        }

        private static GameObject FindAvatarRoot(Transform start)
        {
            GameObject humanoidFallback = null;
            Transform current = start;
            while (current != null)
            {
                Component[] components = current.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null)
                    {
                        continue;
                    }

                    Type type = component.GetType();
                    string fullName = type.FullName ?? string.Empty;
                    if (type.Name == "VRCAvatarDescriptor" ||
                        fullName.EndsWith(".VRCAvatarDescriptor", StringComparison.Ordinal))
                    {
                        return current.gameObject;
                    }
                }

                Animator animator = current.GetComponent<Animator>();
                if (humanoidFallback == null && animator != null && animator.isHuman)
                {
                    humanoidFallback = current.gameObject;
                }

                current = current.parent;
            }

            return humanoidFallback;
        }

        private static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = assemblies[i].GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private readonly struct BonePair
        {
            public readonly Transform Base;
            public readonly Transform Merge;

            public BonePair(Transform baseTransform, Transform mergeTransform)
            {
                Base = baseTransform;
                Merge = mergeTransform;
            }
        }
    }

}
#endif