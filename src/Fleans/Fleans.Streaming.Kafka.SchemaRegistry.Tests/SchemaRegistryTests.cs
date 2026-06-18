using Fleans.Streaming.Kafka.SchemaRegistry;
using Microsoft.Extensions.Configuration;

namespace Fleans.Streaming.Kafka.SchemaRegistry.Tests;

[TestClass]
public class SchemaRegistryTests
{
    [TestMethod]
    public void KafkaSchemaRegistryOptions_Defaults()
    {
        var opts = new KafkaSchemaRegistryOptions();

        Assert.AreEqual(string.Empty, opts.Url);
        Assert.IsNull(opts.BasicAuthUsername);
        Assert.IsNull(opts.BasicAuthPassword);
        Assert.IsNull(opts.SslCaLocation);
        Assert.IsNull(opts.SslKeystoreLocation);
        Assert.IsNull(opts.SslKeystorePassword);
    }

    [TestMethod]
    public void KafkaSchemaRegistryOptions_BindsFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Url"] = "http://registry:8081",
                ["BasicAuthUsername"] = "user",
                ["BasicAuthPassword"] = "pass",
            })
            .Build();

        var opts = new KafkaSchemaRegistryOptions();
        config.Bind(opts);

        Assert.AreEqual("http://registry:8081", opts.Url);
        Assert.AreEqual("user", opts.BasicAuthUsername);
        Assert.AreEqual("pass", opts.BasicAuthPassword);
    }
}
