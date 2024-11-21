using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Fleans.Domain.Surrogates
{
    [GenerateSerializer]
    public struct ExpandoObjectSurrogate
    {
        [Id(0)]
        public Dictionary<string, object> Properties { get; set; }
    }

    [RegisterConverter]
    public sealed class ExpandoObjectSurrogateConverter : IConverter<ExpandoObject, ExpandoObjectSurrogate>,
                                                        IPopulator<ExpandoObject, ExpandoObjectSurrogate>
    {
        public ExpandoObject ConvertFromSurrogate(in ExpandoObjectSurrogate surrogate)
        {
            var expando = new ExpandoObject();
            foreach (var kvp in surrogate.Properties)
            {
                ((IDictionary<string, object>)expando).Add(kvp);
            }
            return expando;
        }

        public ExpandoObjectSurrogate ConvertToSurrogate(in ExpandoObject value)
        {
            var expandoObjectSurrogate = new ExpandoObjectSurrogate();
            expandoObjectSurrogate.Properties = new Dictionary<string, object>();

            foreach (var kvp in (IDictionary<string, object>)value)
            {
                expandoObjectSurrogate.Properties.Add(kvp.Key, kvp.Value);
            }

            return expandoObjectSurrogate;
        }

        public void Populate(in ExpandoObjectSurrogate surrogate, ExpandoObject value)
        {
            foreach (var kvp in surrogate.Properties)
            {
                ((IDictionary<string, object>)value).Add(kvp);
            }
        }
    }
}
