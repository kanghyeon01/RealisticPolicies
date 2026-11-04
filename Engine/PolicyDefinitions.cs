using Colossal.Mathematics;
using Game.City;
using Game.Prefabs;
using Unity.Entities;

namespace RealisticPolicies.Engine
{
    /// <summary>Hood's NATO policy instruments.</summary>
    public enum Instrument : byte
    {
        Authority,
        Treasure,
        Organization,
        Nodality,
    }

    /// <summary>P (raising gives benefits) or N (raising imposes burdens).</summary>
    public enum Direction : byte
    {
        Benefit,
        Burden,
    }

    /// <summary>How a policy turns its delivered strength into effects (docs/PLAN.md §10.5).</summary>
    public enum Mechanism : byte
    {
        /// <summary>Outputs follow the delivered strength linearly; regulation uses the generic compliance ⑦.</summary>
        Generic,

        /// <summary>Required cut set by the slider; companies comply when it costs less than the expected fine (Becker).</summary>
        PerformanceStandard,

        /// <summary>Price per unit of emissions; companies cut until marginal cost equals the price, and may under-report (Allingham–Sandmo).</summary>
        EmissionTax,

        /// <summary>Payment per unit cut; same cutting rule as the tax, no evasion, money flows the other way.</summary>
        AbatementSubsidy,

        /// <summary>Cap on emissions; the allowance price clears the market (Dales 1968, Montgomery 1972). Evasion as for the tax.</summary>
        EmissionTrading,

        /// <summary>Share of the trading cap sold at auction instead of given away; only works with emission trading on.</summary>
        AllowanceAuction,

        /// <summary>Inspectors: no effect of its own, raises detection for regulation and taxes.</summary>
        Monitoring,

        /// <summary>Public emission reports: tiny effect of its own, raises voluntary compliance in proportion to detection.</summary>
        Disclosure,
    }

    /// <summary>Groups a policy can burden or benefit, and that its workload or money can scale with.</summary>
    public enum TargetGroup : byte
    {
        None,
        Households,
        IndustrialCompanies,
        CommercialCompanies,
        OfficeCompanies,
        Count,
    }

    /// <summary>How strongly one group bears the policy's cost or receives its benefit (Wilson 1980 concentration, 0..1).</summary>
    public readonly struct GroupStake
    {
        public readonly TargetGroup Group;
        public readonly float Burden;
        public readonly float Benefit;

        public GroupStake(TargetGroup group, float burden, float benefit)
        {
            Group = group;
            Burden = burden;
            Benefit = benefit;
        }
    }

    /// <summary>Staff a policy needs from one series at full intensity: fixed + per target unit × target count.</summary>
    public readonly struct StaffRequirement
    {
        public readonly ServiceSeries Series;
        public readonly float Fixed;
        public readonly float PerUnit;

        public StaffRequirement(ServiceSeries series, float fixedStaff, float perUnit)
        {
            Series = series;
            Fixed = fixedStaff;
            PerUnit = perUnit;
        }
    }

    public enum OutputKind : byte
    {
        /// <summary>Relative vanilla city modifier on the policy prefab; vanilla applies and stacks it.</summary>
        CityModifier,

        /// <summary>Relative change of a parameter singleton field (see <see cref="ParameterLevers"/>).</summary>
        Parameter,

        /// <summary>Monthly income row on the budget page.</summary>
        Income,

        /// <summary>Monthly expense row on the budget page.</summary>
        Expense,
    }

    /// <summary>What a money output scales with besides its target count.</summary>
    public enum MoneyBasis : byte
    {
        /// <summary>Paid as soon as the policy is set (programme costs).</summary>
        Intensity,

        /// <summary>Follows the delivered effect (revenue that depends on compliance).</summary>
        Delivery,

        /// <summary>Computed by the policy's mechanism (tax revenue, subsidy payments); the per-unit value is the scale.</summary>
        Mechanism,
    }

    /// <summary>One effect of a policy. Effects are reached at full intensity and full delivery.</summary>
    public readonly struct PolicyOutput
    {
        public readonly OutputKind Kind;
        public readonly CityModifierType Modifier;

        /// <summary>Parameter lever id, or budget row id (also the locale key EconomyPanel.BUDGET_ITEM[id]).</summary>
        public readonly string Key;

        /// <summary>Relative effect for modifiers and parameters.</summary>
        public readonly float FullEffect;

        /// <summary>Money per month (one in-game day): fixed + per unit × count of <see cref="Unit"/>.</summary>
        public readonly float FixedMonthly;
        public readonly float PerUnitMonthly;
        public readonly TargetGroup Unit;

        /// <summary>Vanilla IncomeSource or ExpenseSource the money is booked under.</summary>
        public readonly int Host;
        public readonly MoneyBasis Basis;

        private PolicyOutput(OutputKind kind, CityModifierType modifier, string key, float fullEffect, float fixedMonthly, float perUnitMonthly, TargetGroup unit, int host, MoneyBasis basis)
        {
            Kind = kind;
            Modifier = modifier;
            Key = key;
            FullEffect = fullEffect;
            FixedMonthly = fixedMonthly;
            PerUnitMonthly = perUnitMonthly;
            Unit = unit;
            Host = host;
            Basis = basis;
        }

        public static PolicyOutput CityModifier(CityModifierType type, float fullEffect) =>
            new PolicyOutput(OutputKind.CityModifier, type, null, fullEffect, 0f, 0f, TargetGroup.None, 0, MoneyBasis.Delivery);

        public static PolicyOutput Parameter(string lever, float fullEffect) =>
            new PolicyOutput(OutputKind.Parameter, default, lever, fullEffect, 0f, 0f, TargetGroup.None, 0, MoneyBasis.Delivery);

        public static PolicyOutput Income(string row, IncomeSource host, float fixedMonthly, float perUnitMonthly, TargetGroup unit, MoneyBasis basis) =>
            new PolicyOutput(OutputKind.Income, default, row, 0f, fixedMonthly, perUnitMonthly, unit, (int)host, basis);

        public static PolicyOutput Expense(string row, ExpenseSource host, float fixedMonthly, float perUnitMonthly, TargetGroup unit, MoneyBasis basis) =>
            new PolicyOutput(OutputKind.Expense, default, row, 0f, fixedMonthly, perUnitMonthly, unit, (int)host, basis);
    }

    /// <summary>
    /// Everything about one mod policy. Adding a policy means adding a definition here and its texts to Locale.cs
    /// (Policy.TITLE[id], Policy.DESCRIPTION[id], and EconomyPanel.BUDGET_ITEM[row] for money outputs).
    /// </summary>
    public sealed class PolicyDefinition
    {
        public string Id;
        public Instrument Instrument;
        public Direction Direction;
        public Mechanism Mechanism;

        /// <summary>How easy breaches are to see (technology standards are easier to inspect than performance standards).</summary>
        public float InspectionEase = 1f;
        public PolicyCategory Category = PolicyCategory.CityPlanning;

        /// <summary>Policy tab icon (a game UI path); null uses <see cref="PolicyCatalog.kDefaultIcon"/>.</summary>
        public string Icon;

        /// <summary>Name of a vanilla toggle this policy replaces: hidden from the list, and switched over when a city loads.</summary>
        public string ReplacesVanilla;

        /// <summary>Mod policy this one only works with; while that one is off, this one counts as off.</summary>
        public string RequiresPolicy;

        public bool IsSlider;
        public Bounds1 SliderRange;
        public float SliderDefault;
        public float SliderStep;
        public PolicySliderUnit SliderUnit;

        /// <summary>Series whose capacity carries the policy (Pressman–Wildavsky joint action, formula ③); weights sum to 1.</summary>
        public SeriesWeight[] Series;

        /// <summary>Clearance points n in p0^n.</summary>
        public int ClearancePoints;

        /// <summary>Staff needed at full intensity (formula ④).</summary>
        public StaffRequirement[] Staff;

        /// <summary>What the per-unit staff scales with.</summary>
        public TargetGroup Target;

        /// <summary>Slider change (share of the range) that costs nothing (formula ⑥, δ_k).</summary>
        public float ChangeBand;

        /// <summary>Quadratic cost of a slider change beyond the band (κ_k), as extra workload relative to the policy's own staff need.</summary>
        public float ChangeCost;

        /// <summary>One-off extra workload for switching a toggle on or off, relative to the policy's own staff need.</summary>
        public float ToggleCost;

        /// <summary>Base adjustment time τ0 in game days (formula ⑤).</summary>
        public float BaseLagDays;

        public PolicyOutput[] Outputs;

        /// <summary>Groups that bear the cost or receive the benefit (§6 option (나)); plaintiffs come from these.</summary>
        public GroupStake[] Groups;

        /// <summary>Prefab entity, set when the prefab is registered.</summary>
        public Entity Prefab;

        /// <summary>Prefab entity of <see cref="ReplacesVanilla"/>, set at registration.</summary>
        public Entity ReplacedPrefab;

        /// <summary>Normalized intensity 0..1 for a policy entry on the city.</summary>
        public float Intensity(bool active, float adjustment)
        {
            if (!active)
            {
                return 0f;
            }
            if (!IsSlider)
            {
                return 1f;
            }
            float span = SliderRange.max - SliderRange.min;
            return span > 0f ? Unity.Mathematics.math.saturate((adjustment - SliderRange.min) / span) : 0f;
        }
    }

    public static class PolicyDefinitions
    {
        public const string kEmissionStandardEnforcementRow = "RealisticPolicies.EmissionStandard.Enforcement";
        public const string kMonitoringRow = "RealisticPolicies.EnvironmentalMonitoring.Staff";
        public const string kDisclosureRow = "RealisticPolicies.EmissionDisclosure.Operation";
        public const string kTradingOperationRow = "RealisticPolicies.EmissionTrading.Operation";
        public const string kAuctionIncomeRow = "RealisticPolicies.AllowanceAuction.Income";

        /// <summary>Largest cap cut the emission trading slider allows (60%, as the emission standard).</summary>
        public const float kTradingMaxCut = 0.6f;

        // Starting values from docs/PLAN.md §5 and §10.5; all are meant to be tuned in game.
        public static readonly PolicyDefinition[] All =
        {
            new PolicyDefinition
            {
                Id = PolicyCatalog.kEquipmentMandate,
                ReplacesVanilla = PolicyCatalog.kVanillaPollutionManagement,
                Icon = "Media/Game/Policies/AdvancedPollutionManagement.svg",
                Instrument = Instrument.Authority,
                Mechanism = Mechanism.Generic,
                // A filter is either installed or not.
                InspectionEase = 1.5f,
                Direction = Direction.Burden,
                IsSlider = false,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.6f),
                    new SeriesWeight(ServiceSeries.Environment, 0.4f),
                },
                ClearancePoints = 2,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 3f, 0f),
                    new StaffRequirement(ServiceSeries.Environment, 0f, 1f / 50f),
                },
                Target = TargetGroup.IndustrialCompanies,
                // Companies must buy equipment: a large sunk investment.
                ToggleCost = 0.6f,
                BaseLagDays = 4f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.CityModifier(CityModifierType.IndustrialAirPollution, -0.25f),
                    PolicyOutput.CityModifier(CityModifierType.IndustrialGroundPollution, -0.25f),
                    PolicyOutput.CityModifier(CityModifierType.IndustrialGarbage, 0.15f),
                    // Every company pays for the same equipment.
                    PolicyOutput.CityModifier(CityModifierType.IndustrialEfficiency, -0.03f),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.IndustrialCompanies, 1f, 0f),
                    new GroupStake(TargetGroup.Households, 0f, 0.2f),
                },
            },
            new PolicyDefinition
            {
                Id = PolicyCatalog.kEmissionStandard,
                Icon = "Media/Game/Policies/CombustionEngineBan.svg",
                Instrument = Instrument.Authority,
                Mechanism = Mechanism.PerformanceStandard,
                Direction = Direction.Burden,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 60f),
                SliderDefault = 20f,
                SliderStep = 5f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.4f),
                    new SeriesWeight(ServiceSeries.Environment, 0.4f),
                    new SeriesWeight(ServiceSeries.EngineeringUtilities, 0.2f),
                },
                ClearancePoints = 3,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 5f, 0f),
                    new StaffRequirement(ServiceSeries.Environment, 0f, 1f / 20f),
                },
                Target = TargetGroup.IndustrialCompanies,
                // Process changes: even small raises are costly.
                ChangeBand = 0.1f,
                ChangeCost = 3f,
                BaseLagDays = 6f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.CityModifier(CityModifierType.IndustrialAirPollution, -0.6f),
                    PolicyOutput.CityModifier(CityModifierType.IndustrialGroundPollution, -0.6f),
                    PolicyOutput.CityModifier(CityModifierType.IndustrialEfficiency, -0.06f),
                    // Filter waste.
                    PolicyOutput.CityModifier(CityModifierType.IndustrialGarbage, 0.05f),
                    // Inspection costs (PLAN §10.5: small enforcement expense), paid from the moment the standard is set.
                    PolicyOutput.Expense(kEmissionStandardEnforcementRow, ExpenseSource.ServiceUpkeep, 500f, 20f, TargetGroup.IndustrialCompanies, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.IndustrialCompanies, 1f, 0f),
                    new GroupStake(TargetGroup.Households, 0f, 0.2f),
                },
            },
            new PolicyDefinition
            {
                Id = PolicyCatalog.kPollutionTax,
                Instrument = Instrument.Treasure,
                Direction = Direction.Burden,
                Mechanism = Mechanism.EmissionTax,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.8f),
                    new SeriesWeight(ServiceSeries.EngineeringUtilities, 0.2f),
                },
                ClearancePoints = 2,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 4f, 1f / 40f),
                },
                Target = TargetGroup.IndustrialCompanies,
                ChangeBand = 0.15f,
                ChangeCost = 2f,
                BaseLagDays = 6f / 24f,
                Outputs = new[]
                {
                    // Modifier values are reached at the base price; the firm model shapes them in between.
                    PolicyOutput.CityModifier(CityModifierType.IndustrialAirPollution, -0.34f),
                    PolicyOutput.CityModifier(CityModifierType.IndustrialGroundPollution, -0.34f),
                    PolicyOutput.CityModifier(CityModifierType.IndustrialEfficiency, -0.08f),
                    PolicyOutput.Income(BudgetInjectionSystem.kIncomeRowId, IncomeSource.TaxIndustrial, 0f, 2000f, TargetGroup.IndustrialCompanies, MoneyBasis.Mechanism),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.IndustrialCompanies, 1f, 0f),
                    new GroupStake(TargetGroup.Households, 0f, 0.2f),
                },
            },
            new PolicyDefinition
            {
                Id = PolicyCatalog.kGreenSubsidy,
                Icon = "Media/Game/Policies/SmallBusinessEnthusiast.svg",
                Instrument = Instrument.Treasure,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.AbatementSubsidy,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 1f),
                },
                ClearancePoints = 2,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 3f, 1f / 50f),
                },
                Target = TargetGroup.IndustrialCompanies,
                ChangeBand = 0.2f,
                ChangeCost = 1.5f,
                BaseLagDays = 8f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.CityModifier(CityModifierType.IndustrialAirPollution, -0.34f),
                    PolicyOutput.CityModifier(CityModifierType.IndustrialGroundPollution, -0.34f),
                    // Subsidies draw new industry in, which can bring total pollution back up (Baumol & Oates).
                    PolicyOutput.Parameter(ParameterLevers.kIndustrialBaseDemand, 0.15f),
                    PolicyOutput.Expense(BudgetInjectionSystem.kExpenseRowId, ExpenseSource.SubsidyIndustrial, 0f, 2000f, TargetGroup.IndustrialCompanies, MoneyBasis.Mechanism),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.IndustrialCompanies, 0f, 1f),
                    new GroupStake(TargetGroup.Households, 0.1f, 0f),
                },
            },
            new PolicyDefinition
            {
                Id = PolicyCatalog.kEnvironmentalMonitoring,
                Icon = "Media/Game/Policies/FireSafetyInspections.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Monitoring,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.7f),
                    new SeriesWeight(ServiceSeries.PostAndTelecom, 0.3f),
                },
                ClearancePoints = 1,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.Environment, 2f, 1f / 25f),
                },
                Target = TargetGroup.IndustrialCompanies,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 4f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.Expense(kMonitoringRow, ExpenseSource.ServiceUpkeep, 1000f, 30f, TargetGroup.IndustrialCompanies, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.IndustrialCompanies, 0.6f, 0f),
                    new GroupStake(TargetGroup.Households, 0f, 0.2f),
                },
            },
            new PolicyDefinition
            {
                Id = PolicyCatalog.kEmissionDisclosure,
                Icon = "Media/Game/Policies/EnergyConsumptionAwareness.svg",
                Instrument = Instrument.Nodality,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Disclosure,
                IsSlider = false,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.PostAndTelecom, 0.6f),
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.4f),
                },
                ClearancePoints = 1,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 1f, 0f),
                    new StaffRequirement(ServiceSeries.PostAndTelecom, 1f, 0f),
                },
                Target = TargetGroup.IndustrialCompanies,
                ToggleCost = 0.2f,
                BaseLagDays = 6f / 24f,
                Outputs = new[]
                {
                    // Only companies for which cutting is nearly free respond to publicity alone.
                    PolicyOutput.CityModifier(CityModifierType.IndustrialAirPollution, -0.05f),
                    PolicyOutput.CityModifier(CityModifierType.IndustrialGroundPollution, -0.05f),
                    PolicyOutput.Expense(kDisclosureRow, ExpenseSource.ServiceUpkeep, 300f, 2f, TargetGroup.IndustrialCompanies, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.IndustrialCompanies, 0.3f, 0f),
                    new GroupStake(TargetGroup.Households, 0f, 0.1f),
                },
            },
            // §10.6 emission trading: a cap on industrial air pollution (stands in for carbon); the price clears it.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kEmissionTrading,
                Icon = "Media/Game/Policies/NoSmoking.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Burden,
                Mechanism = Mechanism.EmissionTrading,
                IsSlider = true,
                SliderRange = new Bounds1(0f, kTradingMaxCut * 100f),
                SliderDefault = 20f,
                SliderStep = 5f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.5f),
                    new SeriesWeight(ServiceSeries.Environment, 0.5f),
                },
                ClearancePoints = 3,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 4f, 0f),
                    new StaffRequirement(ServiceSeries.Environment, 0f, 1f / 25f),
                },
                Target = TargetGroup.IndustrialCompanies,
                ChangeBand = 0.15f,
                ChangeCost = 2.5f,
                BaseLagDays = 8f / 24f,
                Outputs = new[]
                {
                    // Full effects at the largest cap; the market shapes them (PolicyEngineSystem.Shape).
                    PolicyOutput.CityModifier(CityModifierType.IndustrialAirPollution, -kTradingMaxCut),
                    PolicyOutput.CityModifier(CityModifierType.IndustrialEfficiency, -0.1f),
                    PolicyOutput.Expense(kTradingOperationRow, ExpenseSource.ServiceUpkeep, 400f, 10f, TargetGroup.IndustrialCompanies, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.IndustrialCompanies, 1f, 0f),
                    new GroupStake(TargetGroup.Households, 0f, 0.2f),
                },
            },
            // §10.6 auctioning: the share of the cap sold instead of given away; city income, small carbon leakage.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kAllowanceAuction,
                Icon = "Media/Game/Policies/BigBusinessBenefactor.svg",
                Instrument = Instrument.Treasure,
                Direction = Direction.Burden,
                Mechanism = Mechanism.AllowanceAuction,
                RequiresPolicy = PolicyCatalog.kEmissionTrading,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 10f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 1f),
                },
                ClearancePoints = 2,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 2f, 0f),
                },
                Target = TargetGroup.IndustrialCompanies,
                ChangeBand = 0.2f,
                ChangeCost = 1.5f,
                BaseLagDays = 4f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.Income(kAuctionIncomeRow, IncomeSource.TaxIndustrial, 0f, 2000f, TargetGroup.IndustrialCompanies, MoneyBasis.Mechanism),
                    // Carbon leakage, in proportion to what companies pay for allowances (Dechezleprêtre et al. 2022: little so far).
                    PolicyOutput.Parameter(ParameterLevers.kIndustrialBaseDemand, -0.03f),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.IndustrialCompanies, 0.6f, 0f),
                },
            },
        };
    }

    /// <summary>Model constants (docs/PLAN.md §5–§6). Starting values, to be tuned in game.</summary>
    public static class EngineParameters
    {
        // ① Cobb–Douglas series capacity. No staff means no capacity (user decision: no floor).
        public const float kLabourShare = 0.6f;

        // ② Weberian administrative hub.
        public const float kAdminBase = 0.8f;
        public const float kAdminEfficiencyGain = 0.4f;

        // ③ Pressman–Wildavsky clearance probability.
        public const float kClearanceProbability = 0.98f;

        // ④ M/M/1 utilization cap for the delay; utilization with no staff at all.
        public const float kMaxUtilization = 0.95f;
        public const float kUnstaffedUtilization = 1000f;

        // ⑥ The band and costs are per policy (PolicyDefinition); only the decay is shared.
        public const float kSurgeDecayDays = 1f;

        // ⑦ Compliance: Becker deterrence + Tyler voluntary compliance.
        // Detection = 1 - exp(-kDetection × E × ease × (base inspection + watch gain × monitoring)).
        public const float kDetection = 3f;
        public const float kBaseInspection = 0.4f;
        public const float kWatchGain = 1.2f;

        // Expected fine of a performance standard, in abatement-cost units (firm model).
        public const float kStandardFine = 0.25f;

        // Disclosure raises voluntary compliance, scaled by detection relative to the reference (Ayres & Braithwaite).
        public const float kDisclosureVoluntaryGain = 0.15f;
        public const float kDisclosureReferenceDetection = 0.5f;

        // Share of the pollution tax bill that shows up as lost industrial efficiency.
        public const float kTaxEfficiencyBurden = 0.3f;
        public const float kFineToMedianGain = 2f;
        public const float kGainSpread = 2f;
        public const float kVoluntaryMin = 0.1f;
        public const float kVoluntaryMax = 0.6f;

        // ⑦' Bernstein capture.
        public const float kCaptureMax = 0.4f;
        public const float kCaptureDays = 30f;
        public const float kCaptureResetOnRetune = 0.5f;

        // ⑧ Legitimacy and the reference point (Kahneman–Tversky).
        public const float kLossAversion = 2.25f;
        public const float kReferenceDays = 1f;
        public const float kLegitimacyBase = 0.6f;
        public const float kGrievanceRate = 0.045f;
        public const float kOutputRate = 0.02f;
        public const float kExpectedDelivery = 0.5f;
        public const float kLegitimacyReturnRate = 0.2f;
        public const float kWarningDrop = 0.02f;

        // N-policy credibility (Kydland–Prescott).
        public const float kCredibilityLoss = 0.5f;
        public const float kCredibilityDays = 0.5f;
    }
}
