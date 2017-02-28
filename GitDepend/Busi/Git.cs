﻿using System.Diagnostics;
using System.IO.Abstractions;
using System.Linq;
using LibGit2Sharp;

namespace GitDepend.Busi
{
    /// <summary>
    /// A helper class for dealing with git.exe
    /// </summary>
    public class Git : IGit
    {
        private readonly IProcessManager _processManager;
        private readonly IFileSystem _fileSystem;
        private readonly IConsole _console;

        /// <summary>
        /// The working directory for all git operations.
        /// </summary>
        public string WorkingDirectory { get; set; }

        /// <summary>
        /// Creates a new <see cref="Git"/>
        /// </summary>
        public Git()
        {
            _processManager = DependencyInjection.Resolve<IProcessManager>();
            _fileSystem = DependencyInjection.Resolve<IFileSystem>();
            _console = DependencyInjection.Resolve<IConsole>();

            WorkingDirectory = _fileSystem.Path.GetFullPath(".");
        }

        /// <summary>
        /// Checks out the given branch.
        /// </summary>
        /// <param name="branch">The branch to check out.</param>
        /// <param name="create">Should the branch be created?</param>
        /// <returns>The git return code.</returns>
        public ReturnCode Checkout(string branch, bool create)
        {
            if (create)
            {
                var code = CreateBranch(branch);
                if (code != ReturnCode.Success)
                {
                    return code;
                }
            }

            using (var repo = new Repository(WorkingDirectory))
            {
                var branchRef = repo.Branches.FirstOrDefault(b => b.FriendlyName == branch);

                if (branchRef != null)
                {
                    LibGit2Sharp.Commands.Checkout(repo, branchRef);
                    _console.WriteLine($"Switched to branch '{branch}'");
                    return ReturnCode.Success;
                }
                _console.WriteLine($"error: pathspec '{branch}' did not match any file(s) known to git.");
                return ReturnCode.FailedToRunGitCommand;
            }
        }

        /// <summary>
        /// Creates the given branch.
        /// </summary>
        /// <param name="branch">The branch to create</param>
        /// <returns>The git return code.</returns>
        public ReturnCode CreateBranch(string branch)
        {
            using (var repo = new Repository(WorkingDirectory))
            {
                repo.CreateBranch(branch);
                _console.WriteLine($"Created branch '{branch}'");

                return ReturnCode.Success;
            }
        }

        /// <summary>
        /// Clones a repository.
        /// </summary>
        /// <param name="url">The repository url.</param>
        /// <param name="directory">The directory where the repository should be cloned.</param>
        /// <param name="branch">The branch that should be checked out.</param>
        /// <returns>The git return code.</returns>
        public ReturnCode Clone(string url, string directory, string branch)
        {
            return ExecuteGitCommand($"clone {url} \"{directory}\" -b {branch}");
        }

        /// <summary>
        /// Adds files into the staging area to prepare them for a commit.
        /// </summary>
        /// <param name="files">The files to add to the staging area.</param>
        /// <returns>The git return code.</returns>
        public ReturnCode Add(params string[] files)
        {
            foreach (string file in files)
            {
                ExecuteGitCommand($"add \"{file}\"");
            }

            return ReturnCode.Success;
        }

        /// <summary>
        /// Shows the status of the repository.
        /// </summary>
        /// <returns>The git return code.</returns>
        public ReturnCode Status()
        {
            return ExecuteGitCommand("status");
        }

        /// <summary>
        /// Deletes the specified branch.
        /// </summary>
        /// <param name="branch">The branch to delete.</param>
        /// <param name="force">Should the deletion be forced or not.</param>
        /// <returns></returns>
        public ReturnCode DeleteBranch(string branch, bool force)
        {
            return ExecuteGitCommand(force
                ? $"branch -D {branch}"
                : $"branch -d {branch}");
        }

        /// <summary>
        /// Lists all merged branches.
        /// </summary>
        /// <returns></returns>
        public ReturnCode ListMergedBranches()
        {
            return ExecuteGitCommand("branch --merged");
        }

        /// <summary>
        /// Lists all branches.
        /// </summary>
        /// <returns></returns>
        public ReturnCode ListAllBranches()
        {
            return ExecuteGitCommand("branch");
        }

        /// <summary>
        /// Gets the current branch.
        /// </summary>
        /// <returns></returns>
        public string GetCurrentBranch()
        {
            using (var repo = new Repository(WorkingDirectory))
            {
                return repo.Head.FriendlyName;
            }
        }

        /// <summary>
        /// Makes a commit with the given message.
        /// </summary>
        /// <param name="message"></param>
        /// <returns>The git return code.</returns>
        public ReturnCode Commit(string message)
        {
            var file = _fileSystem.Path.GetTempFileName();
            _fileSystem.File.WriteAllText(file, message);

            var code = ExecuteGitCommand($"commit --file=\"{file}\"");

            _fileSystem.File.Delete(file);
            return code;
        }

        private ReturnCode ExecuteGitCommand(string arguments)
        {
            var info = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = WorkingDirectory,
                UseShellExecute = false,
            };
            var proc = _processManager.Start(info);
            proc?.WaitForExit();

            var code = proc?.ExitCode ?? (int)ReturnCode.FailedToRunGitCommand;

            return code != (int)ReturnCode.Success
                ? ReturnCode.FailedToRunGitCommand
                : ReturnCode.Success;
        }
    }
}
