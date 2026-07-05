using System.Threading.Tasks;

namespace Matrix.TUnit;

public class BasketTests
{
    [Test]
    public async Task Adds_Item() => await Assert.That(2 + 2).IsEqualTo(4);

    [Test]
    public async Task Detects_Wrong() => await Assert.That(2 + 2).IsEqualTo(5);  // fails on purpose

    [Test]
    [Arguments(2, 4)]
    [Arguments(5, 10)]
    public async Task Doubles(int n, int expected) => await Assert.That(n * 2).IsEqualTo(expected);
}
