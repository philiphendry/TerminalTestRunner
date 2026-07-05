using Xunit;

namespace Matrix.MultiTfm;

public class PortableTests
{
    [Fact] public void Works_Everywhere() => Assert.True(2 > 1);
    [Fact] public void Also_Works() => Assert.Equal(4, 2 + 2);
}
