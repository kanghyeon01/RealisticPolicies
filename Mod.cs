using Colossal.Logging;
using Game;
using Game.Modding;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Simulation;

namespace RealisticPolicies
{
    public sealed class Mod : IMod
    {
        public const string kName = "RealisticPolicies";

        public static readonly ILog Log = LogManager.GetLogger(kName).SetShowsErrorsInUI(false);

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info(nameof(OnLoad));

            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN());
            GameManager.instance.localizationManager.AddSource("ko-KR", new LocaleKO());
            Engine.ParameterLevers.RegisterBuiltIns();

            // Prefabs must exist before any city loads, so registration runs in the main loop (menu included),
            // just before PrefabSystem so the new prefabs are initialized in the same frame.
            updateSystem.UpdateBefore<PolicyRegistrationSystem, PrefabSystem>(SystemUpdatePhase.MainLoop);
            // Right after the vanilla job that rebuilds the per-source budget arrays, before BudgetApplySystem reads them.
            updateSystem.UpdateAfter<BudgetInjectionSystem, CityServiceBudgetSystem>(SystemUpdatePhase.ModificationEnd);
            if (SelfTestSystem.IsRequested)
            {
                // Must be set before the engine is registered: the update interval is read once.
                // Short steps make lags visible within minutes; the formulas use the real elapsed time.
                Engine.PolicyEngineSystem.UpdateIntervalFrames = 512;
                Engine.PolicyEngineSystem.VerboseLog = true;
                // Suits only where the self-test files them, so earlier checks are not disturbed.
                Engine.LitigationSystem.TestDisableRandomFiling = true;
            }
            updateSystem.UpdateAt<Engine.PolicyEngineSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<Engine.LitigationSystem, Engine.PolicyEngineSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<ModUISystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<SelfTestSystem>(SystemUpdatePhase.MainLoop);

            // Info panel sections aren't scheduled; the selected-info system updates the ones it knows about.
            updateSystem.World.GetOrCreateSystemManaged<WelfareSeriesSection>();
        }

        public void OnDispose()
        {
            Log.Info(nameof(OnDispose));
        }
    }
}
