using System.Collections.Generic;
using Colossal.Entities;
using Game.Citizens;
using Game.Notifications;
using Game.Triggers;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace RealisticPolicies.Engine
{
    /// <summary>Prefabs of the protest notifications, registered with the court ones (see <see cref="PolicyRegistrationSystem"/>).</summary>
    public static class ProtestPrefabs
    {
        public const string kProtestIconName = "RealisticPolicies.ProtestIcon";
        public const string kRiotIconName = "RealisticPolicies.RiotIcon";
        public const string kProtestChirpId = "RealisticPolicies.Chirp.Protest";
        public const string kRiotChirpId = "RealisticPolicies.Chirp.Riot";

        public static Entity ProtestIcon = Entity.Null;
        public static Entity RiotIcon = Entity.Null;
        public static Entity ProtestChirp = Entity.Null;
        public static Entity RiotChirp = Entity.Null;

        /// <summary>The gathering spot: the game's spawn location marker, set to let people stand around on the lane.</summary>
        public const string kSpotName = "RealisticPolicies.ProtestSpot";
        public static Entity Spot = Entity.Null;
    }

    /// <summary>
    /// Tells the player about protests with the game's own channels (docs/PLAN.md §12 "화면"): a chirp from a protester when
    /// a protest starts and when it turns into a riot, and a notification icon over its park, or over the road it started on
    /// when no park was near (megaphone for a protest, flame for a riot), while it lasts.
    /// </summary>
    public sealed class ProtestNotifications
    {
        private readonly EntityManager m_EntityManager;
        private readonly IconCommandSystem m_IconCommandSystem;
        private readonly CreateChirpSystem m_CreateChirpSystem;
        private readonly Dictionary<Entity, Entity> m_Shown = new Dictionary<Entity, Entity>();
        private bool m_ClearSaved = true;

        public ProtestNotifications(EntityManager entityManager, IconCommandSystem iconCommandSystem, CreateChirpSystem createChirpSystem)
        {
            m_EntityManager = entityManager;
            m_IconCommandSystem = iconCommandSystem;
            m_CreateChirpSystem = createChirpSystem;
        }

        /// <summary>A city loaded: icons of the previous city are gone, and a save may carry ours; clear them once.</summary>
        public void Reset()
        {
            m_Shown.Clear();
            m_ClearSaved = true;
        }

        public void PostChirp(Entity household, Entity target, bool riot)
        {
            Entity chirp = riot ? ProtestPrefabs.RiotChirp : ProtestPrefabs.ProtestChirp;
            if (chirp == Entity.Null || household == Entity.Null || !m_EntityManager.Exists(household)
                || !m_EntityManager.TryGetBuffer(household, true, out DynamicBuffer<HouseholdCitizen> citizens) || citizens.Length == 0)
            {
                return;
            }
            NativeQueue<ChirpCreationData> queue = m_CreateChirpSystem.GetQueue(out JobHandle deps);
            deps.Complete();
            queue.Enqueue(new ChirpCreationData { m_TriggerPrefab = chirp, m_Sender = citizens[0].m_Citizen, m_Target = target });
            m_CreateChirpSystem.AddQueueWriter(default);
        }

        /// <summary>Asks for each protest's icon over its park or road on every update, and removes the others.</summary>
        public void Refresh(DynamicBuffer<Protest> protests)
        {
            if (ProtestPrefabs.ProtestIcon == Entity.Null && ProtestPrefabs.RiotIcon == Entity.Null)
            {
                return;
            }
            var wanted = new Dictionary<Entity, Entity>();
            for (int i = 0; i < protests.Length; i++)
            {
                Entity owner = protests[i].m_IconOwner;
                Entity icon = protests[i].m_Riot ? ProtestPrefabs.RiotIcon : ProtestPrefabs.ProtestIcon;
                if (owner != Entity.Null && icon != Entity.Null && m_EntityManager.Exists(owner))
                {
                    wanted[owner] = icon;
                }
            }
            IconCommandBuffer buffer = m_IconCommandSystem.CreateCommandBuffer();
            if (m_ClearSaved)
            {
                // A save may carry icons from before: take both kinds off every protest park or road once.
                foreach (Entity owner in wanted.Keys)
                {
                    RemoveBoth(buffer, owner);
                }
                m_Shown.Clear();
                m_ClearSaved = false;
            }
            foreach (KeyValuePair<Entity, Entity> entry in m_Shown)
            {
                if (!wanted.TryGetValue(entry.Key, out Entity icon) || icon != entry.Value)
                {
                    if (m_EntityManager.Exists(entry.Key))
                    {
                        buffer.Remove(entry.Key, entry.Value);
                    }
                }
            }
            // Every update, as the game's own warnings do (ElectricityStatusSystem): adding an icon that is already there
            // changes nothing, and one the game did not put up (seen once on a custom park asset) comes back.
            foreach (KeyValuePair<Entity, Entity> entry in wanted)
            {
                buffer.Add(entry.Key, entry.Value, IconPriority.MajorProblem);
            }
            m_Shown.Clear();
            foreach (KeyValuePair<Entity, Entity> entry in wanted)
            {
                m_Shown[entry.Key] = entry.Value;
            }
        }

        private static void RemoveBoth(IconCommandBuffer buffer, Entity owner)
        {
            if (ProtestPrefabs.ProtestIcon != Entity.Null)
            {
                buffer.Remove(owner, ProtestPrefabs.ProtestIcon);
            }
            if (ProtestPrefabs.RiotIcon != Entity.Null)
            {
                buffer.Remove(owner, ProtestPrefabs.RiotIcon);
            }
        }
    }
}
