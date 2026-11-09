using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;
using PedestrianLane = Game.Net.PedestrianLane;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// Protesters on the street (2026-11-09 user decision: no cap, the protest place looks full; a wide place is split
    /// into several). The first gathering place is the protest's park, where the game seats and walks visitors as it does
    /// for any park visit. Then come the sidewalks of the protest's road segments, each with a spot: the game's spawn
    /// location marker set to let people stand around on the lane, as at parks. The game puts everyone who reaches a spot
    /// at a random place along its sidewalk and lets them mill about there. Places open one after another from where the
    /// protest started, the park to about one person in 4 m² and each sidewalk to about one a metre before the next opens
    /// (at least two places, so people can cross over), so a small crowd stays together and a large one spreads over the
    /// whole protest. As many residents as there are participants walk there from home: first those of the road the
    /// aggrieved live on, then those of the protest roads. The game ends a stay by taking the person into the park or
    /// spot; they then cross to another of the protest's places. When a sidewalk is no longer needed or the protest ends,
    /// its spot goes: anyone inside is put back home and those on the way turn home (the game does that when the place
    /// they head for is gone). Those still in the park when it ends are sent home.
    /// </summary>
    public sealed partial class ProtestSystem
    {
        /// <summary>Sidewalks shorter than this (bits at the ends of a segment) get no spot.</summary>
        private const float kMinSidewalkLength = 8f;

        /// <summary>People a sidewalk holds per metre before the next one opens (looks full when they mill about).</summary>
        private const float kPeoplePerMetre = 1f;

        /// <summary>An open sidewalk stays open until the crowd would fit 25% inside the others (no flapping).</summary>
        private const float kKeepSlack = 1.25f;

        /// <summary>At least both sides of the street (or the park and one sidewalk), so people can cross over when a stay ends.</summary>
        private const int kMinSpots = 2;

        /// <summary>People a park holds per m² of its lot before the sidewalks open (about the sidewalks' one a metre).</summary>
        private const float kParkPeoplePerSquareMetre = 0.25f;

        private EntityQuery m_CitizenLocationQuery;

        /// <summary>Gathering spots and residents on the way to or standing at them, at the last update (self-test, logs).</summary>
        public int LastSpots { get; private set; }

        public int LastMarchers { get; private set; }

        /// <summary>At the last update: people sent on to another spot (stay over, or idle at home), who left, who were sent.</summary>
        public int LastMovedOn { get; private set; }

        public int LastLeft { get; private set; }

        public int LastSent { get; private set; }

        private enum MarcherState { Heading, Inside, Idle, Gone }

        private struct SpotPlace
        {
            public int Protest;
            public Entity Edge;
            public int Order;
            public float Capacity;
            public bool Needed;
        }

        private void CreateGatheringQueries()
        {
            m_CitizenLocationQuery = GetEntityQuery(ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<CurrentBuilding>(), ComponentType.Exclude<Deleted>());
        }

        private void UpdateGatherings(Entity city)
        {
            Entity prefab = ProtestPrefabs.Spot;
            if (prefab == Entity.Null)
            {
                return;
            }
            // Who should be out: the participants of protests still going (none once the demand is met).
            DynamicBuffer<Protest> protests = EntityManager.GetBuffer<Protest>(city, true);
            var participants = new Dictionary<int, int>();
            var parks = new Dictionary<int, Entity>();
            var homes = new Dictionary<int, Entity>();
            for (int p = 0; p < protests.Length; p++)
            {
                Protest protest = protests[p];
                participants[protest.m_Id] = protest.m_Granted ? 0 : math.max(0, protest.m_Participants);
                if (protest.m_Place != Entity.Null && EntityManager.Exists(protest.m_Place) && !EntityManager.HasComponent<Deleted>(protest.m_Place))
                {
                    parks[protest.m_Id] = protest.m_Place;
                }
                if (protest.m_HomeEdge != Entity.Null && EntityManager.Exists(protest.m_HomeEdge))
                {
                    homes[protest.m_Id] = protest.m_HomeEdge;
                }
            }

            // The sidewalks each protest needs, in order from where it started (its park first, then segments in the order it spread).
            var places = new Dictionary<Entity, SpotPlace>();
            var sidewalks = new List<Entity>();
            DynamicBuffer<ProtestEdge> edges = EntityManager.GetBuffer<ProtestEdge>(city, true);
            foreach (KeyValuePair<int, int> entry in participants)
            {
                if (entry.Value <= 0)
                {
                    continue;
                }
                float capacity = 0f;
                int order = 0;
                if (parks.TryGetValue(entry.Key, out Entity park))
                {
                    capacity = ParkCapacity(park);
                    order = 1;
                }
                for (int i = 0; i < edges.Length; i++)
                {
                    ProtestEdge edge = edges[i];
                    if (edge.m_Protest != entry.Key || edge.m_Role != (byte)ProtestEdgeRole.Protest || !EntityManager.Exists(edge.m_Edge))
                    {
                        continue;
                    }
                    sidewalks.Clear();
                    Sidewalks(edge.m_Edge, sidewalks);
                    foreach (Entity lane in sidewalks)
                    {
                        bool keep = capacity < entry.Value * kKeepSlack || order < kMinSpots;
                        if (!keep || places.ContainsKey(lane))
                        {
                            continue;
                        }
                        float holds = EntityManager.GetComponentData<Curve>(lane).m_Length * kPeoplePerMetre;
                        places[lane] = new SpotPlace
                        {
                            Protest = entry.Key,
                            Edge = edge.m_Edge,
                            Order = order,
                            Capacity = holds,
                            Needed = capacity < entry.Value || order < kMinSpots,
                        };
                        capacity += holds;
                        order++;
                    }
                }
            }

            // Spots: those no longer kept go, needed ones that are missing are made.
            DynamicBuffer<ProtestSpot> spots = EntityManager.GetBuffer<ProtestSpot>(city);
            var have = new HashSet<Entity>();
            var released = new HashSet<Entity>();
            for (int i = spots.Length - 1; i >= 0; i--)
            {
                ProtestSpot spot = spots[i];
                if (!EntityManager.Exists(spot.m_Spot) || !places.TryGetValue(spot.m_Lane, out SpotPlace place) || place.Protest != spot.m_Protest)
                {
                    released.Add(spot.m_Spot);
                    spots.RemoveAt(i);
                    continue;
                }
                have.Add(spot.m_Lane);
            }
            if (released.Count > 0)
            {
                ReleaseSpots(city, released);
            }
            spots = EntityManager.GetBuffer<ProtestSpot>(city, true);
            var existing = new List<Entity>();
            for (int i = 0; i < spots.Length; i++)
            {
                existing.Add(spots[i].m_Spot);
            }
            foreach (Entity spot in existing)
            {
                KeepSelfReference(spot);
            }
            foreach (KeyValuePair<Entity, SpotPlace> entry in places)
            {
                if (!entry.Value.Needed || have.Contains(entry.Key))
                {
                    continue;
                }
                Entity spot = CreateSpot(prefab, entry.Key);
                EntityManager.GetBuffer<ProtestSpot>(city).Add(new ProtestSpot { m_Protest = entry.Value.Protest, m_Edge = entry.Value.Edge, m_Lane = entry.Key, m_Spot = spot });
            }

            UpdateMarchers(city, participants, places, parks, homes);
            LastSpots = EntityManager.GetBuffer<ProtestSpot>(city, true).Length;
            LastMarchers = EntityManager.GetBuffer<ProtestMarcher>(city, true).Length;
        }

        /// <summary>People the park holds before its road's sidewalks open: its lot at <see cref="kParkPeoplePerSquareMetre"/>.</summary>
        private float ParkCapacity(Entity park)
        {
            if (!EntityManager.TryGetComponent(park, out PrefabRef prefab) || !EntityManager.TryGetComponent(prefab.m_Prefab, out BuildingData lot))
            {
                return 1f;
            }
            return math.max(1f, lot.m_LotSize.x * lot.m_LotSize.y * kCellSize * kCellSize * kParkPeoplePerSquareMetre);
        }

        /// <summary>Sidewalks of a road segment: its pedestrian lanes other than crossings, long enough to stand on.</summary>
        private void Sidewalks(Entity edge, List<Entity> into)
        {
            if (!EntityManager.TryGetBuffer(edge, true, out DynamicBuffer<SubLane> lanes))
            {
                return;
            }
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_SubLane;
                if (EntityManager.TryGetComponent(lane, out PedestrianLane pedestrian) && (pedestrian.m_Flags & PedestrianLaneFlags.Crosswalk) == 0
                    && EntityManager.TryGetComponent(lane, out Curve curve) && curve.m_Length >= kMinSidewalkLength)
                {
                    into.Add(lane);
                }
            }
        }

        /// <summary>
        /// A spot in the middle of a sidewalk. It lists itself as its own spawn location, so a trip can target it as a
        /// place (the list is not saved and is rebuilt here after loading); "Created" makes the game add it to the paths.
        /// </summary>
        private Entity CreateSpot(Entity prefab, Entity lane)
        {
            Curve curve = EntityManager.GetComponentData<Curve>(lane);
            float3 position = MathUtils.Position(curve.m_Bezier, 0.5f);
            Entity spot = EntityManager.CreateEntity(typeof(PrefabRef), typeof(Game.Objects.Transform), typeof(Game.Objects.SpawnLocation), typeof(SpawnLocationElement), typeof(Created));
            EntityManager.SetComponentData(spot, new PrefabRef(prefab));
            EntityManager.SetComponentData(spot, new Game.Objects.Transform(position, quaternion.identity));
            EntityManager.SetComponentData(spot, new Game.Objects.SpawnLocation
            {
                m_ConnectedLane1 = lane,
                m_CurvePosition1 = 0.5f,
                m_Flags = Game.Objects.SpawnLocationFlags.AllowEnter | Game.Objects.SpawnLocationFlags.AllowExit,
            });
            EntityManager.GetBuffer<SpawnLocationElement>(spot).Add(new SpawnLocationElement(spot, SpawnLocationType.SpawnLocation));
            return spot;
        }

        private void KeepSelfReference(Entity spot)
        {
            if (!EntityManager.Exists(spot))
            {
                return;
            }
            if (!EntityManager.HasBuffer<SpawnLocationElement>(spot))
            {
                EntityManager.AddBuffer<SpawnLocationElement>(spot);
            }
            DynamicBuffer<SpawnLocationElement> list = EntityManager.GetBuffer<SpawnLocationElement>(spot);
            if (list.Length == 0)
            {
                list.Add(new SpawnLocationElement(spot, SpawnLocationType.SpawnLocation));
            }
        }

        /// <summary>
        /// Spots go: everyone inside one (tracked or not) is put back home, trips to them are dropped, and the spots are
        /// deleted the game's way. Those on the way turn home by themselves.
        /// </summary>
        private void ReleaseSpots(Entity city, HashSet<Entity> released)
        {
            using (NativeArray<Entity> citizens = m_CitizenLocationQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<CurrentBuilding> locations = m_CitizenLocationQuery.ToComponentDataArray<CurrentBuilding>(Allocator.Temp))
            {
                for (int i = 0; i < citizens.Length; i++)
                {
                    if (released.Contains(locations[i].m_CurrentBuilding) && Home(citizens[i], out Entity home))
                    {
                        EntityManager.SetComponentData(citizens[i], new CurrentBuilding(home));
                    }
                }
            }
            DynamicBuffer<ProtestMarcher> marchers = EntityManager.GetBuffer<ProtestMarcher>(city);
            for (int i = marchers.Length - 1; i >= 0; i--)
            {
                if (!released.Contains(marchers[i].m_Spot))
                {
                    continue;
                }
                Entity citizen = marchers[i].m_Citizen;
                if (EntityManager.Exists(citizen))
                {
                    RemoveTripsTo(citizen, marchers[i].m_Spot);
                }
                marchers = EntityManager.GetBuffer<ProtestMarcher>(city);
                marchers.RemoveAt(i);
            }
            foreach (Entity spot in released)
            {
                if (EntityManager.Exists(spot) && !EntityManager.HasComponent<Deleted>(spot))
                {
                    EntityManager.AddComponent<Deleted>(spot);
                }
            }
        }

        private void UpdateMarchers(Entity city, Dictionary<int, int> participants, Dictionary<Entity, SpotPlace> places,
            Dictionary<int, Entity> parks, Dictionary<int, Entity> homes)
        {
            // The protest's open spots, in order, with what each holds.
            DynamicBuffer<ProtestSpot> spots = EntityManager.GetBuffer<ProtestSpot>(city, true);
            var spotProtest = new Dictionary<Entity, int>();
            var spotLoad = new Dictionary<Entity, int>();
            var ordered = new Dictionary<int, List<(Entity spot, int order, float capacity)>>();
            for (int i = 0; i < spots.Length; i++)
            {
                ProtestSpot spot = spots[i];
                spotProtest[spot.m_Spot] = spot.m_Protest;
                spotLoad[spot.m_Spot] = 0;
                places.TryGetValue(spot.m_Lane, out SpotPlace place);
                if (!ordered.TryGetValue(spot.m_Protest, out List<(Entity, int, float)> list))
                {
                    list = new List<(Entity, int, float)>();
                    ordered[spot.m_Protest] = list;
                }
                list.Add((spot.m_Spot, place.Order, math.max(1f, place.Capacity)));
            }
            // The park comes first (order 0; the sidewalks start at 1 when there is one).
            foreach (KeyValuePair<int, Entity> park in parks)
            {
                if (!participants.TryGetValue(park.Key, out int wanted) || wanted <= 0)
                {
                    continue;
                }
                spotProtest[park.Value] = park.Key;
                spotLoad[park.Value] = 0;
                if (!ordered.TryGetValue(park.Key, out List<(Entity, int, float)> list))
                {
                    list = new List<(Entity, int, float)>();
                    ordered[park.Key] = list;
                }
                list.Add((park.Value, 0, ParkCapacity(park.Value)));
            }
            foreach (List<(Entity spot, int order, float capacity)> list in ordered.Values)
            {
                list.Sort((a, b) => a.order.CompareTo(b.order));
            }

            var active = new Dictionary<int, int>();
            var marching = new HashSet<Entity>();
            int movedOn = 0, left = 0, sent = 0;
            DynamicBuffer<ProtestMarcher> marchers = EntityManager.GetBuffer<ProtestMarcher>(city);
            for (int i = marchers.Length - 1; i >= 0; i--)
            {
                ProtestMarcher marcher = marchers[i];
                bool open = EntityManager.Exists(marcher.m_Citizen) && spotProtest.ContainsKey(marcher.m_Spot)
                    && participants.TryGetValue(marcher.m_Protest, out int wanted) && wanted > 0;
                MarcherState state = open ? State(marcher.m_Citizen, marcher.m_Spot) : MarcherState.Gone;
                bool atPark = EntityManager.HasComponent<Game.Buildings.Park>(marcher.m_Spot);
                if (atPark && state == MarcherState.Inside && !NothingPlanned(marcher.m_Citizen))
                {
                    // The game already moved them on from the park: let them go.
                    state = MarcherState.Gone;
                }
                else if (atPark && !open && EntityManager.Exists(marcher.m_Citizen))
                {
                    // Over (or the park gone): a trip not yet begun is dropped, and anyone in the park is sent home.
                    RemoveTripsTo(marcher.m_Citizen, marcher.m_Spot);
                    if (EntityManager.TryGetComponent(marcher.m_Citizen, out CurrentBuilding inPark) && inPark.m_CurrentBuilding == marcher.m_Spot
                        && NothingPlanned(marcher.m_Citizen))
                    {
                        SendHome(marcher.m_Citizen);
                    }
                }
                if (state == MarcherState.Inside || state == MarcherState.Idle)
                {
                    // A stay ended (inside the spot): cross to another of the protest's spots. Back home and idle: go again.
                    Entity next = state == MarcherState.Inside ? OtherSpot(ordered[marcher.m_Protest], marcher.m_Spot, spotLoad) : marcher.m_Spot;
                    if (next != Entity.Null && EntityManager.HasBuffer<TripNeeded>(marcher.m_Citizen))
                    {
                        EntityManager.GetBuffer<TripNeeded>(marcher.m_Citizen).Add(new TripNeeded { m_TargetAgent = next, m_Purpose = Purpose.Leisure });
                        marcher.m_Spot = next;
                        marchers = EntityManager.GetBuffer<ProtestMarcher>(city);
                        marchers[i] = marcher;
                        state = MarcherState.Heading;
                        movedOn++;
                    }
                    else if (state == MarcherState.Inside)
                    {
                        SendHome(marcher.m_Citizen);
                    }
                }
                if (state == MarcherState.Heading)
                {
                    active.TryGetValue(marcher.m_Protest, out int n);
                    active[marcher.m_Protest] = n + 1;
                    spotLoad[marcher.m_Spot]++;
                    marching.Add(marcher.m_Citizen);
                    continue;
                }
                left++;
                marchers = EntityManager.GetBuffer<ProtestMarcher>(city);
                marchers.RemoveAt(i);
            }

            // Top up each protest to its participants, from residents at home on the road the aggrieved live on and then on its
            // roads, filling its park and sidewalks in order.
            DynamicBuffer<ProtestEdge> edges = EntityManager.GetBuffer<ProtestEdge>(city, true);
            foreach (KeyValuePair<int, int> entry in participants)
            {
                active.TryGetValue(entry.Key, out int already);
                int need = entry.Value - already;
                if (need <= 0 || !ordered.TryGetValue(entry.Key, out List<(Entity spot, int order, float capacity)> own) || own.Count == 0)
                {
                    continue;
                }
                var candidates = new List<Entity>();
                homes.TryGetValue(entry.Key, out Entity home);
                if (home != Entity.Null)
                {
                    Candidates(home, marching, candidates, need);
                }
                for (int i = 0; i < edges.Length && candidates.Count < need; i++)
                {
                    if (edges[i].m_Protest == entry.Key && edges[i].m_Role == (byte)ProtestEdgeRole.Protest && edges[i].m_Edge != home)
                    {
                        Candidates(edges[i].m_Edge, marching, candidates, need);
                    }
                }
                foreach (Entity citizen in candidates)
                {
                    Entity target = FillingSpot(own, spotLoad);
                    EntityManager.GetBuffer<TripNeeded>(citizen).Add(new TripNeeded { m_TargetAgent = target, m_Purpose = Purpose.Leisure });
                    spotLoad[target]++;
                    marching.Add(citizen);
                    marchers = EntityManager.GetBuffer<ProtestMarcher>(city);
                    marchers.Add(new ProtestMarcher { m_Protest = entry.Key, m_Citizen = citizen, m_Spot = target });
                    sent++;
                }
            }
            LastMovedOn = movedOn;
            LastLeft = left;
            LastSent = sent;
        }

        /// <summary>The first spot (from the start of the protest) not yet full; when all are, the least full.</summary>
        private static Entity FillingSpot(List<(Entity spot, int order, float capacity)> own, Dictionary<Entity, int> load)
        {
            Entity least = own[0].spot;
            float leastFill = float.MaxValue;
            foreach ((Entity spot, int order, float capacity) in own)
            {
                float fill = load[spot] / capacity;
                if (fill < 1f)
                {
                    return spot;
                }
                if (fill < leastFill)
                {
                    leastFill = fill;
                    least = spot;
                }
            }
            return least;
        }

        /// <summary>Another of the protest's spots to cross to: the first not yet full, other than the one just left.</summary>
        private static Entity OtherSpot(List<(Entity spot, int order, float capacity)> own, Entity current, Dictionary<Entity, int> load)
        {
            Entity best = Entity.Null;
            float bestFill = float.MaxValue;
            foreach ((Entity spot, int order, float capacity) in own)
            {
                if (spot == current)
                {
                    continue;
                }
                float fill = load[spot] / capacity;
                if (fill < 1f)
                {
                    return spot;
                }
                if (fill < bestFill)
                {
                    bestFill = fill;
                    best = spot;
                }
            }
            return best;
        }

        /// <summary>Residents of the buildings along a segment who are at home with nothing else planned (not children, not ill).</summary>
        private void Candidates(Entity edge, HashSet<Entity> marching, List<Entity> into, int limit)
        {
            if (!EntityManager.TryGetBuffer(edge, true, out DynamicBuffer<ConnectedBuilding> buildings))
            {
                return;
            }
            for (int b = 0; b < buildings.Length && into.Count < limit; b++)
            {
                Entity building = buildings[b].m_Building;
                if (!EntityManager.TryGetBuffer(building, true, out DynamicBuffer<Renter> renters))
                {
                    continue;
                }
                for (int r = 0; r < renters.Length && into.Count < limit; r++)
                {
                    if (!EntityManager.TryGetBuffer(renters[r].m_Renter, true, out DynamicBuffer<HouseholdCitizen> citizens))
                    {
                        continue;
                    }
                    for (int c = 0; c < citizens.Length && into.Count < limit; c++)
                    {
                        Entity citizen = citizens[c].m_Citizen;
                        if (!marching.Contains(citizen) && CanJoin(citizen, building))
                        {
                            into.Add(citizen);
                        }
                    }
                }
            }
        }

        private bool CanJoin(Entity citizen, Entity home)
        {
            if (!EntityManager.TryGetComponent(citizen, out Citizen data) || data.GetAge() == CitizenAge.Child)
            {
                return false;
            }
            if (!EntityManager.TryGetComponent(citizen, out CurrentBuilding current) || current.m_CurrentBuilding != home)
            {
                return false;
            }
            return Free(citizen);
        }

        private bool NothingPlanned(Entity citizen)
        {
            return EntityManager.TryGetBuffer(citizen, true, out DynamicBuffer<TripNeeded> trips) && trips.Length == 0;
        }

        /// <summary>Not travelling, not busy, not ill, with nothing planned.</summary>
        private bool Free(Entity citizen)
        {
            if (EntityManager.HasComponent<CurrentTransport>(citizen) || EntityManager.HasComponent<TravelPurpose>(citizen)
                || EntityManager.HasComponent<HealthProblem>(citizen) || EntityManager.HasComponent<Target>(citizen))
            {
                return false;
            }
            return EntityManager.TryGetBuffer(citizen, true, out DynamicBuffer<TripNeeded> trips) && trips.Length == 0;
        }

        /// <summary>
        /// Where a protester is: on the way or there (a trip, a path or a walk to the spot), inside the spot (a stay
        /// ended), at home with nothing to do (back from a stay; can go again), or busy with something else (let go).
        /// </summary>
        private MarcherState State(Entity citizen, Entity spot)
        {
            if (EntityManager.TryGetComponent(citizen, out CurrentBuilding current) && current.m_CurrentBuilding == spot)
            {
                return MarcherState.Inside;
            }
            if (EntityManager.TryGetBuffer(citizen, true, out DynamicBuffer<TripNeeded> trips))
            {
                for (int i = 0; i < trips.Length; i++)
                {
                    if (trips[i].m_TargetAgent == spot)
                    {
                        return MarcherState.Heading;
                    }
                }
            }
            if (EntityManager.TryGetComponent(citizen, out Target target) && target.m_Target == spot)
            {
                return MarcherState.Heading;
            }
            if (EntityManager.TryGetComponent(citizen, out CurrentTransport transport) && EntityManager.TryGetComponent(transport.m_CurrentTransport, out Target walking)
                && walking.m_Target == spot)
            {
                return MarcherState.Heading;
            }
            if (Home(citizen, out Entity home) && current.m_CurrentBuilding == home && Free(citizen))
            {
                return MarcherState.Idle;
            }
            return MarcherState.Gone;
        }

        private bool Home(Entity citizen, out Entity home)
        {
            home = Entity.Null;
            if (EntityManager.TryGetComponent(citizen, out HouseholdMember member) && EntityManager.TryGetComponent(member.m_Household, out PropertyRenter renter))
            {
                home = renter.m_Property;
            }
            return home != Entity.Null;
        }

        private void SendHome(Entity citizen)
        {
            if (Home(citizen, out Entity home) && EntityManager.HasBuffer<TripNeeded>(citizen))
            {
                EntityManager.GetBuffer<TripNeeded>(citizen).Add(new TripNeeded { m_TargetAgent = home, m_Purpose = Purpose.GoingHome });
            }
        }

        private void RemoveTripsTo(Entity citizen, Entity spot)
        {
            if (!EntityManager.HasBuffer<TripNeeded>(citizen))
            {
                return;
            }
            DynamicBuffer<TripNeeded> trips = EntityManager.GetBuffer<TripNeeded>(citizen);
            for (int i = trips.Length - 1; i >= 0; i--)
            {
                if (trips[i].m_TargetAgent == spot)
                {
                    trips.RemoveAt(i);
                }
            }
        }
    }
}
