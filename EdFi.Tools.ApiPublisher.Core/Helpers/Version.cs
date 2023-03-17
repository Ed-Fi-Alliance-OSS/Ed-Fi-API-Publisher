// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;

namespace EdFi.Tools.ApiPublisher.Core.Helpers
{
    public class Version : IEquatable<Version>
    {
        public Version(string versionText)
        {
            string[] versionParts = versionText.Split('.');

            Major = Convert.ToInt32(versionParts[0]);

            if (versionParts.Length >= 2)
            {
                Minor = Convert.ToInt32(versionParts[1]);
            }

            if (versionParts.Length >= 3)
            {
                Revision = Convert.ToInt32(versionParts[2]);
            }
        }

        public int Major { get; }
        public int Minor { get; }
        public int Revision { get; }

        public override string ToString()
        {
            return $"v{Major}.{Minor}.{Revision}";
        }

        #region Generated Equatable Members
        public bool Equals(Version? other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return Major == other.Major && Minor == other.Minor && Revision == other.Revision;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (obj.GetType() != this.GetType())
            {
                return false;
            }

            return Equals((Version) obj);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Major, Minor, Revision);
        }
        
        #endregion
    }
}
