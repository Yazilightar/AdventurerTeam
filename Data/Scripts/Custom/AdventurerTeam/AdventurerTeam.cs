using System;
using System.Collections.Generic;
using Server;
using Server.Items;
using Server.Mobiles;
using Server.Network;
using Server.Regions;
using Server.Misc; 
using Server.ContextMenus; 

namespace Server.Scripts.Custom
{
    // ========================================================================
    // 1. Captive Task Script (CaptiveAdventurer)
    // ========================================================================
    public class CaptiveAdventurer : BaseCreature
    {
        private bool m_IsRescued;
        private Timer m_DestinationTimer;
        private int m_LostCounter; 

        [Constructable]
        public CaptiveAdventurer() : base(AIType.AI_Melee, FightMode.None, 10, 1, 0.2, 0.4)
        {
            Title = "the captive";
            InitBody();
            InitOutfit();
            m_IsRescued = false;
            m_LostCounter = 0;
            
            CantWalk = true; 
            Blessed = true;  
            Frozen = true;   
        }

        private bool CheckCaptorsNearby()
        {
            foreach (Mobile m in this.GetMobilesInRange(15))
            {
                if (m is AdventurerTeam)
                {
                    AdventurerTeam badGuy = (AdventurerTeam)m;
                    if (badGuy.IsEvil && badGuy.Alive && !badGuy.Deleted)
                        return true;
                }
            }
            return false;
        }

        public override void OnDoubleClick(Mobile from)
        {
            if (m_IsRescued) 
            {
                if (this.ControlMaster == from)
                {
                    this.Say("I am right behind you!");
                    this.ControlOrder = OrderType.Follow;
                    this.ControlTarget = from;
                }
                return; 
            }

            if (CheckCaptorsNearby())
            {
                this.Direction = GetDirectionTo(from);
                this.Animate(32, 5, 1, true, false, 0); 
                this.PlaySound(this.Female ? 0x32D : 0x44A); 
                this.Say("They are watching... kill them first!");
            }
            else
            {
                PerformRescue(from);
            }
        }

        private void PerformRescue(Mobile rescuer)
        {
            if (m_IsRescued) return;

            m_IsRescued = true;
            CantWalk = false;
            Frozen = false;
            Blessed = false; 

            if (rescuer.Player)
            {
                this.Controlled = true;
                this.ControlMaster = rescuer;
                this.ControlOrder = OrderType.Follow;
                this.ControlTarget = rescuer;
                this.IsBonded = false; 
            }

            this.Direction = GetDirectionTo(rescuer);
            this.Animate(33, 5, 1, true, false, 0); 
            
            this.Say(String.Format("You saved me! Thank you, {0}!", rescuer.Name));

            Timer.DelayCall(TimeSpan.FromSeconds(1.0), delegate
            {
                if (this.Alive && !this.Deleted)
                {
                    this.Say("Please, take me to a City or Safe Area.");
                    m_DestinationTimer = Timer.DelayCall(TimeSpan.FromSeconds(3.0), TimeSpan.FromSeconds(3.0), CheckDestination);
                }
            });
        }

        private void CheckDestination()
        {
            if (Deleted || !Alive || Map == null) 
            {
                StopTimer();
                return;
            }

            if (ControlMaster == null || ControlMaster.Deleted || ControlMaster.Map != this.Map || !ControlMaster.InRange(this, 40))
            {
                m_LostCounter++;
                if (m_LostCounter >= 5) 
                {
                    this.Say("I seem to have lost my way...");
                    StopTimer();
                    Timer.DelayCall(TimeSpan.FromSeconds(2.0), Delete);
                }
                return;
            }
            else
            {
                m_LostCounter = 0; 
            }

            if (IsSafeZone(this.Region))
            {
                FinishEscort();
            }
        }

        private bool IsSafeZone(Region reg)
        {
            if (reg == null || reg.Map == Map.Internal) return false;

            string rName = reg.Name;
            if (CheckSafeName(rName)) return true;

            try 
            {
                string wName = Server.Misc.Worlds.GetRegionName(this.Map, this.Location);
                if (CheckSafeName(wName)) return true;
            }
            catch {}

            string typeName = reg.GetType().Name;
            if (typeName == "TownRegion" || typeName == "GuardedRegion" || typeName == "SafeRegion" || 
                typeName == "ProtectedRegion" || typeName == "VillageRegion" || typeName == "UmbraRegion" || 
                typeName == "LunaRegion" || typeName == "BardTownRegion" || typeName == "DawnRegion" || 
                typeName == "GargoyleRegion" || typeName == "StartRegion" || typeName == "PublicRegion")
                return true;

            return false;
        }

        private bool CheckSafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            if (IndexOf(name, "Britain") || IndexOf(name, "Minoc") || IndexOf(name, "Trinsic") || 
                IndexOf(name, "Vesper") || IndexOf(name, "Yew") || IndexOf(name, "Skara") || 
                IndexOf(name, "Magincia") || IndexOf(name, "Moonglow") || IndexOf(name, "Nujel") || 
                IndexOf(name, "Cove") || IndexOf(name, "Jhelom") || IndexOf(name, "Ocllo") || 
                IndexOf(name, "Haven") || IndexOf(name, "Luna") || IndexOf(name, "Umbra") || 
                IndexOf(name, "Zento") || IndexOf(name, "Termur") || IndexOf(name, "Royal") ||
                IndexOf(name, "Buccaneer") || IndexOf(name, "Serpent") || IndexOf(name, "Hold") || 
                IndexOf(name, "Glacial") || IndexOf(name, "Elidor") || IndexOf(name, "Islegem") ||
                IndexOf(name, "Greensky") || IndexOf(name, "Dusk") || IndexOf(name, "Starguide") || 
                IndexOf(name, "Portshine") || IndexOf(name, "Kuldara") || IndexOf(name, "Barako") ||
                IndexOf(name, "Whisper") || IndexOf(name, "Dawn"))
            {
                return true;
            }

            if (IndexOf(name, "Town") || IndexOf(name, "City") || IndexOf(name, "Guard") || 
                IndexOf(name, "Safe") || IndexOf(name, "Village") || IndexOf(name, "Bank") || 
                IndexOf(name, "Inn") || IndexOf(name, "Tavern"))
            {
                return true;
            }

            return false;
        }

        private bool IndexOf(string source, string target)
        {
            return source.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void FinishEscort()
        {
            StopTimer();
            this.Say("We are safe now! Thank you for bringing me back.");
            this.Animate(33, 5, 1, true, false, 0); 

            if (ControlMaster != null && ControlMaster.Player)
            {
                Mobile player = ControlMaster;
                int goldAmount = Utility.RandomMinMax(400, 600);
                Container backpack = player.Backpack;
                if (backpack != null)
                {
                    backpack.DropItem(new Gold(goldAmount));
                    player.SendMessage(String.Format("You have received {0} gold for the escort.", goldAmount));
                }
                
                if (player.Fame < 10000) player.Fame += 100;
                if (player.Karma < 10000) player.Karma += 100;
            }
            Timer.DelayCall(TimeSpan.FromSeconds(3.0), Delete);
        }

        private void StopTimer() { if (m_DestinationTimer != null) { m_DestinationTimer.Stop(); m_DestinationTimer = null; } }
        public override bool ClickTitle { get { return false; } }

        public virtual void InitBody()
        {
            SetStr(90, 100); SetDex(90, 100); SetInt(15, 25);
            Hue = Utility.RandomSkinHue();
            if (Female = Utility.RandomBool()) { Body = 401; Name = NameList.RandomName("female"); }
            else { Body = 400; Name = NameList.RandomName("male"); }
        }

        public virtual void InitOutfit()
        {
            for (int i = Items.Count - 1; i >= 0; --i)
            {
                Item item = Items[i];
                if (item is BaseClothing || item is BaseWeapon || item is BaseArmor) item.Delete();
            }
            AddItem(new Shirt(Utility.RandomNeutralHue()));
            AddItem(new ShortPants(Utility.RandomNeutralHue()));
            if (Utility.RandomBool()) AddItem(new Sandals());
            Utility.AssignRandomHair(this);
        }

        public CaptiveAdventurer(Serial serial) : base(serial) { }
        public override void Serialize(GenericWriter writer) { base.Serialize(writer); writer.Write((int)0); }
        public override void Deserialize(GenericReader reader) { base.Deserialize(reader); int v = reader.ReadInt(); Timer.DelayCall(TimeSpan.Zero, Delete); }
        public override void OnDelete() { StopTimer(); base.OnDelete(); }
    }

    // ========================================================================
    // 2. Adventurer Team - Core Logic
    // ========================================================================
    public enum CitizenClass { Wizard = 1, Fighter = 2, Rogue = 3 }
    public enum TeamPersonality { Balanced = 0, Greedy, Aggressive, Cautious }

    [CorpseName("an adventurer corpse")]
    public class AdventurerTeam : BaseCreature
    {
        #region 1. Dialogue & Text Data
        private static readonly string[] FriendlyChat = new string[] 
        { 
            "The wyrms in the deep caves grow bolder each day...",
            "I barely escaped from a pack of dire wolves yesterday.",
            "These lands are cursed, I tell you. Evil stirs in the shadows.",
            "Lets explore that dungeon, they said. It will be fun, they said.",
            "I saw a dragon's shadow pass overhead last night.",
            "Careful - traps abound in these ancient ruins.",
            "The trollbears have been raiding caravans again.",
            "I seek a legendary blade, lost to time.",
            "Ancient treasures await those brave enough to claim them.",
            "Traveling alone in these parts is a death sentence.",
            "Lost my entire party to a demon in the lower depths.",
            "We could use another sword arm for what lies ahead.",
            "Running low on supplies... need to restock soon.",
            "Any healers nearby? My wounds still ache.",
            "Safe travels, friend.", "Hail, traveler.", "Good day."
        };

        private static readonly string[] EvilChat = new string[] 
        { 
            "Your coin or your life, fool.",
            "The vultures will feast tonight...",
            "This is OUR territory. Pay the toll or bleed.",
            "The weak exist only to serve the strong.",
            "I smell fear... and gold.",
            "Five corpses before noon. Good hunting today.",
            "Their screams still echo in my ears.",
            "Left a trail of bodies from here to the coast.",
            "Only the strong survive here. You don't look strong.",
            "Trespassers end up feeding the crows.",
            "Gold talks. Mercy doesn't.",
            "Can't afford to be weak around these parts. Kill them.",
            "Back off.", "Walk away.", "What are you looking at?", "Get lost."
        };

        private static readonly string[] FriendlyGreetings = new string[] 
        { 
            "Safe travels.", "Hail, traveler.", "Good day.", "Greetings.", "Peace be with you."
        };

        private static readonly string[] EvilGreetings = new string[] 
        { 
            "Back off.", "This is our turf.", "Walk away.", "What are you looking at?", "Get lost."
        };

        private static readonly string[] LootChat = new string[] 
        { 
            "Found something!", "Just trash.", "Mine!", "Shiny!", "Gold!", "Jackpot!", "Empty...",
            "A sack of coins!", "Looks valuable.", "Don't touch that, it's mine."
        };

        private static readonly string[] PanicLines = new string[] 
        { 
            "I'm out of here!", "Run!", "Not worth it!", "Retreat!", "Save yourselves!", "Too strong!",
            "I can't take much more!", "Cover me!", "Fall back!"
        };

        private static readonly string[] SquadDeathLines = new string[] 
        { 
            "Man down!", "You'll pay!", "Medic!", "They got one of us!", "Nooo!", "Hold the line!",
            "Avenge me!", "We lost one!"
        };

        private static readonly string[] CombatYell = new string[] 
        { 
            "Attack!", "Die!", "For glory!", "Surround them!", "Cut off their escape!",
            "Shield wall, hold formation!", "Flank them from the left!", "Cover the rear!",
            "Break their line!", "Press the attack!", "Healer down! Protect them!",
            "No retreat!", "We end this now!", "Fight or die!", "Show no mercy!"
        };

        private static readonly string[] VictoryLines = new string[] 
        { 
            "Too easy.", "Target eliminated.", "Check the bodies.", 
            "That was close! Everyone alright?", "Good fight! Check the body for coin.",
            "Another one bites the dust.", "I need to catch my breath...",
            "Victory! But stay alert.", "Well fought, friends!", "*wipes blood from weapon*",
            "Excellent teamwork!", "They didn't stand a chance!", "Area clear… for now."
        };

        private static readonly string[] RetreatLines = new string[] 
        { 
            "Retreat!", "Fall back!", "Regroup!", "Fall back! I'm badly wounded!",
            "I can't take much more!", "Cover me!", "Too many of them!",
            "I need to heal!", "Not dying here today!", "Tactical withdrawal!",
            "Pull back, now!", "Break contact!"
        };

        private static readonly string[] PotionLines = new string[]
        {
            "*drinks a healing potion*", "*gulps potion hastily*", "Much better!",
            "*uncorks flask*", "Good thing I brought these!", "Ah, that's better!",
            "Feeling it work already.", "That burns going down.", "Just what I needed.",
            "Tastes like piss but gets the job done", "Never leave home without these."
        };

        private static readonly string[] BandageLines = new string[]
        {
            "*applies bandages*", "*binds wounds*", "Just need to stop the bleeding...",
            "*wraps injuries*", "These bandages will hold.", "*tightens the bandage*",
            "Hold still…", "This should slow the bleeding.", "Not pretty, but it'll do.",
            "That'll have to hold.", "Keep watch while I finish this."
        };

        private static readonly string[] HealSpellLines = new string[] 
        { 
            "In Vas Mani!", "Be healed!", "*casts healing magic on ally*",
            "Let the light mend thy wounds!", "Hold still, I'll heal thee!",
            "By the light, be restored!", "Your wounds close now!",
            "Strength return to you!", "Be renewed!"
        };

        private static readonly string[] CureSpellLines = new string[] { "An Nox!", "The poison leaves you." };
        private static readonly string[] OutdoorChat = new string[] { "Nice weather.", "The wind is cold.", "I hate the rain." };
        
        private static readonly string[] HeroicGreetings = new string[] { "Greetings, hero!", "An honor to see you.", "The legends are true!" };
        private static readonly string[] EvilJeerHero = new string[] { "Look at this 'hero'.", "You don't scare us.", "Go save a cat, hero." };
        private static readonly string[] GoodFearMurderer = new string[] { "A killer! Stay back!", "Guards! Help!", "Don't hurt us!", "Monster!" };
        private static readonly string[] EvilGreetMurderer = new string[] { "Respect, killer.", "Business or pleasure?", "Stay out of our way, red.", "Nice kill count." };
        
        private static readonly string[] GreedyDeath = new string[] { "My gold...", "I lost everything...", "Not like this..." };
        private static readonly string[] AggressiveDeath = new string[] { "A glorious death!", "I'll see you in hell!", "Curse you!" };
        private static readonly string[] CautiousDeath = new string[] { "I knew this would happen...", "Should have ran...", "Mistake..." };
        private static readonly string[] StandardDeath = new string[] { "Argh...", "It ends here...", "Ugh...", "Cold..." };
        #endregion

        #region 2. Configuration & Fields
        private static readonly TimeSpan SpeechThrottle = TimeSpan.FromSeconds(2.0);
        private const int DefaultHomeRange = 12; 
        private const int TeamMemberRange = 16;
        private const double HealAllyThreshold = 0.60;
        private const double RetreatThreshold = 0.25;

        private int m_CitizenType;
        private int m_CitizenLevel;
        private bool m_SpawnedBySystem;
        private bool m_IsEvil;
        private bool m_SpawnMounted;
        private int m_TeamId;
        private TeamPersonality m_Personality;

        private DateTime m_LastMessageTime;
        private DateTime m_NextChatTime;
        private DateTime m_NextHealCheck;      
        private DateTime m_NextSelfLogicTime;  
        private DateTime m_PendingDeparture;
        private DateTime m_NextEnvironmentCheck;
        private DateTime m_NextIdleThink;
        private DateTime m_NextGreetingTime;   

        private bool m_IsLeaving;
        private bool m_IsRetreating;
        private bool m_IsUsingBandage;
        private DateTime m_RetreatResetTime;

        private List<AdventurerTeam> m_MySquad = new List<AdventurerTeam>();
        #endregion

        #region 3. Properties
        [CommandProperty(AccessLevel.Owner)]
        public CitizenClass CitizenClass
        {
            get { return (CitizenClass)m_CitizenType; }
            set { m_CitizenType = (int)value; InvalidateProperties(); }
        }

        [CommandProperty(AccessLevel.Owner)]
        public TeamPersonality Personality
        {
            get { return m_Personality; }
            set { m_Personality = value; InvalidateProperties(); }
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

        #region 4. Constructors
        [Constructable]
        public AdventurerTeam() : this(0, false, false) { }
        [Constructable]
        public AdventurerTeam(int teamId, bool isEvil) : this(teamId, isEvil, false) { }
        [Constructable]
        public AdventurerTeam(int teamId, bool isEvil, bool mounted)
            : base(AIType.AI_Melee, FightMode.Closest, 10, 1, 0.2, 0.4)
        {
            if (teamId != 0) this.Team = teamId;
            m_TeamId = teamId;
            m_IsEvil = isEvil;
            m_SpawnedBySystem = (teamId != 0);
            m_SpawnMounted = mounted;
            
            FightMode = FightMode.Closest;
            RangePerception = TeamMemberRange;
            RangeHome = DefaultHomeRange;

            m_Personality = (TeamPersonality)Utility.Random(4);

            InitStatsAndAppearance();
            EnforceMountState(mounted);

            DateTime now = DateTime.UtcNow;
            m_NextChatTime = now.AddSeconds(Utility.RandomMinMax(10, 30));
            m_PendingDeparture = now.AddMinutes(Utility.RandomMinMax(30, 60));
            m_NextEnvironmentCheck = now.AddSeconds(Utility.Random(5));
        }
        
        public AdventurerTeam(Serial serial) : base(serial) { }
        #endregion

        #region 5. Squad Management
        public void AddToSquad(AdventurerTeam member)
        {
            if (member != null && member != this && !m_MySquad.Contains(member))
            {
                m_MySquad.Add(member);
                // Sync personality
                member.Personality = this.Personality; 
            }
        }
        
        public void RemoveFromSquad(AdventurerTeam member)
        {
            m_MySquad.Remove(member);
        }

        public override void OnDelete()
        {
            base.OnDelete();
            for (int i = 0; i < m_MySquad.Count; i++)
            {
                if (m_MySquad[i] != null && !m_MySquad[i].Deleted)
                    m_MySquad[i].RemoveFromSquad(this);
            }
            m_MySquad.Clear();
        }

        private int GetAliveSquadCount()
        {
            int count = 0;
            for (int i = m_MySquad.Count - 1; i >= 0; i--)
            {
                AdventurerTeam member = m_MySquad[i];
                if (member != null && !member.Deleted && member.Alive) count++;
                else m_MySquad.RemoveAt(i);
            }
            return count;
        }
        #endregion

        #region 6. Core Logic (OnThink)
        public override void OnThink()
        {
            base.OnThink();
            if (Deleted || !Alive || Map == null || Map == Map.Internal) return;
            if (Frozen || Paralyzed) return;

            DateTime now = DateTime.UtcNow;

            // 1. Sector Sleep
            Sector sector = Map.GetSector(this);
            if (sector != null && !sector.Active)
            {
                if (!m_IsLeaving && m_TeamId != 0 && now > m_PendingDeparture)
                {
                    if (Utility.RandomDouble() < 0.02) ExecuteDeparture();
                }
                return;
            }

            // 2. Idle Throttling
            if (Combatant == null)
            {
                if (now < m_NextIdleThink) return;
                m_NextIdleThink = now + TimeSpan.FromSeconds(1.5);
            }

            // 3. Environment Checks
            if (now > m_NextEnvironmentCheck)
            {
                m_NextEnvironmentCheck = now + TimeSpan.FromSeconds(5.0);
                if (Combatant == null)
                {
                    ManageLightSource();
                    
                    double lootChance = (m_Personality == TeamPersonality.Greedy) ? 0.5 : 0.2;
                    
                    if (!IsIndoors() && CanSendMessage(now) && Utility.RandomDouble() < 0.1)
                    {
                        Say(GetPooledMessage(OutdoorChat));
                    }
                    
                    IPooledEnumerable eable = this.GetItemsInRange(2);
                    foreach (Item item in eable) 
                    {
                        if (item is Corpse && Utility.RandomDouble() < lootChance) 
                        {
                            Animate(32, 5, 1, true, false, 0); 
                            PlaySound(0x57); 
                            if (CanSendMessage(now)) Say(GetPooledMessage(LootChat));
                            break;
                        }
                    }
                    eable.Free();
                }
            }

            // 4. Self-Survival
            if (now > m_NextSelfLogicTime)
            {
                if (Hits < HitsMax || Poisoned)
                {
                    DoSelfSurvival();
                    double cooldown = (m_CitizenType == 1) ? 4.0 : 8.0;
                    m_NextSelfLogicTime = now + TimeSpan.FromSeconds(cooldown);
                }
            }

            // 5. Squad Support
            if (now > m_NextHealCheck)
            {
                m_NextHealCheck = now + TimeSpan.FromSeconds(2.5);
                PerformSquadSupport(now);
            }

            // 6. Class Logic
            if (m_CitizenType == 3 && !m_IsRetreating && Hits < HitsMax)
                Server.Misc.IntelligentAction.HideFromOthers(this);

            if (m_CitizenType == 2 && Combatant != null && !m_IsRetreating)
                if (Combatant.GetDistanceToSqrt(this) > 4 && Utility.RandomDouble() < 0.05)
                    Server.Misc.IntelligentAction.LeapToAttacker(this, Combatant);

            // 7. Panic/Retreat
            if (Combatant != null && !m_IsRetreating && Hits < HitsMax)
            {
                double panicChance = 0.2;
                if (m_Personality == TeamPersonality.Cautious) panicChance = 0.4;
                if (m_Personality == TeamPersonality.Aggressive) panicChance = 0.05;

                if (GetAliveSquadCount() == 0 && Utility.RandomDouble() < panicChance)
                {
                    m_IsRetreating = true;
                    m_RetreatResetTime = now + TimeSpan.FromSeconds(10.0);
                    if (CanSendMessage(now)) Say(GetPooledMessage(PanicLines));
                    Combatant = null;
                    Warmode = false;
                }
            }

            if (m_IsRetreating)
            {
                if (now > m_RetreatResetTime) m_IsRetreating = false;
                else { Combatant = null; Warmode = false; return; }
            }

            // 8. Timed Departure
            if (!m_IsLeaving && m_TeamId != 0 && now > m_PendingDeparture)
            {
                if (Combatant == null && Utility.RandomDouble() < 0.02)
                {
                    m_IsLeaving = true;
                    if (CanSendMessage(now)) Say("Time to move on.");
                    Timer.DelayCall(TimeSpan.FromSeconds(5.0), ExecuteDeparture);
                }
            }

            // 9. Idle Chatter
            if (Combatant == null && now > m_NextChatTime)
            {
                if (CanSendMessage(now))
                {
                    string[] source = m_IsEvil ? EvilChat : FriendlyChat;
                    Say(GetPooledMessage(source));
                    m_NextChatTime = now + TimeSpan.FromSeconds(Utility.RandomMinMax(20, 60));
                }
            }
        }
        #endregion

        #region 7. Healing & Survival Logic
        private void DoSelfSurvival() 
        { 
            // 1. Potions (Virtual)
            if (Hits < HitsMax * 0.4 && Utility.RandomDouble() < 0.1)
            {
                PublicOverheadMessage(MessageType.Emote, 0x3B2, true, GetPooledMessage(PotionLines));
                Heal(Utility.RandomMinMax(10, 20));
                PlaySound(0x30); 
                return;
            }

            if (Poisoned) 
            { 
                if (m_CitizenType == (int)CitizenClass.Wizard) 
                { 
                    if (Mana >= 10) 
                    { 
                        Mana -= 10; Animate(17, 7, 1, true, false, 0); PlaySound(0x1E0); 
                        Timer.DelayCall(TimeSpan.FromSeconds(1.5), delegate() { if (Alive) CurePoison(this); }); 
                    } 
                } 
                else 
                { 
                    Animate(34, 5, 1, true, false, 0); PlaySound(0x30); 
                    Timer.DelayCall(TimeSpan.FromSeconds(0.5), delegate() { if (Alive) CurePoison(this); }); 
                } 
                return; 
            } 

            if (Hits < HitsMax * 0.6) 
            { 
                int heal = Utility.RandomMinMax(20, 40); 
                if (m_CitizenType == (int)CitizenClass.Wizard) 
                { 
                    if (Mana >= 15) 
                    { 
                        Mana -= 15; Animate(17, 7, 1, true, false, 0); PlaySound(0x1F2); 
                        Timer.DelayCall(TimeSpan.FromSeconds(2.0), delegate() 
                        { 
                            if (Alive) { Heal(heal); FixedParticles(0x376A, 9, 32, 5030, EffectLayer.Waist); } 
                        }); 
                    } 
                } 
                else if (!m_IsUsingBandage)
                { 
                    m_IsUsingBandage = true;
                    PublicOverheadMessage(MessageType.Emote, 0x3B2, true, GetPooledMessage(BandageLines));
                    
                    Timer.DelayCall(TimeSpan.FromSeconds(4.0), delegate() 
                    { 
                        if (Alive && !Deleted) 
                        {
                            Heal(heal); 
                            PlaySound(0x57);
                            m_IsUsingBandage = false;
                        }
                    }); 
                } 
            } 
        }

        private void PerformSquadSupport(DateTime now) 
        { 
            if (m_CitizenType != (int)CitizenClass.Wizard || Mana < 15) return; 
            
            AdventurerTeam target = null; 
            bool isCure = false; 
            
            for (int i = m_MySquad.Count - 1; i >= 0; i--) 
            { 
                AdventurerTeam ally = m_MySquad[i]; 
                if (ally == null || ally.Deleted) { m_MySquad.RemoveAt(i); continue; } 
                
                if (ally.Alive && ally.Map == this.Map && ally.InRange(this, 12) && CanSee(ally)) 
                { 
                    if (ally.Poisoned) { target = ally; isCure = true; break; } 
                    if (ally != this && ally.Hits < (ally.HitsMax * HealAllyThreshold)) { target = ally; isCure = false; } 
                } 
            } 
            
            if (target != null) CastSupportSpell(target, isCure); 
        }

        private void CastSupportSpell(Mobile target, bool isCure) 
        { 
            if (target == this) return; 
            Mana -= 15; 
            Direction = GetDirectionTo(target); 
            Animate(17, 7, 1, true, false, 0); 
            
            if (isCure) { PlaySound(0x1E0); PublicOverheadMessage(MessageType.Emote, 0x3B2, true, "An Nox!"); } 
            else { PlaySound(0x1F2); PublicOverheadMessage(MessageType.Emote, 0x3B2, true, "In Vas Mani!"); } 
            
            Timer.DelayCall(TimeSpan.FromSeconds(1.5), delegate 
            { 
                if (!Deleted && Alive && target.Alive && target.Map == Map && target.InRange(this, 12)) 
                { 
                    if (isCure) { target.CurePoison(this); target.FixedParticles(0x373A, 10, 15, 5012, EffectLayer.Waist); } 
                    else { target.Heal(Utility.RandomMinMax(25, 45)); target.FixedParticles(0x376A, 9, 32, 5030, EffectLayer.Waist); } 
                } 
            }); 
        }
        #endregion

        #region 8. Combat & Event Overrides
        public override void OnGaveMeleeAttack(Mobile defender)
        {
            base.OnGaveMeleeAttack(defender);
            if (m_CitizenType == (int)CitizenClass.Fighter && defender != null)
                Server.Misc.IntelligentAction.PunchStun(defender);
        }

        public override void OnCombatantChange()
        {
            base.OnCombatantChange();
            if (Combatant != null && CanSendMessage(DateTime.UtcNow))
            {
                Say(GetPooledMessage(CombatYell));
            }
            else if (Combatant == null && !m_IsRetreating && CanSendMessage(DateTime.UtcNow))
            {
                Say(GetPooledMessage(VictoryLines));
            }
        }

        public override void OnAfterSpawn()
        {
            base.OnAfterSpawn();
            if (Map != null)
            {
                this.Home = this.Location;
                this.RangeHome = DefaultHomeRange;
                Effects.SendLocationParticles(EffectItem.Create(this.Location, this.Map, EffectItem.DefaultDuration), 0x3728, 10, 10, 5023);
                PlaySound(0x1FE);
            }
        }

        public override void GenerateLoot()
        {
            if (m_CitizenLevel >= 7)
                AddLoot(LootPack.Rich);
            else if (m_CitizenLevel >= 5)
                AddLoot(LootPack.Average);
            else
                AddLoot(LootPack.Meager);

            if (Utility.Random(25) == 0)
            {
                Type t = Loot.AdventurerRareItemTypes[Utility.Random(Loot.AdventurerRareItemTypes.Length)];
                Item r = Activator.CreateInstance(t) as Item;
                if (r != null) PackItem(r);
            }

            if (m_CitizenType == (int)CitizenClass.Wizard)
                AddLoot(LootPack.MedScrolls, (m_CitizenLevel / 3) + 1);
        }

        public override bool OnBeforeDeath()
        {
            if (Utility.RandomDouble() < 0.75) 
            {
                if (m_Personality == TeamPersonality.Greedy) Say(GetPooledMessage(GreedyDeath));
                else if (m_Personality == TeamPersonality.Aggressive) Say(GetPooledMessage(AggressiveDeath));
                else if (m_Personality == TeamPersonality.Cautious) Say(GetPooledMessage(CautiousDeath));
                else Say(GetPooledMessage(StandardDeath));
            }

            TriggerSquadReaction();
            CleanupBackpack();
            return base.OnBeforeDeath();
        }

        public override void OnDamage(int amount, Mobile from, bool willKill)
        {
            base.OnDamage(amount, from, willKill);
            if (willKill || Deleted) return;
            if (Utility.RandomBool()) PlaySound(GetHurtSound());

            if (!m_IsRetreating && m_CitizenType != (int)CitizenClass.Fighter && (double)Hits/HitsMax < 0.2)
            {
               if (Utility.RandomDouble() < 0.3) 
               { 
                   m_IsRetreating = true; 
                   m_RetreatResetTime = DateTime.UtcNow + TimeSpan.FromSeconds(6.0); 
                   Say("I'm dying!"); 
                   Warmode = false; 
               }
            }
        }

        public override void OnMovement(Mobile m, Point3D oldLocation)
        {
            if (m_IsLeaving || Deleted || m == null || !m.Alive || !m.Player) return;
            DateTime now = DateTime.UtcNow;
            
            if (now < m_NextGreetingTime) return;

            if (CanSendMessage(now) && CanSee(m) && m.InRange(this, 8))
            {
                m_NextGreetingTime = now + TimeSpan.FromSeconds(15.0);
                
                bool isMurderer = (m.Kills >= 5);
                bool isHero = (m.Fame >= 10000 && m.Karma >= 5000);

                if (isMurderer)
                {
                    if (!m_IsEvil) Say(GetPooledMessage(GoodFearMurderer));
                    else Say(GetPooledMessage(EvilGreetMurderer));
                }
                else if (isHero)
                {
                    if (!m_IsEvil) { Direction = GetDirectionTo(m); Animate(32, 5, 1, true, false, 0); Say(GetPooledMessage(HeroicGreetings)); }
                    else { Direction = GetDirectionTo(m); Say(GetPooledMessage(EvilJeerHero)); }
                }
                else
                {
                    Say(m_IsEvil ? GetPooledMessage(EvilGreetings) : GetPooledMessage(FriendlyGreetings));
                }
            }
        }

        public override bool IsEnemy(Mobile m)
        {
            if (m is CaptiveAdventurer) return false;
            return base.IsEnemy(m);
        }
        #endregion

        #region 9. Helpers & Setup
        private void TriggerSquadReaction()
        {
            if (m_MySquad.Count == 0) return;
            foreach (var member in m_MySquad)
            {
                if (member != null && !member.Deleted && member.Alive && member.Map == this.Map && member.InRange(this, 15))
                {
                    if (Utility.RandomBool()) { member.Direction = member.GetDirectionTo(this); member.Say(GetPooledMessage(SquadDeathLines)); }
                    break;
                }
            }
        }

        private void CleanupBackpack()
        {
            if (Backpack == null) return;
            List<Item> toDelete = new List<Item>();
            foreach (Item item in Backpack.Items) { if (item is HeldLight || item is Torch) toDelete.Add(item); }
            Item eq = FindItemOnLayer(Layer.TwoHanded);
            if (eq is HeldLight || eq is Torch) toDelete.Add(eq);
            foreach (Item item in toDelete) item.Delete();
        }

        private void ExecuteDeparture()
        {
            if (m_TeamId != 0) AutoTeamMaintainer.RecycleTeamId(m_TeamId);
            if (this.Map != null) { Effects.SendLocationParticles(EffectItem.Create(this.Location, this.Map, EffectItem.DefaultDuration), 0x3728, 10, 10, 5023); PlaySound(0x1FE); }
            Delete();
        }

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

        private bool IsIndoors()
        {
            if (this.Region is DungeonRegion) return true;
            if (this.Region.IsPartOf("DungeonRegion") || this.Region.IsPartOf("CaveRegion") || this.Region.IsPartOf("BardDungeonRegion")) return true;
            return false;
        }

        private void ManageLightSource()
        {
            bool needLight = IsIndoors();
            if (!needLight)
            {
                int h, m;
                Server.Items.Clock.GetTime(this.Map, this.X, this.Y, out h, out m);
                if (h >= 20 || h <= 6) needLight = true;
            }
            Item twoHand = FindItemOnLayer(Layer.TwoHanded);
            Item heldLight = Backpack.FindItemByType(typeof(HeldLight));
            if (needLight) 
            { 
                if (twoHand == null) 
                { 
                    if (heldLight != null) AddItem(heldLight); 
                    else AddItem(new HeldLight()); 
                } 
            } 
            else if (twoHand is HeldLight) 
            {
                PackItem(twoHand);
            }
        }

        private void EnforceMountState(bool s)
        {
            if (s)
            {
                if (Mount == null) new Horse().Rider = this;
            }
            else
            {
                if (Mount != null)
                {
                    IMount m = Mount;
                    m.Rider = null;
                    if (m is Mobile) ((Mobile)m).Delete();
                }
            }
        }
        
        private void InitStatsAndAppearance() 
        { 
            Female = Utility.RandomBool(); 
            Body = Female ? 401 : 400; 
            Name = Female ? NameList.RandomName("female") : NameList.RandomName("male");
            if (!Female) FacialHairItemID = Utility.RandomList(0, 8254, 8255, 8256, 8257, 8267, 8268, 8269);

            m_CitizenLevel = Utility.RandomMinMax(1, 9); 
            Fame = 2500 * m_CitizenLevel; 
            Karma = m_IsEvil ? -Fame : Fame; 
            VirtualArmor = m_CitizenLevel * 10;
            
            Title = m_IsEvil ? TavernPatrons.GetEvilTitle() : TavernPatrons.GetTitle();

            Hue = m_IsEvil ? Utility.RandomList(0x995, 0x8A4, 0x8B0, 0x8AC) : Utility.RandomSkinHue(); 
            FightMode = m_IsEvil ? FightMode.Good : FightMode.Evil;
            Utility.AssignRandomHair(this); 
            SpeechHue = Utility.RandomTalkHue(); 
            HairHue = FacialHairHue = Utility.RandomHairHue();
            
            int baseSkill = 25 + (m_CitizenLevel * 10);
            int strMin = m_CitizenLevel * 20, strMax = m_CitizenLevel * 30; 
            int dexMin = m_CitizenLevel * 20, dexMax = m_CitizenLevel * 30; 
            int intMin = m_CitizenLevel * 20, intMax = m_CitizenLevel * 30; 
            int hitsMin = m_CitizenLevel * 30, hitsMax = m_CitizenLevel * 40;
            
            int type = Utility.Random(3);
            if (type == 0) 
            { 
                IntelligentAction.DressUpWizards(this, false); m_CitizenType = (int)CitizenClass.Wizard; AI = AIType.AI_Mage; 
                SetSkill(SkillName.Magery, baseSkill); 
                intMax += m_CitizenLevel * 30; 
            }
            else if (type == 1) 
            { 
                IntelligentAction.DressUpFighters(this, "", m_IsEvil, false, true); m_CitizenType = (int)CitizenClass.Fighter; AI = AIType.AI_Melee; 
                
                SetSkill(SkillName.Swords, baseSkill); 
                SetSkill(SkillName.Bludgeoning, baseSkill);
                SetSkill(SkillName.Fencing, baseSkill);
                SetSkill(SkillName.Parry, baseSkill);
                SetSkill(SkillName.Tactics, baseSkill + 10);
                SetSkill(SkillName.Anatomy, baseSkill);

                strMax += m_CitizenLevel * 10; 
                hitsMax += m_CitizenLevel * 20; 
            }
            else 
            { 
                IntelligentAction.DressUpRogues(this, "", m_IsEvil, false, true); m_CitizenType = (int)CitizenClass.Rogue; AI = AIType.AI_Archer; 
                SetSkill(SkillName.Marksmanship, baseSkill); 
                dexMax += m_CitizenLevel * 10; 
            }
            
            foreach (Item item in Items) 
            { 
                if (item is BaseClothing && !(item is BaseArmor) && item.Hue == 0 && Utility.RandomBool()) 
                    item.Hue = Utility.RandomNeutralHue(); 
            }
            
            SetStr(strMin, strMax); 
            SetDex(dexMin, dexMax); 
            SetInt(intMin, intMax); 
            SetHits(hitsMin, hitsMax);
            
            AddWeapon(true);
            Server.Misc.IntelligentAction.GiveAdventureGear(this);
        }

        public void AddWeapon(bool initial) 
        { 
             BaseWeapon hand = FindItemOnLayer(Layer.OneHanded) as BaseWeapon; 
             BaseWeapon twohand = FindItemOnLayer(Layer.TwoHanded) as BaseWeapon;
             
             if (!initial && (hand != null || twohand != null)) return;

             if (m_CitizenType == (int)CitizenClass.Fighter) 
             { 
                 if (hand == null && twohand == null) 
                 {
                     switch(Utility.Random(3)) 
                     { 
                         case 0: AddItem(new Longsword()); break; 
                         case 1: AddItem(new BattleAxe()); break; 
                         case 2: AddItem(new Mace()); break; 
                     } 
                 }
             }
             else if (m_CitizenType == (int)CitizenClass.Wizard) 
             { 
                 if (initial) 
                 { 
                     if(hand!=null) hand.Delete(); 
                     if(twohand!=null) twohand.Delete(); 
                 } 
                 AddItem(Utility.RandomBool() ? (Item)new GnarledStaff() : new QuarterStaff()); 
             }
             else if (m_CitizenType == (int)CitizenClass.Rogue) 
             { 
                 if (initial) 
                 { 
                     if(hand!=null) hand.Delete(); 
                     if(twohand!=null) twohand.Delete(); 
                 } 
                 
                 if (Utility.RandomDouble() < 0.15) 
                 { 
                     AddItem(new Item(0x13C6){Name="Throwing Gloves"}); 
                     PackItem(new Item(0xF0E){Name="Throwing Ammo"}); 
                 } 
                 else 
                 { 
                     int ac=60; 
                     switch(Utility.Random(5))
                     { 
                         case 0: AddItem(new Bow()); PackItem(new Arrow(ac)); break; 
                         case 1: AddItem(new Crossbow()); PackItem(new Bolt(ac)); break; 
                         case 2: AddItem(new HeavyCrossbow()); PackItem(new Bolt(ac)); break; 
                         case 3: AddItem(new RepeatingCrossbow()); PackItem(new Bolt(ac)); break; 
                         case 4: AddItem(new CompositeBow()); PackItem(new Arrow(ac)); break; 
                     } 
                 } 
             } 
        }
        #endregion

        #region 10. Serialization
        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write((int)0);
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);
            int version = reader.ReadInt();
            Timer.DelayCall(TimeSpan.Zero, Delete);
        }
        #endregion
    }

    // ========================================================================
    // 3. Auto Team Maintainer (Manager)
    // ========================================================================
    public static class AutoTeamMaintainer
    {
        private static bool s_Enabled = true;
        private static Timer s_MaintenanceTimer;
        private static readonly Queue<int> s_RecycledIds = new Queue<int>();
        private static int s_NextTeamId = 1;
        private static readonly object s_IdLock = new object();

        // Optimized Caching Logic
        private static readonly Dictionary<Mobile, CachedCount> s_NearbyCountCache = new Dictionary<Mobile, CachedCount>();
        private static readonly TimeSpan CountCacheDuration = TimeSpan.FromSeconds(5);

        private class CachedCount
        {
            public int Count;
            public DateTime Time;
        }

        public static void Initialize() 
        { 
            if (s_MaintenanceTimer != null) s_MaintenanceTimer.Stop(); 
            s_MaintenanceTimer = Timer.DelayCall(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), MaintainTeams); 
        }
        
        public static int GetNewTeamId() { lock (s_IdLock) return (s_RecycledIds.Count > 0) ? s_RecycledIds.Dequeue() : s_NextTeamId++; }
        public static void RecycleTeamId(int id) { lock (s_IdLock) s_RecycledIds.Enqueue(id); }

        private static void MaintainTeams()
        {
            if (!s_Enabled) return;
            foreach (NetState state in NetState.Instances)
            {
                Mobile m = state.Mobile;
                if (m != null && m.Player && m.Alive && m.Map != null && m.Map != Map.Internal)
                {
                    // Using random chance to distribute load
                    if (Utility.RandomDouble() < 0.6) TrySpawnTeamForPlayer(m);
                }
            }
            
            PruneNearbyCache();
        }

        private static void TrySpawnTeamForPlayer(Mobile pm)
        {
            DateTime now = DateTime.UtcNow;
            int nearbyCount;
            CachedCount cached;

            if (!s_NearbyCountCache.TryGetValue(pm, out cached))
            {
                cached = new CachedCount();
                s_NearbyCountCache[pm] = cached;
                cached.Time = DateTime.MinValue;
            }

            if (now - cached.Time < CountCacheDuration)
            {
                nearbyCount = cached.Count;
            }
            else
            {
                nearbyCount = ComputeNearbyCount(pm);
                cached.Count = nearbyCount;
                cached.Time = now;
            }

            if (nearbyCount > 5) return;

            Point3D spawnLoc = FindSpawnLocation(pm);
            if (spawnLoc == Point3D.Zero) return;

            int teamId = GetNewTeamId();
            bool isEvil = Utility.RandomBool();
            bool mounted = Utility.RandomDouble() < 0.4;
            int size = Utility.RandomMinMax(2, 5);

            List<AdventurerTeam> newSquadMembers = new List<AdventurerTeam>();
            for (int i = 0; i < size; i++)
            {
                AdventurerTeam npc = new AdventurerTeam(teamId, isEvil, mounted);
                npc.MoveToWorld(spawnLoc, pm.Map);
                npc.Home = spawnLoc; npc.RangeHome = 12;
                newSquadMembers.Add(npc);
            }

            if (isEvil && Utility.RandomDouble() < 0.20)
            {
                CaptiveAdventurer captive = new CaptiveAdventurer();
                captive.MoveToWorld(spawnLoc, pm.Map);
            }

            foreach (AdventurerTeam member in newSquadMembers)
            {
                foreach (AdventurerTeam ally in newSquadMembers)
                {
                    if (member != ally) member.AddToSquad(ally);
                }
            }
        }

        private static int ComputeNearbyCount(Mobile pm)
        {
            int count = 0;
            foreach (Mobile m in pm.GetMobilesInRange(25))
            {
                if (m != null && !m.Deleted && m is AdventurerTeam)
                    count++;
            }
            return count;
        }

        private static void PruneNearbyCache()
        {
            List<Mobile> toRemove = new List<Mobile>();
            DateTime now = DateTime.UtcNow;

            foreach (var kvp in s_NearbyCountCache)
            {
                if (kvp.Key == null || kvp.Key.Deleted || now - kvp.Value.Time > TimeSpan.FromMinutes(1))
                    toRemove.Add(kvp.Key);
            }

            foreach (Mobile m in toRemove)
                s_NearbyCountCache.Remove(m);
        }

        private static Point3D FindSpawnLocation(Mobile nearPlayer)
        {
            Map map = nearPlayer.Map;
            for (int i = 0; i < 5; i++)
            {
                int dist = Utility.RandomMinMax(24, 35);
                double ang = Utility.RandomDouble() * Math.PI * 2;
                int x = nearPlayer.X + (int)(Math.Cos(ang) * dist);
                int y = nearPlayer.Y + (int)(Math.Sin(ang) * dist);
                Point3D p = new Point3D(x, y, map.GetAverageZ(x, y));
                if (IsInForbiddenForbidden(p, map)) continue;
                if (map.CanSpawnMobile(p)) return p;
            }
            return Point3D.Zero;
        }

        private static bool IsInForbiddenForbidden(Point3D loc, Map map)
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
                return true;
            return false;
        }
    }
}
