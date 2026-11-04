using System.Collections.Generic;
using Colossal;

namespace RealisticPolicies
{
    public abstract class LocaleBase : IDictionarySource
    {
        protected abstract Dictionary<string, string> Entries();

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            Dictionary<string, string> entries = Entries();
            // Indexed texts ("id:0", "id:1", ...) tell the game how many variants a random text (chirps) has.
            foreach (string key in entries.Keys)
            {
                int colon = key.LastIndexOf(':');
                if (colon > 0 && int.TryParse(key.Substring(colon + 1), out int index))
                {
                    string id = key.Substring(0, colon);
                    indexCounts.TryGetValue(id, out int count);
                    indexCounts[id] = System.Math.Max(count, index + 1);
                }
            }
            return entries;
        }

        public void Unload()
        {
        }

        protected static string PolicyTitle(string id) => $"Policy.TITLE[{id}]";

        protected static string PolicyDescription(string id) => $"Policy.DESCRIPTION[{id}]";

        protected static string BudgetItem(string id) => $"EconomyPanel.BUDGET_ITEM[{id}]";

        protected static string Chirp(string policyId, bool companies) => Engine.CourtPrefabs.ChirpId(policyId, companies) + ":0";

        protected static string NotificationTitle(string id) => $"Notifications.TITLE[{id}]";

        protected static string NotificationDescription(string id) => $"Notifications.DESCRIPTION[{id}]";
    }

    public sealed class LocaleEN : LocaleBase
    {
        protected override Dictionary<string, string> Entries() => new Dictionary<string, string>
        {
            { PolicyTitle(PolicyCatalog.kEquipmentMandate), "Pollution Control Equipment Mandate" },
            {
                PolicyDescription(PolicyCatalog.kEquipmentMandate),
                "Every industrial company must install filters. Easy to inspect, but every company pays the same cost.\n\n" +
                "- Air and ground pollution -25%\n- Garbage accumulation +15%\n- Industrial efficiency -3%"
            },
            { PolicyTitle(PolicyCatalog.kEmissionStandard), "Emission Standard" },
            { PolicyDescription(PolicyCatalog.kEmissionStandard), "Sets the share of industrial pollution that companies must cut. Companies choose how to cut it." },
            { BudgetItem(BudgetInjectionSystem.kIncomeRowId), "Pollution tax" },
            { BudgetItem(BudgetInjectionSystem.kExpenseRowId), "Green transition subsidy" },
            { BudgetItem(Engine.PolicyDefinitions.kEmissionStandardEnforcementRow), "Emission standard inspections" },
            { BudgetItem(Engine.PolicyDefinitions.kMonitoringRow), "Environmental monitoring staff" },
            { BudgetItem(Engine.LitigationSystem.kLawyerFeeRow), "Legal fees" },
            { NotificationTitle(Engine.CourtPrefabs.kIconName), "Lawsuit awaiting a response" },
            { NotificationDescription(Engine.CourtPrefabs.kIconName), "Citizens or companies have sued the city over a policy. Choose a lawyer or city officials before the verdict, or the city loses by default judgment." },
            { Chirp(PolicyCatalog.kEquipmentMandate, true), "The filter mandate costs us more than the plant can carry. We have taken the city to court." },
            { Chirp(PolicyCatalog.kEquipmentMandate, false), "They dropped the pollution filter rule? We neighbours have filed a lawsuit against the city." },
            { Chirp(PolicyCatalog.kEmissionStandard, true), "Raising the emission standard all at once leaves us no way to run the plant. See you in court." },
            { Chirp(PolicyCatalog.kEmissionStandard, false), "Lower the emission standard and who answers for our air? We are suing the city." },
            { Chirp(PolicyCatalog.kPollutionTax, true), "This pollution tax is crushing us. We have asked the court to revoke it." },
            { Chirp(PolicyCatalog.kPollutionTax, false), "Scrapping the pollution tax leaves the pollution right where it is. Residents are taking it to court." },
            { Chirp(PolicyCatalog.kGreenSubsidy, true), "The green transition subsidy we were promised, cut overnight? We have filed suit." },
            { Chirp(PolicyCatalog.kGreenSubsidy, false), "Our taxes poured into company subsidies? We have filed a lawsuit against the city." },
            { BudgetItem(Engine.LitigationSystem.kDebtRow), "Court damages instalments" },
            { BudgetItem(Engine.PolicyDefinitions.kDisclosureRow), "Emission disclosure office" },
            { PolicyTitle(PolicyCatalog.kPollutionTax), "Pollution Tax" },
            {
                PolicyDescription(PolicyCatalog.kPollutionTax),
                "Industry pays for every unit of pollution it emits. Each company cuts as far as cutting is cheaper than the tax, " +
                "so the city gets the cut at the lowest cost and earns money on what is left. Companies may under-report when inspections are weak."
            },
            { PolicyTitle(PolicyCatalog.kGreenSubsidy), "Green Transition Subsidy" },
            {
                PolicyDescription(PolicyCatalog.kGreenSubsidy),
                "The city pays industry for every unit of pollution it cuts. Companies welcome it, but it costs money, " +
                "is hard to take back, and attracts new industry that brings pollution of its own."
            },
            { PolicyTitle(PolicyCatalog.kEnvironmentalMonitoring), "Environmental Monitoring Unit" },
            {
                PolicyDescription(PolicyCatalog.kEnvironmentalMonitoring),
                "Inspectors check industrial emissions. On its own it changes nothing, " +
                "but it makes the emission standard and the pollution tax far harder to dodge. Needs environment staff."
            },
            { PolicyTitle(PolicyCatalog.kEmissionDisclosure), "Emission Disclosure" },
            {
                PolicyDescription(PolicyCatalog.kEmissionDisclosure),
                "Every company's emissions are published. Only a few cut on publicity alone; " +
                "its real effect is making companies comply with rules willingly, and only where inspections are credible."
            },
            { "RealisticPolicies.Search.Placeholder", "Search policies" },
            { "RealisticPolicies.Series.Header", "Staff series" },
            { "RealisticPolicies.Series.GeneralAdministration", "General administration" },
            { "RealisticPolicies.Series.GeneralAdministration.Tooltip", "The office works like a city hall department. Most policies need general administration staff." },
            { "RealisticPolicies.Series.SocialWelfare", "Social welfare" },
            { "RealisticPolicies.Series.SocialWelfare.Tooltip", "Welfare caseworkers. Residential tax below 8% needs them: the more per resident, the lower it can go (full range at 0.4 per thousand)." },
            { "RealisticPolicies.Series.Tax", "Tax" },
            { "RealisticPolicies.Series.Tax.Tooltip", "Tax officers. Without any, tax rates cannot be changed." },
            { "RealisticPolicies.UI.Lang", "en" },
            { "RealisticPolicies.UI.Tab.Lawsuits", "Lawsuits" },
            { "RealisticPolicies.UI.Report.Input", "Inputs" },
            { "RealisticPolicies.UI.Report.Activity", "Activities" },
            { "RealisticPolicies.UI.Report.Output", "Outputs" },
            { "RealisticPolicies.UI.Report.Outcome", "Outcomes" },
            { "RealisticPolicies.UI.Report.Staff", "{NEED} of {WORKERS}" },
            { "RealisticPolicies.UI.Report.Staff.Tooltip", "Staff this policy needs, out of all staff of the series in the city" },
            { "RealisticPolicies.UI.Report.Effectiveness", "Implementation capacity" },
            { "RealisticPolicies.UI.Report.Effectiveness.Tooltip", "Set by staff, budgets and the approvals the policy must pass" },
            { "RealisticPolicies.UI.Report.Utilization", "Workload ({SERIES})" },
            { "RealisticPolicies.UI.Report.Utilization.Tooltip", "Staff needed ÷ staff available. Above 100% the effect is cut" },
            { "RealisticPolicies.UI.Report.Lag", "Time to take effect" },
            { "RealisticPolicies.UI.Report.LagValue", "about {DAYS} days" },
            { "RealisticPolicies.UI.Report.Capture", "Regulatory capture" },
            { "RealisticPolicies.UI.Report.Capture.Tooltip", "Enforcement grows lax the longer a rule stays unchanged" },
            { "RealisticPolicies.UI.Report.Detection", "Detection chance" },
            { "RealisticPolicies.UI.Report.Compliance", "Companies complying" },
            { "RealisticPolicies.UI.Report.Reporting", "Companies reporting honestly" },
            { "RealisticPolicies.UI.Report.EffectiveRate", "Rate actually collected" },
            { "RealisticPolicies.UI.Report.Abatement", "Average pollution cut by companies" },
            { "RealisticPolicies.UI.Report.Progress", "Share of the target effect" },
            { "RealisticPolicies.UI.Report.WatchDetection", "Detection chance of other rules" },
            { "RealisticPolicies.UI.Report.Voluntary", "Companies complying of their own accord" },
            { "RealisticPolicies.UI.Report.Air", "Average air pollution (since start)" },
            { "RealisticPolicies.UI.Report.Modifier.IndustrialPollution", "Industrial air and ground pollution" },
            { "RealisticPolicies.UI.Report.Modifier.IndustrialAirPollution", "Industrial air pollution" },
            { "RealisticPolicies.UI.Report.Modifier.IndustrialGroundPollution", "Industrial ground pollution" },
            { "RealisticPolicies.UI.Report.Modifier.IndustrialGarbage", "Industrial garbage" },
            { "RealisticPolicies.UI.Report.Modifier.IndustrialEfficiency", "Industrial efficiency" },
            { "RealisticPolicies.UI.Report.Lever.IndustrialBase", "Industrial demand" },
            { "RealisticPolicies.UI.Report.Warning", "Recent decisions about this policy have stirred strong opposition." },
            { "RealisticPolicies.UI.Report.Suspended", "An injunction has stopped this policy until the verdict." },
            { "RealisticPolicies.UI.Report.InCourt", "Case {CASE} is in court." },
            { "RealisticPolicies.UI.Report.OpenLawsuits", "Open the Lawsuits tab" },
            { "RealisticPolicies.UI.Report.Banned", "Banned" },
            { "RealisticPolicies.UI.Report.DaysLeft", "{DAYS} days left" },
            { "RealisticPolicies.UI.Report.Verdict", "Verdict" },
            { "RealisticPolicies.UI.Report.Off", "This policy is off." },
            { "RealisticPolicies.UI.Suit.None", "No lawsuits are pending." },
            { "RealisticPolicies.UI.Suit.CaseNumber", "{YEAR}-Gu-Hap-{N}" },
            { "RealisticPolicies.UI.Suit.Title", "Action to Revoke the {POLICY} Order and Claim State Compensation" },
            { "RealisticPolicies.UI.Suit.Title.NoClaim", "Action to Revoke the {POLICY} Order" },
            { "RealisticPolicies.UI.Suit.Title.Cut", "Action to Revoke the {POLICY} Reduction Order and Claim State Compensation" },
            { "RealisticPolicies.UI.Suit.Title.Cut.NoClaim", "Action to Revoke the {POLICY} Reduction Order" },
            { "RealisticPolicies.UI.Suit.Plaintiffs", "Plaintiffs" },
            { "RealisticPolicies.UI.Suit.Defendants", "Defendants" },
            { "RealisticPolicies.UI.Suit.UntilVerdict", "Until the verdict" },
            { "RealisticPolicies.UI.Suit.HoursLeft", "{HOURS} h left" },
            { "RealisticPolicies.UI.Suit.WinChance", "Chance of winning" },
            { "RealisticPolicies.UI.Suit.WinChance.Tooltip", "Legal advice, not a certainty" },
            { "RealisticPolicies.UI.Suit.Hint.0", "Low" },
            { "RealisticPolicies.UI.Suit.Hint.1", "Medium" },
            { "RealisticPolicies.UI.Suit.Hint.2", "High" },
            { "RealisticPolicies.UI.Suit.Response", "Defence" },
            { "RealisticPolicies.UI.Suit.Response.None", "Not chosen" },
            { "RealisticPolicies.UI.Suit.Response.None.Tooltip", "Without a defence by the verdict, the city loses by default judgment" },
            { "RealisticPolicies.UI.Suit.Response.Lawyer", "Outside lawyer ({FEE})" },
            { "RealisticPolicies.UI.Suit.Response.Official", "City officials" },
            { "RealisticPolicies.UI.Suit.Injunction", "Injunction" },
            { "RealisticPolicies.UI.Suit.Injunction.Requested", "Requested, decided 30 min after filing" },
            { "RealisticPolicies.UI.Suit.Injunction.Granted", "Takes effect in {MINUTES} min" },
            { "RealisticPolicies.UI.Suit.Injunction.Granted.Tooltip", "The court granted it. Choose a defence before it takes effect and it is reviewed once as an objection" },
            { "RealisticPolicies.UI.Suit.Injunction.InEffect", "Policy stopped until the verdict" },
            { "RealisticPolicies.UI.Suit.Injunction.Denied", "Denied" },
            { "RealisticPolicies.UI.Suit.Injunction.Cancelled", "Cancelled on review" },
            { "RealisticPolicies.UI.Suit.ShowClaims", "Show the claims" },
            { "RealisticPolicies.UI.Suit.HideClaims", "Hide the claims" },
            { "RealisticPolicies.UI.Suit.Claims", "Relief sought" },
            { "RealisticPolicies.UI.Suit.Grounds", "Grounds" },
            { "RealisticPolicies.UI.Suit.Ground.Proportionality", "Proportionality: the harm to the plaintiffs outweighs the effect." },
            { "RealisticPolicies.UI.Suit.Ground.LegitimateExpectation", "Legitimate expectation: a large change was made at once, without notice." },
            { "RealisticPolicies.UI.Suit.Ground.Procedure", "Procedure: those affected were not properly heard." },
            { "RealisticPolicies.UI.Suit.Hire", "Hire an outside lawyer" },
            { "RealisticPolicies.UI.Suit.Hire.Detail", "{FEE}, paid until the verdict" },
            { "RealisticPolicies.UI.Suit.Hire.Late", "{FEE}, paid until the verdict (late hire × 1.5)" },
            { "RealisticPolicies.UI.Suit.Assign", "Assign city officials" },
            { "RealisticPolicies.UI.Suit.Assign.Detail", "{N} general administration staff until the verdict" },
            { "RealisticPolicies.UI.Suit.Settle", "Settlement offers" },
            { "RealisticPolicies.UI.Suit.Settle.Left", "{N} left" },
            { "RealisticPolicies.UI.Suit.Settle.Tooltip", "Up to 3 offers per suit; after a refusal, wait 3 hours" },
            { "RealisticPolicies.UI.Suit.Settle.Intensity", "Policy level" },
            { "RealisticPolicies.UI.Suit.Settle.TurnOff", "Switch the policy off" },
            { "RealisticPolicies.UI.Suit.Settle.Restore", "Restore the policy" },
            { "RealisticPolicies.UI.Suit.Settle.Compensation", "Compensation (one-off)" },
            { "RealisticPolicies.UI.Suit.Settle.Offer", "Offer settlement" },
            { "RealisticPolicies.UI.Suit.Settle.Wait", "Next offer in {HOURS} h" },
            { "RealisticPolicies.UI.Suit.Settle.NoneLeft", "No offers left" },
            { "RealisticPolicies.UI.Suit.Settle.Accepted", "The plaintiffs accepted the settlement. The suit is over." },
            { "RealisticPolicies.UI.Suit.Settle.Refused", "The plaintiffs refused the settlement." },
            { "RealisticPolicies.UI.Suit.After", "After the verdict" },
            { "RealisticPolicies.UI.Suit.Lost", "{CASE} lost" },
            { "RealisticPolicies.UI.Suit.LostPart", "{CASE} lost in part" },
            { "RealisticPolicies.UI.Suit.Ban", "Ban" },
            { "RealisticPolicies.UI.Suit.Ban.Toggle.Tooltip", "An on/off policy stays locked during the ban" },
            { "RealisticPolicies.UI.Suit.Ban.Slider.Tooltip", "The slider may be used again only within the conditions the court set" },
            { "RealisticPolicies.UI.Suit.Debt", "Damages left" },
            { "RealisticPolicies.UI.Suit.Debt.Tooltip", "Paid at most {CAP} a day. Overdue: {ARREARS}" },
            { "RealisticPolicies.UI.Plaintiffs.Households", "{NAME} and {N} other households" },
            { "RealisticPolicies.UI.Plaintiffs.IndustrialCompanies", "{NAME} and {N} other industrial companies" },
            { "RealisticPolicies.UI.Plaintiffs.CommercialCompanies", "{NAME} and {N} other commercial companies" },
            { "RealisticPolicies.UI.Plaintiffs.OfficeCompanies", "{NAME} and {N} other office companies" },
            { "RealisticPolicies.UI.Claim.Burden", "1. The order of {DATE} by which the {HOLDER} imposed {POLICY} ({VALUE}) on the plaintiffs is revoked." },
            { "RealisticPolicies.UI.Claim.BurdenToggle", "1. The order of {DATE} by which the {HOLDER} imposed {POLICY} on the plaintiffs is revoked." },
            { "RealisticPolicies.UI.Claim.Cut", "1. The order of {DATE} by which the {HOLDER} lowered {POLICY} from {PRIOR} to {VALUE} is revoked." },
            { "RealisticPolicies.UI.Claim.CutToggle", "1. The order of {DATE} by which the {HOLDER} abolished {POLICY} is revoked." },
            { "RealisticPolicies.UI.Claim.Pay", "2. The {BODY} shall pay the plaintiffs {CLAIM}." },
            { "RealisticPolicies.UI.Claim.Costs", "3. The defendants shall bear the costs of the suit." },
            { "RealisticPolicies.UI.Claim.Date", "{MONTH}/{YEAR}" },
            { "RealisticPolicies.UI.Defendant.City.Holder", "Mayor of {NAME}" },
            { "RealisticPolicies.UI.Defendant.City.Body", "City of {NAME}" },
            { "RealisticPolicies.UI.Defendant.County.Holder", "County Executive of {NAME}" },
            { "RealisticPolicies.UI.Defendant.County.Body", "{NAME} County" },
            { "RealisticPolicies.UI.Defendant.State.Holder", "Governor of {NAME}" },
            { "RealisticPolicies.UI.Defendant.State.Body", "State of {NAME}" },
            { "RealisticPolicies.SeriesName.GeneralAdministration", "General administration" },
            { "RealisticPolicies.SeriesName.Police", "Police" },
            { "RealisticPolicies.SeriesName.Corrections", "Corrections" },
            { "RealisticPolicies.SeriesName.FireAndDisaster", "Fire and disaster" },
            { "RealisticPolicies.SeriesName.Education", "Education" },
            { "RealisticPolicies.SeriesName.Research", "Research" },
            { "RealisticPolicies.SeriesName.Health", "Health" },
            { "RealisticPolicies.SeriesName.Environment", "Environment" },
            { "RealisticPolicies.SeriesName.FacilityMaintenance", "Facility maintenance" },
            { "RealisticPolicies.SeriesName.EngineeringUtilities", "Engineering" },
            { "RealisticPolicies.SeriesName.Transit", "Transit" },
            { "RealisticPolicies.SeriesName.PostAndTelecom", "Post and telecom" },
            { "RealisticPolicies.SeriesName.SocialWelfare", "Social welfare" },
            { "RealisticPolicies.SeriesName.Tax", "Tax" },
        };
    }

    public sealed class LocaleKO : LocaleBase
    {
        protected override Dictionary<string, string> Entries() => new Dictionary<string, string>
        {
            { PolicyTitle(PolicyCatalog.kEquipmentMandate), "오염 방지 설비 의무화" },
            {
                PolicyDescription(PolicyCatalog.kEquipmentMandate),
                "모든 산업체가 필터를 달아야 합니다. 단속은 쉽지만 모든 기업이 같은 비용을 냅니다.\n\n" +
                "- 대기 및 토양 오염 -25%\n- 쓰레기 누적량 +15%\n- 산업 효율 -3%"
            },
            { PolicyTitle(PolicyCatalog.kEmissionStandard), "배출 기준 규제" },
            { PolicyDescription(PolicyCatalog.kEmissionStandard), "산업체가 줄여야 하는 오염 비율을 정합니다. 줄이는 방법은 기업이 고릅니다." },
            { BudgetItem(BudgetInjectionSystem.kIncomeRowId), "오염세" },
            { BudgetItem(BudgetInjectionSystem.kExpenseRowId), "녹색 전환 보조금" },
            { BudgetItem(Engine.PolicyDefinitions.kEmissionStandardEnforcementRow), "배출 기준 단속비" },
            { BudgetItem(Engine.PolicyDefinitions.kMonitoringRow), "환경 감시단 인건비" },
            { BudgetItem(Engine.LitigationSystem.kLawyerFeeRow), "소송 비용" },
            { NotificationTitle(Engine.CourtPrefabs.kIconName), "대응하지 않은 행정소송" },
            { NotificationDescription(Engine.CourtPrefabs.kIconName), "주민이나 기업이 정책을 두고 도시를 상대로 소송을 냈습니다. 판결 전에 변호사나 소송수행 공무원을 정하지 않으면 무변론 판결로 패소합니다." },
            { Chirp(PolicyCatalog.kEquipmentMandate, true), "필터 설치 의무 때문에 공장 설비비가 감당이 안 됩니다. 결국 행정소송을 냈습니다." },
            { Chirp(PolicyCatalog.kEquipmentMandate, false), "오염 방지 설비 의무를 없앤다고요? 이웃들과 함께 행정소송을 냈습니다." },
            { Chirp(PolicyCatalog.kEmissionStandard, true), "배출 기준을 한꺼번에 이렇게 올리면 공장을 돌릴 수가 없습니다. 법원에서 뵙겠습니다." },
            { Chirp(PolicyCatalog.kEmissionStandard, false), "배출 기준을 낮추면 우리 동네 공기는 누가 책임집니까. 행정소송을 냈습니다." },
            { Chirp(PolicyCatalog.kPollutionTax, true), "오염세 부담이 너무 큽니다. 처분 취소 소송을 냈습니다." },
            { Chirp(PolicyCatalog.kPollutionTax, false), "오염세를 없애면 오염은 그대로 남습니다. 주민들이 소송에 나섰습니다." },
            { Chirp(PolicyCatalog.kGreenSubsidy, true), "약속했던 녹색 전환 보조금을 하루아침에 줄이다니요. 소송을 냈습니다." },
            { Chirp(PolicyCatalog.kGreenSubsidy, false), "기업 보조금에 우리 세금을 쏟아붓는 걸 두고 볼 수 없어 소송을 냈습니다." },
            { BudgetItem(Engine.LitigationSystem.kDebtRow), "배상금 분할 납부" },
            { BudgetItem(Engine.PolicyDefinitions.kDisclosureRow), "배출 정보 공개 운영비" },
            { PolicyTitle(PolicyCatalog.kPollutionTax), "오염세" },
            {
                PolicyDescription(PolicyCatalog.kPollutionTax),
                "산업체가 내보내는 오염량만큼 세금을 냅니다. 기업은 세금보다 줄이는 비용이 싼 만큼 오염을 줄이므로, " +
                "가장 적은 비용으로 오염이 줄고 남은 오염에서는 세금을 거둡니다. 단속이 약하면 기업이 배출량을 줄여서 신고합니다."
            },
            { PolicyTitle(PolicyCatalog.kGreenSubsidy), "녹색 전환 보조금" },
            {
                PolicyDescription(PolicyCatalog.kGreenSubsidy),
                "산업체가 줄인 오염만큼 도시가 돈을 줍니다. 기업은 반기지만 돈이 들고, 한번 주면 끊기 어려우며, " +
                "새 산업체를 끌어들여 오염이 다시 늘 수 있습니다."
            },
            { PolicyTitle(PolicyCatalog.kEnvironmentalMonitoring), "환경 감시단" },
            {
                PolicyDescription(PolicyCatalog.kEnvironmentalMonitoring),
                "단속반이 산업체 배출을 점검합니다. 이 정책만으로는 효과가 없지만, " +
                "배출 기준과 오염세를 피하기 훨씬 어렵게 만듭니다. 환경직 인력이 필요합니다."
            },
            { PolicyTitle(PolicyCatalog.kEmissionDisclosure), "배출 정보 공개제" },
            {
                PolicyDescription(PolicyCatalog.kEmissionDisclosure),
                "기업별 배출량을 공개합니다. 공개만으로 줄이는 기업은 소수입니다. " +
                "진짜 효과는 기업이 규제를 스스로 따르게 만드는 것이고, 단속을 믿을 수 있을 때만 통합니다."
            },
            { "RealisticPolicies.Search.Placeholder", "정책 검색" },
            { "RealisticPolicies.Series.Header", "공무원 직렬" },
            { "RealisticPolicies.Series.GeneralAdministration", "일반행정직" },
            { "RealisticPolicies.Series.GeneralAdministration.Tooltip", "시청 부서처럼 일합니다. 대부분의 정책에 일반행정직이 필요합니다." },
            { "RealisticPolicies.Series.SocialWelfare", "사회복지직" },
            { "RealisticPolicies.Series.SocialWelfare.Tooltip", "주거세를 8% 아래로 내리려면 필요합니다. 주민 대비 많을수록 더 낮출 수 있고, 주민 1,000명당 0.4명이 있으면 게임 기본 하한까지 내릴 수 있습니다." },
            { "RealisticPolicies.Series.Tax", "세무직" },
            { "RealisticPolicies.Series.Tax.Tooltip", "세무 공무원입니다. 한 명도 없으면 세율을 바꿀 수 없습니다." },
            { "RealisticPolicies.UI.Lang", "ko" },
            { "RealisticPolicies.UI.Tab.Lawsuits", "행정소송" },
            { "RealisticPolicies.UI.Report.Input", "투입" },
            { "RealisticPolicies.UI.Report.Activity", "활동" },
            { "RealisticPolicies.UI.Report.Output", "산출" },
            { "RealisticPolicies.UI.Report.Outcome", "결과" },
            { "RealisticPolicies.UI.Report.Staff", "{NEED}명 (전체 {WORKERS}명)" },
            { "RealisticPolicies.UI.Report.Staff.Tooltip", "이 정책에 드는 인원 (도시 전체 인원)" },
            { "RealisticPolicies.UI.Report.Effectiveness", "집행 역량" },
            { "RealisticPolicies.UI.Report.Effectiveness.Tooltip", "인원, 예산, 거쳐야 하는 결재 단계로 정해집니다" },
            { "RealisticPolicies.UI.Report.Utilization", "업무 부담 ({SERIES})" },
            { "RealisticPolicies.UI.Report.Utilization.Tooltip", "필요 인원 ÷ 있는 인원. 100%를 넘으면 효과가 줄어듭니다" },
            { "RealisticPolicies.UI.Report.Lag", "효과가 나타나기까지" },
            { "RealisticPolicies.UI.Report.LagValue", "약 {DAYS}일" },
            { "RealisticPolicies.UI.Report.Capture", "규제 포획" },
            { "RealisticPolicies.UI.Report.Capture.Tooltip", "오래 시행할수록 단속이 느슨해집니다" },
            { "RealisticPolicies.UI.Report.Detection", "적발 확률" },
            { "RealisticPolicies.UI.Report.Compliance", "기준을 지키는 기업" },
            { "RealisticPolicies.UI.Report.Reporting", "배출량을 제대로 신고한 기업" },
            { "RealisticPolicies.UI.Report.EffectiveRate", "실제로 걷히는 세율" },
            { "RealisticPolicies.UI.Report.Abatement", "기업 평균 오염 감축" },
            { "RealisticPolicies.UI.Report.Progress", "목표 효과 도달" },
            { "RealisticPolicies.UI.Report.WatchDetection", "다른 규제의 적발 확률" },
            { "RealisticPolicies.UI.Report.Voluntary", "스스로 규제를 지키는 기업" },
            { "RealisticPolicies.UI.Report.Air", "평균 대기 오염 (시행 전 대비)" },
            { "RealisticPolicies.UI.Report.Modifier.IndustrialPollution", "산업 대기·토양 오염" },
            { "RealisticPolicies.UI.Report.Modifier.IndustrialAirPollution", "산업 대기 오염" },
            { "RealisticPolicies.UI.Report.Modifier.IndustrialGroundPollution", "산업 토양 오염" },
            { "RealisticPolicies.UI.Report.Modifier.IndustrialGarbage", "산업 쓰레기" },
            { "RealisticPolicies.UI.Report.Modifier.IndustrialEfficiency", "산업 효율" },
            { "RealisticPolicies.UI.Report.Lever.IndustrialBase", "산업 수요" },
            { "RealisticPolicies.UI.Report.Warning", "최근 이 정책에 대한 결정으로 반발이 커졌습니다." },
            { "RealisticPolicies.UI.Report.Suspended", "집행정지로 판결 때까지 이 정책의 효과가 멈춰 있습니다." },
            { "RealisticPolicies.UI.Report.InCourt", "{CASE} 소송이 진행 중입니다." },
            { "RealisticPolicies.UI.Report.OpenLawsuits", "행정소송 탭 열기" },
            { "RealisticPolicies.UI.Report.Banned", "사용 금지" },
            { "RealisticPolicies.UI.Report.DaysLeft", "{DAYS}일 남음" },
            { "RealisticPolicies.UI.Report.Verdict", "판결" },
            { "RealisticPolicies.UI.Report.Off", "이 정책은 꺼져 있습니다." },
            { "RealisticPolicies.UI.Suit.None", "진행 중인 소송이 없습니다." },
            { "RealisticPolicies.UI.Suit.CaseNumber", "{YEAR}구합{N}" },
            { "RealisticPolicies.UI.Suit.Title", "{POLICY} 처분 취소 및 국가배상" },
            { "RealisticPolicies.UI.Suit.Title.NoClaim", "{POLICY} 처분 취소" },
            { "RealisticPolicies.UI.Suit.Title.Cut", "{POLICY} 축소 처분 취소 및 국가배상" },
            { "RealisticPolicies.UI.Suit.Title.Cut.NoClaim", "{POLICY} 축소 처분 취소" },
            { "RealisticPolicies.UI.Suit.Plaintiffs", "원고" },
            { "RealisticPolicies.UI.Suit.Defendants", "피고" },
            { "RealisticPolicies.UI.Suit.UntilVerdict", "판결까지" },
            { "RealisticPolicies.UI.Suit.HoursLeft", "{HOURS}시간 남음" },
            { "RealisticPolicies.UI.Suit.WinChance", "승소 확률" },
            { "RealisticPolicies.UI.Suit.WinChance.Tooltip", "법률 자문의 전망이라 실제와 다를 수 있습니다" },
            { "RealisticPolicies.UI.Suit.Hint.0", "낮음" },
            { "RealisticPolicies.UI.Suit.Hint.1", "보통" },
            { "RealisticPolicies.UI.Suit.Hint.2", "높음" },
            { "RealisticPolicies.UI.Suit.Response", "대응" },
            { "RealisticPolicies.UI.Suit.Response.None", "아직 정하지 않음" },
            { "RealisticPolicies.UI.Suit.Response.None.Tooltip", "판결 전까지 대응하지 않으면 무변론 판결로 패소합니다" },
            { "RealisticPolicies.UI.Suit.Response.Lawyer", "외부 변호사 ({FEE})" },
            { "RealisticPolicies.UI.Suit.Response.Official", "소송수행 공무원" },
            { "RealisticPolicies.UI.Suit.Injunction", "집행정지" },
            { "RealisticPolicies.UI.Suit.Injunction.Requested", "신청됨 (제기 30분 뒤 결정)" },
            { "RealisticPolicies.UI.Suit.Injunction.Granted", "{MINUTES}분 뒤부터 효력" },
            { "RealisticPolicies.UI.Suit.Injunction.Granted.Tooltip", "법원이 받아들였습니다. 효력이 생기기 전에 대응하면 이의 신청으로 보고 한 번 다시 검토합니다" },
            { "RealisticPolicies.UI.Suit.Injunction.InEffect", "판결 때까지 정책 효과 정지" },
            { "RealisticPolicies.UI.Suit.Injunction.Denied", "기각" },
            { "RealisticPolicies.UI.Suit.Injunction.Cancelled", "재검토로 취소" },
            { "RealisticPolicies.UI.Suit.ShowClaims", "청구 취지와 원인 보기" },
            { "RealisticPolicies.UI.Suit.HideClaims", "청구 취지와 원인 숨기기" },
            { "RealisticPolicies.UI.Suit.Claims", "청구 취지" },
            { "RealisticPolicies.UI.Suit.Grounds", "청구 원인 요지" },
            { "RealisticPolicies.UI.Suit.Ground.Proportionality", "비례원칙 위반: 효과에 비해 원고의 피해가 지나치다." },
            { "RealisticPolicies.UI.Suit.Ground.LegitimateExpectation", "신뢰보호원칙 위반: 예고 없이 한꺼번에 크게 바꿨다." },
            { "RealisticPolicies.UI.Suit.Ground.Procedure", "절차상 하자: 의견을 충분히 듣지 않았다." },
            { "RealisticPolicies.UI.Suit.Hire", "외부 변호사 선임" },
            { "RealisticPolicies.UI.Suit.Hire.Detail", "{FEE} · 판결 때까지 나눠 냄" },
            { "RealisticPolicies.UI.Suit.Hire.Late", "{FEE} · 판결 때까지 나눠 냄 (늦은 선임 1.5배)" },
            { "RealisticPolicies.UI.Suit.Assign", "소송수행 공무원 지정" },
            { "RealisticPolicies.UI.Suit.Assign.Detail", "일반행정직 {N}명 · 판결 때까지" },
            { "RealisticPolicies.UI.Suit.Settle", "화해 제안" },
            { "RealisticPolicies.UI.Suit.Settle.Left", "{N}번 남음" },
            { "RealisticPolicies.UI.Suit.Settle.Tooltip", "소송마다 3번까지 낼 수 있고, 거절당하면 3시간 뒤에 다시 낼 수 있습니다" },
            { "RealisticPolicies.UI.Suit.Settle.Intensity", "정책 강도" },
            { "RealisticPolicies.UI.Suit.Settle.TurnOff", "정책 끄기" },
            { "RealisticPolicies.UI.Suit.Settle.Restore", "정책 되살리기" },
            { "RealisticPolicies.UI.Suit.Settle.Compensation", "보상금 (일시 지급)" },
            { "RealisticPolicies.UI.Suit.Settle.Offer", "화해안 제시" },
            { "RealisticPolicies.UI.Suit.Settle.Wait", "{HOURS}시간 뒤 다시 제안 가능" },
            { "RealisticPolicies.UI.Suit.Settle.NoneLeft", "남은 제안 없음" },
            { "RealisticPolicies.UI.Suit.Settle.Accepted", "원고가 화해안을 받아들였습니다. 소송이 끝났습니다." },
            { "RealisticPolicies.UI.Suit.Settle.Refused", "원고가 화해안을 거절했습니다." },
            { "RealisticPolicies.UI.Suit.After", "판결 이후" },
            { "RealisticPolicies.UI.Suit.Lost", "{CASE} 패소" },
            { "RealisticPolicies.UI.Suit.LostPart", "{CASE} 일부 패소" },
            { "RealisticPolicies.UI.Suit.Ban", "사용 금지" },
            { "RealisticPolicies.UI.Suit.Ban.Toggle.Tooltip", "켜고 끄는 정책이라 금지 기간 동안 잠깁니다" },
            { "RealisticPolicies.UI.Suit.Ban.Slider.Tooltip", "법원이 정한 조건 안에서만 슬라이더를 다시 쓸 수 있습니다" },
            { "RealisticPolicies.UI.Suit.Debt", "남은 배상금" },
            { "RealisticPolicies.UI.Suit.Debt.Tooltip", "하루에 최대 {CAP}씩 냅니다. 밀린 금액: {ARREARS}" },
            { "RealisticPolicies.UI.Plaintiffs.Households", "{NAME} 외 주민 {N}가구" },
            { "RealisticPolicies.UI.Plaintiffs.IndustrialCompanies", "{NAME} 외 산업 기업 {N}곳" },
            { "RealisticPolicies.UI.Plaintiffs.CommercialCompanies", "{NAME} 외 상업 기업 {N}곳" },
            { "RealisticPolicies.UI.Plaintiffs.OfficeCompanies", "{NAME} 외 사무 기업 {N}곳" },
            { "RealisticPolicies.UI.Claim.Burden", "1. 피고 {HOLDER_SUBJECT} {DATE} 원고들에 대하여 한 {POLICY}({VALUE}) 처분을 취소한다." },
            { "RealisticPolicies.UI.Claim.BurdenToggle", "1. 피고 {HOLDER_SUBJECT} {DATE} 원고들에 대하여 한 {POLICY} 처분을 취소한다." },
            { "RealisticPolicies.UI.Claim.Cut", "1. 피고 {HOLDER_SUBJECT} {DATE} {POLICY_OBJECT} {PRIOR}에서 {VALUE}로 낮춘 처분을 취소한다." },
            { "RealisticPolicies.UI.Claim.CutToggle", "1. 피고 {HOLDER_SUBJECT} {DATE} {POLICY_OBJECT} 폐지한 처분을 취소한다." },
            { "RealisticPolicies.UI.Claim.Pay", "2. 피고 {BODY_TOPIC} 원고들에게 {CLAIM_OBJECT} 지급하라." },
            { "RealisticPolicies.UI.Claim.Costs", "3. 소송비용은 피고들이 부담한다." },
            { "RealisticPolicies.UI.Claim.Date", "{YEAR}. {MONTH}." },
            { "RealisticPolicies.UI.Defendant.City.Holder", "{NAME}시장" },
            { "RealisticPolicies.UI.Defendant.City.Body", "{NAME}시" },
            { "RealisticPolicies.UI.Defendant.County.Holder", "{NAME}군수" },
            { "RealisticPolicies.UI.Defendant.County.Body", "{NAME}군" },
            { "RealisticPolicies.UI.Defendant.State.Holder", "{NAME}지사" },
            { "RealisticPolicies.UI.Defendant.State.Body", "{NAME}" },
            { "RealisticPolicies.SeriesName.GeneralAdministration", "일반행정직" },
            { "RealisticPolicies.SeriesName.Police", "경찰직" },
            { "RealisticPolicies.SeriesName.Corrections", "교정직" },
            { "RealisticPolicies.SeriesName.FireAndDisaster", "소방·방재직" },
            { "RealisticPolicies.SeriesName.Education", "교육직" },
            { "RealisticPolicies.SeriesName.Research", "연구직" },
            { "RealisticPolicies.SeriesName.Health", "보건의료직" },
            { "RealisticPolicies.SeriesName.Environment", "환경직" },
            { "RealisticPolicies.SeriesName.FacilityMaintenance", "시설관리직" },
            { "RealisticPolicies.SeriesName.EngineeringUtilities", "공업·기술직" },
            { "RealisticPolicies.SeriesName.Transit", "운수직" },
            { "RealisticPolicies.SeriesName.PostAndTelecom", "우정·통신직" },
            { "RealisticPolicies.SeriesName.SocialWelfare", "사회복지직" },
            { "RealisticPolicies.SeriesName.Tax", "세무직" },
        };
    }

    /// <summary>Options page texts (they need the setting instance for their ids).</summary>
    public sealed class SettingLocale : LocaleBase
    {
        private readonly Setting m_Setting;
        private readonly bool m_Korean;

        public SettingLocale(Setting setting, bool korean)
        {
            m_Setting = setting;
            m_Korean = korean;
        }

        protected override Dictionary<string, string> Entries() => m_Korean
            ? new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Realistic Policies" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "기본" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kLitigationGroup), "행정소송" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.PunitiveDamages)), "징벌적 배상" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.PunitiveDamages)), "소송에서 지면 원고 손해의 5배를 배상합니다 (국내 징벌적 손해배상 규정의 최근 상한). 연간 총수입의 2배 상한은 그대로입니다." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Government)), "자치단체 종류" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Government)), "소송의 피고 이름을 정합니다. 시: ○○시장, ○○시 / 군: ○○군수, ○○군 / 도·주: ○○지사, ○○" },
                { m_Setting.GetEnumValueLocaleID(GovernmentType.City), "시" },
                { m_Setting.GetEnumValueLocaleID(GovernmentType.County), "군" },
                { m_Setting.GetEnumValueLocaleID(GovernmentType.State), "도·주" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.DefaultResponse)), "기본 대응 방식" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.DefaultResponse)), "변호사나 공무원을 고르면 소송이 걸리자마자 그 방식으로 대응합니다 (늦은 선임 할증 없음). '매번 직접 선택'이면 판결 전까지 대응하지 않을 때 무변론 판결로 패소합니다." },
                { m_Setting.GetEnumValueLocaleID(DefaultSuitResponse.AskEachTime), "매번 직접 선택" },
                { m_Setting.GetEnumValueLocaleID(DefaultSuitResponse.AlwaysLawyer), "항상 변호사 선임" },
                { m_Setting.GetEnumValueLocaleID(DefaultSuitResponse.AlwaysOfficials), "항상 공무원 지정" },
            }
            : new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Realistic Policies" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kLitigationGroup), "Administrative litigation" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.PunitiveDamages)), "Punitive damages" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.PunitiveDamages)), "A lost suit pays five times the plaintiffs' losses (the recent ceiling of Korean punitive damage statutes). The cap of twice the annual income still applies." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Government)), "Local government" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Government)), "Names the defendants of a suit. City: Mayor of …, City of … / County: County Executive of …, … County / State: Governor of …, State of …" },
                { m_Setting.GetEnumValueLocaleID(GovernmentType.City), "City" },
                { m_Setting.GetEnumValueLocaleID(GovernmentType.County), "County" },
                { m_Setting.GetEnumValueLocaleID(GovernmentType.State), "State" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.DefaultResponse)), "Default defence" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.DefaultResponse)), "With a lawyer or officials chosen here, the city answers each suit as soon as it is filed (no late-hire surcharge). With 'Ask each time', a suit left unanswered until the verdict is lost by default judgment." },
                { m_Setting.GetEnumValueLocaleID(DefaultSuitResponse.AskEachTime), "Ask each time" },
                { m_Setting.GetEnumValueLocaleID(DefaultSuitResponse.AlwaysLawyer), "Always hire a lawyer" },
                { m_Setting.GetEnumValueLocaleID(DefaultSuitResponse.AlwaysOfficials), "Always assign officials" },
            };
    }
}
