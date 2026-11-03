using System;
using System.IO;
using System.Text;
using Colossal.Entities;
using Colossal.IO.AssetDatabase;
using Colossal.Serialization.Entities;
using Game;
using Game.Assets;
using Game.City;
using Game.Common;
using Game.Policies;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Simulation;
using Game.UI;
using RealisticPolicies.Engine;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Scripting;

namespace RealisticPolicies
{
    /// <summary>
    /// Prototype-only self-test. Runs only when ModsData/RealisticPolicies/selftest.flag exists:
    /// starts a throwaway new city from the main menu, checks every hook the design relies on,
    /// writes report.txt and screenshots next to the flag, then quits the game. Never touches saves.
    /// </summary>
    public sealed class SelfTestSystem : GameSystemBase
    {
        private enum Stage { Off, WaitMenu, Loading, Settle, Activate, WaitModifiers, EngineSamples, WaitMigration, BudgetPrepare, BudgetMeasure, WaitLock, Ui, WelfareUi, WelfareCensus, TaxStaff, TaxFloor, TaxGone, SuitRaise, SuitFiled, SuitInjunction, SuitVerdict, SuitBan, SuitDefault, SuitDefaultVerdict, SuitSettle, MoneyWatch, Finish, Quit }

        private const float kTimeoutSeconds = 1500f;
        private const string kPreferredMap = "Plains";
        private const int kSamples = 5;
        private const uint kSampleFrames = 4096;
        private const float kTestSpeed = 4f;

        private readonly StringBuilder m_Report = new StringBuilder();
        private string m_Directory;
        private Stage m_Stage;
        private float m_TestStart;
        private float m_StageStart;
        private uint m_SimStart;
        private int m_Passed;
        private int m_Failed;
        private bool m_UiDone;
        private Entity m_WelfareOffice;
        private Entity m_TaxOffice;
        private bool m_FeeChecked;
        private string m_SaveName;
        private bool m_MoneyMode;
        private float m_NextMoneySample;
        private int m_MoneyAtStart;
        private uint m_FrameAtStart;
        private const float kMoneyWatchSeconds = 660f;
        private const int kMoneyTestExpense = 50000000;

        private PrefabSystem m_PrefabSystem;
        private CitySystem m_CitySystem;
        private SimulationSystem m_SimulationSystem;
        private CityServiceBudgetSystem m_BudgetSystem;
        private CityStatisticsSystem m_StatisticsSystem;
        private BudgetInjectionSystem m_Injection;
        private PolicyEngineSystem m_Engine;
        private readonly float[,] m_Samples = new float[kSamples, 3];
        private int m_SampleCount;
        private float m_AirBefore;
        private ModUISystem m_UISystem;
        private EntityArchetype m_ModifyArchetype;
        private EndFrameBarrier m_EndFrameBarrier;

        private int m_MoneyStart;
        private int m_BalanceSum;
        private int m_BalanceSamples;
        private const string kTestIncomeRow = "RealisticPolicies.SelfTest.Income";
        private const string kTestExpenseRow = "RealisticPolicies.SelfTest.Expense";
        private float m_DemandBefore;
        private int m_HostIncomeBefore;
        private int m_HostExpenseBefore;

        private static string FlagPath => Path.Combine(Application.persistentDataPath, "ModsData", Mod.kName, "selftest.flag");

        /// <summary>True when the next launch should run the self-test; checked before systems are registered.</summary>
        public static bool IsRequested => File.Exists(FlagPath);

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Directory = Path.Combine(Application.persistentDataPath, "ModsData", Mod.kName);
            string flag = FlagPath;
            if (!File.Exists(flag))
            {
                Enabled = false;
                return;
            }
            // "save:<file name>" loads that save (a copy made for the test); anything else starts a new city.
            string request = File.ReadAllText(flag).Trim();
            // "save:<file name>;money" only watches city money against a large injected expense.
            m_MoneyMode = request.EndsWith(";money", StringComparison.Ordinal);
            if (m_MoneyMode)
            {
                request = request.Substring(0, request.Length - ";money".Length);
            }
            m_SaveName = request.StartsWith("save:", StringComparison.Ordinal) ? request.Substring(5).Trim() : null;
            // Delete at once so a crash never makes the next normal launch start a test city.
            File.Delete(flag);
            Directory.CreateDirectory(Path.Combine(m_Directory, "selftest"));

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_BudgetSystem = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            m_StatisticsSystem = World.GetOrCreateSystemManaged<CityStatisticsSystem>();
            m_Injection = World.GetOrCreateSystemManaged<BudgetInjectionSystem>();
            m_Engine = World.GetOrCreateSystemManaged<PolicyEngineSystem>();
            m_UISystem = World.GetOrCreateSystemManaged<ModUISystem>();
            m_UISystem.UiReport += OnUiReport;
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_ModifyArchetype = EntityManager.CreateArchetype(ComponentType.ReadWrite<Game.Common.Event>(), ComponentType.ReadWrite<Modify>());

            // The test city must never be written: switch the autosave system off for this session only (no setting changes).
            World.GetOrCreateSystemManaged<AutoSaveSystem>().Enabled = false;

            m_TestStart = UnityEngine.Time.realtimeSinceStartup;
            Line($"Self-test started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            SetStage(Stage.WaitMenu);
        }

        [Preserve]
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            if (m_Stage == Stage.Loading && mode == GameMode.Game)
            {
                Line($"City loaded (purpose {purpose}).");
                SetStage(Stage.Settle);
            }
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (m_Stage != Stage.Quit && m_Stage != Stage.Off && UnityEngine.Time.realtimeSinceStartup - m_TestStart > kTimeoutSeconds)
            {
                Check("Self-test finished before the time limit", false, $"stuck in stage {m_Stage}");
                SetStage(Stage.Finish);
            }

            if (((m_Stage >= Stage.Activate && m_Stage <= Stage.WaitLock) || m_Stage == Stage.MoneyWatch) && m_SimulationSystem.selectedSpeed != kTestSpeed)
            {
                // The test needs simulation frames: run at top speed whatever speed the city was loaded with.
                m_SimulationSystem.selectedSpeed = kTestSpeed;
            }

            switch (m_Stage)
            {
                case Stage.WaitMenu:
                    UpdateWaitMenu();
                    break;
                case Stage.Settle:
                    // Wait until the simulation has finished loading; it re-applies "pause after loading" at that point.
                    if (StageSeconds() > 5f && m_SimulationSystem.loadingProgress >= 1f && !GameManager.instance.isGameLoading && m_MoneyMode)
                    {
                        StartMoneyWatch();
                    }
                    else if (StageSeconds() > 5f && m_SimulationSystem.loadingProgress >= 1f && !GameManager.instance.isGameLoading)
                    {
                        RunStaticChecks();
                        SetStage(Stage.Activate);
                    }
                    break;
                case Stage.Activate:
                    ActivatePolicies();
                    SetStage(Stage.WaitModifiers);
                    break;
                case Stage.WaitModifiers:
                    if (SimFrames() >= 1024)
                    {
                        CheckActivation();
                        SetStage(Stage.EngineSamples);
                    }
                    break;
                case Stage.EngineSamples:
                    if (SimFrames() >= kSampleFrames)
                    {
                        if (TakeSample())
                        {
                            CheckEngine();
                            MigrateVanillaToggle();
                            SetStage(Stage.WaitMigration);
                        }
                        else
                        {
                            SetStage(Stage.EngineSamples);
                        }
                    }
                    break;
                case Stage.WaitMigration:
                    if (SimFrames() >= 1024)
                    {
                        CheckMigration();
                        SetStage(Stage.BudgetPrepare);
                    }
                    break;
                case Stage.BudgetPrepare:
                    UpdateBudgetPrepare();
                    break;
                case Stage.BudgetMeasure:
                    UpdateBudgetMeasure();
                    break;
                case Stage.WaitLock:
                    if (SimFrames() >= 2048)
                    {
                        Check("Locked stays on the mod policy after 2048 frames", IsLocked(PolicyCatalog.EmissionStandard), null);
                        m_UiDone = false;
                        m_UISystem.SetSelfTestStage(1);
                        SetStage(Stage.Ui);
                    }
                    break;
                case Stage.Ui:
                    if (m_UiDone || StageSeconds() > 120f)
                    {
                        if (!m_UiDone)
                        {
                            Check("UI self-test reported back", false, "no report within 120 s");
                        }
                        StartWelfareCheck();
                    }
                    break;
                case Stage.WelfareUi:
                    if (m_UiDone || StageSeconds() > 60f)
                    {
                        if (!m_UiDone)
                        {
                            Check("Welfare UI test reported back", false, "no report within 60 s");
                        }
                        m_Engine.RequestCensus();
                        SetStage(Stage.WelfareCensus);
                    }
                    break;
                case Stage.WelfareCensus:
                    if (SimFrames() >= 2048)
                    {
                        CheckWelfareCensus();
                        StartTaxStaff();
                    }
                    break;
                case Stage.TaxStaff:
                    if (SimFrames() >= 2048)
                    {
                        CheckTaxStaff();
                        // Social welfare staff gone: the floor goes back to 8%.
                        World.GetOrCreateSystemManaged<WelfareSeriesSection>().Set(m_WelfareOffice, WelfareSeriesSection.Choice.GeneralAdministration);
                        SetStage(Stage.TaxFloor);
                    }
                    break;
                case Stage.TaxFloor:
                    if (SimFrames() >= 2048)
                    {
                        CheckTaxFloor();
                        // Tax staff gone after having had some: rates go back to the default.
                        World.GetOrCreateSystemManaged<WelfareSeriesSection>().Set(m_TaxOffice, WelfareSeriesSection.Choice.GeneralAdministration);
                        SetStage(Stage.TaxGone);
                    }
                    break;
                case Stage.TaxGone:
                    if (SimFrames() >= 2048)
                    {
                        CheckTaxGone();
                        StartLitigation();
                    }
                    break;
                case Stage.SuitRaise:
                    if (SimFrames() >= 1024)
                    {
                        // Sue the emission standard, with an injunction request, and make the city lose.
                        LitigationSystem.TestForceInjunctionRequest = true;
                        LitigationSystem.TestForcedVerdict = 1;
                        LitigationSystem.TestFilingPolicy = PolicyCatalog.EmissionStandard;
                        SetStage(Stage.SuitFiled);
                    }
                    break;
                case Stage.SuitFiled:
                    if (SuitIndex(PolicyCatalog.EmissionStandard) >= 0)
                    {
                        CheckSuitFiled();
                        SetStage(Stage.SuitInjunction);
                    }
                    else if (SimFrames() > 8192)
                    {
                        Check("A suit is filed against the emission standard", false, "none within 8192 frames");
                        SetStage(Stage.Finish);
                    }
                    break;
                case Stage.SuitInjunction:
                    if (SuitIndex(PolicyCatalog.EmissionStandard) >= 0 && Suit(PolicyCatalog.EmissionStandard).m_Injunction != (byte)InjunctionStage.Requested)
                    {
                        CheckInjunctionAndRespond();
                        SetStage(Stage.SuitVerdict);
                    }
                    else if (SimFrames() > 8192)
                    {
                        Check("The court decides the injunction", false, "no decision within 8192 frames");
                        SetStage(Stage.Finish);
                    }
                    break;
                case Stage.SuitVerdict:
                    if (!m_FeeChecked && SimFrames() >= 2048 && SuitIndex(PolicyCatalog.EmissionStandard) >= 0)
                    {
                        m_FeeChecked = true;
                        Check("Lawyer fees are a budget row while the suit runs", m_Injection.GetRow(LitigationSystem.kLawyerFeeRow) > 0, $"row {m_Injection.GetRow(LitigationSystem.kLawyerFeeRow)}");
                    }
                    if (SuitIndex(PolicyCatalog.EmissionStandard) < 0 && SimFrames() >= 2048)
                    {
                        CheckLostSuit();
                        // Try to put the standard straight back to 60% during the ban.
                        SetPolicy(m_CitySystem.City, PolicyCatalog.EmissionStandard, true, 60f);
                        SetStage(Stage.SuitBan);
                    }
                    else if (SimFrames() > 60000)
                    {
                        Check("The court rules on the emission standard", false, "no verdict within 60000 frames");
                        SetStage(Stage.Finish);
                    }
                    break;
                case Stage.SuitBan:
                    if (SimFrames() >= 3072)
                    {
                        CheckBanCondition();
                        LitigationSystem.TestForceInjunctionRequest = false;
                        LitigationSystem.TestForcedVerdict = -1;
                        LitigationSystem.TestFilingPolicy = PolicyCatalog.EquipmentMandate;
                        SetStage(Stage.SuitDefault);
                    }
                    break;
                case Stage.SuitDefault:
                    if (SuitIndex(PolicyCatalog.EquipmentMandate) >= 0)
                    {
                        Line($"Mandate suit filed; the city does not answer. Duration {Suit(PolicyCatalog.EquipmentMandate).m_DurationFrames} frames.");
                        SetStage(Stage.SuitDefaultVerdict);
                    }
                    else if (SimFrames() > 8192)
                    {
                        Check("A suit is filed against the mandate", false, "none within 8192 frames");
                        SetStage(Stage.Finish);
                    }
                    break;
                case Stage.SuitDefaultVerdict:
                    if (SuitIndex(PolicyCatalog.EquipmentMandate) < 0 && SimFrames() >= 2048)
                    {
                        Check("Unanswered suit: default judgment turns the toggle off and locks it", !IsActive(PolicyCatalog.EquipmentMandate) && IsLocked(PolicyCatalog.EquipmentMandate)
                            && World.GetOrCreateSystemManaged<LitigationSystem>().IsBanned(PolicyCatalog.EquipmentMandate), null);
                        LitigationSystem.TestFilingPolicy = PolicyCatalog.Find(PolicyCatalog.kPollutionTax);
                        SetStage(Stage.SuitSettle);
                    }
                    else if (SimFrames() > 60000)
                    {
                        Check("The court rules on the mandate", false, "no verdict within 60000 frames");
                        SetStage(Stage.Finish);
                    }
                    break;
                case Stage.SuitSettle:
                    if (SuitIndex(PolicyCatalog.Find(PolicyCatalog.kPollutionTax)) >= 0)
                    {
                        CheckSettlement();
                        SetStage(Stage.Finish);
                    }
                    else if (SimFrames() > 8192)
                    {
                        Check("A suit is filed against the pollution tax", false, "none within 8192 frames");
                        SetStage(Stage.Finish);
                    }
                    break;
                case Stage.MoneyWatch:
                    UpdateMoneyWatch();
                    break;
                case Stage.Finish:
                    Finish();
                    SetStage(Stage.Quit);
                    break;
                case Stage.Quit:
                    if (StageSeconds() > 3f)
                    {
                        Application.Quit();
                        Enabled = false;
                    }
                    break;
            }
        }

        private void UpdateWaitMenu()
        {
            if (GameManager.instance.gameMode == GameMode.Game)
            {
                // The player's own save was loaded (e.g. "Resume" in the launcher): never touch it.
                Line("A city was loaded before the test could start its own; self-test aborted.");
                Finish();
                SetStage(Stage.Off);
                Enabled = false;
                return;
            }
            if (GameManager.instance.gameMode != GameMode.MainMenu || StageSeconds() < 3f)
            {
                return;
            }
            // Registration runs in the main loop; it must have happened in the menu so saves can reference the prefabs.
            if (!PolicyCatalog.IsRegistered && StageSeconds() < 60f)
            {
                return;
            }
            Check("Policies registered while in the main menu", PolicyCatalog.IsRegistered, null);
            if (m_SaveName != null)
            {
                StartFromSave();
            }
            else
            {
                StartTestCity();
            }
        }

        private void StartFromSave()
        {
            SaveGameMetadata chosen = null;
            foreach (SaveGameMetadata meta in AssetDatabase.global.GetAssets(default(SearchFilter<SaveGameMetadata>)))
            {
                SaveInfo info = meta.target;
                if (info != null && ((info.path != null && Path.GetFileNameWithoutExtension(info.path) == m_SaveName) || info.displayName == m_SaveName))
                {
                    chosen = meta;
                    break;
                }
            }
            if (chosen == null)
            {
                Check($"Save copy '{m_SaveName}' found", false, null);
                SetStage(Stage.Finish);
                return;
            }
            Line($"Loading the save copy '{m_SaveName}' ({chosen.target.displayName}). It is never saved.");
            SetStage(Stage.Loading);
            _ = GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, chosen);
        }

        private void StartTestCity()
        {
            MapMetadata chosen = null;
            var names = new StringBuilder();
            foreach (MapMetadata meta in AssetDatabase.global.GetAssets(default(SearchFilter<MapMetadata>)))
            {
                MapInfo info = meta.target;
                if (info == null)
                {
                    continue;
                }
                names.Append(info.displayName).Append("; ");
                if (chosen == null || info.displayName == kPreferredMap)
                {
                    chosen = meta;
                }
            }
            Line($"Maps: {names}");
            if (chosen == null)
            {
                Check("A map is available for the test city", false, null);
                SetStage(Stage.Finish);
                return;
            }

            MapInfo target = chosen.target;
            CityConfigurationSystem config = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            config.overrideLoadedOptions = true;
            config.overrideCityName = "RP Selftest";
            config.overrideThemeName = target.theme;
            config.overrideUnlockAll = true;
            config.overrideUnlimitedMoney = false;
            config.overrideNaturalDisasters = false;
            config.overrideLeftHandTraffic = false;
            MapMetadataSystem mapMetadata = World.GetOrCreateSystemManaged<MapMetadataSystem>();
            mapMetadata.mapName = target.displayName;
            mapMetadata.prefabReferences = target.prefabReferences;
            // The menu sets this too; without it the date systems throw on a new city.
            World.GetOrCreateSystemManaged<TimeSystem>().startingYear = target.startingYear != -1 ? target.startingYear : DateTime.Now.Year;

            Line($"Starting a new city on '{target.displayName}' (unlock all on, unlimited money off).");
            SetStage(Stage.Loading);
            _ = GameManager.instance.Load(GameMode.Game, Purpose.NewGame, chosen);
        }

        private void RunStaticChecks()
        {
            Entity mandate = PolicyCatalog.EquipmentMandate;
            Entity standard = PolicyCatalog.EmissionStandard;
            Check("Toggle policy prefab has PolicyData", mandate != Entity.Null && EntityManager.HasComponent<PolicyData>(mandate), null);
            bool sliderOk = standard != Entity.Null && EntityManager.HasComponent<PolicySliderData>(standard);
            string sliderInfo = null;
            if (sliderOk)
            {
                PolicySliderData slider = EntityManager.GetComponentData<PolicySliderData>(standard);
                sliderInfo = $"range [{slider.m_Range.min}, {slider.m_Range.max}], default {slider.m_Default}, step {slider.m_Step}, unit {slider.m_Unit}";
            }
            Check("Slider policy prefab has PolicySliderData", sliderOk, sliderInfo);
            Check("Mod policies start unlocked", !IsLocked(mandate) && !IsLocked(standard), null);
            Check("Vanilla toggle is hidden from the policy list",
                m_PrefabSystem.TryGetPrefab(PolicyCatalog.VanillaPollutionManagement, out PolicyPrefab vanilla) && vanilla.m_Visibility == PolicyVisibility.HideFromPolicyList, null);

            WriteBuildingTable();

        }

        /// <summary>Every staffed service building prefab with its series, cost, upkeep, jobs and unlock milestone (CSV).</summary>
        private void WriteBuildingTable()
        {
            var csv = new StringBuilder("name,series,service,construction_cost,monthly_upkeep,max_workers,milestone\n");
            int rows = 0;
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<ServiceObjectData>(), ComponentType.ReadOnly<WorkplaceData>()))
            using (NativeArray<Entity> prefabs = query.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity prefab = prefabs[i];
                    ServiceSeries series = ServiceSeriesCensus.Classify(EntityManager, prefab);
                    string name = m_PrefabSystem.TryGetPrefab(prefab, out PrefabBase prefabBase) ? prefabBase.name : "?";
                    string service = m_PrefabSystem.TryGetPrefab(EntityManager.GetComponentData<ServiceObjectData>(prefab).m_Service, out PrefabBase servicePrefab) ? servicePrefab.name : "?";
                    uint cost = EntityManager.TryGetComponent(prefab, out PlaceableObjectData placeable) ? placeable.m_ConstructionCost : 0u;
                    int upkeep = 0;
                    if (EntityManager.TryGetBuffer(prefab, true, out DynamicBuffer<ServiceUpkeepData> upkeeps))
                    {
                        for (int u = 0; u < upkeeps.Length; u++)
                        {
                            if (upkeeps[u].m_Upkeep.m_Resource == Game.Economy.Resource.Money)
                            {
                                upkeep += upkeeps[u].m_Upkeep.m_Amount;
                            }
                        }
                    }
                    int workers = EntityManager.GetComponentData<WorkplaceData>(prefab).m_MaxWorkers;
                    int milestone = EntityManager.HasComponent<Locked>(prefab) ? Game.UI.InGame.ProgressionUtils.GetRequiredMilestone(EntityManager, prefab) : 0;
                    csv.Append('"').Append(name).Append("\",").Append(series).Append(',').Append(service).Append(',')
                        .Append(cost).Append(',').Append(upkeep).Append(',').Append(workers).Append(',').Append(milestone).Append('\n');
                    rows++;
                }
            }
            string file = Path.Combine(m_Directory, "selftest", "service-buildings.csv");
            File.WriteAllText(file, csv.ToString());
            Line($"Service building table: {rows} prefabs -> {file}");
        }

        private void ActivatePolicies()
        {
            Entity city = m_CitySystem.City;
            m_DemandBefore = IndustrialBaseDemand();
            SetPolicy(city, PolicyCatalog.EquipmentMandate, true, 0f);
            SetPolicy(city, PolicyCatalog.EmissionStandard, true, 30f);
            SetPolicy(city, PolicyCatalog.Find(PolicyCatalog.kPollutionTax), true, 50f);
            SetPolicy(city, PolicyCatalog.Find(PolicyCatalog.kGreenSubsidy), true, 50f);
            SetPolicy(city, PolicyCatalog.Find(PolicyCatalog.kEnvironmentalMonitoring), true, 60f);
            SetPolicy(city, PolicyCatalog.Find(PolicyCatalog.kEmissionDisclosure), true, 0f);
            // Vanilla toggle on as well, to test the save migration.
            SetPolicy(city, PolicyCatalog.VanillaPollutionManagement, true, 0f);
            Line($"Activated the six environment policies (standard 30%, tax 50%, subsidy 50%, monitoring 60%, disclosure) and the vanilla toggle; industrial base demand {m_DemandBefore}.");
        }

        private void CheckActivation()
        {
            // The engine may already have switched the vanilla toggle over (its first step after loading); the
            // migration stage below turns it on again and checks the switch.
            bool allOn = true;
            foreach (PolicyDefinition definition in PolicyDefinitions.All)
            {
                allOn &= IsActive(definition.Prefab);
            }
            Check("All mod policies are active", allOn, null);
            m_AirBefore = AirModifier();
            m_SampleCount = 0;
            TakeSample();
        }

        /// <summary>Records engine progress for both mod policies and the city air modifier; returns true when all samples are in.</summary>
        private bool TakeSample()
        {
            PolicyStepResult[] results = m_Engine.LastResults;
            m_Samples[m_SampleCount, 0] = results[0].Progress;
            m_Samples[m_SampleCount, 1] = results[1].Progress;
            m_Samples[m_SampleCount, 2] = AirModifier();
            Line($"Sample {m_SampleCount}: mandate {results[0].Progress:0.000}/{results[0].Target:0.000}, standard {results[1].Progress:0.000}/{results[1].Target:0.000}, city air {m_Samples[m_SampleCount, 2]:0.000}, legitimacy {m_Engine.LastState.m_Legitimacy:0.000}");
            m_SampleCount++;
            return m_SampleCount >= kSamples;
        }

        private void CheckEngine()
        {
            PolicyStepResult[] results = m_Engine.LastResults;
            bool rising = true;
            for (int i = 1; i < kSamples; i++)
            {
                rising &= m_Samples[i, 0] > m_Samples[i - 1, 0] && m_Samples[i, 1] > m_Samples[i - 1, 1];
            }
            Check("Delivered effect rises step by step (lag, not instant)", rising && m_Samples[0, 0] < 0.9f * results[0].Target, $"first {m_Samples[0, 0]:0.000}, last {m_Samples[kSamples - 1, 0]:0.000}, target {results[0].Target:0.000}");
            Check("Vanilla applies the engine's effect to the city", m_Samples[kSamples - 1, 2] < m_AirBefore - 0.005f, $"air {m_AirBefore:0.000} -> {m_Samples[kSamples - 1, 2]:0.000}");
            Check("Engine state is stored on the city", EntityManager.HasComponent<PolicyEngineState>(m_CitySystem.City) && EntityManager.HasBuffer<PolicyRuntime>(m_CitySystem.City), null);
            ServiceSeriesCensus census = m_Engine.Census;
            var staffing = new StringBuilder();
            for (int s = 0; s < (int)ServiceSeries.Count; s++)
            {
                staffing.Append($"{(ServiceSeries)s} {census.Workers((ServiceSeries)s)}/{census.MaxWorkers((ServiceSeries)s)} (C {census.Capacity[s]:0.00}, demand {m_Engine.Demand[s]:0.0}); ");
            }
            Line($"Series census (industrial companies {census.IndustrialCompanies}): " + staffing);
            Check("Raising two N policies lowers legitimacy", m_Engine.LastState.m_Legitimacy < EngineParameters.kLegitimacyBase, $"{m_Engine.LastState.m_Legitimacy:0.000}");
            CheckDefinitionFramework();
            CheckFirstBundle();
            CheckTaxFrozen();
        }

        /// <summary>Stage 2: money outputs become budget rows, groups are counted, parameter levers restore.</summary>
        private void CheckDefinitionFramework()
        {
            ServiceSeriesCensus census = m_Engine.Census;
            Line($"Groups: households {census.TargetCount(TargetGroup.Households)}, industrial {census.TargetCount(TargetGroup.IndustrialCompanies)}, " +
                $"commercial {census.TargetCount(TargetGroup.CommercialCompanies)}, office {census.TargetCount(TargetGroup.OfficeCompanies)}");
            Check("Households are counted", census.TargetCount(TargetGroup.Households) > 0, null);

            float industrial = census.TargetCount(TargetGroup.IndustrialCompanies);
            int expected = (int)Mathf.Round((500f + 20f * industrial) * m_Engine.LastResults[1].Intensity);
            int row = m_Injection.GetRow(PolicyDefinitions.kEmissionStandardEnforcementRow);
            Check("Emission standard enforcement is a budget row: (500 + 20 x industrial) x intensity", row == expected && row > 0, $"row {row}, expected {expected}");

            int mandate = System.Array.FindIndex(PolicyDefinitions.All, d => d.Id == PolicyCatalog.kEquipmentMandate);
            float burden = m_Engine.GroupGrievance(mandate, TargetGroup.IndustrialCompanies);
            float benefit = m_Engine.GroupGrievance(mandate, TargetGroup.Households);
            Line($"Mandate grievance: industrial {burden:0.000}, households {benefit:0.000}");

            var lever = new ScaledFloatLever<EconomyParameterData>("SelfTest.CommuterWage",
                (in EconomyParameterData d) => d.m_CommuterWageMultiplier,
                (ref EconomyParameterData d, float v) => d.m_CommuterWageMultiplier = v);
            float before = CommuterWage();
            bool applied = lever.Apply(EntityManager, 1.5f);
            float scaled = CommuterWage();
            lever.Apply(EntityManager, 1.5f);
            float again = CommuterWage();
            lever.Restore(EntityManager);
            float after = CommuterWage();
            Check("Parameter lever writes original x factor, does not compound, and restores", applied && Mathf.Abs(scaled - before * 1.5f) < 1e-5f && again == scaled && after == before,
                $"{before} -> {scaled} -> {again} -> {after}");
        }

        /// <summary>Stage 3: firm model, tax revenue, subsidy payments, demand lever, monitoring and disclosure.</summary>
        private void CheckFirstBundle()
        {
            float cutAtBase = FirmModel.Abatement(FirmModel.kBasePrice);
            Line($"Firm model: cut at base price {cutAtBase:0.000}, at half {FirmModel.Abatement(FirmModel.kBasePrice * 0.5f):0.000}, cost at base {FirmModel.AbatementCost(FirmModel.kBasePrice):0.000}, " +
                $"deterred at 30% cut {FirmModel.DeterredShare(0.3f, 0.6f, EngineParameters.kStandardFine):0.00}, at 60% {FirmModel.DeterredShare(0.6f, 0.6f, EngineParameters.kStandardFine):0.00}");
            Check("Firm model: more price, more cut; nothing at zero", FirmModel.Abatement(0f) == 0f && FirmModel.Abatement(0.3f) < cutAtBase && cutAtBase < FirmModel.Abatement(1.2f), null);

            float industrial = m_Engine.Census.TargetCount(TargetGroup.IndustrialCompanies);
            PolicyStepResult tax = Result(PolicyCatalog.kPollutionTax);
            float taxCut = FirmModel.Abatement(tax.Progress * FirmModel.kBasePrice);
            int expectedTax = (int)Mathf.Round(2000f * industrial * tax.Intensity * (1f - taxCut) * tax.Compliance);
            int taxRow = m_Injection.GetRow(BudgetInjectionSystem.kIncomeRowId);
            Check("Pollution tax income = 2000 x industrial x rate x (1 - cut) x reporting", taxRow == expectedTax && taxRow > 0,
                $"row {taxRow}, expected {expectedTax}, reporting {tax.Compliance:0.00}, delivered price {tax.Progress:0.000}");

            PolicyStepResult subsidy = Result(PolicyCatalog.kGreenSubsidy);
            int expectedSubsidy = (int)Mathf.Round(2000f * industrial * subsidy.Intensity * FirmModel.Abatement(subsidy.Progress * FirmModel.kBasePrice));
            int subsidyRow = m_Injection.GetRow(BudgetInjectionSystem.kExpenseRowId);
            Check("Green subsidy expense = 2000 x industrial x rate x cut", subsidyRow == expectedSubsidy && subsidyRow > 0, $"row {subsidyRow}, expected {expectedSubsidy}");

            float demand = IndustrialBaseDemand();
            float expectedDemand = m_DemandBefore * (1f + 0.15f * subsidy.Progress);
            Check("Subsidy raises industrial base demand through the parameter lever", Mathf.Abs(demand - expectedDemand) <= 1e-4f * Mathf.Max(1f, Mathf.Abs(expectedDemand)) && demand > m_DemandBefore,
                $"{m_DemandBefore} -> {demand}, expected {expectedDemand}");

            PolicyStepResult standard = m_Engine.LastResults[1];
            float unwatched = 1f - Mathf.Exp(-EngineParameters.kDetection * standard.Effectiveness * EngineParameters.kBaseInspection);
            Check("Monitoring raises detection for the emission standard", m_Engine.LastWatch > 0f && standard.Detection > unwatched + 1e-4f,
                $"watch {m_Engine.LastWatch:0.000}, detection {standard.Detection:0.000} vs {unwatched:0.000} without inspectors");
            Check("Disclosure is delivered", m_Engine.LastDisclosure > 0f, $"{m_Engine.LastDisclosure:0.000}");
            Line($"Tax: reporting {tax.Compliance:0.00}, progress {tax.Progress:0.000}; subsidy progress {subsidy.Progress:0.000}; standard compliance {standard.Compliance:0.00}; " +
                $"city efficiency modifier {Delta(EntityManager.GetBuffer<CityModifier>(m_CitySystem.City, true), CityModifierType.IndustrialEfficiency):0.000}");
        }

        private PolicyStepResult Result(string id)
        {
            return m_Engine.LastResults[System.Array.FindIndex(PolicyDefinitions.All, d => d.Id == id)];
        }

        private float IndustrialBaseDemand()
        {
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<DemandParameterData>()))
            {
                return query.GetSingleton<DemandParameterData>().m_IndustrialBaseDemand;
            }
        }

        private float CommuterWage()
        {
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<EconomyParameterData>()))
            {
                return query.GetSingleton<EconomyParameterData>().m_CommuterWageMultiplier;
            }
        }

        private float AirModifier()
        {
            return Delta(EntityManager.GetBuffer<CityModifier>(m_CitySystem.City, true), CityModifierType.IndustrialAirPollution);
        }

        private void MigrateVanillaToggle()
        {
            // Turn the vanilla toggle on again and ask the engine for the switch it does after every load.
            SetPolicy(m_CitySystem.City, PolicyCatalog.VanillaPollutionManagement, true, 0f);
            m_Engine.RequestVanillaMigration();
            Line("Vanilla toggle on again; asked the engine to migrate it.");
        }

        private void CheckMigration()
        {
            Check("Migration turned the vanilla toggle off and kept the mandate on", !IsActive(PolicyCatalog.VanillaPollutionManagement) && IsActive(PolicyCatalog.EquipmentMandate), null);
        }

        private void UpdateBudgetPrepare()
        {
            if (StageSeconds() < 0.5f)
            {
                return;
            }
            if (m_Injection.GetRow(kTestIncomeRow) == 0)
            {
                m_HostIncomeBefore = m_BudgetSystem.GetIncome(IncomeSource.FeeParking);
                m_HostExpenseBefore = m_BudgetSystem.GetExpense(ExpenseSource.MapTileUpkeep);
                m_Injection.SetRow(kTestIncomeRow, true, (int)IncomeSource.FeeParking, 200000);
                m_Injection.SetRow(kTestExpenseRow, false, (int)ExpenseSource.MapTileUpkeep, 50000);
                m_StageStart = UnityEngine.Time.realtimeSinceStartup;
                return;
            }
            int income = m_BudgetSystem.GetIncome(IncomeSource.FeeParking);
            int expense = m_BudgetSystem.GetExpense(ExpenseSource.MapTileUpkeep);
            Check("Injected income shows in the vanilla budget source", income - m_HostIncomeBefore == m_Injection.GetRow(kTestIncomeRow), $"{m_HostIncomeBefore} -> {income}");
            Check("Injected expense shows in the vanilla budget source", expense - m_HostExpenseBefore == m_Injection.GetRow(kTestExpenseRow), $"{m_HostExpenseBefore} -> {expense}");
            m_MoneyStart = Money();
            m_BalanceSum = 0;
            m_BalanceSamples = 0;
            SetStage(Stage.BudgetMeasure);
        }

        private void UpdateBudgetMeasure()
        {
            m_BalanceSum += m_BudgetSystem.GetBalance();
            m_BalanceSamples++;
            uint frames = SimFrames();
            if (frames < 8192)
            {
                return;
            }
            int delta = Money() - m_MoneyStart;
            // BudgetApplySystem adds balance / 1024 every 256 frames.
            float expected = (float)m_BalanceSum / m_BalanceSamples * frames / 262144f;
            Line($"Money moved {delta} over {frames} frames; expected about {expected:0} from the average balance {m_BalanceSum / Math.Max(1, m_BalanceSamples)}.");
            if (EntityManager.GetComponentData<PlayerMoney>(m_CitySystem.City).m_Unlimited)
            {
                Line("Unlimited money: the money movement check is skipped.");
            }
            else
            {
                Check("City money follows the vanilla balance including the injection", Math.Abs(delta - expected) <= Math.Max(200f, Math.Abs(expected) * 0.05f), null);
            }

            // Lock the slider for the next check; a slider tweak makes the vanilla policy binding refresh.
            Entity standard = PolicyCatalog.EmissionStandard;
            if (!EntityManager.HasComponent<Locked>(standard))
            {
                EntityManager.AddComponent<Locked>(standard);
            }
            EntityManager.SetComponentEnabled<Locked>(standard, true);
            SetPolicy(m_CitySystem.City, standard, true, 35f);
            Line("Locked the emission standard.");
            SetStage(Stage.WaitLock);
        }

        /// <summary>Books a large monthly expense through the vanilla budget and watches city money for ~11 real minutes.</summary>
        private void StartMoneyWatch()
        {
            PlayerMoney money = EntityManager.GetComponentData<PlayerMoney>(m_CitySystem.City);
            m_Injection.SetRow(kTestExpenseRow, false, (int)ExpenseSource.MapTileUpkeep, kMoneyTestExpense);
            m_MoneyAtStart = money.money;
            m_FrameAtStart = m_SimulationSystem.frameIndex;
            m_NextMoneySample = 0f;
            Line($"Money watch: unlimited {money.m_Unlimited}, money {m_MoneyAtStart:N0}, injected expense {kMoneyTestExpense:N0} per month.");
            SetStage(Stage.MoneyWatch);
        }

        private void UpdateMoneyWatch()
        {
            float seconds = StageSeconds();
            if (seconds < m_NextMoneySample)
            {
                return;
            }
            m_NextMoneySample = seconds + 30f;
            int money = EntityManager.GetComponentData<PlayerMoney>(m_CitySystem.City).money;
            uint frames = m_SimulationSystem.frameIndex - m_FrameAtStart;
            Line($"  {seconds:0} s, {frames} frames: money {money:N0} (change {money - m_MoneyAtStart:N0}), balance {m_BudgetSystem.GetBalance():N0}, test row {m_BudgetSystem.GetExpense(ExpenseSource.MapTileUpkeep):N0}");
            if (seconds >= kMoneyWatchSeconds)
            {
                Check("City money moves under a large deficit", money != m_MoneyAtStart, $"{m_MoneyAtStart:N0} -> {money:N0} over {frames} frames");
                SetStage(Stage.Finish);
            }
        }

        /// <summary>Selects a welfare office so its info panel (with the mod's checkbox) opens, then hands over to the UI test.</summary>
        private void StartWelfareCheck()
        {
            m_WelfareOffice = Entity.Null;
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Game.Tools.Temp>(), ComponentType.Exclude<Deleted>()))
            using (NativeArray<Entity> buildings = query.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < buildings.Length && m_WelfareOffice == Entity.Null; i++)
                {
                    if (EntityManager.HasComponent<WelfareOfficeData>(EntityManager.GetComponentData<PrefabRef>(buildings[i]).m_Prefab)
                        && WelfareSeriesSection.Get(EntityManager, buildings[i]) == WelfareSeriesSection.Choice.GeneralAdministration)
                    {
                        m_WelfareOffice = buildings[i];
                    }
                }
            }
            if (m_WelfareOffice == Entity.Null)
            {
                Line("No unmarked welfare office in this city; the checkbox check is skipped.");
                SetStage(Stage.Finish);
                return;
            }
            ServiceSeriesCensus census = m_Engine.Census;
            Line($"Welfare office {m_WelfareOffice.Index}: general administration {census.Workers(ServiceSeries.GeneralAdministration)}, social welfare {census.Workers(ServiceSeries.SocialWelfare)} before the checkbox.");
            World.GetOrCreateSystemManaged<Game.UI.InGame.SelectedInfoUISystem>().SetSelection(m_WelfareOffice);
            m_UiDone = false;
            m_UISystem.SetSelfTestStage(2);
            SetStage(Stage.WelfareUi);
        }

        /// <summary>Stage 4, before any tax staff: rates are frozen where they are.</summary>
        private void CheckTaxFrozen()
        {
            TaxSystem taxSystem = World.GetOrCreateSystemManaged<TaxSystem>();
            TaxParameterData limits = TaxLimits();
            Check("Without tax staff (never had any): rates are frozen at the current values", m_Engine.Tax.LastLock == TaxLock.FreezeCurrent
                && limits.m_TotalTaxLimits.x == taxSystem.TaxRate && limits.m_TotalTaxLimits.y == taxSystem.TaxRate
                && limits.m_ResidentialTaxLimits.x == taxSystem.GetTaxRate(TaxAreaType.Residential),
                $"lock {m_Engine.Tax.LastLock}, total {taxSystem.TaxRate} in [{limits.m_TotalTaxLimits.x}, {limits.m_TotalTaxLimits.y}], residential {taxSystem.GetTaxRate(TaxAreaType.Residential)}");
            TaxParameterData original = m_Engine.Tax.Original;
            Line($"Original tax limits: total [{original.m_TotalTaxLimits.x}, {original.m_TotalTaxLimits.y}], residential [{original.m_ResidentialTaxLimits.x}, {original.m_ResidentialTaxLimits.y}], " +
                $"education levels [{original.m_JobLevelTaxLimits.x}, {original.m_JobLevelTaxLimits.y}], resources [{original.m_ResourceTaxLimits.x}, {original.m_ResourceTaxLimits.y}]");
        }

        /// <summary>A second unmarked welfare office becomes the tax office while the first is social welfare.</summary>
        private void StartTaxStaff()
        {
            m_TaxOffice = Entity.Null;
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Game.Tools.Temp>(), ComponentType.Exclude<Deleted>()))
            using (NativeArray<Entity> buildings = query.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < buildings.Length && m_TaxOffice == Entity.Null; i++)
                {
                    if (buildings[i] != m_WelfareOffice
                        && EntityManager.HasComponent<WelfareOfficeData>(EntityManager.GetComponentData<PrefabRef>(buildings[i]).m_Prefab)
                        && WelfareSeriesSection.Get(EntityManager, buildings[i]) == WelfareSeriesSection.Choice.GeneralAdministration)
                    {
                        m_TaxOffice = buildings[i];
                    }
                }
            }
            if (m_TaxOffice == Entity.Null)
            {
                Line("Only one welfare office; the tax staff checks are skipped.");
                StartLitigation();
                return;
            }
            World.GetOrCreateSystemManaged<WelfareSeriesSection>().Set(m_TaxOffice, WelfareSeriesSection.Choice.Tax);
            Line($"Welfare office {m_TaxOffice.Index} set to tax staff.");
            SetStage(Stage.TaxStaff);
        }

        private void CheckTaxStaff()
        {
            ServiceSeriesCensus census = m_Engine.Census;
            TaxParameterData original = m_Engine.Tax.Original;
            TaxParameterData limits = TaxLimits();
            int population = EntityManager.GetComponentData<Population>(m_CitySystem.City).m_Population;
            float ratio = (float)census.Workers(ServiceSeries.SocialWelfare) / population;
            int expectedFloor = Mathf.Max(original.m_ResidentialTaxLimits.x,
                Mathf.RoundToInt(TaxControl.kResidentialFloor - (TaxControl.kResidentialFloor - original.m_ResidentialTaxLimits.x) * Mathf.Min(1f, ratio / TaxControl.kWelfareStaffPerResident)));
            Check("Tax staff lift the lock and restore the original limits", m_Engine.Tax.LastLock == TaxLock.None && census.Workers(ServiceSeries.Tax) > 0
                && limits.m_TotalTaxLimits.Equals(original.m_TotalTaxLimits) && limits.m_CommercialTaxLimits.Equals(original.m_CommercialTaxLimits),
                $"tax staff {census.Workers(ServiceSeries.Tax)}, total [{limits.m_TotalTaxLimits.x}, {limits.m_TotalTaxLimits.y}]");
            Check("Residential floor follows social welfare staff per resident", limits.m_ResidentialTaxLimits.x == expectedFloor && m_Engine.Tax.LastFloor == expectedFloor,
                $"social welfare {census.Workers(ServiceSeries.SocialWelfare)}, population {population}, ratio {ratio * 1000f:0.000} per thousand, floor {limits.m_ResidentialTaxLimits.x}, expected {expectedFloor}");
        }

        private void CheckTaxFloor()
        {
            TaxSystem taxSystem = World.GetOrCreateSystemManaged<TaxSystem>();
            TaxParameterData limits = TaxLimits();
            int lowest = int.MaxValue;
            for (int level = 0; level < 5; level++)
            {
                lowest = Mathf.Min(lowest, taxSystem.GetResidentialTaxRate(level));
            }
            Check("Without social welfare staff, residential tax cannot go below 8%", limits.m_ResidentialTaxLimits.x == TaxControl.kResidentialFloor
                && limits.m_JobLevelTaxLimits.x >= TaxControl.kResidentialFloor && taxSystem.GetTaxRate(TaxAreaType.Residential) >= TaxControl.kResidentialFloor && lowest >= TaxControl.kResidentialFloor,
                $"limit {limits.m_ResidentialTaxLimits.x}, residential {taxSystem.GetTaxRate(TaxAreaType.Residential)}, lowest education level {lowest}");
        }

        private void CheckTaxGone()
        {
            TaxSystem taxSystem = World.GetOrCreateSystemManaged<TaxSystem>();
            Check("Tax staff gone after having had some: every rate back to the 10% default", m_Engine.Tax.LastLock == TaxLock.Default
                && taxSystem.TaxRate == TaxControl.kDefaultRate && taxSystem.GetTaxRate(TaxAreaType.Residential) == TaxControl.kDefaultRate
                && taxSystem.GetTaxRate(TaxAreaType.Industrial) == TaxControl.kDefaultRate && TaxLimits().m_TotalTaxLimits.x == TaxControl.kDefaultRate,
                $"lock {m_Engine.Tax.LastLock}, total {taxSystem.TaxRate}, residential {taxSystem.GetTaxRate(TaxAreaType.Residential)}, industrial {taxSystem.GetTaxRate(TaxAreaType.Industrial)}");
        }

        // ------------------------------------------------------------------ stage 5: litigation

        private void StartLitigation()
        {
            // Suits about 5% of their real length, so a verdict comes within minutes.
            LitigationSystem.TestTimeScale = 0.05f;
            SetPolicy(m_CitySystem.City, PolicyCatalog.EmissionStandard, true, 60f);
            Line("Emission standard raised from 30% to 60% to give the plaintiffs a case.");
            SetStage(Stage.SuitRaise);
        }

        private int SuitIndex(Entity policy) => World.GetOrCreateSystemManaged<LitigationSystem>().FindSuit(policy);

        private Lawsuit Suit(Entity policy) => EntityManager.GetBuffer<Lawsuit>(m_CitySystem.City, true)[SuitIndex(policy)];

        private void CheckSuitFiled()
        {
            LitigationSystem litigation = World.GetOrCreateSystemManaged<LitigationSystem>();
            int index = SuitIndex(PolicyCatalog.EmissionStandard);
            Lawsuit suit = Suit(PolicyCatalog.EmissionStandard);
            SuitGrounds grounds = (SuitGrounds)suit.m_Grounds;
            Check("Suit filed: case number, plaintiffs, workload, legitimate expectation ground",
                suit.m_CaseNumber >= 1001 && suit.m_Plaintiffs > 0 && suit.m_Workload > 0f && (grounds & SuitGrounds.LegitimateExpectation) != 0,
                $"{suit.m_Year}구합{suit.m_CaseNumber}, {suit.m_Plaintiffs} {(TargetGroup)suit.m_Group}, grounds {grounds}, workload {suit.m_Workload:0.0}, " +
                $"prior {suit.m_PriorIntensity:0.00} -> contested {suit.m_ContestedIntensity:0.00}, representative {suit.m_Representative.Index}");
            Line($"Win chance {litigation.WinChance(index):0.00}, hint {litigation.WinHint(index)} (0 low, 1 medium, 2 high), noise {suit.m_HintNoise:0.00}");
        }

        private void CheckInjunctionAndRespond()
        {
            LitigationSystem litigation = World.GetOrCreateSystemManaged<LitigationSystem>();
            Lawsuit suit = Suit(PolicyCatalog.EmissionStandard);
            Check("Injunction decided before the city answered (plaintiff bonus)", suit.m_DecidedUnanswered,
                $"stage {(InjunctionStage)suit.m_Injunction}, p {suit.m_InjunctionProbability:0.00}");
            bool answered = litigation.Respond(SuitIndex(PolicyCatalog.EmissionStandard), SuitResponse.Lawyer);
            suit = Suit(PolicyCatalog.EmissionStandard);
            Check("Answering with a lawyer fixes the fee and reviews the injunction once", answered && suit.m_LawyerFee > 0f && !suit.m_DecidedUnanswered,
                $"fee {suit.m_LawyerFee:0}, injunction {(InjunctionStage)suit.m_Injunction}");
        }

        private void CheckLostSuit()
        {
            Entity city = m_CitySystem.City;
            LitigationSystem litigation = World.GetOrCreateSystemManaged<LitigationSystem>();
            float reference = -1f;
            foreach (PolicyRuntime runtime in EntityManager.GetBuffer<PolicyRuntime>(city, true))
            {
                if (runtime.m_Policy == PolicyCatalog.EmissionStandard)
                {
                    reference = runtime.m_Reference;
                }
            }
            DynamicBuffer<CourtDebt> debts = EntityManager.GetBuffer<CourtDebt>(city, true);
            Check("Lost suit: the standard is off, banned, and people's reference is back to before it",
                !IsActive(PolicyCatalog.EmissionStandard) && litigation.IsBanned(PolicyCatalog.EmissionStandard) && reference == 0f,
                $"active {IsActive(PolicyCatalog.EmissionStandard)}, reference {reference:0.00}");
            Line($"Court debts: {debts.Length}{(debts.Length > 0 ? $", remaining {debts[0].m_Remaining:0}, daily cap {debts[0].m_DailyCap:0}" : "")}; damages row {m_Injection.GetRow(LitigationSystem.kDebtRow)}");
        }

        private void CheckBanCondition()
        {
            PolicyDefinition standard = PolicyCatalog.Definition(PolicyCatalog.kEmissionStandard);
            float intensity = m_Engine.LastIntensity(standard);
            Check("During the ban the slider can only come back within the change band", intensity <= standard.ChangeBand + 1e-3f,
                $"intensity {intensity:0.000}, band {standard.ChangeBand}");
        }

        private void CheckSettlement()
        {
            LitigationSystem litigation = World.GetOrCreateSystemManaged<LitigationSystem>();
            Entity tax = PolicyCatalog.Find(PolicyCatalog.kPollutionTax);
            litigation.Respond(SuitIndex(tax), SuitResponse.Official);
            Lawsuit suit = Suit(tax);
            // An offer that changes nothing leaves the whole grievance: refused unless the case is weak enough that
            // the plaintiffs would rather save their costs (limit above 100% of the grievance).
            bool acceptedNothing = litigation.ProposeSettlement(SuitIndex(tax), suit.m_ContestedIntensity, 0);
            bool weakCase = litigation.LastSettlementLimitShare >= 1f;
            Check("Settlement that changes nothing is accepted only when the plaintiffs' case is weak", acceptedNothing == weakCase,
                $"limit {litigation.LastSettlementLimitShare:0.00} x grievance, accepted {acceptedNothing}");
            if (acceptedNothing)
            {
                Line("Plaintiffs dropped the suit for nothing; the compensated offer is not tried.");
                return;
            }
            int compensation = (int)(suit.m_Claim + 1000000f);
            bool accepted = litigation.ProposeSettlement(SuitIndex(tax), 0.1f, compensation);
            Check("Settlement that lowers the tax and compensates is accepted and ends the suit", accepted && SuitIndex(tax) < 0, $"compensation {compensation}");
        }

        private TaxParameterData TaxLimits()
        {
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TaxParameterData>()))
            {
                return query.GetSingleton<TaxParameterData>();
            }
        }

        private void CheckWelfareCensus()
        {
            ServiceSeriesCensus census = m_Engine.Census;
            bool marked = EntityManager.HasComponent<SocialWelfareDesignation>(m_WelfareOffice);
            Check("Checkbox marks the welfare office", marked, null);
            Check("Marked office's staff count as social welfare", census.MaxWorkers(ServiceSeries.SocialWelfare) > 0,
                $"general administration {census.Workers(ServiceSeries.GeneralAdministration)}, social welfare {census.Workers(ServiceSeries.SocialWelfare)}/{census.MaxWorkers(ServiceSeries.SocialWelfare)}");
            World.GetOrCreateSystemManaged<Game.UI.InGame.SelectedInfoUISystem>().SetSelection(Entity.Null);
        }

        private void OnUiReport(string message)
        {
            if (m_Stage != Stage.Ui && m_Stage != Stage.WelfareUi)
            {
                return;
            }
            if (message == "done")
            {
                m_UiDone = true;
            }
            else if (message.StartsWith("shot:", StringComparison.Ordinal))
            {
                string file = Path.Combine(m_Directory, "selftest", message.Substring(5) + ".png");
                ScreenCapture.CaptureScreenshot(file);
                Line($"Screenshot {file}");
            }
            else if (message.StartsWith("check:", StringComparison.Ordinal))
            {
                // check:<0|1>:<name>|<detail>
                string body = message.Substring(8);
                int bar = body.IndexOf('|');
                Check("[UI] " + (bar >= 0 ? body.Substring(0, bar) : body), message[6] == '1', bar >= 0 ? body.Substring(bar + 1) : null);
            }
            else
            {
                Line("[UI] " + message);
            }
        }

        private void Finish()
        {
            LitigationSystem.TestTimeScale = 1f;
            LitigationSystem.TestForceInjunctionRequest = false;
            LitigationSystem.TestForcedVerdict = -1;
            LitigationSystem.TestFilingPolicy = Entity.Null;
            m_Injection.SetRow(kTestIncomeRow, true, (int)IncomeSource.FeeParking, 0);
            m_Injection.SetRow(kTestExpenseRow, false, (int)ExpenseSource.MapTileUpkeep, 0);
            Line($"Result: {m_Passed} passed, {m_Failed} failed, {UnityEngine.Time.realtimeSinceStartup - m_TestStart:0} s.");
            File.WriteAllText(Path.Combine(m_Directory, "selftest", "report.txt"), m_Report.ToString());
        }

        private void SetPolicy(Entity target, Entity policy, bool active, float adjustment)
        {
            // Same path as the vanilla policy panel: an event created at the end of the frame survives
            // until ModifiedSystem reads it (events made mid-frame are cleaned up first).
            EntityCommandBuffer buffer = m_EndFrameBarrier.CreateCommandBuffer();
            Entity e = buffer.CreateEntity(m_ModifyArchetype);
            buffer.SetComponent(e, new Modify(target, policy, active, adjustment));
        }

        private bool IsActive(Entity policy)
        {
            DynamicBuffer<Policy> policies = EntityManager.GetBuffer<Policy>(m_CitySystem.City, true);
            for (int i = 0; i < policies.Length; i++)
            {
                if (policies[i].m_Policy == policy)
                {
                    return (policies[i].m_Flags & PolicyFlags.Active) != 0;
                }
            }
            return false;
        }

        private int CountActive()
        {
            DynamicBuffer<Policy> policies = EntityManager.GetBuffer<Policy>(m_CitySystem.City, true);
            int count = 0;
            for (int i = 0; i < policies.Length; i++)
            {
                if ((policies[i].m_Flags & PolicyFlags.Active) != 0)
                {
                    count++;
                }
            }
            return count;
        }

        private bool IsLocked(Entity prefab)
        {
            return prefab != Entity.Null && EntityManager.HasComponent<Locked>(prefab) && EntityManager.IsComponentEnabled<Locked>(prefab);
        }

        private static float Delta(DynamicBuffer<CityModifier> modifiers, CityModifierType type)
        {
            return modifiers.Length > (int)type ? modifiers[(int)type].m_Delta.y : 0f;
        }

        private int Money()
        {
            return EntityManager.GetComponentData<PlayerMoney>(m_CitySystem.City).money;
        }

        private void SetStage(Stage stage)
        {
            m_Stage = stage;
            m_StageStart = UnityEngine.Time.realtimeSinceStartup;
            m_SimStart = m_SimulationSystem.frameIndex;
        }

        private float StageSeconds() => UnityEngine.Time.realtimeSinceStartup - m_StageStart;

        private uint SimFrames() => m_SimulationSystem.frameIndex - m_SimStart;

        private void Check(string name, bool ok, string detail)
        {
            if (ok)
            {
                m_Passed++;
            }
            else
            {
                m_Failed++;
            }
            Line($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail != null ? " — " + detail : string.Empty)}");
        }

        private void Line(string text)
        {
            m_Report.AppendLine(text);
            Mod.Log.Info(text);
        }

        [Preserve]
        public SelfTestSystem()
        {
        }
    }
}
