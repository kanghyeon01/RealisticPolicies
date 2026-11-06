using Colossal.Entities;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Game;
using Game.Buildings;
using Game.City;
using Game.Common;
using Game.Policies;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// Shared administrative layer (docs/PLAN.md §5–§6): turns each mod policy's intensity into a delivered effect
    /// through series capacity, joint action, staff workload, lags, compliance, capture and legitimacy, then writes
    /// the effect into the policy prefab's city modifiers, which vanilla applies to the city.
    /// A step is a handful of scalar passes; the building census behind it runs once a day.
    /// </summary>
    public sealed class PolicyEngineSystem : GameSystemBase
    {
        public const int kFramesPerDay = 262144;

        /// <summary>Frames between engine steps (the vanilla statistics period). Set before registration; the self-test shortens it.</summary>
        public static int UpdateIntervalFrames = 8192;

        /// <summary>Measurement runs: every policy delivers its target at once, so long runs show full effects.</summary>
        public static bool TestInstantDelivery;

        /// <summary>Log every step instead of once per day.</summary>
        public static bool VerboseLog;

        private CitySystem m_CitySystem;
        private SimulationSystem m_SimulationSystem;
        private BudgetInjectionSystem m_Budget;
        private CityServiceBudgetSystem m_ServiceBudget;

        // City size factor for the inter-municipal agreement and the tax base for arrears collection (§14.11), per step.
        private float m_CitySizeFactor = 1f;
        private float m_TaxBase;
        private EndFrameBarrier m_EndFrameBarrier;
        private EntityArchetype m_ModifyArchetype;
        private bool m_MigrationDue = true;
        private float[,] m_GroupGrievance;

        // Combined factor per parameter lever this step, and the factor last written.
        private readonly Dictionary<string, float> m_LeverFactors = new Dictionary<string, float>();
        private readonly Dictionary<string, float> m_LeverWritten = new Dictionary<string, float>();
        private EntityQuery m_AdminBuildingQuery;
        private ServiceSeriesCensus m_Census;
        private TaxControl m_TaxControl;
        private LitigationSystem m_Litigation;
        private float[] m_Intensity;

        /// <summary>Factor on each policy's modifiers and levers from the policy that amplifies it (§14.8), last written.</summary>
        private float[] m_Amplification;
        private readonly float[] m_Demand = new float[(int)ServiceSeries.Count];
        private readonly float[] m_Utilization = new float[(int)ServiceSeries.Count];

        // Policy report (stage 7): each policy's own staff need per series, and the frame of its last legitimacy warning.
        private float[,] m_PolicyStaff;
        private uint[] m_WarningFrame;
        private AirPollutionSystem m_AirPollutionSystem;
        private bool m_CensusDue = true;
        private uint m_LastCensusDay = uint.MaxValue;
        private uint m_LastFrame;
        private bool m_HasLastFrame;

        /// <summary>Last step's values per definition index, for logs, tests and the future report panel.</summary>
        public PolicyStepResult[] LastResults { get; private set; }

        public float LastAdminHub { get; private set; }

        /// <summary>Delivered level of environmental monitoring and emission disclosure at the last step.</summary>
        public float LastWatch { get; private set; }

        public float LastDisclosure { get; private set; }

        public PolicyEngineState LastState { get; private set; }

        public ServiceSeriesCensus Census => m_Census;

        public TaxControl Tax => m_TaxControl;

        /// <summary>Completed steps; the litigation system files suits once per step.</summary>
        public int StepCount { get; private set; }

        /// <summary>Normalized intensity 0..1 of a policy at the last step.</summary>
        public float LastIntensity(PolicyDefinition definition)
        {
            int index = System.Array.IndexOf(PolicyDefinitions.All, definition);
            return index >= 0 ? m_Intensity[index] : 0f;
        }

        /// <summary>Staff demanded from each series at the last step.</summary>
        public float[] Demand => m_Demand;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => UpdateIntervalFrames;

        /// <summary>Recount staff at the next step (a building changed series).</summary>
        public void RequestCensus()
        {
            m_CensusDue = true;
        }

        /// <summary>Switch replaced vanilla toggles over at the next step (done automatically after loading).</summary>
        public void RequestVanillaMigration()
        {
            m_MigrationDue = true;
        }

        /// <summary>Grievance of one group about one policy at the last step (loss-averse, against the reference point).</summary>
        public float GroupGrievance(int definitionIndex, TargetGroup group) => m_GroupGrievance[definitionIndex, (int)group];

        /// <summary>Staff of one series the policy needs at the last step (intensity and change surge included).</summary>
        public float PolicyStaff(int definitionIndex, ServiceSeries series) => m_PolicyStaff[definitionIndex, (int)series];

        /// <summary>Frame of the last legitimacy warning this policy caused (0 = none).</summary>
        public uint WarningFrame(int definitionIndex) => m_WarningFrame[definitionIndex];

        /// <summary>City-wide mean of the air pollution map at the last step.</summary>
        public float AirPollution { get; private set; }

        /// <summary>What the administrative reforms (§14.10) did to the administrative layer at the last step.</summary>
        public ReformEffects Reforms { get; private set; } = ReformEffects.None;

        // Emission trading market (§10.6), set at each step before the outputs.

        /// <summary>Allowance price (same units as the firm model; the pollution tax at 100% is 0.6).</summary>
        public float TradingPrice { get; private set; }

        /// <summary>Cut the cap still asks for after what the other mod policies already cut (the waterbed effect).</summary>
        public float TradingRequired { get; private set; }

        /// <summary>Industrial air pollution the other mod policies already cut.</summary>
        public float TradingOtherCut { get; private set; }

        /// <summary>Cut delivered so far under the cap.</summary>
        public float TradingCut { get; private set; }

        /// <summary>Share of companies holding allowances for what they emit.</summary>
        public float TradingCompliance { get; private set; }

        /// <summary>Share of the cap sold at auction.</summary>
        public float TradingAuctionShare { get; private set; }

        private static readonly float s_TradingMaxPrice = FirmModel.PriceForAbatement(PolicyDefinitions.kTradingMaxCut);
        private static readonly float s_TradingMaxCost = FirmModel.AbatementCost(s_TradingMaxPrice);

        // Actual burden 0..1 of the price-driven policies, from the last market update (user decision (다), 2026-11-04).
        private float m_TradingBurden;
        private float m_AuctionBurden;

        /// <summary>
        /// Whether people judge the policy by what it actually costs them rather than by its slider: emission trading
        /// (cutting costs at the allowance price) and auctioning (allowances bought). In this model the price moves only
        /// with the player's decisions (the cap and the other policies), so raising auctioning while allowances are
        /// worthless stirs nothing, and cutting the cap later stirs both.
        /// </summary>
        public static bool UsesActualBurden(PolicyDefinition definition) =>
            definition.Mechanism == Mechanism.EmissionTrading || definition.Mechanism == Mechanism.AllowanceAuction;

        /// <summary>Actual burden 0..1 of a price-driven policy (see <see cref="UsesActualBurden"/>).</summary>
        public float ActualBurden(PolicyDefinition definition) =>
            definition.Mechanism == Mechanism.EmissionTrading ? m_TradingBurden
            : definition.Mechanism == Mechanism.AllowanceAuction ? m_AuctionBurden
            : LastIntensity(definition);

        /// <summary>Actual burden per unit of slider intensity (1 for the other policies); scales damages claimed.</summary>
        public float BurdenFactor(PolicyDefinition definition)
        {
            if (!UsesActualBurden(definition))
            {
                return 1f;
            }
            float intensity = LastIntensity(definition);
            return intensity > 0f ? math.saturate(ActualBurden(definition) / intensity) : 0f;
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Budget = World.GetOrCreateSystemManaged<BudgetInjectionSystem>();
            m_ServiceBudget = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_ModifyArchetype = EntityManager.CreateArchetype(ComponentType.ReadWrite<Game.Common.Event>(), ComponentType.ReadWrite<Modify>());
            m_AdminBuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<AdminBuilding>(),
                ComponentType.ReadOnly<Efficiency>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Destroyed>());
            var groupQueries = new EntityQuery[(int)TargetGroup.Count];
            for (int g = 1; g < groupQueries.Length; g++)
            {
                EntityQueryDesc desc = ServiceSeriesCensus.GroupQueryDesc((TargetGroup)g);
                if (desc != null)
                {
                    groupQueries[g] = GetEntityQuery(desc);
                }
            }
            m_Census = new ServiceSeriesCensus(
                EntityManager,
                World.GetOrCreateSystemManaged<CityServiceBudgetSystem>(),
                World.GetOrCreateSystemManaged<CityStatisticsSystem>(),
                GetEntityQuery(ServiceSeriesCensus.BuildingQueryDesc),
                groupQueries);

            m_Litigation = World.GetOrCreateSystemManaged<LitigationSystem>();
            m_TaxControl = new TaxControl(EntityManager, World.GetOrCreateSystemManaged<TaxSystem>(), GetEntityQuery(ComponentType.ReadWrite<TaxParameterData>()));

            int count = PolicyDefinitions.All.Length;
            m_Intensity = new float[count];
            m_Amplification = new float[count];
            m_GroupGrievance = new float[count, (int)TargetGroup.Count];
            LastResults = new PolicyStepResult[count];
            m_PolicyStaff = new float[count, (int)ServiceSeries.Count];
            m_WarningFrame = new uint[count];
            m_AirPollutionSystem = World.GetOrCreateSystemManaged<AirPollutionSystem>();
        }

        [Preserve]
        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_HasLastFrame = false;
            m_CensusDue = true;
            m_MigrationDue = true;
            System.Array.Clear(m_WarningFrame, 0, m_WarningFrame.Length);
            // The previous city's money rows and parameter changes must not leak into this one.
            m_Budget.ClearRows();
            m_TaxControl.Restore();
            foreach (IParameterLever lever in ParameterLevers.All.Values)
            {
                lever.Restore(EntityManager);
            }
            m_LeverWritten.Clear();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            long start = Perf.Start();
            try
            {
                Step();
            }
            finally
            {
                Perf.Stop("Engine", start);
            }
        }

        private void Step()
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !PolicyCatalog.IsRegistered)
            {
                return;
            }

            uint frame = m_SimulationSystem.frameIndex;
            float dt = (m_HasLastFrame ? frame - m_LastFrame : (uint)UpdateIntervalFrames) / (float)kFramesPerDay;
            m_LastFrame = frame;
            m_HasLastFrame = true;
            if (dt <= 0f)
            {
                return;
            }
            // A long gap (load, pause handling) must not jump the model forward.
            dt = math.min(dt, 1f);

            PolicyDefinition[] all = PolicyDefinitions.All;
            EnsureState(city, all);
            if (m_MigrationDue)
            {
                MigrateReplacedVanilla(city, all);
                m_MigrationDue = false;
            }
            if (ReadIntensities(city, all))
            {
                m_CensusDue = true;
            }
            uint day = frame / kFramesPerDay;
            if (m_CensusDue || day != m_LastCensusDay)
            {
                // The heavy part: walks every staffed building. Daily, after loading and on policy changes only.
                m_Census.Count();
                m_CensusDue = false;
                m_LastCensusDay = day;
            }
            float[] capacity = m_Census.Capacity;
            float adminHub = AdminHub();

            PolicyEngineState state = EntityManager.GetComponentData<PolicyEngineState>(city);
            // Residential floor from social welfare staff, rates held without tax staff (§5.1).
            int population = EntityManager.TryGetComponent(city, out Population cityPopulation) ? cityPopulation.m_Population : 0;
            // Sharing services pays most for small towns (Bel & Sebő 2021): full at 50,000 people, half from 200,000.
            m_CitySizeFactor = 1f - 0.5f * math.saturate((population - 50000f) / 150000f);
            m_TaxBase = TaxBase(all);
            m_TaxControl.Apply(ref state, m_Census.Workers(ServiceSeries.Tax), m_Census.Workers(ServiceSeries.SocialWelfare), population);
            AirPollution = MeanAirPollution();
            DynamicBuffer<PolicyRuntime> runtimes = EntityManager.GetBuffer<PolicyRuntime>(city);
            state.m_Credibility += (1f - math.exp(-dt / EngineParameters.kCredibilityDays)) * (1f - state.m_Credibility);
            float surgeDecay = math.exp(-dt / EngineParameters.kSurgeDecayDays);
            // §14.10 administrative reforms, from what they delivered by the last step.
            ReformEffects reforms = ReformEffects.Compute(all, runtimes, capacity[(int)ServiceSeries.PostAndTelecom]);
            Reforms = reforms;

            // ⑥ Changes since the last step, then ④ staff demand per series.
            System.Array.Clear(m_Demand, 0, m_Demand.Length);
            for (int i = 0; i < all.Length; i++)
            {
                PolicyDefinition definition = all[i];
                int r = FindRuntime(runtimes, definition.Prefab);
                PolicyRuntime runtime = runtimes[r];
                float s = m_Intensity[i];
                float change = s - runtime.m_LastIntensity;
                float size = math.abs(change);
                runtime.m_Surge *= surgeDecay;
                if (definition.IsSlider ? size > definition.ChangeBand : size > 0f)
                {
                    float excess = size - definition.ChangeBand;
                    runtime.m_Surge += (definition.IsSlider ? definition.ChangeCost * excess * excess : definition.ToggleCost)
                        * (change < 0f ? definition.CutCostScale : 1f);
                    runtime.m_AgeDays *= 1f - EngineParameters.kCaptureResetOnRetune;
                }
                // Relaxing a rule costs credibility, unless a court or a settlement made the change. Dropping a
                // procedural reform (public hearings) does not signal weaker enforcement, so it costs none.
                if (definition.Direction == Direction.Burden && definition.Reform == ReformKind.None && change < 0f
                    && !m_Litigation.ConsumeCourtChange(definition.Prefab))
                {
                    state.m_Credibility = math.max(0f, state.m_Credibility + EngineParameters.kCredibilityLoss * change);
                }
                runtime.m_AgeDays = s > 0f ? runtime.m_AgeDays + dt : 0f;
                // Outcome baseline for the report: the city's air when the policy was switched on.
                if (s > 0f && runtime.m_LastIntensity <= 0f)
                {
                    runtime.m_BaselineAir = AirPollution;
                    runtime.m_HasBaseline = true;
                }
                runtime.m_LastIntensity = s;
                runtimes[r] = runtime;

                // Ongoing work scales with intensity; a change adds work even while switching off.
                float workload = s + runtime.m_Surge;
                for (int j = 0; j < (int)ServiceSeries.Count; j++)
                {
                    m_PolicyStaff[i, j] = 0f;
                }
                if (workload > 0f)
                {
                    float targets = m_Census.TargetCount(definition.Target);
                    foreach (StaffRequirement staff in definition.Staff)
                    {
                        float need = workload * (staff.Fixed + staff.PerUnit * targets) * reforms.StaffNeedScale;
                        m_Demand[(int)staff.Series] += need;
                        m_PolicyStaff[i, (int)staff.Series] += need;
                    }
                }
            }
            // Officials defending suits work on top of policy duties (§7 "소송 대응").
            m_Demand[(int)ServiceSeries.GeneralAdministration] += m_Litigation.OfficialStaff;
            float[] available = m_Census.AvailableStaff;
            float availableScale = reforms.AvailableStaffScale;
            for (int j = 0; j < m_Utilization.Length; j++)
            {
                m_Utilization[j] = m_Demand[j] <= 0f ? 0f
                    : available[j] > 0f ? m_Demand[j] / (available[j] * availableScale)
                    : EngineParameters.kUnstaffedUtilization;
            }

            float watch = 0f;
            float disclosure = 0f;
            for (int i = 0; i < all.Length; i++)
            {
                float progress = runtimes[FindRuntime(runtimes, all[i].Prefab)].m_Progress;
                if (all[i].Mechanism == Mechanism.Monitoring)
                {
                    watch = math.max(watch, progress);
                }
                else if (all[i].Mechanism == Mechanism.Disclosure)
                {
                    disclosure = math.max(disclosure, progress);
                }
            }
            LastWatch = watch;
            LastDisclosure = disclosure;

            float grievance = 0f;
            float delivery = 0f;
            for (int i = 0; i < all.Length; i++)
            {
                PolicyDefinition definition = all[i];
                int r = FindRuntime(runtimes, definition.Prefab);
                PolicyRuntime runtime = runtimes[r];
                float s = m_Intensity[i];

                // ③ Joint action: hub × clearance points × series capacities.
                float clearancePoints = math.max(0f, definition.ClearancePoints + reforms.ClearanceShift(definition));
                float effectiveness = adminHub * math.pow(EngineParameters.kClearanceProbability, clearancePoints) * reforms.EffectivenessScale;
                foreach (SeriesWeight series in definition.Series)
                {
                    effectiveness *= math.pow(math.min(1f, capacity[(int)series.Series]), series.Weight);
                }
                // A strong hub cannot deliver more than the policy promises.
                effectiveness = math.min(1f, effectiveness);

                // ④ The busiest series the policy needs sets its delay and rationing.
                float rho = 0f;
                ServiceSeries busiest = ServiceSeries.None;
                foreach (StaffRequirement staff in definition.Staff)
                {
                    if (busiest == ServiceSeries.None || m_Utilization[(int)staff.Series] > rho)
                    {
                        rho = m_Utilization[(int)staff.Series];
                        busiest = staff.Series;
                    }
                }
                float delayFactor = 1f / (1f - math.min(rho, EngineParameters.kMaxUtilization));
                float rationing = rho > 1f ? 1f / rho : 1f;

                // ⑦ Compliance and ⑦' capture, by mechanism (§10.5). Inspectors raise detection; disclosure raises
                // voluntary compliance only as far as breaches can be seen.
                float compliance = 1f;
                float capture = 0f;
                float detection = 1f - math.exp(-EngineParameters.kDetection * effectiveness * definition.InspectionEase
                    * (EngineParameters.kBaseInspection + EngineParameters.kWatchGain * watch));
                float voluntary = math.saturate(math.lerp(EngineParameters.kVoluntaryMin, EngineParameters.kVoluntaryMax, state.m_Legitimacy)
                    + EngineParameters.kDisclosureVoluntaryGain * disclosure * math.min(1f, detection / EngineParameters.kDisclosureReferenceDetection));
                switch (definition.Mechanism)
                {
                    case Mechanism.PerformanceStandard:
                        float cut = s * definition.SliderRange.max / 100f;
                        float deterred = FirmModel.DeterredShare(cut, detection, EngineParameters.kStandardFine);
                        compliance = (voluntary + (1f - voluntary) * deterred) * state.m_Credibility;
                        capture = Capture(runtime, reforms);
                        break;
                    case Mechanism.EmissionTax:
                    case Mechanism.EmissionTrading:
                        // Reporting rate: evaders pay on what they report, so they also face a lower effective price.
                        // Under a cap, the same share holds allowances for what it emits.
                        compliance = (voluntary + (1f - voluntary) * Deterrence(detection)) * state.m_Credibility;
                        capture = Capture(runtime, reforms);
                        break;
                    case Mechanism.Generic:
                        // Reforms bind the city's own staff, not regulated parties: no compliance or capture of their own.
                        if (definition.Instrument == Instrument.Authority && definition.Reform == ReformKind.None)
                        {
                            compliance = (voluntary + (1f - voluntary) * Deterrence(detection)) * state.m_Credibility;
                            capture = Capture(runtime, reforms);
                        }
                        break;
                }

                // ⑤ Partial adjustment toward the target with the workload-dependent lag.
                // An injunction in effect suspends the policy until the verdict.
                bool suspended = m_Litigation.IsSuspended(definition.Prefab);
                float target = suspended ? 0f : s * effectiveness * rationing * compliance * (1f - capture);
                float tau = reforms.BaseLag(definition) * delayFactor;
                runtime.m_Progress += (1f - math.exp(-dt / tau)) * (target - runtime.m_Progress);
                if (TestInstantDelivery)
                {
                    runtime.m_Progress = target;
                }

                // ⑧ Grievance against the reference point, with loss aversion, per group (option (나)).
                // Price-driven policies are judged by their actual burden against the burden people are used to.
                bool actual = UsesActualBurden(definition);
                float felt = actual ? ActualBurden(definition) : s;
                float reference = actual ? runtime.m_BurdenReference : runtime.m_Reference;
                float up = math.max(0f, felt - reference);
                // Public hearings soften the anger of those who carry a newly raised burden (§14.10).
                float burdenRaise = reforms.BurdenRaiseGrievanceScale(definition);
                float down = math.max(0f, reference - felt);
                for (int g = 0; g < (int)TargetGroup.Count; g++)
                {
                    m_GroupGrievance[i, g] = 0f;
                }
                foreach (GroupStake stake in definition.Groups)
                {
                    float losses = stake.Burden * up * burdenRaise + stake.Benefit * down;
                    float gains = stake.Burden * down + stake.Benefit * up;
                    float groupGrievance = EngineParameters.kLossAversion * losses - gains;
                    m_GroupGrievance[i, (int)stake.Group] += groupGrievance;
                    grievance += groupGrievance;
                }
                if (s > 0f)
                {
                    delivery += s * (math.min(1f, runtime.m_Progress / s) - EngineParameters.kExpectedDelivery);
                }
                // People do not get used to a policy that is in court (§6).
                if (m_Litigation.FindSuit(definition.Prefab) < 0)
                {
                    float adapt = 1f - math.exp(-dt / EngineParameters.kReferenceDays);
                    runtime.m_Reference += adapt * (s - runtime.m_Reference);
                    runtime.m_BurdenReference += adapt * (felt - runtime.m_BurdenReference);
                }
                runtimes[r] = runtime;

                LastResults[i] = new PolicyStepResult
                {
                    Detection = detection,
                    Intensity = s,
                    Effectiveness = effectiveness,
                    Utilization = rho,
                    Compliance = compliance,
                    Capture = capture,
                    Target = target,
                    Progress = runtime.m_Progress,
                    LagDays = tau,
                    BusiestSeries = busiest,
                    Voluntary = voluntary,
                };
            }

            float drop = dt * EngineParameters.kGrievanceRate * grievance;
            state.m_Legitimacy = math.saturate(state.m_Legitimacy
                - drop
                + dt * EngineParameters.kOutputRate * delivery
                - dt * EngineParameters.kLegitimacyReturnRate * (state.m_Legitimacy - (EngineParameters.kLegitimacyBase + reforms.LegitimacyBaseShift + PolicyLegitimacyShift(all))));
            if (drop > EngineParameters.kWarningDrop)
            {
                // The report's "recent warning" goes to the policy whose grievance weighs most.
                int worst = -1;
                float worstGrievance = 0f;
                for (int i = 0; i < all.Length; i++)
                {
                    float sum = 0f;
                    for (int g = 0; g < (int)TargetGroup.Count; g++)
                    {
                        sum += m_GroupGrievance[i, g];
                    }
                    if (sum > worstGrievance)
                    {
                        worstGrievance = sum;
                        worst = i;
                    }
                }
                if (worst >= 0)
                {
                    m_WarningFrame[worst] = math.max(1u, frame);
                }
                // Chirper and report-panel warnings come later; the log records it for now.
                Mod.Log.Warn($"Legitimacy warning: a decision cost {drop:0.000} legitimacy in one step.");
            }
            EntityManager.SetComponentData(city, state);

            WriteOutputs(all, runtimes);

            LastAdminHub = adminHub;
            StepCount++;
            LastState = state;
            if (VerboseLog || frame % kFramesPerDay < (uint)UpdateIntervalFrames)
            {
                Log(all, state, adminHub);
            }
        }

        private void EnsureState(Entity city, PolicyDefinition[] all)
        {
            if (!EntityManager.HasComponent<PolicyEngineState>(city))
            {
                EntityManager.AddComponentData(city, PolicyEngineState.Default);
            }
            if (!EntityManager.HasBuffer<PolicyRuntime>(city))
            {
                EntityManager.AddBuffer<PolicyRuntime>(city);
            }
            DynamicBuffer<PolicyRuntime> runtimes = EntityManager.GetBuffer<PolicyRuntime>(city);
            foreach (PolicyDefinition definition in all)
            {
                if (definition.Prefab != Entity.Null && FindRuntime(runtimes, definition.Prefab) < 0)
                {
                    runtimes.Add(new PolicyRuntime { m_Policy = definition.Prefab });
                }
            }
        }

        /// <summary>Reads each mod policy's intensity; returns true when any changed since the last step.</summary>
        private bool ReadIntensities(Entity city, PolicyDefinition[] all)
        {
            DynamicBuffer<Policy> policies = EntityManager.GetBuffer<Policy>(city, true);
            bool changed = false;
            for (int i = 0; i < all.Length; i++)
            {
                float intensity = 0f;
                for (int j = 0; j < policies.Length; j++)
                {
                    if (policies[j].m_Policy == all[i].Prefab)
                    {
                        intensity = all[i].Intensity((policies[j].m_Flags & PolicyFlags.Active) != 0, policies[j].m_Adjustment);
                        break;
                    }
                }
                changed |= intensity != m_Intensity[i];
                m_Intensity[i] = intensity;
            }
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].RequiresPolicy == null || m_Intensity[i] == 0f)
                {
                    continue;
                }
                int required = System.Array.FindIndex(all, d => d.Id == all[i].RequiresPolicy);
                if (required >= 0 && m_Intensity[required] <= 0f)
                {
                    changed = true;
                    m_Intensity[i] = 0f;
                }
            }
            return changed;
        }

        /// <summary>② Weberian hub: average efficiency of administration buildings.</summary>
        private float AdminHub()
        {
            using (NativeArray<Entity> buildings = m_AdminBuildingQuery.ToEntityArray(Allocator.Temp))
            {
                if (buildings.Length == 0)
                {
                    return EngineParameters.kAdminBase;
                }
                float sum = 0f;
                for (int i = 0; i < buildings.Length; i++)
                {
                    sum += BuildingUtils.GetEfficiency(EntityManager.GetBuffer<Efficiency>(buildings[i], true));
                }
                return EngineParameters.kAdminBase + EngineParameters.kAdminEfficiencyGain * (sum / buildings.Length);
            }
        }

        /// <summary>
        /// Writes every output, only where the value changed: city modifiers as a fixed range on the prefab (vanilla
        /// applies and stacks them), parameter levers as original × Π(1 + effect) over the policies using them, and
        /// money as budget rows.
        /// </summary>
        private void WriteOutputs(PolicyDefinition[] all, DynamicBuffer<PolicyRuntime> runtimes)
        {
            UpdateTradingMarket(all, runtimes);
            m_LeverFactors.Clear();
            for (int i = 0; i < all.Length; i++)
            {
                float amplification = 1f;
                string by = all[i].AmplifiedBy;
                if (by != null)
                {
                    for (int j = 0; j < all.Length; j++)
                    {
                        if (all[j].Id == by)
                        {
                            amplification += all[i].AmplifyScale * math.saturate(runtimes[FindRuntime(runtimes, all[j].Prefab)].m_Progress);
                            break;
                        }
                    }
                }
                m_Amplification[i] = amplification;
            }
            for (int i = 0; i < all.Length; i++)
            {
                PolicyDefinition definition = all[i];
                float progress = runtimes[FindRuntime(runtimes, definition.Prefab)].m_Progress;
                float intensity = m_Intensity[i];
                if (EntityManager.HasBuffer<CityModifierData>(definition.Prefab))
                {
                    DynamicBuffer<CityModifierData> modifiers = EntityManager.GetBuffer<CityModifierData>(definition.Prefab);
                    for (int m = 0; m < modifiers.Length; m++)
                    {
                        CityModifierData data = modifiers[m];
                        float amplified = definition.AmplifyOnly == null || definition.AmplifyOnly == data.m_Type ? m_Amplification[i] : 1f;
                        float value = ModifierFor(definition, data.m_Type) * Shape(definition, data.m_Type, progress, intensity, LastResults[i].Compliance) * amplified;
                        if (math.abs(data.m_Range.min - value) > 1e-5f || math.abs(data.m_Range.max - value) > 1e-5f)
                        {
                            data.m_Range = new Bounds1(value, value);
                            modifiers[m] = data;
                        }
                    }
                }
                foreach (PolicyOutput output in definition.Outputs)
                {
                    switch (output.Kind)
                    {
                        case OutputKind.Parameter:
                            if (!m_LeverFactors.TryGetValue(output.Key, out float factor))
                            {
                                factor = 1f;
                            }
                            float age = runtimes[FindRuntime(runtimes, definition.Prefab)].m_AgeDays;
                            m_LeverFactors[output.Key] = factor * (1f + output.FullEffect * LeverShape(definition, progress, intensity) * Fading(definition, age) * LeverAmplification(i));
                            break;
                        case OutputKind.Income:
                        case OutputKind.Expense:
                            float basis = output.Basis == MoneyBasis.Intensity ? intensity
                                : output.Basis == MoneyBasis.Delivery || output.Basis == MoneyBasis.TaxShare ? progress
                                : MoneyShape(definition, progress, intensity, LastResults[i].Compliance);
                            float monthly = output.Basis == MoneyBasis.TaxShare
                                ? output.PerUnitMonthly * m_TaxBase * basis
                                : (output.FixedMonthly + output.PerUnitMonthly * m_Census.TargetCount(output.Unit)) * basis;
                            m_Budget.SetRow(output.Key, output.Kind == OutputKind.Income, output.Host, (int)math.round(monthly));
                            break;
                    }
                }
            }
            foreach (KeyValuePair<string, IParameterLever> entry in ParameterLevers.All)
            {
                if (!m_LeverFactors.TryGetValue(entry.Key, out float factor))
                {
                    factor = 1f;
                }
                if (m_LeverWritten.TryGetValue(entry.Key, out float written) && math.abs(written - factor) < 1e-6f)
                {
                    continue;
                }
                if (entry.Value.Apply(EntityManager, factor))
                {
                    m_LeverWritten[entry.Key] = factor;
                }
            }
        }

        /// <summary>Mean of the air pollution map (a small grid, read once per step).</summary>
        private float MeanAirPollution()
        {
            NativeArray<AirPollution> map = m_AirPollutionSystem.GetMap(true, out Unity.Jobs.JobHandle dependencies);
            dependencies.Complete();
            long sum = 0;
            for (int c = 0; c < map.Length; c++)
            {
                sum += map[c].m_Pollution;
            }
            m_AirPollutionSystem.AddReader(default);
            return map.Length > 0 ? sum / (float)map.Length : 0f;
        }

        private static float Capture(PolicyRuntime runtime, ReformEffects reforms) =>
            EngineParameters.kCaptureMax * reforms.CaptureCeilingScale
            * (1f - math.exp(-runtime.m_AgeDays / (EngineParameters.kCaptureDays * reforms.CaptureTimeScale)));

        /// <summary>Becker deterrence for the generic compliance rule and tax reporting.</summary>
        private static float Deterrence(float detection) =>
            1f / (1f + math.pow(1f / math.max(detection * EngineParameters.kFineToMedianGain, 1e-4f), EngineParameters.kGainSpread));

        /// <summary>
        /// Emission trading (§10.6): the cap asks for a cut of industrial air pollution; what the other mod policies
        /// already cut counts against it (waterbed effect, Perino 2018), and the allowance price is the one at which
        /// companies cut the rest (Montgomery 1972). Evaders and the policy's delay lower the delivered cut.
        /// </summary>
        private void UpdateTradingMarket(PolicyDefinition[] all, DynamicBuffer<PolicyRuntime> runtimes)
        {
            int trading = System.Array.FindIndex(all, d => d.Mechanism == Mechanism.EmissionTrading);
            int auction = System.Array.FindIndex(all, d => d.Mechanism == Mechanism.AllowanceAuction);
            TradingPrice = 0f;
            TradingRequired = 0f;
            TradingCut = 0f;
            TradingOtherCut = 0f;
            TradingCompliance = trading >= 0 ? LastResults[trading].Compliance : 0f;
            TradingAuctionShare = auction >= 0 ? m_Intensity[auction] : 0f;
            m_TradingBurden = 0f;
            m_AuctionBurden = 0f;
            if (trading < 0 || m_Intensity[trading] <= 0f)
            {
                return;
            }
            float other = 0f;
            for (int i = 0; i < all.Length; i++)
            {
                float air = ModifierFor(all[i], CityModifierType.IndustrialAirPollution);
                if (i == trading || air == 0f)
                {
                    continue;
                }
                float progress = runtimes[FindRuntime(runtimes, all[i].Prefab)].m_Progress;
                other -= air * Shape(all[i], CityModifierType.IndustrialAirPollution, progress, m_Intensity[i], LastResults[i].Compliance);
            }
            TradingOtherCut = math.saturate(other);
            float intensity = m_Intensity[trading];
            TradingRequired = math.max(0f, intensity * PolicyDefinitions.kTradingMaxCut - TradingOtherCut);
            TradingPrice = FirmModel.PriceForAbatement(TradingRequired);
            float delivered = math.saturate(runtimes[FindRuntime(runtimes, all[trading].Prefab)].m_Progress / intensity);
            TradingCut = TradingRequired * delivered;
            // Companies feel the cap by its cutting cost, and auctioning by the allowances they buy (both against the
            // largest cap); used for grievance at the next step.
            m_TradingBurden = math.saturate(FirmModel.AbatementCost(TradingPrice) / s_TradingMaxCost);
            m_AuctionBurden = math.saturate(TradingAuctionShare * TradingPrice / s_TradingMaxPrice);
        }

        /// <summary>
        /// The city's own tax income per month, without the mod's income rows booked under tax sources (so arrears
        /// collection does not compound on itself or on the pollution tax).
        /// </summary>
        private float TaxBase(PolicyDefinition[] all)
        {
            float tax = m_ServiceBudget.GetIncome(IncomeSource.TaxResidential) + m_ServiceBudget.GetIncome(IncomeSource.TaxCommercial)
                + m_ServiceBudget.GetIncome(IncomeSource.TaxIndustrial) + m_ServiceBudget.GetIncome(IncomeSource.TaxOffice);
            foreach (PolicyDefinition definition in all)
            {
                foreach (PolicyOutput output in definition.Outputs)
                {
                    if (output.Kind == OutputKind.Income && (output.Host == (int)IncomeSource.TaxResidential || output.Host == (int)IncomeSource.TaxCommercial
                        || output.Host == (int)IncomeSource.TaxIndustrial || output.Host == (int)IncomeSource.TaxOffice))
                    {
                        tax -= m_Budget.GetRow(output.Key);
                    }
                }
            }
            return math.max(0f, tax);
        }

        /// <summary>Share of the effect left after <paramref name="ageDays"/> in force, for policies whose novelty wears off.</summary>
        public static float Fading(PolicyDefinition definition, float ageDays) =>
            definition.EffectHalfLifeDays > 0f
                ? math.max(definition.EffectFloor, math.pow(0.5f, ageDays / definition.EffectHalfLifeDays))
                : 1f;

        /// <summary>Share of a parameter lever's full effect: allowance auctioning by what companies pay; others by delivery.</summary>
        private float LeverShape(PolicyDefinition definition, float progress, float intensity) =>
            definition.Mechanism == Mechanism.AllowanceAuction ? intensity * math.min(1f, TradingPrice / FirmModel.kBasePrice) : progress;

        /// <summary>
        /// Share of a modifier's full value at the current delivery. Prices (tax, subsidy) work through the firm model:
        /// delivery is the effective price relative to the base price, pollution follows the companies' cuts and the
        /// tax's efficiency loss follows their cutting costs plus part of the bill. Everything else is linear.
        /// </summary>
        private float Shape(PolicyDefinition definition, CityModifierType type, float progress, float intensity, float reporting)
        {
            if (definition.Reform == ReformKind.InterMunicipalAgreement)
            {
                return progress * m_CitySizeFactor;
            }
            if (definition.ScalesWithTelecom)
            {
                return progress * Reforms.PostCapacity;
            }
            if (definition.ScalesWithLegitimacy)
            {
                return progress * LegitimacyFactor;
            }
            if (definition.Mechanism == Mechanism.EmissionTrading)
            {
                if (type == CityModifierType.IndustrialAirPollution)
                {
                    return TradingCut / PolicyDefinitions.kTradingMaxCut;
                }
                if (type == CityModifierType.IndustrialEfficiency)
                {
                    // Cutting costs plus the allowances bought at auction, against the same at the largest cap, all auctioned.
                    float full = FirmModel.AbatementCost(s_TradingMaxPrice)
                        + EngineParameters.kTaxBillEfficiencyBurden * s_TradingMaxPrice * (1f - PolicyDefinitions.kTradingMaxCut);
                    float now = FirmModel.AbatementCost(FirmModel.PriceForAbatement(TradingCut))
                        + EngineParameters.kTaxBillEfficiencyBurden * TradingAuctionShare * TradingPrice * (1f - TradingCut) * TradingCompliance;
                    return now / full;
                }
                return progress;
            }
            if (definition.Mechanism != Mechanism.EmissionTax && definition.Mechanism != Mechanism.AbatementSubsidy)
            {
                return progress;
            }
            float price = progress * FirmModel.kBasePrice;
            if (type == CityModifierType.IndustrialEfficiency)
            {
                float full = FirmModel.AbatementCost(FirmModel.kBasePrice)
                    + EngineParameters.kTaxBillEfficiencyBurden * FirmModel.kBasePrice * (1f - FirmModel.Abatement(FirmModel.kBasePrice));
                float now = FirmModel.AbatementCost(price)
                    + EngineParameters.kTaxBillEfficiencyBurden * intensity * FirmModel.kBasePrice * (1f - FirmModel.Abatement(price)) * reporting;
                return now / full;
            }
            if (type == CityModifierType.IndustrialAirPollution || type == CityModifierType.IndustrialGroundPollution)
            {
                return FirmModel.Abatement(price) / FirmModel.Abatement(FirmModel.kBasePrice);
            }
            return progress;
        }

        /// <summary>Per-unit multiplier for mechanism money: tax on reported remaining emissions, subsidy per unit cut.</summary>
        private float MoneyShape(PolicyDefinition definition, float progress, float intensity, float reporting)
        {
            float cut = FirmModel.Abatement(progress * FirmModel.kBasePrice);
            switch (definition.Mechanism)
            {
                case Mechanism.EmissionTax:
                    return intensity * (1f - cut) * reporting;
                case Mechanism.AbatementSubsidy:
                    return intensity * cut;
                case Mechanism.AllowanceAuction:
                    // Auction share × allowance price (in tax units) × allowances companies hold for what they still emit.
                    return intensity * (TradingPrice / FirmModel.kBasePrice) * (1f - TradingCut) * TradingCompliance;
                default:
                    return progress;
            }
        }

        /// <summary>Factor the engine last wrote on a parameter lever (1 when no policy uses it).</summary>
        public float LeverFactor(string key) => m_LeverFactors.TryGetValue(key, out float factor) ? factor : 1f;

        /// <summary>Amplification of a policy's levers (1 when it amplifies only one modifier).</summary>
        public float LeverAmplification(int index) => PolicyDefinitions.All[index].AmplifyOnly == null ? Amplification(index) : 1f;

        /// <summary>Legitimacy against its base, 0.5-1.2 times (§14.8 emergency response).</summary>
        public float LegitimacyFactor => math.clamp(LastState.m_Legitimacy / EngineParameters.kLegitimacyBase, 0.5f, 1.2f);

        /// <summary>Factor on a policy's modifiers and levers from the policy that amplifies it (§14.8); 1 for most.</summary>
        public float Amplification(int index) => m_Amplification != null && index >= 0 && index < m_Amplification.Length && m_Amplification[index] > 0f ? m_Amplification[index] : 1f;

        /// <summary>Legitimacy base shift of the policies that carry one, at what they delivered this step.</summary>
        private float PolicyLegitimacyShift(PolicyDefinition[] all)
        {
            float shift = 0f;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].LegitimacyShift != 0f)
                {
                    shift += all[i].LegitimacyShift * math.saturate(LastResults[i].Progress);
                }
            }
            return shift;
        }

        private static float ModifierFor(PolicyDefinition definition, CityModifierType type)
        {
            foreach (PolicyOutput output in definition.Outputs)
            {
                if (output.Kind == OutputKind.CityModifier && output.Modifier == type)
                {
                    return output.FullEffect;
                }
            }
            return 0f;
        }

        /// <summary>
        /// A city saved with a vanilla toggle that a mod policy replaces: turn the vanilla toggle off and the mod
        /// policy on, through the same end-of-frame Modify events the policy panel uses.
        /// </summary>
        private void MigrateReplacedVanilla(Entity city, PolicyDefinition[] all)
        {
            DynamicBuffer<Policy> policies = EntityManager.GetBuffer<Policy>(city, true);
            EntityCommandBuffer buffer = default;
            bool hasBuffer = false;
            foreach (PolicyDefinition definition in all)
            {
                if (definition.ReplacedPrefab == Entity.Null || !IsActive(policies, definition.ReplacedPrefab))
                {
                    continue;
                }
                if (!hasBuffer)
                {
                    buffer = m_EndFrameBarrier.CreateCommandBuffer();
                    hasBuffer = true;
                }
                Entity off = buffer.CreateEntity(m_ModifyArchetype);
                buffer.SetComponent(off, new Modify(city, definition.ReplacedPrefab, false, 0f));
                if (!IsActive(policies, definition.Prefab))
                {
                    Entity on = buffer.CreateEntity(m_ModifyArchetype);
                    buffer.SetComponent(on, new Modify(city, definition.Prefab, true, definition.SliderDefault));
                }
                Mod.Log.Info($"Migration: '{definition.ReplacesVanilla}' was on; switched to {definition.Id}.");
            }
        }

        private static bool IsActive(DynamicBuffer<Policy> policies, Entity policy)
        {
            for (int i = 0; i < policies.Length; i++)
            {
                if (policies[i].m_Policy == policy)
                {
                    return (policies[i].m_Flags & PolicyFlags.Active) != 0;
                }
            }
            return false;
        }

        private static int FindRuntime(DynamicBuffer<PolicyRuntime> runtimes, Entity policy)
        {
            for (int i = 0; i < runtimes.Length; i++)
            {
                if (runtimes[i].m_Policy == policy)
                {
                    return i;
                }
            }
            return -1;
        }

        private void Log(PolicyDefinition[] all, PolicyEngineState state, float adminHub)
        {
            float[] available = m_Census.AvailableStaff;
            Mod.Log.Info($"Engine: legitimacy {state.m_Legitimacy:0.000}, credibility {state.m_Credibility:0.00}, hub {adminHub:0.00}, " +
                $"households {m_Census.TargetCount(TargetGroup.Households)}, industrial {m_Census.TargetCount(TargetGroup.IndustrialCompanies)}, " +
                $"commercial {m_Census.TargetCount(TargetGroup.CommercialCompanies)}, office {m_Census.TargetCount(TargetGroup.OfficeCompanies)}, " +
                $"general administration {m_Demand[(int)ServiceSeries.GeneralAdministration]:0.0}/{available[(int)ServiceSeries.GeneralAdministration]:0.0}, " +
                $"environment {m_Demand[(int)ServiceSeries.Environment]:0.0}/{available[(int)ServiceSeries.Environment]:0.0} (demand/available staff), " +
                $"watch {LastWatch:0.00}, disclosure {LastDisclosure:0.00}");
            for (int i = 0; i < all.Length; i++)
            {
                PolicyStepResult r = LastResults[i];
                Mod.Log.Info($"  {all[i].Id}: s {r.Intensity:0.00}, E {r.Effectiveness:0.000}, utilization {r.Utilization:0.00}, detection {r.Detection:0.00}, compliance {r.Compliance:0.00}, capture {r.Capture:0.00}, target {r.Target:0.000}, progress {r.Progress:0.000}, lag {r.LagDays * 24f:0.0} h");
            }
        }

        [Preserve]
        public PolicyEngineSystem()
        {
        }
    }

    public struct PolicyStepResult
    {
        public float Detection;
        public float Intensity;
        public float Effectiveness;
        public float Utilization;
        public float Compliance;
        public float Capture;
        public float Target;
        public float Progress;
        public float LagDays;

        /// <summary>The series whose workload sets the policy's delay and rationing.</summary>
        public ServiceSeries BusiestSeries;

        /// <summary>Share of companies that comply of their own accord (legitimacy and disclosure).</summary>
        public float Voluntary;
    }
}
