using Microsoft.Extensions.Options;
using ProjectTemplate.DOMAIN.AggregatesModel.SomeAggregate;
using Sieve.Models;
using Sieve.Services;
using System;
using System.Collections.Generic;
using System.Text;

namespace ProjectTemplate.APPLICATION.Infrastructure.Sieve
{
    public class ApplicationSieveProcessor : SieveProcessor
    {
        public ApplicationSieveProcessor(
            IOptions<SieveOptions> options)
            : base(options)
        {
        }

        protected override SievePropertyMapper MapProperties(SievePropertyMapper mapper)
        {
            mapper.Property<Some>(p => p.SomeValue)
                .CanFilter();

            mapper.Property<Some>(p => p.SomeEnum)
                .CanSort();

            return mapper;
        }
    }
}
