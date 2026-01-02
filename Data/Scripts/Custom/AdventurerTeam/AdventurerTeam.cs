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
    public enum CitizenClass
    {
        Wizard = 1,
        Fighter = 2,
        Rogue = 3
    }

    [CorpseName("an adventurer corpse")]
    public class AdventurerTeam : BaseCreature
    {
        #region Dialogue Data (Static / Flavor Text)
        // Static arrays to save memory. 
        
        private static readonly string[] FriendlyGreetings = new string[]
        {
            "Safe travels, friend.", "Hail, traveler!", "Well met!", 
            "Greetings.", "Good day to you.", "May the virtues guide you."
        };

        private static readonly string[] EvilGreetings = new string[]
        {
            "Stay out of our way.", "Keep walking, fool.", 
            "You're in the wrong neighborhood.", "Begone."
        };

        private static readonly string[] FriendlyChat = new string[]
        {
            "The wyrms in the deep caves grow bolder...",
            "Careful where you tread - traps abound.",
            "We need to restock supplies soon.",
            "I seek a legendary blade, lost to time.",
            "Did you hear that sound?",
            "Stay sharp, everyone.",
            "I've heard rumors of a hidden vault nearby."
        };

        private static readonly string[] EvilChat = new string[]
        {
            "Your coin or your life, fool.",
            "Fresh meat for the crows...",
            "This is OUR territory.",
            "I smell fear... and gold.",
            "Only the strong survive here.",
            "Stop whining and keep moving."
        };

        private static readonly string[] CombatYell = new string[]
        {
            "Surround them!", "Focus fire!", "Shield wall!", 
            "Healer down! Protect them!", "For glory!", "Fight or die!",
            "Don't let them escape!"
        };

        private static readonly string[] VictoryLines = new string[]
        {
            "Target down!", "Good fight!", "Another one bites the dust.", 
            "Clear! Check the bodies.", "Well fought, friends.",
            "Is everyone alright?"
        };

        private static readonly string[] RetreatLines = new string[]
        {
            "Fall back! I'm badly wounded!", "Retreating! Cover me!",
            "Not dying here today!", "Tactical withdrawal!"
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
        // Prevent chat spam
        private static readonly TimeSpan SpeechThrottle = TimeSpan.FromSeconds(2.0);
        
        // AI Logic Thresholds
        private const int TeamMemberRange = 16;
        private const double HealSelfThreshold = 0.50;
        private const double HealAllyThreshold = 0.60;
        private const double RetreatThreshold = 0.25; // Run away if HP < 25%
        #endregion

        #region Instance Fields
        private int m_CitizenType;
        private int m_CitizenLevel;
        private bool m_SpawnedBySystem;
        private bool m_IsEvil;
        private bool m_SpawnMounted;
        private int m_TeamId; // Native Team ID usage

        // AI Timers
        private DateTime m_LastMessageTime;
        private DateTime m_NextChatTime;
        private DateTime m_NextHealCheck;
        private DateTime m_PendingDeparture;
        
        // State Flags
        private bool m_IsLeaving;
        private bool m_IsUsingBandage;
        private bool m_IsRetreating;
        private DateTime m_RetreatResetTime;
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

        public override bool CanRummageCorpses { get { return true; } }
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
            // Assign Native Team ID for simple friend/foe logic
            if (teamId != 0)
                this.Team = teamId;

            m_TeamId = teamId; 
            m_IsEvil = isEvil;
            m_SpawnedBySystem = (teamId != 0);
            m_SpawnMounted = mounted;

            FightMode = FightMode.Closest; 
            RangePerception = TeamMemberRange; 

            InitStatsAndAppearance();
            
            // Critical: Enforce mount state AFTER all other equipment is added
            EnforceMountState(mounted);

            DateTime now = DateTime.UtcNow;
            m_NextChatTime = now.AddSeconds(Utility.RandomMinMax(10, 30));
            m_PendingDeparture = now.AddMinutes(Utility.RandomMinMax(20, 40));
        }

        public AdventurerTeam(Serial serial) : base(serial) { }
        #endregion

        #region Core Logic (OnThink)
        public override void OnThink()
        {
            base.OnThink();
            if (Deleted || Map == null || Map == Map.Internal) return;

            DateTime now = DateTime.UtcNow;

            // 1. Retreat Logic Handling
            if (m_IsRetreating)
            {
                if (now > m_RetreatResetTime)
                {
                    m_IsRetreating = false; // Stop retreating after a few seconds
                }
                else
                {
                    // Force disengage
                    Combatant = null;
                    Warmode = false;
                    return; // Skip other logic while retreating
                }
            }

            // 2. Despawn Logic (Time to leave)
            if (!m_IsLeaving && m_TeamId != 0 && now > m_PendingDeparture)
            {
                if (Combatant == null && Utility.RandomDouble() < 0.05)
                {
                    m_IsLeaving = true;
                    if (CanSendMessage(now)) Say("Time to move on."); 
                    Timer.DelayCall(TimeSpan.FromSeconds(5.0), ExecuteDeparture);
                }
            }

            // 3. Combat Support (Healing) - Checks every 2.5s
            if (now > m_NextHealCheck)
            {
                m_NextHealCheck = now + TimeSpan.FromSeconds(2.5);
                PerformCombatSupport(now);
            }

            // 4. Idle Chatter
            if (Combatant == null && now > m_NextChatTime)
            {
                if (CanSendMessage(now))
                {
                    string[] source = m_IsEvil ? EvilChat : FriendlyChat;
                    Say(GetPooledMessage(source));
                    m_NextChatTime = now + TimeSpan.FromSeconds(Utility.RandomMinMax(15, 45));
                }
            }
        }

        private void PerformCombatSupport(DateTime now)
        {
            double hpRatio = (double)Hits / HitsMax;
            
            // Heal Self
            if (hpRatio < HealSelfThreshold)
                TryHealSelf(now);

            // Wizard Cross-Healing
            if (m_CitizenType == (int)CitizenClass.Wizard && Mana > 10)
            {
                AdventurerTeam injuredAlly = null;
                foreach (Mobile m in this.GetMobilesInRange(12))
                {
                    BaseCreature bc = m as BaseCreature;
                    // Check for Team ID match
                    if (bc != null && bc is AdventurerTeam && bc.Team == this.Team && bc != this && bc.Alive)
                    {
                        if (bc.Hits < (bc.HitsMax * HealAllyThreshold))
                        {
                            injuredAlly = (AdventurerTeam)bc;
                            break;
                        }
                    }
                }
                if (injuredAlly != null) DoMagicHeal(injuredAlly);
            }
        }
        #endregion

        #region Combat Events & Retreat
        public override void OnDamage(int amount, Mobile from, bool willKill)
        {
            base.OnDamage(amount, from, willKill);
            
            if (willKill || Deleted) return;

            // Retreat Logic
            // Fighters never retreat. Others retreat if HP < 25% (35% chance).
            if (!m_IsRetreating && m_CitizenType != (int)CitizenClass.Fighter)
            {
                double hpRatio = (double)Hits / HitsMax;
                if (hpRatio < RetreatThreshold && Utility.RandomDouble() < 0.35)
                {
                    m_IsRetreating = true;
                    m_RetreatResetTime = DateTime.UtcNow + TimeSpan.FromSeconds(6.0);
                    
                    if (CanSendMessage(DateTime.UtcNow)) 
                        Say(GetPooledMessage(RetreatLines));
                    
                    Combatant = null;
                    Warmode = false;
                }
            }
        }

        public override void OnCombatantChange()
        {
            base.OnCombatantChange();
            DateTime now = DateTime.UtcNow;

            if (Combatant != null)
            {
                // Combat Started
                if (CanSendMessage(now) && Utility.RandomDouble() < 0.3)
                    Say(GetPooledMessage(CombatYell));
            }
            else
            {
                // Combat Ended / Victory
                if (!m_IsRetreating && CanSendMessage(now) && Utility.RandomDouble() < 0.5)
                    Say(GetPooledMessage(VictoryLines));
            }
        }

        public override void OnMovement(Mobile m, Point3D oldLocation)
        {
            if (m_IsLeaving || Deleted || m == null || !m.Alive || !m.Player) return;

            // Simple Greeting on approach
            if (CanSendMessage(DateTime.UtcNow) && CanSee(m) && m.InRange(this, 8))
            {
                string[] source = m_IsEvil ? EvilGreetings : FriendlyGreetings;
                Say(GetPooledMessage(source));
            }
        }
        #endregion

        #region Actions (Heal/Support)
        private void TryHealSelf(DateTime now)
        {
            // Potion
            BaseHealPotion potion = Backpack.FindItemByType(typeof(BaseHealPotion)) as BaseHealPotion;
            if (potion != null)
            {
                potion.Drink(this);
                PublicOverheadMessage(MessageType.Emote, 0x3B2, true, GetPooledMessage(PotionLines));
                return;
            }

            // Bandage (Non-Wizards)
            if (m_CitizenType != (int)CitizenClass.Wizard && !m_IsUsingBandage)
            {
                Bandage bandage = Backpack.FindItemByType(typeof(Bandage)) as Bandage;
                if (bandage != null)
                {
                    m_IsUsingBandage = true;
                    bandage.Consume(1);
                    PublicOverheadMessage(MessageType.Emote, 0x3B2, true, GetPooledMessage(BandageLines));
                    
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
            // Magic (Wizards)
            else if (m_CitizenType == (int)CitizenClass.Wizard)
            {
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

        #region Setup (Mounts & Appearance)
        // Helper to enforce consistency. If the team is mounted, EVERYONE gets a horse.
        private void EnforceMountState(bool shouldBeMounted)
        {
            if (shouldBeMounted)
            {
                if (this.Mount == null)
                    new Horse().Rider = this;
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

            int baseSkill = 25 + (m_CitizenLevel * 10);
            int strMin = m_CitizenLevel * 20, strMax = m_CitizenLevel * 30;
            int dexMin = m_CitizenLevel * 20, dexMax = m_CitizenLevel * 30;
            int intMin = m_CitizenLevel * 20, intMax = m_CitizenLevel * 30;
            int hitsMin = m_CitizenLevel * 30, hitsMax = m_CitizenLevel * 40;
            
            // Apply Classes
            int type = Utility.Random(3);
            switch (type)
            {
                case 0: // Wizard
                    IntelligentAction.DressUpWizards(this, false);
                    m_CitizenType = (int)CitizenClass.Wizard;
                    AI = AIType.AI_Mage;
                    SetSkill(SkillName.Psychology, baseSkill);
                    SetSkill(SkillName.Magery, baseSkill);
                    SetSkill(SkillName.Meditation, baseSkill);
                    SetSkill(SkillName.MagicResist, baseSkill);
                    SetSkill(SkillName.FistFighting, baseSkill);
                    SetSkill(SkillName.Tactics, baseSkill - 20);
                    intMax += m_CitizenLevel * 30;
                    break;

                case 1: // Fighter
                    IntelligentAction.DressUpFighters(this, "", m_IsEvil, false, true);
                    m_CitizenType = (int)CitizenClass.Fighter;
                    AI = AIType.AI_Melee;
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
                    break;

                case 2: // Rogue
                    IntelligentAction.DressUpRogues(this, "", m_IsEvil, false, true);
                    m_CitizenType = (int)CitizenClass.Rogue;
                    AI = AIType.AI_Archer;
                    SetSkill(SkillName.Marksmanship, baseSkill);
                    SetSkill(SkillName.Tactics, baseSkill);
                    SetSkill(SkillName.MagicResist, baseSkill);
                    SetSkill(SkillName.Healing, baseSkill);
                    SetSkill(SkillName.Anatomy, baseSkill - 10);
                    dexMax += m_CitizenLevel * 10;
                    break;
            }

            SetStr(strMin, strMax);
            SetDex(dexMin, dexMax);
            SetInt(intMin, intMax);
            SetHits(hitsMin, hitsMax);
            
            AddWeapon(true);
            AddHealingSupplies();
        }

        public void AddWeapon(bool initial)
        {
            BaseWeapon hand = FindItemOnLayer(Layer.OneHanded) as BaseWeapon;
            BaseWeapon twohand = FindItemOnLayer(Layer.TwoHanded) as BaseWeapon;

            if (!initial && (hand != null || twohand != null)) return;

            // Fighter Weapons (Basic variety)
            if (m_CitizenType == (int)CitizenClass.Fighter)
            {
                if (hand != null || twohand != null) return;
                switch (Utility.Random(3))
                {
                    case 0: AddItem(new Longsword()); break;
                    case 1: AddItem(new BattleAxe()); break;
                    case 2: AddItem(new Mace()); break;
                }
            }
            // Wizard Weapons (Staffs)
            else if (m_CitizenType == (int)CitizenClass.Wizard)
            {
                if (initial) { if(hand!=null) hand.Delete(); if(twohand!=null) twohand.Delete(); }
                AddItem(Utility.RandomBool() ? (Item)new GnarledStaff() : new QuarterStaff());
            }
            // Rogue Weapons
            else if (m_CitizenType == (int)CitizenClass.Rogue)
            {
                if (initial) { if(hand!=null) hand.Delete(); if(twohand!=null) twohand.Delete(); }

                // 1. Rare Throwing Gloves
                // 50% Chance to have "Throwing" gear if selected (high variance)
                if (Utility.RandomBool())
                {
                    Item gloves = new Item(0x13C6); // Gloves of Throwing graphic
                    gloves.Name = "Throwing Gloves";
                    AddItem(gloves);
                    Item ammo = new Item(0xF0E); // Rock/Stone graphic
                    ammo.Name = "Throwing Ammunition";
                    PackItem(ammo);
                    return; 
                }

                // 2. Varied Ranged Weapons
                // Ensures different rogues have different attack speeds/damage profiles
                int ammoCount = Utility.RandomMinMax(60, 100);
                switch (Utility.Random(8))
                {
                    case 0: 
                    case 5:
                        AddItem(new Bow()); 
                        PackItem(new Arrow(ammoCount)); 
                        break;
                    case 1: 
                    case 6:
                    case 7:
                        AddItem(new Crossbow()); 
                        PackItem(new Bolt(ammoCount)); 
                        break;
                    case 2: 
                        AddItem(new HeavyCrossbow()); 
                        PackItem(new Bolt(ammoCount)); 
                        break;
                    case 3: 
                        AddItem(new RepeatingCrossbow()); 
                        PackItem(new Bolt(ammoCount)); 
                        break;
                    case 4: 
                        AddItem(new CompositeBow()); 
                        PackItem(new Arrow(ammoCount)); 
                        break;
                }
            }
        }

        private void AddHealingSupplies()
        {
            if (m_CitizenType != (int)CitizenClass.Wizard)
                PackItem(new Bandage(Utility.RandomMinMax(10, 20)));
            
            PackItem(new HealPotion());
            PackItem(new HealPotion());
        }
        #endregion

        #region Helpers
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
			
			// 1 in 25 chance to drop a rare item
            if (Utility.Random(25) == 0)
            {
                Type rareType = Loot.AdventurerRareItemTypes[Utility.Random(Loot.AdventurerRareItemTypes.Length)];
                Item rare = Activator.CreateInstance(rareType) as Item;
                if (rare != null) PackItem(rare);
            }

            if (m_CitizenType == (int)CitizenClass.Wizard)
                AddLoot(LootPack.MedScrolls, (m_CitizenLevel / 3) + 1);
        }

        public override void Serialize(GenericWriter writer) { base.Serialize(writer); writer.Write((int)0); }
        public override void Deserialize(GenericReader reader) { base.Deserialize(reader); int v = reader.ReadInt(); Timer.DelayCall(TimeSpan.Zero, Delete); }
        #endregion
    }

    // ========================================================================
    // AUTO TEAM MAINTAINER (Manager)
    // ========================================================================
    public static class AutoTeamMaintainer
    {
        private static bool s_Enabled = true;
        private static Timer s_MaintenanceTimer;
        private static readonly Queue<int> s_RecycledIds = new Queue<int>();
        private static int s_NextTeamId = 1;
        private static readonly object s_IdLock = new object();

        public static void Initialize()
        {
            if (s_MaintenanceTimer != null) s_MaintenanceTimer.Stop();
            // Check for spawn opportunities every 2 minutes
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
            
            foreach (NetState state in NetState.Instances)
            {
                Mobile m = state.Mobile;
                if (m != null && m.Player && m.Alive && m.Map != null && m.Map != Map.Internal)
                {
                    // 60% chance per player per cycle to try spawning a team
                    if (Utility.RandomDouble() < 0.6) 
                        TrySpawnTeamForPlayer(m);
                }
            }
        }

        private static void TrySpawnTeamForPlayer(Mobile pm)
        {
            // Density Check: Don't spawn if too many adventurers already exist nearby
            int nearbyCount = 0;
            foreach (Mobile m in pm.GetMobilesInRange(40))
            {
                if (m is AdventurerTeam) nearbyCount++;
            }
            if (nearbyCount > 5) return;

            Point3D spawnLoc = FindSpawnLocation(pm);
            if (spawnLoc == Point3D.Zero) return;

            int teamId = GetNewTeamId();
            bool isEvil = Utility.RandomBool();
            bool mounted = Utility.RandomDouble() < 0.4; // 40% chance for mounted team
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
                // Spawn distance 24-35 tiles (off-screen)
                int dist = Utility.RandomMinMax(24, 35);
                double ang = Utility.RandomDouble() * Math.PI * 2;
                int x = nearPlayer.X + (int)(Math.Cos(ang) * dist);
                int y = nearPlayer.Y + (int)(Math.Sin(ang) * dist);
                Point3D p = new Point3D(x, y, map.GetAverageZ(x, y));

                if (IsInForbiddenRegion(p, map)) continue;

                if (map.CanSpawnMobile(p)) return p;
            }
            return Point3D.Zero;
        }

        // Strict region filtering to keep spawns safe and logical
        private static bool IsInForbiddenRegion(Point3D loc, Map map)
        {
            Region reg = Region.Find(loc, map);
            if (reg == null) return false;

            if (reg is WantedRegion || reg is SavageRegion || reg is VillageRegion ||
                reg is UnderHouseRegion || reg is UmbraRegion || reg is TownRegion ||
                reg is StartRegion || reg is SkyHomeDwelling || reg is SafeRegion ||
                reg is ProtectedRegion || reg is PublicRegion || reg is PirateRegion ||
                reg is BardTownRegion || reg is DawnRegion || reg is DungeonHomeRegion ||
                reg is GargoyleRegion || reg is GuardedRegion || reg is HouseRegion ||
                reg is LunaRegion || reg is MazeRegion || reg is MoonCore)
            {
                return true;
            }
            return false;
        }
    }
}
