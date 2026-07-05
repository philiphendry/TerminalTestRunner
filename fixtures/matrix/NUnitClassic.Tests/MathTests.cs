using NUnit.Framework;

namespace Matrix.NUnitClassic;

[TestFixture]
public class MathTests
{
    [Test] public void Adds() => Assert.That(2 + 2, Is.EqualTo(4));
    [Test] public void Fails() => Assert.That(2 + 2, Is.EqualTo(5));  // fails on purpose

    [TestCase(2, 4)]
    [TestCase(3, 6)]
    public void Doubles(int n, int expected) => Assert.That(n * 2, Is.EqualTo(expected));
}
