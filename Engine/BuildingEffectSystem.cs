using Colossal.Entities;
using Game;
using Game.Buildings;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// Policy effects on single buildings' efficiency (docs/PLAN.md "8개 항목 구현 설계", 2026-11-09). The game's output
    /// reads each building's efficiency, the product of its efficiency factors (commerce in ServiceCompanySystem, industry
    /// and offices in ProcessingCompanySystem, city services in their own systems). The game's own systems rewrite their
    /// factors as they go; after them, this multiplies one factor per kind of building by the policy's value:
    /// - commercial buildings, "employee happiness": strikes and collective closures in commerce, 1 − share × 0.2;
    /// - city service buildings, "service budget": performance management, 1 + 0.03 × delivered;
    /// - industrial and office buildings, their city-effect factor: corporate rescue, 1 − 0.5 × share of the industry's
    ///   firms kept alive below the game's own bankruptcy limit (Adalet McGowan et al. 2018).
    /// It remembers, per building, the game's value and what it wrote; a factor still holding what it wrote has not been
    /// rewritten and is left alone, so the value is never multiplied twice. Burst jobs, single-threaded over the chunks.
    /// </summary>
    public sealed partial class BuildingEffectSystem : GameSystemBase
    {
        private const float kPerformanceGain = 0.03f;
        private const float kZombieDrag = 0.5f;
        private const uint kRescueRecountFrames = 8192;

        private PolicyEngineSystem m_Engine;
        private SimulationSystem m_SimulationSystem;
        private ResourceSystem m_ResourceSystem;
        private EntityQuery m_CommercialQuery;
        private EntityQuery m_ServiceQuery;
        private EntityQuery m_IndustryQuery;
        private EntityQuery m_CompanyQuery;

        private NativeParallelHashMap<Entity, float2> m_CommercialTracked;
        private NativeParallelHashMap<Entity, float2> m_ServiceTracked;
        private NativeParallelHashMap<Entity, float2> m_IndustryTracked;
        private NativeArray<float> m_ResourceFactor;
        private uint m_LastRescueCount;
        private bool m_HasRescueCount;

        /// <summary>Multipliers in force at the last update (self-test, logs).</summary>
        public float LastCommercialFactor { get; private set; } = 1f;

        public float LastServiceFactor { get; private set; } = 1f;

        public float LastWorstIndustryFactor { get; private set; } = 1f;

        public int LastZombies { get; private set; }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 32;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Engine = World.GetOrCreateSystemManaged<PolicyEngineSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_ResourceSystem = World.GetOrCreateSystemManaged<ResourceSystem>();
            m_CommercialQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Building>(), ComponentType.ReadWrite<Efficiency>(), ComponentType.ReadOnly<CommercialProperty>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>() },
            });
            m_ServiceQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Building>(), ComponentType.ReadWrite<Efficiency>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[]
                {
                    ComponentType.ReadOnly<CommercialProperty>(), ComponentType.ReadOnly<IndustrialProperty>(), ComponentType.ReadOnly<OfficeProperty>(),
                    ComponentType.ReadOnly<ResidentialProperty>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>(),
                },
            });
            m_IndustryQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Building>(), ComponentType.ReadWrite<Efficiency>(), ComponentType.ReadOnly<Renter>() },
                Any = new[] { ComponentType.ReadOnly<IndustrialProperty>(), ComponentType.ReadOnly<OfficeProperty>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>() },
            });
            m_CompanyQuery = GetEntityQuery(new EntityQueryDesc
            {
                // The companies CompanyMoveAwaySystem judges, without the shops (industry and offices only).
                All = new[] { ComponentType.ReadOnly<Game.Companies.ProcessingCompany>(), ComponentType.ReadOnly<PropertyRenter>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Resources>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Game.Companies.ExtractorCompany>(), ComponentType.ReadOnly<ServiceAvailable>(), ComponentType.ReadOnly<Game.Agents.MovingAway>(),
                    ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>(),
                },
            });
            m_CommercialTracked = new NativeParallelHashMap<Entity, float2>(1024, Allocator.Persistent);
            m_ServiceTracked = new NativeParallelHashMap<Entity, float2>(1024, Allocator.Persistent);
            m_IndustryTracked = new NativeParallelHashMap<Entity, float2>(1024, Allocator.Persistent);
            m_ResourceFactor = new NativeArray<float>(EconomyUtils.ResourceCount, Allocator.Persistent);
            for (int i = 0; i < m_ResourceFactor.Length; i++)
            {
                m_ResourceFactor[i] = 1f;
            }
        }

        [Preserve]
        protected override void OnDestroy()
        {
            Dependency.Complete();
            m_CommercialTracked.Dispose();
            m_ServiceTracked.Dispose();
            m_IndustryTracked.Dispose();
            m_ResourceFactor.Dispose();
            base.OnDestroy();
        }

        [Preserve]
        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            Dependency.Complete();
            m_CommercialTracked.Clear();
            m_ServiceTracked.Clear();
            m_IndustryTracked.Clear();
            for (int i = 0; i < m_ResourceFactor.Length; i++)
            {
                m_ResourceFactor[i] = 1f;
            }
            m_HasRescueCount = false;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (!PolicyCatalog.IsRegistered || m_Engine.StepCount == 0 || ModCleanup.Done)
            {
                return;
            }
            long start = Perf.Start();
            uint frame = m_SimulationSystem.frameIndex;
            if (!m_HasRescueCount || frame - m_LastRescueCount >= kRescueRecountFrames)
            {
                Dependency.Complete();
                CountZombies();
                m_LastRescueCount = frame;
                m_HasRescueCount = true;
            }

            LastCommercialFactor = 1f - ProtestSystem.CommercialLoss;
            LastServiceFactor = 1f + kPerformanceGain * Delivered(PolicyCatalog.kPerformanceManagement);

            BufferTypeHandle<Efficiency> efficiencyType = GetBufferTypeHandle<Efficiency>(false);
            EntityTypeHandle entityType = GetEntityTypeHandle();
            JobHandle handle = new ConstantFactorJob
            {
                m_EntityType = entityType,
                m_EfficiencyType = efficiencyType,
                m_Tracked = m_CommercialTracked,
                m_Factor = EfficiencyFactor.EmployeeHappiness,
                m_Multiplier = LastCommercialFactor,
                m_PrefabType = GetComponentTypeHandle<PrefabRef>(true),
                m_ServiceObjects = GetComponentLookup<ServiceObjectData>(true),
                m_ServicesOnly = false,
            }.Schedule(m_CommercialQuery, Dependency);
            handle = new ConstantFactorJob
            {
                m_EntityType = entityType,
                m_EfficiencyType = efficiencyType,
                m_Tracked = m_ServiceTracked,
                m_Factor = EfficiencyFactor.ServiceBudget,
                m_Multiplier = LastServiceFactor,
                m_PrefabType = GetComponentTypeHandle<PrefabRef>(true),
                m_ServiceObjects = GetComponentLookup<ServiceObjectData>(true),
                m_ServicesOnly = true,
            }.Schedule(m_ServiceQuery, handle);
            handle = new ResourceFactorJob
            {
                m_EntityType = entityType,
                m_EfficiencyType = efficiencyType,
                m_RenterType = GetBufferTypeHandle<Renter>(true),
                m_OfficeType = GetComponentTypeHandle<OfficeProperty>(true),
                m_Prefabs = GetComponentLookup<PrefabRef>(true),
                m_Processes = GetComponentLookup<IndustrialProcessData>(true),
                m_Tracked = m_IndustryTracked,
                m_ResourceFactor = m_ResourceFactor,
            }.Schedule(m_IndustryQuery, handle);
            Dependency = handle;
            Perf.Stop("BuildingEffects", start);
        }

        private float Delivered(string id) => m_Engine.Delivered(id);

        /// <summary>Buildings tracked for each effect (self-test).</summary>
        public int ServiceTrackedCount
        {
            get
            {
                Dependency.Complete();
                return m_ServiceTracked.Count();
            }
        }

        public int CommercialTrackedCount
        {
            get
            {
                Dependency.Complete();
                return m_CommercialTracked.Count();
            }
        }

        /// <summary>
        /// Self-test: city service buildings whose factor still holds what this wrote but not game value x the current
        /// factor; with the factor back at 1, every building still tracked counts.
        /// </summary>
        public int CountServiceMismatch()
        {
            Dependency.Complete();
            if (math.abs(LastServiceFactor - 1f) < 1e-5f)
            {
                return m_ServiceTracked.Count();
            }
            int wrong = 0;
            using (NativeArray<Entity> keys = m_ServiceTracked.GetKeyArray(Allocator.Temp))
            {
                foreach (Entity entity in keys)
                {
                    if (!EntityManager.Exists(entity) || !EntityManager.HasBuffer<Efficiency>(entity))
                    {
                        continue;
                    }
                    float2 entry = m_ServiceTracked[entity];
                    float current = BuildingUtils.GetEfficiencyFactor(EntityManager.GetBuffer<Efficiency>(entity, true), EfficiencyFactor.ServiceBudget);
                    float expected = math.max(0f, entry.x * LastServiceFactor);
                    if (math.abs(current - entry.y) < 1e-5f && math.abs(expected - 1f) > 0.001f && math.abs(entry.y - expected) > 1e-3f)
                    {
                        wrong++;
                    }
                }
            }
            return wrong;
        }

        /// <summary>
        /// Corporate rescue: per industry (output resource), the share of its firms worth less than the game's own
        /// bankruptcy limit (before the policy moved it) — kept alive only by the policy.
        /// </summary>
        private void CountZombies()
        {
            for (int i = 0; i < m_ResourceFactor.Length; i++)
            {
                m_ResourceFactor[i] = 1f;
            }
            LastWorstIndustryFactor = 1f;
            LastZombies = 0;
            if (Delivered(PolicyCatalog.kCorporateRescue) <= 0f
                || !ParameterLevers.TryGet(ParameterLevers.kBankruptcyLimit, out IParameterLever lever)
                || !(lever is ScaledFloatLever<EconomyParameterData> scaled) || !scaled.HasOriginal)
            {
                return;
            }
            float limit = scaled.Original;
            int[] firms = new int[m_ResourceFactor.Length];
            int[] zombies = new int[m_ResourceFactor.Length];
            m_CompanyQuery.CompleteDependency();
            ResourcePrefabs resourcePrefabs = m_ResourceSystem.GetPrefabs();
            ComponentLookup<ResourceData> resourceDatas = GetComponentLookup<ResourceData>(true);
            ComponentTypeHandle<PrefabRef> prefabType = GetComponentTypeHandle<PrefabRef>(true);
            BufferTypeHandle<Resources> resourcesType = GetBufferTypeHandle<Resources>(true);
            using (NativeArray<ArchetypeChunk> chunks = m_CompanyQuery.ToArchetypeChunkArray(Allocator.Temp))
            {
                foreach (ArchetypeChunk chunk in chunks)
                {
                    NativeArray<PrefabRef> prefabs = chunk.GetNativeArray(ref prefabType);
                    BufferAccessor<Resources> resources = chunk.GetBufferAccessor(ref resourcesType);
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        if (!EntityManager.TryGetComponent(prefabs[i].m_Prefab, out IndustrialProcessData process))
                        {
                            continue;
                        }
                        int r = EconomyUtils.GetResourceIndex(process.m_Output.m_Resource);
                        if (r < 0 || r >= firms.Length)
                        {
                            continue;
                        }
                        firms[r]++;
                        int worth = EconomyUtils.GetCompanyTotalWorth(true, process, resources[i], resourcePrefabs, ref resourceDatas);
                        if (worth < limit)
                        {
                            zombies[r]++;
                            LastZombies++;
                        }
                    }
                }
            }
            for (int r = 0; r < firms.Length; r++)
            {
                if (firms[r] > 0 && zombies[r] > 0)
                {
                    float factor = math.saturate(1f - kZombieDrag * zombies[r] / (float)firms[r]);
                    m_ResourceFactor[r] = factor;
                    LastWorstIndustryFactor = math.min(LastWorstIndustryFactor, factor);
                }
            }
        }

        /// <summary>Writes game value × multiplier into the factor, once per rewrite by the game; gives the game value back at 1.</summary>
        private static void Apply(DynamicBuffer<Efficiency> buffer, Entity entity, EfficiencyFactor factor, float multiplier, NativeParallelHashMap<Entity, float2> tracked)
        {
            float current = BuildingUtils.GetEfficiencyFactor(buffer, factor);
            bool known = tracked.TryGetValue(entity, out float2 entry);
            float game = known && math.abs(current - entry.y) < 1e-5f ? entry.x : current;
            if (math.abs(multiplier - 1f) < 1e-5f)
            {
                if (known)
                {
                    if (math.abs(current - entry.y) < 1e-5f)
                    {
                        BuildingUtils.SetEfficiencyFactor(buffer, factor, game);
                    }
                    tracked.Remove(entity);
                }
                return;
            }
            float written = math.max(0f, game * multiplier);
            if (known && math.abs(current - entry.y) < 1e-5f && math.abs(written - entry.y) < 1e-5f)
            {
                return;
            }
            BuildingUtils.SetEfficiencyFactor(buffer, factor, written);
            // SetEfficiencyFactor drops a factor of 1; what the buffer holds then reads back as 1.
            tracked[entity] = new float2(game, BuildingUtils.GetEfficiencyFactor(buffer, factor));
        }

        [BurstCompile]
        private struct ConstantFactorJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle m_EntityType;
            public BufferTypeHandle<Efficiency> m_EfficiencyType;
            [ReadOnly] public ComponentTypeHandle<PrefabRef> m_PrefabType;
            [ReadOnly] public ComponentLookup<ServiceObjectData> m_ServiceObjects;
            public NativeParallelHashMap<Entity, float2> m_Tracked;
            public EfficiencyFactor m_Factor;
            public float m_Multiplier;
            public bool m_ServicesOnly;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);
                BufferAccessor<Efficiency> efficiencies = chunk.GetBufferAccessor(ref m_EfficiencyType);
                NativeArray<PrefabRef> prefabs = m_ServicesOnly ? chunk.GetNativeArray(ref m_PrefabType) : default;
                for (int i = 0; i < chunk.Count; i++)
                {
                    if (m_ServicesOnly && !m_ServiceObjects.HasComponent(prefabs[i].m_Prefab))
                    {
                        continue;
                    }
                    Apply(efficiencies[i], entities[i], m_Factor, m_Multiplier, m_Tracked);
                }
            }
        }

        [BurstCompile]
        private struct ResourceFactorJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle m_EntityType;
            public BufferTypeHandle<Efficiency> m_EfficiencyType;
            [ReadOnly] public BufferTypeHandle<Renter> m_RenterType;
            [ReadOnly] public ComponentTypeHandle<OfficeProperty> m_OfficeType;
            [ReadOnly] public ComponentLookup<PrefabRef> m_Prefabs;
            [ReadOnly] public ComponentLookup<IndustrialProcessData> m_Processes;
            [ReadOnly] public NativeArray<float> m_ResourceFactor;
            public NativeParallelHashMap<Entity, float2> m_Tracked;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);
                BufferAccessor<Efficiency> efficiencies = chunk.GetBufferAccessor(ref m_EfficiencyType);
                BufferAccessor<Renter> renters = chunk.GetBufferAccessor(ref m_RenterType);
                EfficiencyFactor factor = chunk.Has(ref m_OfficeType) ? EfficiencyFactor.CityModifierOfficeEfficiency : EfficiencyFactor.CityModifierIndustrialEfficiency;
                for (int i = 0; i < chunk.Count; i++)
                {
                    float multiplier = 1f;
                    DynamicBuffer<Renter> tenants = renters[i];
                    for (int t = 0; t < tenants.Length; t++)
                    {
                        if (m_Prefabs.TryGetComponent(tenants[t].m_Renter, out PrefabRef prefab) && m_Processes.TryGetComponent(prefab.m_Prefab, out IndustrialProcessData process))
                        {
                            int r = EconomyUtils.GetResourceIndex(process.m_Output.m_Resource);
                            if (r >= 0 && r < m_ResourceFactor.Length)
                            {
                                multiplier = m_ResourceFactor[r];
                            }
                            break;
                        }
                    }
                    Apply(efficiencies[i], entities[i], factor, multiplier, m_Tracked);
                }
            }
        }

        [Preserve]
        public BuildingEffectSystem()
        {
        }
    }
}
