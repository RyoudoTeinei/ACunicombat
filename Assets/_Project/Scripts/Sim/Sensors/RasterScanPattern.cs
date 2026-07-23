using Unity.Mathematics;

namespace ACunicombat.Sensors
{
    /// <summary>
    /// Deterministic serpentine raster used to turn a scan volume into narrow radar dwells.
    /// </summary>
    public sealed class RasterScanPattern
    {
        private readonly float azimuthSpanDegrees;
        private readonly float elevationSpanDegrees;
        private readonly int columnCount;
        private readonly int rowCount;
        private int cursor;

        public RasterScanPattern(
            float azimuthSpanDegrees,
            float elevationSpanDegrees,
            float beamSpacingDegrees)
        {
            this.azimuthSpanDegrees = math.max(0f, azimuthSpanDegrees);
            this.elevationSpanDegrees = math.max(0f, elevationSpanDegrees);
            var spacing = math.max(0.1f, beamSpacingDegrees);
            columnCount = math.max(1, (int)math.ceil(this.azimuthSpanDegrees / spacing) + 1);
            rowCount = math.max(1, (int)math.ceil(this.elevationSpanDegrees / spacing) + 1);
        }

        public int CellCount => columnCount * rowCount;
        public int ColumnCount => columnCount;
        public int RowCount => rowCount;
        public bool IsFrameStart => cursor == 0;
        public float FrameProgress => cursor / (float)CellCount;

        public float2 Next()
        {
            var row = cursor / columnCount;
            var columnInTraversal = cursor % columnCount;
            var column = (row & 1) == 0
                ? columnInTraversal
                : columnCount - 1 - columnInTraversal;

            var azimuth = AxisAngle(column, columnCount, azimuthSpanDegrees);
            var elevation = -AxisAngle(row, rowCount, elevationSpanDegrees);

            cursor++;
            if (cursor >= CellCount)
            {
                cursor = 0;
            }

            return new float2(azimuth, elevation);
        }

        public void Reset() => cursor = 0;

        private static float AxisAngle(int index, int count, float spanDegrees)
        {
            if (count <= 1)
            {
                return 0f;
            }

            return math.lerp(
                spanDegrees * -0.5f,
                spanDegrees * 0.5f,
                index / (float)(count - 1));
        }
    }
}
