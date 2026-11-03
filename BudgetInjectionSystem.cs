using System.Collections.Generic;
using Game;
using Game.City;
using Game.Simulation;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine.Scripting;

namespace RealisticPolicies
{
    /// <summary>One monthly money row of the mod, booked under a vanilla budget source.</summary>
    public readonly struct BudgetRow
    {
        /// <summary>Row id; the budget page shows EconomyPanel.BUDGET_ITEM[id].</summary>
        public readonly string Id;
        public readonly bool Income;

        /// <summary>Vanilla IncomeSource or ExpenseSource the amount is added to.</summary>
        public readonly int Host;

        /// <summary>Money per month (= one in-game day); positive for both income and expense.</summary>
        public readonly int Monthly;

        public BudgetRow(string id, bool income, int host, int monthly)
        {
            Id = id;
            Income = income;
            Host = host;
            Monthly = monthly;
        }
    }

    /// <summary>
    /// Adds the mod's monthly money rows to the vanilla per-source budget arrays, so the vanilla
    /// BudgetApplySystem moves the money and every vanilla total (budget page, toolbar, charts) includes it.
    /// The budget page shows each row separately (see UI module).
    /// </summary>
    public sealed class BudgetInjectionSystem : GameSystemBase
    {
        // Row ids of the first bundle's money flows (also used by the self-test).
        public const string kIncomeRowId = "RealisticPolicies.PollutionTax";
        public const string kExpenseRowId = "RealisticPolicies.GreenSubsidy";

        private readonly Dictionary<string, BudgetRow> m_Rows = new Dictionary<string, BudgetRow>();
        private CityServiceBudgetSystem m_BudgetSystem;
        private NativeArray<int> m_IncomeBySource;
        private NativeArray<int> m_ExpenseBySource;
        private int m_SummedVersion = -1;

        /// <summary>Changes whenever a row is added, changed or removed.</summary>
        public int Version { get; private set; }

        public IEnumerable<BudgetRow> Rows => m_Rows.Values;

        public int RowCount => m_Rows.Count;

        /// <summary>Sets a row's monthly amount; 0 removes it.</summary>
        public void SetRow(string id, bool income, int host, int monthly)
        {
            if (monthly == 0)
            {
                if (m_Rows.Remove(id))
                {
                    Version++;
                }
                return;
            }
            var row = new BudgetRow(id, income, host, monthly);
            if (!m_Rows.TryGetValue(id, out BudgetRow old) || old.Income != income || old.Host != host || old.Monthly != monthly)
            {
                m_Rows[id] = row;
                Version++;
            }
        }

        public int GetRow(string id) => m_Rows.TryGetValue(id, out BudgetRow row) ? row.Monthly : 0;

        public void ClearRows()
        {
            if (m_Rows.Count > 0)
            {
                m_Rows.Clear();
                Version++;
            }
        }

        private struct InjectJob : IJob
        {
            public NativeArray<int> m_Income;
            public NativeArray<int> m_Expenses;
            [ReadOnly] public NativeArray<int> m_AddIncome;
            [ReadOnly] public NativeArray<int> m_AddExpense;

            public void Execute()
            {
                for (int i = 0; i < m_AddIncome.Length; i++)
                {
                    m_Income[i] += m_AddIncome[i];
                }
                for (int i = 0; i < m_AddExpense.Length; i++)
                {
                    m_Expenses[i] += m_AddExpense[i];
                }
            }
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_BudgetSystem = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            m_IncomeBySource = new NativeArray<int>((int)IncomeSource.Count, Allocator.Persistent);
            m_ExpenseBySource = new NativeArray<int>((int)ExpenseSource.Count, Allocator.Persistent);
        }

        [Preserve]
        protected override void OnDestroy()
        {
            Dependency.Complete();
            m_IncomeBySource.Dispose();
            m_ExpenseBySource.Dispose();
            base.OnDestroy();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (m_Rows.Count == 0)
            {
                return;
            }
            if (m_SummedVersion != Version)
            {
                // Rows change a few times a day at most; the last job must finish before the sums are rewritten.
                Dependency.Complete();
                for (int i = 0; i < m_IncomeBySource.Length; i++)
                {
                    m_IncomeBySource[i] = 0;
                }
                for (int i = 0; i < m_ExpenseBySource.Length; i++)
                {
                    m_ExpenseBySource[i] = 0;
                }
                foreach (BudgetRow row in m_Rows.Values)
                {
                    if (row.Income)
                    {
                        m_IncomeBySource[row.Host] += row.Monthly;
                    }
                    else
                    {
                        m_ExpenseBySource[row.Host] += row.Monthly;
                    }
                }
                m_SummedVersion = Version;
            }
            NativeArray<int> income = m_BudgetSystem.GetIncomeArray(out JobHandle incomeDeps);
            NativeArray<int> expenses = m_BudgetSystem.GetExpenseArray(out JobHandle expenseDeps);
            JobHandle handle = new InjectJob
            {
                m_Income = income,
                m_Expenses = expenses,
                m_AddIncome = m_IncomeBySource,
                m_AddExpense = m_ExpenseBySource,
            }.Schedule(JobHandle.CombineDependencies(incomeDeps, expenseDeps, Dependency));
            // Registers our write so BudgetApplySystem and the next array copy wait for it.
            m_BudgetSystem.AddArrayReader(handle);
            Dependency = handle;
        }

        [Preserve]
        public BudgetInjectionSystem()
        {
        }
    }
}
