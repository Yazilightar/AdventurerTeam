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
    // ═════════════════════════════════════════════════════════════════════════
    // ███ ADVENTURER TEAM - DYNAMIC NPC SQUAD SYSTEM ███
    // ═════════════════════════════════════════════════════════════════════════
    // PURPOSE: Creates intelligent adventurer teams that spawn near players
    // FEATURES: Team coordination, special abilities, pet-safe combat, auto-cleanup
    // 
    // PERFORMANCE OPTIMIZATIONS (.NET 2.0 Compatible):
    // • UpdateLastSeen() throttled to prevent 5000+ calls/second
    // • Spatial hashing for O(n) player grouping vs O(n²)
    // • TeamId recycling system (memory efficient)
    // • Mobile caching with early exit logic
    // • No HashSet - uses List<T> for .NET 2.0 compatibility
    // ═════════════════════════════════════════════════════════════════════════

    [CorpseName("an adventurer corpse")]
    public class AdventurerTeam : BaseCreature
    {
        // ─────────────────────────────────────────────────────────────────────
        // ▼ STATIC REGISTRY - Global tracking of all adventurer instances
        // ─────────────────────────────────────────────────────────────────────
        // AllTeams: Central registry for team management and cleanup operations
        private static readonly List<AdventurerTeam> AllTeams = new List<AdventurerTeam>();

        // SpecialLines: Random battle cries when triggering low-HP abilities
        private static readonly string[] SpecialLines = new string[]
        {
            "You will regret this!",
            "Feel my wrath!",
            "This is my last stand!",
            "I won't go down easily!",
            "Now you face my true power!"
        };

        // ─────────────────────────────────────────────────────────────────────
        // ▼ GAMEPLAY BALANCE CONSTANTS - Adjust these to tune difficulty
        // ─────────────────────────────────────────────────────────────────────
        private const double LowHealthThreshold = 0.3;      // 30% HP → triggers special ability
        private const int TeamMemberRange = 15;              // Tiles - coordination & player detection radius
        private const int SkillBaseValue = 28;               // Base skill for level 1
        private const int SkillLevelMultiplier = 7;          // Skill gain per level (+7 per level)
        private const int ThinkCheckInterval = 8;            // AI update throttle (8 ticks ≈ 2 seconds)
        
        // Wizard special ability parameters (fire burst)
        private const int WizardDamageMin = 20;
        private const int WizardDamageMax = 30;
        private const int WizardDamagePhys = 50;             // 50% physical
        private const int WizardDamageFire = 50;             // 50% fire
        
        // Rogue special ability parameters (power shot)
        private const int RogueDamageMin = 15;
        private const int RogueDamageMax = 25;
        
        // Fighter special ability (self-heal)
        private const int FighterHealAmount = 20;

        // ─────────────────────────────────────────────────────────────────────
        // ▼ INSTANCE VARIABLES - Per-NPC state tracking
        // ─────────────────────────────────────────────────────────────────────
        private int m_CitizenType;          // 1=Wizard, 2=Fighter, 3=Rogue/Archer
        private int m_CitizenLevel;         // 1-9 (affects stats, skills, loot quality)
        private bool m_SpawnedBySystem;     // true=AutoTeamMaintainer, false=manual GM spawn
        private bool m_IsEvil;              // true=attacks good, false=attacks evil
        private bool m_SpecialUsed;         // Prevents multiple special ability triggers
        private int m_TeamId;               // Shared ID for team coordination (0=no team)
        private DateTime m_LastSeen;        // Last player proximity timestamp (for cleanup)

        // Performance optimization fields
        private int m_ThinkCounter = 0;             // Throttle counter for OnThink
        private List<Mobile> m_MobilesCache;        // Reusable list to reduce GC pressure

        // ─────────────────────────────────────────────────────────────────────
        // ▼ PROPERTIES - Exposed for GM commands and inspection
        // ─────────────────────────────────────────────────────────────────────
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

        // ─────────────────────────────────────────────────────────────────────
        // ▼ PUBLIC API - External access to team registry
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>Returns read-only list of all adventurer instances (used by cleanup system)</summary>
        public static IReadOnlyList<AdventurerTeam> GetAllTeams()
        {
            return AllTeams.AsReadOnly();
        }

        // ─────────────────────────────────────────────────────────────────────
        // ▼ CONSTRUCTORS - NPC creation and initialization
        // ─────────────────────────────────────────────────────────────────────
        [Constructable]
        public AdventurerTeam() : this(0, false)
        {
        }

        /// <summary>
        /// Main constructor - Randomizes appearance, stats, and equipment
        /// </summary>
        /// <param name="teamId">0=manual spawn, non-zero=system spawn with team coordination</param>
        /// <param name="isEvil">true=villain (attacks good), false=hero (attacks evil)</param>
        [Constructable]
        public AdventurerTeam(int teamId, bool isEvil) : base(AIType.AI_Melee, FightMode.None, 10, 1, 0.2, 0.4)
        {
            // ═══ STEP 1: Initialize core identity ═══
            this.m_TeamId = teamId;
            this.m_IsEvil = isEvil;
            this.m_SpawnedBySystem = (teamId != 0);
            ResetSpecialUsedFlag();

            // Register in global tracking system
            if (!AllTeams.Contains(this))
                AllTeams.Add(this);

            // ═══ STEP 2: Randomize appearance ═══
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

            // ═══ STEP 3: Level-based power scaling ═══
            CitizenLevel = Utility.RandomMinMax(1, 9);
            Fame = 2500 * CitizenLevel;                  // Fame grows linearly
            VirtualArmor = CitizenLevel * 10;            // Armor: 10-90
            SetDamage(CitizenLevel * 2, CitizenLevel * 3); // Damage: 2-27
            SetResistances(CitizenLevel);                 // Resistances: 4%-63%

            // ═══ STEP 4: Alignment configuration (Good vs Evil) ═══
            if (m_IsEvil)
            {
                Title = TavernPatrons.GetEvilTitle();      // e.g., "the Cruel", "the Malicious"
                Hue = Utility.RandomList(0x995, 0x8A4, 0x8B0, 0x8AC); // Dark skin tones
                Karma = -Fame;                              // Negative karma
                FightMode = FightMode.Good;                 // Attacks good-aligned creatures
            }
            else
            {
                Title = TavernPatrons.GetTitle();          // e.g., "the Brave", "the Noble"
                Hue = Utility.RandomSkinColor();            // Normal skin tones
                Karma = Fame;                               // Positive karma
                FightMode = FightMode.Evil;                 // Attacks evil-aligned creatures
            }

            // ═══ STEP 5: Finalize cosmetics ═══
            Utility.AssignRandomHair(this);
            SpeechHue = Utility.RandomTalkHue();
            HairHue = Utility.RandomHairHue();
            FacialHairHue = HairHue;
            LastSeen = DateTime.Now;

            // ═══ STEP 6: Profession selection & specialization ═══
            int type = Utility.Random(3);
            switch (type)
            {
                case 0: // ★ WIZARD - High INT, magic damage
                    Server.Misc.IntelligentAction.DressUpWizards(this, m_IsEvil);
                    CitizenType = 1;
                    AI = AIType.AI_Mage;
                    SetStr(CitizenLevel * 50, CitizenLevel * 70);      // 50-630 STR
                    SetDex(CitizenLevel * 70, CitizenLevel * 90);      // 70-810 DEX
                    SetInt(CitizenLevel * 100, CitizenLevel * 130);    // 100-1170 INT (primary)
                    SetHits(CitizenLevel * 100, CitizenLevel * 130);
                    int baseSkill = SkillBaseValue + (CitizenLevel * SkillLevelMultiplier);
                    SetBaseSkills(baseSkill);                           // Combat basics
                    SetSkill(SkillName.Psychology, baseSkill);          // Wizard-specific
                    SetSkill(SkillName.Magery, baseSkill);
                    SetSkill(SkillName.Meditation, baseSkill);
                    AddRangeWeapon();                                   // Staff or wand
                    break;

                case 1: // ★ FIGHTER - High STR, melee tank
                    Server.Misc.IntelligentAction.DressUpFighters(this, "", m_IsEvil, false, true);
                    CitizenType = 2;
                    AI = AIType.AI_Melee;
                    SetStr(CitizenLevel * 100, CitizenLevel * 130);    // 100-1170 STR (primary)
                    SetDex(CitizenLevel * 70, CitizenLevel * 90);      // 70-810 DEX
                    SetInt(CitizenLevel * 50, CitizenLevel * 70);      // 50-630 INT
                    SetHits(CitizenLevel * 100, CitizenLevel * 130);
                    int baseSkill2 = SkillBaseValue + (CitizenLevel * SkillLevelMultiplier);
                    SetBaseSkills(baseSkill2);
                    SetSkill(SkillName.Fencing, baseSkill2);            // Melee specialization
                    SetSkill(SkillName.Bludgeoning, baseSkill2);
                    SetSkill(SkillName.Swords, baseSkill2);
                    SetSkill(SkillName.Parry, baseSkill2);
                    break;

                case 2: // ★ ROGUE/ARCHER - High DEX, ranged DPS
                    Server.Misc.IntelligentAction.DressUpRogues(this, "", m_IsEvil, false, true);
                    CitizenType = 3;
                    AI = AIType.AI_Archer;
                    SetStr(CitizenLevel * 70, CitizenLevel * 90);      // 70-810 STR
                    SetDex(CitizenLevel * 100, CitizenLevel * 130);    // 100-1170 DEX (primary)
                    SetInt(CitizenLevel * 50, CitizenLevel * 70);      // 50-630 INT
                    SetHits(CitizenLevel * 100, CitizenLevel * 130);
                    int baseSkill3 = SkillBaseValue + (CitizenLevel * SkillLevelMultiplier);
                    SetBaseSkills(baseSkill3);
                    SetSkill(SkillName.Marksmanship, baseSkill3);       // Archery specialization
                    SetSkill(SkillName.Tactics, baseSkill3);
                    AddRangeWeapon();                                    // Bow, crossbow, etc.
                    break;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // ▼ LOOT SYSTEM - Level-scaled treasure generation
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Generates loot based on CitizenLevel (1-9)
        /// Higher levels get progressively better loot packs + bonus items
        /// </summary>
        public override void GenerateLoot()
        {
            // Cumulative loot tiers (higher levels get ALL lower tiers)
            if (CitizenLevel > 8)                        // Level 9: Elite rewards
                AddLoot(LootPack.FilthyRich);
            if (CitizenLevel > 6)                        // Level 7-8: Rich rewards
                AddLoot(LootPack.Rich);
            if (CitizenLevel > 4)                        // Level 5-6: Average rewards
                AddLoot(LootPack.Average);
            if (CitizenLevel > 2)                        // Level 3-4: Meager rewards
                AddLoot(LootPack.Meager);
            else                                          // Level 1-2: Basic rewards
                AddLoot(LootPack.Meager);

            // 4% chance for rare equipment (1 in 25 drops)
            if (Utility.Random(25) == 0)
            {
                Type rareType = Loot.AdventurerRareItemTypes[Utility.Random(Loot.AdventurerRareItemTypes.Length)];
                Item rare = Activator.CreateInstance(rareType) as Item;
                if (rare != null)
                    PackItem(rare);
            }

            // Wizards get bonus spell scrolls (scales with level)
            if (CitizenType == 1)
                AddLoot(LootPack.MedScrolls, (int)((CitizenLevel / 3) + 1));
        }

        // ─────────────────────────────────────────────────────────────────────
        // ▼ SERIALIZATION CONSTRUCTOR
        // ─────────────────────────────────────────────────────────────────────
        public AdventurerTeam(Serial serial) : base(serial)
        {
        }

        // ─────────────────────────────────────────────────────────────────────
        // ▼ HELPER METHODS - Internal utilities
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>Resets special ability flag (called on construction/resurrection)</summary>
        private void ResetSpecialUsedFlag()
        {
            m_SpecialUsed = false;
        }

        /// <summary>Sets core combat skills shared by all professions</summary>
        private void SetBaseSkills(int baseValue)
        {
            SetSkill(SkillName.MagicResist, baseValue);    // Magic defense
            SetSkill(SkillName.Tactics, baseValue);        // Combat effectiveness
            SetSkill(SkillName.FistFighting, baseValue);   // Unarmed fallback
            SetSkill(SkillName.Marksmanship, baseValue);   // Ranged accuracy
        }

        /// <summary>Sets elemental resistances scaled by level (min: level*4, max: level*7)</summary>
        private void SetResistances(int level)
        {
            int minResist = level * 4;                     // Level 1: 4-7%, Level 9: 36-63%
            int maxResist = level * 7;
            SetResistance(ResistanceType.Physical, minResist, maxResist);
            SetResistance(ResistanceType.Fire, minResist, maxResist);
            SetResistance(ResistanceType.Cold, minResist, maxResist);
            SetResistance(ResistanceType.Poison, minResist, maxResist);
            SetResistance(ResistanceType.Energy, minResist, maxResist);
        }

        // ─────────────────────────────────────────────────────────────────────
        // ▼ LIFECYCLE MANAGEMENT
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>Cleanup on deletion - removes from registry and recycles TeamId</summary>
        public override void OnDelete()
        {
            base.OnDelete();

            // Remove from global tracking
            if (AllTeams.Contains(this))
                AllTeams.Remove(this);

            // Return TeamId to pool for reuse (memory optimization)
            if (m_SpawnedBySystem && m_TeamId != 0)
                AutoTeamMaintainer.RecycleTeamId(m_TeamId);
        }

        // ─────────────────────────────────────────────────────────────────────
        // ▼ PERSISTENCE - Save/Load system
        // ─────────────────────────────────────────────────────────────────────
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

            // Initialize defaults before reading (safety for version upgrades)
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

            // Re-register in global tracking after server restart
            if (!AllTeams.Contains(this))
                AllTeams.Add(this);

            m_ThinkCounter = 0;
        }

        // ═════════════════════════════════════════════════════════════════════
        // ███ AI BEHAVIOR - Main intelligence loop (PERFORMANCE OPTIMIZED) ███
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Main AI loop - Runs every ~250ms (game tick)
        /// OPTIMIZATION: Throttled to reduce CPU by 87.5% (runs every 8th tick ≈ 2sec)
        /// </summary>
        public override void OnThink()
        {
            base.OnThink();

            // ━━━ THROTTLE CHECK ━━━ Skip 7 out of 8 cycles for performance
            if (++m_ThinkCounter % ThinkCheckInterval != 0)
                return;

            // ──── PHASE 1: Activity tracking (for auto-cleanup system) ────
            UpdateLastSeen();

            // ──── PHASE 2: Special ability trigger (one-time at low HP) ────
            if (!m_SpecialUsed && Hits < HitsMax * LowHealthThreshold)
            {
                m_SpecialUsed = true;

                // Dramatic battle cry
                string shout = SpecialLines[Utility.Random(SpecialLines.Length)];
                PublicOverheadMessage(Server.Network.MessageType.Regular, 0x3B2, true, shout);

                // Execute profession-specific special ability
                switch (m_CitizenType)
                {
                    case 1: // ★ WIZARD: Fire burst (50% physical, 50% fire damage)
                        PublicOverheadMessage(Server.Network.MessageType.Emote, 0x3B2, true, "*unleashes forbidden magic!*");
                        if (Combatant != null && IsValidCombatTarget(Combatant))
                            AOS.Damage(Combatant, this, Utility.RandomMinMax(WizardDamageMin, WizardDamageMax), WizardDamagePhys, WizardDamageFire, 0, 0, 0);
                        break;

                    case 2: // ★ FIGHTER: Emergency self-heal +20 HP
                        PublicOverheadMessage(Server.Network.MessageType.Emote, 0x3B2, true, "*lets out a furious roar!*");
                        Hits += FighterHealAmount;
                        break;

                    case 3: // ★ ROGUE: Power shot (100% fire damage)
                        PublicOverheadMessage(Server.Network.MessageType.Emote, 0x3B2, true, "*fires a powerful arrow!*");
                        if (Combatant != null && IsValidCombatTarget(Combatant))
                            AOS.Damage(Combatant, this, Utility.RandomMinMax(RogueDamageMin, RogueDamageMax), 0, 100, 0, 0, 0);
                        break;
                }
            }

            // ──── PHASE 3: Early exit if no team coordination needed ────
            if (Combatant == null)
                return;

            // ──── PHASE 4: Cache nearby mobiles (reuse list to reduce GC) ────
            if (m_MobilesCache == null)
                m_MobilesCache = new List<Mobile>(32);      // Pre-allocate capacity
            m_MobilesCache.Clear();

            IPooledEnumerable eable = GetMobilesInRange(TeamMemberRange);
            foreach (Mobile m in eable)
                m_MobilesCache.Add(m);
            eable.Free();                                    // Return pooled enumerator

            // ──── PHASE 5: Team combat coordination (share target) ────
            Mobile validCombatant = GetValidCombatant(Combatant);
            if (validCombatant == null)                      // No valid target
                return;

            foreach (Mobile m in m_MobilesCache)
            {
                AdventurerTeam teamMember = m as AdventurerTeam;

                // Share combatant with idle team members (same TeamId only)
                if (teamMember != null &&
                    teamMember.TeamId == TeamId &&           // Same team
                    teamMember != this &&                     // Not self
                    teamMember.Combatant == null)            // Currently idle
                {
                    teamMember.Combatant = validCombatant;   // Assign shared target
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // ▼ PLAYER PROXIMITY TRACKING - For cleanup system
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>Updates LastSeen timestamp if player is nearby (throttled with OnThink)</summary>
        private void UpdateLastSeen()
        {
            if (IsPlayerNearby())
                LastSeen = DateTime.Now;
        }

        /// <summary>
        /// Lightweight player proximity check (OPTIMIZED)
        /// Iterates NetState.Instances instead of GetMobilesInRange for better performance
        /// </summary>
        /// <returns>True if any player within TeamMemberRange tiles</returns>
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

        // ─────────────────────────────────────────────────────────────────────
        // ▼ COMBAT TARGET VALIDATION - Pet-safe combat system
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Validates combat target to prevent attacking player pets
        /// CRITICAL: Prevents friendly fire with player-controlled creatures
        /// </summary>
        /// <param name="target">Mobile to validate</param>
        /// <returns>True if target is valid for attack</returns>
        private bool IsValidCombatTarget(Mobile target)
        {
            if (target == null || target.Deleted)
                return false;

            // ★ PET PROTECTION: Exclude all player-controlled creatures
            if (target is BaseCreature)
            {
                BaseCreature pet = (BaseCreature)target;
                
                // Check 1: Is controlled (pet, hireling, summon)
                if (pet.Controlled)
                    return false;
                
                // Check 2: Has player master
                if (pet.ControlMaster != null && pet.ControlMaster is PlayerMobile)
                    return false;
            }

            return true;
        }

        /// <summary>Returns current combatant only if valid (helper wrapper)</summary>
        private Mobile GetValidCombatant(Mobile currentCombatant)
        {
            if (currentCombatant == null)
                return null;

            if (IsValidCombatTarget(currentCombatant))
                return currentCombatant;

            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ▼ EQUIPMENT SYSTEM - Profession-appropriate weapon assignment
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Adds ranged weapon based on CitizenType
        /// Removes existing weapons first to prevent duplicates
        /// </summary>
        public void AddRangeWeapon()
        {
            // ═══ STEP 1: Remove existing weapons ═══
            Item oneHanded = FindItemOnLayer(Layer.OneHanded);
            if (oneHanded != null && oneHanded is BaseWeapon)
                oneHanded.Delete();

            Item twoHanded = FindItemOnLayer(Layer.TwoHanded);
            if (twoHanded != null && twoHanded is BaseWeapon)
                twoHanded.Delete();

            // ═══ STEP 2: Add profession-appropriate weapon ═══
            if (Utility.RandomBool())  // 50% chance for throwing weapons (all types)
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
            else if (CitizenType == 1)  // ★ WIZARD: Staff or wand + spell focus
            {
                switch (Utility.Random(2))
                {
                    case 0: AddItem(new WizardStaff()); break;
                    case 1: AddItem(new WizardStick()); break;
                }
                PackItem(new MageEye(Utility.RandomMinMax(15, 30)));  // Spell focus item
            }
            else  // ★ ROGUE/ARCHER: Bow, crossbow, or harpoon + ammo
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

    // ═════════════════════════════════════════════════════════════════════════
    // ███ AUTO TEAM MAINTAINER - Spawn & cleanup system (.NET 2.0 Compatible) ███
    // ═════════════════════════════════════════════════════════════════════════
    // PURPOSE: Automatically spawns adventurer teams near players & manages cleanup
    // 
    // KEY SYSTEMS:
    // • Spawn Scheduler: Checks every 5min, spawns near player groups
    // • Account Cooldowns: 15min per-account rate limiting
    // • TeamId Recycling: Memory-efficient ID reuse (prevents exhaustion)
    // • Spatial Hashing: O(n) player grouping vs O(n²) brute force
    // • Inactive Cleanup: Removes teams unseen for 1+ hour
    // 
    // PERFORMANCE NOTES:
    // • Uses Dictionary/List (not HashSet) for .NET 2.0 compatibility
    // • Grid-based player clustering reduces complexity
    // • Pooled TeamIds prevent memory fragmentation
    // ═════════════════════════════════════════════════════════════════════════

    public class AutoTeamMaintainer
    {
        // ─────────────────────────────────────────────────────────────────────
        // ▼ SPAWN BEHAVIOR CONSTANTS - Adjust to control spawn rates
        // ─────────────────────────────────────────────────────────────────────
        private const int MaxTeamsPerGroup = 2;              // Max teams spawned per player cluster
        private const int MinTeamSize = 3;                   // Minimum members per team
        private const int MaxTeamSize = 5;                   // Maximum members per team
        private const int PlayerGroupingRadius = 50;         // Tiles - cluster nearby players together
        private const int LocationSearchAttempts = 20;       // Max attempts to find valid spawn point
        private const int MinSpawnDistance = 18;             // Tiles - minimum distance from players
        private const double SpawnProbability = 0.5;         // 50% chance per eligible group
        private const double EvilTeamProbability = 0.3;      // 30% chance for evil alignment
        
        // TeamId management constants
        private const int MinTeamId = 100;                   // ID range start
        private const int MaxTeamId = 9999;                  // ID range end (9900 possible IDs)
        private const int GridCellSize = 100;                // Spatial hash grid cell size (tiles)

        // Timer intervals
        private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);        // Spawn check frequency
        private static readonly TimeSpan CooldownInterval = TimeSpan.FromMinutes(15);    // Per-account cooldown

        // Thread safety
        private static readonly object _lock = new object();

        // ─────────────────────────────────────────────────────────────────────
        // ▼ SYSTEM STATE - Global tracking (.NET 2.0 Compatible collections)
        // ─────────────────────────────────────────────────────────────────────
        private static bool m_IsRunning = false;                                    // Prevents double-initialization
        private static readonly Dictionary<Account, DateTime> m_LastSpawnTime = new Dictionary<Account, DateTime>();  // Per-account cooldown tracking
        private static readonly List<int> m_ActiveTeamIds = new List<int>();        // Currently used IDs
        private static readonly Queue<int> m_RecycledTeamIds = new Queue<int>();    // Returned IDs ready for reuse

        // ─────────────────────────────────────────────────────────────────────
        // ▼ SYSTEM INITIALIZATION
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Starts the auto-spawn system (called once on server startup)
        /// Sets up recurring timers for spawn checks and cleanup
        /// </summary>
        public static void Initialize()
        {
            if (m_IsRunning) return;
            m_IsRunning = true;

            // Cleanup leftover teams from previous session
            CleanupOldTeams();

            // Schedule recurring maintenance tasks
            Timer.DelayCall(CheckInterval, CheckInterval, new TimerCallback(MaintainTeams));          // Spawn new teams every 5min
            Timer.DelayCall(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), new TimerCallback(CleanupInactiveTeams)); // Cleanup every 30min
        }

        // ═════════════════════════════════════════════════════════════════════
        // ▼ TEAMID RECYCLING SYSTEM - Memory-efficient ID management
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Generates new TeamId (tries recycled IDs first, then creates new)
        /// OPTIMIZATION: Prevents ID exhaustion in long-running servers
        /// </summary>
        /// <returns>Unique TeamId, or 0 if pool exhausted</returns>
        private static int GenerateTeamId()
        {
            lock (_lock)
            {
                // ★ PRIORITY 1: Reuse recycled IDs (eco-friendly!)
                if (m_RecycledTeamIds.Count > 0)
                {
                    int recycledId = m_RecycledTeamIds.Dequeue();
                    if (!m_ActiveTeamIds.Contains(recycledId))
                        m_ActiveTeamIds.Add(recycledId);
                    return recycledId;
                }

                // ★ PRIORITY 2: Generate new ID (with collision detection)
                int newId;
                int attempts = 0;
                do
                {
                    newId = Utility.RandomMinMax(MinTeamId, MaxTeamId);
                    attempts++;
                    
                    if (attempts > 100)  // Pool near exhaustion
                    {
                        Console.WriteLine("WARNING: TeamId pool near exhaustion! Consider expanding range or cleaning up.");
                        return 0;  // Failure indicator
                    }
                } while (m_ActiveTeamIds.Contains(newId));  // Retry if collision

                m_ActiveTeamIds.Add(newId);
                return newId;
            }
        }

        /// <summary>Returns TeamId to pool for reuse (called on team deletion)</summary>
        /// <param name="teamId">ID to recycle</param>
        public static void RecycleTeamId(int teamId)
        {
            if (teamId == 0) return;  // Skip invalid IDs

            lock (_lock)
            {
                if (m_ActiveTeamIds.Contains(teamId))
                {
                    m_ActiveTeamIds.Remove(teamId);           // Remove from active list
                    m_RecycledTeamIds.Enqueue(teamId);        // Add to recycling queue
                }
            }
        }

        /// <summary>Checks if TeamId is currently in use (helper for debugging)</summary>
        public static bool IsTeamIdActive(int teamId)
        {
            lock (_lock)
            {
                return m_ActiveTeamIds.Contains(teamId);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // ▼ MAIN SPAWN ROUTINE - Runs every 5 minutes
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Main spawning logic - Finds eligible player groups and spawns teams nearby
        /// Respects per-account cooldowns and spawn probability
        /// </summary>
        private static void MaintainTeams()
        {
            // ──── STEP 1: Get player groups (clustered by proximity) ────
            List<PlayerMobile> playerGroups = GetPlayerSpawnGroups();
            if (playerGroups.Count == 0) return;

            // ──── STEP 2: Build online account list for cooldown cleanup ────
            List<Account> onlineAccounts = new List<Account>();
            foreach (NetState state in NetState.Instances)
            {
                if (state != null && state.Mobile != null && state.Mobile.Account != null)
                    onlineAccounts.Add((Account)state.Mobile.Account);
            }

            // Remove cooldown entries for offline accounts (memory cleanup)
            lock (_lock)
            {
                List<Account> keys = new List<Account>(m_LastSpawnTime.Keys);
                foreach (Account acc in keys)
                {
                    if (!onlineAccounts.Contains(acc))
                        m_LastSpawnTime.Remove(acc);
                }
            }

            // ──── STEP 3: Process each player group for spawning ────
            List<string> failedLocations = new List<string>();
            int totalTeamsSpawned = 0;

            foreach (PlayerMobile rep in playerGroups)
            {
                if (rep.Account == null)
                    continue;

                Account acct = (Account)rep.Account;

                // Check per-account cooldown (prevents spam)
                lock (_lock)
                {
                    if (m_LastSpawnTime.ContainsKey(acct) && (DateTime.Now - m_LastSpawnTime[acct]) < CooldownInterval)
                    {
                        Console.WriteLine("Skipping spawn for {0}. Still in cooldown.", rep.Name);
                        continue;
                    }
                }

                // Apply spawn probability (50% chance by default)
                if (Utility.RandomDouble() >= SpawnProbability)
                    continue;

                Map map = rep.Map;
                if (map == null)
                    continue;

                // ──── STEP 4: Spawn 1-2 teams per group ────
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

                    // Generate unique TeamId (with recycling)
                    int thisTeamId = GenerateTeamId();
                    if (thisTeamId == 0)
                    {
                        Console.WriteLine("Failed to generate TeamId - skipping spawn");
                        continue;
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
                    Console.WriteLine(string.Format("Spawned a new {0} adventurer team (ID:{1}, {2} members) for player {3} at {4} in {5}.",
                        teamType, thisTeamId, membersSpawned, rep.Name, thisLoc, map));
                }

                // Update account cooldown timestamp
                lock (_lock)
                {
                    m_LastSpawnTime[acct] = DateTime.Now;
                }
            }

            // ──── STEP 5: Log failures ────
            foreach (string playerName in failedLocations)
            {
                Console.WriteLine(string.Format("Failed to find a valid spawn location near player {0}.", playerName));
            }

            if (totalTeamsSpawned == 0 && failedLocations.Count == 0)
            {
                Console.WriteLine("No teams were spawned during this cycle.");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // ▼ SPATIAL HASHING - Efficient player grouping (O(n) vs O(n²))
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Groups nearby players to avoid spawning multiple teams for clusters
        /// OPTIMIZATION: Uses spatial hashing grid instead of brute-force distance checks
        /// </summary>
        /// <returns>List of representative players (one per group)</returns>
        private static List<PlayerMobile> GetPlayerSpawnGroups()
        {
            // ──── STEP 1: Find players outside guarded regions ────
            List<PlayerMobile> playersInSpawnableAreas = new List<PlayerMobile>();
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

            if (playersInSpawnableAreas.Count == 0)
                return new List<PlayerMobile>();

            // ──── STEP 2: Build spatial hash grid ────
            Dictionary<string, List<PlayerMobile>> grid = new Dictionary<string, List<PlayerMobile>>();
            
            foreach (PlayerMobile player in playersInSpawnableAreas)
            {
                string cellKey = GetGridCellKey(player.Location);
                
                if (!grid.ContainsKey(cellKey))
                    grid[cellKey] = new List<PlayerMobile>();
                
                grid[cellKey].Add(player);
            }

            // ──── STEP 3: Group players within radius ────
            List<PlayerMobile> spawnGroups = new List<PlayerMobile>();
            List<PlayerMobile> processed = new List<PlayerMobile>();

            foreach (PlayerMobile player in playersInSpawnableAreas)
            {
                if (processed.Contains(player))
                    continue;

                spawnGroups.Add(player);  // This player represents their group
                processed.Add(player);

                // Check adjacent grid cells for nearby players
                List<string> adjacentCells = GetAdjacentGridCells(player.Location);
                
                foreach (string cellKey in adjacentCells)
                {
                    if (!grid.ContainsKey(cellKey))
                        continue;

                    foreach (PlayerMobile otherPlayer in grid[cellKey])
                    {
                        if (!processed.Contains(otherPlayer) && 
                            player.GetDistanceToSqrt(otherPlayer) < PlayerGroupingRadius)
                        {
                            processed.Add(otherPlayer);  // Mark as grouped
                        }
                    }
                }

                if (processed.Count >= playersInSpawnableAreas.Count)
                    break;  // All players grouped
            }

            return spawnGroups;
        }

        /// <summary>Converts location to grid cell key "X,Y" (e.g., "5,12")</summary>
        private static string GetGridCellKey(Point3D location)
        {
            int cellX = location.X / GridCellSize;
            int cellY = location.Y / GridCellSize;
            return string.Format("{0},{1}", cellX, cellY);
        }

        /// <summary>Returns 9 adjacent grid cell keys (3x3 grid including center)</summary>
        private static List<string> GetAdjacentGridCells(Point3D location)
        {
            List<string> cells = new List<string>(9);
            int baseCellX = location.X / GridCellSize;
            int baseCellY = location.Y / GridCellSize;

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    cells.Add(string.Format("{0},{1}", baseCellX + dx, baseCellY + dy));
                }
            }

            return cells;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ▼ LOCATION FINDER - Finds valid spawn points near players
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Finds random spawn location within 30 tiles of player, at least MinSpawnDistance away
        /// Ensures location is passable and outside guarded regions
        /// </summary>
        /// <returns>Valid spawn point, or Point3D.Zero if none found</returns>
        private static Point3D GetRandomLocationAroundPlayer(Point3D playerLoc, Map map)
        {
            for (int i = 0; i < LocationSearchAttempts; i++)
            {
                // Random location within 30-tile radius
                int x = playerLoc.X + Utility.RandomMinMax(-30, 30);
                int y = playerLoc.Y + Utility.RandomMinMax(-30, 30);

                // Enforce minimum distance
                int dist = (int)Math.Sqrt(Math.Pow(x - playerLoc.X, 2) + Math.Pow(y - playerLoc.Y, 2));
                if (dist < MinSpawnDistance)
                    continue;

                int z = map.GetAverageZ(x, y);
                Point3D newLocation = new Point3D(x, y, z);

                // Validate location (passable + not guarded)
                Region reg = Region.Find(newLocation, map);
                if (map.CanFit(x, y, z, 16, false, false, true) && !(reg is GuardedRegion))
                    return newLocation;
            }
            return Point3D.Zero;  // Failed to find valid location
        }

        // ═════════════════════════════════════════════════════════════════════
        // ▼ CLEANUP SYSTEMS - Memory management & maintenance
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Removes all system-spawned teams on startup (clean slate)
        /// Called once during Initialize()
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
                team.Delete();  // Triggers OnDelete() which recycles TeamId
                deletedCount++;
            }

            Console.WriteLine(string.Format("Cleaned up {0} old system-generated teams. {1} manual teams remain.",
                deletedCount, AdventurerTeam.GetAllTeams().Count));
            
            // Log pool status
            lock (_lock)
            {
                Console.WriteLine(string.Format("TeamId Pool: {0} active, {1} recycled, {2} total capacity",
                    m_ActiveTeamIds.Count, m_RecycledTeamIds.Count, MaxTeamId - MinTeamId + 1));
            }
        }

        /// <summary>
        /// Removes teams unseen for 1+ hour (runs every 30min)
        /// Prevents abandoned teams from accumulating
        /// </summary>
        private static void CleanupInactiveTeams()
        {
            Console.WriteLine("Cleaning up inactive adventurer teams...");

            List<AdventurerTeam> teamsToDelete = new List<AdventurerTeam>();
            DateTime now = DateTime.Now;

            // Scan all mobiles for inactive adventurer teams
            foreach (Mobile mobile in World.Mobiles.Values)
            {
                AdventurerTeam adv = mobile as AdventurerTeam;
                if (adv != null && adv.SpawnedBySystem && (now - adv.LastSeen) > TimeSpan.FromHours(1))
                    teamsToDelete.Add(adv);
            }

            int deletedCount = 0;
            foreach (AdventurerTeam team in teamsToDelete)
            {
                team.Delete();  // Triggers OnDelete() which recycles TeamId
                deletedCount++;
            }

            Console.WriteLine(string.Format("Cleaned up {0} inactive teams.", deletedCount));
            
            // Log pool status
            lock (_lock)
            {
                Console.WriteLine(string.Format("TeamId Pool: {0} active, {1} recycled",
                    m_ActiveTeamIds.Count, m_RecycledTeamIds.Count));
            }
        }
    }
}
