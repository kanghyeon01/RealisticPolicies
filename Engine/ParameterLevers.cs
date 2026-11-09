using System.Collections.Generic;
using Unity.Entities;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// A field of a vanilla parameter singleton that policies change. Parameter singletons live on prefab entities,
    /// which survive loading another city, so the original value is captured once per session and every write
    /// starts from it: the field is always original × factor, and factor 1 restores it.
    /// </summary>
    public interface IParameterLever
    {
        string Id { get; }

        /// <summary>Writes original × factor. Returns false while the singleton does not exist yet.</summary>
        bool Apply(EntityManager entityManager, float factor);

        /// <summary>Writes the original back, if it was captured.</summary>
        void Restore(EntityManager entityManager);
    }

    /// <summary>A float field of a singleton component, scaled relative to its original value.</summary>
    public sealed class ScaledFloatLever<T> : IParameterLever where T : unmanaged, IComponentData
    {
        public delegate float Getter(in T data);

        public delegate void Setter(ref T data, float value);

        private readonly Getter m_Get;
        private readonly Setter m_Set;
        private EntityQuery m_Query;
        private bool m_HasQuery;
        private bool m_Captured;
        private float m_Original;

        public string Id { get; }

        /// <summary>The game's own value, captured before the first change (corporate rescue counts against it).</summary>
        public bool HasOriginal => m_Captured;

        public float Original => m_Original;

        public ScaledFloatLever(string id, Getter get, Setter set)
        {
            Id = id;
            m_Get = get;
            m_Set = set;
        }

        public bool Apply(EntityManager entityManager, float factor)
        {
            if (!TryGet(entityManager, out Entity entity, out T data))
            {
                return false;
            }
            if (!m_Captured)
            {
                m_Original = m_Get(data);
                m_Captured = true;
            }
            float value = m_Original * factor;
            if (m_Get(data) != value)
            {
                m_Set(ref data, value);
                entityManager.SetComponentData(entity, data);
            }
            return true;
        }

        public void Restore(EntityManager entityManager)
        {
            if (m_Captured)
            {
                Apply(entityManager, 1f);
            }
        }

        private bool TryGet(EntityManager entityManager, out Entity entity, out T data)
        {
            if (!m_HasQuery)
            {
                m_Query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<T>());
                m_HasQuery = true;
            }
            if (m_Query.CalculateEntityCount() != 1)
            {
                entity = Entity.Null;
                data = default;
                return false;
            }
            entity = m_Query.GetSingletonEntity();
            data = entityManager.GetComponentData<T>(entity);
            return true;
        }
    }

    /// <summary>
    /// Several float fields of one singleton scaled by the same factor, for a lever that vanilla spreads over fields
    /// (structural integrity per building level). Captured and restored like <see cref="ScaledFloatLever{T}"/>.
    /// </summary>
    public sealed class ScaledFloatsLever<T> : IParameterLever where T : unmanaged, IComponentData
    {
        private readonly ScaledFloatLever<T>.Getter[] m_Get;
        private readonly ScaledFloatLever<T>.Setter[] m_Set;
        private readonly float[] m_Original;
        private EntityQuery m_Query;
        private bool m_HasQuery;
        private bool m_Captured;

        public string Id { get; }

        public ScaledFloatsLever(string id, ScaledFloatLever<T>.Getter[] get, ScaledFloatLever<T>.Setter[] set)
        {
            Id = id;
            m_Get = get;
            m_Set = set;
            m_Original = new float[get.Length];
        }

        public bool Apply(EntityManager entityManager, float factor)
        {
            if (!m_HasQuery)
            {
                m_Query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<T>());
                m_HasQuery = true;
            }
            if (m_Query.CalculateEntityCount() != 1)
            {
                return false;
            }
            Entity entity = m_Query.GetSingletonEntity();
            T data = entityManager.GetComponentData<T>(entity);
            if (!m_Captured)
            {
                for (int i = 0; i < m_Get.Length; i++)
                {
                    m_Original[i] = m_Get[i](data);
                }
                m_Captured = true;
            }
            bool changed = false;
            for (int i = 0; i < m_Get.Length; i++)
            {
                float value = m_Original[i] * factor;
                if (m_Get[i](data) != value)
                {
                    m_Set[i](ref data, value);
                    changed = true;
                }
            }
            if (changed)
            {
                entityManager.SetComponentData(entity, data);
            }
            return true;
        }

        public void Restore(EntityManager entityManager)
        {
            if (m_Captured)
            {
                Apply(entityManager, 1f);
            }
        }
    }

    /// <summary>
    /// A consumption field of zoned building prefabs (§14.18). Vanilla reads the prefab's ConsumptionData for every
    /// building on each update (`AdjustElectricityConsumptionSystem`, `GarbageAccumulationSystem`), so scaling the
    /// prefab value scales every building of those zones. Originals are captured per prefab once per session, like
    /// the singleton levers; the prefabs are only rewritten when the factor moves.
    /// </summary>
    public sealed class BuildingConsumptionLever : IParameterLever
    {
        public enum Field : byte
        {
            Electricity,
            Garbage,
        }

        private readonly Field m_Field;
        private readonly bool m_Residential;
        private readonly bool m_Commercial;
        private readonly bool m_Industrial;
        private readonly bool m_Office;
        private readonly Dictionary<Entity, float> m_Original = new Dictionary<Entity, float>();
        private readonly List<Entity> m_Covered = new List<Entity>();
        private EntityQuery m_Query;
        private bool m_HasQuery;
        private int m_PrefabCount = -1;
        private float m_Written = 1f;

        public string Id { get; }

        public BuildingConsumptionLever(string id, Field field, bool residential, bool commercial, bool industrial, bool office)
        {
            Id = id;
            m_Field = field;
            m_Residential = residential;
            m_Commercial = commercial;
            m_Industrial = industrial;
            m_Office = office;
        }

        public bool Apply(EntityManager entityManager, float factor)
        {
            if (!m_HasQuery)
            {
                m_Query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Prefabs.PrefabData>(),
                    ComponentType.ReadWrite<Game.Prefabs.ConsumptionData>(), ComponentType.ReadOnly<Game.Prefabs.SpawnableBuildingData>());
                m_HasQuery = true;
            }
            int count = m_Query.CalculateEntityCount();
            if (count == 0)
            {
                return false;
            }
            bool refreshed = count != m_PrefabCount;
            if (refreshed)
            {
                Refresh(entityManager);
                m_PrefabCount = count;
            }
            if (!refreshed && System.Math.Abs(factor - m_Written) < 1e-4f)
            {
                return true;
            }
            foreach (Entity prefab in m_Covered)
            {
                Game.Prefabs.ConsumptionData data = entityManager.GetComponentData<Game.Prefabs.ConsumptionData>(prefab);
                if (!m_Original.TryGetValue(prefab, out float original))
                {
                    original = Get(data);
                    m_Original[prefab] = original;
                }
                Set(ref data, original * factor);
                entityManager.SetComponentData(prefab, data);
            }
            m_Written = factor;
            return true;
        }

        public void Restore(EntityManager entityManager)
        {
            if (m_Original.Count > 0)
            {
                Apply(entityManager, 1f);
            }
        }

        /// <summary>Building prefabs of the covered zones (prefabs are fixed once the game has loaded its assets).</summary>
        private void Refresh(EntityManager entityManager)
        {
            m_Covered.Clear();
            using (Unity.Collections.NativeArray<Entity> prefabs = m_Query.ToEntityArray(Unity.Collections.Allocator.Temp))
            {
                foreach (Entity prefab in prefabs)
                {
                    Entity zone = entityManager.GetComponentData<Game.Prefabs.SpawnableBuildingData>(prefab).m_ZonePrefab;
                    if (zone == Entity.Null || !entityManager.HasComponent<Game.Prefabs.ZoneData>(zone))
                    {
                        continue;
                    }
                    Game.Prefabs.ZoneData zoneData = entityManager.GetComponentData<Game.Prefabs.ZoneData>(zone);
                    bool office = (zoneData.m_ZoneFlags & Game.Prefabs.ZoneFlags.Office) != 0;
                    bool covered = zoneData.m_AreaType == Game.Zones.AreaType.Residential ? m_Residential
                        : zoneData.m_AreaType == Game.Zones.AreaType.Commercial ? m_Commercial
                        : zoneData.m_AreaType == Game.Zones.AreaType.Industrial ? (office ? m_Office : m_Industrial)
                        : false;
                    if (covered)
                    {
                        m_Covered.Add(prefab);
                    }
                }
            }
        }

        private float Get(in Game.Prefabs.ConsumptionData data) =>
            m_Field == Field.Electricity ? data.m_ElectricityConsumption : data.m_GarbageAccumulation;

        private void Set(ref Game.Prefabs.ConsumptionData data, float value)
        {
            if (m_Field == Field.Electricity)
            {
                data.m_ElectricityConsumption = value;
            }
            else
            {
                data.m_GarbageAccumulation = value;
            }
        }
    }

    /// <summary>
    /// Education of newly arriving citizens (§14.6 talent attraction): the factor scales the share of the two highest
    /// levels (3 and 4, the latter being what the four stored probabilities leave), and the lower levels give way in
    /// proportion. The four probabilities are captured once per session like the other levers.
    /// </summary>
    public sealed class NewCitizenEducationLever : IParameterLever
    {
        private EntityQuery m_Query;
        private bool m_HasQuery;
        private bool m_Captured;
        private Unity.Mathematics.float4 m_Original;

        public string Id { get; }

        public NewCitizenEducationLever(string id)
        {
            Id = id;
        }

        public bool Apply(EntityManager entityManager, float factor)
        {
            if (!m_HasQuery)
            {
                m_Query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<Game.Prefabs.DemandParameterData>());
                m_HasQuery = true;
            }
            if (m_Query.CalculateEntityCount() != 1)
            {
                return false;
            }
            Entity entity = m_Query.GetSingletonEntity();
            Game.Prefabs.DemandParameterData data = entityManager.GetComponentData<Game.Prefabs.DemandParameterData>(entity);
            if (!m_Captured)
            {
                m_Original = data.m_NewCitizenEducationParameters;
                m_Captured = true;
            }
            Unity.Mathematics.float4 p = m_Original;
            float low = p.x + p.y + p.z;
            float high = Unity.Mathematics.math.max(0f, 1f - low);
            float level3 = p.w;
            // Level 4 is the remainder; keep at least 10% for the lower levels.
            float scale = Unity.Mathematics.math.min(factor, 0.9f / Unity.Mathematics.math.max(1e-4f, high));
            float newHigh = high * scale;
            float lowScale = low > 0f ? (1f - newHigh) / low : 1f;
            // Factor 1 writes the original back exactly, so switching the policy off leaves no rounding behind.
            var value = factor == 1f ? m_Original : new Unity.Mathematics.float4(p.x * lowScale, p.y * lowScale, p.z * lowScale, level3 * scale);
            if (!data.m_NewCitizenEducationParameters.Equals(value))
            {
                data.m_NewCitizenEducationParameters = value;
                entityManager.SetComponentData(entity, data);
            }
            return true;
        }

        public void Restore(EntityManager entityManager)
        {
            if (m_Captured)
            {
                Apply(entityManager, 1f);
            }
        }
    }

    /// <summary>Levers policy outputs can name by id. Policies that need a new field register it here.</summary>
    public static class ParameterLevers
    {
        /// <summary>Base demand for new industrial (and office) companies, before resource and tax factors.</summary>
        public const string kIndustrialBaseDemand = "Demand.IndustrialBase";

        /// <summary>Wage of simple jobs (job level 0); spreads to incomes, company costs, city service upkeep and schooling.</summary>
        public const string kWage0 = "Economy.Wage0";

        /// <summary>Wage of basic jobs (job level 1), for the minimum wage spillover.</summary>
        public const string kWage1 = "Economy.Wage1";

        /// <summary>Share of the two highest education levels among newly arriving citizens.</summary>
        public const string kNewCitizenHighEducation = "Demand.NewCitizenHighEducation";

        /// <summary>Wellbeing bonus inside welfare coverage for unhappy citizens (§14.7 welfare outreach).</summary>
        public const string kWelfare = "Happiness.Welfare";

        /// <summary>Health and wellbeing penalties of being homeless (negative values; §14.7 Housing First).</summary>
        public const string kHomelessHealth = "Happiness.HomelessHealth";
        public const string kHomelessWellbeing = "Happiness.HomelessWellbeing";

        /// <summary>Garbage and noise that homeless citizens add to the place they stay.</summary>
        public const string kHomelessGarbage = "Garbage.HomelessGarbage";
        public const string kHomelessNoise = "Pollution.HomelessNoise";

        /// <summary>Chance that welfare coverage stops a criminal from offending again (§14.7 reentry support).</summary>
        public const string kWelfareRecurrence = "Police.WelfareRecurrence";

        /// <summary>Harm a crime leaves on the residents and workers of the building (§14.7 victim support).</summary>
        public const string kHomeCrimeEffect = "Police.HomeCrimeEffect";
        public const string kWorkplaceCrimeEffect = "Police.WorkplaceCrimeEffect";

        /// <summary>City service wages relative to the market wage (vanilla 0.5); the upkeep of every service building follows (§14.10).</summary>
        public const string kCityServiceWage = "Economy.CityServiceWage";

        // §14.12 economy and labour.
        public const string kSpecializationBonus = "Economy.SpecializationBonus";
        public const string kBankruptcyLimit = "Economy.BankruptcyLimit";
        public const string kCommuterRatioLimit = "Demand.CommuterRatioLimit";
        public const string kUnemploymentDays = "Economy.UnemploymentDays";
        public const string kFishConsumption = "Extractor.FishConsumption";
        public const string kForestConsumption = "Extractor.ForestConsumption";
        public const string kFertilityConsumption = "Extractor.FertilityConsumption";

        /// <summary>
        /// Ore and oil used up per unit extracted. Vanilla divides by the stored value (`AreaLotSimulationSystem`), so
        /// these levers scale its reciprocal: factor 0.4 means 60% less depletion, like the fish and forest levers.
        /// </summary>
        public const string kOreDepletion = "Extractor.OreDepletion";
        public const string kOilDepletion = "Extractor.OilDepletion";
        public const string kExtractorEfficiency = "Economy.ExtractorEfficiency";

        // §14.13 housing and environment.
        public const string kResidentialLandValue = "Economy.ResidentialLandValue";
        /// <summary>Vacancy share residential demand aims for, per density (x, y, z): a lower target means fewer new homes.</summary>
        public const string kFreeResidential = "Demand.FreeResidentialProportion";
        /// <summary>
        /// Time between home searches. Vanilla stretches the interval by the chance multiplier and homeless households
        /// divide the same value by the divisor, so raising both leaves homeless households' searches as they were
        /// (<c>HouseholdBehaviorSystem</c>).
        /// </summary>
        public const string kLookForHome = "Citizen.LookForHomeChance";
        public const string kBuildingNoise = "Pollution.BuildingNoise";
        public const string kRoadNoise = "Pollution.RoadNoise";
        public const string kRoadAir = "Pollution.RoadAir";
        public const string kWaterTolerance = "Water.PollutionTolerance";
        public const string kGroundwaterUse = "Water.GroundwaterUse";

        // §14.14 traffic and safety.
        /// <summary>Structural integrity of buildings (every level): fire, storm and flood damage per tick is divided by it.</summary>
        public const string kStructuralIntegrity = "Fire.StructuralIntegrity";

        // §14.16 health and education.
        /// <summary>Chance that a teenager goes on to high school (vanilla 0.75).</summary>
        public const string kEnterHighSchool = "Education.EnterHighSchool";
        /// <summary>Chance that an adult without a diploma goes back to high school (vanilla 0.1).</summary>
        public const string kAdultEnterHighSchool = "Education.AdultEnterHighSchool";
        /// <summary>Factor on a working citizen's chance of college and university (vanilla 0.1).</summary>
        public const string kWorkerContinueEducation = "Education.WorkerContinueEducation";

        // §14.18 held policies made possible by prefab consumption values.
        /// <summary>Electricity use of every zoned building (energy efficiency standard).</summary>
        public const string kBuildingElectricity = "Buildings.Electricity";
        /// <summary>Garbage of homes and shops (volume-based waste fee); industry has its own waste rules.</summary>
        public const string kHouseholdGarbage = "Buildings.HouseholdGarbage";

        private static readonly Dictionary<string, IParameterLever> s_Levers = new Dictionary<string, IParameterLever>();

        /// <summary>Levers the mod's own policies use.</summary>
        public static void RegisterBuiltIns()
        {
            Register(new ScaledFloatLever<Game.Prefabs.DemandParameterData>(kIndustrialBaseDemand,
                (in Game.Prefabs.DemandParameterData d) => d.m_IndustrialBaseDemand,
                (ref Game.Prefabs.DemandParameterData d, float v) => d.m_IndustrialBaseDemand = v));
            Register(new ScaledFloatLever<Game.Prefabs.EconomyParameterData>(kWage0,
                (in Game.Prefabs.EconomyParameterData d) => d.m_Wage0,
                (ref Game.Prefabs.EconomyParameterData d, float v) => d.m_Wage0 = (int)System.Math.Round(v)));
            Register(new ScaledFloatLever<Game.Prefabs.EconomyParameterData>(kWage1,
                (in Game.Prefabs.EconomyParameterData d) => d.m_Wage1,
                (ref Game.Prefabs.EconomyParameterData d, float v) => d.m_Wage1 = (int)System.Math.Round(v)));
            Register(new NewCitizenEducationLever(kNewCitizenHighEducation));
            Register(new ScaledFloatLever<Game.Prefabs.CitizenHappinessParameterData>(kWelfare,
                (in Game.Prefabs.CitizenHappinessParameterData d) => d.m_WelfareMultiplier,
                (ref Game.Prefabs.CitizenHappinessParameterData d, float v) => d.m_WelfareMultiplier = v));
            Register(new ScaledFloatLever<Game.Prefabs.CitizenHappinessParameterData>(kHomelessHealth,
                (in Game.Prefabs.CitizenHappinessParameterData d) => d.m_HomelessHealthEffect,
                (ref Game.Prefabs.CitizenHappinessParameterData d, float v) => d.m_HomelessHealthEffect = (int)System.Math.Round(v)));
            Register(new ScaledFloatLever<Game.Prefabs.CitizenHappinessParameterData>(kHomelessWellbeing,
                (in Game.Prefabs.CitizenHappinessParameterData d) => d.m_HomelessWellbeingEffect,
                (ref Game.Prefabs.CitizenHappinessParameterData d, float v) => d.m_HomelessWellbeingEffect = (int)System.Math.Round(v)));
            Register(new ScaledFloatLever<Game.Prefabs.GarbageParameterData>(kHomelessGarbage,
                (in Game.Prefabs.GarbageParameterData d) => d.m_HomelessGarbageProduce,
                (ref Game.Prefabs.GarbageParameterData d, float v) => d.m_HomelessGarbageProduce = (int)System.Math.Round(v)));
            Register(new ScaledFloatLever<Game.Prefabs.PollutionParameterData>(kHomelessNoise,
                (in Game.Prefabs.PollutionParameterData d) => d.m_HomelessNoisePollution,
                (ref Game.Prefabs.PollutionParameterData d, float v) => d.m_HomelessNoisePollution = (int)System.Math.Round(v)));
            Register(new ScaledFloatLever<Game.Prefabs.PoliceConfigurationData>(kWelfareRecurrence,
                (in Game.Prefabs.PoliceConfigurationData d) => d.m_WelfareCrimeRecurrenceFactor,
                (ref Game.Prefabs.PoliceConfigurationData d, float v) => d.m_WelfareCrimeRecurrenceFactor = v));
            Register(new ScaledFloatLever<Game.Prefabs.PoliceConfigurationData>(kHomeCrimeEffect,
                (in Game.Prefabs.PoliceConfigurationData d) => d.m_HomeCrimeEffect,
                (ref Game.Prefabs.PoliceConfigurationData d, float v) => d.m_HomeCrimeEffect = (int)System.Math.Round(v)));
            Register(new ScaledFloatLever<Game.Prefabs.PoliceConfigurationData>(kWorkplaceCrimeEffect,
                (in Game.Prefabs.PoliceConfigurationData d) => d.m_WorkplaceCrimeEffect,
                (ref Game.Prefabs.PoliceConfigurationData d, float v) => d.m_WorkplaceCrimeEffect = (int)System.Math.Round(v)));
            Register(new ScaledFloatLever<Game.Prefabs.EconomyParameterData>(kCityServiceWage,
                (in Game.Prefabs.EconomyParameterData d) => d.m_CityServiceWageAdjustment,
                (ref Game.Prefabs.EconomyParameterData d, float v) => d.m_CityServiceWageAdjustment = v));
            Register(new ScaledFloatLever<Game.Prefabs.EconomyParameterData>(kSpecializationBonus,
                (in Game.Prefabs.EconomyParameterData d) => d.m_MaxCitySpecializationBonus,
                (ref Game.Prefabs.EconomyParameterData d, float v) => d.m_MaxCitySpecializationBonus = v));
            Register(new ScaledFloatLever<Game.Prefabs.EconomyParameterData>(kBankruptcyLimit,
                (in Game.Prefabs.EconomyParameterData d) => d.m_CompanyBankruptcyLimit,
                (ref Game.Prefabs.EconomyParameterData d, float v) => d.m_CompanyBankruptcyLimit = (int)System.Math.Round(v)));
            Register(new ScaledFloatLever<Game.Prefabs.DemandParameterData>(kCommuterRatioLimit,
                (in Game.Prefabs.DemandParameterData d) => d.m_CommuterWorkerRatioLimit,
                (ref Game.Prefabs.DemandParameterData d, float v) => d.m_CommuterWorkerRatioLimit = System.Math.Max(1, (int)System.Math.Round(v))));
            Register(new ScaledFloatLever<Game.Prefabs.EconomyParameterData>(kUnemploymentDays,
                (in Game.Prefabs.EconomyParameterData d) => d.m_UnemploymentAllowanceMaxDays,
                (ref Game.Prefabs.EconomyParameterData d, float v) => d.m_UnemploymentAllowanceMaxDays = v));
            Register(new ScaledFloatLever<Game.Prefabs.ExtractorParameterData>(kFishConsumption,
                (in Game.Prefabs.ExtractorParameterData d) => d.m_FishConsumption,
                (ref Game.Prefabs.ExtractorParameterData d, float v) => d.m_FishConsumption = v));
            Register(new ScaledFloatLever<Game.Prefabs.ExtractorParameterData>(kFertilityConsumption,
                (in Game.Prefabs.ExtractorParameterData d) => d.m_FertilityConsumption,
                (ref Game.Prefabs.ExtractorParameterData d, float v) => d.m_FertilityConsumption = v));
            Register(new ScaledFloatLever<Game.Prefabs.ExtractorParameterData>(kOreDepletion,
                (in Game.Prefabs.ExtractorParameterData d) => 1f / d.m_OreConsumption,
                (ref Game.Prefabs.ExtractorParameterData d, float v) => d.m_OreConsumption = 1f / v));
            Register(new ScaledFloatLever<Game.Prefabs.ExtractorParameterData>(kOilDepletion,
                (in Game.Prefabs.ExtractorParameterData d) => 1f / d.m_OilConsumption,
                (ref Game.Prefabs.ExtractorParameterData d, float v) => d.m_OilConsumption = 1f / v));
            Register(new ScaledFloatLever<Game.Prefabs.ExtractorParameterData>(kForestConsumption,
                (in Game.Prefabs.ExtractorParameterData d) => d.m_ForestConsumption,
                (ref Game.Prefabs.ExtractorParameterData d, float v) => d.m_ForestConsumption = v));
            Register(new ScaledFloatLever<Game.Prefabs.EconomyParameterData>(kExtractorEfficiency,
                (in Game.Prefabs.EconomyParameterData d) => d.m_ExtractorProductionEfficiency,
                (ref Game.Prefabs.EconomyParameterData d, float v) => d.m_ExtractorProductionEfficiency = v));
            Register(new ScaledFloatLever<Game.Prefabs.EconomyParameterData>(kResidentialLandValue,
                (in Game.Prefabs.EconomyParameterData d) => d.m_LandValueModifier.x,
                (ref Game.Prefabs.EconomyParameterData d, float v) => d.m_LandValueModifier.x = v));
            Register(new ScaledFloatsLever<Game.Prefabs.DemandParameterData>(kFreeResidential,
                new ScaledFloatLever<Game.Prefabs.DemandParameterData>.Getter[]
                {
                    (in Game.Prefabs.DemandParameterData d) => d.m_FreeResidentialProportion.x,
                    (in Game.Prefabs.DemandParameterData d) => d.m_FreeResidentialProportion.y,
                    (in Game.Prefabs.DemandParameterData d) => d.m_FreeResidentialProportion.z,
                },
                new ScaledFloatLever<Game.Prefabs.DemandParameterData>.Setter[]
                {
                    (ref Game.Prefabs.DemandParameterData d, float v) => d.m_FreeResidentialProportion.x = v,
                    (ref Game.Prefabs.DemandParameterData d, float v) => d.m_FreeResidentialProportion.y = v,
                    (ref Game.Prefabs.DemandParameterData d, float v) => d.m_FreeResidentialProportion.z = v,
                }));
            Register(new ScaledFloatsLever<Game.Prefabs.CitizenParametersData>(kLookForHome,
                new ScaledFloatLever<Game.Prefabs.CitizenParametersData>.Getter[]
                {
                    (in Game.Prefabs.CitizenParametersData d) => d.m_LookForHomeChanceMultiplier.x,
                    (in Game.Prefabs.CitizenParametersData d) => d.m_LookForHomeChanceMultiplier.y,
                    (in Game.Prefabs.CitizenParametersData d) => d.m_LookForHomeHomelessDivisor,
                },
                new ScaledFloatLever<Game.Prefabs.CitizenParametersData>.Setter[]
                {
                    (ref Game.Prefabs.CitizenParametersData d, float v) => d.m_LookForHomeChanceMultiplier.x = v,
                    (ref Game.Prefabs.CitizenParametersData d, float v) => d.m_LookForHomeChanceMultiplier.y = v,
                    (ref Game.Prefabs.CitizenParametersData d, float v) => d.m_LookForHomeHomelessDivisor = Unity.Mathematics.math.max(1, (int)Unity.Mathematics.math.round(v)),
                }));
            Register(new ScaledFloatLever<Game.Prefabs.PollutionParameterData>(kBuildingNoise,
                (in Game.Prefabs.PollutionParameterData d) => d.m_NoiseMultiplier,
                (ref Game.Prefabs.PollutionParameterData d, float v) => d.m_NoiseMultiplier = v));
            Register(new ScaledFloatLever<Game.Prefabs.PollutionParameterData>(kRoadNoise,
                (in Game.Prefabs.PollutionParameterData d) => d.m_NetNoiseMultiplier,
                (ref Game.Prefabs.PollutionParameterData d, float v) => d.m_NetNoiseMultiplier = v));
            Register(new ScaledFloatLever<Game.Prefabs.PollutionParameterData>(kRoadAir,
                (in Game.Prefabs.PollutionParameterData d) => d.m_NetAirMultiplier,
                (ref Game.Prefabs.PollutionParameterData d, float v) => d.m_NetAirMultiplier = v));
            Register(new ScaledFloatLever<Game.Prefabs.WaterPipeParameterData>(kWaterTolerance,
                (in Game.Prefabs.WaterPipeParameterData d) => d.m_MaxToleratedPollution,
                (ref Game.Prefabs.WaterPipeParameterData d, float v) => d.m_MaxToleratedPollution = v));
            Register(new ScaledFloatLever<Game.Prefabs.WaterPipeParameterData>(kGroundwaterUse,
                (in Game.Prefabs.WaterPipeParameterData d) => d.m_GroundwaterUsageMultiplier,
                (ref Game.Prefabs.WaterPipeParameterData d, float v) => d.m_GroundwaterUsageMultiplier = v));
            Register(new ScaledFloatsLever<Game.Prefabs.FireConfigurationData>(kStructuralIntegrity,
                new ScaledFloatLever<Game.Prefabs.FireConfigurationData>.Getter[]
                {
                    (in Game.Prefabs.FireConfigurationData d) => d.m_BuildingStructuralIntegrity,
                    (in Game.Prefabs.FireConfigurationData d) => d.m_StructuralIntegrityLevel1,
                    (in Game.Prefabs.FireConfigurationData d) => d.m_StructuralIntegrityLevel2,
                    (in Game.Prefabs.FireConfigurationData d) => d.m_StructuralIntegrityLevel3,
                    (in Game.Prefabs.FireConfigurationData d) => d.m_StructuralIntegrityLevel4,
                    (in Game.Prefabs.FireConfigurationData d) => d.m_StructuralIntegrityLevel5,
                },
                new ScaledFloatLever<Game.Prefabs.FireConfigurationData>.Setter[]
                {
                    (ref Game.Prefabs.FireConfigurationData d, float v) => d.m_BuildingStructuralIntegrity = v,
                    (ref Game.Prefabs.FireConfigurationData d, float v) => d.m_StructuralIntegrityLevel1 = v,
                    (ref Game.Prefabs.FireConfigurationData d, float v) => d.m_StructuralIntegrityLevel2 = v,
                    (ref Game.Prefabs.FireConfigurationData d, float v) => d.m_StructuralIntegrityLevel3 = v,
                    (ref Game.Prefabs.FireConfigurationData d, float v) => d.m_StructuralIntegrityLevel4 = v,
                    (ref Game.Prefabs.FireConfigurationData d, float v) => d.m_StructuralIntegrityLevel5 = v,
                }));
            Register(new ScaledFloatLever<Game.Prefabs.EducationParameterData>(kEnterHighSchool,
                (in Game.Prefabs.EducationParameterData d) => d.m_EnterHighSchoolProbability,
                (ref Game.Prefabs.EducationParameterData d, float v) => d.m_EnterHighSchoolProbability = v));
            Register(new ScaledFloatLever<Game.Prefabs.EducationParameterData>(kAdultEnterHighSchool,
                (in Game.Prefabs.EducationParameterData d) => d.m_AdultEnterHighSchoolProbability,
                (ref Game.Prefabs.EducationParameterData d, float v) => d.m_AdultEnterHighSchoolProbability = v));
            Register(new ScaledFloatLever<Game.Prefabs.EducationParameterData>(kWorkerContinueEducation,
                (in Game.Prefabs.EducationParameterData d) => d.m_WorkerContinueEducationProbability,
                (ref Game.Prefabs.EducationParameterData d, float v) => d.m_WorkerContinueEducationProbability = v));
            Register(new BuildingConsumptionLever(kBuildingElectricity, BuildingConsumptionLever.Field.Electricity, true, true, true, true));
            Register(new BuildingConsumptionLever(kHouseholdGarbage, BuildingConsumptionLever.Field.Garbage, true, true, false, false));
        }

        public static IReadOnlyDictionary<string, IParameterLever> All => s_Levers;

        public static void Register(IParameterLever lever)
        {
            s_Levers[lever.Id] = lever;
        }

        public static bool TryGet(string id, out IParameterLever lever) => s_Levers.TryGetValue(id, out lever);

        public static void Unregister(string id)
        {
            s_Levers.Remove(id);
        }
    }
}
