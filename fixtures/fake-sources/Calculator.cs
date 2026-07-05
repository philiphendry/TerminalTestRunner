using System;

namespace Contoso.Sample.Calculations;

/// <summary>
/// A tiny arithmetic helper used by the fake `files` scenario so the 'o' modal has a real,
/// syntax-highlightable source file to open (plan §6.5 / brief M1). The line numbers referenced
/// by the fabricated failure stack traces point at real lines in this file.
/// </summary>
public sealed class Calculator
{
    /// <summary>Adds two integers. (The fake failure claims this returned 41 instead of 42.)</summary>
    public int Add(int a, int b)
    {
        checked
        {
            return a + b;   // line 17 — referenced by the fabricated AssertEqual failure
        }
    }

    public int Subtract(int a, int b) => a - b;

    public int Multiply(int a, int b) => a * b;

    public int Divide(int a, int b)
    {
        if (b == 0)
            throw new DivideByZeroException("cannot divide by zero");
        return a / b;
    }

    public double Average(ReadOnlySpan<int> values)
    {
        if (values.Length == 0)
            throw new ArgumentException("values must not be empty", nameof(values));
        long sum = 0;
        foreach (var v in values)
            sum += v;
        return (double)sum / values.Length;
    }
}
