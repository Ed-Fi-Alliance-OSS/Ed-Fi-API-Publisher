// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.


namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
	public class ConnectionConfiguration
    {
        public Connections Connections { get; set; }
    }
    
    public class Connections
    {
        public NamedConnectionDetailsConfiguration Source { get; set; }
        public NamedConnectionDetailsConfiguration Target { get; set; }
    }

    public class NamedConnectionDetailsConfiguration : NamedConnectionDetailsBase
    {
        public string Url { get; set; }
        public string Key { get; set; }
        public string Secret { get; set; }
        public override bool IsFullyDefined()
        {
            return (Url != null && Key != null && Secret != null);
        }
    }
}
