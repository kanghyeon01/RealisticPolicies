using System.Collections.Generic;
using Colossal.Entities;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Notifications;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Game.Triggers;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace RealisticPolicies.Engine
{
    /// <summary>Prefabs the court notifications use, registered in the main menu (see <see cref="PolicyRegistrationSystem"/>).</summary>
    public static class CourtPrefabs
    {
        public const string kIconName = "RealisticPolicies.LawsuitUnanswered";

        /// <summary>Notification icon shown over the city hall while a suit is unanswered; Entity.Null if the icon could not be made.</summary>
        public static Entity Icon = Entity.Null;

        /// <summary>Chirp prefabs by policy id and plaintiff kind.</summary>
        public static readonly Dictionary<string, Entity> Chirps = new Dictionary<string, Entity>();

        /// <summary>Locale id of a suit chirp (the chirp text is EN/KO locale "{id}:0").</summary>
        public static string ChirpId(string policyId, bool companies) => $"RealisticPolicies.Chirp.Suit.{policyId}.{(companies ? "Companies" : "Residents")}";
    }

    /// <summary>
    /// Tells the player about suits with the game's own channels (docs/PLAN.md §7 "알림"): a chirp from the representative
    /// plaintiff when a suit is filed, and a warning icon over the nearest city hall (or welfare office) until the city answers.
    /// </summary>
    public sealed class CourtNotifications
    {
        private readonly EntityManager m_EntityManager;
        private readonly IconCommandSystem m_IconCommandSystem;
        private readonly CreateChirpSystem m_CreateChirpSystem;
        private readonly EntityQuery m_OfficeQuery;
        private readonly HashSet<Entity> m_Shown = new HashSet<Entity>();
        private bool m_ClearSaved = true;

        public CourtNotifications(EntityManager entityManager, IconCommandSystem iconCommandSystem, CreateChirpSystem createChirpSystem, EntityQuery officeQuery)
        {
            m_EntityManager = entityManager;
            m_IconCommandSystem = iconCommandSystem;
            m_CreateChirpSystem = createChirpSystem;
            m_OfficeQuery = officeQuery;
        }

        public static EntityQueryDesc OfficeQueryDesc => new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Transform>() },
            None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
        };

        /// <summary>A city loaded: icons of the previous city are gone, and a save may carry ours; clear them once.</summary>
        public void Reset()
        {
            m_Shown.Clear();
            m_ClearSaved = true;
        }

        /// <summary>City hall nearest the representative plaintiff, or the nearest welfare office if there is no city hall.</summary>
        public Entity PickIconBuilding(Entity representative)
        {
            float3 from = Position(representative, out bool known);
            Entity best = Entity.Null;
            float bestDistance = float.MaxValue;
            bool bestIsHall = false;
            using (NativeArray<Entity> buildings = m_OfficeQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < buildings.Length; i++)
                {
                    Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(buildings[i]).m_Prefab;
                    bool hall = m_EntityManager.HasComponent<AdminBuildingData>(prefab);
                    if (!hall && !m_EntityManager.HasComponent<WelfareOfficeData>(prefab))
                    {
                        continue;
                    }
                    if (bestIsHall && !hall)
                    {
                        continue;
                    }
                    float distance = known ? math.distancesq(from, m_EntityManager.GetComponentData<Transform>(buildings[i]).m_Position) : 0f;
                    if ((hall && !bestIsHall) || distance < bestDistance)
                    {
                        best = buildings[i];
                        bestDistance = distance;
                        bestIsHall = hall;
                    }
                }
            }
            return best;
        }

        /// <summary>Chirp from the representative plaintiff (a resident of the household, or the company itself), linking the city hall.</summary>
        public void PostChirp(string policyId, Lawsuit suit)
        {
            bool companies = suit.m_Group != (byte)TargetGroup.Households;
            if (!CourtPrefabs.Chirps.TryGetValue(CourtPrefabs.ChirpId(policyId, companies), out Entity chirp) || chirp == Entity.Null)
            {
                return;
            }
            Entity sender = suit.m_Representative;
            if (!companies && m_EntityManager.TryGetBuffer(sender, true, out DynamicBuffer<HouseholdCitizen> citizens) && citizens.Length > 0)
            {
                sender = citizens[0].m_Citizen;
            }
            if (sender == Entity.Null || !m_EntityManager.Exists(sender))
            {
                return;
            }
            NativeQueue<ChirpCreationData> queue = m_CreateChirpSystem.GetQueue(out JobHandle deps);
            deps.Complete();
            queue.Enqueue(new ChirpCreationData { m_TriggerPrefab = chirp, m_Sender = sender, m_Target = suit.m_IconBuilding });
            m_CreateChirpSystem.AddQueueWriter(default);
        }

        /// <summary>Shows the icon over the buildings of unanswered suits and removes it everywhere else.</summary>
        public void Refresh(DynamicBuffer<Lawsuit> suits)
        {
            Entity icon = CourtPrefabs.Icon;
            if (icon == Entity.Null)
            {
                return;
            }
            var wanted = new HashSet<Entity>();
            for (int i = 0; i < suits.Length; i++)
            {
                Entity building = suits[i].m_IconBuilding;
                if (suits[i].m_Response == (byte)SuitResponse.None && building != Entity.Null && m_EntityManager.Exists(building))
                {
                    wanted.Add(building);
                }
            }
            bool changed = m_ClearSaved || !wanted.SetEquals(m_Shown);
            if (!changed)
            {
                return;
            }
            IconCommandBuffer buffer = m_IconCommandSystem.CreateCommandBuffer();
            if (m_ClearSaved)
            {
                using (NativeArray<Entity> buildings = m_OfficeQuery.ToEntityArray(Allocator.Temp))
                {
                    for (int i = 0; i < buildings.Length; i++)
                    {
                        Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(buildings[i]).m_Prefab;
                        if (m_EntityManager.HasComponent<AdminBuildingData>(prefab) || m_EntityManager.HasComponent<WelfareOfficeData>(prefab))
                        {
                            buffer.Remove(buildings[i], icon);
                        }
                    }
                }
                m_Shown.Clear();
                m_ClearSaved = false;
            }
            foreach (Entity building in m_Shown)
            {
                if (!wanted.Contains(building) && m_EntityManager.Exists(building))
                {
                    buffer.Remove(building, icon);
                }
            }
            foreach (Entity building in wanted)
            {
                if (!m_Shown.Contains(building))
                {
                    buffer.Add(building, icon, IconPriority.Warning);
                }
            }
            m_IconCommandSystem.AddCommandBufferWriter(default);
            m_Shown.Clear();
            m_Shown.UnionWith(wanted);
        }

        private float3 Position(Entity member, out bool known)
        {
            known = false;
            if (member != Entity.Null && m_EntityManager.Exists(member) && m_EntityManager.TryGetComponent(member, out PropertyRenter renter)
                && m_EntityManager.TryGetComponent(renter.m_Property, out Transform transform))
            {
                known = true;
                return transform.m_Position;
            }
            return default;
        }
    }
}
