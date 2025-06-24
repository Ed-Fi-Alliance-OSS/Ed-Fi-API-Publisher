// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
namespace EdFi.Tools.ApiPublisher.Connections.Api.Configuration;

public class ApiConnectionDetails : SourceConnectionDetailsBase, ISourceConnectionDetails, ITargetConnectionDetails
{
    public string Url { get; set; }
    public string AuthUrl { get; set; }
    public string Key { get; set; }
    public string Secret { get; set; }
    public string Scope { get; set; }
    public int? SchoolYear { get; set; }

    public bool? TreatForbiddenPostAsWarning { get; set; }

    public string ProfileName { get; set; }

    public override bool IsFullyDefined()
    {
        return (Url != null && Key != null && Secret != null);
    }

    public IEnumerable<string> MissingConfigurationValues()
    {
        if (Url == null)
        {
            yield return "Url";
        }

        if (Key == null)
        {
            yield return "Key";
        }

        if (Secret == null)
        {
            yield return "Secret";
        }
    }

    public override bool NeedsResolution()
    {
        return !IsFullyDefined() && !string.IsNullOrEmpty(Name);
    }

    public bool IsOdsAuthService
    {
        get
        {
            return string.IsNullOrWhiteSpace(AuthUrl);
        }
    }
}
