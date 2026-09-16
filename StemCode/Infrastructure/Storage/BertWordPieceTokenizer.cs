using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StemCode.Infrastructure.Storage;

internal sealed class BertWordPieceTokenizer
{
    private const int PadTokenId = 0;
    private const int UnknownTokenId = 100;
    private const int ClassifierTokenId = 101;
    private const int SeparatorTokenId = 102;
    private const int MaxInputCharsPerWord = 100;
    private const string ContinuingSubwordPrefix = "##";

    private readonly FrozenDictionary<string, int> _vocab;

    private BertWordPieceTokenizer(FrozenDictionary<string, int> vocab)
    {
        _vocab = vocab;
    }

    public static async Task<BertWordPieceTokenizer> LoadAsync(
        string tokenizerJsonPath,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(tokenizerJsonPath);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        JsonElement vocabElement = document.RootElement
            .GetProperty("model")
            .GetProperty("vocab");

        Dictionary<string, int> vocab = new(StringComparer.Ordinal);
        foreach (JsonProperty item in vocabElement.EnumerateObject())
        {
            vocab[item.Name] = item.Value.GetInt32();
        }

        return new BertWordPieceTokenizer(vocab.ToFrozenDictionary(StringComparer.Ordinal));
    }

    public BertTokenizedInput Encode(
        string text,
        int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 3);

        long[] inputIds = new long[maxLength];
        long[] attentionMask = new long[maxLength];
        long[] tokenTypeIds = new long[maxLength];
        int tokenIndex = 0;

        inputIds[tokenIndex] = ClassifierTokenId;
        attentionMask[tokenIndex] = 1;
        tokenIndex++;

        int maxWordPieceCount = maxLength - 2;
        foreach (string token in BasicTokenize(text))
        {
            foreach (int tokenId in WordPieceTokenize(token))
            {
                if (tokenIndex > maxWordPieceCount)
                {
                    inputIds[tokenIndex] = SeparatorTokenId;
                    attentionMask[tokenIndex] = 1;
                    return new BertTokenizedInput(inputIds, attentionMask, tokenTypeIds, tokenIndex + 1);
                }

                inputIds[tokenIndex] = tokenId;
                attentionMask[tokenIndex] = 1;
                tokenIndex++;
            }
        }

        inputIds[tokenIndex] = SeparatorTokenId;
        attentionMask[tokenIndex] = 1;
        tokenIndex++;

        for (int index = tokenIndex; index < inputIds.Length; index++)
        {
            inputIds[index] = PadTokenId;
        }

        return new BertTokenizedInput(inputIds, attentionMask, tokenTypeIds, tokenIndex);
    }

    private IEnumerable<string> BasicTokenize(string text)
    {
        StringBuilder token = new();
        foreach (char character in NormalizeText(text))
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                foreach (string flushed in FlushToken(token))
                {
                    yield return flushed;
                }

                continue;
            }

            if (IsPunctuation(character))
            {
                foreach (string flushed in FlushToken(token))
                {
                    yield return flushed;
                }

                yield return character.ToString();
                continue;
            }

            token.Append(character);
        }

        foreach (string flushed in FlushToken(token))
        {
            yield return flushed;
        }
    }

    private IEnumerable<int> WordPieceTokenize(string token)
    {
        if (token.Length == 0)
        {
            yield break;
        }

        if (token.Length > MaxInputCharsPerWord)
        {
            yield return UnknownTokenId;
            yield break;
        }

        int start = 0;
        List<int> subTokens = [];
        while (start < token.Length)
        {
            int end = token.Length;
            int? currentTokenId = null;
            while (start < end)
            {
                string substring = token[start..end];
                if (start > 0)
                {
                    substring = ContinuingSubwordPrefix + substring;
                }

                if (_vocab.TryGetValue(substring, out int tokenId))
                {
                    currentTokenId = tokenId;
                    break;
                }

                end--;
            }

            if (currentTokenId is null)
            {
                yield return UnknownTokenId;
                yield break;
            }

            subTokens.Add(currentTokenId.Value);
            start = end;
        }

        foreach (int tokenId in subTokens)
        {
            yield return tokenId;
        }
    }

    private static string NormalizeText(string text)
    {
        string lower = text.ToLowerInvariant();
        string decomposed = lower.Normalize(NormalizationForm.FormD);
        StringBuilder builder = new(decomposed.Length);
        foreach (char character in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static IEnumerable<string> FlushToken(StringBuilder token)
    {
        if (token.Length == 0)
        {
            yield break;
        }

        yield return token.ToString();
        token.Clear();
    }

    private static bool IsPunctuation(char character)
    {
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
        return category is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation
            or UnicodeCategory.MathSymbol
            or UnicodeCategory.CurrencySymbol
            or UnicodeCategory.ModifierSymbol
            or UnicodeCategory.OtherSymbol;
    }
}

internal sealed record BertTokenizedInput(
    long[] InputIds,
    long[] AttentionMask,
    long[] TokenTypeIds,
    int TokenCount);
