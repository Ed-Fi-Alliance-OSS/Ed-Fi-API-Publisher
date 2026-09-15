// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Core.Processing.RunState;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Helpers
{
    /// <summary>
    /// Records nothing, for the tests that exercise something other than resumability (see APIPUB-142).
    /// A test about checkpointing supplies a real <see cref="PageCheckpointCoordinator" /> instead.
    /// </summary>
    public class NullPageCheckpointCoordinator : IPageCheckpointCoordinator
    {
        public static readonly NullPageCheckpointCoordinator Instance = new();

        public void Begin(PublishRunState runState, CancellationToken cancellationToken) { }

        public IReadOnlyList<string> TryGetResumeTokens(string resourceUrl, bool isAuthorizationRetryPass) => null;

        public void PartitionsProduced(string resourceUrl, bool isAuthorizationRetryPass, IReadOnlyList<string> startingPageTokens) { }

        public void ItemProduced(SourcePageReference page) { }

        public void PageFullyRead(SourcePageReference page) { }

        public void ItemCompleted(SourcePageReference page, bool lost) { }

        public Task StopAsync() => Task.CompletedTask;
    }
}
