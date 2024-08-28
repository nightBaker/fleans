using AutoMapper;
using ProjectTemplate.APPLICATION.Dtos.Queries.SomeQueries;
using ProjectTemplate.APPLICATION.Interfaces.Mappings;
using ProjectTemplate.DOMAIN.AggregatesModel.SomeAggregate;

namespace ProjectTemplate.APPLICATION.Infrastructure.AutoMapper
{
    public class AutoMapperConfigurations : IHaveCustomMapping
    {
        public void CreateMappings(Profile configuration)
        {
            configuration.CreateMap<Some, SomeDto>();
        }
    }
}
