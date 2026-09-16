using System.Text;

namespace StemCode.Infrastructure.Storage;

internal interface ICodebaseEmbeddingProvider : IDisposable
{
    CodebaseEmbeddingMetadata Metadata { get; }

    Task<sbyte[]> CreateQueryEmbeddingAsync(
        string workspaceRoot,
        string query,
        CancellationToken cancellationToken);

    Task<sbyte[]> CreatePassageEmbeddingAsync(
        string workspaceRoot,
        CodebaseEmbeddingPassage passage,
        CancellationToken cancellationToken);
}

internal sealed record CodebaseEmbeddingMetadata(
    string ModelId,
    string ModelName,
    string ModelRevision,
    string ModelUrl,
    string Quantization,
    int Dimensions);

internal sealed record CodebaseEmbeddingPassage(
    string Path,
    string Language,
    string Content,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> Owners,
    IReadOnlyList<CodebaseIndexedSemanticSymbolDocument> SemanticSymbols,
    IReadOnlyList<CodebaseIndexedCallEdgeDocument> Calls)
{
    private const int MaxInputCharacters = 24_000;

    public string ToModelInputText()
    {
        StringBuilder builder = new();
        builder.Append("path: ").Append(Path).Append('\n');
        builder.Append("language: ").Append(Language).Append('\n');

        if (Symbols.Count > 0)
        {
            builder.Append("symbols: ").AppendJoin(' ', Symbols).Append('\n');
        }

        if (SemanticSymbols.Count > 0)
        {
            builder.Append("semantic symbols: ");
            foreach (CodebaseIndexedSemanticSymbolDocument symbol in SemanticSymbols.Take(64))
            {
                builder.Append(symbol.Kind)
                    .Append(' ')
                    .Append(symbol.Name);
                if (!string.IsNullOrWhiteSpace(symbol.ContainerName))
                {
                    builder.Append(" in ").Append(symbol.ContainerName);
                }

                builder.Append("; ");
            }

            builder.Append('\n');
        }

        if (Owners.Count > 0)
        {
            builder.Append("owners: ").AppendJoin(' ', Owners).Append('\n');
        }

        if (Calls.Count > 0)
        {
            builder.Append("calls: ");
            foreach (CodebaseIndexedCallEdgeDocument call in Calls.Take(64))
            {
                builder.Append(call.CallerSymbol)
                    .Append(" -> ")
                    .Append(call.CalleeSymbol)
                    .Append("; ");
            }

            builder.Append('\n');
        }

        builder.Append("content:\n");
        int remaining = Math.Max(0, MaxInputCharacters - builder.Length);
        builder.Append(Content.AsSpan(0, Math.Min(Content.Length, remaining)));
        return builder.ToString();
    }
}
