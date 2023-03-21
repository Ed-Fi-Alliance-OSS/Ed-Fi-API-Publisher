// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

#nullable enable
using System.Threading.Tasks;
using Jering.Javascript.NodeJS;
using NUnit.Framework;
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.JavascriptHosting
{
    [TestFixture]
    public class ScriptExecutionTests
    {
        class Result
        {
            public string? greeting { get; set; }
        }

        class Person
        {
            public string? name { get; set; }
            public int age { get; set; }
        }
        
        [Test]
        public async Task Should_execute_JavaScript()
        {
            
            var result = await StaticNodeJSService.InvokeFromStringAsync<Result>(
                @"
module.exports = (callback, name) => {

    const result = { greeting: `Hello ${name}!` };

    callback(null, result);
}
", args: new object?[] { "Bob" });

            result!.greeting.ShouldBe("Hello Bob!");
        }

        const string HelloNameSource = @"
module.exports = {
    sayHello: async (name) => {
        const result = { greeting: `Hello ${name}!` };
        
        return result;
    },
    sayGoodbye: async (name) => {
        const result = { greeting: `Goodbye ${name}!` };
        return result;
    }
}
";

        [Test]
        public async Task Should_execute_JavaScript_object_with_functions()
        {
            var result = await StaticNodeJSService.InvokeFromStringAsync<Result>(
                HelloNameSource, 
                cacheIdentifier: "helloNameModule",
                exportName: "sayHello", 
                args: new object?[] { "Bob" });
            
            result!.greeting.ShouldBe("Hello Bob!");

            var result2 = await StaticNodeJSService.InvokeFromStringAsync<Result>(
                HelloNameSource, 
                cacheIdentifier: "helloNameModule",
                exportName: "sayGoodbye", 
                args: new object?[] { "Bob" });
            
            result2!.greeting.ShouldBe("Goodbye Bob!");
        }

        [Test]
        public void Should_throw_invocation_exception_for_non_existing_function()
        {
            Should.ThrowAsync<InvocationException>(
                async () =>
                {
                    await StaticNodeJSService.InvokeFromStringAsync<Result>(
                        HelloNameSource,
                        cacheIdentifier: "helloNameModule",
                        exportName: "doesNotExist",
                        args: new object?[] { "Bob" });
                });
        }
        
        [Test]
        public async Task Should_execute_JavaScript_object_with_object_argument()
        {
            const string helloPersonSource = @"
module.exports = {
    sayHello: async (person) => {
        const result = { greeting: `Hello ${person.name}! You are ${person.age} years old already!` };
        return result;
    },
    sayGoodbye: async (person) => {
        const result = { greeting: `Goodbye ${person.name}! You are ${person.age} years old already!` };
        return result;
    }
}
";

            var results = await StaticNodeJSService.InvokeFromStringAsync<Result>(
                helloPersonSource,
                cacheIdentifier: "helloPersonModule",
                exportName: "sayHello",
                args: new object?[] { new Person { name = "Bob", age = 42 }});

            results?.greeting.ShouldBe("Hello Bob! You are 42 years old already!");
            
            var results2 = await StaticNodeJSService.InvokeFromStringAsync<Result>(
                helloPersonSource,
                cacheIdentifier: "helloPersonModule",
                exportName: "sayGoodbye",
                args: new object?[] { new Person { name = "Bob", age = 42 }});

            results2?.greeting.ShouldBe("Goodbye Bob! You are 42 years old already!");
        }
        
        [Test]
        public async Task Should_execute_JavaScript_object_with_status_codes_with_object_argument()
        {
            const string helloPersonSource2 = @"
module.exports = {
    200: async (person) => {
        const result = { greeting: `Hello ${person.name}! You are ${person.age} years old already!` };
        return result;
    },
    500: async (person) => {
        const result = { greeting: `Goodbye ${person.name}! You are ${person.age} years old already!` };
        return result;
    }
}
";

            var results = await StaticNodeJSService.InvokeFromStringAsync<Result>(
                helloPersonSource2,
                cacheIdentifier: "helloPersonModule2",
                exportName: "200",
                args: new object?[] { new Person { name = "Bob", age = 42 }});

            results?.greeting.ShouldBe("Hello Bob! You are 42 years old already!");
            
            var results2 = await StaticNodeJSService.InvokeFromStringAsync<Result>(
                helloPersonSource2,
                cacheIdentifier: "helloPersonModule2",
                exportName: "500",
                args: new object?[] { new Person { name = "Bob", age = 42 }});

            results2?.greeting.ShouldBe("Goodbye Bob! You are 42 years old already!");
        }

        [Test]
        public async Task Should_execute_JavaScript_object_with_resource_paths_with_object_argument()
        {
            const string helloPersonSource3 = @"
module.exports = {
    '/ed-fi/students/200': async (person) => {
        const result = { greeting: `Hello ${person.name}! You are ${person.age} years old already!` };
        return result;
    },
    '/ed-fi/students/500': async (person) => {
        const result = { greeting: `Goodbye ${person.name}! You are ${person.age} years old already!` };
        return result;
    }
}
";

            var results = await StaticNodeJSService.InvokeFromStringAsync<Result>(
                helloPersonSource3,
                cacheIdentifier: "helloPersonModule3",
                exportName: "/ed-fi/students/200",
                args: new object?[] { new Person { name = "Bob", age = 42 }});

            results?.greeting.ShouldBe("Hello Bob! You are 42 years old already!");
            
            var results2 = await StaticNodeJSService.InvokeFromStringAsync<Result>(
                helloPersonSource3,
                cacheIdentifier: "helloPersonModule3",
                exportName: "/ed-fi/students/500",
                args: new object?[] { new Person { name = "Bob", age = 42 }});

            results2?.greeting.ShouldBe("Goodbye Bob! You are 42 years old already!");
        }
    }
}
