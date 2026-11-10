using Colossal.Serialization.Entities;
using Unity.Entities;

namespace RealisticPolicies.Engine
{
    /// <summary>How the city defends a suit (docs/PLAN.md §7 "소송 대응").</summary>
    public enum SuitResponse : byte
    {
        None,
        Lawyer,
        Official,
    }

    /// <summary>Outcome of a settlement offer.</summary>
    public enum SettlementResult
    {
        Invalid,
        Accepted,
        Refused,
        Waiting,
        NoOffersLeft,
    }

    public enum InjunctionStage : byte
    {
        NotRequested,

        /// <summary>Requested; the court decides 30 minutes after filing.</summary>
        Requested,

        /// <summary>Granted and announced; takes effect one hour after filing.</summary>
        Granted,

        /// <summary>Granted and in effect: the policy's effect is suspended until the verdict.</summary>
        InEffect,
        Denied,

        /// <summary>Granted before the city answered, then cancelled on review (행정소송법 제24조).</summary>
        Cancelled,
    }

    /// <summary>Grounds a suit is fought on.</summary>
    [System.Flags]
    public enum SuitGrounds : byte
    {
        None = 0,
        Proportionality = 1,
        LegitimateExpectation = 2,
        Procedure = 4,
    }

    /// <summary>One pending administrative suit, stored on the city entity and saved with it.</summary>
    [InternalBufferCapacity(0)]
    public struct Lawsuit : IBufferElementData, ISerializable
    {
        private const byte kVersion = 4;

        public Entity m_Policy;
        public int m_Year;
        public int m_CaseNumber;
        public uint m_FiledFrame;
        public uint m_DurationFrames;

        /// <summary>Plaintiff group (<see cref="TargetGroup"/>) and how many of its members sued.</summary>
        public byte m_Group;
        public int m_Plaintiffs;

        /// <summary>Member chosen as the representative plaintiff (writes the chirp; the warning icon goes near it).</summary>
        public Entity m_Representative;

        /// <summary>City hall (or welfare office) nearest the representative, which shows the warning icon until the city answers.</summary>
        public Entity m_IconBuilding;

        /// <summary>The suit is about cutting a benefit (P policy lowered) rather than imposing a burden.</summary>
        public bool m_AgainstCut;

        /// <summary>Intensity the plaintiffs contest, and the intensity before that change (the reference to restore on a loss).</summary>
        public float m_ContestedIntensity;
        public float m_PriorIntensity;

        /// <summary>Plaintiff grievance when they sued.</summary>
        public float m_Grievance;

        /// <summary>Damages claimed so far (money): plaintiffs' losses while the policy is in force.</summary>
        public float m_Claim;

        public byte m_Grounds;
        public float m_Workload;

        public byte m_Response;
        public uint m_ResponseFrame;

        /// <summary>Lawyer fee fixed when chosen, and the part paid so far.</summary>
        public float m_LawyerFee;
        public float m_FeePaid;

        public byte m_Injunction;

        /// <summary>The injunction was decided before the city answered (+1.0 for the plaintiffs); reviewed once on answering.</summary>
        public bool m_DecidedUnanswered;
        public float m_InjunctionProbability;

        /// <summary>Fixed noise of the win-chance hint (σ ≈ 0.1), so the hint does not flicker.</summary>
        public float m_HintNoise;
        public uint m_Seed;

        /// <summary>Settlement offers made so far (at most 3), and when the last one was refused (§7 "화해안 횟수").</summary>
        public byte m_Offers;
        public uint m_LastOfferFrame;

        /// <summary>Month the contested disposition was made, as year × 12 + month (0-based); for the claim text.</summary>
        public int m_ActMonth;

        /// <summary>The detail setting contested and the one before (negative: the default; v4). A ruling restores both.</summary>
        public float m_ContestedSecond;
        public float m_PriorSecond;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Policy);
            writer.Write(m_Year);
            writer.Write(m_CaseNumber);
            writer.Write(m_FiledFrame);
            writer.Write(m_DurationFrames);
            writer.Write(m_Group);
            writer.Write(m_Plaintiffs);
            writer.Write(m_Representative);
            writer.Write(m_AgainstCut);
            writer.Write(m_ContestedIntensity);
            writer.Write(m_PriorIntensity);
            writer.Write(m_Grievance);
            writer.Write(m_Claim);
            writer.Write(m_Grounds);
            writer.Write(m_Workload);
            writer.Write(m_Response);
            writer.Write(m_ResponseFrame);
            writer.Write(m_LawyerFee);
            writer.Write(m_FeePaid);
            writer.Write(m_Injunction);
            writer.Write(m_DecidedUnanswered);
            writer.Write(m_InjunctionProbability);
            writer.Write(m_HintNoise);
            writer.Write(m_Seed);
            writer.Write(m_IconBuilding);
            writer.Write(m_Offers);
            writer.Write(m_LastOfferFrame);
            writer.Write(m_ActMonth);
            writer.Write(m_ContestedSecond);
            writer.Write(m_PriorSecond);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Policy);
            reader.Read(out m_Year);
            reader.Read(out m_CaseNumber);
            reader.Read(out m_FiledFrame);
            reader.Read(out m_DurationFrames);
            reader.Read(out m_Group);
            reader.Read(out m_Plaintiffs);
            reader.Read(out m_Representative);
            reader.Read(out m_AgainstCut);
            reader.Read(out m_ContestedIntensity);
            reader.Read(out m_PriorIntensity);
            reader.Read(out m_Grievance);
            reader.Read(out m_Claim);
            reader.Read(out m_Grounds);
            reader.Read(out m_Workload);
            reader.Read(out m_Response);
            reader.Read(out m_ResponseFrame);
            reader.Read(out m_LawyerFee);
            reader.Read(out m_FeePaid);
            reader.Read(out m_Injunction);
            reader.Read(out m_DecidedUnanswered);
            reader.Read(out m_InjunctionProbability);
            reader.Read(out m_HintNoise);
            reader.Read(out m_Seed);
            if (version >= 2)
            {
                reader.Read(out m_IconBuilding);
            }
            if (version >= 3)
            {
                reader.Read(out m_Offers);
                reader.Read(out m_LastOfferFrame);
                reader.Read(out m_ActMonth);
            }
            m_ContestedSecond = -1f;
            m_PriorSecond = -1f;
            if (version >= 4)
            {
                reader.Read(out m_ContestedSecond);
                reader.Read(out m_PriorSecond);
            }
        }
    }

    /// <summary>A policy the city lost in court: banned for a while, with conditions for sliders (§7 "패소하면").</summary>
    [InternalBufferCapacity(0)]
    public struct PolicyBan : IBufferElementData, ISerializable
    {
        private const byte kVersion = 3;

        public Entity m_Policy;
        public uint m_UntilFrame;

        /// <summary>Grounds the city lost on (<see cref="SuitGrounds"/>); they set the conditions for using a slider again.</summary>
        public byte m_Grounds;

        /// <summary>Proportionality limit intensity found by the court.</summary>
        public float m_ProportionalLimit;

        /// <summary>Highest intensity accepted so far during the ban; a slider may only step up from it within its band.</summary>
        public float m_Accepted;

        /// <summary>The case that led to the ban, for the report (0 in saves from before version 2).</summary>
        public int m_Year;
        public int m_CaseNumber;
        public bool m_Partial;

        /// <summary>
        /// The suit was over a cut benefit: the court put the benefit back and the policy is locked, so the slider
        /// conditions (which cap a burden coming back) do not apply (false in saves from before version 3).
        /// </summary>
        public bool m_AgainstCut;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Policy);
            writer.Write(m_UntilFrame);
            writer.Write(m_Grounds);
            writer.Write(m_ProportionalLimit);
            writer.Write(m_Accepted);
            writer.Write(m_Year);
            writer.Write(m_CaseNumber);
            writer.Write(m_Partial);
            writer.Write(m_AgainstCut);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Policy);
            reader.Read(out m_UntilFrame);
            reader.Read(out m_Grounds);
            reader.Read(out m_ProportionalLimit);
            reader.Read(out m_Accepted);
            if (version >= 2)
            {
                reader.Read(out m_Year);
                reader.Read(out m_CaseNumber);
                reader.Read(out m_Partial);
            }
            m_AgainstCut = false;
            if (version >= 3)
            {
                reader.Read(out m_AgainstCut);
            }
        }
    }

    /// <summary>Money the city owes after a lost suit (damages and the plaintiffs' costs), paid in daily instalments.</summary>
    [InternalBufferCapacity(0)]
    public struct CourtDebt : IBufferElementData, ISerializable
    {
        private const byte kVersion = 2;

        public int m_CaseNumber;
        public double m_Remaining;

        /// <summary>Unpaid instalments; they bear the vanilla loan interest and are paid first.</summary>
        public double m_Arrears;
        public float m_DailyCap;

        /// <summary>Remaining + arrears never exceed this (annual income × 2 at the verdict).</summary>
        public double m_Ceiling;

        /// <summary>Year of the case (0 in saves from before version 2).</summary>
        public int m_Year;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_CaseNumber);
            writer.Write(m_Remaining);
            writer.Write(m_Arrears);
            writer.Write(m_DailyCap);
            writer.Write(m_Ceiling);
            writer.Write(m_Year);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_CaseNumber);
            reader.Read(out m_Remaining);
            reader.Read(out m_Arrears);
            reader.Read(out m_DailyCap);
            reader.Read(out m_Ceiling);
            if (version >= 2)
            {
                reader.Read(out m_Year);
            }
        }
    }

    /// <summary>City-wide litigation counters, saved with the city.</summary>
    public struct LitigationState : IComponentData, ISerializable
    {
        private const byte kVersion = 1;

        /// <summary>Next case number this year (cases are numbered from 1001 each year).</summary>
        public int m_NextCase;
        public int m_CaseYear;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_NextCase);
            writer.Write(m_CaseYear);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_NextCase);
            reader.Read(out m_CaseYear);
        }
    }
}
