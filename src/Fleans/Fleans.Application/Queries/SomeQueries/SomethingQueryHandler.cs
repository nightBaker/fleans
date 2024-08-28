using System;
using MediatR;
using ProjectTemplate.APPLICATION.Dtos.Queries.SomeQueries;
using ProjectTemplate.APPLICATION.Dtos.Queries.SomeQueries.Something;
using ProjectTemplate.DOMAIN.AggregatesModel.SomeAggregate;
using System.Threading;
using System.Threading.Tasks;
using Fleans.Application.Interfaces.Persistence.QueryServices;

namespace ProjectTemplate.APPLICATION.Queries.SomeQueries
{
    public class SomethingQueryHandler : IRequestHandler<SomethingQuery, SomeDto>
    {
        private readonly IQueryService<Some, SomeDto> _queryService;
        public SomethingQueryHandler(IQueryService<Some, SomeDto> queryService)
        {
            _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        }
        public Task<SomeDto> Handle(SomethingQuery request, CancellationToken cancellationToken)
        {
            return _queryService.GetAsync(x => x.Id == request.SomeId);
        }
    }
}
