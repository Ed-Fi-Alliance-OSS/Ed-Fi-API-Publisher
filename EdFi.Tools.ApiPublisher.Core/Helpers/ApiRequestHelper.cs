using System;
using EdFi.Tools.ApiPublisher.Core.Processing;

namespace EdFi.Tools.ApiPublisher.Core.Helpers
{
    public static class ApiRequestHelper
    {
        public static string GetChangeWindowQueryStringParameters(ChangeWindow changeWindow)
        {
            string changeWindowParms = changeWindow == null
                ? String.Empty
                : $"&minChangeVersion={changeWindow.MinChangeVersion}&maxChangeVersion={changeWindow.MaxChangeVersion}";
            
            return changeWindowParms;
        }
    }
}