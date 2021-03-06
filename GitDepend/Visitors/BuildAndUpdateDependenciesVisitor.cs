﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GitDepend.Busi;
using GitDepend.Configuration;
using GitDepend.Resources;

namespace GitDepend.Visitors
{
    /// <summary>
    /// Implementation of <see cref="IVisitor"/> that runs the build for dependencies, and updates the project
    /// to consume the latest dependency artifacts.
    /// </summary>
    public class BuildAndUpdateDependenciesVisitor : IVisitor
    {
        private readonly List<string> _dependeciesToBuild;
        private readonly List<string> _projectsToUpdate;
        private static readonly Regex Pattern = new Regex(@"^(?<id>.*?)\.(?<version>(?:\d\.){2,3}\d(?:-.*?)?)$", RegexOptions.Compiled);
        private readonly IGitDependFileFactory _factory;
        private readonly IGit _git;
        private readonly INuget _nuget;
        private readonly IProcessManager _processManager;
        private readonly IFileSystem _fileSystem;
        private readonly IConsole _console;

        /// <summary>
        /// The list of packages that were updated.
        /// </summary>
        public HashSet<string> UpdatedPackages { get; } = new HashSet<string>();

        /// <summary>
        /// Creates a new <see cref="BuildAndUpdateDependenciesVisitor"/>
        /// </summary>
        /// <param name="dependeciesToBuild">The dependencies to build.</param>
        /// <param name="projectsToUpdate">The projects to update.</param>
        public BuildAndUpdateDependenciesVisitor(List<string> dependeciesToBuild, List<string> projectsToUpdate)
        {
            _dependeciesToBuild = dependeciesToBuild;
            _projectsToUpdate = projectsToUpdate;
            _factory = DependencyInjection.Resolve<IGitDependFileFactory>();
            _git = DependencyInjection.Resolve<IGit>();
            _nuget = DependencyInjection.Resolve<INuget>();
            _processManager = DependencyInjection.Resolve<IProcessManager>();
            _fileSystem = DependencyInjection.Resolve<IFileSystem>();
            _console = DependencyInjection.Resolve<IConsole>();
        }

        /// <summary>
        /// The directory where nuget packages are cached.
        /// </summary>
        public string GetCacheDirectory()
        {
            var dir = _fileSystem.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitDepend");

            var cacheDir = _fileSystem.Path.Combine(dir, "cache");
            if (_fileSystem.Directory.Exists(cacheDir))
            {
                return cacheDir;
            }

            try
            {
                _fileSystem.Directory.CreateDirectory(cacheDir);
            }
            catch (Exception ex)
            {
                _console.Error.WriteLine(ex.Message);
                return null;
            }

            return cacheDir;
        }

        #region Implementation of IVisitor

        /// <summary>
        /// The return code
        /// </summary>
        public ReturnCode ReturnCode { get; set; }

        /// <summary>
        /// Visits a project dependency.
        /// </summary>
        /// <param name="directory">The directory of the project.</param>
        /// <param name="dependency">The <see cref="Dependency"/> to visit.</param>
        /// <returns>The return code.</returns>
        public ReturnCode VisitDependency(string directory, Dependency dependency)
        {
            string dir;
            ReturnCode code;
            var config = _factory.LoadFromDirectory(dependency.Directory, out dir, out code);

            if (code != ReturnCode.Success)
            {
                return ReturnCode = code;
            }

            var cacheDir = GetCacheDirectory();

            if (string.IsNullOrEmpty(cacheDir))
            {
                return ReturnCode = ReturnCode.CouldNotCreateCacheDirectory;
            }

            int exitCode = 0;

            if (_dependeciesToBuild.Any(d => string.Equals(d, dependency.Configuration.Name, StringComparison.InvariantCultureIgnoreCase)))
            {
                var buildScript = _fileSystem.Path.Combine(dependency.Directory, config.Build.Script);
                var info = new ProcessStartInfo(buildScript, config.Build.Arguments)
                {
                    WorkingDirectory = dependency.Directory,
                    UseShellExecute = false
                };

                using (var proc = _processManager.Start(info))
                {
                    proc.WaitForExit();
                    exitCode = proc.ExitCode;
                }
            }

            var artifactsDir = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(directory, dependency.Directory, dependency.Configuration.Packages.Directory));

            if (!_fileSystem.Directory.Exists(artifactsDir))
            {
                _console.WriteLine(strings.DIRECTORY_NOT_FOUND, artifactsDir);
                return ReturnCode.FailedToLocateArtifactsDir;
            }

            foreach (var file in _fileSystem.Directory.GetFiles(artifactsDir, "*.nupkg"))
            {
                var name = _fileSystem.Path.GetFileName(file);
                if (!string.IsNullOrEmpty(name))
                {
                    _fileSystem.File.Copy(file, _fileSystem.Path.Combine(cacheDir, name), true);
                }
            }

            return ReturnCode = exitCode == 0
                ? ReturnCode.Success
                : ReturnCode.FailedToRunBuildScript;
        }

        /// <summary>
        /// Visists a project.
        /// </summary>
        /// <param name="directory">The directory of the project.</param>
        /// <param name="config">The <see cref="GitDependFile"/> with project configuration information.</param>
        /// <returns>The return code.</returns>
        public ReturnCode VisitProject(string directory, GitDependFile config)
        {
            if (config == null)
            {
                return ReturnCode = ReturnCode.Success;
            }

            // If there are specific dependencies specified
            // and this one isn't in the list
            // skip it.
            if (!_projectsToUpdate.Any() ||
                _projectsToUpdate.All(d => !string.Equals(d, config.Name, StringComparison.InvariantCultureIgnoreCase)))
            {
                return ReturnCode.Success;
            }

            if (string.IsNullOrEmpty(directory) || !_fileSystem.Directory.Exists(directory))
            {
                return ReturnCode = ReturnCode.DirectoryDoesNotExist;
            }

            StringBuilder commitMessage = new StringBuilder();
            commitMessage.AppendLine("GitDepend: updating dependencies");
            commitMessage.AppendLine();

            var solutions = _fileSystem.Directory.GetFiles(directory, "*.sln", SearchOption.AllDirectories);

            foreach (var solution in solutions)
            {
                var returnCode = _nuget.Restore(solution);
                if (returnCode != ReturnCode.Success)
                {
                    return returnCode;
                }
            }

            foreach (var solution in solutions)
            {
                var path = solution.Remove(0, directory.Length + 1);
                commitMessage.AppendLine(path);

                foreach (var dependency in config.Dependencies)
                {
                    var dependencyDir = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(directory, dependency.Directory));
                    var dir = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(dependencyDir, dependency.Configuration.Packages.Directory));

                    foreach (var file in _fileSystem.Directory.GetFiles(dir, "*.nupkg"))
                    {
                        var name = _fileSystem.Path.GetFileNameWithoutExtension(file);


                        if (string.IsNullOrEmpty(name))
                        {
                            continue;
                        }

                        var match = Pattern.Match(name);

                        if (!match.Success)
                        {
                            continue;
                        }

                        var id = match.Groups["id"].Value;
                        var version = match.Groups["version"].Value;

                        commitMessage.AppendLine($"* {id}.{version}");

                        _nuget.WorkingDirectory = directory;

                        var cacheDir = GetCacheDirectory();

                        if (string.IsNullOrEmpty(cacheDir))
                        {
                            return ReturnCode = ReturnCode.CouldNotCreateCacheDirectory;
                        }

                        var returnCode = _nuget.Update(solution, id, version, cacheDir);
                        if (returnCode != ReturnCode.Success)
                        {
                            return returnCode;
                        }

                        var package = $"{id}.{version}";
                        if (!UpdatedPackages.Contains(package))
                        {
                            UpdatedPackages.Add(package);
                        }
                    }
                }
            }

            _console.WriteLine("================================================================================");
            _console.WriteLine(strings.MAKING_UPDATE_COMMIT_ON  + directory);
            _git.WorkingDirectory = directory;
            _git.Add("*.csproj", @"*\packages.config");
            _console.WriteLine("================================================================================");
            _git.Status();

            _git.Commit(commitMessage.ToString());

            return ReturnCode = ReturnCode.Success;
        }

        #endregion
    }
}
