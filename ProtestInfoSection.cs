using Colossal.Entities;
using Colossal.UI.Binding;
using Game.City;
using Game.Simulation;
using Game.UI.InGame;
using RealisticPolicies.Engine;
using Unity.Entities;
using UnityEngine.Scripting;

namespace RealisticPolicies
{
    /// <summary>
    /// The protest's own section in the info panel of the park or road its icon sits on (2026-11-09 user decision, preview
    /// docs/previews/protest-building-panel.html, option B): clicking the icon selects that park or road, as the game does
    /// for any notification icon, and this section shows that one protest as the protests tab does, without the response
    /// buttons; a link opens the protests tab, where the response is chosen. RealisticPolicies.mjs draws it.
    /// </summary>
    public partial class ProtestInfoSection : InfoSectionBase
    {
        private CitySystem m_CitySystem;
        private ProtestUISystem m_ProtestUI;
        private Protest m_Protest;

        protected override string group => nameof(ProtestInfoSection);

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_ProtestUI = World.GetOrCreateSystemManaged<ProtestUISystem>();
            m_InfoUISystem.AddTopSection(this);
        }

        protected override void Reset()
        {
            m_Protest = default;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            visible = selectedEntity != Entity.Null && Find(selectedEntity, out _);
        }

        protected override void OnProcess()
        {
            Find(selectedEntity, out m_Protest);
        }

        public override void OnWriteProperties(IJsonWriter writer)
        {
            writer.PropertyName("protest");
            m_ProtestUI.WriteProtest(writer, m_CitySystem.City, m_Protest);
        }

        /// <summary>The protest whose icon sits on this park or road.</summary>
        private bool Find(Entity owner, out Protest found)
        {
            found = default;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.TryGetBuffer(city, true, out DynamicBuffer<Protest> protests))
            {
                return false;
            }
            for (int i = 0; i < protests.Length; i++)
            {
                if (protests[i].m_IconOwner == owner)
                {
                    found = protests[i];
                    return true;
                }
            }
            return false;
        }

        [Preserve]
        public ProtestInfoSection()
        {
        }
    }
}
