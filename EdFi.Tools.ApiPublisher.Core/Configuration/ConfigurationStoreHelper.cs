namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public static class ConfigurationStoreHelper
    {
        public static string Key(string apiConnectionName)
        {
            return $"/ed-fi/apiPublisher/connections/{apiConnectionName}";
        }
    }
}