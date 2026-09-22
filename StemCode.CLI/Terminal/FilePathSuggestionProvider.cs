namespace StemCode.CLI;

internal static class FilePathSuggestionProvider
{
    private const string ReadCommandPrefix = "/read ";
    private const string ImportCommandPrefix = "/import ";
    private const string ExportJsonCommandPrefix = "/export json ";
    private const string ExportHtmlCommandPrefix = "/export html ";
    private const string ExportTrajectoryCommandPrefix = "/export trajectory ";
    private const string DirectShellPrefix = "!";
    private static readonly char[] DirectorySeparators = ['/', '\\'];

    public static IReadOnlyList<FilePathSuggestion> GetSuggestions(
        string rootDirectory,
        string input,
        int maxCount,
        string? homeDirectory = null)
    {
        string home = homeDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (maxCount <= 0 ||
            !TryCreateRequest(rootDirectory, input, home, out FilePathSuggestionRequest? request) ||
            request is null)
        {
            return [];
        }

        string directoryPart = GetDirectoryPart(request.PathText);
        string namePrefix = GetNamePrefix(request.PathText);
        if (!TryResolveDirectory(request.RootDirectory, directoryPart, out string? searchDirectory) ||
            !Directory.Exists(searchDirectory))
        {
            return [];
        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // For shell commands (and plain path input) the user types a path token in place
        // (e.g. "cd ./sr"); we complete only the final name component while preserving the
        // directory portion exactly as typed. Slash commands instead receive the full
        // workspace-relative path.
        string literalDirectoryPrefix = request.PreserveTypedPath
            ? GetLiteralDirectoryPrefix(request.LiteralPathText)
            : string.Empty;

        // Home-relative paths (~/...) resolve outside the workspace, so the "current
        // directory" shorthand would render as "~./" and is skipped; the directory listing
        // below already covers the home contents.
        bool isHomePath = request.LiteralPathText.StartsWith("~", StringComparison.Ordinal);

        List<FilePathSuggestion> suggestions = [];
        if (request.PreserveTypedPath &&
            !isHomePath &&
            ShouldSuggestCurrentDirectory(request.PathText, namePrefix))
        {
            // When the typed token already ends with a separator ("./", "src/") the
            // directory is fully specified, so we keep the literal prefix instead of
            // appending another "./" (which would render as "././").
            string displayPath = literalDirectoryPrefix.Length > 0 &&
                literalDirectoryPrefix[^1] is '/' or '\\'
                ? literalDirectoryPrefix
                : literalDirectoryPrefix + "./";
            suggestions.Add(new FilePathSuggestion(
                request.CommandPrefix + displayPath,
                displayPath,
                "Current directory",
                IsDirectory: true));

            if (suggestions.Count >= maxCount)
            {
                return suggestions;
            }
        }

        foreach (DirectoryInfo directory in EnumerateDirectories(searchDirectory)
            .Where(directory => directory.Name.StartsWith(namePrefix, comparison))
            .OrderBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (ShouldSkipPath(request.RootDirectory, directory.FullName))
            {
                continue;
            }

            string displayPath = request.PreserveTypedPath
                ? literalDirectoryPrefix + directory.Name + "/"
                : ToDisplayPath(request.RootDirectory, directory.FullName) + "/";
            suggestions.Add(new FilePathSuggestion(
                request.CommandPrefix + displayPath,
                displayPath,
                "Directory",
                IsDirectory: true));

            if (suggestions.Count >= maxCount)
            {
                return suggestions;
            }
        }

        foreach (FileInfo file in EnumerateFiles(searchDirectory)
            .Where(file => file.Name.StartsWith(namePrefix, comparison))
            .Where(file => !request.JsonOnly || string.Equals(file.Extension, ".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (ShouldSkipPath(request.RootDirectory, file.FullName))
            {
                continue;
            }

            string displayPath = request.PreserveTypedPath
                ? literalDirectoryPrefix + file.Name
                : ToDisplayPath(request.RootDirectory, file.FullName);
            suggestions.Add(new FilePathSuggestion(
                request.CommandPrefix + displayPath,
                displayPath,
                request.JsonOnly ? "JSON file" : "File",
                IsDirectory: false));

            if (suggestions.Count >= maxCount)
            {
                return suggestions;
            }
        }

        return suggestions;
    }

    private static bool ShouldSuggestCurrentDirectory(
        string pathText,
        string namePrefix)
    {
        string trimmed = pathText.TrimStart();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (!".".StartsWith(namePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return trimmed.EndsWith("/", StringComparison.Ordinal) ||
            trimmed.EndsWith("\\", StringComparison.Ordinal) ||
            string.Equals(namePrefix, ".", StringComparison.Ordinal);
    }

    private static bool TryCreateRequest(
        string rootDirectory,
        string input,
        string homeDirectory,
        out FilePathSuggestionRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(rootDirectory) ||
            string.IsNullOrWhiteSpace(input) ||
            input.Contains('\n', StringComparison.Ordinal))
        {
            return false;
        }

        string fullRoot = Path.GetFullPath(rootDirectory);
        if (TryCreateRequest(input, ReadCommandPrefix, fullRoot, homeDirectory, jsonOnly: false, out request) ||
            TryCreateRequest(input, ImportCommandPrefix, fullRoot, homeDirectory, jsonOnly: true, out request) ||
            TryCreateRequest(input, ExportJsonCommandPrefix, fullRoot, homeDirectory, jsonOnly: false, out request) ||
            TryCreateRequest(input, ExportHtmlCommandPrefix, fullRoot, homeDirectory, jsonOnly: false, out request) ||
            TryCreateRequest(input, ExportTrajectoryCommandPrefix, fullRoot, homeDirectory, jsonOnly: false, out request) ||
            TryCreateBangRequest(input, fullRoot, homeDirectory, out request) ||
            TryCreatePlainPathRequest(input, fullRoot, homeDirectory, out request))
        {
            return true;
        }

        return false;
    }

    private static bool TryCreatePlainPathRequest(
        string input,
        string rootDirectory,
        string homeDirectory,
        out FilePathSuggestionRequest? request)
    {
        request = null;
        // Complete the last whitespace-delimited token wherever it appears so path
        // tokens fire in the middle of a command line, not only at the very start
        // (e.g. "cd ./src", "run ~/notes", "echo ~/file.txt").
        int lastWhitespaceIndex = -1;
        for (int index = input.Length - 1; index >= 0; index--)
        {
            if (char.IsWhiteSpace(input[index]))
            {
                lastWhitespaceIndex = index;
                break;
            }
        }

        int tokenStart = lastWhitespaceIndex + 1;
        string token = input[tokenStart..];
        if (!IsPathToken(token))
        {
            return false;
        }

        // Everything before the token (including its separating whitespace) is preserved
        // verbatim so the completed path is inserted in place. When the path token is the
        // entire input this prefix is empty, matching the previous start-of-line behaviour.
        string commandPrefix = input[..tokenStart];
        request = CreatePathRequest(
            rootDirectory,
            commandPrefix,
            token,
            homeDirectory,
            jsonOnly: false,
            preserveTypedPath: true);
        return true;
    }

    private static bool TryCreateBangRequest(
        string input,
        string rootDirectory,
        string homeDirectory,
        out FilePathSuggestionRequest? request)
    {
        request = null;
        if (!input.StartsWith(DirectShellPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        int bangLength = input.StartsWith("!!", StringComparison.Ordinal) ? 2 : 1;
        string rest = input[bangLength..];

        // A path typed directly after the bang (e.g. "!./script.sh" or "!~/notes.txt")
        // should complete like an argument token too, without requiring a command name
        // and a separating space first.
        if (IsPathToken(rest))
        {
            request = CreatePathRequest(
                rootDirectory,
                input[..bangLength],
                rest.TrimStart(),
                homeDirectory,
                jsonOnly: false,
                preserveTypedPath: true);
            return true;
        }

        // Only complete an argument token: there must be a command name followed by
        // whitespace after the bang prefix(es). "!cd" is still naming the command, while
        // "!cd ./src" (or a trailing space) is completing an argument.
        if (!rest.TrimStart().Any(char.IsWhiteSpace))
        {
            return false;
        }

        int lastWhitespaceIndex = -1;
        for (int index = input.Length - 1; index >= bangLength; index--)
        {
            if (char.IsWhiteSpace(input[index]))
            {
                lastWhitespaceIndex = index;
                break;
            }
        }

        if (lastWhitespaceIndex < 0)
        {
            return false;
        }

        string commandPrefix = input[..(lastWhitespaceIndex + 1)];
        string pathText = input[(lastWhitespaceIndex + 1)..];
        if (Path.IsPathRooted(pathText))
        {
            return false;
        }

        request = CreatePathRequest(
            rootDirectory,
            commandPrefix,
            pathText,
            homeDirectory,
            jsonOnly: false,
            preserveTypedPath: true);
        return true;
    }

    private static bool TryCreateRequest(
        string input,
        string commandPrefix,
        string rootDirectory,
        string homeDirectory,
        bool jsonOnly,
        out FilePathSuggestionRequest? request)
    {
        request = null;
        if (!input.StartsWith(commandPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string pathText = input[commandPrefix.Length..];
        if (Path.IsPathRooted(pathText))
        {
            return false;
        }

        request = CreatePathRequest(
            rootDirectory,
            commandPrefix,
            pathText,
            homeDirectory,
            jsonOnly,
            preserveTypedPath: false);
        return true;
    }

    private static FilePathSuggestionRequest CreatePathRequest(
        string rootDirectory,
        string commandPrefix,
        string pathText,
        string homeDirectory,
        bool jsonOnly,
        bool preserveTypedPath)
    {
        string literalPathText = pathText;
        string resolvedPathText = pathText;
        string baseDirectory = rootDirectory;

        if (TryExpandTilde(pathText, homeDirectory, out string? expandedPath, out string? tildeBase))
        {
            resolvedPathText = expandedPath;
            baseDirectory = tildeBase;
        }

        return new FilePathSuggestionRequest(
            baseDirectory,
            commandPrefix,
            resolvedPathText,
            literalPathText,
            jsonOnly,
            preserveTypedPath);
    }

    private static bool IsPathToken(string text)
    {
        string trimmed = text.TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        return trimmed.StartsWith("./", StringComparison.Ordinal) ||
            trimmed.StartsWith("../", StringComparison.Ordinal) ||
            trimmed.StartsWith("~/", StringComparison.Ordinal) ||
            string.Equals(trimmed, ".", StringComparison.Ordinal) ||
            string.Equals(trimmed, "..", StringComparison.Ordinal) ||
            string.Equals(trimmed, "~", StringComparison.Ordinal);
    }

    private static bool TryExpandTilde(
        string pathText,
        string homeDirectory,
        out string? expandedPath,
        out string? baseDirectory)
    {
        expandedPath = null;
        baseDirectory = null;

        string trimmed = pathText.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '~')
        {
            return false;
        }

        if (string.IsNullOrEmpty(homeDirectory))
        {
            return false;
        }

        baseDirectory = homeDirectory;
        if (trimmed.Length == 1)
        {
            expandedPath = EndWithSeparator(homeDirectory);
            return true;
        }

        string afterTilde = trimmed[1..].TrimStart('/');
        expandedPath = string.IsNullOrEmpty(afterTilde)
            ? EndWithSeparator(homeDirectory)
            : Path.Combine(homeDirectory, afterTilde);
        return true;
    }

    private static string EndWithSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static string GetLiteralDirectoryPrefix(string pathText)
    {
        string trimmed = pathText.TrimStart();
        int lastSeparator = trimmed.LastIndexOfAny(DirectorySeparators);
        return lastSeparator < 0
            ? string.Empty
            : trimmed[..(lastSeparator + 1)];
    }

    private static string GetDirectoryPart(string pathText)
    {
        string normalized = NormalizeSeparators(pathText);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        if (normalized.EndsWith(Path.DirectorySeparatorChar))
        {
            return normalized.TrimEnd(Path.DirectorySeparatorChar);
        }

        return Path.GetDirectoryName(normalized) ?? string.Empty;
    }

    private static string GetNamePrefix(string pathText)
    {
        string normalized = NormalizeSeparators(pathText);
        if (normalized.Length == 0 ||
            normalized.EndsWith(Path.DirectorySeparatorChar))
        {
            return string.Empty;
        }

        return Path.GetFileName(normalized);
    }

    private static string NormalizeSeparators(string value)
    {
        string normalized = value.TrimStart();
        foreach (char separator in DirectorySeparators)
        {
            normalized = normalized.Replace(separator, Path.DirectorySeparatorChar);
        }

        return normalized;
    }

    private static bool TryResolveDirectory(
        string rootDirectory,
        string directoryPart,
        out string? fullDirectoryPath)
    {
        fullDirectoryPath = null;
        string fullRoot = Path.GetFullPath(rootDirectory);

        string candidate = Path.IsPathRooted(directoryPart)
            ? Path.GetFullPath(directoryPart)
            : Path.GetFullPath(Path.Combine(fullRoot, directoryPart));

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!candidate.Equals(fullRoot, comparison) &&
            !candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
        {
            return false;
        }

        fullDirectoryPath = candidate;
        return true;
    }

    private static IEnumerable<DirectoryInfo> EnumerateDirectories(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).EnumerateDirectories();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<FileInfo> EnumerateFiles(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).EnumerateFiles();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool ShouldSkipPath(
        string rootDirectory,
        string path)
    {
        string relativePath = ToDisplayPath(rootDirectory, path);
        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment =>
                string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return IsPathOrChild(relativePath, ".stemcode/cache") ||
            IsPathOrChild(relativePath, ".stemcode/logs") ||
            IsPathOrChild(relativePath, ".stemcode/sessions");
    }

    private static bool IsPathOrChild(
        string relativePath,
        string skippedPath)
    {
        return string.Equals(relativePath, skippedPath, StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith(skippedPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToDisplayPath(
        string rootDirectory,
        string path)
    {
        return Path.GetRelativePath(rootDirectory, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private sealed record FilePathSuggestionRequest(
        string RootDirectory,
        string CommandPrefix,
        string PathText,
        string LiteralPathText,
        bool JsonOnly,
        bool PreserveTypedPath);
}

internal readonly record struct FilePathSuggestion(
    string CompletedInput,
    string DisplayPath,
    string Description,
    bool IsDirectory);
