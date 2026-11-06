using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticPolicies.Engine
{
    /// <summary>Administrative and fiscal reforms (§14.10, §14.11): policies that change the administrative layer.</summary>
    public enum ReformKind : byte
    {
        None,
        ProcedureSimplification,
        EGovernment,
        CivilServicePay,
        SpecialistPosts,
        ParticipatoryBudgeting,
        PublicHearings,
        PerformanceManagement,
        Ombudsman,
        InformationDisclosure,
        ArrearsCollection,
        CompetitiveBidding,
        InterMunicipalAgreement,
        FiscalRule,
        ContractingOut,
    }

    /// <summary>How a report row of a reform's engine effect is shown.</summary>
    public enum ReformUnit : byte
    {
        Percent,
        Points,
        Hours,
        Count,
    }

    /// <summary>One engine effect of a reform, for the report panel (§14.10, option A: rows in the outcome section).</summary>
    public readonly struct ReformRow
    {
        public readonly string Key;
        public readonly float Value;
        public readonly ReformUnit Unit;

        /// <summary>Whether the effect helps the city (drawn green) or costs it (red).</summary>
        public readonly bool Good;

        public ReformRow(string key, float value, ReformUnit unit, bool good)
        {
            Key = key;
            Value = value;
            Unit = unit;
            Good = good;
        }
    }

    /// <summary>
    /// What the reforms do to the administrative layer at one step. Built from the reforms' delivered progress at the
    /// start of the step (the previous step's delivery), so a reform never feeds back on itself within a step.
    /// Neutral values (1 for scales, 0 for shifts) mean no reform is in force.
    /// </summary>
    public struct ReformEffects
    {
        // Starting values, docs/PLAN.md §14.10 (approved 2026-11-05) and §14.11 (approved 2026-11-06). Effects are at full delivery.

        /// <summary>Procedure simplification: decision points of regulations -1, their base lag -30% (estimate).</summary>
        public const float kSimplificationLag = 0.3f;

        /// <summary>Fewer procedures, less room for rent seeking: capture ceiling -25% (이재형 2004, Djankov et al. 2002).</summary>
        public const float kSimplificationCapture = 0.25f;

        /// <summary>Procedural violation the court sees in suits against regulations (5% → about 13% to lose).</summary>
        public const float kSimplificationProcedure = 0.15f;

        /// <summary>E-government, scaled by the post and telecom capacity: staff need -15% (임준형 2010: small time savings).</summary>
        public const float kEGovernmentStaff = 0.15f;

        /// <summary>Transparency: capture ceiling -20% (이재형 2004 OPEN elasticity -0.70; Banerjee et al. 2020 leakage -19%).</summary>
        public const float kEGovernmentCapture = 0.2f;

        /// <summary>Civil service pay at the largest raise (30%): fewer quits, so staff available +9% (이겨레 2024).</summary>
        public const float kPayStaff = 0.09f;

        /// <summary>Higher pay, less corruption, but only a little: capture ceiling -10% (Van Rijckeghem & Weder 2001).</summary>
        public const float kPayCapture = 0.1f;

        /// <summary>Specialist posts: longer tenure, effectiveness +10% (김광호 2008, 정성수 외 2010: learning 3–6 months).</summary>
        public const float kSpecialistEffect = 0.1f;

        /// <summary>Without rotation, capture builds up 30% faster (rotation guards against collusion; estimate).</summary>
        public const float kSpecialistCaptureTime = 0.3f;

        /// <summary>Participatory budgeting: legitimacy the city returns to +0.05, growing over 10 days (Touchton & Wampler 2014).</summary>
        public const float kBudgetLegitimacy = 0.05f;
        public const float kBudgetRampDays = 10f;

        /// <summary>Public hearings: grievance from raising a burden -30% (김지수·이정철 2022, Esaiasson et al. 2012).</summary>
        public const float kHearingGrievance = 0.3f;

        /// <summary>Public hearings: procedural violation -0.1 in every suit.</summary>
        public const float kHearingProcedure = 0.1f;

        /// <summary>Public hearings: four more hours before a raised burden takes effect.</summary>
        public const float kHearingLagDays = 4f / 24f;

        /// <summary>Performance management: effectiveness +5% only (이희태 2010, 노종호 2016: pay for performance barely moves officials).</summary>
        public const float kPerformanceEffect = 0.05f;

        /// <summary>Targets invite gaming: capture builds up 15% faster (Bevan & Hood 2006).</summary>
        public const float kPerformanceCaptureTime = 0.15f;

        /// <summary>
        /// Targets also bend the reports: other policies' delivery reads up to 10 points higher than it is (goal
        /// displacement, Bevan & Hood 2006). Only the report changes, never the effects.
        /// </summary>
        public const float kPerformanceReportInflation = 0.1f;

        /// <summary>Open records and audits cut that gaming by up to half (§14.8; Bevan & Hood 2006).</summary>
        public const float kDisclosureGamingCut = 0.5f;

        /// <summary>Ombudsman: grievances go to complaints instead of courts, suits filed -25% (Hirschman 1970; estimate).</summary>
        public const float kOmbudsmanFiling = 0.25f;
        public const float kOmbudsmanLegitimacy = 0.02f;

        /// <summary>Wider information disclosure: capture ceiling -20% (Cordis & Warren 2014, 이재형 2004).</summary>
        public const float kDisclosureCapture = 0.2f;
        public const float kDisclosureLegitimacy = 0.03f;

        /// <summary>Plaintiffs can see more, so the court leans their way: +0.3 on the verdict's z (estimate).</summary>
        public const float kDisclosurePlaintiff = 0.3f;

        /// <summary>Competitive bidding invites collusion: capture ceiling +10%.</summary>
        public const float kBiddingCapture = 0.1f;

        /// <summary>Contracting out: ties between officials and contractors, capture ceiling +10%; less trust in services.</summary>
        public const float kContractingCapture = 0.1f;
        public const float kContractingLegitimacy = 0.02f;

        public float Simplification;
        public float EGovernment;
        public float Pay;
        public float Specialist;
        public float Budget;
        public float Hearings;
        public float Performance;
        public float Ombudsman;
        public float Disclosure;
        public float Bidding;
        public float FiscalRule;
        public float Contracting;

        /// <summary>Post and telecom capacity e-government works through (0..1).</summary>
        public float PostCapacity;

        /// <summary>Share of the participatory budgeting legitimacy gain reached so far (0..1).</summary>
        public float BudgetRamp;

        public static ReformEffects None => new ReformEffects { PostCapacity = 1f, BudgetRamp = 1f };

        public static ReformEffects Compute(PolicyDefinition[] all, DynamicBuffer<PolicyRuntime> runtimes, float postCapacity)
        {
            ReformEffects effects = None;
            effects.PostCapacity = math.saturate(postCapacity);
            effects.BudgetRamp = 0f;
            foreach (PolicyDefinition definition in all)
            {
                if (definition.Reform == ReformKind.None)
                {
                    continue;
                }
                PolicyRuntime runtime = default;
                for (int r = 0; r < runtimes.Length; r++)
                {
                    if (runtimes[r].m_Policy == definition.Prefab)
                    {
                        runtime = runtimes[r];
                        break;
                    }
                }
                float delivered = math.saturate(runtime.m_Progress);
                switch (definition.Reform)
                {
                    case ReformKind.ProcedureSimplification:
                        effects.Simplification = delivered;
                        break;
                    case ReformKind.EGovernment:
                        effects.EGovernment = delivered;
                        break;
                    case ReformKind.CivilServicePay:
                        effects.Pay = delivered;
                        break;
                    case ReformKind.SpecialistPosts:
                        effects.Specialist = delivered;
                        break;
                    case ReformKind.ParticipatoryBudgeting:
                        effects.Budget = delivered;
                        effects.BudgetRamp = math.saturate(runtime.m_AgeDays / kBudgetRampDays);
                        break;
                    case ReformKind.PublicHearings:
                        effects.Hearings = delivered;
                        break;
                    case ReformKind.PerformanceManagement:
                        effects.Performance = delivered;
                        break;
                    case ReformKind.Ombudsman:
                        effects.Ombudsman = delivered;
                        break;
                    case ReformKind.InformationDisclosure:
                        effects.Disclosure = delivered;
                        break;
                    case ReformKind.CompetitiveBidding:
                        effects.Bidding = delivered;
                        break;
                    case ReformKind.FiscalRule:
                        effects.FiscalRule = delivered;
                        break;
                    case ReformKind.ContractingOut:
                        effects.Contracting = delivered;
                        break;
                }
            }
            return effects;
        }

        /// <summary>Decision points to add to a policy's own (regulations fewer; budget and spending policies more).</summary>
        public float ClearanceShift(PolicyDefinition definition)
        {
            if (definition.Reform != ReformKind.None)
            {
                return 0f;
            }
            float shift = 0f;
            if (definition.Instrument == Instrument.Authority)
            {
                shift -= Simplification;
            }
            if (definition.Instrument == Instrument.Treasure)
            {
                shift += Budget;
                // A fiscal rule makes every new spending benefit pass a budget check.
                if (definition.Direction == Direction.Benefit)
                {
                    shift += FiscalRule;
                }
            }
            return shift;
        }

        /// <summary>A policy's base lag in game days after the reforms.</summary>
        public float BaseLag(PolicyDefinition definition)
        {
            float lag = definition.BaseLagDays;
            if (definition.Reform != ReformKind.None)
            {
                return lag;
            }
            if (definition.Instrument == Instrument.Authority)
            {
                lag *= 1f - kSimplificationLag * Simplification;
            }
            if (definition.Direction == Direction.Burden)
            {
                lag += kHearingLagDays * Hearings;
            }
            return lag;
        }

        /// <summary>Factor on every policy's staff need.</summary>
        public float StaffNeedScale => 1f - kEGovernmentStaff * EGovernment * PostCapacity;

        /// <summary>Factor on the staff each series has for policy work.</summary>
        public float AvailableStaffScale => 1f + kPayStaff * Pay;

        /// <summary>Factor on every policy's effectiveness (before the cap at 1).</summary>
        public float EffectivenessScale => (1f + kSpecialistEffect * Specialist) * (1f + kPerformanceEffect * Performance);

        /// <summary>Factor on the capture ceiling c_max.</summary>
        public float CaptureCeilingScale =>
            (1f - kSimplificationCapture * Simplification)
            * (1f - kEGovernmentCapture * EGovernment * PostCapacity)
            * (1f - kPayCapture * Pay)
            * (1f - kDisclosureCapture * Disclosure)
            * (1f + kBiddingCapture * Bidding)
            * (1f + kContractingCapture * Contracting);

        /// <summary>Factor on the capture time constant (smaller = capture builds up faster).</summary>
        public float CaptureTimeScale => (1f - kSpecialistCaptureTime * Specialist) * (1f - kPerformanceCaptureTime * Performance);

        /// <summary>Points added to the delivery that other policies' reports show (§14.11, option B 2026-11-06).</summary>
        public float ReportInflation => kPerformanceReportInflation * Performance * (1f - kDisclosureGamingCut * Disclosure);

        /// <summary>Shift of the legitimacy the city returns to.</summary>
        public float LegitimacyBaseShift =>
            kBudgetLegitimacy * Budget * BudgetRamp + kOmbudsmanLegitimacy * Ombudsman + kDisclosureLegitimacy * Disclosure
            - kContractingLegitimacy * Contracting;

        /// <summary>Factor on the grievance of burdened groups when a burden is raised.</summary>
        public float BurdenRaiseGrievanceScale(PolicyDefinition definition) =>
            definition.Reform == ReformKind.None && definition.Direction == Direction.Burden ? 1f - kHearingGrievance * Hearings : 1f;

        /// <summary>Procedural violation the court adds (simplification) or removes (hearings) in a suit against the policy.</summary>
        public float ProceduralShift(PolicyDefinition definition)
        {
            float shift = -kHearingProcedure * Hearings;
            if (definition.Reform == ReformKind.None && definition.Instrument == Instrument.Authority)
            {
                shift += kSimplificationProcedure * Simplification;
            }
            return shift;
        }

        /// <summary>Factor on the daily chance that aggrieved groups go to court.</summary>
        public float SuitFilingScale => 1f - kOmbudsmanFiling * Ombudsman;

        /// <summary>Added to the verdict's z (higher = the city loses more often).</summary>
        public float PlaintiffShift => kDisclosurePlaintiff * Disclosure;

        /// <summary>The engine effects one reform contributes now, for the report's outcome section.</summary>
        public List<ReformRow> Rows(PolicyDefinition definition)
        {
            var rows = new List<ReformRow>();
            switch (definition.Reform)
            {
                case ReformKind.ProcedureSimplification:
                    rows.Add(new ReformRow("RegulationClearance", -Simplification, ReformUnit.Count, true));
                    rows.Add(new ReformRow("RegulationLag", -kSimplificationLag * Simplification, ReformUnit.Percent, true));
                    rows.Add(new ReformRow("CaptureCeiling", -kSimplificationCapture * Simplification, ReformUnit.Percent, true));
                    rows.Add(new ReformRow("RegulationProcedure", kSimplificationProcedure * Simplification, ReformUnit.Points, false));
                    break;
                case ReformKind.EGovernment:
                    rows.Add(new ReformRow("StaffNeed", -kEGovernmentStaff * EGovernment * PostCapacity, ReformUnit.Percent, true));
                    rows.Add(new ReformRow("CaptureCeiling", -kEGovernmentCapture * EGovernment * PostCapacity, ReformUnit.Percent, true));
                    break;
                case ReformKind.CivilServicePay:
                    rows.Add(new ReformRow("AvailableStaff", kPayStaff * Pay, ReformUnit.Percent, true));
                    rows.Add(new ReformRow("CaptureCeiling", -kPayCapture * Pay, ReformUnit.Percent, true));
                    break;
                case ReformKind.SpecialistPosts:
                    rows.Add(new ReformRow("Effectiveness", kSpecialistEffect * Specialist, ReformUnit.Percent, true));
                    rows.Add(new ReformRow("CaptureTime", -kSpecialistCaptureTime * Specialist, ReformUnit.Percent, false));
                    break;
                case ReformKind.ParticipatoryBudgeting:
                    rows.Add(new ReformRow("Legitimacy", kBudgetLegitimacy * Budget * BudgetRamp, ReformUnit.Points, true));
                    rows.Add(new ReformRow("MoneyClearance", Budget, ReformUnit.Count, false));
                    break;
                case ReformKind.PublicHearings:
                    rows.Add(new ReformRow("BurdenGrievance", -kHearingGrievance * Hearings, ReformUnit.Percent, true));
                    rows.Add(new ReformRow("SuitProcedure", -kHearingProcedure * Hearings, ReformUnit.Points, true));
                    rows.Add(new ReformRow("BurdenLag", kHearingLagDays * 24f * Hearings, ReformUnit.Hours, false));
                    break;
                case ReformKind.PerformanceManagement:
                    rows.Add(new ReformRow("Effectiveness", kPerformanceEffect * Performance, ReformUnit.Percent, true));
                    rows.Add(new ReformRow("CaptureTime", -kPerformanceCaptureTime * Performance, ReformUnit.Percent, false));
                    rows.Add(new ReformRow("ReportInflation", ReportInflation, ReformUnit.Percent, false));
                    break;
                case ReformKind.Ombudsman:
                    rows.Add(new ReformRow("SuitFiling", -kOmbudsmanFiling * Ombudsman, ReformUnit.Percent, true));
                    rows.Add(new ReformRow("Legitimacy", kOmbudsmanLegitimacy * Ombudsman, ReformUnit.Points, true));
                    break;
                case ReformKind.InformationDisclosure:
                    rows.Add(new ReformRow("CaptureCeiling", -kDisclosureCapture * Disclosure, ReformUnit.Percent, true));
                    rows.Add(new ReformRow("Legitimacy", kDisclosureLegitimacy * Disclosure, ReformUnit.Points, true));
                    rows.Add(new ReformRow("PlaintiffOdds", kDisclosurePlaintiff * Disclosure, ReformUnit.Points, false));
                    break;
                case ReformKind.CompetitiveBidding:
                    rows.Add(new ReformRow("CaptureCeiling", kBiddingCapture * Bidding, ReformUnit.Percent, false));
                    break;
                case ReformKind.FiscalRule:
                    rows.Add(new ReformRow("SpendingClearance", FiscalRule, ReformUnit.Count, false));
                    break;
                case ReformKind.ContractingOut:
                    rows.Add(new ReformRow("CaptureCeiling", kContractingCapture * Contracting, ReformUnit.Percent, false));
                    rows.Add(new ReformRow("Legitimacy", -kContractingLegitimacy * Contracting, ReformUnit.Points, false));
                    break;
            }
            return rows;
        }
    }
}
