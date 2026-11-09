using Game.Simulation;
using Unity.Entities;

namespace RealisticPolicies
{
    /// <summary>
    /// Getting a city ready for the mod's removal (2026-11-09 user decision, options button after a confirmation): the game
    /// runs no mod code when a mod is taken out, and a save made during a riot then keeps what the mod set in the game's own
    /// data (closed car lanes, police cars kept from other calls, a blank notification icon where the riot icon was; tested
    /// without the mod). The button ends every protest the game's way (lanes open, police cars back to their duties,
    /// gathering spots gone) and takes the protest and lawsuit icons off. Until the next load, no protest starts, no suit
    /// moves and no icon comes back, so the city can be saved clean.
    /// </summary>
    public static class ModCleanup
    {
        /// <summary>The loaded city was cleaned up; the protest and lawsuit systems stay still until the next load.</summary>
        public static bool Done { get; private set; }

        /// <summary>A city was loaded: the mod runs as usual again.</summary>
        public static void Reset()
        {
            Done = false;
        }

        /// <summary>Cleans up the loaded city now (also while paused). Returns false when no city is loaded.</summary>
        public static bool Run()
        {
            World world = World.DefaultGameObjectInjectionWorld;
            Entity city = world?.GetExistingSystemManaged<CitySystem>()?.City ?? Entity.Null;
            if (city == Entity.Null)
            {
                Mod.Log.Info("Clean-up for removal: no city loaded, nothing to do.");
                return false;
            }
            world.GetExistingSystemManaged<Engine.ProtestSystem>()?.CleanUpForRemoval();
            world.GetExistingSystemManaged<Engine.LitigationSystem>()?.CleanUpForRemoval();
            Done = true;
            Mod.Log.Info("Clean-up for removal done: protests ended, roads open, police released, icons removed.");
            return true;
        }
    }
}
