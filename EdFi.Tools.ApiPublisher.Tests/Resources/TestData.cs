using System.IO;
using System.Reflection;

namespace EdFi.Tools.ApiPublisher.Tests.Resources
{
    public static class TestData
    {
        public static class Dependencies
        {
            // ReSharper disable once InconsistentNaming
            public static string GraphML()
            {
                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("EdFi.Tools.ApiPublisher.Tests.Resources.Dependencies-GraphML-v5.2.xml");

                using var sr = new StreamReader(stream);

                return sr.ReadToEnd();
            }
        }
    }
}