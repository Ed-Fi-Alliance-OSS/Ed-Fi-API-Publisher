// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using NUnit.Framework;
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.Configuration
{
    /// <summary>
    /// Covers how the number of retries for a source read rejected with 429 is resolved. It exists as its own
    /// option so that the 429 handling can be turned off without also disabling the retries that cover every
    /// other transient failure, which is what turning off <see cref="Options.MaxRetryAttempts" /> would do:
    /// that value drives the retry policies for source reads and for target writes alike.
    /// </summary>
    [TestFixture]
    public class TooManyRequestsRetryAttemptsTests
    {
        [Test]
        public void By_default_it_should_follow_MaxRetryAttempts()
        {
            var options = new Options { MaxRetryAttempts = 7 };

            options.TooManyRequestsRetryAttempts.ShouldBe(Options.FollowMaxRetryAttempts);
            options.ResolvedTooManyRequestsRetryAttempts.ShouldBe(7);
        }

        [Test]
        public void Zero_should_turn_off_the_429_retries_without_touching_the_others()
        {
            var options = new Options { MaxRetryAttempts = 7, TooManyRequestsRetryAttempts = 0 };

            options.ResolvedTooManyRequestsRetryAttempts.ShouldBe(0);

            // The lever an operator reaches for must leave every other retry policy alone
            options.MaxRetryAttempts.ShouldBe(7);
        }

        [Test]
        public void An_explicit_count_should_be_used_as_given()
        {
            var options = new Options { MaxRetryAttempts = 7, TooManyRequestsRetryAttempts = 2 };

            options.ResolvedTooManyRequestsRetryAttempts.ShouldBe(2);
        }

        [Test]
        public void Any_negative_value_should_resolve_the_same_as_the_sentinel()
        {
            // CLI options validation rejects anything below the sentinel; this covers a library consumer that
            // bypasses it, so that an out-of-range value follows MaxRetryAttempts rather than disabling retries
            var options = new Options { MaxRetryAttempts = 7, TooManyRequestsRetryAttempts = -5 };

            options.ResolvedTooManyRequestsRetryAttempts.ShouldBe(7);
        }
    }
}
