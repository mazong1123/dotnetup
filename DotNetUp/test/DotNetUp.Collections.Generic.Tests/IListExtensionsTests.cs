using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace DotNetUp.Collections.Generic.Tests
{
    public class IListExtensionsTests
    {
        [Fact]
        public void ShuffleCountNotChangeTest()
        {
            // Prepare
            IList<int> l = new List<int>();
            l.Add(2);
            l.Add(3);
            l.Add(6);

            int expected = 3;

            // Act
            l.Shuffle();
            int actual = l.Count;

            // Assert
            Assert.Equal(expected, actual);
        }
    }
}
