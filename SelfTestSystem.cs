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
        private enum Stage { Off, WaitMenu, Loading, Settle, Activate, WaitModifiers, EngineSamples, WaitMigration, BudgetPrepare, BudgetMeasure, WaitLock, Ui, WelfareUi, WelfareCensus, TaxStaff, TaxFloor, TaxGone, SuitRaise, SuitFiled, SuitIcon, SuitInjunction, SuitVerdict, SuitBan, SuitDefault, SuitDefaultVerdict, SuitSettle, SuitAuto, ReportUi, SaveGame, Reloading, ReloadSettle, Trading, TradingOff, Bundle2, Bundle2Off, LaterBundles, LaterBundlesOff, MoneyWatch, Finish, Quit }

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
        private Entity m_IconBuilding;
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
            else if (m_Stage == Stage.Reloading && mode == GameMode.Game)
            {
                Line("Saved city loaded again.");
                World.GetOrCreateSystemManaged<AutoSaveSystem>().Enabled = false;
                m_SimulationSystem.selectedSpeed = kTestSpeed;
                SetStage(Stage.ReloadSettle);
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
                        FocusIconBuilding();
                        SetStage(Stage.SuitIcon);
                    }
                    else if (SimFrames() > 8192)
                    {
                        Check("A suit is filed against the emission standard", false, "none within 8192 frames");
                        SetStage(Stage.Finish);
                    }
                    break;
                case Stage.SuitIcon:
                    if (StageSeconds() >= 4f && SimFrames() >= 256)
                    {
                        CheckSuitNotifications();
                        SetStage(Stage.SuitInjunction);
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
                        Check("Answering removes the warning icon and the badge count", !HasLawsuitIcon(m_IconBuilding)
                            && World.GetOrCreateSystemManaged<LitigationSystem>().UnansweredCount == 0, null);
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
                        // Next suit is answered by the "default response" setting (here: always a lawyer).
                        LitigationSystem.TestDefaultResponse = DefaultSuitResponse.AlwaysLawyer;
                        LitigationSystem.TestFilingPolicy = PolicyCatalog.Find(PolicyCatalog.kPollutionTax);
                        SetStage(Stage.SuitAuto);
                    }
                    else if (SimFrames() > 8192)
                    {
                        Check("A suit is filed against the pollution tax", false, "none within 8192 frames");
                        SetStage(Stage.Finish);
                    }
                    break;
                case Stage.SuitAuto:
                    if (SuitIndex(PolicyCatalog.Find(PolicyCatalog.kPollutionTax)) >= 0)
                    {
                        Lawsuit auto = Suit(PolicyCatalog.Find(PolicyCatalog.kPollutionTax));
                        Check("Default response answers a new suit at once with a lawyer, without the late surcharge",
                            auto.m_Response == (byte)SuitResponse.Lawyer && auto.m_LawyerFee > 0f, $"response {auto.m_Response}, fee {auto.m_LawyerFee:0}");
                        LitigationSystem.TestDefaultResponse = null;
                        CheckSettlementLimits();
                        m_UiDone = false;
                        m_UISystem.SetSelfTestStage(3);
                        SetStage(Stage.ReportUi);
                    }
                    else if (SimFrames() > 8192)
                    {
                        Check("A second suit is filed against the pollution tax", false, "none within 8192 frames");
                        SetStage(Stage.Finish);
                    }
                    break;
                case Stage.ReportUi:
                    if (m_UiDone || StageSeconds() > 90f)
                    {
                        if (!m_UiDone)
                        {
                            Check("Report UI test reported back", false, "no report within 90 s");
                        }
                        StartSaveRoundTrip();
                    }
                    break;
                case Stage.SaveGame:
                    if (m_SaveTask != null && m_SaveTask.IsCompleted)
                    {
                        bool saved = !m_SaveTask.IsFaulted && m_SaveTask.Result && FindSave(kRoundTripSave) != null;
                        Check("The city saves with the mod's data", saved, m_SaveTask.IsFaulted ? m_SaveTask.Exception?.GetBaseException().Message : null);
                        m_SaveTask = null;
                        if (saved)
                        {
                            Line($"Reloading '{kRoundTripSave}'.");
                            SetStage(Stage.Reloading);
                            _ = GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, FindSave(kRoundTripSave));
                        }
                        else
                        {
                            SetStage(Stage.Finish);
                        }
                    }
                    else if (StageSeconds() > 120f)
                    {
                        Check("The city saves with the mod's data", false, "no result within 120 s");
                        SetStage(Stage.Finish);
                    }
                    break;
                case Stage.Reloading:
                    if (StageSeconds() > 300f)
                    {
                        Check("The saved city loads again", false, "not loaded within 300 s");
                        SetStage(Stage.Finish);
                    }
                    break;
                case Stage.ReloadSettle:
                    if (StageSeconds() >= 5f && SimFrames() >= 1024)
                    {
                        CheckRoundTrip();
                        StartTrading();
                    }
                    break;
                case Stage.Trading:
                    if (SimFrames() >= 4096)
                    {
                        CheckTrading();
                        // Auctioning without the cap does nothing.
                        SetPolicy(m_CitySystem.City, PolicyCatalog.Find(PolicyCatalog.kEmissionTrading), false, 0f);
                        SetStage(Stage.TradingOff);
                    }
                    break;
                case Stage.TradingOff:
                    if (SimFrames() >= 2048)
                    {
                        PolicyDefinition auctionDefinition = PolicyCatalog.Definition(PolicyCatalog.kAllowanceAuction);
                        int auctionIndex = System.Array.IndexOf(PolicyDefinitions.All, auctionDefinition);
                        float auctionGrievance = m_Engine.GroupGrievance(auctionIndex, TargetGroup.IndustrialCompanies);
                        Check("With no allowances to pay for, auctioning stirs no grievance (judged by the actual burden)",
                            m_Engine.ActualBurden(auctionDefinition) == 0f && auctionGrievance <= 1e-4f,
                            $"burden {m_Engine.ActualBurden(auctionDefinition):0.000}, grievance {auctionGrievance:0.000}");
                        Check("Allowance auctioning does nothing while emission trading is off",
                            m_Engine.LastIntensity(auctionDefinition) == 0f && m_Injection.GetRow(PolicyDefinitions.kAuctionIncomeRow) == 0,
                            $"auction intensity {m_Engine.LastIntensity(auctionDefinition):0.00}, revenue {m_Injection.GetRow(PolicyDefinitions.kAuctionIncomeRow)}");
                        StartBundle2();
                    }
                    break;
                case Stage.Bundle2:
                    if (SimFrames() >= 8192)
                    {
                        CheckBundle2();
                        SetPolicy(m_CitySystem.City, PolicyCatalog.Find(PolicyCatalog.kMinimumWage), false, 0f);
                        SetPolicy(m_CitySystem.City, PolicyCatalog.Find(PolicyCatalog.kTalentAttraction), false, 0f);
                        SetPolicy(m_CitySystem.City, PolicyCatalog.Find(PolicyCatalog.kRedevelopment), false, 0f);
                        SetStage(Stage.Bundle2Off);
                    }
                    break;
                case Stage.Bundle2Off:
                    if (SimFrames() >= 4096)
                    {
                        CheckBundle2Off();
                        StartLaterBundles();
                    }
                    break;
                case Stage.LaterBundles:
                    if (SimFrames() >= 16384)
                    {
                        CheckLaterBundles();
                        StopLaterBundles();
                        SetStage(Stage.LaterBundlesOff);
                    }
                    break;
                case Stage.LaterBundlesOff:
                    if (SimFrames() >= 8192)
                    {
                        CheckLaterBundlesOff();
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
            int suable = 0;
            foreach (PolicyDefinition d in PolicyDefinitions.All)
            {
                suable += d.Instrument == Instrument.Authority || d.Instrument == Instrument.Treasure ? 1 : 0;
            }
            Check($"Lawsuit icon and court chirps are registered (two per A/T policy, {2 * suable})", CourtPrefabs.Icon != Entity.Null && CourtPrefabs.Chirps.Count == 2 * suable,
                $"icon {(CourtPrefabs.Icon != Entity.Null ? "on" : "off")}, {CourtPrefabs.Chirps.Count} chirps");
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
                // Only the first bundle is switched on here; later policies get their own stages.
                if (System.Array.IndexOf(kFirstBundle, definition.Id) >= 0)
                {
                    allOn &= IsActive(definition.Prefab);
                }
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

        /// <summary>Follows the city hall that shows the warning icon with the game's orbit camera, for the screenshot.</summary>
        private void FocusIconBuilding()
        {
            m_IconBuilding = Suit(PolicyCatalog.EmissionStandard).m_IconBuilding;
            Game.Rendering.CameraUpdateSystem cameras = World.GetOrCreateSystemManaged<Game.Rendering.CameraUpdateSystem>();
            if (m_IconBuilding != Entity.Null && cameras.orbitCameraController != null)
            {
                cameras.orbitCameraController.followedEntity = m_IconBuilding;
                cameras.orbitCameraController.zoom = 250f;
                cameras.activeCameraController = cameras.orbitCameraController;
            }
        }

        private void CheckSuitNotifications()
        {
            LitigationSystem litigation = World.GetOrCreateSystemManaged<LitigationSystem>();
            bool hall = m_IconBuilding != Entity.Null && EntityManager.HasComponent<AdminBuildingData>(EntityManager.GetComponentData<PrefabRef>(m_IconBuilding).m_Prefab);
            Check("The warning icon sits on the city hall nearest the representative", hall && HasLawsuitIcon(m_IconBuilding),
                $"building {m_IconBuilding.Index}, city hall {hall}");
            Check("The badge counts one unanswered suit", litigation.UnansweredCount == 1, $"{litigation.UnansweredCount}");
            int chirps = 0;
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Triggers.Chirp>(), ComponentType.ReadOnly<PrefabRef>()))
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    foreach (Entity chirp in CourtPrefabs.Chirps.Values)
                    {
                        chirps += prefab == chirp ? 1 : 0;
                    }
                }
            }
            Check("The representative plaintiff posted a chirp", chirps > 0, $"{chirps} court chirps");
            string file = Path.Combine(m_Directory, "selftest", "suit-icon.png");
            ScreenCapture.CaptureScreenshot(file);
            Line($"Screenshot {file}");
        }

        private bool HasLawsuitIcon(Entity building)
        {
            if (building == Entity.Null || !EntityManager.TryGetBuffer(building, true, out DynamicBuffer<Game.Notifications.IconElement> icons))
            {
                return false;
            }
            for (int i = 0; i < icons.Length; i++)
            {
                if (EntityManager.TryGetComponent(icons[i].m_Icon, out PrefabRef prefab) && prefab.m_Prefab == CourtPrefabs.Icon)
                {
                    return true;
                }
            }
            return false;
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
            // A refused offer must wait three game hours (2026-11-04 user proposal), so the limit cannot be probed.
            bool retried = litigation.ProposeSettlement(SuitIndex(tax), 0.1f, 0);
            Check("A refused settlement offer blocks the next one for 3 hours", !retried && litigation.LastSettlementResult == SettlementResult.Waiting,
                $"{litigation.LastSettlementResult}, offers {Suit(tax).m_Offers}");
            LitigationSystem.TestSkipSettlementWait = true;
            int compensation = (int)(suit.m_Claim + 1000000f);
            bool accepted = litigation.ProposeSettlement(SuitIndex(tax), 0.1f, compensation);
            LitigationSystem.TestSkipSettlementWait = false;
            Check("Settlement that lowers the tax and compensates is accepted and ends the suit", accepted && SuitIndex(tax) < 0, $"compensation {compensation}");
        }

        // ------------------------------------------------------------------ stage 8: offer limits, save round trip, timing

        private const string kRoundTripSave = "RP SelfTest Stage8";

        private static readonly string[] kFirstBundle =
        {
            PolicyCatalog.kEquipmentMandate, PolicyCatalog.kEmissionStandard, PolicyCatalog.kPollutionTax,
            PolicyCatalog.kGreenSubsidy, PolicyCatalog.kEnvironmentalMonitoring, PolicyCatalog.kEmissionDisclosure,
        };

        private EconomyParameterData m_EconomyBefore;
        private int m_Bundle2Peak;
        private Unity.Mathematics.float4 m_EducationBefore;

        /// <summary>§14.6: minimum wage 20%, talent attraction 100%, redevelopment 50%.</summary>
        private void StartBundle2()
        {
            m_EconomyBefore = Singleton<EconomyParameterData>();
            m_EducationBefore = Singleton<DemandParameterData>().m_NewCitizenEducationParameters;
            SetPolicy(m_CitySystem.City, PolicyCatalog.Find(PolicyCatalog.kMinimumWage), true, 20f);
            SetPolicy(m_CitySystem.City, PolicyCatalog.Find(PolicyCatalog.kTalentAttraction), true, 100f);
            SetPolicy(m_CitySystem.City, PolicyCatalog.Find(PolicyCatalog.kRedevelopment), true, 50f);
            Line($"Bundle 2 on: minimum wage +20%, talent attraction 100%, redevelopment 50%. Wages {m_EconomyBefore.m_Wage0}/{m_EconomyBefore.m_Wage1}, new citizen education {m_EducationBefore}.");
            SetStage(Stage.Bundle2);
        }

        private T Singleton<T>() where T : unmanaged, IComponentData
        {
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<T>()))
            {
                return query.GetSingleton<T>();
            }
        }

        private static float HighShare(Unity.Mathematics.float4 p) => p.w + Unity.Mathematics.math.max(0f, 1f - (p.x + p.y + p.z + p.w));

        private void CheckBundle2()
        {
            EconomyParameterData economy = Singleton<EconomyParameterData>();
            float raise0 = economy.m_Wage0 / (float)m_EconomyBefore.m_Wage0 - 1f;
            float raise1 = economy.m_Wage1 / (float)m_EconomyBefore.m_Wage1 - 1f;
            Line($"Bundle 2: wages {economy.m_Wage0}/{economy.m_Wage1} (+{raise0:P1}/+{raise1:P1}).");
            Check("Minimum wage raises the simple-job wage, up to the slider's 20%", raise0 > 0f && raise0 <= 0.2001f, $"+{raise0:P1}");
            // Wages are whole numbers, so allow one unit of rounding on each.
            float expected1 = 0.3f * raise0 * m_EconomyBefore.m_Wage1;
            float actual1 = economy.m_Wage1 - m_EconomyBefore.m_Wage1;
            Check("Basic-job wages follow at about 0.3 of the raise (spillover)", raise0 > 0f && Unity.Mathematics.math.abs(actual1 - expected1) <= 1.5f,
                $"basic +{actual1:0}, expected +{expected1:0.0}");
            m_Bundle2Peak = economy.m_Wage0;
            Unity.Mathematics.float4 education = Singleton<DemandParameterData>().m_NewCitizenEducationParameters;
            float before = HighShare(m_EducationBefore);
            float after = HighShare(education);
            Check("Talent attraction raises the share of highly educated newcomers (at most double)", after > before && after <= 2f * before + 1e-4f,
                $"levels 3-4: {before:P1} -> {after:P1}; probabilities {education}");
            Check("Newcomer education probabilities still add up within 1", education.x + education.y + education.z + education.w <= 1f + 1e-4f, $"{education}");
            PolicyDefinition talent = PolicyCatalog.Definition(PolicyCatalog.kTalentAttraction);
            Check("Talent attraction fades: 71% after 5 days, never below 50%",
                Unity.Mathematics.math.abs(PolicyEngineSystem.Fading(talent, 5f) - 0.7071f) < 1e-3f && PolicyEngineSystem.Fading(talent, 100f) == 0.5f && PolicyEngineSystem.Fading(talent, 0f) == 1f, null);
            float leveling = AirModifierOf(PolicyCatalog.Find(PolicyCatalog.kRedevelopment), Game.City.CityModifierType.BuildingLevelingCost);
            Check("Redevelopment lowers the building upgrade cost", leveling < 0f && leveling >= -0.5f, $"modifier {leveling:0.000}");
            Check("Bundle 2 money rows appear", m_Injection.GetRow(PolicyDefinitions.kMinimumWageRow) > 0 && m_Injection.GetRow(PolicyDefinitions.kTalentRow) > 0
                && m_Injection.GetRow(PolicyDefinitions.kRedevelopmentRow) > 0,
                $"inspection {m_Injection.GetRow(PolicyDefinitions.kMinimumWageRow)}, grants {m_Injection.GetRow(PolicyDefinitions.kTalentRow)}, redevelopment {m_Injection.GetRow(PolicyDefinitions.kRedevelopmentRow)}");
        }

        /// <summary>
        /// Switched off, the effects wind down with the policies' lag; restoring a lever (as on loading another city)
        /// writes the game's own value back exactly.
        /// </summary>
        private void CheckBundle2Off()
        {
            EconomyParameterData economy = Singleton<EconomyParameterData>();
            Check("Switched off, the minimum wage winds back toward the game's wage", economy.m_Wage0 <= m_Bundle2Peak && economy.m_Wage0 >= m_EconomyBefore.m_Wage0,
                $"{m_EconomyBefore.m_Wage0} <= {economy.m_Wage0} <= {m_Bundle2Peak}");
            ParameterLevers.All[ParameterLevers.kWage0].Restore(EntityManager);
            ParameterLevers.All[ParameterLevers.kWage1].Restore(EntityManager);
            ParameterLevers.All[ParameterLevers.kNewCitizenHighEducation].Restore(EntityManager);
            economy = Singleton<EconomyParameterData>();
            Unity.Mathematics.float4 education = Singleton<DemandParameterData>().m_NewCitizenEducationParameters;
            Check("Restoring the levers gives back the game's wages and newcomer education exactly",
                economy.m_Wage0 == m_EconomyBefore.m_Wage0 && economy.m_Wage1 == m_EconomyBefore.m_Wage1 && education.Equals(m_EducationBefore),
                $"wages {economy.m_Wage0}/{economy.m_Wage1}, education {education}");
        }

        // ------------------------------------------------------------------ batches 3–10 (§14.7, §14.10–14.16)

        private static readonly string[] kLaterBundles =
        {
            PolicyCatalog.kWelfareOutreach, PolicyCatalog.kHousingFirst, PolicyCatalog.kReentrySupport, PolicyCatalog.kVictimSupport,
            PolicyCatalog.kProcedureSimplification, PolicyCatalog.kEGovernment, PolicyCatalog.kCivilServicePay,
            PolicyCatalog.kSpecialistPosts, PolicyCatalog.kParticipatoryBudgeting, PolicyCatalog.kPublicHearings,
            PolicyCatalog.kPerformanceManagement, PolicyCatalog.kOmbudsman, PolicyCatalog.kInformationDisclosure,
            PolicyCatalog.kArrearsCollection, PolicyCatalog.kCompetitiveBidding, PolicyCatalog.kInterMunicipalAgreement,
            PolicyCatalog.kFiscalRule, PolicyCatalog.kContractingOut,
            PolicyCatalog.kIndustryCluster, PolicyCatalog.kCorporateRescue, PolicyCatalog.kRegionalJobPact,
            PolicyCatalog.kUnemploymentDuration, PolicyCatalog.kResourceQuota,
            PolicyCatalog.kRentCap, PolicyCatalog.kNoiseControl, PolicyCatalog.kVehicleEmissions, PolicyCatalog.kDrinkingWater,
            PolicyCatalog.kGroundwaterProtection, PolicyCatalog.kParkCare,
            PolicyCatalog.kSpeedCameras, PolicyCatalog.kDisasterCode, PolicyCatalog.kFireCode,
            PolicyCatalog.kCommunityPolicing, PolicyCatalog.kZeroTolerance, PolicyCatalog.kHarsherSentencing,
            PolicyCatalog.kEmergencyResponse, PolicyCatalog.kCpted,
            PolicyCatalog.kHealthCampaign, PolicyCatalog.kEmergencyCare, PolicyCatalog.kAirQualityAlert,
            PolicyCatalog.kPublicHealthcare, PolicyCatalog.kScholarships, PolicyCatalog.kFreeHighSchool, PolicyCatalog.kLifelongLearning,
            PolicyCatalog.kDisasterWarning, PolicyCatalog.kEnergyCode, PolicyCatalog.kWasteFee,
        };

        private Unity.Mathematics.float2 m_ConsumptionBefore;

        /// <summary>Electricity and garbage summed over the zoned building prefabs (the §14.18 levers scale these).</summary>
        private Unity.Mathematics.float2 ZonedConsumption()
        {
            Unity.Mathematics.float2 sum = 0f;
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<ConsumptionData>(), ComponentType.ReadOnly<SpawnableBuildingData>()))
            using (NativeArray<ConsumptionData> data = query.ToComponentDataArray<ConsumptionData>(Allocator.Temp))
            {
                for (int i = 0; i < data.Length; i++)
                {
                    sum += new Unity.Mathematics.float2(data[i].m_ElectricityConsumption, data[i].m_GarbageAccumulation);
                }
            }
            return sum;
        }

        private FireConfigurationData m_FireBefore;
        private ExtractorParameterData m_ExtractorBefore;
        private EducationParameterData m_EducationParamsBefore;
        private WaterPipeParameterData m_WaterBefore;
        private PoliceConfigurationData m_PoliceBefore;
        private CitizenHappinessParameterData m_HappinessBefore;
        private GarbageParameterData m_GarbageBefore;
        private PollutionParameterData m_PollutionBefore;
        private EconomyParameterData m_LaterEconomyBefore;
        private DemandParameterData m_LaterDemandBefore;

        /// <summary>Every policy of batches 3–10 at its default strength at once; random suits off so the court stays out.</summary>
        private void StartLaterBundles()
        {
            LitigationSystem.TestDisableRandomFiling = true;
            // Batch 3 runs on social welfare staff and arrears collection on tax staff; the earlier stages handed both
            // offices back to general administration, and the reload since then made new entities, so find them again.
            m_WelfareOffice = FindGeneralWelfareOffice(Entity.Null);
            m_TaxOffice = FindGeneralWelfareOffice(m_WelfareOffice);
            World.GetOrCreateSystemManaged<WelfareSeriesSection>().Set(m_WelfareOffice, WelfareSeriesSection.Choice.SocialWelfare);
            World.GetOrCreateSystemManaged<WelfareSeriesSection>().Set(m_TaxOffice, WelfareSeriesSection.Choice.Tax);
            Line($"Welfare office {m_WelfareOffice.Index} set to social welfare, {m_TaxOffice.Index} to tax staff.");
            m_FireBefore = Singleton<FireConfigurationData>();
            m_ExtractorBefore = Singleton<ExtractorParameterData>();
            m_EducationParamsBefore = Singleton<EducationParameterData>();
            m_WaterBefore = Singleton<WaterPipeParameterData>();
            m_PoliceBefore = Singleton<PoliceConfigurationData>();
            m_HappinessBefore = Singleton<CitizenHappinessParameterData>();
            m_GarbageBefore = Singleton<GarbageParameterData>();
            m_PollutionBefore = Singleton<PollutionParameterData>();
            m_LaterEconomyBefore = Singleton<EconomyParameterData>();
            m_LaterDemandBefore = Singleton<DemandParameterData>();
            m_ConsumptionBefore = ZonedConsumption();
            foreach (string id in kLaterBundles)
            {
                PolicyDefinition definition = PolicyCatalog.Definition(id);
                float value = definition.IsSlider ? (definition.SliderDefault > 0f ? definition.SliderDefault : 0.5f * definition.SliderRange.max) : 0f;
                SetPolicy(m_CitySystem.City, PolicyCatalog.Find(id), true, value);
            }
            Line($"Batches 3-10 on: {kLaterBundles.Length} policies at their default strength (random suits off).");
            LogDisasterWarning("before");
            SetStage(Stage.LaterBundles);
        }

        /// <summary>
        /// §14.14 early warning (held): vanilla's warning time starts at 0, so log which prefabs give it and the city's
        /// current value, to size an absolute effect later.
        /// </summary>
        private void LogDisasterWarning(string when)
        {
            var sources = new StringBuilder();
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<CityModifierData>()))
            using (NativeArray<Entity> prefabs = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity prefab in prefabs)
                {
                    DynamicBuffer<CityModifierData> modifiers = EntityManager.GetBuffer<CityModifierData>(prefab, true);
                    for (int m = 0; m < modifiers.Length; m++)
                    {
                        if (modifiers[m].m_Type == CityModifierType.DisasterWarningTime)
                        {
                            string name = m_PrefabSystem.GetPrefabName(prefab);
                            sources.Append($"{name} {modifiers[m].m_Mode} [{modifiers[m].m_Range.min}, {modifiers[m].m_Range.max}]; ");
                        }
                    }
                }
            }
            DynamicBuffer<CityModifier> city = EntityManager.GetBuffer<CityModifier>(m_CitySystem.City, true);
            int index = (int)CityModifierType.DisasterWarningTime;
            string current = city.Length > index ? $"{city[index].m_Delta}" : "none";
            Line($"Disaster warning ({when}): city delta {current}; sources: {(sources.Length > 0 ? sources.ToString() : "none")}");
        }

        private static string Short(string id) => id.Substring(id.LastIndexOf('.') + 1);

        /// <summary>A welfare office still counted as general administration, other than <paramref name="except"/>.</summary>
        private Entity FindGeneralWelfareOffice(Entity except)
        {
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Game.Tools.Temp>(), ComponentType.Exclude<Deleted>()))
            using (NativeArray<Entity> buildings = query.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < buildings.Length; i++)
                {
                    if (buildings[i] != except
                        && EntityManager.HasComponent<WelfareOfficeData>(EntityManager.GetComponentData<PrefabRef>(buildings[i]).m_Prefab)
                        && WelfareSeriesSection.Get(EntityManager, buildings[i]) == WelfareSeriesSection.Choice.GeneralAdministration)
                    {
                        return buildings[i];
                    }
                }
            }
            return Entity.Null;
        }

        private void CheckLaterBundles()
        {
            var wrong = new StringBuilder();
            int outputs = 0;
            foreach (string id in kLaterBundles)
            {
                PolicyDefinition definition = PolicyCatalog.Definition(id);
                Entity prefab = PolicyCatalog.Find(id);
                if (!IsActive(prefab))
                {
                    wrong.Append($"{Short(id)} not active; ");
                    continue;
                }
                foreach (PolicyOutput output in definition.Outputs)
                {
                    switch (output.Kind)
                    {
                        case OutputKind.CityModifier:
                            float value = AirModifierOf(prefab, output.Modifier);
                            outputs++;
                            if (!(value * output.FullEffect > 0f))
                            {
                                wrong.Append($"{Short(id)} {output.Modifier} {value:0.0000}; ");
                            }
                            break;
                        case OutputKind.Parameter:
                            if (output.Key == ParameterLevers.kIndustrialBaseDemand)
                            {
                                break;
                            }
                            float change = m_Engine.LeverFactor(output.Key) - 1f;
                            outputs++;
                            if (!(change * output.FullEffect > 0f))
                            {
                                wrong.Append($"{Short(id)} {output.Key} x{change + 1f:0.0000}; ");
                            }
                            break;
                        case OutputKind.Income:
                        case OutputKind.Expense:
                            if (output.Basis == MoneyBasis.Intensity || output.Basis == MoneyBasis.TaxShare)
                            {
                                outputs++;
                                int row = m_Injection.GetRow(output.Key);
                                if (row <= 0)
                                {
                                    wrong.Append($"{Short(id)} row {Short(output.Key)} {row}; ");
                                }
                            }
                            break;
                    }
                }
                if (definition.Reform != ReformKind.None)
                {
                    System.Collections.Generic.List<ReformRow> rows = m_Engine.Reforms.Rows(definition);
                    if (rows.Count > 0)
                    {
                        outputs++;
                        bool any = false;
                        foreach (ReformRow row in rows)
                        {
                            any |= Mathf.Abs(row.Value) > 1e-5f;
                        }
                        if (!any)
                        {
                            wrong.Append($"{Short(id)} reform rows all zero; ");
                        }
                    }
                }
            }
            Line($"Staff for the stage: social welfare {m_Engine.Census.Workers(ServiceSeries.SocialWelfare)}, tax {m_Engine.Census.Workers(ServiceSeries.Tax)}.");
            Check($"Every output of batches 3-10 moves the right way ({outputs} outputs)", wrong.Length == 0, wrong.Length == 0 ? null : wrong.ToString());

            FireConfigurationData fire = Singleton<FireConfigurationData>();
            float ratioBefore = m_FireBefore.m_StructuralIntegrityLevel3 / m_FireBefore.m_BuildingStructuralIntegrity;
            float ratioNow = fire.m_StructuralIntegrityLevel3 / fire.m_BuildingStructuralIntegrity;
            Check("Fire safety code raises every building's structural integrity by one factor, not the non-building default",
                fire.m_BuildingStructuralIntegrity > m_FireBefore.m_BuildingStructuralIntegrity && Mathf.Abs(ratioNow - ratioBefore) < 1e-4f
                && fire.m_DefaultStructuralIntegrity == m_FireBefore.m_DefaultStructuralIntegrity,
                $"building {m_FireBefore.m_BuildingStructuralIntegrity} -> {fire.m_BuildingStructuralIntegrity}, level 3 {m_FireBefore.m_StructuralIntegrityLevel3} -> {fire.m_StructuralIntegrityLevel3}");
            ExtractorParameterData extractor = Singleton<ExtractorParameterData>();
            Check("Natural resource quota slows the depletion of every resource (ore and oil store the reciprocal)",
                extractor.m_FishConsumption < m_ExtractorBefore.m_FishConsumption && extractor.m_ForestConsumption < m_ExtractorBefore.m_ForestConsumption
                && extractor.m_FertilityConsumption < m_ExtractorBefore.m_FertilityConsumption
                && extractor.m_OreConsumption > m_ExtractorBefore.m_OreConsumption && extractor.m_OilConsumption > m_ExtractorBefore.m_OilConsumption,
                $"fish {m_ExtractorBefore.m_FishConsumption} -> {extractor.m_FishConsumption}, ore {m_ExtractorBefore.m_OreConsumption} -> {extractor.m_OreConsumption}, oil {m_ExtractorBefore.m_OilConsumption} -> {extractor.m_OilConsumption}");
            EducationParameterData education = Singleton<EducationParameterData>();
            Check("Free high school and lifelong learning raise the schooling chances (high school at most +20%)",
                education.m_EnterHighSchoolProbability > m_EducationParamsBefore.m_EnterHighSchoolProbability
                && education.m_EnterHighSchoolProbability <= 1.2f * m_EducationParamsBefore.m_EnterHighSchoolProbability + 1e-4f
                && education.m_AdultEnterHighSchoolProbability > m_EducationParamsBefore.m_AdultEnterHighSchoolProbability
                && education.m_WorkerContinueEducationProbability > m_EducationParamsBefore.m_WorkerContinueEducationProbability,
                $"teen {m_EducationParamsBefore.m_EnterHighSchoolProbability} -> {education.m_EnterHighSchoolProbability}, adult {m_EducationParamsBefore.m_AdultEnterHighSchoolProbability} -> {education.m_AdultEnterHighSchoolProbability}, worker {m_EducationParamsBefore.m_WorkerContinueEducationProbability} -> {education.m_WorkerContinueEducationProbability}");
            Unity.Mathematics.float2 consumption = ZonedConsumption();
            Check("Energy code and waste fee lower the electricity and garbage of zoned building prefabs",
                consumption.x < m_ConsumptionBefore.x && consumption.y < m_ConsumptionBefore.y,
                $"electricity {m_ConsumptionBefore.x:0} -> {consumption.x:0}, garbage {m_ConsumptionBefore.y:0} -> {consumption.y:0}");
            int warning = System.Array.IndexOf(PolicyDefinitions.All, PolicyCatalog.Definition(PolicyCatalog.kDisasterWarning));
            float seconds = AirModifierOf(PolicyCatalog.Find(PolicyCatalog.kDisasterWarning), CityModifierType.DisasterWarningTime);
            float expectedSeconds = 180f * m_Engine.LastResults[warning].Progress * m_Engine.Reforms.PostCapacity;
            Check("Early warning adds seconds = 180 x delivery x post and telecom capacity",
                seconds > 0f && Mathf.Abs(seconds - expectedSeconds) < 0.05f * expectedSeconds + 0.01f,
                $"+{seconds:0.00} s, expected {expectedSeconds:0.00} (telecom {m_Engine.Reforms.PostCapacity:0.00})");
            int housingFirst = System.Array.IndexOf(PolicyDefinitions.All, PolicyCatalog.Definition(PolicyCatalog.kHousingFirst));
            float amplification = m_Engine.Amplification(housingFirst);
            Check("Welfare outreach amplifies Housing First (§14.8, at most x1.2)", amplification > 1f && amplification <= 1.2f + 1e-4f, $"x{amplification:0.0000}");
            float inflation = m_Engine.Reforms.ReportInflation;
            Check("Performance management inflates the other policies' reported delivery (at most 10 points)", inflation > 0f && inflation <= 0.1f + 1e-4f, $"+{inflation:0.0000}");
            Line($"Legitimacy {m_Engine.LastState.m_Legitimacy:0.000} with community policing (+0.03) and zero tolerance (-0.04) on.");
            LogDisasterWarning("with batches on");
        }

        private void StopLaterBundles()
        {
            foreach (string id in kLaterBundles)
            {
                SetPolicy(m_CitySystem.City, PolicyCatalog.Find(id), false, 0f);
            }
        }

        /// <summary>Switched off and the levers restored, every parameter the later batches touch is the game's own again.</summary>
        private void CheckLaterBundlesOff()
        {
            var keys = new System.Collections.Generic.HashSet<string>();
            foreach (string id in kLaterBundles)
            {
                foreach (PolicyOutput output in PolicyCatalog.Definition(id).Outputs)
                {
                    // Industrial base demand is shared with the first bundle, which may still be on.
                    if (output.Kind == OutputKind.Parameter && output.Key != ParameterLevers.kIndustrialBaseDemand)
                    {
                        keys.Add(output.Key);
                    }
                }
            }
            foreach (string key in keys)
            {
                ParameterLevers.All[key].Restore(EntityManager);
            }
            ExtractorParameterData extractor = Singleton<ExtractorParameterData>();
            bool extractorBack = Same(extractor.m_FishConsumption, m_ExtractorBefore.m_FishConsumption) && Same(extractor.m_ForestConsumption, m_ExtractorBefore.m_ForestConsumption)
                && Same(extractor.m_FertilityConsumption, m_ExtractorBefore.m_FertilityConsumption)
                && Same(extractor.m_OreConsumption, m_ExtractorBefore.m_OreConsumption) && Same(extractor.m_OilConsumption, m_ExtractorBefore.m_OilConsumption);
            EconomyParameterData economy = Singleton<EconomyParameterData>();
            bool economyBack = economy.m_UnemploymentAllowanceMaxDays == m_LaterEconomyBefore.m_UnemploymentAllowanceMaxDays
                && economy.m_CompanyBankruptcyLimit == m_LaterEconomyBefore.m_CompanyBankruptcyLimit
                && economy.m_ExtractorProductionEfficiency == m_LaterEconomyBefore.m_ExtractorProductionEfficiency
                && economy.m_LandValueModifier.Equals(m_LaterEconomyBefore.m_LandValueModifier)
                && economy.m_CityServiceWageAdjustment == m_LaterEconomyBefore.m_CityServiceWageAdjustment
                && economy.m_MaxCitySpecializationBonus == m_LaterEconomyBefore.m_MaxCitySpecializationBonus;
            bool demandBack = Singleton<DemandParameterData>().m_CommuterWorkerRatioLimit == m_LaterDemandBefore.m_CommuterWorkerRatioLimit;
            var differ = new StringBuilder();
            if (!Singleton<FireConfigurationData>().Equals(m_FireBefore)) differ.Append("fire; ");
            if (!Singleton<EducationParameterData>().Equals(m_EducationParamsBefore)) differ.Append("education; ");
            if (!Singleton<WaterPipeParameterData>().Equals(m_WaterBefore)) differ.Append("water; ");
            if (!Singleton<PoliceConfigurationData>().Equals(m_PoliceBefore)) differ.Append("police; ");
            if (!Singleton<CitizenHappinessParameterData>().Equals(m_HappinessBefore)) differ.Append("happiness; ");
            if (!Singleton<GarbageParameterData>().Equals(m_GarbageBefore)) differ.Append("garbage; ");
            if (!Singleton<PollutionParameterData>().Equals(m_PollutionBefore)) differ.Append("pollution; ");
            if (!extractorBack) differ.Append("extractor; ");
            if (!economyBack) differ.Append("economy; ");
            if (!demandBack) differ.Append("demand; ");
            Unity.Mathematics.float2 consumption = ZonedConsumption();
            if (!Same(consumption.x, m_ConsumptionBefore.x) || !Same(consumption.y, m_ConsumptionBefore.y)) differ.Append($"building consumption {consumption} vs {m_ConsumptionBefore}; ");
            Check($"Restoring the {keys.Count} levers of batches 3-10 gives back the game's values", differ.Length == 0, differ.Length == 0 ? null : differ.ToString());
            LitigationSystem.TestDisableRandomFiling = false;
            World.GetOrCreateSystemManaged<WelfareSeriesSection>().Set(m_WelfareOffice, WelfareSeriesSection.Choice.GeneralAdministration);
            World.GetOrCreateSystemManaged<WelfareSeriesSection>().Set(m_TaxOffice, WelfareSeriesSection.Choice.GeneralAdministration);
        }

        private static bool Same(float a, float b) => Mathf.Abs(a - b) <= 1e-6f * Mathf.Max(1f, Mathf.Abs(b));

        private float AirModifierOf(Entity prefab, Game.City.CityModifierType type)
        {
            if (!EntityManager.TryGetBuffer(prefab, true, out DynamicBuffer<CityModifierData> modifiers))
            {
                return 0f;
            }
            for (int m = 0; m < modifiers.Length; m++)
            {
                if (modifiers[m].m_Type == type)
                {
                    return modifiers[m].m_Range.min;
                }
            }
            return 0f;
        }
        private float m_TradingDemandBefore;

        /// <summary>Emission trading at its largest cap with half the allowances auctioned (§10.6).</summary>
        private void StartTrading()
        {
            m_TradingDemandBefore = IndustrialBaseDemand();
            SetPolicy(m_CitySystem.City, PolicyCatalog.Find(PolicyCatalog.kEmissionTrading), true, 60f);
            SetPolicy(m_CitySystem.City, PolicyCatalog.Find(PolicyCatalog.kAllowanceAuction), true, 50f);
            Line("Emission trading on (cap -60%), half the allowances auctioned.");
            SetStage(Stage.Trading);
        }

        private void CheckTrading()
        {
            PolicyEngineSystem e = m_Engine;
            float required = Unity.Mathematics.math.max(0f, 0.6f - e.TradingOtherCut);
            Line($"Trading: other policies cut {e.TradingOtherCut:0.000}, cap asks {e.TradingRequired:0.000} more, price {e.TradingPrice:0.000} " +
                $"(tax at 100% = {FirmModel.kBasePrice}), delivered cut {e.TradingCut:0.000}, holding allowances {e.TradingCompliance:0.00}, auction {e.TradingAuctionShare:0.00}.");
            Check("The cap counts what other policies already cut (waterbed effect)", Unity.Mathematics.math.abs(e.TradingRequired - required) < 1e-3f,
                $"required {e.TradingRequired:0.000}, expected {required:0.000}");
            Check("The allowance price clears the cap: companies cut what is left at that price",
                required <= 0f ? e.TradingPrice == 0f : Unity.Mathematics.math.abs(FirmModel.Abatement(e.TradingPrice) - required) < 0.01f,
                $"abatement at the price {FirmModel.Abatement(e.TradingPrice):0.000}");
            int revenue = m_Injection.GetRow(PolicyDefinitions.kAuctionIncomeRow);
            Check("Auctioned allowances bring city revenue when they have a price", e.TradingPrice > 0f ? revenue > 0 : revenue == 0, $"revenue {revenue}/month");
            PolicyDefinition auctioning = PolicyCatalog.Definition(PolicyCatalog.kAllowanceAuction);
            float expectedBurden = Unity.Mathematics.math.saturate(e.TradingAuctionShare * e.TradingPrice / FirmModel.PriceForAbatement(PolicyDefinitions.kTradingMaxCut));
            Check("Auctioning is felt by what companies pay: auction share × allowance price",
                Unity.Mathematics.math.abs(e.ActualBurden(auctioning) - expectedBurden) < 1e-3f, $"burden {e.ActualBurden(auctioning):0.000}, expected {expectedBurden:0.000}");
            Check("Emissions trading has an administration cost", m_Injection.GetRow(PolicyDefinitions.kTradingOperationRow) > 0,
                $"{m_Injection.GetRow(PolicyDefinitions.kTradingOperationRow)}/month");
            // Other levers (the green subsidy) move the same demand, so the leakage share itself is checked.
            float leakage = 0.03f * e.TradingAuctionShare * System.Math.Min(1f, e.TradingPrice / FirmModel.kBasePrice);
            float demand = IndustrialBaseDemand();
            Check("Carbon leakage stays small: at most 3% of industrial demand, in proportion to the auction burden",
                leakage >= 0f && leakage <= 0.03f && (e.TradingPrice <= 0f) == (leakage == 0f),
                $"leakage {leakage:P2}, industrial base demand {m_TradingDemandBefore:0.0000} -> {demand:0.0000}");
        }
        private System.Threading.Tasks.Task<bool> m_SaveTask;
        private string m_SavedSnapshot;

        /// <summary>Three offers per suit and three hours after a refusal (2026-11-04 user proposal), with every offer refused.</summary>
        private void CheckSettlementLimits()
        {
            LitigationSystem litigation = World.GetOrCreateSystemManaged<LitigationSystem>();
            Entity tax = PolicyCatalog.Find(PolicyCatalog.kPollutionTax);
            LitigationSystem.TestRefuseSettlement = true;
            bool first = litigation.ProposeSettlement(SuitIndex(tax), 0.1f, 0);
            SettlementResult firstResult = litigation.LastSettlementResult;
            litigation.ProposeSettlement(SuitIndex(tax), 0.1f, 0);
            SettlementResult waiting = litigation.LastSettlementResult;
            float waitHours = litigation.SettlementWaitHours(Suit(tax));
            Check("A refused offer is counted and the next one must wait about 3 hours", !first && firstResult == SettlementResult.Refused
                && waiting == SettlementResult.Waiting && waitHours > 2.9f && waitHours <= 3.01f && Suit(tax).m_Offers == 1,
                $"{firstResult}, then {waiting}, wait {waitHours:0.00} h, offers {Suit(tax).m_Offers}");
            LitigationSystem.TestSkipSettlementWait = true;
            litigation.ProposeSettlement(SuitIndex(tax), 0.1f, 0);
            litigation.ProposeSettlement(SuitIndex(tax), 0.1f, 0);
            litigation.ProposeSettlement(SuitIndex(tax), 0.1f, 0);
            Check("After three refused offers no more can be made", litigation.LastSettlementResult == SettlementResult.NoOffersLeft && Suit(tax).m_Offers == 3,
                $"{litigation.LastSettlementResult}, offers {Suit(tax).m_Offers}");
            LitigationSystem.TestSkipSettlementWait = false;
            LitigationSystem.TestRefuseSettlement = false;
        }

        private static SaveGameMetadata FindSave(string name)
        {
            foreach (SaveGameMetadata meta in AssetDatabase.global.GetAssets(default(SearchFilter<SaveGameMetadata>)))
            {
                SaveInfo info = meta.target;
                if (info != null && ((info.path != null && Path.GetFileNameWithoutExtension(info.path) == name) || info.displayName == name))
                {
                    return meta;
                }
            }
            return null;
        }

        /// <summary>Saves the test city under a new name (deleted after the test) and remembers what the mod stored.</summary>
        private void StartSaveRoundTrip()
        {
            m_SavedSnapshot = Snapshot();
            Line($"Mod data before saving: {m_SavedSnapshot}");
            var preview = Game.UI.ScreenCaptureHelper.CreateRenderTarget("RealisticPoliciesSelfTest", 680, 383);
            Game.UI.ScreenCaptureHelper.CaptureScreenshot(Camera.main, preview, new Game.UI.Menu.MenuHelpers.SaveGamePreviewSettings());
            SaveInfo info = World.GetOrCreateSystemManaged<Game.UI.Menu.MenuUISystem>().GetSaveInfo(false);
            m_SaveTask = GameManager.instance.Save(kRoundTripSave, info, AssetDatabase.user, preview);
            SetStage(Stage.SaveGame);
        }

        /// <summary>The mod's saved data in a comparable text: suits, bans, debts, runtime baselines and series designations.</summary>
        private string Snapshot()
        {
            Entity city = m_CitySystem.City;
            var text = new StringBuilder();
            if (EntityManager.TryGetBuffer(city, true, out DynamicBuffer<Lawsuit> suits))
            {
                for (int i = 0; i < suits.Length; i++)
                {
                    Lawsuit s = suits[i];
                    text.Append($"suit {s.m_Year}-{s.m_CaseNumber} r{s.m_Response} o{s.m_Offers} g{s.m_Grounds} m{s.m_ActMonth} p{s.m_Plaintiffs} fee{s.m_LawyerFee:0}; ");
                }
            }
            if (EntityManager.TryGetBuffer(city, true, out DynamicBuffer<PolicyBan> bans))
            {
                for (int i = 0; i < bans.Length; i++)
                {
                    text.Append($"ban {bans[i].m_Year}-{bans[i].m_CaseNumber} partial {bans[i].m_Partial}; ");
                }
            }
            if (EntityManager.TryGetBuffer(city, true, out DynamicBuffer<CourtDebt> debts))
            {
                text.Append($"debts {debts.Length}; ");
            }
            if (EntityManager.TryGetBuffer(city, true, out DynamicBuffer<PolicyRuntime> runtimes))
            {
                int baselines = 0;
                for (int i = 0; i < runtimes.Length; i++)
                {
                    baselines += runtimes[i].m_HasBaseline ? 1 : 0;
                }
                text.Append($"runtimes {runtimes.Length}, baselines {baselines}; ");
            }
            using (EntityQuery welfare = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SocialWelfareDesignation>()))
            using (EntityQuery tax = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TaxDesignation>()))
            {
                text.Append($"welfare {welfare.CalculateEntityCount()}, tax {tax.CalculateEntityCount()}; ");
            }
            if (EntityManager.TryGetComponent(city, out PolicyEngineState state))
            {
                text.Append($"tax staff seen {state.m_TaxStaffSeen}");
            }
            return text.ToString();
        }

        private void CheckRoundTrip()
        {
            string loaded = Snapshot();
            Line($"Mod data after loading: {loaded}");
            Check("Suits, bans, debts, baselines and series choices survive saving and loading", loaded == m_SavedSnapshot, loaded == m_SavedSnapshot ? null : $"before: {m_SavedSnapshot}");
            var reports = World.GetOrCreateSystemManaged<LitigationSystem>();
            Check("Litigation runs after loading", reports.Version > 0, $"version {reports.Version}");
        }

        /// <summary>Average and worst time of each mod system over the whole test (RiverPort: about 300,000 people).</summary>
        private void ReportTiming()
        {
            foreach (System.Collections.Generic.KeyValuePair<string, Perf.Entry> entry in Perf.Entries)
            {
                Perf.Entry e = entry.Value;
                Line($"Timing {entry.Key}: {e.Count} updates, average {e.TotalMs / System.Math.Max(1, e.Count):0.000} ms, worst {e.MaxMs:0.00} ms");
            }
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
            if (m_Stage != Stage.Ui && m_Stage != Stage.WelfareUi && m_Stage != Stage.ReportUi)
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
            LitigationSystem.TestDisableRandomFiling = false;
            LitigationSystem.TestForceInjunctionRequest = false;
            LitigationSystem.TestForcedVerdict = -1;
            LitigationSystem.TestFilingPolicy = Entity.Null;
            LitigationSystem.TestDefaultResponse = null;
            LitigationSystem.TestSkipSettlementWait = false;
            LitigationSystem.TestRefuseSettlement = false;
            ReportTiming();
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
