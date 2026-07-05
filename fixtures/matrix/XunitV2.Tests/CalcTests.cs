using Xunit;

namespace Matrix.XunitV2;

public class Calculator
{
    public int Add(int a, int b) => a + b;
    public int Divide(int a, int b) => a / b;
}

public class CalcTests
{
    readonly Calculator _c = new();

    [Fact] public void Add_ReturnsSum() => Assert.Equal(4, _c.Add(2, 2));
    [Fact] public void Divide_Works() => Assert.Equal(2, _c.Divide(4, 2));
    [Fact] public void Failing_Assertion() => Assert.Equal(5, _c.Add(2, 2));
    [Fact(Skip = "not ready")] public void Skipped_Test() { }

    [Theory]
    [InlineData(1, 1, 2)]
    [InlineData(2, 3, 5)]
    [InlineData(10, 22, 33)]  // wrong on purpose
    public void Add_Theory(int a, int b, int expected) => Assert.Equal(expected, _c.Add(a, b));
}
