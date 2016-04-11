/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Dbg=System.Management.Automation.Diagnostics;

namespace System.Management.Automation
{
    /// <summary>
    /// Used to enumerate the commands on the system that match the specified
    /// command name
    /// </summary>
    internal class CommandPathSearch : IEnumerable<String>, IEnumerator<String>
    {
        [TraceSource("CommandSearch","CommandSearch")]
        private static PSTraceSource tracer = PSTraceSource.GetTracer ("CommandSearch", "CommandSearch");

        /// <summary>
        /// Constructs a command searching enumerator that resolves the location
        /// of a command using the PATH environment variable.
        /// </summary>
        /// 
        /// <param name="patterns">
        /// The patterns to search for in the path.
        /// </param>
        /// 
        /// <param name="lookupPaths">
        /// The paths to directories in which to lookup the command.
        /// </param>
        /// 
        /// <param name="context">
        /// The exection context for the current engine instance.
        /// </param>
        internal CommandPathSearch(
            IEnumerable<string> patterns,
            IEnumerable<string> lookupPaths,
            ExecutionContext context)
        {
            Init(patterns, lookupPaths, context);
        }

        internal CommandPathSearch(
            string commandName,
            IEnumerable<string> lookupPaths,
            ExecutionContext context,
            Collection<string> acceptableCommandNames)
        {
            if (acceptableCommandNames != null)
            {
                // The name passed in is not a pattern. To minimize enumerating the file system, we
                // turn the command name into a pattern and then match against extensions in PATHEXT.
                // The old code would enumerate the file system many more times, once per possible extension.
                // Porting note: this is wrong on Linux, where we don't depend on extensions
                if (Platform.IsWindows)
                {
                    commandName = commandName + ".*";
                }
                this.postProcessEnumeratedFiles = CheckAgainstAcceptableCommandNames;
                this.acceptableCommandNames = acceptableCommandNames;
            }
            else
            {
                postProcessEnumeratedFiles = JustCheckExtensions;
            }
            
            Init(new [] { commandName }, lookupPaths, context);
            this.orderedPathExt = CommandDiscovery.PathExtensionsWithPs1Prepended;
        }

        private void Init(IEnumerable<string> commandPatterns, IEnumerable<string> searchPath, ExecutionContext context)
        {
            // Note, discovery must be set before resolving the current directory

            this._context = context;
            this.patterns = commandPatterns;

            this.lookupPaths = new LookupPathCollection(searchPath);
            ResolveCurrentDirectoryInLookupPaths();

            this.Reset();
        }

        /// <summary>
        /// Ensures that all the paths in the lookupPaths member are absolute
        /// file system paths.
        /// </summary>
        /// 
        private void ResolveCurrentDirectoryInLookupPaths()
        {
            var indexesToRemove = new SortedDictionary<int, int>();
            int removalListCount = 0;

            string fileSystemProviderName = _context.ProviderNames.FileSystem;

            SessionStateInternal sessionState = _context.EngineSessionState;

            // Only use the directory if it gets resolved by the FileSystemProvider
            bool isCurrentDriveValid =
                sessionState.CurrentDrive != null &&
                sessionState.CurrentDrive.Provider.NameEquals(fileSystemProviderName) &&
                sessionState.IsProviderLoaded(fileSystemProviderName);

            string environmentCurrentDirectory = Directory.GetCurrentDirectory();

            LocationGlobber pathResolver = _context.LocationGlobber;

            // Loop through the relative paths and resolve them

            foreach (int index in this.lookupPaths.IndexOfRelativePath())
            {
                string resolvedDirectory = null;
                string resolvedPath = null;

                CommandDiscovery.discoveryTracer.WriteLine(
                    "Lookup directory \"{0}\" appears to be a relative path. Attempting resolution...",
                    lookupPaths[index]);

                if (isCurrentDriveValid)
                {
                    try
                    {
                        ProviderInfo provider;
                        resolvedPath =
                            pathResolver.GetProviderPath(
                                lookupPaths[index],
                                out provider);
                    }
                    catch (ProviderInvocationException providerInvocationException)
                    {
                        CommandDiscovery.discoveryTracer.WriteLine(
                            "The relative path '{0}', could not be resolved because the provider threw an exception: '{1}'",
                            lookupPaths[index],
                            providerInvocationException.Message);
                    }
                    catch (InvalidOperationException)
                    {
                        CommandDiscovery.discoveryTracer.WriteLine(
                            "The relative path '{0}', could not resolve a home directory for the provider",
                            lookupPaths[index]);
                    }


                    // Note, if the directory resolves to multiple paths, only the first is used.

                    if (!String.IsNullOrEmpty(resolvedPath))
                    {
                        CommandDiscovery.discoveryTracer.TraceError(
                            "The relative path resolved to: {0}",
                            resolvedPath);

                        resolvedDirectory = resolvedPath;
                    }
                    else
                    {
                        CommandDiscovery.discoveryTracer.WriteLine(
                            "The relative path was not a file system path. {0}",
                            lookupPaths[index]);
                    }
                }
                else
                {
                    CommandDiscovery.discoveryTracer.TraceWarning(
                        "The current drive is not set, using the process current directory: {0}",
                        environmentCurrentDirectory);

                    resolvedDirectory = environmentCurrentDirectory;
                }

                // If we successfully resolved the path, make sure it is unique. Remove
                // any duplicates found after the first occurrence of the path.

                if (resolvedDirectory != null)
                {
                    int existingIndex = lookupPaths.IndexOf(resolvedDirectory);

                    if (existingIndex != -1)
                    {
                        if (existingIndex > index)
                        {
                            // The relative path index is less than the explicit path,
                            // so remove the explicit path.

                            indexesToRemove.Add(removalListCount++, existingIndex);
                            lookupPaths[index] = resolvedDirectory;
                        }
                        else
                        {
                            // The explicit path index is less than the relative path
                            // index, so remove the relative path.

                            indexesToRemove.Add(removalListCount++, index);
                        }
                    }
                    else
                    {
                        // Change the relative path to the resolved path.

                        lookupPaths[index] = resolvedDirectory;
                    }
                }
                else
                {
                    // The directory couldn't be resolved so remove it from the
                    // lookup paths.

                    indexesToRemove.Add(removalListCount++, index);
                }
            }

            // Now remove all the duplicates starting from the back of the collection.
            // As each element is removed, elements that follow are moved up to occupy
            // the emptied index.

            for (int removeIndex = indexesToRemove.Count; removeIndex > 0; --removeIndex)
            {
                int indexToRemove = indexesToRemove[removeIndex - 1];
                lookupPaths.RemoveAt(indexToRemove);
            }
        } // ResolveCurrentDirectoryInLookupPaths

        /// <summary>
        /// Gets an instance of a command enumerator
        /// </summary>
        /// 
        /// <returns>
        /// An instance of this class as IEnumerator.
        /// </returns>
        /// 
        IEnumerator<string> IEnumerable<string>.GetEnumerator()
        {
            return this;
        } // GetEnumerator

        /// <summary>
        /// Gets an instance of a command enumerator
        /// </summary>
        /// 
        /// <returns>
        /// An instance of this class as IEnumerator.
        /// </returns>
        /// 
        IEnumerator IEnumerable.GetEnumerator()
        {
            return this;
        } // GetEnumerator

        /// <summary>
        /// Moves the enumerator to the next command match
        /// </summary>
        /// 
        /// <returns>
        /// true if there was another command that matches, false otherwise.
        /// </returns>
        /// 
        public bool MoveNext()
        {
            bool result = false;

            if (justReset)
            {
                justReset = false;

                if (!patternEnumerator.MoveNext())
                {
                    tracer.TraceError("No patterns were specified");
                    return false;
                }

                if (!lookupPathsEnumerator.MoveNext())
                {
                    tracer.TraceError("No lookup paths were specified");
                    return false;
                }

                GetNewDirectoryResults(patternEnumerator.Current, lookupPathsEnumerator.Current);
            }

            do // while lookupPathsEnumerator is valid
            {
                do // while patternEnumerator is valid
                {
                    // Try moving to the next path in the current results

                    if (!currentDirectoryResultsEnumerator.MoveNext())
                    {
                        tracer.WriteLine("Current directory results are invalid");

                        // Since a path was not found in the current result,
                        // advance the pattern and try again

                        if (!patternEnumerator.MoveNext())
                        {
                            tracer.WriteLine("Current patterns exhausted in current directory: {0}", lookupPathsEnumerator.Current);
                            break;
                        }

                        // Get the results of the next pattern

                        GetNewDirectoryResults(patternEnumerator.Current, lookupPathsEnumerator.Current);
                    }
                    else
                    {
                        tracer.WriteLine("Next path found: {0}", currentDirectoryResultsEnumerator.Current);
                        result = true;
                        break;
                    }

                    // Since we have reset the results, loop again to find the next result.
                } while (true);

                if (result)
                {
                    break;
                }

                // Since the path was not found in the current results, and all patterns were exhausted,
                // advance the path and continue

                if (!lookupPathsEnumerator.MoveNext())
                {
                    tracer.WriteLine("All lookup paths exhausted, no more matches can be found");
                    break;
                }

                // Reset the pattern enumerator and get new results using the new lookup path

                patternEnumerator = patterns.GetEnumerator();

                if (!patternEnumerator.MoveNext())
                {
                    tracer.WriteLine("All patterns exhausted, no more matches can be found");
                    break;
                }

                GetNewDirectoryResults(patternEnumerator.Current, lookupPathsEnumerator.Current);

            } while (true);

            return result;
        } // MoveNext

        /// <summary>
        /// Resets the enumerator to before the first command match
        /// </summary>
        public void Reset()
        {
            lookupPathsEnumerator = lookupPaths.GetEnumerator();
            patternEnumerator = patterns.GetEnumerator();
            currentDirectoryResults = Utils.EmptyArray<string>();
            currentDirectoryResultsEnumerator = currentDirectoryResults.GetEnumerator();
            justReset = true;
        } // Reset

        /// <summary>
        /// Gets the path to the current command match.
        /// </summary>
        /// <value></value>
        /// 
        /// <exception cref="InvalidOperationException">
        /// The enumerator is positioned before the first element of
        /// the collection or after the last element.
        /// </exception>
        /// 
        string IEnumerator<string>.Current
        {
            get 
            { 
                if (currentDirectoryResults == null)
                {
                    throw PSTraceSource.NewInvalidOperationException();
                }

                return currentDirectoryResultsEnumerator.Current; 
            }
        } // Current

        object IEnumerator.Current
        {
            get
            {
                return ((IEnumerator<string>) this).Current;
            }
        }

        /// <summary>
        /// Required by the IEnumerator generic interface.
        /// Resets the searcher.
        /// </summary>
        public void Dispose()
        {
            Reset();
            GC.SuppressFinalize(this);
        }
        #region private members

        /// <summary>
        /// Gets the matching files in the specified directories and resets
        /// the currentDirectoryResultsEnumerator to this new set of results.
        /// </summary>
        /// 
        /// <param name="pattern">
        /// The pattern used to find the matching files in the specified directory.
        /// </param>
        /// 
        /// <param name="directory">
        /// The path to the directory to find the files in.
        /// </param>
        /// 
        private void GetNewDirectoryResults(string pattern, string directory)
        {
            IEnumerable<string> result = null;
            try
            {
                CommandDiscovery.discoveryTracer.WriteLine("Looking for {0} in {1}", pattern, directory);

                // Get the matching files in the directory
                if (Directory.Exists(directory))
                {
                    // Win8 bug 92113: Directory.GetFiles() regressed in NET4
                    // Directory.GetFiles(directory, ".") used to return null with CLR 2.
                    // but with CLR4 it started acting like "*". This is a appcompat bug in CLR4
                    // but they cannot fix it as CLR4 is already RTMd by the time this was reported.
                    // If they revert it, it will become a CLR4 appcompat issue. So, using the workaround
                    // to forcefully use null if pattern is "."
                    if (pattern.Length != 1 || pattern[0] != '.')
                    {
                        var matchingFiles = Directory.EnumerateFiles(directory, pattern);

                        result = this.postProcessEnumeratedFiles != null
                            ? this.postProcessEnumeratedFiles(matchingFiles.ToArray())
                            : matchingFiles;
                    }
                }
            }
            catch (ArgumentException)
            {
                // The pattern contained illegal file system characters
            }
            catch (IOException)
            {
                // A directory specified in the lookup path was not
                // accessible
            }
            catch (UnauthorizedAccessException)
            {
                // A directory specified in the lookup path was not
                // accessible
            }
            catch (NotSupportedException)
            {
                // A directory specified in the lookup path was not
                // accessible
            }

            currentDirectoryResults = result ?? Utils.EmptyArray<string>();
            currentDirectoryResultsEnumerator = currentDirectoryResults.GetEnumerator();
        } // GetMatchingPathsInDirectory

        private IEnumerable<string> CheckAgainstAcceptableCommandNames(string[] fileNames)
        {
            var baseNames = fileNames.Select(Path.GetFileName).ToArray();

            // Result must be ordered by PATHEXT order of precedence.
            // acceptableCommandNames is in this order, so 

            // Porting note: allow files with executable bit on non-Windows platforms

            Collection<string> result = null;
            if (baseNames.Length > 0)
            {
                foreach (var name in acceptableCommandNames)
                {
                    for (int i = 0; i < baseNames.Length; i++)
                    {
                        if (name.Equals(baseNames[i], StringComparison.OrdinalIgnoreCase)
                            || (!Platform.IsWindows && Platform.NonWindowsIsExecutable(name)))
                        {
                            if (result == null)
                                result = new Collection<string>();
                            result.Add(fileNames[i]);
                            break;
                        }
                    }
                }
            }

            return result;
        }

        private IEnumerable<string> JustCheckExtensions(string[] fileNames)
        {
            // Warning: pretty duplicated code
            // Result must be ordered by PATHEXT order of precedence.

            // Porting note: allow files with executable bit on non-Windows platforms

            Collection<string> result = null;
            foreach (var allowedExt in orderedPathExt)
            {
                foreach (var fileName in fileNames)
                {
                    if (fileName.EndsWith(allowedExt, StringComparison.OrdinalIgnoreCase)
                        || (!Platform.IsWindows && Platform.NonWindowsIsExecutable(fileName)))
                    {
                        if (result == null)
                            result = new Collection<string>();
                        result.Add(fileName);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// The directory paths in which to look for commands.
        /// This is derived from the PATH environment variable
        /// </summary>
        private LookupPathCollection lookupPaths;

        /// <summary>
        /// The enumerator for the lookup paths
        /// </summary>
        private IEnumerator<string> lookupPathsEnumerator;

        /// <summary>
        /// The list of results matching the pattern in the current
        /// path lookup directory. Resets to null.
        /// </summary>
        private IEnumerable<string> currentDirectoryResults;

        /// <summary>
        /// The enumerator for the list of results
        /// </summary>
        private IEnumerator<string> currentDirectoryResultsEnumerator;

        /// <summary>
        /// The command name to search for
        /// </summary>
        private IEnumerable<string> patterns;

        /// <summary>
        /// The enumerator for the patterns
        /// </summary>
        private IEnumerator<string> patternEnumerator;

        /// <summary>
        /// A reference to the execution context for this runspace
        /// </summary>
        private ExecutionContext _context;

        /// <summary>
        /// When reset is called, this gets set to true. Once MoveNext
        /// is called, this gets set to false.
        /// </summary>
        private bool justReset;

        /// <summary>
        /// If not null, called with the enumerated files for futher processing
        /// </summary>
        private Func<string[], IEnumerable<string>> postProcessEnumeratedFiles;

        private string[] orderedPathExt;
        private Collection<string> acceptableCommandNames;

        #endregion private members

    } // CommandSearch
}
