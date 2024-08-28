using MediatR;
using Sieve.Models;

namespace ProjectTemplate.APPLICATION.Dtos.Queries.SomeQueries.SomethingList
{
    public class SomethingListQuery : SieveModel, IRequest<ListResultsDto<SomeDto>>
    {
        
    }
}
