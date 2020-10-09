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

        static ReflectionUtility()
        {
            DescriptionAttributeType = Type.GetType("NUnit.Framework.DescriptionAttribute, NUnit.Framework");
            DescriptionAttributeGetPropertiesMethod = DescriptionAttributeType.GetProperty("Properties").GetMethod;

            var iPropertyBagType = Type.GetType("NUnit.Framework.Interfaces.IPropertyBag, NUnit.Framework");
            IPropertyBagGetMethod = iPropertyBagType.GetMethod("Get", new[] { typeof(string) });

            CategoryAttributeType = Type.GetType("NUnit.Framework.CategoryAttribute, NUnit.Framework");
            CategoryAttributeGetNameMethod = CategoryAttributeType.GetProperty("Name").GetMethod;
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

            // TODO: Deriving assembly name from the file name might not be reliable.
            var assemblyName = Path.GetFileNameWithoutExtension(result.AssemblyPath);
            return Type.GetType($"{result.FullTypeName}, {assemblyName}");
        }
    }
}