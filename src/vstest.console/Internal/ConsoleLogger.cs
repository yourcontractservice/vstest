// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.CommandLine.Internal
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Text;

    using Microsoft.VisualStudio.TestPlatform.ObjectModel;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
    using Microsoft.VisualStudio.TestPlatform.Utilities;

    using CommandLineResources = Resources.Resources;

    /// <summary>
    /// Logger for sending output to the console.
    /// All the console logger messages prints to Standard Output with respective color, except OutputLevel.Error messages
    /// from adapters and test run result on failed.
    /// </summary>
    [FriendlyName(ConsoleLogger.FriendlyName)]
    [ExtensionUri(ConsoleLogger.ExtensionUri)]
    internal class ConsoleLogger : ITestLoggerWithParameters
    {
        #region Constants
        private const string TestMessageFormattingPrefix = " ";

        /// <summary>
        /// Prefix used for formatting the result output
        /// </summary>
        private const string TestResultPrefix = "  ";

        /// <summary>
        /// Unicode for tick
        /// </summary>
        private const char PassedTestIndicator = '\u221a';

        /// <summary>
        /// Indicator for failed tests
        /// </summary>
        private const char FailedTestIndicator = 'X';

        /// <summary>
        /// Indicated skipped and not run tests
        /// </summary>
        private const char SkippedTestIndicator = '!';

        /// <summary>
        /// Bool to decide whether Verbose level should be added as prefix or not in log messages.
        /// </summary>
        internal static bool AppendPrefix;

        /// <summary>
        /// Bool to decide whether progress indicator should be enabled.
        /// </summary>
        internal static bool EnableProgress;

        /// <summary>
        /// Uri used to uniquely identify the console logger.
        /// </summary>
        public const string ExtensionUri = "logger://Microsoft/TestPlatform/ConsoleLogger/v1";

        /// <summary>
        /// Alternate user friendly string to uniquely identify the console logger.
        /// </summary>
        public const string FriendlyName = "Console";

        /// <summary>
        /// Parameter for Verbosity
        /// </summary>
        public const string VerbosityParam = "verbosity";

        /// <summary>
        /// Parameter for log message prefix
        /// </summary>
        public const string PrefixParam = "prefix";

        /// <summary>
        /// Parameter for disabling progress
        /// </summary>
        public const string ProgressIndicatorParam = "progress";

        /// <summary>
        ///  Property Id storing the ParentExecutionId.
        /// </summary>
        public const string ParentExecutionIdPropertyIdentifier = "ParentExecId";

        /// <summary>
        ///  Property Id storing the ExecutionId.
        /// </summary>
        public const string ExecutionIdPropertyIdentifier = "ExecutionId";

        #endregion

        internal enum Verbosity
        {
            Quiet,
            Minimal,
            Normal,
            Detailed
        }

        #region Fields

        /// <summary>
        /// Level of verbosity
        /// </summary>
#if NET451
        private Verbosity verbosityLevel = Verbosity.Normal;
#else
        // Keep default verbosity for x-plat command line as minimal
        private Verbosity verbosityLevel = Verbosity.Minimal;
#endif

        private bool testRunHasErrorMessages = false;
        private ConcurrentDictionary<Guid, TestOutcome> leafExecutionIdAndTestOutcomePairDictionary = new ConcurrentDictionary<Guid, TestOutcome>();

        #endregion

        #region Constructor

        /// <summary>
        /// Default constructor.
        /// </summary>
        public ConsoleLogger()
        {
        }

        /// <summary>
        /// Constructor added for testing purpose
        /// </summary>
        /// <param name="output"></param>
        internal ConsoleLogger(IOutput output, IProgressIndicator progressIndicator)
        {
            ConsoleLogger.Output = output;
            this.progressIndicator = progressIndicator;
        }

        #endregion

        #region Properties 

        /// <summary>
        /// Gets instance of IOutput used for sending output.
        /// </summary>
        /// <remarks>Protected so this can be detoured for testing purposes.</remarks>
        protected static IOutput Output
        {
            get;
            private set;
        }

        private IProgressIndicator progressIndicator;

        /// <summary>
        /// Get the verbosity level for the console logger
        /// </summary>
        public Verbosity VerbosityLevel => verbosityLevel;

        #endregion

        #region ITestLoggerWithParameters

        /// <summary>
        /// Initializes the Test Logger.
        /// </summary>
        /// <param name="events">Events that can be registered for.</param>
        /// <param name="testRunDirectory">Test Run Directory</param>
        public void Initialize(TestLoggerEvents events, string testRunDirectory)
        {
            if (events == null)
            {
                throw new ArgumentNullException("events");
            }

            if (ConsoleLogger.Output == null)
            {
                ConsoleLogger.Output = ConsoleOutput.Instance;
            }

            if (this.progressIndicator == null && !Console.IsOutputRedirected && EnableProgress)
            {
                // Progress indicator needs to be displayed only for cli experience.
                this.progressIndicator = new ProgressIndicator(Output, new ConsoleHelper());
            }

            // Register for the events.
            events.TestRunMessage += this.TestMessageHandler;
            events.TestResult += this.TestResultHandler;
            events.TestRunComplete += this.TestRunCompleteHandler;
            events.TestRunStart += this.TestRunStartHandler;

            // Register for the discovery events.
            events.DiscoveryMessage += this.TestMessageHandler;

            // TODO Get changes from https://github.com/Microsoft/vstest/pull/1111/
            // events.DiscoveredTests += DiscoveredTestsHandler;
        }

        public void Initialize(TestLoggerEvents events, Dictionary<string, string> parameters)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            if (parameters.Count == 0)
            {
                throw new ArgumentException("No default parameters added", nameof(parameters));
            }

            var verbosityExists = parameters.TryGetValue(ConsoleLogger.VerbosityParam, out string verbosity);
            if (verbosityExists && Enum.TryParse(verbosity, true, out Verbosity verbosityLevel))
            {
                this.verbosityLevel = verbosityLevel;
            }

            var prefixExists = parameters.TryGetValue(ConsoleLogger.PrefixParam, out string prefix);
            if (prefixExists)
            {
                bool.TryParse(prefix, out AppendPrefix);
            }

            var progressArgExists = parameters.TryGetValue(ConsoleLogger.ProgressIndicatorParam, out string enableProgress);
            if (progressArgExists)
            {
                bool.TryParse(enableProgress, out EnableProgress);
            }

            Initialize(events, String.Empty);
        }
        #endregion

        #region Private Methods

        /// <summary>
        /// Prints the timespan onto console. 
        /// </summary>
        private static void PrintTimeSpan(TimeSpan timeSpan)
        {
            if (timeSpan.TotalDays >= 1)
            {
                Output.Information(false, string.Format(CultureInfo.CurrentCulture, CommandLineResources.ExecutionTimeFormatString, timeSpan.TotalDays, CommandLineResources.Days));
            }
            else if (timeSpan.TotalHours >= 1)
            {
                Output.Information(false, string.Format(CultureInfo.CurrentCulture, CommandLineResources.ExecutionTimeFormatString, timeSpan.TotalHours, CommandLineResources.Hours));
            }
            else if (timeSpan.TotalMinutes >= 1)
            {
                Output.Information(false, string.Format(CultureInfo.CurrentCulture, CommandLineResources.ExecutionTimeFormatString, timeSpan.TotalMinutes, CommandLineResources.Minutes));
            }
            else
            {
                Output.Information(false, string.Format(CultureInfo.CurrentCulture, CommandLineResources.ExecutionTimeFormatString, timeSpan.TotalSeconds, CommandLineResources.Seconds));
            }
        }

        /// <summary>
        /// Constructs a well formatted string using the given prefix before every message content on each line.
        /// </summary>
        private static string GetFormattedOutput(Collection<TestResultMessage> testMessageCollection)
        {
            if (testMessageCollection != null)
            {
                var sb = new StringBuilder();
                foreach (var message in testMessageCollection)
                {
                    var prefix = String.Format(CultureInfo.CurrentCulture, "{0}{1}", Environment.NewLine, TestMessageFormattingPrefix);
                    var messageText = message.Text?.Replace(Environment.NewLine, prefix).TrimEnd(TestMessageFormattingPrefix.ToCharArray());

                    if (!string.IsNullOrWhiteSpace(messageText))
                    {
                        sb.AppendFormat(CultureInfo.CurrentCulture, "{0}{1}", TestMessageFormattingPrefix, messageText);
                    }
                }
                return sb.ToString();
            }
            return String.Empty;
        }

        /// <summary>
        /// Collects all the messages of a particular category(Standard Output/Standard Error/Debug Traces) and returns a collection.
        /// </summary>
        private static Collection<TestResultMessage> GetTestMessages(Collection<TestResultMessage> Messages, string requiredCategory)
        {
            var selectedMessages = Messages.Where(msg => msg.Category.Equals(requiredCategory, StringComparison.OrdinalIgnoreCase));
            var requiredMessageCollection = new Collection<TestResultMessage>(selectedMessages.ToList());
            return requiredMessageCollection;
        }

        /// <summary>
        /// outputs the Error messages, Stack Trace, and other messages for the parameter test.
        /// </summary>
        private static void DisplayFullInformation(TestResult result)
        {

            // Add newline if it is not in given output data.
            var addAdditionalNewLine = false;

            Debug.Assert(result != null, "a null result can not be displayed");
            if (!String.IsNullOrEmpty(result.ErrorMessage))
            {
                addAdditionalNewLine = true;
                Output.Information(false, ConsoleColor.Red, string.Format("{0}{1}", TestResultPrefix, CommandLineResources.ErrorMessageBanner));
                var errorMessage = String.Format(CultureInfo.CurrentCulture, "{0}{1}{2}", TestResultPrefix, TestMessageFormattingPrefix, result.ErrorMessage);
                Output.Information(false, ConsoleColor.Red, errorMessage);
            }

            if (!String.IsNullOrEmpty(result.ErrorStackTrace))
            {
                addAdditionalNewLine = false;
                Output.Information(false, ConsoleColor.Red, string.Format("{0}{1}", TestResultPrefix, CommandLineResources.StacktraceBanner));
                var stackTrace = String.Format(CultureInfo.CurrentCulture, "{0}{1}", TestResultPrefix, result.ErrorStackTrace);
                Output.Information(false, ConsoleColor.Red, stackTrace);
            }

            var stdOutMessagesCollection = GetTestMessages(result.Messages, TestResultMessage.StandardOutCategory);
            if (stdOutMessagesCollection.Count > 0)
            {
                addAdditionalNewLine = true;
                var stdOutMessages = GetFormattedOutput(stdOutMessagesCollection);

                if (!string.IsNullOrEmpty(stdOutMessages))
                {
                    Output.Information(false, string.Format("{0}{1}", TestResultPrefix, CommandLineResources.StdOutMessagesBanner));
                    Output.Information(false, stdOutMessages);
                }
            }

            var stdErrMessagesCollection = GetTestMessages(result.Messages, TestResultMessage.StandardErrorCategory);
            if (stdErrMessagesCollection.Count > 0)
            {
                addAdditionalNewLine = false;
                var stdErrMessages = GetFormattedOutput(stdErrMessagesCollection);

                if (!string.IsNullOrEmpty(stdErrMessages))
                {
                    Output.Information(false, ConsoleColor.Red, string.Format("{0}{1}", TestResultPrefix, CommandLineResources.StdErrMessagesBanner));
                    Output.Information(false, ConsoleColor.Red, stdErrMessages);
                }
            }

            var DbgTrcMessagesCollection = GetTestMessages(result.Messages, TestResultMessage.DebugTraceCategory);
            if (DbgTrcMessagesCollection.Count > 0)
            {
                addAdditionalNewLine = false;
                var dbgTrcMessages = GetFormattedOutput(DbgTrcMessagesCollection);

                if (!string.IsNullOrEmpty(dbgTrcMessages))
                {
                    Output.Information(false, string.Format("{0}{1}", TestResultPrefix, CommandLineResources.DbgTrcMessagesBanner));
                    Output.Information(false, dbgTrcMessages);
                }
            }

            var addnlInfoMessagesCollection = GetTestMessages(result.Messages, TestResultMessage.AdditionalInfoCategory);
            if (addnlInfoMessagesCollection.Count > 0)
            {
                addAdditionalNewLine = false;
                var addnlInfoMessages = GetFormattedOutput(addnlInfoMessagesCollection);

                if (!string.IsNullOrEmpty(addnlInfoMessages))
                {
                    Output.Information(false, string.Format("{0}{1}", TestResultPrefix, CommandLineResources.AddnlInfoMessagesBanner));
                    Output.Information(false, addnlInfoMessages);
                }
            }

            if (addAdditionalNewLine)
            {
                Output.WriteLine(String.Empty, OutputLevel.Information);
            }
        }

        /// <summary>
        /// Returns the parent Execution id of given test result.
        /// </summary>
        /// <param name="testResult"></param>
        /// <returns></returns>
        private Guid GetParentExecutionId(TestResult testResult)
        {
            var parentExecutionIdProperty = testResult.Properties.FirstOrDefault(property =>
                property.Id.Equals(ParentExecutionIdPropertyIdentifier));
            return parentExecutionIdProperty == null
                ? Guid.Empty
                : testResult.GetPropertyValue(parentExecutionIdProperty, Guid.Empty);
        }

        /// <summary>
        /// Returns execution id of given test result
        /// </summary>
        /// <param name="testResult"></param>
        /// <returns></returns>
        private Guid GetExecutionId(TestResult testResult)
        {
            var executionIdProperty = testResult.Properties.FirstOrDefault(property =>
                property.Id.Equals(ExecutionIdPropertyIdentifier));
            var executionId = Guid.Empty;

            if (executionIdProperty != null)
            {
                executionId = testResult.GetPropertyValue(executionIdProperty, Guid.Empty);
            }

            return executionId.Equals(Guid.Empty) ? Guid.NewGuid() : executionId;
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Called when a test run start is received
        /// </summary>
        private void TestRunStartHandler(object sender, TestRunStartEventArgs e)
        {
            ValidateArg.NotNull<object>(sender, "sender");
            ValidateArg.NotNull<TestRunStartEventArgs>(e, "e");

            // Print all test containers.
            Output.WriteLine(string.Empty, OutputLevel.Information);
            Output.WriteLine(string.Format(CultureInfo.CurrentCulture, CommandLineResources.TestSourcesDiscovered, CommandLineOptions.Instance.Sources.Count()), OutputLevel.Information);
            if (verbosityLevel == Verbosity.Detailed)
            {
                foreach (var source in CommandLineOptions.Instance.Sources)
                {
                    Output.WriteLine(source, OutputLevel.Information);
                }
            }
        }

        /// <summary>
        /// Called when a test message is received.
        /// </summary>
        private void TestMessageHandler(object sender, TestRunMessageEventArgs e)
        {
            ValidateArg.NotNull<object>(sender, "sender");
            ValidateArg.NotNull<TestRunMessageEventArgs>(e, "e");

            switch (e.Level)
            {
                case TestMessageLevel.Informational:
                    {
                        if (verbosityLevel == Verbosity.Quiet || verbosityLevel == Verbosity.Minimal)
                        {
                            break;
                        }

                        // Pause the progress indicator to print the message
                        this.progressIndicator?.Pause();

                        Output.Information(AppendPrefix, e.Message);

                        // Resume the progress indicator after printing the message
                        this.progressIndicator?.Start();

                        break;
                    }

                case TestMessageLevel.Warning:
                    {
                        if (verbosityLevel == Verbosity.Quiet)
                        {
                            break;
                        }

                        // Pause the progress indicator to print the message
                        this.progressIndicator?.Pause();

                        Output.Warning(AppendPrefix, e.Message);

                        // Resume the progress indicator after printing the message
                        this.progressIndicator?.Start();

                        break;
                    }

                case TestMessageLevel.Error:
                    {
                        // Pause the progress indicator to print the message
                        this.progressIndicator?.Pause();

                        this.testRunHasErrorMessages = true;
                        Output.Error(AppendPrefix, e.Message);

                        // Resume the progress indicator after printing the message
                        this.progressIndicator?.Start();

                        break;
                    }
                default:
                    EqtTrace.Warning("ConsoleLogger.TestMessageHandler: The test message level is unrecognized: {0}", e.Level.ToString());
                    break;
            }
        }

        /// <summary>
        /// Called when a test result is received.
        /// </summary>
        private void TestResultHandler(object sender, TestResultEventArgs e)
        {
            ValidateArg.NotNull<object>(sender, "sender");
            ValidateArg.NotNull<TestResultEventArgs>(e, "e");

            var testDisplayName = e.Result.DisplayName;

            if (string.IsNullOrWhiteSpace(e.Result.DisplayName))
            {
                testDisplayName = e.Result.TestCase.DisplayName;
            }

            string formattedDuration = this.GetFormattedDurationString(e.Result.Duration);
            if (!string.IsNullOrEmpty(formattedDuration))
            {
                testDisplayName = string.Format("{0} [{1}]", testDisplayName, formattedDuration);
            }

            var executionId = GetExecutionId(e.Result);
            var parentExecutionId = GetParentExecutionId(e.Result);

            if (parentExecutionId != Guid.Empty && leafExecutionIdAndTestOutcomePairDictionary.ContainsKey(parentExecutionId))
            {
                leafExecutionIdAndTestOutcomePairDictionary.TryRemove(parentExecutionId, out _);
            }

            leafExecutionIdAndTestOutcomePairDictionary.TryAdd(executionId, e.Result.Outcome);

            switch (e.Result.Outcome)
            {
                case TestOutcome.Skipped:
                    {
                        if (this.verbosityLevel == Verbosity.Quiet)
                        {
                            break;
                        }

                        // Pause the progress indicator before displaying test result information
                        this.progressIndicator?.Pause();

                        Output.Write(string.Format("{0}{1} ", TestResultPrefix, SkippedTestIndicator), OutputLevel.Information, ConsoleColor.Yellow);
                        Output.WriteLine(testDisplayName, OutputLevel.Information);
                        if (this.verbosityLevel == Verbosity.Detailed)
                        {
                            DisplayFullInformation(e.Result);
                        }

                        // Resume the progress indicator after displaying the test result information
                        this.progressIndicator?.Start();

                        break;
                    }

                case TestOutcome.Failed:
                    {
                        if (this.verbosityLevel == Verbosity.Quiet)
                        {
                            break;
                        }

                        // Pause the progress indicator before displaying test result information
                        this.progressIndicator?.Pause();

                        Output.Write(string.Format("{0}{1} ", TestResultPrefix, FailedTestIndicator), OutputLevel.Information, ConsoleColor.Red);
                        Output.WriteLine(testDisplayName, OutputLevel.Information);
                        DisplayFullInformation(e.Result);

                        // Resume the progress indicator after displaying the test result information
                        this.progressIndicator?.Start();

                        break;
                    }

                case TestOutcome.Passed:
                    {
                        if (this.verbosityLevel == Verbosity.Normal || this.verbosityLevel == Verbosity.Detailed)
                        {
                            // Pause the progress indicator before displaying test result information
                            this.progressIndicator?.Pause();

                            Output.Write(string.Format("{0}{1} ", TestResultPrefix, PassedTestIndicator), OutputLevel.Information, ConsoleColor.Green);
                            Output.WriteLine(testDisplayName, OutputLevel.Information);
                            if (this.verbosityLevel == Verbosity.Detailed)
                            {
                                DisplayFullInformation(e.Result);
                            }

                            // Resume the progress indicator after displaying the test result information
                            this.progressIndicator?.Start();
                        }

                        break;
                    }

                default:
                    {
                        if (this.verbosityLevel == Verbosity.Quiet)
                        {
                            break;
                        }

                        // Pause the progress indicator before displaying test result information
                        this.progressIndicator?.Pause();

                        Output.Write(string.Format("{0}{1} ", TestResultPrefix, SkippedTestIndicator), OutputLevel.Information, ConsoleColor.Yellow);
                        Output.WriteLine(testDisplayName, OutputLevel.Information);
                        if (this.verbosityLevel == Verbosity.Detailed)
                        {
                            DisplayFullInformation(e.Result);
                        }

                        // Resume the progress indicator after displaying the test result information
                        this.progressIndicator?.Start();

                        break;
                    }
            }
        }

        private string GetFormattedDurationString(TimeSpan duration)
        {
            if (duration == default(TimeSpan))
            {
                return null;
            }

            var time = new List<string>();
            if (duration.Hours > 0)
            {
                time.Add(duration.Hours + "h");
            }

            if (duration.Minutes > 0)
            {
                time.Add(duration.Minutes + "m");
            }

            if (duration.Hours == 0)
            {
                if (duration.Seconds > 0)
                {
                    time.Add(duration.Seconds + "s");
                }

                if (duration.Milliseconds > 0 && duration.Minutes == 0)
                {
                    time.Add(duration.Milliseconds + "ms");
                }
            }

            return time.Count == 0 ? "< 1ms" : string.Join(" ", time);
        }

        /// <summary>
        /// Called when a test run is completed.
        /// </summary>
        private void TestRunCompleteHandler(object sender, TestRunCompleteEventArgs e)
        {
            var testsTotal = 0;
            var testsPassed = 0;
            var testsFailed = 0;
            var testsSkipped = 0;
            // Stop the progress indicator as we are about to print the summary
            this.progressIndicator?.Stop();
            Output.WriteLine(string.Empty, OutputLevel.Information);

            // Printing Run-level Attachments
            var runLevelAttachementCount = (e.AttachmentSets == null) ? 0 : e.AttachmentSets.Sum(attachmentSet => attachmentSet.Attachments.Count);
            if (runLevelAttachementCount > 0)
            {
                Output.Information(false, CommandLineResources.AttachmentsBanner);
                foreach (var attachmentSet in e.AttachmentSets)
                {
                    foreach (var uriDataAttachment in attachmentSet.Attachments)
                    {
                        var attachmentOutput = string.Format(CultureInfo.CurrentCulture, CommandLineResources.AttachmentOutputFormat, uriDataAttachment.Uri.LocalPath);
                        Output.Information(false, attachmentOutput);
                    }
                }
            }

            foreach (KeyValuePair<Guid, TestOutcome> entry in leafExecutionIdAndTestOutcomePairDictionary)
            {
                testsTotal++;
                switch (entry.Value)
                {
                    case TestOutcome.Failed:
                        testsFailed++;
                        break;
                    case TestOutcome.Passed:
                        testsPassed++;
                        break;
                    case TestOutcome.Skipped:
                        testsSkipped++;
                        break;
                    default:
                        break;
                }
            }

            if (e.IsCanceled)
            {
                Output.Error(false, CommandLineResources.TestRunCanceled);
            }
            else if (e.IsAborted)
            {
                Output.Error(false, CommandLineResources.TestRunAborted);
            }
            else if (testsFailed > 0 || this.testRunHasErrorMessages)
            {
                Output.Error(false, CommandLineResources.TestRunFailed);
            }
            else if (testsTotal > 0)
            {
                Output.Information(false, ConsoleColor.Green, CommandLineResources.TestRunSuccessful);
            }

            // Output a summary.
            if (testsTotal > 0)
            {
                string totalTestsformat = (e.IsAborted || e.IsCanceled) ? CommandLineResources.TestRunSummaryForCanceledOrAbortedRun : CommandLineResources.TestRunSummaryTotalTests;
                Output.Information(false, string.Format(CultureInfo.CurrentCulture, totalTestsformat, testsTotal));

                if (testsPassed > 0)
                {
                    Output.Information(false, ConsoleColor.Green, string.Format(CultureInfo.CurrentCulture, CommandLineResources.TestRunSummaryPassedTests, testsPassed));
                }
                if (testsFailed > 0)
                {
                    Output.Information(false, ConsoleColor.Red, string.Format(CultureInfo.CurrentCulture, CommandLineResources.TestRunSummaryFailedTests, testsFailed));
                }
                if (testsSkipped > 0)
                {
                    Output.Information(false, ConsoleColor.Yellow, string.Format(CultureInfo.CurrentCulture, CommandLineResources.TestRunSummarySkippedTests, testsSkipped));
                }
            }

            if (testsTotal > 0)
            {
                if (e.ElapsedTimeInRunningTests.Equals(TimeSpan.Zero))
                {
                    EqtTrace.Info("Skipped printing test execution time on console because it looks like the test run had faced some errors");
                }
                else
                {
                    PrintTimeSpan(e.ElapsedTimeInRunningTests);
                }
            }
        }
        #endregion

        /// <summary>
        /// Raises test run warning occurred before console logger starts listening warning events.
        /// </summary>
        /// <param name="warningMessage"></param>
        public static void RaiseTestRunWarning(string warningMessage)
        {
            if (ConsoleLogger.Output == null)
            {
                ConsoleLogger.Output = ConsoleOutput.Instance;
            }

            Output.Warning(AppendPrefix, warningMessage);
        }
    }
}
