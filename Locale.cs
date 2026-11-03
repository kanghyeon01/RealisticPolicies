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
                "- Air and ground pollution -25%\n- Garbage accumulation +15%"
            },
            { PolicyTitle(PolicyCatalog.kEmissionStandard), "Emission Standard" },
            { PolicyDescription(PolicyCatalog.kEmissionStandard), "Sets the share of industrial pollution that companies must cut. Companies choose how to cut it." },
            { BudgetItem(BudgetInjectionSystem.kIncomeRowId), "Pollution tax" },
            { BudgetItem(BudgetInjectionSystem.kExpenseRowId), "Green transition subsidy" },
            { BudgetItem(Engine.PolicyDefinitions.kEmissionStandardEnforcementRow), "Emission standard inspections" },
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
                "- 대기 및 토양 오염 -25%\n- 쓰레기 누적량 +15%"
            },
            { PolicyTitle(PolicyCatalog.kEmissionStandard), "배출 기준 규제" },
            { PolicyDescription(PolicyCatalog.kEmissionStandard), "산업체가 줄여야 하는 오염 비율을 정합니다. 줄이는 방법은 기업이 고릅니다." },
            { BudgetItem(BudgetInjectionSystem.kIncomeRowId), "오염세" },
            { BudgetItem(BudgetInjectionSystem.kExpenseRowId), "녹색 전환 보조금" },
            { BudgetItem(Engine.PolicyDefinitions.kEmissionStandardEnforcementRow), "배출 기준 단속비" },
            { "RealisticPolicies.Search.Placeholder", "정책 검색" },
            { "RealisticPolicies.Welfare.Label", "사회복지직으로 지정" },
            { "RealisticPolicies.Welfare.Tooltip", "체크하면 이 사무소 직원이 사회복지직으로 계산됩니다. 체크하지 않으면 시청 부서처럼 일반행정직으로 계산됩니다." },
        };
    }
}
