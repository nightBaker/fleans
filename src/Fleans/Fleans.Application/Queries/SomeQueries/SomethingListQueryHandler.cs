using System;
using MediatR;
using ProjectTemplate.APPLICATION.Dtos.Queries;
using ProjectTemplate.APPLICATION.Dtos.Queries.SomeQueries;
using ProjectTemplate.APPLICATION.Dtos.Queries.SomeQueries.SomethingList;
using ProjectTemplate.DOMAIN.AggregatesModel.SomeAggregate;
using System.Threading;
using System.Threading.Tasks;
using Fleans.Application.Interfaces.Persistence.QueryServices;

namespace ProjectTemplate.APPLICATION.Queries.SomeQueries
{
    public class SomethingListQueryHandler : IRequestHandler<SomethingListQuery, ListResultsDto<SomeDto>>
    {
        private readonly IQueryService<Some, SomeDto> _queryService;
        public SomethingListQueryHandler(IQueryService<Some, SomeDto> queryService)
        {
            _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        }
        public  Task<ListResultsDto<SomeDto>> Handle(SomethingListQuery request, CancellationToken cancellationToken)
        {
            return  _queryService.GetAllAsync(request);
        }
    }
}
