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

    /// <summary>
    /// Rent support: months each household has had it (2026-11-10), saved with the city. A household that has had the
    /// months the detail setting allows gets no more; the list is emptied when the policy is switched off.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct RentSupportMonths : IBufferElementData, ISerializable
    {
        private const byte kVersion = 1;

        public Entity m_Household;
        public float m_Months;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Household);
            writer.Write(m_Months);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Household);
            reader.Read(out m_Months);
        }
    }

    /// <summary>
    /// A building and the simulation frame it was last built or rebuilt (new, level-up, placed), for the building codes
    /// (<see cref="BuildingWorksSystem"/>, 2026-11-10). Saved with the city.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct BuildingWorks : IBufferElementData, ISerializable
    {
        private const byte kVersion = 1;

        public Entity m_Building;
        public uint m_Frame;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Building);
            writer.Write(m_Frame);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Building);
            reader.Read(out m_Frame);
        }
    }

    /// <summary>Per-policy engine state, one element per mod policy, saved with the city.</summary>
    [InternalBufferCapacity(0)]
    public struct PolicyRuntime : IBufferElementData, ISerializable
    {
        private const byte kVersion = 6;

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

        /// <summary>Mean air pollution when the policy was switched on, for the report's outcome row.</summary>
        public float m_BaselineAir;
        public bool m_HasBaseline;

        /// <summary>Actual burden people are used to, for policies whose burden follows a market price (emission trading).</summary>
        public float m_BurdenReference;

        /// <summary>
        /// Simulation frame the policy was last switched on: staff go to the policies in this order (2026-11-09 user
        /// decision). 0 for policies already on in older saves, which then come first in definition order.
        /// </summary>
        public uint m_EnactedFrame;

        /// <summary>Buildings in the city when the policy was switched on: building codes reach only those built since (v5).</summary>
        public int m_StartBuildings;

        /// <summary>The policy's detail setting (second slider, 2026-11-10); negative means the default (v6).</summary>
        public float m_Second;

        /// <summary>The detail setting people are used to (with <see cref="m_Reference"/>); negative means the default (v6).</summary>
        public float m_SecondReference;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Policy);
            writer.Write(m_Progress);
            writer.Write(m_Reference);
            writer.Write(m_AgeDays);
            writer.Write(m_LastIntensity);
            writer.Write(m_Surge);
            writer.Write(m_BaselineAir);
            writer.Write(m_HasBaseline);
            writer.Write(m_BurdenReference);
            writer.Write(m_EnactedFrame);
            writer.Write(m_StartBuildings);
            writer.Write(m_Second);
            writer.Write(m_SecondReference);
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
            if (version >= 2)
            {
                reader.Read(out m_BaselineAir);
                reader.Read(out m_HasBaseline);
            }
            if (version >= 3)
            {
                reader.Read(out m_BurdenReference);
            }
            if (version >= 4)
            {
                reader.Read(out m_EnactedFrame);
            }
            if (version >= 5)
            {
                reader.Read(out m_StartBuildings);
            }
            m_Second = -1f;
            m_SecondReference = -1f;
            if (version >= 6)
            {
                reader.Read(out m_Second);
                reader.Read(out m_SecondReference);
            }
        }
    }
}
