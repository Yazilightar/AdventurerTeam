using System;
using System.Collections.Generic;
using Server;
using Server.Items;
using Server.Mobiles;
using Server.Regions;
using Server.Network;
using Server.Accounting;
using Server.Misc;

namespace Server.Scripts.Custom
{
    // Define citizen types using an enum for better readability
    public enum CitizenClass
    {
        Wizard = 1,
        Fighter = 2,
        Rogue = 3
    }

    [CorpseName("an adventurer corpse")]
    public class AdventurerTeam : BaseCreature
    {
        // High-Performance Global Team Index (Optimized for O(1) Team Lookup)
        // Key: TeamId (int), Value: List of all active members in that team
        public static readonly Dictionary<int, List<AdventurerTeam>> AllTeams = new Dictionary<int, List<AdventurerTeam>>(512);
        
        // Static lock object to protect AllTeams from race conditions during write operations
        internal static readonly object AllTeamsLock = new object();

        // Special combat shout lines when using ultimate ability
        private static readonly string[] SpecialLines = new string[]
        {
            "You will regret this!", "Feel my wrath!", "This is my last stand!",
            "I won't go down easily!", "Now you face my true power!"
        };

        // Configuration constants
        private const double LowHealthThreshold = 0.30;       // Trigger special ability below 30% HP
        private const int TeamMemberRange = 15;            // Range for team coordination
        private const int ThinkInterval = 33;              // OnThink runs every ~6-7 seconds (33 * 200ms)
        private const int SkillBase = 28;
        private const int SkillPerLevel = 7;

        // Special ability values
        private const int WizMin = 20, WizMax = 30;
        private const int WizPhys = 50, WizFire = 50;
        private const int RogueMin = 15, RogueMax = 25;
        private const int FighterHeal = 20;

        // Instance fields
        private int m_CitizenType;       // Stores the CitizenClass value as int
        private int m_CitizenLevel;      // Level 1-9
        private bool m_SpawnedBySystem;
        private bool m_IsEvil;
        private bool m_SpecialUsed;
        private int m_TeamId;
        private DateTime m_LastSeen;
        private int m_ThinkCounter = 0;

        [CommandProperty(AccessLevel.Owner)]
        public CitizenClass CitizenClass { get { return (CitizenClass)m_CitizenType; } set { m_CitizenType = (int)value; InvalidateProperties(); } }

        [CommandProperty(AccessLevel.Owner)]
        public int CitizenLevel { get { return m_CitizenLevel; } set { m_CitizenLevel = value; InvalidateProperties(); } }

        [CommandProperty(AccessLevel.GameMaster)]
        public bool SpawnedBySystem { get { return m_SpawnedBySystem; } set { m_SpawnedBySystem = value; } }

        [CommandProperty(AccessLevel.GameMaster)]
        public int TeamId { get { return m_TeamId; } set { m_TeamId = value; } }

        [CommandProperty(AccessLevel.GameMaster)]
        public DateTime LastSeen { get { return m_LastSeen; } set { m_LastSeen = value; } }

        [CommandProperty(AccessLevel.GameMaster)]
        public bool IsEvil { get { return m_IsEvil; } set { m_IsEvil = value; } }

        private bool IsTeamLeader
        {
            // O(M) operation (M = team size) using the optimized dictionary lookup
            get { return m_TeamId != 0 && GetTeamLeader() == this; }
        }

        [Constructable]
        public AdventurerTeam() : this(0, false) { }

        [Constructable]
        public AdventurerTeam(int teamId, bool isEvil) : base(AIType.AI_Melee, FightMode.None, 10, 1, 0.2, 0.4)
        {
            m_TeamId = teamId;
            m_IsEvil = isEvil;
            m_SpawnedBySystem = (teamId != 0);
            m_SpecialUsed = false;

            // PERFORMANCE & SAFETY OPTIMIZATION: Use lock and TryGetValue for atomic operation
            lock (AllTeamsLock) 
            {
                if (m_TeamId != 0)
                {
                    List<AdventurerTeam> list;
                    
                    // Attempt to get the list (O(1) lookup)
                    if (!AllTeams.TryGetValue(m_TeamId, out list))
                    {
                        // If key does not exist, create and add it
                        list = new List<AdventurerTeam>();
                        AllTeams[m_TeamId] = list; 
                    }
                    
                    // Add the new mobile to the team list
                    list.Add(this);
                }
            }

            InitStatsAndAppearance();
        }

        private void InitStatsAndAppearance()
        {
            Female = Utility.RandomBool();
            Body = Female ? 401 : 400;
            Name = Female ? NameList.RandomName("female") : NameList.RandomName("male");

            if (!Female)
                FacialHairItemID = Utility.RandomList(0, 8254, 8255, 8256, 8257, 8267, 8268, 8269);

            m_CitizenLevel = Utility.RandomMinMax(1, 9);
            Fame = 2500 * m_CitizenLevel;
            Karma = m_IsEvil ? -Fame : Fame;
            VirtualArmor = m_CitizenLevel * 10;
            SetDamage(m_CitizenLevel * 2, m_CitizenLevel * 3);

            if (m_IsEvil)
            {
                Title = TavernPatrons.GetEvilTitle();
                Hue = Utility.RandomList(0x995, 0x8A4, 0x8B0, 0x8AC);
                FightMode = FightMode.Good;
            }
            else
            {
                Title = TavernPatrons.GetTitle();
                Hue = Utility.RandomSkinHue();
                FightMode = FightMode.Evil;
            }

            Utility.AssignRandomHair(this);
            SpeechHue = Utility.RandomTalkHue();
            HairHue = FacialHairHue = Utility.RandomHairHue();

            int type = Utility.Random(3);
            int baseSkill = SkillBase + (m_CitizenLevel * SkillPerLevel);

            switch (type)
            {
                case 0: // Wizard
                    IntelligentAction.DressUpWizards(this, m_IsEvil);
                    m_CitizenType = (int)CitizenClass.Wizard;
                    AI = AIType.AI_Mage;
                    SetSkill(SkillName.Psychology, baseSkill);
                    SetSkill(SkillName.Magery, baseSkill);
                    SetSkill(SkillName.Meditation, baseSkill);
                    break;

                case 1: // Fighter
                    IntelligentAction.DressUpFighters(this, "", m_IsEvil, false, true);
                    m_CitizenType = (int)CitizenClass.Fighter;
                    AI = AIType.AI_Melee;
                    SetSkill(SkillName.Fencing, baseSkill);
                    SetSkill(SkillName.Bludgeoning, baseSkill);
                    SetSkill(SkillName.Swords, baseSkill);
                    SetSkill(SkillName.Parry, baseSkill);
                    break;

                case 2: // Archer/Rogue
                    IntelligentAction.DressUpRogues(this, "", m_IsEvil, false, true);
                    m_CitizenType = (int)CitizenClass.Rogue;
                    AI = AIType.AI_Archer;
                    SetSkill(SkillName.Marksmanship, baseSkill);
                    SetSkill(SkillName.Tactics, baseSkill);
                    break;
            }

            SetStr(m_CitizenLevel * 70, m_CitizenLevel * 110);
            SetDex(m_CitizenLevel * 70, m_CitizenLevel * 110);
            SetInt(m_CitizenLevel * 70, m_CitizenLevel * 130);
            SetHits(m_CitizenLevel * 100, m_CitizenLevel * 140);

            SetResistance(ResistanceType.Physical, m_CitizenLevel * 4, m_CitizenLevel * 7);
            SetResistance(ResistanceType.Fire,      m_CitizenLevel * 4, m_CitizenLevel * 7);
            SetResistance(ResistanceType.Cold,      m_CitizenLevel * 4, m_CitizenLevel * 7);
            SetResistance(ResistanceType.Poison,    m_CitizenLevel * 4, m_CitizenLevel * 7);
            SetResistance(ResistanceType.Energy,    m_CitizenLevel * 4, m_CitizenLevel * 7);

            // Set general skills (MagicResist, Tactics, FistFighting)
            SetSkill(SkillName.MagicResist, baseSkill);
            SetSkill(SkillName.Tactics, baseSkill);
            SetSkill(SkillName.FistFighting, baseSkill);

            AddRangeWeapon();
            m_LastSeen = DateTime.UtcNow;
        }

        public override void OnThink()
        {
            base.OnThink();

            // Only run the custom logic every ThinkInterval (~6-7 seconds)
            if (++m_ThinkCounter % ThinkInterval != 0)
                return;

            if (IsPlayerNearby())
                m_LastSeen = DateTime.UtcNow;

            // Special Ability Logic (Ultimate)
            if (!m_SpecialUsed && Hits < HitsMax * LowHealthThreshold)
            {
                m_SpecialUsed = true;
                PublicOverheadMessage(MessageType.Regular, 0x3B2, true, SpecialLines[Utility.Random(SpecialLines.Length)]);

                switch ((CitizenClass)m_CitizenType)
                {
                    case CitizenClass.Wizard:
                        PublicOverheadMessage(MessageType.Emote, 0x3B2, true, "*unleashes forbidden magic!*");
                        if (Combatant != null)
                            AOS.Damage(Combatant, this, Utility.RandomMinMax(WizMin, WizMax), WizPhys, WizFire, 0, 0, 0);
                        break;

                    case CitizenClass.Fighter:
                        PublicOverheadMessage(MessageType.Emote, 0x3B2, true, "*lets out a furious roar!*");
                        // Instantly restore a portion of health
                        Hits += FighterHeal;
                        break;

                    case CitizenClass.Rogue:
                        PublicOverheadMessage(MessageType.Emote, 0x3B2, true, "*fires a powerful arrow!*");
                        if (Combatant != null)
                            // Pure physical damage ability
                            AOS.Damage(Combatant, this, Utility.RandomMinMax(RogueMin, RogueMax), 0, 100, 0, 0, 0);
                        break;
                }
            }

            // Team Coordination Logic (Only Leader actively pulls target)
            if (m_TeamId == 0 || !IsTeamLeader || Combatant == null)
                return;

            // Search only nearby mobiles
            IPooledEnumerable eable = GetMobilesInRange(TeamMemberRange);
            foreach (Mobile m in eable)
            {
                if (m is AdventurerTeam)
                {
                    AdventurerTeam at = (AdventurerTeam)m;
                    // Check if it's a team member, not the leader, and currently has no target
                    if (at.m_TeamId == m_TeamId && at != this && at.Combatant == null)
                        at.Combatant = Combatant; // Sync target
                }
            }
            eable.Free();
        }

        private bool IsPlayerNearby()
        {
            // Efficiently check if any player is in range
            IPooledEnumerable eable = Map.GetMobilesInRange(Location, TeamMemberRange);
            foreach (Mobile m in eable)
            {
                if (m is PlayerMobile && m.AccessLevel == AccessLevel.Player)
                {
                    eable.Free();
                    return true;
                }
            }
            eable.Free();
            return false;
        }

        // PERFORMANCE OPTIMIZATION: O(M) lookup using cached team list (M = team size)
        private AdventurerTeam GetTeamLeader()
        {
            if (m_TeamId == 0 || !AllTeams.ContainsKey(m_TeamId))
                return null;

            AdventurerTeam leader = null;
            // No lock needed as we are only reading the list reference.
            List<AdventurerTeam> teamList = AllTeams[m_TeamId];

            foreach (AdventurerTeam at in teamList)
            {
                // Serial is a good way to determine a unique leader within a dynamic group
                if (at.Map == Map && (leader == null || at.Serial < leader.Serial))
                    leader = at;
            }
            return leader;
        }

        public void AddRangeWeapon()
        {
            // Remove existing weapons
            BaseWeapon hand = FindItemOnLayer(Layer.OneHanded) as BaseWeapon;
            if (hand != null)
                hand.Delete();

            BaseWeapon twohand = FindItemOnLayer(Layer.TwoHanded) as BaseWeapon;
            if (twohand != null)
                twohand.Delete();

            // 50% chance for throwing weapons (for wizards and archers/rogues)
            if (Utility.RandomBool() && (m_CitizenType == (int)CitizenClass.Wizard || m_CitizenType == (int)CitizenClass.Rogue))
            {
                // NOTE: Assuming ThrowingGloves and ThrowingWeapon are custom BaseWeapon/BaseItem derivatives available in your scripts
                // Placeholder classes REMOVED. Ensure these classes exist in your project:
                // ThrowingGloves (BaseEquippableItem), ThrowingWeapon (BaseItem)
                ThrowingGloves glove = new ThrowingGloves();
                ThrowingWeapon ammo = new ThrowingWeapon(Utility.RandomMinMax(15, 30));

                switch (Utility.Random(5))
                {
                    case 0: // Throwing Stones
                        // We must assume the custom classes have these properties/methods
                        // If they don't, you must uncomment and use the placeholder classes from the previous version, 
                        // and declare them OUTSIDE of this method (e.g., in the same file but outside the AdventurerTeam class).
                        glove.GloveType = "Stones";  
                        ammo.ammo = "Throwing Stones";   
                        ammo.ItemID = 0x10B6; 
                        ammo.Name = "throwing stone"; 
                        break;
                    case 1: // Throwing Axes
                        glove.GloveType = "Axes";    
                        ammo.ammo = "Throwing Axes";     
                        ammo.ItemID = 0x10B3; 
                        ammo.Name = "throwing axe"; 
                        break;
                    case 2: // Throwing Daggers
                        glove.GloveType = "Daggers"; 
                        ammo.ammo = "Throwing Daggers";  
                        ammo.ItemID = 0x10B7; 
                        ammo.Name = "throwing dagger"; 
                        break;
                    case 3: // Throwing Darts
                        glove.GloveType = "Darts";   
                        ammo.ammo = "Throwing Darts";    
                        ammo.ItemID = 0x10B5; 
                        ammo.Name = "throwing dart"; 
                        break;
                    case 4: // Throwing Stars
                        glove.GloveType = "Stars";   
                        ammo.ammo = "Throwing Stars";    
                        ammo.ItemID = 0x10B2; 
                        ammo.Name = "throwing star"; 
                        break;
                }

                AddItem(glove);
                PackItem(ammo);
            }
            else if (m_CitizenType == (int)CitizenClass.Wizard) // Wizard weapons
            {
                // NOTE: Assuming WizardStaff, WizardStick, and MageEye are custom BaseWeapon/BaseItem derivatives available
                // Placeholder classes REMOVED. Ensure these classes exist in your project.
                if (Utility.RandomBool())
                    AddItem(new WizardStaff());
                else
                    AddItem(new WizardStick());

                PackItem(new MageEye(Utility.RandomMinMax(15, 30)));
            }
            else // Archer weapons (default for Rogue if throwing failed or Fighter if no default melee weapon was dressed)
            {
                // NOTE: Assuming all items below are standard or custom BaseRanged weapons available
                // Placeholder class REMOVED. Ensure these classes exist in your project.
                switch (Utility.Random(8))
                {
                    case 0: AddItem(new Bow());          PackItem(new Arrow(Utility.RandomMinMax(20, 40))); break;
                    case 1: AddItem(new Crossbow());         PackItem(new Bolt(Utility.RandomMinMax(20, 40))); break;
                    case 2: AddItem(new HeavyCrossbow());       PackItem(new Bolt(Utility.RandomMinMax(20, 40))); break;
                    case 3: AddItem(new RepeatingCrossbow());   PackItem(new Bolt(Utility.RandomMinMax(20, 40))); break;
                    case 4: AddItem(new CompositeBow());      PackItem(new Arrow(Utility.RandomMinMax(20, 40))); break;
                    case 5: AddItem(new MagicalShortbow());     PackItem(new Arrow(Utility.RandomMinMax(20, 40))); break;
                    case 6: AddItem(new ElvenCompositeLongbow()); PackItem(new Arrow(Utility.RandomMinMax(20, 40))); break;
                    case 7: AddItem(new Harpoon());           PackItem(new HarpoonRope(Utility.RandomMinMax(15, 30))); break;
                }
            }
        }

        public override void GenerateLoot()
        {
            // Loot logic using fall-through cases
            switch (m_CitizenLevel)
            {
                case 9: AddLoot(LootPack.FilthyRich); goto case 7;
                case 8: case 7: AddLoot(LootPack.Rich); goto case 5;
                case 6: case 5: AddLoot(LootPack.Average); goto case 3;
                case 4: case 3: AddLoot(LootPack.Meager); break;
                default: AddLoot(LootPack.Meager); break;
            }

            // Rare item logic...
            if (Utility.Random(25) == 0 && Loot.AdventurerRareItemTypes != null && Loot.AdventurerRareItemTypes.Length > 0)
            {
                Type t = Loot.AdventurerRareItemTypes[Utility.Random(Loot.AdventurerRareItemTypes.Length)];
                Item rare = Loot.Construct(t);
                if (rare != null)
                    PackItem(rare);
            }

            if (m_CitizenType == (int)CitizenClass.Wizard)
                AddLoot(LootPack.MedScrolls, (m_CitizenLevel / 3) + 1);
        }

        public override void OnDelete()
        {
            // PERFORMANCE & SAFETY OPTIMIZATION: Remove from the Dictionary and cleanup empty list
            lock (AllTeamsLock)
            {
                if (m_TeamId != 0 && AllTeams.ContainsKey(m_TeamId))
                {
                    List<AdventurerTeam> list = AllTeams[m_TeamId];
                    list.Remove(this);

                    // Clean up the dictionary key if the team list is empty
                    if (list.Count == 0)
                        AllTeams.Remove(m_TeamId);
                }
            }
            
            // Recycle the team ID if it was system-spawned
            if (m_SpawnedBySystem && m_TeamId != 0)
                AutoTeamMaintainer.RecycleTeamId(m_TeamId);
            
            base.OnDelete();
        }

        public override void OnAfterDelete()
        {
            m_SpecialUsed = false; 
            base.OnAfterDelete();
        }

        public AdventurerTeam(Serial serial) : base(serial) { }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write(2); // Version
            writer.Write(m_CitizenType);
            writer.Write(m_CitizenLevel);
            writer.Write(m_SpawnedBySystem);
            writer.Write(m_TeamId);
            writer.Write(m_LastSeen);
            writer.Write(m_IsEvil);
            writer.Write(m_SpecialUsed);
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);
            int version = reader.ReadInt();

            m_CitizenType = reader.ReadInt();
            m_CitizenLevel = reader.ReadInt();
            m_SpawnedBySystem = reader.ReadBool();
            m_TeamId = reader.ReadInt();
            m_LastSeen = reader.ReadDateTime();
            m_IsEvil = reader.ReadBool();
            m_SpecialUsed = version >= 2 ? reader.ReadBool() : false;

            // PERFORMANCE & SAFETY OPTIMIZATION: Add to AllTeams Dictionary on deserialization
            lock (AllTeamsLock)
            {
                if (m_TeamId != 0)
                {
                    List<AdventurerTeam> list;

                    if (!AllTeams.TryGetValue(m_TeamId, out list))
                    {
                        list = new List<AdventurerTeam>();
                        AllTeams[m_TeamId] = list; 
                    }
                    
                    // Add the mobile to the list if it's not already present 
                    if (!list.Contains(this))
                        list.Add(this);
                }
            }
        }
    }

    // Automatic adventurer team spawner and manager
    public class AutoTeamMaintainer
    {
        private const int PlayerGroupRadius = 50;
        private const int MinSpawnDist = 18;
        private const int SearchAttempts = 20;
        private const double SpawnChance = 0.50;
        private const double EvilChance = 0.30;
        private const int MinTeamSize = 3, MaxTeamSize = 5;
        private const int MaxTeamsPerGroup = 2;

        private const int MinTeamId = 100, MaxTeamId = 9999;
        // Array to quickly check if an ID is active
        private static readonly bool[] TeamIdActive = new bool[MaxTeamId - MinTeamId + 1];
        // List for recycled IDs to minimize array search time
        private static readonly List<int> RecycledIds = new List<int>(512);

        private static readonly Dictionary<Account, DateTime> LastSpawnTime = new Dictionary<Account, DateTime>();
        private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
        private static readonly object _lock = new object(); // Lock for TeamID management and LastSpawnTime dictionary

        public static void Initialize()
        {
            // Start periodic timers for maintenance
            Timer.DelayCall(CheckInterval, CheckInterval, new TimerCallback(MaintainTeams));
            Timer.DelayCall(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), new TimerCallback(CleanupInactive));
        }

        private static void MaintainTeams()
        {
            List<PlayerMobile> reps = GetPlayerGroups();
            int totalTeamsSpawned = 0; // Tracks the total number of teams spawned in this cycle

            foreach (PlayerMobile pm in reps)
            {
                if (pm.Account == null) continue;
                Account acc = (Account)pm.Account;

                lock (_lock) // Lock protects LastSpawnTime dictionary
                {
                    // Check spawn cooldown per account
                    if (LastSpawnTime.ContainsKey(acc) && DateTime.UtcNow - LastSpawnTime[acc] < Cooldown)
                        continue;
                }

                if (Utility.RandomDouble() >= SpawnChance) continue;

                Map map = pm.Map; // Get map variable
                if (map == null) continue;

                int teams = Utility.RandomMinMax(1, MaxTeamsPerGroup);
                for (int t = 0; t < teams; t++)
                {
                    Point3D loc = FindSpawnLocation(pm);
                    if (loc == Point3D.Zero) continue;

                    bool evil = Utility.RandomDouble() < EvilChance;
                    int teamId = GenerateTeamId();
                    if (teamId == 0) continue;

                    int size = Utility.RandomMinMax(MinTeamSize, MaxTeamSize);
                    string teamType = evil ? "evil" : "friendly"; // Team type string
                    int membersSpawned = 0; // Tracks the number of members actually spawned

                    for (int i = 0; i < size; i++)
                    {
                        Point3D p = new Point3D(
                            loc.X + Utility.RandomMinMax(-3, 3),
                            loc.Y + Utility.RandomMinMax(-3, 3),
                            map.GetAverageZ(loc.X, loc.Y));

                        if (map.CanSpawnMobile(p.X, p.Y, p.Z))
                        {
                            AdventurerTeam member = new AdventurerTeam(teamId, evil);
                            member.MoveToWorld(p, map);
                            membersSpawned++; // Update member count
                        }
                    }

                    if (membersSpawned > 0)
                    {
                        totalTeamsSpawned++;
                        // CONSOLE OUTPUT (Requested by user) - using string.Format for compatibility
                        Console.WriteLine(string.Format("[AdventurerTeam Spawn] Spawned a new {0} adventurer team (ID:{1}, {2} members) for player {3} at {4} in {5}.",
                            teamType, teamId, membersSpawned, pm.Name, loc, map.Name));
                    }
                    else
                    {
                         // If no members were spawned, recycle the Team ID
                         RecycleTeamId(teamId);
                    }

                    lock (_lock) LastSpawnTime[acc] = DateTime.UtcNow;
                }
            }
            
            // Optional: Summary output
            if (totalTeamsSpawned > 0)
            {
                Console.WriteLine(string.Format("--- [AdventurerTeam Spawn Summary] Total teams this cycle: {0}. ---", totalTeamsSpawned));
            }
        }

        // PERFORMANCE OPTIMIZATION: Uses Dictionary.ContainsKey to achieve O(1) player lookup (compatible with .NET 2.0)
        private static List<PlayerMobile> GetPlayerGroups()
        {
            List<PlayerMobile> reps = new List<PlayerMobile>();
            // Using Dictionary to mimic HashSet for O(1) performance in older .NET versions
            Dictionary<PlayerMobile, bool> processed = new Dictionary<PlayerMobile, bool>(); 

            foreach (NetState ns in NetState.Instances)
            {
                PlayerMobile pm = ns.Mobile as PlayerMobile;
                
                // Check if player is valid, outside guarded region, and not yet processed
                if (pm != null && pm.AccessLevel == AccessLevel.Player && !(pm.Region is GuardedRegion) && processed.ContainsKey(pm) == false)
                {
                    reps.Add(pm);
                    processed.Add(pm, true); // Mark as processed (O(1))

                    IPooledEnumerable eable = pm.GetMobilesInRange(PlayerGroupRadius);
                    foreach (Mobile m in eable)
                    {
                        PlayerMobile p = m as PlayerMobile;
                        // Mark nearby players as processed to avoid double-counting groups (O(1))
                        if (p != null && processed.ContainsKey(p) == false)
                            processed.Add(p, true);
                    }
                    eable.Free();
                }
            }
            return reps;
        }

        private static Point3D FindSpawnLocation(PlayerMobile pm)
        {
            Map map = pm.Map;
            if (map == null) return Point3D.Zero;

            for (int i = 0; i < SearchAttempts; i++)
            {
                int x = pm.X + Utility.RandomMinMax(-30, 30);
                int y = pm.Y + Utility.RandomMinMax(-30, 30);
                
                // Check minimum distance from player
                int dx = x - pm.X;
                int dy = y - pm.Y;
                if (dx * dx + dy * dy < MinSpawnDist * MinSpawnDist) continue;

                int z = map.GetAverageZ(x, y);
                Point3D p = new Point3D(x, y, z);

                // Check if location is fit for spawning and not in a guarded area
                if (map.CanFit(x, y, z, 16, false, false, true) && !(Region.Find(p, map) is GuardedRegion))
                    return p;
            }
            return Point3D.Zero;
        }

        private static int GenerateTeamId()
        {
            lock (_lock) // Protects TeamIdActive array and RecycledIds list
            {
                // 1. Check recycled IDs first (O(1) pop from list)
                if (RecycledIds.Count > 0)
                {
                    int id = RecycledIds[RecycledIds.Count - 1];
                    RecycledIds.RemoveAt(RecycledIds.Count - 1);
                    TeamIdActive[id - MinTeamId] = true;
                    return id;
                }

                // 2. Search array for the first inactive ID (O(N) worst case)
                for (int i = 0; i < TeamIdActive.Length; i++)
                {
                    if (!TeamIdActive[i])
                    {
                        TeamIdActive[i] = true;
                        return MinTeamId + i;
                    }
                }
                return 0; // No available IDs
            }
        }

        public static void RecycleTeamId(int id)
        {
            if (id < MinTeamId || id > MaxTeamId) return;
            lock (_lock) // Protects TeamIdActive array and RecycledIds list
            {
                int idx = id - MinTeamId;
                if (idx < TeamIdActive.Length && TeamIdActive[idx])
                {
                    TeamIdActive[idx] = false;
                    RecycledIds.Add(id);
                }
            }
        }

        // OPTIMIZATION: Creates a snapshot of AllTeams for safe iteration, ensuring no InvalidOperationException
        private static void CleanupInactive()
        {
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            int deleted = 0;

            // Create a copy of AllTeams for safe iteration
            Dictionary<int, List<AdventurerTeam>> snapshot;
            
            // Lock the original dictionary while creating the copy
            lock (AdventurerTeam.AllTeamsLock)
            {
                snapshot = new Dictionary<int, List<AdventurerTeam>>(AdventurerTeam.AllTeams);
            }
            
            // Iterate over the safe snapshot
            foreach (KeyValuePair<int, List<AdventurerTeam>> pair in snapshot)
            {
                // Iterate backwards over the List<AdventurerTeam>
                for (int i = pair.Value.Count - 1; i >= 0; i--)
                {
                    AdventurerTeam at = pair.Value[i];
                    if (at.SpawnedBySystem && at.LastSeen < cutoff)
                    {
                        at.Delete(); // OnDelete handles the cleanup from the original live dictionary
                        deleted++;
                    }
                }
            }
            
            Console.WriteLine("[AdventurerTeam] Cleaned up " + deleted + " inactive adventurer teams.");
        }
    }
}
