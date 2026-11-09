using Game;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
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
    /// §14.17 lifelong learning, for the unemployed only (docs/PLAN.md "8개 항목 구현 설계", 2026-11-09). The game's adult
    /// entry values also send working people back to school, so the policy no longer moves them. Instead, unemployed
    /// adults start the game's own school search, the way <c>ApplyToSchoolSystem</c> does it: a seeker entity with
    /// <c>Owner</c>, <c>SchoolSeeker</c> and <c>CurrentBuilding</c>, and <c>HasSchoolSeeker</c> on the citizen. The game
    /// then finds the school, or drops the search when none has room. 3% of unemployed adults a day at full delivery,
    /// those already studying or searching, in hospital, elderly, at the top level or in the game's retry wait left out.
    /// Burst job over all citizens every 4096 frames.
    /// </summary>
    public sealed partial class LifelongLearningSystem : GameSystemBase
    {
        private const float kDailyShare = 0.03f;
        private const int kInterval = 4096;

        private PolicyEngineSystem m_Engine;
        private SimulationSystem m_SimulationSystem;
        private EndFrameBarrier m_EndFrameBarrier;
        private EntityQuery m_CitizenQuery;

        /// <summary>Seekers started at the last update (self-test, logs) and in total since loading.</summary>
        public int LastStarted { get; private set; }

        public int TotalStarted { get; private set; }

        private NativeArray<int> m_Count;
        private JobHandle m_CountHandle;
        private bool m_CountPending;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => kInterval;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Engine = World.GetOrCreateSystemManaged<PolicyEngineSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_CitizenQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<HouseholdMember>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Worker>(), ComponentType.ReadOnly<Game.Citizens.Student>(), ComponentType.ReadOnly<HasJobSeeker>(),
                    ComponentType.ReadOnly<HasSchoolSeeker>(), ComponentType.ReadOnly<HealthProblem>(), ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            m_Count = new NativeArray<int>(1, Allocator.Persistent);
        }

        [Preserve]
        protected override void OnDestroy()
        {
            m_CountHandle.Complete();
            m_Count.Dispose();
            base.OnDestroy();
        }

        [Preserve]
        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_CountHandle.Complete();
            m_CountPending = false;
            LastStarted = 0;
            TotalStarted = 0;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (m_CountPending)
            {
                m_CountHandle.Complete();
                LastStarted = m_Count[0];
                TotalStarted += LastStarted;
                m_CountPending = false;
            }
            if (!PolicyCatalog.IsRegistered || ModCleanup.Done)
            {
                return;
            }
            float delivered = m_Engine.Delivered(PolicyCatalog.kLifelongLearning);
            if (delivered <= 0f)
            {
                LastStarted = 0;
                return;
            }
            long start = Perf.Start();
            uint frame = m_SimulationSystem.frameIndex;
            m_Count[0] = 0;
            JobHandle handle = new StartSearchJob
            {
                m_EntityType = GetEntityTypeHandle(),
                m_CitizenType = GetComponentTypeHandle<Citizen>(true),
                m_MemberType = GetComponentTypeHandle<HouseholdMember>(true),
                m_Households = GetComponentLookup<Household>(true),
                m_Renters = GetComponentLookup<PropertyRenter>(true),
                m_Tourists = GetComponentLookup<TouristHousehold>(true),
                m_MovingAways = GetComponentLookup<MovingAway>(true),
                m_Cooldowns = GetComponentLookup<SchoolSeekerCooldown>(true),
                m_Chance = kDailyShare * delivered * kInterval / PolicyEngineSystem.kFramesPerDay,
                m_Frame = frame,
                m_Seed = frame ^ 0x11FE1EA5u,
                m_Count = m_Count,
                m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer(),
            }.Schedule(m_CitizenQuery, Dependency);
            m_EndFrameBarrier.AddJobHandleForProducer(handle);
            Dependency = handle;
            m_CountHandle = handle;
            m_CountPending = true;
            Perf.Stop("LifelongLearning", start);
        }

        [BurstCompile]
        private struct StartSearchJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle m_EntityType;
            [ReadOnly] public ComponentTypeHandle<Citizen> m_CitizenType;
            [ReadOnly] public ComponentTypeHandle<HouseholdMember> m_MemberType;
            [ReadOnly] public ComponentLookup<Household> m_Households;
            [ReadOnly] public ComponentLookup<PropertyRenter> m_Renters;
            [ReadOnly] public ComponentLookup<TouristHousehold> m_Tourists;
            [ReadOnly] public ComponentLookup<MovingAway> m_MovingAways;
            [ReadOnly] public ComponentLookup<SchoolSeekerCooldown> m_Cooldowns;
            public float m_Chance;
            public uint m_Frame;
            public uint m_Seed;
            public NativeArray<int> m_Count;
            public EntityCommandBuffer m_CommandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);
                NativeArray<Citizen> citizens = chunk.GetNativeArray(ref m_CitizenType);
                NativeArray<HouseholdMember> members = chunk.GetNativeArray(ref m_MemberType);
                var random = Random.CreateFromIndex(m_Seed + (uint)unfilteredChunkIndex);
                for (int i = 0; i < chunk.Count; i++)
                {
                    Citizen citizen = citizens[i];
                    if (citizen.GetAge() != CitizenAge.Adult || random.NextFloat() >= m_Chance)
                    {
                        continue;
                    }
                    Entity entity = entities[i];
                    if (m_Cooldowns.TryGetComponent(entity, out SchoolSeekerCooldown cooldown) && m_Frame < cooldown.m_SimulationFrame + ApplyToSchoolSystem.kCoolDown)
                    {
                        continue;
                    }
                    // The next level, as the game picks it: adults go to high school and up; a first try at
                    // college goes straight to university.
                    int level = citizen.GetEducationLevel() + 1;
                    if (level == (int)Game.Prefabs.SchoolLevel.College && citizen.GetFailedEducationCount() == 0)
                    {
                        level = (int)Game.Prefabs.SchoolLevel.University;
                    }
                    if (level < (int)Game.Prefabs.SchoolLevel.HighSchool || level > (int)Game.Prefabs.SchoolLevel.University)
                    {
                        continue;
                    }
                    Entity household = members[i].m_Household;
                    if (!CitizenUtils.HasMovedIn(household, m_Households) || !m_Renters.TryGetComponent(household, out PropertyRenter renter)
                        || m_Tourists.HasComponent(household) || m_MovingAways.HasComponent(household))
                    {
                        continue;
                    }
                    Entity seeker = m_CommandBuffer.CreateEntity();
                    m_CommandBuffer.AddComponent(seeker, new Owner { m_Owner = entity });
                    m_CommandBuffer.AddComponent(seeker, new SchoolSeeker { m_Level = level });
                    m_CommandBuffer.AddComponent(seeker, new CurrentBuilding { m_CurrentBuilding = renter.m_Property });
                    m_CommandBuffer.AddComponent(entity, new HasSchoolSeeker { m_Seeker = seeker });
                    m_Count[0]++;
                }
            }
        }

        [Preserve]
        public LifelongLearningSystem()
        {
        }
    }
}
