using System.Globalization;

namespace StemCode.Application.Models;

public static class ContextSizeOptions
{
    public const int MinimumTokens = 2_048;

    public static int Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string normalized = value.Trim();
        long tokens;

        if (normalized.EndsWith("k", StringComparison.OrdinalIgnoreCase))
        {
            string thousandsText = normalized[..^1].Trim();
            if (!long.TryParse(
                    thousandsText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long thousands) ||
                thousands <= 0 ||
                thousands > int.MaxValue / 1_000)
            {
                throw CreateInvalidValueException(value);
            }

            tokens = thousands * 1_000;
        }
        else if (!long.TryParse(
                     normalized,
                     NumberStyles.None,
                     CultureInfo.InvariantCulture,
                     out tokens) ||
                 tokens <= 0)
        {
            throw CreateInvalidValueException(value);
        }

        if (tokens < MinimumTokens || tokens > int.MaxValue)
        {
            throw CreateInvalidValueException(value);
        }

        return (int)tokens;
    }

    private static ArgumentException CreateInvalidValueException(string value)
    {
        return new ArgumentException(
            $"Invalid context size '{value.Trim()}'. Use 8k, 32k, 64k, 125k, 256k, or a token count of at least {MinimumTokens.ToString("N0", CultureInfo.InvariantCulture)}.",
            nameof(value));
    }
}
