using Xunit;
using LibX;

namespace TestsX;

public class XTests
{
    [Fact] public void Neg_Positive() => Assert.Equal(-5, X.Neg(5));
    [Fact] public void Neg_Negative() => Assert.Equal(4, X.Neg(-4));
    [Fact] public void Neg_Zero() => Assert.Equal(0, X.Neg(0));
}
