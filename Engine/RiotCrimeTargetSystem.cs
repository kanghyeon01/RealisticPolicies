using System.Collections.Generic;
using Game;
using Game.Citizens;
using Game.Simulation;
using Unity.Entities;
using UnityEngine.Scripting;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// Sends a rioter's crime to the riot (2026-11-10 user decision "당연히 폭동 현장 근처로"). The game's criminal system
    /// gives a new criminal a crime trip with no target, and the trip system then finds the building with the most crime
    /// anywhere in the city (self-test: 1.8 and 5.3 km away). A trip with a target goes straight there. So, just before the
    /// trip system reads trips (same 16-frame cadence, run before it), this writes the riot building chosen for the crime
    /// into that trip. A trip given at the start instead did not hold: the trip system could take it before the game had
    /// made the citizen a criminal, and the criminal system then added its own.
    /// </summary>
    public sealed partial class RiotCrimeTargetSystem : GameSystemBase
    {
        /// <summary>Riot buildings chosen for crimes, by criminal, until the game's crime trip has been pointed there.</summary>
        internal static readonly Dictionary<Entity, (Entity target, uint until)> Pending = new Dictionary<Entity, (Entity, uint)>();

        /// <summary>Crime trips pointed at a riot building since loading (self-test, logs).</summary>
        public static int Pointed { get; private set; }

        /// <summary>
        /// What became of each rioter's pending target since loading (self-test diagnosis 2026-11-10: crime scenes far
        /// from the riot): pointed, the game's trip already had a target, given up, and at which frame.
        /// </summary>
        internal static readonly Dictionary<Entity, string> Outcomes = new Dictionary<Entity, string>();

        private const uint kKeepFrames = 16384;

        private SimulationSystem m_SimulationSystem;
        private readonly List<Entity> m_Done = new List<Entity>();

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        /// <summary>Remember the riot building for this rioter's crime.</summary>
        internal static void Add(Entity citizen, Entity target, uint frame)
        {
            if (citizen != Entity.Null && target != Entity.Null)
            {
                Pending[citizen] = (target, frame + kKeepFrames);
                if (Outcomes.Count > 1024)
                {
                    Outcomes.Clear();
                }
                Outcomes[citizen] = $"pending since frame {frame}";
            }
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
        }

        [Preserve]
        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            Pending.Clear();
            Outcomes.Clear();
            Pointed = 0;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (Pending.Count == 0)
            {
                return;
            }
            uint frame = m_SimulationSystem.frameIndex;
            m_Done.Clear();
            foreach (KeyValuePair<Entity, (Entity target, uint until)> pair in Pending)
            {
                Entity citizen = pair.Key;
                if (frame > pair.Value.until || !EntityManager.Exists(citizen) || !EntityManager.Exists(pair.Value.target))
                {
                    Outcomes[citizen] = $"given up at frame {frame} ({(frame > pair.Value.until ? "no crime trip in time" : !EntityManager.Exists(citizen) ? "citizen gone" : "building gone")}, criminal {EntityManager.HasComponent<Criminal>(citizen)})";
                    m_Done.Add(citizen);
                    continue;
                }
                if (!EntityManager.HasComponent<Criminal>(citizen) || !EntityManager.HasBuffer<TripNeeded>(citizen))
                {
                    continue;
                }
                DynamicBuffer<TripNeeded> trips = EntityManager.GetBuffer<TripNeeded>(citizen);
                for (int i = 0; i < trips.Length; i++)
                {
                    TripNeeded trip = trips[i];
                    if (trip.m_Purpose == Purpose.Crime)
                    {
                        if (trip.m_TargetAgent == Entity.Null)
                        {
                            trip.m_TargetAgent = pair.Value.target;
                            trips[i] = trip;
                            Pointed++;
                            Outcomes[citizen] = $"pointed at frame {frame}";
                        }
                        else
                        {
                            Outcomes[citizen] = $"the game's crime trip already had target {trip.m_TargetAgent.Index} at frame {frame}";
                        }
                        m_Done.Add(citizen);
                        break;
                    }
                }
            }
            foreach (Entity citizen in m_Done)
            {
                Pending.Remove(citizen);
            }
        }

        [Preserve]
        public RiotCrimeTargetSystem()
        {
        }
    }
}
