namespace EdFi.Tools.ApiPublisher.Core.Helpers
{
    public static class VersionExtensions
    {
        public static bool IsAtLeast(this Version apiVersion, int majorVersion, int minorVersion)
        {
            return apiVersion.Major > majorVersion 
                || (apiVersion.Major == majorVersion && apiVersion.Minor >= minorVersion);
        }
    }
}