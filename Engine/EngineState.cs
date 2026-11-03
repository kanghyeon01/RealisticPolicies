using Colossal.Serialization.Entities;
using Unity.Entities;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// City-wide engine state, stored on the city entity and saved with the city.
    /// Without the mod the loader drops this unknown component and the save still opens.
    /// </summary>
    public struct PolicyEngineState : IComponentData, ISerializable
    {
        private const byte kVersion = 2;

        /// <summary>Hidden legitimacy 0..1 (formula ⑧).</summary>
        public float m_Legitimacy;

        /// <summary>Credibility of N policies 0..1; drops when they are relaxed.</summary>
        public float m_Credibility;

        /// <summary>The city has had tax staff since the mod came in (decides how rates are held without them).</summary>
        public bool m_TaxStaffSeen;

        public static PolicyEngineState Default => new PolicyEngineState
        {
            m_Legitimacy = EngineParameters.kLegitimacyBase,
            m_Credibility = 1f,
        };

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Legitimacy);
            writer.Write(m_Credibility);
            writer.Write(m_TaxStaffSeen);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Legitimacy);
            reader.Read(out m_Credibility);
            if (version >= 2)
            {
                reader.Read(out m_TaxStaffSeen);
            }
        }
    }

    /// <summary>Per-policy engine state, one element per mod policy, saved with the city.</summary>
    [InternalBufferCapacity(0)]
    public struct PolicyRuntime : IBufferElementData, ISerializable
    {
        private const byte kVersion = 1;

        /// <summary>Policy prefab (remapped by the serializer like vanilla policy references).</summary>
        public Entity m_Policy;

        /// <summary>Delivered share of the full effect 0..1 (formula ⑤).</summary>
        public float m_Progress;

        /// <summary>Intensity people are used to (reference point R).</summary>
        public float m_Reference;

        /// <summary>Days the policy has been in force, for capture (formula ⑦').</summary>
        public float m_AgeDays;

        /// <summary>Intensity at the previous engine step, to detect changes.</summary>
        public float m_LastIntensity;

        /// <summary>One-off extra workload from large changes (formula ⑥), relative to the policy's own staff need; decays.</summary>
        public float m_Surge;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Policy);
            writer.Write(m_Progress);
            writer.Write(m_Reference);
            writer.Write(m_AgeDays);
            writer.Write(m_LastIntensity);
            writer.Write(m_Surge);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Policy);
            reader.Read(out m_Progress);
            reader.Read(out m_Reference);
            reader.Read(out m_AgeDays);
            reader.Read(out m_LastIntensity);
            reader.Read(out m_Surge);
        }
    }
}
