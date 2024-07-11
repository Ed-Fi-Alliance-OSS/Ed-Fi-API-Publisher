// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Core.Processing
{
	public static class EdFiApiConstants
    {
        /// <summary>
        /// Gets the path suffix to the "deletes" child resource under each of the data management API's resources.
        /// </summary>
        public const string DeletesPathSuffix = "/deletes";
        
        /// <summary>
        /// Gets the path suffix to the "deletes" child resource under each of the data management API's resources.
        /// </summary>
        public const string KeyChangesPathSuffix = "/keyChanges";
        
        /// <summary>
        /// Gets the path segment to the data management API, including the version.
        /// </summary>
        public const string DataManagementApiSegment = "data/v3";
        
        /// <summary>
        /// Gets the path segment to the change queries feature of the API, including the version.
        /// </summary>
        public const string ChangeQueriesApiSegment = "changeQueries/v1";
        
        /// <summary>
        /// Gets the name of the property which holds the natural key values for a deleted item (if supported by the source API).
        /// </summary>
        public const string KeyValuesPropertyName = "keyValues";

        /// <summary>
        /// Gets the name of the property which holds the old (previous) natural key values for an item whose key has changed (if supported by the source API).
        /// </summary>
        public const string OldKeyValuesPropertyName = "oldKeyValues";

        /// <summary>
        /// Gets the name of the property which holds the new natural key values for an item whose key has changed (if supported by the source API).
        /// </summary>
        public const string NewKeyValuesPropertyName = "newKeyValues";
    }
}
