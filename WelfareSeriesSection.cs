using Colossal.Serialization.Entities;
using Colossal.UI.Binding;
using Game.Prefabs;
using Game.UI.InGame;
using RealisticPolicies.Engine;
using Unity.Entities;
using UnityEngine.Scripting;

namespace RealisticPolicies
{
    /// <summary>
    /// Marks a welfare office as social welfare staff; unmarked welfare offices count as general administration
    /// (players often use them as city-hall departments). Saved with the building; without the mod the loader drops it.
    /// </summary>
    public struct SocialWelfareDesignation : IComponentData, IEmptySerializable
    {
    }

    /// <summary>Welfare office info panel: "Social welfare staff" checkbox. RealisticPolicies.mjs draws it.</summary>
    public partial class WelfareSeriesSection : InfoSectionBase
    {
        private const string kToggleTrigger = "ToggleSocialWelfare";

        private PolicyEngineSystem m_Engine;
        private bool m_SocialWelfare;

        protected override string group => nameof(WelfareSeriesSection);

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Engine = World.GetOrCreateSystemManaged<PolicyEngineSystem>();
            m_InfoUISystem.AddMiddleSection(this);
            AddBinding(new TriggerBinding(ModUISystem.kGroup, kToggleTrigger, Toggle));
        }

        protected override void Reset()
        {
            m_SocialWelfare = false;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            visible = selectedEntity != Entity.Null && EntityManager.HasComponent<WelfareOfficeData>(selectedPrefab);
        }

        protected override void OnProcess()
        {
            m_SocialWelfare = EntityManager.HasComponent<SocialWelfareDesignation>(selectedEntity);
        }

        public override void OnWriteProperties(IJsonWriter writer)
        {
            writer.PropertyName("socialWelfare");
            writer.Write(m_SocialWelfare);
        }

        private void Toggle()
        {
            Entity building = selectedEntity;
            if (building == Entity.Null || !EntityManager.Exists(building))
            {
                return;
            }
            if (EntityManager.HasComponent<SocialWelfareDesignation>(building))
            {
                EntityManager.RemoveComponent<SocialWelfareDesignation>(building);
            }
            else
            {
                EntityManager.AddComponent<SocialWelfareDesignation>(building);
            }
            m_Engine.RequestCensus();
            m_InfoUISystem.RequestUpdate();
        }

        [Preserve]
        public WelfareSeriesSection()
        {
        }
    }
}
