using Colossal.Entities;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// City conditions that change how strongly some policies work (docs/POLICY-REVIEW.md §5a, 2026-11-10 user decision
    /// "전부 넣어"). Each factor is 1 in a reference city and stays within the range the review set, so an average city
    /// keeps today's effect. One condition per effect, from the study that estimated it:
    /// - unemployment benefit extension: unemployment rate (Kroft &amp; Notowidigdo 2016);
    /// - reentry support: unemployment rate, narrowed (Yang 2017; Raphael &amp; Weiman 2007);
    /// - rent support: vacancy, the landlords' share (Fack 2006);
    /// - welfare outreach: share of poor households (Finkelstein &amp; Notowidigdo 2019);
    /// - fiscal rule: budget balance and loan (Afonso &amp; Jalles 2019; Feld et al. 2013);
    /// - air quality alerts: children and elderly (Neidell 2009);
    /// - emergency care: distance to the nearest hospital (Nicholl et al. 2007);
    /// - public healthcare: hospital load (Aiken et al. 2002);
    /// - park care (crime part): share of poor households (Branas et al. 2018);
    /// (Inter-municipal agreement already scales with city size in the engine, Bel &amp; Sebő 2021: not repeated here.)
    /// - building codes (energy, disaster, fire, CPTED): share of the stock built, levelled up or renovated under the
    ///   code (Aroonruengsawat et al. 2012), which the engine passes in per policy (<see cref="BuildingWorksSystem"/>).
    /// Counted once per engine step; the hospital distance from a sample of households.
    /// </summary>
    public sealed class CityConditions
    {
        public const float kReferenceUnemployment = 0.062f;
        private const float kReferencePoorShare = 0.3f;
        private const float kReferenceVulnerable = 1.6f * 0.18f + 0.4f * 0.14f;
        private const float kReferenceHospitalMetres = 2000f;
        private const float kReferencePopulation = 100000f;
        private const int kDistanceSample = 400;

        private readonly EntityManager m_EntityManager;
        private readonly CountHouseholdDataSystem m_Households;
        private readonly CountResidentialPropertySystem m_Properties;
        private readonly CityServiceBudgetSystem m_Budget;
        private readonly ServiceSeriesCensus m_Census;
        private EntityQuery m_HospitalQuery;
        private EntityQuery m_HomeQuery;
        private EntityQuery m_LoanQuery;
        private EntityQuery m_BuildingQuery;

        public float Unemployment { get; private set; } = kReferenceUnemployment;
        public float Vacancy { get; private set; } = 0.05f;
        public float PoorShare { get; private set; } = kReferencePoorShare;
        public float Vulnerable { get; private set; } = kReferenceVulnerable;
        public float HospitalLoad { get; private set; } = 0.5f;
        public float HospitalMetres { get; private set; } = kReferenceHospitalMetres;
        public int Population { get; private set; }
        public int Balance { get; private set; }
        public int Loan { get; private set; }
        public int MonthlyIncome { get; private set; } = 1;

        /// <summary>Zoned and service buildings now (the building codes compare it with the count when the code started).</summary>
        public int Buildings { get; private set; }

        public CityConditions(EntityManager entityManager, World world, ServiceSeriesCensus census)
        {
            m_EntityManager = entityManager;
            m_Census = census;
            m_Households = world.GetOrCreateSystemManaged<CountHouseholdDataSystem>();
            m_Properties = world.GetOrCreateSystemManaged<CountResidentialPropertySystem>();
            m_Budget = world.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            m_HospitalQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Buildings.Hospital>(), ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            m_HomeQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ResidentialProperty>(), ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.ReadOnly<Renter>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            m_LoanQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<Loan>());
            m_BuildingQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Building>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        public void Count()
        {
            CountHouseholdDataSystem.HouseholdData data = m_Households.GetHouseholdCountData();
            Population = data.Population();
            Unemployment = data.m_WorkableCitizenCount > 0 ? math.saturate(data.Unemployed() / (float)data.m_WorkableCitizenCount) : kReferenceUnemployment;
            int people = data.m_ChildrenCount + data.m_TeenCount + data.m_AdultCount + data.m_SeniorCount;
            if (people > 0)
            {
                Vulnerable = (1.6f * (data.m_ChildrenCount + data.m_TeenCount) + 0.4f * data.m_SeniorCount) / people;
            }
            CountResidentialPropertySystem.ResidentialPropertyData homes = m_Properties.GetResidentialPropertyData();
            int total = math.csum(homes.m_TotalProperties);
            Vacancy = total > 0 ? math.saturate(math.csum(homes.m_FreeProperties) / (float)total) : 0.05f;
            float households = m_Census.TargetCount(TargetGroup.Households);
            PoorShare = households > 0f ? math.saturate(m_Census.TargetCount(TargetGroup.PoorHouseholds) / households) : kReferencePoorShare;
            Balance = m_Budget.GetBalance();
            MonthlyIncome = math.max(1, m_Budget.GetTotalIncome());
            Loan = m_LoanQuery.IsEmptyIgnoreFilter ? 0 : m_LoanQuery.GetSingleton<Loan>().m_Amount;
            Buildings = m_BuildingQuery.CalculateEntityCount();
            CountHospitals();
        }

        /// <summary>Patients over capacity, and the mean straight distance from a sample of homes to the nearest hospital.</summary>
        private void CountHospitals()
        {
            m_HospitalQuery.CompleteDependency();
            using (NativeArray<Entity> hospitals = m_HospitalQuery.ToEntityArray(Allocator.Temp))
            {
                if (hospitals.Length == 0)
                {
                    HospitalLoad = 1f;
                    HospitalMetres = 2f * kReferenceHospitalMetres;
                    return;
                }
                var positions = new NativeArray<float2>(hospitals.Length, Allocator.Temp);
                int patients = 0, capacity = 0;
                for (int i = 0; i < hospitals.Length; i++)
                {
                    positions[i] = m_EntityManager.GetComponentData<Game.Objects.Transform>(hospitals[i]).m_Position.xz;
                    if (m_EntityManager.TryGetBuffer(hospitals[i], true, out DynamicBuffer<Patient> list))
                    {
                        patients += list.Length;
                    }
                    Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(hospitals[i]).m_Prefab;
                    if (m_EntityManager.HasComponent<HospitalData>(prefab))
                    {
                        capacity += m_EntityManager.GetComponentData<HospitalData>(prefab).m_PatientCapacity;
                    }
                }
                HospitalLoad = capacity > 0 ? math.saturate(patients / (float)capacity) : 0.5f;
                using (NativeArray<Game.Objects.Transform> homes = m_HomeQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp))
                {
                    int step = math.max(1, homes.Length / kDistanceSample);
                    double sum = 0;
                    int n = 0;
                    for (int h = 0; h < homes.Length; h += step)
                    {
                        float best = float.MaxValue;
                        for (int i = 0; i < positions.Length; i++)
                        {
                            best = math.min(best, math.distance(homes[h].m_Position.xz, positions[i]));
                        }
                        sum += best;
                        n++;
                    }
                    HospitalMetres = n > 0 ? (float)(sum / n) : kReferenceHospitalMetres;
                }
                positions.Dispose();
            }
        }

        /// <summary>Factor for a policy's effect in this city (1 for policies without a city condition). <paramref name="covered"/> is the code's share of the building stock.</summary>
        public float Factor(string id, float covered)
        {
            float lnU = math.log(math.max(0.005f, Unemployment) / kReferenceUnemployment);
            switch (id)
            {
                case PolicyCatalog.kUnemploymentDuration:
                    return math.clamp(1f - 2.77f * lnU, 0.3f, 1.7f);
                case PolicyCatalog.kReentrySupport:
                    return math.clamp(1f - 0.5f * lnU, 0.75f, 1.25f);
                case PolicyCatalog.kWelfareOutreach:
                    return math.clamp(PoorShare / kReferencePoorShare, 0.5f, 1.5f);
                case PolicyCatalog.kParkCare:
                    return math.clamp(1f + 0.5f * (PoorShare - kReferencePoorShare) / kReferencePoorShare, 0.75f, 1.5f);
                case PolicyCatalog.kAirQualityAlert:
                    return math.clamp(Vulnerable / kReferenceVulnerable, 0.5f, 1.5f);
                case PolicyCatalog.kEmergencyCare:
                    return math.clamp(HospitalMetres / kReferenceHospitalMetres, 0.5f, 1.5f);
                case PolicyCatalog.kPublicHealthcare:
                    return math.clamp(0.5f + HospitalLoad, 0.5f, 1.5f);
                case PolicyCatalog.kFiscalRule:
                    bool sound = Balance >= 0 && Loan <= 6 * MonthlyIncome;
                    bool weak = Balance < 0 && Loan > 12 * MonthlyIncome;
                    return sound ? 1.25f : weak ? 0.75f : 1f;
                case PolicyCatalog.kEnergyCode:
                case PolicyCatalog.kDisasterCode:
                case PolicyCatalog.kFireCode:
                case PolicyCatalog.kCpted:
                    return math.saturate(covered);
                default:
                    return 1f;
            }
        }

        /// <summary>Building codes: they reach only buildings built or rebuilt under them.</summary>
        public static bool IsBuildingCode(string id) =>
            id == PolicyCatalog.kEnergyCode || id == PolicyCatalog.kDisasterCode || id == PolicyCatalog.kFireCode || id == PolicyCatalog.kCpted;

        /// <summary>Rent support: the share of the money landlords take in higher rents (78% when homes are scarce, Fack 2006).</summary>
        public float LandlordShare => math.lerp(0.78f, 0.3f, math.saturate((Vacancy - 0.03f) / 0.07f));

        /// <summary>Unemployment benefit extension: the job search delay scales with this (Kroft &amp; Notowidigdo 2016).</summary>
        public float SearchDelayFactor => Factor(PolicyCatalog.kUnemploymentDuration, 1f);
    }
}
