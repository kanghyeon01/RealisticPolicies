using System.Collections.Generic;
using Colossal.Entities;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Economy;
using Game.Events;
using Game.Net;
using Game.Policies;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// Protests and riots (docs/PLAN.md §12). Every update:
    /// 1. protest pressure: benefit spending cut and residents' burden raised, in money against a slow reference point
    ///    (τ = 12 days), as a share of the city's income; burden and residential tax rises count 0.3;
    /// 2. a new protest starts with yearly rate 1.4 × (e^(0.21 P) − 1) × legitimacy factor, in a park near where the
    ///    aggrieved live (on their own road when none is near), with a character (peaceful, general, hardline, extreme)
    ///    fixed for its life;
    /// 3. each protest's size and intensity move hour by hour (early escalation, base decay, the threshold-model target,
    ///    concession and repression), it spreads along roads at 10% of residents and becomes a riot at intensity 0.6;
    /// 4. riots raise building crime to the intensity, start fires, and cost legitimacy when left alone; repression costs
    ///    legitimacy by (1 − intensity)²; roads are closed and police posted in <see cref="ProtestSystem"/>'s road part.
    /// </summary>
    public sealed partial class ProtestSystem : GameSystemBase
    {
        public const int kIntervalFrames = 2048;
        private const float kFramesPerHour = PolicyEngineSystem.kFramesPerDay / 24f;

        // Pressure and start (§12 "첫 시위", 2026-11-08/09 user decisions).
        private const float kReferenceDays = 12f;
        private const float kBurdenWeight = 0.3f;
        private const float kBaseRate = 1.4f;
        private const float kRateSlope = 0.21f;
        private const float kSurpriseB = 2f;
        private const float kMixedFactor = 1.5f;

        // Dynamics (§12 "세기의 변화", "초기 고조"), per hour.
        private const float kEscalation = 0.2f;
        private const float kEscalationHalfLifeHours = 3f;
        private const float kProtestDecay = 0.04f;
        private const float kRiotDecay = 0.10f;
        private const float kRiotThreshold = 0.6f;
        private const float kSpreadSize = 0.10f;
        private const float kStartSize = 0.05f;
        private const float kDissolveSize = 0.02f;
        private const float kViolenceSizeLoss = 0.12f;
        private const float kRepressionMemoryHours = 6f;
        private const int kMaxSegments = 12;
        private const int kClosureParticipants = 1000;

        // Riot damage per riot segment and hour, times intensity (§12): arson 0.15 (starting value), crimes 7 per fire.
        private const float kRiotArson = 0.15f;
        private const float kRiotCrimesPerArson = 7f;

        // Opinion effect (§12 "시위의 여론 효과", 2026-11-09 user decision: by protest or riot, not by character): a protest
        // draws opinion toward its cause, about +2 points (Wasow 2020, Madestam et al. 2013); a riot pushes it toward order
        // (Wasow 2020) while the rioting group itself holds together more (Enos et al. 2019, size from estimate).
        private const float kProtestOpinion = 1.02f;
        private const float kRiotOpinion = 0.98f;
        private const float kRiotCohesion = 1.10f;

        // Where it gathers (§12, 2026-11-02 user decisions): parks within twice the 128 m grievance radius, by Huff.
        private const float kPlaceSearchRadius = 256f;
        private const float kCellSize = 8f;

        // Legitimacy per hour (§12 "대응").
        private const float kWatchLegitimacy = 0.002f;
        private const float kDisperseLegitimacy = 0.01f;
        private const float kIgnoredRiotLegitimacy = 0.01f;

        // Character tables: size growth, intensity growth, intensity added by repression, reaction to concession, start intensity.
        private static readonly float[] kSizeGrowth = { 1.5f, 1f, 0.8f, 0.5f };
        private static readonly float[] kIntensityGrowth = { 0.2f, 0.6f, 1.2f, 2f };
        private static readonly float[] kRepressionIntensity = { 0.02f, 0.04f, 0.08f, 0.15f };
        private static readonly float[] kConcessionReaction = { 1f, 0.8f, 0.5f, 0.2f };
        private static readonly float[] kStartIntensity = { 0.1f, 0.3f, 0.4f, 0.5f };
        private static readonly float[] kBaseTraitChance = { 0.2f, 0.5f, 0.2f, 0.1f };

        private CitySystem m_CitySystem;
        private SimulationSystem m_SimulationSystem;
        private TimeSystem m_TimeSystem;
        private PolicyEngineSystem m_Engine;
        private BudgetInjectionSystem m_Budget;
        private CityServiceBudgetSystem m_ServiceBudget;
        private TaxSystem m_TaxSystem;
        private EndFrameBarrier m_EndFrameBarrier;
        private EntityArchetype m_ModifyArchetype;
        private EntityQuery m_HouseholdQuery;
        private EntityQuery m_FirePrefabQuery;
        private EntityQuery m_CrimePrefabQuery;
        private CityStatisticsSystem m_Statistics;

        /// <summary>Riot crimes started through the game's crime event since the city was loaded (self-test, logs).</summary>
        public int RiotCrimes { get; private set; }

        /// <summary>Rioters made criminals since the city was loaded (self-test: where their crime happens).</summary>
        internal readonly List<Entity> RiotCriminals = new List<Entity>();
        private EntityQuery m_PoliceConfigQuery;
        private EntityQuery m_ParkQuery;
        private ProtestNotifications m_Notifications;

        private uint m_LastFrame;
        private bool m_HasLastFrame;

        /// <summary>Pressure per policy (index of PolicyDefinitions.All) in % of monthly income, and for the residential tax.</summary>
        private float[] m_PolicyPressure;
        private float m_TaxPressure;

        /// <summary>Total pressure that drives new protests (policies already protested left out), % of monthly income.</summary>
        public float LastPressure { get; private set; }

        /// <summary>Expected new protests per game year at the last update.</summary>
        public float LastYearlyRate { get; private set; }

        /// <summary>Residents' grievance per household weight at the last update, for the grievance map.</summary>
        public float LastGrievanceLevel { get; private set; }

        /// <summary>Force a protest at the next update (self-test and debugging) over the largest pressure.</summary>
        public static bool TestStartProtest;

        /// <summary>Self-test: the forced protest's demand is this policy instead of the largest pressure.</summary>
        internal static Entity TestProtestPolicy = Entity.Null;

        /// <summary>Self-test: the forced protest is about the residential tax.</summary>
        internal static bool TestProtestTax;

        /// <summary>Self-test: the next protest finds no park (starts on the residents' road).</summary>
        internal static bool TestNoPark;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => kIntervalFrames;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            m_Engine = World.GetOrCreateSystemManaged<PolicyEngineSystem>();
            m_Budget = World.GetOrCreateSystemManaged<BudgetInjectionSystem>();
            m_ServiceBudget = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            m_TaxSystem = World.GetOrCreateSystemManaged<TaxSystem>();
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_ModifyArchetype = EntityManager.CreateArchetype(ComponentType.ReadWrite<Game.Common.Event>(), ComponentType.ReadWrite<Modify>());
            m_HouseholdQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<HouseholdCitizen>(), ComponentType.ReadOnly<PropertyRenter>(), ComponentType.ReadOnly<Resources>() },
                None = new[]
                {
                    ComponentType.ReadOnly<TouristHousehold>(), ComponentType.ReadOnly<CommuterHousehold>(), ComponentType.ReadOnly<Game.Agents.MovingAway>(),
                    ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(),
                },
            });
            m_FirePrefabQuery = GetEntityQuery(ComponentType.ReadOnly<EventData>(), ComponentType.ReadOnly<FireData>(), ComponentType.Exclude<Locked>());
            m_CrimePrefabQuery = GetEntityQuery(ComponentType.ReadOnly<EventData>(), ComponentType.ReadOnly<CrimeData>(), ComponentType.Exclude<Locked>());
            m_Statistics = World.GetOrCreateSystemManaged<CityStatisticsSystem>();
            m_PoliceConfigQuery = GetEntityQuery(ComponentType.ReadOnly<PoliceConfigurationData>());
            m_ParkQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Buildings.Park>(), ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Common.Destroyed>() },
            });
            m_Notifications = new ProtestNotifications(EntityManager,
                World.GetOrCreateSystemManaged<Game.Notifications.IconCommandSystem>(),
                World.GetOrCreateSystemManaged<Game.Triggers.CreateChirpSystem>());
            m_PolicyPressure = new float[PolicyDefinitions.All.Length];
            CreateRoadQueries();
            CreateGatheringQueries();
        }

        [Preserve]
        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_HasLastFrame = false;
            m_Notifications.Reset();
            ResetRoads();
            ModCleanup.Reset();
            RiotCrimes = 0;
            RiotCriminals.Clear();
            PublicStaffLoss = 0f;
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
                Perf.Stop("Protests", start);
            }
        }

        private void Step()
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !PolicyCatalog.IsRegistered || !EntityManager.HasComponent<PolicyEngineState>(city) || m_Engine.StepCount == 0
                || ModCleanup.Done)
            {
                return;
            }
            uint frame = m_SimulationSystem.frameIndex;
            float dtFrames = m_HasLastFrame ? frame - m_LastFrame : kIntervalFrames;
            m_LastFrame = frame;
            m_HasLastFrame = true;
            if (dtFrames <= 0f)
            {
                return;
            }
            // A long gap (load, pause handling) must not jump protests forward.
            dtFrames = math.min(dtFrames, kIntervalFrames * 4f);
            float dtDays = dtFrames / PolicyEngineSystem.kFramesPerDay;
            float dtHours = dtFrames / kFramesPerHour;

            EnsureState(city);
            // Each part timed on its own (performance review 2026-11-09), read by the self-test as "Protests.*".
            long part = Perf.Start();
            UpdatePressure(city, frame, dtDays);
            Perf.Stop("Protests.Pressure", part);
            part = Perf.Start();
            ConsiderStart(city, frame, dtDays);
            Perf.Stop("Protests.Start", part);
            part = Perf.Start();
            UpdateProtests(city, frame, dtHours);
            UpdateIndustrialActions(city, frame, dtDays, dtHours);
            Perf.Stop("Protests.Dynamics", part);
            part = Perf.Start();
            UpdateRoads(city, frame);
            Perf.Stop("Protests.Roads", part);
            part = Perf.Start();
            UpdateGatherings(city);
            Perf.Stop("Protests.Gatherings", part);
            m_Notifications.Refresh(EntityManager.GetBuffer<Protest>(city, true));
        }

        private void EnsureState(Entity city)
        {
            if (!EntityManager.HasComponent<ProtestCityState>(city))
            {
                EntityManager.AddComponentData(city, new ProtestCityState { m_NextId = 1 });
            }
            if (!EntityManager.HasBuffer<Protest>(city))
            {
                EntityManager.AddBuffer<Protest>(city);
            }
            if (!EntityManager.HasBuffer<ProtestEdge>(city))
            {
                EntityManager.AddBuffer<ProtestEdge>(city);
            }
            if (!EntityManager.HasBuffer<ClosedLane>(city))
            {
                EntityManager.AddBuffer<ClosedLane>(city);
            }
            if (!EntityManager.HasBuffer<PolicePost>(city))
            {
                EntityManager.AddBuffer<PolicePost>(city);
            }
            if (!EntityManager.HasBuffer<ProtestPressure>(city))
            {
                EntityManager.AddBuffer<ProtestPressure>(city);
            }
            if (!EntityManager.HasBuffer<RepressedArea>(city))
            {
                EntityManager.AddBuffer<RepressedArea>(city);
            }
            if (!EntityManager.HasBuffer<ProtestSpot>(city))
            {
                EntityManager.AddBuffer<ProtestSpot>(city);
            }
            if (!EntityManager.HasBuffer<ProtestMarcher>(city))
            {
                EntityManager.AddBuffer<ProtestMarcher>(city);
            }
            if (!EntityManager.HasBuffer<IndustrialAction>(city))
            {
                EntityManager.AddBuffer<IndustrialAction>(city);
            }
        }

        // ------------------------------------------------------------------ pressure

        /// <summary>Monthly city income (all sources), at least 1.</summary>
        private float MonthlyIncome() => math.max(1f, m_ServiceBudget.GetTotalIncome());

        /// <summary>A policy's budget spending per month (its expense rows as booked).</summary>
        private float Spending(PolicyDefinition definition)
        {
            float monthly = 0f;
            foreach (PolicyOutput output in definition.Outputs)
            {
                if (output.Kind == OutputKind.Expense)
                {
                    monthly += m_Budget.GetRow(output.Key);
                }
            }
            return monthly;
        }

        /// <summary>What a policy costs residents per month in money: half the residential tax per unit of their burden (the lawsuit rule).</summary>
        private float ResidentBurden(PolicyDefinition definition)
        {
            float stake = 0f;
            foreach (GroupStake group in definition.Groups)
            {
                if (group.Group == TargetGroup.Households)
                {
                    stake += group.Burden;
                }
            }
            if (stake <= 0f)
            {
                return 0f;
            }
            float felt = PolicyEngineSystem.UsesActualBurden(definition) ? m_Engine.ActualBurden(definition) : m_Engine.LastIntensity(definition);
            return 0.5f * m_ServiceBudget.GetIncome(IncomeSource.TaxResidential) * stake * felt;
        }

        /// <summary>While a protest over this demand goes on: opinion toward it (protest) or toward order (riot); 1 otherwise.</summary>
        private static float OpinionFactor(DynamicBuffer<Protest> protests, Entity policy)
        {
            for (int i = 0; i < protests.Length; i++)
            {
                if (protests[i].m_Policy == policy)
                {
                    return protests[i].m_Riot ? kRiotOpinion : kProtestOpinion;
                }
            }
            return 1f;
        }

        private void UpdatePressure(Entity city, uint frame, float dtDays)
        {
            PolicyDefinition[] all = PolicyDefinitions.All;
            DynamicBuffer<ProtestPressure> pressures = EntityManager.GetBuffer<ProtestPressure>(city);
            DynamicBuffer<Protest> protests = EntityManager.GetBuffer<Protest>(city, true);
            float income = MonthlyIncome();
            float adapt = 1f - math.exp(-dtDays / kReferenceDays);
            float total = 0f;
            float grievance = 0f;
            for (int i = 0; i < all.Length; i++)
            {
                PolicyDefinition definition = all[i];
                m_PolicyPressure[i] = 0f;
                if (definition.Prefab == Entity.Null)
                {
                    continue;
                }
                float spend = Spending(definition);
                float burden = ResidentBurden(definition);
                float intensity = m_Engine.LastIntensity(definition);
                int p = FindPressure(pressures, definition.Prefab);
                if (p < 0)
                {
                    // First sight (new city, mod just added, new policy): people are used to things as they are.
                    pressures.Add(new ProtestPressure { m_Policy = definition.Prefab, m_SpendRef = spend, m_BurdenRef = burden, m_IntensityRef = intensity, m_SurpriseFactor = 1f });
                    continue;
                }
                ProtestPressure pressure = pressures[p];
                float cut = definition.Direction == Direction.Benefit ? math.max(0f, pressure.m_SpendRef - spend) : 0f;
                float raised = math.max(0f, burden - pressure.m_BurdenRef);
                float money = cut + kBurdenWeight * raised;
                if (pressure.m_SurpriseUntil > frame)
                {
                    money *= math.max(1f, pressure.m_SurpriseFactor);
                }
                m_PolicyPressure[i] = 100f * money / income * OpinionFactor(protests, definition.Prefab);
                if (!HasProtest(protests, definition.Prefab))
                {
                    total += m_PolicyPressure[i];
                }
                grievance += math.max(0f, m_Engine.GroupGrievance(i, TargetGroup.Households));
                pressure.m_SpendRef += adapt * (spend - pressure.m_SpendRef);
                pressure.m_BurdenRef += adapt * (burden - pressure.m_BurdenRef);
                pressure.m_IntensityRef += adapt * (intensity - pressure.m_IntensityRef);
                pressures[p] = pressure;
            }

            // Residential tax raised (2026-11-08 user decision): the extra tax at the higher rate, against the rate people are used to.
            ProtestCityState state = EntityManager.GetComponentData<ProtestCityState>(city);
            float rate = m_TaxSystem.GetTaxRate(TaxAreaType.Residential);
            if (!state.m_HasRateRef)
            {
                state.m_ResidentialRateRef = rate;
                state.m_HasRateRef = true;
            }
            float revenue = m_ServiceBudget.GetIncome(IncomeSource.TaxResidential);
            float taxRaised = rate > 0f ? revenue * math.max(0f, rate - state.m_ResidentialRateRef) / rate : 0f;
            m_TaxPressure = 100f * kBurdenWeight * taxRaised / income * OpinionFactor(protests, Entity.Null);
            if (!HasProtest(protests, Entity.Null))
            {
                total += m_TaxPressure;
            }
            state.m_ResidentialRateRef += adapt * (rate - state.m_ResidentialRateRef);
            if (state.m_MixedUntil > frame)
            {
                total *= kMixedFactor;
            }
            EntityManager.SetComponentData(city, state);

            LastPressure = total;
            LastGrievanceLevel = grievance + total / 5f;
            float legitimacy = m_Engine.LastState.m_Legitimacy;
            float legitimacyFactor = math.clamp(EngineParameters.kLegitimacyBase / math.max(0.01f, legitimacy), 0.5f, 2f);
            // No background rate: protests come only from policy changes (2026-11-09 user decision).
            LastYearlyRate = kBaseRate * (math.exp(kRateSlope * total) - 1f) * legitimacyFactor;
        }

        private static int FindPressure(DynamicBuffer<ProtestPressure> pressures, Entity policy)
        {
            for (int i = 0; i < pressures.Length; i++)
            {
                if (pressures[i].m_Policy == policy)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool HasProtest(DynamicBuffer<Protest> protests, Entity policy)
        {
            for (int i = 0; i < protests.Length; i++)
            {
                if (protests[i].m_Policy == policy)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Residents who expected to win a suit the city won: their pressure × (1 + 2 × (p − 0.5)) for a year (b = 2).</summary>
        public void NoteSurpriseWin(Entity policy, float plaintiffWin)
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<ProtestPressure>(city))
            {
                return;
            }
            DynamicBuffer<ProtestPressure> pressures = EntityManager.GetBuffer<ProtestPressure>(city);
            int p = FindPressure(pressures, policy);
            if (p < 0)
            {
                return;
            }
            ProtestPressure pressure = pressures[p];
            pressure.m_SurpriseFactor = 1f + kSurpriseB * math.max(0f, plaintiffWin - 0.5f);
            pressure.m_SurpriseUntil = m_SimulationSystem.frameIndex + (uint)(kReferenceDays * PolicyEngineSystem.kFramesPerDay);
            pressures[p] = pressure;
            Mod.Log.Info($"Protest pressure: residents lost a suit they expected to win (p {plaintiffWin:0.00}); pressure × {pressure.m_SurpriseFactor:0.00} for a year.");
        }

        // ------------------------------------------------------------------ start

        private void ConsiderStart(Entity city, uint frame, float dtDays)
        {
            bool forced = TestStartProtest;
            TestStartProtest = false;
            if (forced && (TestProtestTax || TestProtestPolicy != Entity.Null))
            {
                PolicyDefinition chosen = TestProtestTax ? null : Definition(TestProtestPolicy);
                TestProtestTax = false;
                TestProtestPolicy = Entity.Null;
                Start(city, frame, chosen);
                return;
            }
            if (!forced)
            {
                if (LastYearlyRate <= 0f)
                {
                    return;
                }
                float chance = 1f - math.exp(-LastYearlyRate * dtDays / 12f);
                var random = Random.CreateFromIndex(frame ^ 0x51ED27u);
                if (random.NextFloat() >= chance)
                {
                    return;
                }
            }
            // The demand: the policy (or the residential tax) that adds most to the pressure, not already protested.
            DynamicBuffer<Protest> protests = EntityManager.GetBuffer<Protest>(city, true);
            PolicyDefinition[] all = PolicyDefinitions.All;
            int best = -1;
            float bestPressure = HasProtest(protests, Entity.Null) ? -1f : m_TaxPressure;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Prefab != Entity.Null && m_PolicyPressure[i] > bestPressure && !HasProtest(protests, all[i].Prefab))
                {
                    best = i;
                    bestPressure = m_PolicyPressure[i];
                }
            }
            if (bestPressure <= 0f && !forced)
            {
                return;
            }
            Start(city, frame, best >= 0 ? all[best] : null);
        }

        private void Start(Entity city, uint frame, PolicyDefinition definition)
        {
            var random = Random.CreateFromIndex(frame ^ 0x9E3779B9u);
            if (!PickStartEdge(ref random, out Entity home, out Entity representative))
            {
                Mod.Log.Info("Protest: no residents to protest.");
                return;
            }
            bool noPark = TestNoPark;
            TestNoPark = false;
            float distance = 0f;
            Entity park = noPark ? Entity.Null : PickPark(ref random, home, out distance);
            Entity edge = park != Entity.Null ? EntityManager.GetComponentData<Building>(park).m_RoadEdge : home;
            ProtestCityState state = EntityManager.GetComponentData<ProtestCityState>(city);
            int id = state.m_NextId++;
            EntityManager.SetComponentData(city, state);

            ProtestTrait trait = PickTrait(city, ref random, edge, frame);
            float intensityNow;
            float demand;
            if (definition != null)
            {
                intensityNow = m_Engine.LastIntensity(definition);
                DynamicBuffer<ProtestPressure> pressures = EntityManager.GetBuffer<ProtestPressure>(city, true);
                int p = FindPressure(pressures, definition.Prefab);
                float reference = p >= 0 ? pressures[p].m_IntensityRef : intensityNow;
                // Benefit cut: back up to what people were used to; burden raised: back down.
                demand = definition.Direction == Direction.Benefit ? math.max(reference, intensityNow) : math.min(reference, intensityNow);
            }
            else
            {
                intensityNow = m_TaxSystem.GetTaxRate(TaxAreaType.Residential);
                demand = math.floor(EntityManager.GetComponentData<ProtestCityState>(city).m_ResidentialRateRef);
            }

            var protest = new Protest
            {
                m_Id = id,
                m_Policy = definition != null ? definition.Prefab : Entity.Null,
                m_Representative = representative,
                m_StartEdge = edge,
                m_IconOwner = park != Entity.Null ? park : edge,
                m_HomeEdge = home,
                m_Place = park,
                m_Trait = (byte)trait,
                m_Response = (byte)ProtestResponse.None,
                m_Size = kStartSize,
                m_Intensity = kStartIntensity[(int)trait],
                m_DemandIntensity = demand,
                m_StartIntensity = intensityNow,
                m_StartFrame = frame,
            };
            EntityManager.GetBuffer<Protest>(city).Add(protest);
            EntityManager.GetBuffer<ProtestEdge>(city).Add(new ProtestEdge { m_Protest = id, m_Edge = edge, m_Role = (byte)ProtestEdgeRole.Protest });
            m_Notifications.PostChirp(representative, protest.m_IconOwner, false);
            Mod.Log.Info($"Protest {id} starts over {(definition != null ? definition.Id : "the residential tax")} ({trait}), pressure {LastPressure:0.00}%, yearly rate {LastYearlyRate:0.00}, "
                + (park != Entity.Null ? $"in park {park.Index} {distance:0} m from the residents' road." : "on the residents' road (no park within reach)."));
        }

        /// <summary>
        /// Where the aggrieved live: a road segment chosen in proportion to the grievance weight of the households along it
        /// (poorer and unhappier households weigh more). The protest gathers in a park near it (<see cref="PickPark"/>).
        /// </summary>
        private bool PickStartEdge(ref Random random, out Entity edge, out Entity representative)
        {
            edge = Entity.Null;
            representative = Entity.Null;
            int poor = ServiceSeriesCensus.PoorWealthLimit(EntityManager);
            int comfortable = ComfortableLimit();
            // Read chunk by chunk with lookups instead of one EntityManager call per household (performance review
            // 2026-11-09: this pass was most of the ~100 ms spike when a protest starts).
            var weights = new Dictionary<Entity, float>();
            float total = 0f;
            m_HouseholdQuery.CompleteDependency();
            EntityManager.CompleteDependencyBeforeRO<Building>();
            EntityManager.CompleteDependencyBeforeRO<Citizen>();
            int capacity = m_HouseholdQuery.CalculateEntityCount();
            var householdOf = new Entity[capacity];
            var edgeOf = new Entity[capacity];
            int used = 0;
            EntityTypeHandle entityType = GetEntityTypeHandle();
            ComponentTypeHandle<Household> householdType = GetComponentTypeHandle<Household>(true);
            ComponentTypeHandle<PropertyRenter> renterType = GetComponentTypeHandle<PropertyRenter>(true);
            BufferTypeHandle<Resources> resourcesType = GetBufferTypeHandle<Resources>(true);
            BufferTypeHandle<HouseholdCitizen> citizenType = GetBufferTypeHandle<HouseholdCitizen>(true);
            ComponentLookup<Building> buildingLookup = GetComponentLookup<Building>(true);
            ComponentLookup<Citizen> citizenLookup = GetComponentLookup<Citizen>(true);
            using (NativeArray<ArchetypeChunk> chunks = m_HouseholdQuery.ToArchetypeChunkArray(Allocator.Temp))
            {
                foreach (ArchetypeChunk chunk in chunks)
                {
                    NativeArray<Entity> ids = chunk.GetNativeArray(entityType);
                    NativeArray<Household> data = chunk.GetNativeArray(ref householdType);
                    NativeArray<PropertyRenter> renters = chunk.GetNativeArray(ref renterType);
                    BufferAccessor<Resources> resources = chunk.GetBufferAccessor(ref resourcesType);
                    BufferAccessor<HouseholdCitizen> members = chunk.GetBufferAccessor(ref citizenType);
                    for (int i = 0; i < chunk.Count && used < capacity; i++)
                    {
                        if (!buildingLookup.TryGetComponent(renters[i].m_Property, out Building building) || building.m_RoadEdge == Entity.Null)
                        {
                            continue;
                        }
                        int wealth = EconomyUtils.GetHouseholdTotalWealth(data[i], resources[i]);
                        float wellbeing = members[i].Length > 0 && citizenLookup.TryGetComponent(members[i][0].m_Citizen, out Citizen first) ? first.m_WellBeing / 100f : 0.5f;
                        float weight = WeightOf(wealth, wellbeing, poor, comfortable);
                        weights.TryGetValue(building.m_RoadEdge, out float w);
                        weights[building.m_RoadEdge] = w + weight;
                        householdOf[used] = ids[i];
                        edgeOf[used] = building.m_RoadEdge;
                        used++;
                        total += weight;
                    }
                }
            }
            if (total <= 0f)
            {
                return false;
            }
            float pick = random.NextFloat(total);
            foreach (KeyValuePair<Entity, float> entry in weights)
            {
                pick -= entry.Value;
                if (pick <= 0f)
                {
                    edge = entry.Key;
                    break;
                }
            }
            if (edge == Entity.Null)
            {
                foreach (Entity key in weights.Keys)
                {
                    edge = key;
                }
            }
            int onEdge = 0;
            for (int i = 0; i < used; i++)
            {
                onEdge += edgeOf[i] == edge ? 1 : 0;
            }
            int which = random.NextInt(onEdge);
            for (int i = 0; i < used; i++)
            {
                if (edgeOf[i] == edge && which-- == 0)
                {
                    representative = householdOf[i];
                    break;
                }
            }
            return true;
        }

        private int ComfortableLimit()
        {
            using (EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<CitizenHappinessParameterData>()))
            {
                return query.IsEmptyIgnoreFilter ? 3000 : query.GetSingleton<CitizenHappinessParameterData>().m_WealthyMoneyAmount.z;
            }
        }

        /// <summary>
        /// How much a household feels the city's decisions (estimate): poorer households 1.5, modest 1, comfortable and up 0.7,
        /// times 1 + (0.5 − wellbeing) of its first member.
        /// </summary>
        internal float HouseholdWeight(Entity household, int poorLimit, int comfortableLimit)
        {
            int wealth = EconomyUtils.GetHouseholdTotalWealth(EntityManager.GetComponentData<Household>(household), EntityManager.GetBuffer<Resources>(household, true));
            float vulnerability = wealth < poorLimit ? 1.5f : wealth < comfortableLimit ? 1f : 0.7f;
            float wellbeing = 0.5f;
            DynamicBuffer<HouseholdCitizen> citizens = EntityManager.GetBuffer<HouseholdCitizen>(household, true);
            if (citizens.Length > 0 && EntityManager.TryGetComponent(citizens[0].m_Citizen, out Citizen citizen))
            {
                wellbeing = citizen.m_WellBeing / 100f;
            }
            return vulnerability * math.max(0.1f, 1f + (0.5f - wellbeing));
        }

        /// <summary>The same weight from a household's wealth and its first member's wellbeing (0..1).</summary>
        private static float WeightOf(int wealth, float wellbeing, int poorLimit, int comfortableLimit)
        {
            float vulnerability = wealth < poorLimit ? 1.5f : wealth < comfortableLimit ? 1f : 0.7f;
            return vulnerability * math.max(0.1f, 1f + (0.5f - wellbeing));
        }

        /// <summary>
        /// The character (§12 "특성이 정해지는 방식"): 20/50/20/10% shifted toward hardline and extreme by low legitimacy, high
        /// pressure, a night start and repression in the area during the last game year (starting values).
        /// </summary>
        private ProtestTrait PickTrait(Entity city, ref Random random, Entity edge, uint frame)
        {
            float shift = 0f;
            float legitimacy = m_Engine.LastState.m_Legitimacy;
            if (legitimacy < 0.4f) shift += 0.1f;
            if (legitimacy > 0.8f) shift -= 0.05f;
            if (LastPressure >= 5f) shift += 0.05f;
            float hour = m_TimeSystem.normalizedTime * 24f;
            if (hour >= 22f || hour < 6f) shift += 0.05f;
            if (RecentlyRepressed(city, edge, frame)) shift += 0.15f;
            float[] p = (float[])kBaseTraitChance.Clone();
            p[0] = math.max(0.02f, p[0] - shift * 0.5f);
            p[1] = math.max(0.02f, p[1] - shift * 0.5f);
            p[2] = math.max(0.02f, p[2] + shift * 0.5f);
            p[3] = math.max(0.02f, p[3] + shift * 0.5f);
            float sum = p[0] + p[1] + p[2] + p[3];
            float pick = random.NextFloat(sum);
            for (int t = 0; t < 4; t++)
            {
                pick -= p[t];
                if (pick <= 0f)
                {
                    return (ProtestTrait)t;
                }
            }
            return ProtestTrait.General;
        }

        private bool RecentlyRepressed(Entity city, Entity edge, uint frame)
        {
            DynamicBuffer<RepressedArea> areas = EntityManager.GetBuffer<RepressedArea>(city);
            uint year = (uint)(kReferenceDays * PolicyEngineSystem.kFramesPerDay);
            for (int i = areas.Length - 1; i >= 0; i--)
            {
                if (frame - areas[i].m_Frame > year || !EntityManager.Exists(areas[i].m_Edge))
                {
                    areas.RemoveAt(i);
                    continue;
                }
                if (areas[i].m_Edge == edge || AreNeighbours(areas[i].m_Edge, edge))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The park it gathers in (Huff 1963; 2026-11-02 user decision): one within 256 m of where the aggrieved live,
        /// chosen in proportion to lot area ÷ distance², so a large park a little further off wins over a one-cell park
        /// next door. Only parks with their own lot beside a road count (<see cref="IsRoadsidePark"/>). Null when none is
        /// in reach: it then starts on the residents' own road (2026-11-09 user decision).
        /// </summary>
        /// <summary>
        /// A park with its own lot beside a road (2026-11-09 user decision "도로 옆에 붙어있는 부지를 가진 공원만"): joined to a
        /// road segment, with a lot, and not one of the park buildings made to stand on a road, such as pedestrian
        /// overpasses (the game's <see cref="Game.Prefabs.BuildingFlags.CanBeOnRoad"/> and <see cref="Game.Prefabs.BuildingFlags.CanBeOnRoadArea"/>).
        /// </summary>
        internal static bool IsRoadsidePark(EntityManager entityManager, Entity park, out BuildingData lot)
        {
            lot = default;
            if (!entityManager.TryGetComponent(park, out Building building) || building.m_RoadEdge == Entity.Null
                || !entityManager.TryGetComponent(park, out PrefabRef prefab) || !entityManager.TryGetComponent(prefab.m_Prefab, out lot))
            {
                return false;
            }
            return (lot.m_Flags & (Game.Prefabs.BuildingFlags.CanBeOnRoad | Game.Prefabs.BuildingFlags.CanBeOnRoadArea)) == 0 && lot.m_LotSize.x > 0 && lot.m_LotSize.y > 0;
        }

        private Entity PickPark(ref Random random, Entity home, out float distance)
        {
            distance = 0f;
            if (!EntityManager.TryGetComponent(home, out Curve curve))
            {
                return Entity.Null;
            }
            float2 from = Colossal.Mathematics.MathUtils.Position(curve.m_Bezier, 0.5f).xz;
            var candidates = new List<(Entity park, float score, float distance)>();
            float total = 0f;
            using (NativeArray<Entity> parks = m_ParkQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < parks.Length; i++)
                {
                    Entity park = parks[i];
                    if (!IsRoadsidePark(EntityManager, park, out BuildingData lot))
                    {
                        continue;
                    }
                    float d = math.distance(from, EntityManager.GetComponentData<Game.Objects.Transform>(park).m_Position.xz);
                    if (d > kPlaceSearchRadius)
                    {
                        continue;
                    }
                    // Lot area in m²; distance no less than one cell, so a park on the residents' own road stays finite.
                    float area = lot.m_LotSize.x * lot.m_LotSize.y * kCellSize * kCellSize;
                    float near = math.max(d, kCellSize);
                    float score = area / (near * near);
                    candidates.Add((park, score, d));
                    total += score;
                }
            }
            if (total <= 0f)
            {
                return Entity.Null;
            }
            float pick = random.NextFloat(total);
            foreach ((Entity park, float score, float d) in candidates)
            {
                pick -= score;
                if (pick <= 0f)
                {
                    distance = d;
                    return park;
                }
            }
            distance = candidates[candidates.Count - 1].distance;
            return candidates[candidates.Count - 1].park;
        }

        // ------------------------------------------------------------------ dynamics

        private void UpdateProtests(Entity city, uint frame, float dtHours)
        {
            DynamicBuffer<Protest> protests = EntityManager.GetBuffer<Protest>(city);
            PolicyEngineState state = EntityManager.GetComponentData<PolicyEngineState>(city);
            float legitimacyLoss = 0f;
            for (int i = protests.Length - 1; i >= 0; i--)
            {
                Protest protest = protests[i];
                PolicyDefinition definition = protest.m_Policy != Entity.Null ? Definition(protest.m_Policy) : null;
                if (protest.m_Policy != Entity.Null && definition == null)
                {
                    End(city, i, frame, "its policy is gone");
                    protests = EntityManager.GetBuffer<Protest>(city);
                    continue;
                }
                protest.m_AgeHours += dtHours;
                var trait = (ProtestTrait)protest.m_Trait;
                int t = (int)trait;

                // Concession: the share of the demand met through the policy (or the tax rate) and compensation.
                float concession = Concession(protest, definition);
                if (concession > 0.01f)
                {
                    protest.m_EverConceded = true;
                }
                bool granted = concession >= 0.999f;
                if (granted && !protest.m_Granted)
                {
                    protest.m_Granted = true;
                    Mod.Log.Info($"Protest {protest.m_Id}: the demand is met in full; {(protest.m_Riot ? "the crowd leaves, the riot fades at 10% an hour" : "it ends")}.");
                }

                // Repression strength: the police level times the share of cars in place; after they leave it fades.
                var response = (ProtestResponse)protest.m_Response;
                bool repressing = response == ProtestResponse.Watch || response == ProtestResponse.Disperse;
                if (repressing)
                {
                    float level = response == ProtestResponse.Disperse ? 1f : 0.5f;
                    float arrived = protest.m_CarsNeeded > 0 ? math.saturate((float)protest.m_CarsArrived / protest.m_CarsNeeded) : 0f;
                    protest.m_Repression = level * arrived;
                    protest.m_EverRepressed = true;
                }
                else
                {
                    protest.m_Repression *= math.exp(-dtHours / kRepressionMemoryHours);
                }
                float x = protest.m_Repression;

                if (protest.m_Granted)
                {
                    // Full acceptance (2026-11-08 user decision): the crowd goes home at once; a riot that has started fades at
                    // a fixed 10% an hour whatever else happens, and ends below the riot threshold.
                    protest.m_Size = 0f;
                    if (protest.m_Riot)
                    {
                        protest.m_Intensity *= math.pow(1f - kRiotDecay, dtHours);
                    }
                    if (!protest.m_Riot || protest.m_Intensity < kRiotThreshold)
                    {
                        protests[i] = protest;
                        End(city, i, frame, "the demand was met");
                        protests = EntityManager.GetBuffer<Protest>(city);
                        continue;
                    }
                }
                else
                {
                    // Early escalation fades by half every 3 hours (2026-11-08 user decision).
                    float escalation = kEscalation * math.pow(0.5f, protest.m_AgeHours / kEscalationHalfLifeHours);
                    // Threshold-model target: those who would join in the end, from the grievance behind the demand; concession
                    // lowers it by the character's reaction, repression bends it n-shaped (Steinert-Threlkeld et al. 2022).
                    float pressure = definition != null ? m_PolicyPressure[System.Array.IndexOf(PolicyDefinitions.All, definition)] : m_TaxPressure;
                    float target = (0.05f + 0.25f * math.saturate(pressure / 5f))
                        * (1f - concession * kConcessionReaction[t]) * RepressionTarget(x) * (protest.m_Riot ? kRiotCohesion : 1f);
                    float size = protest.m_Size;
                    size += dtHours * (escalation * kSizeGrowth[t] * (target - size) - kProtestDecay * size
                        - (protest.m_Riot ? kViolenceSizeLoss * size : 0f));

                    float intensity = protest.m_Intensity;
                    float decay = protest.m_Riot ? kRiotDecay : kProtestDecay;
                    float change = escalation * kIntensityGrowth[t] - decay - concession * kConcessionReaction[t] * 0.1f;
                    if (repressing && protest.m_Riot && response == ProtestResponse.Disperse)
                    {
                        // Forced dispersal brings a riot down (London 2011: the police surge ended it, Davies et al. 2013).
                        change -= x * 0.2f;
                    }
                    intensity += dtHours * intensity * change;
                    if (!protest.m_Riot && x > 0f)
                    {
                        // Repressing a non-violent crowd radicalises it (Lichbach 1987, Moore 1998).
                        intensity += dtHours * x * kRepressionIntensity[t];
                    }
                    protest.m_Size = math.saturate(size);
                    protest.m_Intensity = math.saturate(intensity);
                }

                bool wasRiot = protest.m_Riot;
                protest.m_Riot = protest.m_Intensity >= kRiotThreshold;
                if (protest.m_Riot && !wasRiot)
                {
                    m_Notifications.PostChirp(protest.m_Representative, protest.m_IconOwner, true);
                    Mod.Log.Info($"Protest {protest.m_Id} turns into a riot (intensity {protest.m_Intensity:0.00}).");
                }

                // Legitimacy: repression costs by (1 − violence)²; a dangerous riot left alone costs too (LA 1992).
                if (repressing)
                {
                    float rate = response == ProtestResponse.Disperse ? kDisperseLegitimacy : kWatchLegitimacy;
                    legitimacyLoss += dtHours * rate * (1f - protest.m_Intensity) * (1f - protest.m_Intensity);
                }
                else if (protest.m_Riot && response != ProtestResponse.Negotiate)
                {
                    legitimacyLoss += dtHours * kIgnoredRiotLegitimacy * (protest.m_Intensity - kRiotThreshold) / (1f - kRiotThreshold);
                }

                protest.m_Residents = Residents(city, protest);
                protest.m_Participants = (int)math.round(protest.m_Size * protest.m_Residents);
                protests[i] = protest;

                if (!protest.m_Riot && protest.m_Size < kDissolveSize && protest.m_AgeHours > 1f)
                {
                    End(city, i, frame, "too few people are left");
                    protests = EntityManager.GetBuffer<Protest>(city);
                    continue;
                }

                SpreadOrShrink(city, protest, frame, dtHours);
                if (protest.m_Riot)
                {
                    RiotEffects(city, protest, frame, dtHours);
                }
                if (repressing)
                {
                    RememberRepression(city, protest.m_Id, frame);
                }
                protests = EntityManager.GetBuffer<Protest>(city);
            }
            if (legitimacyLoss > 0f)
            {
                state = EntityManager.GetComponentData<PolicyEngineState>(city);
                state.m_Legitimacy = math.saturate(state.m_Legitimacy - legitimacyLoss);
                EntityManager.SetComponentData(city, state);
            }
        }

        /// <summary>Next size target from repression strength x (n-shape): +60% at 0.5, ×0.4 at 1 (§12, starting values).</summary>
        private static float RepressionTarget(float x) => x <= 0.5f ? 1f + 1.2f * x : 1.6f - 2.4f * (x - 0.5f);

        /// <summary>Share of the demand met 0..1: the policy moved toward the demand, plus compensation against a year of the cut.</summary>
        private float Concession(Protest protest, PolicyDefinition definition)
        {
            float now = definition != null ? m_Engine.LastIntensity(definition) : m_TaxSystem.GetTaxRate(TaxAreaType.Residential);
            float gap = protest.m_DemandIntensity - protest.m_StartIntensity;
            float policy;
            if (math.abs(gap) < 1e-4f)
            {
                // Nothing measurable to give back (the demand equals the start): only a move past the start counts.
                policy = 0f;
            }
            else
            {
                policy = math.saturate((now - protest.m_StartIntensity) / gap);
            }
            float yearly = 12f * MonthlyIncome() * math.max(0.001f, (definition != null ? m_PolicyPressure[System.Array.IndexOf(PolicyDefinitions.All, definition)] : m_TaxPressure) / 100f);
            float money = protest.m_Compensation > 0f ? math.saturate(protest.m_Compensation / yearly) : 0f;
            return math.saturate(policy + money);
        }

        /// <summary>Residents along its segments and, when it gathers in a park, along the road the aggrieved came from.</summary>
        private int Residents(Entity city, Protest protest)
        {
            DynamicBuffer<ProtestEdge> edges = EntityManager.GetBuffer<ProtestEdge>(city, true);
            int residents = 0;
            bool homeCounted = protest.m_HomeEdge == Entity.Null;
            for (int i = 0; i < edges.Length; i++)
            {
                if (edges[i].m_Protest == protest.m_Id && edges[i].m_Role == (byte)ProtestEdgeRole.Protest)
                {
                    residents += EdgeResidents(edges[i].m_Edge);
                    homeCounted |= edges[i].m_Edge == protest.m_HomeEdge;
                }
            }
            if (!homeCounted && EntityManager.Exists(protest.m_HomeEdge))
            {
                residents += EdgeResidents(protest.m_HomeEdge);
            }
            return math.max(1, residents);
        }

        /// <summary>Citizens living in the buildings along a road segment.</summary>
        private int EdgeResidents(Entity edge)
        {
            int residents = 0;
            if (!EntityManager.TryGetBuffer(edge, true, out DynamicBuffer<ConnectedBuilding> buildings))
            {
                return 0;
            }
            for (int b = 0; b < buildings.Length; b++)
            {
                if (!EntityManager.TryGetBuffer(buildings[b].m_Building, true, out DynamicBuffer<Renter> renters))
                {
                    continue;
                }
                for (int r = 0; r < renters.Length; r++)
                {
                    if (EntityManager.TryGetBuffer(renters[r].m_Renter, true, out DynamicBuffer<HouseholdCitizen> citizens))
                    {
                        residents += citizens.Length;
                    }
                }
            }
            return residents;
        }

        private int Shops(Entity edge)
        {
            int shops = 0;
            if (EntityManager.TryGetBuffer(edge, true, out DynamicBuffer<ConnectedBuilding> buildings))
            {
                for (int b = 0; b < buildings.Length; b++)
                {
                    if (EntityManager.HasComponent<CommercialProperty>(buildings[b].m_Building))
                    {
                        shops++;
                    }
                }
            }
            return shops;
        }

        /// <summary>
        /// Spread along the road at 10% of residents: about one neighbour segment every two hours, weighted 1.27 per shop
        /// (Baudains et al. 2013); police at an end take the shops' extra away beyond it (§12). Below 10% it shrinks from the
        /// edges, about one end segment every two hours.
        /// </summary>
        private void SpreadOrShrink(Entity city, Protest protest, uint frame, float dtHours)
        {
            var random = Random.CreateFromIndex((uint)protest.m_Id * 7919u ^ frame);
            DynamicBuffer<ProtestEdge> edges = EntityManager.GetBuffer<ProtestEdge>(city);
            var own = new List<Entity>();
            for (int i = 0; i < edges.Length; i++)
            {
                if (edges[i].m_Protest == protest.m_Id && edges[i].m_Role == (byte)ProtestEdgeRole.Protest)
                {
                    own.Add(edges[i].m_Edge);
                }
            }
            if (protest.m_Size >= kSpreadSize && own.Count < kMaxSegments)
            {
                if (random.NextFloat() >= 0.5f * dtHours)
                {
                    return;
                }
                var candidates = new List<(Entity edge, float weight)>();
                float total = 0f;
                foreach (Entity edge in own)
                {
                    foreach (Entity neighbour in Neighbours(edge))
                    {
                        if (own.Contains(neighbour) || IsProtestEdge(city, neighbour) || !EntityManager.HasComponent<Game.Net.Road>(neighbour))
                        {
                            continue;
                        }
                        float shopBonus = math.pow(1.27f, math.min(10, Shops(neighbour)));
                        if (PoliceHold(city, protest.m_Id, edge, neighbour))
                        {
                            shopBonus = 1f + (shopBonus - 1f) * (1f - protest.m_Repression);
                        }
                        float weight = shopBonus * (1f + EdgeResidents(neighbour) / 50f);
                        candidates.Add((neighbour, weight));
                        total += weight;
                    }
                }
                if (total <= 0f)
                {
                    return;
                }
                float pick = random.NextFloat(total);
                foreach ((Entity edge, float weight) in candidates)
                {
                    pick -= weight;
                    if (pick <= 0f)
                    {
                        edges.Add(new ProtestEdge { m_Protest = protest.m_Id, m_Edge = edge, m_Role = (byte)ProtestEdgeRole.Protest });
                        Mod.Log.Info($"Protest {protest.m_Id} spreads to a neighbouring segment ({own.Count + 1} segments).");
                        return;
                    }
                }
            }
            else if (protest.m_Size < kSpreadSize && own.Count > 1 && random.NextFloat() < 0.5f * dtHours)
            {
                // Shrink from an end: a segment with only one neighbour in the protest, never the start.
                foreach (Entity edge in own)
                {
                    if (edge == protest.m_StartEdge)
                    {
                        continue;
                    }
                    int inside = 0;
                    foreach (Entity neighbour in Neighbours(edge))
                    {
                        if (own.Contains(neighbour))
                        {
                            inside++;
                        }
                    }
                    if (inside <= 1)
                    {
                        for (int i = edges.Length - 1; i >= 0; i--)
                        {
                            if (edges[i].m_Protest == protest.m_Id && edges[i].m_Edge == edge && edges[i].m_Role == (byte)ProtestEdgeRole.Protest)
                            {
                                edges.RemoveAt(i);
                            }
                        }
                        return;
                    }
                }
            }
        }

        private bool IsProtestEdge(Entity city, Entity edge)
        {
            DynamicBuffer<ProtestEdge> edges = EntityManager.GetBuffer<ProtestEdge>(city, true);
            for (int i = 0; i < edges.Length; i++)
            {
                if (edges[i].m_Edge == edge && edges[i].m_Role == (byte)ProtestEdgeRole.Protest)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Road segments that share a node with this one.</summary>
        private List<Entity> Neighbours(Entity edge)
        {
            var result = new List<Entity>();
            if (!EntityManager.TryGetComponent(edge, out Edge data))
            {
                return result;
            }
            foreach (Entity node in new[] { data.m_Start, data.m_End })
            {
                if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<ConnectedEdge> connected))
                {
                    continue;
                }
                for (int i = 0; i < connected.Length; i++)
                {
                    Entity other = connected[i].m_Edge;
                    if (other != edge && !result.Contains(other))
                    {
                        result.Add(other);
                    }
                }
            }
            return result;
        }

        private bool AreNeighbours(Entity a, Entity b) => Neighbours(a).Contains(b);

        /// <summary>Riots: building crime held at the intensity (2026-11-08 user decision) and fires (방화) along riot segments.</summary>
        private void RiotEffects(Entity city, Protest protest, uint frame, float dtHours)
        {
            float maxCrime = m_PoliceConfigQuery.IsEmptyIgnoreFilter ? 100000f : m_PoliceConfigQuery.GetSingleton<PoliceConfigurationData>().m_MaxCrimeAccumulation;
            float floor = protest.m_Intensity * maxCrime;
            var random = Random.CreateFromIndex((uint)protest.m_Id * 104729u ^ frame);
            DynamicBuffer<ProtestEdge> edges = EntityManager.GetBuffer<ProtestEdge>(city, true);
            var riotEdges = new List<Entity>();
            for (int i = 0; i < edges.Length; i++)
            {
                if (edges[i].m_Protest == protest.m_Id && edges[i].m_Role == (byte)ProtestEdgeRole.Protest)
                {
                    riotEdges.Add(edges[i].m_Edge);
                }
            }
            var crimeTargets = new List<Entity>();
            foreach (Entity edge in riotEdges)
            {
                if (!EntityManager.TryGetBuffer(edge, true, out DynamicBuffer<ConnectedBuilding> buildings))
                {
                    continue;
                }
                var targets = new List<Entity>();
                for (int b = 0; b < buildings.Length; b++)
                {
                    Entity building = buildings[b].m_Building;
                    if (EntityManager.TryGetComponent(building, out CrimeProducer crime) && crime.m_Crime < floor)
                    {
                        crime.m_Crime = floor;
                        EntityManager.SetComponentData(building, crime);
                    }
                    if (!EntityManager.HasComponent<OnFire>(building) && !EntityManager.HasComponent<Destroyed>(building))
                    {
                        targets.Add(building);
                    }
                }
                crimeTargets.AddRange(targets);
                // Arson: about 0.15 × intensity fires an hour per riot segment (starting value).
                if (targets.Count > 0 && random.NextFloat() < kRiotArson * protest.m_Intensity * dtHours)
                {
                    StartFire(targets[random.NextInt(targets.Count)]);
                }
            }

            // Crimes: about 7 for each fire (2026-11-09 user decision; London 2011, Met figures: 1,149 burglaries to 162
            // arsons), started on rioters through the game's crime event, so the game shows them, sends police and counts them.
            float expected = kRiotArson * kRiotCrimesPerArson * protest.m_Intensity * dtHours * riotEdges.Count;
            int crimes = (int)math.floor(expected) + (random.NextFloat() < expected - math.floor(expected) ? 1 : 0);
            if (crimes > 0)
            {
                var rioters = new List<Entity>();
                DynamicBuffer<ProtestMarcher> marchers = EntityManager.GetBuffer<ProtestMarcher>(city, true);
                for (int i = 0; i < marchers.Length; i++)
                {
                    if (marchers[i].m_Protest == protest.m_Id && EntityManager.Exists(marchers[i].m_Citizen)
                        && !EntityManager.HasComponent<Criminal>(marchers[i].m_Citizen))
                    {
                        rioters.Add(marchers[i].m_Citizen);
                    }
                }
                for (int c = 0; c < crimes && rioters.Count > 0; c++)
                {
                    int pick = random.NextInt(rioters.Count);
                    Entity target = crimeTargets.Count > 0 ? crimeTargets[random.NextInt(crimeTargets.Count)] : Entity.Null;
                    if (StartCrime(rioters[pick], target))
                    {
                        RiotCrimes++;
                        RiotCriminals.Add(rioters[pick]);
                    }
                    rioters.RemoveAt(pick);
                }
            }
        }

        /// <summary>
        /// A crime by this citizen through the game's own crime event (as CrimeCheckSystem starts one), at a building on the
        /// riot's roads (2026-11-10 user decision). The game's criminal system adds a crime trip with no target and lets the
        /// trip system find the building with the most crime anywhere in the city, which can be kilometres away (self-test
        /// 2026-11-10: 5.3 km); it adds none when the citizen already has a crime trip, and a trip that names its target goes
        /// straight there. So the rioter is given a crime trip to the riot building first; the crime scene opens there.
        /// </summary>
        private bool StartCrime(Entity citizen, Entity target)
        {
            using (NativeArray<Entity> prefabs = m_CrimePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    if (EntityManager.GetComponentData<CrimeData>(prefabs[i]).m_RandomTargetType != EventTargetType.Citizen)
                    {
                        continue;
                    }
                    EventData eventData = EntityManager.GetComponentData<EventData>(prefabs[i]);
                    EntityCommandBuffer buffer = m_EndFrameBarrier.CreateCommandBuffer();
                    Entity e = buffer.CreateEntity(eventData.m_Archetype);
                    buffer.SetComponent(e, new PrefabRef(prefabs[i]));
                    buffer.SetBuffer<TargetElement>(e).Add(new TargetElement(citizen));
                    if (target != Entity.Null && EntityManager.TryGetBuffer(citizen, false, out DynamicBuffer<TripNeeded> trips))
                    {
                        trips.Add(new TripNeeded { m_TargetAgent = target, m_Purpose = Purpose.Crime, m_Priority = 128 });
                    }
                    NativeQueue<StatisticsEvent> statistics = m_Statistics.GetStatisticsEventQueue(out Unity.Jobs.JobHandle deps);
                    deps.Complete();
                    statistics.Enqueue(new StatisticsEvent { m_Statistic = StatisticType.CrimeCount, m_Change = 1f });
                    m_Statistics.AddWriter(default);
                    return true;
                }
            }
            return false;
        }

        /// <summary>A building fire through the game's own fire event (as FireHazardSystem starts one).</summary>
        private void StartFire(Entity building)
        {
            using (NativeArray<Entity> prefabs = m_FirePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    FireData fire = EntityManager.GetComponentData<FireData>(prefabs[i]);
                    if (fire.m_RandomTargetType != EventTargetType.Building)
                    {
                        continue;
                    }
                    EventData eventData = EntityManager.GetComponentData<EventData>(prefabs[i]);
                    EntityCommandBuffer buffer = m_EndFrameBarrier.CreateCommandBuffer();
                    Entity e = buffer.CreateEntity(eventData.m_Archetype);
                    buffer.SetComponent(e, new PrefabRef(prefabs[i]));
                    buffer.SetBuffer<TargetElement>(e).Add(new TargetElement(building));
                    return;
                }
            }
        }

        private void RememberRepression(Entity city, int protestId, uint frame)
        {
            DynamicBuffer<ProtestEdge> edges = EntityManager.GetBuffer<ProtestEdge>(city, true);
            var list = new List<Entity>();
            for (int i = 0; i < edges.Length; i++)
            {
                if (edges[i].m_Protest == protestId && edges[i].m_Role == (byte)ProtestEdgeRole.Protest)
                {
                    list.Add(edges[i].m_Edge);
                }
            }
            DynamicBuffer<RepressedArea> areas = EntityManager.GetBuffer<RepressedArea>(city);
            foreach (Entity edge in list)
            {
                bool found = false;
                for (int a = 0; a < areas.Length; a++)
                {
                    if (areas[a].m_Edge == edge)
                    {
                        areas[a] = new RepressedArea { m_Edge = edge, m_Frame = frame };
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    areas.Add(new RepressedArea { m_Edge = edge, m_Frame = frame });
                }
            }
        }

        /// <summary>
        /// Before the mod is taken out (<see cref="ModCleanup"/>): every protest ends, then the roads, gatherings and icons
        /// are brought in line at once, as after an ordinary end (lanes open, police cars back to their duties, spots gone).
        /// </summary>
        internal void CleanUpForRemoval()
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<Protest>(city))
            {
                return;
            }
            uint frame = m_SimulationSystem.frameIndex;
            DynamicBuffer<Protest> protests = EntityManager.GetBuffer<Protest>(city);
            for (int i = protests.Length - 1; i >= 0; i--)
            {
                End(city, i, frame, "the city is being readied for removing the mod");
                protests = EntityManager.GetBuffer<Protest>(city);
            }
            UpdateRoads(city, frame);
            UpdateGatherings(city);
            m_Notifications.Refresh(EntityManager.GetBuffer<Protest>(city, true));
            EndIndustrialActions(city);
        }

        private void End(Entity city, int index, uint frame, string why)
        {
            DynamicBuffer<Protest> protests = EntityManager.GetBuffer<Protest>(city);
            Protest protest = protests[index];
            if (protest.m_EverConceded && protest.m_EverRepressed)
            {
                // Concession and repression in one protest invite the next (Lichbach 1987; ×1.5 for a year, estimate).
                ProtestCityState state = EntityManager.GetComponentData<ProtestCityState>(city);
                state.m_MixedUntil = frame + (uint)(kReferenceDays * PolicyEngineSystem.kFramesPerDay);
                EntityManager.SetComponentData(city, state);
            }
            protests.RemoveAt(index);
            RemovePosts(city, protest.m_Id);
            DynamicBuffer<ProtestEdge> edges = EntityManager.GetBuffer<ProtestEdge>(city);
            for (int i = edges.Length - 1; i >= 0; i--)
            {
                if (edges[i].m_Protest == protest.m_Id)
                {
                    edges.RemoveAt(i);
                }
            }
            Mod.Log.Info($"Protest {protest.m_Id} ends: {why} (after {protest.m_AgeHours:0.0} h).");
        }

        // ------------------------------------------------------------------ responses (report panel)

        public static PolicyDefinition Definition(Entity prefab)
        {
            foreach (PolicyDefinition definition in PolicyDefinitions.All)
            {
                if (definition.Prefab == prefab)
                {
                    return definition;
                }
            }
            return null;
        }

        /// <summary>Sets the city's response to a protest; switching away from repression sends the police home.</summary>
        public void Respond(int protestId, ProtestResponse response)
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<Protest>(city))
            {
                return;
            }
            DynamicBuffer<Protest> protests = EntityManager.GetBuffer<Protest>(city);
            for (int i = 0; i < protests.Length; i++)
            {
                if (protests[i].m_Id != protestId)
                {
                    continue;
                }
                Protest protest = protests[i];
                protest.m_Response = (byte)response;
                if (response != ProtestResponse.Watch && response != ProtestResponse.Disperse)
                {
                    protest.m_CarsNeeded = 0;
                    protest.m_CarsArrived = 0;
                }
                protests[i] = protest;
                if (response != ProtestResponse.Watch && response != ProtestResponse.Disperse)
                {
                    RemovePosts(city, protestId);
                }
                Mod.Log.Info($"Protest {protestId}: response {response}.");
                return;
            }
        }

        /// <summary>
        /// Negotiation (§12): sets the demanded policy to <paramref name="intensity"/> (0..1; for the tax protest, the residential
        /// rate) through the policy panel's event path and pays <paramref name="compensation"/> at once.
        /// </summary>
        public void Negotiate(int protestId, float intensity, int compensation)
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<Protest>(city))
            {
                return;
            }
            DynamicBuffer<Protest> protests = EntityManager.GetBuffer<Protest>(city);
            for (int i = 0; i < protests.Length; i++)
            {
                if (protests[i].m_Id != protestId)
                {
                    continue;
                }
                Protest protest = protests[i];
                protest.m_Response = (byte)ProtestResponse.Negotiate;
                if (compensation > 0)
                {
                    PlayerMoney money = EntityManager.GetComponentData<PlayerMoney>(city);
                    money.Subtract(compensation);
                    EntityManager.SetComponentData(city, money);
                    protest.m_Compensation += compensation;
                }
                protests[i] = protest;
                PolicyDefinition definition = protest.m_Policy != Entity.Null ? Definition(protest.m_Policy) : null;
                if (definition != null)
                {
                    SetIntensity(city, definition, math.saturate(intensity));
                }
                else
                {
                    m_TaxSystem.SetTaxRate(TaxAreaType.Residential, (int)math.round(intensity));
                }
                RemovePosts(city, protestId);
                Mod.Log.Info($"Protest {protestId}: negotiation, policy to {intensity:0.00}, compensation {compensation}.");
                return;
            }
        }

        private void SetIntensity(Entity city, PolicyDefinition definition, float intensity)
        {
            float adjustment = definition.IsSlider
                ? definition.SliderRange.min + intensity * (definition.SliderRange.max - definition.SliderRange.min)
                : 0f;
            if (definition.IsSlider && definition.SliderStep > 0f)
            {
                adjustment = math.round(adjustment / definition.SliderStep) * definition.SliderStep;
            }
            EntityCommandBuffer buffer = m_EndFrameBarrier.CreateCommandBuffer();
            Entity e = buffer.CreateEntity(m_ModifyArchetype);
            buffer.SetComponent(e, new Modify(city, definition.Prefab, intensity > 0f, adjustment));
        }

        /// <summary>Policy pressure (% of monthly income) of a protest's demand at the last update, for the panel.</summary>
        public float DemandPressure(Entity policy)
        {
            if (policy == Entity.Null)
            {
                return m_TaxPressure;
            }
            int index = System.Array.FindIndex(PolicyDefinitions.All, d => d.Prefab == policy);
            return index >= 0 ? m_PolicyPressure[index] : 0f;
        }

        [Preserve]
        public ProtestSystem()
        {
        }
    }
}
