// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement
{
    /// <summary>
    /// How a client paces itself against the API it reads from: how much it will ask of the API at once.
    /// </summary>
    public class ApiThrottlingPolicy
    {
        /// <summary>
        /// Asks the API for as much as the caller produces, which is how a client behaves when it has not been
        /// configured otherwise.
        /// </summary>
        public static readonly ApiThrottlingPolicy None = new();

        /// <summary>
        /// The most requests the client will have in flight against the API at one time. Zero leaves the API
        /// uncapped.
        /// </summary>
        public int MaxConcurrentRequests { get; init; }
    }
}
