using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Prefabs;
using Game.SceneFlow;
using RealisticPolicies.Engine;
using Unity.Entities;
using UnityEngine.Scripting;

namespace RealisticPolicies
{
    public static class PolicyCatalog
    {
        public const string kEquipmentMandate = "RealisticPolicies.EquipmentMandate";
        public const string kEmissionStandard = "RealisticPolicies.EmissionStandard";
        public const string kPollutionTax = "RealisticPolicies.PollutionTax";
        public const string kGreenSubsidy = "RealisticPolicies.GreenSubsidy";
        public const string kEnvironmentalMonitoring = "RealisticPolicies.EnvironmentalMonitoring";
        public const string kEmissionDisclosure = "RealisticPolicies.EmissionDisclosure";
        public const string kEmissionTrading = "RealisticPolicies.EmissionTrading";
        public const string kAllowanceAuction = "RealisticPolicies.AllowanceAuction";
        public const string kMinimumWage = "RealisticPolicies.MinimumWage";
        public const string kTalentAttraction = "RealisticPolicies.TalentAttraction";
        public const string kRedevelopment = "RealisticPolicies.Redevelopment";
        public const string kWelfareOutreach = "RealisticPolicies.WelfareOutreach";
        public const string kHousingFirst = "RealisticPolicies.HousingFirst";
        public const string kReentrySupport = "RealisticPolicies.ReentrySupport";
        public const string kVictimSupport = "RealisticPolicies.VictimSupport";
        public const string kProcedureSimplification = "RealisticPolicies.ProcedureSimplification";
        public const string kEGovernment = "RealisticPolicies.EGovernment";
        public const string kCivilServicePay = "RealisticPolicies.CivilServicePay";
        public const string kSpecialistPosts = "RealisticPolicies.SpecialistPosts";
        public const string kParticipatoryBudgeting = "RealisticPolicies.ParticipatoryBudgeting";
        public const string kPublicHearings = "RealisticPolicies.PublicHearings";
        public const string kPerformanceManagement = "RealisticPolicies.PerformanceManagement";
        public const string kOmbudsman = "RealisticPolicies.Ombudsman";
        public const string kInformationDisclosure = "RealisticPolicies.InformationDisclosure";
        public const string kArrearsCollection = "RealisticPolicies.ArrearsCollection";
        public const string kCompetitiveBidding = "RealisticPolicies.CompetitiveBidding";
        public const string kInterMunicipalAgreement = "RealisticPolicies.InterMunicipalAgreement";
        public const string kFiscalRule = "RealisticPolicies.FiscalRule";
        public const string kContractingOut = "RealisticPolicies.ContractingOut";
        public const string kIndustryCluster = "RealisticPolicies.IndustryCluster";
        public const string kCorporateRescue = "RealisticPolicies.CorporateRescue";
        public const string kRegionalJobPact = "RealisticPolicies.RegionalJobPact";
        public const string kUnemploymentDuration = "RealisticPolicies.UnemploymentDuration";
        public const string kResourceQuota = "RealisticPolicies.ResourceQuota";
        public const string kRentCap = "RealisticPolicies.RentCap";
        public const string kNoiseControl = "RealisticPolicies.NoiseControl";
        public const string kVehicleEmissions = "RealisticPolicies.VehicleEmissions";
        public const string kDrinkingWater = "RealisticPolicies.DrinkingWater";
        public const string kGroundwaterProtection = "RealisticPolicies.GroundwaterProtection";
        public const string kParkCare = "RealisticPolicies.ParkCare";
        public const string kSpeedCameras = "RealisticPolicies.SpeedCameras";
        public const string kDisasterCode = "RealisticPolicies.DisasterCode";
        public const string kFireCode = "RealisticPolicies.FireCode";
        public const string kCommunityPolicing = "RealisticPolicies.CommunityPolicing";
        public const string kZeroTolerance = "RealisticPolicies.ZeroTolerance";
        public const string kHarsherSentencing = "RealisticPolicies.HarsherSentencing";
        public const string kEmergencyResponse = "RealisticPolicies.EmergencyResponse";
        public const string kCpted = "RealisticPolicies.Cpted";
        public const string kHealthCampaign = "RealisticPolicies.HealthCampaign";
        public const string kEmergencyCare = "RealisticPolicies.EmergencyCare";
        public const string kAirQualityAlert = "RealisticPolicies.AirQualityAlert";
        public const string kPublicHealthcare = "RealisticPolicies.PublicHealthcare";
        public const string kScholarships = "RealisticPolicies.Scholarships";
        public const string kFreeHighSchool = "RealisticPolicies.FreeHighSchool";
        public const string kLifelongLearning = "RealisticPolicies.LifelongLearning";
        public const string kDisasterWarning = "RealisticPolicies.DisasterWarning";
        public const string kEnergyCode = "RealisticPolicies.EnergyCode";
        public const string kWasteFee = "RealisticPolicies.WasteFee";
        public const string kUniversityIndustry = "RealisticPolicies.UniversityIndustry";
        public const string kLivelihoodSupport = "RealisticPolicies.LivelihoodSupport";
        public const string kRentSupport = "RealisticPolicies.RentSupport";
        public const string kFlexibleHours = "RealisticPolicies.FlexibleHours";

        // Vanilla toggle that the equipment mandate replaces; hidden from the policy list.
        public const string kVanillaPollutionManagement = "Advanced Pollution Management";

        // Icon for policies that do not name their own.
        public const string kDefaultIcon = "Media/Game/Icons/Policies.svg";

        private static readonly Dictionary<string, PolicyDefinition> s_ById = new Dictionary<string, PolicyDefinition>();

        public static bool IsRegistered { get; internal set; }

        public static PolicyDefinition Definition(string id)
        {
            if (s_ById.Count == 0)
            {
                foreach (PolicyDefinition definition in PolicyDefinitions.All)
                {
                    s_ById[definition.Id] = definition;
                }
            }
            return s_ById.TryGetValue(id, out PolicyDefinition found) ? found : null;
        }

        /// <summary>Prefab entity of a mod policy, or Entity.Null before registration.</summary>
        public static Entity Find(string id) => Definition(id)?.Prefab ?? Entity.Null;

        public static Entity EquipmentMandate => Find(kEquipmentMandate);
        public static Entity EmissionStandard => Find(kEmissionStandard);
        public static Entity VanillaPollutionManagement => Definition(kEquipmentMandate)?.ReplacedPrefab ?? Entity.Null;
    }

    /// <summary>
    /// Adds a prefab for every policy definition once vanilla prefabs are available, and hides the vanilla toggles
    /// they replace. Disables itself after the first successful pass.
    /// </summary>
    public sealed partial class PolicyRegistrationSystem : GameSystemBase
    {
        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_NotificationIconQuery;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_NotificationIconQuery = GetEntityQuery(ComponentType.ReadOnly<NotificationIconData>(), ComponentType.ReadOnly<PrefabData>());
        }

        [Preserve]
        protected override void OnUpdate()
        {
            // Vanilla policies arrive together; one of them tells that they are loaded.
            if (!m_PrefabSystem.TryGetPrefab(new PrefabID(nameof(PolicyTogglePrefab), PolicyCatalog.kVanillaPollutionManagement), out PrefabBase _))
            {
                return;
            }

            bool allAdded = true;
            foreach (PolicyDefinition definition in PolicyDefinitions.All)
            {
                PolicyPrefab prefab = CreatePrefab(definition);
                bool added = m_PrefabSystem.AddPrefab(prefab);
                definition.Prefab = added ? m_PrefabSystem.GetEntity(prefab) : Entity.Null;
                allAdded &= added;
                if (definition.ReplacesVanilla != null)
                {
                    allAdded &= HideReplaced(definition);
                }
            }
            PolicyCatalog.IsRegistered = allAdded;
            RegisterCourtPrefabs();
            RegisterActionPolicy();

            Mod.Log.Info($"Registered {PolicyDefinitions.All.Length} policies (mode {GameManager.instance.gameMode}, all added: {allAdded}).");
            Enabled = false;
        }

        /// <summary>The lawsuit warning icon (drawn to match the game's icons) and one chirp per suable policy and plaintiff kind.</summary>
        private void RegisterCourtPrefabs()
        {
            using (Unity.Collections.NativeArray<Entity> icons = m_NotificationIconQuery.ToEntityArray(Unity.Collections.Allocator.Temp))
            {
                NotificationIconPrefab reference = null;
                for (int i = 0; i < icons.Length && reference == null; i++)
                {
                    if (m_PrefabSystem.TryGetPrefab(icons[i], out NotificationIconPrefab candidate) && candidate.m_Icon != null)
                    {
                        reference = candidate;
                    }
                }
                if (reference == null)
                {
                    Mod.Log.Warn("No vanilla notification icon found; the lawsuit icon is off.");
                }
                else
                {
                    UnityEngine.Texture2D texture = LawsuitIcon.Create(reference.m_Icon, out string problem);
                    Mod.Log.Info($"Notification icons: {reference.m_Icon.width}x{reference.m_Icon.height}, {reference.m_Icon.graphicsFormat}, {reference.m_Icon.mipmapCount} mips (reference '{reference.name}').");
                    if (texture == null)
                    {
                        Mod.Log.Warn($"Lawsuit icon not made: {problem}.");
                    }
                    else
                    {
                        NotificationIconPrefab icon = PrefabBase.Create<NotificationIconPrefab>(CourtPrefabs.kIconName);
                        icon.m_Icon = texture;
                        icon.m_DisplaySize = reference.m_DisplaySize;
                        icon.m_PulsateAmplitude = reference.m_PulsateAmplitude;
                        CourtPrefabs.Icon = m_PrefabSystem.AddPrefab(icon) ? m_PrefabSystem.GetEntity(icon) : Entity.Null;
                    }
                    ProtestPrefabs.ProtestIcon = AddIcon(reference, ProtestPrefabs.kProtestIconName, ProtestIcons.DrawProtest);
                    ProtestPrefabs.RiotIcon = AddIcon(reference, ProtestPrefabs.kRiotIconName, ProtestIcons.DrawRiot);
                }
            }

            foreach (PolicyDefinition definition in PolicyDefinitions.All)
            {
                if (definition.Instrument != Instrument.Authority && definition.Instrument != Instrument.Treasure)
                {
                    continue;
                }
                foreach (bool companies in new[] { true, false })
                {
                    string id = CourtPrefabs.ChirpId(definition.Id, companies);
                    ChirpPrefab chirp = PrefabBase.Create<ChirpPrefab>(id);
                    chirp.AddComponent<RandomLocalization>().m_LocalizationID = id;
                    CourtPrefabs.Chirps[id] = m_PrefabSystem.AddPrefab(chirp) ? m_PrefabSystem.GetEntity(chirp) : Entity.Null;
                }
            }
            ProtestPrefabs.ProtestChirp = AddChirp(ProtestPrefabs.kProtestChirpId);
            ProtestPrefabs.RiotChirp = AddChirp(ProtestPrefabs.kRiotChirpId);

            // Protesters' gathering spot: the game's own spawn location marker (as parks have), set so that people who
            // reach it stand around on the sidewalk next to it instead of going in.
            MarkerObjectPrefab spot = PrefabBase.Create<MarkerObjectPrefab>(ProtestPrefabs.kSpotName);
            Game.Prefabs.SpawnLocation location = spot.AddComponent<Game.Prefabs.SpawnLocation>();
            location.m_ConnectionType = RouteConnectionType.Pedestrian;
            location.m_HangaroundOnLane = true;
            ProtestPrefabs.Spot = m_PrefabSystem.AddPrefab(spot) ? m_PrefabSystem.GetEntity(spot) : Entity.Null;
            Mod.Log.Info($"Court prefabs: icon {(CourtPrefabs.Icon != Entity.Null ? "on" : "off")}, {CourtPrefabs.Chirps.Count} chirps; " +
                $"protest icons {(ProtestPrefabs.ProtestIcon != Entity.Null && ProtestPrefabs.RiotIcon != Entity.Null ? "on" : "off")}.");
        }

        /// <summary>
        /// Strikes and closures (§12 "파업"): a toggle kept out of the policy list whose industrial and office efficiency the
        /// game applies like any policy's; ProtestSystem writes the values and switches it on and off.
        /// </summary>
        private void RegisterActionPolicy()
        {
            PolicyTogglePrefab prefab = PrefabBase.Create<PolicyTogglePrefab>(Engine.ProtestSystem.kActionPolicyName);
            prefab.m_Category = PolicyCategory.Services;
            prefab.m_Visibility = PolicyVisibility.HideFromPolicyList;
            prefab.AddComponent<CityModifiers>().m_Modifiers = new[]
            {
                new CityModifierInfo { m_Type = Game.City.CityModifierType.IndustrialEfficiency, m_Mode = ModifierValueMode.Relative, m_Range = new Bounds1(0f, 0f) },
                new CityModifierInfo { m_Type = Game.City.CityModifierType.OfficeEfficiency, m_Mode = ModifierValueMode.Relative, m_Range = new Bounds1(0f, 0f) },
            };
            prefab.AddComponent<UIObject>().m_Icon = PolicyCatalog.kDefaultIcon;
            Engine.ProtestSystem.ActionPolicy = m_PrefabSystem.AddPrefab(prefab) ? m_PrefabSystem.GetEntity(prefab) : Entity.Null;
            Mod.Log.Info($"Strike and closure policy (hidden from the list): {(Engine.ProtestSystem.ActionPolicy != Entity.Null ? "on" : "off")}.");
        }

        private Entity AddIcon(NotificationIconPrefab reference, string name, System.Func<int, UnityEngine.Color32[]> draw)
        {
            UnityEngine.Texture2D texture = LawsuitIcon.Create(reference.m_Icon, draw, name, out string problem);
            if (texture == null)
            {
                Mod.Log.Warn($"{name} not made: {problem}.");
                return Entity.Null;
            }
            NotificationIconPrefab icon = PrefabBase.Create<NotificationIconPrefab>(name);
            icon.m_Icon = texture;
            icon.m_DisplaySize = reference.m_DisplaySize;
            icon.m_PulsateAmplitude = reference.m_PulsateAmplitude;
            return m_PrefabSystem.AddPrefab(icon) ? m_PrefabSystem.GetEntity(icon) : Entity.Null;
        }

        private Entity AddChirp(string id)
        {
            ChirpPrefab chirp = PrefabBase.Create<ChirpPrefab>(id);
            chirp.AddComponent<RandomLocalization>().m_LocalizationID = id;
            return m_PrefabSystem.AddPrefab(chirp) ? m_PrefabSystem.GetEntity(chirp) : Entity.Null;
        }

        private bool HideReplaced(PolicyDefinition definition)
        {
            if (!m_PrefabSystem.TryGetPrefab(new PrefabID(nameof(PolicyTogglePrefab), definition.ReplacesVanilla), out PrefabBase prefabBase)
                || !(prefabBase is PolicyPrefab vanilla))
            {
                Mod.Log.Warn($"{definition.Id}: vanilla policy '{definition.ReplacesVanilla}' not found.");
                return false;
            }
            LogVanilla(vanilla);
            vanilla.m_Visibility = PolicyVisibility.HideFromPolicyList;
            definition.ReplacedPrefab = m_PrefabSystem.GetEntity(vanilla);
            return true;
        }

        /// <summary>Builds the prefab; every city modifier starts at zero and the engine writes the delivered effect.</summary>
        private static PolicyPrefab CreatePrefab(PolicyDefinition definition)
        {
            PolicyPrefab prefab;
            if (definition.IsSlider)
            {
                PolicySliderPrefab slider = PrefabBase.Create<PolicySliderPrefab>(definition.Id);
                slider.m_SliderRange = definition.SliderRange;
                slider.m_SliderDefault = definition.SliderDefault;
                slider.m_SliderStep = definition.SliderStep;
                slider.m_Unit = definition.SliderUnit;
                prefab = slider;
            }
            else
            {
                prefab = PrefabBase.Create<PolicyTogglePrefab>(definition.Id);
            }
            prefab.m_Category = definition.Category;
            var modifiers = new List<CityModifierInfo>();
            foreach (PolicyOutput output in definition.Outputs)
            {
                if (output.Kind == OutputKind.CityModifier)
                {
                    modifiers.Add(new CityModifierInfo { m_Type = output.Modifier, m_Mode = output.Absolute ? ModifierValueMode.Absolute : ModifierValueMode.Relative, m_Range = new Bounds1(0f, 0f) });
                }
            }
            // Always, even with no modifiers: the game's city policy list takes only prefabs with CityOptionData or
            // CityModifierData (PoliciesUISystem), so a policy without the component never showed (2026-11-11: 28 of 61).
            prefab.AddComponent<CityModifiers>().m_Modifiers = modifiers.ToArray();
            prefab.AddComponent<UIObject>().m_Icon = definition.Icon ?? PolicyCatalog.kDefaultIcon;
            return prefab;
        }

        private static void LogVanilla(PolicyPrefab vanilla)
        {
            CityModifiers modifiers = vanilla.GetComponent<CityModifiers>();
            Mod.Log.Info($"Vanilla '{vanilla.name}': category={vanilla.m_Category}, icon={vanilla.GetComponent<UIObject>()?.m_Icon}");
            if (modifiers?.m_Modifiers == null)
            {
                Mod.Log.Info("  no city modifiers");
                return;
            }
            foreach (CityModifierInfo info in modifiers.m_Modifiers)
            {
                Mod.Log.Info($"  modifier {info.m_Type} {info.m_Mode} [{info.m_Range.min}, {info.m_Range.max}]");
            }
        }

        [Preserve]
        public PolicyRegistrationSystem()
        {
        }
    }
}
