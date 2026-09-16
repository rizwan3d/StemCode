using StemCode.Application.Abstractions;
using StemCode.Application.Tools.Models;
using StemCode.Application.Utilities;
using StemCode.Infrastructure.Workspaces;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Frozen;
using System.Threading;

namespace StemCode.Infrastructure.Storage;

internal sealed class WorkspaceCodebaseIndexService : ICodebaseIndexService, IDisposable
{
    private const int CurrentIndexVersion = 5;
    private const int MaxParallelIndexingTasks = 8;
    private const int MaxParallelSearchTasks = 8;
    private const int MaxIndexedFiles = 5_000;
    private const int MaxIndexFileBytes = 262_144;
    private const int MaxEmbeddingTokensPerQuery = 256;
    private const int MaxSymbolsPerFile = 80;
    private const int MaxSemanticSymbolsPerFile = 128;
    private const int MaxDependenciesPerFile = 64;
    private const int MaxCallsPerFile = 160;
    private const int MaxResolvedPathsPerDependency = 12;
    private const int MaxGraphItemsPerMatch = 6;
    private const int MaxSnippetsPerMatch = 3;
    private const int MaxSnippetCharacters = 220;
    private const int MaxStatusSamples = 8;
    private const int MaxSignatureCharacters = 160;

    private static readonly string[] IgnoreFilePaths =
    [
        Path.Combine(".stemcode", ".stemcodeignore")
    ];

    private static readonly string[] CodeOwnersCandidatePaths =
    [
        ".github/CODEOWNERS",
        "CODEOWNERS",
        "docs/CODEOWNERS"
    ];

    private static readonly HashSet<string> DefaultIgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".hg",
        ".idea",
        ".svn",
        ".vs",
        "bin",
        "build",
        "coverage",
        "dist",
        "node_modules",
        "obj",
        "packages",
        "publish",
        "TestResults"
    };

    private static readonly string[] DefaultIgnoredPathPrefixes =
    [
        ".stemcode/cache/",
        ".stemcode/logs/",
        ".stemcode/sessions/",
        ".stemcode/temp/",
        ".stemcode/tmp/"
    ];

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z",
        ".a",
        ".avi",
        ".bmp",
        ".class",
        ".dll",
        ".dmg",
        ".doc",
        ".docx",
        ".exe",
        ".gif",
        ".gz",
        ".ico",
        ".jar",
        ".jpeg",
        ".jpg",
        ".lockb",
        ".mov",
        ".mp3",
        ".mp4",
        ".o",
        ".pdb",
        ".pdf",
        ".png",
        ".so",
        ".sqlite",
        ".tar",
        ".webp",
        ".woff",
        ".woff2",
        ".zip"
    };

    private static readonly HashSet<string> CallableControlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "catch",
        "foreach",
        "for",
        "if",
        "nameof",
        "new",
        "return",
        "sizeof",
        "switch",
        "throw",
        "typeof",
        "using",
        "while"
    };

    private static readonly HashSet<string> TypeLikeSymbolKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "class",
        "enum",
        "interface",
        "record",
        "struct"
    };

    private static readonly Regex NamespaceRegex = new(@"\bnamespace\s+([A-Za-z_][\w\.]*)", RegexOptions.Compiled);
    private static readonly Regex TypeRegex = new(@"\b(class|interface|record|struct|enum)\s+([A-Za-z_][\w]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NamedFunctionRegex = new(@"^\s*(?:export\s+)?(?:async\s+)?(?:function|def|fn)\s+([A-Za-z_][\w]*)\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AssignedFunctionRegex = new(@"^\s*(?:export\s+)?(?:const|let|var)\s+([A-Za-z_][\w]*)\s*=\s*(?:async\s+)?(?:function\s*\(|\()", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex JSImportFromRegex = new(@"^\s*(?:import|export)\b.*?\bfrom\s*[""']([^""']+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex JSImportBareRegex = new(@"^\s*import\s*[""']([^""']+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RequireRegex = new(@"\brequire\(\s*[""']([^""']+)[""']\s*\)", RegexOptions.Compiled);
    private static readonly Regex CSharpUsingRegex = new(@"^\s*(?:global\s+)?using\s+([A-Za-z_][\w\.]*)\s*;", RegexOptions.Compiled);
    private static readonly Regex PythonImportRegex = new(@"^\s*import\s+([A-Za-z_][\w\.]*)", RegexOptions.Compiled);
    private static readonly Regex PythonFromImportRegex = new(@"^\s*from\s+([A-Za-z_][\w\.]*)\s+import\b", RegexOptions.Compiled);
    private static readonly Regex JavaImportRegex = new(@"^\s*import\s+([A-Za-z_][\w\.]*)\s*;", RegexOptions.Compiled);
    private static readonly Regex GoInlineImportRegex = new(@"^\s*import\s+""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex GoImportEntryRegex = new(@"^\s*""([^""]+)""\s*$", RegexOptions.Compiled);
    private static readonly Regex ProjectReferenceRegex = new(@"<ProjectReference\s+Include=""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PackageReferenceRegex = new(@"<PackageReference\s+Include=""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CallCandidateRegex = new(@"(?<![\w\.])([A-Za-z_][\w]*)\s*\(", RegexOptions.Compiled);

    private readonly TimeProvider _timeProvider;
    private readonly IWorkspaceRootProvider _workspaceRootProvider;
    private readonly ICodebaseEmbeddingProvider _embeddingProvider;
    private readonly bool _ownsEmbeddingProvider;

    private readonly SemaphoreSlim _rebuildGate = new(1, 1);
    private readonly FileSystemWatcher? _watcher;
    private int _dirtyGeneration;
    private volatile bool _watchHealthy = true;
    private CodebaseIndexSnapshot? _snapshot;

    public WorkspaceCodebaseIndexService(
        IWorkspaceRootProvider workspaceRootProvider,
        TimeProvider? timeProvider = null)
        : this(
            workspaceRootProvider,
            new TinyE5OnnxCodebaseEmbeddingProvider(),
            ownsEmbeddingProvider: true,
            timeProvider)
    {
    }

    internal WorkspaceCodebaseIndexService(
        IWorkspaceRootProvider workspaceRootProvider,
        ICodebaseEmbeddingProvider embeddingProvider,
        TimeProvider? timeProvider = null)
        : this(
            workspaceRootProvider,
            embeddingProvider,
            ownsEmbeddingProvider: false,
            timeProvider)
    {
    }

    private WorkspaceCodebaseIndexService(
        IWorkspaceRootProvider workspaceRootProvider,
        ICodebaseEmbeddingProvider embeddingProvider,
        bool ownsEmbeddingProvider,
        TimeProvider? timeProvider)
    {
        _workspaceRootProvider = workspaceRootProvider;
        _embeddingProvider = embeddingProvider;
        _ownsEmbeddingProvider = ownsEmbeddingProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _watcher = TryCreateWatcher(GetWorkspaceRoot());
    }

    public void Dispose()
    {
        _rebuildGate.Dispose();
        _watcher?.Dispose();
        if (_ownsEmbeddingProvider)
        {
            _embeddingProvider.Dispose();
        }
    }

    public async Task<CodebaseIndexBuildResult> BuildAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // An explicit build always rebuilds (the single-flight gate serializes it with
        // any concurrent search/list rebuild). The rebuilt snapshot is published atomically.
        await _rebuildGate.WaitAsync(cancellationToken);
        try
        {
            RebuildOutcome outcome = await RebuildAsync(force, cancellationToken);
            Interlocked.Exchange(ref _snapshot, outcome.Snapshot);
            return outcome.BuildResult;
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    private async Task<RebuildOutcome> RebuildAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Stopwatch stopwatch = Stopwatch.StartNew();
        string workspaceRoot = GetWorkspaceRoot();
        string indexPath = GetIndexPath(workspaceRoot);
        WorkspaceScan scan = ScanWorkspace(workspaceRoot, cancellationToken);
        IReadOnlyList<CodeOwnersRule> ownershipRules = LoadOwnershipRules(workspaceRoot);
        CodebaseIndexDocument? existingIndex = await LoadIndexAsync(
            indexPath,
            cancellationToken);
        Dictionary<string, CodebaseIndexedFileDocument> existingFiles = (existingIndex?.Files ?? [])
            .Where(static file => file is not null && !string.IsNullOrWhiteSpace(file.Path))
            .ToDictionary(static file => file.Path, StringComparer.OrdinalIgnoreCase);

        CodebaseIndexCandidate[] candidates = scan.Files
            .Take(MaxIndexedFiles)
            .ToArray();
        CandidateIndexResult[] candidateResults = new CandidateIndexResult[candidates.Length];
        ParallelOptions parallelOptions = new()
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = GetMaxParallelism(MaxParallelIndexingTasks)
        };
        await Parallel.ForEachAsync(
            Enumerable.Range(0, candidates.Length),
            parallelOptions,
            async (index, ct) =>
            {
                CodebaseIndexCandidate candidate = candidates[index];
                string[] owners = ResolveOwners(candidate.RelativePath, ownershipRules);
                if (!force &&
                    existingFiles.TryGetValue(candidate.RelativePath, out CodebaseIndexedFileDocument? existingFile) &&
                    HasSameMetadata(candidate, existingFile) &&
                    OwnersEqual(existingFile.Owners, owners))
                {
                    // Clone the reused file into brand-new objects so the published,
                    // resident snapshot is never mutated by the rebuild path.
                    candidateResults[index] = CandidateIndexResult.Reused(
                        CloneFileForReuse(existingFile, owners),
                        owners);
                    return;
                }

                CodebaseIndexedFileDocument? indexedFile = await IndexFileAsync(
                    candidate,
                    owners,
                    ct);
                candidateResults[index] = indexedFile is null
                    ? CandidateIndexResult.Skipped()
                    : CandidateIndexResult.Indexed(indexedFile, existingFiles.ContainsKey(candidate.RelativePath));
            });

        List<CodebaseIndexedFileDocument> indexedFiles = new(candidates.Length);
        int added = 0;
        int updated = 0;
        int reused = 0;
        int skipped = scan.SkippedFileCount;

        foreach (CandidateIndexResult result in candidateResults)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (result.ReusedFile is not null)
            {
                indexedFiles.Add(result.ReusedFile);
                reused++;
                continue;
            }

            if (result.IndexedFile is not null)
            {
                indexedFiles.Add(result.IndexedFile);
                if (result.WasExistingFile)
                {
                    updated++;
                }
                else
                {
                    added++;
                }

                continue;
            }

            skipped++;
        }

        if (scan.Files.Count > MaxIndexedFiles)
        {
            skipped += scan.Files.Count - MaxIndexedFiles;
        }

        // Operates only on the freshly built, not-yet-published file objects.
        ResolveRepositoryMetadata(indexedFiles);

        HashSet<string> currentPaths = scan.Files
            .Take(MaxIndexedFiles)
            .Select(static file => file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int removed = existingFiles.Keys.Count(path => !currentPaths.Contains(path));

        DateTimeOffset builtAtUtc = _timeProvider.GetUtcNow();
        CodebaseIndexDocument index = new()
        {
            Version = CurrentIndexVersion,
            EmbeddingModelId = _embeddingProvider.Metadata.ModelId,
            EmbeddingModelName = _embeddingProvider.Metadata.ModelName,
            EmbeddingModelRevision = _embeddingProvider.Metadata.ModelRevision,
            EmbeddingModelUrl = _embeddingProvider.Metadata.ModelUrl,
            EmbeddingQuantization = _embeddingProvider.Metadata.Quantization,
            EmbeddingDimensions = _embeddingProvider.Metadata.Dimensions,
            BuiltAtUtc = builtAtUtc,
            OwnershipRuleCount = ownershipRules.Count,
            Files = indexedFiles
                .OrderBy(static file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        await SaveIndexAsync(
            indexPath,
            index,
            cancellationToken);

        stopwatch.Stop();
        int dirtyGeneration = Volatile.Read(ref _dirtyGeneration);
        CodebaseIndexSnapshot snapshot = BuildSnapshot(index, dirtyGeneration);
        CodebaseIndexStats stats = snapshot.Stats;
        CodebaseIndexBuildResult buildResult = new CodebaseIndexBuildResult(
            ToWorkspaceRelativePath(workspaceRoot, indexPath),
            builtAtUtc,
            index.Files.Count,
            added,
            updated,
            removed,
            reused,
            skipped,
            stopwatch.ElapsedMilliseconds,
            stats,
            CreateBuildWarnings(scan, ownershipRules.Count, skipped));
        return new RebuildOutcome(snapshot, buildResult);
    }

    public async Task<CodebaseIndexStatusResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string workspaceRoot = GetWorkspaceRoot();
        string indexPath = GetIndexPath(workspaceRoot);
        WorkspaceScan scan = ScanWorkspace(workspaceRoot, cancellationToken);
        CodebaseIndexDocument? index = await LoadIndexAsync(
            indexPath,
            cancellationToken);

        if (index is null)
        {
            string[] sampleNewFiles = scan.Files
                .Select(static file => file.RelativePath)
                .Take(MaxStatusSamples)
                .ToArray();
            return new CodebaseIndexStatusResult(
                ToWorkspaceRelativePath(workspaceRoot, indexPath),
                Exists: false,
                IsStale: scan.Files.Count > 0,
                BuiltAtUtc: null,
                IndexedFileCount: 0,
                WorkspaceFileCount: scan.Files.Count,
                NewFileCount: scan.Files.Count,
                ChangedFileCount: 0,
                DeletedFileCount: 0,
                SkippedFileCount: scan.SkippedFileCount,
                EmptyStats(),
                CreateStatusWarnings(
                    exists: false,
                    isStale: scan.Files.Count > 0,
                    newFileCount: scan.Files.Count,
                    changedFileCount: 0,
                    deletedFileCount: 0,
                    scan,
                    ownershipRuleCount: 0),
                SampleNewFiles: sampleNewFiles,
                SampleChangedFiles: [],
                SampleDeletedFiles: []);
        }

        Dictionary<string, CodebaseIndexedFileDocument> indexedFiles = index.Files
            .Where(static file => file is not null && !string.IsNullOrWhiteSpace(file.Path))
            .ToDictionary(static file => file.Path, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CodebaseIndexCandidate> workspaceFiles = scan.Files
            .ToDictionary(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase);

        string[] newFiles = workspaceFiles.Keys
            .Where(path => !indexedFiles.ContainsKey(path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] changedFiles = workspaceFiles.Values
            .Where(file => indexedFiles.TryGetValue(file.RelativePath, out CodebaseIndexedFileDocument? indexedFile) &&
                           !HasSameMetadata(file, indexedFile))
            .Select(static file => file.RelativePath)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] deletedFiles = indexedFiles.Keys
            .Where(path => !workspaceFiles.ContainsKey(path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        bool isStale = newFiles.Length > 0 || changedFiles.Length > 0 || deletedFiles.Length > 0;
        return new CodebaseIndexStatusResult(
            ToWorkspaceRelativePath(workspaceRoot, indexPath),
            Exists: true,
            IsStale: isStale,
            BuiltAtUtc: index.BuiltAtUtc,
            IndexedFileCount: index.Files.Count,
            WorkspaceFileCount: scan.Files.Count,
            NewFileCount: newFiles.Length,
            ChangedFileCount: changedFiles.Length,
            DeletedFileCount: deletedFiles.Length,
            SkippedFileCount: scan.SkippedFileCount,
            BuildStats(index),
            CreateStatusWarnings(
                exists: true,
                isStale,
                newFiles.Length,
                changedFiles.Length,
                deletedFiles.Length,
                scan,
                index.OwnershipRuleCount),
            SampleNewFiles: newFiles.Take(MaxStatusSamples).ToArray(),
            SampleChangedFiles: changedFiles.Take(MaxStatusSamples).ToArray(),
            SampleDeletedFiles: deletedFiles.Take(MaxStatusSamples).ToArray());
    }

    public async Task<CodebaseIndexSearchResult> SearchAsync(
        string query,
        int limit,
        bool includeSnippets,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        cancellationToken.ThrowIfCancellationRequested();

        // Capture the resident snapshot exactly once. If the workspace is unchanged since
        // it was built (dirty generation matches and the file watcher is healthy), no
        // workspace scan or rebuild happens on the search path at all.
        CodebaseIndexSnapshot? current = Volatile.Read(ref _snapshot);
        bool needsFresh = current is null ||
                          current.Generation != Volatile.Read(ref _dirtyGeneration) ||
                          !_watchHealthy ||
                          _watcher is null;
        FreshSnapshot fresh = needsFresh
            ? await EnsureFreshSnapshotAsync(force: false, cancellationToken)
            : new FreshSnapshot(current!, null);
        CodebaseIndexSnapshot snapshot = fresh.Snapshot;

        string workspaceRoot = GetWorkspaceRoot();
        string indexPath = GetIndexPath(workspaceRoot);
        string normalizedQuery = query.Trim();
        string[] queryTerms = Tokenize(normalizedQuery)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxEmbeddingTokensPerQuery)
            .ToArray();
        sbyte[] queryEmbedding = await _embeddingProvider.CreateQueryEmbeddingAsync(
            workspaceRoot,
            normalizedQuery,
            cancellationToken);
        int maxResults = Math.Clamp(limit, 1, 50);

        CodebaseIndexSearchMatch?[] scoredMatches = new CodebaseIndexSearchMatch?[snapshot.Files.Length];
        Parallel.For(
            0,
            snapshot.Files.Length,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = GetMaxParallelism(MaxParallelSearchTasks)
            },
            fileIndex =>
            {
                scoredMatches[fileIndex] = ScoreFile(
                    workspaceRoot,
                    snapshot.Files[fileIndex],
                    normalizedQuery,
                    queryTerms,
                    queryEmbedding,
                    includeSnippets: false,
                    snapshot.IncomingCallMap,
                    cancellationToken);
            });

        CodebaseIndexSearchMatch[] matches = scoredMatches
            .Where(static match => match is not null && match.Score > 0)
            .Select(static match => match!)
            .OrderByDescending(static match => match.Score)
            .ThenBy(static match => match.Path, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray();

        if (includeSnippets)
        {
            for (int matchIndex = 0; matchIndex < matches.Length; matchIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CodebaseIndexSearchMatch match = matches[matchIndex];
                matches[matchIndex] = match with
                {
                    Snippets = CreateSnippets(
                        workspaceRoot,
                        match.Path,
                        normalizedQuery,
                        queryTerms,
                        cancellationToken)
                };
            }
        }

        bool indexWasUpdated = fresh.BuildResult is not null;
        return new CodebaseIndexSearchResult(
            normalizedQuery,
            ToWorkspaceRelativePath(workspaceRoot, indexPath),
            indexWasUpdated,
            snapshot.Files.Length,
            snapshot.Stats,
            CreateQueryWarnings(indexWasUpdated, fresh.BuildResult),
            matches);
    }

    public async Task<CodebaseIndexListResult> ListAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Capture the resident snapshot exactly once; skip the per-search workspace scan
        // when the workspace has not changed since the snapshot was built.
        CodebaseIndexSnapshot? current = Volatile.Read(ref _snapshot);
        bool needsFresh = current is null ||
                          current.Generation != Volatile.Read(ref _dirtyGeneration) ||
                          !_watchHealthy ||
                          _watcher is null;
        FreshSnapshot fresh = needsFresh
            ? await EnsureFreshSnapshotAsync(force: false, cancellationToken)
            : new FreshSnapshot(current!, null);
        CodebaseIndexSnapshot snapshot = fresh.Snapshot;

        string workspaceRoot = GetWorkspaceRoot();
        string indexPath = GetIndexPath(workspaceRoot);
        string[] files = snapshot.Files
            .Select(static file => file.Path)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 10_000))
            .ToArray();

        bool indexWasUpdated = fresh.BuildResult is not null;
        return new CodebaseIndexListResult(
            ToWorkspaceRelativePath(workspaceRoot, indexPath),
            snapshot.Files.Length,
            files.Length,
            snapshot.Stats,
            CreateQueryWarnings(indexWasUpdated, fresh.BuildResult),
            files);
    }

    private CodebaseIndexSearchMatch ScoreFile(
        string workspaceRoot,
        CodebaseIndexedFileDocument file,
        string normalizedQuery,
        IReadOnlyList<string> queryTerms,
        IReadOnlyList<sbyte> queryEmbedding,
        bool includeSnippets,
        FrozenDictionary<string, CodebaseIndexedCallEdgeDocument[]> incomingCallMap,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string normalizedPath = file.Path.Replace('\\', '/');
        double similarity = CosineSimilarity(queryEmbedding, file.Embedding);
        double score = Math.Max(0, similarity);

        if (normalizedPath.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.08;
        }

        if (file.Symbols.Any(symbol => symbol.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)))
        {
            score += 0.05;
        }

        if (file.SemanticSymbols.Any(symbol =>
                symbol.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(symbol.Signature) &&
                 symbol.Signature.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)) ||
                queryTerms.Any(term =>
                    symbol.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(symbol.ContainerName) &&
                     symbol.ContainerName.Contains(term, StringComparison.OrdinalIgnoreCase)))))
        {
            score += 0.07;
        }

        if (file.Dependencies.Any(dependency =>
                dependency.Target.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                dependency.ResolvedPaths.Any(path => path.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))))
        {
            score += 0.04;
        }

        if (file.Calls.Any(call =>
                call.CalleeSymbol.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                call.CallerSymbol.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)))
        {
            score += 0.04;
        }

        if (file.Owners.Any(owner =>
                owner.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                queryTerms.Any(term => owner.Contains(term, StringComparison.OrdinalIgnoreCase))))
        {
            score += 0.03;
        }

        CodebaseIndexSnippet[] snippets = score <= 0 || !includeSnippets
            ? []
            : CreateSnippets(workspaceRoot, file.Path, normalizedQuery, queryTerms, cancellationToken);

        incomingCallMap.TryGetValue(file.Path, out CodebaseIndexedCallEdgeDocument[]? incomingCalls);

        return new CodebaseIndexSearchMatch(
            file.Path,
            file.Language,
            Math.Round(score * 100, 3),
            file.Symbols.Take(10).ToArray(),
            file.SemanticSymbols
                .Take(MaxGraphItemsPerMatch)
                .Select(ToSemanticSymbol)
                .ToArray(),
            file.Dependencies
                .Take(MaxGraphItemsPerMatch)
                .Select(ToDependency)
                .ToArray(),
            file.Calls
                .Take(MaxGraphItemsPerMatch)
                .Select(ToCallEdge)
                .ToArray(),
            (incomingCalls ?? [])
                .Take(MaxGraphItemsPerMatch)
                .Select(ToCallEdge)
                .ToArray(),
            file.Owners,
            snippets);
    }

    private static CodebaseIndexSnippet[] CreateSnippets(
        string workspaceRoot,
        string relativePath,
        string normalizedQuery,
        IReadOnlyList<string> queryTerms,
        CancellationToken cancellationToken)
    {
        string fullPath = WorkspacePath.Resolve(workspaceRoot, relativePath);
        if (!File.Exists(fullPath))
        {
            return [];
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(fullPath, Encoding.UTF8);
        }
        catch (Exception exception) when (IsFileSystemAccessException(exception) ||
                                          exception is DecoderFallbackException or InvalidDataException)
        {
            return [];
        }

        List<CodebaseIndexSnippet> snippets = [];
        for (int index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = lines[index].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            bool matches = line.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                queryTerms.Any(term => line.Contains(term, StringComparison.OrdinalIgnoreCase));
            if (!matches)
            {
                continue;
            }

            snippets.Add(new CodebaseIndexSnippet(
                index + 1,
                Truncate(line, MaxSnippetCharacters)));
            if (snippets.Count >= MaxSnippetsPerMatch)
            {
                break;
            }
        }

        return snippets.ToArray();
    }

    private static int GetMaxParallelism(int configuredMaximum)
    {
        return Math.Max(1, Math.Min(Environment.ProcessorCount, configuredMaximum));
    }

    private async Task<CodebaseIndexedFileDocument?> IndexFileAsync(
        CodebaseIndexCandidate candidate,
        string[] owners,
        CancellationToken cancellationToken)
    {
        string content;
        try
        {
            content = await File.ReadAllTextAsync(
                candidate.FullPath,
                Encoding.UTF8,
                cancellationToken);
        }
        catch (Exception exception) when (IsFileSystemAccessException(exception) ||
                                          exception is DecoderFallbackException or InvalidDataException)
        {
            return null;
        }

        string language = GetLanguage(candidate.RelativePath);
        FileSemanticAnalysis analysis = AnalyzeFile(candidate.RelativePath, language, content);
        string[] symbols = analysis.Symbols
            .Concat(analysis.SemanticSymbols.Select(static symbol => symbol.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSymbolsPerFile)
            .ToArray();
        CodebaseIndexedDependencyDocument[] dependencies = ExtractDependencies(
            candidate.RelativePath,
            language,
            content);
        sbyte[] embedding = await _embeddingProvider.CreatePassageEmbeddingAsync(
            GetWorkspaceRoot(),
            new CodebaseEmbeddingPassage(
                candidate.RelativePath,
                language,
                content,
                symbols,
                owners,
                analysis.SemanticSymbols,
                analysis.Calls),
            cancellationToken);

        return new CodebaseIndexedFileDocument
        {
            Path = candidate.RelativePath,
            Length = candidate.Length,
            LastWriteTimeUtc = candidate.LastWriteTimeUtc,
            Language = language,
            LineCount = CountLines(content),
            Symbols = symbols,
            SemanticSymbols = analysis.SemanticSymbols,
            Dependencies = dependencies,
            Owners = owners,
            Calls = analysis.Calls,
            Embedding = embedding
        };
    }

    private WorkspaceScan ScanWorkspace(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        WorkspaceIgnoreMatcher ignoreMatcher = WorkspaceIgnoreMatcher.Load(
            workspaceRoot,
            IgnoreFilePaths);
        List<CodebaseIndexCandidate> files = [];
        int skippedFileCount = 0;
        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(workspaceRoot);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directoryPath = pendingDirectories.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directoryPath);
            }
            catch (Exception exception) when (IsFileSystemAccessException(exception))
            {
                continue;
            }

            foreach (string entry in entries.Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (IsFileSystemAccessException(exception))
                {
                    continue;
                }

                bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
                string relativePath = WorkspacePath.ToRelativePath(workspaceRoot, entry);
                if (IsDefaultIgnoredPath(relativePath, isDirectory) ||
                    ignoreMatcher.IsIgnored(entry, isDirectory))
                {
                    if (!isDirectory)
                    {
                        skippedFileCount++;
                    }

                    continue;
                }

                if (isDirectory)
                {
                    if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pendingDirectories.Push(entry);
                    }

                    continue;
                }

                if (!TryCreateCandidate(entry, relativePath, out CodebaseIndexCandidate? candidate))
                {
                    skippedFileCount++;
                    continue;
                }

                files.Add(candidate!);
            }
        }

        return new WorkspaceScan(
            files.OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            skippedFileCount);
    }

    private static bool TryCreateCandidate(
        string fullPath,
        string relativePath,
        out CodebaseIndexCandidate? candidate)
    {
        candidate = null;
        string extension = Path.GetExtension(relativePath);
        if (BinaryExtensions.Contains(extension))
        {
            return false;
        }

        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(fullPath);
        }
        catch (Exception exception) when (IsFileSystemAccessException(exception))
        {
            return false;
        }

        if (fileInfo.Length <= 0 ||
            fileInfo.Length > MaxIndexFileBytes)
        {
            return false;
        }

        candidate = new CodebaseIndexCandidate(
            fullPath,
            relativePath.Replace('\\', '/'),
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc);
        return true;
    }

    private static bool HasSameMetadata(
        CodebaseIndexCandidate candidate,
        CodebaseIndexedFileDocument indexedFile)
    {
        return candidate.Length == indexedFile.Length &&
            candidate.LastWriteTimeUtc == indexedFile.LastWriteTimeUtc;
    }

    private static bool OwnersEqual(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<CodebaseIndexDocument?> LoadIndexAsync(
        string indexPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(indexPath))
        {
            return null;
        }

        try
        {
            await using FileStream stream = File.OpenRead(indexPath);
            CodebaseIndexDocument? document = await JsonSerializer.DeserializeAsync(
                stream,
                CodebaseIndexJsonContext.Default.CodebaseIndexDocument,
                cancellationToken);
            return document is { Version: CurrentIndexVersion } &&
                   HasCurrentEmbeddingMetadata(document, _embeddingProvider.Metadata)
                ? document
                : null;
        }
        catch (Exception exception) when (exception is JsonException ||
                                          IsFileSystemAccessException(exception))
        {
            return null;
        }
    }

    private static bool HasCurrentEmbeddingMetadata(
        CodebaseIndexDocument document,
        CodebaseEmbeddingMetadata metadata)
    {
        return string.Equals(document.EmbeddingModelId, metadata.ModelId, StringComparison.Ordinal) &&
               string.Equals(document.EmbeddingModelRevision, metadata.ModelRevision, StringComparison.Ordinal) &&
               string.Equals(document.EmbeddingQuantization, metadata.Quantization, StringComparison.Ordinal) &&
               document.EmbeddingDimensions == metadata.Dimensions;
    }

    private static async Task SaveIndexAsync(
        string indexPath,
        CodebaseIndexDocument index,
        CancellationToken cancellationToken)
    {
        string? parentDirectory = Path.GetDirectoryName(indexPath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        // Serialize to memory once, then persist. The preferred path is an atomic temp-file
        // rename (MoveFileEx / renameat2) so a crash/interrupt can never leave a half-written
        // index behind. Some restricted filesystems/sandboxes block rename/copy operations and
        // lock temp files, surfacing that as a sharing violation; in that case we fall back to a
        // truncating create-write of the already-serialized bytes (no temp file to be locked).
        await using var memory = new MemoryStream();
        await JsonSerializer.SerializeAsync(
            memory,
            index,
            CodebaseIndexJsonContext.Default.CodebaseIndexDocument,
            cancellationToken);
        await memory.WriteAsync("\n"u8.ToArray(), cancellationToken);
        await memory.FlushAsync(cancellationToken);
        byte[] bytes = memory.ToArray();

        string tempPath = string.Concat(
            indexPath,
            ".",
            Guid.NewGuid().ToString("N"),
            ".tmp");
        bool wroteViaTemp = false;
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
            File.Move(tempPath, indexPath, overwrite: true);
            wroteViaTemp = true;
        }
        catch (IOException)
        {
            await File.WriteAllBytesAsync(indexPath, bytes, cancellationToken);
        }
        finally
        {
            if (!wroteViaTemp && File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static FileSemanticAnalysis AnalyzeFile(
        string relativePath,
        string language,
        string content)
    {
        string[] lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        if (string.Equals(language, "python", StringComparison.Ordinal))
        {
            return AnalyzePythonFile(relativePath, lines);
        }

        List<string> rawSymbols = ExtractSymbols(content).ToList();
        List<CodebaseIndexedSemanticSymbolDocument> symbols = [];
        List<CodebaseIndexedCallEdgeDocument> calls = [];
        List<OpenScope> activeScopes = [];
        List<OpenScope> pendingScopes = [];
        int braceDepth = 0;

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string line = lines[lineIndex];
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            int lineNumber = lineIndex + 1;
            int leadingClosings = CountLeadingCharacters(trimmed, '}');
            if (leadingClosings > 0)
            {
                braceDepth = Math.Max(0, braceDepth - leadingClosings);
                CloseCompletedScopes(activeScopes, braceDepth, lineNumber - 1);
            }

            string? currentContainer = GetCurrentContainerName(activeScopes);
            if (TryReadDeclaration(lines, lineIndex, trimmed, activeScopes, out string? declarationName, out string? declarationKind))
            {
                CodebaseIndexedSemanticSymbolDocument symbol = new()
                {
                    Name = declarationName!,
                    Kind = declarationKind!,
                    ContainerName = currentContainer,
                    Signature = Truncate(trimmed, MaxSignatureCharacters),
                    StartLine = lineNumber,
                    EndLine = lineNumber
                };
                symbols.Add(symbol);

                bool shouldOpenScope = ShouldTreatAsScope(lines, lineIndex, trimmed, declarationKind!);
                if (shouldOpenScope)
                {
                    pendingScopes.Add(new OpenScope(symbol));
                }
            }

            OpenScope? callableScope = activeScopes.LastOrDefault(static scope => scope.Symbol.Kind is "function" or "method");
            if (callableScope is not null)
            {
                foreach (string callee in ExtractCallNames(trimmed))
                {
                    if (string.Equals(callee, callableScope.Symbol.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    calls.Add(new CodebaseIndexedCallEdgeDocument
                    {
                        CallerSymbol = callableScope.Symbol.Name,
                        CallerPath = relativePath,
                        CalleeSymbol = callee,
                        LineNumber = lineNumber
                    });

                    if (calls.Count >= MaxCallsPerFile)
                    {
                        break;
                    }
                }
            }

            int openings = line.Count(static character => character == '{');
            for (int index = 0; index < openings && pendingScopes.Count > 0; index++)
            {
                OpenScope scope = pendingScopes[^1];
                pendingScopes.RemoveAt(pendingScopes.Count - 1);
                scope.ActiveDepth = braceDepth + index + 1;
                activeScopes.Add(scope);
            }

            braceDepth += openings;

            int trailingClosings = Math.Max(0, line.Count(static character => character == '}') - leadingClosings);
            if (trailingClosings > 0)
            {
                braceDepth = Math.Max(0, braceDepth - trailingClosings);
                CloseCompletedScopes(activeScopes, braceDepth, lineNumber);
            }

            if (symbols.Count >= MaxSemanticSymbolsPerFile && calls.Count >= MaxCallsPerFile)
            {
                break;
            }
        }

        foreach (OpenScope scope in activeScopes.Concat(pendingScopes))
        {
            scope.Symbol.EndLine = Math.Max(scope.Symbol.EndLine, lines.Length);
        }

        return new FileSemanticAnalysis(
            rawSymbols.ToArray(),
            symbols
                .Take(MaxSemanticSymbolsPerFile)
                .ToArray(),
            calls
                .Take(MaxCallsPerFile)
                .ToArray());
    }

    private static FileSemanticAnalysis AnalyzePythonFile(
        string relativePath,
        string[] lines)
    {
        List<string> rawSymbols = [];
        List<CodebaseIndexedSemanticSymbolDocument> symbols = [];
        List<CodebaseIndexedCallEdgeDocument> calls = [];
        List<PythonScope> scopes = [];

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string line = lines[lineIndex];
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            int indent = CountLeadingWhitespace(line);
            while (scopes.Count > 0 && indent <= scopes[^1].Indent)
            {
                scopes[^1].Symbol.EndLine = lineIndex;
                scopes.RemoveAt(scopes.Count - 1);
            }

            int lineNumber = lineIndex + 1;
            Match classMatch = Regex.Match(trimmed, @"^class\s+([A-Za-z_][\w]*)");
            if (classMatch.Success)
            {
                string? container = scopes.LastOrDefault()?.Symbol.Name;
                CodebaseIndexedSemanticSymbolDocument symbol = new()
                {
                    Name = classMatch.Groups[1].Value,
                    Kind = "class",
                    ContainerName = container,
                    Signature = Truncate(trimmed, MaxSignatureCharacters),
                    StartLine = lineNumber,
                    EndLine = lineNumber
                };
                symbols.Add(symbol);
                rawSymbols.Add(symbol.Name);
                scopes.Add(new PythonScope(indent, symbol));
                continue;
            }

            Match functionMatch = Regex.Match(trimmed, @"^(?:async\s+)?def\s+([A-Za-z_][\w]*)\s*\(");
            if (functionMatch.Success)
            {
                string? container = scopes.LastOrDefault()?.Symbol.Name;
                CodebaseIndexedSemanticSymbolDocument symbol = new()
                {
                    Name = functionMatch.Groups[1].Value,
                    Kind = scopes.Any(static scope => string.Equals(scope.Symbol.Kind, "class", StringComparison.OrdinalIgnoreCase)) ? "method" : "function",
                    ContainerName = container,
                    Signature = Truncate(trimmed, MaxSignatureCharacters),
                    StartLine = lineNumber,
                    EndLine = lineNumber
                };
                symbols.Add(symbol);
                rawSymbols.Add(symbol.Name);
                scopes.Add(new PythonScope(indent, symbol));
                continue;
            }

            PythonScope? callableScope = scopes.LastOrDefault(static scope => scope.Symbol.Kind is "function" or "method");
            if (callableScope is not null)
            {
                foreach (string callee in ExtractCallNames(trimmed))
                {
                    calls.Add(new CodebaseIndexedCallEdgeDocument
                    {
                        CallerSymbol = callableScope.Symbol.Name,
                        CallerPath = relativePath,
                        CalleeSymbol = callee,
                        LineNumber = lineNumber
                    });
                }
            }
        }

        foreach (PythonScope scope in scopes)
        {
            scope.Symbol.EndLine = lines.Length;
        }

        return new FileSemanticAnalysis(
            rawSymbols.ToArray(),
            symbols.Take(MaxSemanticSymbolsPerFile).ToArray(),
            calls.Take(MaxCallsPerFile).ToArray());
    }

    private static bool TryReadDeclaration(
        string[] lines,
        int lineIndex,
        string trimmed,
        IReadOnlyList<OpenScope> activeScopes,
        out string? name,
        out string? kind)
    {
        name = null;
        kind = null;

        Match namespaceMatch = NamespaceRegex.Match(trimmed);
        if (namespaceMatch.Success)
        {
            name = namespaceMatch.Groups[1].Value;
            kind = "namespace";
            return true;
        }

        Match typeMatch = TypeRegex.Match(trimmed);
        if (typeMatch.Success)
        {
            kind = typeMatch.Groups[1].Value.ToLowerInvariant();
            name = typeMatch.Groups[2].Value;
            return true;
        }

        Match namedFunctionMatch = NamedFunctionRegex.Match(trimmed);
        if (namedFunctionMatch.Success)
        {
            name = namedFunctionMatch.Groups[1].Value;
            kind = IsInsideTypeScope(activeScopes) ? "method" : "function";
            return true;
        }

        Match assignedFunctionMatch = AssignedFunctionRegex.Match(trimmed);
        if (assignedFunctionMatch.Success)
        {
            name = assignedFunctionMatch.Groups[1].Value;
            kind = IsInsideTypeScope(activeScopes) ? "method" : "function";
            return true;
        }

        if (!LooksLikeCallableDeclaration(lines, lineIndex, trimmed))
        {
            return false;
        }

        string[] tokens = ReadIdentifierTokens(trimmed);
        int openParenIndex = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (openParenIndex <= 0 || tokens.Length == 0)
        {
            return false;
        }

        string beforeOpenParen = trimmed[..openParenIndex];
        string[] beforeTokens = ReadIdentifierTokens(beforeOpenParen);
        if (beforeTokens.Length == 0)
        {
            return false;
        }

        string candidate = beforeTokens[^1];
        if (CallableControlKeywords.Contains(candidate))
        {
            return false;
        }

        name = candidate;
        kind = IsInsideTypeScope(activeScopes) ? "method" : "function";
        return true;
    }

    private static bool LooksLikeCallableDeclaration(
        string[] lines,
        int lineIndex,
        string trimmed)
    {
        if (!trimmed.Contains('(') || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmed.Contains('=') &&
            !trimmed.Contains("=>", StringComparison.Ordinal) &&
            !trimmed.Contains("function", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string lower = trimmed.ToLowerInvariant();
        if (lower.StartsWith("if ") ||
            lower.StartsWith("for ") ||
            lower.StartsWith("foreach ") ||
            lower.StartsWith("while ") ||
            lower.StartsWith("switch ") ||
            lower.StartsWith("catch ") ||
            lower.StartsWith("return ") ||
            lower.StartsWith("new "))
        {
            return false;
        }

        if (trimmed.Contains('{') ||
            trimmed.Contains("=>", StringComparison.Ordinal) ||
            trimmed.EndsWith(';'))
        {
            return true;
        }

        for (int index = lineIndex + 1; index < lines.Length; index++)
        {
            string next = lines[index].Trim();
            if (next.Length == 0)
            {
                continue;
            }

            return string.Equals(next, "{", StringComparison.Ordinal);
        }

        return false;
    }

    private static bool ShouldTreatAsScope(
        string[] lines,
        int lineIndex,
        string trimmed,
        string kind)
    {
        if (string.Equals(kind, "enum", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.Contains('{'))
        {
            return true;
        }

        if (trimmed.Contains("=>", StringComparison.Ordinal))
        {
            return false;
        }

        for (int index = lineIndex + 1; index < lines.Length; index++)
        {
            string next = lines[index].Trim();
            if (next.Length == 0)
            {
                continue;
            }

            return string.Equals(next, "{", StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsInsideTypeScope(IReadOnlyList<OpenScope> scopes)
    {
        return scopes.Any(scope => TypeLikeSymbolKinds.Contains(scope.Symbol.Kind));
    }

    private static string? GetCurrentContainerName(IReadOnlyList<OpenScope> scopes)
    {
        for (int index = scopes.Count - 1; index >= 0; index--)
        {
            string kind = scopes[index].Symbol.Kind;
            if (string.Equals(kind, "namespace", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return scopes[index].Symbol.Name;
        }

        return scopes.Count > 0
            ? scopes[^1].Symbol.Name
            : null;
    }

    private static void CloseCompletedScopes(
        List<OpenScope> scopes,
        int braceDepth,
        int endLine)
    {
        for (int index = scopes.Count - 1; index >= 0; index--)
        {
            OpenScope scope = scopes[index];
            if (scope.ActiveDepth <= braceDepth)
            {
                continue;
            }

            scope.Symbol.EndLine = Math.Max(scope.Symbol.StartLine, endLine);
            scopes.RemoveAt(index);
        }
    }

    private static string[] ExtractCallNames(string line)
    {
        List<string> calls = [];
        foreach (Match match in CallCandidateRegex.Matches(line))
        {
            string candidate = match.Groups[1].Value;
            if (CallableControlKeywords.Contains(candidate))
            {
                continue;
            }

            calls.Add(candidate);
        }

        return calls.ToArray();
    }

    private static string[] ExtractSymbols(string content)
    {
        List<string> symbols = [];
        string[] lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 ||
                line.StartsWith("//", StringComparison.Ordinal) ||
                line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            string[] tokens = ReadIdentifierTokens(line);
            if (tokens.Length == 0)
            {
                continue;
            }

            AddKeywordSymbol(tokens, symbols, "namespace");
            AddKeywordSymbol(tokens, symbols, "class");
            AddKeywordSymbol(tokens, symbols, "interface");
            AddKeywordSymbol(tokens, symbols, "record");
            AddKeywordSymbol(tokens, symbols, "struct");
            AddKeywordSymbol(tokens, symbols, "enum");
            AddKeywordSymbol(tokens, symbols, "function");
            AddKeywordSymbol(tokens, symbols, "def");

            if (TryGetCallableSymbol(line, tokens, out string? callableSymbol))
            {
                symbols.Add(callableSymbol!);
            }

            if (symbols.Count >= MaxSymbolsPerFile * 2)
            {
                break;
            }
        }

        return symbols.ToArray();
    }

    private static bool TryGetCallableSymbol(
        string line,
        string[] tokens,
        out string? symbol)
    {
        symbol = null;
        int openParenIndex = line.IndexOf('(', StringComparison.Ordinal);
        if (openParenIndex <= 0 || tokens.Length == 0)
        {
            return false;
        }

        string firstToken = tokens[0].ToLowerInvariant();
        if (CallableControlKeywords.Contains(firstToken))
        {
            return false;
        }

        string beforeOpenParen = line[..openParenIndex];
        string[] beforeTokens = ReadIdentifierTokens(beforeOpenParen);
        if (beforeTokens.Length == 0)
        {
            return false;
        }

        string candidate = beforeTokens[^1];
        if (CallableControlKeywords.Contains(candidate))
        {
            return false;
        }

        symbol = candidate;
        return true;
    }

    private static void AddKeywordSymbol(
        string[] tokens,
        List<string> symbols,
        string keyword)
    {
        for (int index = 0; index < tokens.Length - 1; index++)
        {
            if (!string.Equals(tokens[index], keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            symbols.Add(tokens[index + 1]);
            return;
        }
    }

    private static CodebaseIndexedDependencyDocument[] ExtractDependencies(
        string relativePath,
        string language,
        string content)
    {
        List<CodebaseIndexedDependencyDocument> dependencies = [];
        string[] lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        bool inGoImportBlock = false;
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (string.Equals(language, "typescript", StringComparison.Ordinal) ||
                string.Equals(language, "javascript", StringComparison.Ordinal))
            {
                AddDependencyMatches(relativePath, dependencies, JSImportFromRegex.Matches(line), "import");
                AddDependencyMatches(relativePath, dependencies, JSImportBareRegex.Matches(line), "import");
                AddDependencyMatches(relativePath, dependencies, RequireRegex.Matches(line), "require");
            }

            if (string.Equals(language, "csharp", StringComparison.Ordinal))
            {
                AddDependencyMatches(relativePath, dependencies, CSharpUsingRegex.Matches(line), "using");
            }

            if (string.Equals(language, "python", StringComparison.Ordinal))
            {
                AddDependencyMatches(relativePath, dependencies, PythonImportRegex.Matches(line), "import");
                AddDependencyMatches(relativePath, dependencies, PythonFromImportRegex.Matches(line), "from_import");
            }

            if (string.Equals(language, "java", StringComparison.Ordinal))
            {
                AddDependencyMatches(relativePath, dependencies, JavaImportRegex.Matches(line), "import");
            }

            if (string.Equals(language, "go", StringComparison.Ordinal))
            {
                if (line.StartsWith("import (", StringComparison.Ordinal))
                {
                    inGoImportBlock = true;
                    continue;
                }

                if (inGoImportBlock)
                {
                    if (line.StartsWith(')'))
                    {
                        inGoImportBlock = false;
                    }
                    else
                    {
                        AddDependencyMatches(relativePath, dependencies, GoImportEntryRegex.Matches(line), "import");
                    }

                    continue;
                }

                AddDependencyMatches(relativePath, dependencies, GoInlineImportRegex.Matches(line), "import");
            }
        }

        if (string.Equals(language, "msbuild", StringComparison.Ordinal))
        {
            AddDependencyMatches(relativePath, dependencies, ProjectReferenceRegex.Matches(content), "project_reference");
            AddDependencyMatches(relativePath, dependencies, PackageReferenceRegex.Matches(content), "package_reference");
        }

        return dependencies
            .GroupBy(static dependency => $"{dependency.Kind}\n{dependency.Target}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(MaxDependenciesPerFile)
            .ToArray();
    }

    private static void AddDependencyMatches(
        string relativePath,
        List<CodebaseIndexedDependencyDocument> dependencies,
        MatchCollection matches,
        string kind)
    {
        foreach (Match match in matches.Cast<Match>())
        {
            string target = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            bool isRelative = target.StartsWith("./", StringComparison.Ordinal) ||
                target.StartsWith("../", StringComparison.Ordinal) ||
                target.StartsWith("/", StringComparison.Ordinal);

            dependencies.Add(new CodebaseIndexedDependencyDocument
            {
                Kind = kind,
                Target = target,
                IsWorkspaceLocal = isRelative || string.Equals(kind, "project_reference", StringComparison.OrdinalIgnoreCase),
                ResolvedPaths = isRelative || string.Equals(kind, "project_reference", StringComparison.OrdinalIgnoreCase)
                    ? ResolveRelativeDependencyPaths(relativePath, target)
                    : []
            });

            if (dependencies.Count >= MaxDependenciesPerFile)
            {
                break;
            }
        }
    }

    private static string[] ResolveRelativeDependencyPaths(
        string currentPath,
        string target)
    {
        string currentDirectory = Path.GetDirectoryName(currentPath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        string normalizedTarget = target.Replace('\\', '/');

        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return [];
        }

        if (normalizedTarget.StartsWith("/", StringComparison.Ordinal))
        {
            normalizedTarget = NormalizeWorkspaceRelativePath(normalizedTarget);
        }
        else
        {
            string combined = string.IsNullOrWhiteSpace(currentDirectory)
                ? normalizedTarget
                : $"{currentDirectory.Replace('\\', '/')}/{normalizedTarget}";
            normalizedTarget = NormalizeWorkspaceRelativePath(combined);
        }

        List<string> candidates = [normalizedTarget];

        if (!Path.HasExtension(normalizedTarget))
        {
            foreach (string extension in new[] { ".ts", ".tsx", ".js", ".jsx", ".cs", ".py", ".go", ".java", ".json" })
            {
                candidates.Add(normalizedTarget + extension);
            }

            foreach (string indexName in new[] { "/index.ts", "/index.tsx", "/index.js", "/index.jsx" })
            {
                candidates.Add(normalizedTarget.TrimEnd('/') + indexName);
            }
        }

        return candidates
            .Select(static path => path.Replace('\\', '/').TrimStart('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxResolvedPathsPerDependency)
            .ToArray();
    }

    private static string NormalizeWorkspaceRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string[] segments = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        List<string> normalizedSegments = [];

        foreach (string segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (normalizedSegments.Count > 0)
                {
                    normalizedSegments.RemoveAt(normalizedSegments.Count - 1);
                }

                continue;
            }

            normalizedSegments.Add(segment);
        }

        return string.Join('/', normalizedSegments);
    }

    private static double CosineSimilarity(
        IReadOnlyList<sbyte> left,
        IReadOnlyList<sbyte> right)
    {
        if (left.Count == 0 || right.Count == 0 || left.Count != right.Count)
        {
            return 0;
        }

        double sum = 0;
        double leftSumSquares = 0;
        double rightSumSquares = 0;
        for (int index = 0; index < left.Count; index++)
        {
            sum += left[index] * right[index];
            leftSumSquares += left[index] * left[index];
            rightSumSquares += right[index] * right[index];
        }

        if (leftSumSquares <= double.Epsilon || rightSumSquares <= double.Epsilon)
        {
            return 0;
        }

        return sum / Math.Sqrt(leftSumSquares * rightSumSquares);
    }

    private static IEnumerable<string> Tokenize(string value)
    {
        foreach (string token in ReadIdentifierTokens(value))
        {
            string normalized = token.ToLowerInvariant();
            if (normalized.Length < 2)
            {
                continue;
            }

            yield return normalized;
            foreach (string part in SplitCamelCase(token))
            {
                string normalizedPart = part.ToLowerInvariant();
                if (normalizedPart.Length >= 2 &&
                    !string.Equals(normalizedPart, normalized, StringComparison.Ordinal))
                {
                    yield return normalizedPart;
                }
            }
        }
    }

    private static string[] ReadIdentifierTokens(string value)
    {
        List<string> tokens = [];
        StringBuilder current = new();
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character) || character == '_' || character == '.')
            {
                current.Append(character);
                continue;
            }

            FlushCurrent();
        }

        FlushCurrent();
        return tokens
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .ToArray();

        void FlushCurrent()
        {
            if (current.Length == 0)
            {
                return;
            }

            tokens.Add(current.ToString().Trim('_', '.'));
            current.Clear();
        }
    }

    private static IEnumerable<string> SplitCamelCase(string value)
    {
        if (value.Length == 0)
        {
            yield break;
        }

        int start = 0;
        for (int index = 1; index < value.Length; index++)
        {
            if (!char.IsUpper(value[index]) || !char.IsLower(value[index - 1]))
            {
                continue;
            }

            yield return value[start..index];
            start = index;
        }

        yield return value[start..];
    }

    private static int CountLines(string content)
    {
        if (content.Length == 0)
        {
            return 0;
        }

        int count = 1;
        foreach (char character in content)
        {
            if (character == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static string GetLanguage(string relativePath)
    {
        return Path.GetExtension(relativePath).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".csproj" => "msbuild",
            ".css" => "css",
            ".fs" => "fsharp",
            ".go" => "go",
            ".html" => "html",
            ".java" => "java",
            ".js" => "javascript",
            ".json" => "json",
            ".jsx" => "javascript",
            ".md" => "markdown",
            ".ps1" => "powershell",
            ".py" => "python",
            ".rs" => "rust",
            ".sh" => "shell",
            ".sln" or ".slnx" => "solution",
            ".sql" => "sql",
            ".ts" => "typescript",
            ".tsx" => "typescript",
            ".xml" => "xml",
            ".yaml" or ".yml" => "yaml",
            _ => "text"
        };
    }

    private static bool IsDefaultIgnoredPath(
        string relativePath,
        bool isDirectory)
    {
        string normalizedPath = relativePath
            .Replace('\\', '/')
            .Trim('/');
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        if (DefaultIgnoredPathPrefixes.Any(prefix =>
                normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!isDirectory)
        {
            return false;
        }

        string name = Path.GetFileName(normalizedPath);
        return DefaultIgnoredDirectoryNames.Contains(name);
    }

    private static bool IsFileSystemAccessException(Exception exception)
    {
        return exception is UnauthorizedAccessException or
            IOException or
            PathTooLongException or
            System.Security.SecurityException;
    }

    private string GetWorkspaceRoot()
    {
        return Path.GetFullPath(_workspaceRootProvider.GetWorkspaceRoot());
    }

    private static string GetIndexPath(string workspaceRoot)
    {
        return Path.Combine(
            workspaceRoot,
            ".stemcode",
            "cache",
            "codebase-index.json");
    }

    private static string ToWorkspaceRelativePath(
        string workspaceRoot,
        string path)
    {
        return WorkspacePath.ToRelativePath(workspaceRoot, path);
    }

    private static string Truncate(
        string value,
        int maxCharacters)
    {
        string normalized = value.Trim();
        return normalized.Length <= maxCharacters
            ? normalized
            : normalized[..Math.Max(0, maxCharacters - 3)].TrimEnd() + "...";
    }

    private static int CountLeadingCharacters(
        string value,
        char character)
    {
        int count = 0;
        while (count < value.Length && value[count] == character)
        {
            count++;
        }

        return count;
    }

    private static int CountLeadingWhitespace(string value)
    {
        int count = 0;
        while (count < value.Length && char.IsWhiteSpace(value[count]))
        {
            count++;
        }

        return count;
    }

    private static CodebaseIndexStats EmptyStats()
    {
        return new CodebaseIndexStats(
            SemanticSymbolCount: 0,
            DependencyEdgeCount: 0,
            CallEdgeCount: 0,
            OwnedFileCount: 0,
            OwnershipRuleCount: 0);
    }

    private static CodebaseIndexStats BuildStats(CodebaseIndexDocument index)
    {
        return new CodebaseIndexStats(
            SemanticSymbolCount: index.Files.Sum(static file => file.SemanticSymbols.Length),
            DependencyEdgeCount: index.Files.Sum(static file => file.Dependencies.Length),
            CallEdgeCount: index.Files.Sum(static file => file.Calls.Length),
            OwnedFileCount: index.Files.Count(static file => file.Owners.Length > 0),
            OwnershipRuleCount: index.OwnershipRuleCount);
    }

    private static IReadOnlyList<string> CreateBuildWarnings(
        WorkspaceScan scan,
        int ownershipRuleCount,
        int skippedFileCount)
    {
        List<string> warnings = [];
        if (scan.Files.Count > MaxIndexedFiles)
        {
            warnings.Add($"Only the first {MaxIndexedFiles} eligible files were indexed.");
        }

        if (ownershipRuleCount == 0)
        {
            warnings.Add("No CODEOWNERS file was found, so the ownership map is empty.");
        }

        if (skippedFileCount > 0)
        {
            warnings.Add($"{skippedFileCount} files were skipped because they were ignored, binary, empty, oversized, or unreadable.");
        }

        return warnings;
    }

    private static IReadOnlyList<string> CreateStatusWarnings(
        bool exists,
        bool isStale,
        int newFileCount,
        int changedFileCount,
        int deletedFileCount,
        WorkspaceScan scan,
        int ownershipRuleCount)
    {
        List<string> warnings = [];
        if (!exists)
        {
            warnings.Add("Index has not been built yet.");
        }

        if (isStale)
        {
            warnings.Add($"Index is stale: {newFileCount} new, {changedFileCount} changed, {deletedFileCount} deleted.");
        }

        if (scan.Files.Count > MaxIndexedFiles)
        {
            warnings.Add($"Only the first {MaxIndexedFiles} eligible files can be indexed in one pass.");
        }

        if (ownershipRuleCount == 0)
        {
            warnings.Add("No CODEOWNERS file was found, so ownership lookups may be empty.");
        }

        return warnings;
    }

    private static IReadOnlyList<string> CreateQueryWarnings(
        bool indexWasUpdated,
        CodebaseIndexBuildResult? buildResult)
    {
        List<string> warnings = [];
        if (indexWasUpdated)
        {
            warnings.Add("Index was stale and was refreshed before returning results.");
        }

        if (buildResult is not null)
        {
            warnings.AddRange(buildResult.Warnings);
        }

        return warnings.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static CodebaseIndexSemanticSymbol ToSemanticSymbol(CodebaseIndexedSemanticSymbolDocument symbol)
    {
        return new CodebaseIndexSemanticSymbol(
            symbol.Name,
            symbol.Kind,
            string.IsNullOrWhiteSpace(symbol.ContainerName) ? null : symbol.ContainerName,
            string.IsNullOrWhiteSpace(symbol.Signature) ? null : symbol.Signature,
            symbol.StartLine,
            symbol.EndLine);
    }

    private static CodebaseIndexDependency ToDependency(CodebaseIndexedDependencyDocument dependency)
    {
        return new CodebaseIndexDependency(
            dependency.Kind,
            dependency.Target,
            dependency.IsWorkspaceLocal,
            dependency.ResolvedPaths);
    }

    private static CodebaseIndexCallEdge ToCallEdge(CodebaseIndexedCallEdgeDocument edge)
    {
        return new CodebaseIndexCallEdge(
            edge.CallerSymbol,
            edge.CallerPath,
            edge.CalleeSymbol,
            string.IsNullOrWhiteSpace(edge.CalleePath) ? null : edge.CalleePath,
            edge.LineNumber,
            edge.IsResolved);
    }

    private static void ResolveRepositoryMetadata(List<CodebaseIndexedFileDocument> indexedFiles)
    {
        Dictionary<string, CodebaseIndexedFileDocument> pathMap = indexedFiles.ToDictionary(
            static file => file.Path,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string[]> namespaceMap = BuildNamespaceMap(indexedFiles);

        foreach (CodebaseIndexedFileDocument file in indexedFiles)
        {
            foreach (CodebaseIndexedDependencyDocument dependency in file.Dependencies)
            {
                List<string> resolvedPaths = [];
                foreach (string candidate in dependency.ResolvedPaths)
                {
                    if (pathMap.ContainsKey(candidate))
                    {
                        resolvedPaths.Add(candidate);
                    }
                }

                if (resolvedPaths.Count == 0 &&
                    namespaceMap.TryGetValue(dependency.Target, out string[]? namespacePaths) &&
                    namespacePaths is not null)
                {
                    resolvedPaths.AddRange(namespacePaths);
                }

                dependency.ResolvedPaths = resolvedPaths
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxResolvedPathsPerDependency)
                    .ToArray();
                if (dependency.ResolvedPaths.Length > 0)
                {
                    dependency.IsWorkspaceLocal = true;
                }
            }
        }

        Dictionary<string, List<CallableSymbolTarget>> callableSymbolMap = BuildCallableSymbolMap(indexedFiles);
        foreach (CodebaseIndexedFileDocument file in indexedFiles)
        {
            HashSet<string> preferredDependencyPaths = file.Dependencies
                .SelectMany(static dependency => dependency.ResolvedPaths)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (CodebaseIndexedCallEdgeDocument call in file.Calls)
            {
                if (!callableSymbolMap.TryGetValue(call.CalleeSymbol, out List<CallableSymbolTarget>? candidates) ||
                    candidates.Count == 0)
                {
                    continue;
                }

                CallableSymbolTarget? resolved = candidates.Count == 1
                    ? candidates[0]
                    : candidates.FirstOrDefault(candidate => string.Equals(candidate.Path, file.Path, StringComparison.OrdinalIgnoreCase))
                      ?? candidates.FirstOrDefault(candidate => preferredDependencyPaths.Contains(candidate.Path))
                      ?? candidates[0];

                if (resolved is null)
                {
                    continue;
                }

                call.CalleePath = resolved.Path;
                call.IsResolved = true;
            }
        }
    }

    private static Dictionary<string, string[]> BuildNamespaceMap(IReadOnlyList<CodebaseIndexedFileDocument> files)
    {
        return files
            .SelectMany(static file => file.SemanticSymbols
                .Where(static symbol => string.Equals(symbol.Kind, "namespace", StringComparison.OrdinalIgnoreCase))
                .Select(symbol => (symbol.Name, file.Path)))
            .GroupBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static item => item.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxResolvedPathsPerDependency)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, List<CallableSymbolTarget>> BuildCallableSymbolMap(IReadOnlyList<CodebaseIndexedFileDocument> files)
    {
        Dictionary<string, List<CallableSymbolTarget>> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (CodebaseIndexedFileDocument file in files)
        {
            foreach (CodebaseIndexedSemanticSymbolDocument symbol in file.SemanticSymbols)
            {
                if (symbol.Kind is not "function" and not "method")
                {
                    continue;
                }

                if (!map.TryGetValue(symbol.Name, out List<CallableSymbolTarget>? candidates))
                {
                    candidates = [];
                    map[symbol.Name] = candidates;
                }

                candidates.Add(new CallableSymbolTarget(file.Path, symbol.Name));
            }
        }

        return map;
    }

    private static IReadOnlyList<CodeOwnersRule> LoadOwnershipRules(string workspaceRoot)
    {
        foreach (string candidatePath in CodeOwnersCandidatePaths)
        {
            string fullPath = WorkspacePath.Resolve(workspaceRoot, candidatePath);
            if (!File.Exists(fullPath))
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(fullPath, Encoding.UTF8);
            }
            catch (Exception exception) when (IsFileSystemAccessException(exception) ||
                                              exception is DecoderFallbackException or InvalidDataException)
            {
                return [];
            }

            List<CodeOwnersRule> rules = [];
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2 || parts[0].StartsWith('!'))
                {
                    continue;
                }

                string pattern = parts[0];
                string[] owners = parts.Skip(1).ToArray();
                Regex matcher = BuildCodeOwnersRegex(pattern);
                rules.Add(new CodeOwnersRule(pattern, owners, matcher));
            }

            return rules;
        }

        return [];
    }

    private static string[] ResolveOwners(
        string relativePath,
        IReadOnlyList<CodeOwnersRule> rules)
    {
        string normalizedPath = relativePath.Replace('\\', '/').TrimStart('/');
        string[] owners = [];
        foreach (CodeOwnersRule rule in rules)
        {
            if (rule.Matcher.IsMatch(normalizedPath))
            {
                owners = rule.Owners;
            }
        }

        return owners.Take(8).ToArray();
    }

    private static Regex BuildCodeOwnersRegex(string pattern)
    {
        string normalizedPattern = pattern.Replace('\\', '/');
        bool anchored = normalizedPattern.StartsWith("/", StringComparison.Ordinal);
        bool directoryOnly = normalizedPattern.EndsWith("/", StringComparison.Ordinal);
        string corePattern = normalizedPattern.Trim('/');
        string escaped = Regex.Escape(corePattern)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", @"[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", @"[^/]", StringComparison.Ordinal);

        if (directoryOnly)
        {
            escaped = $"{escaped}(?:/.*)?";
        }

        string prefix = anchored ? "^" : @"^(?:.*/)?";
        string suffix = "$";
        return new Regex(
            prefix + escaped + suffix,
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }

    private sealed record WorkspaceScan(
        IReadOnlyList<CodebaseIndexCandidate> Files,
        int SkippedFileCount);

    private sealed record CodebaseIndexCandidate(
        string FullPath,
        string RelativePath,
        long Length,
        DateTimeOffset LastWriteTimeUtc);

    private sealed record FileSemanticAnalysis(
        string[] Symbols,
        CodebaseIndexedSemanticSymbolDocument[] SemanticSymbols,
        CodebaseIndexedCallEdgeDocument[] Calls);

    private sealed record CandidateIndexResult(
        CodebaseIndexedFileDocument? ReusedFile,
        CodebaseIndexedFileDocument? IndexedFile,
        string[] Owners,
        bool WasExistingFile)
    {
        public static CandidateIndexResult Reused(
            CodebaseIndexedFileDocument file,
            string[] owners)
        {
            return new CandidateIndexResult(file, null, owners, WasExistingFile: true);
        }

        public static CandidateIndexResult Indexed(
            CodebaseIndexedFileDocument file,
            bool wasExistingFile)
        {
            return new CandidateIndexResult(null, file, [], wasExistingFile);
        }

        public static CandidateIndexResult Skipped()
        {
            return new CandidateIndexResult(null, null, [], WasExistingFile: false);
        }
    }

    private sealed class OpenScope
    {
        public OpenScope(CodebaseIndexedSemanticSymbolDocument symbol)
        {
            Symbol = symbol;
        }

        public int ActiveDepth { get; set; } = int.MaxValue;

        public CodebaseIndexedSemanticSymbolDocument Symbol { get; }
    }

    private sealed class PythonScope
    {
        public PythonScope(int indent, CodebaseIndexedSemanticSymbolDocument symbol)
        {
            Indent = indent;
            Symbol = symbol;
        }

        public int Indent { get; }

        public CodebaseIndexedSemanticSymbolDocument Symbol { get; }
    }

    private sealed record CallableSymbolTarget(string Path, string Name);

    // ---- Resident snapshot, single-flight rebuild, and dirty-generation tracking ----
    //
    // The index is deserialized once into a CodebaseIndexSnapshot that is kept resident.
    // Every search/list captures that snapshot via a single Volatile.Read and reads from the
    // frozen maps it contains, so no per-search workspace scan or JSON deserialize is required
    // on the hot path. A rebuild produces entirely new objects and publishes them with
    // Interlocked.Exchange; the previously published snapshot is never mutated.
    //
    // A FileSystemWatcher bumps a dirty generation whenever the workspace changes. When the
    // generation still matches the snapshot's generation (and the watcher is healthy), the
    // search path skips the workspace scan entirely. Note: integer-id CSR / inverted indexes
    // are intentionally NOT used here; revisit only if profiling shows the frozen-map version
    // is still too costly.

    private async Task<FreshSnapshot> EnsureFreshSnapshotAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        CodebaseIndexSnapshot? current = Volatile.Read(ref _snapshot);
        if (!RequiresScanRebuild(current, force))
        {
            return new FreshSnapshot(current!, null);
        }

        // Single-flight rebuild gate: the first caller through rebuilds; later arrivals that
        // find the snapshot already fresh after acquiring the gate just use the published one.
        await _rebuildGate.WaitAsync(cancellationToken);
        try
        {
            current = Volatile.Read(ref _snapshot);
            if (!RequiresScanRebuild(current, force))
            {
                return new FreshSnapshot(current!, null);
            }

            if (current is null && !force)
            {
                CodebaseIndexSnapshot? loaded = await TryLoadSnapshotAsync(cancellationToken);
                if (loaded is not null)
                {
                    Interlocked.Exchange(ref _snapshot, loaded);
                    return new FreshSnapshot(loaded, null);
                }
            }

            RebuildOutcome outcome = await RebuildAsync(force, cancellationToken);
            Interlocked.Exchange(ref _snapshot, outcome.Snapshot);
            return new FreshSnapshot(outcome.Snapshot, outcome.BuildResult);
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    private bool RequiresScanRebuild(CodebaseIndexSnapshot? current, bool force)
    {
        if (force)
        {
            return true;
        }

        if (current is null)
        {
            return true;
        }

        // Without a reliable watcher we cannot trust the dirty generation, so fall back to a
        // real scan/rebuild decision on every entry.
        if (_watcher is null || !_watchHealthy)
        {
            return true;
        }

        return current.Generation != Volatile.Read(ref _dirtyGeneration);
    }

    private async Task<CodebaseIndexSnapshot?> TryLoadSnapshotAsync(CancellationToken cancellationToken)
    {
        string workspaceRoot = GetWorkspaceRoot();
        string indexPath = GetIndexPath(workspaceRoot);
        CodebaseIndexDocument? document = await LoadIndexAsync(indexPath, cancellationToken);
        if (document is null)
        {
            return null;
        }

        return BuildSnapshot(document, Volatile.Read(ref _dirtyGeneration));
    }

    private static CodebaseIndexSnapshot BuildSnapshot(
        CodebaseIndexDocument document,
        int generation)
    {
        CodebaseIndexedFileDocument[] files = document.Files
            .Where(static file => file is not null && !string.IsNullOrWhiteSpace(file.Path))
            .ToArray();
        FrozenDictionary<string, CodebaseIndexedFileDocument> fileMap = files.ToFrozenDictionary(
            static file => file.Path,
            StringComparer.OrdinalIgnoreCase);
        FrozenDictionary<string, CodebaseIndexedCallEdgeDocument[]> incomingCallMap = files
            .SelectMany(static file => file.Calls)
            .Where(static call => !string.IsNullOrWhiteSpace(call.CalleePath))
            .GroupBy(static call => call.CalleePath!, StringComparer.OrdinalIgnoreCase)
            .Select(static group => (
                Key: group.Key,
                Edges: group
                    .OrderBy(static call => call.CallerPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static call => call.LineNumber)
                    .ToArray()))
            .ToFrozenDictionary(
                static item => item.Key,
                static item => item.Edges,
                StringComparer.OrdinalIgnoreCase);
        CodebaseIndexStats stats = BuildStats(document);
        return new CodebaseIndexSnapshot(
            document,
            files,
            fileMap,
            incomingCallMap,
            stats,
            generation);
    }

    private static CodebaseIndexedFileDocument CloneFileForReuse(
        CodebaseIndexedFileDocument source,
        string[] owners)
    {
        return new CodebaseIndexedFileDocument
        {
            Path = source.Path,
            Length = source.Length,
            LastWriteTimeUtc = source.LastWriteTimeUtc,
            Language = source.Language,
            LineCount = source.LineCount,
            Symbols = (string[])source.Symbols.Clone(),
            SemanticSymbols = source.SemanticSymbols
                .Select(static symbol => new CodebaseIndexedSemanticSymbolDocument
                {
                    Name = symbol.Name,
                    Kind = symbol.Kind,
                    ContainerName = symbol.ContainerName,
                    Signature = symbol.Signature,
                    StartLine = symbol.StartLine,
                    EndLine = symbol.EndLine
                })
                .ToArray(),
            Dependencies = source.Dependencies
                .Select(static dependency => new CodebaseIndexedDependencyDocument
                {
                    Kind = dependency.Kind,
                    Target = dependency.Target,
                    IsWorkspaceLocal = dependency.IsWorkspaceLocal,
                    ResolvedPaths = (string[])dependency.ResolvedPaths.Clone()
                })
                .ToArray(),
            Owners = (string[])owners.Clone(),
            Calls = source.Calls
                .Select(static call => new CodebaseIndexedCallEdgeDocument
                {
                    CallerSymbol = call.CallerSymbol,
                    CallerPath = call.CallerPath,
                    CalleeSymbol = call.CalleeSymbol,
                    CalleePath = call.CalleePath,
                    LineNumber = call.LineNumber,
                    IsResolved = call.IsResolved
                })
                .ToArray(),
            Embedding = (sbyte[])source.Embedding.Clone()
        };
    }

    private FileSystemWatcher? TryCreateWatcher(string workspaceRoot)
    {
        try
        {
            if (!Directory.Exists(workspaceRoot))
            {
                return null;
            }

            var watcher = new FileSystemWatcher(workspaceRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                InternalBufferSize = 65536
            };
            watcher.Created += MarkDirty;
            watcher.Changed += MarkDirty;
            watcher.Deleted += MarkDirty;
            watcher.Renamed += MarkRenamedDirty;
            watcher.Error += MarkWatchUnhealthy;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void MarkDirty(object? sender, FileSystemEventArgs e)
    {
        if (IsOwnMetadataPath(e.FullPath))
        {
            return;
        }

        Interlocked.Increment(ref _dirtyGeneration);
    }

    private void MarkRenamedDirty(object? sender, RenamedEventArgs e)
    {
        // Only ignore renames that stay entirely inside our own metadata tree (e.g. temp swap);
        // anything touching the real workspace is treated as a change.
        if (!IsOwnMetadataPath(e.FullPath) || !IsOwnMetadataPath(e.OldFullPath))
        {
            Interlocked.Increment(ref _dirtyGeneration);
        }
    }

    private void MarkWatchUnhealthy(object? sender, ErrorEventArgs e) => _watchHealthy = false;

    private static bool IsOwnMetadataPath(string fullPath)
    {
        string normalized = fullPath.Replace('\\', '/');
        return normalized.Contains("/.stemcode/", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("/.stemcode", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RebuildOutcome(
        CodebaseIndexSnapshot Snapshot,
        CodebaseIndexBuildResult BuildResult);

    private sealed record FreshSnapshot(
        CodebaseIndexSnapshot Snapshot,
        CodebaseIndexBuildResult? BuildResult);

    private sealed class CodebaseIndexSnapshot
    {
        public CodebaseIndexSnapshot(
            CodebaseIndexDocument document,
            CodebaseIndexedFileDocument[] files,
            FrozenDictionary<string, CodebaseIndexedFileDocument> fileMap,
            FrozenDictionary<string, CodebaseIndexedCallEdgeDocument[]> incomingCallMap,
            CodebaseIndexStats stats,
            int generation)
        {
            Document = document;
            Files = files;
            FileMap = fileMap;
            IncomingCallMap = incomingCallMap;
            Stats = stats;
            Generation = generation;
        }

        public CodebaseIndexDocument Document { get; }

        public CodebaseIndexedFileDocument[] Files { get; }

        public FrozenDictionary<string, CodebaseIndexedFileDocument> FileMap { get; }

        public FrozenDictionary<string, CodebaseIndexedCallEdgeDocument[]> IncomingCallMap { get; }

        public CodebaseIndexStats Stats { get; }

        public int Generation { get; }
    }

    private sealed record CodeOwnersRule(string Pattern, string[] Owners, Regex Matcher);
}

internal sealed class CodebaseIndexDocument
{
    public int Version { get; set; }

    public string EmbeddingModelId { get; set; } = string.Empty;

    public string EmbeddingModelName { get; set; } = string.Empty;

    public string EmbeddingModelRevision { get; set; } = string.Empty;

    public string EmbeddingModelUrl { get; set; } = string.Empty;

    public string EmbeddingQuantization { get; set; } = string.Empty;

    public int EmbeddingDimensions { get; set; }

    public DateTimeOffset BuiltAtUtc { get; set; }

    public int OwnershipRuleCount { get; set; }

    public List<CodebaseIndexedFileDocument> Files { get; set; } = [];
}

internal sealed class CodebaseIndexedFileDocument
{
    public string Path { get; set; } = string.Empty;

    public long Length { get; set; }

    public DateTimeOffset LastWriteTimeUtc { get; set; }

    public string Language { get; set; } = "text";

    public int LineCount { get; set; }

    public string[] Symbols { get; set; } = [];

    public CodebaseIndexedSemanticSymbolDocument[] SemanticSymbols { get; set; } = [];

    public CodebaseIndexedDependencyDocument[] Dependencies { get; set; } = [];

    public string[] Owners { get; set; } = [];

    public CodebaseIndexedCallEdgeDocument[] Calls { get; set; } = [];

    public sbyte[] Embedding { get; set; } = [];
}

internal sealed class CodebaseIndexedSemanticSymbolDocument
{
    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string? ContainerName { get; set; }

    public string? Signature { get; set; }

    public int StartLine { get; set; }

    public int EndLine { get; set; }
}

internal sealed class CodebaseIndexedDependencyDocument
{
    public string Kind { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public bool IsWorkspaceLocal { get; set; }

    public string[] ResolvedPaths { get; set; } = [];
}

internal sealed class CodebaseIndexedCallEdgeDocument
{
    public string CallerSymbol { get; set; } = string.Empty;

    public string CallerPath { get; set; } = string.Empty;

    public string CalleeSymbol { get; set; } = string.Empty;

    public string? CalleePath { get; set; }

    public int LineNumber { get; set; }

    public bool IsResolved { get; set; }
}
