using System;
using System.Collections.Generic;
using System.Linq;

namespace Contoso.Sample.Networking;

/// <summary>
/// The long (~300 line) fixture source for the fake `files` scenario (brief M1). Fabricated
/// failure stack traces reference deep lines in this file (e.g. line 193, line 232) so the 'o'
/// modal opens scrolled to a highlighted line well inside a large file. The logic is plausible
/// but inert — nothing here ever runs; it exists purely to be read in the modal.
/// </summary>
public sealed class PaymentProcessor
{
    private readonly IReadOnlyDictionary<string, decimal> _fxRates;
    private readonly List<PaymentRecord> _ledger = new();
    private readonly decimal _feePercent;

    public PaymentProcessor(IReadOnlyDictionary<string, decimal> fxRates, decimal feePercent = 0.029m)
    {
        _fxRates = fxRates ?? throw new ArgumentNullException(nameof(fxRates));
        if (feePercent is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(feePercent), "fee must be a fraction in [0,1]");
        _feePercent = feePercent;
    }

    public IReadOnlyList<PaymentRecord> Ledger => _ledger;

    // --- Currency conversion -------------------------------------------------

    public decimal Convert(decimal amount, string from, string to)
    {
        if (amount < 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), "amount must be non-negative");
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return amount;

        var fromRate = RateFor(from);
        var toRate = RateFor(to);
        var usd = amount / fromRate;
        return Math.Round(usd * toRate, 2, MidpointRounding.ToEven);
    }

    private decimal RateFor(string currency)
    {
        if (!_fxRates.TryGetValue(currency.ToUpperInvariant(), out var rate))
            throw new KeyNotFoundException($"no FX rate configured for '{currency}'");
        if (rate <= 0m)
            throw new InvalidOperationException($"FX rate for '{currency}' must be positive");
        return rate;
    }

    // --- Fees ----------------------------------------------------------------

    public decimal FeeFor(decimal amount)
    {
        if (amount < 0m)
            throw new ArgumentOutOfRangeException(nameof(amount));
        var raw = amount * _feePercent;
        return Math.Round(raw, 2, MidpointRounding.AwayFromZero);
    }

    public decimal NetAfterFee(decimal amount) => amount - FeeFor(amount);

    // --- Validation ----------------------------------------------------------

    public ValidationResult ValidateCard(CardDetails card)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(card.Number))
            errors.Add("card number is required");
        else if (!LuhnValid(card.Number))
            errors.Add("card number failed the Luhn check");

        if (card.ExpiryMonth is < 1 or > 12)
            errors.Add("expiry month out of range");

        if (card.ExpiryYear < 2000)
            errors.Add("expiry year looks wrong");

        if (string.IsNullOrWhiteSpace(card.Cvv) || card.Cvv.Length is < 3 or > 4)
            errors.Add("CVV must be 3 or 4 digits");

        return errors.Count == 0
            ? ValidationResult.Ok()
            : ValidationResult.Fail(errors);
    }

    private static bool LuhnValid(string number)
    {
        var digits = number.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (digits.Length < 12) return false;
        var sum = 0;
        var alt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i];
            if (alt)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            alt = !alt;
        }
        return sum % 10 == 0;
    }

    // --- Charging ------------------------------------------------------------

    public PaymentRecord Charge(CardDetails card, decimal amount, string currency)
    {
        var validation = ValidateCard(card);
        if (!validation.IsValid)
            throw new PaymentException("card validation failed: " + string.Join("; ", validation.Errors));

        var usd = Convert(amount, currency, "USD");
        var fee = FeeFor(usd);
        var record = new PaymentRecord(
            Id: Guid.Empty,
            CardLast4: Last4(card.Number),
            Currency: currency.ToUpperInvariant(),
            Amount: amount,
            AmountUsd: usd,
            Fee: fee,
            Status: PaymentStatus.Captured);

        _ledger.Add(record);
        return record;
    }

    public PaymentRecord Refund(Guid paymentId, decimal amount)
    {
        var original = _ledger.FirstOrDefault(r => r.Id == paymentId)
                       ?? throw new PaymentException($"no payment with id {paymentId}");
        if (amount > original.Amount)
            throw new PaymentException("refund exceeds original charge");

        var refund = original with
        {
            Amount = -amount,
            Status = PaymentStatus.Refunded,
        };
        _ledger.Add(refund);
        return refund;
    }

    private static string Last4(string number)
    {
        var digits = new string(number.Where(char.IsDigit).ToArray());
        return digits.Length <= 4 ? digits : digits[^4..];
    }

    // --- Reporting -----------------------------------------------------------

    public decimal TotalCaptured(string currency)
    {
        return _ledger
            .Where(r => r.Status == PaymentStatus.Captured)
            .Where(r => string.Equals(r.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .Sum(r => r.Amount);
    }

    public decimal TotalFees()
    {
        return _ledger
            .Where(r => r.Status == PaymentStatus.Captured)
            .Sum(r => r.Fee);
    }

    public IEnumerable<PaymentRecord> Failed()
    {
        return _ledger.Where(r => r.Status == PaymentStatus.Declined);
    }

    public IReadOnlyDictionary<string, decimal> TotalsByCurrency()
    {
        return _ledger
            .Where(r => r.Status == PaymentStatus.Captured)
            .GroupBy(r => r.Currency)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
    }

    // --- Settlement (the deep referenced region) -----------------------------

    public SettlementBatch Settle(DateOnly asOf)
    {
        var captured = _ledger
            .Where(r => r.Status == PaymentStatus.Captured)
            .ToList();

        if (captured.Count == 0)
            throw new PaymentException("nothing to settle");   // line 193 — referenced frame

        var gross = captured.Sum(r => r.AmountUsd);
        var fees = captured.Sum(r => r.Fee);
        var net = gross - fees;

        var lines = captured
            .GroupBy(r => r.Currency)
            .Select(g => new SettlementLine(
                Currency: g.Key,
                Count: g.Count(),
                Gross: g.Sum(r => r.AmountUsd),
                Fees: g.Sum(r => r.Fee)))
            .OrderByDescending(l => l.Gross)
            .ToList();

        return new SettlementBatch(asOf, gross, fees, net, lines);
    }

    public bool CanSettle() => _ledger.Any(r => r.Status == PaymentStatus.Captured);

    // --- Risk scoring --------------------------------------------------------

    public int RiskScore(CardDetails card, decimal amount, string currency)
    {
        var score = 0;
        if (amount > 1000m) score += 20;
        if (amount > 10000m) score += 40;

        if (!string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase))
            score += 10;

        var validation = ValidateCard(card);
        if (!validation.IsValid)
            score += 50;

        if (card.ExpiryYear == DateTime.UtcNow.Year && card.ExpiryMonth <= DateTime.UtcNow.Month)
            score += 15;

        return Math.Clamp(score, 0, 100);   // line 232 — referenced frame
    }

    public bool IsHighRisk(CardDetails card, decimal amount, string currency) =>
        RiskScore(card, amount, currency) >= 60;
}

public sealed record CardDetails(string Number, int ExpiryMonth, int ExpiryYear, string Cvv);

public enum PaymentStatus
{
    Pending,
    Captured,
    Declined,
    Refunded,
}

public sealed record PaymentRecord(
    Guid Id,
    string CardLast4,
    string Currency,
    decimal Amount,
    decimal AmountUsd,
    decimal Fee,
    PaymentStatus Status);

public sealed record SettlementLine(string Currency, int Count, decimal Gross, decimal Fees);

public sealed record SettlementBatch(
    DateOnly AsOf,
    decimal Gross,
    decimal Fees,
    decimal Net,
    IReadOnlyList<SettlementLine> Lines);

public sealed class ValidationResult
{
    private ValidationResult(bool ok, IReadOnlyList<string> errors)
    {
        IsValid = ok;
        Errors = errors;
    }

    public bool IsValid { get; }
    public IReadOnlyList<string> Errors { get; }

    public static ValidationResult Ok() => new(true, Array.Empty<string>());
    public static ValidationResult Fail(IReadOnlyList<string> errors) => new(false, errors);
}

public sealed class PaymentException : Exception
{
    public PaymentException(string message) : base(message) { }
    public PaymentException(string message, Exception inner) : base(message, inner) { }
}
