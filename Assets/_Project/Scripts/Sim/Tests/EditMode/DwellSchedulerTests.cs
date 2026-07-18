using ACunicombat.Sensors;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    public class DwellSchedulerTests
    {
        private static Dwell Make(double due, DwellPriority priority, ulong seq)
        {
            return new Dwell
            {
                DueTime = due,
                Priority = priority,
                Sequence = seq,
                BeamDirection = new float3(1, 0, 0),
            };
        }

        [Test]
        public void OverBudget_HighPriorityPreemptsEarlierDue()
        {
            // 评审 P2 回归：过载时更早到期的广域搜索不得压住稍晚到期的导弹末制导。
            using var scheduler = new DwellScheduler(16);
            using var results = new NativeList<Dwell>(16, Allocator.Temp);
            scheduler.Schedule(Make(0.10, DwellPriority.WideSearch, 1));
            scheduler.Schedule(Make(0.20, DwellPriority.MissileTerminal, 2));
            scheduler.Schedule(Make(0.15, DwellPriority.TrackWhileScan, 3));

            var consumed = scheduler.ConsumeDue(simTime: 0.5, budget: 1, results);

            Assert.That(consumed, Is.EqualTo(1));
            Assert.That(results[0].Priority, Is.EqualTo(DwellPriority.MissileTerminal));
            // 其余两个回队顺延，DueTime 不变。
            Assert.That(scheduler.Count, Is.EqualTo(2));
            Assert.That(scheduler.DeferredCount, Is.EqualTo(2));
        }

        [Test]
        public void NotDue_NeverConsumed()
        {
            using var scheduler = new DwellScheduler(16);
            using var results = new NativeList<Dwell>(16, Allocator.Temp);
            scheduler.Schedule(Make(1.0, DwellPriority.MissileTerminal, 1));

            var consumed = scheduler.ConsumeDue(simTime: 0.5, budget: 8, results);

            Assert.That(consumed, Is.Zero);
            Assert.That(scheduler.Count, Is.EqualTo(1));
        }

        [Test]
        public void WithinBudget_AllDueConsumedInPriorityOrder()
        {
            using var scheduler = new DwellScheduler(16);
            using var results = new NativeList<Dwell>(16, Allocator.Temp);
            scheduler.Schedule(Make(0.3, DwellPriority.WideSearch, 1));
            scheduler.Schedule(Make(0.2, DwellPriority.SingleTargetTrack, 2));
            scheduler.Schedule(Make(0.1, DwellPriority.AlertConfirm, 3));

            var consumed = scheduler.ConsumeDue(simTime: 0.5, budget: 8, results);

            Assert.That(consumed, Is.EqualTo(3));
            Assert.That(results[0].Priority, Is.EqualTo(DwellPriority.SingleTargetTrack));
            Assert.That(results[1].Priority, Is.EqualTo(DwellPriority.AlertConfirm));
            Assert.That(results[2].Priority, Is.EqualTo(DwellPriority.WideSearch));
        }
    }
}
