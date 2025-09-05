using System.CommandLine;
using LibGit2Sharp;
using System.Text;
using System.Text.RegularExpressions;

namespace GitMarkdownDiffMarker
{
    internal partial class Program
    {
        static async Task<int> Main(string[] args)
        {
            var sourceGitHashOption = new Option<string?>(
                aliases: ["-s", "--source-git-hash"],
                description: "Source Git hash (optional)");

            var targetGitHashOption = new Option<string?>(
                aliases: ["-t", "--target-git-hash"],
                description: "Target Git hash (optional)");

            var fileOption = new Option<string>(
                aliases: ["-f", "--file"],
                description: "Glob pattern for markdown files to process (e.g., '*.md', 'docs/**/*.md', 'README.md')")
            {
                IsRequired = true
            };

            var rootCommand = new RootCommand("Highlight Markdown changes between two Git commits or workspace")
            {
                sourceGitHashOption,
                targetGitHashOption,
                fileOption
            };

            rootCommand.SetHandler(async (string? sourceGitHash, string? targetGitHash, string filePattern) =>
            {
                await ProcessMarkdownDiff(sourceGitHash, targetGitHash, filePattern);
            }, sourceGitHashOption, targetGitHashOption, fileOption);

            return await rootCommand.InvokeAsync(args);
        }

        static string RemoveChangeMarkers(string content)
        {
            var lineEnding = DetectLineEnding(content);
            var lines = content.Replace("\r\n", "\n").Split('\n');
            var result = new List<string>();

            // Footer detection helpers
            static bool IsFooter(string s) => s == "<br/><mark>(Change markers generated with [MarkdownGitDiffMarker](https://github.com/thgossler/MarkdownGitDiffMarker))</mark>";

            for (int idx = 0; idx < lines.Length; idx++)
            {
                var line = lines[idx];
                var trimmed = line.Trim();

                // Remove the footer block if present
                if (IsFooter(trimmed))
                {
                    // Skip optional trailing blank line
                    if (idx + 1 < lines.Length && string.IsNullOrWhiteSpace(lines[idx + 1])) idx++;
                    continue;
                }

                if (trimmed.StartsWith("<mark>**[CHANGE]**</mark>") ||
                    trimmed.StartsWith("<mark>**[CHANGE] in table**</mark>") ||
                    trimmed.StartsWith("<mark>**[CHANGE] in figure**</mark>"))
                {
                    // Also skip a single empty line that may follow a banner we inserted
                    if (idx + 1 < lines.Length && string.IsNullOrWhiteSpace(lines[idx + 1]))
                    {
                        idx++; // skip following blank line
                    }
                    continue;
                }

                // Remove inline markers
                var processedLine = line;
                processedLine = Regex.Replace(processedLine, @"<mark>(.*?)</mark>", "$1");
                processedLine = Regex.Replace(processedLine, @"<ins>(.*?)</ins>", "$1");
                // Remove strike-through wrapper anywhere
                processedLine = Regex.Replace(processedLine, @"~~(.*?)~~", "$1");
                // Remove OLD:/NEW: prefixes we add for figures
                processedLine = processedLine.Replace("OLD:<br/>", string.Empty).Replace("NEW:<br/>", string.Empty);
                // Remove trailing explicit <br/> we inserted for figure pairs
                processedLine = Regex.Replace(processedLine, @"<br/>\s*$", string.Empty);

                result.Add(processedLine);
            }

            var finalResult = new List<string>();
            bool inSummarySection = false;
            foreach (var l in result)
            {
                if (l.Trim() == "## Summary of Changes") { inSummarySection = true; continue; }
                if (!inSummarySection) finalResult.Add(l);
            }

            while (finalResult.Count > 0 && string.IsNullOrWhiteSpace(finalResult[^1]))
            {
                finalResult.RemoveAt(finalResult.Count - 1);
            }

            return string.Join(lineEnding, finalResult);
        }

        static string DetectLineEnding(string content)
        {
            // Detect line ending style: prioritize \r\n, then \n, default to system
            if (content.Contains("\r\n"))
                return "\r\n";
            if (content.Contains('\n'))
                return "\n";
            
            // Default to system line ending
            return Environment.NewLine;
        }

        static async Task ProcessMarkdownDiff(string? sourceGitHash, string? targetGitHash, string filePattern)
        {
            try
            {
                // First resolve glob pattern to actual files
                var matchingFiles = ResolveGlobPattern(filePattern);
                
                if (matchingFiles.Count == 0)
                {
                    Console.Error.WriteLine($"No files found matching pattern: {filePattern}");
                    return;
                }

                // Filter to only markdown files
                var markdownFiles = matchingFiles.Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).ToList();
                
                if (markdownFiles.Count == 0)
                {
                    Console.Error.WriteLine($"No markdown files found matching pattern: {filePattern}");
                    return;
                }

                // Now find Git repository, trying from file locations if needed
                var repoPath = FindGitRepository(markdownFiles);
                if (repoPath == null)
                {
                    Console.Error.WriteLine("No Git repository found in current directory, parent directories, or near the specified files.");
                    return;
                }

                using var repo = new Repository(repoPath);

                // Validate argument combinations
                if (sourceGitHash == null && targetGitHash != null)
                {
                    Console.Error.WriteLine("Invalid combination: TargetGitHash specified without SourceGitHash.");
                    Console.Error.WriteLine("Use one of the following patterns:");
                    Console.Error.WriteLine("  - File pattern only: Compare workspace with HEAD");
                    Console.Error.WriteLine("  - SourceGitHash + File pattern: Compare workspace with specified commit");
                    Console.Error.WriteLine("  - Both hashes + File pattern: Compare two commits");
                    return;
                }

                // Validate Git hashes if provided
                if (sourceGitHash != null && !IsValidGitHash(repo, sourceGitHash))
                {
                    Console.Error.WriteLine($"Error: Invalid or non-existent Git hash: {sourceGitHash}");
                    return;
                }

                if (targetGitHash != null && !IsValidGitHash(repo, targetGitHash))
                {
                    Console.Error.WriteLine($"Error: Invalid or non-existent Git hash: {targetGitHash}");
                    return;
                }

                Console.WriteLine($"Found {markdownFiles.Count} markdown file(s) matching pattern '{filePattern}'");

                if (sourceGitHash == null && targetGitHash == null)
                {
                    // Compare workspace changes with HEAD commit
                    await CompareWorkspaceWithCommit(repo, "HEAD", markdownFiles);
                }
                else if (sourceGitHash != null && targetGitHash == null)
                {
                    // Compare workspace changes with specified commit
                    await CompareWorkspaceWithCommit(repo, sourceGitHash, markdownFiles);
                }
                else if (sourceGitHash != null && targetGitHash != null)
                {
                    // Compare both commits and don't consider workspace changes
                    var src = ResolveCommit(repo, sourceGitHash);
                    var dst = ResolveCommit(repo, targetGitHash);
                    await CompareTwoCommits(repo, src!, dst!, markdownFiles);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
            }
        }

        static bool IsValidGitHash(Repository repo, string commitish)
        {
            try
            {
                var commit = ResolveCommit(repo, commitish);
                return commit != null;
            }
            catch
            {
                return false;
            }
        }

        static async Task CompareWorkspaceWithCommit(Repository repo, string commitish, List<string> specificFiles)
        {
            var commit = ResolveCommit(repo, commitish);
            if (commit == null)
            {
                Console.Error.WriteLine($"Commit {commitish} not found.");
                return;
            }

            Console.WriteLine($"Comparing workspace changes with commit {commit.Sha[..7]}");

            foreach (var file in specificFiles)
            {
                Console.WriteLine($"Processing {file}...");
                await ProcessSpecificWorkspaceFile(repo, commit, file);
            }
        }

        static async Task CompareTwoCommits(Repository repo, Commit sourceCommit, Commit targetCommit, List<string> specificFiles)
        {
            if (sourceCommit == null)
            {
                Console.Error.WriteLine($"Source commit {sourceCommit} not found.");
                return;
            }

            if (targetCommit == null)
            {
                Console.Error.WriteLine($"Target commit {targetCommit} not found.");
                return;
            }

            Console.WriteLine($"Comparing commits {sourceCommit.Sha[..7]} -> {targetCommit.Sha[..7]}");

            foreach (var file in specificFiles)
            {
                Console.WriteLine($"Processing {file}...");
                await ProcessSpecificCommitFile(repo, sourceCommit, targetCommit, file);
            }
        }

        static async Task ProcessSpecificWorkspaceFile(Repository repo, Commit commit, string specificFile)
        {
            var resolvedPath = ResolveFilePath(specificFile);
            if (resolvedPath == null)
            {
                Console.Error.WriteLine($"File not found: {specificFile}");
                return;
            }

            var repoRelativePath = GetRepositoryRelativePath(repo, resolvedPath);
            if (repoRelativePath == null)
            {
                Console.Error.WriteLine($"File is not within the repository: {specificFile}");
                return;
            }

            string oldContent = "";
            string newContent = "";

            try
            {
                // Get content from commit
                var oldBlob = commit.Tree[repoRelativePath]?.Target as Blob;
                if (oldBlob != null)
                {
                    oldContent = oldBlob.GetContentText();
                }

                // Get content from workspace
                if (File.Exists(resolvedPath))
                {
                    newContent = await File.ReadAllTextAsync(resolvedPath);
                }

                var changesSummary = new List<string>();
                var result = GenerateMarkdownDiff(oldContent, newContent, repoRelativePath, changesSummary);
                
                // Overwrite the original file in-place
                await File.WriteAllTextAsync(resolvedPath, result);

                Console.WriteLine($"Updated file in-place: {resolvedPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error processing {specificFile}: {ex.Message}");
            }
        }

        static async Task ProcessSpecificCommitFile(Repository repo, Commit sourceCommit, Commit targetCommit, string specificFile)
        {
            var resolvedPath = ResolveFilePath(specificFile);
            if (resolvedPath == null)
            {
                Console.Error.WriteLine($"File not found: {specificFile}");
                return;
            }

            var repoRelativePath = GetRepositoryRelativePath(repo, resolvedPath);
            if (repoRelativePath == null)
            {
                Console.Error.WriteLine($"File is not within the repository: {specificFile}");
                return;
            }

            string oldContent = "";
            string newContent = "";

            try
            {
                // Get content from source commit
                var oldBlob = sourceCommit.Tree[repoRelativePath]?.Target as Blob;
                if (oldBlob != null)
                {
                    oldContent = oldBlob.GetContentText();
                }

                // Get content from target commit
                var newBlob = targetCommit.Tree[repoRelativePath]?.Target as Blob;
                if (newBlob != null)
                {
                    newContent = newBlob.GetContentText();
                }

                var changesSummary = new List<string>();
                var result = GenerateMarkdownDiff(oldContent, newContent, repoRelativePath, changesSummary);
                
                // Overwrite the original file in-place
                await File.WriteAllTextAsync(resolvedPath, result);

                Console.WriteLine($"Updated file in-place: {resolvedPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error processing {specificFile}: {ex.Message}");
            }
        }

        static string? GetRepositoryRelativePath(Repository repo, string absolutePath)
        {
            try
            {
                var repoWorkingDir = repo.Info.WorkingDirectory;
                var fullPath = Path.GetFullPath(absolutePath);
                
                if (fullPath.StartsWith(repoWorkingDir, StringComparison.OrdinalIgnoreCase))
                {
                    var relativePath = Path.GetRelativePath(repoWorkingDir, fullPath);
                    return relativePath.Replace('\\', '/'); // Git uses forward slashes
                }
                
                return null;
            }
            catch
            {
                return null;
            }
        }

        static string? ResolveFilePath(string filePath)
        {
            try
            {
                // If it's already an absolute path and exists, return it
                if (Path.IsPathRooted(filePath) && File.Exists(filePath))
                {
                    return filePath;
                }

                // Try as relative path from current directory
                var relativePath = Path.Combine(Directory.GetCurrentDirectory(), filePath);
                if (File.Exists(relativePath))
                {
                    return relativePath;
                }

                // Return null if file not found
                return null;
            }
            catch
            {
                return null;
            }
        }

        static Commit? ResolveCommit(Repository repo, string commitish)
        {
            try
            {
                // Try to resolve as a direct commit hash first
                var commit = repo.Lookup<Commit>(commitish);
                if (commit != null)
                {
                    return commit;
                }

                // Handle HEAD~n notation
                if (commitish.StartsWith("HEAD~"))
                {
                    if (int.TryParse(commitish.Substring(5), out int stepsBack))
                    {
                        var headCommit = repo.Head.Tip;
                        var currentCommit = headCommit;
                        
                        for (int i = 0; i < stepsBack && currentCommit?.Parents.Any() == true; i++)
                        {
                            currentCommit = currentCommit.Parents.First();
                        }
                        
                        return currentCommit;
                    }
                }

                // Handle HEAD notation
                if (commitish == "HEAD")
                {
                    return repo.Head.Tip;
                }

                // Try to resolve as a reference (branch name, tag, etc.)
                var reference = repo.Refs[commitish];
                if (reference != null)
                {
                    return reference.ResolveToDirectReference()?.Target as Commit;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        static string GenerateMarkdownDiff(string oldContent, string newContent, string filePath, List<string> changesSummary)
        {
            // Use cross-platform temporary directory and file naming
            var tempDir = Path.GetTempPath();
            var safeGuid = Guid.NewGuid().ToString("N");
            var oldFile = Path.Combine(tempDir, $"old_{safeGuid}.md");
            var newFile = Path.Combine(tempDir, $"new_{safeGuid}.md");
            var cleanNewFile = Path.Combine(tempDir, $"clean_new_{safeGuid}.md");
            
            try
            {
                // Use UTF-8 without BOM to avoid encoding issues
                var utf8NoBom = new System.Text.UTF8Encoding(false);
                File.WriteAllText(oldFile, oldContent, utf8NoBom);
                File.WriteAllText(newFile, newContent, utf8NoBom);
                
                // Create a clean version of new content (without any existing markers)
                var cleanNewContent = RemoveChangeMarkers(newContent);
                File.WriteAllText(cleanNewFile, cleanNewContent, utf8NoBom);
                
                // Get Git diff output using clean content
                var gitDiff = GetGitDiff(oldFile, cleanNewFile);
                
                // Parse diff and apply markdown markers intelligently
                var result = ApplyIntelligentDiffMarkers(cleanNewContent, gitDiff, changesSummary);

                // Append footer block
                var lineEnding = DetectLineEnding(newContent);
                var sb = new StringBuilder(result);
                if (!result.EndsWith(lineEnding)) sb.Append(lineEnding);
                sb.Append(lineEnding);
                sb.Append("<br/><mark>_(Change markers generated with [MarkdownGitDiffMarker](https://github.com/thgossler/MarkdownGitDiffMarker))_</mark>").Append(lineEnding);
                
                return sb.ToString();
            }
            finally
            {
                // Clean up temporary files
                CleanupTempFile(oldFile);
                CleanupTempFile(newFile);
                CleanupTempFile(cleanNewFile);
            }
        }

        static string ApplyIntelligentDiffMarkers(string newContent, string gitDiff, List<string> changesSummary)
        {
            var lineEnding = DetectLineEnding(newContent);
            var newLines = newContent.Replace("\r\n", "\n").Split('\n');
            var result = new StringBuilder();

            // -------- Local helpers --------
            bool IsSeparatorRow(string s) => Regex.IsMatch(s.Trim(), @"^\|?\s*(:?-+\s*:?-?\s*\|)+\s*:?-+\s*:?-?\s*$");
            bool IsHeading(string line, out string hashes, out string space, out string rest)
            {
                var m = Regex.Match(line, @"^\s{0,3}(#{1,6})(\s*)(.*)$");
                if (m.Success)
                { hashes = m.Groups[1].Value; space = m.Groups[2].Value; rest = m.Groups[3].Value; return true; }
                hashes = space = rest = string.Empty; return false;
            }
            bool IsImageLine(string line) => Regex.IsMatch(line.Trim(), @"^!\[[^\]]*\]\([^\)]*\)");
            string LeadingWhitespace(string s) { int n = 0; while (n < s.Length && (s[n] == ' ' || s[n] == '\t')) n++; return n > 0 ? s.Substring(0, n) : string.Empty; }
            int GetBulletPrefixLen(string s) { var m = Regex.Match(s, @"^(\s*(?:[\-\*\+]\s+|\d+[\.)]\s+))"); return m.Success ? m.Groups[1].Length : 0; }
            bool IsBulletLine(string s) => GetBulletPrefixLen(s) > 0;
            int FindPriorBulletPrefixLen(int lineIndex)
            {
                for (int j = lineIndex - 1; j >= Math.Max(1, lineIndex - 6); j--)
                {
                    var prev = newLines[j - 1];
                    if (string.IsNullOrWhiteSpace(prev)) break;
                    if (IsHeading(prev, out _, out _, out _)) break;
                    int len = GetBulletPrefixLen(prev);
                    if (len > 0) return len;
                }
                return 0;
            }
            string WrapOutsideTable(string line)
            {
                if (string.IsNullOrWhiteSpace(line)) return line;
                if (IsHeading(line, out var hashes, out var space, out var rest))
                { var prefix = line[..line.IndexOf(hashes, StringComparison.Ordinal)]; return $"{prefix}{hashes}{space}<mark>{rest}</mark>"; }
                var m = Regex.Match(line, @"^\s*([\-\*\+]\s+|\d+[\.)]\s+)(.*)$");
                if (m.Success) { var prefix = m.Groups[1].Value; var body = m.Groups[2].Value; return $"{prefix}<mark>{body}</mark>"; }
                return $"<mark>{line}</mark>";
            }
            string WrapDeletionOutsideTable(string line)
            {
                if (string.IsNullOrWhiteSpace(line)) return line;
                if (IsHeading(line, out var hashes, out var space, out var rest))
                { var prefix = line[..line.IndexOf(hashes, StringComparison.Ordinal)]; return $"{prefix}{hashes}{space}<mark>~~{rest}~~</mark>"; }
                var m = Regex.Match(line, @"^\s*([\-\*\+]\s+|\d+[\.)]\s+)(.*)$");
                if (m.Success) { var prefix = m.Groups[1].Value; var body = m.Groups[2].Value; return $"{prefix}<mark>~~{body}~~</mark>"; }
                return $"<mark>~~{line}~~</mark>";
            }
            string WrapTableCells(string line)
            {
                var cells = line.Split('|');
                for (int i = 0; i < cells.Length; i++)
                {
                    var cell = cells[i];
                    if (string.IsNullOrEmpty(cell)) continue;
                    var trimmed = cell.Trim();
                    if (trimmed.Length == 0) continue;
                    if (Regex.IsMatch(trimmed, @"^:?-+:?$")) continue;
                    int leftSpaces = 0, rightSpaces = 0;
                    while (leftSpaces < cell.Length && cell[leftSpaces] == ' ') leftSpaces++;
                    while (rightSpaces < cell.Length - leftSpaces && cell[cell.Length - 1 - rightSpaces] == ' ') rightSpaces++;
                    var left = new string(' ', leftSpaces);
                    var right = new string(' ', rightSpaces);
                    cells[i] = $"{left}<mark>{trimmed}</mark>{right}";
                }
                return string.Join("|", cells);
            }
            string WrapDeletedTableCells(string line)
            {
                var cells = line.Split('|');
                for (int i = 0; i < cells.Length; i++)
                {
                    var cell = cells[i]; if (string.IsNullOrEmpty(cell)) continue; var trimmed = cell.Trim(); if (trimmed.Length == 0) continue; if (Regex.IsMatch(trimmed, @"^:?-+:?$")) continue;
                    int leftSpaces = 0, rightSpaces = 0; while (leftSpaces < cell.Length && cell[leftSpaces] == ' ') leftSpaces++; while (rightSpaces < cell.Length - leftSpaces && cell[cell.Length - 1 - rightSpaces] == ' ') rightSpaces++;
                    var left = new string(' ', leftSpaces); var right = new string(' ', rightSpaces);
                    cells[i] = $"{left}<mark>~~{trimmed}~~</mark>{right}";
                }
                return string.Join("|", cells);
            }
            string InlineBulletChange(string line, bool deletion)
            {
                var m = Regex.Match(line, @"^(\s*(?:[\-\*\+]\s+|\d+[\.)]\s+))(.*)$");
                if (!m.Success) return deletion ? WrapDeletionOutsideTable(line) : WrapOutsideTable(line);
                var prefix = m.Groups[1].Value; var body = m.Groups[2].Value;
                var bodyWrapped = deletion ? $"<mark>~~{body}~~</mark>" : $"<mark>{body}</mark>";
                return $"{prefix}<mark>**[CHANGE]**</mark> {bodyWrapped}";
            }
            // -------- End helpers --------

            // Parse git diff -> new-side changed lines and deletions-before map
            var changedNewLineNumbers = new HashSet<int>();
            var deletionsBefore = new Dictionary<int, List<string>>();
            int oldLineNo = 0, newLineNo = 0;
            foreach (var dl in gitDiff.Replace("\r\n", "\n").Split('\n'))
            {
                if (dl.StartsWith("@@"))
                {
                    var m = Regex.Match(dl, @"@@\s*-(\d+)(?:,(\d+))?\s*\+(\d+)(?:,(\d+))?\s*@@");
                    if (m.Success) { oldLineNo = int.Parse(m.Groups[1].Value); newLineNo = int.Parse(m.Groups[3].Value); }
                    continue;
                }
                if (dl.StartsWith("+++") || dl.StartsWith("---") || dl.Length == 0) continue;
                if (dl[0] == ' ') { oldLineNo++; newLineNo++; }
                else if (dl[0] == '+') { changedNewLineNumbers.Add(newLineNo); newLineNo++; }
                else if (dl[0] == '-')
                {
                    var content = dl.Length > 1 ? dl.Substring(1) : string.Empty;
                    if (!string.IsNullOrWhiteSpace(content))
                    { if (!deletionsBefore.TryGetValue(newLineNo, out var list)) { list = new List<string>(); deletionsBefore[newLineNo] = list; } list.Add(content); }
                    oldLineNo++;
                }
            }

            // Detect table regions
            var tableRegions = new List<(int start, int end)>();
            int? currentTableStart = null;
            for (int i = 0; i < newLines.Length; i++)
            {
                var t = newLines[i].Trim();
                bool looksTable = t.Contains('|');
                if (looksTable) { if (currentTableStart == null) currentTableStart = i + 1; }
                else { if (currentTableStart != null) { tableRegions.Add((currentTableStart.Value, i)); currentTableStart = null; } }
            }
            if (currentTableStart != null) tableRegions.Add((currentTableStart.Value, newLines.Length));

            var changedTableStarts = new HashSet<int>();
            foreach (var (start, end) in tableRegions)
            {
                for (int ln = start; ln <= end; ln++) if (changedNewLineNumbers.Contains(ln)) { changedTableStarts.Add(start); break; }
            }

            // Split deletions into table vs non-table
            var tableDeletionsByNewLine = new Dictionary<int, List<string>>();
            foreach (var kv in deletionsBefore.ToList())
            {
                var reg = tableRegions.FirstOrDefault(r => kv.Key >= r.start && kv.Key <= r.end);
                if (reg.start != 0) { tableDeletionsByNewLine[kv.Key] = kv.Value.ToList(); deletionsBefore.Remove(kv.Key); }
            }

            // Emit
            var tableBannerEmitted = new HashSet<int>();
            bool inRun = false; // for grouping consecutive changed lines outside tables
            bool bulletChangeMarkerEmitted = false; // inline bullet [CHANGE] emitted for current run

            for (int i = 1; i <= newLines.Length; i++)
            {
                var line = newLines[i - 1];
                bool inTable = tableRegions.Any(r => i >= r.start && i <= r.end);
                bool isChanged = changedNewLineNumbers.Contains(i);

                // If at table start and table has changes or deletions -> banner once
                var region = tableRegions.FirstOrDefault(r => r.start == i);
                bool tableHasDeletionsHere = region.start != 0 && tableDeletionsByNewLine.Keys.Any(k => k >= region.start && k <= region.end);
                if (region.start == i && (changedTableStarts.Contains(region.start) || tableHasDeletionsHere) && !tableBannerEmitted.Contains(region.start))
                { result.Append($"<mark>**[CHANGE] in table**</mark>{lineEnding}{lineEnding}"); tableBannerEmitted.Add(region.start); }

                if (inTable)
                {
                    if (tableDeletionsByNewLine.TryGetValue(i, out var dels))
                        foreach (var d in dels) { if (IsSeparatorRow(d)) result.Append(d).Append(lineEnding); else result.Append(WrapDeletedTableCells(d)).Append(lineEnding); }

                    if (isChanged)
                    { if (IsSeparatorRow(line)) result.Append(line).Append(lineEnding); else result.Append(WrapTableCells(line)).Append(lineEnding); }
                    else result.Append(line).Append(lineEnding);
                    continue;
                }

                // Non-table deletions mapped to this line
                if (deletionsBefore.TryGetValue(i, out var delList) && delList.Count > 0)
                {
                    bool newIsImg = IsImageLine(line); bool anyDelImg = delList.Any(IsImageLine);
                    if (newIsImg || anyDelImg)
                    {
                        result.Append($"<mark>**[CHANGE] in figure**</mark>{lineEnding}");
                        var oldImg = delList.FirstOrDefault(IsImageLine);
                        if (!string.IsNullOrEmpty(oldImg)) result.Append($"<mark><br/>OLD:<br/>{oldImg}</mark><br/>{lineEnding}");
                        if (newIsImg) result.Append($"<mark>NEW:<br/>{line}</mark>{lineEnding}"); else result.Append(line).Append(lineEnding);
                        inRun = false; bulletChangeMarkerEmitted = false; continue;
                    }

                    var banner = "<mark>**[CHANGE]**</mark>";
                    bool anyBulletDel = delList.Any(IsBulletLine);
                    bool bannerEmitted = false;
                    bool bulletDelChipEmitted = false;
                    if (!anyBulletDel)
                    {
                        result.Append($"{banner}{lineEnding}");
                        bannerEmitted = true;
                    }
                    foreach (var d in delList)
                    {
                        if (IsBulletLine(d))
                        {
                            if (!bulletDelChipEmitted)
                            {
                                result.Append(InlineBulletChange(d, deletion: true)).Append(lineEnding);
                                bulletDelChipEmitted = true;
                            }
                            else
                            {
                                // Additional deleted bullets in same cluster: deletion body only
                                result.Append(WrapDeletionOutsideTable(d)).Append(lineEnding);
                            }
                        }
                        else
                        {
                            if (!bannerEmitted)
                            {
                                result.Append($"{banner}{lineEnding}");
                                bannerEmitted = true;
                            }
                            result.Append(WrapDeletionOutsideTable(d)).Append(lineEnding);
                        }
                    }

                    // Mark that a change run is active if we emitted any marker (banner or first bullet chip)
                    if (bannerEmitted || bulletDelChipEmitted)
                    {
                        inRun = true;
                        if (bannerEmitted || bulletDelChipEmitted) bulletChangeMarkerEmitted = true;
                    }

                    // Now handle current (new) line impacted by deletions
                    if (isChanged)
                    {
                        if (IsBulletLine(line))
                        {
                            if (!bulletChangeMarkerEmitted)
                            {
                                result.Append(InlineBulletChange(line, deletion: false)).Append(lineEnding);
                                bulletChangeMarkerEmitted = true;
                            }
                            else
                            {
                                // Subsequent bullets in the same run: body only
                                result.Append(WrapOutsideTable(line)).Append(lineEnding);
                            }
                        }
                        else if (FindPriorBulletPrefixLen(i) > 0)
                        {
                            if (string.IsNullOrWhiteSpace(line)) result.Append(line).Append(lineEnding);
                            else { var ws = LeadingWhitespace(line); var body = line.Substring(ws.Length); result.Append(ws).Append($"<mark>{body}</mark>").Append(lineEnding); }
                        }
                        else result.Append(WrapOutsideTable(line)).Append(lineEnding);
                    }
                    else result.Append(line).Append(lineEnding);

                    // Do not reset inRun/bulletChangeMarkerEmitted here; continue the same change block
                    continue;
                }

                // Changed (non-table) lines, no deletions mapped here
                if (isChanged)
                {
                    // Handle images first: emit only figure banner + NEW, no generic banner
                    if (IsImageLine(line))
                    {
                        result.Append($"<mark>**[CHANGE] in figure**</mark>{lineEnding}");
                        result.Append($"<mark>NEW:<br/>{line}</mark>{lineEnding}");
                        inRun = false; bulletChangeMarkerEmitted = false; // isolate figures
                        continue;
                    }

                    bool inListContext = IsBulletLine(line) || FindPriorBulletPrefixLen(i) > 0;
                    if (!inRun)
                    {
                        if (!inListContext)
                        {
                            result.Append($"<mark>**[CHANGE]**</mark>{lineEnding}");
                        }
                        inRun = true;
                        // If a generic banner was emitted for this run, consider the run already "marked"
                        bulletChangeMarkerEmitted = !inListContext;
                    }
                    if (IsBulletLine(line))
                    {
                        if (!bulletChangeMarkerEmitted)
                        {
                            result.Append(InlineBulletChange(line, deletion: false)).Append(lineEnding);
                            bulletChangeMarkerEmitted = true;
                        }
                        else
                        {
                            // Subsequent bullets in the same run: body only
                            result.Append(WrapOutsideTable(line)).Append(lineEnding);
                        }
                    }
                    else if (inListContext)
                    {
                          if (string.IsNullOrWhiteSpace(line)) result.Append(line).Append(lineEnding);
                          else { var ws = LeadingWhitespace(line); var body = line.Substring(ws.Length); result.Append(ws).Append($"<mark>{body}</mark>").Append(lineEnding); }
                     }
                     else
                     { result.Append(WrapOutsideTable(line)).Append(lineEnding); }
                }
                else
                {
                    inRun = false; bulletChangeMarkerEmitted = false; result.Append(line).Append(lineEnding);
                }
            }

            // Deletions beyond EOF
            int beyond = newLines.Length + 1;
            if (deletionsBefore.TryGetValue(beyond, out var tailDels) && tailDels.Count > 0)
            {
                bool anyImg = tailDels.Any(IsImageLine);
                var banner = anyImg ? "<mark>**[CHANGE] in figure**</mark>" : "<mark>**[CHANGE]**</mark>";
                result.Append($"{banner}{lineEnding}");
                foreach (var d in tailDels)
                { if (anyImg && IsImageLine(d)) result.Append($"<mark><br/>OLD:<br/>{d}</mark>{lineEnding}"); else result.Append(WrapDeletionOutsideTable(d)).Append(lineEnding); }
            }

            return result.ToString();
        }

        static void CleanupTempFile(string filePath)
        {
            try { if (File.Exists(filePath)) { File.SetAttributes(filePath, FileAttributes.Normal); File.Delete(filePath); } } catch { }
        }

        static string GetGitDiff(string oldFile, string newFile)
        {
            try
            {
                using var p = new System.Diagnostics.Process();
                var git = GetGitExecutable();
                if (string.IsNullOrEmpty(git)) return string.Empty;
                p.StartInfo.FileName = git;
                var a = oldFile.Replace('\\','/');
                var b = newFile.Replace('\\','/');
                p.StartInfo.Arguments = $"diff --no-index --unified=0 \"{a}\" \"{b}\"";
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return output;
            }
            catch { return string.Empty; }
        }

        static string GetGitExecutable()
        {
            var names = OperatingSystem.IsWindows() ? new[] { "git.exe", "git" } : new[] { "git" };
            foreach (var n in names) if (IsExecutableInPath(n)) return n;
            if (OperatingSystem.IsWindows())
            {
                foreach (var p in new[]{ @"C:\\Program Files\\Git\\bin\\git.exe", @"C:\\Program Files (x86)\\Git\\bin\\git.exe", @"C:\\Git\\bin\\git.exe" })
                    if (File.Exists(p)) return p;
            }
            return string.Empty;
        }

        static List<string> ResolveGlobPattern(string pattern)
        {
            try
            {
                var results = new List<string>();
                string searchPath, searchPattern;
                if (Path.IsPathRooted(pattern))
                {
                    searchPath = Path.GetDirectoryName(pattern) ?? (OperatingSystem.IsWindows() ? "C:\\" : "/");
                    searchPattern = Path.GetFileName(pattern) ?? "*";
                }
                else
                {
                    searchPath = Directory.GetCurrentDirectory();
                    searchPattern = pattern;
                }

                searchPattern = searchPattern.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

                if (searchPattern.Contains("**"))
                {
                    var parts = searchPattern.Split(new[] {"**"}, StringSplitOptions.RemoveEmptyEntries);
                    var prefix = parts.Length > 0 ? parts[0].TrimEnd(Path.DirectorySeparatorChar) : string.Empty;
                    var suffix = parts.Length > 1 ? parts[1].TrimStart(Path.DirectorySeparatorChar) : "*";
                    var baseDir = string.IsNullOrEmpty(prefix) ? searchPath : Path.Combine(searchPath, prefix);
                    if (Directory.Exists(baseDir)) results.AddRange(Directory.GetFiles(baseDir, suffix, SearchOption.AllDirectories));
                }
                else if (searchPattern.Contains('*') || searchPattern.Contains('?'))
                {
                    if (Directory.Exists(searchPath)) results.AddRange(Directory.GetFiles(searchPath, searchPattern, SearchOption.TopDirectoryOnly));
                }
                else
                {
                    var full = Path.IsPathRooted(pattern) ? pattern : Path.Combine(searchPath, pattern);
                    if (File.Exists(full)) results.Add(full);
                }

                return results.Distinct().ToList();
            }
            catch { return new List<string>(); }
        }

        static string? FindGitRepository(List<string>? filePaths = null)
        {
            if (filePaths != null)
            {
                foreach (var f in filePaths)
                {
                    try
                    {
                        var d = Path.GetDirectoryName(Path.GetFullPath(f));
                        while (d != null)
                        {
                            if (Directory.Exists(Path.Combine(d, ".git"))) return d;
                            d = Directory.GetParent(d)?.FullName;
                        }
                    }
                    catch { }
                }
            }
            var dir = Directory.GetCurrentDirectory();
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir, ".git"))) return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }
            return null;
        }

        static bool IsExecutableInPath(string name)
        {
            try
            {
                using var p = new System.Diagnostics.Process();
                p.StartInfo.FileName = OperatingSystem.IsWindows() ? "where" : "which";
                p.StartInfo.Arguments = name;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                p.WaitForExit();
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        static string[] SplitLines(string content)
        {
            if (string.IsNullOrEmpty(content)) return Array.Empty<string>();
            var normalized = content.Replace("\r\n", "\n");
            return normalized.Split('\n');
        }
    }
}