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
    /// Marks a welfare office as social welfare staff. Unmarked welfare offices count as general administration
    /// (players often use them as city-hall departments). Saved with the building; without the mod the loader drops it.
    /// </summary>
    public struct SocialWelfareDesignation : IComponentData, IEmptySerializable
    {
    }

    /// <summary>Marks a welfare office as tax staff (tax offices are needed to change tax rates).</summary>
    public struct TaxDesignation : IComponentData, IEmptySerializable
    {
    }

    /// <summary>Welfare office info panel: which series its staff count as. RealisticPolicies.mjs draws it.</summary>
    public partial class WelfareSeriesSection : InfoSectionBase
    {
        private const string kSetTrigger = "SetWelfareSeries";

        /// <summary>Choices in the panel, in display order.</summary>
        public enum Choice
        {
            GeneralAdministration,
            SocialWelfare,
            Tax,
        }

        private PolicyEngineSystem m_Engine;
        private Choice m_Choice;

        protected override string group => nameof(WelfareSeriesSection);

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Engine = World.GetOrCreateSystemManaged<PolicyEngineSystem>();
            m_InfoUISystem.AddMiddleSection(this);
            AddBinding(new TriggerBinding<int>(ModUISystem.kGroup, kSetTrigger, choice => Set(selectedEntity, (Choice)choice)));
        }

        protected override void Reset()
        {
            m_Choice = Choice.GeneralAdministration;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            visible = selectedEntity != Entity.Null && EntityManager.HasComponent<WelfareOfficeData>(selectedPrefab);
        }

        protected override void OnProcess()
        {
            m_Choice = Get(EntityManager, selectedEntity);
        }

        public override void OnWriteProperties(IJsonWriter writer)
        {
            writer.PropertyName("series");
            writer.Write((int)m_Choice);
        }

        public static Choice Get(EntityManager entityManager, Entity building)
        {
            return entityManager.HasComponent<SocialWelfareDesignation>(building) ? Choice.SocialWelfare
                : entityManager.HasComponent<TaxDesignation>(building) ? Choice.Tax
                : Choice.GeneralAdministration;
        }

        /// <summary>Assigns a welfare office to a series and recounts staff at the next engine step.</summary>
        public void Set(Entity building, Choice choice)
        {
            if (building == Entity.Null || !EntityManager.Exists(building) || Get(EntityManager, building) == choice)
            {
                return;
            }
            EntityManager.RemoveComponent<SocialWelfareDesignation>(building);
            EntityManager.RemoveComponent<TaxDesignation>(building);
            if (choice == Choice.SocialWelfare)
            {
                EntityManager.AddComponent<SocialWelfareDesignation>(building);
            }
            else if (choice == Choice.Tax)
            {
                EntityManager.AddComponent<TaxDesignation>(building);
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
