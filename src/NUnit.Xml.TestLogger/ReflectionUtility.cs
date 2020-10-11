// Copyright (c) CSG Systems Inc. Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.Extension.NUnit.Xml.TestLogger
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;

    internal static class ReflectionUtility
    {
        private static readonly Type DescriptionAttributeType;
        private static readonly MethodInfo DescriptionAttributeGetPropertiesMethod;

        private static readonly MethodInfo IPropertyBagGetMethod;

        private static readonly Type CategoryAttributeType;
        private static readonly MethodInfo CategoryAttributeGetNameMethod;

        private static readonly Type TestAttributeType;
        private static readonly Type TestCaseAttributeType;

        private static readonly Type ITestDataType;
        private static readonly MethodInfo ITestDataGetArgumentsMethod;

        static ReflectionUtility()
        {
            DescriptionAttributeType = Type.GetType("NUnit.Framework.DescriptionAttribute, NUnit.Framework");
            DescriptionAttributeGetPropertiesMethod = DescriptionAttributeType.GetProperty("Properties").GetMethod;

            var iPropertyBagType = Type.GetType("NUnit.Framework.Interfaces.IPropertyBag, NUnit.Framework");
            IPropertyBagGetMethod = iPropertyBagType.GetMethod("Get", new[] { typeof(string) });

            CategoryAttributeType = Type.GetType("NUnit.Framework.CategoryAttribute, NUnit.Framework");
            CategoryAttributeGetNameMethod = CategoryAttributeType.GetProperty("Name").GetMethod;

            TestAttributeType = Type.GetType("NUnit.Framework.TestAttribute, NUnit.Framework");
            TestCaseAttributeType = Type.GetType("NUnit.Framework.TestCaseAttribute, NUnit.Framework");

            ITestDataType = Type.GetType("NUnit.Framework.Interfaces.ITestData, NUnit.Framework");
            ITestDataGetArgumentsMethod = ITestDataType.GetProperty("Arguments").GetMethod;
        }

        public static string GetDescription(IEnumerable<Attribute> attributes)
        {
            return attributes
                .Where(x => x.GetType() == DescriptionAttributeType)
                .Select(x =>
                {
                    var properties = DescriptionAttributeGetPropertiesMethod.Invoke(x, null);
                    return (string)IPropertyBagGetMethod.Invoke(properties, new object[] { "Description" });
                })
                .FirstOrDefault();
        }

        public static string GetDescription(TestResultInfo result)
        {
            var splitMethod = result.Method.Split('(', ')');
            var methodName = splitMethod[0];
            var testCaseArgs = splitMethod.Length == 1 ? null : splitMethod[1];

            var testFixtureType = ReflectionUtility.GetTestFixtureType(result);

            var matchingMethod = testFixtureType.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(x => x.Name == methodName)
                .FirstOrDefault(x => IsSignatureMatching(x, testCaseArgs));

            if (matchingMethod == null)
            {
                return null;
            }

            var attributes = matchingMethod.GetCustomAttributes(false).Cast<Attribute>();
            return GetDescription(attributes);
        }

        public static string[] GetCategories(IEnumerable<Attribute> attributes)
        {
            return attributes
                .Where(x => x.GetType() == CategoryAttributeType)
                .Select(x => CategoryAttributeGetNameMethod.Invoke(x, null))
                .OfType<string>()
                .ToArray();
        }

        public static Type GetTestFixtureType(TestResultInfo result)
        {
            if (result == null)
            {
                return null;
            }

            var assemblyName = Path.GetFileNameWithoutExtension(result.AssemblyPath);
            return Type.GetType($"{result.FullTypeName}, {assemblyName}");
        }

        private static bool IsSignatureMatching(MethodInfo method, string testCaseArgs)
        {
            var attributes = method.GetCustomAttributes(false);

            if (testCaseArgs == null)
            {
                // test case decorated with [NUnit.Framework.TestAttribute()] has no parameters.
                return method.GetParameters().Length == 0 && attributes.FirstOrDefault(x => x.GetType() == TestAttributeType) != null;
            }

            // test case decorated with [NUnit.Framework.TestCaseAttribute] must have matching arguments.
            var testCaseAttributes = attributes.Where(x => x.GetType() == TestCaseAttributeType).ToArray();
            return testCaseAttributes
                .Select(x => ExtractTestCaseArguments(x))
                .Any(x => x == testCaseArgs);
        }

        private static string ExtractTestCaseArguments(object testCaseAttribute)
        {
            var arguments = ((object[])ITestDataGetArgumentsMethod.Invoke(testCaseAttribute, null))
                .Select(x => x == null ? "null" : $"\"{x}\"")
                .ToArray();

            return string.Join(",", arguments);
        }
    }
}