﻿// Copyright (c) Spekt Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.Extension.NUnit.Xml.TestLogger
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Xml;
    using System.Xml.Linq;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;

    [FriendlyName(FriendlyName)]
    [ExtensionUri(ExtensionUri)]
    public class NUnitXmlTestLogger : ITestLoggerWithParameters
    {
        /// <summary>
        /// Uri used to uniquely identify the logger.
        /// </summary>
        public const string ExtensionUri = "logger://Microsoft/TestPlatform/NUnitXmlLogger/v1";

        /// <summary>
        /// Alternate user friendly string to uniquely identify the console logger.
        /// </summary>
        public const string FriendlyName = "nunit";

        public const string LogFilePathKey = "LogFilePath";
        public const string LogFileName = "LogFileName";

        public const string ResultDirectoryKey = "TestRunDirectory";

        private const string ResultStatusPassed = "Passed";
        private const string ResultStatusFailed = "Failed";

        private const string DateFormat = "yyyy-MM-ddT HH:mm:ssZ";

        private const string AssemblyToken = "{assembly}";
        private const string FrameworkToken = "{framework}";

        private readonly object resultsGuard = new object();
        private string outputFilePath;

        private List<TestResultInfo> results;
        private DateTime localStartTime;

        public static IEnumerable<TestSuite> GroupTestSuites(IEnumerable<TestSuite> suites)
        {
            var groups = suites;
            var roots = new List<TestSuite>();
            while (groups.Any())
            {
                groups = groups.GroupBy(r =>
                                {
                                    var name = r.FullName.SubstringBeforeDot();
                                    if (string.IsNullOrEmpty(name))
                                    {
                                        roots.Add(r);
                                    }

                                    return name;
                                })
                                .OrderBy(g => g.Key)
                                .Where(g => !string.IsNullOrEmpty(g.Key))
                                .Select(g => AggregateTestSuites(g, "TestSuite", g.Key.SubstringAfterDot(), g.Key))
                                .ToList();
            }

            return roots;
        }

        public void Initialize(TestLoggerEvents events, string testResultsDirPath)
        {
            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            if (testResultsDirPath == null)
            {
                throw new ArgumentNullException(nameof(testResultsDirPath));
            }

            var outputPath = Path.Combine(testResultsDirPath, "TestResults.xml");
            this.InitializeImpl(events, outputPath);
        }

        public void Initialize(TestLoggerEvents events, Dictionary<string, string> parameters)
        {
            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            if (parameters.TryGetValue(LogFileName, out string outputPathName) && parameters.TryGetValue(ResultDirectoryKey, out string outputFileDirectory))
            {
                outputPathName = Path.Combine(outputFileDirectory, outputPathName);
                this.InitializeImpl(events, outputPathName);
            }
            else if (parameters.TryGetValue(LogFilePathKey, out string outputPath))
            {
                this.InitializeImpl(events, outputPath);
            }
            else if (parameters.TryGetValue(DefaultLoggerParameterNames.TestRunDirectory, out string outputDir))
            {
                this.Initialize(events, outputDir);
            }
            else
            {
                throw new ArgumentException($"Expected {LogFilePathKey} or {DefaultLoggerParameterNames.TestRunDirectory} parameter", nameof(parameters));
            }
        }

        /// <summary>
        /// Called when a test message is received.
        /// </summary>
        internal void TestMessageHandler(object sender, TestRunMessageEventArgs e)
        {
        }

        /// <summary>
        /// Called when a test starts.
        /// </summary>
        internal void TestRunStartHandler(object sender, TestRunStartEventArgs e)
        {
            if (this.outputFilePath.Contains(AssemblyToken))
            {
                string assemblyPath = e.TestRunCriteria.AdapterSourceMap["_none_"].First();
                string assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
                this.outputFilePath = this.outputFilePath.Replace(AssemblyToken, assemblyName);
            }

            if (this.outputFilePath.Contains(FrameworkToken))
            {
                XmlDocument runSettings = new XmlDocument();
                runSettings.LoadXml(e.TestRunCriteria.TestRunSettings);
                XmlNode x = runSettings.GetElementsByTagName("TargetFrameworkVersion")[0];
                string framework = x.InnerText;
                framework = framework.Replace(",Version=v", string.Empty).Replace(".", string.Empty);
                this.outputFilePath = this.outputFilePath.Replace(FrameworkToken, framework);
            }
        }

        /// <summary>
        /// Called when a test result is received.
        /// </summary>
        internal void TestResultHandler(object sender, TestResultEventArgs e)
        {
            TestResult result = e.Result;

            var parsedName = TestCaseNameParser.Parse(result.TestCase.FullyQualifiedName);

            lock (this.resultsGuard)
            {
                this.results.Add(new TestResultInfo(
                    result,
                    parsedName.NamespaceName,
                    parsedName.TypeName,
                    parsedName.MethodName));
            }
        }

        /// <summary>
        /// Called when a test run is completed.
        /// </summary>
        internal void TestRunCompleteHandler(object sender, TestRunCompleteEventArgs e)
        {
            List<TestResultInfo> resultList;
            lock (this.resultsGuard)
            {
                resultList = this.results;
                this.results = new List<TestResultInfo>();
            }

            var doc = new XDocument(this.CreateTestRunElement(resultList));

            // Create directory if not exist
            var loggerFileDirPath = Path.GetDirectoryName(this.outputFilePath);
            if (!Directory.Exists(loggerFileDirPath))
            {
                Directory.CreateDirectory(loggerFileDirPath);
            }

            using (var f = File.Create(this.outputFilePath))
            {
                doc.Save(f);
            }

            var resultsFileMessage = string.Format(CultureInfo.CurrentCulture, "Results File: {0}", this.outputFilePath);
            Console.WriteLine(resultsFileMessage);
        }

        private static TestSuite AggregateTestSuites(
            IEnumerable<TestSuite> suites,
            string testSuiteType,
            string name,
            string fullName)
        {
            var element = new XElement("test-suite");

            int total = 0;
            int passed = 0;
            int failed = 0;
            int skipped = 0;
            int inconclusive = 0;
            int error = 0;
            var time = TimeSpan.Zero;
            DateTime? startTime = null;
            DateTime? endTime = null;

            foreach (var result in suites)
            {
                total += result.Total;
                passed += result.Passed;
                failed += result.Failed;
                skipped += result.Skipped;
                inconclusive += result.Inconclusive;
                error += result.Error;
                time += result.Time;

                if (result.StartTime.HasValue && (!startTime.HasValue || result.StartTime.Value < startTime.Value))
                {
                    startTime = result.StartTime;
                }

                if (result.EndTime.HasValue && (!endTime.HasValue || result.EndTime.Value > endTime.Value))
                {
                    endTime = result.EndTime;
                }

                element.Add(result.Element);
            }

            element.SetAttributeValue("type", testSuiteType);
            element.SetAttributeValue("name", name);
            element.SetAttributeValue("fullname", fullName);
            element.SetAttributeValue("total", total);
            element.SetAttributeValue("passed", passed);
            element.SetAttributeValue("failed", failed);
            element.SetAttributeValue("inconclusive", inconclusive);
            element.SetAttributeValue("skipped", skipped);

            var resultString = failed > 0 ? ResultStatusFailed : ResultStatusPassed;
            element.SetAttributeValue("result", resultString);

            if (startTime.HasValue)
            {
                element.SetAttributeValue("start-time", startTime.Value.ToString(DateFormat, CultureInfo.InvariantCulture));
            }

            if (endTime.HasValue)
            {
                element.SetAttributeValue("end-time", endTime.Value.ToString(DateFormat, CultureInfo.InvariantCulture));
            }

            element.SetAttributeValue("duration", time.TotalSeconds);

            return new TestSuite
            {
                Element = element,
                Name = name,
                FullName = fullName,
                Total = total,
                Passed = passed,
                Failed = failed,
                Inconclusive = inconclusive,
                Skipped = skipped,
                Error = error,
                StartTime = startTime,
                EndTime = endTime,
                Time = time
            };
        }

        private static TestSuite AggregateTestCases(
            IEnumerable<TestCase> cases,
            string fullTypeName,
            string methodName)
        {
            var fullName = $"{fullTypeName}.{methodName}";
            var element = new XElement("test-suite");

            var total = 0;
            var passed = 0;
            var failed = 0;
            var skipped = 0;
            var inconclusive = 0;
            var error = 0;
            var time = TimeSpan.Zero;
            DateTime? startTime = null;
            DateTime? endTime = null;

            foreach (var result in cases)
            {
                total += result.Total;
                passed += result.Passed;
                failed += result.Failed;
                skipped += result.Skipped;
                inconclusive += result.Inconclusive;
                error += result.Error;
                time += result.Time;

                if (result.StartTime.HasValue && (!startTime.HasValue || result.StartTime.Value < startTime.Value))
                {
                    startTime = result.StartTime;
                }

                if (result.EndTime.HasValue && (!endTime.HasValue || result.EndTime.Value > endTime.Value))
                {
                    endTime = result.EndTime;
                }

                element.Add(result.Element);
            }

            element.SetAttributeValue("type", "ParameterizedMethod");
            element.SetAttributeValue("name", methodName);
            element.SetAttributeValue("fullname", fullName);
            element.SetAttributeValue("className", fullTypeName);
            element.SetAttributeValue("total", total);
            element.SetAttributeValue("passed", passed);
            element.SetAttributeValue("failed", failed);
            element.SetAttributeValue("inconclusive", inconclusive);
            element.SetAttributeValue("skipped", skipped);

            var resultString = failed > 0 ? ResultStatusFailed : ResultStatusPassed;
            element.SetAttributeValue("result", resultString);

            if (startTime.HasValue)
            {
                element.SetAttributeValue("start-time", startTime.Value.ToString(DateFormat, CultureInfo.InvariantCulture));
            }

            if (endTime.HasValue)
            {
                element.SetAttributeValue("end-time", endTime.Value.ToString(DateFormat, CultureInfo.InvariantCulture));
            }

            element.SetAttributeValue("duration", time.TotalSeconds);

            return new TestSuite
            {
                Element = element,
                Name = methodName,
                FullName = fullName,
                Total = total,
                Passed = passed,
                Failed = failed,
                Inconclusive = inconclusive,
                Skipped = skipped,
                Error = error,
                StartTime = startTime,
                EndTime = endTime,
                Time = time
            };
        }

        private TestSuite CreateFixture(IGrouping<string, TestResultInfo> resultsByType)
        {
            var element = new XElement("test-suite");

            int total = 0;
            int passed = 0;
            int failed = 0;
            int skipped = 0;
            int inconclusive = 0;
            int error = 0;
            var time = TimeSpan.Zero;
            DateTime? startTime = null;
            DateTime? endTime = null;

            var testFixtureType = ReflectionUtility.GetTestFixtureType(resultsByType.FirstOrDefault());
            var properties = this.CreateFixturePropertiesElement(testFixtureType);
            element.Add(properties);

            var group = resultsByType
                .GroupBy(x => x.TestResultKey())
                .OrderBy(x => x.Key);

            foreach (var entry in group)
            {
                if (entry.Count() == 1)
                {
                    var testResult = entry.First();
                    var propertiesElement = CreatePropertiesElement(testResult);
                    var testCase = CreateTestCase(testResult);
                    testCase.Element.Add(propertiesElement);

                    failed += testCase.Failed;
                    passed += testCase.Passed;
                    skipped += testCase.Skipped;
                    inconclusive += testCase.Inconclusive;
                    total++;
                    time += testCase.Time;

                    if (!startTime.HasValue || testCase.StartTime < startTime)
                    {
                        startTime = testCase.StartTime;
                    }

                    if (!endTime.HasValue || testCase.EndTime > endTime)
                    {
                        endTime = testCase.EndTime;
                    }

                    element.Add(testCase.Element);
                }
                else
                {
                    var testSuite = AggregateTestCases(entry.Select(CreateTestCase), entry.Key.fullTypeName, entry.Key.methodName);
                    failed += testSuite.Failed;
                    passed += testSuite.Passed;
                    skipped += testSuite.Skipped;
                    inconclusive += testSuite.Inconclusive;
                    total += testSuite.Total;
                    time += testSuite.Time;

                    if (!startTime.HasValue || testSuite.StartTime < startTime)
                    {
                        startTime = testSuite.StartTime;
                    }

                    if (!endTime.HasValue || testSuite.EndTime > endTime)
                    {
                        endTime = testSuite.EndTime;
                    }

                    var firstTestResult = entry.First();
                    var propertiesElement = CreatePropertiesElement(firstTestResult);
                    testSuite.Element.AddFirst(propertiesElement);

                    element.Add(testSuite.Element);
                }
            }

            // Create test-suite element for the TestFixture
            var name = resultsByType.Key.SubstringAfterDot();

            element.SetAttributeValue("type", "TestFixture");
            element.SetAttributeValue("name", name);
            element.SetAttributeValue("fullname", resultsByType.Key);

            element.SetAttributeValue("total", total);
            element.SetAttributeValue("passed", passed);
            element.SetAttributeValue("failed", failed);
            element.SetAttributeValue("inconclusive", inconclusive);
            element.SetAttributeValue("skipped", skipped);

            var resultString = failed > 0 ? ResultStatusFailed : ResultStatusPassed;
            element.SetAttributeValue("result", resultString);

            if (startTime.HasValue)
            {
                element.SetAttributeValue("start-time", startTime.Value.ToString(DateFormat, CultureInfo.InvariantCulture));
            }

            if (endTime.HasValue)
            {
                element.SetAttributeValue("end-time", endTime.Value.ToString(DateFormat, CultureInfo.InvariantCulture));
            }

            element.SetAttributeValue("duration", time.TotalSeconds);

            return new TestSuite
            {
                Element = element,
                Name = name,
                FullName = resultsByType.Key,
                Total = total,
                Passed = passed,
                Failed = failed,
                Inconclusive = inconclusive,
                Skipped = skipped,
                Error = error,
                StartTime = startTime,
                EndTime = endTime,
                Time = time
            };
        }

#pragma warning disable SA1204 // Static elements should appear before instance elements
        private static TestCase CreateTestCase(TestResultInfo result)
#pragma warning restore SA1204 // Static elements should appear before instance elements
        {
            var element = new XElement(
                "test-case",
                new XAttribute("name", result.Name),
                new XAttribute("fullname", result.FullTypeName + "." + result.Method),
                new XAttribute("methodname", result.Method),
                new XAttribute("classname", result.FullTypeName),
                new XAttribute("result", OutcomeToString(result.Outcome)),
                new XAttribute("start-time", result.StartTime.ToString(DateFormat, CultureInfo.InvariantCulture)),
                new XAttribute("end-time", result.EndTime.ToString(DateFormat, CultureInfo.InvariantCulture)),
                new XAttribute("duration", result.Duration.TotalSeconds),
                new XAttribute("asserts", 0));

            StringBuilder stdOut = new StringBuilder();
            foreach (var m in result.Messages)
            {
                if (TestResultMessage.StandardOutCategory.Equals(m.Category, StringComparison.OrdinalIgnoreCase))
                {
                    stdOut.AppendLine(m.Text);
                }
            }

            if (!string.IsNullOrWhiteSpace(stdOut.ToString()))
            {
                element.Add(new XElement("output", new XCData(stdOut.ToString())));
            }

            if (result.Outcome == TestOutcome.Failed)
            {
                element.Add(new XElement(
                    "failure",
                    new XElement("message", result.ErrorMessage.ReplaceInvalidXmlChar()),
                    new XElement("stack-trace", result.ErrorStackTrace.ReplaceInvalidXmlChar())));
            }

            var testCase = new TestCase
                {
                    Element = element,
                    Name = result.Name,
                    FullName = result.FullTypeName + "." + result.Method,
                    StartTime = result.StartTime,
                    EndTime = result.EndTime,
                    Time = result.Duration
                };

            switch (result.Outcome)
            {
                case TestOutcome.Failed:
                    testCase.Failed = 1;
                    testCase.Total = 1;
                    break;

                case TestOutcome.Passed:
                    testCase.Passed = 1;
                    testCase.Total = 1;
                    break;

                case TestOutcome.Skipped:
                    testCase.Skipped = 1;
                    testCase.Total = 1;
                    break;

                case TestOutcome.None:
                    testCase.Inconclusive = 1;
                    testCase.Total = 1;
                    break;
            }

            return testCase;
        }

        private static XElement CreatePropertiesElement(TestResultInfo result)
        {
            var propertyElements = new HashSet<XElement>(result.Traits.Select(CreatePropertyElement));

#pragma warning disable CS0618 // Type or member is obsolete

            // Required since TestCase.Properties is a superset of TestCase.Traits
            // Unfortunately not all NUnit properties are available as traits
            var traitProperties = result.TestCase.Properties.Where(t => t.Attributes.HasFlag(TestPropertyAttributes.Trait));

#pragma warning restore CS0618 // Type or member is obsolete

            foreach (var p in traitProperties)
            {
                var propValue = result.TestCase.GetPropertyValue(p);

                if (p.Id == "NUnit.TestCategory")
                {
                    var elements = CreatePropertyElement("Category", (string[])propValue);

                    foreach (var element in elements)
                    {
                        propertyElements.Add(element);
                    }
                }
            }

            // NUnit attributes not passed through in traits.
            var description = ReflectionUtility.GetDescription(result);
            if (description != null)
            {
                var propertyElement = CreatePropertyElement("Description", description).Single();
                propertyElements.Add(propertyElement);
            }

            return propertyElements.Any()
                ? new XElement("properties", propertyElements.Distinct())
                : null;
        }

        private static XElement CreatePropertyElement(Trait trait)
        {
            return CreatePropertyElement(trait.Name, trait.Value).Single();
        }

        private static IEnumerable<XElement> CreatePropertyElement(string name, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("message", nameof(name));
            }

            foreach (var value in values)
            {
                yield return new XElement(
                "property",
                new XAttribute("name", name),
                new XAttribute("value", value));
            }
        }

        private static string OutcomeToString(TestOutcome outcome)
        {
            switch (outcome)
            {
                case TestOutcome.Failed:
                    return ResultStatusFailed;

                case TestOutcome.Passed:
                    return "Passed";

                case TestOutcome.Skipped:
                    return "Skipped";

                default:
                    return "Inconclusive";
            }
        }

        private XElement CreateFixturePropertiesElement(Type testFixtureType)
        {
            if (testFixtureType == null)
            {
                return null;
            }

            var propertyElements = new HashSet<XElement>();

            var attributes = testFixtureType.GetCustomAttributes(false)
                .Cast<Attribute>();

            var description = ReflectionUtility.GetDescription(attributes);
            if (description != null)
            {
                var propertyElement = CreatePropertyElement("Description", description).Single();
                propertyElements.Add(propertyElement);
            }

            var categories = ReflectionUtility.GetCategories(attributes);
            propertyElements.UnionWith(CreatePropertyElement("Category", categories));

            return propertyElements.Any()
                ? new XElement("properties", propertyElements.Distinct())
                : null;
        }

        private void InitializeImpl(TestLoggerEvents events, string outputPath)
        {
            events.TestRunMessage += this.TestMessageHandler;
            events.TestRunStart += this.TestRunStartHandler;
            events.TestResult += this.TestResultHandler;
            events.TestRunComplete += this.TestRunCompleteHandler;

            this.outputFilePath = Path.GetFullPath(outputPath);

            lock (this.resultsGuard)
            {
                this.results = new List<TestResultInfo>();
            }

            this.localStartTime = DateTime.UtcNow;
        }

        private XElement CreateTestRunElement(List<TestResultInfo> results)
        {
            var testSuites = from result in results
                             group result by result.AssemblyPath
                             into resultsByAssembly
                             orderby resultsByAssembly.Key
                             select this.CreateAssemblyElement(resultsByAssembly);

            var element = new XElement("test-run", testSuites);

            element.SetAttributeValue("id", 2);

            element.SetAttributeValue("duration", results.Sum(x => x.Duration.TotalSeconds));

            var total = testSuites.Sum(x => (int)x.Attribute("total"));

            // TODO test case count is actually count before filtering
            element.SetAttributeValue("testcasecount", total);
            element.SetAttributeValue("total", total);
            element.SetAttributeValue("passed", testSuites.Sum(x => (int)x.Attribute("passed")));

            var failed = testSuites.Sum(x => (int)x.Attribute("failed"));
            element.SetAttributeValue("failed", failed);
            element.SetAttributeValue("inconclusive", testSuites.Sum(x => (int)x.Attribute("inconclusive")));
            element.SetAttributeValue("skipped", testSuites.Sum(x => (int)x.Attribute("skipped")));

            var resultString = failed > 0 ? ResultStatusFailed : ResultStatusPassed;
            element.SetAttributeValue("result", resultString);

            element.SetAttributeValue("start-time", this.localStartTime.ToString(DateFormat, CultureInfo.InvariantCulture));
            element.SetAttributeValue("end-time", DateTime.UtcNow.ToString(DateFormat, CultureInfo.InvariantCulture));

            return element;
        }

        private XElement CreateAssemblyElement(IGrouping<string, TestResultInfo> resultsByAssembly)
        {
            var assemblyPath = resultsByAssembly.Key;
            var fixtures = from resultsInAssembly in resultsByAssembly
                           group resultsInAssembly by resultsInAssembly.FullTypeName
                           into resultsByType
                           orderby resultsByType.Key
                           select this.CreateFixture(resultsByType);
            var fixtureGroups = GroupTestSuites(fixtures);
            var suite = AggregateTestSuites(
                fixtureGroups,
                "Assembly",
                Path.GetFileName(assemblyPath),
                assemblyPath);

            XElement errorsElement = new XElement("errors");
            suite.Element.Add(errorsElement);

            return suite.Element;
        }

        public class TestSuite
        {
            public XElement Element { get; set; }

            public string Name { get; set; }

            public string FullName { get; set; }

            public int Total { get; set; }

            public int Passed { get; set; }

            public int Failed { get; set; }

            public int Inconclusive { get; set; }

            public int Skipped { get; set; }

            public int Error { get; set; }

            public TimeSpan Time { get; set; }

            public DateTime? StartTime { get; set; }

            public DateTime? EndTime { get; set; }
        }

        public class TestCase
        {
            public XElement Element { get; set; }

            public string Name { get; set; }

            public string FullName { get; set; }

            public int Total { get; set; }

            public int Passed { get; set; }

            public int Failed { get; set; }

            public int Inconclusive { get; set; }

            public int Skipped { get; set; }

            public int Error { get; set; }

            public TimeSpan Time { get; set; }

            public DateTime? StartTime { get; set; }

            public DateTime? EndTime { get; set; }
        }
    }
}
