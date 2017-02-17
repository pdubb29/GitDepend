﻿using System;
using System.IO.Abstractions;
using GitDepend.Busi;
using GitDepend.CommandLine;
using GitDepend.Visitors;

namespace GitDepend.Commands
{
    /// <summary>
    /// An implementation of <see cref="ICommand"/> that recursively updates all dependencies.
    /// </summary>
    public class UpdateCommand : ICommand
    {
        private readonly UpdateSubOptions _options;
        private readonly IConsole _console;
        private readonly IDependencyVisitorAlgorithm _algorithm;

        /// <summary>
        /// The verb name
        /// </summary>
        public const string Name = "update";

        /// <summary>
        /// Creates a new <see cref="UpdateCommand"/>
        /// </summary>
        /// <param name="options">The <see cref="UpdateSubOptions"/> that configure the command.</param>
        public UpdateCommand(UpdateSubOptions options)
        {
            _options = options;
            _console = DependencyInjection.Resolve<IConsole>();
            _algorithm = DependencyInjection.Resolve<IDependencyVisitorAlgorithm>();
        }

        #region Implementation of ICommand

        /// <summary>
        /// Executes the command.
        /// </summary>
        /// <returns>The return code.</returns>
        public ReturnCode Execute()
        {
            IVisitor visitor = new CheckOutBranchVisitor();
            _algorithm.TraverseDependencies(visitor, _options.Directory);

            if (visitor.ReturnCode != ReturnCode.Success)
            {
                _console.WriteLine("Could not ensure the correct branch on all dependencies.");
                return visitor.ReturnCode;
            }

            _algorithm.Reset();
            visitor = new BuildAndUpdateDependenciesVisitor();
            _algorithm.TraverseDependencies(visitor, _options.Directory);

            if (visitor.ReturnCode == ReturnCode.Success)
            {
                _console.WriteLine("Update complete!");
            }

            return visitor.ReturnCode;
        }

        #endregion
    }
}
