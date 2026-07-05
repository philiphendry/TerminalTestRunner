using Xunit;
using LibA;

namespace TestsCore;

public class CalcTests
{
    [Fact] public void Add_TwoPositives() => Assert.Equal(3, Calc.Add(1, 2));
    [Fact] public void Add_WithZero() => Assert.Equal(5, Calc.Add(5, 0));
    [Fact] public void Add_Negatives() => Assert.Equal(-3, Calc.Add(-1, -2));
    [Fact] public void Add_Commutes() => Assert.Equal(Calc.Add(2, 7), Calc.Add(7, 2));
    [Fact] public void Add_LargeValues() => Assert.Equal(2000, Calc.Add(1000, 1000));
    // AC2 (paste inside the class): [Fact] public void Brand_New() => Assert.True(true);
}
