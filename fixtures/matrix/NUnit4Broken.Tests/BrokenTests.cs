using NUnit.Framework;

namespace Matrix.NUnit4Broken;

[TestFixture]
public class BrokenTests
{
    [Test] public void ShouldNeverBeDiscovered() => Assert.Pass();
}
