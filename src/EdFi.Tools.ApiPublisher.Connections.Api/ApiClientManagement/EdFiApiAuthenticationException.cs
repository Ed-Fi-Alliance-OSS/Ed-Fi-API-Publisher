// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement
{
    /// <summary>
    /// Indicates that an API client cannot authenticate, either because the initial bearer token could not be
    /// obtained or because the token could not be refreshed before it expired. Publishing cannot continue, since
    /// every subsequent request would be rejected.
    /// </summary>
    public class EdFiApiAuthenticationException : Exception
    {
        public EdFiApiAuthenticationException()
        {
        }

        public EdFiApiAuthenticationException(string message)
            : base(message)
        {
        }

        public EdFiApiAuthenticationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Indicates whether the supplied exception, or any of its inner exceptions, represents an authentication
        /// failure. Exceptions thrown from the request pipeline are wrapped by <see cref="HttpClient" />, so the
        /// inner exceptions have to be inspected as well.
        /// </summary>
        public static bool IsRepresentedBy(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is EdFiApiAuthenticationException)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
