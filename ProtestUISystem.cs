using Colossal.Entities;
using Colossal.UI.Binding;
using Game.City;
using Game.Simulation;
using Game.UI;
using RealisticPolicies.Engine;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace RealisticPolicies
{
    /// <summary>
    /// The "protests" tab of the city information panel (docs/PLAN.md §12 "화면", docs/previews/protest-panel.html): each
    /// protest's demand, people, intensity, character, closure and the city's response, with the player's actions.
    /// </summary>
    public sealed partial class ProtestUISystem : UISystemBase
    {
        private const string kGroup = ModUISystem.kGroup;

        private ProtestSystem m_Protests;
        private PolicyEngineSystem m_Engine;
        private CitySystem m_CitySystem;
        private SimulationSystem m_SimulationSystem;
        private TaxSystem m_TaxSystem;
        private RawValueBinding m_Binding;
        private uint m_LastFrame;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Protests = World.GetOrCreateSystemManaged<ProtestSystem>();
            m_Engine = World.GetOrCreateSystemManaged<PolicyEngineSystem>();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_TaxSystem = World.GetOrCreateSystemManaged<TaxSystem>();
            AddBinding(m_Binding = new RawValueBinding(kGroup, "protests", Write));
            AddBinding(new TriggerBinding<int, int>(kGroup, "respondProtest", Respond));
            AddBinding(new TriggerBinding<int, float, int>(kGroup, "negotiateProtest", Negotiate));
        }

        [Preserve]
        protected override void OnUpdate()
        {
            // The protests move every few hundred frames; the panel follows at the same pace.
            uint frame = m_SimulationSystem.frameIndex;
            if (frame - m_LastFrame >= 256u || frame < m_LastFrame)
            {
                m_LastFrame = frame;
                m_Binding.Update();
            }
        }

        private void Respond(int id, int response)
        {
            if (response >= (int)ProtestResponse.None && response <= (int)ProtestResponse.Ignore)
            {
                m_Protests.Respond(id, (ProtestResponse)response);
                m_Binding.Update();
            }
        }

        /// <summary>Negotiation: <paramref name="value"/> is the slider value shown (policy units, or the residential tax %).</summary>
        private void Negotiate(int id, float value, int compensation)
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.TryGetBuffer(city, true, out DynamicBuffer<Protest> protests))
            {
                return;
            }
            for (int i = 0; i < protests.Length; i++)
            {
                if (protests[i].m_Id != id)
                {
                    continue;
                }
                PolicyDefinition definition = protests[i].m_Policy != Entity.Null ? ProtestSystem.Definition(protests[i].m_Policy) : null;
                float intensity = value;
                if (definition != null)
                {
                    intensity = definition.IsSlider
                        ? math.saturate((value - definition.SliderRange.min) / math.max(1e-4f, definition.SliderRange.max - definition.SliderRange.min))
                        : (value > 0.5f ? 1f : 0f);
                }
                m_Protests.Negotiate(id, intensity, math.max(0, compensation));
                m_Binding.Update();
                return;
            }
        }

        private void Write(IJsonWriter writer)
        {
            Entity city = m_CitySystem.City;
            writer.TypeBegin("RealisticPolicies.Protests");
            writer.PropertyName("pressure");
            writer.Write(m_Protests.LastPressure);
            writer.PropertyName("yearlyRate");
            writer.Write(m_Protests.LastYearlyRate);
            writer.PropertyName("list");
            if (city == Entity.Null || !EntityManager.TryGetBuffer(city, true, out DynamicBuffer<Protest> protests))
            {
                writer.ArrayBegin(0);
                writer.ArrayEnd();
            }
            else
            {
                writer.ArrayBegin(protests.Length);
                for (int i = 0; i < protests.Length; i++)
                {
                    WriteProtest(writer, city, protests[i]);
                }
                writer.ArrayEnd();
            }
            // Strikes and collective closures (§12 "파업"), shown as cards after the protests.
            writer.PropertyName("actions");
            if (city == Entity.Null || !EntityManager.TryGetBuffer(city, true, out DynamicBuffer<IndustrialAction> actions))
            {
                writer.ArrayBegin(0);
                writer.ArrayEnd();
            }
            else
            {
                writer.ArrayBegin(actions.Length);
                for (int i = 0; i < actions.Length; i++)
                {
                    WriteAction(writer, actions[i]);
                }
                writer.ArrayEnd();
            }
            writer.TypeEnd();
        }

        private void WriteAction(IJsonWriter writer, IndustrialAction action)
        {
            PolicyDefinition definition = ProtestSystem.Definition(action.m_Policy);
            float min = definition != null && definition.IsSlider ? definition.SliderRange.min : 0f;
            float max = definition != null && definition.IsSlider ? definition.SliderRange.max : 1f;
            writer.TypeBegin("RealisticPolicies.IndustrialAction");
            writer.PropertyName("id");
            writer.Write(action.m_Id);
            writer.PropertyName("policyId");
            writer.Write(definition != null ? definition.Id : "");
            writer.PropertyName("toggle");
            writer.Write(definition != null && !definition.IsSlider);
            writer.PropertyName("kind");
            writer.Write((int)action.m_Kind);
            writer.PropertyName("sector");
            writer.Write((int)action.m_Sector);
            writer.PropertyName("share");
            writer.Write(action.m_Share);
            writer.PropertyName("loss");
            writer.Write(action.m_Share * 0.2f);
            writer.PropertyName("hours");
            writer.Write(action.m_AgeHours);
            writer.PropertyName("now");
            writer.Write(definition != null ? min + m_Engine.LastIntensity(definition) * (max - min) : 0f);
            writer.PropertyName("demand");
            writer.Write(min + action.m_DemandIntensity * (max - min));
            writer.TypeEnd();
        }

        /// <summary>One protest as the protests tab and the building info section show it.</summary>
        internal void WriteProtest(IJsonWriter writer, Entity city, Protest protest)
        {
            PolicyDefinition definition = protest.m_Policy != Entity.Null ? ProtestSystem.Definition(protest.m_Policy) : null;
            int segments = Segments(city, protest.m_Id);
            writer.TypeBegin("RealisticPolicies.Protest");
            writer.PropertyName("id");
            writer.Write(protest.m_Id);
            writer.PropertyName("policyId");
            writer.Write(definition != null ? definition.Id : "");
            writer.PropertyName("toggle");
            writer.Write(definition != null && !definition.IsSlider);
            writer.PropertyName("cut");
            writer.Write(definition == null || definition.Direction == Direction.Benefit);
            // Slider in the policy's own units (or the tax %), for negotiation and the demand line.
            float min = 0f, max = 100f, step = 1f;
            float now, demand;
            if (definition != null)
            {
                min = definition.IsSlider ? definition.SliderRange.min : 0f;
                max = definition.IsSlider ? definition.SliderRange.max : 1f;
                step = definition.IsSlider ? math.max(definition.SliderStep, 1f) : 1f;
                now = min + m_Engine.LastIntensity(definition) * (max - min);
                demand = min + protest.m_DemandIntensity * (max - min);
            }
            else
            {
                TaxParameterLimits(out min, out max);
                now = m_TaxSystem.GetTaxRate(TaxAreaType.Residential);
                demand = protest.m_DemandIntensity;
            }
            writer.PropertyName("min");
            writer.Write(min);
            writer.PropertyName("max");
            writer.Write(max);
            writer.PropertyName("step");
            writer.Write(step);
            writer.PropertyName("now");
            writer.Write(now);
            writer.PropertyName("demand");
            writer.Write(demand);
            writer.PropertyName("people");
            writer.Write(protest.m_Participants);
            writer.PropertyName("share");
            writer.Write(protest.m_Size);
            writer.PropertyName("intensity");
            writer.Write(protest.m_Intensity);
            writer.PropertyName("trait");
            writer.Write((int)protest.m_Trait);
            writer.PropertyName("hours");
            writer.Write(protest.m_AgeHours);
            writer.PropertyName("riot");
            writer.Write(protest.m_Riot);
            writer.PropertyName("granted");
            writer.Write(protest.m_Granted);
            writer.PropertyName("segments");
            writer.Write(segments);
            writer.PropertyName("closed");
            writer.Write(protest.m_Riot || protest.m_Participants >= 1000);
            writer.PropertyName("response");
            writer.Write((int)protest.m_Response);
            writer.PropertyName("carsNeeded");
            writer.Write(protest.m_CarsNeeded);
            writer.PropertyName("carsArrived");
            writer.Write(protest.m_CarsArrived);
            writer.PropertyName("compensation");
            writer.Write(protest.m_Compensation);
            writer.PropertyName("riotHoursLeft");
            writer.Write(protest.m_Riot ? math.max(0f, math.log(0.6f / math.max(0.6f, protest.m_Intensity)) / math.log(0.9f)) : 0f);
            writer.TypeEnd();
        }

        private void TaxParameterLimits(out float min, out float max)
        {
            Game.Prefabs.TaxParameterData limits = m_TaxSystem.GetTaxParameterData();
            min = limits.m_ResidentialTaxLimits.x;
            max = limits.m_ResidentialTaxLimits.y;
        }

        private int Segments(Entity city, int id)
        {
            if (!EntityManager.TryGetBuffer(city, true, out DynamicBuffer<ProtestEdge> edges))
            {
                return 0;
            }
            int n = 0;
            for (int i = 0; i < edges.Length; i++)
            {
                if (edges[i].m_Protest == id && edges[i].m_Role == (byte)ProtestEdgeRole.Protest)
                {
                    n++;
                }
            }
            return n;
        }

        [Preserve]
        public ProtestUISystem()
        {
        }
    }
}
