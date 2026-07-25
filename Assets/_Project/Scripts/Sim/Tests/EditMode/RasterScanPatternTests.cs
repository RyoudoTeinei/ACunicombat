using ACunicombat.Sensors;
using NUnit.Framework;

namespace ACunicombat.Tests.EditMode
{
    public class RasterScanPatternTests
    {
        [Test]
        public void OneFrame_CoversConfiguredVolumeWithSerpentineRows()
        {
            var pattern = new RasterScanPattern(100f, 60f, 9f);

            Assert.That(pattern.ColumnCount, Is.EqualTo(13));
            Assert.That(pattern.RowCount, Is.EqualTo(8));
            Assert.That(pattern.CellCount, Is.EqualTo(104));
            Assert.That(pattern.IsFrameStart, Is.True);

            var first = pattern.Next();
            for (var i = 1; i < pattern.ColumnCount; i++)
            {
                pattern.Next();
            }

            var secondRowFirst = pattern.Next();
            Assert.That(first.x, Is.EqualTo(-50f).Within(0.001f));
            Assert.That(first.y, Is.EqualTo(30f).Within(0.001f));
            Assert.That(secondRowFirst.x, Is.EqualTo(50f).Within(0.001f));
            // 8 行均布 ±30°：行距 = 60/7 ≈ 8.571°（实现保证 ≤ 请求的 9° 以维持
            // 波束重叠）。原期望 20° 意味着 10° 行距，与上面断言的 8 行/60° 矛盾。
            Assert.That(secondRowFirst.y, Is.EqualTo(30f - 60f / 7f).Within(0.001f));

            for (var i = pattern.ColumnCount + 1; i < pattern.CellCount; i++)
            {
                pattern.Next();
            }

            Assert.That(pattern.IsFrameStart, Is.True);
            Assert.That(pattern.FrameProgress, Is.Zero);
        }

        [Test]
        public void Reset_ReturnsCursorToFirstCell()
        {
            var pattern = new RasterScanPattern(100f, 60f, 9f);
            var first = pattern.Next();
            pattern.Next();

            pattern.Reset();

            Assert.That(pattern.Next(), Is.EqualTo(first));
        }
    }
}
