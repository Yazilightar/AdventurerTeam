// ====================================================================
// AdventurerTeam.cs
// RunUO 2.0 + .NET 2.0 compatible
//
// Design notes:
// - Heavy logic runs only on "primary thinkers" (leader or a small rotating subset).
// - Expensive scans (mobiles/items) are aggressively throttled.
// - Timer.DelayCall allocations were replaced with lightweight pending-action timestamps.
// - DateTime.UtcNow is cached per OnThink tick to reduce overhead.
// ====================================================================

using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.CompilerServices;
using Server;
using Server.Items;
using Server.Mobiles;
using Server.Regions;
using Server.Network;
using Server.Misc;
using Server.Commands;

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
        #region Dialogue (static)

        private static readonly string[] FriendlyChat = new string[]
        {
            "The wyrms in the deep caves grow bolder each day...",
            "I barely escaped from a pack of dire wolves yesterday.",
            "These lands are cursed, I tell you. Evil stirs in the shadows.",
            "The undead rise more frequently near the old crypts.",
            "I saw a dragon's shadow pass overhead last night.",
            "Careful where you tread - traps abound in ancient ruins.",
            "The forest trolls have been raiding caravans again.",
            "They say a forgotten tomb lies beneath the old keep.",
            "I seek a legendary blade, lost to time.",
            "Ancient treasures await those brave enough to claim them.",
            "A merchant spoke of ruins filled with gold and jewels.",
            "The old wizard's tower supposedly holds great power.",
            "I heard whispers of a hidden vault in the mountains.",
            "My companions fell to an ambush three days past.",
            "I seek fellow brave souls to delve into the darkness.",
            "Traveling alone in these parts is a death sentence.",
            "Lost my entire party to a demon in the lower depths.",
            "We could use another sword arm for what lies ahead.",
            "Running low on supplies... need to restock soon.",
            "Any healers nearby? My wounds still ache.",
            "I'd pay good coin for quality healing potions.",
            "These old bandages won't hold much longer.",
            "Need better armor before venturing deeper.",
            "Red-cloaked murderers were spotted near the crossroads!",
            "The northern pass is held by bandits now.",
            "Beware the dark knights - they show no mercy.",
            "A band of reavers camps just beyond those hills.",
            "Stay out of the eastern woods after dark.",
            "The old legends speak of power sealed in these ruins.",
            "Strange lights dance in the graveyard at midnight.",
            "I've seen things down there that defy explanation.",
            "The ancients left more than just treasure behind.",
            "Dark rituals are being performed in the lower levels."
        };

        private static readonly string[] EvilChat = new string[]
        {
            "Your coin or your life, fool.",
            "Fresh meat for the crows...",
            "This is OUR territory. Pay the toll or bleed.",
            "The weak exist only to serve the strong.",
            "I smell fear... and gold.",
            "Eight corpses before noon. Good hunting today.",
            "Their screams still echo in my ears... delightful.",
            "Left a trail of bodies from here to the coast.",
            "The river runs red with their blood.",
            "I've lost count of how many I've killed this week.",
            "These ruins belong to us now. Leave or join the dead.",
            "Turn back while you still draw breath.",
            "Only the strong survive here. You don't look strong.",
            "Trespassers end up feeding the crows.",
            "This dungeon is ours. Find your own grave to rob.",
            "Need someone killed? I know people...",
            "For the right price, anyone can disappear.",
            "We don't ask questions. We just collect heads.",
            "Gold talks. Mercy doesn't.",
            "Honor is for the dead and the foolish.",
            "In the end, only power matters.",
            "The darkness welcomes all who embrace it.",
            "Law and order? Chains for the weak.",
            "Morality is a luxury we can't afford."
        };

        private static readonly string[] CombatYell = new string[]
        {
            "Surround them!", "Cut off their escape!", "Focus on the spell-caster!",
            "Shield wall, hold formation!", "Flank them from the left!",
            "Watch for ambushes!", "Cover the rear!", "Break their line!",
            "Press the attack!", "Fall back and regroup!", "Healer down! Protect them!",
            "They're flanking us!", "Ambush! Weapons ready!", "Trap! Watch your step!",
            "Reinforcements incoming!", "We're surrounded!", "Hold the line!",
            "For glory!", "Stand and fight!", "No retreat!", "We end this now!",
            "Fight or die!", "To the last breath!", "Show no mercy!", "Give them steel!"
        };

        private static readonly string[] WizardCombatTactics = new string[]
        {
            "*weaves protective spell*", "*channels arcane power*",
            "Focus on my target!", "Magic will turn the tide!",
            "*mutters incantation*", "Keep them at range!",
            "*gestures mystically*", "The arcane flows through me!"
        };

        private static readonly string[] FighterCombatTactics = new string[]
        {
            "*adopts defensive stance*", "*raises shield*", "Hold the line!",
            "I won't fall!", "*grits teeth*", "Come on then!",
            "Is that all you've got?!", "*plants feet firmly*",
            "Stand behind me!", "I'll take the brunt!"
        };

        private static readonly string[] RogueCombatTactics = new string[]
        {
            "*repositions for better shot*", "Keep them busy!",
            "*aims carefully*", "I'll flank them!", "Watch your back!",
            "*nocks another arrow*", "*finds vantage point*",
            "Cover while I reload!"
        };

        private static readonly string[] LowHealthWarnings = new string[]
        {
            "I'm hurt badly!", "Can't take much more!", "Need help here!",
            "I'm in trouble!", "Wounds are serious!", "*bleeding heavily*",
            "Getting weak...", "Lost a lot of blood!"
        };

        private static readonly string[] CriticalHealthWarnings = new string[]
        {
            "I'm going down!", "This is bad!", "Near death here!",
            "Can barely stand!", "*gasps in pain*", "Someone help!",
            "Won't last much longer!", "Vision fading..."
        };

        private static readonly string[] EnemyCountReactions = new string[]
        {
            "More of them incoming!", "We're outnumbered!", "Too many enemies!",
            "Watch the flanks!", "Stay together!", "Multiple targets!",
            "They're swarming us!", "Don't split up!"
        };

        private static readonly string[] AllyDownReactions = new string[]
        {
            "They got our wizard!", "Protect the wounded!", "Man down!",
            "Cover them while they recover!", "Keep fighting!", "Don't let up!"
        };

        private static readonly string[] VictoryLines = new string[]
        {
            "That was close! Everyone alright?", "Good fight! Check the body for coin.",
            "We make a good team!", "Another one bites the dust.",
            "I need to catch my breath...", "Did anyone get hurt badly?",
            "That beast was tougher than expected.", "Victory! But stay alert.",
            "Well fought, friends!", "*wipes blood from weapon*",
            "Excellent teamwork!", "They didn't stand a chance!"
        };

        private static readonly string[] LootingLines = new string[]
        {
            "*searches the corpse*", "Let's see what it was carrying...",
            "Hmm, not much here.", "This should fetch a good price!",
            "*pockets the loot*", "Split the gold evenly, friends.",
            "Nothing valuable on this one.", "Some coin and trinkets...",
            "Better than nothing.", "I'll carry this."
        };

        private static readonly string[] RetreatLines = new string[]
        {
            "Fall back! I'm badly wounded!", "I can't take much more!",
            "Retreating! Cover me!", "Too many of them!",
            "*stumbles backward*", "I need to heal!",
            "Not dying here today!", "Getting out of here!",
            "Tactical withdrawal!"
        };

        private static readonly string[] MourningLines = new string[]
        {
            "No! They got him!", "Man down! Avenge our fallen!",
            "*grieves* We'll make them pay!",
            "Hold the line! Don't let their death be in vain!",
            "They were a good fighter...", "Damn it! Stay focused!",
            "Another one lost...", "This place claims too many lives!",
            "We'll honor their memory!", "Fight harder!"
        };

        private static readonly string[] CorpseComments = new string[]
        {
            "Someone died here recently...", "*examines the corpse* Poor soul.",
            "This place is dangerous indeed.", "We should be more careful.",
            "Whatever killed them might still be near.",
            "The bodies pile up in these cursed ruins.",
            "Death is everywhere here...", "Another victim of this place."
        };

        private static readonly string[] InjuredComments = new string[]
        {
            "I could use some healing...", "These wounds won't heal themselves.",
            "*winces in pain*", "I've felt better, that's for sure.",
            "Need to rest soon...", "Anyone have bandages?",
            "Still bleeding...", "These cuts sting."
        };

        private static readonly string[] IdleComments = new string[]
        {
            "I wonder what lies deeper in...", "Stay sharp. I sense danger.",
            "We should keep moving.", "Anyone else hear that?",
            "*adjusts equipment*", "Something doesn't feel right...",
            "Keep your eyes open.", "This silence is unnerving.",
            "Too quiet...", "What's that sound?"
        };

        private static readonly string[] PotionLines = new string[]
        {
            "*drinks a healing potion*", "That should help!",
            "*gulps potion hastily*", "Much better!",
            "*uncorks flask*", "Good thing I brought these!",
            "*downs potion*", "Ah, that's better!"
        };

        private static readonly string[] BandageLines = new string[]
        {
            "*applies bandages*", "*binds wounds*",
            "Just need to stop the bleeding...", "*wraps injuries*",
            "These bandages will hold.", "*treats wounds*"
        };

        private static readonly string[] HealSpellLines = new string[]
        {
            "*casts healing magic on ally*", "In Vas Mani! Be healed!",
            "*channels healing energy*", "Let the light mend your wounds!",
            "*weaves restorative spell*", "Hold still, I'll heal you!"
        };

        private static readonly string[] OutOfSuppliesLines = new string[]
        {
            "Out of potions!", "No healing left!", "Need supplies badly!",
            "Someone have a spare potion?", "My bandages are gone!",
            "Out of healing supplies!"
        };

        private static readonly string[] DepartureLines = new string[]
        {
            "We've lingered here long enough. Let's move on.",
            "Time to seek fortune elsewhere, friends.",
            "These halls grow quiet. Onward!",
            "Our work here is done. To the next adventure!",
            "Come, companions. Other treasures await.",
            "This place yields no more. Let's go.",
            "The road calls us. Time to move on.",
            "We've cleared this area. Forward!",
            "No more prey here. Let's find richer hunting grounds.",
            "Time to depart. Other challenges await us."
        };

        private static readonly string[] LeaderOrders = new string[]
        {
            "Formation! Back to back!",
            "Protect each other!",
            "Watch your flanks!",
            "Focus fire!"
        };

        // Cached lengths (tiny micro-optimization).
        private static readonly int FriendlyChatLen = FriendlyChat.Length;
        private static readonly int EvilChatLen = EvilChat.Length;
        private static readonly int CombatYellLen = CombatYell.Length;
        private static readonly int VictoryLinesLen = VictoryLines.Length;
        private static readonly int LootingLinesLen = LootingLines.Length;
        private static readonly int RetreatLinesLen = RetreatLines.Length;
        private static readonly int MourningLinesLen = MourningLines.Length;
        private static readonly int CorpseCommentsLen = CorpseComments.Length;
        private static readonly int InjuredCommentsLen = InjuredComments.Length;
        private static readonly int IdleCommentsLen = IdleComments.Length;
        private static readonly int PotionLinesLen = PotionLines.Length;
        private static readonly int BandageLinesLen = BandageLines.Length;
        private static readonly int HealSpellLinesLen = HealSpellLines.Length;
        private static readonly int OutOfSuppliesLinesLen = OutOfSuppliesLines.Length;
        private static readonly int DepartureLinesLen = DepartureLines.Length;
        private static readonly int LeaderOrdersLen = LeaderOrders.Length;

        #endregion

        #region Team Storage

        internal class TeamInfo
        {
            public readonly List<AdventurerTeam> Members = new List<AdventurerTeam>(8);
            public AdventurerTeam Leader;

            // Centralized scan cache (updated by the team leader).
            public DateTime SharedScanTime = DateTime.MinValue;
            public int SharedEnemyCount = 0;
            public int SharedInjuredAllies = 0;
            public readonly List<AdventurerTeam> SharedNearbyMembers = new List<AdventurerTeam>(MaxCachedNearbyMembers);
        }

        internal static readonly Dictionary<int, TeamInfo> AllTeams = new Dictionary<int, TeamInfo>(512);
        internal static readonly object AllTeamsLock = new object();

        #endregion

        #region Performance Constants

        private const double CriticalHealthThreshold = 0.30;
        private const double LowHealthThreshold = 0.50;
        private const double RetreatThreshold = 0.25;
        private const double HealAllyThreshold = 0.60;

        private const int TeamMemberRange = 12;
        private const int ActiveScanIntervalSeconds = 15;

        private const int SkillBase = 25;
        private const int SkillPerLevel = 10;

        private const int ChatIntervalMin = 20;
        private const int ChatIntervalMax = 40;

        private static readonly TimeSpan GreetCooldown = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan MovementThrottle = TimeSpan.FromSeconds(5);

        // Coarse-grained tick throttle.
        private const int ThinkThrottleTicks = 2;


        // Dynamic tick throttle settings.
        // NOTE: Values are intentionally small and easy to tune per shard.
        private const int CombatThinkThrottle = 1;
        private const int LeaderIdleThinkThrottle = 2;
        private const int NonLeaderIdleThinkThrottle = 5;

        // Hard cap for cached nearby members (keeps scans bounded in crowded areas).
        private const int MaxCachedNearbyMembers = 8;
        // Lock striping for team dictionary (reduces contention).
        private static readonly object[] TeamLocks = new object[4];

        // Rescan throttles.
        private const double CombatScanIntervalSeconds = 8.0;
        private const double TeamScanIntervalSeconds = 18.0;
        private const int EnvironmentScanMinSeconds = 30;
        private const int EnvironmentScanMaxSeconds = 60;

        // Pending action delays.
        private static readonly TimeSpan CelebrateDelay = TimeSpan.FromSeconds(1.0);
        private static readonly TimeSpan LootingDelay = TimeSpan.FromSeconds(1.4);
        private static readonly TimeSpan MourningDelay = TimeSpan.FromSeconds(1.6);
        private static readonly TimeSpan RetreatResetDelay = TimeSpan.FromSeconds(6.0);
        private static readonly TimeSpan DepartureDelay = TimeSpan.FromSeconds(15.0);

        #endregion

        #region Instance Fields

        private int m_CitizenType;
        private int m_CitizenLevel;
        private bool m_SpawnedBySystem;
        private bool m_IsEvil;
        private int m_TeamId;

        private DateTime m_LastSeen;
        private DateTime m_NextActiveScan;
        private DateTime m_NextChatTime;
        private DateTime m_LastGreetTime;
        private DateTime m_LastMovementCheck;

        private readonly List<AdventurerTeam> m_CachedNearbyMembers = new List<AdventurerTeam>(10);

        private DateTime m_LastCombatTime;
        private bool m_CelebrationDone;
        private bool m_IsRetreating;
        private bool m_DeathAnnounced;
        private DateTime m_NextEnvironmentCheck;

        private int m_CombatRound;
        private DateTime m_LastTacticalShout;
        private bool m_HasWarnedLowHealth;
        private bool m_HasWarnedCriticalHealth;

        private DateTime m_NextSelfHeal;
        private DateTime m_NextAllyHeal;
        private bool m_IsUsingBandage;

        private DateTime m_BoredTime;
        private bool m_IsLeaving;

        private DateTime m_LastCombatScan;
        private int m_CachedEnemyCount;
        private int m_CachedInjuredAllies;

        private DateTime m_LastTeamScan;

        // Delayed healing (no timer allocations).
        private DateTime m_PendingHealTime;
        private Mobile m_PendingHealTarget;
        private int m_PendingHealAmount;
        private bool m_PendingIsMagicHeal;

        // Delayed actions (no timer allocations).
        private bool m_PendingCelebrate;
        private DateTime m_PendingCelebrateTime;

        private bool m_PendingLoot;
        private DateTime m_PendingLootTime;

        private bool m_PendingMourn;
        private DateTime m_PendingMournTime;

        private bool m_PendingRetreatReset;
        private DateTime m_PendingRetreatResetTime;

        private bool m_PendingDeparture;
        private DateTime m_PendingDepartureTime;

        // Cached random values.
        private double m_CachedRandom1;
        private double m_CachedRandom2;
        private double m_CachedRandom3;
        private DateTime m_NextRandomRefresh;

        // Per-NPC speech throttle.
        private DateTime m_LastMessageTime;

        // Combatant distance cache.
        private Mobile m_CachedCombatant;
        private int m_CachedCombatantDistance;

        // Leader cache.
        private bool m_CachedIsLeader;
        private DateTime m_NextLeaderCheck;

        // Optional scratch buffer for future string operations.
        private readonly StringBuilder m_StringBuilderCache = new StringBuilder(128);

        // Small per-NPC message pool (reuses static string references).
        private const int MessagePoolSize = 8;
        private readonly string[] m_MessagePool = new string[MessagePoolSize];
        private int m_MessagePoolIndex;


        #endregion

        #region Static Init

        static AdventurerTeam()
        {
            for (int i = 0; i < TeamLocks.Length; i++)
                TeamLocks[i] = new object();
        }

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

        [CommandProperty(AccessLevel.GameMaster)]
        public bool SpawnedBySystem
        {
            get { return m_SpawnedBySystem; }
            set { m_SpawnedBySystem = value; }
        }

        [CommandProperty(AccessLevel.GameMaster)]
        public int TeamId
        {
            get { return m_TeamId; }
            set { m_TeamId = value; }
        }

        [CommandProperty(AccessLevel.GameMaster)]
        public DateTime LastSeen
        {
            get { return m_LastSeen; }
            set { m_LastSeen = value; }
        }

        [CommandProperty(AccessLevel.GameMaster)]
        public bool IsEvil
        {
            get { return m_IsEvil; }
            set { m_IsEvil = value; }
        }

        public override bool AlwaysMurderer
        {
            get { return m_IsEvil; }
        }

        private bool IsTeamLeader
        {
            get
            {
                return IsTeamLeaderAt(DateTime.UtcNow);
            }
        }

        // Uses caller-provided 'now' to avoid repeated DateTime.UtcNow calls in hot paths.
        private bool IsTeamLeaderAt(DateTime now)
        {
            if (now >= m_NextLeaderCheck)
            {
                m_NextLeaderCheck = now.AddSeconds(10);
                m_CachedIsLeader = CheckIfLeaderInternal();
            }

            return m_CachedIsLeader;
        }
        #endregion

        #region Constructors

        [Constructable]
        public AdventurerTeam()
            : this(0, false)
        {
        }

        [Constructable]
        public AdventurerTeam(int teamId, bool isEvil)
            : base(AIType.AI_Melee, FightMode.None, 10, 1, 0.2, 0.4)
        {
            m_TeamId = teamId;
            m_IsEvil = isEvil;
            m_SpawnedBySystem = (teamId != 0);

            InitStatsAndAppearance();
            AddToTeam(teamId);

            DateTime now = DateTime.UtcNow;

            m_LastSeen = now;
            m_NextActiveScan = now.AddSeconds(ActiveScanIntervalSeconds);
            m_NextChatTime = now.AddSeconds(Utility.RandomMinMax(3, 15));
            m_NextEnvironmentCheck = now.AddSeconds(Utility.RandomMinMax(EnvironmentScanMinSeconds, EnvironmentScanMaxSeconds));
            m_LastGreetTime = DateTime.MinValue;
            m_LastMovementCheck = DateTime.MinValue;

            m_LastCombatTime = DateTime.MinValue;
            m_CelebrationDone = false;
            m_IsRetreating = false;
            m_DeathAnnounced = false;

            m_CombatRound = 0;
            m_LastTacticalShout = DateTime.MinValue;
            m_HasWarnedLowHealth = false;
            m_HasWarnedCriticalHealth = false;

            m_NextSelfHeal = DateTime.MinValue;
            m_NextAllyHeal = DateTime.MinValue;
            m_IsUsingBandage = false;

            m_BoredTime = now.AddMinutes(Utility.RandomMinMax(15, 30));
            m_IsLeaving = false;

            m_LastCombatScan = DateTime.MinValue;
            m_CachedEnemyCount = 0;
            m_CachedInjuredAllies = 0;

            m_LastTeamScan = DateTime.MinValue;

            m_PendingHealTime = DateTime.MinValue;
            m_PendingHealTarget = null;

            m_LastMessageTime = DateTime.MinValue;

            m_CachedCombatant = null;
            m_CachedCombatantDistance = 999;

            m_CachedIsLeader = false;
            m_NextLeaderCheck = DateTime.MinValue;

            RefreshRandomCache();
            m_NextRandomRefresh = now.AddSeconds(2);
        }

        public AdventurerTeam(Serial serial)
            : base(serial)
        {
        }

        #endregion

        #region Team Helpers

        private object GetTeamLock()
        {
            int id = m_TeamId;
            int index = (id == 0) ? 0 : (Math.Abs(id) % TeamLocks.Length);
            return TeamLocks[index];
        }

        private bool CheckIfLeaderInternal()
        {
            if (m_TeamId == 0)
                return false;

            object teamLock = GetTeamLock();

            lock (teamLock)
            {
                TeamInfo ti;

                if (!AllTeams.TryGetValue(m_TeamId, out ti))
                    return false;

                return (ti.Leader != null && ti.Leader.Serial == Serial);
            }
        }

        private void AddToTeam(int teamId)
        {
            if (teamId == 0)
                return;

            TeamInfo ti;
            object teamLock = GetTeamLock();

            lock (teamLock)
            {
                if (!AllTeams.TryGetValue(teamId, out ti))
                {
                    ti = new TeamInfo();
                    AllTeams[teamId] = ti;
                }
            }

            lock (ti.Members)
            {
                if (!ti.Members.Contains(this))
                    ti.Members.Add(this);

                if (ti.Leader == null || Serial < ti.Leader.Serial)
                    ti.Leader = this;
            }
        }

        private void RemoveFromTeam(int teamId)
        {
            if (teamId == 0)
                return;

            TeamInfo ti;
            object teamLock = GetTeamLock();

            lock (teamLock)
            {
                if (!AllTeams.TryGetValue(teamId, out ti))
                    return;

                lock (ti.Members)
                {
                    ti.Members.Remove(this);

                    if (ti.Leader == this)
                    {
                        AdventurerTeam newLeader = null;

                        for (int i = 0; i < ti.Members.Count; i++)
                        {
                            AdventurerTeam at = ti.Members[i];

                            if (at == null || at.Deleted)
                            {
                                ti.Members.RemoveAt(i--);
                                continue;
                            }

                            if (newLeader == null || at.Serial < newLeader.Serial)
                                newLeader = at;
                        }

                        ti.Leader = newLeader;
                    }

                    if (ti.Members.Count == 0)
                    {
                        AllTeams.Remove(teamId);
                        AutoTeamMaintainer.RecycleTeamId(teamId);
                    }
                }
            }
        }

        #endregion

        // --------------------------------------------------------------------
        // Centralized team cache helpers
        // --------------------------------------------------------------------
        private bool TryGetTeamInfo(int teamId, out TeamInfo ti)
        {
            ti = null;

            if (teamId == 0)
                return false;

            object teamLock = GetTeamLock();
            lock (teamLock)
            {
                return AllTeams.TryGetValue(teamId, out ti);
            }
        }

        private List<AdventurerTeam> GetSharedNearbyMembers(DateTime now)
        {
            if (m_TeamId == 0)
                return m_CachedNearbyMembers;

            TeamInfo ti;
            if (TryGetTeamInfo(m_TeamId, out ti) && ti != null)
            {
                if ((now - ti.SharedScanTime).TotalSeconds < (TeamScanIntervalSeconds * 2.0))
                    return ti.SharedNearbyMembers;
            }

            return m_CachedNearbyMembers;
        }

		#region Caches / Throttles

        private bool CanSendMessage(DateTime now)
        {
            if ((now - m_LastMessageTime).TotalSeconds < 0.5)
                return false;

            m_LastMessageTime = now;
            return true;
        }

        // Returns a pooled message for frequently used speech lines.
        private string GetPooledMessage(string[] source, int sourceLen)
        {
            int slot = (m_MessagePoolIndex++ & (MessagePoolSize - 1));
            string msg = m_MessagePool[slot];

            if (msg == null || m_CachedRandom3 < 0.25)
                msg = source[Utility.Random(sourceLen)];

            m_MessagePool[slot] = msg;
            return msg;
        }


        private void RefreshRandomCache()
        {
            // Cached random values are used for all probabilistic decisions.
            m_CachedRandom1 = Utility.RandomDouble();
            m_CachedRandom2 = Utility.RandomDouble();
            m_CachedRandom3 = Utility.RandomDouble();
        }

        

        private bool IsPrimaryThinker(DateTime now, bool isLeader)
        {
            if (m_TeamId == 0)
                return true;

            if (isLeader)
                return true;

            // Allow a small rotating subset to avoid a totally silent team.
            return (Serial.Value % 3) == (now.Second % 3);
        }


        #endregion

        #region Initialization

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

            int strMin = m_CitizenLevel * 20, strMax = m_CitizenLevel * 30;
            int dexMin = m_CitizenLevel * 20, dexMax = m_CitizenLevel * 30;
            int intMin = m_CitizenLevel * 20, intMax = m_CitizenLevel * 30;

            int hitsMin = m_CitizenLevel * 30, hitsMax = m_CitizenLevel * 40;
            int resistMin = m_CitizenLevel * 3, resistMax = m_CitizenLevel * 6;

            switch (type)
            {
                case 0: // Wizard
                    IntelligentAction.DressUpWizards(this, m_IsEvil);
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
                    resistMax += m_CitizenLevel * 3;
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
                    resistMax += m_CitizenLevel * 2;
                    break;
            }

            SetStr(strMin, strMax);
            SetDex(dexMin, dexMax);
            SetInt(intMin, intMax);
            SetHits(hitsMin, hitsMax);

            int finalResistMax = (resistMax > 75) ? 75 : resistMax;

            for (int r = 0; r <= (int)ResistanceType.Energy; r++)
                SetResistance((ResistanceType)r, resistMin, finalResistMax);

            AddWeapon(true);
            AddHealingSupplies();
        }

        public void AddWeapon(bool initial)
        {
            BaseWeapon hand = FindItemOnLayer(Layer.OneHanded) as BaseWeapon;
            BaseWeapon twohand = FindItemOnLayer(Layer.TwoHanded) as BaseWeapon;

            if (!initial && (hand != null || twohand != null))
                return;

            if (m_CitizenType == (int)CitizenClass.Fighter)
            {
                if (hand != null || twohand != null)
                    return;

                switch (Utility.Random(3))
                {
                    case 0: AddItem(new Longsword()); break;
                    case 1: AddItem(new BattleAxe()); break;
                    case 2: AddItem(new Mace()); break;
                }

                return;
            }

            if (initial)
            {
                if (hand != null) hand.Delete();
                if (twohand != null) twohand.Delete();
            }

            if (Utility.RandomBool() && (m_CitizenType == (int)CitizenClass.Wizard || m_CitizenType == (int)CitizenClass.Rogue))
            {
                Item glove = new Item(0x13C6);
                glove.Name = "Throwing Gloves";
                AddItem(glove);

                Item ammo = new Item(0xF0E);
                ammo.Name = "Throwing Ammunition";
                PackItem(ammo);
                return;
            }

            if (m_CitizenType == (int)CitizenClass.Wizard)
            {
                AddItem(Utility.RandomBool() ? (Item)new GnarledStaff() : new QuarterStaff());
                return;
            }

            if (m_CitizenType == (int)CitizenClass.Rogue)
            {
                switch (Utility.Random(8))
                {
                    case 0: AddItem(new Bow()); PackItem(new Arrow(Utility.RandomMinMax(20, 40))); break;
                    case 1: AddItem(new Crossbow()); PackItem(new Bolt(Utility.RandomMinMax(20, 40))); break;
                    case 2: AddItem(new HeavyCrossbow()); PackItem(new Bolt(Utility.RandomMinMax(20, 40))); break;
                    case 3: AddItem(new RepeatingCrossbow()); PackItem(new Bolt(Utility.RandomMinMax(20, 40))); break;
                    case 4: AddItem(new CompositeBow()); PackItem(new Arrow(Utility.RandomMinMax(20, 40))); break;
                    case 5: AddItem(new Bow()); PackItem(new Arrow(Utility.RandomMinMax(20, 40))); break;
                    case 6: AddItem(new Crossbow()); PackItem(new Bolt(Utility.RandomMinMax(20, 40))); break;
                    case 7: AddItem(new Crossbow()); PackItem(new Bolt(Utility.RandomMinMax(20, 40))); break;
                }

                return;
            }
        }

        private void AddHealingSupplies()
        {
            int potionCount = Utility.RandomMinMax(3, 5);
            int bandageCount = Utility.RandomMinMax(20, 40);

            switch (m_CitizenType)
            {
                case (int)CitizenClass.Wizard:
                    if (m_CitizenLevel >= 7)
                    {
                        for (int i = 0; i < Math.Max(2, potionCount - 2); i++)
                            PackItem(new GreaterHealPotion());
                    }
                    else if (m_CitizenLevel >= 4)
                    {
                        for (int i = 0; i < Math.Max(2, potionCount - 2); i++)
                            PackItem(new HealPotion());
                    }
                    else
                    {
                        for (int i = 0; i < Math.Max(2, potionCount - 2); i++)
                            PackItem(new LesserHealPotion());
                    }
                    break;

                case (int)CitizenClass.Fighter:
                    PackItem(new Bandage(bandageCount + 10));

                    if (m_CitizenLevel >= 7)
                    {
                        for (int i = 0; i < potionCount; i++)
                            PackItem(new GreaterHealPotion());
                    }
                    else if (m_CitizenLevel >= 4)
                    {
                        for (int i = 0; i < potionCount; i++)
                            PackItem(new HealPotion());
                    }
                    else
                    {
                        for (int i = 0; i < potionCount; i++)
                            PackItem(new LesserHealPotion());
                    }
                    break;

                case (int)CitizenClass.Rogue:
                    PackItem(new Bandage(bandageCount));

                    if (m_CitizenLevel >= 7)
                    {
                        for (int i = 0; i < potionCount; i++)
                            PackItem(new GreaterHealPotion());
                    }
                    else if (m_CitizenLevel >= 4)
                    {
                        for (int i = 0; i < potionCount; i++)
                            PackItem(new HealPotion());
                    }
                    else
                    {
                        for (int i = 0; i < potionCount; i++)
                            PackItem(new LesserHealPotion());
                    }
                    break;
            }
        }

        #endregion

        #region Main AI


        // --------------------------------------------------------------------
        // OnThink state machine
        // --------------------------------------------------------------------
        private enum ThinkState
        {
            Idle,
            Combat,
            Retreating,
            Leaving
        }

        private ThinkState GetThinkState()
        {
            if (m_IsLeaving)
                return ThinkState.Leaving;

            if (m_IsRetreating)
                return ThinkState.Retreating;

            if (Combatant != null)
                return ThinkState.Combat;

            return ThinkState.Idle;
        }

        private void ThinkIdle(DateTime now, bool isLeader)
        {
            if (!m_IsLeaving && Combatant == null && now > m_BoredTime && isLeader)
            {
                if (m_CachedRandom1 < 0.02)
                    InitiateTeamDeparture(now);
            }

            if (!m_IsRetreating)
            {
                TryHealSelf(now);
                TryHealAllies(now);
            }

            if (m_TeamId != 0 && isLeader && now >= m_NextActiveScan)
            {
                UnifiedTeamScan(now);
                CheckFallenAllies(now);
                m_NextActiveScan = now.AddSeconds(ActiveScanIntervalSeconds);
            }
            else
            {
                CheckEnvironment(now);
            }
        }

        private void ThinkCombat(DateTime now, bool isLeader)
        {
            if (!m_IsRetreating)
            {
                TryHealSelf(now);
                TryHealAllies(now);
            }

            UpdateCombatTactics(now);

            if (!m_IsRetreating)
                CheckRetreatCondition(now);

            if (m_TeamId != 0 && isLeader)
            {
                ApplyCombatantToNearbyMembers(now);

                if (now >= m_NextActiveScan)
                {
                    UnifiedTeamScan(now);
                    CheckFallenAllies(now);
                    m_NextActiveScan = now.AddSeconds(ActiveScanIntervalSeconds);
                }
            }
        }

        private void ThinkRetreating(DateTime now, bool isLeader)
        {
            TryHealSelf(now);

            if (m_TeamId != 0 && isLeader && now >= m_NextActiveScan)
            {
                UnifiedTeamScan(now);
                CheckFallenAllies(now);
                m_NextActiveScan = now.AddSeconds(ActiveScanIntervalSeconds);
            }
        }

        private void ThinkLeaving(DateTime now, bool isLeader)
        {
            // Leaving is handled through pending actions.
        }

        public override void OnThink()
        {
            base.OnThink();

            if (Deleted || Map == null || Map == Map.Internal)
                return;

            DateTime now = DateTime.UtcNow;
            bool isLeader = IsTeamLeaderAt(now);

            int throttle = (Combatant != null) ? CombatThinkThrottle : (isLeader ? LeaderIdleThinkThrottle : NonLeaderIdleThinkThrottle);
            if (throttle > 1 && (Serial.Value % throttle) != 0)
                return;

            if (!IsPrimaryThinker(now, isLeader))
                return;

            if (now >= m_NextRandomRefresh)
            {
                RefreshRandomCache();
                m_NextRandomRefresh = now.AddSeconds(2);
            }

            ProcessPendingActions(now);
            ProcessPendingHeal(now);

            if (m_TeamId != 0 && isLeader)
            {
                TeamInfo ti;
                if (TryGetTeamInfo(m_TeamId, out ti) && ti != null)
                {
                    if (Combatant != null && (now - ti.SharedScanTime).TotalSeconds >= CombatScanIntervalSeconds)
                        UnifiedTeamScan(now, ti);
                }
            }

            switch (GetThinkState())
            {
                case ThinkState.Leaving:
                    ThinkLeaving(now, isLeader);
                    break;

                case ThinkState.Retreating:
                    ThinkRetreating(now, isLeader);
                    break;

                case ThinkState.Combat:
                    ThinkCombat(now, isLeader);
                    break;

                default:
                    ThinkIdle(now, isLeader);
                    break;
            }

            if (now >= m_NextChatTime)
            {
                HandleChat(now);
                m_NextChatTime = now.AddSeconds(Utility.RandomMinMax(ChatIntervalMin, ChatIntervalMax));
            }
        }


        private void ProcessPendingHeal(DateTime now)
        {
            if (m_PendingHealTarget == null)
                return;

            if (now < m_PendingHealTime)
                return;

            Mobile target = m_PendingHealTarget;

            if (!target.Deleted && target.Alive)
            {
                target.Heal(m_PendingHealAmount, this);
                target.FixedParticles(0x376A, 9, 32, 5030, EffectLayer.Waist);
                target.PlaySound(m_PendingIsMagicHeal ? 0x202 : 0x57);
            }

            m_PendingHealTarget = null;
            m_IsUsingBandage = false;
        }

        private void ProcessPendingActions(DateTime now)
        {
            if (m_PendingCelebrate && now >= m_PendingCelebrateTime)
            {
                m_PendingCelebrate = false;

                if (!Deleted && Map != null)
                {
                    if (m_CachedRandom1 < 0.60)
                        Say(GetPooledMessage(VictoryLines, VictoryLinesLen));

                    if (IsTeamLeaderAt(now) && m_CachedRandom2 < 0.40)
                    {
                        m_PendingLoot = true;
                        m_PendingLootTime = now + LootingDelay;
                    }
                }
            }

            if (m_PendingLoot && now >= m_PendingLootTime)
            {
                m_PendingLoot = false;

                if (!Deleted && Map != null)
                    Say(GetPooledMessage(LootingLines, LootingLinesLen));
            }

            if (m_PendingMourn && now >= m_PendingMournTime)
            {
                m_PendingMourn = false;

                if (!Deleted && Map != null)
                    Say(GetPooledMessage(MourningLines, MourningLinesLen));
            }

            if (m_PendingRetreatReset && now >= m_PendingRetreatResetTime)
            {
                m_PendingRetreatReset = false;

                if (!Deleted)
                    m_IsRetreating = false;
            }

            if (m_PendingDeparture && now >= m_PendingDepartureTime)
            {
                m_PendingDeparture = false;

                if (!Deleted)
                    ExecuteTeamDeparture();
            }
        }

        private void HandleChat(DateTime now)
        {
            if (Deleted || Map == null)
                return;

            if (m_CachedRandom2 >= 0.38)
                return;

            string line;

            if (Combatant != null)
                line = GetPooledMessage(CombatYell, CombatYellLen);
            else
                line = m_IsEvil ? GetPooledMessage(EvilChat, EvilChatLen) : GetPooledMessage(FriendlyChat, FriendlyChatLen);

            if (CanSendMessage(now))
                PublicOverheadMessage(MessageType.Regular, m_IsEvil ? 0x22 : SpeechHue, true, line);
        }

        #endregion

        #region Combat

        private void UpdateCombatTactics(DateTime now)
        {
            if (Combatant == null || Deleted)
                return;

            // Inline combatant cache.
            Mobile c = Combatant;
            if (c != m_CachedCombatant)
            {
                m_CachedCombatant = c;
                m_CachedCombatantDistance = (c != null) ? (int)GetDistanceToSqrt(c) : 999;
            }

            if (m_CachedCombatantDistance > 12)
                return;

            if (m_CombatRound < 3 && Hits > HitsMax * 0.60)
            {
                m_CombatRound++;
                return;
            }

            m_CombatRound++;

            if (now > m_BoredTime)
                m_BoredTime = now.AddMinutes(Utility.RandomMinMax(20, 40));

            if (now - m_LastTacticalShout < TimeSpan.FromSeconds(8))
                return;

            int enemyCount = m_CachedEnemyCount;
            int injuredAllies = m_CachedInjuredAllies;

            if (m_TeamId != 0)
            {
                TeamInfo ti;
                if (TryGetTeamInfo(m_TeamId, out ti) && ti != null)
                {
                    if (IsTeamLeaderAt(now) && (now - ti.SharedScanTime).TotalSeconds >= CombatScanIntervalSeconds)
                        UnifiedTeamScan(now, ti);

                    if ((now - ti.SharedScanTime).TotalSeconds < (CombatScanIntervalSeconds * 2.0))
                    {
                        enemyCount = ti.SharedEnemyCount;
                        injuredAllies = ti.SharedInjuredAllies;
                        m_CachedEnemyCount = enemyCount;
                        m_CachedInjuredAllies = injuredAllies;
                    }
                }
            }
            else
            {
                if ((now - m_LastCombatScan).TotalSeconds >= CombatScanIntervalSeconds)
                {
                    m_LastCombatScan = now;
                    m_CachedEnemyCount = 0;
                    m_CachedInjuredAllies = 0;

                    IPooledEnumerable eable = Map.GetMobilesInRange(Location, 8);
                    try
                    {
                        foreach (Mobile m in eable)
                        {
                            if (m == null || m == this || m.Deleted || !m.Alive)
                                continue;

                            Mobile mc = m.Combatant;
                            if (mc != null)
                            {
                                AdventurerTeam atCombatant = mc as AdventurerTeam;
                                if (mc == this || (atCombatant != null && atCombatant.m_TeamId == m_TeamId))
                                    m_CachedEnemyCount++;
                            }

                            AdventurerTeam ally = m as AdventurerTeam;
                            if (ally != null && ally != this && ally.m_TeamId == m_TeamId)
                            {
                                if (ally.Hits < ally.HitsMax * 0.50)
                                    m_CachedInjuredAllies++;
                            }
                        }
                    }
                    finally
                    {
                        if (eable != null)
                            eable.Free();
                    }

                    enemyCount = m_CachedEnemyCount;
                    injuredAllies = m_CachedInjuredAllies;
                }
            }

            if (Hits < HitsMax * CriticalHealthThreshold && !m_HasWarnedCriticalHealth)
            {
                m_HasWarnedCriticalHealth = true;

                if (m_CachedRandom1 < 0.70 && CanSendMessage(now))
                {
                    Say(GetPooledMessage(CriticalHealthWarnings, CriticalHealthWarnings.Length));
                    m_LastTacticalShout = now;

                    if (m_CitizenType == (int)CitizenClass.Fighter && m_CachedRandom2 < 0.40)
                        Say("But I won't give up!");
                }

                return;
            }

            if (Hits < HitsMax * LowHealthThreshold && !m_HasWarnedLowHealth)
            {
                m_HasWarnedLowHealth = true;

                if (m_CachedRandom1 < 0.55 && CanSendMessage(now))
                {
                    Say(GetPooledMessage(LowHealthWarnings, LowHealthWarnings.Length));
                    m_LastTacticalShout = now;

                    List<AdventurerTeam> nearby = GetSharedNearbyMembers(now);
                    if (nearby.Count > 0 && m_CachedRandom2 < 0.30)
                    {
                        AdventurerTeam responder = nearby[Utility.Random(nearby.Count)];
                        if (responder != null && !responder.Deleted)
                            responder.Say("Hold on! I'll help!");
                    }
                }

                return;
            }

            if (enemyCount >= 3 && m_CombatRound > 2)
            {
                if (m_CachedRandom2 < 0.45 && CanSendMessage(now))
                {
                    Say(GetPooledMessage(EnemyCountReactions, EnemyCountReactions.Length));
                    m_LastTacticalShout = now;

                    if (IsTeamLeaderAt(now) && m_CachedRandom1 < 0.50)
                        Say(GetPooledMessage(LeaderOrders, LeaderOrdersLen));
                }

                return;
            }

            if (injuredAllies > 0 && IsTeamLeaderAt(now) && m_CombatRound > 3)
            {
                if (m_CachedRandom1 < 0.35 && CanSendMessage(now))
                {
                    Say(GetPooledMessage(AllyDownReactions, AllyDownReactions.Length));
                    m_LastTacticalShout = now;
                }

                return;
            }

            if (Hits > HitsMax * LowHealthThreshold && (m_CombatRound % 5) == 0 && m_CachedRandom1 < 0.30)
            {
                string[] tactics = null;

                switch (m_CitizenType)
                {
                    case (int)CitizenClass.Wizard: tactics = WizardCombatTactics; break;
                    case (int)CitizenClass.Fighter: tactics = FighterCombatTactics; break;
                    case (int)CitizenClass.Rogue: tactics = RogueCombatTactics; break;
                }

                if (tactics != null && tactics.Length > 0)
                {
                    Say(GetPooledMessage(tactics, tactics.Length));
                    m_LastTacticalShout = now;
                }
            }
        }


        public override void OnCombatantChange()
        {
            base.OnCombatantChange();

            DateTime now = DateTime.UtcNow;

            if (Combatant == null)
            {
                if (!m_CelebrationDone && m_LastCombatTime != DateTime.MinValue &&
                    now - m_LastCombatTime < TimeSpan.FromSeconds(10))
                {
                    m_PendingCelebrate = true;
                    m_PendingCelebrateTime = now + CelebrateDelay;
                    m_CelebrationDone = true;
                }

                m_CombatRound = 0;
                m_HasWarnedLowHealth = false;
                m_HasWarnedCriticalHealth = false;
            }
            else
            {
                m_LastCombatTime = now;
                m_CelebrationDone = false;

                m_CombatRound = 0;
                m_HasWarnedLowHealth = false;
                m_HasWarnedCriticalHealth = false;
            }
        }

        private void CheckRetreatCondition(DateTime now)
        {
            if (m_IsRetreating || Deleted || Combatant == null)
                return;

            if (Hits < HitsMax * RetreatThreshold && m_CitizenType != (int)CitizenClass.Fighter)
            {
                if (m_CachedRandom1 < 0.35)
                {
                    m_IsRetreating = true;

                    if (CanSendMessage(now))
                        Say(RetreatLines[Utility.Random(RetreatLinesLen)]);

                    Combatant = null;

                    m_PendingRetreatReset = true;
                    m_PendingRetreatResetTime = now + RetreatResetDelay;
                }
            }
        }

        #endregion

        #region Team Coordination / Environment

        private void CoordinateTeam(DateTime now)
        {
            if (m_TeamId == 0 || Map == null)
                return;

            if (IsTeamLeaderAt(now))
            {
                if (now >= m_NextActiveScan)
                {
                    UnifiedTeamScan(now);
                    m_NextActiveScan = now.AddSeconds(ActiveScanIntervalSeconds);
                }

                ApplyCombatantToNearbyMembers(now);
            }
        }

        // Performs a single scan that populates team member cache and combat pressure metrics.
        // Intended to be called by the team leader only.
        private void UnifiedTeamScan(DateTime now)
        {
            TeamInfo ti;
            if (!TryGetTeamInfo(m_TeamId, out ti) || ti == null)
                return;

            UnifiedTeamScan(now, ti);
        }

        private void UnifiedTeamScan(DateTime now, TeamInfo ti)
        {
            if (Map == null)
                return;

            ti.SharedScanTime = now;
            ti.SharedEnemyCount = 0;
            ti.SharedInjuredAllies = 0;
            ti.SharedNearbyMembers.Clear();

            IPooledEnumerable eable = Map.GetMobilesInRange(Location, TeamMemberRange);
            try
            {
                foreach (Mobile m in eable)
                {
                    if (m == null || m == this || m.Deleted || !m.Alive)
                        continue;

                    AdventurerTeam at = m as AdventurerTeam;
                    if (at != null && at.m_TeamId == m_TeamId)
                    {
                        if (ti.SharedNearbyMembers.Count < MaxCachedNearbyMembers)
                            ti.SharedNearbyMembers.Add(at);

                        if (at.Hits < at.HitsMax * 0.50)
                            ti.SharedInjuredAllies++;

                        continue;
                    }

                    Mobile mc = m.Combatant;
                    if (mc != null)
                    {
                        AdventurerTeam atCombatant = mc as AdventurerTeam;
                        if (atCombatant != null && atCombatant.m_TeamId == m_TeamId)
                            ti.SharedEnemyCount++;
                    }

                    if (ti.SharedEnemyCount >= 8 && ti.SharedInjuredAllies >= 4)
                        break;
                }
            }
            finally
            {
                if (eable != null)
                    eable.Free();
            }
        }

        private void ApplyCombatantToNearbyMembers(DateTime now)
        {
            if (Combatant == null)
                return;

            List<AdventurerTeam> nearby = GetSharedNearbyMembers(now);
            if (nearby == null || nearby.Count == 0)
                return;

            for (int i = 0; i < nearby.Count; i++)
            {
                AdventurerTeam at = nearby[i];

                if (at == null || at.Deleted)
                    continue;

                if (at.Combatant == null && !at.m_IsRetreating && at.InRange(this, 10))
                    at.Combatant = Combatant;
            }
        }


        private void CheckFallenAllies(DateTime now)
        {
            List<AdventurerTeam> nearby = GetSharedNearbyMembers(now);

            if (nearby.Count == 0)
                return;

            for (int i = 0; i < nearby.Count; i++)
            {
                AdventurerTeam at = nearby[i];

                if (at != null && !at.Deleted && !at.Alive && !at.m_DeathAnnounced)
                {
                    at.m_DeathAnnounced = true;

                    if (m_CachedRandom1 < 0.50)
                    {
                        m_PendingMourn = true;
                        m_PendingMournTime = now + MourningDelay;
                    }
                }
            }
        }

        private void CheckEnvironment(DateTime now)
        {
            if (Map == null || Combatant != null)
                return;

            if (now < m_NextEnvironmentCheck)
                return;

            m_NextEnvironmentCheck = now.AddSeconds(Utility.RandomMinMax(EnvironmentScanMinSeconds, EnvironmentScanMaxSeconds));

            if (m_CachedRandom1 > 0.30)
                return;

            if (now - m_LastCombatTime < TimeSpan.FromSeconds(90))
                return;

            bool foundCorpse = false;

            IPooledEnumerable eable = Map.GetItemsInRange(Location, 8);

            try
            {
                foreach (Item item in eable)
                {
                    if (item is Corpse)
                    {
                        foundCorpse = true;
                        break;
                    }
                }
            }
            finally
            {
                if (eable != null)
                    eable.Free();
            }

            if (foundCorpse)
            {
                Say(GetPooledMessage(CorpseComments, CorpseCommentsLen));
                return;
            }

            if (Hits < HitsMax * 0.60)
            {
                Say(GetPooledMessage(InjuredComments, InjuredCommentsLen));
                return;
            }

            if (m_CachedRandom2 < 0.15)
                Say(GetPooledMessage(IdleComments, IdleCommentsLen));
        }

        #endregion

        #region Healing

        private void TryHealSelf(DateTime now)
        {
            if (m_IsUsingBandage || now < m_NextSelfHeal)
                return;

            if (Hits >= HitsMax * 0.70)
                return;

            double hpPercent = (double)Hits / HitsMax;

            if (hpPercent < 0.35 && m_CachedRandom1 < 0.60)
            {
                if (TryUsePotionHealing(now))
                    return;
            }

            if (m_CitizenType == (int)CitizenClass.Wizard && m_CachedRandom1 < 0.50)
            {
                if (TryUseMagicHealing(this, now))
                    return;
            }

            if ((m_CitizenType == (int)CitizenClass.Fighter || m_CitizenType == (int)CitizenClass.Rogue) &&
                hpPercent > 0.35 && m_CachedRandom1 < 0.50)
            {
                if (TryUseBandageHealing(this, now))
                    return;
            }

            if (hpPercent < 0.50 && m_CachedRandom1 < 0.30)
                TryUsePotionHealing(now);
        }

        private void TryHealAllies(DateTime now)
        {
            if (m_CitizenType != (int)CitizenClass.Wizard)
                return;


            // Requires an up-to-date nearby member cache (built by the leader).
            List<AdventurerTeam> nearby = GetSharedNearbyMembers(now);
            if (nearby.Count == 0)
                return;
            if (now < m_NextAllyHeal)
                return;

            if (Combatant != null && Hits < HitsMax * 0.50)
                return;

            AdventurerTeam mostInjured = null;
            double lowestHpPercent = HealAllyThreshold;

            for (int i = 0; i < nearby.Count; i++)
            {
                AdventurerTeam ally = nearby[i];

                if (ally == null || ally.Deleted || !ally.Alive)
                    continue;

                if (!InRange(ally.Location, 8))
                    continue;

                if (!InLOS(ally))
                    continue;

                double allyHpPercent = (double)ally.Hits / ally.HitsMax;

                if (allyHpPercent < lowestHpPercent)
                {
                    mostInjured = ally;
                    lowestHpPercent = allyHpPercent;
                }
            }

            if (mostInjured != null && m_CachedRandom2 < 0.40)
            {
                if (TryUseMagicHealing(mostInjured, now))
                    m_NextAllyHeal = now.AddSeconds(8);
            }
        }

        private bool TryUsePotionHealing(DateTime now)
        {
            BaseHealPotion potion = FindPotionInBackpack();

            if (potion != null)
            {
                potion.Drink(this);
                PublicOverheadMessage(MessageType.Emote, 0x3B2, true, GetPooledMessage(PotionLines, PotionLinesLen));
                m_NextSelfHeal = now.AddSeconds(10);
                return true;
            }

            if (m_CachedRandom1 < 0.15 && CanSendMessage(now))
                Say(GetPooledMessage(OutOfSuppliesLines, OutOfSuppliesLinesLen));

            return false;
        }

        private bool TryUseBandageHealing(Mobile target, DateTime now)
        {
            if (Backpack == null)
                return false;

            if (m_IsUsingBandage)
                return false;

            Bandage bandage = Backpack.FindItemByType(typeof(Bandage)) as Bandage;

            if (bandage == null || bandage.Amount <= 0)
            {
                if (m_CachedRandom1 < 0.10 && CanSendMessage(now))
                    Say(GetPooledMessage(OutOfSuppliesLines, OutOfSuppliesLinesLen));

                return false;
            }

            m_IsUsingBandage = true;

            try
            {
                bandage.Consume(1);

                PublicOverheadMessage(MessageType.Emote, 0x3B2, true, GetPooledMessage(BandageLines, BandageLinesLen));
                PlaySound(0x57);

                double healingSkill = Skills[SkillName.Healing].Value;
                double anatomySkill = Skills[SkillName.Anatomy].Value;

                int minHeal = (int)(healingSkill * 0.2) + 5;
                int maxHeal = (int)(healingSkill * 0.4) + 10;
                double anatomyBonus = 1.0 + (anatomySkill / 500.0);

                int healAmount = (int)(Utility.RandomMinMax(minHeal, maxHeal) * anatomyBonus);

                m_PendingHealTime = now.AddSeconds(4.0);
                m_PendingHealTarget = target;
                m_PendingHealAmount = healAmount;
                m_PendingIsMagicHeal = false;

                m_NextSelfHeal = now.AddSeconds(10);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[AdventurerTeam] Bandage healing failed: " + ex.Message);
                m_IsUsingBandage = false;
                return false;
            }
        }

        private bool TryUseMagicHealing(Mobile target, DateTime now)
        {
            if (m_CitizenType != (int)CitizenClass.Wizard)
                return false;

            if (Mana < 4)
                return false;

            try
            {
                int healAmount;
                int manaCost;

                if (Mana >= 11 && m_CitizenLevel >= 5)
                {
                    healAmount = Utility.RandomMinMax(25, 35);
                    manaCost = 11;
                }
                else if (Mana >= 6)
                {
                    healAmount = Utility.RandomMinMax(15, 25);
                    manaCost = 6;
                }
                else
                {
                    healAmount = Utility.RandomMinMax(10, 18);
                    manaCost = 4;
                }

                if (Mana < manaCost)
                    return false;

                Mana -= manaCost;

                if (target != this)
                    Direction = GetDirectionTo(target);

                Animate(17, 7, 1, true, false, 0);
                PlaySound(0x1F2);

                m_PendingHealTime = now.AddSeconds(0.8);
                m_PendingHealTarget = target;
                m_PendingHealAmount = healAmount;
                m_PendingIsMagicHeal = true;

                if (target == this)
                {
                    PublicOverheadMessage(MessageType.Emote, 0x3B2, true, "*channels healing magic*");
                    m_NextSelfHeal = now.AddSeconds(8);
                }
                else
                {
                    PublicOverheadMessage(MessageType.Emote, 0x3B2, true, GetPooledMessage(HealSpellLines, HealSpellLinesLen));
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[AdventurerTeam] Magic healing failed: " + ex.Message);
                return false;
            }
        }

        private BaseHealPotion FindPotionInBackpack()
        {
            if (Backpack == null)
                return null;

            Item ghp = Backpack.FindItemByType(typeof(GreaterHealPotion));
            if (ghp is BaseHealPotion)
                return (BaseHealPotion)ghp;

            Item hp = Backpack.FindItemByType(typeof(HealPotion));
            if (hp is BaseHealPotion)
                return (BaseHealPotion)hp;

            Item lhp = Backpack.FindItemByType(typeof(LesserHealPotion));
            if (lhp is BaseHealPotion)
                return (BaseHealPotion)lhp;

            return null;
        }

        #endregion

        #region Departure

        private void InitiateTeamDeparture(DateTime now)
        {
            if (m_IsLeaving)
                return;

            m_IsLeaving = true;

            if (CanSendMessage(now))
                Say(DepartureLines[Utility.Random(DepartureLinesLen)]);

            m_PendingDeparture = true;
            m_PendingDepartureTime = now + DepartureDelay;
        }

        private void ExecuteTeamDeparture()
        {
            if (m_TeamId == 0)
                return;

            int teamId = m_TeamId;
            List<AdventurerTeam> toDelete = new List<AdventurerTeam>(8);

            lock (AllTeamsLock)
            {
                TeamInfo ti;

                if (AllTeams.TryGetValue(teamId, out ti))
                {
                    lock (ti.Members)
                    {
                        toDelete.AddRange(ti.Members);
                        ti.Members.Clear();
                    }

                    AllTeams.Remove(teamId);
                }
            }

            AutoTeamMaintainer.RecycleTeamId(teamId);

            for (int i = 0; i < toDelete.Count; i++)
            {
                AdventurerTeam member = toDelete[i];

                if (member != null && !member.Deleted)
                {
                    member.m_TeamId = 0;
                    member.Delete();
                }
            }
        }

        #endregion

        #region Player Interaction

        public override void OnMovement(Mobile m, Point3D oldLocation)
        {
            if (m_IsLeaving || Deleted || m == null || m.Map != Map)
                return;

            DateTime now = DateTime.UtcNow;

            if (now - m_LastMovementCheck < MovementThrottle)
                return;

            PlayerMobile pm = m as PlayerMobile;

            if (pm != null && InRange(m, 12) && !InRange(oldLocation, 12) && CanSee(m))
            {
                m_LastMovementCheck = now;

                if (now - m_LastGreetTime < GreetCooldown)
                    return;

                if (Combatant != null)
                {
                    if (!m_IsEvil && pm.Karma >= 0 && m_CachedRandom1 < 0.30)
                    {
                        Say("Help us! We're under attack!");
                        m_LastGreetTime = now;
                    }
                    return;
                }

                if (Hits < HitsMax * 0.40)
                {
                    if (!m_IsEvil && m_CachedRandom2 < 0.25)
                    {
                        Say("Do you have any healing potions to spare?");
                        m_LastGreetTime = now;
                    }
                    return;
                }

                if (m_CachedRandom2 < 0.45)
                {
                    m_LastGreetTime = now;

                    if (m_IsEvil)
                    {
                        if (pm.Karma >= 0)
                        {
                            switch (Utility.Random(5))
                            {
                                case 0: Say("Well, well... a virtuous fool wanders into the wolf's den."); break;
                                case 1: Say("*grins wickedly* Such noble bearing... shame to dirty it with blood."); break;
                                case 2: Say("A hero? How... disappointing. They die just like everyone else."); break;
                                case 3: Say("Your kind isn't welcome here. Leave while you still can."); break;
                                case 4: Say("*narrows eyes* I know your type. Self-righteous fool."); break;
                            }
                        }
                        else if (pm.Karma < 0)
                        {
                            switch (Utility.Random(4))
                            {
                                case 0: Say("Ah, a kindred spirit. These lands are ripe for plunder."); break;
                                case 1: Say("Another reaver... good. Strength in numbers."); break;
                                case 2: Say("Your reputation precedes you. Care to join our hunt?"); break;
                                case 3: Say("A fellow outlaw! The guards chase us both, eh?"); break;
                            }
                        }
                        else
                        {
                            switch (Utility.Random(4))
                            {
                                case 0: Say("Neutral ground means nothing here. Choose a side or become prey."); break;
                                case 1: Say("Wanderer... your neutrality won't protect you."); break;
                                case 2: Say("Smart ones pay tribute. Foolish ones... don't."); break;
                                case 3: Say("Gray reputation? Playing both sides, are we?"); break;
                            }
                        }
                    }
                    else
                    {
                        if (pm.Karma < 0)
                        {
                            switch (Utility.Random(4))
                            {
                                case 0: Say("A murderer! Guards should know of this!"); break;
                                case 1: Say("*steps back cautiously* I know what you are..."); break;
                                case 2: Say("The darkness clings to you like a shroud. Begone!"); break;
                                case 3: Say("*grips weapon* Stay back, red hand!"); break;
                            }
                        }
                        else
                        {
                            switch (Utility.Random(6))
                            {
                                case 0: Say("Hail, fellow traveler! These paths grow more perilous each day."); break;
                                case 1: Say("Well met! Safety in numbers, they say."); break;
                                case 2: Say("Greetings! Seeking fortune in these forsaken ruins?"); break;
                                case 3: Say("Another brave soul! The darkness fears our light."); break;
                                case 4: Say("Ho there! Watch your step - danger lurks everywhere."); break;
                                case 5: Say("Thank the gods! Another friendly face in this cursed place."); break;
                            }
                        }
                    }
                }
            }
        }

        public override void OnDamage(int amount, Mobile from, bool willKill)
        {
            PlayerMobile pm = from as PlayerMobile;

            if (pm != null && from.AccessLevel == AccessLevel.Player)
                m_LastSeen = DateTime.UtcNow;

            base.OnDamage(amount, from, willKill);
        }

        #endregion

        #region Loot / Serialization

        public override void GenerateLoot()
        {
            switch (m_CitizenLevel)
            {
                case 9:
                    AddLoot(LootPack.FilthyRich);
                    goto case 7;
                case 8:
                case 7:
                    AddLoot(LootPack.Rich);
                    goto case 5;
                case 6:
                case 5:
                    AddLoot(LootPack.Average);
                    goto case 3;
                case 4:
                case 3:
                    AddLoot(LootPack.Meager);
                    break;
                default:
                    AddLoot(LootPack.Meager);
                    break;
            }

            if (Utility.Random(25) == 0)
            {
                if (Loot.AdventurerRareItemTypes != null && Loot.AdventurerRareItemTypes.Length > 0)
                {
                    Type t = Loot.AdventurerRareItemTypes[Utility.Random(Loot.AdventurerRareItemTypes.Length)];
                    Item rare = Loot.Construct(t);

                    if (rare != null)
                        PackItem(rare);
                }
            }

            if (m_CitizenType == (int)CitizenClass.Wizard)
                AddLoot(LootPack.MedScrolls, (m_CitizenLevel / 3) + 1);
        }

        public override void OnDelete()
        {
            RemoveFromTeam(m_TeamId);
            base.OnDelete();
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

			// NO PERSISTENCE MODE - delete ALL old NPCs on restart
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

        private static readonly Dictionary<int, bool> s_UsedTeamIds = new Dictionary<int, bool>();
        private static readonly Queue<int> s_RecycledIds = new Queue<int>();
        private static int s_NextTeamId = 1;
        private static readonly object s_IdLock = new object();

        private const int MaxConcurrentTeams = 12;
        private const int TeamSize = 4;
        private const int PlayerGroupRadius = 30;
        private const int MinSpawnDist = 18;
        private const int MaxSpawnDist = 25;

        public static void Initialize()
        {
            if (s_MaintenanceTimer != null)
                s_MaintenanceTimer.Stop();

            s_MaintenanceTimer = Timer.DelayCall(
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(2),
                new TimerCallback(MaintainTeams));

            Console.WriteLine("[AdventurerTeam] AutoTeamMaintainer initialized");
        }

        [CommandProperty(AccessLevel.Administrator)]
        public static bool Enabled
        {
            get { return s_Enabled; }
            set
            {
                s_Enabled = value;

                if (value)
                    Initialize();
                else
                {
                    if (s_MaintenanceTimer != null)
                    {
                        s_MaintenanceTimer.Stop();
                        s_MaintenanceTimer = null;
                    }
                }
            }
        }

        public static int GetNewTeamId()
        {
            lock (s_IdLock)
            {
                int id;

                if (s_RecycledIds.Count > 0)
                    id = s_RecycledIds.Dequeue();
                else
                    id = s_NextTeamId++;

                s_UsedTeamIds[id] = true;
                return id;
            }
        }

        public static void RecycleTeamId(int id)
        {
            lock (s_IdLock)
            {
                if (s_UsedTeamIds.ContainsKey(id))
                {
                    s_UsedTeamIds.Remove(id);
                    s_RecycledIds.Enqueue(id);
                }
            }
        }

        private static void MaintainTeams()
        {
            if (!s_Enabled)
                return;

            try
            {
                int activeTeams;

                lock (AdventurerTeam.AllTeamsLock)
                {
                    activeTeams = AdventurerTeam.AllTeams.Count;
                }

                if (activeTeams >= MaxConcurrentTeams)
                    return;

                List<PlayerMobile> activePlayers = new List<PlayerMobile>();

                foreach (NetState state in NetState.Instances)
                {
                    Mobile m = state.Mobile;

                    if (!(m is PlayerMobile))
                        continue;

                    if (m.AccessLevel != AccessLevel.Player)
                        continue;

                    if (!m.Alive || m.Map == null || m.Map == Map.Internal)
                        continue;

                    activePlayers.Add((PlayerMobile)m);

                    if (activePlayers.Count >= 10)
                        break;
                }

                if (activePlayers.Count == 0)
                    return;

                int playersToCheck = Math.Min(6, activePlayers.Count);

                for (int i = 0; i < playersToCheck; i++)
                {
                    int idx = Utility.Random(activePlayers.Count);
                    PlayerMobile pm = activePlayers[idx];
                    activePlayers.RemoveAt(idx);

                    lock (AdventurerTeam.AllTeamsLock)
                    {
                        activeTeams = AdventurerTeam.AllTeams.Count;
                    }

                    if (activeTeams >= MaxConcurrentTeams)
                        break;

                    TrySpawnTeamForPlayer(pm);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[AutoTeamMaintainer] Error: " + ex.Message);
            }
        }

        private static void TrySpawnTeamForPlayer(PlayerMobile pm)
        {
            if (pm == null || pm.Deleted || pm.Map == null)
                return;

            int nearbyAdventurers = 0;

            IPooledEnumerable eable = pm.Map.GetMobilesInRange(pm.Location, PlayerGroupRadius);

            try
            {
                foreach (Mobile m in eable)
                {
                    if (m is AdventurerTeam)
                    {
                        nearbyAdventurers++;

                        if (nearbyAdventurers >= TeamSize)
                            break;
                    }
                }
            }
            finally
            {
                if (eable != null)
                    eable.Free();
            }

            if (nearbyAdventurers >= TeamSize)
                return;

            Point3D spawnLoc = FindSpawnLocation(pm);

            if (spawnLoc == Point3D.Zero)
                return;

            int teamId = GetNewTeamId();
            bool isEvil = Utility.RandomBool();

            for (int i = 0; i < TeamSize; i++)
            {
                AdventurerTeam npc = new AdventurerTeam(teamId, isEvil);
                npc.MoveToWorld(spawnLoc, pm.Map);
            }
        }

        private static Point3D FindSpawnLocation(Mobile nearPlayer)
        {
            Map map = nearPlayer.Map;
            Point3D center = nearPlayer.Location;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                int dist = Utility.RandomMinMax(MinSpawnDist, MaxSpawnDist);
                int angle = Utility.Random(360);

                double radians = angle * Math.PI / 180.0;
                int xOffset = (int)(Math.Cos(radians) * dist);
                int yOffset = (int)(Math.Sin(radians) * dist);

                Point3D testLoc = new Point3D(center.X + xOffset, center.Y + yOffset, center.Z);

                if (!map.CanSpawnMobile(testLoc))
                    continue;

                if (IsInForbiddenRegion(testLoc, map))
                    continue;

                return testLoc;
            }

            return Point3D.Zero;
        }

        private static bool IsInForbiddenRegion(Point3D loc, Map map)
        {
            Region reg = Region.Find(loc, map);

            if (reg == null)
                return false;

            return (reg is WantedRegion ||
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
                    reg is MoonCore);
        }
    }
}
