using System.Collections.Generic;
using Colossal.Entities;
using Game.Buildings;
using Game.Common;
using Game.Citizens;
using Game.Companies;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticPolicies.Engine
{
    /// <summary>Civil-service series (직렬); public buildings are assigned by their function component (docs/PLAN.md §5.1).</summary>
    public enum ServiceSeries : byte
    {
        GeneralAdministration,
        Police,
        Corrections,
        FireAndDisaster,
        Education,
        Research,
        Health,
        Environment,
        FacilityMaintenance,
        EngineeringUtilities,
        Transit,
        PostAndTelecom,
        SocialWelfare,
        Tax,
        Count,
        None = byte.MaxValue,
    }

    public readonly struct SeriesWeight
    {
        public readonly ServiceSeries Series;
        public readonly float Weight;

        public SeriesWeight(ServiceSeries series, float weight)
        {
            Series = series;
            Weight = weight;
        }
    }

    /// <summary>
    /// Counts staff per series from the city's service buildings, and the targets policies scale with.
    /// The vanilla statistics are per service and merge police with administration, so buildings are read
    /// directly. This walks every staffed building, so the engine runs it only once a day, after loading
    /// and when a mod policy changes.
    /// </summary>
    public sealed class ServiceSeriesCensus
    {
        private readonly EntityManager m_EntityManager;
        private readonly CityServiceBudgetSystem m_BudgetSystem;
        private readonly CityStatisticsSystem m_StatisticsSystem;
        private readonly EntityQuery m_BuildingQuery;
        private readonly EntityQuery[] m_GroupQueries;
        private readonly float[] m_GroupCounts = new float[(int)TargetGroup.Count];

        // Prefab -> series; prefabs never change their components, so the classification is cached.
        private readonly Dictionary<Entity, ServiceSeries> m_PrefabSeries = new Dictionary<Entity, ServiceSeries>();
        private readonly HashSet<Entity> m_WelfarePrefabs = new HashSet<Entity>();
        private readonly Dictionary<Entity, float> m_ServiceBudget = new Dictionary<Entity, float>();

        private readonly int[] m_Workers = new int[(int)ServiceSeries.Count];
        private readonly int[] m_MaxWorkers = new int[(int)ServiceSeries.Count];
        private readonly float[] m_BudgetWeighted = new float[(int)ServiceSeries.Count];

        /// <summary>Capacity C_j per series (formula ①); 0 when the series has no buildings.</summary>
        public float[] Capacity { get; } = new float[(int)ServiceSeries.Count];

        /// <summary>Staff available for policy work per series: workers × budget^(1-α) (formula ④).</summary>
        public float[] AvailableStaff { get; } = new float[(int)ServiceSeries.Count];

        public int IndustrialCompanies => (int)m_GroupCounts[(int)TargetGroup.IndustrialCompanies];

        public int Workers(ServiceSeries series) => m_Workers[(int)series];

        public int MaxWorkers(ServiceSeries series) => m_MaxWorkers[(int)series];

        /// <param name="groupQueries">One query per <see cref="TargetGroup"/> (index 0, None, is unused); see <see cref="GroupQueryDesc"/>.</param>
        public ServiceSeriesCensus(EntityManager entityManager, CityServiceBudgetSystem budgetSystem, CityStatisticsSystem statisticsSystem, EntityQuery buildingQuery, EntityQuery[] groupQueries)
        {
            m_EntityManager = entityManager;
            m_BudgetSystem = budgetSystem;
            m_StatisticsSystem = statisticsSystem;
            m_BuildingQuery = buildingQuery;
            m_GroupQueries = groupQueries;
        }

        /// <summary>Welfare offices in the city, whatever staff they are assigned to; they give the welfare coverage.</summary>
        public int WelfareOffices { get; private set; }

        public static EntityQueryDesc BuildingQueryDesc => new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<WorkProvider>(),
                ComponentType.ReadOnly<Employee>(),
                ComponentType.ReadOnly<PrefabRef>(),
            },
            None = new[]
            {
                ComponentType.ReadOnly<Temp>(),
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Destroyed>(),
            },
        };

        /// <summary>Members of a group that live or work in the city (tourists, commuters and offices kept apart).</summary>
        public static EntityQueryDesc GroupQueryDesc(TargetGroup group)
        {
            ComponentType member;
            ComponentType[] exclude;
            switch (group)
            {
                case TargetGroup.Households:
                    member = ComponentType.ReadOnly<Household>();
                    exclude = new[] { ComponentType.ReadOnly<TouristHousehold>(), ComponentType.ReadOnly<CommuterHousehold>() };
                    break;
                case TargetGroup.IndustrialCompanies:
                    // Offices carry the industrial tag too, but vanilla industrial modifiers skip them.
                    member = ComponentType.ReadOnly<IndustrialCompany>();
                    exclude = new[] { ComponentType.ReadOnly<OfficeCompany>() };
                    break;
                case TargetGroup.CommercialCompanies:
                    member = ComponentType.ReadOnly<CommercialCompany>();
                    exclude = new ComponentType[0];
                    break;
                case TargetGroup.OfficeCompanies:
                    member = ComponentType.ReadOnly<OfficeCompany>();
                    exclude = new ComponentType[0];
                    break;
                case TargetGroup.HomelessCitizens:
                    // Homeless households rent nothing, so they get their own query; citizens are summed in Count().
                    return new EntityQueryDesc
                    {
                        All = new[] { ComponentType.ReadOnly<HomelessHousehold>(), ComponentType.ReadOnly<HouseholdCitizen>() },
                        None = new[]
                        {
                            ComponentType.ReadOnly<TouristHousehold>(),
                            ComponentType.ReadOnly<CommuterHousehold>(),
                            ComponentType.ReadOnly<Temp>(),
                            ComponentType.ReadOnly<Deleted>(),
                        },
                    };
                case TargetGroup.Criminals:
                    return new EntityQueryDesc
                    {
                        All = new[] { ComponentType.ReadOnly<Criminal>() },
                        None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
                    };
                case TargetGroup.TertiaryStudents:
                    // Levels are summed in Count(): college and university only.
                    return new EntityQueryDesc
                    {
                        All = new[] { ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<Game.Citizens.Student>() },
                        None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
                    };
                case TargetGroup.PoorHouseholds:
                    // Wealth is checked in Count(); homeless households count too (they rent nothing).
                    return PoorHouseholdQueryDesc;
                default:
                    // MonthlyCrimes comes from the city statistics, not from a query.
                    return null;
            }
            var none = new System.Collections.Generic.List<ComponentType>(exclude)
            {
                ComponentType.ReadOnly<Temp>(),
                ComponentType.ReadOnly<Deleted>(),
            };
            return new EntityQueryDesc
            {
                All = new[] { member, ComponentType.ReadOnly<PropertyRenter>() },
                None = none.ToArray(),
            };
        }

        /// <summary>Households that live in the city, with or without a home (wealth is checked separately).</summary>
        public static EntityQueryDesc PoorHouseholdQueryDesc => new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<HouseholdCitizen>(), ComponentType.ReadOnly<Game.Economy.Resources>() },
            None = new[]
            {
                ComponentType.ReadOnly<TouristHousehold>(),
                ComponentType.ReadOnly<CommuterHousehold>(),
                ComponentType.ReadOnly<Game.Agents.MovingAway>(),
                ComponentType.ReadOnly<Temp>(),
                ComponentType.ReadOnly<Deleted>(),
            },
        };

        /// <summary>The game's wealth lines (wretched, poor, modest, comfortable, wealthy start at 0, .x, .y, .z, .w); null before prefabs load.</summary>
        public static int4? WealthLines(EntityManager entityManager)
        {
            using (EntityQuery query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<CitizenHappinessParameterData>()))
            {
                return query.IsEmptyIgnoreFilter ? (int4?)null : query.GetSingleton<CitizenHappinessParameterData>().m_WealthyMoneyAmount;
            }
        }

        /// <summary>Wealth below which a household is poor: the game's "modest" threshold (wretched and poor below it).</summary>
        public static int PoorWealthLimit(EntityManager entityManager)
        {
            using (EntityQuery query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<CitizenHappinessParameterData>()))
            {
                return query.IsEmptyIgnoreFilter ? 1000 : query.GetSingleton<CitizenHappinessParameterData>().m_WealthyMoneyAmount.y;
            }
        }

        /// <summary>A random member of a group (for the representative plaintiff); Entity.Null when the group is empty.</summary>
        public Entity RandomMember(TargetGroup group, ref Unity.Mathematics.Random random)
        {
            if (group == TargetGroup.None)
            {
                return Entity.Null;
            }
            // One member at a random place, found by walking the chunks (no copy of the whole group).
            EntityQuery query = m_GroupQueries[(int)group];
            int count = query.CalculateEntityCount();
            if (count == 0)
            {
                return Entity.Null;
            }
            int pick = random.NextInt(count);
            query.CompleteDependency();
            EntityTypeHandle entityType = m_EntityManager.GetEntityTypeHandle();
            using (NativeArray<ArchetypeChunk> chunks = query.ToArchetypeChunkArray(Allocator.Temp))
            {
                foreach (ArchetypeChunk chunk in chunks)
                {
                    if (pick < chunk.Count)
                    {
                        return chunk.GetNativeArray(entityType)[pick];
                    }
                    pick -= chunk.Count;
                }
            }
            return Entity.Null;
        }

        /// <summary>Size of a group at the last count; 0 for <see cref="TargetGroup.None"/>.</summary>
        public float TargetCount(TargetGroup group)
        {
            return m_GroupCounts[(int)group];
        }

        public void Count()
        {
            System.Array.Clear(m_Workers, 0, m_Workers.Length);
            System.Array.Clear(m_MaxWorkers, 0, m_MaxWorkers.Length);
            System.Array.Clear(m_BudgetWeighted, 0, m_BudgetWeighted.Length);
            m_ServiceBudget.Clear();
            int welfareOffices = 0;

            // Read chunk by chunk: far cheaper than one EntityManager call per building (performance review 2026-11-09).
            m_BuildingQuery.CompleteDependency();
            ComponentTypeHandle<PrefabRef> prefabType = m_EntityManager.GetComponentTypeHandle<PrefabRef>(true);
            ComponentTypeHandle<WorkProvider> providerType = m_EntityManager.GetComponentTypeHandle<WorkProvider>(true);
            BufferTypeHandle<Employee> employeeType = m_EntityManager.GetBufferTypeHandle<Employee>(true);
            ComponentTypeHandle<RealisticPolicies.SocialWelfareDesignation> welfareType = m_EntityManager.GetComponentTypeHandle<RealisticPolicies.SocialWelfareDesignation>(true);
            ComponentTypeHandle<RealisticPolicies.TaxDesignation> taxType = m_EntityManager.GetComponentTypeHandle<RealisticPolicies.TaxDesignation>(true);
            using (NativeArray<ArchetypeChunk> chunks = m_BuildingQuery.ToArchetypeChunkArray(Allocator.Temp))
            {
                foreach (ArchetypeChunk chunk in chunks)
                {
                    NativeArray<PrefabRef> prefabs = chunk.GetNativeArray(ref prefabType);
                    NativeArray<WorkProvider> providers = chunk.GetNativeArray(ref providerType);
                    BufferAccessor<Employee> employees = chunk.GetBufferAccessor(ref employeeType);
                    bool welfareDesignated = chunk.Has(ref welfareType);
                    bool taxDesignated = chunk.Has(ref taxType);
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        Entity prefab = prefabs[i].m_Prefab;
                        ServiceSeries series = SeriesOf(prefab);
                        if (series == ServiceSeries.None)
                        {
                            continue;
                        }
                        if (m_WelfarePrefabs.Contains(prefab))
                        {
                            welfareOffices++;
                            // The player can assign a welfare office to social welfare or tax staff.
                            if (welfareDesignated)
                            {
                                series = ServiceSeries.SocialWelfare;
                            }
                            else if (taxDesignated)
                            {
                                series = ServiceSeries.Tax;
                            }
                        }
                        int max = providers[i].m_MaxWorkers;
                        int s = (int)series;
                        m_Workers[s] += employees[i].Length;
                        m_MaxWorkers[s] += max;
                        m_BudgetWeighted[s] += max * BudgetOf(prefab);
                    }
                }
            }
            WelfareOffices = welfareOffices;

            for (int s = 0; s < Capacity.Length; s++)
            {
                if (m_MaxWorkers[s] <= 0)
                {
                    Capacity[s] = 0f;
                    AvailableStaff[s] = 0f;
                    continue;
                }
                float staffing = math.saturate((float)m_Workers[s] / m_MaxWorkers[s]);
                float budgetFactor = math.pow(m_BudgetWeighted[s] / m_MaxWorkers[s], 1f - EngineParameters.kLabourShare);
                Capacity[s] = math.pow(staffing, EngineParameters.kLabourShare) * budgetFactor;
                AvailableStaff[s] = m_Workers[s] * budgetFactor;
            }

            for (int g = 1; g < m_GroupCounts.Length; g++)
            {
                switch ((TargetGroup)g)
                {
                    case TargetGroup.HomelessCitizens:
                        m_GroupCounts[g] = HomelessCitizens(m_GroupQueries[g]);
                        break;
                    case TargetGroup.MonthlyCrimes:
                        m_GroupCounts[g] = m_StatisticsSystem != null ? m_StatisticsSystem.GetStatisticValue(Game.City.StatisticType.CrimeCount) : 0f;
                        break;
                    case TargetGroup.TertiaryStudents:
                        m_GroupCounts[g] = TertiaryStudents(m_GroupQueries[g]);
                        break;
                    case TargetGroup.PoorHouseholds:
                        m_GroupCounts[g] = PoorHouseholds(m_GroupQueries[g]);
                        break;
                    default:
                        m_GroupCounts[g] = m_GroupQueries[g].CalculateEntityCount();
                        break;
                }
            }
        }

        private int TertiaryStudents(EntityQuery students)
        {
            int count = 0;
            using (NativeArray<Game.Citizens.Student> all = students.ToComponentDataArray<Game.Citizens.Student>(Allocator.Temp))
            {
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].m_Level >= (byte)SchoolLevel.College && all[i].m_Level <= (byte)SchoolLevel.University)
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        private int PoorHouseholds(EntityQuery households)
        {
            int limit = PoorWealthLimit(m_EntityManager);
            int count = 0;
            households.CompleteDependency();
            ComponentTypeHandle<Household> householdType = m_EntityManager.GetComponentTypeHandle<Household>(true);
            BufferTypeHandle<Game.Economy.Resources> resourcesType = m_EntityManager.GetBufferTypeHandle<Game.Economy.Resources>(true);
            using (NativeArray<ArchetypeChunk> chunks = households.ToArchetypeChunkArray(Allocator.Temp))
            {
                foreach (ArchetypeChunk chunk in chunks)
                {
                    NativeArray<Household> data = chunk.GetNativeArray(ref householdType);
                    BufferAccessor<Game.Economy.Resources> resources = chunk.GetBufferAccessor(ref resourcesType);
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        if (Game.Economy.EconomyUtils.GetHouseholdTotalWealth(data[i], resources[i]) < limit)
                        {
                            count++;
                        }
                    }
                }
            }
            return count;
        }

        private int HomelessCitizens(EntityQuery households)
        {
            int citizens = 0;
            households.CompleteDependency();
            BufferTypeHandle<HouseholdCitizen> citizenType = m_EntityManager.GetBufferTypeHandle<HouseholdCitizen>(true);
            using (NativeArray<ArchetypeChunk> chunks = households.ToArchetypeChunkArray(Allocator.Temp))
            {
                foreach (ArchetypeChunk chunk in chunks)
                {
                    BufferAccessor<HouseholdCitizen> members = chunk.GetBufferAccessor(ref citizenType);
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        citizens += members[i].Length;
                    }
                }
            }
            return citizens;
        }

        private float BudgetOf(Entity prefab)
        {
            if (!m_EntityManager.TryGetComponent(prefab, out ServiceObjectData serviceObject))
            {
                return 1f;
            }
            Entity service = serviceObject.m_Service;
            if (!m_ServiceBudget.TryGetValue(service, out float budget))
            {
                budget = m_BudgetSystem.GetServiceBudget(service) / 100f;
                m_ServiceBudget[service] = budget;
            }
            return budget;
        }

        private ServiceSeries SeriesOf(Entity prefab)
        {
            if (!m_PrefabSeries.TryGetValue(prefab, out ServiceSeries series))
            {
                series = Classify(m_EntityManager, prefab);
                m_PrefabSeries[prefab] = series;
                if (m_EntityManager.HasComponent<WelfareOfficeData>(prefab))
                {
                    m_WelfarePrefabs.Add(prefab);
                }
            }
            return series;
        }

        /// <summary>Series of a building prefab by its function component, or <see cref="ServiceSeries.None"/>.</summary>
        public static ServiceSeries Classify(EntityManager em, Entity prefab)
        {
            // Welfare offices count as general administration unless the player marks one as social welfare.
            if (em.HasComponent<AdminBuildingData>(prefab) || em.HasComponent<WelfareOfficeData>(prefab)) return ServiceSeries.GeneralAdministration;
            if (em.HasComponent<PoliceStationData>(prefab)) return ServiceSeries.Police;
            if (em.HasComponent<PrisonData>(prefab)) return ServiceSeries.Corrections;
            if (em.HasComponent<FireStationData>(prefab) || em.HasComponent<DisasterFacilityData>(prefab) || em.HasComponent<EmergencyShelterData>(prefab)) return ServiceSeries.FireAndDisaster;
            if (em.HasComponent<SchoolData>(prefab)) return ServiceSeries.Education;
            if (em.HasComponent<ResearchFacilityData>(prefab)) return ServiceSeries.Research;
            if (em.HasComponent<HospitalData>(prefab) || em.HasComponent<DeathcareFacilityData>(prefab)) return ServiceSeries.Health;
            if (em.HasComponent<GarbageFacilityData>(prefab)) return ServiceSeries.Environment;
            if (em.HasComponent<MaintenanceDepotData>(prefab)) return ServiceSeries.FacilityMaintenance;
            if (em.HasComponent<PowerPlantData>(prefab) || em.HasComponent<WaterPumpingStationData>(prefab) || em.HasComponent<SewageOutletData>(prefab) || em.HasComponent<WaterTowerData>(prefab)) return ServiceSeries.EngineeringUtilities;
            if (em.HasComponent<TransportDepotData>(prefab) || em.HasComponent<TransportStationData>(prefab)) return ServiceSeries.Transit;
            if (em.HasComponent<PostFacilityData>(prefab) || em.HasComponent<TelecomFacilityData>(prefab)) return ServiceSeries.PostAndTelecom;
            return ServiceSeries.None;
        }
    }
}
