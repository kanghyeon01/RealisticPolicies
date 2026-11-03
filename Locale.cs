using System.Collections.Generic;
using Colossal;

namespace RealisticPolicies
{
    public abstract class LocaleBase : IDictionarySource
    {
        protected abstract Dictionary<string, string> Entries();

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return Entries();
        }

        public void Unload()
        {
        }

        protected static string PolicyTitle(string id) => $"Policy.TITLE[{id}]";

        protected static string PolicyDescription(string id) => $"Policy.DESCRIPTION[{id}]";

        protected static string BudgetItem(string id) => $"EconomyPanel.BUDGET_ITEM[{id}]";
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
            { "RealisticPolicies.Welfare.Label", "Social welfare staff" },
            { "RealisticPolicies.Welfare.Tooltip", "Checked: this office's staff count as social welfare staff. Unchecked: they count as general administration, like a city hall department." },
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
            { BudgetItem(Engine.PolicyDefinitions.kDisclosureRow), "배출 정보 공개 운영비" },
            { PolicyTitle(PolicyCatalog.kPollutionTax), "오염세" },
            {
                PolicyDescription(PolicyCatalog.kPollutionTax),
                "산업체가 내보내는 오염마다 세금을 냅니다. 기업마다 세금보다 줄이는 비용이 싼 만큼 줄이므로, " +
                "가장 적은 비용으로 오염을 줄이고 남은 오염에서는 세금을 거둡니다. 단속이 약하면 기업이 배출량을 줄여서 신고합니다."
            },
            { PolicyTitle(PolicyCatalog.kGreenSubsidy), "녹색 전환 보조금" },
            {
                PolicyDescription(PolicyCatalog.kGreenSubsidy),
                "산업체가 줄인 오염만큼 시가 돈을 줍니다. 기업은 반기지만 돈이 들고, 한번 주면 끊기 어려우며, " +
                "새 산업체를 끌어들여 오염이 다시 늘 수 있습니다."
            },
            { PolicyTitle(PolicyCatalog.kEnvironmentalMonitoring), "환경 감시단" },
            {
                PolicyDescription(PolicyCatalog.kEnvironmentalMonitoring),
                "단속반이 산업체 배출을 점검합니다. 혼자서는 아무것도 바꾸지 않지만, " +
                "배출 기준과 오염세를 피하기 훨씬 어렵게 만듭니다. 환경직 인력이 필요합니다."
            },
            { PolicyTitle(PolicyCatalog.kEmissionDisclosure), "배출 정보 공개제" },
            {
                PolicyDescription(PolicyCatalog.kEmissionDisclosure),
                "기업별 배출량을 공개합니다. 공개만으로 줄이는 기업은 소수입니다. " +
                "진짜 효과는 기업이 규제를 스스로 따르게 만드는 것이고, 단속이 믿을 만할 때만 통합니다."
            },
            { "RealisticPolicies.Search.Placeholder", "정책 검색" },
            { "RealisticPolicies.Welfare.Label", "사회복지직으로 지정" },
            { "RealisticPolicies.Welfare.Tooltip", "체크하면 이 사무소 직원이 사회복지직으로 계산됩니다. 체크하지 않으면 시청 부서처럼 일반행정직으로 계산됩니다." },
        };
    }
}
