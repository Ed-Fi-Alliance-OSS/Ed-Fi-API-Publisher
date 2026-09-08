// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Core.Metadata
{
    /// <summary>
    /// The reasons a document can be abandoned without being published or reported as an error. They are
    /// reported with the run summary, so they are worded for an operator and kept in one place to keep the
    /// summary from listing the same reason twice (see APIPUB-120).
    /// </summary>
    public static class SkipReasons
    {
        /// <summary>
        /// The target rejected a POST with a 403 for a resource configured with
        /// <c>treatForbiddenPostAsWarning</c>, so the resource's remaining documents are not published.
        /// </summary>
        public const string ResourceIgnoredAfterAuthorizationFailure =
            "a resource was ignored after an authorization failure (treatForbiddenPostAsWarning)";
    }
}
