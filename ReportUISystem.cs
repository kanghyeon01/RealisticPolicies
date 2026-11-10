using Colossal.Entities;
using Colossal.UI.Binding;
using Game.City;
using Game.Prefabs;
using Game.Simulation;
using Game.UI;
using RealisticPolicies.Engine;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace RealisticPolicies
{
    /// <summary>
    /// Report panel data (docs/PLAN.md §10.8 stage 7): the logic model of each mod policy (input → activity → output →
    /// outcome) for the policy page, and the suits, bans and debts for the lawsuit tab, with the player's court actions.
    /// </summary>
    public sealed partial class ReportUISystem : UISystemBase
    {
        private const string kGroup = ModUISystem.kGroup;

        // The report keeps a warning for one game day after the decision that caused it.
        private const uint kWarningFrames = PolicyEngineSystem.kFramesPerDay;

        private PolicyEngineSystem m_Engine;
        private LitigationSystem m_Litigation;
        private BudgetInjectionSystem m_Budget;
        private CitySystem m_CitySystem;
        private CityConfigurationSystem m_CityConfiguration;
        private SimulationSystem m_SimulationSystem;
        private NameSystem m_NameSystem;
        private RawValueBinding m_ReportsBinding;
        private RawValueBinding m_LawsuitsBinding;
        private int m_LastStep = -1;
        private int m_LastLitigationVersion = -1;
        private int m_LastGovernment = -1;
        private string m_LastCityName;
        private int m_SettlementKey;
        private SettlementResult m_SettlementResult;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Engine = World.GetOrCreateSystemManaged<PolicyEngineSystem>();
            m_Litigation = World.GetOrCreateSystemManaged<LitigationSystem>();
            m_Budget = World.GetOrCreateSystemManaged<BudgetInjectionSystem>();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_CityConfiguration = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            AddBinding(m_ReportsBinding = new RawValueBinding(kGroup, "policyReports", WriteReports));
            AddBinding(m_LawsuitsBinding = new RawValueBinding(kGroup, "lawsuits", WriteLawsuits));
            AddBinding(new TriggerBinding<int, int>(kGroup, "respondSuit", RespondSuit));
            AddBinding(new TriggerBinding<int, float, int>(kGroup, "proposeSettlement", ProposeSettlement));
            // The detail setting (second slider) in the policy's column (2026-11-10, docs/previews/policy-second-slider.html).
            AddBinding(new TriggerBinding<string, float>(kGroup, "setPolicySecond", (id, value) => m_Engine.SetSecond(id, value)));
        }

        [Preserve]
        protected override void OnUpdate()
        {
            long start = Engine.Perf.Start();
            try
            {
                Step();
            }
            finally
            {
                Engine.Perf.Stop("ReportUI", start);
            }
        }

        private void Step()
        {
            int government = Mod.Setting != null ? (int)Mod.Setting.Government : 0;
            string cityName = m_CityConfiguration.cityName;
            bool litigationChanged = m_Litigation.Version != m_LastLitigationVersion
                || government != m_LastGovernment || cityName != m_LastCityName;
            if (m_Engine.StepCount != m_LastStep || litigationChanged)
            {
                m_LastStep = m_Engine.StepCount;
                m_ReportsBinding.Update();
            }
            if (litigationChanged)
            {
                m_LastLitigationVersion = m_Litigation.Version;
                m_LastGovernment = government;
                m_LastCityName = cityName;
                m_LawsuitsBinding.Update();
            }
        }

        // ------------------------------------------------------------------ actions

        /// <summary>Suits are addressed by year × 100000 + case number, which stays the same while others end.</summary>
        private static int Key(int year, int caseNumber) => year * 100000 + caseNumber;

        private int FindSuit(int key)
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.TryGetBuffer(city, true, out DynamicBuffer<Lawsuit> suits))
            {
                return -1;
            }
            for (int i = 0; i < suits.Length; i++)
            {
                if (Key(suits[i].m_Year, suits[i].m_CaseNumber) == key)
                {
                    return i;
                }
            }
            return -1;
        }

        private void RespondSuit(int key, int response)
        {
            int index = FindSuit(key);
            if (index >= 0 && (response == (int)SuitResponse.Lawyer || response == (int)SuitResponse.Official))
            {
                m_Litigation.Respond(index, (SuitResponse)response);
            }
        }

        /// <summary>Settlement offer with the policy's slider value (or 0/1 for a toggle) and a one-off compensation.</summary>
        private void ProposeSettlement(int key, float sliderValue, int compensation)
        {
            int index = FindSuit(key);
            if (index < 0)
            {
                return;
            }
            Lawsuit suit = EntityManager.GetBuffer<Lawsuit>(m_CitySystem.City, true)[index];
            PolicyDefinition definition = LitigationSystem.Definition(suit.m_Policy);
            if (definition == null)
            {
                return;
            }
            m_Litigation.ProposeSettlement(index, ToIntensity(definition, sliderValue), math.max(0, compensation));
            m_SettlementKey = key;
            m_SettlementResult = m_Litigation.LastSettlementResult;
            m_LawsuitsBinding.Update();
        }

        private static float SliderValue(PolicyDefinition definition, float intensity) =>
            definition.IsSlider ? definition.SliderRange.min + intensity * (definition.SliderRange.max - definition.SliderRange.min) : intensity;

        private static float ToIntensity(PolicyDefinition definition, float value)
        {
            if (!definition.IsSlider)
            {
                return math.saturate(value);
            }
            float span = definition.SliderRange.max - definition.SliderRange.min;
            return span > 0f ? math.saturate((value - definition.SliderRange.min) / span) : 0f;
        }

        // ------------------------------------------------------------------ policy reports

        private void WriteReports(IJsonWriter writer)
        {
            PolicyDefinition[] all = PolicyDefinitions.All;
            Entity city = m_CitySystem.City;
            bool ready = city != Entity.Null && m_Engine.StepCount > 0 && EntityManager.HasBuffer<PolicyRuntime>(city);
            writer.ArrayBegin(ready ? all.Length : 0);
            if (!ready)
            {
                writer.ArrayEnd();
                return;
            }
            DynamicBuffer<PolicyRuntime> runtimes = EntityManager.GetBuffer<PolicyRuntime>(city, true);
            uint frame = m_SimulationSystem.frameIndex;
            PolicyDefinition standard = PolicyCatalog.Definition(PolicyCatalog.kEmissionStandard);
            int standardIndex = System.Array.IndexOf(all, standard);
            for (int i = 0; i < all.Length; i++)
            {
                PolicyDefinition definition = all[i];
                PolicyStepResult result = m_Engine.LastResults[i];
                PolicyRuntime runtime = default;
                for (int r = 0; r < runtimes.Length; r++)
                {
                    if (runtimes[r].m_Policy == definition.Prefab)
                    {
                        runtime = runtimes[r];
                    }
                }
                writer.TypeBegin("RealisticPolicies.PolicyReport");
                writer.PropertyName("id");
                writer.Write(definition.Id);
                writer.PropertyName("on");
                writer.Write(result.Intensity > 0f);
                writer.PropertyName("mechanism");
                writer.Write(definition.Mechanism.ToString());
                writer.PropertyName("authority");
                writer.Write(definition.Instrument == Instrument.Authority);
                writer.PropertyName("suspended");
                writer.Write(m_Litigation.IsSuspended(definition.Prefab));
                writer.PropertyName("second");
                if (definition.Second == null)
                {
                    writer.WriteNull();
                }
                else
                {
                    SecondSetting second = definition.Second;
                    writer.TypeBegin("RealisticPolicies.SecondSetting");
                    writer.PropertyName("key");
                    writer.Write(second.Key);
                    writer.PropertyName("value");
                    writer.Write(runtime.m_Policy != Entity.Null && runtime.m_Second >= 0f ? runtime.m_Second : second.Default);
                    writer.PropertyName("min");
                    writer.Write(second.Range.min);
                    writer.PropertyName("max");
                    writer.Write(second.Range.max);
                    writer.PropertyName("step");
                    writer.Write(second.Step);
                    writer.PropertyName("unit");
                    writer.Write(second.Unit);
                    writer.TypeEnd();
                }

                // Input: staff per series and the policy's money rows.
                writer.PropertyName("staff");
                writer.ArrayBegin(definition.Staff.Length);
                foreach (StaffRequirement staff in definition.Staff)
                {
                    writer.TypeBegin("RealisticPolicies.StaffRow");
                    writer.PropertyName("series");
                    writer.Write(staff.Series.ToString());
                    writer.PropertyName("need");
                    writer.Write(m_Engine.PolicyStaff(i, staff.Series));
                    writer.PropertyName("workers");
                    writer.Write(m_Engine.Census.Workers(staff.Series));
                    writer.TypeEnd();
                }
                writer.ArrayEnd();
                int moneyRows = 0;
                foreach (PolicyOutput output in definition.Outputs)
                {
                    moneyRows += output.Kind == OutputKind.Income || output.Kind == OutputKind.Expense ? 1 : 0;
                }
                writer.PropertyName("money");
                writer.ArrayBegin(moneyRows);
                foreach (PolicyOutput output in definition.Outputs)
                {
                    if (output.Kind != OutputKind.Income && output.Kind != OutputKind.Expense)
                    {
                        continue;
                    }
                    writer.TypeBegin("RealisticPolicies.MoneyRow");
                    writer.PropertyName("row");
                    writer.Write(output.Key);
                    writer.PropertyName("income");
                    writer.Write(output.Kind == OutputKind.Income);
                    writer.PropertyName("monthly");
                    writer.Write(m_Budget.GetRow(output.Key));
                    writer.TypeEnd();
                }
                writer.ArrayEnd();

                // Activity.
                writer.PropertyName("effectiveness");
                writer.Write(result.Effectiveness);
                writer.PropertyName("staffShare");
                writer.Write(result.StaffShare);
                writer.PropertyName("busiest");
                writer.Write(result.BusiestSeries == ServiceSeries.None ? "" : result.BusiestSeries.ToString());
                writer.PropertyName("lagDays");
                writer.Write(result.LagDays);
                writer.PropertyName("capture");
                writer.Write(result.Capture);

                // Output.
                writer.PropertyName("detection");
                writer.Write(result.Detection);
                writer.PropertyName("compliance");
                writer.Write(result.Compliance);
                // §14.11 performance management: other policies' reports read higher than they deliver (gaming).
                float inflation = result.Intensity > 0f && definition.Reform != Engine.ReformKind.PerformanceManagement
                    ? m_Engine.Reforms.ReportInflation
                    : 0f;
                writer.PropertyName("progress");
                writer.Write(result.Intensity > 0f ? math.saturate(result.Progress / math.max(1e-4f, result.Intensity) + inflation) : 0f);
                writer.PropertyName("inflated");
                writer.Write(inflation > 0.005f);
                // City conditions scaling the effect (2026-11-10): the report says only whether they raise or lower it.
                writer.PropertyName("condition");
                writer.Write(m_Engine.ConditionFactor(definition));
                float price = result.Progress * FirmModel.kBasePrice;
                writer.PropertyName("abatement");
                writer.Write(definition.Mechanism == Mechanism.EmissionTax || definition.Mechanism == Mechanism.AbatementSubsidy ? FirmModel.Abatement(price) : 0f);
                writer.PropertyName("effectiveRate");
                writer.Write(definition.Mechanism == Mechanism.EmissionTax ? SliderValue(definition, result.Intensity) * result.Compliance : 0f);
                // Monitoring lifts the emission standard's detection; disclosure lifts voluntary compliance.
                float baseDetection = 0f;
                float watchedDetection = 0f;
                float voluntaryGain = 0f;
                if (standardIndex >= 0)
                {
                    PolicyStepResult standardResult = m_Engine.LastResults[standardIndex];
                    float exposure = EngineParameters.kDetection * standardResult.Effectiveness * standard.InspectionEase;
                    baseDetection = 1f - math.exp(-exposure * EngineParameters.kBaseInspection);
                    watchedDetection = standardResult.Detection;
                    voluntaryGain = EngineParameters.kDisclosureVoluntaryGain * m_Engine.LastDisclosure
                        * math.min(1f, standardResult.Detection / EngineParameters.kDisclosureReferenceDetection);
                }
                // Emission trading market (§10.6), the same for both trading policies.
                writer.PropertyName("intensity");
                writer.Write(result.Intensity);
                writer.PropertyName("tradingPrice");
                writer.Write(m_Engine.TradingPrice / FirmModel.kBasePrice);
                writer.PropertyName("tradingOther");
                writer.Write(m_Engine.TradingOtherCut);
                writer.PropertyName("tradingCut");
                writer.Write(m_Engine.TradingCut);
                writer.PropertyName("detectionBefore");
                writer.Write(baseDetection);
                writer.PropertyName("detectionAfter");
                writer.Write(watchedDetection);
                writer.PropertyName("voluntaryGain");
                writer.Write(voluntaryGain);

                // Outcome: the modifiers as written now, parameter levers, and the city's air since the policy started.
                int modifierCount = 0;
                foreach (PolicyOutput output in definition.Outputs)
                {
                    modifierCount += output.Kind == OutputKind.CityModifier ? 1 : 0;
                }
                writer.PropertyName("modifiers");
                writer.ArrayBegin(modifierCount);
                foreach (PolicyOutput output in definition.Outputs)
                {
                    if (output.Kind != OutputKind.CityModifier)
                    {
                        continue;
                    }
                    writer.TypeBegin("RealisticPolicies.ModifierRow");
                    writer.PropertyName("type");
                    writer.Write(output.Modifier.ToString());
                    writer.PropertyName("value");
                    writer.Write(ModifierValue(definition.Prefab, output.Modifier));
                    writer.PropertyName("absolute");
                    writer.Write(output.Absolute);
                    writer.TypeEnd();
                }
                writer.ArrayEnd();
                int leverCount = 0;
                foreach (PolicyOutput output in definition.Outputs)
                {
                    leverCount += output.Kind == OutputKind.Parameter ? 1 : 0;
                }
                writer.PropertyName("levers");
                writer.ArrayBegin(leverCount);
                foreach (PolicyOutput output in definition.Outputs)
                {
                    if (output.Kind != OutputKind.Parameter)
                    {
                        continue;
                    }
                    writer.TypeBegin("RealisticPolicies.LeverRow");
                    writer.PropertyName("key");
                    writer.Write(output.Key);
                    writer.PropertyName("value");
                    writer.Write(output.FullEffect * result.Progress * PolicyEngineSystem.Fading(definition, runtime.m_AgeDays) * m_Engine.LeverAmplification(i));
                    writer.TypeEnd();
                }
                writer.ArrayEnd();
                // Reforms (§14.10 option A): what this reform does to the administrative layer, as outcome rows.
                System.Collections.Generic.List<Engine.ReformRow> reformRows = definition.Reform != Engine.ReformKind.None
                    ? m_Engine.Reforms.Rows(definition)
                    : null;
                if (definition.LegitimacyShift != 0f)
                {
                    // §14.15: policing that builds or spends trust shows it like a reform.
                    if (reformRows == null)
                    {
                        reformRows = new System.Collections.Generic.List<Engine.ReformRow>();
                    }
                    reformRows.Add(new Engine.ReformRow("Legitimacy", definition.LegitimacyShift * math.saturate(result.Progress),
                        Engine.ReformUnit.Points, definition.LegitimacyShift > 0f));
                }
                writer.PropertyName("reforms");
                writer.ArrayBegin(reformRows?.Count ?? 0);
                if (reformRows != null)
                {
                    foreach (Engine.ReformRow row in reformRows)
                    {
                        writer.TypeBegin("RealisticPolicies.ReformRow");
                        writer.PropertyName("key");
                        writer.Write(row.Key);
                        writer.PropertyName("value");
                        writer.Write(row.Value);
                        writer.PropertyName("unit");
                        writer.Write((int)row.Unit);
                        writer.PropertyName("good");
                        writer.Write(row.Good);
                        writer.TypeEnd();
                    }
                }
                writer.ArrayEnd();
                bool air = result.Intensity > 0f && runtime.m_HasBaseline && runtime.m_BaselineAir > 0f;
                writer.PropertyName("airChange");
                writer.Write(air ? m_Engine.AirPollution / runtime.m_BaselineAir - 1f : 0f);
                writer.PropertyName("hasAir");
                writer.Write(air);
                writer.PropertyName("fading");
                writer.Write(definition.EffectHalfLifeDays > 0f ? PolicyEngineSystem.Fading(definition, runtime.m_AgeDays) : -1f);

                uint warning = m_Engine.WarningFrame(i);
                writer.PropertyName("warning");
                writer.Write(warning != 0 && frame - warning < kWarningFrames);
                writer.PropertyName("noWelfare");
                writer.Write(definition.NeedsWelfareCoverage && m_Engine.Census.WelfareOffices == 0);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
        }

        private float ModifierValue(Entity prefab, CityModifierType type)
        {
            if (!EntityManager.TryGetBuffer(prefab, true, out DynamicBuffer<CityModifierData> modifiers))
            {
                return 0f;
            }
            for (int m = 0; m < modifiers.Length; m++)
            {
                if (modifiers[m].m_Type == type)
                {
                    return modifiers[m].m_Range.min;
                }
            }
            return 0f;
        }

        // ------------------------------------------------------------------ lawsuits

        private void WriteLawsuits(IJsonWriter writer)
        {
            Entity city = m_CitySystem.City;
            writer.TypeBegin("RealisticPolicies.Lawsuits");
            writer.PropertyName("cityName");
            writer.Write(m_CityConfiguration.cityName ?? "");
            writer.PropertyName("government");
            writer.Write(Mod.Setting != null ? (int)Mod.Setting.Government : 0);
            writer.PropertyName("maxOffers");
            writer.Write(LitigationSystem.kMaxSettlementOffers);
            writer.PropertyName("settlementKey");
            writer.Write(m_SettlementKey);
            writer.PropertyName("settlementResult");
            writer.Write(m_SettlementResult.ToString());

            bool hasCity = city != Entity.Null && EntityManager.HasBuffer<Lawsuit>(city);
            writer.PropertyName("suits");
            if (!hasCity)
            {
                writer.ArrayBegin(0);
                writer.ArrayEnd();
            }
            else
            {
                DynamicBuffer<Lawsuit> suits = EntityManager.GetBuffer<Lawsuit>(city, true);
                writer.ArrayBegin(suits.Length);
                for (int i = 0; i < suits.Length; i++)
                {
                    WriteSuit(writer, suits[i], i);
                }
                writer.ArrayEnd();
            }

            writer.PropertyName("bans");
            if (!hasCity || !EntityManager.TryGetBuffer(city, true, out DynamicBuffer<PolicyBan> bans))
            {
                writer.ArrayBegin(0);
                writer.ArrayEnd();
            }
            else
            {
                writer.ArrayBegin(bans.Length);
                for (int i = 0; i < bans.Length; i++)
                {
                    PolicyDefinition definition = LitigationSystem.Definition(bans[i].m_Policy);
                    writer.TypeBegin("RealisticPolicies.Ban");
                    writer.PropertyName("policyId");
                    writer.Write(definition != null ? definition.Id : "");
                    writer.PropertyName("year");
                    writer.Write(bans[i].m_Year);
                    writer.PropertyName("number");
                    writer.Write(bans[i].m_CaseNumber);
                    writer.PropertyName("partial");
                    writer.Write(bans[i].m_Partial);
                    writer.PropertyName("daysLeft");
                    writer.Write(m_Litigation.BanDaysLeft(bans[i]));
                    writer.PropertyName("toggle");
                    writer.Write(definition != null && !definition.IsSlider);
                    writer.TypeEnd();
                }
                writer.ArrayEnd();
            }

            writer.PropertyName("debts");
            if (!hasCity || !EntityManager.TryGetBuffer(city, true, out DynamicBuffer<CourtDebt> debts))
            {
                writer.ArrayBegin(0);
                writer.ArrayEnd();
            }
            else
            {
                writer.ArrayBegin(debts.Length);
                for (int i = 0; i < debts.Length; i++)
                {
                    writer.TypeBegin("RealisticPolicies.Debt");
                    writer.PropertyName("year");
                    writer.Write(debts[i].m_Year);
                    writer.PropertyName("number");
                    writer.Write(debts[i].m_CaseNumber);
                    writer.PropertyName("remaining");
                    writer.Write((int)math.round(debts[i].m_Remaining + debts[i].m_Arrears));
                    writer.PropertyName("arrears");
                    writer.Write((int)math.round(debts[i].m_Arrears));
                    writer.PropertyName("dailyCap");
                    writer.Write((int)math.round(debts[i].m_DailyCap));
                    writer.TypeEnd();
                }
                writer.ArrayEnd();
            }
            writer.TypeEnd();
        }

        private void WriteSuit(IJsonWriter writer, Lawsuit suit, int index)
        {
            PolicyDefinition definition = LitigationSystem.Definition(suit.m_Policy);
            writer.TypeBegin("RealisticPolicies.Suit");
            writer.PropertyName("key");
            writer.Write(Key(suit.m_Year, suit.m_CaseNumber));
            writer.PropertyName("year");
            writer.Write(suit.m_Year);
            writer.PropertyName("number");
            writer.Write(suit.m_CaseNumber);
            writer.PropertyName("policyId");
            writer.Write(definition != null ? definition.Id : "");
            writer.PropertyName("slider");
            writer.Write(definition != null && definition.IsSlider);
            writer.PropertyName("againstCut");
            writer.Write(suit.m_AgainstCut);
            writer.PropertyName("contested");
            writer.Write(definition != null ? SliderValue(definition, suit.m_ContestedIntensity) : 0f);
            writer.PropertyName("prior");
            writer.Write(definition != null ? SliderValue(definition, suit.m_PriorIntensity) : 0f);
            writer.PropertyName("group");
            writer.Write(((TargetGroup)suit.m_Group).ToString());
            writer.PropertyName("plaintiffs");
            writer.Write(suit.m_Plaintiffs);

            // The representative: a resident of the household, or the company itself (as in the chirp).
            Entity representative = suit.m_Representative;
            if (suit.m_Group == (byte)TargetGroup.Households && EntityManager.TryGetBuffer(representative, true, out DynamicBuffer<Game.Citizens.HouseholdCitizen> citizens)
                && citizens.Length > 0)
            {
                representative = citizens[0].m_Citizen;
            }
            writer.PropertyName("representative");
            if (representative != Entity.Null && EntityManager.Exists(representative))
            {
                m_NameSystem.BindName(writer, representative);
            }
            else
            {
                writer.WriteNull();
            }

            writer.PropertyName("remainingHours");
            writer.Write(m_Litigation.RemainingHours(suit));
            writer.PropertyName("progress");
            writer.Write(m_Litigation.Progress(suit));
            writer.PropertyName("hint");
            writer.Write(m_Litigation.WinHint(index));
            writer.PropertyName("response");
            writer.Write((int)suit.m_Response);
            writer.PropertyName("lawyerFee");
            writer.Write((int)math.round(m_Litigation.LawyerFeeNow(suit)));
            writer.PropertyName("late");
            writer.Write(m_Litigation.IsLate(suit));
            writer.PropertyName("officials");
            writer.Write(m_Litigation.OfficialsNeeded(suit));
            writer.PropertyName("injunction");
            writer.Write((int)suit.m_Injunction);
            writer.PropertyName("injunctionHours");
            writer.Write(m_Litigation.InjunctionHours(suit));
            writer.PropertyName("decidedUnanswered");
            writer.Write(suit.m_DecidedUnanswered);
            writer.PropertyName("claim");
            writer.Write((int)math.round(suit.m_Claim));
            writer.PropertyName("grounds");
            writer.Write((int)suit.m_Grounds);
            writer.PropertyName("actYear");
            writer.Write(suit.m_ActMonth / 12);
            writer.PropertyName("actMonth");
            writer.Write(suit.m_ActMonth % 12 + 1);
            writer.PropertyName("offers");
            writer.Write((int)suit.m_Offers);
            writer.PropertyName("waitHours");
            writer.Write(m_Litigation.SettlementWaitHours(suit));
            writer.TypeEnd();
        }

        [Preserve]
        public ReportUISystem()
        {
        }
    }
}
