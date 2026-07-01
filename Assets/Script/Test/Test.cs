using NUnit.Framework;

public class CalculatorTest
{
    [Test]
    public void AddTwoNumbers()
    {
        int expected = 5;
        int actual = Calculator.Add(2, 3);

        Assert.AreEqual(expected, actual);
    }
}

public class Calculator
{
    public static int Add(int a, int b)
    {
        return a + b;
    }
}