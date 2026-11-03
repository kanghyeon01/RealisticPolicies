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
        public ServiceSeriesCensus(EntityManager entityManager, CityServiceBudgetSystem budgetSystem, EntityQuery buildingQuery, EntityQuery[] groupQueries)
        {
            m_EntityManager = entityManager;
            m_BudgetSystem = budgetSystem;
            m_BuildingQuery = buildingQuery;
            m_GroupQueries = groupQueries;
        }

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
                default:
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

        /// <summary>A random member of a group (for the representative plaintiff); Entity.Null when the group is empty.</summary>
        public Entity RandomMember(TargetGroup group, ref Unity.Mathematics.Random random)
        {
            if (group == TargetGroup.None)
            {
                return Entity.Null;
            }
            using (NativeArray<Entity> members = m_GroupQueries[(int)group].ToEntityArray(Allocator.Temp))
            {
                return members.Length == 0 ? Entity.Null : members[random.NextInt(members.Length)];
            }
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

            using (NativeArray<Entity> buildings = m_BuildingQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < buildings.Length; i++)
                {
                    Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(buildings[i]).m_Prefab;
                    ServiceSeries series = SeriesOf(prefab);
                    if (series == ServiceSeries.None)
                    {
                        continue;
                    }
                    if (m_WelfarePrefabs.Contains(prefab))
                    {
                        // The player can assign a welfare office to social welfare or tax staff.
                        if (m_EntityManager.HasComponent<RealisticPolicies.SocialWelfareDesignation>(buildings[i]))
                        {
                            series = ServiceSeries.SocialWelfare;
                        }
                        else if (m_EntityManager.HasComponent<RealisticPolicies.TaxDesignation>(buildings[i]))
                        {
                            series = ServiceSeries.Tax;
                        }
                    }
                    int max = m_EntityManager.GetComponentData<WorkProvider>(buildings[i]).m_MaxWorkers;
                    int s = (int)series;
                    m_Workers[s] += m_EntityManager.GetBuffer<Employee>(buildings[i], true).Length;
                    m_MaxWorkers[s] += max;
                    m_BudgetWeighted[s] += max * BudgetOf(prefab);
                }
            }

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
                m_GroupCounts[g] = m_GroupQueries[g].CalculateEntityCount();
            }
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
