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
        #region Dialogue Data (Static)
        private static readonly string[] FriendlyChat = new string[]
        {
            "The wyrms in the deep caves grow bolder each day...",
            "I barely escaped from a pack of dire wolves yesterday.",
            "These lands are cursed, I tell you.",
            "I seek a legendary blade, lost to time.",
            "Careful where you tread - traps abound.",
            "Running low on supplies... need to restock soon.",
            "Red-cloaked murderers were spotted near the crossroads!"
        };

        private static readonly string[] EvilChat = new string[]
        {
            "Your coin or your life, fool.",
            "Fresh meat for the crows...",
            "The weak exist only to serve the strong.",
            "I smell fear... and gold.",
            "Turn back while you still draw breath.",
            "Gold talks. Mercy doesn't."
        };

        private static readonly string[] CombatYell = new string[]
        {
            "Surround them!", "Focus on the spell-caster!",
            "Shield wall, hold formation!", "Flank them!",
            "Healer down! Protect them!", "Hold the line!",
            "For glory!", "Fight or die!"
        };

        private static readonly string[] LowHealthWarnings = new string[]
        {
            "I'm hurt badly!", "Need help here!", "Wounds are serious!", "Getting weak..."
        };

        private static readonly string[] CriticalHealthWarnings = new string[]
        {
            "I'm going down!", "Near death here!", "Someone help!", "Vision fading..."
        };

        private static readonly string[] VictoryLines = new string[]
        {
            "That was close! Everyone alright?", "Good fight! Check the body for coin.",
            "Another one bites the dust.", "Victory! But stay alert.",
            "Well fought, friends!"
        };

        private static readonly string[] LootingLines = new string[]
        {
            "*searches the corpse*", "This should fetch a good price!",
            "Split the gold evenly, friends.", "I'll carry this."
        };

        private static readonly string[] RetreatLines = new string[]
        {
            "Fall back! I'm badly wounded!", "Retreating! Cover me!",
            "Not dying here today!", "Tactical withdrawal!"
        };

        private static readonly string[] MourningLines = new string[]
        {
            "No! They got him!", "Man down! Avenge our fallen!",
            "We'll honor their memory!", "Fight harder!"
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

        private static readonly string[] OutOfSuppliesLines = new string[]
        {
            "I'm out of supplies!", "No more potions!", "I need bandages!"
        };

        private static readonly string[] DepartureLines = new string[]
        {
            "Time to move on.", "Nothing left here.", "Let's go, team."
        };

        // Pre-cached lengths
        private static readonly int FriendlyChatLen = FriendlyChat.Length;
        private static readonly int EvilChatLen = EvilChat.Length;
        private static readonly int CombatYellLen = CombatYell.Length;
        private static readonly int VictoryLinesLen = VictoryLines.Length;
        private static readonly int LootingLinesLen = LootingLines.Length;
        private static readonly int RetreatLinesLen = RetreatLines.Length;
        private static readonly int MourningLinesLen = MourningLines.Length;
        private static readonly int LowHealthWarningsLen = LowHealthWarnings.Length;
        private static readonly int CriticalHealthWarningsLen = CriticalHealthWarnings.Length;
        private static readonly int PotionLinesLen = PotionLines.Length;
        private static readonly int BandageLinesLen = BandageLines.Length;
        private static readonly int HealSpellLinesLen = HealSpellLines.Length;
        private static readonly int OutOfSuppliesLinesLen = OutOfSuppliesLines.Length;
        private static readonly int DepartureLinesLen = DepartureLines.Length;
        #endregion

        #region Shared Team State
        internal class TeamInfo
        {
            public readonly List<AdventurerTeam> Members = new List<AdventurerTeam>(8);
            public AdventurerTeam Leader;

            public DateTime SharedScanTime = DateTime.MinValue;
            public int SharedEnemyCount = 0;
            public int SharedInjuredAllies = 0;
            public readonly List<AdventurerTeam> SharedNearbyMembers = new List<AdventurerTeam>(MaxCachedNearbyMembers);
        }

        internal static readonly Dictionary<int, TeamInfo> AllTeams = new Dictionary<int, TeamInfo>(512);
        internal static readonly object AllTeamsLock = new object();
        private static readonly object[] TeamLocks = new object[4];
        #endregion

        #region Configuration & Constants
        private static readonly TimeSpan HalfSecond = TimeSpan.FromSeconds(0.5);
        private static readonly long HalfSecondTicks = TimeSpan.FromSeconds(0.5).Ticks;
        private static readonly TimeSpan TwoSeconds = TimeSpan.FromSeconds(2.0);
        private static readonly TimeSpan EightSeconds = TimeSpan.FromSeconds(8.0);
        private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10.0);
        private static readonly TimeSpan GreetCooldown = TimeSpan.FromSeconds(45.0);
        private static readonly TimeSpan MovementThrottle = TimeSpan.FromSeconds(5.0);

        private const int MaxCachedNearbyMembers = 8;
        private const double CombatScanIntervalSeconds = 8.0;
        private const double TeamScanIntervalSeconds = 18.0;
        
        private const double CriticalHealthThreshold = 0.30;
        private const double LowHealthThreshold = 0.50;
        private const double RetreatThreshold = 0.25;
        private const double HealAllyThreshold = 0.60;
        private const int TeamMemberRange = 12;

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
        private DateTime m_BoredTime;
        
        private DateTime m_LastCombatTime;
        private bool m_CelebrationDone;

        private readonly List<AdventurerTeam> m_CachedNearbyMembers = new List<AdventurerTeam>(10);
        private Mobile m_CachedCombatant;
        private int m_CachedCombatantDistSq;

        private bool m_IsRetreating;
        private bool m_DeathAnnounced;
        private bool m_IsLeaving;
        private bool m_HasWarnedLowHealth;
        private bool m_HasWarnedCriticalHealth;

        private DateTime m_NextSelfHeal;
        private DateTime m_NextAllyHeal;
        private bool m_IsUsingBandage;
        
        private DateTime m_PendingHealTime;
        private Mobile m_PendingHealTarget;
        private int m_PendingHealAmount;
        private bool m_PendingIsMagicHeal;

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

        private double m_CachedRandom1;
        private double m_CachedRandom2;
        private DateTime m_NextRandomRefresh;

        private DateTime m_LastMessageTime;
        private const int MessagePoolSize = 8;
        private readonly string[] m_MessagePool = new string[MessagePoolSize];
        private int m_MessagePoolIndex;

        private bool m_CachedIsLeader;
        private DateTime m_NextLeaderCheck;
        #endregion

        #region Static Constructor
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
        public bool IsEvil
        {
            get { return m_IsEvil; }
            set { m_IsEvil = value; }
        }

        public override bool AlwaysMurderer { get { return m_IsEvil; } }

        private bool IsTeamLeaderAt(DateTime now)
        {
            if (now >= m_NextLeaderCheck)
            {
                m_NextLeaderCheck = now + TenSeconds;
                m_CachedIsLeader = CheckIfLeaderInternal();
            }
            return m_CachedIsLeader;
        }
        #endregion

        #region Constructors & Setup
        [Constructable]
        public AdventurerTeam() : this(0, false) { }

        [Constructable]
        public AdventurerTeam(int teamId, bool isEvil) : base(AIType.AI_Melee, FightMode.None, 10, 1, 0.2, 0.4)
        {
            m_TeamId = teamId;
            m_IsEvil = isEvil;
            m_SpawnedBySystem = (teamId != 0);

            InitStatsAndAppearance();
            AddToTeam(teamId);

            DateTime now = DateTime.UtcNow;
            m_LastSeen = now;
            m_NextActiveScan = now.AddSeconds(15);
            m_NextChatTime = now.AddSeconds(Utility.RandomMinMax(3, 15));
            m_BoredTime = now.AddMinutes(Utility.RandomMinMax(15, 30));
            m_NextRandomRefresh = now + TwoSeconds;
            m_CachedCombatantDistSq = 9999;
            RefreshRandomCache();
        }

        public AdventurerTeam(Serial serial) : base(serial) { }
        #endregion

        #region Team Management (Thread-Safe)
        private object GetTeamLock()
        {
            if (m_TeamId == 0) return TeamLocks[0];
            int index = (m_TeamId < 0 ? -m_TeamId : m_TeamId) % TeamLocks.Length;
            return TeamLocks[index];
        }

        private bool CheckIfLeaderInternal()
        {
            if (m_TeamId == 0) return false;
            object teamLock = GetTeamLock();
            lock (teamLock)
            {
                TeamInfo ti;
                if (!AllTeams.TryGetValue(m_TeamId, out ti)) return false;
                return (ti.Leader != null && ti.Leader.Serial == Serial);
            }
        }

        private void AddToTeam(int teamId)
        {
            if (teamId == 0) return;
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
                if (!ti.Members.Contains(this)) ti.Members.Add(this);
                if (ti.Leader == null || Serial < ti.Leader.Serial) ti.Leader = this;
            }
        }

        private void RemoveFromTeam(int teamId)
        {
            if (teamId == 0) return;
            TeamInfo ti;
            object teamLock = GetTeamLock();

            lock (teamLock)
            {
                if (!AllTeams.TryGetValue(teamId, out ti)) return;
                lock (ti.Members)
                {
                    ti.Members.Remove(this);
                    if (ti.Leader == this)
                    {
                        AdventurerTeam newLeader = null;
                        for (int i = 0; i < ti.Members.Count; i++)
                        {
                            AdventurerTeam at = ti.Members[i];
                            if (at == null || at.Deleted) { ti.Members.RemoveAt(i--); continue; }
                            if (newLeader == null || at.Serial < newLeader.Serial) newLeader = at;
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

        private List<AdventurerTeam> GetSharedNearbyMembers(DateTime now)
        {
            if (m_TeamId == 0) return m_CachedNearbyMembers;
            TeamInfo ti;
            if (AllTeams.TryGetValue(m_TeamId, out ti) && ti != null)
            {
                if ((now - ti.SharedScanTime).TotalSeconds < (TeamScanIntervalSeconds * 2.0))
                    return ti.SharedNearbyMembers;
            }
            return m_CachedNearbyMembers;
        }
        #endregion

        #region Optimization Utils
        private bool CanSendMessage(DateTime now)
        {
            if ((now.Ticks - m_LastMessageTime.Ticks) < HalfSecondTicks) return false;
            m_LastMessageTime = now;
            return true;
        }

        private string GetPooledMessage(string[] source, int sourceLen)
        {
            int slot = (m_MessagePoolIndex++ & (MessagePoolSize - 1));
            string msg = m_MessagePool[slot];
            if (msg == null) msg = source[Utility.Random(sourceLen)];
            m_MessagePool[slot] = msg;
            return msg;
        }

        private void RefreshRandomCache()
        {
            int raw = Utility.Random(1000000);
            m_CachedRandom1 = (raw % 1000) * 0.001;
            m_CachedRandom2 = ((raw / 1000) % 1000) * 0.001;
        }

        private bool IsPrimaryThinker(DateTime now)
        {
            return (Serial.Value % 3) == (now.Second % 3);
        }
        #endregion

        #region Event-Driven Logic

        public override void OnDamage(int amount, Mobile from, bool willKill)
        {
            base.OnDamage(amount, from, willKill);

            if (willKill || Deleted) return;

            DateTime now = DateTime.UtcNow;
            int maxHits = HitsMax;
            double hpRatio = (maxHits > 0) ? (double)Hits / maxHits : 0;

            if (!m_IsRetreating && hpRatio < RetreatThreshold && m_CitizenType != (int)CitizenClass.Fighter)
            {
                if (Utility.RandomDouble() < 0.35)
                {
                    m_IsRetreating = true;
                    if (CanSendMessage(now)) Say(RetreatLines[Utility.Random(RetreatLinesLen)]);
                    Combatant = null;
                    m_PendingRetreatReset = true;
                    m_PendingRetreatResetTime = now + RetreatResetDelay;
                }
            }

            if (hpRatio < CriticalHealthThreshold && !m_HasWarnedCriticalHealth)
            {
                m_HasWarnedCriticalHealth = true;
                if (CanSendMessage(now)) Say(GetPooledMessage(CriticalHealthWarnings, CriticalHealthWarningsLen));
            }
            else if (hpRatio < LowHealthThreshold && !m_HasWarnedLowHealth)
            {
                m_HasWarnedLowHealth = true;
                if (CanSendMessage(now)) Say(GetPooledMessage(LowHealthWarnings, LowHealthWarningsLen));
            }

            TryHealSelf(now, hpRatio);
        }

        public override void OnCombatantChange()
        {
            base.OnCombatantChange();
            DateTime now = DateTime.UtcNow;

            if (Combatant == null)
            {
                if (!m_CelebrationDone && m_LastCombatTime != DateTime.MinValue)
                {
                    m_PendingCelebrate = true;
                    m_PendingCelebrateTime = now + CelebrateDelay;
                    m_CelebrationDone = true;
                }
                m_HasWarnedLowHealth = false;
                m_HasWarnedCriticalHealth = false;
            }
            else
            {
                m_LastCombatTime = now;
                m_CelebrationDone = false;
                
                if (CanSendMessage(now) && Utility.RandomDouble() < 0.4)
                    Say(GetPooledMessage(CombatYell, CombatYellLen));
            }
        }

        public override void OnMovement(Mobile m, Point3D oldLocation)
        {
            if (m_IsLeaving || Deleted || m == null || m.Map != Map) return;
            
            DateTime now = DateTime.UtcNow;
            if (now - m_LastMovementCheck < MovementThrottle) return;

            PlayerMobile pm = m as PlayerMobile;
            if (pm == null) return;

            int dx = X - m.X;
            int dy = Y - m.Y;
            if ((dx * dx + dy * dy) > 144) return;

            if (CanSee(m))
            {
                m_LastMovementCheck = now;
                if (now - m_LastGreetTime < GreetCooldown) return;

                if (Combatant != null)
                {
                    if (!m_IsEvil && pm.Karma >= 0 && m_CachedRandom1 < 0.30)
                    {
                        Say("Help us! We're under attack!");
                        m_LastGreetTime = now;
                    }
                }
                else if (m_CachedRandom2 < 0.45)
                {
                    m_LastGreetTime = now;
                    Say(m_IsEvil ? "Stay out of our way." : "Safe travels, friend.");
                }
            }
        }
        #endregion

        #region Core AI Loop (OnThink)

        public override void OnThink()
        {
            base.OnThink();

            if (Deleted || Map == null || Map == Map.Internal) return;

            DateTime now = DateTime.UtcNow;
            
            ProcessPendingActions(now);
            ProcessPendingHeal(now);

            if (!IsPrimaryThinker(now)) return;

            if (now >= m_NextRandomRefresh)
            {
                RefreshRandomCache();
                m_NextRandomRefresh = now + TwoSeconds;
            }

            bool isLeader = IsTeamLeaderAt(now);

            if (Combatant != null)
            {
                if (m_TeamId != 0 && isLeader)
                {
                    TeamInfo ti;
                    if (AllTeams.TryGetValue(m_TeamId, out ti) && 
                        (now - ti.SharedScanTime).TotalSeconds >= CombatScanIntervalSeconds)
                    {
                        UnifiedTeamScan(now, ti);
                    }
                    ApplyCombatantToNearbyMembers(now);
                }

                if (!m_IsRetreating)
                {
                    TryHealAllies(now);
                }
            }
            else
            {
                if (!m_IsLeaving && now > m_BoredTime && isLeader && m_CachedRandom1 < 0.02)
                {
                    InitiateTeamDeparture(now);
                }

                if (!m_IsRetreating)
                {
                    if (Hits < HitsMax && now >= m_NextSelfHeal)
                        TryHealSelf(now, (double)Hits/HitsMax);
                    
                    TryHealAllies(now);
                }
            }

            if (now >= m_NextChatTime && Combatant == null)
            {
                HandleChat(now);
                m_NextChatTime = now.AddSeconds(Utility.RandomMinMax(20, 40));
            }
        }
        #endregion

        #region Action Processing
        private void ProcessPendingHeal(DateTime now)
        {
            if (m_PendingHealTarget == null || now < m_PendingHealTime) return;

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
                if (!Deleted && Map != null && CanSendMessage(now))
                {
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
                if (!Deleted && Map != null) Say(GetPooledMessage(LootingLines, LootingLinesLen));
            }

            if (m_PendingMourn && now >= m_PendingMournTime)
            {
                m_PendingMourn = false;
                if (!Deleted && Map != null) Say(GetPooledMessage(MourningLines, MourningLinesLen));
            }

            if (m_PendingRetreatReset && now >= m_PendingRetreatResetTime)
            {
                m_PendingRetreatReset = false;
                if (!Deleted) m_IsRetreating = false;
            }

            if (m_PendingDeparture && now >= m_PendingDepartureTime)
            {
                m_PendingDeparture = false;
                if (!Deleted) ExecuteTeamDeparture();
            }
        }

        private void HandleChat(DateTime now)
        {
            if (Deleted || Map == null || m_CachedRandom2 >= 0.38) return;
            if (!CanSendMessage(now)) return;

            string line = m_IsEvil ? GetPooledMessage(EvilChat, EvilChatLen) : GetPooledMessage(FriendlyChat, FriendlyChatLen);
            PublicOverheadMessage(MessageType.Regular, m_IsEvil ? 0x22 : SpeechHue, true, line);
        }
        #endregion

        #region Scanning & Team Logic
        private void UnifiedTeamScan(DateTime now, TeamInfo ti)
        {
            if (Map == null) return;

            ti.SharedScanTime = now;
            ti.SharedEnemyCount = 0;
            ti.SharedInjuredAllies = 0;
            ti.SharedNearbyMembers.Clear();

            IPooledEnumerable eable = Map.GetMobilesInRange(Location, TeamMemberRange);
            try
            {
                foreach (Mobile m in eable)
                {
                    if (m == null || m == this || m.Deleted || !m.Alive) continue;

                    AdventurerTeam at = m as AdventurerTeam;
                    if (at != null && at.m_TeamId == m_TeamId)
                    {
                        if (ti.SharedNearbyMembers.Count < MaxCachedNearbyMembers)
                            ti.SharedNearbyMembers.Add(at);
                        
                        if (at.Hits < (at.HitsMax >> 1))
                            ti.SharedInjuredAllies++;
                        continue;
                    }

                    if (m.Combatant != null)
                    {
                        AdventurerTeam atCombatant = m.Combatant as AdventurerTeam;
                        if (atCombatant != null && atCombatant.m_TeamId == m_TeamId)
                            ti.SharedEnemyCount++;
                    }

                    if (ti.SharedEnemyCount >= 8 && ti.SharedInjuredAllies >= 4 && ti.SharedNearbyMembers.Count >= MaxCachedNearbyMembers)
                        break;
                }
            }
            finally
            {
                if (eable != null) eable.Free();
            }
        }

        private void ApplyCombatantToNearbyMembers(DateTime now)
        {
            if (Combatant == null) return;
            List<AdventurerTeam> nearby = GetSharedNearbyMembers(now);
            if (nearby == null) return;

            int count = nearby.Count;
            for (int i = 0; i < count; i++)
            {
                AdventurerTeam at = nearby[i];
                if (at == null || at.Deleted) continue;
                if (at.Combatant == null && !at.m_IsRetreating && at.InRange(this, 10))
                    at.Combatant = Combatant;
            }
        }
        #endregion

        #region Healing System
        private void TryHealSelf(DateTime now, double hpRatio)
        {
            if (m_IsUsingBandage || now < m_NextSelfHeal) return;
            if (hpRatio >= 0.70) return;

            if (hpRatio < 0.35 && m_CachedRandom1 < 0.60)
                if (TryUsePotionHealing(now)) return;

            if (m_CitizenType == (int)CitizenClass.Wizard && m_CachedRandom1 < 0.50)
                if (TryUseMagicHealing(this, now)) return;

            if ((m_CitizenType == (int)CitizenClass.Fighter || m_CitizenType == (int)CitizenClass.Rogue) &&
                hpRatio > 0.35 && m_CachedRandom1 < 0.50)
                if (TryUseBandageHealing(this, now)) return;

            if (hpRatio < 0.50 && m_CachedRandom1 < 0.30)
                TryUsePotionHealing(now);
        }

        private void TryHealAllies(DateTime now)
        {
            if (m_CitizenType != (int)CitizenClass.Wizard) return;
            if (now < m_NextAllyHeal) return;
            if (Combatant != null && Hits < (HitsMax >> 1)) return;

            List<AdventurerTeam> nearby = GetSharedNearbyMembers(now);
            int count = nearby.Count;
            if (count == 0) return;

            AdventurerTeam mostInjured = null;
            double lowestHpPercent = HealAllyThreshold;

            for (int i = 0; i < count; i++)
            {
                AdventurerTeam ally = nearby[i];
                if (ally == null || ally.Deleted || !ally.Alive) continue;

                if (ally.Map != Map) continue;
                int dx = X - ally.X;
                int dy = Y - ally.Y;
                if ((dx * dx + dy * dy) > 64) continue;

                if (!InLOS(ally)) continue;

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
                    m_NextAllyHeal = now + EightSeconds;
            }
        }

        private bool TryUsePotionHealing(DateTime now)
        {
            BaseHealPotion potion = FindPotionInBackpack();
            if (potion != null)
            {
                potion.Drink(this);
                PublicOverheadMessage(MessageType.Emote, 0x3B2, true, GetPooledMessage(PotionLines, PotionLinesLen));
                m_NextSelfHeal = now + TenSeconds;
                return true;
            }
            if (m_CachedRandom1 < 0.15 && CanSendMessage(now))
                Say(GetPooledMessage(OutOfSuppliesLines, OutOfSuppliesLinesLen));
            return false;
        }

        private bool TryUseBandageHealing(Mobile target, DateTime now)
        {
            if (Backpack == null || m_IsUsingBandage) return false;
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
                int healAmount = (int)(Utility.RandomMinMax((int)(healingSkill * 0.2) + 5, (int)(healingSkill * 0.4) + 10) * (1.0 + (anatomySkill / 500.0)));

                m_PendingHealTime = now.AddSeconds(4.0);
                m_PendingHealTarget = target;
                m_PendingHealAmount = healAmount;
                m_PendingIsMagicHeal = false;
                m_NextSelfHeal = now + TenSeconds;
                return true;
            }
            catch { m_IsUsingBandage = false; return false; }
        }

        private bool TryUseMagicHealing(Mobile target, DateTime now)
        {
            if (m_CitizenType != (int)CitizenClass.Wizard || Mana < 4) return false;

            int healAmount;
            int manaCost;

            if (Mana >= 11 && m_CitizenLevel >= 5) { healAmount = Utility.RandomMinMax(25, 35); manaCost = 11; }
            else if (Mana >= 6) { healAmount = Utility.RandomMinMax(15, 25); manaCost = 6; }
            else { healAmount = Utility.RandomMinMax(10, 18); manaCost = 4; }

            if (Mana < manaCost) return false;

            Mana -= manaCost;
            if (target != this) Direction = GetDirectionTo(target);

            Animate(17, 7, 1, true, false, 0);
            PlaySound(0x1F2);

            m_PendingHealTime = now.AddSeconds(0.8);
            m_PendingHealTarget = target;
            m_PendingHealAmount = healAmount;
            m_PendingIsMagicHeal = true;

            if (target == this)
            {
                PublicOverheadMessage(MessageType.Emote, 0x3B2, true, "*channels healing magic*");
                m_NextSelfHeal = now + EightSeconds;
            }
            else
            {
                PublicOverheadMessage(MessageType.Emote, 0x3B2, true, GetPooledMessage(HealSpellLines, HealSpellLinesLen));
            }
            return true;
        }

        private BaseHealPotion FindPotionInBackpack()
        {
            if (Backpack == null) return null;
            Item item = Backpack.FindItemByType(typeof(GreaterHealPotion));
            if (item != null) return (BaseHealPotion)item;
            item = Backpack.FindItemByType(typeof(HealPotion));
            if (item != null) return (BaseHealPotion)item;
            item = Backpack.FindItemByType(typeof(LesserHealPotion));
            if (item != null) return (BaseHealPotion)item;
            return null;
        }
        #endregion

        #region Departure Logic
        private void InitiateTeamDeparture(DateTime now)
        {
            if (m_IsLeaving) return;
            m_IsLeaving = true;
            if (CanSendMessage(now)) Say(DepartureLines[Utility.Random(DepartureLinesLen)]);
            m_PendingDeparture = true;
            m_PendingDepartureTime = now + DepartureDelay;
        }

        private void ExecuteTeamDeparture()
        {
            if (m_TeamId == 0) return;
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

        #region Setup (Stats)
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

            if (!initial && (hand != null || twohand != null)) return;

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

            if (initial)
            {
                if (hand != null) hand.Delete();
                if (twohand != null) twohand.Delete();
            }

            // Custom Throwing Gloves Check
            if (Utility.RandomBool() && (m_CitizenType != (int)CitizenClass.Fighter))
            {
                AddItem(new Item(0x13C6) { Name = "Throwing Gloves" });
                PackItem(new Item(0xF0E) { Name = "Throwing Ammunition" });
                return;
            }

            if (m_CitizenType == (int)CitizenClass.Wizard)
            {
                AddItem(Utility.RandomBool() ? (Item)new GnarledStaff() : new QuarterStaff());
            }
            else if (m_CitizenType == (int)CitizenClass.Rogue)
            {
                int ammoCount = Utility.RandomMinMax(60, 100);
                switch (Utility.Random(8))
                {
                    case 0: AddItem(new Bow()); PackItem(new Arrow(ammoCount)); break;
                    case 1: AddItem(new Crossbow()); PackItem(new Bolt(ammoCount)); break;
                    case 2: AddItem(new HeavyCrossbow()); PackItem(new Bolt(ammoCount)); break;
                    case 3: AddItem(new RepeatingCrossbow()); PackItem(new Bolt(ammoCount)); break;
                    case 4: AddItem(new CompositeBow()); PackItem(new Arrow(ammoCount)); break;
                    case 5: AddItem(new Bow()); PackItem(new Arrow(ammoCount)); break;
                    case 6: AddItem(new Crossbow()); PackItem(new Bolt(ammoCount)); break;
                    case 7: AddItem(new Crossbow()); PackItem(new Bolt(ammoCount)); break;
                }
            }
        }

        private void AddHealingSupplies()
        {
            int potionCount = Utility.RandomMinMax(3, 5);
            int bandageCount = Utility.RandomMinMax(20, 40);

            if (m_CitizenType == (int)CitizenClass.Fighter || m_CitizenType == (int)CitizenClass.Rogue)
                PackItem(new Bandage(bandageCount));

            for (int i = 0; i < potionCount; i++)
            {
                if (m_CitizenLevel >= 7) PackItem(new GreaterHealPotion());
                else if (m_CitizenLevel >= 4) PackItem(new HealPotion());
                else PackItem(new LesserHealPotion());
            }
        }
        #endregion

        #region Serialization
        public override void GenerateLoot()
        {
            if (m_CitizenLevel >= 7) AddLoot(LootPack.Rich);
            else if (m_CitizenLevel >= 5) AddLoot(LootPack.Average);
            else AddLoot(LootPack.Meager);
            
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

        private static readonly List<PlayerMobile> s_PlayerCache = new List<PlayerMobile>(64);
        private static DateTime s_PlayerCacheExpiry = DateTime.MinValue;

        private const int MaxConcurrentTeams = 12;
        private const int MinTeamSize = 2;
        private const int MaxTeamSize = 6;
        private const int PlayerGroupRadius = 30;
        private const int MinSpawnDist = 18;
        private const int MaxSpawnDist = 25;

        public static void Initialize()
        {
            if (s_MaintenanceTimer != null) s_MaintenanceTimer.Stop();
            s_MaintenanceTimer = Timer.DelayCall(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), new TimerCallback(MaintainTeams));
        }

        [CommandProperty(AccessLevel.Administrator)]
        public static bool Enabled
        {
            get { return s_Enabled; }
            set
            {
                s_Enabled = value;
                if (value) Initialize();
                else if (s_MaintenanceTimer != null) { s_MaintenanceTimer.Stop(); s_MaintenanceTimer = null; }
            }
        }

        public static int GetNewTeamId()
        {
            lock (s_IdLock)
            {
                int id = (s_RecycledIds.Count > 0) ? s_RecycledIds.Dequeue() : s_NextTeamId++;
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
            if (!s_Enabled) return;

            int activeTeams;
            lock (AdventurerTeam.AllTeamsLock) { activeTeams = AdventurerTeam.AllTeams.Count; }
            if (activeTeams >= MaxConcurrentTeams) return;

            DateTime now = DateTime.UtcNow;

            if (now >= s_PlayerCacheExpiry)
            {
                s_PlayerCache.Clear();
                foreach (NetState state in NetState.Instances)
                {
                    Mobile m = state.Mobile;
                    if (m is PlayerMobile && m.AccessLevel == AccessLevel.Player && m.Alive && m.Map != null && m.Map != Map.Internal)
                        s_PlayerCache.Add((PlayerMobile)m);
                }
                s_PlayerCacheExpiry = now.AddSeconds(30);
            }

            if (s_PlayerCache.Count == 0) return;

            int checks = Math.Min(6, s_PlayerCache.Count);
            for (int i = 0; i < checks; i++)
            {
                if (s_PlayerCache.Count == 0) break;
                int idx = Utility.Random(s_PlayerCache.Count);
                PlayerMobile pm = s_PlayerCache[idx];
                s_PlayerCache.RemoveAt(idx);

                lock (AdventurerTeam.AllTeamsLock) { activeTeams = AdventurerTeam.AllTeams.Count; }
                if (activeTeams >= MaxConcurrentTeams) break;

                TrySpawnTeamForPlayer(pm);
            }
        }

        private static void TrySpawnTeamForPlayer(PlayerMobile pm)
        {
            if (pm == null || pm.Deleted || pm.Map == null) return;

            int thisTeamSize = Utility.RandomMinMax(MinTeamSize, MaxTeamSize);

            int nearbyAdventurers = 0;
            IPooledEnumerable eable = pm.Map.GetMobilesInRange(pm.Location, PlayerGroupRadius);
            try
            {
                foreach (Mobile m in eable)
                {
                    if (m is AdventurerTeam)
                    {
                        nearbyAdventurers++;
                        if (nearbyAdventurers >= MaxTeamSize) break;
                    }
                }
            }
            finally { if (eable != null) eable.Free(); }

            if (nearbyAdventurers >= MaxTeamSize) return;

            Point3D spawnLoc = FindSpawnLocation(pm);
            if (spawnLoc == Point3D.Zero) return;

            int teamId = GetNewTeamId();
            bool isEvil = Utility.RandomBool();

            for (int i = 0; i < thisTeamSize; i++)
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
                double radians = Utility.RandomDouble() * Math.PI * 2.0;
                int xOffset = (int)(Math.Cos(radians) * dist);
                int yOffset = (int)(Math.Sin(radians) * dist);
                Point3D testLoc = new Point3D(center.X + xOffset, center.Y + yOffset, center.Z);

                if (map.CanSpawnMobile(testLoc) && !IsInForbiddenRegion(testLoc, map))
                    return testLoc;
            }
            return Point3D.Zero;
        }

        private static bool IsInForbiddenRegion(Point3D loc, Map map)
        {
            Region reg = Region.Find(loc, map);
            if (reg == null) return false;
            
            if (reg is GuardedRegion) return true;
            
            if (reg.Name != null)
            {
                string rName = reg.Name.ToLower();
                if (rName.IndexOf("town") >= 0 || rName.IndexOf("safe") >= 0 || rName.IndexOf("house") >= 0)
                    return true;
            }
            return false;
        }
    }
}
