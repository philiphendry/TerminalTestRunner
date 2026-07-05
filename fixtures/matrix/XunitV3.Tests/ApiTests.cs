using Xunit;

namespace Matrix.XunitV3;

public class ApiTests
{
    [Fact] public void Get_Succeeds() => Assert.True(1 + 1 == 2);
    [Fact] public void Post_Succeeds() => Assert.Equal("ok", "ok");
    [Fact] public void Failing() => Assert.Equal(3, 1 + 1);
    [Theory][InlineData(2)][InlineData(4)] public void Even(int n) => Assert.True(n % 2 == 0);
}
