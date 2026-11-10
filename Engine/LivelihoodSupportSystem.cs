using System.Collections.Generic;
using Game;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// §14.21 livelihood support: pays households whose income is below a share of the city's median household income
    /// (the detail setting, 32% as Korea's livelihood benefit) a monthly amount from the city budget, lowest income first,
    /// as many as the social welfare staff can handle (the engine's delivered share). The slider is the money each
    /// household gets a month (0-1,600; the game's unemployment benefit is 800).
    /// The game rates wellbeing on household income (<c>CitizenHappinessSystem.GetWealthWellbeing</c>), which
    /// <c>HouseholdBehaviorSystem</c> recomputes from wages and benefits every time it updates a household; so right
    /// after it updates a slice of households, this adds the support to the income of the supported ones in that slice.
    /// The money itself is paid into the households' accounts once per engine step.
    /// </summary>
    public partial class LivelihoodSupportSystem : GameSystemBase
    {
        /// <summary>Households one social welfare worker handles (50-70 judged right, 박윤희·기정훈 2011; the middle).</summary>
        public const float kHouseholdsPerWorker = 60f;

        // The household update of HouseholdBehaviorSystem: 16 slices, one every 64 frames.
        private const int kSlices = 16;

        private SimulationSystem m_SimulationSystem;
        private PolicyEngineSystem m_Engine;
        private BudgetInjectionSystem m_Budget;
        private EntityQuery m_HouseholdQuery;
        private EntityQuery m_EconomyQuery;

        private readonly List<Entity>[] m_BySlice = new List<Entity>[kSlices];
        private readonly HashSet<Entity> m_Supported = new HashSet<Entity>();
        private uint m_LastPayFrame;
        private bool m_HasPaid;
        private int m_MonthlyEach;

        /// <summary>Households who qualify (income below the detail setting's share of the median) at the last selection.</summary>
        public int LastPoor { get; private set; }

        /// <summary>At the last selection: households counted, their median income and the income limit (without this support).</summary>
        public int LastHouseholds { get; private set; }

        public int LastMedianIncome { get; private set; }

        public int LastIncomeLimit { get; private set; }

        /// <summary>Households paid at the last selection.</summary>
        public int LastSupported { get; private set; }

        /// <summary>Money per supported household per month at the last selection.</summary>
        public int LastMonthlyEach => m_MonthlyEach;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 64;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Engine = World.GetOrCreateSystemManaged<PolicyEngineSystem>();
            m_Budget = World.GetOrCreateSystemManaged<BudgetInjectionSystem>();
            m_HouseholdQuery = GetEntityQuery(ServiceSeriesCensus.PoorHouseholdQueryDesc);
            m_EconomyQuery = GetEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
            for (int i = 0; i < kSlices; i++)
            {
                m_BySlice[i] = new List<Entity>();
            }
            RequireForUpdate(m_EconomyQuery);
        }

        [Preserve]
        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            Clear();
            m_HasPaid = false;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            PolicyDefinition definition = PolicyCatalog.Definition(PolicyCatalog.kLivelihoodSupport);
            if (definition == null || definition.Prefab == Entity.Null)
            {
                return;
            }
            uint frame = m_SimulationSystem.frameIndex;
            float intensity = m_Engine.LastIntensity(definition);
            if (intensity <= 0f)
            {
                if (m_Supported.Count > 0 || m_MonthlyEach != 0)
                {
                    Clear();
                    m_Budget.SetRow(PolicyDefinitions.kLivelihoodRow, false, (int)Game.City.ExpenseSource.SubsidyResidential, 0);
                }
                // Switched off: the next payment after it comes back does not cover the time it was off.
                m_HasPaid = false;
                return;
            }
            long start = Perf.Start();
            // Choose and pay once per engine step; add to income every slice.
            uint interval = (uint)PolicyEngineSystem.UpdateIntervalFrames;
            if (!m_HasPaid || frame - m_LastPayFrame >= interval)
            {
                float days = m_HasPaid ? math.min(1f, (frame - m_LastPayFrame) / (float)PolicyEngineSystem.kFramesPerDay) : 0f;
                Select(definition, intensity);
                Pay(days);
                m_LastPayFrame = frame;
                m_HasPaid = true;
            }
            AddIncome(SimulationUtils.GetUpdateFrameWithInterval(frame, 64u, kSlices));
            Perf.Stop("LivelihoodSupport", start);
        }

        private void Clear()
        {
            m_Supported.Clear();
            for (int i = 0; i < kSlices; i++)
            {
                m_BySlice[i].Clear();
            }
            m_MonthlyEach = 0;
            LastSupported = 0;
        }

        /// <summary>
        /// The lowest incomes first, as many as the delivered share of those who qualify allows. Who qualifies: income
        /// without this support below the detail setting's share of the city's median household income (also without
        /// it), as Korea's livelihood benefit takes households under 32% of the standard median income (2026-11-10 user
        /// decision; it was wealth, which in the test city was about the same for almost every household).
        /// </summary>
        private void Select(PolicyDefinition definition, float intensity)
        {
            // The support added to last selection's households' income (AddIncome) is taken off again, or they would
            // drop out of the next selection and come back the one after.
            var previous = new HashSet<Entity>(m_Supported);
            int previousEach = m_MonthlyEach;
            Clear();
            int index = System.Array.IndexOf(PolicyDefinitions.All, definition);
            float delivered = index >= 0 ? math.saturate(m_Engine.LastResults[index].Progress / intensity) : 0f;
            // The slider is money per household per month (2026-11-10 user decision; was a share of the unemployment benefit).
            m_MonthlyEach = (int)math.round(definition.SliderRange.min + intensity * (definition.SliderRange.max - definition.SliderRange.min));
            float share = m_Engine.SecondValue(definition) / 100f;

            // Chunk by chunk (no EntityManager call per household), and only the `count` lowest are picked out, not the
            // whole list sorted (performance review 2026-11-09): key = income in the high bits, the household's slot below.
            m_HouseholdQuery.CompleteDependency();
            int total = m_HouseholdQuery.CalculateEntityCount();
            long[] keys = new long[total];
            Entity[] entities = new Entity[total];
            int households = 0;
            EntityTypeHandle entityType = GetEntityTypeHandle();
            ComponentTypeHandle<Household> householdType = GetComponentTypeHandle<Household>(true);
            using (NativeArray<ArchetypeChunk> chunks = m_HouseholdQuery.ToArchetypeChunkArray(Allocator.Temp))
            {
                foreach (ArchetypeChunk chunk in chunks)
                {
                    NativeArray<Entity> ids = chunk.GetNativeArray(entityType);
                    NativeArray<Household> data = chunk.GetNativeArray(ref householdType);
                    for (int i = 0; i < chunk.Count && households < total; i++)
                    {
                        int income = data[i].m_Income;
                        if (previousEach > 0 && previous.Contains(ids[i]))
                        {
                            income -= previousEach;
                        }
                        keys[households] = ((long)math.max(0, income) << 32) | (uint)households;
                        entities[households] = ids[i];
                        households++;
                    }
                }
            }

            // The median: the largest of the lower half (picked out, not sorted).
            int median = 0;
            if (households > 0)
            {
                int half = (households + 1) / 2;
                if (half < households)
                {
                    SelectSmallest(keys, households, half);
                }
                long largest = 0;
                for (int i = 0; i < half; i++)
                {
                    largest = math.max(largest, keys[i]);
                }
                median = (int)(largest >> 32);
            }
            long limit = (long)math.round(median * share);
            int poorCount = 0;
            for (int i = 0; i < households; i++)
            {
                if ((keys[i] >> 32) < limit)
                {
                    keys[poorCount++] = keys[i];
                }
            }
            LastMedianIncome = median;
            LastIncomeLimit = (int)limit;
            LastHouseholds = households;
            LastPoor = poorCount;
            int count = (int)math.round(poorCount * delivered);
            if (count > 0 && count < poorCount)
            {
                SelectSmallest(keys, poorCount, count);
            }
            for (int i = 0; i < count; i++)
            {
                Entity entity = entities[(int)(keys[i] & 0xFFFFFFFFL)];
                m_Supported.Add(entity);
                if (EntityManager.HasComponent<UpdateFrame>(entity))
                {
                    m_BySlice[EntityManager.GetSharedComponent<UpdateFrame>(entity).m_Index & (kSlices - 1)].Add(entity);
                }
            }
            LastSupported = count;
            m_Budget.SetRow(PolicyDefinitions.kLivelihoodRow, false, (int)Game.City.ExpenseSource.SubsidyResidential, count * m_MonthlyEach);
        }

        /// <summary>Moves the <paramref name="k"/> smallest of the first <paramref name="n"/> keys to the front (quickselect, any order).</summary>
        internal static void SelectSmallest(long[] keys, int n, int k)
        {
            int left = 0, right = n - 1;
            while (left < right)
            {
                long pivot = keys[(left + right) >> 1];
                int i = left, j = right;
                while (i <= j)
                {
                    while (keys[i] < pivot) i++;
                    while (keys[j] > pivot) j--;
                    if (i <= j)
                    {
                        long swap = keys[i];
                        keys[i] = keys[j];
                        keys[j] = swap;
                        i++;
                        j--;
                    }
                }
                if (k - 1 <= j)
                {
                    right = j;
                }
                else if (k - 1 >= i)
                {
                    left = i;
                }
                else
                {
                    return;
                }
            }
        }

        /// <summary>Pays the money for the elapsed part of the month into the supported households' accounts.</summary>
        private void Pay(float days)
        {
            int amount = (int)math.round(m_MonthlyEach * days);
            if (amount <= 0)
            {
                return;
            }
            foreach (Entity entity in m_Supported)
            {
                if (EntityManager.Exists(entity) && EntityManager.HasBuffer<Resources>(entity))
                {
                    DynamicBuffer<Resources> resources = EntityManager.GetBuffer<Resources>(entity);
                    EconomyUtils.AddResources(Resource.Money, amount, resources);
                }
            }
        }

        /// <summary>Adds the support to the income the game has just recomputed for this slice of households.</summary>
        private void AddIncome(uint slice)
        {
            List<Entity> households = m_BySlice[slice & (kSlices - 1)];
            for (int i = 0; i < households.Count; i++)
            {
                Entity entity = households[i];
                if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<Household>(entity))
                {
                    continue;
                }
                Household household = EntityManager.GetComponentData<Household>(entity);
                household.m_Income += m_MonthlyEach;
                EntityManager.SetComponentData(entity, household);
            }
        }
    }
}
