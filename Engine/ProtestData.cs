using Colossal.Serialization.Entities;
using Unity.Entities;

namespace RealisticPolicies.Engine
{
    /// <summary>A protest's character, fixed when it starts (docs/PLAN.md §12 "시위의 세 값").</summary>
    public enum ProtestTrait : byte
    {
        Peaceful,
        General,
        Hardline,
        Extreme,
    }

    /// <summary>What the city does about a protest. None counts as ignoring it, but keeps the badge up.</summary>
    public enum ProtestResponse : byte
    {
        None,
        Negotiate,
        Watch,
        Disperse,
        Ignore,
    }

    /// <summary>One protest (or riot), saved with the city.</summary>
    [InternalBufferCapacity(0)]
    public struct Protest : IBufferElementData, ISerializable
    {
        private const byte kVersion = 2;

        public int m_Id;

        /// <summary>The policy the protest is about (its demand).</summary>
        public Entity m_Policy;

        /// <summary>Household whose member posts the chirp.</summary>
        public Entity m_Representative;

        /// <summary>
        /// Where it started: the road in front of its park, or the residents' own road when no park is near
        /// (<see cref="ProtestSystem"/> "PickPark").
        /// </summary>
        public Entity m_StartEdge;

        /// <summary>The park, or the start segment when there is none: carries the icon, and clicking it selects that.</summary>
        public Entity m_IconOwner;

        /// <summary>Where the aggrieved live (the start segment in saves from before parks, version 1); they walk from there.</summary>
        public Entity m_HomeEdge;

        /// <summary>The park it gathers in, or null when it started on the residents' road.</summary>
        public Entity m_Place;

        public byte m_Trait;
        public byte m_Response;

        /// <summary>Share of the residents along its road segments taking part, 0..1.</summary>
        public float m_Size;

        /// <summary>Violence 0..1; a riot from 0.6.</summary>
        public float m_Intensity;

        public float m_AgeHours;

        /// <summary>Policy intensity the protesters want back (what they were used to), and the intensity when it began.</summary>
        public float m_DemandIntensity;
        public float m_StartIntensity;

        /// <summary>Lump-sum compensation paid in negotiation so far.</summary>
        public float m_Compensation;

        /// <summary>Repression strength still felt (decays after the police leave).</summary>
        public float m_Repression;

        public uint m_StartFrame;

        /// <summary>Residents along its segments and how many take part (cached at the last update, for the panel).</summary>
        public int m_Residents;
        public int m_Participants;

        /// <summary>Police cars the response needs and those standing at their places.</summary>
        public int m_CarsNeeded;
        public int m_CarsArrived;

        public bool m_Riot;
        public bool m_Granted;
        public bool m_EverRepressed;
        public bool m_EverConceded;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Id);
            writer.Write(m_Policy);
            writer.Write(m_Representative);
            writer.Write(m_StartEdge);
            writer.Write(m_IconOwner);
            writer.Write(m_Trait);
            writer.Write(m_Response);
            writer.Write(m_Size);
            writer.Write(m_Intensity);
            writer.Write(m_AgeHours);
            writer.Write(m_DemandIntensity);
            writer.Write(m_StartIntensity);
            writer.Write(m_Compensation);
            writer.Write(m_Repression);
            writer.Write(m_StartFrame);
            writer.Write(m_Residents);
            writer.Write(m_Participants);
            writer.Write(m_CarsNeeded);
            writer.Write(m_CarsArrived);
            writer.Write(m_Riot);
            writer.Write(m_Granted);
            writer.Write(m_EverRepressed);
            writer.Write(m_EverConceded);
            writer.Write(m_HomeEdge);
            writer.Write(m_Place);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Id);
            reader.Read(out m_Policy);
            reader.Read(out m_Representative);
            reader.Read(out m_StartEdge);
            reader.Read(out m_IconOwner);
            reader.Read(out m_Trait);
            reader.Read(out m_Response);
            reader.Read(out m_Size);
            reader.Read(out m_Intensity);
            reader.Read(out m_AgeHours);
            reader.Read(out m_DemandIntensity);
            reader.Read(out m_StartIntensity);
            reader.Read(out m_Compensation);
            reader.Read(out m_Repression);
            reader.Read(out m_StartFrame);
            reader.Read(out m_Residents);
            reader.Read(out m_Participants);
            reader.Read(out m_CarsNeeded);
            reader.Read(out m_CarsArrived);
            reader.Read(out m_Riot);
            reader.Read(out m_Granted);
            reader.Read(out m_EverRepressed);
            reader.Read(out m_EverConceded);
            if (version >= 2)
            {
                reader.Read(out m_HomeEdge);
                reader.Read(out m_Place);
            }
            else
            {
                m_HomeEdge = m_StartEdge;
                m_Place = Entity.Null;
            }
        }
    }

    /// <summary>Role of a road segment in a protest.</summary>
    public enum ProtestEdgeRole : byte
    {
        /// <summary>The protest stands on it (closed when it is a riot or 1,000+ people).</summary>
        Protest,

        /// <summary>Beyond an end node, held by the police during repression; only the way in is closed.</summary>
        Police,
    }

    /// <summary>A road segment of a protest, saved with the city.</summary>
    [InternalBufferCapacity(0)]
    public struct ProtestEdge : IBufferElementData, ISerializable
    {
        private const byte kVersion = 1;

        public int m_Protest;
        public Entity m_Edge;
        public byte m_Role;

        /// <summary>For police segments: the end node it starts at (the riot side).</summary>
        public Entity m_FromNode;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Protest);
            writer.Write(m_Edge);
            writer.Write(m_Role);
            writer.Write(m_FromNode);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Protest);
            reader.Read(out m_Edge);
            reader.Read(out m_Role);
            reader.Read(out m_FromNode);
        }
    }

    /// <summary>A car lane the mod made public-transport-only for a closure, to be given back.</summary>
    [InternalBufferCapacity(0)]
    public struct ClosedLane : IBufferElementData, ISerializable
    {
        private const byte kVersion = 1;

        public Entity m_Lane;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Lane);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Lane);
        }
    }

    /// <summary>A police place at a protest: the site the police are sent to, its event and the point on the road.</summary>
    [InternalBufferCapacity(0)]
    public struct PolicePost : IBufferElementData, ISerializable
    {
        private const byte kVersion = 1;

        public int m_Protest;
        public Entity m_Site;
        public Entity m_Event;
        public Entity m_Point;
        public uint m_CreatedFrame;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Protest);
            writer.Write(m_Site);
            writer.Write(m_Event);
            writer.Write(m_Point);
            writer.Write(m_CreatedFrame);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Protest);
            reader.Read(out m_Site);
            reader.Read(out m_Event);
            reader.Read(out m_Point);
            reader.Read(out m_CreatedFrame);
        }
    }

    /// <summary>A gathering spot on one sidewalk of a protest road: protesters walk there and stand along the sidewalk.</summary>
    [InternalBufferCapacity(0)]
    public struct ProtestSpot : IBufferElementData, ISerializable
    {
        private const byte kVersion = 1;

        public int m_Protest;
        public Entity m_Edge;
        public Entity m_Lane;
        public Entity m_Spot;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Protest);
            writer.Write(m_Edge);
            writer.Write(m_Lane);
            writer.Write(m_Spot);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Protest);
            reader.Read(out m_Edge);
            reader.Read(out m_Lane);
            reader.Read(out m_Spot);
        }
    }

    /// <summary>A resident sent to a protest spot (on the way, or standing there).</summary>
    [InternalBufferCapacity(0)]
    public struct ProtestMarcher : IBufferElementData, ISerializable
    {
        private const byte kVersion = 1;

        public int m_Protest;
        public Entity m_Citizen;
        public Entity m_Spot;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Protest);
            writer.Write(m_Citizen);
            writer.Write(m_Spot);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Protest);
            reader.Read(out m_Citizen);
            reader.Read(out m_Spot);
        }
    }

    /// <summary>Slow reference points of protest pressure per policy (τ = 12 days, docs/PLAN.md §12), saved with the city.</summary>
    [InternalBufferCapacity(0)]
    public struct ProtestPressure : IBufferElementData, ISerializable
    {
        private const byte kVersion = 1;

        public Entity m_Policy;

        /// <summary>Monthly budget spending, residents' monthly burden in money, and intensity people are used to.</summary>
        public float m_SpendRef;
        public float m_BurdenRef;
        public float m_IntensityRef;

        /// <summary>After a court win the plaintiffs did not expect, their pressure × this until the frame below.</summary>
        public float m_SurpriseFactor;
        public uint m_SurpriseUntil;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Policy);
            writer.Write(m_SpendRef);
            writer.Write(m_BurdenRef);
            writer.Write(m_IntensityRef);
            writer.Write(m_SurpriseFactor);
            writer.Write(m_SurpriseUntil);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Policy);
            reader.Read(out m_SpendRef);
            reader.Read(out m_BurdenRef);
            reader.Read(out m_IntensityRef);
            reader.Read(out m_SurpriseFactor);
            reader.Read(out m_SurpriseUntil);
        }
    }

    /// <summary>A road segment where the police dispersed people, remembered for later protests' character.</summary>
    [InternalBufferCapacity(0)]
    public struct RepressedArea : IBufferElementData, ISerializable
    {
        private const byte kVersion = 1;

        public Entity m_Edge;
        public uint m_Frame;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Edge);
            writer.Write(m_Frame);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Edge);
            reader.Read(out m_Frame);
        }
    }

    /// <summary>City-wide protest state, saved with the city.</summary>
    public struct ProtestCityState : IComponentData, ISerializable
    {
        private const byte kVersion = 1;

        public int m_NextId;

        /// <summary>Residential tax rate people are used to (slow reference).</summary>
        public float m_ResidentialRateRef;
        public bool m_HasRateRef;

        /// <summary>Concession and repression mixed in one protest: the next protest pressure × 1.5 until this frame.</summary>
        public uint m_MixedUntil;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_NextId);
            writer.Write(m_ResidentialRateRef);
            writer.Write(m_HasRateRef);
            writer.Write(m_MixedUntil);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_NextId);
            reader.Read(out m_ResidentialRateRef);
            reader.Read(out m_HasRateRef);
            reader.Read(out m_MixedUntil);
        }
    }
}
