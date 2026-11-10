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

        // Units only: staff and money can scale with these, but they are never stakeholders (no plaintiffs).

        /// <summary>Citizens of homeless households.</summary>
        HomelessCitizens,

        /// <summary>Citizens marked as criminals (planning, wanted, arrested or in prison).</summary>
        Criminals,

        /// <summary>Crimes in the last month (one in-game day), from the city statistics.</summary>
        MonthlyCrimes,

        /// <summary>Citizens studying at college or university.</summary>
        TertiaryStudents,

        /// <summary>Households below "modest" wealth (the game's wretched and poor levels), homeless ones included.</summary>
        PoorHouseholds,
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

        /// <summary>Share of the city's own tax income, following the delivered effect; the per-unit value is the share.</summary>
        TaxShare,

        /// <summary>Paid by a system of its own (livelihood support pays households one by one); the engine leaves the row alone.</summary>
        External,
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

        /// <summary>The modifier adds an amount (vanilla Absolute mode, e.g. seconds) instead of a relative change.</summary>
        public readonly bool Absolute;

        private PolicyOutput(OutputKind kind, CityModifierType modifier, string key, float fullEffect, float fixedMonthly, float perUnitMonthly, TargetGroup unit, int host, MoneyBasis basis, bool absolute = false)
        {
            Absolute = absolute;
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

        /// <summary>A city modifier that adds <paramref name="fullAmount"/> at full delivery (vanilla Absolute mode).</summary>
        public static PolicyOutput CityModifierAbsolute(CityModifierType type, float fullAmount) =>
            new PolicyOutput(OutputKind.CityModifier, type, null, fullAmount, 0f, 0f, TargetGroup.None, 0, MoneyBasis.Delivery, absolute: true);

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
    /// <summary>
    /// The second value of a policy, set in the detail column under "세부 설정" (docs/previews/policy-second-slider.html):
    /// the game's policy row has room for one slider only. Changing it counts like moving the policy for lawsuits
    /// (Scales: the felt level is the first slider × value ÷ default) and, through the money it moves, for protests.
    /// </summary>
    public sealed class SecondSetting
    {
        /// <summary>Locale key part: "RealisticPolicies.UI.Second.{Key}" (label) and ".Hint".</summary>
        public string Key;
        public Bounds1 Range;
        public float Step;
        public float Default;

        /// <summary>"money" (¢), "months" or "minutes": how the UI writes the value.</summary>
        public string Unit;

        /// <summary>The felt level of the policy (lawsuits) and, for programme sizes, its scale follow value ÷ default.</summary>
        public bool ScalesIntensity;
    }

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

        /// <summary>The vanilla lever only works inside welfare coverage; the report warns when the city has no welfare office.</summary>
        public bool NeedsWelfareCoverage;

        /// <summary>Administrative reform this policy is (§14.10); it changes the administrative layer, see <see cref="ReformEffects"/>.</summary>
        public ReformKind Reform;

        /// <summary>
        /// Days in force over which the policy's lever effect halves (0 = no fading), down to <see cref="EffectFloor"/>.
        /// Novelty that wears off: innovation city inflows peaked early and then declined (임소현·지수호 2024).
        /// </summary>
        public float EffectHalfLifeDays;
        public float EffectFloor;

        /// <summary>A second value set in the policy's detail column (2026-11-10 user decision), or null.</summary>
        public SecondSetting Second;

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

        /// <summary>Extra factor on the change cost when the policy is cut back (hard to undo, e.g. contracting out).</summary>
        public float CutCostScale = 1f;

        /// <summary>
        /// Shift of the legitimacy base at full delivery, for policies whose own conduct builds or spends trust in
        /// the city (§14.15 policing; Tyler 2004). Zero for almost all policies.
        /// </summary>
        public float LegitimacyShift;

        /// <summary>
        /// Policy whose delivery makes this one work better (§14.8 interactions): modifiers and levers × (1 +
        /// AmplifyScale × its progress). Money is not scaled. Null for most policies.
        /// </summary>
        public string AmplifiedBy;
        public float AmplifyScale;

        /// <summary>When set, the amplification touches only this modifier (not the levers or other modifiers).</summary>
        public CityModifierType? AmplifyOnly;

        /// <summary>
        /// Effects scale with the city's legitimacy against its base, 0.5-1.2 times (§14.8: people report crime and
        /// help the police more when they trust them, Tyler & Fagan 2008).
        /// </summary>
        public bool ScalesWithLegitimacy;

        /// <summary>
        /// Effects also scale with the post and telecom capacity (§14.18 early warning: an alert reaches people only
        /// through the networks), on top of the policy's own series.
        /// </summary>
        public bool ScalesWithTelecom;

        /// <summary>
        /// Effects scale with the share of the population at college or university, full at 5% (§14.20-2: firms gain
        /// from research only as far as there are people to absorb it, Cohen & Levinthal 1990).
        /// </summary>
        public bool ScalesWithStudents;

        /// <summary>
        /// Staff follow the cases, not the slider: the slider sets the amount each case gets (livelihood support, one
        /// social worker per 60 households, 2026-11-09 user decision). Turning such a policy up does not take more staff.
        /// </summary>
        public bool StaffPerCase;

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
        public const string kMinimumWageRow = "RealisticPolicies.MinimumWage.Inspection";
        public const string kTalentRow = "RealisticPolicies.TalentAttraction.Grants";
        public const string kRedevelopmentRow = "RealisticPolicies.Redevelopment.Grants";
        public const string kOutreachRow = "RealisticPolicies.WelfareOutreach.Visits";
        public const string kHousingFirstRow = "RealisticPolicies.HousingFirst.Housing";
        public const string kReentryRow = "RealisticPolicies.ReentrySupport.Programs";
        public const string kVictimSupportRow = "RealisticPolicies.VictimSupport.Services";
        public const string kEGovernmentRow = "RealisticPolicies.EGovernment.Systems";
        public const string kSpecialistRow = "RealisticPolicies.SpecialistPosts.Allowances";
        public const string kBudgetRow = "RealisticPolicies.ParticipatoryBudgeting.Operation";
        public const string kHearingRow = "RealisticPolicies.PublicHearings.Hearings";
        public const string kPerformanceRow = "RealisticPolicies.PerformanceManagement.Bonuses";
        public const string kOmbudsmanRow = "RealisticPolicies.Ombudsman.Office";
        public const string kDisclosureOfficeRow = "RealisticPolicies.InformationDisclosure.Office";
        public const string kArrearsCostRow = "RealisticPolicies.ArrearsCollection.Collection";
        public const string kArrearsIncomeRow = "RealisticPolicies.ArrearsCollection.Collected";
        public const string kAgreementRow = "RealisticPolicies.InterMunicipalAgreement.Share";
        public const string kContractingRow = "RealisticPolicies.ContractingOut.Oversight";
        public const string kClusterRow = "RealisticPolicies.IndustryCluster.Grants";
        public const string kRescueRow = "RealisticPolicies.CorporateRescue.Fund";
        public const string kJobPactRow = "RealisticPolicies.RegionalJobPact.Share";
        public const string kUnemploymentRow = "RealisticPolicies.UnemploymentDuration.Benefits";
        public const string kQuotaRow = "RealisticPolicies.ResourceQuota.Monitoring";
        public const string kRentCapRow = "RealisticPolicies.RentCap.Disputes";
        public const string kNoiseRow = "RealisticPolicies.NoiseControl.Inspection";
        public const string kVehicleRow = "RealisticPolicies.VehicleEmissions.Testing";
        public const string kWaterRow = "RealisticPolicies.DrinkingWater.Treatment";
        public const string kGroundwaterRow = "RealisticPolicies.GroundwaterProtection.Metering";
        public const string kParkRow = "RealisticPolicies.ParkCare.Upkeep";
        public const string kSpeedCameraRow = "RealisticPolicies.SpeedCameras.Operation";
        public const string kDisasterCodeRow = "RealisticPolicies.DisasterCode.Inspection";
        public const string kFireCodeRow = "RealisticPolicies.FireCode.Inspection";
        public const string kCommunityPolicingRow = "RealisticPolicies.CommunityPolicing.Patrols";
        public const string kZeroToleranceRow = "RealisticPolicies.ZeroTolerance.Enforcement";
        public const string kSentencingRow = "RealisticPolicies.HarsherSentencing.Prisons";
        public const string kEmergencyResponseRow = "RealisticPolicies.EmergencyResponse.Dispatch";
        public const string kCptedRow = "RealisticPolicies.Cpted.Design";
        public const string kHealthCampaignRow = "RealisticPolicies.HealthCampaign.Media";
        public const string kEmergencyCareRow = "RealisticPolicies.EmergencyCare.Network";
        public const string kAirAlertRow = "RealisticPolicies.AirQualityAlert.Alerts";
        public const string kPublicHealthcareRow = "RealisticPolicies.PublicHealthcare.Staff";
        public const string kScholarshipRow = "RealisticPolicies.Scholarships.Grants";
        public const string kFreeHighSchoolRow = "RealisticPolicies.FreeHighSchool.Fees";
        public const string kLifelongRow = "RealisticPolicies.LifelongLearning.Courses";
        public const string kWarningRow = "RealisticPolicies.DisasterWarning.Network";
        public const string kEnergyCodeRow = "RealisticPolicies.EnergyCode.Inspection";
        public const string kWasteFeeRow = "RealisticPolicies.WasteFee.BagSales";
        public const string kUniversityIndustryRow = "RealisticPolicies.UniversityIndustry.Grants";
        public const string kLivelihoodRow = "RealisticPolicies.LivelihoodSupport.Payments";
        public const string kRentSupportRow = "RealisticPolicies.RentSupport.Payments";
        public const string kFlexibleHoursRow = "RealisticPolicies.FlexibleHours.Coordination";

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
                    // Cutting costs plus a tenth of the bill; at the base cut this is below the emission standard's
                    // loss at the same cut (-3.8%), as a price spreads the cuts to the cheapest companies (§14.9).
                    PolicyOutput.CityModifier(CityModifierType.IndustrialEfficiency, -0.03f),
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
                    // At the largest cap, all auctioned: cutting costs are about 60% of the emission standard's at the
                    // same 60% cut (the market sends cuts to the cheapest companies), plus a tenth of the allowance
                    // bill, so below the standard's -6% (§14.9, 2026-11-05).
                    PolicyOutput.CityModifier(CityModifierType.IndustrialEfficiency, -0.04f),
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
            // §14.6 minimum wage: the simple-job wage, with a spillover to basic jobs (홍장표·문영만 2022).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kMinimumWage,
                Icon = "Media/Game/Policies/PaidParking.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 40f),
                SliderDefault = 10f,
                SliderStep = 5f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 1f),
                },
                ClearancePoints = 2,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 3f, 1f / 100f),
                },
                Target = TargetGroup.CommercialCompanies,
                // Usual yearly raises were 7–8%; a jump beyond 10 points stirs opposition (강창희 2021).
                ChangeBand = 0.25f,
                ChangeCost = 2f,
                BaseLagDays = 6f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.Parameter(ParameterLevers.kWage0, 0.4f),
                    PolicyOutput.Parameter(ParameterLevers.kWage1, 0.12f),
                    // §14.8: an 8% rise cut returns to prison within a year by 3.8% (Agan & Makowsky 2023). Vanilla's only
                    // reoffending lever works inside welfare coverage, so this is an approximation (+0.2 at +40%).
                    PolicyOutput.Parameter(ParameterLevers.kWelfareRecurrence, 0.2f),
                    PolicyOutput.Expense(kMinimumWageRow, ExpenseSource.ServiceUpkeep, 300f, 5f, TargetGroup.CommercialCompanies, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0f, 0.5f),
                    new GroupStake(TargetGroup.CommercialCompanies, 0.8f, 0f),
                    new GroupStake(TargetGroup.IndustrialCompanies, 0.6f, 0f),
                    new GroupStake(TargetGroup.OfficeCompanies, 0.2f, 0f),
                },
            },
            // §14.6 talent attraction: settlement grants raise the share of highly educated newcomers, fading over time.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kTalentAttraction,
                Icon = "Media/Game/Policies/HighTechHousing.svg",
                Instrument = Instrument.Treasure,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                // Months of grants per newcomer household (2026-11-10): longer grants bring more and cost more, linearly
                // against 6 months (estimate).
                Second = new SecondSetting { Key = "TalentMonths", Range = new Bounds1(1f, 24f), Step = 1f, Default = 6f, Unit = "months", ScalesIntensity = true },
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 1f),
                },
                ClearancePoints = 2,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 2f, 1f / 20000f),
                },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1.5f,
                // Inflows follow jobs and housing with a delay (임태경 2026).
                BaseLagDays = 1f,
                EffectHalfLifeDays = 10f,
                // At least half stays while the grants run: the Danish foreigners' scheme kept working as long as it
                // lasted (Kleven et al. 2014); the faster fade came from a one-off relocation (§14.9).
                EffectFloor = 0.5f,
                Outputs = new[]
                {
                    PolicyOutput.Parameter(ParameterLevers.kNewCitizenHighEducation, 1f),
                    PolicyOutput.Expense(kTalentRow, ExpenseSource.SubsidyResidential, 1000f, 0.05f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.OfficeCompanies, 0f, 0.3f),
                },
            },
            // §14.6 redevelopment: cheaper building upgrades; higher levels raise rents and push poorer households out.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kRedevelopment,
                Icon = "Media/Game/Policies/GatedCommunity.svg",
                Instrument = Instrument.Treasure,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 50f),
                SliderDefault = 20f,
                SliderStep = 5f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.6f),
                    new SeriesWeight(ServiceSeries.FacilityMaintenance, 0.4f),
                },
                ClearancePoints = 2,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 3f, 0f),
                    new StaffRequirement(ServiceSeries.FacilityMaintenance, 2f, 0f),
                },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1.5f,
                BaseLagDays = 12f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.CityModifier(CityModifierType.BuildingLevelingCost, -0.5f),
                    PolicyOutput.Expense(kRedevelopmentRow, ExpenseSource.SubsidyResidential, 500f, 0.03f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.CommercialCompanies, 0f, 0.4f),
                    new GroupStake(TargetGroup.IndustrialCompanies, 0f, 0.3f),
                    new GroupStake(TargetGroup.Households, 0.3f, 0f),
                },
            },
            // §14.7 welfare outreach: visiting teams find people in need, so welfare coverage lifts unhappy citizens more.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kWelfareOutreach,
                Icon = "Media/Game/Policies/TouristInformation.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                NeedsWelfareCoverage = true,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.SocialWelfare, 1f),
                },
                ClearancePoints = 1,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.SocialWelfare, 2f, 1f / 10000f),
                },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 4f / 24f,
                Outputs = new[]
                {
                    // One more team member finds 9–17% more people in need (이영글 외 2019); doubling a team, about +50%.
                    PolicyOutput.Parameter(ParameterLevers.kWelfare, 0.5f),
                    PolicyOutput.Expense(kOutreachRow, ExpenseSource.ServiceUpkeep, 300f, 0.02f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0f, 0.4f),
                },
            },
            // §14.7 Housing First: homes plus case management take people off the street, so homelessness harms less.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kHousingFirst,
                Icon = "Media/Game/Policies/ExtraInsulation.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.SocialWelfare, 1f),
                },
                ClearancePoints = 2,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.SocialWelfare, 2f, 1f / 50f),
                },
                Target = TargetGroup.HomelessCitizens,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                // A rent grant of about two months before people settle (남기철 2009).
                BaseLagDays = 2f,
                // §14.8: housing support reaches the street homeless only when someone finds and links them (a
                // continuum of support, 박은철 2015). +20% at full outreach (starting value, no measured size).
                AmplifiedBy = PolicyCatalog.kWelfareOutreach,
                AmplifyScale = 0.2f,
                Outputs = new[]
                {
                    // About half stay housed: 45.6% with short rent grants, 90% with public rentals (남기철 2009).
                    PolicyOutput.Parameter(ParameterLevers.kHomelessHealth, -0.5f),
                    PolicyOutput.Parameter(ParameterLevers.kHomelessWellbeing, -0.5f),
                    PolicyOutput.Parameter(ParameterLevers.kHomelessGarbage, -0.5f),
                    PolicyOutput.Parameter(ParameterLevers.kHomelessNoise, -0.5f),
                    // §14.8: Housing First reoffending 0.29–0.55 times the control (Somers et al. 2013) and 39.1% of
                    // the homeless have been in prison (김연수·신소라 2016). Only homeless offenders gain, so a quarter of
                    // reentry support's lever (starting value).
                    PolicyOutput.Parameter(ParameterLevers.kWelfareRecurrence, 0.3f),
                    // §14.8: emergency visits 0.63 and admissions 0.76 times (Baxter et al. 2019); small across the city.
                    PolicyOutput.CityModifier(CityModifierType.HospitalEfficiency, 0.02f),
                    PolicyOutput.Expense(kHousingFirstRow, ExpenseSource.SubsidyResidential, 200f, 2f, TargetGroup.HomelessCitizens, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0.1f, 0.2f),
                    new GroupStake(TargetGroup.CommercialCompanies, 0f, 0.2f),
                },
            },
            // §14.7 reentry support: jobs, training and counselling for released offenders inside welfare coverage.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kReentrySupport,
                // §14.8: each year in prison costs 3-6 points of later employment (Mueller-Smith 2015), so longer
                // sentences weaken job-based reentry support.
                AmplifiedBy = PolicyCatalog.kHarsherSentencing,
                AmplifyScale = -0.2f,
                Icon = "Media/Game/Policies/HarshPrisonSentences.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                NeedsWelfareCoverage = true,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.SocialWelfare, 1f),
                },
                ClearancePoints = 2,
                Staff = new[]
                {
                    // One officer per about 80 ex-offenders a year (배임호 2013).
                    new StaffRequirement(ServiceSeries.SocialWelfare, 2f, 1f / 80f),
                },
                Target = TargetGroup.Criminals,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 6f / 24f,
                Outputs = new[]
                {
                    // Target: about 30% less reoffending inside welfare coverage. Korean odds ratios (이동훈 외 2019) are
                    // observational; abroad, job programmes alone did not cut reoffending (Visher et al. 2005) and
                    // education cut it by 13 points (Davis et al. 2013). Starting value; the vanilla chance shrinks with
                    // city size, so this is set against the measured drop in game.
                    PolicyOutput.Parameter(ParameterLevers.kWelfareRecurrence, 1.2f),
                    PolicyOutput.Expense(kReentryRow, ExpenseSource.ServiceUpkeep, 300f, 3f, TargetGroup.Criminals, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0.1f, 0.2f),
                },
            },
            // §14.7 victim support: counselling, medical and legal help soften the harm crime leaves behind.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kVictimSupport,
                Icon = "Media/Game/Policies/AdvancedVaccinationProgram.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.SocialWelfare, 1f),
                },
                ClearancePoints = 1,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.SocialWelfare, 1f, 1f / 10f),
                },
                Target = TargetGroup.MonthlyCrimes,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 4f / 24f,
                Outputs = new[]
                {
                    // Trauma weighs most on life satisfaction (김성언 2018); trauma therapy has a moderate effect
                    // (d = 0.52, Sherman 1998) and general victim services less evidence, so 25% less harm.
                    PolicyOutput.Parameter(ParameterLevers.kHomeCrimeEffect, -0.25f),
                    PolicyOutput.Parameter(ParameterLevers.kWorkplaceCrimeEffect, -0.25f),
                    PolicyOutput.Expense(kVictimSupportRow, ExpenseSource.ServiceUpkeep, 200f, 20f, TargetGroup.MonthlyCrimes, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0f, 0.3f),
                },
            },
            // §14.10 administrative reforms: they change the administrative layer (ReformEffects), not a vanilla lever.
            // Procedure simplification: regulations pass fewer decision points and less rent seeking, but the court
            // finds more procedural fault.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kProcedureSimplification,
                Icon = "Media/Game/Policies/LogisticUpgrade.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                Reform = ReformKind.ProcedureSimplification,
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
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 3f, 0f),
                },
                Target = TargetGroup.None,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 6f / 24f,
                Outputs = new PolicyOutput[0],
                Groups = new[]
                {
                    new GroupStake(TargetGroup.IndustrialCompanies, 0f, 0.3f),
                    new GroupStake(TargetGroup.CommercialCompanies, 0f, 0.3f),
                    new GroupStake(TargetGroup.Households, 0.1f, 0f),
                },
            },
            // E-government: less paperwork per policy and more transparency, as far as post and telecom can carry it.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kEGovernment,
                Icon = "Media/Game/Policies/PaidOutsideServices.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                Reform = ReformKind.EGovernment,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.PostAndTelecom, 0.6f),
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.4f),
                },
                ClearancePoints = 2,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.PostAndTelecom, 2f, 1f / 20000f),
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 1f, 0f),
                },
                Target = TargetGroup.Households,
                // Switching systems stalls work at first (payment delays in Banerjee et al. 2020).
                ChangeBand = 0.1f,
                ChangeCost = 3f,
                BaseLagDays = 12f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.Expense(kEGovernmentRow, ExpenseSource.ServiceUpkeep, 500f, 0.02f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0f, 0.1f),
                },
            },
            // Civil service pay: vanilla service upkeep pays it; fewer quits and a little less corruption.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kCivilServicePay,
                Icon = "Media/Game/Policies/TaxiStartingFee.svg",
                Instrument = Instrument.Treasure,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                Reform = ReformKind.CivilServicePay,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 30f),
                SliderDefault = 10f,
                SliderStep = 5f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 1f),
                },
                ClearancePoints = 1,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 1f, 0f),
                },
                Target = TargetGroup.None,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 4f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.Parameter(ParameterLevers.kCityServiceWage, 0.3f),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0.1f, 0f),
                },
            },
            // Specialist posts: officials stay longer in one field; they learn the job, and grow close to those they oversee.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kSpecialistPosts,
                Icon = "Media/Game/Policies/SchoolsOut.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                Reform = ReformKind.SpecialistPosts,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 1f),
                },
                ClearancePoints = 1,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 2f, 0f),
                },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 12f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.Expense(kSpecialistRow, ExpenseSource.ServiceUpkeep, 200f, 0.01f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new GroupStake[0],
            },
            // Participatory budgeting: residents decide part of the budget; legitimacy grows slowly, money policies slow down.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kParticipatoryBudgeting,
                Icon = "Media/Game/Policies/CityPromotion.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                Reform = ReformKind.ParticipatoryBudgeting,
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
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 2f, 1f / 10000f),
                },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 12f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.Expense(kBudgetRow, ExpenseSource.ServiceUpkeep, 200f, 0.01f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0f, 0.3f),
                },
            },
            // Mandatory public hearings: raising a burden angers less and holds up better in court, but takes longer.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kPublicHearings,
                Icon = "Media/Game/Policies/AdvancedLightningRods.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Generic,
                Reform = ReformKind.PublicHearings,
                IsSlider = false,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 1f),
                },
                ClearancePoints = 1,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 2f, 0f),
                },
                Target = TargetGroup.None,
                ToggleCost = 0.3f,
                BaseLagDays = 4f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.Expense(kHearingRow, ExpenseSource.ServiceUpkeep, 300f, 0f, TargetGroup.None, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.IndustrialCompanies, 0.1f, 0f),
                    new GroupStake(TargetGroup.CommercialCompanies, 0.1f, 0f),
                    new GroupStake(TargetGroup.Households, 0f, 0.2f),
                },
            },
            // §14.11 administration and finance. Performance management: a little more delivery, and targets invite gaming.
            // City service buildings also run up to 3% better at full delivery (BuildingEffectSystem; small positive mean
            // effect, Gerrish 2016).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kPerformanceManagement,
                Icon = "Media/Game/Policies/RouteVehicleCount.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                Reform = ReformKind.PerformanceManagement,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[] { new SeriesWeight(ServiceSeries.GeneralAdministration, 1f) },
                ClearancePoints = 1,
                Staff = new[] { new StaffRequirement(ServiceSeries.GeneralAdministration, 2f, 0f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 12f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.Expense(kPerformanceRow, ExpenseSource.ServiceUpkeep, 200f, 0.01f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new GroupStake[0],
            },
            // Ombudsman: complaints get a hearing outside the courts.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kOmbudsman,
                Icon = "Media/Game/Policies/PreferParks.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                Reform = ReformKind.Ombudsman,
                IsSlider = false,
                Series = new[] { new SeriesWeight(ServiceSeries.GeneralAdministration, 1f) },
                ClearancePoints = 1,
                Staff = new[] { new StaffRequirement(ServiceSeries.GeneralAdministration, 2f, 1f / 20000f) },
                Target = TargetGroup.Households,
                ToggleCost = 0.2f,
                BaseLagDays = 8f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.Expense(kOmbudsmanRow, ExpenseSource.ServiceUpkeep, 300f, 0f, TargetGroup.None, MoneyBasis.Intensity),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0f, 0.2f) },
            },
            // Wider information disclosure: less capture and more trust, but plaintiffs can see more.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kInformationDisclosure,
                Icon = "Media/Game/Policies/WaterUseAwareness.svg",
                Instrument = Instrument.Nodality,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                Reform = ReformKind.InformationDisclosure,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.6f),
                    new SeriesWeight(ServiceSeries.PostAndTelecom, 0.4f),
                },
                ClearancePoints = 1,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 1f, 0f),
                    new StaffRequirement(ServiceSeries.PostAndTelecom, 1f, 0f),
                },
                Target = TargetGroup.None,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 8f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.Expense(kDisclosureOfficeRow, ExpenseSource.ServiceUpkeep, 200f, 0f, TargetGroup.None, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0f, 0.2f),
                    new GroupStake(TargetGroup.IndustrialCompanies, 0.1f, 0f),
                },
            },
            // Arrears collection: tax staff chase unpaid local taxes.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kArrearsCollection,
                Icon = "Media/Game/Policies/LotParkingFee.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Generic,
                Reform = ReformKind.ArrearsCollection,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[] { new SeriesWeight(ServiceSeries.Tax, 1f) },
                ClearancePoints = 1,
                Staff = new[] { new StaffRequirement(ServiceSeries.Tax, 3f, 1f / 5000f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 12f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.Expense(kArrearsCostRow, ExpenseSource.ServiceUpkeep, 200f, 0.01f, TargetGroup.Households, MoneyBasis.Intensity),
                    // About 5% of the levy goes unpaid; at full effort tax staff bring in up to 2% of tax income (estimate;
                    // 성남시 collected 40% more arrears with a survey team, 김순화 2024).
                    PolicyOutput.Income(kArrearsIncomeRow, IncomeSource.TaxResidential, 0f, 0.02f, TargetGroup.None, MoneyBasis.TaxShare),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0.2f, 0f),
                    new GroupStake(TargetGroup.CommercialCompanies, 0.1f, 0f),
                },
            },
            // Competitive bidding: cheaper upkeep for every service building, and some room for bid rigging.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kCompetitiveBidding,
                Icon = "Media/Game/Policies/RouteTicketPrice.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                Reform = ReformKind.CompetitiveBidding,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[] { new SeriesWeight(ServiceSeries.GeneralAdministration, 1f) },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.GeneralAdministration, 3f, 0f) },
                Target = TargetGroup.None,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 1f,
                Outputs = new[]
                {
                    // Lowest-price bids won at 65.6% of the estimate (백성준 2008); only part of upkeep is procured.
                    PolicyOutput.CityModifier(CityModifierType.CityServiceBuildingBaseUpkeepCost, -0.08f),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0f, 0.1f),
                    new GroupStake(TargetGroup.CommercialCompanies, 0.1f, 0f),
                },
            },
            // Inter-municipal service agreement: cheaper services bought from neighbours, most for small towns.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kInterMunicipalAgreement,
                Icon = "Media/Game/Policies/HeavyTrafficBan.svg",
                Instrument = Instrument.Treasure,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                Reform = ReformKind.InterMunicipalAgreement,
                IsSlider = false,
                Series = new[] { new SeriesWeight(ServiceSeries.GeneralAdministration, 1f) },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.GeneralAdministration, 1f, 0f) },
                Target = TargetGroup.None,
                ToggleCost = 0.3f,
                BaseLagDays = 1f,
                Outputs = new[]
                {
                    PolicyOutput.CityModifier(CityModifierType.CityServiceImportCost, -0.2f),
                    PolicyOutput.Expense(kAgreementRow, ExpenseSource.ServiceUpkeep, 500f, 0f, TargetGroup.None, MoneyBasis.Intensity),
                },
                Groups = new GroupStake[0],
            },
            // Fiscal rule: cheaper loans, but every new spending benefit passes a budget check.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kFiscalRule,
                Icon = "Media/Game/Policies/BikeBanOnSidewalks.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Generic,
                Reform = ReformKind.FiscalRule,
                IsSlider = false,
                Series = new[] { new SeriesWeight(ServiceSeries.GeneralAdministration, 1f) },
                ClearancePoints = 1,
                Staff = new[] { new StaffRequirement(ServiceSeries.GeneralAdministration, 1f, 0f) },
                Target = TargetGroup.None,
                ToggleCost = 0.3f,
                BaseLagDays = 1f,
                Outputs = new[]
                {
                    // Balance rules improved budgets in Europe (허경선 2022); lenders price the commitment.
                    PolicyOutput.CityModifier(CityModifierType.LoanInterest, -0.15f),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0.1f, 0f) },
            },
            // Contracting out: a little cheaper upkeep, quality cut where it is hard to write into a contract, and hard
            // to undo once in-house capacity is gone (Hart, Shleifer & Vishny 1997; Warner & Hefetz 2012).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kContractingOut,
                // §14.8: savings come from competition, not ownership; open bidding keeps it (Bel, Fageda & Warner 2010).
                AmplifiedBy = PolicyCatalog.kCompetitiveBidding,
                AmplifyScale = 0.5f,
                AmplifyOnly = CityModifierType.CityServiceBuildingBaseUpkeepCost,
                Icon = "Media/Game/Policies/OutOfService.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                Reform = ReformKind.ContractingOut,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 30f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[] { new SeriesWeight(ServiceSeries.GeneralAdministration, 1f) },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.GeneralAdministration, 2f, 0f) },
                Target = TargetGroup.None,
                ChangeBand = 0.1f,
                ChangeCost = 2f,
                // Taking services back in means rebuilding staff and equipment.
                CutCostScale = 3f,
                BaseLagDays = 1f,
                Outputs = new[]
                {
                    // No systematic savings across studies (Bel, Fageda & Warner 2010); mixed in Korea (§14.11).
                    PolicyOutput.CityModifier(CityModifierType.CityServiceBuildingBaseUpkeepCost, -0.05f),
                    // Quality that is hard to contract for slips (hospitals stand in for such services).
                    PolicyOutput.CityModifier(CityModifierType.HospitalEfficiency, -0.03f),
                    PolicyOutput.Expense(kContractingRow, ExpenseSource.ServiceUpkeep, 200f, 0f, TargetGroup.None, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0.1f, 0f),
                    new GroupStake(TargetGroup.CommercialCompanies, 0f, 0.2f),
                },
            },
            // §14.12 economy and labour. Industry cluster support: a larger specialization bonus for industries the city
            // already concentrates in; incumbent plants near a large new plant gained 12% TFP (Greenstone, Hornbeck & Moretti 2010).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kIndustryCluster,
                Icon = "Media/Game/Policies/LogisticUpgrade.svg",
                Instrument = Instrument.Treasure,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[] { new SeriesWeight(ServiceSeries.GeneralAdministration, 1f) },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.GeneralAdministration, 2f, 1f / 100f) },
                Target = TargetGroup.IndustrialCompanies,
                ChangeBand = 0.2f,
                ChangeCost = 1.5f,
                BaseLagDays = 1f,
                Outputs = new[]
                {
                    // Vanilla ceiling 0.15; at full support +0.12 (the 12% TFP gain).
                    PolicyOutput.Parameter(ParameterLevers.kSpecializationBonus, 0.8f),
                    PolicyOutput.Expense(kClusterRow, ExpenseSource.SubsidyIndustrial, 500f, 10f, TargetGroup.IndustrialCompanies, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.IndustrialCompanies, 0f, 0.4f),
                },
            },
            // Corporate rescue: companies in debt survive longer, but zombies hold back healthy firms and new entry
            // (Caballero, Hoshi & Kashyap 2008; Adalet McGowan, Andrews & Millot 2018).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kCorporateRescue,
                Icon = "Media/Game/Policies/BigBusinessBenefactor.svg",
                Instrument = Instrument.Treasure,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 30f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[] { new SeriesWeight(ServiceSeries.GeneralAdministration, 1f) },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.GeneralAdministration, 2f, 0f) },
                Target = TargetGroup.IndustrialCompanies,
                ChangeBand = 0.2f,
                ChangeCost = 1.5f,
                BaseLagDays = 12f / 24f,
                Outputs = new[]
                {
                    // The debt a company may carry before it goes bankrupt doubles at full support.
                    PolicyOutput.Parameter(ParameterLevers.kBankruptcyLimit, 1f),
                    // The drag on healthy firms is per industry, from its share of firms kept alive (BuildingEffectSystem).
                    PolicyOutput.Parameter(ParameterLevers.kIndustrialBaseDemand, -0.05f),
                    PolicyOutput.Expense(kRescueRow, ExpenseSource.SubsidyIndustrial, 300f, 2f, TargetGroup.IndustrialCompanies, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.IndustrialCompanies, 0f, 0.3f),
                    new GroupStake(TargetGroup.CommercialCompanies, 0f, 0.3f),
                },
            },
            // Regional employment pact: more workers may commute in from the region (Monte, Redding & Rossi-Hansberg 2018).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kRegionalJobPact,
                Icon = "Media/Game/Policies/HighSpeedHighways.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[] { new SeriesWeight(ServiceSeries.GeneralAdministration, 1f) },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.GeneralAdministration, 1f, 0f) },
                Target = TargetGroup.None,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 1f,
                Outputs = new[]
                {
                    // Vanilla lets commuters fill up to 1 in 8 jobs; at full pact about 1 in 5.
                    PolicyOutput.Parameter(ParameterLevers.kCommuterRatioLimit, -0.4f),
                    PolicyOutput.Expense(kJobPactRow, ExpenseSource.ServiceUpkeep, 300f, 0f, TargetGroup.None, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.IndustrialCompanies, 0f, 0.2f),
                    new GroupStake(TargetGroup.CommercialCompanies, 0f, 0.2f),
                    new GroupStake(TargetGroup.OfficeCompanies, 0f, 0.2f),
                },
            },
            // Longer unemployment benefits. Vanilla pays them from nowhere, so the city pays the extra days here.
            // Abroad, 20 → 30 weeks cut job finding by 5–9% (Card, Chetty & Weber 2007); in Korea short benefits led to
            // quicker but shorter jobs (실업급여 지급기간의 일자리 매칭 효과).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kUnemploymentDuration,
                Icon = "Media/Game/Policies/SchoolsOut.svg",
                Instrument = Instrument.Treasure,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 5f),
                SliderDefault = 2f,
                SliderStep = 1f,
                SliderUnit = PolicySliderUnit.integer,
                Series = new[] { new SeriesWeight(ServiceSeries.GeneralAdministration, 1f) },
                ClearancePoints = 1,
                Staff = new[] { new StaffRequirement(ServiceSeries.GeneralAdministration, 1f, 1f / 20000f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 4f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.Parameter(ParameterLevers.kUnemploymentDays, 1f),
                    PolicyOutput.Expense(kUnemploymentRow, ExpenseSource.SubsidyResidential, 100f, 0.03f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0f, 0.4f),
                    new GroupStake(TargetGroup.CommercialCompanies, 0.1f, 0f),
                },
            },
            // Resource quotas on fishing and logging: slower depletion, less output now. Catch-share fisheries
            // collapsed half as often (Costello, Gaines & Lynham 2008).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kResourceQuota,
                Icon = "Media/Game/Policies/Recycling.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 60f),
                SliderDefault = 20f,
                SliderStep = 5f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.Environment, 0.6f),
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.4f),
                },
                ClearancePoints = 2,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.Environment, 2f, 1f / 50f),
                },
                Target = TargetGroup.IndustrialCompanies,
                ChangeBand = 0.15f,
                ChangeCost = 2f,
                BaseLagDays = 8f / 24f,
                Outputs = new[]
                {
                    // The cut equals the slider (60% at most), for every natural resource vanilla depletes.
                    PolicyOutput.Parameter(ParameterLevers.kFishConsumption, -0.6f),
                    PolicyOutput.Parameter(ParameterLevers.kForestConsumption, -0.6f),
                    PolicyOutput.Parameter(ParameterLevers.kFertilityConsumption, -0.6f),
                    PolicyOutput.Parameter(ParameterLevers.kOreDepletion, -0.6f),
                    PolicyOutput.Parameter(ParameterLevers.kOilDepletion, -0.6f),
                    // Output falls less than the cut: kept stock stays productive (estimate).
                    PolicyOutput.Parameter(ParameterLevers.kExtractorEfficiency, -0.2f),
                    PolicyOutput.Expense(kQuotaRow, ExpenseSource.ServiceUpkeep, 300f, 5f, TargetGroup.IndustrialCompanies, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.IndustrialCompanies, 0.6f, 0f),
                    new GroupStake(TargetGroup.Households, 0f, 0.1f),
                },
            },
            // §14.13 housing and environment. Rent cap: rents fall and tenants stay, but landlords pull units off the
            // market and put off upgrades (Diamond, McQuade & Qian 2019: rental supply -15%, citywide rents +5.1%).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kRentCap,
                Icon = "Media/Game/Policies/GatedCommunity.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 30f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[] { new SeriesWeight(ServiceSeries.GeneralAdministration, 1f) },
                ClearancePoints = 3,
                Staff = new[] { new StaffRequirement(ServiceSeries.GeneralAdministration, 3f, 1f / 20000f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.15f,
                ChangeCost = 2f,
                BaseLagDays = 8f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.Parameter(ParameterLevers.kResidentialLandValue, -0.3f),
                    // Fewer new rental homes (new construction falls in 11 of 16 studies, Kholodilin 2024) and fewer moves (tenants 20% more likely to stay, Diamond, McQuade & Qian 2019).
                    PolicyOutput.Parameter(ParameterLevers.kFreeResidential, -0.3f),
                    PolicyOutput.Parameter(ParameterLevers.kLookForHome, 0.25f),
                    PolicyOutput.CityModifier(CityModifierType.BuildingLevelingCost, 0.3f),
                    PolicyOutput.Expense(kRentCapRow, ExpenseSource.ServiceUpkeep, 200f, 0f, TargetGroup.None, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    // Tenants gain, landlords lose: both live among the households (§10.4, both sides concentrated).
                    new GroupStake(TargetGroup.Households, 0.3f, 0.4f),
                },
            },
            // Noise control for buildings and roads (WHO 2018 guidelines: road traffic below 53 dB Lden).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kNoiseControl,
                Icon = "Media/Game/Policies/SpeedBumps.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.Environment, 0.6f),
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.4f),
                },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.Environment, 2f, 1f / 100f) },
                Target = TargetGroup.IndustrialCompanies,
                ChangeBand = 0.2f,
                ChangeCost = 1.5f,
                BaseLagDays = 8f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.Parameter(ParameterLevers.kBuildingNoise, -0.3f),
                    PolicyOutput.Parameter(ParameterLevers.kRoadNoise, -0.3f),
                    PolicyOutput.CityModifier(CityModifierType.IndustrialEfficiency, -0.01f),
                    PolicyOutput.Expense(kNoiseRow, ExpenseSource.ServiceUpkeep, 300f, 2f, TargetGroup.IndustrialCompanies, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0f, 0.3f),
                    new GroupStake(TargetGroup.IndustrialCompanies, 0.2f, 0f),
                    new GroupStake(TargetGroup.CommercialCompanies, 0.1f, 0f),
                },
            },
            // Vehicle emission standard: road air pollution down; low emission zones cut NO2 by a few percent at most
            // (Morfeld et al. 2014), fleet standards more over time.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kVehicleEmissions,
                Icon = "Media/Game/Policies/CombustionEngineBan.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.Environment, 0.6f),
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.4f),
                },
                ClearancePoints = 2,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.Environment, 2f, 0f),
                    new StaffRequirement(ServiceSeries.GeneralAdministration, 1f, 0f),
                },
                Target = TargetGroup.Households,
                ChangeBand = 0.15f,
                ChangeCost = 2f,
                BaseLagDays = 1f,
                Outputs = new[]
                {
                    PolicyOutput.Parameter(ParameterLevers.kRoadAir, -0.25f),
                    PolicyOutput.Expense(kVehicleRow, ExpenseSource.ServiceUpkeep, 300f, 0f, TargetGroup.None, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0.3f, 0.2f),
                    new GroupStake(TargetGroup.CommercialCompanies, 0.1f, 0f),
                },
            },
            // Drinking water standard: pumps stop sending water above a stricter pollution limit. Contamination hurts
            // infants of less educated mothers most (Currie et al. 2013).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kDrinkingWater,
                Icon = "Media/Game/Policies/WaterUseAwareness.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.EngineeringUtilities, 0.7f),
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.3f),
                },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.EngineeringUtilities, 2f, 1f / 20000f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1.5f,
                BaseLagDays = 6f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.Parameter(ParameterLevers.kWaterTolerance, -0.5f),
                    PolicyOutput.Expense(kWaterRow, ExpenseSource.ServiceUpkeep, 500f, 0.02f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0f, 0.3f) },
            },
            // Groundwater protection: each unit pumped draws down the aquifer less (paid pumping cut irrigation by
            // 24% in a Gujarat trial; a 10% price rise cut California pumping by about 3.6%).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kGroundwaterProtection,
                Icon = "Media/Game/Policies/Recycling.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.EngineeringUtilities, 0.6f),
                    new SeriesWeight(ServiceSeries.Environment, 0.4f),
                },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.EngineeringUtilities, 2f, 0f) },
                Target = TargetGroup.IndustrialCompanies,
                ChangeBand = 0.2f,
                ChangeCost = 1.5f,
                BaseLagDays = 12f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.Parameter(ParameterLevers.kGroundwaterUse, -0.3f),
                    PolicyOutput.CityModifier(CityModifierType.IndustrialEfficiency, -0.01f),
                    PolicyOutput.Expense(kGroundwaterRow, ExpenseSource.ServiceUpkeep, 200f, 0f, TargetGroup.None, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.IndustrialCompanies, 0.2f, 0f),
                    new GroupStake(TargetGroup.Households, 0f, 0.1f),
                },
            },
            // Park care: better kept parks draw more visitors (NYC park renovations), though activity gains are mixed
            // (Cohen et al. 2009 found none without programming).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kParkCare,
                Icon = "Media/Game/Policies/PreferParks.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[] { new SeriesWeight(ServiceSeries.FacilityMaintenance, 1f) },
                ClearancePoints = 1,
                Staff = new[] { new StaffRequirement(ServiceSeries.FacilityMaintenance, 2f, 1f / 10000f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 1f,
                Outputs = new[]
                {
                    PolicyOutput.CityModifier(CityModifierType.ParkEntertainment, 0.2f),
                    // §14.8: greening vacant lots cut shootings by 7-29% in poor areas (Branas et al. 2018); small citywide.
                    PolicyOutput.CityModifier(CityModifierType.CrimeAccumulation, -0.05f),
                    PolicyOutput.Expense(kParkRow, ExpenseSource.ServiceUpkeep, 300f, 0.02f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0f, 0.3f) },
            },
            // §14.14 traffic and safety. Speed cameras: vanilla's city safety modifier only reaches roads with highway
            // rules (streets take a district modifier). Accident chance is divided by safety, so +30% safety means 23%
            // fewer crashes; Cochrane: most studies 14–25% fewer crashes near cameras (Wilson et al. 2010), Korean
            // improvement sites 24.13% (Yoon et al. 2017).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kSpeedCameras,
                Icon = "Media/Game/Policies/HighSpeedHighways.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.Police, 0.7f),
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.3f),
                },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.Police, 2f, 0f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1.5f,
                BaseLagDays = 12f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.CityModifier(CityModifierType.HighwayTrafficSafety, 0.3f),
                    PolicyOutput.Expense(kSpeedCameraRow, ExpenseSource.ServiceUpkeep, 300f, 0f, TargetGroup.None, MoneyBasis.Intensity),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0.2f, 0.2f) },
            },
            // Disaster-resistant building code: storm and flood damage down; homes built to the Florida code took 53%
            // less damage (Simmons, Czajkowski & Done 2018), but a code reaches the stock slowly, so the full effect
            // is smaller and building upgrades cost more.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kDisasterCode,
                Icon = "Media/Game/Policies/AdvancedLightningRods.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.5f),
                    new SeriesWeight(ServiceSeries.FireAndDisaster, 0.5f),
                },
                ClearancePoints = 3,
                Staff = new[] { new StaffRequirement(ServiceSeries.GeneralAdministration, 2f, 1f / 20000f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.15f,
                ChangeCost = 2f,
                BaseLagDays = 2f,
                Outputs = new[]
                {
                    PolicyOutput.CityModifier(CityModifierType.DisasterDamageRate, -0.3f),
                    PolicyOutput.CityModifier(CityModifierType.BuildingLevelingCost, 0.1f),
                    PolicyOutput.Expense(kDisasterCodeRow, ExpenseSource.ServiceUpkeep, 200f, 0.01f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0.2f, 0.2f),
                    new GroupStake(TargetGroup.IndustrialCompanies, 0.1f, 0f),
                },
            },
            // Fire safety code (fire-resistant materials, sprinklers, fire doors): buildings hold out longer, since
            // fire damage per tick is divided by structural integrity. No study measures structural codes alone; +50% integrity
            // (a third less damage per tick) rests on indirect evidence of the same size: average burned area -29% over 25 years
            // of fire-code revisions in Japan (Kobayashi 2013) and property damage per fire -30 to -35% with sprinklers (Ahrens 2017).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kFireCode,
                Icon = "Media/Game/Policies/FireSafetyInspections.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.FireAndDisaster, 0.7f),
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.3f),
                },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.FireAndDisaster, 2f, 1f / 20000f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.15f,
                ChangeCost = 2f,
                BaseLagDays = 2f,
                Outputs = new[]
                {
                    PolicyOutput.Parameter(ParameterLevers.kStructuralIntegrity, 0.5f),
                    PolicyOutput.CityModifier(CityModifierType.BuildingLevelingCost, 0.05f),
                    PolicyOutput.Expense(kFireCodeRow, ExpenseSource.ServiceUpkeep, 300f, 0.01f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0.15f, 0.25f),
                    new GroupStake(TargetGroup.CommercialCompanies, 0.1f, 0f),
                },
            },
            // §14.15 policing and justice. Community policing: satisfaction, legitimacy and perceived disorder improve,
            // crime itself only a little (Gill et al. 2014). Foot patrols take officers in proportion to the city.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kCommunityPolicing,
                Icon = "Media/Game/Policies/CityPromotion.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[] { new SeriesWeight(ServiceSeries.Police, 1f) },
                ClearancePoints = 1,
                Staff = new[] { new StaffRequirement(ServiceSeries.Police, 3f, 1f / 5000f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 2f,
                LegitimacyShift = 0.03f,
                Outputs = new[]
                {
                    PolicyOutput.CityModifier(CityModifierType.CrimeAccumulation, -0.1f),
                    PolicyOutput.CityModifier(CityModifierType.CrimeProbability, -0.05f),
                    PolicyOutput.Expense(kCommunityPolicingRow, ExpenseSource.ServiceUpkeep, 300f, 0.02f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0f, 0.3f) },
            },
            // Zero tolerance: crime levels in buildings drop fast, but aggressive order maintenance aimed at people
            // cuts no crime (Braga, Welsh & Schnell 2015), and stops spend trust (Tyler, Fagan & Geller 2014). The cost
            // runs through legitimacy into compliance with every other rule.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kZeroTolerance,
                Icon = "Media/Game/Policies/HeavyTrafficBan.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.Police, 0.8f),
                    new SeriesWeight(ServiceSeries.Corrections, 0.2f),
                },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.Police, 3f, 1f / 10000f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1.5f,
                BaseLagDays = 6f / 24f,
                LegitimacyShift = -0.04f,
                Outputs = new[]
                {
                    PolicyOutput.CityModifier(CityModifierType.CrimeAccumulation, -0.15f),
                    PolicyOutput.Expense(kZeroToleranceRow, ExpenseSource.ServiceUpkeep, 300f, 0.02f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    // The stops fall on poorer neighbourhoods (concentrated burden), the calm is spread thin.
                    new GroupStake(TargetGroup.Households, 0.4f, 0.1f),
                },
            },
            // Harsher sentencing: longer prison terms keep offenders off the street (incapacitation); deterrence from
            // severity is small (Nagin 2013), larger only for narrow groups (three strikes: -17 to -20% among those
            // with two strikes, Helland & Tabarrok 2007). Prisons fill and cost more.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kHarsherSentencing,
                Icon = "Media/Game/Policies/HarshPrisonSentences.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                // Months added to each prison term (2026-11-10 user decision on units; preview 1-12, default 4).
                SliderRange = new Bounds1(0f, 12f),
                SliderDefault = 4f,
                SliderStep = 1f,
                SliderUnit = PolicySliderUnit.integer,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.Corrections, 0.7f),
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.3f),
                },
                ClearancePoints = 3,
                Staff = new[] { new StaffRequirement(ServiceSeries.Corrections, 2f, 1f / 50f) },
                Target = TargetGroup.Criminals,
                ChangeBand = 0.15f,
                ChangeCost = 2f,
                BaseLagDays = 1f,
                Outputs = new[]
                {
                    // A game day is a month; vanilla terms are 1-100 days (Crime.m_PrisonTimeRange, mean about 50), so
                    // 12 months is about +24%. Deterrence kept at the old rate: -3% for +50% (about 25 months).
                    PolicyOutput.CityModifierAbsolute(CityModifierType.PrisonTime, 12f),
                    PolicyOutput.CityModifier(CityModifierType.CrimeProbability, -0.0144f),
                    PolicyOutput.Expense(kSentencingRow, ExpenseSource.ServiceUpkeep, 200f, 3f, TargetGroup.Criminals, MoneyBasis.Intensity),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0.05f, 0.1f) },
            },
            // Emergency call response: crimes are noticed sooner. A 10% longer response cuts the chance of clearing a
            // crime by 4.7 points (Blanes i Vidal & Kirchmaier 2018).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kEmergencyResponse,
                ScalesWithLegitimacy = true,
                Icon = "Media/Game/Policies/OutOfService.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.Police, 0.6f),
                    new SeriesWeight(ServiceSeries.PostAndTelecom, 0.4f),
                },
                ClearancePoints = 1,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.Police, 2f, 1f / 20000f),
                    new StaffRequirement(ServiceSeries.PostAndTelecom, 1f, 0f),
                },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 1f,
                Outputs = new[]
                {
                    // §14.8: trust brings more calls and cooperation; zero tolerance spends it, community policing builds it.
                    PolicyOutput.CityModifier(CityModifierType.CrimeResponseTime, -0.3f),
                    PolicyOutput.Expense(kEmergencyResponseRow, ExpenseSource.ServiceUpkeep, 300f, 0.01f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0f, 0.2f) },
            },
            // Crime prevention through environmental design: lighting, sight lines and fewer hiding places; better
            // street lighting cut crime by 21% (Welsh & Farrington 2008). New and rebuilt buildings follow it.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kCpted,
                Icon = "Media/Game/Policies/GatedCommunity.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.5f),
                    new SeriesWeight(ServiceSeries.Police, 0.5f),
                },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.GeneralAdministration, 2f, 1f / 20000f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.15f,
                ChangeCost = 1.5f,
                BaseLagDays = 2f,
                Outputs = new[]
                {
                    PolicyOutput.CityModifier(CityModifierType.CrimeAccumulation, -0.2f),
                    PolicyOutput.CityModifier(CityModifierType.BuildingLevelingCost, 0.05f),
                    PolicyOutput.Expense(kCptedRow, ExpenseSource.ServiceUpkeep, 200f, 0.01f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0.1f, 0.25f),
                    new GroupStake(TargetGroup.CommercialCompanies, 0.1f, 0f),
                },
            },
            // §14.16 health and education. Public health campaign: mass media campaigns change the behaviour of about
            // 5–9% of people (Snyder et al. 2004); disease chance -10% at most. Works through health staff.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kHealthCampaign,
                Icon = "Media/Game/Policies/NoSmoking.svg",
                Instrument = Instrument.Nodality,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.Health, 0.7f),
                    new SeriesWeight(ServiceSeries.PostAndTelecom, 0.3f),
                },
                ClearancePoints = 1,
                Staff = new[] { new StaffRequirement(ServiceSeries.Health, 1f, 1f / 50000f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 1f,
                Outputs = new[]
                {
                    PolicyOutput.CityModifier(CityModifierType.DiseaseProbability, -0.1f),
                    PolicyOutput.Expense(kHealthCampaignRow, ExpenseSource.ServiceUpkeep, 200f, 0.01f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0f, 0.1f) },
            },
            // Emergency medical system (trauma centres, transfer network): fewer of the sick and injured die. Trauma
            // centre care: in-hospital death RR 0.80, one-year RR 0.75 (MacKenzie et al. 2006).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kEmergencyCare,
                Icon = "Media/Game/Policies/AdvancedVaccinationProgram.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.Health, 0.6f),
                    new SeriesWeight(ServiceSeries.FireAndDisaster, 0.4f),
                },
                ClearancePoints = 2,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.Health, 3f, 1f / 20000f),
                    new StaffRequirement(ServiceSeries.FireAndDisaster, 1f, 0f),
                },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1.5f,
                BaseLagDays = 2f,
                Outputs = new[]
                {
                    PolicyOutput.CityModifier(CityModifierType.RecoveryFailChange, -0.2f),
                    PolicyOutput.Expense(kEmergencyCareRow, ExpenseSource.ServiceUpkeep, 500f, 0.03f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0f, 0.2f) },
            },
            // Air quality alerts: people avoid the outdoors on bad days (smog alerts cut zoo and observatory visits,
            // Neidell 2009), so pollution harms health less but outdoor leisure falls.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kAirQualityAlert,
                Icon = "Media/Game/Policies/AdvancedPollutionManagement.svg",
                Instrument = Instrument.Nodality,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.Environment, 0.6f),
                    new SeriesWeight(ServiceSeries.PostAndTelecom, 0.4f),
                },
                ClearancePoints = 1,
                Staff = new[]
                {
                    new StaffRequirement(ServiceSeries.Environment, 1f, 0f),
                    new StaffRequirement(ServiceSeries.PostAndTelecom, 1f, 0f),
                },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 6f / 24f,
                Outputs = new[]
                {
                    PolicyOutput.CityModifier(CityModifierType.PollutionHealthAffect, -0.15f),
                    PolicyOutput.CityModifier(CityModifierType.Entertainment, -0.05f),
                    PolicyOutput.Expense(kAirAlertRow, ExpenseSource.ServiceUpkeep, 150f, 0f, TargetGroup.None, MoneyBasis.Intensity),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0f, 0.1f) },
            },
            // Public healthcare: more staff and beds in hospitals. Each extra patient per nurse raised the odds of
            // death by 7% (Aiken et al. 2002); hospital efficiency +10% at most.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kPublicHealthcare,
                Icon = "Media/Game/Policies/AdvancedVaccinationProgram.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[] { new SeriesWeight(ServiceSeries.Health, 1f) },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.Health, 3f, 1f / 10000f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1.5f,
                BaseLagDays = 2f,
                Outputs = new[]
                {
                    PolicyOutput.CityModifier(CityModifierType.HospitalEfficiency, 0.1f),
                    PolicyOutput.Expense(kPublicHealthcareRow, ExpenseSource.ServiceUpkeep, 500f, 0.03f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0f, 0.2f) },
            },
            // Scholarships: a $1,000 grant raised college attendance by 3.6 points (Dynarski 2003). If universities
            // are full, the money goes out and nothing changes, which the report shows.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kScholarships,
                Icon = "Media/Game/Policies/SchoolsOut.svg",
                Instrument = Instrument.Treasure,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                // The slider is the share of university students paid; the detail is the money each gets a month
                // (2026-11-10). The effect follows the money against ¢300 (Dynarski 2003 is per $1,000: linear, estimate).
                Second = new SecondSetting { Key = "ScholarshipAmount", Range = new Bounds1(50f, 1000f), Step = 50f, Default = 300f, Unit = "money", ScalesIntensity = true },
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.Education, 0.7f),
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.3f),
                },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.Education, 1f, 1f / 20000f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1.5f,
                BaseLagDays = 1f,
                Outputs = new[]
                {
                    PolicyOutput.CityModifier(CityModifierType.UniversityInterest, 0.3f),
                    PolicyOutput.CityModifier(CityModifierType.UniversityGraduation, 0.05f),
                    // Students paid × money each (the detail setting scales the ¢300).
                    PolicyOutput.Expense(kScholarshipRow, ExpenseSource.SubsidyResidential, 0f, 300f, TargetGroup.TertiaryStudents, MoneyBasis.Intensity),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0f, 0.3f) },
            },
            // Free high school: a lottery scholarship raised secondary completion by 26 points in Ghana (Duflo, Dupas &
            // Kremer 2021). Where nearly everyone already enrols the gain is small; vanilla 0.75 → 0.9 at most.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kFreeHighSchool,
                Icon = "Media/Game/Policies/SchoolsOut.svg",
                Instrument = Instrument.Treasure,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 100f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[] { new SeriesWeight(ServiceSeries.Education, 1f) },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.Education, 1f, 0f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1.5f,
                BaseLagDays = 1f,
                Outputs = new[]
                {
                    PolicyOutput.Parameter(ParameterLevers.kEnterHighSchool, 0.2f),
                    // §14.8: 10 points more high school graduates, 7-9% fewer arrests (Lochner & Moretti 2004).
                    PolicyOutput.CityModifier(CityModifierType.CrimeProbability, -0.03f),
                    PolicyOutput.Expense(kFreeHighSchoolRow, ExpenseSource.SubsidyResidential, 300f, 0.03f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0f, 0.3f) },
            },
            // Lifelong learning: adults and workers go back to school. Training pays off slowly, more after two to
            // three years than in the first (Card, Kluve & Weber 2018).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kLifelongLearning,
                Icon = "Media/Game/Policies/SchoolsOut.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.Education, 0.7f),
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.3f),
                },
                ClearancePoints = 1,
                Staff = new[] { new StaffRequirement(ServiceSeries.Education, 2f, 1f / 20000f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 2f,
                Outputs = new[]
                {
                    // Unemployed adults only: the game's own school search, started for them (LifelongLearningSystem).
                    PolicyOutput.Expense(kLifelongRow, ExpenseSource.ServiceUpkeep, 300f, 0.02f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0f, 0.2f) },
            },
            // §14.18 disaster early warning (user's formula, 2026-11-06): extra warning seconds = 180 × delivery × post and
            // telecom capacity, added to what vanilla's warning sirens give (EarlyDisasterWarningSystem01: up to 360 s).
            // Tornado warnings cut injuries by about 32%, and deaths for lead times up to 15 minutes (Simmons & Sutter
            // 2008); farm weather warnings cut insured damage (농장맞춤형 기상재해 조기경보서비스, DBpia).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kDisasterWarning,
                Icon = "Media/Game/Policies/AdvancedLightningRods.svg",
                Instrument = Instrument.Organization,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                // Seconds of extra warning (2026-11-10 user decision on units; preview 10-180, default 90).
                SliderRange = new Bounds1(0f, 180f),
                SliderDefault = 90f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.integer,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.FireAndDisaster, 0.7f),
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.3f),
                },
                ClearancePoints = 1,
                Staff = new[] { new StaffRequirement(ServiceSeries.FireAndDisaster, 2f, 1f / 50000f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 1f,
                ScalesWithTelecom = true,
                Outputs = new[]
                {
                    PolicyOutput.CityModifierAbsolute(CityModifierType.DisasterWarningTime, 180f),
                    PolicyOutput.Expense(kWarningRow, ExpenseSource.ServiceUpkeep, 300f, 0.01f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0f, 0.2f) },
            },
            // Building energy efficiency standard: buildings built and renovated to the code use less electricity.
            // Florida's code cut electricity use by 4% (Jacobsen & Kotchen 2013), state codes 3-5% per person
            // (Aroonruengsawat, Auffhammer & Sanstad 2012), and California's savings fell short of engineering
            // estimates (Levinson 2016). -10% at most over the whole stock; building upgrades cost 5% more.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kEnergyCode,
                Icon = "Media/Game/Policies/EnergyConsumptionAwareness.svg",
                Instrument = Instrument.Authority,
                Direction = Direction.Burden,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.EngineeringUtilities, 0.6f),
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.4f),
                },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.EngineeringUtilities, 2f, 1f / 20000f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.15f,
                ChangeCost = 1.5f,
                BaseLagDays = 2f,
                Outputs = new[]
                {
                    PolicyOutput.Parameter(ParameterLevers.kBuildingElectricity, -0.1f),
                    PolicyOutput.CityModifier(CityModifierType.BuildingLevelingCost, 0.05f),
                    PolicyOutput.Expense(kEnergyCodeRow, ExpenseSource.ServiceUpkeep, 200f, 0.01f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    // Owners pay for the insulation, tenants save on the bills.
                    new GroupStake(TargetGroup.Households, 0.15f, 0.1f),
                    new GroupStake(TargetGroup.CommercialCompanies, 0.1f, 0f),
                    new GroupStake(TargetGroup.IndustrialCompanies, 0.1f, 0f),
                },
            },
            // Volume-based waste fee: taken out of the city policies on 2026-11-10 (user decision). It moves to the district
            // policies with a new model in which households really pay per bag (docs/PLAN.md §12 "지구 정책").
            // University–industry support (§14.20-2, 2026-11-06 user decision): grants for joint research between
            // companies and universities. Companies and private universities are not public bodies, so this supports
            // rather than mandates. Supported firms' sales rose by 11.4% one to seven years later (Jensen et al.), more
            // for high-tech firms; the gain needs people to absorb the research (Cohen & Levinthal 1990), so the
            // effect is full only when 5% of the population studies at college or university.
            new PolicyDefinition
            {
                Id = PolicyCatalog.kUniversityIndustry,
                Icon = "Media/Game/Policies/HighTechHousing.svg",
                Instrument = Instrument.Treasure,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 100f),
                SliderDefault = 50f,
                SliderStep = 10f,
                SliderUnit = PolicySliderUnit.percentage,
                Series = new[]
                {
                    new SeriesWeight(ServiceSeries.Education, 0.5f),
                    new SeriesWeight(ServiceSeries.GeneralAdministration, 0.5f),
                },
                ClearancePoints = 2,
                Staff = new[] { new StaffRequirement(ServiceSeries.GeneralAdministration, 1f, 1f / 200f) },
                Target = TargetGroup.OfficeCompanies,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 2f,
                ScalesWithStudents = true,
                Outputs = new[]
                {
                    PolicyOutput.CityModifier(CityModifierType.OfficeSoftwareEfficiency, 0.1f),
                    PolicyOutput.CityModifier(CityModifierType.IndustrialElectronicsEfficiency, 0.1f),
                    PolicyOutput.Expense(kUniversityIndustryRow, ExpenseSource.SubsidyOffice, 300f, 5f, TargetGroup.OfficeCompanies, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.OfficeCompanies, 0f, 0.4f),
                    new GroupStake(TargetGroup.IndustrialCompanies, 0f, 0.2f),
                },
            },
            // Livelihood support (§14.21, 2026-11-08 user proposal): money each month for households below "modest"
            // wealth, from the city budget, poorest first. Social welfare staff handle 60 households each (50-70 judged
            // right by welfare officials, 박윤희·기정훈 2011). 100% pays the game's unemployment benefit per household.
            // The game rates wellbeing on household income, so the money is also added to income (LivelihoodSupportSystem):
            // happier households commit less crime and keep their homes. Losing such support raised income-generating
            // crime by 60% (Deshpande & Mueller-Smith 2022); cash cut homelessness (Dwyer et al. 2023); work hardly fell
            // (Jones & Marinescu 2022; 이상은 2004).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kLivelihoodSupport,
                Icon = "Media/Game/Policies/TouristInformation.svg",
                Instrument = Instrument.Treasure,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 1600f),
                SliderDefault = 400f,
                SliderStep = 50f,
                SliderUnit = PolicySliderUnit.money,
                // Who may get it: households whose income (without this support) is below this share of the city's median
                // household income (2026-11-10 user decision "안 가로 가자"). Wealth could not tell the poor apart: in the
                // test city 86% of households were under the game's "modest" line and the bottom 3% and the median were
                // close (1,047 vs 1,345), while income ran from 0 to a median of 2,720. Korea's basic livelihood
                // thresholds as shares of the standard median income (2024): livelihood 32%, medical 40%, housing 48%,
                // education 50%. At 32% about 13% of the test city's households qualify.
                Second = new SecondSetting { Key = "LivelihoodIncome", Range = new Bounds1(20f, 50f), Step = 1f, Default = 32f, Unit = "percent" },
                Series = new[] { new SeriesWeight(ServiceSeries.SocialWelfare, 1f) },
                ClearancePoints = 1,
                Staff = new[] { new StaffRequirement(ServiceSeries.SocialWelfare, 1f, 1f / LivelihoodSupportSystem.kHouseholdsPerWorker) },
                StaffPerCase = true,
                Target = TargetGroup.PoorHouseholds,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 0.5f,
                Outputs = new[]
                {
                    PolicyOutput.Expense(kLivelihoodRow, ExpenseSource.SubsidyResidential, 0f, 0f, TargetGroup.PoorHouseholds, MoneyBasis.External),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0f, 0.5f) },
            },
            // Rent support (2026-11-09, in place of public rental housing): a share of the rent (slider 0-50%) back to poor
            // renting households, poorest first, 100 households per social worker (RentSupportSystem). Supported tenants
            // move and are evicted less while it lasts (Moretti & Schmid 2022; NLIHC review; direction only).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kRentSupport,
                Icon = "Media/Game/Policies/TouristInformation.svg",
                Instrument = Instrument.Treasure,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 1000f),
                SliderDefault = 300f,
                SliderStep = 50f,
                SliderUnit = PolicySliderUnit.money,
                // Months each household may get it; then the next poorest gets it (2026-11-10; Seoul's youth rent
                // support pays up to 12 months).
                Second = new SecondSetting { Key = "RentMonths", Range = new Bounds1(1f, 24f), Step = 1f, Default = 12f, Unit = "months" },
                Series = new[] { new SeriesWeight(ServiceSeries.SocialWelfare, 1f) },
                ClearancePoints = 1,
                Staff = new[] { new StaffRequirement(ServiceSeries.SocialWelfare, 1f, 1f / RentSupportSystem.kHouseholdsPerWorker) },
                StaffPerCase = true,
                Target = TargetGroup.PoorHouseholds,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 0.5f,
                Outputs = new[]
                {
                    PolicyOutput.Expense(kRentSupportRow, ExpenseSource.SubsidyResidential, 0f, 0f, TargetGroup.PoorHouseholds, MoneyBasis.External),
                },
                Groups = new[] { new GroupStake(TargetGroup.Households, 0f, 0.4f) },
            },
            // Flexible working hours support (2026-11-09, renamed by the user: hours are the firms' call): the city pays
            // workplaces that adopt staggered starts; a share of workplaces (slider 0-50%) take it up and their workers
            // start up to 90 minutes later (FlexibleHoursSystem). Peak traffic falls by about 5-10% (NCHRP Synthesis 73;
            // Vickrey 1969 bottleneck).
            new PolicyDefinition
            {
                Id = PolicyCatalog.kFlexibleHours,
                Icon = "Media/Game/Policies/RouteVehicleCount.svg",
                Instrument = Instrument.Treasure,
                Direction = Direction.Benefit,
                Mechanism = Mechanism.Generic,
                IsSlider = true,
                SliderRange = new Bounds1(0f, 50f),
                SliderDefault = 30f,
                SliderStep = 5f,
                SliderUnit = PolicySliderUnit.percentage,
                // How much later workers may start (2026-11-10): the delays spread over 0 to this.
                Second = new SecondSetting { Key = "FlexibleDelay", Range = new Bounds1(30f, 120f), Step = 15f, Default = 90f, Unit = "minutes" },
                Series = new[] { new SeriesWeight(ServiceSeries.GeneralAdministration, 1f) },
                ClearancePoints = 1,
                Staff = new[] { new StaffRequirement(ServiceSeries.GeneralAdministration, 1f, 0f) },
                Target = TargetGroup.Households,
                ChangeBand = 0.2f,
                ChangeCost = 1f,
                BaseLagDays = 1f,
                Outputs = new[]
                {
                    PolicyOutput.Expense(kFlexibleHoursRow, ExpenseSource.SubsidyCommercial, 100f, 0.005f, TargetGroup.Households, MoneyBasis.Intensity),
                },
                Groups = new[]
                {
                    new GroupStake(TargetGroup.Households, 0f, 0.1f),
                    new GroupStake(TargetGroup.CommercialCompanies, 0.1f, 0f),
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

        // Share of the pollution tax bill (or of the allowances bought at auction) that shows up as lost industrial
        // efficiency. Small: the UK Climate Change Levy cut energy intensity by 18.1% with no significant effect on
        // employment, revenue or exit (Martin, de Preux & Wagner 2014), the EU ETS cut emissions by 14–16% without
        // shrinking activity (Colmer et al. 2025), and at the same cut a price must cost less than a uniform standard
        // (§14.9, 2026-11-05; was 0.3).
        public const float kTaxBillEfficiencyBurden = 0.1f;
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
