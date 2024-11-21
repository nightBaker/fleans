using Microsoft.Extensions.Hosting;
using Orleans.Serialization;
using System.Dynamic;

namespace Fleans.Domain
{
    public static class DomainDependencyInjection
    {
        public static void AddFleans(this IHostBuilder hostBuilder)
        {
            hostBuilder.UseOrleans(static siloBuilder =>
             {
                 siloBuilder.Services.AddSerializer(serializerBuilder =>
                 {
                     //serializerBuilder.AddNewtonsoftJsonSerializer(
                     //    isSupported: type => type == typeof(ExpandoObject), new Newtonsoft.Json.JsonSerializerSettings
                     //    {
                     //        TypeNameHandling = Newtonsoft.Json.TypeNameHandling.All
                     //    });
                 });
             });
        }
    }
}
