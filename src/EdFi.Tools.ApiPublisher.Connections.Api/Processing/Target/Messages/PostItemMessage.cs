// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages
{
    public class PostItemMessage : ISourcePagedProcessDataMessage
    {
        public string ResourceUrl { get; set; }

        /// <summary>
        /// The source item's "id", stamped the first time processing reads a valid id from
        /// <see cref="Item"/> so that it survives after <see cref="Item"/> is released once the item has
        /// been processed.
        /// </summary>
        public string Id { get; set; }

        public JObject Item { get; set; }

        /// <summary>
        /// Describes the source page this item was read from (paging metadata only), captured when the message is
        /// created from the page. One string instance is shared by every item of a page.
        /// </summary>
        public string SourcePage { get; set; }

        /// <summary>
        /// The zero-based position of this item within its source page's JSON array (counting every element, including
        /// non-object elements that produce no message), captured when the message is created from the page.
        /// </summary>
        public int? SourceItemIndex { get; set; }

        /// <summary>
        /// Identifies the cursor-paged source page this item came from, so that the run can tell when every
        /// document of that page has reached the target and record the page as behind it (see APIPUB-142).
        /// Null for an item read with offset paging, which is not checkpointed. One instance is shared by
        /// every item of a page, like <see cref="SourcePage" />.
        /// </summary>
        public SourcePageReference SourcePageReference { get; set; }

        /// <summary>
        /// Indicates an authorization-retry ("#Retry") pipeline exists that will re-publish the entire resource
        /// after its update prerequisites complete, so a 403 response for this item can be skipped without
        /// publishing an error (see APIPUB-133).
        /// </summary>
        public bool HasAuthorizationRetryPipeline { get; set; }

        /// <summary>
        /// Indicates that this document is being published by the authorization retry pass rather than by the
        /// pass that read the resource for the first time. Both passes carry the same resource URL, so the run
        /// summary needs to tell them apart to report what became of each document (see APIPUB-120).
        /// </summary>
        public bool IsAuthorizationRetryPass { get; set; }

        /// <summary>
        /// Cancellation token from the resource's processing cancellation source, used to abandon in-flight
        /// requests when processing of the resource has been cancelled.
        /// </summary>
        public CancellationToken CancellationToken { get; set; }
    }
}
