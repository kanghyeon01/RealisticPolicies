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
    public sealed class PolicyRegistrationSystem : GameSystemBase
    {
        private PrefabSystem m_PrefabSystem;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
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

            Mod.Log.Info($"Registered {PolicyDefinitions.All.Length} policies (mode {GameManager.instance.gameMode}, all added: {allAdded}).");
            Enabled = false;
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
                    modifiers.Add(new CityModifierInfo { m_Type = output.Modifier, m_Mode = ModifierValueMode.Relative, m_Range = new Bounds1(0f, 0f) });
                }
            }
            if (modifiers.Count > 0)
            {
                prefab.AddComponent<CityModifiers>().m_Modifiers = modifiers.ToArray();
            }
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
