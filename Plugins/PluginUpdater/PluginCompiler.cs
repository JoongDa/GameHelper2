// <copyright file="PluginCompiler.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace PluginUpdater
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Metadata;
    using System.Reflection.Metadata.Ecma335;
    using System.Reflection.PortableExecutable;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Xml.Linq;

    public sealed record PluginBuildResult(string RepositoryName, string PluginName, string Commit, string OutputDirectory);

    /// <summary>Builds a repository against the installed host without changing its working tree.</summary>
    public sealed class PluginCompiler
    {
        private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".github", "bin", "obj",
        };

        private readonly string hostDirectory;
        private readonly string stagingDirectory;
        private readonly Action<string> log;

        public PluginCompiler(string hostDirectory, string managerDirectory, Action<string> log)
        {
            this.hostDirectory = Path.GetFullPath(hostDirectory);
            this.stagingDirectory = Path.Combine(Path.GetFullPath(managerDirectory), "Staging");
            this.log = log;
        }

        public async Task<PluginBuildResult> BuildAsync(
            PluginInfo plugin, CancellationToken cancellationToken = default, string? projectRelativePath = null)
        {
            // MSBuild still has tasks with MAX_PATH limitations. Keep all compiler
            // paths short, then copy the verified payload to the installation volume.
            var buildId = Guid.NewGuid().ToString("N");
            var temporaryDirectory = Path.Combine(Path.GetTempPath(), "ghp-" + buildId);
            try
            {
                var result = await this.BuildInTemporaryDirectoryAsync(
                    plugin, temporaryDirectory, cancellationToken, projectRelativePath).ConfigureAwait(false);
                var stagedPayload = Path.Combine(this.stagingDirectory, buildId, "payload");
                await Task.Run(() => CopyPayload(result.OutputDirectory, stagedPayload, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
                return result with { OutputDirectory = stagedPayload };
            }
            finally
            {
                this.DeleteTemporaryBuildDirectory(temporaryDirectory);
            }
        }

        private async Task<PluginBuildResult> BuildInTemporaryDirectoryAsync(
            PluginInfo plugin, string temporaryDirectory, CancellationToken cancellationToken, string? projectRelativePath)
        {
            if (!File.Exists(Path.Combine(this.hostDirectory, "GameHelper.dll")))
            {
                throw new FileNotFoundException("GameHelper.dll was not found in the host directory.");
            }

            var sourceDirectory = Path.GetFullPath(plugin.Path);
            var project = SelectProject(sourceDirectory, projectRelativePath);
            var sourceCopy = Path.Combine(temporaryDirectory, "source");
            var outputDirectory = Path.Combine(temporaryDirectory, "payload");
            Directory.CreateDirectory(outputDirectory);
            this.log($"Building {plugin.Name} from {Path.GetRelativePath(sourceDirectory, project)}");

            await Task.Run(() => CopyTree(sourceDirectory, sourceCopy, cancellationToken), cancellationToken).ConfigureAwait(false);
            var copiedProject = Path.Combine(sourceCopy, Path.GetRelativePath(sourceDirectory, project));
            this.PrepareProjects(sourceCopy);
            PrepareNuGetConfiguration(sourceCopy);
            var emptyTargets = Path.Combine(sourceCopy, "Directory.Build.targets");
            if (!File.Exists(emptyTargets))
            {
                File.WriteAllText(emptyTargets, "<Project />");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = sourceCopy,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[]
            {
                "build", copiedProject, "--configuration", "Release", "--output", outputDirectory,
                "--nologo", "--verbosity", "minimal", "-m:1", "-nr:false", "-p:TargetFramework=net10.0-windows",
                "-p:RuntimeIdentifier=win-x64", "-p:PlatformTarget=x64", "-p:EnableWindowsTargeting=true",
                "-p:SelfContained=false", "-p:UseAppHost=false", "-p:NuGetAudit=false", "-p:RestoreIgnoreFailedSources=true",
                $"-p:GameHelperPath={this.hostDirectory}",
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("The .NET 10 SDK is required to compile plugins. Install the SDK and retry.", ex);
            }

            using var cancellation = timeout.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });
            var standardOutput = this.ReadOutputAsync(process.StandardOutput);
            var standardError = this.ReadOutputAsync(process.StandardError);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The cancellation callback stops this owned build process. Wait for
                // it to release its files before the outer finally removes the snapshot.
                await process.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
                throw;
            }

            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Compilation of {plugin.Name} failed. See the build output in the log.");
            }

            CopyAssets(Path.GetDirectoryName(copiedProject)!, outputDirectory, cancellationToken);
            var entryPoints = Directory.EnumerateFiles(outputDirectory, "*.dll")
                .Where(IsPluginAssembly).ToArray();
            if (entryPoints.Length != 1)
            {
                throw new InvalidDataException($"The build must contain exactly one GameHelper plugin assembly, found {entryPoints.Length}.");
            }

            var pluginName = AssemblyName.GetAssemblyName(entryPoints[0]).Name
                ?? throw new InvalidDataException("The plugin assembly has no name.");
            ValidatePluginName(pluginName);
            if (!string.Equals(Path.GetFileNameWithoutExtension(entryPoints[0]), pluginName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The plugin DLL filename must match its assembly name.");
            }

            // Shared host assemblies are resolved from the host's load context.
            foreach (var hostFile in Directory.EnumerateFiles(this.hostDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var filename = Path.GetFileName(hostFile);
                if (filename.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    filename.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var duplicate = Path.Combine(outputDirectory, filename);
                    if (File.Exists(duplicate) && !string.Equals(duplicate, entryPoints[0], StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(duplicate);
                    }
                }
            }

            foreach (var directory in new[] { "config", "configs" })
            {
                var configPath = Path.Combine(outputDirectory, directory);
                if (Directory.Exists(configPath))
                {
                    Directory.Delete(configPath, recursive: true);
                }
            }

            this.log($"Compiled {pluginName} successfully.");
            return new PluginBuildResult(plugin.Name, pluginName, plugin.FullCommit, outputDirectory);
        }

        private void DeleteTemporaryBuildDirectory(string temporaryDirectory)
        {
            var fullPath = Path.GetFullPath(temporaryDirectory);
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var name = Path.GetFileName(fullPath);
            if (!string.Equals(Path.GetDirectoryName(fullPath), temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith("ghp-", StringComparison.Ordinal) || !Guid.TryParseExact(name[4..], "N", out _))
            {
                throw new InvalidOperationException("Invalid temporary plugin build directory.");
            }

            try
            {
                if (Directory.Exists(fullPath))
                {
                    if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException("The temporary build directory became a filesystem link.");
                    }

                    Directory.Delete(fullPath, recursive: true);
                }
            }
            catch (IOException ex)
            {
                this.log($"Could not remove temporary build files: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                this.log($"Could not remove temporary build files: {ex.Message}");
            }
        }

        private static void CopyPayload(string sourceDirectory, string destinationDirectory, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(destinationDirectory);
            foreach (var entry in new DirectoryInfo(sourceDirectory).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Compiled plugin output cannot contain filesystem links.");
                }

                var target = Path.Combine(destinationDirectory, entry.Name);
                if (entry is DirectoryInfo directory)
                {
                    CopyPayload(directory.FullName, target, cancellationToken);
                }
                else
                {
                    File.Copy(entry.FullName, target);
                    File.SetAttributes(target, FileAttributes.Normal);
                }
            }
        }

        internal static void ValidatePluginName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.EndsWith('.') || name.EndsWith(' ') ||
                new[] { "PluginUpdater", "GameHelper", "GameOffsets", "Launcher" }.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Invalid plugin name '{name}'.");
            }
        }

        private static string SelectProject(string sourceDirectory, string? projectRelativePath)
        {
            if (!string.IsNullOrWhiteSpace(projectRelativePath))
            {
                var specified = Path.GetFullPath(Path.Combine(sourceDirectory, projectRelativePath));
                if (!specified.StartsWith(Path.TrimEndingDirectorySeparator(sourceDirectory) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) || !File.Exists(specified) ||
                    !specified.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The selected project must be a .csproj inside the repository.");
                }

                return specified;
            }

            var projects = EnumerateSourceFiles(sourceDirectory)
                .Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToArray();
            var hostProjects = projects.Where(path => XDocument.Load(path).Descendants()
                .Any(element => element.Name.LocalName is "Reference" or "ProjectReference" &&
                    IsHostReference(element.Attribute("Include")?.Value))).ToArray();
            var candidates = hostProjects.Length > 0 ? hostProjects : projects;
            if (candidates.Length != 1)
            {
                throw new InvalidDataException("Choose a project in the plugin settings. Candidate projects: " +
                    string.Join(", ", candidates.Select(path => Path.GetRelativePath(sourceDirectory, path))));
            }

            return candidates[0];
        }

        private static IEnumerable<string> EnumerateSourceFiles(string directory)
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (ExcludedDirectories.Contains(entry.Name))
                {
                    continue;
                }

                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Source links are unsupported: {entry.FullName}");
                }

                if (entry is DirectoryInfo child)
                {
                    if (!ExcludedDirectories.Contains(child.Name))
                    {
                        foreach (var file in EnumerateSourceFiles(child.FullName))
                        {
                            yield return file;
                        }
                    }
                }
                else
                {
                    yield return entry.FullName;
                }
            }
        }

        private static bool IsHostReference(string? include)
        {
            var name = Path.GetFileNameWithoutExtension((include ?? string.Empty).Replace('\\', '/')).Split(',')[0];
            return name is "GameHelper" or "GameOffsets";
        }

        private void PrepareProjects(string sourceCopy)
        {
            var hostReferences = Directory.EnumerateFiles(this.hostDirectory, "*.dll")
                .Where(IsManagedAssembly).ToArray();
            foreach (var path in EnumerateSourceFiles(sourceCopy).Where(path =>
                path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)))
            {
                var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
                var root = document.Root!;
                var ns = root.Name.Namespace;
                foreach (var reference in document.Descendants().Where(element =>
                    element.Name.LocalName is "ProjectReference" or "Reference" &&
                    IsHostReference(element.Attribute("Include")?.Value)).ToArray())
                {
                    reference.Remove();
                }

                foreach (var target in document.Descendants().Where(element => element.Name.LocalName == "Target" &&
                    element.Attribute("Name")?.Value is "CopyFiles" or "CopyPluginLocalizationFiles").ToArray())
                {
                    target.Remove();
                }

                if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    root.Add(new XElement(ns + "ItemGroup", hostReferences.Select(file =>
                        new XElement(ns + "Reference", new XAttribute("Include", Path.GetFileNameWithoutExtension(file)),
                            new XElement(ns + "HintPath", file), new XElement(ns + "Private", "false")))));
                    root.Add(new XElement(ns + "PropertyGroup",
                        new XElement(ns + "GameHelperPath", this.hostDirectory),
                        new XElement(ns + "CopyLocalLockFileAssemblies", "true"),
                        new XElement(ns + "EnableWindowsTargeting", "true")));
                }

                document.Save(path);
            }
        }

        private static void PrepareNuGetConfiguration(string sourceCopy)
        {
            var configPath = Directory.EnumerateFiles(sourceCopy, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => Path.GetFileName(path).Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase))
                ?? Path.Combine(sourceCopy, "NuGet.Config");
            var document = File.Exists(configPath) ? XDocument.Load(configPath) : new XDocument(new XElement("configuration"));
            var sources = document.Root!.Element("packageSources");
            if (sources == null)
            {
                sources = new XElement("packageSources");
                document.Root.Add(sources);
            }

            // Keep repository feeds while excluding unrelated machine-wide feeds.
            if (sources.Element("clear") == null)
            {
                sources.AddFirst(new XElement("clear"));
            }

            if (!sources.Elements("add").Any(element =>
                string.Equals(element.Attribute("value")?.Value, "https://api.nuget.org/v3/index.json", StringComparison.OrdinalIgnoreCase)))
            {
                sources.Add(new XElement("add", new XAttribute("key", "GameHelperNuGet"),
                    new XAttribute("value", "https://api.nuget.org/v3/index.json")));
            }

            document.Save(configPath);
        }

        private static bool IsManagedAssembly(string path)
        {
            try
            {
                AssemblyName.GetAssemblyName(path);
                return true;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        private static bool IsPluginAssembly(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata)
                {
                    return false;
                }

                var metadata = pe.GetMetadataReader();
                return metadata.TypeDefinitions.Count(handle =>
                {
                    var type = metadata.GetTypeDefinition(handle);
                    return (type.Attributes & TypeAttributes.Sealed) != 0 &&
                        (type.Attributes & TypeAttributes.Abstract) == 0 &&
                        IsPluginBase(metadata, type.BaseType, 0);
                }) == 1;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        private static bool IsPluginBase(MetadataReader metadata, EntityHandle handle, int depth)
        {
            if (handle.IsNil || depth > 32)
            {
                return false;
            }

            if (handle.Kind == HandleKind.TypeReference)
            {
                var reference = metadata.GetTypeReference((TypeReferenceHandle)handle);
                return metadata.GetString(reference.Name) == "PCore`1" &&
                    metadata.GetString(reference.Namespace) == "GameHelper.Plugin";
            }

            if (handle.Kind == HandleKind.TypeDefinition)
            {
                return IsPluginBase(metadata, metadata.GetTypeDefinition((TypeDefinitionHandle)handle).BaseType, depth + 1);
            }

            if (handle.Kind == HandleKind.TypeSpecification)
            {
                var signature = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                if (signature.ReadByte() == 0x15) // ELEMENT_TYPE_GENERICINST followed by its generic type.
                {
                    signature.ReadByte();
                    var codedHandle = signature.ReadCompressedInteger();
                    var row = codedHandle >> 2;
                    var genericType = (codedHandle & 3) switch
                    {
                        0 => (EntityHandle)MetadataTokens.TypeDefinitionHandle(row),
                        1 => MetadataTokens.TypeReferenceHandle(row),
                        2 => MetadataTokens.TypeSpecificationHandle(row),
                        _ => default,
                    };
                    return IsPluginBase(metadata, genericType, depth + 1);
                }
            }

            return false;
        }

        private static void CopyTree(string sourceDirectory, string destinationDirectory, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(destinationDirectory);
            foreach (var path in EnumerateSourceFiles(sourceDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(sourceDirectory, path);
                var target = Path.Combine(destinationDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target, overwrite: true);
                File.SetAttributes(target, FileAttributes.Normal);
            }
        }

        private static void CopyAssets(string projectDirectory, string outputDirectory, CancellationToken cancellationToken)
        {
            var excludedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".user", ".pdb", ".dll", ".exe",
            };
            foreach (var path in EnumerateSourceFiles(projectDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(projectDirectory, path);
                var topDirectory = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (topDirectory is "config" or "configs" || excludedExtensions.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                var target = Path.Combine(outputDirectory, relative);
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(path, target);
                }
            }
        }

        private async Task ReadOutputAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                this.log(line);
            }
        }
    }
}
