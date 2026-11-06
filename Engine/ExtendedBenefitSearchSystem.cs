using Game;
using Game.Agents;
using Game.Citizens;
using Game.Common;
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
    /// §14.12 longer unemployment benefits: people search less hard while benefits last. Vanilla's job search waits a
    /// fixed 5,000–10,000 frames between attempts (`CitizenFindJobSystem`, constants the mod cannot change), so this
    /// system pushes the last attempt of some unemployed citizens back to now, but only during the extension the
    /// policy added (between the game's own and the current benefit days). Each run delays an eligible citizen with
    /// probability <see cref="kDelayChance"/>; against vanilla's cooldown this cuts search attempts by about 10%
    /// (simulated). Card, Chetty & Weber (2007): extending benefits from 20 to 30 weeks cut job finding by 5–9%.
    /// </summary>
    public partial class ExtendedBenefitSearchSystem : GameSystemBase
    {
        public const int kIntervalFrames = 4096;

        /// <summary>Chance per run that an eligible citizen's next search is pushed back.</summary>
        public const float kDelayChance = 0.1f;

        /// <summary>Totals since the session started (self-test and log).</summary>
        public static long EligibleSeen;
        public static long Delayed;

        private EntityQuery m_UnemployedQuery;
        private EntityQuery m_EconomyQuery;
        private SimulationSystem m_SimulationSystem;
        private PolicyEngineSystem m_Engine;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => kIntervalFrames;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Engine = World.GetOrCreateSystemManaged<PolicyEngineSystem>();
            // HasJobSeeker is enableable and disabled while a citizen is not searching, so the enabled state is
            // ignored here and checked per citizen.
            m_UnemployedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<HouseholdMember>(), ComponentType.ReadWrite<HasJobSeeker>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Worker>(), ComponentType.ReadOnly<Student>(), ComponentType.ReadOnly<HealthProblem>(),
                    ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(),
                },
                Options = EntityQueryOptions.IgnoreComponentEnabledState,
            });
            m_EconomyQuery = GetEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
            RequireForUpdate(m_EconomyQuery);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            // Days the game itself pays (the lever's original) and days paid now; nothing to do without an extension.
            float factor = m_Engine.LeverFactor(ParameterLevers.kUnemploymentDays);
            if (factor <= 1.0001f)
            {
                return;
            }
            float currentDays = m_EconomyQuery.GetSingleton<EconomyParameterData>().m_UnemploymentAllowanceMaxDays;
            int from = (int)math.ceil(currentDays / factor * PayWageSystem.kUpdatesPerDay);
            int to = (int)(currentDays * PayWageSystem.kUpdatesPerDay);
            if (to <= from)
            {
                return;
            }
            long start = Perf.Start();
            uint now = m_SimulationSystem.frameIndex;
            var random = Random.CreateFromIndex(now);
            using (NativeArray<Entity> entities = m_UnemployedQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Citizen> citizens = m_UnemployedQuery.ToComponentDataArray<Citizen>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Citizen citizen = citizens[i];
                    // Paid in the extension: the counter rises only while benefits are paid and stops at the limit.
                    if (citizen.m_UnemploymentCounter < from || citizen.m_UnemploymentCounter >= to)
                    {
                        continue;
                    }
                    CitizenAge age = citizen.GetAge();
                    if (age == CitizenAge.Child || age == CitizenAge.Elderly || EntityManager.IsComponentEnabled<HasJobSeeker>(entities[i]))
                    {
                        continue;
                    }
                    EligibleSeen++;
                    if (random.NextFloat() >= kDelayChance)
                    {
                        continue;
                    }
                    HasJobSeeker seeker = EntityManager.GetComponentData<HasJobSeeker>(entities[i]);
                    if (seeker.m_Seeker != Entity.Null)
                    {
                        continue;
                    }
                    seeker.m_LastJobSeekFrameIndex = now;
                    EntityManager.SetComponentData(entities[i], seeker);
                    Delayed++;
                }
            }
            Perf.Stop("ExtendedBenefitSearch", start);
        }
    }
}
