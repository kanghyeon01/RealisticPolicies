using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// Rent support (new policy, 2026-11-09, in place of public rental housing): gives poor renting households a share of
    /// their rent back each month (slider 0-50%), poorest first, as many as the social welfare staff handle (100
    /// households each; the engine's delivered share). The same way as <see cref="LivelihoodSupportSystem"/>: the money is
    /// paid into the households' accounts once per engine step and added to the income the game has just recomputed, so
    /// they can keep paying their rent and move or lose their homes less often (Moretti &amp; Schmid 2022, direction only).
    /// The expense row is what is actually paid.
    /// </summary>
    public sealed partial class RentSupportSystem : GameSystemBase
    {
        /// <summary>Households one social welfare worker handles (an allowance case is lighter than livelihood support; estimate).</summary>
        public const float kHouseholdsPerWorker = 100f;

        /// <summary>Largest share of the rent given back (the slider's top, 50%).</summary>
        public const float kMaxShare = 0.5f;

        private const int kSlices = 16;

        private SimulationSystem m_SimulationSystem;
        private PolicyEngineSystem m_Engine;
        private BudgetInjectionSystem m_Budget;
        private EntityQuery m_HouseholdQuery;

        private readonly List<Entity>[] m_BySlice = new List<Entity>[kSlices];
        private readonly Dictionary<Entity, int> m_Supported = new Dictionary<Entity, int>();
        private uint m_LastPayFrame;
        private bool m_HasPaid;

        /// <summary>Poor renting households at the last selection, those supported, and the money a month in all.</summary>
        public int LastRenters { get; private set; }

        public int LastSupported { get; private set; }

        public int LastMonthly { get; private set; }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 64;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Engine = World.GetOrCreateSystemManaged<PolicyEngineSystem>();
            m_Budget = World.GetOrCreateSystemManaged<BudgetInjectionSystem>();
            EntityQueryDesc desc = ServiceSeriesCensus.PoorHouseholdQueryDesc;
            var all = new List<ComponentType>(desc.All) { ComponentType.ReadOnly<PropertyRenter>() };
            desc.All = all.ToArray();
            m_HouseholdQuery = GetEntityQuery(desc);
            for (int i = 0; i < kSlices; i++)
            {
                m_BySlice[i] = new List<Entity>();
            }
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
            PolicyDefinition definition = PolicyCatalog.Definition(PolicyCatalog.kRentSupport);
            if (definition == null || definition.Prefab == Entity.Null)
            {
                return;
            }
            float intensity = m_Engine.LastIntensity(definition);
            if (intensity <= 0f || ModCleanup.Done)
            {
                if (m_Supported.Count > 0 || LastMonthly != 0)
                {
                    Clear();
                    m_Budget.SetRow(PolicyDefinitions.kRentSupportRow, false, (int)Game.City.ExpenseSource.SubsidyResidential, 0);
                }
                return;
            }
            long start = Perf.Start();
            uint frame = m_SimulationSystem.frameIndex;
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
            Perf.Stop("RentSupport", start);
        }

        private void Clear()
        {
            m_Supported.Clear();
            for (int i = 0; i < kSlices; i++)
            {
                m_BySlice[i].Clear();
            }
            LastSupported = 0;
            LastMonthly = 0;
        }

        /// <summary>The poorest renting households first, as many as the delivered share allows; each gets share × its rent.</summary>
        private void Select(PolicyDefinition definition, float intensity)
        {
            Clear();
            int index = System.Array.IndexOf(PolicyDefinitions.All, definition);
            float delivered = index >= 0 ? math.saturate(m_Engine.LastResults[index].Progress / intensity) : 0f;
            float share = intensity * kMaxShare;
            int limit = ServiceSeriesCensus.PoorWealthLimit(EntityManager);

            m_HouseholdQuery.CompleteDependency();
            int total = m_HouseholdQuery.CalculateEntityCount();
            long[] keys = new long[total];
            Entity[] entities = new Entity[total];
            int[] rents = new int[total];
            int poorCount = 0;
            EntityTypeHandle entityType = GetEntityTypeHandle();
            ComponentTypeHandle<Household> householdType = GetComponentTypeHandle<Household>(true);
            ComponentTypeHandle<PropertyRenter> renterType = GetComponentTypeHandle<PropertyRenter>(true);
            BufferTypeHandle<Resources> resourcesType = GetBufferTypeHandle<Resources>(true);
            using (NativeArray<ArchetypeChunk> chunks = m_HouseholdQuery.ToArchetypeChunkArray(Allocator.Temp))
            {
                foreach (ArchetypeChunk chunk in chunks)
                {
                    NativeArray<Entity> ids = chunk.GetNativeArray(entityType);
                    NativeArray<Household> data = chunk.GetNativeArray(ref householdType);
                    NativeArray<PropertyRenter> renters = chunk.GetNativeArray(ref renterType);
                    BufferAccessor<Resources> resources = chunk.GetBufferAccessor(ref resourcesType);
                    for (int i = 0; i < chunk.Count && poorCount < total; i++)
                    {
                        if (renters[i].m_Rent <= 0)
                        {
                            continue;
                        }
                        int wealth = EconomyUtils.GetHouseholdTotalWealth(data[i], resources[i]);
                        if (wealth < limit)
                        {
                            keys[poorCount] = ((long)wealth << 32) | (uint)poorCount;
                            entities[poorCount] = ids[i];
                            rents[poorCount] = renters[i].m_Rent;
                            poorCount++;
                        }
                    }
                }
            }
            LastRenters = poorCount;
            int count = (int)math.round(poorCount * delivered);
            if (count > 0 && count < poorCount)
            {
                LivelihoodSupportSystem.SelectSmallest(keys, poorCount, count);
            }
            int monthly = 0;
            for (int i = 0; i < count; i++)
            {
                int slot = (int)(keys[i] & 0xFFFFFFFFL);
                Entity entity = entities[slot];
                int each = (int)math.round(rents[slot] * share);
                if (each <= 0)
                {
                    continue;
                }
                m_Supported[entity] = each;
                monthly += each;
                if (EntityManager.HasComponent<UpdateFrame>(entity))
                {
                    m_BySlice[EntityManager.GetSharedComponent<UpdateFrame>(entity).m_Index & (kSlices - 1)].Add(entity);
                }
            }
            LastSupported = m_Supported.Count;
            LastMonthly = monthly;
            m_Budget.SetRow(PolicyDefinitions.kRentSupportRow, false, (int)Game.City.ExpenseSource.SubsidyResidential, monthly);
        }

        /// <summary>Pays each supported household its share for the elapsed part of the month.</summary>
        private void Pay(float days)
        {
            if (days <= 0f)
            {
                return;
            }
            foreach (KeyValuePair<Entity, int> pair in m_Supported)
            {
                int amount = (int)math.round(pair.Value * days);
                if (amount > 0 && EntityManager.Exists(pair.Key) && EntityManager.HasBuffer<Resources>(pair.Key))
                {
                    EconomyUtils.AddResources(Resource.Money, amount, EntityManager.GetBuffer<Resources>(pair.Key));
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
                if (!m_Supported.TryGetValue(entity, out int each) || !EntityManager.Exists(entity) || !EntityManager.HasComponent<Household>(entity))
                {
                    continue;
                }
                Household household = EntityManager.GetComponentData<Household>(entity);
                household.m_Income += each;
                EntityManager.SetComponentData(entity, household);
            }
        }

        [Preserve]
        public RentSupportSystem()
        {
        }
    }
}
