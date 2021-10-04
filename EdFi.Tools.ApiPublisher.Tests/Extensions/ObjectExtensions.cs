using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Web;
using EdFi.Tools.ApiPublisher.Tests.Serialization;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace EdFi.Tools.ApiPublisher.Tests.Extensions
{
    public static class ObjectExtensions
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(ObjectExtensions));

        public static IDictionary<string, object> ToDictionary(
            this object instance,
            Func<PropertyDescriptor, object, bool> selector = null)
        {
            // Default selector
            if (selector == null)
            {
                selector = (descriptor, o) => true;
            }

            var dictionary = new Dictionary<string, object>(StringComparer.InvariantCultureIgnoreCase);

            if (instance != null)
            {
                foreach (PropertyDescriptor descriptor in TypeDescriptor.GetProperties(instance))
                {
                    object value = null;

                    try
                    {
                        value = descriptor.GetValue(instance);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex.Message);

                        // ODS-1943 PropertyDescriptors GetValue method wrapping the ArgumentException with TargetInvocationException,
                        // which is abstracting the actual exception.
                        // c.f. https://referencesource.microsoft.com/#System/compmod/system/componentmodel/ReflectPropertyDescriptor.cs,3b48df1474c54332
                        if (ex is TargetInvocationException && ex.InnerException is ArgumentException)
                        {
                            throw new Exception(ex.Message, ex.InnerException);
                        }

                        throw;
                    }

                    if (selector(descriptor, value))
                    {
                        dictionary.Add(descriptor.Name, value);
                    }
                }
            }

            return dictionary;
        }

        public static void FromDictionary(
            this object instance,
            IDictionary<string, object> values,
            Func<PropertyDescriptor, object, bool> selector = null)
        {
            if (selector == null)
            {
                selector = (descriptor, o) => true;
            }

            if (instance == null)
            {
                return;
            }

            foreach (PropertyDescriptor descriptor in TypeDescriptor.GetProperties(instance))
            {
                object value;

                if (values.TryGetValue(descriptor.Name, out value) && selector(descriptor, value))
                {
                    descriptor.SetValue(instance, value);
                }
            }
        }

        public static IDictionary<string, string> ToQueryStringParams(this object instance)
        {
            string json = JsonConvert.SerializeObject(
                instance,
               MockRequests.SerializerSettings);

            var obj = JObject.Parse(json);
            
            var queryStringParms = new Dictionary<string, string>();

            foreach (var property in obj.Properties())
            {
                queryStringParms[property.Name] = property.Value.ToString();
            }

            return queryStringParms;
        }
        
        public static NameValueCollection ParseQueryString(this Uri uri)
        {
            return HttpUtility.ParseQueryString(uri.Query);
        }
    }
}