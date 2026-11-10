using System;
using System.IO;
using System.Linq;
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
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;

namespace RealisticPolicies
{
    /// <summary>
    /// Prototype-only self-test. Runs only when ModsData/RealisticPolicies/selftest.flag exists:
    /// starts a throwaway new city from the main menu, checks every hook the design relies on,
    /// writes report.txt and screenshots next to the flag, then quits the game. Never touches saves.
    /// </summary>
    public sealed partial class SelfTestSystem : GameSystemBase
    {
        private enum Stage { Off, WaitMenu, Loading, Settle, Activate, WaitModifiers, EngineSamples, WaitMigration, BudgetPrepare, BudgetMeasure, WaitLock, Ui, WelfareUi, WelfareCensus, TaxStaff, TaxFloor, TaxGone, SuitRaise, SuitFiled, SuitIcon, SuitInjunction, SuitVerdict, SuitBan, SuitDefault, SuitDefaultVerdict, SuitSettle, SuitAuto, ReportUi, SaveGame, Reloading, ReloadSettle, Trading, TradingOff, Bundle2, Bundle2Off, Details, DetailsUi, LaterBundles, LaterBundlesOff,
            ProtestStart, ProtestIcon, ProtestPanel, ProtestUi, ProtestCrowd, ProtestPartial, ProtestFull, RiotStart, RiotClosed, RiotPolice, RiotShots, RiotSave, RiotReloading, RiotReloadSettle, RiotEnd, ActionStart, ActionRun, ActionUi, ActionEnd, CleanupStart, CleanupRiot, CleanupCheck,
            MoneyWatch, Measure, Finish, Quit }

        private const float kTimeoutSeconds = 3000f;
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
        private bool m_ProtestsOnly;
        private bool m_ChangedOnly;
        private bool m_DetailsOnly;
        private bool m_LivelihoodOn;
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

        /// <summary>A measurement run (";measure:..." in the flag): the engine keeps the game's own cadence and quiet log.</summary>
        public static bool IsMeasureRequested => IsRequested && File.ReadAllText(FlagPath).Contains(";measure:");

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
            int measureAt = request.IndexOf(";measure:", StringComparison.Ordinal);
            if (measureAt >= 0)
            {
                m_MeasureVariant = request.Substring(measureAt + ";measure:".Length).Trim();
                request = request.Substring(0, measureAt);
            }
            // "save:<file name>;changed" runs only what changed lately (2026-11-10 user decision): batches 3-10 with the new
            // policies, then protests, strikes, the riot with its save and reload, and the clean-up. The stages passed many
            // times and not touched since (batches 1-2, the tax and series UI, lawsuits, the first save, trading) are left out.
            // "save:<file name>;details" runs only the detail settings stage (2026-11-11).
            m_DetailsOnly = request.EndsWith(";details", StringComparison.Ordinal);
            if (m_DetailsOnly)
            {
                request = request.Substring(0, request.Length - ";details".Length);
            }
            m_ChangedOnly = request.EndsWith(";changed", StringComparison.Ordinal);
            if (m_ChangedOnly)
            {
                request = request.Substring(0, request.Length - ";changed".Length);
            }
            // "save:<file name>;protests" runs only the protest and riot stages (PLAN §12).
            m_ProtestsOnly = request.EndsWith(";protests", StringComparison.Ordinal);
            if (m_ProtestsOnly)
            {
                request = request.Substring(0, request.Length - ";protests".Length);
            }
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
            else if (m_Stage == Stage.RiotReloading && mode == GameMode.Game)
            {
                Line("City saved during the riot loaded again.");
                LogLoadedRoads();
                World.GetOrCreateSystemManaged<AutoSaveSystem>().Enabled = false;
                m_SimulationSystem.selectedSpeed = kTestSpeed;
                SetStage(Stage.RiotReloadSettle);
            }
        }

        [Preserve]
        protected override void OnUpdate()
        {
            HoldEdgeScrolling();
            if (m_Stage != Stage.Quit && m_Stage != Stage.Off && UnityEngine.Time.realtimeSinceStartup - m_TestStart > (m_MeasureVariant != null ? 4f * 3600f : kTimeoutSeconds))
            {
                Check("Self-test finished before the time limit", false, $"stuck in stage {m_Stage}");
                SetStage(Stage.Finish);
            }

            // Top speed in every stage that runs the city (2026-11-10 user: "배속은 무조건 걸어야 해"; the simulation is slow,
            // and the stages between the policy batches used to run at whatever speed the city had). Only the menu, loading,
            // saving and quitting are left alone.
            bool idle = m_Stage == Stage.Off || m_Stage == Stage.WaitMenu || m_Stage == Stage.Loading || m_Stage == Stage.SaveGame
                || m_Stage == Stage.Reloading || m_Stage == Stage.RiotSave || m_Stage == Stage.RiotReloading || m_Stage == Stage.Finish
                || m_Stage == Stage.Quit;
            if (!idle && m_SimulationSystem.selectedSpeed != kTestSpeed)
            {
                m_SimulationSystem.selectedSpeed = kTestSpeed;
            }

            switch (m_Stage)
            {
                case Stage.WaitMenu:
                    UpdateWaitMenu();
                    break;
                case Stage.Settle:
                    // Wait until the simulation has finished loading; it re-applies "pause after loading" at that point.
                    if (StageSeconds() > 5f && m_SimulationSystem.loadingProgress >= 1f && !GameManager.instance.isGameLoading && m_MeasureVariant != null)
                    {
                        StartMeasure();
                    }
                    else if (StageSeconds() > 5f && m_SimulationSystem.loadingProgress >= 1f && !GameManager.instance.isGameLoading && m_MoneyMode)
                    {
                        StartMoneyWatch();
                    }
                    else if (StageSeconds() > 5f && m_SimulationSystem.loadingProgress >= 1f && !GameManager.instance.isGameLoading && m_DetailsOnly)
                    {
                        m_SimulationSystem.selectedSpeed = kTestSpeed;
                        Line("Details-only run: the detail settings stage.");
                        StartDetails();
                    }
                    else if (StageSeconds() > 5f && m_SimulationSystem.loadingProgress >= 1f && !GameManager.instance.isGameLoading && m_ChangedOnly)
                    {
                        m_SimulationSystem.selectedSpeed = kTestSpeed;
                        Line("Changed-only run: detail settings, batches 3-10, protests, strikes, riot and clean-up.");
                        StartDetails();
                    }
                    else if (StageSeconds() > 5f && m_SimulationSystem.loadingProgress >= 1f && !GameManager.instance.isGameLoading && m_ProtestsOnly)
                    {
                        m_SimulationSystem.selectedSpeed = kTestSpeed;
                        StartProtests();
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
                        // Social welfare staff gone: the floor goes back to 0% (no residential subsidy).
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
                        StartDetails();
                    }
                    break;
                case Stage.Details:
                    UpdateDetails();
                    break;
                case Stage.DetailsUi:
                    if (m_UiDone || StageSeconds() > 90f)
                    {
                        if (!m_UiDone)
                        {
                            Check("Detail settings UI test reported back", false, "no report within 90 s");
                        }
                        // A switched-off policy winds down over days; the batches that follow start these two afresh.
                        DynamicBuffer<PolicyRuntime> runtimes = EntityManager.GetBuffer<PolicyRuntime>(m_CitySystem.City);
                        for (int r = 0; r < runtimes.Length; r++)
                        {
                            if (runtimes[r].m_Policy == PolicyCatalog.Find(PolicyCatalog.kRentSupport) || runtimes[r].m_Policy == PolicyCatalog.Find(PolicyCatalog.kTalentAttraction))
                            {
                                PolicyRuntime runtime = runtimes[r];
                                runtime.m_Progress = 0f;
                                runtimes[r] = runtime;
                            }
                        }
                        if (m_DetailsOnly)
                        {
                            SetStage(Stage.Finish);
                            break;
                        }
                        StartLaterBundles();
                    }
                    break;
                case Stage.LaterBundles:
                    if (!m_LivelihoodOn && SimFrames() >= 1024)
                    {
                        // Switched on after the other welfare policies: it gets only the social workers they leave.
                        m_LivelihoodOn = true;
                        SetPolicy(m_CitySystem.City, PolicyCatalog.Find(PolicyCatalog.kLivelihoodSupport), true, PolicyCatalog.Definition(PolicyCatalog.kLivelihoodSupport).SliderDefault);
                        // Rent support after livelihood support: it gets only the social workers left.
                        SetPolicy(m_CitySystem.City, PolicyCatalog.Find(PolicyCatalog.kRentSupport), true, PolicyCatalog.Definition(PolicyCatalog.kRentSupport).SliderDefault);
                        Line("Livelihood support, then rent support, switched on after the other batches.");
                    }
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
                        StartProtests();
                    }
                    break;
                case Stage.ProtestStart:
                    UpdateProtestStart();
                    break;
                case Stage.ProtestIcon:
                    HoldProtest();
                    if (StageSeconds() >= 4f && SimFrames() >= 512)
                    {
                        CheckProtestNotifications();
                        // Clicking a notification icon selects the park or road it sits on: the protest's section shows there.
                        World.GetOrCreateSystemManaged<Game.UI.InGame.SelectedInfoUISystem>().SetSelection(m_IconOwnerOf);
                        m_UiDone = false;
                        m_UISystem.SetSelfTestStage(5);
                        SetStage(Stage.ProtestPanel);
                    }
                    break;
                case Stage.ProtestPanel:
                    HoldProtest();
                    if (m_UiDone || StageSeconds() > 60f)
                    {
                        if (!m_UiDone)
                        {
                            Check("Building panel UI test reported back", false, "no report within 60 s");
                        }
                        Check("Selecting the park or road under the protest icon shows the protest's section",
                            World.GetOrCreateSystemManaged<ProtestInfoSection>().visible, null);
                        World.GetOrCreateSystemManaged<Game.UI.InGame.SelectedInfoUISystem>().SetSelection(Entity.Null);
                        m_UiDone = false;
                        m_UISystem.SetSelfTestStage(4);
                        SetStage(Stage.ProtestUi);
                    }
                    break;
                case Stage.ProtestUi:
                    HoldProtest();
                    if (m_UiDone || StageSeconds() > 90f)
                    {
                        if (!m_UiDone)
                        {
                            Check("Protest UI test reported back", false, "no report within 90 s");
                        }
                        Follow(m_IconOwnerOf, 180f, 45f);
                        m_ShotStep = 0;
                        SetStage(Stage.ProtestCrowd);
                    }
                    break;
                case Stage.ProtestCrowd:
                    HoldProtest();
                    if (m_ShotStep == 0 && StageSeconds() >= 6f && SimFrames() >= 4096 && CrowdArrived(m_ProtestId))
                    {
                        CheckCrowd("Protesters walk from home to the protest's park or road and stand there", m_ProtestId, "protest-crowd");
                        // Close up on someone standing there, to see the crowd as a player would.
                        Entity person = StandingMarcher(m_ProtestId);
                        Follow(person != Entity.Null ? person : m_IconOwnerOf, 45f, 35f);
                        m_ShotStep = 1;
                        m_StageStart = UnityEngine.Time.realtimeSinceStartup;
                    }
                    else if (m_ShotStep == 1 && StageSeconds() >= 4f)
                    {
                        Shot("protest-crowd-close");
                        // Where a walker stopped short of its spot, close and from higher up (2026-11-10 user: "어디에
                        // 멈췄는지 알면 좋겠는데").
                        if (m_StoppedWalker != Entity.Null && EntityManager.Exists(m_StoppedWalker))
                        {
                            Follow(m_StoppedWalker, 60f, 45f);
                            m_ShotStep = 2;
                            m_StageStart = UnityEngine.Time.realtimeSinceStartup;
                        }
                        else
                        {
                            StartPartialNegotiation();
                        }
                    }
                    else if (m_ShotStep == 2 && StageSeconds() >= 4f)
                    {
                        Shot("protest-crowd-stopped");
                        Follow(m_StoppedWalker, 300f, 60f);
                        m_ShotStep = 3;
                        m_StageStart = UnityEngine.Time.realtimeSinceStartup;
                    }
                    else if (m_ShotStep == 3 && StageSeconds() >= 4f)
                    {
                        Shot("protest-crowd-stopped-wide");
                        StartPartialNegotiation();
                    }
                    break;
                case Stage.ProtestPartial:
                    HoldProtest();
                    if (SimFrames() >= 4096)
                    {
                        CheckPartialNegotiation();
                    }
                    break;
                case Stage.ProtestFull:
                    UpdateProtestFull();
                    break;
                case Stage.RiotStart:
                    UpdateRiotStart();
                    break;
                case Stage.RiotClosed:
                    PinRiot(0.8f);
                    if (SimFrames() >= 4096)
                    {
                        CheckRiotClosed();
                    }
                    break;
                case Stage.RiotPolice:
                    PinRiot(0.8f);
                    UpdateRiotPolice();
                    break;
                case Stage.RiotShots:
                    PinRiot(0.8f);
                    UpdateRiotShots();
                    break;
                case Stage.RiotSave:
                    UpdateProtestSave();
                    break;
                case Stage.RiotReloading:
                    if (StageSeconds() > 300f)
                    {
                        Check("The city saved during the riot loads again", false, "not loaded within 300 s");
                        SetStage(Stage.Finish);
                    }
                    break;
                case Stage.RiotReloadSettle:
                    if (StageSeconds() >= 5f && SimFrames() >= 4096)
                    {
                        CheckProtestRoundTrip();
                        GrantRiot();
                    }
                    break;
                case Stage.RiotEnd:
                    UpdateRiotEnd();
                    break;
                case Stage.ActionStart:
                    UpdateActionStart();
                    break;
                case Stage.ActionRun:
                    UpdateActionRun();
                    break;
                case Stage.ActionUi:
                    if (m_UiDone || StageSeconds() > 60f)
                    {
                        if (!m_UiDone)
                        {
                            Check("Strike card UI test reported back", false, "no report within 60 s");
                        }
                        // The policies were never changed: with the held change released, both end as "demand met".
                        ProtestSystem.TestHeldChange = -1f;
                        Line("Held change released: the strike and the closure should end.");
                        SetStage(Stage.ActionEnd);
                    }
                    break;
                case Stage.ActionEnd:
                    UpdateActionEnd();
                    break;
                case Stage.CleanupStart:
                    UpdateCleanupStart();
                    break;
                case Stage.CleanupRiot:
                    PinRiot(0.8f);
                    UpdateCleanupRiot();
                    break;
                case Stage.CleanupCheck:
                    UpdateCleanupCheck();
                    break;
                case Stage.MoneyWatch:
                    UpdateMoneyWatch();
                    break;
                case Stage.Measure:
                    UpdateMeasure();
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
            float room = m_Engine.Tax.LastRoom;
            Unity.Mathematics.int2 residential = TaxControl.Narrow(original.m_ResidentialTaxLimits, room);
            int expectedFloor = Mathf.Max(residential.x, Mathf.Max(original.m_ResidentialTaxLimits.x,
                Mathf.RoundToInt(TaxControl.kResidentialFloor - (TaxControl.kResidentialFloor - original.m_ResidentialTaxLimits.x) * Mathf.Min(1f, ratio / TaxControl.kWelfareStaffPerResident))));
            float expectedRoom = Mathf.Clamp01((float)census.Workers(ServiceSeries.Tax) / population / TaxControl.kTaxStaffPerResident);
            Check("Tax staff lift the lock; the room around 10% follows tax staff per resident", m_Engine.Tax.LastLock == TaxLock.None && census.Workers(ServiceSeries.Tax) > 0
                && Mathf.Abs(room - expectedRoom) < 1e-4f
                && limits.m_TotalTaxLimits.Equals(TaxControl.Narrow(original.m_TotalTaxLimits, room)) && limits.m_CommercialTaxLimits.Equals(TaxControl.Narrow(original.m_CommercialTaxLimits, room)),
                $"tax staff {census.Workers(ServiceSeries.Tax)}, room {room:0.00} (expected {expectedRoom:0.00}), total [{limits.m_TotalTaxLimits.x}, {limits.m_TotalTaxLimits.y}]");
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
            Check("Without social welfare staff, residential tax cannot go below 0% (no subsidy)", limits.m_ResidentialTaxLimits.x == TaxControl.kResidentialFloor
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
            // The minimum wage also moves the reoffending lever (§14.8); the later stage compares against the original.
            ParameterLevers.All[ParameterLevers.kWelfareRecurrence].Restore(EntityManager);
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
            PolicyCatalog.kDisasterWarning, PolicyCatalog.kEnergyCode,
            PolicyCatalog.kUniversityIndustry, PolicyCatalog.kLivelihoodSupport,
            PolicyCatalog.kRentSupport, PolicyCatalog.kFlexibleHours,
        };

        private Unity.Mathematics.float2 m_ConsumptionBefore;
        private long m_EligibleBefore;
        private long m_DelayedBefore;

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
        private int m_DetailStep;
        private uint m_DetailFrame;
        private Entity m_DetailOffice;
        private float m_DetailPriorSecond;
        private bool m_DetailAgainstCut;
        private uint m_DetailVerdictFrame;

        private const float kDetailRentMonths = 2f;
        private const float kDetailMonthScale = 32f;

        /// <summary>
        /// Detail settings that need time or a court (2026-11-11): rent support on its own with social workers, at most
        /// 2 months a household with the months counted 32 times faster, and talent attraction with its grant months cut
        /// from 6 to 1, sued over the cut and left unanswered so the city loses.
        /// </summary>
        private void StartDetails()
        {
            LitigationSystem.TestDisableRandomFiling = true;
            LitigationSystem.TestTimeScale = 0.05f;
            m_DetailOffice = FindGeneralWelfareOffice(Entity.Null);
            World.GetOrCreateSystemManaged<WelfareSeriesSection>().Set(m_DetailOffice, WelfareSeriesSection.Choice.SocialWelfare);
            RentSupportSystem.TestMonthScale = kDetailMonthScale;
            m_Engine.SetSecond(PolicyCatalog.kRentSupport, kDetailRentMonths);
            SetPolicy(m_CitySystem.City, PolicyCatalog.Find(PolicyCatalog.kRentSupport), true, PolicyCatalog.Definition(PolicyCatalog.kRentSupport).SliderDefault);
            PolicyDefinition talent = PolicyCatalog.Definition(PolicyCatalog.kTalentAttraction);
            m_Engine.SetSecond(PolicyCatalog.kTalentAttraction, talent.Second.Default);
            SetPolicy(m_CitySystem.City, talent.Prefab, true, talent.SliderDefault);
            Line($"Details: welfare office {m_DetailOffice.Index} set to social welfare; rent support on alone, at most {kDetailRentMonths:0} months a household (months x{kDetailMonthScale:0}); talent attraction on with {talent.Second.Default:0} months of grants.");
            m_DetailStep = 0;
            m_DetailVerdictFrame = 0;
            SetStage(Stage.Details);
        }

        private void UpdateDetails()
        {
            Entity city = m_CitySystem.City;
            PolicyDefinition talent = PolicyCatalog.Definition(PolicyCatalog.kTalentAttraction);
            int talentIndex = System.Array.IndexOf(PolicyDefinitions.All, talent);
            RentSupportSystem rent = World.GetOrCreateSystemManaged<RentSupportSystem>();
            uint frame = m_SimulationSystem.frameIndex;
            switch (m_DetailStep)
            {
                case 0:
                    if (SimFrames() >= 8192 && m_Engine.LastIntensity(talent) > 0f)
                    {
                        // As if the grants had run for weeks at 6 months: the cut is then felt in full.
                        DynamicBuffer<PolicyRuntime> runtimes = EntityManager.GetBuffer<PolicyRuntime>(city);
                        for (int r = 0; r < runtimes.Length; r++)
                        {
                            if (runtimes[r].m_Policy == talent.Prefab)
                            {
                                PolicyRuntime runtime = runtimes[r];
                                runtime.m_Reference = m_Engine.LastIntensity(talent);
                                runtime.m_SecondReference = talent.Second.Default;
                                runtimes[r] = runtime;
                            }
                        }
                        m_Engine.SetSecond(PolicyCatalog.kTalentAttraction, 1f);
                        Line("Talent attraction: grant months cut from 6 to 1, the slider left alone.");
                        m_DetailStep = 1;
                        m_DetailFrame = frame;
                    }
                    break;
                case 1:
                    if (m_Engine.GroupGrievance(talentIndex, TargetGroup.OfficeCompanies) > 0f)
                    {
                        LitigationSystem.TestFilingPolicy = talent.Prefab;
                        m_DetailStep = 2;
                        m_DetailFrame = frame;
                    }
                    else if (frame - m_DetailFrame > 24576)
                    {
                        Check("Cutting only the detail setting aggrieves those who had the benefit", false,
                            $"no grievance within 24576 frames (felt {m_Engine.LastResults[talentIndex].Felt:0.000})");
                        FinishDetails(rent, false);
                    }
                    break;
                case 2:
                    if (SuitIndex(talent.Prefab) >= 0)
                    {
                        Lawsuit suit = Suit(talent.Prefab);
                        m_DetailPriorSecond = suit.m_PriorSecond;
                        m_DetailAgainstCut = suit.m_AgainstCut;
                        Check("A suit over a cut detail setting records the setting before and after the cut",
                            suit.m_AgainstCut && Mathf.Approximately(suit.m_ContestedSecond, 1f) && suit.m_PriorSecond > 5f && suit.m_PriorSecond <= 6f,
                            $"against a cut {suit.m_AgainstCut}, {(TargetGroup)suit.m_Group}, months {suit.m_PriorSecond:0.00} -> {suit.m_ContestedSecond:0.00}, slider {suit.m_PriorIntensity:0.000} -> {suit.m_ContestedIntensity:0.000}, {suit.m_DurationFrames} frames");
                        m_DetailStep = 3;
                        m_DetailFrame = frame;
                    }
                    else if (frame - m_DetailFrame > 16384)
                    {
                        Check("A suit is filed over the cut detail setting", false, "none within 16384 frames");
                        FinishDetails(rent, false);
                    }
                    break;
                case 3:
                    if (m_DetailVerdictFrame == 0 && SuitIndex(talent.Prefab) < 0)
                    {
                        m_DetailVerdictFrame = frame;
                    }
                    // The court's changes go through the policy panel's event path and the engine's next step.
                    if (m_DetailVerdictFrame != 0 && frame - m_DetailVerdictFrame >= 2048)
                    {
                        float reference = -1f;
                        foreach (PolicyRuntime runtime in EntityManager.GetBuffer<PolicyRuntime>(city, true))
                        {
                            if (runtime.m_Policy == talent.Prefab)
                            {
                                reference = runtime.m_SecondReference;
                            }
                        }
                        float months = m_Engine.SecondValue(talent);
                        Check("Lost suit over a cut detail setting: the setting is back to what it was, the policy stays on and is locked",
                            m_DetailAgainstCut && Mathf.Approximately(months, 6f) && IsActive(talent.Prefab) && IsLocked(talent.Prefab)
                            && World.GetOrCreateSystemManaged<LitigationSystem>().IsBanned(talent.Prefab) && Mathf.Abs(reference - m_DetailPriorSecond) < 0.3f,
                            $"months {months:0.00} (before the cut {m_DetailPriorSecond:0.00}), reference {reference:0.00}, active {IsActive(talent.Prefab)}, locked {IsLocked(talent.Prefab)}");
                        m_DetailStep = 4;
                        m_DetailFrame = frame;
                    }
                    else if (frame - m_DetailFrame > 60000)
                    {
                        Check("The court rules on the cut detail setting", false, "no verdict within 60000 frames");
                        FinishDetails(rent, false);
                    }
                    break;
                case 4:
                    // Rent support: the first households have had their 2 months and others get it in their place.
                    if ((rent.LastExhausted > 0 && rent.LastSupported > 0) || SimFrames() > 98304)
                    {
                        FinishDetails(rent, true);
                    }
                    break;
            }
        }

        private void FinishDetails(RentSupportSystem rent, bool checkRent)
        {
            if (checkRent)
            {
                int rentIndex = System.Array.IndexOf(PolicyDefinitions.All, PolicyCatalog.Definition(PolicyCatalog.kRentSupport));
                float rentStaff = m_Engine.PolicyStaff(rentIndex, ServiceSeries.SocialWelfare) * m_Engine.PolicyStaffShare(rentIndex, ServiceSeries.SocialWelfare);
                // A household stops once it has had the months; the last payment may run a little over.
                Check($"Rent support pays no household for longer than the months set in details ({kDetailRentMonths:0}): those who have had them drop out and others take their place",
                    rentStaff > 0.05f && rent.LastExhausted > 0 && rent.LastSupported > 0 && rent.SupportedPastLimit() == 0
                    && rent.LastMaxMonths >= kDetailRentMonths && rent.LastMaxMonths <= kDetailRentMonths + 0.1f,
                    $"staff got {rentStaff:0.0}, poor renters left {rent.LastRenters}, supported now {rent.LastSupported}, have had their months {rent.LastExhausted}, " +
                    $"supported past the limit {rent.SupportedPastLimit()}, most months had {rent.LastMaxMonths:0.00}, monthly {rent.LastMonthly}");
            }
            RentSupportSystem.TestMonthScale = 1f;
            LitigationSystem.TestFilingPolicy = Entity.Null;
            LitigationSystem.TestTimeScale = 1f;
            SetPolicy(m_CitySystem.City, PolicyCatalog.Find(PolicyCatalog.kRentSupport), false, 0f);
            SetPolicy(m_CitySystem.City, PolicyCatalog.Find(PolicyCatalog.kTalentAttraction), false, 0f);
            m_Engine.SetSecond(PolicyCatalog.kRentSupport, PolicyCatalog.Definition(PolicyCatalog.kRentSupport).Second.Default);
            m_Engine.SetSecond(PolicyCatalog.kTalentAttraction, PolicyCatalog.Definition(PolicyCatalog.kTalentAttraction).Second.Default);
            World.GetOrCreateSystemManaged<WelfareSeriesSection>().Set(m_DetailOffice, WelfareSeriesSection.Choice.GeneralAdministration);
            // The detail settings as the player sees them (policies off, defaults).
            m_UiDone = false;
            m_UISystem.SetSelfTestStage(7);
            SetStage(Stage.DetailsUi);
        }

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
            m_EligibleBefore = ExtendedBenefitSearchSystem.EligibleSeen;
            m_DelayedBefore = ExtendedBenefitSearchSystem.Delayed;
            m_LivelihoodOn = false;
            foreach (string id in kLaterBundles)
            {
                // Both take social workers per case: switched on later, after the other welfare policies.
                if (id == PolicyCatalog.kLivelihoodSupport || id == PolicyCatalog.kRentSupport)
                {
                    continue;
                }
                PolicyDefinition definition = PolicyCatalog.Definition(id);
                float value = definition.IsSlider ? (definition.SliderDefault > 0f ? definition.SliderDefault : 0.5f * definition.SliderRange.max) : 0f;
                SetPolicy(m_CitySystem.City, PolicyCatalog.Find(id), true, value);
            }
            Line($"Batches 3-10 on: {kLaterBundles.Length} policies at their default strength (random suits off).");
            // Detail settings (2026-11-10): changed from their defaults, checked with the later results.
            // Building codes reach only buildings built or levelled up since (option 1): within a test few are, so the
            // checks of their effects run at full coverage, and the count is checked on its own.
            PolicyEngineSystem.TestCodeCoverage = 1f;
            m_Engine.SetSecond(PolicyCatalog.kScholarships, 600f);
            m_Engine.SetSecond(PolicyCatalog.kFlexibleHours, 60f);
            Line("Details: scholarships ¢600 a student, flexible hours up to 60 min.");
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

            // §14.21: the poorest households first, as many as the social welfare staff reach, booked as one budget row.
            LivelihoodSupportSystem livelihood = World.GetOrCreateSystemManaged<LivelihoodSupportSystem>();
            int livelihoodRow = m_Injection.GetRow(PolicyDefinitions.kLivelihoodRow);
            int benefit = Singleton<EconomyParameterData>().m_UnemploymentBenefit;
            float livelihoodIntensity = m_Engine.LastIntensity(PolicyCatalog.Definition(PolicyCatalog.kLivelihoodSupport));
            int livelihoodIndex = System.Array.IndexOf(PolicyDefinitions.All, PolicyCatalog.Definition(PolicyCatalog.kLivelihoodSupport));
            float livelihoodNeed = m_Engine.PolicyStaff(livelihoodIndex, ServiceSeries.SocialWelfare);
            float livelihoodShare = m_Engine.PolicyStaffShare(livelihoodIndex, ServiceSeries.SocialWelfare);
            float livelihoodStaff = livelihoodNeed * livelihoodShare;
            Check("Livelihood support pays the lowest-income households (the slider's amount each), at most 60 per social worker it got, as one budget row",
                livelihood.LastPoor > 0 && livelihood.LastSupported <= livelihood.LastPoor
                && livelihood.LastSupported <= Mathf.CeilToInt(livelihoodStaff * LivelihoodSupportSystem.kHouseholdsPerWorker) + 1
                && livelihood.LastMonthlyEach == Mathf.RoundToInt(livelihoodIntensity * PolicyCatalog.Definition(PolicyCatalog.kLivelihoodSupport).SliderRange.max) && livelihoodRow == livelihood.LastSupported * livelihood.LastMonthlyEach,
                $"poor {livelihood.LastPoor}, supported {livelihood.LastSupported}, staff got {livelihoodStaff:0.0} of {livelihoodNeed:0.0} needed, each {livelihood.LastMonthlyEach} (slider {livelihoodIntensity:0.00} of 1,600; benefit {benefit}), row {livelihoodRow}, social welfare staff {m_Engine.Census.Workers(ServiceSeries.SocialWelfare)}");
            // The other welfare policies came first, so they keep their staff: none gets a smaller share than livelihood support.
            var earlier = new StringBuilder();
            bool keep = true;
            foreach (string id in new[] { PolicyCatalog.kWelfareOutreach, PolicyCatalog.kHousingFirst, PolicyCatalog.kReentrySupport, PolicyCatalog.kVictimSupport })
            {
                int index = System.Array.IndexOf(PolicyDefinitions.All, PolicyCatalog.Definition(id));
                float share = m_Engine.PolicyStaffShare(index, ServiceSeries.SocialWelfare);
                keep &= share >= livelihoodShare - 1e-5f;
                earlier.Append($"{Short(id)} {share:P0} of {m_Engine.PolicyStaff(index, ServiceSeries.SocialWelfare):0.0}; ");
            }
            Check("Welfare policies switched on before livelihood support keep their social workers", keep, earlier + $"livelihood {livelihoodShare:P1}");
            PolicyDefinition livelihoodDefinition = PolicyCatalog.Definition(PolicyCatalog.kLivelihoodSupport);
            float baseLag = m_Engine.Reforms.BaseLag(livelihoodDefinition);
            float lag = m_Engine.LastResults[livelihoodIndex].LagDays;
            Check("Households livelihood support cannot take do not slow it down (only the work ahead of it counts)",
                lag <= 2f * baseLag + 1e-3f, $"lag {lag:0.00} days, base {baseLag:0.00}");
            CheckSecondSettings(livelihood, livelihoodNeed);
            CheckCodeCoverage();
            CheckNewItems();
            CheckStaffOrder();
            int universityIndex = System.Array.IndexOf(PolicyDefinitions.All, PolicyCatalog.Definition(PolicyCatalog.kUniversityIndustry));
            Line($"University-industry support: delivery {m_Engine.LastResults[universityIndex].Progress:0.000}, software offices {AirModifierOf(PolicyCatalog.Find(PolicyCatalog.kUniversityIndustry), CityModifierType.OfficeSoftwareEfficiency):0.0000}.");

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
            Check("Free high school raises the teen chance (at most +20%); lifelong learning leaves the adult and worker chances to the game",
                education.m_EnterHighSchoolProbability > m_EducationParamsBefore.m_EnterHighSchoolProbability
                && education.m_EnterHighSchoolProbability <= 1.2f * m_EducationParamsBefore.m_EnterHighSchoolProbability + 1e-4f
                && education.m_AdultEnterHighSchoolProbability == m_EducationParamsBefore.m_AdultEnterHighSchoolProbability
                && education.m_WorkerContinueEducationProbability == m_EducationParamsBefore.m_WorkerContinueEducationProbability,
                $"teen {m_EducationParamsBefore.m_EnterHighSchoolProbability} -> {education.m_EnterHighSchoolProbability}, adult {m_EducationParamsBefore.m_AdultEnterHighSchoolProbability} -> {education.m_AdultEnterHighSchoolProbability}, worker {m_EducationParamsBefore.m_WorkerContinueEducationProbability} -> {education.m_WorkerContinueEducationProbability}");
            Unity.Mathematics.float2 consumption = ZonedConsumption();
            Check("Energy code lowers the electricity of zoned building prefabs (the waste fee left the city policies)",
                consumption.x < m_ConsumptionBefore.x,
                $"electricity {m_ConsumptionBefore.x:0} -> {consumption.x:0}, garbage {m_ConsumptionBefore.y:0} -> {consumption.y:0}");
            long eligible = ExtendedBenefitSearchSystem.EligibleSeen - m_EligibleBefore;
            long delayed = ExtendedBenefitSearchSystem.Delayed - m_DelayedBefore;
            if (eligible >= 50)
            {
                float share = delayed / (float)eligible;
                // 10% in a city at the reference unemployment (6.2%), fewer where jobs are scarce (Kroft & Notowidigdo 2016).
                float factor = m_Engine.Conditions != null ? m_Engine.Conditions.SearchDelayFactor : 1f;
                float expected = ExtendedBenefitSearchSystem.kDelayChance * factor;
                Check("Longer benefits delay the job search of 10% x the unemployment factor of those paid in the extension per run",
                    share > 0.5f * expected && share < 1.5f * expected,
                    $"{delayed} of {eligible} checks ({share:P1}), expected {expected:P1} (unemployment {(m_Engine.Conditions != null ? m_Engine.Conditions.Unemployment : 0f):P1}, factor {factor:0.00})");
            }
            else
            {
                Line($"Extended-benefit search: only {eligible} checks of citizens paid in the extension ({delayed} delayed); too few to judge.");
            }
            int warning = System.Array.IndexOf(PolicyDefinitions.All, PolicyCatalog.Definition(PolicyCatalog.kDisasterWarning));
            float seconds = AirModifierOf(PolicyCatalog.Find(PolicyCatalog.kDisasterWarning), CityModifierType.DisasterWarningTime);
            float expectedSeconds = 180f * m_Engine.LastResults[warning].Progress * m_Engine.Reforms.PostCapacity;
            Check("Early warning adds seconds = 180 x delivery x post and telecom capacity",
                seconds > 0f && Mathf.Abs(seconds - expectedSeconds) < 0.05f * expectedSeconds + 0.01f,
                $"+{seconds:0.00} s, expected {expectedSeconds:0.00} (telecom {m_Engine.Reforms.PostCapacity:0.00})");
            int housingFirst = System.Array.IndexOf(PolicyDefinitions.All, PolicyCatalog.Definition(PolicyCatalog.kHousingFirst));
            float amplification = m_Engine.Amplification(housingFirst);
            Check("Welfare outreach amplifies Housing First (§14.8, at most x1.2)", amplification > 1f && amplification <= 1.2f + 1e-4f, $"x{amplification:0.0000}");
            int contracting = System.Array.IndexOf(PolicyDefinitions.All, PolicyCatalog.Definition(PolicyCatalog.kContractingOut));
            int bidding = System.Array.IndexOf(PolicyDefinitions.All, PolicyCatalog.Definition(PolicyCatalog.kCompetitiveBidding));
            float expectedBoost = 1f + 0.5f * Mathf.Clamp01(m_Engine.LastResults[bidding].Progress);
            float hospital = AirModifierOf(PolicyCatalog.Find(PolicyCatalog.kContractingOut), CityModifierType.HospitalEfficiency);
            float upkeep = AirModifierOf(PolicyCatalog.Find(PolicyCatalog.kContractingOut), CityModifierType.CityServiceBuildingBaseUpkeepCost);
            Check("Competitive bidding enlarges only the contracting-out savings (§14.8)",
                Mathf.Abs(m_Engine.Amplification(contracting) - expectedBoost) < 0.02f && m_Engine.LeverAmplification(contracting) == 1f
                && hospital != 0f && Mathf.Abs(upkeep / hospital - (0.05f / 0.03f) * m_Engine.Amplification(contracting)) < 0.05f,
                $"x{m_Engine.Amplification(contracting):0.000} (expected {expectedBoost:0.000}), upkeep {upkeep:0.0000}, hospital {hospital:0.0000}");
            int reentry = System.Array.IndexOf(PolicyDefinitions.All, PolicyCatalog.Definition(PolicyCatalog.kReentrySupport));
            Check("Harsher sentencing weakens reentry support (§14.8, at most -20%)",
                m_Engine.Amplification(reentry) < 1f && m_Engine.Amplification(reentry) >= 0.8f - 1e-4f, $"x{m_Engine.Amplification(reentry):0.000}");
            int response = System.Array.IndexOf(PolicyDefinitions.All, PolicyCatalog.Definition(PolicyCatalog.kEmergencyResponse));
            float responseValue = AirModifierOf(PolicyCatalog.Find(PolicyCatalog.kEmergencyResponse), CityModifierType.CrimeResponseTime);
            float expectedResponse = -0.3f * m_Engine.LastResults[response].Progress * m_Engine.LegitimacyFactor;
            Check("Emergency response follows legitimacy (§14.8, 0.5-1.2 times)",
                Mathf.Abs(responseValue - expectedResponse) < 0.05f * Mathf.Abs(expectedResponse) + 1e-4f,
                $"{responseValue:0.0000}, expected {expectedResponse:0.0000} (legitimacy x{m_Engine.LegitimacyFactor:0.00})");
            float inflation = m_Engine.Reforms.ReportInflation;
            Check("Disclosure cuts the gaming of performance targets (§14.8)",
                Mathf.Abs(inflation - ReformEffects.kPerformanceReportInflation * m_Engine.Reforms.Performance * (1f - 0.5f * m_Engine.Reforms.Disclosure)) < 1e-5f && m_Engine.Reforms.Disclosure > 0f,
                $"+{inflation:0.0000}, performance {m_Engine.Reforms.Performance:0.000}, disclosure {m_Engine.Reforms.Disclosure:0.000}");
            Check("Performance management inflates the other policies' reported delivery (at most 10 points)", inflation > 0f && inflation <= 0.1f + 1e-4f, $"+{inflation:0.0000}");
            Line($"Legitimacy {m_Engine.LastState.m_Legitimacy:0.000} with community policing (+0.03) and zero tolerance (-0.04) on.");
            LogDisasterWarning("with batches on");
        }

        /// <summary>In every series, staff went in enactment order: full shares first, at most one partial, then none.</summary>
        private void CheckStaffOrder()
        {
            var wrong = new StringBuilder();
            int[] order = m_Engine.StaffOrder;
            for (int j = 0; j < (int)ServiceSeries.Count; j++)
            {
                var series = (ServiceSeries)j;
                float last = 1f;
                int partial = 0;
                foreach (int i in order)
                {
                    if (m_Engine.PolicyStaff(i, series) <= 0f)
                    {
                        continue;
                    }
                    float share = m_Engine.PolicyStaffShare(i, series);
                    if (share > last + 1e-5f)
                    {
                        wrong.Append($"{series}: {Short(PolicyDefinitions.All[i].Id)} {share:0.00} after {last:0.00}; ");
                    }
                    partial += share > 1e-5f && share < 1f - 1e-5f ? 1 : 0;
                    last = share;
                }
                if (partial > 1)
                {
                    wrong.Append($"{series}: {partial} partly staffed; ");
                }
            }
            Check("In every series staff go in the order the policies were switched on", wrong.Length == 0, wrong.Length == 0 ? null : wrong.ToString());
        }

        private void StopLaterBundles()
        {
            // As if the batches had run for weeks: switching them off is then a real cut (the protest check after it).
            Protests.TestSettleReferences();
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
            LivelihoodSupportSystem livelihood = World.GetOrCreateSystemManaged<LivelihoodSupportSystem>();
            Check("Livelihood support stops paying when switched off", m_Injection.GetRow(PolicyDefinitions.kLivelihoodRow) == 0 && livelihood.LastSupported == 0,
                $"row {m_Injection.GetRow(PolicyDefinitions.kLivelihoodRow)}, supported {livelihood.LastSupported}");
            RentSupportSystem rent = World.GetOrCreateSystemManaged<RentSupportSystem>();
            BuildingEffectSystem effects = World.GetOrCreateSystemManaged<BuildingEffectSystem>();
            FlexibleHoursSystem flexible = World.GetOrCreateSystemManaged<FlexibleHoursSystem>();
            int serviceLeft = effects.CountServiceMismatch();
            // A switched-off policy winds down (its delivery falls over days), so the service factor follows what is left of it.
            float performanceLeft = m_Engine.Delivered(PolicyCatalog.kPerformanceManagement);
            Check("Rent support stops paying, flexible hours stop holding trips and city services follow performance management as it winds down",
                m_Injection.GetRow(PolicyDefinitions.kRentSupportRow) == 0 && rent.LastSupported == 0 && flexible.LastShare == 0f
                && Mathf.Abs(effects.LastServiceFactor - (1f + 0.03f * performanceLeft)) < 1e-3f && serviceLeft == 0,
                $"rent row {m_Injection.GetRow(PolicyDefinitions.kRentSupportRow)}, supported {rent.LastSupported}, flexible share {flexible.LastShare:0.000}, service factor {effects.LastServiceFactor:0.0000} (performance management left {performanceLeft:0.000}), buildings off {serviceLeft}");
            LitigationSystem.TestDisableRandomFiling = false;
            World.GetOrCreateSystemManaged<WelfareSeriesSection>().Set(m_WelfareOffice, WelfareSeriesSection.Choice.GeneralAdministration);
            World.GetOrCreateSystemManaged<WelfareSeriesSection>().Set(m_TaxOffice, WelfareSeriesSection.Choice.GeneralAdministration);
        }

        /// <summary>The 2026-11-09 items: rent support, flexible hours, lifelong learning seekers, building efficiency effects.</summary>
        private void CheckNewItems()
        {
            RentSupportSystem rent = World.GetOrCreateSystemManaged<RentSupportSystem>();
            int rentRow = m_Injection.GetRow(PolicyDefinitions.kRentSupportRow);
            // Switched on after livelihood support, it gets only the social workers left (none in a city with few of them):
            // as many households as the staff it got reach, never more, and the row is what is paid.
            int rentIndex = System.Array.IndexOf(PolicyDefinitions.All, PolicyCatalog.Definition(PolicyCatalog.kRentSupport));
            float rentStaff = m_Engine.PolicyStaff(rentIndex, ServiceSeries.SocialWelfare) * m_Engine.PolicyStaffShare(rentIndex, ServiceSeries.SocialWelfare);
            bool reached = rentStaff < 0.05f || rent.LastSupported > 0;
            Check("Rent support gives poor renting households part of their rent back, poorest first, at most 100 per social worker it got, booked as what is paid",
                rent.LastRenters > 0 && reached && rent.LastSupported <= Mathf.CeilToInt(rentStaff * RentSupportSystem.kHouseholdsPerWorker) + 1
                && rentRow == rent.LastMonthly,
                $"poor renters {rent.LastRenters}, supported {rent.LastSupported}, staff got {rentStaff:0.0}, monthly {rent.LastMonthly}, row {rentRow}");
            FlexibleHoursSystem flexible = World.GetOrCreateSystemManaged<FlexibleHoursSystem>();
            int workers = 0, taking = 0;
            var firms = new System.Collections.Generic.HashSet<Entity>();
            var adopting = new System.Collections.Generic.HashSet<Entity>();
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Citizens.Worker>(), ComponentType.ReadOnly<Game.Citizens.Citizen>()))
            using (Unity.Collections.NativeArray<Game.Citizens.Citizen> citizens = query.ToComponentDataArray<Game.Citizens.Citizen>(Unity.Collections.Allocator.Temp))
            using (Unity.Collections.NativeArray<Game.Citizens.Worker> jobs = query.ToComponentDataArray<Game.Citizens.Worker>(Unity.Collections.Allocator.Temp))
            {
                for (int i = 0; i < citizens.Length; i++)
                {
                    workers++;
                    firms.Add(jobs[i].m_Workplace);
                    bool part = FlexibleHoursSystem.Participates(citizens[i], jobs[i], flexible.LastShare, flexible.LastMaxDelayDays, out float delay);
                    if (part)
                    {
                        adopting.Add(jobs[i].m_Workplace);
                    }
                    if (part && delay <= flexible.LastMaxDelayDays)
                    {
                        taking++;
                    }
                }
            }
            float shareTaking = workers > 0 ? taking / (float)workers : 0f;
            float firmShare = firms.Count > 0 ? adopting.Count / (float)firms.Count : 0f;
            Check("Flexible hours support: the share of workplaces adopting it follows the delivered slider (each workplace fixed, delay up to 90 min)",
                flexible.LastShare > 0f && firms.Count > 0 && Mathf.Abs(firmShare - flexible.LastShare) < 0.05f,
                $"share {flexible.LastShare:0.000}, {adopting.Count} of {firms.Count} workplaces ({firmShare:0.000}), {taking} of {workers} workers ({shareTaking:0.000}), going-to-work trips held at the last update {flexible.LastHeld}, time of day {World.GetOrCreateSystemManaged<TimeSystem>().normalizedTime * 24f:0.0} h");

            LifelongLearningSystem lifelong = World.GetOrCreateSystemManaged<LifelongLearningSystem>();
            Line($"Lifelong learning: {lifelong.TotalStarted} school searches started for unemployed adults since loading (delivery {m_Engine.Delivered(PolicyCatalog.kLifelongLearning):0.000}).");

            BuildingEffectSystem effects = World.GetOrCreateSystemManaged<BuildingEffectSystem>();
            int mismatch = effects.CountServiceMismatch();
            Check("Performance management raises city service buildings' service budget factor (1 + 0.03 x delivered), on each building once",
                effects.LastServiceFactor > 1f && effects.LastServiceFactor <= 1.03f + 1e-5f && mismatch == 0,
                $"factor {effects.LastServiceFactor:0.0000}, buildings off the expected value {mismatch}, tracked {effects.ServiceTrackedCount}");
            Entity rescue = PolicyCatalog.Find(PolicyCatalog.kCorporateRescue);
            Check("Corporate rescue no longer lowers industry and offices city-wide; the drag is per industry",
                AirModifierOf(rescue, Game.City.CityModifierType.IndustrialEfficiency) == 0f && AirModifierOf(rescue, Game.City.CityModifierType.OfficeEfficiency) == 0f
                && effects.LastWorstIndustryFactor >= 0.5f - 1e-5f && effects.LastWorstIndustryFactor <= 1f,
                $"zombie firms {effects.LastZombies}, worst industry factor {effects.LastWorstIndustryFactor:0.000}, delivery {m_Engine.Delivered(PolicyCatalog.kCorporateRescue):0.000}");
        }

        private static bool Same(float a, float b) => Mathf.Abs(a - b) <= 1e-6f * Mathf.Max(1f, Mathf.Abs(b));

        // ------------------------------------------------------------------ protests and riots (PLAN §12)

        private const string kProtestSave = "RP SelfTest Protest";
        private readonly System.Collections.Generic.HashSet<int> m_KnownProtests = new System.Collections.Generic.HashSet<int>();
        private readonly System.Collections.Generic.List<Entity> m_ClosedLanesSeen = new System.Collections.Generic.List<Entity>();
        private readonly System.Collections.Generic.List<Entity> m_PostEntities = new System.Collections.Generic.List<Entity>();
        private int m_ProtestId;
        private int m_RiotId;
        private bool m_PostsChecked;
        private int m_ShotStep;
        private uint m_EndSeenAt;
        private Entity m_HeldCar;
        private string m_ProtestSnapshot;

        private ProtestSystem Protests => World.GetOrCreateSystemManaged<ProtestSystem>();

        private DynamicBuffer<Protest> ProtestBuffer(bool readOnly = true) => EntityManager.GetBuffer<Protest>(m_CitySystem.City, readOnly);

        private int ProtestIndex(int id)
        {
            if (!EntityManager.HasBuffer<Protest>(m_CitySystem.City))
            {
                return -1;
            }
            DynamicBuffer<Protest> protests = ProtestBuffer();
            for (int i = 0; i < protests.Length; i++)
            {
                if (protests[i].m_Id == id)
                {
                    return i;
                }
            }
            return -1;
        }

        private Protest GetProtest(int id) => ProtestBuffer()[ProtestIndex(id)];

        private void SetProtest(Protest protest)
        {
            int index = ProtestIndex(protest.m_Id);
            if (index >= 0)
            {
                DynamicBuffer<Protest> protests = ProtestBuffer(false);
                protests[index] = protest;
            }
        }

        /// <summary>A protest that started during this stage, about <paramref name="policy"/> (Entity.Null: the residential tax).</summary>
        private int NewProtest(Entity policy)
        {
            if (!EntityManager.HasBuffer<Protest>(m_CitySystem.City))
            {
                return 0;
            }
            DynamicBuffer<Protest> protests = ProtestBuffer();
            for (int i = 0; i < protests.Length; i++)
            {
                if (!m_KnownProtests.Contains(protests[i].m_Id) && protests[i].m_Policy == policy)
                {
                    return protests[i].m_Id;
                }
            }
            return 0;
        }

        private void RememberProtests()
        {
            m_KnownProtests.Clear();
            if (EntityManager.HasBuffer<Protest>(m_CitySystem.City))
            {
                DynamicBuffer<Protest> protests = ProtestBuffer();
                for (int i = 0; i < protests.Length; i++)
                {
                    m_KnownProtests.Add(protests[i].m_Id);
                }
            }
        }

        private bool HasIcon(Entity building, Entity icon)
        {
            if (building == Entity.Null || icon == Entity.Null || !EntityManager.TryGetBuffer(building, true, out DynamicBuffer<Game.Notifications.IconElement> icons))
            {
                return false;
            }
            for (int i = 0; i < icons.Length; i++)
            {
                if (EntityManager.TryGetComponent(icons[i].m_Icon, out PrefabRef prefab) && prefab.m_Prefab == icon)
                {
                    return true;
                }
            }
            return false;
        }

        private int CountChirps(Entity chirpPrefab)
        {
            int chirps = 0;
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Triggers.Chirp>(), ComponentType.ReadOnly<PrefabRef>()))
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    chirps += EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab == chirpPrefab ? 1 : 0;
                }
            }
            return chirps;
        }

        private void Follow(Entity entity, float zoom, float pitch = 0f)
        {
            Game.Rendering.CameraUpdateSystem cameras = World.GetOrCreateSystemManaged<Game.Rendering.CameraUpdateSystem>();
            if (entity != Entity.Null && cameras.orbitCameraController != null)
            {
                cameras.orbitCameraController.followedEntity = entity;
                cameras.orbitCameraController.zoom = zoom;
                if (pitch > 0f)
                {
                    // Looking down on the roads, so their colours show in the screenshot.
                    cameras.orbitCameraController.rotation = new Vector3(pitch, cameras.orbitCameraController.rotation.y, 0f);
                }
                cameras.activeCameraController = cameras.orbitCameraController;
            }
        }

        private void Shot(string name)
        {
            string file = Path.Combine(m_Directory, "selftest", name + ".png");
            ScreenCapture.CaptureScreenshot(file);
            Line($"Screenshot {file}");
        }

        /// <summary>
        /// Household wealth and income across the city, to set the livelihood support wealth limit's default (2026-11-10
        /// user: change the default; Korea pays livelihood benefit below 32% of the median income, and 4.9% of people get
        /// basic livelihood security, 7.5% in the poorest province).
        /// </summary>
        private void LogHouseholdDistribution()
        {
            using (EntityQuery query = EntityManager.CreateEntityQuery(ServiceSeriesCensus.PoorHouseholdQueryDesc))
            using (NativeArray<Entity> households = query.ToEntityArray(Allocator.Temp))
            {
                int n = households.Length;
                if (n == 0)
                {
                    return;
                }
                var wealth = new int[n];
                var income = new int[n];
                for (int i = 0; i < n; i++)
                {
                    Game.Citizens.Household household = EntityManager.GetComponentData<Game.Citizens.Household>(households[i]);
                    wealth[i] = Game.Economy.EconomyUtils.GetHouseholdTotalWealth(household, EntityManager.GetBuffer<Game.Economy.Resources>(households[i], true));
                    income[i] = household.m_Income;
                }
                System.Array.Sort(wealth);
                System.Array.Sort(income);
                string Quantiles(int[] sorted) =>
                    string.Join(", ", new[] { 0.03f, 0.05f, 0.075f, 0.1f, 0.25f, 0.5f, 0.75f, 0.9f }.Select(q => $"{q:P1} {sorted[Mathf.Min(n - 1, (int)(q * n))]}"));
                float Below(int[] sorted, float line) => sorted.Count(v => v < line) / (float)n;
                int medianWealth = wealth[n / 2];
                int medianIncome = income[n / 2];
                int4? lines = ServiceSeriesCensus.WealthLines(EntityManager);
                Line($"Households {n}. Wealth: {Quantiles(wealth)}; game lines {lines}. Income a month: {Quantiles(income)}.");
                Line($"Below 32% of the median income ({0.32f * medianIncome:0}): {Below(income, 0.32f * medianIncome):P1}; below 32% of the median wealth ({0.32f * medianWealth:0}): {Below(wealth, 0.32f * medianWealth):P1}; "
                    + $"below the game's modest line ({(lines.HasValue ? lines.Value.y : 0)}): {Below(wealth, lines.HasValue ? lines.Value.y : 0):P1}; below its poor line ({(lines.HasValue ? lines.Value.x : 0)}): {Below(wealth, lines.HasValue ? lines.Value.x : 0):P1}.");
            }
        }

        private void StartProtests()
        {
            LogHouseholdDistribution();
            LitigationSystem.TestDisableRandomFiling = true;
            RememberProtests();
            Line($"Protests before the stage: {m_KnownProtests.Count}; pressure {Protests.LastPressure:0.000}% of the affected households' income, {Protests.LastHourlyRate:0.000} new protests an hour.");
            // Batches 3-10 were just switched off: their benefits were cut, so people would come out within hours (the
            // test keeps chance starts off so they do not get in the way).
            // The protests-only run skips the batches, so there is no cut to judge (2026-11-10).
            if (m_ProtestsOnly)
            {
                Line("Benefit-cut pressure not checked: the protests-only run has no batches switched off.");
            }
            else
            {
                Check("Cutting benefits makes people ready to protest within hours (pressure against the affected households' income)",
                    Protests.LastPressure > 1f && Protests.LastHourlyRate > 0f,
                    $"pressure {Protests.LastPressure:0.00}%, {Protests.LastHourlyRate:0.000} an hour (mean wait {(Protests.LastHourlyRate > 0f ? 1f / Protests.LastHourlyRate : 0f):0.0} h)");
            }
            ProtestSystem.TestProtestPolicy = PolicyCatalog.Find(PolicyCatalog.kScholarships);
            ProtestSystem.TestStartProtest = true;
            // In a park over a subway station when the city has one (2026-11-10 user decision: the crowd stands on its
            // ground-level paths).
            ProtestSystem.TestStationPark = true;
            Line("Forcing a protest over the scholarships, in a station park if there is one.");
            SetStage(Stage.ProtestStart);
        }

        private void UpdateProtestStart()
        {
            int id = NewProtest(PolicyCatalog.Find(PolicyCatalog.kScholarships));
            if (id > 0)
            {
                m_ProtestId = id;
                Protest protest = GetProtest(id);
                Check("A forced protest starts over the scholarships on a lived-in road segment, with a character and a representative",
                    EntityManager.Exists(protest.m_StartEdge) && EntityManager.Exists(protest.m_Representative) && protest.m_Residents >= 1 && protest.m_Trait <= 3 && protest.m_Size > 0f,
                    $"edge {protest.m_StartEdge.Index}, residents {protest.m_Residents}, trait {(ProtestTrait)protest.m_Trait}, size {protest.m_Size:0.000}, intensity {protest.m_Intensity:0.00}, demand {protest.m_DemandIntensity:0.00} from {protest.m_StartIntensity:0.00}");
                if (ProtestSystem.TestStationParkUsed != Entity.Null)
                {
                    // Chosen by the test, anywhere in the city: what matters is that the crowd can stand there (checked below).
                    Check("The forced protest gathers in a park with something underground (station park, chosen by the test)",
                        protest.m_Place == ProtestSystem.TestStationParkUsed, $"park {protest.m_Place.Index}");
                }
                else
                {
                    CheckPlace("It gathers in a park with its own lot beside a road within 256 m of where the aggrieved live (Huff), or on their road when none is in reach", protest, false);
                }
                Follow(protest.m_IconOwner, 300f);
                SetStage(Stage.ProtestIcon);
            }
            else if (SimFrames() > 8192)
            {
                Check("A forced protest starts over the scholarships", false, "none within 8192 frames");
                SetStage(Stage.Finish);
            }
        }

        private void CheckProtestNotifications()
        {
            Protest protest = GetProtest(m_ProtestId);
            Check("The protest shows the megaphone icon over its park (or its road when there is none)", HasIcon(protest.m_IconOwner, ProtestPrefabs.ProtestIcon),
                $"{(protest.m_Place != Entity.Null ? "park" : "road")} {protest.m_IconOwner.Index}");
            Check("A protester posts the protest chirp", CountChirps(ProtestPrefabs.ProtestChirp) > 0, $"{CountChirps(ProtestPrefabs.ProtestChirp)} chirps");
            Shot("protest-icon");
            m_IconOwnerOf = protest.m_IconOwner;
            // For the negotiation checks: the demand is to bring the scholarships back from where they are now to 60%.
            protest.m_StartIntensity = m_Engine.LastIntensity(PolicyCatalog.Definition(PolicyCatalog.kScholarships));
            protest.m_DemandIntensity = 0.6f;
            SetProtest(protest);
            Line($"Demand set to bring the scholarships from {protest.m_StartIntensity:0.00} to 0.60.");
        }

        /// <summary>
        /// Where the protest gathers (§12, 2026-11-02 and 2026-11-09 user decisions): a park within 256 m of the residents'
        /// road, the protest starting on the park's road with the icon on the park; with none in reach (or none allowed by
        /// the test), the residents' own road, with the icon on that road.
        /// </summary>
        private void CheckPlace(string name, Protest protest, bool noParkForced)
        {
            Unity.Mathematics.float2 from = EntityManager.TryGetComponent(protest.m_HomeEdge, out Game.Net.Curve curve) ? Colossal.Mathematics.MathUtils.Position(curve.m_Bezier, 0.5f).xz : default;
            // Parks in reach: those that count (own lot beside a road) and those left out (made to stand on a road).
            int inReach = 0;
            var onRoad = new System.Collections.Generic.List<string>();
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Buildings.Park>(), ComponentType.ReadOnly<Game.Buildings.Building>(),
                ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Game.Tools.Temp>()))
            using (NativeArray<Entity> parks = query.ToEntityArray(Allocator.Temp))
            {
                PrefabSystem prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
                foreach (Entity park in parks)
                {
                    if (EntityManager.GetComponentData<Game.Buildings.Building>(park).m_RoadEdge == Entity.Null
                        || Unity.Mathematics.math.distance(from, EntityManager.GetComponentData<Game.Objects.Transform>(park).m_Position.xz) > 256f)
                    {
                        continue;
                    }
                    if (ProtestSystem.IsRoadsidePark(EntityManager, park, out _))
                    {
                        inReach++;
                    }
                    else
                    {
                        onRoad.Add(prefabs.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(park).m_Prefab));
                    }
                }
            }
            Line($"Parks within 256 m: {inReach} beside a road, {onRoad.Count} left out as standing on a road ({string.Join(", ", onRoad)}).");
            if (protest.m_Place != Entity.Null)
            {
                float d = Unity.Mathematics.math.distance(from, EntityManager.GetComponentData<Game.Objects.Transform>(protest.m_Place).m_Position.xz);
                Unity.Mathematics.int2 lot = EntityManager.TryGetComponent(EntityManager.GetComponentData<PrefabRef>(protest.m_Place).m_Prefab, out BuildingData data) ? data.m_LotSize : default;
                string parkName = World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefabName(EntityManager.GetComponentData<PrefabRef>(protest.m_Place).m_Prefab);
                Check(name, !noParkForced && EntityManager.HasComponent<Game.Buildings.Park>(protest.m_Place) && ProtestSystem.IsRoadsidePark(EntityManager, protest.m_Place, out _) && d <= 256f
                    && protest.m_StartEdge == EntityManager.GetComponentData<Game.Buildings.Building>(protest.m_Place).m_RoadEdge && protest.m_IconOwner == protest.m_Place,
                    $"park {protest.m_Place.Index} '{parkName}' ({lot.x}x{lot.y} cells) {d:0} m from the residents' road, {inReach} parks in reach");
            }
            else
            {
                Check(name, (noParkForced || inReach == 0) && protest.m_StartEdge == protest.m_HomeEdge && protest.m_IconOwner == protest.m_StartEdge,
                    $"residents' road {protest.m_StartEdge.Index}, {inReach} parks in reach{(noParkForced ? " (parks left out by the test)" : "")}");
            }
        }

        /// <summary>People heading to (and standing at) the gathering places of one protest (its park and spots), or at any spot when null.</summary>
        private (int heading, int standing) CountCrowd(int? protestId)
        {
            var spots = new System.Collections.Generic.HashSet<Entity>();
            if (EntityManager.TryGetBuffer(m_CitySystem.City, true, out DynamicBuffer<ProtestSpot> buffer))
            {
                for (int i = 0; i < buffer.Length; i++)
                {
                    if (protestId == null || buffer[i].m_Protest == protestId.Value)
                    {
                        spots.Add(buffer[i].m_Spot);
                    }
                }
            }
            if (protestId != null && ProtestIndex(protestId.Value) >= 0 && GetProtest(protestId.Value).m_Place != Entity.Null)
            {
                spots.Add(GetProtest(protestId.Value).m_Place);
            }
            int heading = 0;
            int standing = 0;
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Creatures.Resident>(), ComponentType.ReadOnly<Target>(), ComponentType.Exclude<Deleted>()))
            using (NativeArray<Entity> people = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity person in people)
                {
                    Entity target = EntityManager.GetComponentData<Target>(person).m_Target;
                    if (target == Entity.Null || (protestId != null && !spots.Contains(target)) || (protestId == null && !EntityManager.HasComponent<Game.Objects.SpawnLocation>(target)))
                    {
                        continue;
                    }
                    if (protestId == null && (!EntityManager.TryGetComponent(target, out PrefabRef prefab) || prefab.m_Prefab != ProtestPrefabs.Spot))
                    {
                        continue;
                    }
                    heading++;
                    if (IsStanding(person))
                    {
                        standing++;
                    }
                }
            }
            return (heading, standing);
        }

        private uint m_NextArrivalCheck;

        /// <summary>
        /// Someone stands at the place yet (in the park when there is one), or long enough has passed: from a park up to
        /// 256 m away the walk takes about 12,000 frames (the first run checked at 4,096 frames with all 70 still on the way;
        /// the second stopped waiting when the nearer sidewalks filled, with the park-goers 40-50 m short). Looked at every
        /// 1,024 frames.
        /// </summary>
        private bool CrowdArrived(int protestId)
        {
            uint frames = SimFrames();
            // Up to about 4 game hours: a park across water or a highway can be a 400 m+ walk with transit on the way
            // (2026-11-10 run: all 68 still walking after 20480 frames, a third of the way along 100-step paths).
            if (frames >= 40960)
            {
                return true;
            }
            if (frames < m_NextArrivalCheck && m_NextArrivalCheck - frames <= 1024)
            {
                return false;
            }
            m_NextArrivalCheck = frames + 1024;
            Entity park = ProtestIndex(protestId) >= 0 ? GetProtest(protestId).m_Place : Entity.Null;
            return park != Entity.Null ? StandingAt(protestId, park) > 0 : CountCrowd(protestId).standing > 0;
        }

        /// <summary>Marchers of this protest sent to this place who stand there now.</summary>
        private int StandingAt(int protestId, Entity place)
        {
            int count = 0;
            // A park over a station: the crowd stands on its ground-level paths, at spots listed with the park as their edge.
            var placeSpots = new System.Collections.Generic.HashSet<Entity> { place };
            DynamicBuffer<ProtestSpot> spots = EntityManager.GetBuffer<ProtestSpot>(m_CitySystem.City, true);
            for (int i = 0; i < spots.Length; i++)
            {
                if (spots[i].m_Protest == protestId && spots[i].m_Edge == place)
                {
                    placeSpots.Add(spots[i].m_Spot);
                }
            }
            DynamicBuffer<ProtestMarcher> sent = EntityManager.GetBuffer<ProtestMarcher>(m_CitySystem.City, true);
            for (int i = 0; i < sent.Length; i++)
            {
                if (sent[i].m_Protest == protestId && placeSpots.Contains(sent[i].m_Spot)
                    && EntityManager.TryGetComponent(sent[i].m_Citizen, out Game.Citizens.CurrentTransport transport) && IsStanding(transport.m_CurrentTransport))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>A person staying at the place: the game marks the lane they mill about on ("hang around").</summary>
        private bool IsStanding(Entity person)
        {
            return EntityManager.TryGetComponent(person, out Game.Creatures.HumanCurrentLane lane) && (lane.m_Flags & Game.Creatures.CreatureLaneFlags.Hangaround) != 0;
        }

        /// <summary>Someone of this protest milling about at a spot, for a close-up.</summary>
        private Entity StandingMarcher(int protestId)
        {
            DynamicBuffer<ProtestMarcher> sent = EntityManager.GetBuffer<ProtestMarcher>(m_CitySystem.City, true);
            for (int i = 0; i < sent.Length; i++)
            {
                if (sent[i].m_Protest == protestId && EntityManager.TryGetComponent(sent[i].m_Citizen, out Game.Citizens.CurrentTransport transport) && IsStanding(transport.m_CurrentTransport))
                {
                    return transport.m_CurrentTransport;
                }
            }
            return Entity.Null;
        }

        /// <summary>What a lane belongs to: the owner chain's prefab names (a building's pedestrian area, a road, ...).</summary>
        private string LaneOwner(Entity lane)
        {
            var names = new StringBuilder();
            Entity at = lane;
            for (int depth = 0; depth < 4 && at != Entity.Null; depth++)
            {
                string name = EntityManager.TryGetComponent(at, out PrefabRef prefab) && m_PrefabSystem.TryGetPrefab(prefab.m_Prefab, out PrefabBase prefabBase) ? prefabBase.name : "?";
                names.Append(depth == 0 ? "" : " < ").Append($"{name} {at.Index}");
                at = EntityManager.TryGetComponent(at, out Owner owner) ? owner.m_Owner : Entity.Null;
            }
            return names.ToString();
        }

        /// <summary>A walker that stopped short of its spot at the last crowd check (for a picture of where).</summary>
        private Entity m_StoppedWalker;

        /// <param name="parkCheck">Check that someone stands in the park: only right after they arrive, as a small park
        /// (16 people in one cell) can be empty a moment later while its stays end and the next walkers come.</param>
        private void CheckCrowd(string name, int protestId, string shot, bool parkCheck = true)
        {
            int index = ProtestIndex(protestId);
            int participants = index >= 0 ? GetProtest(protestId).m_Participants : 0;
            int spots = 0;
            DynamicBuffer<ProtestSpot> buffer = EntityManager.GetBuffer<ProtestSpot>(m_CitySystem.City, true);
            for (int i = 0; i < buffer.Length; i++)
            {
                spots += buffer[i].m_Protest == protestId ? 1 : 0;
            }
            int marchers = 0;
            DynamicBuffer<ProtestMarcher> sent = EntityManager.GetBuffer<ProtestMarcher>(m_CitySystem.City, true);
            for (int i = 0; i < sent.Length; i++)
            {
                marchers += sent[i].m_Protest == protestId ? 1 : 0;
            }
            (int heading, int standing) = CountCrowd(protestId);
            Protest of = index >= 0 ? GetProtest(protestId) : default;
            // In the park: those standing there, or standing within its lot (a station park's crowd stands on spots on its
            // ground-level paths, not in the park building: 2026-11-10); from home: marchers living on the road the
            // aggrieved live on.
            int inPark = 0;
            int fromHome = 0;
            Unity.Mathematics.float3 parkCentre = default;
            float lotReach = 0f;
            if (of.m_Place != Entity.Null && EntityManager.TryGetComponent(of.m_Place, out Game.Objects.Transform parkAt)
                && EntityManager.TryGetComponent(of.m_Place, out PrefabRef parkPrefab) && EntityManager.TryGetComponent(parkPrefab.m_Prefab, out BuildingData parkLot))
            {
                parkCentre = parkAt.m_Position;
                // Half the lot's diagonal, in metres (8 m cells). A crowd sent to the sidewalks in front of the park (its paths
                // could not be reached, option ㄱ) stands just beyond the lot: the road's width more.
                lotReach = 0.5f * 8f * Unity.Mathematics.math.length(new Unity.Mathematics.float2(parkLot.m_LotSize))
                    + (Protests.GathersInFrontOfPark(protestId) ? 40f : 0f);
            }
            m_StoppedWalker = Entity.Null;
            for (int i = 0; i < sent.Length; i++)
            {
                if (sent[i].m_Protest != protestId)
                {
                    continue;
                }
                if (of.m_Place != Entity.Null && EntityManager.TryGetComponent(sent[i].m_Citizen, out Game.Citizens.CurrentTransport walkerInPark)
                    && IsStanding(walkerInPark.m_CurrentTransport)
                    && (sent[i].m_Spot == of.m_Place || (lotReach > 0f && EntityManager.TryGetComponent(walkerInPark.m_CurrentTransport, out Game.Objects.Transform standingAt)
                        && Unity.Mathematics.math.distance(standingAt.m_Position.xz, parkCentre.xz) <= lotReach)))
                {
                    inPark++;
                }
                if (EntityManager.TryGetComponent(sent[i].m_Citizen, out Game.Citizens.HouseholdMember member) && EntityManager.TryGetComponent(member.m_Household, out Game.Buildings.PropertyRenter renter)
                    && EntityManager.TryGetComponent(renter.m_Property, out Game.Buildings.Building home) && home.m_RoadEdge == of.m_HomeEdge)
                {
                    fromHome++;
                }
            }
            Line($"Protest {protestId}: {inPark} standing in its park, {fromHome} of those sent live on the road the aggrieved live on.");
            // Where the people sent are: trip not started, waiting for a path, walking, standing, gone into the spot.
            int pending = 0, waiting = 0, walking = 0, still = 0, inside = 0, other = 0;
            for (int i = 0; i < sent.Length; i++)
            {
                if (sent[i].m_Protest != protestId)
                {
                    continue;
                }
                Entity citizen = sent[i].m_Citizen;
                Entity spot = sent[i].m_Spot;
                bool tripPending = false;
                if (EntityManager.TryGetBuffer(citizen, true, out DynamicBuffer<Game.Citizens.TripNeeded> trips))
                {
                    for (int k = 0; k < trips.Length; k++)
                    {
                        tripPending |= trips[k].m_TargetAgent == spot;
                    }
                }
                if (EntityManager.TryGetComponent(citizen, out Game.Citizens.CurrentBuilding current) && current.m_CurrentBuilding == spot) inside++;
                else if (EntityManager.TryGetComponent(citizen, out Game.Citizens.CurrentTransport transport) && EntityManager.TryGetComponent(transport.m_CurrentTransport, out Game.Creatures.Resident resident))
                {
                    if (IsStanding(transport.m_CurrentTransport)) still++;
                    else walking++;
                }
                else if (tripPending) pending++;
                else if (EntityManager.TryGetComponent(citizen, out Target target) && target.m_Target == spot) waiting++;
                else other++;
            }
            // A few of those walking: what their path looks like and how far they are from their spot.
            int samples = 0;
            for (int i = 0; i < sent.Length && samples < 5; i++)
            {
                if (sent[i].m_Protest != protestId || !EntityManager.TryGetComponent(sent[i].m_Citizen, out Game.Citizens.CurrentTransport walker)
                    || !EntityManager.TryGetComponent(walker.m_CurrentTransport, out Game.Creatures.Resident walkerData) || (walkerData.m_Flags & Game.Creatures.ResidentFlags.Hangaround) != 0)
                {
                    continue;
                }
                samples++;
                Entity creature = walker.m_CurrentTransport;
                Entity spot = sent[i].m_Spot;
                string divert = EntityManager.TryGetComponent(creature, out Game.Creatures.Divert d) ? d.m_Purpose.ToString() : "-";
                string path = "-";
                if (EntityManager.TryGetComponent(creature, out Game.Pathfind.PathOwner owner) && EntityManager.TryGetBuffer(creature, true, out DynamicBuffer<Game.Pathfind.PathElement> elements))
                {
                    Entity last = elements.Length > 0 ? elements[elements.Length - 1].m_Target : Entity.Null;
                    string lastKind = last == spot ? "spot" : last == Entity.Null ? "none" : EntityManager.HasComponent<Game.Net.PedestrianLane>(last) ? "sidewalk" : "other";
                    string flags = elements.Length > 0 ? elements[elements.Length - 1].m_Flags.ToString() : "-";
                    path = $"{owner.m_State} at {owner.m_ElementIndex}/{elements.Length}, last {lastKind} ({flags})";
                }
                string lane = "-";
                float distance = -1f;
                if (EntityManager.TryGetComponent(creature, out Game.Creatures.HumanCurrentLane current))
                {
                    Entity spotLane = EntityManager.TryGetComponent(spot, out Game.Objects.SpawnLocation location) ? location.m_ConnectedLane1 : Entity.Null;
                    lane = $"{(current.m_Lane == spotLane ? "spot lane" : current.m_Lane == spot ? "spot" : "other")} {current.m_CurvePosition.x:0.00}->{current.m_CurvePosition.y:0.00} {current.m_Flags}";
                }
                string where = "";
                if (EntityManager.TryGetComponent(creature, out Game.Objects.Transform here))
                {
                    if (EntityManager.TryGetComponent(spot, out Game.Objects.Transform there))
                    {
                        distance = Unity.Mathematics.math.distance(here.m_Position, there.m_Position);
                    }
                    if (lotReach > 0f)
                    {
                        float fromCentre = Unity.Mathematics.math.distance(here.m_Position.xz, parkCentre.xz);
                        where = $", {fromCentre:0} m from the park's centre (its lot reaches {lotReach:0} m), height {here.m_Position.y - parkCentre.y:0.0} m against the park";
                    }
                    if (distance > 30f && current.m_Flags.ToString().Contains("EndReached"))
                    {
                        // The citizen, not the walking body, which the game may replace before the picture.
                        if (m_StoppedWalker == Entity.Null)
                        {
                            m_StoppedWalker = sent[i].m_Citizen;
                        }
                        where += $", at ({here.m_Position.x:0}, {here.m_Position.y:0}, {here.m_Position.z:0}) on {LaneOwner(current.m_Lane)}";
                    }
                }
                Line($"  walker {creature.Index}: flags {walkerData.m_Flags}, divert {divert}, path {path}, lane {lane}, {distance:0} m from its spot{where}");
            }
            Line($"Crowd of protest {protestId}: {pending} trip not started, {waiting} waiting for a path, {walking} walking, {still} standing, {inside} gone into the spot, {other} elsewhere; "
                + $"last update moved on {Protests.LastMovedOn}, left {Protests.LastLeft}, sent {Protests.LastSent}.");
            Check(name, (spots > 0 || of.m_Place != Entity.Null) && marchers > 0 && standing > 0,
                $"{participants} participants, {(of.m_Place != Entity.Null ? "a park and " : "")}{spots} spots, {marchers} sent, {heading} on the way or there, {standing} standing");
            if (of.m_Place != Entity.Null && parkCheck)
            {
                Check("Protesters stand in the park (or in front of it when its paths cannot be reached), and they come from the road the aggrieved live on", inPark > 0 && fromHome > 0,
                    $"{inPark} standing in the park{(Protests.GathersInFrontOfPark(protestId) ? " or in front of it (its paths could not be reached)" : "")}, {fromHome} from the residents' road");
            }
            if (shot != null)
            {
                Shot(shot);
            }
        }

        private void StartPartialNegotiation()
        {
            Protest protest = GetProtest(m_ProtestId);
            Check("[UI] Clicking Negotiate in the protests tab sets the response", protest.m_Response == (byte)ProtestResponse.Negotiate, $"response {(ProtestResponse)protest.m_Response}");
            Protests.Negotiate(m_ProtestId, 0.3f, 0);
            Line("Negotiation: scholarships to 30% (half the demand), no compensation.");
            SetStage(Stage.ProtestPartial);
        }

        private void CheckPartialNegotiation()
        {
            CheckCrowd("Protesters are still out on the street a while later", m_ProtestId, null, false);
            int index = ProtestIndex(m_ProtestId);
            float now = m_Engine.LastIntensity(PolicyCatalog.Definition(PolicyCatalog.kScholarships));
            Protest protest = index >= 0 ? GetProtest(m_ProtestId) : default;
            Check("Meeting half the demand sets the policy through the policy panel's path and keeps the protest going",
                IsActive(PolicyCatalog.Find(PolicyCatalog.kScholarships)) && Mathf.Abs(now - 0.3f) < 0.01f && index >= 0 && !protest.m_Granted && protest.m_EverConceded
                && protest.m_Response == (byte)ProtestResponse.Negotiate,
                $"scholarships {now:0.00}, protest {(index >= 0 ? "on" : "gone")}, granted {protest.m_Granted}, conceded {protest.m_EverConceded}, size {protest.m_Size:0.000}");
            Protests.Negotiate(m_ProtestId, 0.6f, 0);
            Line("Negotiation: scholarships to 60% (the full demand).");
            m_EndSeenAt = 0;
            SetStage(Stage.ProtestFull);
        }

        private void UpdateProtestFull()
        {
            if (ProtestIndex(m_ProtestId) >= 0)
            {
                if (SimFrames() > 16384)
                {
                    Protest protest = GetProtest(m_ProtestId);
                    Check("Meeting the full demand ends a protest", false, $"still on: granted {protest.m_Granted}, riot {protest.m_Riot}, scholarships {m_Engine.LastIntensity(PolicyCatalog.Definition(PolicyCatalog.kScholarships)):0.00}");
                    StartActionTest();
                }
                return;
            }
            if (m_EndSeenAt == 0)
            {
                m_EndSeenAt = SimFrames();
                return;
            }
            if (SimFrames() - m_EndSeenAt < 1024)
            {
                return;
            }
            Check("Meeting the full demand ends the protest and takes its icon away", !HasIcon(m_IconOwnerOf, ProtestPrefabs.ProtestIcon), $"ended within {m_EndSeenAt} frames");
            SetPolicy(m_CitySystem.City, PolicyCatalog.Find(PolicyCatalog.kScholarships), false, 0f);
            // Strikes and closures before the riot: their protests-tab check runs before the riot's save and reload.
            StartActionTest();
        }

        private Entity m_IconOwnerOf;

        private void StartRiot()
        {
            RememberProtests();
            ProtestSystem.TestProtestTax = true;
            // This one starts on the residents' road, as when no park is near: its icon is on the road.
            ProtestSystem.TestNoPark = true;
            ProtestSystem.TestStartProtest = true;
            Line("Forcing a protest over the residential tax, to be turned into a riot.");
            SetStage(Stage.RiotStart);
        }

        private void UpdateRiotStart()
        {
            int id = NewProtest(Entity.Null);
            if (id > 0)
            {
                m_RiotId = id;
                PinRiot(0.8f);
                Protest protest = GetProtest(id);
                Line($"Tax protest {id} on edge {protest.m_StartEdge.Index} ({protest.m_Residents} residents) pushed to intensity 0.8; legitimacy {m_Engine.LastState.m_Legitimacy:0.000}.");
                CheckPlace("Without a park in reach it starts on the residents' road, with the icon on that road", protest, true);
                // Clicking the icon on a road selects the road: the protest's section shows in the road's panel.
                World.GetOrCreateSystemManaged<Game.UI.InGame.SelectedInfoUISystem>().SetSelection(protest.m_IconOwner);
                Follow(protest.m_IconOwner, 400f);
                SetStage(Stage.RiotClosed);
            }
            else if (SimFrames() > 8192)
            {
                Check("A forced protest starts over the residential tax", false, "none within 8192 frames");
                SetStage(Stage.Finish);
            }
        }

        /// <summary>
        /// Keeps the first protest going and below a riot while its negotiation is checked (a forced protest with little
        /// real pressure behind it would otherwise fade out, and an extreme one turn into a riot).
        /// </summary>
        private void HoldProtest()
        {
            if (ProtestIndex(m_ProtestId) < 0)
            {
                return;
            }
            Protest protest = GetProtest(m_ProtestId);
            if (protest.m_Size < 0.1f || protest.m_Intensity > 0.4f)
            {
                protest.m_Size = Mathf.Max(protest.m_Size, 0.1f);
                protest.m_Intensity = Mathf.Min(protest.m_Intensity, 0.4f);
                SetProtest(protest);
            }
        }

        /// <summary>Holds the riot at least at <paramref name="intensity"/> and a third of the residents, so the stages see a riot.</summary>
        private void PinRiot(float intensity)
        {
            if (ProtestIndex(m_RiotId) < 0)
            {
                return;
            }
            Protest protest = GetProtest(m_RiotId);
            if (protest.m_Intensity < intensity || protest.m_Size < 0.3f)
            {
                protest.m_Intensity = Mathf.Max(protest.m_Intensity, intensity);
                protest.m_Size = Mathf.Max(protest.m_Size, 0.3f);
                SetProtest(protest);
            }
        }

        private void CheckRiotClosed()
        {
            Protest protest = GetProtest(m_RiotId);
            Check("Selecting the road under the riot icon shows the protest's section in the road's panel",
                World.GetOrCreateSystemManaged<ProtestInfoSection>().visible && World.GetOrCreateSystemManaged<Game.UI.InGame.SelectedInfoUISystem>().selectedEntity == protest.m_IconOwner,
                $"road {protest.m_IconOwner.Index}");
            Shot("riot-road-panel");
            World.GetOrCreateSystemManaged<Game.UI.InGame.SelectedInfoUISystem>().SetSelection(Entity.Null);
            Check("At intensity 0.6 or more it is a riot, with the riot chirp and the flame icon",
                protest.m_Riot && HasIcon(protest.m_IconOwner, ProtestPrefabs.RiotIcon) && CountChirps(ProtestPrefabs.RiotChirp) > 0,
                $"riot {protest.m_Riot}, icon {HasIcon(protest.m_IconOwner, ProtestPrefabs.RiotIcon)}, chirps {CountChirps(ProtestPrefabs.RiotChirp)}");
            DynamicBuffer<ClosedLane> closed = EntityManager.GetBuffer<ClosedLane>(m_CitySystem.City, true);
            int flagged = 0;
            for (int i = 0; i < closed.Length; i++)
            {
                if (EntityManager.TryGetComponent(closed[i].m_Lane, out Game.Net.CarLane lane) && (lane.m_Flags & Game.Net.CarLaneFlags.PublicOnly) != 0)
                {
                    flagged++;
                }
            }
            Check("A riot closes its roads to private cars: public-only lanes, recorded to give back",
                closed.Length > 0 && flagged == closed.Length && Protests.LastClosedEdges.Contains(protest.m_StartEdge),
                $"{closed.Length} lanes recorded, {flagged} public-only, {Protests.LastClosedEdges.Count} closed segments, {Protests.LastClosedNodes.Count} closed nodes");

            float maxCrime = Singleton<PoliceConfigurationData>().m_MaxCrimeAccumulation;
            int buildings = 0;
            int raised = 0;
            if (EntityManager.TryGetBuffer(protest.m_StartEdge, true, out DynamicBuffer<Game.Buildings.ConnectedBuilding> connected))
            {
                for (int b = 0; b < connected.Length; b++)
                {
                    if (EntityManager.TryGetComponent(connected[b].m_Building, out Game.Buildings.CrimeProducer crime))
                    {
                        buildings++;
                        raised += crime.m_Crime >= 0.5f * maxCrime ? 1 : 0;
                    }
                }
            }
            Check("Buildings along a riot hold crime at the riot's intensity", buildings > 0 && raised > 0, $"{raised} of {buildings} buildings at half the maximum or more (max {maxCrime:0})");
            Protests.Respond(m_RiotId, ProtestResponse.Disperse);
            Line("Response: repression by forced dispersal.");
            m_PostsChecked = false;
            SetStage(Stage.RiotPolice);
        }

        private int PostCount(int protestId)
        {
            DynamicBuffer<PolicePost> posts = EntityManager.GetBuffer<PolicePost>(m_CitySystem.City, true);
            int count = 0;
            for (int i = 0; i < posts.Length; i++)
            {
                count += posts[i].m_Protest == protestId ? 1 : 0;
            }
            return count;
        }

        /// <summary>A police car standing at one of the riot's posts, or Entity.Null.</summary>
        private Entity StandingCar()
        {
            var sites = new System.Collections.Generic.HashSet<Entity>();
            DynamicBuffer<PolicePost> posts = EntityManager.GetBuffer<PolicePost>(m_CitySystem.City, true);
            for (int i = 0; i < posts.Length; i++)
            {
                sites.Add(posts[i].m_Site);
            }
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Vehicles.PoliceCar>(), ComponentType.ReadOnly<ServiceDispatch>()))
            using (NativeArray<Entity> cars = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity car in cars)
                {
                    Game.Vehicles.PoliceCar data = EntityManager.GetComponentData<Game.Vehicles.PoliceCar>(car);
                    DynamicBuffer<ServiceDispatch> dispatches = EntityManager.GetBuffer<ServiceDispatch>(car, true);
                    if ((data.m_State & Game.Vehicles.PoliceCarFlags.AtTarget) != 0 && dispatches.Length > 0
                        && EntityManager.TryGetComponent(dispatches[0].m_Request, out PoliceEmergencyRequest request) && sites.Contains(request.m_Site))
                    {
                        return car;
                    }
                }
            }
            return Entity.Null;
        }

        private void UpdateRiotPolice()
        {
            if (ProtestIndex(m_RiotId) < 0)
            {
                Check("The riot lasts while it is repressed", false, "it ended");
                SetStage(Stage.Finish);
                return;
            }
            Protest protest = GetProtest(m_RiotId);
            if (!m_PostsChecked && SimFrames() >= 4096)
            {
                m_PostsChecked = true;
                DynamicBuffer<PolicePost> posts = EntityManager.GetBuffer<PolicePost>(m_CitySystem.City, true);
                bool sites = posts.Length > 0;
                for (int i = 0; i < posts.Length; i++)
                {
                    sites &= EntityManager.HasComponent<Game.Events.AccidentSite>(posts[i].m_Site) && EntityManager.Exists(posts[i].m_Point);
                }
                Check("Forced dispersal sets up police posts that ask the game for police cars, one after another",
                    PostCount(m_RiotId) >= 1 && sites && protest.m_CarsNeeded >= 1,
                    $"{PostCount(m_RiotId)} posts for {protest.m_CarsNeeded} cars needed ({protest.m_Participants} people), {Protests.LastPoliceEdges.Count} police segments, {Protests.LastClosedNodes.Count} closed nodes");
            }
            if (m_PostsChecked && protest.m_CarsArrived >= 1)
            {
                m_HeldCar = StandingCar();
                Game.Vehicles.PoliceCar car = m_HeldCar != Entity.Null ? EntityManager.GetComponentData<Game.Vehicles.PoliceCar>(m_HeldCar) : default;
                Check("A police car reaches its post and stands there, taken off other calls",
                    m_HeldCar != Entity.Null && car.m_PurposeMask == 0 && Protests.HeldCarCount >= 1,
                    $"{protest.m_CarsArrived}/{protest.m_CarsNeeded} in place after {SimFrames()} frames, purpose {car.m_PurposeMask}, held {Protests.HeldCarCount}, repression {protest.m_Repression:0.00}");
                m_ShotStep = 0;
                SetStage(Stage.RiotShots);
            }
            else if (SimFrames() > 60000)
            {
                Check("A police car reaches a riot post", false, $"needed {protest.m_CarsNeeded}, posts {PostCount(m_RiotId)}, arrived {protest.m_CarsArrived}");
                m_ShotStep = 0;
                SetStage(Stage.RiotShots);
            }
        }

        /// <summary>Screenshots of the riot as it is and in the protest infoview, with the road colour check.</summary>
        private void UpdateRiotShots()
        {
            Game.Tools.ToolSystem tools = World.GetOrCreateSystemManaged<Game.Tools.ToolSystem>();
            if (m_ShotStep == 0)
            {
                Follow(GetProtest(m_RiotId).m_IconOwner, 500f, 60f);
                m_ShotStep = 1;
                m_StageStart = UnityEngine.Time.realtimeSinceStartup;
            }
            else if (m_ShotStep == 1 && StageSeconds() >= 4f && CrowdArrived(m_RiotId))
            {
                // Waits until someone stands there: on a long road the walk to the spots can take a while (one run checked
                // with all 383 still 29-99 m away, some posting a letter on the way).
                CheckCrowd("Rioters fill the sidewalks of the riot roads", m_RiotId, null);
                Shot("riot-police");
                Entity person = StandingMarcher(m_RiotId);
                Follow(person != Entity.Null ? person : GetProtest(m_RiotId).m_IconOwner, 45f, 35f);
                m_ShotStep = 2;
                m_StageStart = UnityEngine.Time.realtimeSinceStartup;
            }
            else if (m_ShotStep == 2 && StageSeconds() >= 4f)
            {
                Shot("riot-crowd-close");
                Follow(GetProtest(m_RiotId).m_IconOwner, 500f, 60f);
                tools.infoview = ProtestInfoview.Prefab;
                m_ShotStep = 3;
                m_StageStart = UnityEngine.Time.realtimeSinceStartup;
            }
            else if (m_ShotStep == 3 && StageSeconds() >= 4f)
            {
                Protest protest = GetProtest(m_RiotId);
                int riot = ProtestInfoview.Index(EntityManager, m_PrefabSystem, ProtestInfoview.Riot);
                int police = ProtestInfoview.Index(EntityManager, m_PrefabSystem, ProtestInfoview.Police);
                Game.Net.EdgeColor color = EntityManager.TryGetComponent(protest.m_StartEdge, out Game.Net.EdgeColor c) ? c : default;
                int policeOk = 0;
                foreach (Entity edge in Protests.LastPoliceEdges.Keys)
                {
                    policeOk += EntityManager.TryGetComponent(edge, out Game.Net.EdgeColor e) && e.m_Index == police ? 1 : 0;
                }
                Check("The protest infoview colours riot roads and police roads in their legend colours",
                    ProtestInfoview.IsActive(tools) && riot >= 0 && color.m_Index == riot && policeOk == Protests.LastPoliceEdges.Count,
                    $"infoview {ProtestInfoview.IsActive(tools)}, riot index {riot}, start edge {color.m_Index}, police roads {policeOk}/{Protests.LastPoliceEdges.Count} (index {police})");
                Shot("riot-infoview");
                tools.infoview = null;
                StartProtestSave();
            }
        }

        private string ProtestSnapshot()
        {
            Protest protest = GetProtest(m_RiotId);
            int pressures = EntityManager.GetBuffer<ProtestPressure>(m_CitySystem.City, true).Length;
            return $"protest {protest.m_Id} response {(ProtestResponse)protest.m_Response} trait {(ProtestTrait)protest.m_Trait} demand {protest.m_DemandIntensity:0.00} repressed {protest.m_EverRepressed}; pressures {pressures}";
        }

        private void StartProtestSave()
        {
            m_CrimesBeforeSave = Protests.RiotCrimes;
            LogCrimeScenes(GetProtest(m_RiotId));
            m_ProtestSnapshot = ProtestSnapshot();
            Line($"Protest data before saving: {m_ProtestSnapshot}; closed lanes {EntityManager.GetBuffer<ClosedLane>(m_CitySystem.City, true).Length}, posts {PostCount(m_RiotId)}.");
            var preview = Game.UI.ScreenCaptureHelper.CreateRenderTarget("RealisticPoliciesSelfTest", 680, 383);
            Game.UI.ScreenCaptureHelper.CaptureScreenshot(Camera.main, preview, new Game.UI.Menu.MenuHelpers.SaveGamePreviewSettings());
            SaveInfo info = World.GetOrCreateSystemManaged<Game.UI.Menu.MenuUISystem>().GetSaveInfo(false);
            m_SaveTask = GameManager.instance.Save(kProtestSave, info, AssetDatabase.user, preview);
            SetStage(Stage.RiotSave);
        }

        private void UpdateProtestSave()
        {
            if (m_SaveTask != null && m_SaveTask.IsCompleted)
            {
                bool saved = !m_SaveTask.IsFaulted && m_SaveTask.Result && FindSave(kProtestSave) != null;
                Check("The city saves during a riot", saved, m_SaveTask.IsFaulted ? m_SaveTask.Exception?.GetBaseException().Message : null);
                m_SaveTask = null;
                if (saved)
                {
                    Line($"Reloading '{kProtestSave}'.");
                    SetStage(Stage.RiotReloading);
                    _ = GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, FindSave(kProtestSave));
                }
                else
                {
                    SetStage(Stage.Finish);
                }
            }
            else if (StageSeconds() > 120f)
            {
                Check("The city saves during a riot", false, "no result within 120 s");
                SetStage(Stage.Finish);
            }
        }

        /// <summary>What the load itself left of the closures and posts, before the mod's next update repairs them.</summary>
        private void LogLoadedRoads()
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<ClosedLane>(city))
            {
                Line("Loaded roads: no protest buffers yet.");
                return;
            }
            DynamicBuffer<ClosedLane> closed = EntityManager.GetBuffer<ClosedLane>(city, true);
            int flagged = 0;
            int exists = 0;
            for (int i = 0; i < closed.Length; i++)
            {
                exists += EntityManager.Exists(closed[i].m_Lane) ? 1 : 0;
                if (EntityManager.TryGetComponent(closed[i].m_Lane, out Game.Net.CarLane lane) && (lane.m_Flags & Game.Net.CarLaneFlags.PublicOnly) != 0)
                {
                    flagged++;
                }
            }
            var posts = new StringBuilder();
            DynamicBuffer<PolicePost> buffer = EntityManager.GetBuffer<PolicePost>(city, true);
            for (int i = 0; i < buffer.Length; i++)
            {
                PolicePost post = buffer[i];
                int targets = EntityManager.TryGetBuffer(post.m_Event, true, out DynamicBuffer<Game.Events.TargetElement> t) ? t.Length : -1;
                posts.Append($"[site {EntityManager.Exists(post.m_Site)}/{EntityManager.HasComponent<Game.Events.AccidentSite>(post.m_Site)}, "
                    + $"point {EntityManager.Exists(post.m_Point)}/{EntityManager.HasComponent<Game.Events.InvolvedInAccident>(post.m_Point)}/{EntityManager.HasComponent<Game.Objects.Attached>(post.m_Point)}, "
                    + $"event {EntityManager.Exists(post.m_Event)} targets {targets}] ");
            }
            Line($"Loaded roads, before the mod updates: {flagged} of {closed.Length} recorded lanes public-only ({exists} exist); posts {posts}");
        }

        private void CheckProtestRoundTrip()
        {
            if (ProtestIndex(m_RiotId) < 0)
            {
                Check("The riot survives saving and loading", false, "not in the loaded city");
                SetStage(Stage.Finish);
                return;
            }
            string loaded = ProtestSnapshot();
            Line($"Protest data after loading: {loaded}");
            Check("The riot survives saving and loading (response, character, demand, pressure references)", loaded == m_ProtestSnapshot, loaded == m_ProtestSnapshot ? null : $"before: {m_ProtestSnapshot}");
            DynamicBuffer<ClosedLane> closed = EntityManager.GetBuffer<ClosedLane>(m_CitySystem.City, true);
            int flagged = 0;
            for (int i = 0; i < closed.Length; i++)
            {
                if (EntityManager.TryGetComponent(closed[i].m_Lane, out Game.Net.CarLane lane) && (lane.m_Flags & Game.Net.CarLaneFlags.PublicOnly) != 0)
                {
                    flagged++;
                }
            }
            Check("Closed lanes are still closed after loading", closed.Length > 0 && flagged == closed.Length, $"{flagged} of {closed.Length} lanes public-only");
            DynamicBuffer<PolicePost> posts = EntityManager.GetBuffer<PolicePost>(m_CitySystem.City, true);
            int intact = 0;
            for (int i = 0; i < posts.Length; i++)
            {
                intact += Protests.PostIntact(posts[i]) ? 1 : 0;
            }
            CheckCrowd("After loading, the rioters keep coming to the riot roads", m_RiotId, null);
            Protest protest = GetProtest(m_RiotId);
            Check("After loading, police posts are whole (broken ones asked for again)", posts.Length > 0 && intact == posts.Length,
                $"{intact} of {posts.Length} posts whole, {protest.m_CarsArrived}/{protest.m_CarsNeeded} cars in place");
        }

        private void GrantRiot()
        {
            Protest protest = GetProtest(m_RiotId);
            // Just above the riot threshold, so the fade after the concession ends it within a few updates.
            protest.m_Intensity = 0.62f;
            SetProtest(protest);
            m_IconOwnerOf = protest.m_IconOwner;
            m_ClosedLanesSeen.Clear();
            DynamicBuffer<ClosedLane> closed = EntityManager.GetBuffer<ClosedLane>(m_CitySystem.City, true);
            for (int i = 0; i < closed.Length; i++)
            {
                m_ClosedLanesSeen.Add(closed[i].m_Lane);
            }
            m_PostEntities.Clear();
            DynamicBuffer<PolicePost> posts = EntityManager.GetBuffer<PolicePost>(m_CitySystem.City, true);
            for (int i = 0; i < posts.Length; i++)
            {
                m_PostEntities.Add(posts[i].m_Site);
                m_PostEntities.Add(posts[i].m_Point);
                m_PostEntities.Add(posts[i].m_Event);
            }
            m_HeldCar = StandingCar();

            float loss = Mathf.Max(1f, Protests.DemandMoney(Entity.Null));
            float pressure = Protests.DemandPressure(Entity.Null);
            int compensation = Mathf.CeilToInt(12f * loss * 1.1f) + 1;
            int rate = World.GetOrCreateSystemManaged<TaxSystem>().GetTaxRate(TaxAreaType.Residential);
            int before = Money();
            Protests.Negotiate(m_RiotId, rate, compensation);
            int paid = before - Money();
            bool postsGone = PostCount(m_RiotId) == 0;
            foreach (Entity entity in m_PostEntities)
            {
                postsGone &= !EntityManager.Exists(entity);
            }
            Check("A year of the protesters' loss as compensation is paid at once and the police posts go",
                paid == compensation && postsGone, $"paid {paid} of {compensation} (loss {loss:0}/month, pressure {pressure:0.000}%), posts {PostCount(m_RiotId)}");
            m_EndSeenAt = 0;
            SetStage(Stage.RiotEnd);
        }

        private void UpdateRiotEnd()
        {
            if (ProtestIndex(m_RiotId) >= 0)
            {
                if (SimFrames() > 30000)
                {
                    Protest protest = GetProtest(m_RiotId);
                    Check("A riot whose demand is met fades and ends", false, $"granted {protest.m_Granted}, intensity {protest.m_Intensity:0.00}");
                    SetStage(Stage.Finish);
                }
                return;
            }
            if (m_EndSeenAt == 0)
            {
                m_EndSeenAt = SimFrames();
                Line($"The riot ended {m_EndSeenAt} frames after the concession.");
                return;
            }
            if (SimFrames() - m_EndSeenAt < 2560)
            {
                return;
            }
            int stillClosed = 0;
            foreach (Entity lane in m_ClosedLanesSeen)
            {
                if (EntityManager.TryGetComponent(lane, out Game.Net.CarLane data) && (data.m_Flags & Game.Net.CarLaneFlags.PublicOnly) != 0)
                {
                    stillClosed++;
                }
            }
            int recorded = EntityManager.GetBuffer<ClosedLane>(m_CitySystem.City, true).Length;
            Check("When the riot ends every closed lane opens again", recorded == 0 && stillClosed == 0, $"{stillClosed} of {m_ClosedLanesSeen.Count} lanes still public-only, {recorded} recorded");
            Game.Vehicles.PoliceCar car = m_HeldCar != Entity.Null && EntityManager.Exists(m_HeldCar) && EntityManager.HasComponent<Game.Vehicles.PoliceCar>(m_HeldCar)
                ? EntityManager.GetComponentData<Game.Vehicles.PoliceCar>(m_HeldCar) : default;
            bool carBack = m_HeldCar == Entity.Null || !EntityManager.Exists(m_HeldCar) || car.m_PurposeMask != 0;
            Check("The police car loaded while standing gets its station's duties back", carBack && Protests.HeldCarCount == 0,
                m_HeldCar == Entity.Null ? "no car stood there after loading" : $"purpose {car.m_PurposeMask}, held {Protests.HeldCarCount}");
            ProtestCityState state = EntityManager.GetComponentData<ProtestCityState>(m_CitySystem.City);
            Check("Concession after repression raises the start rate of the next protests for a year", state.m_MixedUntil > m_SimulationSystem.frameIndex,
                $"until frame {state.m_MixedUntil}, now {m_SimulationSystem.frameIndex}");
            int leftSpots = EntityManager.GetBuffer<ProtestSpot>(m_CitySystem.City, true).Length;
            (int leftHeading, int leftStanding) = CountCrowd(null);
            Check("When the riot ends its gathering spots go and nobody still heads for them", leftSpots == 0 && leftHeading == 0,
                $"{leftSpots} spots, {leftHeading} people heading to a spot ({leftStanding} standing)");
            Check("The riot's icon goes when it ends", !HasIcon(m_IconOwnerOf, ProtestPrefabs.RiotIcon) && !HasIcon(m_IconOwnerOf, ProtestPrefabs.ProtestIcon), null);

            Entity scholarships = PolicyCatalog.Find(PolicyCatalog.kScholarships);
            Protests.NoteSurpriseWin(scholarships, 0.8f);
            DynamicBuffer<ProtestPressure> pressures = EntityManager.GetBuffer<ProtestPressure>(m_CitySystem.City, true);
            float factor = 0f;
            uint until = 0;
            for (int i = 0; i < pressures.Length; i++)
            {
                if (pressures[i].m_Policy == scholarships)
                {
                    factor = pressures[i].m_SurpriseFactor;
                    until = pressures[i].m_SurpriseUntil;
                }
            }
            Check("A suit residents expected to win (p 0.8) but lost raises their pressure x1.6 for a year", Mathf.Abs(factor - 1.6f) < 1e-4f && until > m_SimulationSystem.frameIndex,
                $"x{factor:0.00} until {until}");
            Line($"Legitimacy after the protest stages: {m_Engine.LastState.m_Legitimacy:0.000}.");
            StartCleanupTest();
        }

        // ------------------------------------------------------------------ strikes and collective closures (§12 "파업")

        private float m_BaseIndustrialEfficiency;

        private float CityIndustrialEfficiency()
        {
            DynamicBuffer<Game.City.CityModifier> modifiers = EntityManager.GetBuffer<Game.City.CityModifier>(m_CitySystem.City, true);
            int index = (int)Game.City.CityModifierType.IndustrialEfficiency;
            return modifiers.Length > index ? modifiers[index].m_Delta.y : 0f;
        }

        /// <summary>
        /// A closure in industry (emission standard), a public servants' strike (contracting out) and a shop workers' strike
        /// (minimum wage), held at a 0.3 change. Contracting out, not civil service pay: batches 3-10 switch civil service pay on
        /// and off just before, a real cut the strike rightly keeps going over (changed-only run 2026-11-10).
        /// </summary>
        private void StartActionTest()
        {
            m_BaseIndustrialEfficiency = CityIndustrialEfficiency();
            ProtestSystem.TestHeldChange = 0.3f;
            ProtestSystem.TestStartActions.Add((PolicyCatalog.Find(PolicyCatalog.kEmissionStandard), ActionKind.Closure, ActionSector.Industrial));
            ProtestSystem.TestStartActions.Add((PolicyCatalog.Find(PolicyCatalog.kContractingOut), ActionKind.Strike, ActionSector.Public));
            ProtestSystem.TestStartActions.Add((PolicyCatalog.Find(PolicyCatalog.kMinimumWage), ActionKind.Strike, ActionSector.Commercial));
            Line($"Forcing an industrial closure, a public servants' strike and a shop workers' strike; city industrial efficiency modifier now {m_BaseIndustrialEfficiency:0.000}.");
            SetStage(Stage.ActionStart);
        }

        private void UpdateActionStart()
        {
            int count = EntityManager.GetBuffer<IndustrialAction>(m_CitySystem.City, true).Length;
            if (count >= 3)
            {
                SetStage(Stage.ActionRun);
            }
            else if (SimFrames() > 8192)
            {
                Check("A strike and a closure start", false, $"{count} within 8192 frames");
                ProtestSystem.TestHeldChange = -1f;
                StartRiot();
            }
        }

        private void UpdateActionRun()
        {
            if (SimFrames() < 6144)
            {
                return;
            }
            DynamicBuffer<IndustrialAction> actions = EntityManager.GetBuffer<IndustrialAction>(m_CitySystem.City, true);
            var parts = new System.Collections.Generic.List<string>();
            bool grew = actions.Length >= 3;
            for (int i = 0; i < actions.Length; i++)
            {
                parts.Add($"{(ActionKind)actions[i].m_Kind} {(ActionSector)actions[i].m_Sector} {actions[i].m_Share:0.000}");
                grew &= actions[i].m_Share > 0.05f;
            }
            Check("A strike and a closure start and draw more people in", grew, string.Join(", ", parts));
            float now = CityIndustrialEfficiency();
            Check("The closure lowers industrial efficiency through the game's city modifier (hidden policy on); people still go to work",
                Protests.IsActionPolicyActive(m_CitySystem.City) && Protests.LastIndustrialLoss > 0f && now < m_BaseIndustrialEfficiency - 0.005f,
                $"loss {Protests.LastIndustrialLoss:0.000}, city modifier {m_BaseIndustrialEfficiency:0.000} -> {now:0.000}, hidden policy {Protests.IsActionPolicyActive(m_CitySystem.City)}");
            Check("Public servants on strike leave less staff for policy work", ProtestSystem.PublicStaffLoss > 0f, $"staff loss {ProtestSystem.PublicStaffLoss:0.000}");
            BuildingEffectSystem effects = World.GetOrCreateSystemManaged<BuildingEffectSystem>();
            Check("A shop workers' strike lowers each shop's employee happiness factor (people still go to work)",
                ProtestSystem.CommercialLoss > 0f && effects.LastCommercialFactor < 1f && effects.CommercialTrackedCount > 0,
                $"shop loss {ProtestSystem.CommercialLoss:0.000}, factor {effects.LastCommercialFactor:0.000}, shops changed {effects.CommercialTrackedCount}");
            m_UiDone = false;
            m_UISystem.SetSelfTestStage(6);
            SetStage(Stage.ActionUi);
        }

        private void UpdateActionEnd()
        {
            if (SimFrames() < 4096)
            {
                return;
            }
            int left = EntityManager.GetBuffer<IndustrialAction>(m_CitySystem.City, true).Length;
            Check("Giving back what they ask ends the strike and the closure, and the losses go",
                left == 0 && Protests.LastIndustrialLoss == 0f && ProtestSystem.PublicStaffLoss == 0f && ProtestSystem.CommercialLoss == 0f
                && World.GetOrCreateSystemManaged<BuildingEffectSystem>().CommercialTrackedCount == 0,
                $"{left} left, industrial loss {Protests.LastIndustrialLoss:0.000}, staff loss {ProtestSystem.PublicStaffLoss:0.000}, hidden policy {Protests.IsActionPolicyActive(m_CitySystem.City)}");
            StartRiot();
        }

        // ------------------------------------------------------------------ clean-up before removing the mod

        private int m_CrimesBeforeSave;
        private Entity[] m_CleanupLanes = new Entity[0];
        private Entity m_CleanupOwner;

        /// <summary>
        /// Distance from the scene of a rioter's crime (the game's crime site whose event is that rioter's) to the building
        /// chosen for it on its own riot's roads; the nearest such scene, -1 while none has opened. Each crime is measured
        /// against its own riot (2026-11-10: a crime of the riot carried over the save, at its own building, was measured
        /// against the clean-up riot's roads 5 km away and failed the check).
        /// </summary>
        private float RiotCrimeSceneDistance()
        {
            var sites = new System.Collections.Generic.Dictionary<Entity, Unity.Mathematics.float3>();
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Events.AccidentSite>(), ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.Exclude<Deleted>()))
            using (NativeArray<Entity> found = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity site in found)
                {
                    Entity e = EntityManager.GetComponentData<Game.Events.AccidentSite>(site).m_Event;
                    if (e != Entity.Null && !sites.ContainsKey(e))
                    {
                        sites[e] = EntityManager.GetComponentData<Game.Objects.Transform>(site).m_Position;
                    }
                }
            }
            float best = -1f;
            foreach (ProtestSystem.RiotCrimeRecord record in Protests.RiotCrimeRecords)
            {
                if (!EntityManager.TryGetComponent(record.Citizen, out Game.Citizens.Criminal criminal) || criminal.m_Event == Entity.Null
                    || !sites.TryGetValue(criminal.m_Event, out Unity.Mathematics.float3 scene)
                    || !EntityManager.TryGetComponent(record.Target, out Game.Objects.Transform chosen))
                {
                    continue;
                }
                float d = Unity.Mathematics.math.distance(scene, chosen.m_Position);
                best = best < 0f ? d : Unity.Mathematics.math.min(best, d);
            }
            return best;
        }

        /// <summary>Middles of a protest's road segments.</summary>
        private System.Collections.Generic.List<Unity.Mathematics.float3> ProtestRoadMiddles(int protestId)
        {
            var roads = new System.Collections.Generic.List<Unity.Mathematics.float3>();
            DynamicBuffer<ProtestEdge> edges = EntityManager.GetBuffer<ProtestEdge>(m_CitySystem.City, true);
            for (int i = 0; i < edges.Length; i++)
            {
                if (edges[i].m_Protest == protestId && EntityManager.TryGetComponent(edges[i].m_Edge, out Game.Net.Curve curve))
                {
                    roads.Add(Colossal.Mathematics.MathUtils.Position(curve.m_Bezier, 0.5f));
                }
            }
            return roads;
        }

        private static float Nearest(System.Collections.Generic.List<Unity.Mathematics.float3> points, Unity.Mathematics.float3 at)
        {
            float best = -1f;
            foreach (Unity.Mathematics.float3 point in points)
            {
                float d = Unity.Mathematics.math.distance(at, point);
                best = best < 0f ? d : Unity.Mathematics.math.min(best, d);
            }
            return best;
        }

        /// <summary>
        /// Each riot crime since loading (diagnosis 2026-11-10: scenes 2.7-6.2 km from the riot in three runs): its riot,
        /// start frame, the building chosen for it, what became of that target, the criminal's event and its crime scene,
        /// and how far the scene is from the chosen building, from this riot's roads and from its own riot's roads.
        /// </summary>
        private void LogRiotCrimes(Protest riot)
        {
            var sites = new System.Collections.Generic.Dictionary<Entity, Unity.Mathematics.float3>();
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Events.AccidentSite>(), ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.Exclude<Deleted>()))
            using (NativeArray<Entity> found = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity site in found)
                {
                    Entity e = EntityManager.GetComponentData<Game.Events.AccidentSite>(site).m_Event;
                    if (e != Entity.Null && !sites.ContainsKey(e))
                    {
                        sites[e] = EntityManager.GetComponentData<Game.Objects.Transform>(site).m_Position;
                    }
                }
            }
            var roads = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<Unity.Mathematics.float3>>();
            var text = new StringBuilder($"Riot crimes since loading: {Protests.RiotCrimeRecords.Count} (this riot is {riot.m_Id}, now frame {m_SimulationSystem.frameIndex}).");
            foreach (ProtestSystem.RiotCrimeRecord record in Protests.RiotCrimeRecords)
            {
                if (!roads.TryGetValue(record.Protest, out System.Collections.Generic.List<Unity.Mathematics.float3> own))
                {
                    own = ProtestRoadMiddles(record.Protest);
                    roads[record.Protest] = own;
                }
                text.Append($"\n  riot {record.Protest}{(record.Protest == riot.m_Id ? " (this)" : "")}, frame {record.Frame}, citizen {record.Citizen.Index}");
                bool hasTarget = EntityManager.TryGetComponent(record.Target, out Game.Objects.Transform targetAt);
                text.Append(hasTarget ? $", chosen building {record.Target.Index} {Nearest(own, targetAt.m_Position):0} m from its riot's roads" : ", no building chosen");
                text.Append($", target {(RiotCrimeTargetSystem.Outcomes.TryGetValue(record.Citizen, out string outcome) ? outcome : "not tracked")}");
                if (!EntityManager.TryGetComponent(record.Citizen, out Game.Citizens.Criminal criminal))
                {
                    text.Append(", no longer a criminal");
                    continue;
                }
                text.Append($", criminal flags {criminal.m_Flags}, event {criminal.m_Event.Index}");
                if (criminal.m_Event != Entity.Null && sites.TryGetValue(criminal.m_Event, out Unity.Mathematics.float3 scene))
                {
                    text.Append($", scene at ({scene.x:0}, {scene.z:0}): {(hasTarget ? Unity.Mathematics.math.distance(scene, targetAt.m_Position) : -1f):0} m from the chosen building, "
                        + $"{Nearest(ProtestRoadMiddles(riot.m_Id), scene):0} m from this riot's roads, {Nearest(own, scene):0} m from its own riot's roads");
                }
                else
                {
                    text.Append(", no crime scene yet");
                }
                if (EntityManager.TryGetComponent(record.Citizen, out Game.Citizens.TravelPurpose purpose))
                {
                    text.Append($", travelling for {purpose.m_Purpose}");
                }
                if (EntityManager.TryGetComponent(record.Citizen, out Target target) && target.m_Target != Entity.Null)
                {
                    text.Append($", heading to {target.m_Target.Index}{(target.m_Target == record.Target ? " (the chosen building)" : "")}");
                }
            }
            Line(text.ToString());
        }

        /// <summary>Crime scenes the game has open now, and how many lie within 300 m of the riot's start (log only).</summary>
        private void LogCrimeScenes(Protest riot)
        {
            Unity.Mathematics.float3 at = EntityManager.TryGetComponent(riot.m_StartEdge, out Game.Net.Curve curve) ? Colossal.Mathematics.MathUtils.Position(curve.m_Bezier, 0.5f) : default;
            int scenes = 0, near = 0;
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Events.AccidentSite>(), ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.Exclude<Deleted>()))
            using (NativeArray<Entity> sites = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity site in sites)
                {
                    if ((EntityManager.GetComponentData<Game.Events.AccidentSite>(site).m_Flags & Game.Events.AccidentSiteFlags.CrimeScene) == 0)
                    {
                        continue;
                    }
                    scenes++;
                    near += Unity.Mathematics.math.distance(at, EntityManager.GetComponentData<Game.Objects.Transform>(site).m_Position) <= 300f ? 1 : 0;
                }
            }
            Line($"Riot crimes so far: {Protests.RiotCrimes} started; crime scenes open in the city {scenes}, within 300 m of the riot {near}.");
        }

        /// <summary>A new riot to clean up: started on a road, dispersed by police, then the options button is pressed.</summary>
        private void StartCleanupTest()
        {
            RememberProtests();
            ProtestSystem.TestProtestTax = true;
            ProtestSystem.TestNoPark = true;
            ProtestSystem.TestStartProtest = true;
            Line("Forcing a riot to test the clean-up for removing the mod.");
            SetStage(Stage.CleanupStart);
        }

        private void UpdateCleanupStart()
        {
            int id = NewProtest(Entity.Null);
            if (id > 0)
            {
                m_RiotId = id;
                PinRiot(0.8f);
                Protests.Respond(id, ProtestResponse.Disperse);
                SetStage(Stage.CleanupRiot);
            }
            else if (SimFrames() > 8192)
            {
                Check("A riot starts for the clean-up test", false, "none within 8192 frames");
                SetStage(Stage.Finish);
            }
        }

        private void UpdateCleanupRiot()
        {
            // The crime check waits here, on a riot held at 0.8: about 0.16 crimes per segment and protest update, so a short
            // riot often has none yet (the first run checked after about 6 updates on one segment and saw 0).
            int crimes = m_CrimesBeforeSave + Protests.RiotCrimes;
            float sceneDistance = RiotCrimeSceneDistance();
            if (SimFrames() < 6144 || ((crimes == 0 || sceneDistance < 0f) && SimFrames() < 98304))
            {
                return;
            }
            Protest riot = GetProtest(m_RiotId);
            LogCrimeScenes(riot);
            Check("Rioters commit crimes through the game's crime event (about 7 for each fire)", crimes > 0,
                $"{crimes} crimes started ({m_CrimesBeforeSave} before saving, {Protests.RiotCrimes} since loading), {SimFrames()} frames into this riot");
            // The game sends a criminal to the building with the most crime; the riot holds its buildings' crime at the
            // intensity, so the crime should happen at the riot (2026-11-09 user: "당연히 폭동 현장 근처로").
            Check("A rioter's crime happens near its riot (within 300 m of the building chosen for it on that riot's roads)", sceneDistance >= 0f && sceneDistance <= 300f,
                (sceneDistance >= 0f ? $"{sceneDistance:0} m from the chosen building" : "no crime scene of a rioter yet")
                + $", crime trips pointed at riot buildings {RiotCrimeTargetSystem.Pointed}");
            LogRiotCrimes(riot);
            DynamicBuffer<ClosedLane> closed = EntityManager.GetBuffer<ClosedLane>(m_CitySystem.City, true);
            m_CleanupLanes = new Entity[closed.Length];
            for (int i = 0; i < closed.Length; i++)
            {
                m_CleanupLanes[i] = closed[i].m_Lane;
            }
            m_CleanupOwner = riot.m_IconOwner;
            Line($"Before the clean-up: riot {riot.m_Riot}, {closed.Length} closed lanes, {PostCount(m_RiotId)} posts, {Protests.HeldCarCount} police cars held, "
                + $"{EntityManager.GetBuffer<ProtestSpot>(m_CitySystem.City, true).Length} spots, icon {HasIcon(m_CleanupOwner, ProtestPrefabs.RiotIcon)}.");
            bool ran = ModCleanup.Run();
            Line($"Options button 'Prepare for removal' pressed (confirmed): {(ran ? "done" : "no city")}.");
            SetStage(Stage.CleanupCheck);
        }

        private void UpdateCleanupCheck()
        {
            // Past a protest update, so nothing the mod would add again has come back.
            if (SimFrames() < 2560)
            {
                return;
            }
            Entity city = m_CitySystem.City;
            int stillClosed = 0;
            foreach (Entity lane in m_CleanupLanes)
            {
                if (EntityManager.TryGetComponent(lane, out Game.Net.CarLane carLane) && (carLane.m_Flags & Game.Net.CarLaneFlags.PublicOnly) != 0)
                {
                    stillClosed++;
                }
            }
            int protests = EntityManager.GetBuffer<Protest>(city, true).Length;
            int posts = EntityManager.GetBuffer<PolicePost>(city, true).Length;
            int spots = EntityManager.GetBuffer<ProtestSpot>(city, true).Length;
            bool icon = HasIcon(m_CleanupOwner, ProtestPrefabs.RiotIcon) || HasIcon(m_CleanupOwner, ProtestPrefabs.ProtestIcon);
            int lawsuitIcons = 0;
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Deleted>()))
            using (NativeArray<Entity> buildings = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity building in buildings)
                {
                    lawsuitIcons += HasLawsuitIcon(building) ? 1 : 0;
                }
            }
            Check("'Prepare for removal' ends every protest, opens the closed roads, releases police and takes the icons off, and they stay off",
                ModCleanup.Done && protests == 0 && m_CleanupLanes.Length > 0 && stillClosed == 0 && posts == 0 && Protests.HeldCarCount == 0 && spots == 0 && !icon && lawsuitIcons == 0,
                $"{protests} protests, {stillClosed} of {m_CleanupLanes.Length} lanes still closed, {posts} posts, {Protests.HeldCarCount} cars held, {spots} spots, riot icon {icon}, lawsuit icons {lawsuitIcons}");
            SetStage(Stage.Finish);
        }

        // ------------------------------------------------------------------ measurement runs (PLAN §14.19)

        private const uint kMeasureSampleFrames = 32768;
        private const int kMeasureSamples = 16;
        private string m_MeasureVariant;
        private int m_MeasureCount;
        private string m_MeasurePath;

        /// <summary>
        /// "save:...;measure:base" or ";measure:policies": two days at the game's own engine cadence, sampled every
        /// eighth of a day. The policies run turns batches 3-10 on at full strength and delivers them at once.
        /// </summary>
        private void StartMeasure()
        {
            m_WelfareOffice = FindGeneralWelfareOffice(Entity.Null);
            m_TaxOffice = FindGeneralWelfareOffice(m_WelfareOffice);
            World.GetOrCreateSystemManaged<WelfareSeriesSection>().Set(m_WelfareOffice, WelfareSeriesSection.Choice.SocialWelfare);
            World.GetOrCreateSystemManaged<WelfareSeriesSection>().Set(m_TaxOffice, WelfareSeriesSection.Choice.Tax);
            if (m_MeasureVariant == "policies")
            {
                PolicyEngineSystem.TestInstantDelivery = true;
                foreach (string id in kLaterBundles)
                {
                    PolicyDefinition definition = PolicyCatalog.Definition(id);
                    SetPolicy(m_CitySystem.City, PolicyCatalog.Find(id), true, definition.IsSlider ? definition.SliderRange.max : 0f);
                }
            }
            Line($"Measurement '{m_MeasureVariant}': {kMeasureSamples} samples every {kMeasureSampleFrames} frames; welfare office {m_WelfareOffice.Index} social welfare, {m_TaxOffice.Index} tax.");
            LogOpenItems();
            m_MeasurePath = Path.Combine(m_Directory, "selftest", $"measure-{m_MeasureVariant}.csv");
            File.WriteAllText(m_MeasurePath, MeasureHeader + "\n");
            m_MeasureCount = 0;
            m_SimulationSystem.selectedSpeed = kTestSpeed;
            SetStage(Stage.Measure);
        }

        private void UpdateMeasure()
        {
            if (m_MeasureCount > 0 && SimFrames() < kMeasureSampleFrames)
            {
                return;
            }
            File.AppendAllText(m_MeasurePath, MeasureRow() + "\n");
            m_MeasureCount++;
            if (m_MeasureCount > kMeasureSamples)
            {
                PolicyEngineSystem.TestInstantDelivery = false;
                if (m_MeasureVariant == "policies")
                {
                    var off = new StringBuilder();
                    foreach (string id in kLaterBundles)
                    {
                        PolicyStepResult r = m_Engine.LastResults[System.Array.IndexOf(PolicyDefinitions.All, PolicyCatalog.Definition(id))];
                        off.Append($"{Short(id)} {r.Progress:0.000}/{r.Intensity:0.00} c{r.Compliance:0.00}; ");
                    }
                    Line("Delivered at the end (progress/intensity, compliance): " + off);
                }
                Line($"Search delay: {ExtendedBenefitSearchSystem.Delayed} of {ExtendedBenefitSearchSystem.EligibleSeen} checks.");
                SetStage(Stage.Finish);
                return;
            }
            SetStage(Stage.Measure);
        }

        private const string MeasureHeader = "sample,frame,population,households,homeless,unemployed,workers,wellbeing,health,deathRate,birthRate,movedIn,movedAway,crimeCount,crimeRate,escaped,money," +
            "criminals,prisoners,meanBuildingCrime,sick,students1,students2,students3,students4,elecWanted,elecFulfilled,waterWanted,waterFresh,waterShortBuildings," +
            "garbageStock,onFire,destroyed,trafficAccidents,meanRent,level1,level2,level3,level4,level5,air,legitimacy,credibility,warningDelta";

        private string MeasureRow()
        {
            var cs = System.Globalization.CultureInfo.InvariantCulture;
            int Stat(StatisticType type, int parameter = 0) => m_StatisticsSystem.GetStatisticValue(type, parameter);
            var row = new System.Collections.Generic.List<string>
            {
                m_MeasureCount.ToString(cs), m_SimulationSystem.frameIndex.ToString(cs),
                Stat(StatisticType.Population).ToString(cs), Stat(StatisticType.HouseholdCount).ToString(cs), Stat(StatisticType.HomelessCount).ToString(cs),
                Stat(StatisticType.Unemployed).ToString(cs), Stat(StatisticType.WorkerCount).ToString(cs), Stat(StatisticType.Wellbeing).ToString(cs),
                Stat(StatisticType.Health).ToString(cs), Stat(StatisticType.DeathRate).ToString(cs), Stat(StatisticType.BirthRate).ToString(cs),
                Stat(StatisticType.CitizensMovedIn).ToString(cs), Stat(StatisticType.CitizensMovedAway).ToString(cs), Stat(StatisticType.CrimeCount).ToString(cs),
                Stat(StatisticType.CrimeRate).ToString(cs), Stat(StatisticType.EscapedArrestCount).ToString(cs), Stat(StatisticType.Money).ToString(cs),
            };

            int criminals = 0, prisoners = 0;
            using (EntityQuery q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Citizens.Criminal>(), ComponentType.Exclude<Deleted>()))
            using (NativeArray<Game.Citizens.Criminal> a = q.ToComponentDataArray<Game.Citizens.Criminal>(Allocator.Temp))
            {
                criminals = a.Length;
                for (int i = 0; i < a.Length; i++)
                {
                    prisoners += (a[i].m_Flags & Game.Citizens.CriminalFlags.Prisoner) != 0 ? 1 : 0;
                }
            }
            double crime = 0;
            int producers = 0;
            using (EntityQuery q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Buildings.CrimeProducer>(), ComponentType.Exclude<Deleted>()))
            using (NativeArray<Game.Buildings.CrimeProducer> a = q.ToComponentDataArray<Game.Buildings.CrimeProducer>(Allocator.Temp))
            {
                producers = a.Length;
                for (int i = 0; i < a.Length; i++)
                {
                    crime += a[i].m_Crime;
                }
            }
            int sick = 0;
            using (EntityQuery q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Citizens.HealthProblem>(), ComponentType.Exclude<Deleted>()))
            using (NativeArray<Game.Citizens.HealthProblem> a = q.ToComponentDataArray<Game.Citizens.HealthProblem>(Allocator.Temp))
            {
                for (int i = 0; i < a.Length; i++)
                {
                    sick += (a[i].m_Flags & Game.Citizens.HealthProblemFlags.Sick) != 0 ? 1 : 0;
                }
            }
            var students = new int[5];
            using (EntityQuery q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Citizens.Student>(), ComponentType.Exclude<Deleted>()))
            using (NativeArray<Game.Citizens.Student> a = q.ToComponentDataArray<Game.Citizens.Student>(Allocator.Temp))
            {
                for (int i = 0; i < a.Length; i++)
                {
                    students[Unity.Mathematics.math.clamp(a[i].m_Level, 0, 4)]++;
                }
            }
            long elecWanted = 0, elecFulfilled = 0;
            using (EntityQuery q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Buildings.ElectricityConsumer>(), ComponentType.Exclude<Deleted>()))
            using (NativeArray<Game.Buildings.ElectricityConsumer> a = q.ToComponentDataArray<Game.Buildings.ElectricityConsumer>(Allocator.Temp))
            {
                for (int i = 0; i < a.Length; i++)
                {
                    elecWanted += a[i].m_WantedConsumption;
                    elecFulfilled += a[i].m_FulfilledConsumption;
                }
            }
            long waterWanted = 0, waterFresh = 0;
            int waterShort = 0;
            using (EntityQuery q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Buildings.WaterConsumer>(), ComponentType.Exclude<Deleted>()))
            using (NativeArray<Game.Buildings.WaterConsumer> a = q.ToComponentDataArray<Game.Buildings.WaterConsumer>(Allocator.Temp))
            {
                for (int i = 0; i < a.Length; i++)
                {
                    waterWanted += a[i].m_WantedConsumption;
                    waterFresh += a[i].m_FulfilledFresh;
                    waterShort += a[i].m_FulfilledFresh < a[i].m_WantedConsumption ? 1 : 0;
                }
            }
            long garbage = 0;
            using (EntityQuery q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Buildings.GarbageProducer>(), ComponentType.Exclude<Deleted>()))
            using (NativeArray<Game.Buildings.GarbageProducer> a = q.ToComponentDataArray<Game.Buildings.GarbageProducer>(Allocator.Temp))
            {
                for (int i = 0; i < a.Length; i++)
                {
                    garbage += a[i].m_Garbage;
                }
            }
            int onFire, destroyed, accidents = 0;
            using (EntityQuery q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Events.OnFire>(), ComponentType.Exclude<Deleted>()))
            {
                onFire = q.CalculateEntityCount();
            }
            using (EntityQuery q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Common.Destroyed>(), ComponentType.Exclude<Deleted>()))
            {
                destroyed = q.CalculateEntityCount();
            }
            using (EntityQuery q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Events.AccidentSite>(), ComponentType.Exclude<Deleted>()))
            using (NativeArray<Game.Events.AccidentSite> a = q.ToComponentDataArray<Game.Events.AccidentSite>(Allocator.Temp))
            {
                for (int i = 0; i < a.Length; i++)
                {
                    accidents += (a[i].m_Flags & Game.Events.AccidentSiteFlags.TrafficAccident) != 0 ? 1 : 0;
                }
            }
            double rent = 0;
            int renters = 0;
            using (EntityQuery q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Citizens.Household>(), ComponentType.ReadOnly<Game.Buildings.PropertyRenter>(), ComponentType.Exclude<Deleted>()))
            using (NativeArray<Game.Buildings.PropertyRenter> a = q.ToComponentDataArray<Game.Buildings.PropertyRenter>(Allocator.Temp))
            {
                renters = a.Length;
                for (int i = 0; i < a.Length; i++)
                {
                    rent += a[i].m_Rent;
                }
            }
            var levels = new int[6];
            using (EntityQuery q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Game.Tools.Temp>()))
            using (NativeArray<PrefabRef> a = q.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                for (int i = 0; i < a.Length; i++)
                {
                    if (EntityManager.TryGetComponent(a[i].m_Prefab, out SpawnableBuildingData spawnable))
                    {
                        levels[Unity.Mathematics.math.clamp((int)spawnable.m_Level, 0, 5)]++;
                    }
                }
            }
            DynamicBuffer<CityModifier> city = EntityManager.GetBuffer<CityModifier>(m_CitySystem.City, true);
            float warning = city.Length > (int)CityModifierType.DisasterWarningTime ? city[(int)CityModifierType.DisasterWarningTime].m_Delta.x : 0f;
            row.Add(criminals.ToString(cs));
            row.Add(prisoners.ToString(cs));
            row.Add((producers > 0 ? crime / producers : 0).ToString("0.00", cs));
            row.Add(sick.ToString(cs));
            for (int l = 1; l <= 4; l++)
            {
                row.Add(students[l].ToString(cs));
            }
            row.Add(elecWanted.ToString(cs));
            row.Add(elecFulfilled.ToString(cs));
            row.Add(waterWanted.ToString(cs));
            row.Add(waterFresh.ToString(cs));
            row.Add(waterShort.ToString(cs));
            row.Add(garbage.ToString(cs));
            row.Add(onFire.ToString(cs));
            row.Add(destroyed.ToString(cs));
            row.Add(accidents.ToString(cs));
            row.Add((renters > 0 ? rent / renters : 0).ToString("0.0", cs));
            for (int l = 1; l <= 5; l++)
            {
                row.Add(levels[l].ToString(cs));
            }
            row.Add(m_Engine.AirPollution.ToString("0.0000", cs));
            row.Add(m_Engine.LastState.m_Legitimacy.ToString("0.000", cs));
            row.Add(m_Engine.LastState.m_Credibility.ToString("0.000", cs));
            row.Add(warning.ToString("0.0", cs));
            return string.Join(",", row);
        }

        /// <summary>PLAN §13: which prefabs are administration buildings, and whether the unlock system can reach our prefabs.</summary>
        private void LogOpenItems()
        {
            var admin = new StringBuilder();
            using (EntityQuery q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<AdminBuildingData>()))
            using (NativeArray<Entity> prefabs = q.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity prefab in prefabs)
                {
                    admin.Append(m_PrefabSystem.GetPrefabName(prefab)).Append("; ");
                }
            }
            using (EntityQuery q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Buildings.AdminBuilding>(), ComponentType.Exclude<Deleted>()))
            {
                Line($"Admin building prefabs: {(admin.Length > 0 ? admin.ToString() : "none")}; placed in this city: {q.CalculateEntityCount()}");
            }
            int withRequirement = 0;
            foreach (PolicyDefinition definition in PolicyDefinitions.All)
            {
                withRequirement += EntityManager.HasBuffer<UnlockRequirement>(definition.Prefab) ? 1 : 0;
            }
            Line($"Mod policy prefabs with an UnlockRequirement buffer (UnlockSystem only touches those): {withRequirement} of {PolicyDefinitions.All.Length}");
        }

        /// <summary>Building codes count the buildings built or levelled up since they started (2026-11-10, option 1).</summary>
        private void CheckCodeCoverage()
        {
            PolicyEngineSystem.TestCodeCoverage = -1f;
            BuildingWorksSystem works = World.GetOrCreateSystemManaged<BuildingWorksSystem>();
            Entity city = m_CitySystem.City;
            int recorded = EntityManager.HasBuffer<BuildingWorks>(city) ? EntityManager.GetBuffer<BuildingWorks>(city, true).Length : 0;
            int buildings = m_Engine.Conditions != null ? m_Engine.Conditions.Buildings : 0;
            var text = new StringBuilder();
            bool ok = recorded > 0 && buildings > 0;
            foreach (string id in new[] { PolicyCatalog.kEnergyCode, PolicyCatalog.kDisasterCode, PolicyCatalog.kFireCode, PolicyCatalog.kCpted })
            {
                PolicyDefinition definition = PolicyCatalog.Definition(id);
                DynamicBuffer<PolicyRuntime> runtimes = EntityManager.GetBuffer<PolicyRuntime>(city, true);
                uint enacted = 0;
                float age = 0f;
                for (int r = 0; r < runtimes.Length; r++)
                {
                    if (runtimes[r].m_Policy == definition.Prefab)
                    {
                        enacted = runtimes[r].m_EnactedFrame;
                        age = runtimes[r].m_AgeDays;
                    }
                }
                int built = works.BuiltSince(enacted);
                ok &= enacted > 0 && built <= recorded;
                text.Append($"{Short(id)} built since {built}, age {age:0.00} d, last coverage {m_Engine.Covered(definition):0.0000}; ");
            }
            Check("Building codes count the buildings built or levelled up since they started (the game's construction)", ok,
                $"buildings under construction or placed recorded {recorded}, buildings {buildings}; " + text);
        }

        /// <summary>Detail settings (second sliders, 2026-11-10) as set when batches 3-10 came on.</summary>
        private void CheckSecondSettings(LivelihoodSupportSystem livelihood, float livelihoodNeed)
        {
            PolicyDefinition scholarships = PolicyCatalog.Definition(PolicyCatalog.kScholarships);
            float level = m_Engine.LastIntensity(scholarships);
            float students = m_Engine.Census.TargetCount(TargetGroup.TertiaryStudents);
            int row = m_Injection.GetRow(PolicyDefinitions.kScholarshipRow);
            float expected = 300f * students * level * 2f;
            Check("Scholarships pay students x share x the amount set in details (¢600 = twice the default)",
                Mathf.Approximately(m_Engine.SecondValue(scholarships), 600f) && Mathf.Abs(row - expected) <= 1f + 0.01f * expected,
                $"detail {m_Engine.SecondValue(scholarships):0}, students {students:0}, share {level:0.00}, row {row}, expected {expected:0}");
            Check("A detail setting counts in the level people feel (scholarships: slider x 600/300)",
                Mathf.Abs(m_Engine.FeltIntensity(scholarships) - 2f * level) < 1e-4f
                && Mathf.Abs(m_Engine.LastResults[System.Array.IndexOf(PolicyDefinitions.All, scholarships)].Felt - 2f * level) < 1e-4f,
                $"felt {m_Engine.FeltIntensity(scholarships):0.000}, slider {level:0.000}");
            FlexibleHoursSystem flexible = World.GetOrCreateSystemManaged<FlexibleHoursSystem>();
            Check("Flexible hours delay at most the minutes set in details (60)",
                Mathf.Abs(flexible.LastMaxDelayDays - 60f / 1440f) < 1e-6f, $"{flexible.LastMaxDelayDays * 1440f:0.0} min");
            PolicyDefinition livelihoodDefinition = PolicyCatalog.Definition(PolicyCatalog.kLivelihoodSupport);
            float qualifying = livelihood.LastHouseholds > 0 ? livelihood.LastPoor / (float)livelihood.LastHouseholds : 0f;
            Check("Livelihood support picks households with income under 32% of the median (the default in details), about 5-25% of households here",
                Mathf.Approximately(m_Engine.SecondValue(livelihoodDefinition), 32f) && qualifying >= 0.05f && qualifying <= 0.25f
                && livelihood.LastIncomeLimit == Mathf.RoundToInt(0.32f * livelihood.LastMedianIncome),
                $"limit {m_Engine.SecondValue(livelihoodDefinition):0}% of median {livelihood.LastMedianIncome} = {livelihood.LastIncomeLimit}, qualifying {livelihood.LastPoor} of {livelihood.LastHouseholds} ({qualifying:P1}), supported {livelihood.LastSupported}, staff needed {livelihoodNeed:0.0}");
            // Back to the defaults for the later stages (random suits and protests are off in the test).
            m_Engine.SetSecond(PolicyCatalog.kScholarships, scholarships.Second.Default);
            m_Engine.SetSecond(PolicyCatalog.kFlexibleHours, PolicyCatalog.Definition(PolicyCatalog.kFlexibleHours).Second.Default);
        }

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
            if (m_Stage != Stage.Ui && m_Stage != Stage.WelfareUi && m_Stage != Stage.ReportUi && m_Stage != Stage.ProtestUi && m_Stage != Stage.ProtestPanel
                && m_Stage != Stage.ActionUi && m_Stage != Stage.DetailsUi)
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

        // The cursor sits on another monitor while the test runs, and the game read it as being at the screen edge and kept
        // pushing the camera (2026-11-10 user report). Edge scrolling is off while the test runs and given back at the end;
        // the value is not saved by the test.
        private bool m_EdgeScrollingHeld;
        private bool m_EdgeScrollingBefore;

        private void HoldEdgeScrolling()
        {
            Game.Settings.GameplaySettings gameplay = Game.Settings.SharedSettings.instance?.gameplay;
            if (m_EdgeScrollingHeld || gameplay == null || m_Stage == Stage.Quit || m_Stage == Stage.Off)
            {
                return;
            }
            m_EdgeScrollingBefore = gameplay.edgeScrolling;
            gameplay.edgeScrolling = false;
            m_EdgeScrollingHeld = true;
            Line($"Edge scrolling off for the test (was {(m_EdgeScrollingBefore ? "on" : "off")}).");
        }

        private void ReleaseEdgeScrolling()
        {
            Game.Settings.GameplaySettings gameplay = Game.Settings.SharedSettings.instance?.gameplay;
            if (m_EdgeScrollingHeld && gameplay != null)
            {
                gameplay.edgeScrolling = m_EdgeScrollingBefore;
            }
            m_EdgeScrollingHeld = false;
        }

        [Preserve]
        protected override void OnDestroy()
        {
            ReleaseEdgeScrolling();
            base.OnDestroy();
        }

        private void Finish()
        {
            PolicyEngineSystem.TestCodeCoverage = -1f;
            ReleaseEdgeScrolling();
            LitigationSystem.TestTimeScale = 1f;
            RentSupportSystem.TestMonthScale = 1f;
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
