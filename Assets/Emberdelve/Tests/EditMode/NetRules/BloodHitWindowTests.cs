using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.Tests.NetRules
{
    public sealed class BloodHitWindowTests
    {
        [Test]
        public void AcceptsUniqueOutOfOrderHitsAndRejectsDuplicates()
        {
            var window = new BloodHitWindow();
            Assert.True(window.Accept(3, true, 2, 1, 2, 3));
            Assert.True(window.Accept(1, true, 2, 1, 2, 3));
            Assert.False(window.Accept(3, true, 2, 1, 2, 3));
            Assert.True(window.Accept(2, true, 2, 1, 2, 3));
        }

        [Test]
        public void RejectsInvalidInputsWithoutAdvancingWindow()
        {
            var window = new BloodHitWindow();
            Assert.False(window.Accept(0, true, 1, 0, 0, 0));
            Assert.False(window.Accept(9000, false, 1, 0, 0, 0));
            Assert.False(window.Accept(9000, true, 0, 0, 0, 0));
            Assert.False(window.Accept(9000, true, float.NaN, 0, 0, 0));
            Assert.False(window.Accept(9000, true, 1, float.PositiveInfinity, 0, 0));
            Assert.False(window.Accept(9000, true, 1, 0, float.NaN, 0));
            Assert.False(window.Accept(9000, true, 1, 0, 0, float.NegativeInfinity));
            Assert.True(window.Accept(1, true, 1, 0, 0, 0));
        }

        [Test]
        public void RejectsStaleEventsAndClearStartsNewSession()
        {
            var window = new BloodHitWindow();
            Assert.True(window.Accept(1, true, 1, 0, 0, 0));
            Assert.True(window.Accept(1026, true, 1, 0, 0, 0));
            Assert.False(window.Accept(1, true, 1, 0, 0, 0));
            window.Clear();
            Assert.True(window.Accept(1, true, 1, 0, 0, 0));
        }
    }
}
