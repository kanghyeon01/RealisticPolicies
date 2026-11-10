using Game;
using Game.Citizens;
using Game.Common;
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
    /// Flexible working hours support (new policy, 2026-11-09; renamed by the user the same day: working hours are the
    /// companies' business, so the city supports firms that adopt staggered starts and each firm decides). A share of
    /// workplaces (slider 0-50%, each workplace always the same) adopt it; their workers start 0-90 minutes later, each
    /// worker always the same. The game fixes each citizen's start within ±1 hour (<c>WorkerSystem.GetWorkOffset</c>) and
    /// adds a "going to work" trip every time it looks while it is time to work and the citizen is not there. So, just
    /// before the game turns trips into journeys (<c>TripNeededSystem</c>), this takes that trip out again for a
    /// participant whose later start has not come yet; the game adds it back next time, and after the delay it goes
    /// through. Output counts the jobs filled, not the hours at work, so only traffic changes. Peak-hour traffic falls by
    /// about 5-10% (NCHRP Synthesis 73). Burst job every 16 frames, as the trip system.
    /// </summary>
    public sealed partial class FlexibleHoursSystem : GameSystemBase
    {
        /// <summary>Largest share of workers taking part (the slider's top, 50%).</summary>
        public const float kMaxShare = 0.5f;

        /// <summary>The latest start at the last update, as a fraction of a day (detail setting, 30-120 minutes; default 90).</summary>
        public float LastMaxDelayDays { get; private set; } = 1.5f / 24f;

        private PolicyEngineSystem m_Engine;
        private TimeSystem m_TimeSystem;
        private EntityQuery m_WorkerQuery;
        private EntityQuery m_EconomyQuery;

        private NativeArray<int> m_Held;
        private JobHandle m_Handle;
        private bool m_Pending;

        /// <summary>Trips held back at the last update (self-test), and the share of workers taking part.</summary>
        public int LastHeld { get; private set; }

        public float LastShare { get; private set; }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Engine = World.GetOrCreateSystemManaged<PolicyEngineSystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            // The citizens WorkerSystem sends to work.
            m_WorkerQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Worker>(), ComponentType.ReadOnly<Citizen>(), ComponentType.ReadWrite<TripNeeded>() },
                None = new[] { ComponentType.ReadOnly<TravelPurpose>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_EconomyQuery = GetEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
            m_Held = new NativeArray<int>(1, Allocator.Persistent);
            RequireForUpdate(m_EconomyQuery);
        }

        [Preserve]
        protected override void OnDestroy()
        {
            m_Handle.Complete();
            m_Held.Dispose();
            base.OnDestroy();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (m_Pending)
            {
                m_Handle.Complete();
                LastHeld = m_Held[0];
                m_Pending = false;
            }
            if (!PolicyCatalog.IsRegistered || ModCleanup.Done)
            {
                return;
            }
            PolicyDefinition definition = PolicyCatalog.Definition(PolicyCatalog.kFlexibleHours);
            float intensity = definition != null && definition.Prefab != Entity.Null ? m_Engine.LastIntensity(definition) : 0f;
            // The share asked for, reached as far as the policy is delivered.
            LastShare = kMaxShare * math.min(intensity, m_Engine.Delivered(PolicyCatalog.kFlexibleHours));
            if (definition != null)
            {
                LastMaxDelayDays = m_Engine.SecondValue(definition) / (24f * 60f);
            }
            if (LastShare <= 0f)
            {
                LastHeld = 0;
                return;
            }
            long start = Perf.Start();
            m_Held[0] = 0;
            m_Handle = new HoldJob
            {
                m_CitizenType = GetComponentTypeHandle<Citizen>(true),
                m_WorkerType = GetComponentTypeHandle<Worker>(true),
                m_TripType = GetBufferTypeHandle<TripNeeded>(false),
                m_Economy = m_EconomyQuery.GetSingleton<EconomyParameterData>(),
                m_TimeOfDay = m_TimeSystem.normalizedTime,
                m_Share = LastShare,
                m_MaxDelay = LastMaxDelayDays,
                m_Held = m_Held,
            }.Schedule(m_WorkerQuery, Dependency);
            Dependency = m_Handle;
            m_Pending = true;
            Perf.Stop("FlexibleHours", start);
        }

        /// <summary>
        /// Whether the citizen's workplace has adopted staggered starts (fixed per workplace), and the citizen's delay
        /// (0 to the latest start, as a fraction of a day, fixed per citizen).
        /// </summary>
        public static bool Participates(Citizen citizen, Worker worker, float share, float maxDelay, out float delay)
        {
            var firm = Random.CreateFromIndex((uint)worker.m_Workplace.Index ^ 0xF1E8u);
            bool part = firm.NextFloat() < share;
            delay = Random.CreateFromIndex(citizen.m_PseudoRandom ^ 0x5A61u).NextFloat() * maxDelay;
            return part;
        }

        [BurstCompile]
        private struct HoldJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<Citizen> m_CitizenType;
            [ReadOnly] public ComponentTypeHandle<Worker> m_WorkerType;
            public BufferTypeHandle<TripNeeded> m_TripType;
            public EconomyParameterData m_Economy;
            public float m_TimeOfDay;
            public float m_Share;
            public float m_MaxDelay;
            public NativeArray<int> m_Held;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Citizen> citizens = chunk.GetNativeArray(ref m_CitizenType);
                NativeArray<Worker> workers = chunk.GetNativeArray(ref m_WorkerType);
                BufferAccessor<TripNeeded> trips = chunk.GetBufferAccessor(ref m_TripType);
                for (int i = 0; i < chunk.Count; i++)
                {
                    DynamicBuffer<TripNeeded> buffer = trips[i];
                    Worker worker = workers[i];
                    if (buffer.Length == 0 || !Participates(citizens[i], worker, m_Share, m_MaxDelay, out float delay))
                    {
                        continue;
                    }
                    float begin = WorkerSystem.GetTimeToWork(citizens[i], worker, ref m_Economy, true).x;
                    if (math.frac(m_TimeOfDay - begin + 1f) >= delay)
                    {
                        continue;
                    }
                    for (int t = buffer.Length - 1; t >= 0; t--)
                    {
                        if (buffer[t].m_Purpose == Purpose.GoingToWork && buffer[t].m_TargetAgent == worker.m_Workplace)
                        {
                            buffer.RemoveAt(t);
                            m_Held[0]++;
                        }
                    }
                }
            }
        }

        [Preserve]
        public FlexibleHoursSystem()
        {
        }
    }
}
