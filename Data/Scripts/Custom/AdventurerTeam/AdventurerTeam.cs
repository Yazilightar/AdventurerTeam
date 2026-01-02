using System;
using System.Collections.Generic;
using Server;
using Server.Items;
using Server.Mobiles;
using Server.Network;
using Server.Regions;
using Server.Misc;

namespace Server.Scripts.Custom
{
    // Defines the three types of adventurers in the team
    public enum CitizenClass
    {
        Wizard = 1,
        Fighter = 2,
        Rogue = 3
    }

    [CorpseName("an adventurer corpse")]
    public class AdventurerTeam : BaseCreature
    {
        #region Dialogue Data (Static)
        // Static arrays are used to save memory across multiple instances.
        
        private static readonly string[] FriendlyGreetings = new string[]
        {
            "Safe travels, friend.", "Hail, traveler!", "Well met!", 
            "Greetings.", "Good day to you.", "May the virtues guide you."
        };

        private static readonly string[] EvilGreetings = new string[]
        {
            "Stay out of our way.", "Keep walking, fool.", 
            "What are you looking at?", "You're in the wrong neighborhood.", "Begone."
        };

        private static readonly string[] IdleChat = new string[]
        {
            "The wyrms in the deep caves grow bolder...",
            "Careful where you tread - traps abound.",
            "We need to restock supplies soon.",
            "I seek a legendary blade, lost to time.",
            "Did you hear that sound?",
            "Stay sharp, everyone."
        };

        private static readonly string[] CombatYell = new string[]
        {
            "Surround them!", "Focus fire!", "Shield wall!", 
            "Healer down! Protect them!", "For glory!", "Fight or die!"
        };

        private static readonly string[] VictoryLines = new string[]
        {
            "Target down!", "Good fight!", "Another one bites the dust.", 
            "Clear!", "Well fought, friends."
        };

        private static readonly string[] PotionLines = new string[]
        {
            "*drinks potion*", "That's better!", "Glug, glug...", "Aha!"
        };

        private static readonly string[] BandageLines = new string[]
        {
            "*applies bandages*", "Stopping the blood...", "Hold still."
        };

        private static readonly string[] HealSpellLines = new string[]
        {
            "In Vas Mani!", "Be healed!", "The light mends you."
        };
        #endregion

        #region Configuration
        // Limits how often the NPC can speak to prevent chat spam
        private static readonly TimeSpan SpeechThrottle = TimeSpan.FromSeconds(2.0);
        
        // Match standard client screen range (16 tiles)
        private const int TeamMemberRange = 16;
        
        // Thresholds for triggering healing logic
        private const double HealSelfThreshold = 0.50;
        private const double HealAllyThreshold = 0.60;
        #endregion

        #region Instance Fields
        private int m_CitizenType;
        private int m_CitizenLevel;
        private bool m_SpawnedBySystem;
        private bool m_IsEvil;
        private bool m_SpawnMounted;

        // Timers for AI decisions
        private DateTime m_LastMessageTime;
        private DateTime m_NextChatTime;
        private DateTime m_NextCombatYell;
        private DateTime m_NextHealCheck;
        private DateTime m_PendingDeparture;
        private bool m_IsLeaving;
        
        private bool m_IsUsingBandage;

        // Internal reference to the team ID for cross-healing logic
        private int m_TeamId; 
        #endregion

        #region Properties
        [CommandProperty(AccessLevel.Owner)]
        public CitizenClass CitizenClass
        {
            get { return (CitizenClass)m_CitizenType; }
            set { m_CitizenType = (int)value; InvalidateProperties(); }
        }

        [CommandProperty(AccessLevel.Owner)]
        public int CitizenLevel
        {
            get { return m_CitizenLevel; }
            set { m_CitizenLevel = value; InvalidateProperties(); }
        }

        public bool SpawnedBySystem
        {
            get { return m_SpawnedBySystem; }
            set { m_SpawnedBySystem = value; }
        }

        [CommandProperty(AccessLevel.GameMaster)]
        public bool IsEvil
        {
            get { return m_IsEvil; }
            set { m_IsEvil = value; }
        }

        [CommandProperty(AccessLevel.GameMaster)]
        public int TeamId
        {
            get { return m_TeamId; }
            set { m_TeamId = value; }
        }

        // Native Integration: Allows the Core AI to handle looting automatically
        public override bool CanRummageCorpses { get { return true; } }
        
        // Native Integration: Sets Karma status
        public override bool AlwaysMurderer { get { return m_IsEvil; } }
        #endregion

        #region Constructors
        [Constructable]
        public AdventurerTeam() : this(0, false, false) { }

        [Constructable]
        public AdventurerTeam(int teamId, bool isEvil) : this(teamId, isEvil, false) { }

        [Constructable]
        public AdventurerTeam(int teamId, bool isEvil, bool mounted) 
            : base(AIType.AI_Melee, FightMode.Closest, 10, 1, 0.2, 0.4)
        {
            // Assign Native Team ID: Mobs with the same ID will consider each other allies
            if (teamId != 0)
                this.Team = teamId;

            m_TeamId = teamId; 
            m_IsEvil = isEvil;
            m_SpawnedBySystem = (teamId != 0);
            m_SpawnMounted = mounted;

            // Use Closest targeting strategy. RangePerception set to 16 to match screen size.
            FightMode = FightMode.Closest; 
            RangePerception = TeamMemberRange; 

            InitStatsAndAppearance();

            // Set initial timers
            DateTime now = DateTime.UtcNow;
            m_NextChatTime = now.AddSeconds(Utility.RandomMinMax(10, 30));
            m_PendingDeparture = now.AddMinutes(Utility.RandomMinMax(20, 40));
        }

        public AdventurerTeam(Serial serial) : base(serial) { }
        #endregion

        #region Native Overrides (Sound & Behavior)
        // Use standard human sounds based on gender
        public override int GetHurtSound() { return Female ? 0x14D : 0x156; }
        public override int GetDeathSound() { return Female ? 0x150 : 0x159; }
        public override int GetAttackSound() { return 0x258; }

        public override void OnGotMeleeAttack(Mobile attacker)
        {
            base.OnGotMeleeAttack(attacker);
            // Small chance to cry out using external library when hit
            if (Utility.RandomDouble() < 0.25)
                Server.Misc.IntelligentAction.CryOut(this);
        }
        #endregion

        #region Core Logic (OnThink)
        public override void OnThink()
        {
            // Always call base to let Core AI handle movement and combat
            base.OnThink(); 
            
            if (Deleted || Map == null || Map == Map.Internal) return;

            DateTime now = DateTime.UtcNow;

            // Logic: Despawn the team after a set duration if not fighting
            if (!m_IsLeaving && m_TeamId != 0 && now > m_PendingDeparture)
            {
                if (Combatant == null && Utility.RandomDouble() < 0.05)
                {
                    m_IsLeaving = true;
                    if (CanSendMessage(now)) Say("Time to move on."); 
                    Timer.DelayCall(TimeSpan.FromSeconds(5.0), new TimerCallback(ExecuteDeparture));
                }
            }

            // Logic: Check if we or allies need healing (runs every 2.5s)
            if (now > m_NextHealCheck)
            {
                m_NextHealCheck = now + TimeSpan.FromSeconds(2.5);
                PerformCombatSupport(now);
            }

            // Logic: Idle Chatter when not fighting
            if (Combatant == null && now > m_NextChatTime)
            {
                if (CanSendMessage(now))
                {
                    Say(GetPooledMessage(IdleChat));
                    m_NextChatTime = now + TimeSpan.FromSeconds(Utility.RandomMinMax(15, 45));
                }
            }
        }

        // Handles Self-Healing and Wizard Cross-Healing
        private void PerformCombatSupport(DateTime now)
        {
            double hpRatio = (double)Hits / HitsMax;
            
            // 1. Heal Self
            if (hpRatio < HealSelfThreshold)
            {
                TryHealSelf(now);
            }

            // 2. Heal Allies (Wizard only)
            if (m_CitizenType == (int)CitizenClass.Wizard && Mana > 10)
            {
                AdventurerTeam injuredAlly = null;
                // Scan nearby mobiles
                foreach (Mobile m in this.GetMobilesInRange(TeamMemberRange))
                {
                    // Cast to BaseCreature to access the Team property safely
                    BaseCreature bc = m as BaseCreature;
                    
                    // Check: Is Adventurer? Same Team? Not Self? Alive? Within Spell Range (12)?
                    if (bc != null && bc is AdventurerTeam && bc.Team == this.Team && bc != this && bc.Alive && bc.InRange(this, 12))
                    {
                        if (bc.Hits < (bc.HitsMax * HealAllyThreshold))
                        {
                            injuredAlly = (AdventurerTeam)bc;
                            break; // Heal the first injured ally found
                        }
                    }
                }

                if (injuredAlly != null)
                {
                    DoMagicHeal(injuredAlly);
                }
            }
        }
        #endregion

        #region Combat & Movement Events
        public override void OnCombatantChange()
        {
            base.OnCombatantChange();
            
            if (Combatant != null)
            {
                // Trigger combat yell on new target
                DateTime now = DateTime.UtcNow;
                if (now > m_NextCombatYell && CanSendMessage(now) && Utility.RandomDouble() < 0.3)
                {
                    Say(GetPooledMessage(CombatYell));
                    m_NextCombatYell = now + TimeSpan.FromSeconds(10.0);
                }
            }
            else
            {
                // Chance to celebrate victory
                if (Utility.RandomDouble() < 0.4 && CanSendMessage(DateTime.UtcNow))
                    Say(GetPooledMessage(VictoryLines));
            }
        }

        public override void OnMovement(Mobile m, Point3D oldLocation)
        {
            // Simple greeting system for players
            if (m_IsLeaving || Deleted || m == null || !m.Alive || !m.Player) return;

            DateTime now = DateTime.UtcNow;
            // Only greet if visible, close enough, and speech throttle allows
            if (CanSendMessage(now) && CanSee(m) && m.InRange(this, 8))
            {
                string[] source = m_IsEvil ? EvilGreetings : FriendlyGreetings;
                Say(GetPooledMessage(source));
            }
        }
        #endregion

        #region Actions (Heal/Support)
        private void TryHealSelf(DateTime now)
        {
            // 1. Try Potion
            BaseHealPotion potion = Backpack.FindItemByType(typeof(BaseHealPotion)) as BaseHealPotion;
            if (potion != null)
            {
                potion.Drink(this);
                PublicOverheadMessage(MessageType.Emote, 0x3B2, true, GetPooledMessage(PotionLines));
                return;
            }

            // 2. Try Bandage (Fighters/Rogues)
            if (m_CitizenType != (int)CitizenClass.Wizard)
            {
                if (!m_IsUsingBandage)
                {
                    Bandage bandage = Backpack.FindItemByType(typeof(Bandage)) as Bandage;
                    if (bandage != null)
                    {
                        m_IsUsingBandage = true;
                        bandage.Consume(1);
                        PublicOverheadMessage(MessageType.Emote, 0x3B2, true, GetPooledMessage(BandageLines));
                        
                        // Delay to simulate bandage timer
                        Timer.DelayCall(TimeSpan.FromSeconds(4.0), delegate 
                        { 
                            if (!Deleted && Alive) 
                            {
                                Heal(Utility.RandomMinMax(20, 40)); 
                                PlaySound(0x57);
                                m_IsUsingBandage = false;
                            }
                        });
                    }
                }
            }
            else
            {
                // 3. Wizard uses magic
                DoMagicHeal(this);
            }
        }

        private void DoMagicHeal(Mobile target)
        {
            if (Mana < 10) return;
            
            Mana -= 10;
            if (target != this) Direction = GetDirectionTo(target);
            
            Animate(17, 7, 1, true, false, 0);
            PlaySound(0x1F2);
            
            PublicOverheadMessage(MessageType.Emote, 0x3B2, true, GetPooledMessage(HealSpellLines));
            
            // Casting Delay
            Timer.DelayCall(TimeSpan.FromSeconds(1.0), delegate
            {
                if (!Deleted && Alive && target.Alive && target.Map == Map && target.InRange(this, 12))
                {
                    target.Heal(Utility.RandomMinMax(20, 35));
                    target.FixedParticles(0x376A, 9, 32, 5030, EffectLayer.Waist);
                }
            });
        }
        #endregion

        #region Setup (Stats, DressUp, Mounts)
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
                FightMode = FightMode.Good; // Evil fights Good
            }
            else
            {
                Title = TavernPatrons.GetTitle();
                Hue = Utility.RandomSkinHue();
                FightMode = FightMode.Evil; // Good fights Evil
            }

            Utility.AssignRandomHair(this);
            SpeechHue = Utility.RandomTalkHue();
            HairHue = FacialHairHue = Utility.RandomHairHue();

            int baseSkill = 25 + (m_CitizenLevel * 10);
            int strMin = m_CitizenLevel * 20, strMax = m_CitizenLevel * 30;
            int dexMin = m_CitizenLevel * 20, dexMax = m_CitizenLevel * 30;
            int intMin = m_CitizenLevel * 20, intMax = m_CitizenLevel * 30;
            int hitsMin = m_CitizenLevel * 30, hitsMax = m_CitizenLevel * 40;
            int resistMin = m_CitizenLevel * 3, resistMax = m_CitizenLevel * 6;

            int type = Utility.Random(3);
            switch (type)
            {
                case 0: // Wizard
                    IntelligentAction.DressUpWizards(this, false);
                    m_CitizenType = (int)CitizenClass.Wizard;
                    AI = AIType.AI_Mage;
                    
                    // Full caster skills
                    SetSkill(SkillName.Psychology, baseSkill);
                    SetSkill(SkillName.Magery, baseSkill);
                    SetSkill(SkillName.Meditation, baseSkill);
                    SetSkill(SkillName.MagicResist, baseSkill);
                    SetSkill(SkillName.FistFighting, baseSkill); // Defensive wrestling
                    SetSkill(SkillName.Tactics, baseSkill - 20);
                    intMax += m_CitizenLevel * 30;
                    break;

                case 1: // Fighter
                    // 'true' for isTown (standard guard look). Weapon override handled by AddWeapon.
                    IntelligentAction.DressUpFighters(this, "", m_IsEvil, false, true);
                    m_CitizenType = (int)CitizenClass.Fighter;
                    AI = AIType.AI_Melee;
                    
                    // Full melee skills ensuring proficiency with any weapon
                    SetSkill(SkillName.Fencing, baseSkill);
                    SetSkill(SkillName.Bludgeoning, baseSkill);
                    SetSkill(SkillName.Swords, baseSkill);
                    SetSkill(SkillName.Parry, baseSkill);
                    SetSkill(SkillName.MagicResist, baseSkill);
                    SetSkill(SkillName.Tactics, baseSkill + 10);
                    SetSkill(SkillName.Healing, baseSkill + 10);
                    SetSkill(SkillName.Anatomy, baseSkill);
                    
                    strMax += m_CitizenLevel * 10;
                    hitsMax += m_CitizenLevel * 20;
                    resistMax += m_CitizenLevel * 3;
                    break;

                case 2: // Rogue
                    IntelligentAction.DressUpRogues(this, "", m_IsEvil, false, true);
                    m_CitizenType = (int)CitizenClass.Rogue;
                    AI = AIType.AI_Archer;
                    
                    // Full ranged skills
                    SetSkill(SkillName.Marksmanship, baseSkill);
                    SetSkill(SkillName.Tactics, baseSkill);
                    SetSkill(SkillName.MagicResist, baseSkill);
                    SetSkill(SkillName.Healing, baseSkill);
                    SetSkill(SkillName.Anatomy, baseSkill - 10);
                    
                    dexMax += m_CitizenLevel * 10;
                    resistMax += m_CitizenLevel * 2;
                    break;
            }

            // Manual Mount Control: Enforce team consistency
            if (m_SpawnMounted)
            {
                if (this.Mount == null) new Horse().Rider = this;
            }
            else
            {
                if (this.Mount != null)
                {
                    IMount mount = this.Mount;
                    mount.Rider = null;
                    if (mount is Mobile) ((Mobile)mount).Delete();
                }
            }

            SetStr(strMin, strMax);
            SetDex(dexMin, dexMax);
            SetInt(intMin, intMax);
            SetHits(hitsMin, hitsMax);
            int finalResist = (resistMax > 75) ? 75 : resistMax;
            for (int r = 0; r <= (int)ResistanceType.Energy; r++)
                SetResistance((ResistanceType)r, resistMin, finalResist);

            AddWeapon(true);
            AddHealingSupplies();
        }

        public void AddWeapon(bool initial)
        {
            BaseWeapon hand = FindItemOnLayer(Layer.OneHanded) as BaseWeapon;
            BaseWeapon twohand = FindItemOnLayer(Layer.TwoHanded) as BaseWeapon;

            if (!initial && (hand != null || twohand != null)) return;

            // Fighter: Random melee weapon
            if (m_CitizenType == (int)CitizenClass.Fighter)
            {
                if (hand != null || twohand != null) return;
                switch (Utility.Random(3))
                {
                    case 0: AddItem(new Longsword()); break;
                    case 1: AddItem(new BattleAxe()); break;
                    case 2: AddItem(new Mace()); break;
                }
                return;
            }

            // Clean up existing weapons for other classes (overriding DressUp defaults)
            if (initial)
            {
                if (hand != null) hand.Delete();
                if (twohand != null) twohand.Delete();
            }

            // Throwing Gloves Logic (Rare)
            if (Utility.RandomBool() && (m_CitizenType != (int)CitizenClass.Fighter))
            {
                Item gloves = new Item(0x13C6); gloves.Name = "Throwing Gloves"; AddItem(gloves);
                Item ammo = new Item(0xF0E); ammo.Name = "Throwing Ammunition"; PackItem(ammo);
                return;
            }

            // Wizard Weapons
            if (m_CitizenType == (int)CitizenClass.Wizard)
            {
                AddItem(Utility.RandomBool() ? (Item)new GnarledStaff() : new QuarterStaff());
            }
            // Rogue Weapons
            else if (m_CitizenType == (int)CitizenClass.Rogue)
            {
                int ammoCount = Utility.RandomMinMax(60, 100);
                switch (Utility.Random(8))
                {
                    case 0: case 5: AddItem(new Bow()); PackItem(new Arrow(ammoCount)); break;
                    case 1: case 6: case 7: AddItem(new Crossbow()); PackItem(new Bolt(ammoCount)); break;
                    case 2: AddItem(new HeavyCrossbow()); PackItem(new Bolt(ammoCount)); break;
                    case 3: AddItem(new RepeatingCrossbow()); PackItem(new Bolt(ammoCount)); break;
                    case 4: AddItem(new CompositeBow()); PackItem(new Arrow(ammoCount)); break;
                }
            }
        }

        private void AddHealingSupplies()
        {
            int potionCount = Utility.RandomMinMax(3, 5);
            int bandageCount = Utility.RandomMinMax(20, 40);

            if (m_CitizenType != (int)CitizenClass.Wizard)
                PackItem(new Bandage(bandageCount));

            for (int i = 0; i < potionCount; i++)
                PackItem(new HealPotion());
        }
        #endregion

        #region Helpers & Serialization
        private bool CanSendMessage(DateTime now)
        {
            if ((now.Ticks - m_LastMessageTime.Ticks) < SpeechThrottle.Ticks) return false;
            m_LastMessageTime = now;
            return true;
        }

        private string GetPooledMessage(string[] source)
        {
            if (source == null || source.Length == 0) return "";
            return source[Utility.Random(source.Length)];
        }

        private void ExecuteDeparture()
        {
            if (m_TeamId != 0) AutoTeamMaintainer.RecycleTeamId(m_TeamId);
            Delete();
        }

        public override void GenerateLoot()
        {
            if (m_CitizenLevel >= 7) AddLoot(LootPack.Rich);
            else if (m_CitizenLevel >= 5) AddLoot(LootPack.Average);
            else AddLoot(LootPack.Meager);

            // Rare item drop chance (1/25)
            if (Utility.Random(25) == 0)
            {
                Type rareType = Loot.AdventurerRareItemTypes[Utility.Random(Loot.AdventurerRareItemTypes.Length)];
                Item rare = Activator.CreateInstance(rareType) as Item;
                if (rare != null) PackItem(rare);
            }

            if (m_CitizenType == (int)CitizenClass.Wizard)
                AddLoot(LootPack.MedScrolls, (m_CitizenLevel / 3) + 1);
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write((int)0);
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);
            int version = reader.ReadInt();
            Timer.DelayCall(TimeSpan.Zero, new TimerCallback(Delete));
        }
        #endregion
    }

    // ========================================================================
    // AUTO TEAM MAINTAINER
    // ========================================================================
    public static class AutoTeamMaintainer
    {
        private static bool s_Enabled = true;
        private static Timer s_MaintenanceTimer;
        private static readonly Queue<int> s_RecycledIds = new Queue<int>();
        private static int s_NextTeamId = 1;
        private static readonly object s_IdLock = new object();

        // Spawn distance settings
        private const int MaxSpawnDist = 35;
        private const int MinSpawnDist = 24;

        public static void Initialize()
        {
            if (s_MaintenanceTimer != null) s_MaintenanceTimer.Stop();
            // Check every 2 minutes
            s_MaintenanceTimer = Timer.DelayCall(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), MaintainTeams);
        }

        public static int GetNewTeamId()
        {
            lock (s_IdLock) return (s_RecycledIds.Count > 0) ? s_RecycledIds.Dequeue() : s_NextTeamId++;
        }

        public static void RecycleTeamId(int id)
        {
            lock (s_IdLock) s_RecycledIds.Enqueue(id);
        }

        private static void MaintainTeams()
        {
            if (!s_Enabled) return;
            
            // Loop through all connected players
            foreach (NetState state in NetState.Instances)
            {
                Mobile m = state.Mobile;
                // Valid player in the world
                if (m != null && m.Player && m.Alive && m.Map != null && m.Map != Map.Internal)
                {
                    // 60% chance to spawn a team per player per check cycle (Optimized for Single Player)
                    if (Utility.RandomDouble() < 0.6) 
                        TrySpawnTeamForPlayer(m);
                }
            }
        }

        private static void TrySpawnTeamForPlayer(Mobile pm)
        {
            // Density Check: Don't spawn if there are already Adventurers nearby
            int nearbyCount = 0;
            foreach (Mobile m in pm.GetMobilesInRange(40))
            {
                if (m is AdventurerTeam) nearbyCount++;
            }
            if (nearbyCount > 5) return;

            // Attempt to find a valid location
            Point3D spawnLoc = FindSpawnLocation(pm);
            if (spawnLoc == Point3D.Zero) return;

            // Generate team parameters
            int teamId = GetNewTeamId();
            bool isEvil = Utility.RandomBool();
            bool mounted = Utility.RandomDouble() < 0.4;
            int size = Utility.RandomMinMax(2, 5);

            for (int i = 0; i < size; i++)
            {
                AdventurerTeam npc = new AdventurerTeam(teamId, isEvil, mounted);
                npc.MoveToWorld(spawnLoc, pm.Map);
            }
        }

        private static Point3D FindSpawnLocation(Mobile nearPlayer)
        {
            Map map = nearPlayer.Map;
            for (int i = 0; i < 5; i++)
            {
                int dist = Utility.RandomMinMax(MinSpawnDist, MaxSpawnDist);
                double ang = Utility.RandomDouble() * Math.PI * 2;
                int x = nearPlayer.X + (int)(Math.Cos(ang) * dist);
                int y = nearPlayer.Y + (int)(Math.Sin(ang) * dist);
                Point3D p = new Point3D(x, y, map.GetAverageZ(x, y));

                if (IsInForbiddenRegion(p, map))
                    continue;

                // Check for valid terrain and not in guarded area
                if (map.CanSpawnMobile(p))
                    return p;
            }
            return Point3D.Zero;
        }

        private static bool IsInForbiddenRegion(Point3D loc, Map map)
        {
            Region reg = Region.Find(loc, map);
            if (reg == null) return false;

            // Strict region filtering to prevent spawns in towns, houses, or dungeons
            if (reg is WantedRegion ||
                reg is SavageRegion ||
                reg is VillageRegion ||
                reg is UnderHouseRegion ||
                reg is UmbraRegion ||
                reg is TownRegion ||
                reg is StartRegion ||
                reg is SkyHomeDwelling ||
                reg is SafeRegion ||
                reg is ProtectedRegion ||
                reg is PublicRegion ||
                reg is PirateRegion ||
                reg is BardTownRegion ||
                reg is DawnRegion ||
                reg is DungeonHomeRegion ||
                reg is GargoyleRegion ||
                reg is GuardedRegion ||
                reg is HouseRegion ||
                reg is LunaRegion ||
                reg is MazeRegion ||
                reg is MoonCore)
            {
                return true;
            }

            return false;
        }
    }
}
