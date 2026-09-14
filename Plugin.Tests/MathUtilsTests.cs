using SPTMap.Utils;
using Xunit;

namespace SPTMap.Tests
{
    public class MathUtilsTests
    {
        [Fact]
        public void ComputeViewBounds_AtZoom1_CoversFullBoundsRegardlessOfFocus()
        {
            var (viewMin, viewMax) = MathUtils.ComputeViewBounds(
                (0, 0), (100, 100), zoom: 1f, focusPos: (10, 90));

            Assert.Equal((0f, 0f), viewMin);
            Assert.Equal((100f, 100f), viewMax);
        }

        [Fact]
        public void ComputeViewBounds_NoFocus_CentersOnMap()
        {
            var (viewMin, viewMax) = MathUtils.ComputeViewBounds(
                (0, 0), (100, 100), zoom: 2f, focusPos: null);

            Assert.Equal((25f, 25f), viewMin);
            Assert.Equal((75f, 75f), viewMax);
        }

        [Fact]
        public void ComputeViewBounds_FocusNearEdge_ClampsInsteadOfOverhanging()
        {
            var (viewMin, viewMax) = MathUtils.ComputeViewBounds(
                (0, 0), (100, 100), zoom: 4f, focusPos: (2, 2));

            // half-span at zoom 4 is 12.5 - a focus at (2,2) would want a view starting at -10.5,
            // which must clamp to the map's own min (0,0) instead of reading past it.
            Assert.Equal((0f, 0f), viewMin);
            Assert.Equal((25f, 25f), viewMax);
        }

        [Fact]
        public void ComputeViewBounds_FocusInMiddle_CentersExactlyOnFocus()
        {
            var (viewMin, viewMax) = MathUtils.ComputeViewBounds(
                (0, 0), (100, 100), zoom: 4f, focusPos: (50, 50));

            Assert.Equal((37.5f, 37.5f), viewMin);
            Assert.Equal((62.5f, 62.5f), viewMax);
        }
    }
}
