using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Tracking
{
    /// <summary>
    /// 告警相关器：把匿名的 WarningObservation 按（频段 + 方位邻近）聚成辐射源
    /// 威胁，并从**观测到的照射间隔统计**推断威胁级别——不读辐射方任何模式标志：
    ///   间隔 ~1s（搜索帧扫过）→ Spiked；~0.24s（TWS 重访你）→ Tracked；
    ///   ~0.035s（连续照射）→ Locked；导引头波形 → MissileActive。
    /// 升级即时、降级带滞回（告警宁可多响 0.5s，不可早收）。
    /// 对欺骗天然诚实：对方想装 STT 就得真的提高驻留率。
    /// </summary>
    public sealed class EsmThreatCorrelator
    {
        public const int MaxThreats = 16;
        private const int WaveformTableCapacity = 16;

        private const float AssociationAngleCosine = 0.94f; // ~20°：涵盖 RWR 测向误差
        private const float DirectionSmoothing = 0.35f;
        private const float IntervalSmoothing = 0.4f;
        // 波程结构区分（比裸照射间隔更抗误报）：搜索波束扫过也会连给 2~3 条
        // 短间隔接触，若只看间隔会瞬间误报锁定。gap ≤ 阈值视为同一次连续照射
        // （累计照射时长），gap > 阈值才是重访间隔样本。
        // 搜索 = 短照射(~0.08s) + 长重访(~3s)；TWS = 单驻留 + ~0.24s 重访；
        // STT = 一次永不结束的连续照射。
        private const float ContinuousGapSeconds = 0.15f;
        private const float LockedContinuousSeconds = 0.5f;
        /// <summary>识别置信度门槛：低于此值只知道"有辐射"，不下波形结论。</summary>
        private const float IdentificationThreshold = 0.35f;
        private const double TrackedIntervalSeconds = 0.6;  // TWS ~0.24s 的 2.5 倍余量
        private const double DeEscalateHoldSeconds = 0.6;
        private const double DropUnseenSeconds = 5.0;

        private struct ThreatSlot
        {
            public bool Active;
            public EsmThreatId Id;
            public float3 Direction;
            public ushort FrequencyBand;
            public ushort WaveformId;
            public EsmWaveformClass WaveformClass;
            public EsmThreatLevel Level;
            public double FirstSeen;
            public double LastSeen;
            public float IntervalEma;      // 重访间隔估计；<0 表示样本不足
            public float ContinuousIlluminationSeconds;
            public float IdConfidenceEma;  // 波形识别置信度（低于门槛按 Unknown 分类）
            public float PowerEmaDb;
            public float PowerTrendDbPerSecond;
            public int ContactCount;
            public double LastLevelChange;
        }

        private struct WaveformEntry
        {
            public ushort Band;
            public ushort WaveformId;
            public EsmWaveformClass Class;
        }

        private readonly ThreatSlot[] slots = new ThreatSlot[MaxThreats];
        private readonly byte[] generations = new byte[MaxThreats];
        private readonly WaveformEntry[] waveformTable = new WaveformEntry[WaveformTableCapacity];
        private int waveformCount;
        private int activeCount;

        public EsmThreatCorrelator()
        {
            for (var i = 0; i < generations.Length; i++)
            {
                generations[i] = 1;
            }
        }

        public int Count => activeCount;

        /// <summary>波形库登记（P2 扩展为威胁类型/射程条目）。重复登记覆盖。</summary>
        public void RegisterWaveformClass(ushort band, ushort waveformId, EsmWaveformClass waveformClass)
        {
            for (var i = 0; i < waveformCount; i++)
            {
                if (waveformTable[i].Band == band && waveformTable[i].WaveformId == waveformId)
                {
                    waveformTable[i].Class = waveformClass;
                    return;
                }
            }

            if (waveformCount < waveformTable.Length)
            {
                waveformTable[waveformCount++] = new WaveformEntry
                {
                    Band = band,
                    WaveformId = waveformId,
                    Class = waveformClass,
                };
            }
        }

        public void Process(NativeList<WarningObservation> contacts, SensorId receiverId, double simTime)
        {
            for (var i = 0; i < contacts.Length; i++)
            {
                var contact = contacts[i];
                if (!contact.ReceiverId.Equals(receiverId))
                {
                    continue;
                }

                var slotIndex = FindAssociation(contact.Direction, contact.FrequencyBand);
                if (slotIndex < 0)
                {
                    slotIndex = AllocateSlot(in contact, simTime);
                    if (slotIndex < 0)
                    {
                        continue; // 满 16 个辐射源：丢弃新弱源（P2：按威胁度淘汰）。
                    }
                }

                UpdateSlot(slotIndex, in contact);
            }

            for (var i = 0; i < slots.Length; i++)
            {
                if (!slots[i].Active)
                {
                    continue;
                }

                var slot = slots[i];
                var unseen = simTime - slot.LastSeen;
                if (unseen > DropUnseenSeconds)
                {
                    ReleaseSlot(i);
                    continue;
                }

                if (unseen > ContinuousGapSeconds)
                {
                    // 照射已中断：连续照射证据清零，锁定级不再靠陈旧累计维持。
                    slot.ContinuousIlluminationSeconds = 0f;
                }

                var supported = SupportedLevel(in slot, unseen);
                if (supported > slot.Level)
                {
                    // 升级即时。
                    slot.Level = supported;
                    slot.LastLevelChange = simTime;
                }
                else if (supported < slot.Level &&
                         simTime - slot.LastLevelChange >= DeEscalateHoldSeconds)
                {
                    // 降级逐档带滞回。
                    slot.Level--;
                    slot.LastLevelChange = simTime;
                }

                slots[i] = slot;
            }
        }

        public int CopyThreats(ThreatSnapshot[] destination)
        {
            if (destination == null || destination.Length == 0)
            {
                return 0;
            }

            var count = 0;
            for (var i = 0; i < slots.Length && count < destination.Length; i++)
            {
                if (!slots[i].Active)
                {
                    continue;
                }

                destination[count++] = BuildSnapshot(in slots[i]);
            }

            return count;
        }

        public bool TryGetHighestThreat(out ThreatSnapshot snapshot)
        {
            var bestIndex = -1;
            var bestKey = -1.0;
            for (var i = 0; i < slots.Length; i++)
            {
                if (!slots[i].Active)
                {
                    continue;
                }

                var key = (double)slots[i].Level * 1_000.0 + slots[i].PowerEmaDb;
                if (key > bestKey)
                {
                    bestKey = key;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                snapshot = default;
                return false;
            }

            snapshot = BuildSnapshot(in slots[bestIndex]);
            return true;
        }

        private EsmThreatLevel SupportedLevel(in ThreatSlot slot, double unseen)
        {
            // 证据窗口：超过 3 个照射周期（或至少 0.9s）没再被照到，当前级别失去支撑。
            var evidenceWindow = math.max(
                slot.IntervalEma > 0f ? slot.IntervalEma * 3.0 : 0.0,
                0.9);
            if (unseen > evidenceWindow)
            {
                return unseen > 2.5 ? EsmThreatLevel.None : EsmThreatLevel.Spiked;
            }

            if (slot.WaveformClass == EsmWaveformClass.MissileSeeker ||
                slot.WaveformClass == EsmWaveformClass.InfraredPlume)
            {
                return EsmThreatLevel.MissileActive;
            }

            if (slot.WaveformClass == EsmWaveformClass.DataLinkUplink)
            {
                // 解出制导上行波形 = 附近有弹在飞，早于 pitbull 的软告警；
                // 4 Hz 上行的重访间隔本身也会推到 Tracked，这里显式钉住下限。
                return slot.ContinuousIlluminationSeconds >= LockedContinuousSeconds
                    ? EsmThreatLevel.Locked
                    : EsmThreatLevel.Tracked;
            }

            if (slot.ContinuousIlluminationSeconds >= LockedContinuousSeconds)
            {
                return EsmThreatLevel.Locked;
            }

            if (slot.IntervalEma > 0f && slot.IntervalEma < TrackedIntervalSeconds)
            {
                return EsmThreatLevel.Tracked;
            }

            return EsmThreatLevel.Spiked;
        }

        private int FindAssociation(float3 direction, ushort band)
        {
            var bestIndex = -1;
            var bestDot = AssociationAngleCosine;
            for (var i = 0; i < slots.Length; i++)
            {
                if (!slots[i].Active || slots[i].FrequencyBand != band)
                {
                    continue;
                }

                var dot = math.dot(direction, slots[i].Direction);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private int AllocateSlot(in WarningObservation contact, double simTime)
        {
            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i].Active)
                {
                    continue;
                }

                slots[i] = new ThreatSlot
                {
                    Active = true,
                    Id = new EsmThreatId(new Handle(i, generations[i])),
                    Direction = contact.Direction,
                    FrequencyBand = contact.FrequencyBand,
                    WaveformId = contact.WaveformId,
                    WaveformClass = EsmWaveformClass.Unknown, // 首条接触置信度未积累，UpdateSlot 里判
                    IdConfidenceEma = 0f,
                    Level = EsmThreatLevel.None,
                    FirstSeen = contact.Timestamp,
                    LastSeen = contact.Timestamp - 1e-3, // 首条接触也能进 UpdateSlot 的间隔采样保护
                    IntervalEma = -1f,
                    PowerEmaDb = contact.ReceivedPowerDb,
                    LastLevelChange = simTime,
                };
                activeCount++;
                return i;
            }

            return -1;
        }

        private void UpdateSlot(int index, in WarningObservation contact)
        {
            var slot = slots[index];
            var gap = (float)(contact.Timestamp - slot.LastSeen);
            if (slot.ContactCount > 0 && gap > 0.005f)
            {
                if (gap <= ContinuousGapSeconds)
                {
                    // 同一次波束照射的延续：累计连续照射时长（STT 的锁定证据）。
                    slot.ContinuousIlluminationSeconds += gap;
                }
                else
                {
                    // 新的一次照射：上一间隔是重访周期样本（TWS/搜索的证据）。
                    slot.IntervalEma = slot.IntervalEma <= 0f
                        ? gap
                        : math.lerp(slot.IntervalEma, gap, IntervalSmoothing);
                    slot.ContinuousIlluminationSeconds = 0f;
                }
            }

            if (gap > 1e-4f)
            {
                var trendSample = (contact.ReceivedPowerDb - slot.PowerEmaDb) / gap;
                slot.PowerTrendDbPerSecond = math.lerp(
                    slot.PowerTrendDbPerSecond, trendSample, 0.3f);
            }

            slot.PowerEmaDb = math.lerp(slot.PowerEmaDb, contact.ReceivedPowerDb, 0.3f);
            slot.Direction = math.normalizesafe(
                math.lerp(slot.Direction, contact.Direction, DirectionSmoothing),
                slot.Direction);
            slot.WaveformId = contact.WaveformId;
            slot.IdConfidenceEma = math.lerp(slot.IdConfidenceEma, contact.WaveformConfidence, 0.3f);
            slot.WaveformClass = slot.IdConfidenceEma >= IdentificationThreshold
                ? LookupWaveformClass(contact.FrequencyBand, contact.WaveformId)
                : EsmWaveformClass.Unknown;
            slot.LastSeen = contact.Timestamp;
            slot.ContactCount++;
            slots[index] = slot;
        }

        private EsmWaveformClass LookupWaveformClass(ushort band, ushort waveformId)
        {
            for (var i = 0; i < waveformCount; i++)
            {
                if (waveformTable[i].Band == band && waveformTable[i].WaveformId == waveformId)
                {
                    return waveformTable[i].Class;
                }
            }

            return EsmWaveformClass.Unknown;
        }

        private void ReleaseSlot(int index)
        {
            slots[index] = default;
            var next = (byte)(generations[index] + 1);
            generations[index] = next == 0 ? (byte)1 : next;
            activeCount--;
        }

        private static ThreatSnapshot BuildSnapshot(in ThreatSlot slot)
        {
            return new ThreatSnapshot
            {
                ThreatId = slot.Id,
                DirectionWorld = slot.Direction,
                Level = slot.Level,
                WaveformClass = slot.WaveformClass,
                FrequencyBand = slot.FrequencyBand,
                WaveformId = slot.WaveformId,
                IlluminationIntervalSeconds = slot.IntervalEma,
                ContinuousIlluminationSeconds = slot.ContinuousIlluminationSeconds,
                IdentificationConfidence = slot.IdConfidenceEma,
                FirstSeenTime = slot.FirstSeen,
                LastSeenTime = slot.LastSeen,
                ReceivedPowerDb = slot.PowerEmaDb,
                PowerTrendDbPerSecond = slot.PowerTrendDbPerSecond,
                ContactCount = slot.ContactCount,
            };
        }
    }
}
