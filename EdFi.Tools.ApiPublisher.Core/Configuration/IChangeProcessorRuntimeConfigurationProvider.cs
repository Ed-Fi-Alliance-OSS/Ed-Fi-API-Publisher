namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public interface IChangeProcessorRuntimeConfigurationProvider
    {
        ChangeProcessorConfiguration GetRuntimeConfiguration(string[] commandLineArgs);
    }
}