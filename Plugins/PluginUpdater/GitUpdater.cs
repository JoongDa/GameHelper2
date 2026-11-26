// <copyright file="GitUpdater.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace PluginUpdater
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using LibGit2Sharp;

    /// <summary>Git state of one plugin source repository.</summary>
    public class PluginInfo
    {
        public required string Name { get; set; }
        public required string Path { get; set; }
        public string CurrentCommit { get; set; } = string.Empty;
        public string FullCommit { get; set; } = string.Empty;
        public string LatestCommit { get; set; } = string.Empty;
        public string LatestCommitMessage { get; set; } = string.Empty;
        public string BehindAhead { get; set; } = string.Empty;
        public int BehindBy { get; set; }
        public int AheadBy { get; set; }
        public string LastMessage { get; set; } = string.Empty;
        public string? PreviousCommit { get; set; }
        public int UncommittedChangeCount { get; set; }
        public List<string> AvailableBranches { get; set; } = new();
        public string CurrentBranch { get; set; } = string.Empty;
        public string RemoteUrl { get; set; } = string.Empty;
    }

    /// <summary>Maintains plugin source repositories independently of their compiled DLLs.</summary>
    public sealed class GitUpdater : IDisposable
    {
        private readonly ConcurrentDictionary<string, PluginInfo> plugins = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim operationLock = new(1, 1);
        private readonly CancellationTokenSource lifetime = new();
        private readonly CancellationToken cancellationToken;
        private readonly Action<string>? log;
        private int disposed;

        public GitUpdater(string sourceDirectory, Action<string>? log = null, bool refreshOnStartup = true)
        {
            this.SourceDirectory = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(sourceDirectory));
            this.log = log;
            this.cancellationToken = this.lifetime.Token;
            Directory.CreateDirectory(this.SourceDirectory);
            if (refreshOnStartup)
            {
                this.UpdateLocal();
            }
        }

        public string SourceDirectory { get; }

        public event Action<int, int>? ProgressChanged;

        public List<PluginInfo> GetPluginInfo()
        {
            return this.plugins.Values.OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
                .Select(CopyPluginInfo).ToList();
        }

        public IEnumerable<PluginInfo> GetManualPlugins()
        {
            this.ThrowIfDisposed();
            return this.GetSourceFolders().Where(path => !HasGitMetadata(path))
                .Select(path => new PluginInfo { Name = System.IO.Path.GetFileName(path), Path = path }).ToList();
        }

        public void UpdateLocal()
        {
            this.ThrowIfDisposed();
            this.operationLock.Wait(this.cancellationToken);
            try
            {
                this.RefreshLocal();
            }
            finally
            {
                this.operationLock.Release();
            }
        }

        public Task UpdateGitInfoAsync()
        {
            return this.RunAsync(() =>
            {
                this.RefreshLocal();
                var folders = this.GetSourceFolders().Where(HasGitMetadata).ToList();
                var errors = new List<Exception>();
                this.ProgressChanged?.Invoke(0, folders.Count);
                for (var index = 0; index < folders.Count; index++)
                {
                    this.cancellationToken.ThrowIfCancellationRequested();
                    var name = System.IO.Path.GetFileName(folders[index]);
                    try
                    {
                        using var repository = new Repository(folders[index]);
                        var remote = GetHeadRemote(repository);
                        this.Fetch(repository, remote);
                        EnsureTrackingBranch(repository, remote);
                        this.Publish(name, repository);
                    }
                    catch (Exception ex) when (!this.cancellationToken.IsCancellationRequested)
                    {
                        errors.Add(new InvalidOperationException($"{name}: {ex.Message}", ex));
                        var info = this.plugins.TryGetValue(name, out var current)
                            ? CopyPluginInfo(current)
                            : new PluginInfo { Name = name, Path = folders[index] };
                        info.LastMessage = ex.Message;
                        this.plugins[name] = info;
                        this.log?.Invoke($"Cannot check {name}: {ex.Message}");
                    }
                    finally
                    {
                        this.ProgressChanged?.Invoke(index + 1, folders.Count);
                    }
                }

                if (errors.Count > 0)
                {
                    throw new AggregateException("Some plugin repositories could not be checked.", errors);
                }
            });
        }

        public Task UpdatePluginAsync(string pluginName)
        {
            return this.RunAsync(() =>
            {
                using var repository = this.OpenRepository(pluginName);
                var remote = GetHeadRemote(repository);
                this.Fetch(repository, remote);
                EnsureTrackingBranch(repository, remote);
                if (repository.Head.TrackedBranch == null)
                {
                    throw new InvalidOperationException("The current branch has no remote tracking branch.");
                }

                var result = Commands.Pull(repository,
                    new Signature("Plugin Updater", "updater@local", DateTimeOffset.Now),
                    new PullOptions
                    {
                        FetchOptions = this.CreateFetchOptions(),
                    });
                if (result.Status == MergeStatus.Conflicts)
                {
                    const string message = "The update has merge conflicts. Resolve them in the source folder or use Force update.";
                    this.Publish(pluginName, repository, message);
                    throw new InvalidOperationException(message);
                }

                this.Publish(pluginName, repository, result.Status == MergeStatus.UpToDate
                    ? "Already up to date"
                    : $"Updated to {ShortCommit(repository.Head.Tip)}");
            });
        }

        public Task ForceUpdatePluginAsync(string pluginName)
        {
            return this.RunAsync(() =>
            {
                using var repository = this.OpenRepository(pluginName);
                var remote = GetHeadRemote(repository);
                this.Fetch(repository, remote);
                EnsureTrackingBranch(repository, remote);
                var target = repository.Head.TrackedBranch?.Tip
                    ?? throw new InvalidOperationException("The current branch has no remote tracking branch.");
                repository.Reset(ResetMode.Hard, target);
                this.Publish(pluginName, repository, $"Force updated to {ShortCommit(target)}");
            });
        }

        public Task RevertPluginAsync(string pluginName)
        {
            return this.RunAsync(() =>
            {
                using var repository = this.OpenRepository(pluginName);
                var parent = repository.Head.Tip?.Parents.FirstOrDefault()
                    ?? throw new InvalidOperationException("Cannot revert a repository with no parent commit.");
                repository.Reset(ResetMode.Hard, parent);
                this.Publish(pluginName, repository, $"Reset to parent commit {ShortCommit(parent)}");
            });
        }

        public Task ChangeBranchAsync(string pluginName, string branchName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
            return this.RunAsync(() =>
            {
                using var repository = this.OpenRepository(pluginName);
                foreach (var remote in repository.Network.Remotes)
                {
                    this.Fetch(repository, remote);
                }

                var selected = repository.Branches[branchName]
                    ?? repository.Branches[$"{GetHeadRemote(repository).Name}/{branchName}"]
                    ?? throw new InvalidOperationException($"Branch '{branchName}' was not found.");
                var branch = selected;
                if (selected.IsRemote)
                {
                    branch = repository.Branches.FirstOrDefault(candidate =>
                        !candidate.IsRemote && candidate.TrackedBranch?.CanonicalName == selected.CanonicalName);
                    if (branch == null)
                    {
                        var slash = selected.FriendlyName.IndexOf('/');
                        var localName = selected.FriendlyName[(slash + 1)..];
                        if (repository.Branches[localName] != null)
                        {
                            throw new InvalidOperationException($"Local branch '{localName}' already tracks a different branch.");
                        }

                        branch = repository.CreateBranch(localName, selected.Tip);
                        repository.Branches.Update(branch, item => item.TrackedBranch = selected.CanonicalName);
                    }
                }

                Commands.Checkout(repository, branch);
                this.Publish(pluginName, repository, $"Switched to {branch.FriendlyName}");
            });
        }

        public async Task<PluginInfo> CloneRepositoryAsync(string repoUrl)
        {
            var (url, name, branch) = ParseRepositoryUrl(repoUrl);
            var targetPath = this.GetRepositoryPath(name);
            PluginInfo? result = null;
            await this.RunAsync(() =>
            {
                if (Directory.Exists(targetPath) || File.Exists(targetPath))
                {
                    throw new InvalidOperationException($"A source directory named '{name}' already exists.");
                }

                try
                {
                    var options = new CloneOptions { IsBare = false, Checkout = true };
                    options.FetchOptions.CredentialsProvider = this.GetCredentials;
                    options.FetchOptions.OnTransferProgress = _ => !this.cancellationToken.IsCancellationRequested;
                    if (!string.IsNullOrEmpty(branch))
                    {
                        options.BranchName = branch;
                    }

                    Repository.Clone(url, targetPath, options);
                    this.cancellationToken.ThrowIfCancellationRequested();
                    using var repository = new Repository(targetPath);
                    result = this.Publish(name, repository, "Source downloaded");
                }
                catch
                {
                    if (Directory.Exists(targetPath))
                    {
                        try
                        {
                            DeleteSourceDirectory(targetPath);
                        }
                        catch (Exception cleanupError)
                        {
                            this.log?.Invoke($"Cannot remove incomplete clone '{name}': {cleanupError.Message}");
                        }
                    }

                    throw;
                }
            }).ConfigureAwait(false);
            return CopyPluginInfo(result!);
        }

        public void DeletePlugin(string pluginName)
        {
            this.ThrowIfDisposed();
            var path = this.GetRepositoryPath(pluginName);
            this.operationLock.Wait(this.cancellationToken);
            try
            {
                if (!Directory.Exists(path))
                {
                    throw new DirectoryNotFoundException($"Source directory '{pluginName}' was not found.");
                }

                DeleteSourceDirectory(path);
                this.plugins.TryRemove(pluginName, out _);
            }
            finally
            {
                this.operationLock.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 0)
            {
                this.lifetime.Cancel();
                this.lifetime.Dispose();
            }
        }

        private async Task RunAsync(Action action)
        {
            this.ThrowIfDisposed();
            await this.operationLock.WaitAsync(this.cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Run(action, this.cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (this.cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(this.cancellationToken);
            }
            finally
            {
                this.operationLock.Release();
            }
        }

        private void RefreshLocal()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in this.GetSourceFolders().Where(HasGitMetadata))
            {
                this.cancellationToken.ThrowIfCancellationRequested();
                var name = System.IO.Path.GetFileName(path);
                names.Add(name);
                try
                {
                    using var repository = new Repository(path);
                    this.Publish(name, repository);
                }
                catch (Exception ex)
                {
                    this.plugins[name] = new PluginInfo { Name = name, Path = path, LastMessage = ex.Message };
                    this.log?.Invoke($"Cannot read '{name}': {ex.Message}");
                }
            }

            foreach (var name in this.plugins.Keys.Where(name => !names.Contains(name)))
            {
                this.plugins.TryRemove(name, out _);
            }
        }

        private PluginInfo Publish(string name, Repository repository, string message = "")
        {
            var head = repository.Head;
            var tracked = head.TrackedBranch;
            var localBranches = repository.Branches.Where(branch => !branch.IsRemote).ToList();
            var trackedNames = localBranches.Select(branch => branch.TrackedBranch?.CanonicalName)
                .Where(branch => branch != null).ToHashSet(StringComparer.Ordinal);
            var branches = localBranches.Select(branch => branch.FriendlyName)
                .Concat(repository.Branches.Where(branch => branch.IsRemote &&
                    !branch.FriendlyName.EndsWith("/HEAD", StringComparison.Ordinal) &&
                    !trackedNames.Contains(branch.CanonicalName)).Select(branch => branch.FriendlyName))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var ahead = tracked == null ? 0 : head.TrackingDetails.AheadBy ?? 0;
            var behind = tracked == null ? 0 : head.TrackingDetails.BehindBy ?? 0;
            var parent = head.Tip?.Parents.FirstOrDefault();
            var info = new PluginInfo
            {
                Name = name,
                Path = this.GetRepositoryPath(name),
                CurrentCommit = ShortCommit(head.Tip),
                FullCommit = head.Tip?.Sha ?? string.Empty,
                LatestCommit = ShortCommit(tracked?.Tip ?? head.Tip),
                LatestCommitMessage = tracked?.Tip?.MessageShort ?? string.Empty,
                AheadBy = ahead,
                BehindBy = behind,
                BehindAhead = ahead == 0 && behind == 0 ? string.Empty : $"{behind} behind, {ahead} ahead",
                PreviousCommit = parent == null ? null : $"{ShortCommit(parent)} {parent.MessageShort}",
                UncommittedChangeCount = repository.RetrieveStatus().Count(),
                AvailableBranches = branches,
                CurrentBranch = repository.Info.IsHeadDetached ? "(detached HEAD)" : head.FriendlyName,
                LastMessage = message,
                RemoteUrl = repository.Network.Remotes[tracked?.RemoteName ?? "origin"]?.Url ?? string.Empty,
            };
            this.plugins[name] = info;
            return info;
        }

        private Repository OpenRepository(string name)
        {
            var path = this.GetRepositoryPath(name);
            if (!HasGitMetadata(path))
            {
                throw new InvalidOperationException($"'{name}' has no Git metadata and cannot be updated.");
            }

            return new Repository(path);
        }

        private IEnumerable<string> GetSourceFolders()
        {
            return Directory.GetDirectories(this.SourceDirectory)
                .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0);
        }

        private string GetRepositoryPath(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
                name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
                name.Any(char.IsControl) || name.EndsWith('.') || name.EndsWith(' '))
            {
                throw new ArgumentException("Invalid plugin repository name.", nameof(name));
            }

            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(this.SourceDirectory, name));
            if (!string.Equals(System.IO.Path.GetDirectoryName(path), this.SourceDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The repository must be a direct child of the source directory.", nameof(name));
            }

            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Plugin source directories cannot be directory links.");
            }

            return path;
        }

        private void Fetch(Repository repository, Remote remote)
        {
            this.cancellationToken.ThrowIfCancellationRequested();
            Commands.Fetch(repository, remote.Name, remote.FetchRefSpecs.Select(spec => spec.Specification),
                this.CreateFetchOptions(), null);
            this.cancellationToken.ThrowIfCancellationRequested();
        }

        private FetchOptions CreateFetchOptions()
        {
            return new FetchOptions
            {
                CredentialsProvider = this.GetCredentials,
                OnTransferProgress = _ => !this.cancellationToken.IsCancellationRequested,
            };
        }

        private Credentials GetCredentials(string url, string usernameFromUrl, SupportedCredentialTypes types)
        {
            this.cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var uri = new Uri(url);
                var start = new ProcessStartInfo("git.exe")
                {
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                start.ArgumentList.Add("credential");
                start.ArgumentList.Add("fill");
                start.Environment["GIT_TERMINAL_PROMPT"] = "0";
                start.Environment["GCM_INTERACTIVE"] = "Never";
                using var process = Process.Start(start) ?? throw new IOException("Cannot start Git's credential helper.");
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                process.StandardInput.NewLine = "\n";
                process.StandardInput.WriteLine($"protocol={uri.Scheme}");
                process.StandardInput.WriteLine($"host={uri.Authority}");
                process.StandardInput.WriteLine($"path={uri.AbsolutePath.TrimStart('/')}");
                process.StandardInput.WriteLine();
                process.StandardInput.Close();
                if (!process.WaitForExit(15000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                    throw new TimeoutException("Git's credential helper timed out.");
                }

                var credentials = output.GetAwaiter().GetResult().Split('\n')
                    .Select(line => line.TrimEnd('\r').Split('=', 2))
                    .Where(parts => parts.Length == 2)
                    .GroupBy(parts => parts[0], StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.Ordinal);
                _ = errors.GetAwaiter().GetResult();
                if (process.ExitCode == 0 && credentials.TryGetValue("username", out var username) &&
                    credentials.TryGetValue("password", out var password))
                {
                    return new UsernamePasswordCredentials { Username = username, Password = password };
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UriFormatException or
                System.ComponentModel.Win32Exception or TimeoutException)
            {
                this.log?.Invoke("Git credentials could not be retrieved from the configured credential helper.");
            }

            return new DefaultCredentials();
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
        }

        private static Remote GetHeadRemote(Repository repository)
        {
            var trackedRemote = repository.Head.TrackedBranch?.RemoteName;
            var remote = trackedRemote == null ? null : repository.Network.Remotes[trackedRemote];
            return remote ?? repository.Network.Remotes["updater_remote"] ?? repository.Network.Remotes["origin"]
                ?? repository.Network.Remotes.FirstOrDefault()
                ?? throw new InvalidOperationException("The repository has no remote.");
        }

        private static void EnsureTrackingBranch(Repository repository, Remote remote)
        {
            if (!repository.Info.IsHeadDetached && repository.Head.TrackedBranch == null &&
                repository.Branches[$"{remote.Name}/{repository.Head.FriendlyName}"] is { } branch)
            {
                repository.Branches.Update(repository.Head, local => local.TrackedBranch = branch.CanonicalName);
            }
        }

        private static bool HasGitMetadata(string path)
        {
            return Directory.Exists(System.IO.Path.Combine(path, ".git")) || File.Exists(System.IO.Path.Combine(path, ".git"));
        }

        private static string ShortCommit(Commit? commit)
        {
            return commit == null ? string.Empty : commit.Sha[..7];
        }

        private static (string Url, string Name, string? Branch) ParseRepositoryUrl(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            var url = value.Trim().TrimEnd('/');
            string? branch = null;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file")
            {
                var treeIndex = uri.AbsolutePath.IndexOf("/tree/", StringComparison.OrdinalIgnoreCase);
                if (treeIndex >= 0)
                {
                    branch = Uri.UnescapeDataString(uri.AbsolutePath[(treeIndex + 6)..]).TrimEnd('/');
                    var builder = new UriBuilder(uri) { Path = uri.AbsolutePath[..treeIndex], Query = string.Empty, Fragment = string.Empty };
                    url = builder.Uri.AbsoluteUri.TrimEnd('/');
                }
                else if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                {
                    throw new ArgumentException("Repository URLs cannot contain a query string or fragment.", nameof(value));
                }
            }

            var lastSeparator = Math.Max(url.LastIndexOf('/'), url.LastIndexOf('\\'));
            var name = Uri.UnescapeDataString(url[(lastSeparator + 1)..]);
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }

            return (url, name, branch);
        }

        private static PluginInfo CopyPluginInfo(PluginInfo source)
        {
            return new PluginInfo
            {
                Name = source.Name,
                Path = source.Path,
                CurrentCommit = source.CurrentCommit,
                FullCommit = source.FullCommit,
                LatestCommit = source.LatestCommit,
                LatestCommitMessage = source.LatestCommitMessage,
                BehindAhead = source.BehindAhead,
                AheadBy = source.AheadBy,
                BehindBy = source.BehindBy,
                PreviousCommit = source.PreviousCommit,
                UncommittedChangeCount = source.UncommittedChangeCount,
                CurrentBranch = source.CurrentBranch,
                AvailableBranches = source.AvailableBranches.ToList(),
                LastMessage = source.LastMessage,
                RemoteUrl = source.RemoteUrl,
            };
        }

        private static void DeleteSourceDirectory(string path)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        DeleteSourceDirectory(entry);
                    }
                    else
                    {
                        Directory.Delete(entry);
                    }
                }
                else
                {
                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        File.SetAttributes(entry, FileAttributes.Normal);
                    }

                    File.Delete(entry);
                }
            }

            File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(path);
        }
    }
}
