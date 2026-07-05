using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Matrix.MSTestSdk;

[TestClass]
public class WidgetTests
{
    [TestMethod] public void Creates() => Assert.AreEqual(4, 2 + 2);
    [TestMethod] public void Breaks() => Assert.AreEqual(5, 2 + 2);  // fails on purpose

    [DataTestMethod]
    [DataRow(2, 4)]
    [DataRow(5, 10)]
    public void Doubles(int n, int expected) => Assert.AreEqual(expected, n * 2);
}
