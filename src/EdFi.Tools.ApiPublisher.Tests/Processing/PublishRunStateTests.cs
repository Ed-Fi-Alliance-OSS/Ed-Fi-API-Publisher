// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing.RunState;
using NUnit.Framework;
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// APIPUB-142: what a resume is allowed to continue. Every branch of the match is pinned here, because a
    /// branch that quietly passes hands one publication's page tokens to a run against different APIs.
    /// </summary>
    [TestFixture]
    public class PublishRunStateTests
    {
        [Test]
        public void State_for_the_same_source_and_target_is_resumable()
        {
            var state = PublishRunState.StartNew("SourceOds", "TargetOds", changeWindow: null);

            state.Matches("SourceOds", "TargetOds", out string reason).ShouldBeTrue();
            reason.ShouldBeNull();
        }

        [Test]
        public void State_for_a_different_source_is_refused()
        {
            var state = PublishRunState.StartNew("SourceOds", "TargetOds", changeWindow: null);

            state.Matches("AnotherSourceOds", "TargetOds", out string reason).ShouldBeFalse();
            reason.ShouldContain("SourceOds");
            reason.ShouldContain("AnotherSourceOds");
        }

        [Test]
        public void State_for_a_different_target_is_refused()
        {
            var state = PublishRunState.StartNew("SourceOds", "TargetOds", changeWindow: null);

            state.Matches("SourceOds", "AnotherTargetOds", out string reason).ShouldBeFalse();
            reason.ShouldContain("TargetOds");
            reason.ShouldContain("AnotherTargetOds");
        }

        /// <summary>
        /// The case this class exists for. Connection names are optional, and two absent names compare equal,
        /// so without an explicit refusal the state of a publication between one pair of APIs is accepted by a
        /// run between a different pair and its page tokens replayed against a source that never issued them.
        /// </summary>
        [Test]
        public void State_written_by_a_run_with_unnamed_connections_is_refused()
        {
            var state = PublishRunState.StartNew(sourceConnectionName: null, targetConnectionName: null, changeWindow: null);

            state.Matches(null, null, out string reason).ShouldBeFalse();
            reason.ShouldContain("not named");
        }

        [Test]
        public void A_run_with_unnamed_connections_cannot_resume_named_state()
        {
            var state = PublishRunState.StartNew("SourceOds", "TargetOds", changeWindow: null);

            state.Matches(null, null, out string reason).ShouldBeFalse();
            reason.ShouldContain("not named");
        }

        [Test]
        public void An_empty_name_counts_as_no_name()
        {
            var state = PublishRunState.StartNew("   ", "TargetOds", changeWindow: null);

            state.Matches("   ", "TargetOds", out string reason).ShouldBeFalse();
            reason.ShouldContain("not named");
        }

        [Test]
        public void State_written_by_another_publisher_build_is_refused()
        {
            var state = PublishRunState.StartNew("SourceOds", "TargetOds", changeWindow: null);

            // What an upgrade between the failed run and the resume leaves behind
            state.PublisherVersion = "0.0.1-from-an-older-build";

            state.Matches("SourceOds", "TargetOds", out string reason).ShouldBeFalse();
            reason.ShouldContain("0.0.1-from-an-older-build");
        }
    }
}
