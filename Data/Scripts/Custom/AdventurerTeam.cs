using System;
using System.Collections.Generic;
using Server;
using Server.Items;
using Server.Mobiles;
using Server.Commands;
using Server.Regions;
using Server.Network;
using Server.Accounting;
using Server.Misc;

namespace Server.Scripts.Custom
{
    // =========================================================================
    // SECTION 1: AdventurerTeam NPC Definition
    // =========================================================================
    // This class defines an adventurer NPC that can be part of a team.
    // Teams are managed via TeamId and share combatants for coordinated attacks.
    // Special abilities trigger at low health, and they update LastSeen when players are nearby.
    // Optimizations: Reduced OnThink frequency for team/player checks to lower CPU/GC pressure.
    // =========================================================================

    [CorpseName("an adventurer corpse")]
    public class AdventurerTeam : BaseCreature
    {
        // =====================================================================
        // STATIC FIELDS
        // =====================================================================
        // AllTeams: Global registry of all AdventurerTeam instances for team management
        private static readonly List<AdventurerTeam> AllTeams = new List<AdventurerTeam>();

        // SpecialLines: Random battle cry messages when triggering special ability
        private static readonly string[] SpecialLines = new string[]
        {
            "You will regret this!",
            "Feel my wrath!",
            "This is my last stand!",
            "I won't go down easily!",
            "Now you face my true power!"
        };

        // =====================================================================
        // CONSTANTS - Adjust these values to balance gameplay
        // =====================================================================
        private const double LowHealthThreshold = 0.3;      // Special ability triggers at 30% HP
        private const int TeamMemberRange = 15;              // Range (tiles) for team coordination & player detection
        private const int SkillBaseValue = 28;               // Base skill value for all citizen types
        private const int SkillLevelMultiplier = 7;          // Skill bonus per citizen level
        private const int ThinkCheckInterval = 8;            // OnThink throttle: process every 8 ticks (~2 sec)
        private const int WizardDamageMin = 20;              // Wizard special: minimum damage
        private const int WizardDamageMax = 30;              // Wizard special: maximum damage
        private const int WizardDamagePhys = 50;             // Wizard special: physical damage %
        private const int WizardDamageFire = 50;             // Wizard special: fire damage %
        private const int RogueDamageMin = 15;               // Rogue special: minimum damage
        private const int RogueDamageMax = 25;               // Rogue special: maximum damage
        private const int FighterHealAmount = 20;            // Fighter special: HP heal amount

        // =====================================================================
        // INSTANCE FIELDS
        // =====================================================================
        private int m_CitizenType;          // 1=Wizard, 2=Fighter, 3=Rogue/Archer
        private int m_CitizenLevel;         // Level 1-9, affects stats and loot
        private bool m_SpawnedBySystem;     // True if spawned by AutoTeamMaintainer
        private bool m_IsEvil;              // True=attacks good, False=attacks evil
        private bool m_SpecialUsed;         // Prevents multiple special ability triggers
        private int m_TeamId;               // Team identifier for coordinated combat
        private DateTime m_LastSeen;        // Last time a player was nearby (for cleanup)

        // Performance optimization fields
        private int m_ThinkCounter = 0;             // Counter for OnThink throttling
        private List<Mobile> m_MobilesCache;        // Reusable list to reduce GC pressure

        // =====================================================================
        // PROPERTIES - Exposed for GM commands and serialization
        // =====================================================================
        [CommandProperty(AccessLevel.Owner)]
        public int CitizenType { get { return m_CitizenType; } set { m_CitizenType = value; InvalidateProperties(); } }

        [CommandProperty(AccessLevel.Owner)]
        public int CitizenLevel { get { return m_CitizenLevel; } set { m_CitizenLevel = value; InvalidateProperties(); } }

        [CommandProperty(AccessLevel.GameMaster)]
        public bool SpawnedBySystem { get { return m_SpawnedBySystem; } set { m_SpawnedBySystem = value; InvalidateProperties(); } }

        [CommandProperty(AccessLevel.GameMaster)]
        public int TeamId { get { return m_TeamId; } set { m_TeamId = value; } }

        [CommandProperty(AccessLevel.GameMaster)]
        public DateTime LastSeen { get { return m_LastSeen; } set { m_LastSeen = value; } }

        [CommandProperty(AccessLevel.GameMaster)]
        public bool IsEvil { get { return m_IsEvil; } set { m_IsEvil = value; } }

        // =====================================================================
        // STATIC METHODS
        // =====================================================================
        /// <summary>
        /// Returns a read-only list of all registered AdventurerTeam instances.
        /// Used by AutoTeamMaintainer for cleanup operations.
        /// </summary>
        public static IReadOnlyList<AdventurerTeam> GetAllTeams()
        {
            return AllTeams.AsReadOnly();
        }

        // =====================================================================
        // CONSTRUCTORS
        // =====================================================================
        [Constructable]
        public AdventurerTeam() : this(0, false)
        {
        }

        /// <summary>
        /// Main constructor for AdventurerTeam.
        /// </summary>
        /// <param name="teamId">Team identifier (0 = manually spawned, non-zero = system spawned)</param>
        /// <param name="isEvil">If true, attacks good creatures; if false, attacks evil creatures</param>
        [Constructable]
        public AdventurerTeam(int teamId, bool isEvil) : base(AIType.AI_Melee, FightMode.None, 10, 1, 0.2, 0.4)
        {
            // Initialize team properties
            this.m_TeamId = teamId;
            this.m_IsEvil = isEvil;
            this.m_SpawnedBySystem = (teamId != 0);
            ResetSpecialUsedFlag();

            // Register in global team list
            if (!AllTeams.Contains(this))
                AllTeams.Add(this);

            // Randomize gender and appearance
            if (Female = Utility.RandomBool())
            {
                Body = 401;
                Name = NameList.RandomName("female");
            }
            else
            {
                Body = 400;
                Name = NameList.RandomName("male");
                FacialHairItemID = Utility.RandomList(0,0,8254,8255,8256,8257,8267,8268,8269);
            }

            // Set level-based attributes
            CitizenLevel = Utility.RandomMinMax(1, 9);
            Fame = 2500 * CitizenLevel;
            VirtualArmor = CitizenLevel * 10;
            SetDamage(CitizenLevel * 2, CitizenLevel * 3);
            SetResistances(CitizenLevel);

            // Configure alignment (good vs evil)
            if (m_IsEvil)
            {
                Title = TavernPatrons.GetEvilTitle();  // Dependency: Custom TavernPatrons module
                Hue = Utility.RandomList(0x995, 0x8A4, 0x8B0, 0x8AC);
                Karma = -Fame;
                FightMode = FightMode.Good;  // Evil NPCs attack good creatures
            }
            else
            {
                Title = TavernPatrons.GetTitle();  // Dependency: Custom TavernPatrons module
                Hue = Utility.RandomSkinColor();
                Karma = Fame;
                FightMode = FightMode.Evil;  // Good NPCs attack evil creatures
            }

            // Finalize appearance
            Utility.AssignRandomHair(this);
            SpeechHue = Utility.RandomTalkHue();
            HairHue = Utility.RandomHairHue();
            FacialHairHue = HairHue;
            LastSeen = DateTime.Now;

            // Randomize citizen type and configure class-specific attributes
            int type = Utility.Random(3);
            switch (type)
            {
                case 0: // WIZARD - High INT, uses magic
                    Server.Misc.IntelligentAction.DressUpWizards(this, m_IsEvil);
                    CitizenType = 1;
                    AI = AIType.AI_Mage;
                    SetStr(CitizenLevel * 50, CitizenLevel * 70);
                    SetDex(CitizenLevel * 70, CitizenLevel * 90);
                    SetInt(CitizenLevel * 100, CitizenLevel * 130);
                    SetHits(CitizenLevel * 100, CitizenLevel * 130);
                    int baseSkill = SkillBaseValue + (CitizenLevel * SkillLevelMultiplier);
                    SetBaseSkills(baseSkill);
                    SetSkill(SkillName.Psychology, baseSkill);
                    SetSkill(SkillName.Magery, baseSkill);
                    SetSkill(SkillName.Meditation, baseSkill);
                    AddRangeWeapon();
                    break;

                case 1: // FIGHTER - High STR, melee combat
                    Server.Misc.IntelligentAction.DressUpFighters(this, "", m_IsEvil, false, true);
                    CitizenType = 2;
                    AI = AIType.AI_Melee;
                    SetStr(CitizenLevel * 100, CitizenLevel * 130);
                    SetDex(CitizenLevel * 70, CitizenLevel * 90);
                    SetInt(CitizenLevel * 50, CitizenLevel * 70);
                    SetHits(CitizenLevel * 100, CitizenLevel * 130);
                    int baseSkill2 = SkillBaseValue + (CitizenLevel * SkillLevelMultiplier);
                    SetBaseSkills(baseSkill2);
                    SetSkill(SkillName.Fencing, baseSkill2);
                    SetSkill(SkillName.Bludgeoning, baseSkill2);
                    SetSkill(SkillName.Swords, baseSkill2);
                    SetSkill(SkillName.Parry, baseSkill2);
                    break;

                case 2: // ROGUE/ARCHER - High DEX, ranged combat
                    Server.Misc.IntelligentAction.DressUpRogues(this, "", m_IsEvil, false, true);
                    CitizenType = 3;
                    AI = AIType.AI_Archer;
                    SetStr(CitizenLevel * 70, CitizenLevel * 90);
                    SetDex(CitizenLevel * 100, CitizenLevel * 130);
                    SetInt(CitizenLevel * 50, CitizenLevel * 70);
                    SetHits(CitizenLevel * 100, CitizenLevel * 130);
                    int baseSkill3 = SkillBaseValue + (CitizenLevel * SkillLevelMultiplier);
                    SetBaseSkills(baseSkill3);
                    SetSkill(SkillName.Marksmanship, baseSkill3);
                    SetSkill(SkillName.Tactics, baseSkill3);
                    AddRangeWeapon();
                    break;
            }
        }

        // =====================================================================
        // LOOT GENERATION
        // =====================================================================
        /// <summary>
        /// Generates tiered loot based on CitizenLevel.
        /// Higher levels get progressively better loot packs.
        /// </summary>
        public override void GenerateLoot()
        {
            // Tiered loot: higher levels get better drops
            if (CitizenLevel > 8)
                AddLoot(LootPack.FilthyRich);
            if (CitizenLevel > 6)
                AddLoot(LootPack.Rich);
            if (CitizenLevel > 4)
                AddLoot(LootPack.Average);
            if (CitizenLevel > 2)
                AddLoot(LootPack.Meager);
            else
                AddLoot(LootPack.Meager);

            // 4% chance for rare item (Dependency: Custom Loot module)
            if (Utility.Random(25) == 0)
            {
                Type rareType = Loot.AdventurerRareItemTypes[Utility.Random(Loot.AdventurerRareItemTypes.Length)];
                Item rare = Activator.CreateInstance(rareType) as Item;
                if (rare != null)
                    PackItem(rare);
            }

            // Wizards get bonus spell scrolls
            if (CitizenType == 1)
                AddLoot(LootPack.MedScrolls, (int)((CitizenLevel / 3) + 1));
        }

        // =====================================================================
        // SERIALIZATION CONSTRUCTOR
        // =====================================================================
        public AdventurerTeam(Serial serial) : base(serial)
        {
        }

        // =====================================================================
        // HELPER METHODS
        // =====================================================================
        private void ResetSpecialUsedFlag()
        {
            m_SpecialUsed = false;
        }

        /// <summary>
        /// Sets common base skills for all citizen types.
        /// </summary>
        private void SetBaseSkills(int baseValue)
        {
            SetSkill(SkillName.MagicResist, baseValue);
            SetSkill(SkillName.Tactics, baseValue);
            SetSkill(SkillName.FistFighting, baseValue);
            SetSkill(SkillName.Marksmanship, baseValue);
        }

        /// <summary>
        /// Sets elemental resistances based on level.
        /// </summary>
        private void SetResistances(int level)
        {
            int minResist = level * 4;
            int maxResist = level * 7;
            SetResistance(ResistanceType.Physical, minResist, maxResist);
            SetResistance(ResistanceType.Fire, minResist, maxResist);
            SetResistance(ResistanceType.Cold, minResist, maxResist);
            SetResistance(ResistanceType.Poison, minResist, maxResist);
            SetResistance(ResistanceType.Energy, minResist, maxResist);
        }

        // =====================================================================
        // LIFECYCLE METHODS
        // =====================================================================
        /// <summary>
        /// Removes this instance from the global team registry on deletion.
        /// </summary>
        public override void OnDelete()
        {
            base.OnDelete();

            if (AllTeams.Contains(this))
                AllTeams.Remove(this);
        }

        // =====================================================================
        // SERIALIZATION / DESERIALIZATION
        // =====================================================================
        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write(1); // Version number for future compatibility

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

            // Initialize defaults before reading
            m_CitizenType = 0;
            m_CitizenLevel = 1;
            m_SpawnedBySystem = false;
            m_TeamId = 0;
            m_LastSeen = DateTime.Now;
            m_IsEvil = false;
            m_SpecialUsed = false;

            if (version >= 1)
            {
                m_CitizenType = reader.ReadInt();
                m_CitizenLevel = reader.ReadInt();
                m_SpawnedBySystem = reader.ReadBool();
                m_TeamId = reader.ReadInt();
                m_LastSeen = reader.ReadDateTime();
                m_IsEvil = reader.ReadBool();
                m_SpecialUsed = reader.ReadBool();
            }

            // Re-register in global team list after load
            if (!AllTeams.Contains(this))
                AllTeams.Add(this);

            m_ThinkCounter = 0;
        }

        // =====================================================================
        // AI BEHAVIOR - OnThink
        // =====================================================================
        /// <summary>
        /// Main AI loop. Handles:
        /// 1. LastSeen update (always runs - prevents premature cleanup)
        /// 2. Special ability trigger at low health (one-time)
        /// 3. Team combat coordination (throttled for performance)
        /// </summary>
        public override void OnThink()
        {
            base.OnThink();

            // ─────────────────────────────────────────────────────────────────
            // STEP 1: Update LastSeen timestamp (NOT throttled - critical for cleanup logic)
            // This runs every OnThink to ensure accurate activity tracking.
            // ─────────────────────────────────────────────────────────────────
            UpdateLastSeen();

            // ─────────────────────────────────────────────────────────────────
            // STEP 2: Special ability trigger (one-time at low health)
            // Each citizen type has a unique ability that triggers once per life.
            // ─────────────────────────────────────────────────────────────────
            if (!m_SpecialUsed && Hits < HitsMax * LowHealthThreshold)
            {
                m_SpecialUsed = true;

                // Random battle cry
                string shout = SpecialLines[Utility.Random(SpecialLines.Length)];
                PublicOverheadMessage(Server.Network.MessageType.Regular, 0x3B2, true, shout);

                switch (m_CitizenType)
                {
                    case 1: // WIZARD SPECIAL: Burst magic damage
                        PublicOverheadMessage(Server.Network.MessageType.Emote, 0x3B2, true, "*unleashes forbidden magic!*");
                        if (Combatant != null && IsValidCombatTarget(Combatant))
                            AOS.Damage(Combatant, this, Utility.RandomMinMax(WizardDamageMin, WizardDamageMax), WizardDamagePhys, WizardDamageFire, 0, 0, 0);
                        break;

                    case 2: // FIGHTER SPECIAL: Emergency heal
                        PublicOverheadMessage(Server.Network.MessageType.Emote, 0x3B2, true, "*lets out a furious roar!*");
                        Hits += FighterHealAmount;
                        break;

                    case 3: // ROGUE SPECIAL: Powerful shot (100% fire damage)
                        PublicOverheadMessage(Server.Network.MessageType.Emote, 0x3B2, true, "*fires a powerful arrow!*");
                        if (Combatant != null && IsValidCombatTarget(Combatant))
                            AOS.Damage(Combatant, this, Utility.RandomMinMax(RogueDamageMin, RogueDamageMax), 0, 100, 0, 0, 0);
                        break;
                }
            }

            // ─────────────────────────────────────────────────────────────────
            // STEP 3: Early exit conditions (performance optimization)
            // Skip expensive operations if not needed.
            // ─────────────────────────────────────────────────────────────────
            if (Combatant == null && m_SpecialUsed)
                return;

            // ─────────────────────────────────────────────────────────────────
            // STEP 4: Throttle check (process every ThinkCheckInterval ticks)
            // Reduces CPU usage by ~87.5% for team coordination logic.
            // ─────────────────────────────────────────────────────────────────
            if (++m_ThinkCounter % ThinkCheckInterval != 0)
                return;

            // ─────────────────────────────────────────────────────────────────
            // STEP 5: Cache nearby mobiles (reuse list to reduce GC pressure)
            // ─────────────────────────────────────────────────────────────────
            if (m_MobilesCache == null)
                m_MobilesCache = new List<Mobile>(32);
            m_MobilesCache.Clear();

            IPooledEnumerable eable = GetMobilesInRange(TeamMemberRange);
            foreach (Mobile m in eable)
                m_MobilesCache.Add(m);
            eable.Free();

            // ─────────────────────────────────────────────────────────────────
            // STEP 6: Team combat coordination
            // Share current combatant with idle team members for coordinated attacks.
            // Only shares valid targets (excludes player pets).
            // ─────────────────────────────────────────────────────────────────
            Mobile validCombatant = GetValidCombatant(Combatant);
            foreach (Mobile m in m_MobilesCache)
            {
                AdventurerTeam teamMember = m as AdventurerTeam;

                if (teamMember != null &&
                    teamMember.TeamId == TeamId &&
                    teamMember != this &&
                    teamMember.Combatant == null &&
                    validCombatant != null)
                {
                    teamMember.Combatant = validCombatant;
                }
            }
        }

        // =====================================================================
        // LASTSEEN OPTIMIZATION METHODS
        // =====================================================================
        /// <summary>
        /// Updates LastSeen timestamp if a player is nearby.
        /// Uses lightweight distance check instead of GetMobilesInRange.
        /// Called every OnThink (not throttled) to ensure accurate tracking.
        /// </summary>
        private void UpdateLastSeen()
        {
            if (IsPlayerNearby())
                LastSeen = DateTime.Now;
        }

        /// <summary>
        /// Lightweight player proximity check.
        /// Iterates NetState.Instances instead of GetMobilesInRange for better performance.
        /// </summary>
        /// <returns>True if any player is within TeamMemberRange tiles</returns>
        private bool IsPlayerNearby()
        {
            foreach (NetState state in NetState.Instances)
            {
                if (state == null || state.Mobile == null)
                    continue;

                Mobile player = state.Mobile;
                if (player is PlayerMobile && GetDistanceToSqrt(player) <= TeamMemberRange)
                    return true;
            }
            return false;
        }

        // =====================================================================
        // COMBAT TARGET VALIDATION
        // =====================================================================
        /// <summary>
        /// Validates if a target is a valid combat target.
        /// Excludes player-controlled pets to prevent friendly fire.
        /// </summary>
        /// <param name="target">The mobile to validate</param>
        /// <returns>True if target is valid for combat</returns>
        private bool IsValidCombatTarget(Mobile target)
        {
            if (target == null || target.Deleted)
                return false;

            // Exclude player-controlled creatures (pets, summons, etc.)
            if (target is BaseCreature)
            {
                BaseCreature pet = (BaseCreature)target;
                if (pet.Controlled)
                    return false;

                if (pet.ControlMaster != null && pet.ControlMaster is PlayerMobile)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Returns the current combatant only if it's a valid target.
        /// Used when sharing combatants with team members.
        /// </summary>
        private Mobile GetValidCombatant(Mobile currentCombatant)
        {
            if (currentCombatant == null)
                return null;

            if (IsValidCombatTarget(currentCombatant))
                return currentCombatant;

            return null;
        }

        // =====================================================================
        // EQUIPMENT METHODS
        // =====================================================================
        /// <summary>
        /// Adds appropriate ranged weapon based on citizen type.
        /// Removes existing weapons first to prevent duplicates.
        /// </summary>
        public void AddRangeWeapon()
        {
            // Remove existing weapons
            Item oneHanded = FindItemOnLayer(Layer.OneHanded);
            if (oneHanded != null && oneHanded is BaseWeapon)
                oneHanded.Delete();

            Item twoHanded = FindItemOnLayer(Layer.TwoHanded);
            if (twoHanded != null && twoHanded is BaseWeapon)
                twoHanded.Delete();

            // 50% chance for throwing weapons
            if (Utility.RandomBool())
            {
                ThrowingGloves glove = new ThrowingGloves();
                ThrowingWeapon ammo = new ThrowingWeapon(Utility.RandomMinMax(15, 30));

                switch (Utility.Random(5))
                {
                    case 0:
                        glove.GloveType = "Stones";
                        ammo.ammo = "Throwing Stones";
                        ammo.ItemID = 0x10B6;
                        ammo.Name = "throwing stone";
                        break;
                    case 1:
                        glove.GloveType = "Axes";
                        ammo.ammo = "Throwing Axes";
                        ammo.ItemID = 0x10B3;
                        ammo.Name = "throwing axe";
                        break;
                    case 2:
                        glove.GloveType = "Daggers";
                        ammo.ammo = "Throwing Daggers";
                        ammo.ItemID = 0x10B7;
                        ammo.Name = "throwing dagger";
                        break;
                    case 3:
                        glove.GloveType = "Darts";
                        ammo.ammo = "Throwing Darts";
                        ammo.ItemID = 0x10B5;
                        ammo.Name = "throwing dart";
                        break;
                    case 4:
                        glove.GloveType = "Stars";
                        ammo.ammo = "Throwing Stars";
                        ammo.ItemID = 0x10B2;
                        ammo.Name = "throwing star";
                        break;
                }

                AddItem(glove);
                PackItem(ammo);
            }
            else if (CitizenType == 1) // Wizard gets staff/wand
            {
                switch (Utility.Random(2))
                {
                    case 0: AddItem(new WizardStaff()); break;
                    case 1: AddItem(new WizardStick()); break;
                }
                PackItem(new MageEye(Utility.RandomMinMax(15, 30)));
            }
            else // Rogue/Archer gets ranged weapons
            {
                switch (Utility.Random(8))
                {
                    case 0: AddItem(new Bow()); PackItem(new Arrow(Utility.RandomMinMax(15, 30))); break;
                    case 1: AddItem(new Crossbow()); PackItem(new Bolt(Utility.RandomMinMax(15, 30))); break;
                    case 2: AddItem(new HeavyCrossbow()); PackItem(new Bolt(Utility.RandomMinMax(15, 30))); break;
                    case 3: AddItem(new RepeatingCrossbow()); PackItem(new Bolt(Utility.RandomMinMax(15, 30))); break;
                    case 4: AddItem(new CompositeBow()); PackItem(new Arrow(Utility.RandomMinMax(15, 30))); break;
                    case 5: AddItem(new MagicalShortbow()); PackItem(new Arrow(Utility.RandomMinMax(15, 30))); break;
                    case 6: AddItem(new ElvenCompositeLongbow()); PackItem(new Arrow(Utility.RandomMinMax(15, 30))); break;
                    case 7: AddItem(new Harpoon()); PackItem(new HarpoonRope(Utility.RandomMinMax(15, 30))); break;
                }
            }
        }
    }

    // =========================================================================
    // SECTION 2: Automated Team Maintainer
    // =========================================================================
    // Handles automatic spawning, grouping, and cleanup of adventurer teams.
    // Features:
    // - Player proximity-based spawning with cooldown per account
    // - Multi-member teams with randomized sizes
    // - Periodic cleanup of inactive/abandoned teams
    // =========================================================================

    public class AutoTeamMaintainer
    {
        // =====================================================================
        // CONSTANTS - Adjust these values to control spawn behavior
        // =====================================================================
        private const int MaxTeamsPerGroup = 2;              // Max teams spawned per player group
        private const int MinTeamSize = 3;                   // Minimum members per team
        private const int MaxTeamSize = 5;                   // Maximum members per team
        private const int PlayerGroupingRadius = 50;         // Radius (tiles) for grouping nearby players
        private const int LocationSearchAttempts = 20;       // Max attempts to find valid spawn location
        private const int MinSpawnDistance = 18;             // Minimum distance from player for spawn
        private const double SpawnProbability = 0.5;         // 50% chance to spawn per eligible group
        private const double EvilTeamProbability = 0.3;      // 30% chance for evil team
        private const int MinTeamId = 100;                   // Minimum generated TeamId
        private const int MaxTeamId = 9999;                  // Maximum generated TeamId

        // Timer intervals
        private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);       // Spawn check interval
        private static readonly TimeSpan CooldownInterval = TimeSpan.FromMinutes(15);   // Per-account spawn cooldown

        // Thread safety lock
        private static readonly object _lock = new object();

        // =====================================================================
        // STATIC FIELDS
        // =====================================================================
        private static bool m_IsRunning = false;
        private static readonly Dictionary<Account, DateTime> m_LastSpawnTime = new Dictionary<Account, DateTime>();
        private static readonly List<int> m_UsedTeamIds = new List<int>();

        // =====================================================================
        // INITIALIZATION
        // =====================================================================
        /// <summary>
        /// Initializes the AutoTeamMaintainer system.
        /// Called automatically on server startup.
        /// </summary>
        public static void Initialize()
        {
            if (m_IsRunning) return;
            m_IsRunning = true;

            // Clean up any leftover teams from previous session
            CleanupOldTeams();

            // Schedule recurring maintenance tasks
            Timer.DelayCall(CheckInterval, CheckInterval, new TimerCallback(MaintainTeams));
            Timer.DelayCall(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), new TimerCallback(CleanupInactiveTeams));
        }

        // =====================================================================
        // MAIN SPAWNING LOGIC
        // =====================================================================
        /// <summary>
        /// Main spawning routine. Finds eligible player groups and spawns teams nearby.
        /// Respects per-account cooldowns and spawn probability.
        /// </summary>
        private static void MaintainTeams()
        {
            List<PlayerMobile> playerGroups = GetPlayerSpawnGroups();
            if (playerGroups.Count == 0) return;

            // Build list of online accounts for cooldown cleanup
            List<Account> onlineAccounts = new List<Account>();
            foreach (NetState state in NetState.Instances)
            {
                if (state != null && state.Mobile != null && state.Mobile.Account != null)
                    onlineAccounts.Add((Account)state.Mobile.Account);
            }

            // Remove cooldown entries for offline accounts
            lock (_lock)
            {
                var keys = new List<Account>(m_LastSpawnTime.Keys);
                foreach (Account acc in keys)
                    if (!onlineAccounts.Contains(acc))
                        m_LastSpawnTime.Remove(acc);
            }

            List<string> failedLocations = new List<string>();
            int totalTeamsSpawned = 0;

            foreach (PlayerMobile rep in playerGroups)
            {
                if (rep.Account == null)
                    continue;

                Account acct = (Account)rep.Account;

                // Check per-account cooldown
                lock (_lock)
                {
                    if (m_LastSpawnTime.ContainsKey(acct) && (DateTime.Now - m_LastSpawnTime[acct]) < CooldownInterval)
                    {
                        Console.WriteLine("Skipping spawn for {0}. Still in cooldown.", rep.Name);
                        continue;
                    }
                }

                // Apply spawn probability
                if (Utility.RandomDouble() >= SpawnProbability)
                    continue;

                Map map = rep.Map;
                if (map == null)
                    continue;

                // Spawn 1-2 teams per eligible group
                int numTeams = Utility.RandomMinMax(1, MaxTeamsPerGroup);
                for (int i = 0; i < numTeams; i++)
                {
                    int thisTeamSize = Utility.RandomMinMax(MinTeamSize, MaxTeamSize);
                    bool thisIsEvil = Utility.RandomDouble() < EvilTeamProbability;
                    Point3D thisLoc = GetRandomLocationAroundPlayer(rep.Location, map);

                    if (thisLoc == Point3D.Zero)
                    {
                        failedLocations.Add(rep.Name);
                        continue;
                    }

                    // Generate unique TeamId
                    int thisTeamId;
                    lock (_lock)
                    {
                        do
                        {
                            thisTeamId = Utility.RandomMinMax(MinTeamId, MaxTeamId);
                        } while (m_UsedTeamIds.Contains(thisTeamId));
                        m_UsedTeamIds.Add(thisTeamId);
                    }

                    string teamType = thisIsEvil ? "evil" : "friendly";

                    // Spawn team members with slight position offsets
                    int membersSpawned = 0;
                    for (int j = 0; j < thisTeamSize; j++)
                    {
                        int offsetX = Utility.RandomMinMax(-3, 3);
                        int offsetY = Utility.RandomMinMax(-3, 3);
                        Point3D spawnLoc = new Point3D(thisLoc.X + offsetX, thisLoc.Y + offsetY, map.GetAverageZ(thisLoc.X + offsetX, thisLoc.Y + offsetY));

                        // Validate spawn location
                        if (!map.CanFit(spawnLoc.X, spawnLoc.Y, spawnLoc.Z, 6, false, false, true) || 
                            !map.CanSpawnMobile(spawnLoc.X, spawnLoc.Y, spawnLoc.Z))
                            continue;

                        AdventurerTeam newMember = new AdventurerTeam(thisTeamId, thisIsEvil);
                        newMember.MoveToWorld(spawnLoc, map);
                        membersSpawned++;
                    }

                    totalTeamsSpawned++;
                    Console.WriteLine(string.Format("Spawned a new {0} adventurer team ({1} members) for player {2} at {3} in {4}.",
                        teamType, membersSpawned, rep.Name, thisLoc, map));
                }

                // Update account cooldown
                lock (_lock)
                {
                    m_LastSpawnTime[acct] = DateTime.Now;
                }
            }

            // Log failed spawn attempts
            foreach (var playerName in failedLocations)
            {
                Console.WriteLine(string.Format("Failed to find a valid spawn location near player {0}.", playerName));
            }

            if (totalTeamsSpawned == 0 && failedLocations.Count == 0)
            {
                Console.WriteLine("No teams were spawned during this cycle.");
            }
        }

        // =====================================================================
        // PLAYER GROUPING
        // =====================================================================
        /// <summary>
        /// Groups nearby players together to avoid spawning multiple teams for clustered players.
        /// Returns one representative player per group.
        /// </summary>
        private static List<PlayerMobile> GetPlayerSpawnGroups()
        {
            List<PlayerMobile> playersInSpawnableAreas = new List<PlayerMobile>();

            // Find players outside guarded regions
            foreach (NetState state in NetState.Instances)
            {
                Mobile m = state.Mobile;
                PlayerMobile p = m as PlayerMobile;
                if (p != null)
                {
                    Region reg = Region.Find(m.Location, m.Map);
                    if (!(reg is GuardedRegion))
                        playersInSpawnableAreas.Add(p);
                }
            }

            // Group nearby players (O(n²) - acceptable for typical player counts)
            List<PlayerMobile> spawnGroups = new List<PlayerMobile>();
            List<PlayerMobile> groupedPlayers = new List<PlayerMobile>();

            foreach (PlayerMobile player in playersInSpawnableAreas)
            {
                if (!groupedPlayers.Contains(player))
                {
                    spawnGroups.Add(player);  // This player represents their group
                    groupedPlayers.Add(player);

                    // Find other players within grouping radius
                    foreach (PlayerMobile otherPlayer in playersInSpawnableAreas)
                    {
                        if (player != otherPlayer && !groupedPlayers.Contains(otherPlayer))
                        {
                            if (player.GetDistanceToSqrt(otherPlayer) < PlayerGroupingRadius)
                                groupedPlayers.Add(otherPlayer);
                        }
                    }
                }
            }

            return spawnGroups;
        }

        // =====================================================================
        // LOCATION FINDING
        // =====================================================================
        /// <summary>
        /// Finds a valid spawn location near a player.
        /// Ensures minimum distance and avoids guarded regions.
        /// </summary>
        /// <returns>Valid spawn point, or Point3D.Zero if none found</returns>
        private static Point3D GetRandomLocationAroundPlayer(Point3D playerLoc, Map map)
        {
            for (int i = 0; i < LocationSearchAttempts; i++)
            {
                int x = playerLoc.X + Utility.RandomMinMax(-30, 30);
                int y = playerLoc.Y + Utility.RandomMinMax(-30, 30);

                // Ensure minimum distance from player
                int dist = (int)Math.Sqrt(Math.Pow(x - playerLoc.X, 2) + Math.Pow(y - playerLoc.Y, 2));
                if (dist < MinSpawnDistance)
                    continue;

                int z = map.GetAverageZ(x, y);
                Point3D newLocation = new Point3D(x, y, z);

                // Validate location (must be passable and outside guarded regions)
                Region reg = Region.Find(newLocation, map);
                if (map.CanFit(x, y, z, 16, false, false, true) && !(reg is GuardedRegion))
                    return newLocation;
            }
            return Point3D.Zero;
        }

        // =====================================================================
        // CLEANUP METHODS
        // =====================================================================
        /// <summary>
        /// Removes all system-spawned teams on startup.
        /// Ensures clean slate after server restart.
        /// </summary>
        private static void CleanupOldTeams()
        {
            Console.WriteLine("Cleaning up old adventurer teams...");

            List<AdventurerTeam> teamsToDelete = new List<AdventurerTeam>();
            foreach (AdventurerTeam team in AdventurerTeam.GetAllTeams())
            {
                if (team.SpawnedBySystem)
                    teamsToDelete.Add(team);
            }

            int deletedCount = 0;
            foreach (AdventurerTeam team in teamsToDelete)
            {
                int teamId = team.TeamId;
                team.Delete();
                if (teamId != 0)
                    lock (_lock)
                    {
                        m_UsedTeamIds.Remove(teamId);
                    }
                deletedCount++;
            }

            Console.WriteLine(string.Format("Cleaned up {0} old system-generated teams. {1} manual teams remain.",
                deletedCount, AdventurerTeam.GetAllTeams().Count));
        }

        /// <summary>
        /// Removes teams that haven't been near a player for over 1 hour.
        /// Prevents abandoned teams from accumulating.
        /// </summary>
        private static void CleanupInactiveTeams()
        {
            Console.WriteLine("Cleaning up inactive adventurer teams...");

            List<AdventurerTeam> teamsToDelete = new List<AdventurerTeam>();
            DateTime now = DateTime.Now;

            // Use World.Mobiles for reliability
            foreach (Mobile mobile in World.Mobiles.Values)
            {
                AdventurerTeam adv = mobile as AdventurerTeam;
                if (adv != null && adv.SpawnedBySystem && (now - adv.LastSeen) > TimeSpan.FromHours(1))
                    teamsToDelete.Add(adv);
            }

            int deletedCount = 0;
            foreach (AdventurerTeam team in teamsToDelete)
            {
                int teamId = team.TeamId;
                team.Delete();
                if (teamId != 0)
                    lock (_lock)
                    {
                        m_UsedTeamIds.Remove(teamId);
                    }
                deletedCount++;
            }

            Console.WriteLine(string.Format("Cleaned up {0} inactive teams.", deletedCount));
        }
    }
}
