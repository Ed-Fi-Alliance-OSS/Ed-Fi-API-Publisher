// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.Helpers
{
    [TestFixture]
    public class JTokenExtensionsTests
    {
        [Test]
        public void SafeValue_returns_the_string_value_for_a_scalar_token()
        {
            JToken token = "0123456789abcdef0123456789abcdef";

            token.SafeValue().ShouldBe("0123456789abcdef0123456789abcdef");
        }

        [Test]
        public void SafeValue_returns_null_for_a_JSON_null_token()
        {
            JToken token = JValue.CreateNull();

            token.SafeValue().ShouldBeNull();
        }

        [Test]
        public void SafeValue_returns_null_for_an_absent_token()
        {
            JToken token = null;

            token.SafeValue().ShouldBeNull();
        }

        [Test]
        public void SafeValue_returns_null_instead_of_throwing_for_an_object_token()
        {
            JToken token = new JObject { ["nested"] = "value" };

            Should.NotThrow(() => token.SafeValue()).ShouldBeNull();
        }

        [Test]
        public void SafeValue_returns_null_instead_of_throwing_for_an_array_token()
        {
            JToken token = new JArray("one", "two");

            Should.NotThrow(() => token.SafeValue()).ShouldBeNull();
        }
    }
}
