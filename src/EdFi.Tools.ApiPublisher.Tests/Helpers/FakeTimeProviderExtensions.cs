// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.Tools.ApiPublisher.Tests.Helpers
{
    public static class FakeTimeProviderExtensions
    {
        /// <summary>
        /// Advances the fake clock in steps until the supplied task completes, so that a code path that waits on
        /// <see cref="Task.Delay(TimeSpan, TimeProvider)" /> against the fake clock is not left waiting forever.
        /// The wall-clock timeout is a safety net for a task that never completes; it is not what drives the clock.
        /// </summary>
        public static async Task AdvanceUntilCompletedAsync(
            this FakeTimeProvider timeProvider,
            Task task,
            TimeSpan step,
            TimeSpan? wallClockTimeout = null)
        {
            var stopwatch = Stopwatch.StartNew();
            var timeout = wallClockTimeout ?? TimeSpan.FromSeconds(30);

            while (!task.IsCompleted)
            {
                if (stopwatch.Elapsed > timeout)
                {
                    throw new TimeoutException(
                        $"The task did not complete within {timeout.TotalSeconds:N0} wall-clock seconds while the fake clock was advanced by {step} per step.");
                }

                timeProvider.Advance(step);

                // Let the continuation released by the advance run before the next step
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }

            // Observe the outcome (including an exception) on the caller's terms
            await task;
        }
    }
}
