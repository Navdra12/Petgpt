using System.IO.Compression;
using System.IO;

namespace PetGPT.Characters;

public sealed class CharacterCatalog
{
    private const int MaximumDiagnostics = 64;
    private static readonly TimeSpan AbandonedStagingAge = TimeSpan.FromDays(1);

    private readonly string _bundledRoot;
    private readonly string _installedRoot;
    private readonly string _stagingRoot;
    private readonly PackValidator _validator;
    private readonly Action<string>? _beforePublish;
    private readonly Dictionary<(string Id, string Version), CharacterPack> _resolved = new();

    public CharacterCatalog()
        : this(
            Path.Combine(AppContext.BaseDirectory, "Pets"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PetGPT",
                "Pets"),
            new PackValidator())
    {
    }

    public CharacterCatalog(string bundledRoot, string installedRoot)
        : this(bundledRoot, installedRoot, new PackValidator())
    {
    }

    internal CharacterCatalog(
        string bundledRoot,
        string installedRoot,
        PackValidator validator,
        Action<string>? beforePublish = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(installedRoot);
        _bundledRoot = Path.GetFullPath(bundledRoot);
        _installedRoot = Path.GetFullPath(installedRoot);
        _stagingRoot = Path.Combine(_installedRoot, ".staging");
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _beforePublish = beforePublish;
    }

    public CharacterCatalogResult LoadInstalled()
    {
        var packs = new List<CharacterPack>();
        var diagnostics = new List<string>();
        var bundledIds = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<(string Id, string Version)>();

        if (Directory.Exists(_bundledRoot) && IsReparsePoint(_bundledRoot))
        {
            AddDiagnostic(diagnostics, "reparse_point");
        }
        else if (Directory.Exists(_bundledRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(_bundledRoot).OrderBy(path => path, StringComparer.Ordinal))
            {
                var validation = _validator.ValidateDirectory(directory, CharacterPackSource.Bundled);
                if (!validation.IsValid)
                {
                    AddDiagnostics(diagnostics, validation.DiagnosticCodes);
                    continue;
                }

                var pack = validation.Pack!;
                if (!bundledIds.Add(pack.Id) || !keys.Add((pack.Id, pack.Version)))
                {
                    AddDiagnostic(diagnostics, "duplicate_pack_id");
                    continue;
                }
                packs.Add(pack);
            }
        }

        if (Directory.Exists(_installedRoot) && IsReparsePoint(_installedRoot))
        {
            AddDiagnostic(diagnostics, "reparse_point");
        }
        else if (Directory.Exists(_installedRoot))
        {
            foreach (var idDirectory in Directory.EnumerateDirectories(_installedRoot).OrderBy(path => path, StringComparer.Ordinal))
            {
                if (Path.GetFileName(idDirectory).Equals(".staging", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsReparsePoint(idDirectory))
                {
                    AddDiagnostic(diagnostics, "reparse_point");
                    continue;
                }
                foreach (var versionDirectory in Directory.EnumerateDirectories(idDirectory).OrderBy(path => path, StringComparer.Ordinal))
                {
                    var validation = _validator.ValidateDirectory(versionDirectory, CharacterPackSource.Installed);
                    if (!validation.IsValid)
                    {
                        AddDiagnostics(diagnostics, validation.DiagnosticCodes);
                        continue;
                    }

                    var pack = validation.Pack!;
                    if (bundledIds.Contains(pack.Id))
                    {
                        AddDiagnostic(diagnostics, "bundled_id_shadow");
                        continue;
                    }
                    if (!Path.GetFileName(idDirectory).Equals(pack.Id, StringComparison.Ordinal) ||
                        !Path.GetFileName(versionDirectory).Equals(pack.Version, StringComparison.Ordinal))
                    {
                        AddDiagnostic(diagnostics, "catalog_path_mismatch");
                        continue;
                    }
                    if (!keys.Add((pack.Id, pack.Version)))
                    {
                        AddDiagnostic(diagnostics, "duplicate_pack_id");
                        continue;
                    }
                    packs.Add(pack);
                }
            }
        }

        _resolved.Clear();
        foreach (var pack in packs)
            _resolved.Add((pack.Id, pack.Version), pack);
        return new CharacterCatalogResult(packs, diagnostics);
    }

    public CharacterPack? Resolve(string id, string? version)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
            return null;
        return _resolved.TryGetValue((id, version), out var pack) ? pack : null;
    }

    public Task<CharacterImportResult> ImportAsync(string selectedPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Import(selectedPath, cancellationToken));
    }

    private CharacterImportResult Import(string selectedPath, CancellationToken cancellationToken)
    {
        string? stagingDirectory = null;
        try
        {
            if (string.IsNullOrWhiteSpace(selectedPath))
                return CharacterImportResult.Failure("invalid_source");
            var source = Path.GetFullPath(selectedPath);
            if (!Directory.Exists(source) && !File.Exists(source))
                return CharacterImportResult.Failure("source_missing");
            if (IsReparsePoint(source))
                return CharacterImportResult.Failure("reparse_point");

            Directory.CreateDirectory(_installedRoot);
            if (IsReparsePoint(_installedRoot))
                return CharacterImportResult.Failure("reparse_point");
            Directory.CreateDirectory(_stagingRoot);
            if (IsReparsePoint(_stagingRoot))
                return CharacterImportResult.Failure("reparse_point");
            CleanupAbandonedStaging();
            stagingDirectory = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);

            if (Directory.Exists(source))
            {
                var files = PreflightFolder(source, cancellationToken);
                CopyFolder(source, stagingDirectory, files, cancellationToken);
            }
            else
            {
                if (!Path.GetExtension(source).Equals(".petpack", StringComparison.OrdinalIgnoreCase))
                    return CharacterImportResult.Failure("unsupported_import_type");
                if (new FileInfo(source).Length > _validator.Limits.MaximumArchiveBytes)
                    return CharacterImportResult.Failure("archive_too_large");
                ExtractArchive(source, stagingDirectory, cancellationToken);
            }

            var validation = _validator.ValidateDirectory(stagingDirectory, CharacterPackSource.Installed);
            if (!validation.IsValid)
                return CharacterImportResult.Failure(validation.DiagnosticCodes);

            var candidate = validation.Pack!;
            var bundledIds = LoadBundledIds();
            if (bundledIds.Contains(candidate.Id))
                return CharacterImportResult.Failure("bundled_id_shadow");

            var idRoot = Path.Combine(_installedRoot, candidate.Id);
            var target = Path.Combine(idRoot, candidate.Version);
            if (Directory.Exists(target) || File.Exists(target))
                return CharacterImportResult.Failure("version_exists");
            if (IsExistingReparsePoint(idRoot))
                return CharacterImportResult.Failure("reparse_point");

            Directory.CreateDirectory(idRoot);
            if (IsExistingReparsePoint(idRoot))
                return CharacterImportResult.Failure("reparse_point");
            cancellationToken.ThrowIfCancellationRequested();
            _beforePublish?.Invoke(target);
            if (IsExistingReparsePoint(idRoot) || IsExistingReparsePoint(target))
                return CharacterImportResult.Failure("reparse_point");
            Directory.Move(stagingDirectory, target);
            stagingDirectory = null;

            var published = _validator.ValidateDirectory(target, CharacterPackSource.Installed);
            if (!published.IsValid)
            {
                DeleteTreeIfSafe(target, _installedRoot);
                return CharacterImportResult.Failure("published_validation_failed");
            }
            _resolved[(published.Pack!.Id, published.Pack.Version)] = published.Pack;
            return CharacterImportResult.Success(published.Pack, published.WarningCodes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ImportException exception)
        {
            return CharacterImportResult.Failure(exception.Code);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
        {
            return CharacterImportResult.Failure("import_failed");
        }
        finally
        {
            if (stagingDirectory is not null)
                DeleteStagingDirectory(stagingDirectory);
        }
    }

    private IReadOnlyList<FolderFile> PreflightFolder(string sourceRoot, CancellationToken cancellationToken)
    {
        var files = new List<FolderFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(sourceRoot));
        long expandedBytes = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ImportException("reparse_point");

            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ImportException("reparse_point");
                var relative = Path.GetRelativePath(sourceRoot, entry.FullName).Replace('\\', '/');
                if (!PackValidator.IsSafeRelativePath(relative))
                    throw new ImportException("unsafe_path");
                if (!names.Add(relative))
                    throw new ImportException("case_collision");
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                    continue;
                }

                var file = (FileInfo)entry;
                if (files.Count + 1 > _validator.Limits.MaximumFiles)
                    throw new ImportException("too_many_files");
                expandedBytes = checked(expandedBytes + file.Length);
                if (expandedBytes > _validator.Limits.MaximumExpandedBytes)
                    throw new ImportException("expanded_too_large");
                files.Add(new FolderFile(relative, file.FullName, file.Length));
            }
        }

        return files;
    }

    private static void CopyFolder(
        string sourceRoot,
        string stagingRoot,
        IReadOnlyList<FolderFile> files,
        CancellationToken cancellationToken)
    {
        _ = sourceRoot;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = ResolveStagingPath(stagingRoot, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyBounded(input, output, file.Length, cancellationToken);
        }
    }

    private void ExtractArchive(string archivePath, string stagingRoot, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var planned = new List<ZipArchiveEntry>();
        var names = new Dictionary<string, (string Spelling, bool IsDirectory, bool Explicit)>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (IsArchiveSymlink(entry))
                throw new ImportException("reparse_point");
            var isDirectory = name.EndsWith("/", StringComparison.Ordinal);
            var checkedName = isDirectory ? name.TrimEnd('/') : name;
            if (!PackValidator.IsSafeRelativePath(checkedName))
                throw new ImportException("unsafe_path");
            var components = checkedName.Split('/');
            for (var index = 0; index < components.Length; index++)
            {
                var componentPath = string.Join('/', components.Take(index + 1));
                var explicitEntry = index == components.Length - 1;
                var directory = !explicitEntry || isDirectory;
                if (names.TryGetValue(componentPath, out var existing))
                {
                    if (!existing.Spelling.Equals(componentPath, StringComparison.Ordinal) ||
                        existing.IsDirectory != directory || (explicitEntry && existing.Explicit))
                    {
                        throw new ImportException("case_collision");
                    }
                    if (explicitEntry)
                        names[componentPath] = (componentPath, directory, true);
                }
                else
                {
                    names.Add(componentPath, (componentPath, directory, explicitEntry));
                }
            }
            if (isDirectory)
                continue;
            if (planned.Count + 1 > _validator.Limits.MaximumFiles)
                throw new ImportException("too_many_files");
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > _validator.Limits.MaximumExpandedBytes)
                throw new ImportException("expanded_too_large");
            _ = ResolveStagingPath(stagingRoot, name);
            planned.Add(entry);
        }

        foreach (var entry in planned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = ResolveStagingPath(stagingRoot, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyBounded(input, output, entry.Length, cancellationToken);
        }
    }

    private HashSet<string> LoadBundledIds()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(_bundledRoot))
            return result;
        foreach (var directory in Directory.EnumerateDirectories(_bundledRoot))
        {
            var validation = _validator.ValidateDirectory(directory, CharacterPackSource.Bundled);
            if (validation.IsValid)
                result.Add(validation.Pack!.Id);
        }
        return result;
    }

    private static void CopyBounded(Stream input, Stream output, long expectedLength, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long written = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            written = checked(written + read);
            if (written > expectedLength)
                throw new ImportException("entry_length_changed");
            output.Write(buffer, 0, read);
        }
        if (written != expectedLength)
            throw new ImportException("entry_length_changed");
        output.Flush();
        if (output is FileStream fileOutput)
            fileOutput.Flush(flushToDisk: true);
    }

    private static string ResolveStagingPath(string stagingRoot, string relativePath)
    {
        if (!PackValidator.IsSafeRelativePath(relativePath))
            throw new ImportException("unsafe_path");
        var root = Path.GetFullPath(stagingRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ImportException("unsafe_path");
        return candidate;
    }

    private void CleanupAbandonedStaging()
    {
        if (!Directory.Exists(_stagingRoot))
            return;
        foreach (var directory in Directory.EnumerateDirectories(_stagingRoot))
        {
            try
            {
                var info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || ContainsReparsePoint(info.FullName))
                    continue;
                if (DateTime.UtcNow - info.LastWriteTimeUtc > AbandonedStagingAge)
                    DeleteStagingDirectory(info.FullName);
            }
            catch
            {
                // Staging cleanup is best-effort and cannot affect installed versions.
            }
        }
    }

    private void DeleteStagingDirectory(string path)
    {
        try
        {
            var canonical = Path.GetFullPath(path);
            var prefix = _stagingRoot.EndsWith(Path.DirectorySeparatorChar)
                ? _stagingRoot
                : _stagingRoot + Path.DirectorySeparatorChar;
            if (canonical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                DeleteTreeIfSafe(canonical, _stagingRoot);
        }
        catch
        {
            // Failed staging cleanup is bounded to the staging root.
        }
    }

    private static bool ContainsReparsePoint(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    return true;
                if (entry is DirectoryInfo child)
                    pending.Push(child);
            }
        }
        return false;
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsExistingReparsePoint(string path) =>
        (Directory.Exists(path) || File.Exists(path)) && IsReparsePoint(path);

    private static void DeleteTreeIfSafe(string path, string allowedRoot)
    {
        var canonical = Path.GetFullPath(path);
        var canonicalRoot = Path.GetFullPath(allowedRoot);
        var prefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        if (!canonical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(canonical) || ContainsReparsePoint(canonical))
        {
            return;
        }
        Directory.Delete(canonical, recursive: true);
    }

    private static bool IsArchiveSymlink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static void AddDiagnostics(List<string> target, IEnumerable<string> codes)
    {
        foreach (var code in codes)
            AddDiagnostic(target, code);
    }

    private static void AddDiagnostic(List<string> target, string code)
    {
        if (target.Count < MaximumDiagnostics && !target.Contains(code, StringComparer.Ordinal))
            target.Add(code);
    }

    private sealed record FolderFile(string RelativePath, string SourcePath, long Length);

    private sealed class ImportException(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }
}

public sealed class CharacterCatalogResult
{
    internal CharacterCatalogResult(IEnumerable<CharacterPack> packs, IEnumerable<string> diagnosticCodes)
    {
        Packs = Array.AsReadOnly(packs.ToArray());
        DiagnosticCodes = Array.AsReadOnly(diagnosticCodes.Distinct(StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyList<CharacterPack> Packs { get; }
    public IReadOnlyList<string> DiagnosticCodes { get; }
}

public sealed class CharacterImportResult
{
    private CharacterImportResult(CharacterPack? pack, IEnumerable<string> diagnostics, IEnumerable<string> warnings)
    {
        Pack = pack;
        DiagnosticCodes = Array.AsReadOnly(diagnostics.Distinct(StringComparer.Ordinal).ToArray());
        WarningCodes = Array.AsReadOnly(warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public bool Succeeded => Pack is not null && DiagnosticCodes.Count == 0;
    public CharacterPack? Pack { get; }
    public IReadOnlyList<string> DiagnosticCodes { get; }
    public IReadOnlyList<string> WarningCodes { get; }

    internal static CharacterImportResult Success(CharacterPack pack, IEnumerable<string> warnings) =>
        new(pack, [], warnings);

    internal static CharacterImportResult Failure(params string[] diagnostics) =>
        new(null, diagnostics, []);

    internal static CharacterImportResult Failure(IEnumerable<string> diagnostics) =>
        new(null, diagnostics, []);
}
