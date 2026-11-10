using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.City;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// When each building was last built or rebuilt, for the building codes (2026-11-10 user decision, option 1): a code
    /// reaches the buildings built or levelled up after it started (codes apply to new building and major renovation;
    /// Aroonruengsawat et al. 2012), plus the few others renovated each year (about 1% of the stock, European Commission
    /// 2019; IEA). In the game a new zoned building and a level-up both put the building under construction
    /// (<c>BuildingUpkeepSystem</c> adds <see cref="Game.Objects.UnderConstruction"/> to the same entity); a placed service building
    /// appears at once (<see cref="Created"/>). The frames are saved with the city (<see cref="BuildingWorks"/>).
    /// </summary>
    public sealed partial class BuildingWorksSystem : GameSystemBase
    {
        // Under construction lasts far longer than this, so a poll this often sees every one.
        private const uint kPollFrames = 512;

        // Entries for buildings that are gone are dropped once a game day.
        private const uint kPruneFrames = 262144;

        // Loading a city may mark its buildings as created; those are not new.
        private const uint kIgnoreAfterLoadFrames = 600;

        private SimulationSystem m_SimulationSystem;
        private CitySystem m_CitySystem;
        private EntityQuery m_CreatedQuery;
        private EntityQuery m_ConstructionQuery;
        private readonly Dictionary<Entity, int> m_Index = new Dictionary<Entity, int>();
        private bool m_IndexBuilt;
        private uint m_LoadFrame;
        private uint m_LastPoll;
        private uint m_LastPrune;
        private bool m_Loaded;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_CreatedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<Created>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            m_ConstructionQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<Game.Objects.UnderConstruction>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        [Preserve]
        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_Index.Clear();
            m_IndexBuilt = false;
            m_Loaded = false;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || ModCleanup.Done)
            {
                return;
            }
            uint frame = m_SimulationSystem.frameIndex;
            if (!m_Loaded)
            {
                m_Loaded = true;
                m_LoadFrame = frame;
                m_LastPoll = frame;
                m_LastPrune = frame;
            }
            bool created = !m_CreatedQuery.IsEmptyIgnoreFilter && frame - m_LoadFrame >= kIgnoreAfterLoadFrames;
            bool poll = frame - m_LastPoll >= kPollFrames;
            bool prune = frame - m_LastPrune >= kPruneFrames;
            if (!created && !poll && !prune)
            {
                return;
            }
            long start = Perf.Start();
            if (!EntityManager.HasBuffer<BuildingWorks>(city))
            {
                EntityManager.AddBuffer<BuildingWorks>(city);
            }
            DynamicBuffer<BuildingWorks> works = EntityManager.GetBuffer<BuildingWorks>(city);
            if (prune)
            {
                m_LastPrune = frame;
                for (int i = works.Length - 1; i >= 0; i--)
                {
                    Entity building = works[i].m_Building;
                    if (building == Entity.Null || !EntityManager.Exists(building) || !EntityManager.HasComponent<Building>(building))
                    {
                        works.RemoveAtSwapBack(i);
                    }
                }
                m_IndexBuilt = false;
            }
            if (!m_IndexBuilt)
            {
                m_Index.Clear();
                for (int i = 0; i < works.Length; i++)
                {
                    m_Index[works[i].m_Building] = i;
                }
                m_IndexBuilt = true;
            }
            if (created)
            {
                Mark(works, m_CreatedQuery, frame);
            }
            if (poll)
            {
                m_LastPoll = frame;
                Mark(works, m_ConstructionQuery, frame);
            }
            Perf.Stop("BuildingWorks", start);
        }

        private void Mark(DynamicBuffer<BuildingWorks> works, EntityQuery query, uint frame)
        {
            using (NativeArray<Entity> buildings = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity building in buildings)
                {
                    if (m_Index.TryGetValue(building, out int i))
                    {
                        BuildingWorks entry = works[i];
                        entry.m_Frame = frame;
                        works[i] = entry;
                    }
                    else
                    {
                        m_Index[building] = works.Length;
                        works.Add(new BuildingWorks { m_Building = building, m_Frame = frame });
                    }
                }
            }
        }

        /// <summary>Buildings built or rebuilt at or after <paramref name="frame"/> (0 before the first count).</summary>
        public int BuiltSince(uint frame)
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<BuildingWorks>(city))
            {
                return 0;
            }
            DynamicBuffer<BuildingWorks> works = EntityManager.GetBuffer<BuildingWorks>(city, true);
            if (frame == 0)
            {
                // Switched on before the frame was kept (older saves): every building counted so far.
                return works.Length;
            }
            int count = 0;
            for (int i = 0; i < works.Length; i++)
            {
                // Frames grow with the game; a difference keeps the order across a wrap.
                if ((int)(works[i].m_Frame - frame) >= 0)
                {
                    count++;
                }
            }
            return count;
        }

        [Preserve]
        public BuildingWorksSystem()
        {
        }
    }
}
