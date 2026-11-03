using System;
using Colossal.UI.Binding;
using Game.City;
using Game.UI;
using UnityEngine.Scripting;

namespace RealisticPolicies
{
    /// <summary>Bindings shared with the UI module.</summary>
    public sealed class ModUISystem : UISystemBase
    {
        public const string kGroup = "RealisticPolicies";

        private BudgetInjectionSystem m_Budget;
        private RawValueBinding m_BudgetRowsBinding;
        private ValueBinding<int> m_SelfTestStageBinding;
        private int m_LastBudgetVersion = -1;

        /// <summary>Raised when the UI module reports a self-test result line.</summary>
        public event Action<string> UiReport;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Budget = World.GetOrCreateSystemManaged<BudgetInjectionSystem>();
            AddBinding(m_BudgetRowsBinding = new RawValueBinding(kGroup, "budgetRows", WriteBudgetRows));
            AddBinding(m_SelfTestStageBinding = new ValueBinding<int>(kGroup, "selfTestStage", 0));
            AddBinding(new TriggerBinding<string>(kGroup, "uiReport", message => UiReport?.Invoke(message)));
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (m_Budget.Version != m_LastBudgetVersion)
            {
                m_LastBudgetVersion = m_Budget.Version;
                m_BudgetRowsBinding.Update();
            }
        }

        public void SetSelfTestStage(int stage)
        {
            m_SelfTestStageBinding.Update(stage);
        }

        private void WriteBudgetRows(IJsonWriter writer)
        {
            writer.ArrayBegin(m_Budget.RowCount);
            foreach (BudgetRow row in m_Budget.Rows)
            {
                // The budget page names sources by their enum names; expense values are negative there.
                string host = row.Income ? ((IncomeSource)row.Host).ToString() : ((ExpenseSource)row.Host).ToString();
                WriteRow(writer, row.Id, row.Income, host, row.Income ? row.Monthly : -row.Monthly);
            }
            writer.ArrayEnd();
        }

        private static void WriteRow(IJsonWriter writer, string id, bool income, string host, int value)
        {
            writer.TypeBegin("RealisticPolicies.BudgetRow");
            writer.PropertyName("id");
            writer.Write(id);
            writer.PropertyName("income");
            writer.Write(income);
            writer.PropertyName("host");
            writer.Write(host);
            writer.PropertyName("value");
            writer.Write(value);
            writer.TypeEnd();
        }

        [Preserve]
        public ModUISystem()
        {
        }
    }
}
