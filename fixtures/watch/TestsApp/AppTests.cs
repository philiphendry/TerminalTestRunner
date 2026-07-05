using Xunit;
using LibF;

namespace TestsApp;

public class AppTests
{
    // F.Compute(x) = E.Scale = D.Combine = B.Twice(x) + C.Inc(x) = 2x + (x+1) = 3x + 1
    [Fact] public void Compute_Zero() => Assert.Equal(1, F.Compute(0));
    [Fact] public void Compute_One() => Assert.Equal(4, F.Compute(1));
    [Fact] public void Compute_Two() => Assert.Equal(7, F.Compute(2));
    [Fact] public void Compute_Ten() => Assert.Equal(31, F.Compute(10));
}
